using FilmLight.Core;
using FilmLight.Fujifilm;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.System.Display;
using Windows.UI;

namespace FilmLight;

public sealed class MainWindow : Window
{
    private readonly Grid root = new() { RequestedTheme = ElementTheme.Dark };
    private readonly Border light = new() { IsHitTestVisible = false };
    private readonly TextBlock status = new() { FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock error = new() { Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 174, 145)), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock value = new() { FontSize = 23, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MinWidth = 108, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock valueLabel = new() { FontSize = 12, Foreground = new SolidColorBrush(Colors.LightGray), TextAlignment = TextAlignment.Center };
    private readonly Button start = MakeButton("Start RGB", 145);
    private readonly Button minus = MakeButton("\u2212", 64);
    private readonly Button plus = MakeButton("+", 64);
    private readonly Button white = MakeButton("White", 100);
    private readonly Button rgb = MakeButton("RGB", 100);
    private readonly Button reset = MakeButton("Reset", 90);
    private readonly ToggleSwitch repeat = new() { Header = "Repeat sets", MinWidth = 115, VerticalAlignment = VerticalAlignment.Center };
    private readonly DisplayRequest displayRequest = new();
    private readonly Button cameraButton = MakeButton("Camera", 110);
    private readonly Button setupButton = MakeButton("Setup", 90);
    private readonly Button references = MakeButton("Capture references", 180);
    private readonly Button newRoll = MakeButton("New roll", 115);
    private readonly Button openRoll = MakeButton("Open folder", 130);
    private readonly Button processFilm = MakeButton("Process film", 130);
    private NegativeProcessingDialog? processingDialog;
    private readonly TextBlock rollStatus = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock captureStage = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid captureProgress = new() { ColumnSpacing = 4, Height = 4 };
    private readonly StackPanel channelSteps = new() { Orientation = Orientation.Horizontal, Spacing = 16 };
    private readonly StackPanel captureHistory = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly ScrollViewer historyScroller = new()
    {
        HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        MaxHeight = 90
    };
    private readonly Button undoCapture = MakeButton("Undo / retake", 135);
    private readonly Button cancelCapture = MakeButton("Cancel frame", 135);
    private readonly Dictionary<LightChannel, TextBlock> channelLabels = [];
    private readonly Dictionary<LightChannel, Border> channelBars = [];
    private readonly Dictionary<string, CaptureTile> captureTiles = [];
    private string? selectedCaptureId;
    private string? activeRollCaptureId;
    private bool cancelingCapture;
    private ScanPhase scanPhase;
    private string? historyRollDirectory;
    private RollSession? roll;
    private readonly TextBlock cameraStatus = new() { Text = "CAMERA \u00b7 not connected \u00b7 tap Camera to detect USB", FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly Border cameraIndicator = new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Colors.Gray), VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer cameraMonitor = new() { Interval = TimeSpan.FromSeconds(2) };
    private CameraActivity cameraActivity = new(CameraStage.Disconnected, "Not connected");
    private bool checkingCamera;
    private CameraClient? camera;
    private bool connecting;
    private Task? activeCapture;
    private bool closeRequested;
    private bool closingReady;
    private int downloadedFiles;
    private AppSettings settings = new();
    private CancellationTokenSource? captureCancellation;
    private bool rgbMode;
    private bool running;
    private bool closed;
    private bool wakeActive;
    private bool windowActive;
    private bool initializing = true;

    public MainWindow()
    {
        Title = "Film Light";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 850));
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(light);
        var toolbar = new StackPanel { Spacing = 8, Padding = new Thickness(20, 12, 20, 12) };
        var toolbarBackground = new Border { Background = new SolidColorBrush(Color.FromArgb(255, 18, 20, 24)), Child = toolbar };
        Grid.SetRow(toolbarBackground, 1);
        root.Children.Add(toolbarBackground);

        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "FILM LIGHT", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(status, 1);
        header.Children.Add(status);
        var fullscreen = MakeButton("Full screen", 130);
        fullscreen.Height = 44;
        fullscreen.Click += async (_, _) =>
        {
            if (running)
            {
                Pause("Paused for fullscreen change.");
                if (camera is not null && activeCapture is not null) await activeCapture;
            }
            var full = AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen;
            AppWindow.SetPresenter(full ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
            fullscreen.Content = full ? "Exit full screen" : "Full screen";
        };
        Grid.SetColumn(fullscreen, 2);
        header.Children.Add(fullscreen);
        toolbar.Children.Add(header);
        var cameraStrip = new Grid { ColumnSpacing = 10 };
        cameraStrip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        cameraStrip.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        cameraStrip.Children.Add(cameraIndicator);
        Grid.SetColumn(cameraStatus, 1);
        cameraStrip.Children.Add(cameraStatus);
        toolbar.Children.Add(cameraStrip);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        controls.Children.Add(white);
        controls.Children.Add(rgb);
        controls.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 8, 4, 8), Background = new SolidColorBrush(Colors.DimGray) });
        controls.Children.Add(minus);
        var readout = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        readout.Children.Add(valueLabel);
        readout.Children.Add(value);
        controls.Children.Add(readout);
        controls.Children.Add(plus);
        controls.Children.Add(reset);
        controls.Children.Add(repeat);
        controls.Children.Add(start);
        controls.Children.Add(cameraButton);
        controls.Children.Add(references);
        references.Click += async (_, _) =>
        {
            activeCapture = StartAsync(RollCaptureKind.References);
            await activeCapture;
            activeCapture = null;
        };
        newRoll.Click += async (_, _) => await NewRollAsync();
        openRoll.Click += async (_, _) => await OpenRollAsync();
        processFilm.Click += async (_, _) =>
        {
            if (roll is null || running || connecting || camera?.Faulted == true) return;
            Pause("Paused for film processing.");
            try
            {
                await using var dialog = new NegativeProcessingDialog(roll);
                dialog.Dialog.XamlRoot = root.XamlRoot;
                processingDialog = dialog;
                await dialog.Dialog.ShowAsync();
            }
            catch (Exception ex) { ShowError($"Could not process film: {ex.Message}"); }
            finally { processingDialog = null; }
        };
        cameraButton.Click += async (_, _) => await ShowCameraAsync();
        setupButton.Click += async (_, _) => await ShowSetupAsync();
        controls.Children.Add(setupButton);
        toolbar.Children.Add(new ScrollViewer
        {
            Content = controls,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        toolbar.Children.Add(error);
        var rollStrip = new Grid { ColumnSpacing = 12 };
        rollStrip.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        rollStrip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        rollStrip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        rollStrip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        rollStatus.VerticalAlignment = VerticalAlignment.Center;
        rollStrip.Children.Add(rollStatus);
        newRoll.Height = openRoll.Height = processFilm.Height = 44;
        Grid.SetColumn(newRoll, 1);
        Grid.SetColumn(openRoll, 2);
        Grid.SetColumn(processFilm, 3);
        rollStrip.Children.Add(newRoll);
        rollStrip.Children.Add(openRoll);
        rollStrip.Children.Add(processFilm);
        toolbar.Children.Add(rollStrip);
        var progressStrip = new Grid { ColumnSpacing = 12 };
        progressStrip.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        progressStrip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        progressStrip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        progressStrip.Children.Add(captureStage);
        undoCapture.Height = cancelCapture.Height = 36;
        undoCapture.FontSize = cancelCapture.FontSize = 14;
        Grid.SetColumn(undoCapture, 1);
        Grid.SetColumn(cancelCapture, 2);
        progressStrip.Children.Add(undoCapture);
        progressStrip.Children.Add(cancelCapture);
        undoCapture.Click += (_, _) => RetakeSelectedCapture();
        cancelCapture.Click += async (_, _) => await CancelCurrentCaptureAsync();
        ToolTipService.SetToolTip(undoCapture, "Reject the selected capture and queue a full retake. Original files stay on disk.");
        toolbar.Children.Add(progressStrip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(captureProgress, "Saved color progress");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(captureProgress,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content);
        toolbar.Children.Add(captureProgress);
        foreach (var channel in Enum.GetValues<LightChannel>())
        {
            captureProgress.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var bar = new Border { Height = 4, CornerRadius = new CornerRadius(2) };
            Grid.SetColumn(bar, channelBars.Count);
            channelBars.Add(channel, bar);
            captureProgress.Children.Add(bar);
            var label = new TextBlock { FontSize = 11 };
            channelLabels.Add(channel, label);
            channelSteps.Children.Add(label);
        }
        toolbar.Children.Add(channelSteps);
        historyScroller.Content = captureHistory;
        toolbar.Children.Add(historyScroller);
        Content = root;

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += async (_, args) =>
        {
            args.Handled = true;
            Pause("Paused. Restart begins a fresh RGB set.");
            if (camera is not null && activeCapture is not null) await activeCapture;
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            fullscreen.Content = "Full screen";
        };
        root.KeyboardAccelerators.Add(escape);
        white.Click += (_, _) => SetMode(false);
        rgb.Click += (_, _) => SetMode(true);
        minus.Click += (_, _) => Adjust(-1);
        plus.Click += (_, _) => Adjust(1);
        reset.Click += (_, _) =>
        {
            settings = rgbMode ? settings with { Interval = 5 } : settings with { Temperature = 5000 };
            Persist();
            Refresh();
        };
        repeat.Toggled += (_, _) =>
        {
            if (initializing) return;
            settings = settings with { Repeat = repeat.IsOn };
            Persist();
        };
        start.Click += async (_, _) =>
        {
            if (running) Pause("Paused. Light held; restart begins a fresh RGB set.");
            else
            {
                activeCapture = StartAsync();
                await activeCapture;
                activeCapture = null;
            }
        };
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                windowActive = false;
                Pause("Paused because Film Light lost focus. Restart when ready.");
                if (camera?.Faulted != true && (!running || camera is null)) ReleaseWake();
            }
            else
            {
                windowActive = true;
                AcquireWake();
            }
        };
        Closed += (_, _) =>
        {
            closed = true;
            cameraMonitor.Stop();
            captureCancellation?.Cancel();
            ReleaseWake();
        };
        AppWindow.Closing += async (_, args) =>
        {
            if (closingReady) return;
            args.Cancel = true;
            if (processingDialog is not null)
            {
                processingDialog.Cancel();
                ShowError("Close film processing before closing Film Light.");
                return;
            }
            if (connecting || camera?.Faulted == true)
            {
                ShowError(connecting ? "Wait for camera connection to finish before closing."
                    : "Confirm the camera is idle and disconnect using Re-arm before closing.");
                return;
            }
            if (closeRequested) return;
            closeRequested = true;
            Pause("Closing after the current camera exposure/download finishes.");
            if (activeCapture is not null) await activeCapture;
            try
            {
                if (camera is not null)
                {
                    await camera.DisconnectAsync();
                    await camera.DisposeAsync();
                }
                closingReady = true;
                Close();
            }
            catch (CameraSdkException ex)
            {
                closeRequested = false;
                ShowError($"Could not close camera cleanly: {ex.Message}. Use Camera to confirm it is idle and disconnect.");
            }
        };
        try { settings = AppSettings.Load(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowError($"Could not load settings: {ex.Message}");
        }
        if (settings.ActiveRollDirectory is { } activeRoll)
        {
            try
            {
                roll = RollSession.Load(activeRoll);
                if (roll.Pending is { } pending)
                    rgbMode = pending.Kind != RollCaptureKind.White;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                ShowError($"Could not restore roll: {ex.Message}. Start a new roll to continue; existing files are untouched.");
            }
        }
        repeat.IsOn = settings.Repeat;
        initializing = false;
        status.Text = "Uniform white \u00b7 nominal 5000 K";
        Refresh();
        if (rgbMode && roll?.Pending is { } unfinished)
        {
            ShowChannel(new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue }
                .First(channel => !unfinished.Channels.ContainsKey(channel)));
            status.Text = "Unfinished capture restored \u00b7 confirm the camera is idle before connecting and resuming.";
        }
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        fullscreen.Content = "Exit full screen";
        cameraMonitor.Tick += async (_, _) => await CheckCameraAsync();
        cameraMonitor.Start();
    }

    private static Button MakeButton(string text, double width) => new()
    {
        Content = text, MinWidth = width, Height = 60, FontSize = 18,
        CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 14, 8)
    };

    private void SetMode(bool useRgb)
    {
        rgbMode = useRgb;
        status.Text = useRgb
            ? camera is null ? "Timer mode \u00b7 camera not connected" : $"{camera.Connection?.Model} \u00b7 USB capture + download"
            : "Uniform white \u00b7 nominal color temperature";
        Refresh();
        if (useRgb) ShowChannel(LightChannel.Red);
    }

    private void Adjust(int direction)
    {
        settings = rgbMode
            ? settings with { Interval = Math.Clamp(settings.Interval + direction, 1, 120) }
            : settings with { Temperature = Math.Clamp(settings.Temperature + direction * 100, 3000, 7500) };
        Persist();
        Refresh();
    }

    private void Refresh()
    {
        var locked = running || connecting || cancelingCapture || camera?.Faulted == true || cameraActivity.Stage == CameraStage.Busy;
        white.IsEnabled = rgb.IsEnabled = reset.IsEnabled = !locked;
        if (roll?.Pending is not null)
            white.IsEnabled = rgb.IsEnabled = reset.IsEnabled = false;
        repeat.IsEnabled = !locked;
        cameraButton.IsEnabled = !running && !connecting && !cancelingCapture;
        setupButton.IsEnabled = !locked;
        cameraButton.Content = connecting ? "Connecting..." : camera?.Faulted == true ? "Re-arm" : camera is null ? "Camera" : "X-T5";
        repeat.Visibility = rgbMode ? Visibility.Visible : Visibility.Collapsed;
        start.Visibility = rgbMode || camera is not null ? Visibility.Visible : Visibility.Collapsed;
        start.IsEnabled = !connecting && !cancelingCapture && camera?.Faulted != true && (running || cameraActivity.Stage != CameraStage.Busy);
        if (camera is null && roll?.Pending is not null) start.IsEnabled = false;
        references.Visibility = rgbMode && camera is not null ? Visibility.Visible : Visibility.Collapsed;
        references.IsEnabled = !locked && roll?.HasReferences != true
            && (roll?.Pending is null || roll.Pending.Kind == RollCaptureKind.References);
        newRoll.IsEnabled = !locked;
        openRoll.IsEnabled = !running && !connecting && roll is not null;
        processFilm.IsEnabled = !locked && roll?.Captures.Any(c => c.Complete && !c.Discarded
            && c.Kind != RollCaptureKind.References) == true;
        rollStatus.Text = roll is null ? "A roll folder is created automatically when you connect the camera."
            : $"{Path.GetFileName(roll.DirectoryPath)} \u00b7 references {(roll.HasReferences ? "saved" : "not captured")} \u00b7 {roll.CompletedFrames} frames"
                + (roll.Pending is { } pending ? $" \u00b7 unfinished {pending.Kind}: {pending.Channels.Count} colors saved" : "");
        valueLabel.Text = rgbMode ? camera is null ? "SECONDS / COLOR" : "USB TETHERED" : "NOMINAL WHITE";
        value.Text = rgbMode ? camera is null ? $"{settings.Interval} s" : "AUTO" : $"{settings.Temperature} K";
        minus.IsEnabled = !locked && roll?.Pending is null && (rgbMode ? camera is null && settings.Interval > 1 : settings.Temperature > 3000);
        plus.IsEnabled = !locked && roll?.Pending is null && (rgbMode ? camera is null && settings.Interval < 120 : settings.Temperature < 7500);
        white.Background = new SolidColorBrush(rgbMode ? Color.FromArgb(255, 40, 43, 49) : Color.FromArgb(255, 65, 76, 90));
        rgb.Background = new SolidColorBrush(rgbMode ? Color.FromArgb(255, 65, 76, 90) : Color.FromArgb(255, 40, 43, 49));
        start.Content = running ? "Pause" : camera is not null && roll?.Pending is { } unfinished
            ? unfinished.Channels.Count == 0 ? $"Capture {CaptureLabel(unfinished).ToLowerInvariant()}" : "Resume capture"
            : rgbMode ? camera is null ? "Start RGB" : "Capture frame" : "Capture white";
        if (!rgbMode)
            light.Background = new SolidColorBrush(WhiteColor(settings.Temperature));
        RefreshRollView();
    }

    // A relative RGB tint around full white, not a calibrated display temperature.
    private static Color WhiteColor(int temperature)
    {
        var offset = (temperature - 5000) / 2500.0;
        return offset < 0
            ? Color.FromArgb(255, 255, (byte)(255 + offset * 35), (byte)(255 + offset * 105))
            : Color.FromArgb(255, (byte)(255 - offset * 65), (byte)(255 - offset * 25), 255);
    }

    private void ShowChannel(LightChannel channel) => light.Background = new SolidColorBrush(channel switch
    {
        LightChannel.Red => Colors.Red,
        LightChannel.Green => Color.FromArgb(255, 0, 255, 0),
        LightChannel.Blue => Colors.Blue,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    });

    private async Task StartAsync(RollCaptureKind? requestedKind = null)
    {
        if (captureCancellation is not null || connecting || camera?.Faulted == true || (!rgbMode && camera is null)) return;
        using var cancellation = new CancellationTokenSource();
        captureCancellation = cancellation;
        running = true;
        Refresh();
        try
        {
            RollCaptureKind? kind = null;
            if (camera is not null)
            {
                if (roll is null) throw new InvalidOperationException("Start a new roll before capturing.");
                kind = requestedKind ?? roll.Pending?.Kind ?? (rgbMode ? RollCaptureKind.Frame : RollCaptureKind.White);
                var capture = roll.BeginCapture(kind.Value);
                activeRollCaptureId = selectedCaptureId = capture.Id;
                downloadedFiles = capture.Channels.Values.Sum(files => files.Length);
                camera.BeginScan(capture.Id);
                scanPhase = ScanPhase.Countdown;
                RefreshRollView();
            }
            else downloadedFiles = 0;
            error.Text = "";
            if (!rgbMode && camera is not null)
            {
                scanPhase = ScanPhase.Holding;
                status.Text = "White \u00b7 exposing and downloading...";
                CaptureCompleted(LightChannel.White, await camera.CaptureAsync(LightChannel.White, cancellation.Token));
                status.Text = $"White complete \u00b7 {downloadedFiles} files saved";
                return;
            }
            var setNumber = 1;
            do
            {
                await new ScanSequence(camera: camera, completedChannels: roll?.Pending?.Channels.Keys).RunAsync(
                    new(settings.Interval, camera is null && settings.Repeat, setNumber == 1 ? 3 : 0),
                    progress =>
                    {
                        scanPhase = progress.Phase;
                        var currentSet = camera is null ? progress.SetNumber : setNumber;
                        status.Text = progress.Phase switch
                        {
                            ScanPhase.Countdown => $"Starting in {Math.Ceiling(progress.Remaining.TotalSeconds):0} s \u00b7 " +
                                (camera is null ? "start your intervalometer" : "USB camera ready"),
                            ScanPhase.Holding => $"{progress.Channel} \u00b7 set {currentSet} \u00b7 " +
                                (camera is null ? $"{progress.Remaining.TotalSeconds:0.0} s" : $"exposing / downloading \u00b7 {downloadedFiles} files saved"),
                            _ => $"Set {currentSet} complete \u00b7 blue held" + (camera is null ? "" : $" \u00b7 {downloadedFiles} files saved")
                        };
                        RefreshCaptureProgress();
                    },
                    (channel, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        ShowChannel(channel);
                        return Task.CompletedTask;
                    }, CaptureCompleted, cancellation.Token);
                if (camera is null || !settings.Repeat || kind != RollCaptureKind.Frame) break;
                cancellation.Token.ThrowIfCancellationRequested();
                var next = roll!.BeginCapture(RollCaptureKind.Frame);
                activeRollCaptureId = selectedCaptureId = next.Id;
                camera.BeginScan(next.Id);
                downloadedFiles = 0;
                setNumber++;
                RefreshRollView();
            } while (true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (camera is not null) status.Text = "Paused \u00b7 saved colors and references kept. Resume when ready.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException)
        {
            ShowError($"Scan stopped: {ex.Message}");
            status.Text = "Scan failed \u00b7 light held; saved colors and references kept. Re-arm, reconnect, then resume.";
        }
        finally
        {
            captureCancellation = null;
            running = false;
            if (!windowActive && camera?.Faulted != true) ReleaseWake();
            if (!closed) Refresh();
        }
    }

    private void Pause(string message)
    {
        if (!running) return;
        captureCancellation?.Cancel();
        status.Text = camera is null ? message : "Pausing \u00b7 light held until current exposure/download finishes.";
    }

    private void CaptureCompleted(LightChannel channel, CaptureResult result)
    {
        if (camera is not null)
        {
            var currentRoll = roll ?? throw new InvalidOperationException("No active roll.");
            selectedCaptureId = currentRoll.Pending?.Id;
            currentRoll.RecordCapture(channel, result);
            RefreshRollView();
        }
        downloadedFiles += result.FilePaths.Count;
        status.Text = $"{channel} downloaded \u00b7 {downloadedFiles} files saved to {roll?.DirectoryPath ?? settings.OutputPath}";
    }

    private static string CaptureLabel(RollCapture capture) => capture.Kind switch
    {
        RollCaptureKind.References => "References",
        RollCaptureKind.Frame => $"Frame {capture.FrameNumber}",
        _ => "White"
    };

    private void RefreshRollView()
    {
        if (closed) return;
        if (historyRollDirectory != roll?.DirectoryPath)
        {
            captureTiles.Clear();
            captureHistory.Children.Clear();
            selectedCaptureId = activeRollCaptureId = null;
            historyRollDirectory = roll?.DirectoryPath;
        }
        var captures = roll?.Captures.Where(capture => !capture.Discarded)
            .OrderBy(capture => capture.Kind == RollCaptureKind.References ? 0 : 1)
            .ThenBy(capture => capture.Kind == RollCaptureKind.Frame ? capture.FrameNumber : int.MaxValue)
            .ToArray() ?? [];
        if (!captures.Any(capture => capture.Id == selectedCaptureId))
            selectedCaptureId = roll?.Pending?.Id ?? captures.LastOrDefault()?.Id;
        foreach (var id in captureTiles.Keys.Where(id => !captures.Any(capture => capture.Id == id)).ToArray())
            captureTiles.Remove(id);
        foreach (var capture in captures)
        {
            if (!captureTiles.TryGetValue(capture.Id, out var tile))
            {
                var image = new Image { Width = 72, Height = 40, Stretch = Stretch.UniformToFill };
                var placeholder = new TextBlock { Text = "No image", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var preview = new Grid { Width = 72, Height = 40, Background = new SolidColorBrush(Color.FromArgb(255, 28, 30, 34)) };
                preview.Children.Add(image);
                preview.Children.Add(placeholder);
                var label = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
                var detail = new TextBlock { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new SolidColorBrush(Colors.LightGray) };
                var content = new StackPanel { Spacing = 1 };
                content.Children.Add(preview);
                content.Children.Add(label);
                content.Children.Add(detail);
                var button = new Button { Content = content, Width = 86, Height = 78, Padding = new Thickness(4), CornerRadius = new CornerRadius(5) };
                var id = capture.Id;
                button.Click += (_, _) =>
                {
                    selectedCaptureId = id;
                    RefreshRollView();
                };
                tile = new(button, image, placeholder, label, detail);
                captureTiles.Add(id, tile);
            }
            tile.Label.Text = CaptureLabel(capture);
            tile.Detail.Text = capture.Complete ? "Saved" : $"{capture.Channels.Count}/{(capture.Kind == RollCaptureKind.White ? 1 : 3)} saved";
            tile.Button.Background = new SolidColorBrush(capture.Id == selectedCaptureId
                ? Color.FromArgb(255, 65, 76, 90) : Color.FromArgb(255, 36, 39, 44));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile.Button, CaptureLabel(capture));
            var path = capture.Channels.OrderBy(pair => pair.Key == LightChannel.Green ? 0 : 1)
                .SelectMany(pair => pair.Value)
                .OrderBy(file => Path.GetExtension(file).Equals(".JPG", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (path is not null && path != tile.PreviewPath)
            {
                tile.PreviewPath = path;
                tile.Image.Source = null;
                tile.Placeholder.Text = "Loading...";
                tile.Placeholder.Visibility = Visibility.Visible;
                _ = LoadThumbnailAsync(capture.Id, tile, path);
            }
        }
        var buttons = captures.Select(capture => captureTiles[capture.Id].Button).ToArray();
        if (!captureHistory.Children.SequenceEqual(buttons))
        {
            captureHistory.Children.Clear();
            foreach (var button in buttons) captureHistory.Children.Add(button);
            if (selectedCaptureId is { } selected && captureTiles.TryGetValue(selected, out var tile))
                DispatcherQueue.TryEnqueue(() => { if (!closed) tile.Button.StartBringIntoView(); });
        }
        historyScroller.Visibility = captures.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshCaptureProgress();
    }

    private async Task LoadThumbnailAsync(string captureId, CaptureTile tile, string path)
    {
        try
        {
            await using var stream = await CapturePreview.OpenAsync(path);
            using var source = stream.AsRandomAccessStream();
            var bitmap = new BitmapImage { DecodePixelWidth = 72 };
            await bitmap.SetSourceAsync(source);
            if (!closed && captureTiles.TryGetValue(captureId, out var current) && current == tile && tile.PreviewPath == path)
            {
                tile.Image.Source = bitmap;
                tile.Placeholder.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or NotSupportedException or ArgumentException)
        {
            if (!closed && captureTiles.TryGetValue(captureId, out var current) && current == tile && tile.PreviewPath == path)
            {
                tile.Placeholder.Text = "No preview";
                tile.Placeholder.Visibility = Visibility.Visible;
                ToolTipService.SetToolTip(tile.Button, $"Thumbnail unavailable: {ex.Message}");
                ShowError($"Thumbnail unavailable for {tile.Label.Text}: {ex.Message}. Capture files are unchanged.");
            }
        }
    }

    private void RefreshCaptureProgress()
    {
        if (closed) return;
        var captures = roll?.Captures.Where(capture => !capture.Discarded).ToArray() ?? [];
        var selected = captures.FirstOrDefault(capture => capture.Id == selectedCaptureId);
        var current = running || cancelingCapture
            ? captures.FirstOrDefault(capture => capture.Id == activeRollCaptureId)
            : roll?.Pending ?? selected;
        var total = current?.Kind == RollCaptureKind.White ? 1 : 3;
        if (current is null)
        {
            captureStage.Text = connecting ? "Connecting camera..."
                : roll is null ? "Create roll > Connect camera > References (optional) > Capture frames"
                : camera is null ? "Roll ready \u00b7 connect camera"
                : "Ready \u00b7 capture references (optional) or frame 1";
        }
        else
        {
            var stage = cancelingCapture ? "Canceling \u00b7 waiting for camera to finish"
                : running ? scanPhase == ScanPhase.Countdown ? status.Text
                    : cameraActivity.Channel.HasValue ? $"{cameraActivity.Channel} \u00b7 {cameraActivity.Message}" : status.Text
                : camera?.Faulted == true ? "Camera fault \u00b7 re-arm before retaking"
                : current.Complete ? "Saved"
                : camera is null ? "Connect camera to continue"
                : current.Channels.Count == 0 ? "Ready to capture" : "Paused \u00b7 resume or undo to retake";
            captureStage.Text = $"{CaptureLabel(current)} \u00b7 {stage} \u00b7 {current.Channels.Count}/{total} colors saved";
        }
        channelSteps.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (channel, label) in channelLabels)
        {
            label.Visibility = (total == 1) == (channel == LightChannel.White) ? Visibility.Visible : Visibility.Collapsed;
            var bar = channelBars[channel];
            bar.Visibility = label.Visibility;
            captureProgress.ColumnDefinitions[(int)channel].Width = new GridLength(
                bar.Visibility == Visibility.Visible ? 1 : 0, GridUnitType.Star);
            var saved = current?.Channels.ContainsKey(channel) == true;
            var active = running && !saved && scanPhase == ScanPhase.Holding && cameraActivity.Channel == channel;
            label.Text = $"{channel}: {(saved ? "saved" : active ? "capturing" : "pending")}";
            label.Foreground = new SolidColorBrush(saved ? Colors.LightGreen : active ? Colors.Gold : Colors.Gray);
            bar.Background = new SolidColorBrush(saved ? Colors.LightGreen : active ? Colors.Gold : Color.FromArgb(255, 50, 54, 62));
        }
        undoCapture.IsEnabled = !running && !connecting && !cancelingCapture && camera?.Faulted != true
            && cameraActivity.Stage != CameraStage.Busy && selected is not null
            && (roll?.Pending is null || roll.Pending.Channels.Count == 0 || roll.Pending.Id == selected.Id);
        cancelCapture.Visibility = running && camera is not null ? Visibility.Visible : Visibility.Collapsed;
        cancelCapture.IsEnabled = !cancelingCapture && activeRollCaptureId is not null;
        cancelCapture.Content = current?.Kind == RollCaptureKind.References ? "Cancel references"
            : current?.Kind == RollCaptureKind.White ? "Cancel white" : "Cancel frame";
    }

    private void RetakeSelectedCapture()
    {
        if (!undoCapture.IsEnabled || roll is null || selectedCaptureId is null) return;
        try
        {
            var replacement = roll.RetakeCapture(selectedCaptureId);
            selectedCaptureId = activeRollCaptureId = replacement.Id;
            rgbMode = replacement.Kind != RollCaptureKind.White;
            status.Text = $"{CaptureLabel(replacement)} rejected \u00b7 original files kept. Capture again when aligned.";
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError($"Could not undo capture: {ex.Message}");
        }
    }

    private async Task CancelCurrentCaptureAsync()
    {
        if (!running || cancelingCapture || camera is null || roll is null || activeRollCaptureId is null) return;
        var currentRoll = roll;
        var captureId = activeRollCaptureId;
        var captureTask = activeCapture;
        cancelingCapture = true;
        Pause("Canceling the current capture.");
        Refresh();
        try
        {
            if (captureTask is not null) await captureTask;
            if (closed) return;
            var replacement = currentRoll.RetakeCapture(captureId);
            selectedCaptureId = activeRollCaptureId = replacement.Id;
            status.Text = $"{CaptureLabel(replacement)} canceled \u00b7 original files kept; all colors will be retaken.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError($"Could not cancel capture: {ex.Message}");
        }
        finally
        {
            cancelingCapture = false;
            if (!closed) Refresh();
        }
    }

    private sealed record CaptureTile(Button Button, Image Image, TextBlock Placeholder, TextBlock Label, TextBlock Detail)
    {
        public string? PreviewPath { get; set; }
    }

    private void UpdateCameraActivity(CameraActivity activity)
    {
        if (closed) return;
        cameraActivity = activity;
        cameraStatus.Text = $"CAMERA \u00b7 {(activity.Channel.HasValue ? $"{activity.Channel} \u00b7 " : "")}{activity.Message}";
        cameraIndicator.Background = new SolidColorBrush(activity.Stage switch
        {
            CameraStage.Ready => Colors.LightGreen,
            CameraStage.Disconnected => Colors.Gray,
            CameraStage.Faulted => Colors.OrangeRed,
            _ => Colors.Gold
        });
        RefreshCaptureProgress();
    }

    private async Task CheckCameraAsync()
    {
        if (closed || closeRequested || checkingCamera || connecting || running || cancelingCapture || camera is null || camera.Faulted) return;
        checkingCamera = true;
        try { await camera.CheckConnectionAsync(); }
        catch (CameraSdkException ex)
        {
            ShowError($"Camera connection lost: {ex.Message}");
            status.Text = "Camera connection lost \u00b7 light held";
        }
        finally
        {
            checkingCamera = false;
            if (!closed) Refresh();
        }
    }

    private async Task ShowCameraAsync()
    {
        if (running || connecting) return;
        if (camera is not null)
        {
            var faulted = camera.Faulted;
            var confirm = new CheckBox { Content = "I confirm the camera is idle (no exposure or processing)." };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = camera.Faulted
                    ? "Camera connection faulted. Keep the light held until the camera is idle. Resolve any exposure/USB problem before disconnecting; reconnect will preserve queued images in Recovery."
                    : $"{camera.Connection?.Model}\nFirmware: {camera.Connection?.Firmware}\nSDK: {camera.Connection?.SdkVersion}\nPhotos: {roll?.DirectoryPath ?? settings.OutputPath}\nSaved references and unfinished captures survive disconnect."
            });
            content.Children.Add(confirm);
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot, RequestedTheme = ElementTheme.Dark,
                Title = camera.Faulted ? "Re-arm camera" : "USB camera connected",
                Content = content, PrimaryButtonText = faulted ? "Reconnect" : "Disconnect",
                SecondaryButtonText = faulted ? "Disconnect" : "", CloseButtonText = "Done",
                IsPrimaryButtonEnabled = false, IsSecondaryButtonEnabled = false
            };
            confirm.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = true;
            confirm.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.None) return;
            connecting = true;
            Refresh();
            try
            {
                try { await camera.DisconnectAsync(); }
                finally { await camera.DisposeAsync(); }
                camera = null;
                UpdateCameraActivity(new(CameraStage.Disconnected, "USB camera disconnected \u00b7 timer mode available"));
                status.Text = "Camera disconnected \u00b7 timer mode available";
                if (faulted && choice == ContentDialogResult.Primary)
                {
                    await ConnectRollCameraAsync();
                    error.Text = "";
                    status.Text = "Camera reconnected \u00b7 references kept; resume the unfinished capture when ready.";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // The operator confirmed idle; the helper is disposed even if SDK teardown failed.
                camera = null;
                UpdateCameraActivity(new(CameraStage.Disconnected, "USB camera disconnected \u00b7 see error details"));
                ShowError($"Camera disconnect/reconnect failed: {ex.Message}");
            }
            finally { connecting = false; Refresh(); }
            return;
        }

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Connect the X-T5 by USB in USB TETHER SHOOTING AUTO mode. Set M focus and S drive. Choose shutter speed, aperture, ISO and white balance on the camera; Film Light uses camera-priority triggering and never sets exposure values. Keep settings fixed within each RGB set.\n\nSelect RAW, JPEG or RAW+JPEG on the camera (not HEIF). Use a writable card and close other tether apps. Photos download automatically; existing queued images are preserved in Recovery. Do not press the camera shutter during app captures."
        });
        var sdkPath = new TextBlock { Text = settings.SdkPath, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var outputPath = new TextBlock { Text = settings.OutputPath, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var sdkFolder = MakeButton("SDK folder", 160);
        var outputFolder = MakeButton("Change roll location", 200);
        sdkFolder.Click += async (_, _) =>
        {
            var path = await PickFolderAsync();
            if (path is null) return;
            settings = settings with { SdkDirectory = path };
            sdkPath.Text = path;
            Persist();
        };
        outputFolder.Click += async (_, _) =>
        {
            var path = await PickFolderAsync();
            if (path is null) return;
            settings = settings with { OutputDirectory = path };
            outputPath.Text = path;
            Persist();
        };
        panel.Children.Add(sdkFolder);
        panel.Children.Add(sdkPath);
        panel.Children.Add(outputFolder);
        panel.Children.Add(outputPath);
        var ready = new CheckBox { Content = "Camera is idle and configured; no other tether app is running." };
        panel.Children.Add(ready);
        var connectDialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "Connect Fujifilm X-T5",
            Content = new ScrollViewer { Content = panel, MaxHeight = 500 },
            PrimaryButtonText = "Connect USB", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false
        };
        ready.Checked += (_, _) => connectDialog.IsPrimaryButtonEnabled = true;
        ready.Unchecked += (_, _) => connectDialog.IsPrimaryButtonEnabled = false;
        if (await connectDialog.ShowAsync() != ContentDialogResult.Primary) return;
        connecting = true;
        UpdateCameraActivity(new(CameraStage.Loading, "Starting USB detection"));
        Refresh();
        try
        {
            if (roll is null)
            {
                if (settings.ActiveRollDirectory is not null)
                    throw new InvalidDataException("The saved roll could not be restored. Start a new roll explicitly; existing photos will not be overwritten.");
                CreateRoll();
            }
            await ConnectRollCameraAsync();
            error.Text = "";
            status.Text = $"{camera!.Connection?.Model} connected \u00b7 firmware {camera.Connection?.Firmware} \u00b7 captures saved to tablet + card";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError($"Camera connection failed: {ex.Message}");
            if (cameraActivity.Stage is not (CameraStage.Disconnected or CameraStage.Faulted))
                UpdateCameraActivity(new(CameraStage.Faulted, "Connection failed \u00b7 see details"));
            status.Text = "Camera not connected \u00b7 timer mode unchanged";
        }
        finally
        {
            connecting = false;
            Refresh();
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        try { return (await picker.PickSingleFolderAsync())?.Path; }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ShowError($"Could not open folder picker: {ex.Message}");
            return null;
        }

    }

    private async Task ConnectRollCameraAsync()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "CameraHost", "FilmLight.CameraHost.exe");
        if (!File.Exists(helper))
            throw new FileNotFoundException("Camera helper is missing. Build/publish with tools\\Publish.ps1.");
        var currentRoll = roll ?? throw new InvalidOperationException("No active roll.");
        CameraClient? pending = new(helper, currentRoll.DirectoryPath, UpdateCameraActivity);
        try
        {
            await pending.ConnectAsync(settings.SdkPath);
            camera = pending;
            pending = null;
        }
        finally
        {
            if (pending is not null) await pending.DisposeAsync();
        }
    }

    private void CreateRoll()
    {
        var created = RollSession.Create(settings.OutputPath);
        var updated = settings with { ActiveRollDirectory = created.DirectoryPath };
        updated.Save();
        roll = created;
        settings = updated;
    }

    private async Task NewRollAsync()
    {
        if (running || connecting || camera?.Faulted == true || cameraActivity.Stage == CameraStage.Busy) return;
        if (roll is not null || settings.ActiveRollDirectory is not null)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "Start a new roll?",
                Content = "The current roll's photos and references stay on disk. The new roll gets its own folder and references.",
                PrimaryButtonText = "New roll", CloseButtonText = "Cancel"
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        connecting = true;
        Refresh();
        try
        {
            var reconnect = camera is not null;
            if (camera is not null)
            {
                await camera.DisconnectAsync();
                await camera.DisposeAsync();
                camera = null;
            }
            CreateRoll();
            if (reconnect) await ConnectRollCameraAsync();
            error.Text = "";
            status.Text = camera is null ? "New roll ready \u00b7 connect the camera, then capture references or frames."
                : "New roll ready \u00b7 camera connected; capture references or frames.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError($"Could not start a new roll: {ex.Message}");
        }
        finally { connecting = false; Refresh(); }
    }

    private async Task OpenRollAsync()
    {
        if (roll is null || running || connecting) return;
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(roll.DirectoryPath);
            if (!await Launcher.LaunchFolderAsync(folder))
                ShowError("Windows could not open the roll folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowError($"Could not open the roll folder: {ex.Message}");
        }
    }

    private void Persist()
    {
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not save settings: {ex.Message}");
        }
    }

    private void ShowError(string message) => error.Text = message;

    private void AcquireWake()
    {
        if (wakeActive || closed) return;
        try
        {
            displayRequest.RequestActive();
            wakeActive = true;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ShowError($"Could not keep the display awake: {ex.Message}");
        }
    }

    private void ReleaseWake()
    {
        if (!wakeActive) return;
        try
        {
            displayRequest.RequestRelease();
            wakeActive = false;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ShowError($"Could not release the display wake request: {ex.Message}");
        }
    }

    private async Task ShowSetupAsync()
    {
        Pause("Paused for setup. Restart begins a fresh RGB set.");
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = "Before scanning",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Set Windows brightness manually and turn off automatic brightness, adaptive color, Night light and HDR. No display settings are changed by this app.\n\nWhite starts at full RGB white, labelled nominal 5000 K. Temperature controls tint, not a calibrated light spectrum.\n\nTimer RGB: start your intervalometer during the 3-second countdown and align exposures near the middle of each hold.\n\nUSB RGB: connect through Camera. Film Light triggers each exposure and downloads the complete RAW/JPEG set before changing color. White mode also offers Capture white. Camera settings determine file formats; files are saved to tablet and card.\n\nPause holds the light and waits for a triggered exposure/download to finish. Losing focus pauses. After a camera fault, confirm the camera is idle before re-arming. Escape exits fullscreen and pauses."
            },
            PrimaryButtonText = "Display settings",
            CloseButtonText = "Done"
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (!await Launcher.LaunchUriAsync(new Uri("ms-settings:display")))
                ShowError("Windows could not open Display settings.");
        }
    }
}
