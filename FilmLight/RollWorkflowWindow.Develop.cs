using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using FilmLight.Workflow;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.System;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace FilmLight;

public sealed partial class RollWorkflowWindow
{
    private sealed record DevelopParameter(string Label, double Min, double Max, double Step, double Default, bool Instant,
        Func<DevelopRecipe, double> Get, Func<DevelopRecipe, double, DevelopRecipe> Set, Func<double, string> Format);

    private sealed record DevelopSlider(Slider Slider, DevelopParameter Param, TextBlock? Value);

    private static DevelopParameter Gain(string label, Func<DevelopRecipe, double> get, Func<DevelopRecipe, double, DevelopRecipe> set) =>
        new(label, Math.Log2(.1), 2, .01, 0, true, r => Math.Log2(get(r)),
            (r, v) => set(r, Math.Round(Math.Clamp(Math.Pow(2, v), .1, 4), 4)), v => $"{Math.Pow(2, v):0.00}x");

    private static DevelopParameter Base(string label, Func<DevelopRecipe, double> get, Func<DevelopRecipe, double, DevelopRecipe> set) =>
        new(label, .05, 2, .01, 1, false, get, set, v => $"{v:0.00}");

    private static DevelopParameter Crop(string label, Func<DevelopRecipe, double> get, Func<DevelopRecipe, double, DevelopRecipe> set) =>
        new(label, 0, .45, .005, 0, false, get, set, v => $"{v:P1}");

    private static readonly DevelopParameter PExposure = new("Exposure", -4, 4, .05, 0, true,
        r => r.ExposureEv, (r, v) => r with { ExposureEv = v }, v => $"{v:+0.00;-0.00;0.00}");
    private static readonly DevelopParameter PRed = Gain("Red", r => r.RedGain, (r, v) => r with { RedGain = v });
    private static readonly DevelopParameter PGreen = Gain("Green", r => r.GreenGain, (r, v) => r with { GreenGain = v });
    private static readonly DevelopParameter PBlue = Gain("Blue", r => r.BlueGain, (r, v) => r with { BlueGain = v });
    private static readonly DevelopParameter PBlack = new("Black", 0, .5, .005, 0, true,
        r => r.BlackPoint, (r, v) => r with { BlackPoint = v }, v => $"{v:0.000}");
    private static readonly DevelopParameter PWhite = new("White", .5, 1, .005, 1, true,
        r => r.WhitePoint, (r, v) => r with { WhitePoint = v }, v => $"{v:0.000}");
    private static readonly DevelopParameter PBaseR = Base("Base R", r => r.FilmBaseRed, (r, v) => r with { FilmBaseRed = v });
    private static readonly DevelopParameter PBaseG = Base("Base G", r => r.FilmBaseGreen, (r, v) => r with { FilmBaseGreen = v });
    private static readonly DevelopParameter PBaseB = Base("Base B", r => r.FilmBaseBlue, (r, v) => r with { FilmBaseBlue = v });
    private static readonly DevelopParameter PCropL = Crop("Left", r => r.CropLeft, (r, v) => r with { CropLeft = v });
    private static readonly DevelopParameter PCropT = Crop("Top", r => r.CropTop, (r, v) => r with { CropTop = v });
    private static readonly DevelopParameter PCropR = Crop("Right", r => r.CropRight, (r, v) => r with { CropRight = v });
    private static readonly DevelopParameter PCropB = Crop("Bottom", r => r.CropBottom, (r, v) => r with { CropBottom = v });

    private static SolidColorBrush DevelopBrush(byte a, byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(a, r, g, b));
    private static readonly SolidColorBrush DevelopRed = DevelopBrush(255, 235, 87, 87);
    private static readonly SolidColorBrush DevelopGreen = DevelopBrush(255, 92, 200, 110);
    private static readonly SolidColorBrush DevelopBlue = DevelopBrush(255, 90, 150, 255);
    private static readonly SolidColorBrush DevelopMuted = DevelopBrush(255, 160, 166, 176);

    private bool syncingDevelopControls;
    private readonly List<DevelopSlider> developSliders = [];
    private readonly List<(Control Proxy, Control Original)> developActions = [];
    private ToggleButton developNegativeToggle = null!;
    private Grid developPreviewArea = null!;
    private StackPanel developSampling = null!;
    private Border workflowToolbar = null!;
    private Border livePreviewStatus = null!;
    private TextBlock livePreviewText = null!;
    private ProgressRing livePreviewRing = null!;
    private FrameworkElement developConsole = null!, calibrationTools = null!;
    private TextBlock developIdentity = null!, developWarnings = null!, developMessage = null!;
    private Ellipse colorPuck = null!;
    private TextBlock colorReadout = null!;

    private DevelopRecipe? liveDraft;
    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? liveCts;
    private Task liveTask = Task.CompletedTask;
    private bool liveRendering;
    private double? liveLastSeconds;
    private byte[]? instantBase, instantOut;
    private WriteableBitmap? instantBitmap;
    private DevelopRecipe? instantRecipe;
    private int instantVersion;

    private bool DevelopConsoleActive => store is not null
        && phase == WorkflowPhase.Develop && !capturing && !faultHeld;

    private void InitializeDevelopConsole(Grid previewArea, StackPanel sampling, Border toolbar)
    {
        developPreviewArea = previewArea;
        developSampling = sampling;
        workflowToolbar = toolbar;
        liveTimer.Tick += (_, _) => { liveTimer.Stop(); _ = CommitLiveAsync(); };
        Closed += (_, _) => { liveTimer.Stop(); liveCts?.Cancel(); };
        sourceView.Checked += (_, _) => SyncNegativeToggle();
        sourceView.Unchecked += (_, _) => SyncNegativeToggle();
        message.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => SyncStatusCopies());
        warnings.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => SyncStatusCopies());
        BuildLiveChip();
        BuildDevelopConsole();
    }

    private void RefreshDevelopConsole()
    {
        UpdateDevelopLayout();
        foreach (var (proxy, original) in developActions)
        {
            proxy.IsEnabled = original.IsEnabled;
            proxy.Visibility = original.Visibility;
            proxy.Opacity = original.Opacity;
        }
        SyncDevelopControls();
        SyncStatusCopies();
        if (DevelopConsoleActive && liveDraft is null && !liveRendering && positiveBitmap is not null && sourceView.IsChecked != true)
        {
            preview.Opacity = 1;
            SetLiveStatus(displayedRecipe == draft
                ? $"Exact preview{(liveLastSeconds is { } s ? $" ({s:0.0}s)" : "")} - saved for export"
                : "Settings saved - preview needs refresh", false);
        }
    }

    private void PrepareLivePreview(FramePreview result)
    {
        _ = LoadInstantBaseAsync(result, ++instantVersion);
    }

    private void FlushDevelopEditsBeforeOperation(ref Func<CancellationToken, Task> action)
    {
        liveTimer.Stop();
        var changed = liveDraft;
        liveDraft = null;
        liveCts?.Cancel();
        var pending = liveTask;
        var inner = action;
        action = async token =>
        {
            await pending;
            if (changed is not null)
            {
                var saved = Workflow.Edit(changed.FrameId, changed);
                if (SelectedFrame?.Id == saved.FrameId) draft = saved;
            }
            await inner(token);
        };
    }

    private void LiveEdit(DevelopParameter param, double value) =>
        LiveSet(r => param.Set(r, Math.Round(value, 4)), param.Instant);

    private void LiveSet(Func<DevelopRecipe, DevelopRecipe> change, bool instant)
    {
        if (syncingDevelopControls || busy || faultHeld || store is null || SelectedFrame is not { } frame) return;
        var basis = liveDraft ?? draft;
        if (basis.FrameId != frame.Id) return;
        var next = change(basis);
        if (next == basis) return;
        if (next.BlackPoint >= next.WhitePoint - .01)
        {
            SyncDevelopControls();
            return;
        }
        liveDraft = next;
        if (instant) ApplyInstant(next);
        else preview.Opacity = .55;
        SetLiveStatus(instant ? "Adjusting (instant approximation)" : "Adjusting (needs exact render)", false);
        SyncDevelopControls();
        liveTimer.Stop();
        liveTimer.Start();
    }

    private async Task CommitLiveAsync()
    {
        if (liveDraft is not { } target || busy || store is null || SelectedFrame is not { } frame || target.FrameId != frame.Id) return;
        liveCts?.Cancel();
        await liveTask;
        if (!ReferenceEquals(target, liveDraft) || busy) return;
        try { draft = Workflow.Edit(frame.Id, target); }
        catch (Exception ex) when (IsOperationalError(ex))
        {
            liveDraft = null;
            message.Text = $"Edit rejected: {ex.Message}";
            Refresh();
            return;
        }
        liveDraft = null;
        var cts = liveCts = new CancellationTokenSource();
        liveTask = LiveRenderAsync(frame.Id, cts);
    }

    private async Task LiveRenderAsync(Guid frameId, CancellationTokenSource cts)
    {
        liveRendering = true;
        SetLiveStatus("Rendering exact preview...", true);
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await Workflow.PreviewAsync(frameId, null, cts.Token);
            if (cts.IsCancellationRequested || liveDraft is not null || SelectedFrame?.Id != frameId || result.Recipe != draft) return;
            await DisplayPreviewAsync(result, cts.Token);
            liveLastSeconds = clock.Elapsed.TotalSeconds;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) when (IsOperationalError(ex))
        {
            SetLiveStatus("Exact render failed", false);
            message.Text = $"Live preview failed: {ex.Message}";
        }
        finally
        {
            liveRendering = false;
            if (ReferenceEquals(liveCts, cts)) liveCts = null;
            cts.Dispose();
            if (!closing) Refresh();
        }
    }

    private async Task LoadInstantBaseAsync(FramePreview result, int version)
    {
        try
        {
            using var file = File.OpenRead(result.PositivePath);
            var decoder = await BitmapDecoder.CreateAsync(file.AsRandomAccessStream());
            var scale = Math.Min(1, 1800.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            uint width = Math.Max(1, (uint)(decoder.PixelWidth * scale)), height = Math.Max(1, (uint)(decoder.PixelHeight * scale));
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            if (version != instantVersion) return;
            instantBase = data.DetachPixelData();
            instantOut = new byte[instantBase.Length];
            instantBitmap = new WriteableBitmap((int)width, (int)height);
            instantRecipe = result.Recipe;
            if (liveDraft is { } pending) ApplyInstant(pending);
        }
        catch (Exception ex) when (IsOperationalError(ex))
        {
            instantBase = null;
            message.Text = $"Instant preview unavailable: {ex.Message}. Exact rendering remains available.";
        }
    }

    /// <summary>Approximates the exact controls step on the displayed sRGB positive with one LUT per channel.</summary>
    private void ApplyInstant(DevelopRecipe r)
    {
        if (instantBase is null || instantOut is null || instantBitmap is null || instantRecipe is not { } r0
            || sourceView.IsChecked == true || displayedFrame != SelectedFrame?.Id) return;
        if (r.CropLeft != r0.CropLeft || r.CropTop != r0.CropTop || r.CropRight != r0.CropRight || r.CropBottom != r0.CropBottom
            || r.FilmBaseRed != r0.FilmBaseRed || r.FilmBaseGreen != r0.FilmBaseGreen || r.FilmBaseBlue != r0.FilmBaseBlue)
        {
            preview.Opacity = .55;
            return;
        }
        static double Decode(double c) => c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
        static double Encode(double c) => c <= .0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - .055;
        double e0 = Math.Pow(2, r0.ExposureEv), e1 = Math.Pow(2, r.ExposureEv);
        double[] g0 = [r0.BlueGain, r0.GreenGain, r0.RedGain], g1 = [r.BlueGain, r.GreenGain, r.RedGain];
        var luts = new byte[3][];
        for (int c = 0; c < 3; c++)
        {
            luts[c] = new byte[256];
            double k = e1 * g1[c] / (e0 * g0[c]);
            for (int p = 0; p < 256; p++)
            {
                double before = Decode(p / 255.0) * (r0.WhitePoint - r0.BlackPoint) + r0.BlackPoint;
                double after = Math.Clamp((before * k - r.BlackPoint) / (r.WhitePoint - r.BlackPoint), 0, 1);
                luts[c][p] = (byte)Math.Round(Encode(after) * 255);
            }
        }
        byte[] src = instantBase, dst = instantOut, lb = luts[0], lg = luts[1], lr = luts[2];
        for (int i = 0; i < src.Length; i += 4)
        {
            dst[i] = lb[src[i]];
            dst[i + 1] = lg[src[i + 1]];
            dst[i + 2] = lr[src[i + 2]];
            dst[i + 3] = 255;
        }
        using (var stream = instantBitmap.PixelBuffer.AsStream())
        {
            stream.Position = 0;
            stream.Write(dst, 0, dst.Length);
        }
        instantBitmap.Invalidate();
        preview.Source = instantBitmap;
        preview.Opacity = 1;
    }

    private void SetLiveStatus(string text, bool spinning)
    {
        livePreviewText.Text = text;
        livePreviewRing.IsActive = spinning;
        livePreviewRing.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDevelopLayout()
    {
        var active = DevelopConsoleActive;
        static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
        developConsole.Visibility = calibrationTools.Visibility = Show(active);
        livePreviewStatus.Visibility = Show(active);
        developSampling.Children[0].Visibility = Show(!active);
        workflowToolbar.Visibility = Show(!active);
        workspace.Padding = new Thickness(active ? 10 : 18);
        workspace.RowSpacing = active ? 6 : 12;
        frames.FontSize = active ? 14 : 18;
        previewLabel.FontSize = active ? 15 : 20;
        samplingLabel.FontSize = markerLegend.FontSize = active ? 12 : 14;
        if (active) samplingLabel.Foreground = markerLegend.Foreground = DevelopMuted;
        else
        {
            samplingLabel.ClearValue(TextBlock.ForegroundProperty);
            markerLegend.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private void SyncDevelopControls()
    {
        syncingDevelopControls = true;
        try
        {
            var recipe = liveDraft ?? draft;
            var enabled = !busy && !faultHeld && SelectedFrame is not null;
            foreach (var item in developSliders)
            {
                var value = item.Param.Get(recipe);
                item.Slider.Value = value;
                item.Slider.IsEnabled = enabled;
                if (item.Value is { } text) text.Text = item.Param.Format(value);
            }
            SyncPad(recipe);
        }
        finally { syncingDevelopControls = false; }
    }

    private void SyncNegativeToggle()
    {
        developNegativeToggle.IsChecked = sourceView.IsChecked;
    }

    private void SyncStatusCopies()
    {
        if (developIdentity is null) return;
        var controls = draft.InheritControls
            ? SelectedFrame is { Number: > 1 } frame ? $"following frame {frame.Number - 1}" : "default controls"
            : "local controls";
        developIdentity.Text = $"{identity.Text} - {controls}";
        developWarnings.Text = warnings.Text.Trim();
        developMessage.Text = message.Text;
    }

    private static Button DevelopIcon(string glyph, string tip, Brush? foreground = null, double size = 36)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 16 };
        if (foreground is not null) icon.Foreground = foreground;
        var button = new Button
        {
            Content = icon, Width = size, Height = size, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0)
        };
        ToolTipService.SetToolTip(button, tip);
        AutomationProperties.SetName(button, tip);
        return button;
    }

    private Button CreateDevelopAction(Button original, string glyph, string tip, Action execute,
        Brush? foreground = null, double size = 36)
    {
        var button = DevelopIcon(glyph, tip, foreground, size);
        button.Click += (_, _) => { if (original.IsEnabled) execute(); };
        developActions.Add((button, original));
        return button;
    }

    private Button CreateAdvancedSettingsButton()
    {
        var button = DevelopIcon("\uE713", "Advanced develop settings");
        button.Click += (_, _) => settingsFlyout.ShowAt(button);
        developActions.Add((button, developSettings));
        return button;
    }

    private StackPanel CreateCalibrationTools()
    {
        var negative = new ToggleButton
        {
            Content = new FontIcon { Glyph = "\uE7A1", FontSize = 16 }, Width = 36, Height = 36, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0)
        };
        ToolTipService.SetToolTip(negative, "Show negative / source");
        AutomationProperties.SetName(negative, "Show negative");
        negative.Checked += (_, _) => sourceView.IsChecked = true;
        negative.Unchecked += (_, _) => sourceView.IsChecked = false;
        developNegativeToggle = negative;
        developActions.Add((negative, sourceView));
        return new StackPanel
        {
            Orientation = Orientation.Vertical, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                negative,
                CreateDevelopAction(autoPicks, "\uE945", "Auto-pick backlight + film base", () => StartOperation(AutoPickAsync)),
                CreateDevelopAction(pickBacklight, "\uEF3C", "Pick backlight white balance (red marker)",
                    () => BeginSampling(CalibrationTarget.Backlight), DevelopRed),
                CreateDevelopAction(pickFilmBase, "\uEF3C", "Pick film base (blue marker)",
                    () => BeginSampling(CalibrationTarget.FilmBase), DevelopBlue),
                CreateDevelopAction(resetPicks, "\uE7A7", "Reset picks for this frame", () => StartOperation(ResetPicksAsync))
            }
        };
    }

    private TextBlock CreateResetLabel(string text, DevelopParameter param, double size = 12.5)
    {
        var label = new TextBlock { Text = text, FontSize = size, Foreground = DevelopMuted, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(label, $"{param.Label} - double-click to reset");
        label.DoubleTapped += (_, _) => LiveEdit(param, param.Default);
        return label;
    }

    private Slider CreateDevelopSlider(DevelopParameter param, Brush? tint, TextBlock? value)
    {
        var slider = new Slider
        {
            Minimum = param.Min, Maximum = param.Max, StepFrequency = param.Step, SmallChange = param.Step,
            LargeChange = param.Step * 10, IsThumbToolTipEnabled = false, VerticalAlignment = VerticalAlignment.Center
        };
        if (tint is not null) slider.Foreground = tint;
        AutomationProperties.SetName(slider, param.Label);
        slider.ValueChanged += (_, e) => LiveEdit(param, e.NewValue);
        // Slider drag handling swallows DoubleTapped, so detect two quick releases (handledEventsToo).
        var lastRelease = 0L;
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            var now = Environment.TickCount64;
            if (now - lastRelease < 500)
            {
                lastRelease = 0;
                LiveEdit(param, param.Default);
            }
            else lastRelease = now;
        }), true);
        ToolTipService.SetToolTip(slider, $"{param.Label} - double-click to reset");
        developSliders.Add(new(slider, param, value));
        return slider;
    }

    /// <summary>Compact horizontal row: label | slider | value.</summary>
    private Grid CreateCropRow(DevelopParameter param, Brush? tint = null)
    {
        var value = new TextBlock { FontSize = 12.5, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = CreateDevelopSlider(param, tint, value);
        slider.Margin = new Thickness(6, 0, 6, 0);
        var row = new Grid { Height = 32 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(58) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(50) });
        row.Children.Add(CreateResetLabel(param.Label, param));
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }

    /// <summary>Vertical channel strip: value on top, slider, label below.</summary>
    private StackPanel CreateChannelStrip(DevelopParameter param, string label, Brush? tint = null, double height = 170)
    {
        var value = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
        var slider = CreateDevelopSlider(param, tint, value);
        slider.Orientation = Orientation.Vertical;
        slider.Height = height;
        slider.HorizontalAlignment = HorizontalAlignment.Center;
        var caption = CreateResetLabel(label, param, 12);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        return new StackPanel { Width = 54, Spacing = 4, Children = { value, slider, caption } };
    }

    private static TextBlock DevelopSection(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, CharacterSpacing = 80, Foreground = DevelopMuted,
        Margin = new Thickness(0, 8, 0, 2)
    };

    private StackPanel CreateCropRows(params (DevelopParameter Param, Brush? Tint)[] rows)
    {
        var panel = new StackPanel();
        foreach (var (param, tint) in rows) panel.Children.Add(CreateCropRow(param, tint));
        return panel;
    }

    private void BuildLiveChip()
    {
        livePreviewText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        livePreviewRing = new ProgressRing { Width = 14, Height = 14, MinWidth = 0, MinHeight = 0, IsActive = false, Visibility = Visibility.Collapsed };
        livePreviewStatus = new Border
        {
            Background = DevelopBrush(200, 0, 0, 0), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8),
            IsHitTestVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { livePreviewRing, livePreviewText } }
        };
        AddToPreview(livePreviewStatus);
    }

    private void AddToPreview(UIElement element) =>
        developPreviewArea.Children.Insert(developPreviewArea.Children.IndexOf(activityOverlay), element);

    private void BuildDevelopConsole()
    {
        var nav = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 12, 0) };
        foreach (var (item, button) in phaseButtons)
            nav.Children.Add(CreateDevelopAction(button, item switch
            {
                WorkflowPhase.Capture => "\uE722",
                WorkflowPhase.Develop => "\uE9E9",
                _ => "\uEDE1"
            }, $"{(int)item}. {item}", () => StartOperation(_ => NavigateAsync(item)), null, 40));
        nav.Children.Add(new Border { Height = 1, Background = DevelopMuted, Opacity = .3, Margin = new Thickness(4) });
        nav.Children.Add(CreateDevelopAction(cancel, "\uE769", "Pause / cancel safely", PauseOperation, null, 40));
        nav.Children.Add(CreateDevelopAction(connect, "\uE88E", "Connect USB camera", () => StartOperation(ConnectCameraAsync), null, 40));
        nav.Children.Add(CreateDevelopAction(close, "\uE8BB", "Close workflow", async () => await RequestCloseAsync(), null, 40));

        StackPanel Group(string title, params UIElement[] strips)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var strip in strips) row.Children.Add(strip);
            return new StackPanel { Children = { DevelopSection(title), row } };
        }
        Border Divider() => new() { Width = 1, Background = DevelopMuted, Opacity = .25, Margin = new Thickness(10, 12, 10, 0) };

        var cropButton = DevelopIcon("\uE7A8", "Crop");
        cropButton.Flyout = new Flyout
        {
            Content = new StackPanel
            {
                Width = 320, Children = { DevelopSection("CROP"), CreateCropRows((PCropL, null), (PCropT, null), (PCropR, null), (PCropB, null)) }
            }
        };
        var filmBase = Group("FILM BASE", CreateChannelStrip(PBaseR, "R", DevelopRed, 130), CreateChannelStrip(PBaseG, "G", DevelopGreen, 130),
            CreateChannelStrip(PBaseB, "B", DevelopBlue, 130));
        filmBase.Opacity = .8;
        ToolTipService.SetToolTip(filmBase, "Film base changes re-run the darktable inversion (slower)");
        var console = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Group("EXPOSURE", CreateChannelStrip(PExposure, "EV")),
                Divider(),
                Group("COLOR", CreateColorPadStrip(), CreateChannelStrip(PRed, "Red", DevelopRed), CreateChannelStrip(PGreen, "Green", DevelopGreen), CreateChannelStrip(PBlue, "Blue", DevelopBlue)),
                Divider(),
                Group("LEVELS", CreateChannelStrip(PBlack, "Black"), CreateChannelStrip(PWhite, "White")),
                Divider(),
                filmBase,
                Divider(),
                new StackPanel { Children = { DevelopSection("CROP"), cropButton } }
            }
        };
        developIdentity = new TextBlock { FontSize = 13, Foreground = DevelopMuted, TextTrimming = TextTrimming.CharacterEllipsis };
        var center = new Grid();
        center.RowDefinitions.Add(new() { Height = GridLength.Auto });
        center.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        center.Children.Add(developIdentity);
        var consoleScroll = new ScrollViewer
        {
            Content = console, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(consoleScroll, 1);
        center.Children.Add(consoleScroll);

        developWarnings = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Orange) };
        developMessage = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var side = new Grid { Width = 300, RowSpacing = 8, Margin = new Thickness(12, 0, 0, 0) };
        side.RowDefinitions.Add(new() { Height = GridLength.Auto });
        side.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var advanced = CreateAdvancedSettingsButton();
        advanced.Height = 40;
        advanced.HorizontalAlignment = HorizontalAlignment.Right;
        side.Children.Add(advanced);
        var status = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 6, Children = { developMessage, developWarnings } },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(status, 1);
        side.Children.Add(status);

        var layout = new Grid
        {
            Background = DevelopBrush(255, 18, 20, 24), Padding = new Thickness(10, 8, 10, 8),
            Height = workflowToolbar.Height
        };
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.Children.Add(nav);
        Grid.SetColumn(center, 1);
        layout.Children.Add(center);
        Grid.SetColumn(side, 2);
        layout.Children.Add(side);
        AutomationProperties.SetName(layout, "Develop console");
        Grid.SetRow(layout, 1);
        Grid.SetColumnSpan(layout, 2);
        root.Children.Add(layout);
        developConsole = layout;

        var tools = CreateCalibrationTools();
        calibrationTools = new Border
        {
            Background = DevelopBrush(170, 0, 0, 0), CornerRadius = new CornerRadius(8), Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8),
            Child = tools
        };
        AddToPreview(calibrationTools);
    }

    private const double PadSize = 150, PadTempRange = 3, PadTintRange = 1.5;

    /// <summary>Color pad sized to sit beside the vertical strips in the bottom console.</summary>
    private StackPanel CreateColorPadStrip()
    {
        var pad = CreateColorPad();
        pad.Margin = new Thickness(0, 20, 12, 0);
        return pad;
    }

    /// <summary>2D color-balance pad: x = cool/warm, y = magenta/green.</summary>
    private StackPanel CreateColorPad()
    {
        var padSurface = new Grid { Width = PadSize, Height = PadSize, Background = new SolidColorBrush(Colors.Transparent) };
        var padDragging = false;
        var warmCool = new LinearGradientBrush { StartPoint = new(0, .5), EndPoint = new(1, .5) };
        warmCool.GradientStops.Add(new() { Color = Windows.UI.Color.FromArgb(255, 60, 120, 255), Offset = 0 });
        warmCool.GradientStops.Add(new() { Color = Windows.UI.Color.FromArgb(255, 128, 128, 128), Offset = .5 });
        warmCool.GradientStops.Add(new() { Color = Windows.UI.Color.FromArgb(255, 255, 170, 60), Offset = 1 });
        var tint = new LinearGradientBrush { StartPoint = new(.5, 0), EndPoint = new(.5, 1) };
        tint.GradientStops.Add(new() { Color = Windows.UI.Color.FromArgb(150, 255, 70, 220), Offset = 0 });
        tint.GradientStops.Add(new() { Color = Windows.UI.Color.FromArgb(0, 128, 128, 128), Offset = .5 });
        tint.GradientStops.Add(new() { Color = Windows.UI.Color.FromArgb(150, 70, 255, 80), Offset = 1 });
        padSurface.Children.Add(new Rectangle { Fill = warmCool, RadiusX = 6, RadiusY = 6 });
        padSurface.Children.Add(new Rectangle { Fill = tint, RadiusX = 6, RadiusY = 6 });
        padSurface.Children.Add(new Rectangle { Width = 1, Fill = DevelopBrush(70, 255, 255, 255) });
        padSurface.Children.Add(new Rectangle { Height = 1, Fill = DevelopBrush(70, 255, 255, 255) });
        colorPuck = new Ellipse { Width = 16, Height = 16, Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 2, Fill = DevelopBrush(90, 0, 0, 0) };
        var puckLayer = new Canvas { IsHitTestVisible = false, Children = { colorPuck } };
        padSurface.Children.Add(puckLayer);
        ToolTipService.SetToolTip(padSurface, "Drag: cool/warm (x), magenta/green (y). Wheel: warmth, Shift+wheel: tint. Double-click: neutral.");
        AutomationProperties.SetName(padSurface, "Color balance pad");
        padSurface.PointerPressed += (_, e) =>
        {
            padDragging = padSurface.CapturePointer(e.Pointer);
            PadMove(e.GetCurrentPoint(padSurface).Position);
            e.Handled = true;
        };
        padSurface.PointerMoved += (_, e) => { if (padDragging) PadMove(e.GetCurrentPoint(padSurface).Position); };
        padSurface.PointerReleased += (_, e) => { padDragging = false; padSurface.ReleasePointerCapture(e.Pointer); };
        padSurface.PointerCaptureLost += (_, _) => padDragging = false;
        padSurface.DoubleTapped += (_, _) => LiveSet(r => WithBalance(r, 0, 0), true);
        padSurface.PointerWheelChanged += (_, e) =>
        {
            var notches = e.GetCurrentPoint(padSurface).Properties.MouseWheelDelta / 120.0;
            var shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
            LiveSet(r =>
            {
                var (t, g) = Balance(r);
                return shift ? WithBalance(r, t, g - notches * .05) : WithBalance(r, t + notches * .05, g);
            }, true);
            e.Handled = true;
        };
        colorReadout = new TextBlock { FontSize = 11.5, Foreground = DevelopMuted, HorizontalAlignment = HorizontalAlignment.Center };
        return new StackPanel { Spacing = 4, Children = { padSurface, colorReadout } };
    }

    /// <summary>Warmth = log2(R/B); tint = log2 G relative to the R/B mean. Overall gain level is preserved.</summary>
    private static (double Temperature, double Tint) Balance(DevelopRecipe r)
    {
        double lr = Math.Log2(r.RedGain), lg = Math.Log2(r.GreenGain), lb = Math.Log2(r.BlueGain);
        return (lr - lb, lg - (lr + lb) / 2);
    }

    private static DevelopRecipe WithBalance(DevelopRecipe r, double temperature, double tint)
    {
        temperature = Math.Clamp(temperature, -PadTempRange, PadTempRange);
        tint = Math.Clamp(tint, -PadTintRange, PadTintRange);
        var mean = (Math.Log2(r.RedGain) + Math.Log2(r.BlueGain)) / 2;
        static double GainOf(double log2) => Math.Round(Math.Clamp(Math.Pow(2, log2), .1, 4), 4);
        return r with { RedGain = GainOf(mean + temperature / 2), BlueGain = GainOf(mean - temperature / 2), GreenGain = GainOf(mean + tint) };
    }

    private void PadMove(Windows.Foundation.Point point)
    {
        var temperature = (Math.Clamp(point.X / PadSize, 0, 1) * 2 - 1) * PadTempRange;
        var tint = (Math.Clamp(point.Y / PadSize, 0, 1) * 2 - 1) * PadTintRange;
        LiveSet(r => WithBalance(r, temperature, tint), true);
    }

    private void SyncPad(DevelopRecipe r)
    {
        var (temperature, tint) = Balance(r);
        Canvas.SetLeft(colorPuck, (Math.Clamp(temperature / PadTempRange, -1, 1) + 1) / 2 * PadSize - colorPuck.Width / 2);
        Canvas.SetTop(colorPuck, (Math.Clamp(tint / PadTintRange, -1, 1) + 1) / 2 * PadSize - colorPuck.Height / 2);
        colorReadout.Text = $"R {r.RedGain:0.00}   G {r.GreenGain:0.00}   B {r.BlueGain:0.00}";
    }
}
