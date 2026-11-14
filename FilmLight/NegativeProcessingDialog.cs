using System.Runtime.InteropServices.WindowsRuntime;
using FilmLight.Core;
using FilmLight.Processing;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Foundation;

namespace FilmLight;

internal sealed class NegativeProcessingDialog : IAsyncDisposable
{
    internal ContentDialog Dialog { get; } = new();
    private readonly RollSession roll;
    private readonly DarktableCliNegativeRenderer renderer;
    private readonly NegativeProcessor processor;
    private readonly ComboBox frames = new() { Header = "Captured frame", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel adjustments = new() { Spacing = 12 };
    private readonly Image preview = new() { Height = 240, Stretch = Stretch.Uniform };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock warnings = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly CheckBox sourceView = new() { Content = "Show source for sampling" };
    private readonly CheckBox batch = new() { Content = "Export all matching frames using this film/light calibration" };
    private readonly CheckBox acceptRgb = new() { Content = "I understand RGB reconstruction is experimental" };
    private readonly DispatcherTimer redraw = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, (NumberBox Box, Func<double> Read)> numbers = [];
    private readonly RollCapture[] captures;
    private PreparedNegative? prepared;
    private NegativeConversionSettings current = NegativeConversionSettings.Default;
    private RollCapture? selected;
    private CancellationTokenSource? operation;
    private Task? active;
    private bool syncing;
    private bool closing;
    private string? picker;

    internal NegativeProcessingDialog(RollSession roll)
    {
        this.roll = roll;
        Dialog.Title = "Process film";
        Dialog.PrimaryButtonText = "Export positive";
        Dialog.SecondaryButtonText = "Cancel conversion";
        Dialog.IsPrimaryButtonEnabled = Dialog.IsSecondaryButtonEnabled = false;
        Dialog.CloseButtonText = "Done";
        Dialog.RequestedTheme = ElementTheme.Dark;
        var cli = DarktableCliNegativeRenderer.DiscoverPath()
            ?? throw new NotSupportedException("Install darktable 5.6.x, or set FILMLIGHT_DARKTABLE_CLI to darktable-cli.exe, then reopen Process film.");
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilmLight", "Processing");
        renderer = new(cli, cache);
        processor = new(renderer, Path.Combine(cache, "linear"));
        var body = new StackPanel { Spacing = 16, MinWidth = 320 };
        body.Children.Add(new TextBlock
        {
            Text = "Convert original RAF captures without changing them. The 16-bit sRGB TIFF master, JPEG, and conversion record are saved in this roll's Positives folder.",
            TextWrapping = TextWrapping.Wrap
        });
        captures = roll.Captures.Where(c => c.Complete && !c.Discarded && c.Kind != RollCaptureKind.References).ToArray();
        foreach (var capture in captures)
            frames.Items.Add(new ComboBoxItem { Content = capture.Kind == RollCaptureKind.White
                ? $"White light - {capture.Id[..8]}" : $"RGB frame {capture.FrameNumber}" });
        body.Children.Add(frames);
        body.Children.Add(preview);
        body.Children.Add(new TextBlock { Text = "Uncropped preview. Click the source to sample light or clear film.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(sourceView);
        var sampling = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var filmBase = new Button { Content = "Sample film base", MinHeight = 44 };
        var lightSource = new Button { Content = "Sample light source", MinHeight = 44 };
        sampling.Children.Add(filmBase); sampling.Children.Add(lightSource);
        filmBase.Click += (_, _) => ArmPicker("base");
        lightSource.Click += (_, _) => ArmPicker("light");
        body.Children.Add(sampling);
        var tone = new Button { Content = "Auto tone within crop", MinHeight = 44 };
        tone.Click += async (_, _) => await RunAsync(async ct =>
        {
            if (prepared is null) return;
            current = current with { Conversion = NegativeAnalysis.AutoTone(prepared.Basis, current.Conversion, current.Frame.Crop) };
            SyncControls();
            await RenderAsync(ct);
        });
        body.Children.Add(tone);
        var filmType = new ComboBox { Header = "Film type", ItemsSource = new[] { "Color negative", "Black and white" }, SelectedIndex = 0 };
        filmType.SelectionChanged += async (_, _) =>
        {
            if (syncing || prepared is null) return;
            current = current with { Conversion = current.Conversion with { FilmType = filmType.SelectedIndex == 1 ? FilmType.BlackAndWhite : FilmType.Color } };
            await RunAsync(RenderAsync);
        };
        adjustments.Children.Add(filmType);
        AddNumber("Density range", .1, 6, () => current.Conversion.DensityRange, v => current = current with { Conversion = current.Conversion with { DensityRange = v } });
        AddNumber("Scan exposure bias", -1, 1, () => current.Conversion.ScanExposureBias, v => current = current with { Conversion = current.Conversion with { ScanExposureBias = v } });
        AddNumber("Paper black", -.5, .5, () => current.Conversion.PaperBlack, v => current = current with { Conversion = current.Conversion with { PaperBlack = v } });
        AddNumber("Paper grade", 1, 8, () => current.Conversion.PaperGrade, v => current = current with { Conversion = current.Conversion with { PaperGrade = v } });
        AddNumber("Paper gloss", .0001, 1, () => current.Conversion.PaperGloss, v => current = current with { Conversion = current.Conversion with { PaperGloss = v } });
        AddNumber("Print exposure", .5, 2, () => current.Conversion.PrintExposure, v => current = current with { Conversion = current.Conversion with { PrintExposure = v } });
        AddNumber("Highlights warmth", -1, 1, () => ColorBalanceControls.ToHighlights(current.Conversion.HighlightsBalance).Warmth,
            v => current = current with { Conversion = current.Conversion with { HighlightsBalance = ColorBalanceControls.FromHighlights(ColorBalanceControls.ToHighlights(current.Conversion.HighlightsBalance) with { Warmth = v }) } });
        AddNumber("Highlights tint", -1, 1, () => ColorBalanceControls.ToHighlights(current.Conversion.HighlightsBalance).Tint,
            v => current = current with { Conversion = current.Conversion with { HighlightsBalance = ColorBalanceControls.FromHighlights(ColorBalanceControls.ToHighlights(current.Conversion.HighlightsBalance) with { Tint = v }) } });
        AddNumber("Finish exposure (EV)", -3, 3, () => current.Finish.ExposureEv, v => current = current with { Finish = current.Finish with { ExposureEv = v } });
        AddNumber("Finish highlights", -100, 100, () => current.Finish.Highlights, v => current = current with { Finish = current.Finish with { Highlights = v } });
        AddNumber("Finish shadows", -100, 100, () => current.Finish.Shadows, v => current = current with { Finish = current.Finish with { Shadows = v } });
        foreach (var dimension in new[] { "X", "Y", "Width", "Height" })
        {
            string key = dimension;
            AddNumber($"Crop {key} (0-1)", 0, 1,
                () => key switch { "X" => current.Frame.Crop.X, "Y" => current.Frame.Crop.Y, "Width" => current.Frame.Crop.Width, _ => current.Frame.Crop.Height },
                v => current = current with { Frame = current.Frame with { Crop = key switch
                {
                    "X" => current.Frame.Crop with { X = v }, "Y" => current.Frame.Crop with { Y = v },
                    "Width" => current.Frame.Crop with { Width = v }, _ => current.Frame.Crop with { Height = v }
                } } });
        }
        AddNumber("Rotation (degrees)", -180, 180, () => current.Frame.RotationDegrees,
            v => current = current with { Frame = current.Frame with { RotationDegrees = v } });
        body.Children.Add(new Expander { Header = "Conversion, Print Finish and crop", Content = adjustments,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        body.Children.Add(warnings);
        body.Children.Add(acceptRgb);
        body.Children.Add(batch);
        body.Children.Add(new TextBlock
        {
            Text = "Batch exports keep saved per-frame tone, finish and crop. New frames get their own auto tone. Only film base, light and color calibration are shared; do not mix film or capture conditions.",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(progress); body.Children.Add(message);
        Dialog.Content = new ScrollViewer { Content = body, MaxHeight = 600, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        frames.SelectionChanged += async (_, _) => await RunAsync(async ct =>
        {
            SaveCurrent();
            selected = frames.SelectedIndex >= 0 ? captures[frames.SelectedIndex] : null;
            prepared = null;
            if (selected is null) return;
            string path = SettingsPath(selected.Id);
            current = File.Exists(path) ? NegativeConversionValidation.Deserialize(await File.ReadAllTextAsync(path, ct)) : NegativeConversionSettings.Default;
            prepared = await processor.PrepareAsync(selected, ReferencesFor(selected), current.Light, ct);
            if (!File.Exists(path)) current = NegativeProcessor.Initialize(prepared, current);
            UpdateWarnings();
            acceptRgb.Visibility = prepared.Kind == RollCaptureKind.Frame ? Visibility.Visible : Visibility.Collapsed;
            syncing = true;
            filmType.SelectedIndex = current.Conversion.FilmType == FilmType.BlackAndWhite ? 1 : 0;
            syncing = false;
            SyncControls();
            await RenderAsync(ct);
        });
        sourceView.Checked += async (_, _) => await RunAsync(RenderAsync);
        sourceView.Unchecked += async (_, _) => await RunAsync(RenderAsync);
        preview.PointerPressed += async (_, args) =>
        {
            if (picker is null || prepared is null || sourceView.IsChecked != true || operation is not null) return;
            var point = args.GetCurrentPoint(preview).Position;
            double scale = Math.Min(preview.ActualWidth / prepared.Basis.Width, preview.ActualHeight / prepared.Basis.Height);
            double x = (point.X - (preview.ActualWidth - prepared.Basis.Width * scale) / 2) / (prepared.Basis.Width * scale);
            double y = (point.Y - (preview.ActualHeight - prepared.Basis.Height * scale) / 2) / (prepared.Basis.Height * scale);
            if (x < 0 || x > 1 || y < 0 || y > 1) return;
            var region = new NormalizedRect(Math.Clamp(x - .02, 0, .96), Math.Clamp(y - .02, 0, .96), .04, .04);
            string mode = picker;
            picker = null;
            await RunAsync(async ct =>
            {
                if (mode == "light")
                {
                    var measurement = await processor.MeasureLightAsync(selected!, current.Light, region, ct);
                    if (measurement.IsBlocked) throw new InvalidDataException("Light source appears clipped. Retake it with a lower exposure.");
                    if (measurement.Residual > .02) throw new InvalidDataException("Light measurement did not converge. Choose a clear, unclipped light-only area.");
                    current = current with { Light = measurement.Light };
                    prepared = await processor.PrepareAsync(selected!, ReferencesFor(selected!), current.Light, ct);
                    current = NegativeAnalysis.RemeasureFilmBase(prepared.Basis, current);
                    UpdateWarnings(string.Join(", ", measurement.Sample.Warnings));
                }
                else
                {
                    var sample = NegativeAnalysis.SampleFilmBase(prepared.Basis, region);
                    current = current with { BaseSample = sample, Conversion = current.Conversion with { FilmBase = sample.Value } };
                    UpdateWarnings();
                }
                SyncControls();
                await RenderAsync(ct);
            });
        };
        Dialog.PrimaryButtonClick += async (_, args) => { args.Cancel = true; await RunAsync(ExportAsync); };
        Dialog.SecondaryButtonClick += (_, args) => { args.Cancel = true; Cancel(); };
        redraw.Tick += async (_, _) => { redraw.Stop(); await RunAsync(RenderAsync); };
        Dialog.Closing += (_, args) =>
        {
            if (operation is null) return;
            args.Cancel = true;
            closing = true;
            Cancel();
        };
        Dialog.Opened += (_, _) =>
        {
            if (frames.Items.Count > 0) frames.SelectedIndex = 0;
        };
    }

    private RollCapture? ReferencesFor(RollCapture capture) => capture.Kind == RollCaptureKind.Frame
        ? roll.Captures.SingleOrDefault(c => c.Kind == RollCaptureKind.References && c.Complete && !c.Discarded) : null;

    private void AddNumber(string name, double minimum, double maximum, Func<double> read, Action<double> write)
    {
        var box = new NumberBox { Header = name, Minimum = minimum, Maximum = maximum, SmallChange = (maximum - minimum) / 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Value = read() };
        box.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            if (!double.IsFinite(box.Value)) { message.Text = $"{name} needs a finite number."; return; }
            write(box.Value);
            redraw.Stop(); redraw.Start();
        };
        numbers.Add(name, (box, read));
        adjustments.Children.Add(box);
    }

    private void SyncControls()
    {
        syncing = true;
        foreach (var entry in numbers.Values) entry.Box.Value = entry.Read();
        syncing = false;
    }

    private void UpdateWarnings(string? extra = null)
    {
        var notices = prepared?.Warnings.ToList() ?? [];
        if (current.BaseSample is { } sample)
        {
            if (sample.IsEstimate) notices.Add("Film base is estimated. Sample clear, unexposed film for deliberate calibration.");
            notices.AddRange(sample.Warnings.Select(warning => $"Film base: {warning}"));
        }
        if (!string.IsNullOrWhiteSpace(extra)) notices.Add(extra);
        warnings.Text = string.Join("\n", notices);
    }

    private void ArmPicker(string mode)
    {
        if (prepared is null || operation is not null) return;
        picker = mode;
        sourceView.IsChecked = true;
        message.Text = mode == "base" ? "Click clear, unexposed film (not the bare light or picture content)."
            : "On a white-light capture, click bare backlight with no film. This is separate from the film base.";
    }

    private async Task RenderAsync(CancellationToken ct)
    {
        if (prepared is null) return;
        NegativeConversionValidation.Validate(current);
        bool showSource = sourceView.IsChecked == true;
        var basis = prepared.Basis;
        var settings = current;
        var image = await Task.Run(() => showSource
            ? NegadoctorMath.RenderNegativeView(basis)
            : NegadoctorMath.RenderPositive(basis, settings.Conversion, settings.Finish), ct);
        ct.ThrowIfCancellationRequested();
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (var stream = bitmap.PixelBuffer.AsStream()) await stream.WriteAsync(image.Pixels, ct);
        preview.Source = bitmap;
        SaveCurrent();
    }

    private async Task ExportAsync(CancellationToken ct)
    {
        if (selected is null || prepared is null) throw new InvalidOperationException("Prepare a frame before exporting.");
        if (selected.Kind == RollCaptureKind.Frame && acceptRgb.IsChecked != true)
            throw new InvalidOperationException("Acknowledge the experimental RGB reconstruction before exporting.");
        var calibration = current;
        var captures = batch.IsChecked == true
            ? roll.Captures.Where(c => !c.Discarded && c.Complete && c.Kind == selected.Kind).ToArray() : [selected];
        for (int i = 0; i < captures.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var capture = captures[i];
            message.Text = $"Preparing {i + 1} of {captures.Length}...";
            var options = calibration;
            var basis = prepared;
            if (capture.Id != selected.Id)
            {
                string path = SettingsPath(capture.Id);
                options = File.Exists(path) ? NegativeConversionValidation.Deserialize(await File.ReadAllTextAsync(path, ct)) : NegativeConversionSettings.Default;
                options = options with
                {
                    Light = calibration.Light, BaseSample = null,
                    Conversion = options.Conversion with
                    {
                        FilmType = calibration.Conversion.FilmType, FilmBase = calibration.Conversion.FilmBase,
                        HighlightsBalance = calibration.Conversion.HighlightsBalance,
                        ShadowsBalance = calibration.Conversion.ShadowsBalance, DensityRange = calibration.Conversion.DensityRange
                    }
                };
                basis = await processor.PrepareAsync(capture, ReferencesFor(capture), options.Light, ct);
                if (!File.Exists(path)) options = options with { Conversion = NegativeAnalysis.AutoTone(basis.Basis, options.Conversion, options.Frame.Crop) };
            }
            message.Text = $"Exporting {i + 1} of {captures.Length}...";
            string output = await processor.ExportAsync(basis, options, Path.Combine(roll.DirectoryPath, "Positives"), EncodeJpegAsync, ct);
            message.Text = $"Saved {i + 1} of {captures.Length}: {output}";
        }
    }

    private static async Task EncodeJpegAsync(string tiff, string jpeg, CancellationToken ct)
    {
        using var source = await (await StorageFile.GetFileFromPathAsync(tiff)).OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(source);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new(),
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        ct.ThrowIfCancellationRequested();
        await using var target = new FileStream(jpeg, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var randomAccess = target.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, randomAccess,
            new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(.95f, PropertyType.Single) });
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, decoder.PixelWidth, decoder.PixelHeight,
            decoder.DpiX, decoder.DpiY, pixels.DetachPixelData());
        await encoder.FlushAsync();
        ct.ThrowIfCancellationRequested();
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (operation is not null) return;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        frames.IsEnabled = Dialog.IsPrimaryButtonEnabled = sourceView.IsEnabled = false;
        foreach (var control in adjustments.Children.OfType<Control>()) control.IsEnabled = false;
        Dialog.IsSecondaryButtonEnabled = true;
        progress.Visibility = Visibility.Visible;
        try { active = work(operation.Token); await active; }
        catch (OperationCanceledException) { message.Text = "Conversion canceled. Already exported positives are kept."; }
        catch (Exception ex)
        {
            message.Text = ex.Message;
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilmLight", "Logs");
            try { Directory.CreateDirectory(folder); await File.AppendAllTextAsync(Path.Combine(folder, "processing.log"), $"{DateTimeOffset.Now:O} {ex}\n"); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
            { message.Text += $"\nCould not write processing log: {logError.Message}"; }
        }
        finally
        {
            operation.Dispose(); operation = null; active = null;
            frames.IsEnabled = sourceView.IsEnabled = true;
            Dialog.IsPrimaryButtonEnabled = prepared is not null;
            foreach (var control in adjustments.Children.OfType<Control>()) control.IsEnabled = true;
            Dialog.IsSecondaryButtonEnabled = false;
            progress.Visibility = Visibility.Collapsed;
            if (closing) Dialog.Hide();
        }
    }

    private string SettingsPath(string id) => Path.Combine(roll.DirectoryPath, ".processing", id + ".json");

    private void SaveCurrent()
    {
        if (selected is null || prepared is null) return;
        string json = NegativeConversionValidation.Serialize(current);
        string path = SettingsPath(selected.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, true);
    }

    internal void Cancel() => operation?.Cancel();

    public async ValueTask DisposeAsync()
    {
        redraw.Stop();
        await lifetime.CancelAsync();
        if (active is not null) await active;
        await renderer.DisposeAsync();
        lifetime.Dispose();
    }
}
