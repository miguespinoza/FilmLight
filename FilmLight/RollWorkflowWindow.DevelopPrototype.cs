#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using FilmLight.Workflow;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
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

/*
 * PROTOTYPE - THROWAWAY. Question: "What should the Develop panel look like, with live adjustments?"
 *
 * Three structurally different Develop layouts plus the current baseline, rendered on the real roll workflow
 * window and switched from the floating yellow pill (or Left/Right keys when no slider/text box has focus):
 *   A - Inspector rail: compact always-visible slider column beside the preview.
 *   B - Bottom console: the bottom band becomes a mixing console of vertical sliders.
 *   C - On-image HUD: floating card over the image with a 2D color-balance pad.
 * Every variant shares the live engine: an instant per-channel LUT on the shown positive while dragging,
 * then a debounced exact render (darktable inversion is cached; exposure/gains/levels are applied after it).
 *
 * Run: tools\Prototype-DevelopPanel.ps1 [-Variant A|B|C|Current]  (sets FILMLIGHT_PROTOTYPE_DEVELOP).
 * Debug builds only. Delete this file and the Prototype* hooks once a variant is chosen.
 */
public sealed partial class RollWorkflowWindow
{
    private sealed record ProtoParam(string Label, double Min, double Max, double Step, double Default, bool Instant,
        Func<DevelopRecipe, double> Get, Func<DevelopRecipe, double, DevelopRecipe> Set, Func<double, string> Format);

    private sealed record ProtoSlider(Slider Slider, ProtoParam Param, TextBlock? Value);

    private static readonly string[] ProtoKeys = ["Current", "A", "B", "C"];
    private static readonly Dictionary<string, string> ProtoNames = new()
    {
        ["Current"] = "Current (baseline)", ["A"] = "Inspector rail", ["B"] = "Bottom console", ["C"] = "On-image HUD + color pad"
    };

    private static ProtoParam Gain(string label, Func<DevelopRecipe, double> get, Func<DevelopRecipe, double, DevelopRecipe> set) =>
        new(label, Math.Log2(.1), 2, .01, 0, true, r => Math.Log2(get(r)),
            (r, v) => set(r, Math.Round(Math.Clamp(Math.Pow(2, v), .1, 4), 4)), v => $"{Math.Pow(2, v):0.00}x");

    private static ProtoParam Base(string label, Func<DevelopRecipe, double> get, Func<DevelopRecipe, double, DevelopRecipe> set) =>
        new(label, .05, 2, .01, 1, false, get, set, v => $"{v:0.00}");

    private static ProtoParam Crop(string label, Func<DevelopRecipe, double> get, Func<DevelopRecipe, double, DevelopRecipe> set) =>
        new(label, 0, .45, .005, 0, false, get, set, v => $"{v:P1}");

    private static readonly ProtoParam PExposure = new("Exposure", -4, 4, .05, 0, true,
        r => r.ExposureEv, (r, v) => r with { ExposureEv = v }, v => $"{v:+0.00;-0.00;0.00}");
    private static readonly ProtoParam PRed = Gain("Red", r => r.RedGain, (r, v) => r with { RedGain = v });
    private static readonly ProtoParam PGreen = Gain("Green", r => r.GreenGain, (r, v) => r with { GreenGain = v });
    private static readonly ProtoParam PBlue = Gain("Blue", r => r.BlueGain, (r, v) => r with { BlueGain = v });
    private static readonly ProtoParam PBlack = new("Black", 0, .5, .005, 0, true,
        r => r.BlackPoint, (r, v) => r with { BlackPoint = v }, v => $"{v:0.000}");
    private static readonly ProtoParam PWhite = new("White", .5, 1, .005, 1, true,
        r => r.WhitePoint, (r, v) => r with { WhitePoint = v }, v => $"{v:0.000}");
    private static readonly ProtoParam PBaseR = Base("Base R", r => r.FilmBaseRed, (r, v) => r with { FilmBaseRed = v });
    private static readonly ProtoParam PBaseG = Base("Base G", r => r.FilmBaseGreen, (r, v) => r with { FilmBaseGreen = v });
    private static readonly ProtoParam PBaseB = Base("Base B", r => r.FilmBaseBlue, (r, v) => r with { FilmBaseBlue = v });
    private static readonly ProtoParam PCropL = Crop("Left", r => r.CropLeft, (r, v) => r with { CropLeft = v });
    private static readonly ProtoParam PCropT = Crop("Top", r => r.CropTop, (r, v) => r with { CropTop = v });
    private static readonly ProtoParam PCropR = Crop("Right", r => r.CropRight, (r, v) => r with { CropRight = v });
    private static readonly ProtoParam PCropB = Crop("Bottom", r => r.CropBottom, (r, v) => r with { CropBottom = v });

    private static SolidColorBrush ProtoBrush(byte a, byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(a, r, g, b));
    private static readonly SolidColorBrush ProtoRed = ProtoBrush(255, 235, 87, 87);
    private static readonly SolidColorBrush ProtoGreen = ProtoBrush(255, 92, 200, 110);
    private static readonly SolidColorBrush ProtoBlue = ProtoBrush(255, 90, 150, 255);
    private static readonly SolidColorBrush ProtoMuted = ProtoBrush(255, 160, 166, 176);

    private bool protoEnabled;
    private string protoKey = "Current";
    private bool protoSyncing;
    private bool protoCompact;
    private readonly List<ProtoSlider> protoSliders = [];
    private readonly List<(Control Proxy, Control Original)> protoMirrors = [];
    private readonly List<ToggleButton> protoNegativeToggles = [];
    private readonly Dictionary<Control, (object Content, double MinHeight, double MinWidth, Thickness Padding, double FontSize)> protoChromeSaved = [];
    private Grid protoPreviewArea = null!;
    private StackPanel protoPreviewHeader = null!;
    private StackPanel protoSampling = null!;
    private Border protoToolbarBorder = null!;
    private TextBlock protoSwitchLabel = null!;
    private Border protoLiveChip = null!;
    private TextBlock protoLiveText = null!;
    private ProgressRing protoLiveRing = null!;
    private FrameworkElement protoA = null!, protoAHeaderTools = null!, protoB = null!, protoBTools = null!, protoC = null!, protoCTools = null!;
    private TextBlock protoBIdentity = null!, protoBWarnings = null!, protoBMessage = null!;
    private readonly List<(Ellipse Puck, TextBlock Readout)> protoPads = [];

    // Live preview engine state.
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

    private bool ProtoActive => protoEnabled && protoKey != "Current" && store is not null
        && phase == WorkflowPhase.Develop && !capturing && !faultHeld;

    partial void PrototypeInit()
    {
        var requested = Environment.GetEnvironmentVariable("FILMLIGHT_PROTOTYPE_DEVELOP");
        if (string.IsNullOrWhiteSpace(requested)) return;
        protoEnabled = true;
        protoKey = ProtoKeys.FirstOrDefault(k => k.Equals(requested.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "A";
        Title += " - PROTOTYPE develop panel";
        protoPreviewArea = (Grid)ProtoParent(preview)!;
        protoPreviewHeader = (StackPanel)ProtoParent(previewLabel)!;
        protoSampling = (StackPanel)ProtoParent(samplingLabel)!;
        protoToolbarBorder = root.Children.OfType<Border>().First(b => Grid.GetRow(b) == 1);
        liveTimer.Tick += (_, _) => { liveTimer.Stop(); _ = CommitLiveAsync(); };
        Closed += (_, _) => { liveTimer.Stop(); liveCts?.Cancel(); };
        sourceView.Checked += (_, _) => SyncNegativeToggles();
        sourceView.Unchecked += (_, _) => SyncNegativeToggles();
        message.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => SyncStatusCopies());
        warnings.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => SyncStatusCopies());
        BuildLiveChip();
        BuildVariantA();
        BuildVariantB();
        BuildVariantC();
        BuildSwitcher();
        root.KeyDown += ProtoKeyDown;
    }

    partial void PrototypeAfterRefresh()
    {
        if (!protoEnabled) return;
        ProtoApply();
        foreach (var (proxy, original) in protoMirrors)
        {
            proxy.IsEnabled = original.IsEnabled;
            proxy.Visibility = original.Visibility;
            proxy.Opacity = original.Opacity;
        }
        ProtoSyncControls();
        SyncStatusCopies();
        if (ProtoActive && liveDraft is null && !liveRendering && positiveBitmap is not null && sourceView.IsChecked != true)
        {
            preview.Opacity = 1;
            SetLiveStatus(displayedRecipe == draft
                ? $"Exact preview{(liveLastSeconds is { } s ? $" ({s:0.0}s)" : "")} - saved for export"
                : "Settings saved - preview needs refresh", false);
        }
    }

    partial void PrototypeAfterDisplay(FramePreview result)
    {
        if (!protoEnabled) return;
        _ = LoadInstantBaseAsync(result, ++instantVersion);
    }

    partial void PrototypeBeforeOperation(ref Func<CancellationToken, Task> action)
    {
        if (!protoEnabled) return;
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

    // ---------- Live engine ----------

    private void LiveEdit(ProtoParam param, double value) =>
        LiveSet(r => param.Set(r, Math.Round(value, 4)), param.Instant);

    private void LiveSet(Func<DevelopRecipe, DevelopRecipe> change, bool instant)
    {
        if (protoSyncing || busy || faultHeld || store is null || SelectedFrame is not { } frame) return;
        var basis = liveDraft ?? draft;
        if (basis.FrameId != frame.Id) return;
        var next = change(basis);
        if (next == basis) return;
        if (next.BlackPoint >= next.WhitePoint - .01)
        {
            ProtoSyncControls();
            return;
        }
        liveDraft = next;
        if (instant) ApplyInstant(next);
        else preview.Opacity = .55;
        SetLiveStatus(instant ? "Adjusting (instant approximation)" : "Adjusting (needs exact render)", false);
        ProtoSyncControls();
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
        catch (OperationCanceledException) { }
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
        catch (Exception ex) when (IsOperationalError(ex)) { instantBase = null; }
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
        protoLiveText.Text = text;
        protoLiveRing.IsActive = spinning;
        protoLiveRing.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Variant switching ----------

    private void ProtoApply()
    {
        var active = ProtoActive;
        var key = active ? protoKey : "Current";
        static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
        protoA.Visibility = protoAHeaderTools.Visibility = Show(key == "A");
        protoB.Visibility = protoBTools.Visibility = Show(key == "B");
        protoC.Visibility = protoCTools.Visibility = Show(key == "C");
        protoLiveChip.Visibility = Show(active);
        protoSampling.Children[0].Visibility = Show(!active);
        // Collapse the rows, not the buttons, so proxies can still invoke the originals.
        capturePanel.Children[0].Visibility = Show(!active);
        previewLabel.Visibility = Show(key != "A");
        protoPreviewHeader.Orientation = key == "A" ? Orientation.Horizontal : Orientation.Vertical;
        ProtoChrome(active);
        protoSwitchLabel.Text = protoKey == "Current" ? ProtoNames[protoKey] : $"{protoKey} - {ProtoNames[protoKey]}"
            + (protoKey != "Current" && !active ? "  (open 2. Develop to see it)" : "");
    }

    private void ProtoCycle(int delta)
    {
        var index = Array.IndexOf(ProtoKeys, protoKey);
        protoKey = ProtoKeys[(index + delta + ProtoKeys.Length) % ProtoKeys.Length];
        Refresh();
    }

    private void ProtoKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        var focused = FocusManager.GetFocusedElement(root.XamlRoot);
        if (focused is TextBox or Slider or ComboBox or PasswordBox or RichEditBox or NumberBox) return;
        ProtoCycle(e.Key == VirtualKey.Left ? -1 : 1);
        e.Handled = true;
    }

    /// <summary>Icon-only, tighter chrome for the existing window while a prototype variant is active.</summary>
    private void ProtoChrome(bool compact)
    {
        if (compact == protoCompact) return;
        protoCompact = compact;
        var items = new List<(Control Control, string Glyph, string Tip)>();
        foreach (var (item, button) in phaseButtons)
            items.Add((button, item switch
            {
                WorkflowPhase.Capture => "\uE722",
                WorkflowPhase.Develop => "\uE9E9",
                _ => "\uEDE1"
            }, $"{(int)item}. {item}"));
        items.Add((cancel, "\uE769", "Pause / cancel safely"));
        items.Add((connect, "\uE88E", "Connect USB camera"));
        items.Add((release, "\uE7E8", "Confirm idle / release camera"));
        items.Add((close, "\uE8BB", "Close workflow"));
        foreach (var (control, glyph, tip) in items)
        {
            if (compact)
            {
                protoChromeSaved[control] = (((ContentControl)control).Content, control.MinHeight, control.MinWidth, control.Padding, control.FontSize);
                ((ContentControl)control).Content = new FontIcon { Glyph = glyph, FontSize = 16 };
                control.MinHeight = 36;
                control.MinWidth = 44;
                control.Padding = new Thickness(8, 4, 8, 4);
                ToolTipService.SetToolTip(control, tip);
                AutomationProperties.SetName(control, tip);
            }
            else if (protoChromeSaved.TryGetValue(control, out var saved))
            {
                ((ContentControl)control).Content = saved.Content;
                control.MinHeight = saved.MinHeight;
                control.MinWidth = saved.MinWidth;
                control.Padding = saved.Padding;
                control.FontSize = saved.FontSize;
                ToolTipService.SetToolTip(control, null);
                control.ClearValue(AutomationProperties.NameProperty);
            }
        }
        if (phaseButtons.Values.FirstOrDefault() is { } first && ProtoParent(first) is StackPanel navigation) navigation.Spacing = compact ? 4 : 10;
        if (ProtoParent(capturePanel) is StackPanel toolbar)
        {
            toolbar.Padding = compact ? new Thickness(10, 6, 10, 2) : new Thickness(18, 12, 18, 4);
            toolbar.Spacing = compact ? 4 : 8;
        }
        identity.FontSize = compact ? 15 : 21;
        warnings.FontSize = message.FontSize = compact ? 12.5 : 17;
        workspace.Padding = new Thickness(compact ? 10 : 18);
        workspace.RowSpacing = compact ? 6 : 12;
        frames.FontSize = compact ? 14 : 18;
        previewLabel.FontSize = compact ? 15 : 20;
        samplingLabel.FontSize = markerLegend.FontSize = compact ? 12 : 14;
        if (compact) samplingLabel.Foreground = markerLegend.Foreground = ProtoMuted;
        else
        {
            samplingLabel.ClearValue(TextBlock.ForegroundProperty);
            markerLegend.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private void ProtoSyncControls()
    {
        protoSyncing = true;
        try
        {
            var recipe = liveDraft ?? draft;
            var enabled = !busy && !faultHeld && SelectedFrame is not null;
            foreach (var item in protoSliders)
            {
                var value = item.Param.Get(recipe);
                item.Slider.Value = value;
                item.Slider.IsEnabled = enabled;
                if (item.Value is { } text) text.Text = item.Param.Format(value);
            }
            SyncPad(recipe);
        }
        finally { protoSyncing = false; }
    }

    private void SyncNegativeToggles()
    {
        foreach (var toggle in protoNegativeToggles) toggle.IsChecked = sourceView.IsChecked;
    }

    private void SyncStatusCopies()
    {
        if (protoBIdentity is null) return;
        var controls = draft.InheritControls
            ? SelectedFrame is { Number: > 1 } frame ? $"following frame {frame.Number - 1}" : "default controls"
            : "local controls";
        protoBIdentity.Text = $"{identity.Text} - {controls}";
        protoBWarnings.Text = warnings.Text.Trim();
        protoBMessage.Text = message.Text;
    }

    // ---------- Shared building blocks ----------

    private static Button ProtoIcon(string glyph, string tip, Brush? foreground = null, double size = 36)
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

    private Button ProtoProxy(Button original, string glyph, string tip, Brush? foreground = null, double size = 36)
    {
        var button = ProtoIcon(glyph, tip, foreground, size);
        button.Click += (_, _) => { if (original.IsEnabled) new ButtonAutomationPeer(original).Invoke(); };
        protoMirrors.Add((button, original));
        return button;
    }

    private Button ProtoAdvancedButton()
    {
        var button = ProtoIcon("\uE713", "Advanced: import XMP, darktable location, step controls");
        button.Click += (_, _) => settingsFlyout.ShowAt(button);
        protoMirrors.Add((button, developSettings));
        return button;
    }

    private StackPanel ProtoTools(Orientation orientation)
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
        protoNegativeToggles.Add(negative);
        protoMirrors.Add((negative, sourceView));
        return new StackPanel
        {
            Orientation = orientation, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                negative,
                ProtoProxy(autoPicks, "\uE945", "Auto-pick backlight + film base"),
                ProtoProxy(pickBacklight, "\uEF3C", "Pick backlight white balance (red marker)", ProtoRed),
                ProtoProxy(pickFilmBase, "\uEF3C", "Pick film base (blue marker)", ProtoBlue),
                ProtoProxy(resetPicks, "\uE7A7", "Reset picks for this frame")
            }
        };
    }

    private TextBlock ProtoResetLabel(string text, ProtoParam param, double size = 12.5)
    {
        var label = new TextBlock { Text = text, FontSize = size, Foreground = ProtoMuted, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(label, $"{param.Label} - double-click to reset");
        label.DoubleTapped += (_, _) => LiveEdit(param, param.Default);
        return label;
    }

    private Slider ProtoSliderFor(ProtoParam param, Brush? tint, TextBlock? value)
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
        protoSliders.Add(new(slider, param, value));
        return slider;
    }

    /// <summary>Compact horizontal row: label | slider | value.</summary>
    private Grid ProtoRow(ProtoParam param, Brush? tint = null)
    {
        var value = new TextBlock { FontSize = 12.5, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = ProtoSliderFor(param, tint, value);
        slider.Margin = new Thickness(6, 0, 6, 0);
        var row = new Grid { Height = 32 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(58) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(50) });
        row.Children.Add(ProtoResetLabel(param.Label, param));
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }

    /// <summary>Vertical channel strip: value on top, slider, label below.</summary>
    private StackPanel ProtoStrip(ProtoParam param, string label, Brush? tint = null, double height = 170)
    {
        var value = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
        var slider = ProtoSliderFor(param, tint, value);
        slider.Orientation = Orientation.Vertical;
        slider.Height = height;
        slider.HorizontalAlignment = HorizontalAlignment.Center;
        var caption = ProtoResetLabel(label, param, 12);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        return new StackPanel { Width = 54, Spacing = 4, Children = { value, slider, caption } };
    }

    private static TextBlock ProtoSection(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, CharacterSpacing = 80, Foreground = ProtoMuted,
        Margin = new Thickness(0, 8, 0, 2)
    };

    private StackPanel ProtoRows(params (ProtoParam Param, Brush? Tint)[] rows)
    {
        var panel = new StackPanel();
        foreach (var (param, tint) in rows) panel.Children.Add(ProtoRow(param, tint));
        return panel;
    }

    private void BuildLiveChip()
    {
        protoLiveText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        protoLiveRing = new ProgressRing { Width = 14, Height = 14, MinWidth = 0, MinHeight = 0, IsActive = false, Visibility = Visibility.Collapsed };
        protoLiveChip = new Border
        {
            Background = ProtoBrush(200, 0, 0, 0), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8),
            IsHitTestVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { protoLiveRing, protoLiveText } }
        };
        AddToPreview(protoLiveChip);
    }

    private void AddToPreview(UIElement element) =>
        protoPreviewArea.Children.Insert(protoPreviewArea.Children.IndexOf(activityOverlay), element);

    // FrameworkElement.Parent is null until the window loads, so walk the logical tree we built.
    private UIElement? ProtoParent(UIElement target)
    {
        UIElement? Walk(UIElement node)
        {
            IEnumerable<UIElement> children = node switch
            {
                Panel panel => panel.Children,
                Border { Child: { } child } => [child],
                ContentControl { Content: UIElement content } => [content],
                _ => []
            };
            foreach (var child in children)
            {
                if (ReferenceEquals(child, target)) return node;
                if (Walk(child) is { } found) return found;
            }
            return null;
        }
        return Walk(root);
    }

    private void BuildSwitcher()
    {
        Button Arrow(string glyph, int delta, string tip)
        {
            var button = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 14, Foreground = new SolidColorBrush(Colors.Black) },
                Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
                Width = 32, Height = 32, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0)
            };
            ToolTipService.SetToolTip(button, tip);
            AutomationProperties.SetName(button, tip);
            button.Click += (_, _) => ProtoCycle(delta);
            return button;
        }
        protoSwitchLabel = new TextBlock
        {
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.Black),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0)
        };
        var pill = new Border
        {
            Background = ProtoBrush(255, 255, 214, 10), CornerRadius = new CornerRadius(18), Padding = new Thickness(4),
            BorderBrush = new SolidColorBrush(Colors.Black), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { Arrow("\uE76B", -1, "Previous variant (Left)"), protoSwitchLabel, Arrow("\uE76C", 1, "Next variant (Right)") }
            }
        };
        AutomationProperties.SetName(pill, "Prototype variant switcher");
        Grid.SetRowSpan(pill, 2);
        Grid.SetColumnSpan(pill, 2);
        root.Children.Add(pill);
    }

    // ---------- Variant A: inspector rail beside the preview ----------

    private void BuildVariantA()
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Develop", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { ProtoAdvancedButton() } };
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        Expander Fold(string title, UIElement content) => new()
        {
            Header = new TextBlock { Text = title, FontSize = 13 }, Content = content, IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 4, 8, 4)
        };

        var panel = new StackPanel
        {
            Children =
            {
                header,
                ProtoSection("EXPOSURE"),
                ProtoRows((PExposure, null), (PBlack, null), (PWhite, null)),
                ProtoSection("COLOR"),
                ProtoRows((PRed, ProtoRed), (PGreen, ProtoGreen), (PBlue, ProtoBlue)),
                Fold("Film base (slow - re-inverts)", ProtoRows((PBaseR, ProtoRed), (PBaseG, ProtoGreen), (PBaseB, ProtoBlue))),
                Fold("Crop", ProtoRows((PCropL, null), (PCropT, null), (PCropR, null), (PCropB, null)))
            }
        };
        var rail = new Border
        {
            Width = 300, Background = ProtoBrush(255, 31, 34, 40), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(10, 0, 0, 0),
            Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        workspace.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        workspace.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(rail, 1);
        Grid.SetRowSpan(rail, 3);
        workspace.Children.Add(rail);
        protoA = rail;

        var tools = ProtoTools(Orientation.Horizontal);
        tools.Margin = new Thickness(8, 0, 0, 0);
        protoPreviewHeader.Children.Add(tools);
        protoAHeaderTools = tools;
    }

    // ---------- Variant B: bottom console replaces the bottom band ----------

    private void BuildVariantB()
    {
        var nav = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 12, 0) };
        foreach (var (item, button) in phaseButtons)
            nav.Children.Add(ProtoProxy(button, item switch
            {
                WorkflowPhase.Capture => "\uE722",
                WorkflowPhase.Develop => "\uE9E9",
                _ => "\uEDE1"
            }, $"{(int)item}. {item}", null, 40));
        nav.Children.Add(new Border { Height = 1, Background = ProtoMuted, Opacity = .3, Margin = new Thickness(4) });
        nav.Children.Add(ProtoProxy(cancel, "\uE769", "Pause / cancel safely", null, 40));
        nav.Children.Add(ProtoProxy(connect, "\uE88E", "Connect USB camera", null, 40));
        nav.Children.Add(ProtoProxy(close, "\uE8BB", "Close workflow", null, 40));

        StackPanel Group(string title, params UIElement[] strips)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var strip in strips) row.Children.Add(strip);
            return new StackPanel { Children = { ProtoSection(title), row } };
        }
        Border Divider() => new() { Width = 1, Background = ProtoMuted, Opacity = .25, Margin = new Thickness(10, 12, 10, 0) };

        var cropButton = ProtoIcon("\uE7A8", "Crop");
        cropButton.Flyout = new Flyout
        {
            Content = new StackPanel
            {
                Width = 320, Children = { ProtoSection("CROP"), ProtoRows((PCropL, null), (PCropT, null), (PCropR, null), (PCropB, null)) }
            }
        };
        var filmBase = Group("FILM BASE", ProtoStrip(PBaseR, "R", ProtoRed, 130), ProtoStrip(PBaseG, "G", ProtoGreen, 130),
            ProtoStrip(PBaseB, "B", ProtoBlue, 130));
        filmBase.Opacity = .8;
        ToolTipService.SetToolTip(filmBase, "Film base changes re-run the darktable inversion (slower)");
        var console = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Group("EXPOSURE", ProtoStrip(PExposure, "EV")),
                Divider(),
                Group("COLOR", ProtoPadInStrip(), ProtoStrip(PRed, "Red", ProtoRed), ProtoStrip(PGreen, "Green", ProtoGreen), ProtoStrip(PBlue, "Blue", ProtoBlue)),
                Divider(),
                Group("LEVELS", ProtoStrip(PBlack, "Black"), ProtoStrip(PWhite, "White")),
                Divider(),
                filmBase,
                Divider(),
                new StackPanel { Children = { ProtoSection("CROP"), cropButton } }
            }
        };
        protoBIdentity = new TextBlock { FontSize = 13, Foreground = ProtoMuted, TextTrimming = TextTrimming.CharacterEllipsis };
        var center = new Grid();
        center.RowDefinitions.Add(new() { Height = GridLength.Auto });
        center.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        center.Children.Add(protoBIdentity);
        var consoleScroll = new ScrollViewer
        {
            Content = console, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(consoleScroll, 1);
        center.Children.Add(consoleScroll);

        protoBWarnings = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Orange) };
        protoBMessage = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var side = new Grid { Width = 300, RowSpacing = 8, Margin = new Thickness(12, 0, 0, 0) };
        side.RowDefinitions.Add(new() { Height = GridLength.Auto });
        side.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var advanced = ProtoAdvancedButton();
        advanced.Height = 40;
        advanced.HorizontalAlignment = HorizontalAlignment.Right;
        side.Children.Add(advanced);
        var status = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 6, Children = { protoBMessage, protoBWarnings } },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(status, 1);
        side.Children.Add(status);

        var layout = new Grid
        {
            Background = ProtoBrush(255, 18, 20, 24), Padding = new Thickness(10, 8, 10, 48),
            Height = protoToolbarBorder.Height
        };
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.Children.Add(nav);
        Grid.SetColumn(center, 1);
        layout.Children.Add(center);
        Grid.SetColumn(side, 2);
        layout.Children.Add(side);
        Grid.SetRow(layout, 1);
        Grid.SetColumnSpan(layout, 2);
        root.Children.Add(layout);
        protoB = layout;

        var tools = ProtoTools(Orientation.Vertical);
        protoBTools = new Border
        {
            Background = ProtoBrush(170, 0, 0, 0), CornerRadius = new CornerRadius(8), Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8),
            Child = tools
        };
        AddToPreview(protoBTools);
    }

    // ---------- Variant C: on-image HUD with a 2D color-balance pad ----------

    private const double PadSize = 150, PadTempRange = 3, PadTintRange = 1.5;

    private void BuildVariantC()
    {
        var padBlock = new StackPanel { Spacing = 4, Children = { ProtoSection("COLOR"), ProtoColorPad() } };


        var more = ProtoIcon("\uE712", "Film base and crop");
        more.Flyout = new Flyout
        {
            Content = new StackPanel
            {
                Width = 320,
                Children =
                {
                    ProtoSection("FILM BASE (slow - re-inverts)"),
                    ProtoRows((PBaseR, ProtoRed), (PBaseG, ProtoGreen), (PBaseB, ProtoBlue)),
                    ProtoSection("CROP"),
                    ProtoRows((PCropL, null), (PCropT, null), (PCropR, null), (PCropB, null))
                }
            }
        };
        var collapse = ProtoIcon("\uE96E", "Hide adjustments");
        var buttons = new StackPanel { Spacing = 6, Children = { more, ProtoAdvancedButton(), collapse } };

        var levels = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { ProtoStrip(PExposure, "EV", null, 150), ProtoStrip(PBlack, "Black", null, 150), ProtoStrip(PWhite, "White", null, 150) }
        };
        var card = new Border
        {
            Background = ProtoBrush(235, 26, 28, 33), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 10, 8),
            BorderBrush = ProtoBrush(50, 255, 255, 255), BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 14,
                Children = { padBlock, new StackPanel { Children = { ProtoSection("EXPOSURE / LEVELS"), levels } }, buttons }
            }
        };
        var reopen = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new FontIcon { Glyph = "\uE9E9", FontSize = 14 }, new TextBlock { Text = "Adjust", FontSize = 13 } }
            },
            Visibility = Visibility.Collapsed, Background = ProtoBrush(220, 26, 28, 33)
        };
        collapse.Click += (_, _) => { card.Visibility = Visibility.Collapsed; reopen.Visibility = Visibility.Visible; };
        reopen.Click += (_, _) => { card.Visibility = Visibility.Visible; reopen.Visibility = Visibility.Collapsed; };
        protoC = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 10), Children = { card, reopen }
        };
        AddToPreview(protoC);

        protoCTools = new Border
        {
            Background = ProtoBrush(170, 0, 0, 0), CornerRadius = new CornerRadius(8), Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8),
            Child = ProtoTools(Orientation.Vertical)
        };
        AddToPreview(protoCTools);
    }

    /// <summary>Color pad sized to sit beside the vertical strips in the bottom console.</summary>
    private StackPanel ProtoPadInStrip()
    {
        var pad = ProtoColorPad();
        pad.Margin = new Thickness(0, 20, 12, 0);
        return pad;
    }

    /// <summary>2D color-balance pad: x = cool/warm, y = magenta/green. Each instance stays in sync via SyncPad.</summary>
    private StackPanel ProtoColorPad()
    {
        var padSurface = new Grid { Width = PadSize, Height = PadSize, Background = new SolidColorBrush(Colors.Transparent) };
        Ellipse padPuck;
        TextBlock padReadout;
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
        padSurface.Children.Add(new Rectangle { Width = 1, Fill = ProtoBrush(70, 255, 255, 255) });
        padSurface.Children.Add(new Rectangle { Height = 1, Fill = ProtoBrush(70, 255, 255, 255) });
        padPuck = new Ellipse { Width = 16, Height = 16, Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 2, Fill = ProtoBrush(90, 0, 0, 0) };
        var puckLayer = new Canvas { IsHitTestVisible = false, Children = { padPuck } };
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
        padReadout = new TextBlock { FontSize = 11.5, Foreground = ProtoMuted, HorizontalAlignment = HorizontalAlignment.Center };
        protoPads.Add((padPuck, padReadout));
        return new StackPanel { Spacing = 4, Children = { padSurface, padReadout } };
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
        foreach (var (padPuck, padReadout) in protoPads)
        {
            Canvas.SetLeft(padPuck, (Math.Clamp(temperature / PadTempRange, -1, 1) + 1) / 2 * PadSize - padPuck.Width / 2);
            Canvas.SetTop(padPuck, (Math.Clamp(tint / PadTintRange, -1, 1) + 1) / 2 * PadSize - padPuck.Height / 2);
            padReadout.Text = $"R {r.RedGain:0.00}   G {r.GreenGain:0.00}   B {r.BlueGain:0.00}";
        }
    }
}
#endif
