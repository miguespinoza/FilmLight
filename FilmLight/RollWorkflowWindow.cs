using FilmLight.Core;
using FilmLight.Fujifilm;
using FilmLight.Workflow;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.System.Display;

namespace FilmLight;

public sealed class RollWorkflowWindow : Window
{
    private readonly Grid root = new() { RequestedTheme = ElementTheme.Dark };
    private readonly Border light = new() { IsHitTestVisible = false, Background = new SolidColorBrush(Colors.White) };
    private readonly Grid workspace = new() { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 26, 30)), Padding = new Thickness(18), RowSpacing = 12 };
    private readonly Image preview = new() { Stretch = Stretch.Uniform };
    private readonly Border activityOverlay = new()
    {
        Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(235, 24, 26, 30))
    };
    private readonly ProgressRing activityRing = new() { Width = 48, Height = 48 };
    private readonly TextBlock activityStage = Text("", 24);
    private readonly TextBlock activityDetail = Text("");
    private readonly TextBlock activityElapsed = Text("", 16);
    private readonly TextBlock activityContext = Text("", 16);
    private readonly Stopwatch operationClock = new();
    private readonly Stopwatch stageClock = new();
    private readonly DispatcherTimer activityTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock previewLabel = Text("Capture a frame to see its positive preview.", 20);
    private readonly TextBlock samplingLabel = Text("Pick exposed backlight for WB, or clear/unexposed film for the base. Samples use linear inputs, not JPEG colors.", 14);
    private readonly Button pickBacklight = Button("Pick backlight WB");
    private readonly Button pickFilmBase = Button("Pick film base");
    private readonly Button autoPicks = Button("Auto-pick light + base");
    private readonly Button resetPicks = Button("Reset picks");
    private readonly ToggleButton sourceView = new() { Content = "Show negative", MinHeight = 48, FontSize = 17 };
    private readonly Button developSettings = Button("Develop settings");
    private readonly Flyout settingsFlyout = new();
    private readonly StackPanel settingsPanel = new() { Spacing = 10 };
    private readonly StackPanel capturePanel = new() { Spacing = 8 };
    private readonly StackPanel exportPanel = new() { Spacing = 10 };
    private readonly TextBlock identity = Text("", 21);
    private readonly TextBlock message = Text("Create or open a roll, capture a picture, then pick the exposed light and clear film.");
    private readonly TextBlock warnings = Text("");
    private readonly TextBlock recipeLabel = Text("");
    private readonly TextBlock exportLabel = Text("");
    private readonly TextBlock jobs = Text("");
    private readonly TextBlock sourceLabel = Text("");
    private readonly CheckBox idleConfirmation = new()
    {
        Content = "Camera is idle: no exposure, download, or processing.", FontSize = 17
    };
    private readonly StackPanel setupRow;
    private readonly ComboBox filmKind = new() { MinWidth = 210, FontSize = 18 };
    private readonly ComboBox mode = new() { MinWidth = 150, FontSize = 18 };
    private readonly ComboBox frames = new() { MinWidth = 220, FontSize = 18 };
    private readonly Button create = Button("New roll");
    private readonly Button open = Button("Open / resume");
    private readonly Button demo = Button("Demo camera");
    private readonly Button nextFrame = Button("Capture frame 1");
    private readonly Button render = Button("Render Preview");
    private readonly Button accept = Button("Accept this preview");
    private readonly Button batch = Button("Export missing / failed");
    private readonly Button retry = Button("Retry selected frame");
    private readonly Button showOutputs = Button("Open positives folder");
    private readonly Button cancel = Button("Pause / cancel safely");
    private readonly Button connect = Button("Connect USB camera");
    private readonly Button release = Button("Confirm idle / release camera");
    private readonly Button close = Button("Close workflow");
    private readonly List<Control> lockedControls = [];
    private readonly Dictionary<WorkflowPhase, Button> phaseButtons = [];
    private readonly Dictionary<string, TextBlock> recipeValues = [];
    private readonly DisplayRequest wake = new();
    private readonly RollLibrary library = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FilmLight", "Rolls"));
    private readonly Action<CameraClient> cameraReleased;
    private readonly Func<Window, XamlRoot, Task<CameraClient?>> connectCamera;
    private CameraClient? camera;
    private IWorkflowCaptureSource? source;
    private RollStore? store;
    private DevelopRecipe draft = new();
    private DevelopRecipe? previewedRecipe;
    private Guid? previewedFrame;
    private string? previewedXmpHash;
    private string? darktablePath = DefaultDarktablePath();
    private BitmapImage? positiveBitmap;
    private BitmapImage? sourceBitmap;
    private Guid? displayedFrame;
    private DevelopRecipe? displayedRecipe;
    private CalibrationTarget? samplingTarget;
    private CancellationTokenSource? cancellation;
    private Task? operation;
    private bool busy;
    private bool previewInProgress;
    private bool capturing;
    private bool connectingCamera;
    private bool faultHeld;
    private bool demoSticky;
    private bool refreshing;
    private bool closing;
    private bool closeReady;
    private bool wakeActive;
    private bool isActive = true;
    private WorkflowPhase phase = WorkflowPhase.Capture;
    private string lastWarning = "";

    public RollWorkflowWindow(CameraClient? connectedCamera, Action<CameraClient> onCameraReleased,
        Func<Window, XamlRoot, Task<CameraClient?>> onConnectCamera)
    {
        camera = connectedCamera;
        cameraReleased = onCameraReleased;
        connectCamera = onConnectCamera;
        if (camera is not null) source = new RealCameraCaptureSource(camera);
        Title = "Film Light - Roll workflow";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 950));
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(light);
        workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var previewHeader = new StackPanel { Spacing = 8 };
        previewHeader.Children.Add(previewLabel);
        previewHeader.Children.Add(frames);
        workspace.Children.Add(previewHeader);
        var previewArea = new Grid();
        previewArea.Children.Add(preview);
        var activityPanel = new StackPanel
        {
            Spacing = 12, MaxWidth = 520, Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };
        activityPanel.Children.Add(activityRing);
        foreach (var label in new[] { activityStage, activityDetail, activityElapsed, activityContext })
        {
            label.TextAlignment = TextAlignment.Center;
            activityPanel.Children.Add(label);
        }
        activityOverlay.Child = activityPanel;
        previewArea.Children.Add(activityOverlay);
        Grid.SetRow(previewArea, 1);
        workspace.Children.Add(previewArea);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(activityRing, "Workflow activity indicator");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(activityRing, "WorkflowActivityIndicator");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(activityRing,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(activityStage, "WorkflowActivityStage");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(activityElapsed, "WorkflowActivityElapsed");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(activityStage,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        activityTimer.Tick += (_, _) => UpdateElapsed();
        var sampling = new StackPanel { Spacing = 8 };
        sampling.Children.Add(HorizontalScroll(Row(sourceView, autoPicks, pickBacklight, pickFilmBase, resetPicks)));
        sampling.Children.Add(samplingLabel);
        Grid.SetRow(sampling, 2);
        workspace.Children.Add(sampling);
        Grid.SetColumn(workspace, 1);
        root.Children.Add(workspace);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(light, "Illumination area");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(preview, "Captured frame preview");

        var toolbar = new StackPanel { Spacing = 8, Padding = new Thickness(18, 12, 18, 4) };
        toolbar.Children.Add(identity);
        toolbar.Children.Add(sourceLabel);
        filmKind.ItemsSource = Enum.GetValues<FilmKind>();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(filmKind, "Film kind");
        filmKind.SelectedItem = FilmKind.ColorNegative;
        mode.ItemsSource = Enum.GetValues<CaptureMode>();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mode, "Capture mode");
        mode.SelectedItem = CaptureMode.White;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(frames, "Representative frame selection");
        setupRow = Row(create, open, demo, filmKind, mode);
        toolbar.Children.Add(HorizontalScroll(setupRow));
        var navigation = Row();
        foreach (var item in Enum.GetValues<WorkflowPhase>().Where(p => p != WorkflowPhase.References))
        {
            var target = item;
            var button = Button($"{(int)item}. {item}");
            button.Click += (_, _) => StartOperation(_ => NavigateAsync(target));
            navigation.Children.Add(button);
            phaseButtons.Add(item, button);
        }
        navigation.Children.Add(cancel);
        navigation.Children.Add(connect);
        navigation.Children.Add(release);
        navigation.Children.Add(close);
        toolbar.Children.Add(HorizontalScroll(navigation));
        toolbar.Children.Add(idleConfirmation);
        idleConfirmation.Checked += (_, _) => Refresh();
        idleConfirmation.Unchecked += (_, _) => Refresh();
        settingsFlyout.Content = new ScrollViewer
        {
            Content = settingsPanel, Height = 440, Width = 430,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        developSettings.Flyout = settingsFlyout;
        capturePanel.Children.Add(HorizontalScroll(Row(nextFrame, developSettings, render, accept)));
        toolbar.Children.Add(capturePanel);
        toolbar.Children.Add(exportPanel);
        warnings.Foreground = new SolidColorBrush(Colors.Orange);
        var statusPanel = new StackPanel { Spacing = 4, Padding = new Thickness(18, 4, 18, 12) };
        statusPanel.Children.Add(new ScrollViewer { Content = warnings, MaxHeight = 40, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        statusPanel.Children.Add(new ScrollViewer { Content = message, MaxHeight = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var toolbarScroll = new ScrollViewer { Content = toolbar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toolbarScroll, "Workflow toolbar");
        var toolbarLayout = new Grid();
        toolbarLayout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        toolbarLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        toolbarLayout.Children.Add(toolbarScroll);
        Grid.SetRow(statusPanel, 1);
        toolbarLayout.Children.Add(statusPanel);
        var toolbarBorder = new Border
        {
            // Keep illumination geometry fixed during exposures, previews and faults.
            Height = 340,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 20, 24)),
            Child = toolbarLayout
        };
        Grid.SetRow(toolbarBorder, 1);
        Grid.SetColumnSpan(toolbarBorder, 2);
        root.Children.Add(toolbarBorder);
        Content = root;

        BuildRecipeControls();
        exportPanel.Children.Add(exportLabel);
        exportPanel.Children.Add(HorizontalScroll(Row(batch, retry, showOutputs)));
        exportPanel.Children.Add(new ScrollViewer { Content = jobs, MaxHeight = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        create.Click += (_, _) => StartOperation(CreateAsync);
        open.Click += (_, _) => StartOperation(OpenAsync);
        demo.Click += (_, _) =>
        {
            if (busy || faultHeld || store is not null) return;
            UseDemo();
            Refresh();
        };
        nextFrame.Click += (_, _) => StartOperation(CaptureAsync);
        render.Click += (_, _) => { settingsFlyout.Hide(); StartOperation(RenderAsync); };
        accept.Click += (_, _) => StartOperation(token => AcceptAsync(token));
        batch.Click += (_, _) => StartOperation(token => ExportAsync(false, token));
        retry.Click += (_, _) => StartOperation(token => ExportAsync(true, token));
        showOutputs.Click += (_, _) => StartOperation(async _ =>
        {
            if (store is null) throw new InvalidOperationException("Open a roll first.");
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(PositiveRoot);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder))
                throw new InvalidOperationException("Windows could not open the positives folder.");
        });
        cancel.Click += (_, _) =>
        {
            cancellation?.Cancel();
            SetActivity("Pausing safely", capturing
                ? "Waiting for the current exposure and download to finish. Illumination stays held."
                : "Stopping processing. Completed captures and diagnostic files remain saved.");
            message.Text = capturing
                ? "Pausing: current camera exposure/download must finish. Illumination stays held."
                : "Pausing: stopping processing. Saved captures remain available.";
        };
        release.Click += (_, _) => StartOperation(ReleaseCameraAsync, allowFault: true);
        connect.Click += (_, _) => StartOperation(ConnectCameraAsync);
        close.Click += async (_, _) => await RequestCloseAsync();
        frames.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            ClearDisplayedPreview();
            InvalidatePreview();
            if (SelectedFrame is not null) StartOperation(RenderAsync);
            else Refresh();
        };
        sourceView.Checked += (_, _) => UpdatePreviewImage();
        sourceView.Unchecked += (_, _) => UpdatePreviewImage();
        pickBacklight.Click += (_, _) => BeginSampling(CalibrationTarget.Backlight);
        pickFilmBase.Click += (_, _) => BeginSampling(CalibrationTarget.FilmBase);
        autoPicks.Click += (_, _) => StartOperation(AutoPickAsync);
        resetPicks.Click += (_, _) => StartOperation(async token =>
        {
            await SaveDraftAsync(draft with { PictureCalibration = new() }, token);
            await RenderAsync(token);
        });
        preview.PointerPressed += PickPreviewPoint;
        mode.SelectionChanged += (_, _) => { if (!refreshing) Refresh(); };
        lockedControls.AddRange([create, open, demo, filmKind, mode, frames, nextFrame, render, accept,
            batch, retry, showOutputs, autoPicks, pickBacklight, pickFilmBase, resetPicks, developSettings, sourceView]);
        lockedControls.AddRange(phaseButtons.Values);
        Activated += (_, args) =>
        {
            isActive = args.WindowActivationState != WindowActivationState.Deactivated;
            if (!isActive && capturing)
            {
                cancellation?.Cancel();
                SetActivity("Pausing safely", "Focus lost. Waiting for the current exposure/download; light stays held.");
                message.Text = "Focus lost: pausing after the current exposure/download. Light held.";
            }
            if (isActive) AcquireWake();
            else if (!busy && !faultHeld) ReleaseWake();
        };
        AppWindow.Closing += async (_, args) =>
        {
            if (closeReady) return;
            args.Cancel = true;
            await RequestCloseAsync();
        };
        Closed += (_, _) =>
        {
            activityTimer.Stop();
            ReleaseWake();
        };
        Refresh();
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        AcquireWake();
    }

    private static TextBlock Text(string text, double size = 17) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };

    private static string? DefaultDarktablePath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "darktable", "bin", "darktable-cli.exe");
        return File.Exists(path) ? path : null;
    }

    public void UpdateCameraActivity(CameraActivity activity)
    {
        if ((capturing || connectingCamera) && !closing && cancellation?.IsCancellationRequested != true)
        {
            SetActivity("Camera", $"{(activity.Channel.HasValue ? $"{activity.Channel}: " : "")}{activity.Message}");
            message.Text = $"CAMERA - {(activity.Channel.HasValue ? $"{activity.Channel}: " : "")}{activity.Message}";
        }
    }

    private async Task RequestCloseAsync()
    {
        if (closing || closeReady) return;
        closing = true;
        cancellation?.Cancel();
        SetActivity("Closing safely", "Waiting for the current transaction to finish. Light and camera ownership remain held.");
        message.Text = "Closing after the current transaction finishes. Light and camera ownership remain held.";
        Refresh();
        if (operation is not null) await operation;
        if (faultHeld || camera?.Faulted == true)
        {
            closing = false;
            message.Text = "Camera fault: illumination and lease retained. Confirm the camera is idle, then release it before closing.";
            Refresh();
            return;
        }
        closeReady = true;
        Close();
    }

    private static Button Button(string text) => new()
    {
        Content = text, MinHeight = 58, MinWidth = 100, FontSize = 18, Padding = new Thickness(16, 8, 16, 8)
    };

    private static StackPanel Row(params UIElement[] elements)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var element in elements) row.Children.Add(element);
        return row;
    }

    private static ScrollViewer HorizontalScroll(UIElement element) => new()
    {
        Content = element, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private void UseDemo()
    {
        demoSticky = true;
        source = new DemoCaptureSource();
        Title = "DEMO / SYNTHETIC - Film Light roll workflow";
        message.Text = "DEMO CAMERA selected explicitly. All captures, previews and exports are synthetic, not camera photographs.";
    }

    private void StartOperation(Func<CancellationToken, Task> action, bool allowFault = false)
    {
        if (busy || closing || (faultHeld && !allowFault)) return;
        busy = true;
        cancellation = new();
        operationClock.Restart();
        stageClock.Restart();
        activityContext.Text = "Please wait. Use Pause / cancel safely to stop the operation.";
        SetActivity("Working", "Waiting for the requested operation.");
        activityOverlay.Visibility = Visibility.Visible;
        activityRing.IsActive = true;
        activityTimer.Start();
        UpdateElapsed();
        Refresh();
        operation = RunOperationAsync(action, cancellation);
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action, CancellationTokenSource tokenSource)
    {
        try { await action(tokenSource.Token); }
        catch (OperationCanceledException) when (tokenSource.IsCancellationRequested)
        {
            if (previewInProgress) previewLabel.Text = "Positive preview paused - saved capture retained.";
            message.Text = "Paused. Completed captures remain saved; incomplete inputs are retained. Use Render Preview to retry a canceled preview.";
        }
        catch (Exception ex) when (IsOperationalError(ex))
        {
            if (previewInProgress) previewLabel.Text = "Positive preview failed - see the error below.";
            message.Text = $"Operation failed: {ex.Message}";
        }
        finally
        {
            faultHeld |= camera?.Faulted == true;
            tokenSource.Dispose();
            cancellation = null;
            busy = false;
            previewInProgress = false;
            activityTimer.Stop();
            operationClock.Stop();
            stageClock.Stop();
            activityRing.IsActive = false;
            activityOverlay.Visibility = Visibility.Collapsed;
            if (operationClock.Elapsed.TotalSeconds >= 1)
                message.Text += $" Elapsed {Elapsed(operationClock)}.";
            if (!isActive && !faultHeld) ReleaseWake();
            Refresh();
        }
    }

    private static bool IsOperationalError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or System.Text.Json.JsonException
        or System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception or TimeoutException
        or NotSupportedException;

    private void SetActivity(string stage, string detail)
    {
        if (activityStage.Text != stage) stageClock.Restart();
        activityStage.Text = stage;
        activityDetail.Text = detail;
        UpdateElapsed();
    }

    private static string Elapsed(Stopwatch clock) => $"{(int)clock.Elapsed.TotalMinutes:00}:{clock.Elapsed.Seconds:00}";

    private void UpdateElapsed() =>
        activityElapsed.Text = $"Elapsed {Elapsed(operationClock)} | Current stage {Elapsed(stageClock)}";

    private async Task CreateAsync(CancellationToken token)
    {
        if (source is null) throw new InvalidOperationException("Use Connect USB camera here or explicitly select Demo camera.");
        token.ThrowIfCancellationRequested();
        var kind = (FilmKind)filmKind.SelectedItem;
        var simulated = source.IsDemo;
        var created = await Task.Run(() => library.Create(kind, simulated), token);
        LoadRoll(created, Mode);
        message.Text = $"Created {created.RootPath}. Capture a picture with {RequiredPicks} visible, then pick with the mouse.";
    }

    private async Task OpenAsync(CancellationToken token)
    {
        var path = await PickRollAsync(token);
        if (path is null) return;
        token.ThrowIfCancellationRequested();
        var opened = await Task.Run(() => RollStore.Open(path), token);
        if (demoSticky && !opened.Document.IsDemo)
            throw new InvalidOperationException("This window is permanently DEMO / SYNTHETIC. Close it and reopen the workflow to open a real roll.");
        if (opened.Document.IsDemo && !demoSticky)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot, Title = "Open DEMO / SYNTHETIC roll?",
                Content = "This roll contains simulated camera inputs. Select Demo camera explicitly to continue.",
                PrimaryButtonText = "Use Demo camera", CloseButtonText = "Cancel"
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            UseDemo();
        }
        LoadRoll(opened);
        message.Text = $"Resumed {opened.RootPath}. No unfinished job is treated as successful.";
    }

    private void LoadRoll(RollStore loaded, CaptureMode? requestedMode = null)
    {
        store = loaded;
        phase = loaded.Document.Phase == WorkflowPhase.References ? WorkflowPhase.Capture : loaded.Document.Phase;
        if (phase != loaded.Document.Phase) loaded.SetPhase(phase);
        draft = loaded.Document.Recipes.LastOrDefault() ?? new DevelopRecipe { FilmKind = loaded.Document.FilmKind };
        draft = draft with { PictureCalibration = draft.PictureCalibration ?? new(), Accepted = false, AcceptedAt = null };
        refreshing = true;
        filmKind.SelectedItem = loaded.Document.FilmKind;
        mode.SelectedItem = loaded.Document.Captures.FirstOrDefault()?.Mode ?? requestedMode ?? CaptureMode.White;
        refreshing = false;
        ClearDisplayedPreview();
        InvalidatePreview();
        lastWarning = "";
    }

    private CaptureMode Mode => (CaptureMode)(mode.SelectedItem ?? CaptureMode.White);
    private RollCapture? SelectedFrame => (frames.SelectedItem as FrameChoice)?.Capture;
    private sealed record FrameChoice(RollCapture Capture)
    {
        public override string ToString() => $"Frame {Capture.Number} - {Capture.Mode} - " +
            $"{string.Join("+", Capture.Files.Select(f => Path.GetExtension(f.Path).TrimStart('.').ToUpperInvariant()).Distinct())}" +
            $"{(Capture.IsDemo ? " - SYNTHETIC" : "")}";
    }

    private bool PicksReady => draft.PictureCalibration?.IsComplete(draft.FilmKind) == true;
    private string RequiredPicks => draft.FilmKind == FilmKind.Positive ? "exposed light" : "exposed light and clear film";

    private Task NavigateAsync(WorkflowPhase target)
    {
        settingsFlyout.Hide();
        if (store is null) throw new InvalidOperationException("Create or open a roll first.");
        if (target == WorkflowPhase.Develop && !store.Document.Captures.Any(c => c.Purpose == CapturePurpose.Frame))
            throw new InvalidOperationException("Capture a frame before Develop. There are no frames yet.");
        if (target == WorkflowPhase.Export && AcceptedRecipe() is null)
            throw new InvalidOperationException("Render and accept a preview before Batch Export.");
        store.SetPhase(target);
        phase = target;
        message.Text = target switch
        {
            WorkflowPhase.Capture => "Capture frames: each saved frame is automatically rendered as a positive. Light stays fixed while previewing.",
            WorkflowPhase.Develop => "Choose a representative frame. Adjust processing only, render a preview, then accept the exact recipe.",
            _ => "Batch uses only the accepted revision identified below, never unaccepted edits. Existing successful outputs are retained."
        };
        return Task.CompletedTask;
    }

    private async Task CaptureAsync(CancellationToken token)
    {
        settingsFlyout.Hide();
        if (store is null || source is null) throw new InvalidOperationException("Create/open a roll and select an available capture source.");
        if (source.IsDemo != store.Document.IsDemo) throw new InvalidOperationException("Capture source does not match this roll's immutable demo/real identity.");
        if (store.Document.Captures.Any(c => c.Mode != Mode))
            throw new InvalidOperationException("Capture mode is fixed after the first picture. Create a separate roll for another mode.");
        if (phase != WorkflowPhase.Capture)
            throw new InvalidOperationException("Use the Capture phase for frames.");
        var number = store.Document.Captures.Where(c => c.Purpose == CapturePurpose.Frame).Select(c => c.Number).DefaultIfEmpty(0).Max() + 1;
        activityContext.Text = $"Capturing frame {number}. Illumination stays fixed until all exposures and downloads finish.";
        SetActivity($"Capturing frame {number}", "Setting the light, then exposing and downloading the complete capture.");
        message.Text = $"Frame {number}: exposing / downloading. Light and camera ownership are locked.";
        var directory = Path.Combine(store.RootPath, "Acquisition", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        capturing = true;
        Refresh();
        try
        {
            var files = await source.CaptureAsync(CapturePurpose.Frame, Mode, number, directory, ShowLightAsync, token);
            token.ThrowIfCancellationRequested();
            var activeStore = store;
            var captureMode = Mode;
            var simulated = source.IsDemo;
            message.Text = "Saving the complete capture set into the roll. Inputs remain retained.";
            SetActivity("Saving capture", $"Saving frame {number} into the roll before starting its positive preview.");
            var captured = await Task.Run(() => activeStore.AddCapture(CapturePurpose.Frame, captureMode, files, simulated));
            capturing = false;
            ClearDisplayedPreview();
            InvalidatePreview();
            Refresh();
            refreshing = true;
            frames.SelectedItem = frames.Items.OfType<FrameChoice>().Single(c => c.Capture.Id == captured.Id);
            refreshing = false;
            try
            {
                await RenderAsync(token);
                message.Text = $"Frame {captured.Number} saved. " + (PicksReady
                    ? "Positive preview uses the roll's saved light and film picks. Advance and capture the next frame."
                    : $"Provisional positive preview ready. Pick {RequiredPicks} in this picture; the picks apply to the whole roll.");
            }
            catch (Exception ex) when (IsOperationalError(ex))
            {
                throw new InvalidOperationException($"Frame {captured.Number} was saved, but its positive preview failed: {ex.Message} Use Render Preview to retry; do not recapture.", ex);
            }
        }
        finally { capturing = false; }
    }

    private Task ShowLightAsync(LightChannel channel, CancellationToken token)
    {
        if (DispatcherQueue.HasThreadAccess) return SetLightAsync(channel, token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { await SetLightAsync(channel, token); completion.TrySetResult(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { completion.TrySetCanceled(token); }
            catch (Exception ex) when (IsOperationalError(ex)) { completion.TrySetException(ex); }
        }))
            completion.TrySetException(new InvalidOperationException("The illumination window is unavailable."));
        return completion.Task;
    }

    private async Task SetLightAsync(LightChannel channel, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        light.Background = new SolidColorBrush(channel switch
        {
            LightChannel.White => Colors.White,
            LightChannel.Red => Colors.Red,
            LightChannel.Green => Windows.UI.Color.FromArgb(255, 0, 255, 0),
            LightChannel.Blue => Colors.Blue,
            _ => throw new ArgumentOutOfRangeException(nameof(channel))
        });
        // Allow the display to settle before the camera starts its exposure.
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
    }

    private void BuildRecipeControls()
    {
        settingsPanel.Children.Add(Text("Develop - processing controls, NOT camera exposure", 20));
        settingsPanel.Children.Add(recipeLabel);
        var xmp = Button("Import calibrated XMP");
        var cli = Button("Choose darktable-cli.exe");
        settingsPanel.Children.Add(xmp);
        settingsPanel.Children.Add(cli);
        lockedControls.AddRange([xmp, cli]);
        xmp.Click += (_, _) => StartOperation(async token =>
        {
            if (store is null) throw new InvalidOperationException("Open a roll first.");
            var path = await PickFileAsync(".xmp");
            if (path is null) return;
            token.ThrowIfCancellationRequested();
            var directory = Path.Combine(store.RootPath, "ImportedSettings");
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, $"{Guid.NewGuid():N}.xmp");
            File.Copy(path, destination, false);
            await SaveDraftAsync(draft with { XmpTemplatePath = destination }, token);
            message.Text = $"Imported negadoctor XMP: {Path.GetFileName(path)}. Measured film-base settings and the roll controls are applied to this print recipe.";
        });
        cli.Click += (_, _) => StartOperation(async _ =>
        {
            var path = await PickFileAsync(".exe");
            if (path is null) return;
            if (!Path.GetFileName(path).Equals("darktable-cli.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Select darktable-cli.exe from your darktable installation.");
            darktablePath = path;
            InvalidatePreview();
            message.Text = store?.Document.IsDemo == true
                ? $"darktable location selected: {path}. This DEMO roll continues using the explicitly synthetic renderer."
                : $"Real processor selected: {path}";
        });
        AddSetting("Exposure EV", r => r.ExposureEv, (r, v) => r with { ExposureEv = v }, .25, -5, 5);
        AddSetting("Red gain", r => r.RedGain, (r, v) => r with { RedGain = v }, .05, .1, 4);
        AddSetting("Green gain", r => r.GreenGain, (r, v) => r with { GreenGain = v }, .05, .1, 4);
        AddSetting("Blue gain", r => r.BlueGain, (r, v) => r with { BlueGain = v }, .05, .1, 4);
        AddSetting("Film base R", r => r.FilmBaseRed, (r, v) => r with { FilmBaseRed = v }, .05, .05, 2);
        AddSetting("Film base G", r => r.FilmBaseGreen, (r, v) => r with { FilmBaseGreen = v }, .05, .05, 2);
        AddSetting("Film base B", r => r.FilmBaseBlue, (r, v) => r with { FilmBaseBlue = v }, .05, .05, 2);
        AddSetting("Black point", r => r.BlackPoint, (r, v) => r with { BlackPoint = v }, .01, 0, .9);
        AddSetting("White point", r => r.WhitePoint, (r, v) => r with { WhitePoint = v }, .01, .1, 1);
        AddSetting("Crop left", r => r.CropLeft, (r, v) => r with { CropLeft = v }, .01, 0, .45);
        AddSetting("Crop top", r => r.CropTop, (r, v) => r with { CropTop = v }, .01, 0, .45);
        AddSetting("Crop right", r => r.CropRight, (r, v) => r with { CropRight = v }, .01, 0, .45);
        AddSetting("Crop bottom", r => r.CropBottom, (r, v) => r with { CropBottom = v }, .01, 0, .45);
    }

    private void AddSetting(string label, Func<DevelopRecipe, double> read,
        Func<DevelopRecipe, double, DevelopRecipe> write, double step, double minimum, double maximum)
    {
        var value = Text("");
        recipeValues.Add(label, value);
        var minus = Button("-");
        var plus = Button("+");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(minus, $"Decrease {label}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(plus, $"Increase {label}");
        minus.MinWidth = plus.MinWidth = 65;
        minus.Click += (_, _) => StartOperation(token => SaveDraftAsync(
            write(draft, Math.Round(Math.Clamp(read(draft) - step, minimum, maximum), 4)), token));
        plus.Click += (_, _) => StartOperation(token => SaveDraftAsync(
            write(draft, Math.Round(Math.Clamp(read(draft) + step, minimum, maximum), 4)), token));
        settingsPanel.Children.Add(Row(minus, value, plus));
        lockedControls.AddRange([minus, plus]);
    }

    private Task SaveDraftAsync(DevelopRecipe changed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (store is null) throw new InvalidOperationException("Create or open a roll first.");
        draft = store.SaveRecipe(changed with { Accepted = false, AcceptedAt = null }, false);
        InvalidatePreview();
        message.Text = "Unaccepted edits. Render Preview again before acceptance. Export still uses only a previously accepted revision.";
        return Task.CompletedTask;
    }

    private void InvalidatePreview()
    {
        previewedRecipe = null;
        previewedFrame = null;
        previewedXmpHash = null;
        preview.Opacity = .4;
        samplingTarget = null;
    }

    private void ValidateRecipe()
    {
        if (draft.BlackPoint >= draft.WhitePoint)
            throw new InvalidOperationException("Black point must be less than white point.");
        if (draft.XmpTemplatePath is not null && !File.Exists(draft.XmpTemplatePath))
            throw new InvalidOperationException("The imported XMP is missing. Import it again before rendering.");
    }

    private IFrameProcessor Processor()
    {
        if (store?.Document.IsDemo == true) return new DemoFrameProcessor();
        if (string.IsNullOrWhiteSpace(darktablePath) || !File.Exists(darktablePath))
            throw new InvalidOperationException("Install darktable and choose darktable-cli.exe in Develop settings. Real captures never fall back to a demo processor.");
        return new DarktableFrameProcessor(darktablePath, Path.Combine(store!.RootPath, "Working"));
    }

    private sealed class CallbackProgress(Action<ProcessingProgress> report) : IProgress<ProcessingProgress>
    {
        public void Report(ProcessingProgress value) => report(value);
    }

    private IProgress<ProcessingProgress> Progress()
    {
        var owner = cancellation;
        return new CallbackProgress(p =>
        {
            void Update()
            {
                if (!busy || closing || cancellation != owner || owner?.IsCancellationRequested == true) return;
                var frame = store?.Document.Captures.FirstOrDefault(c => c.Id == p.FrameId);
                var detail = $"{(frame is null ? "" : $"Frame {frame.Number}: ")}{p.Message}";
                SetActivity(p.Stage, detail);
                message.Text = $"{p.Stage}: {detail}" + (p.Total > 0 ? $" ({p.Completed}/{p.Total})" : "");
            }
            if (DispatcherQueue.HasThreadAccess) Update();
            else if (!DispatcherQueue.TryEnqueue(Update))
                throw new InvalidOperationException("The workflow progress window is unavailable.");
        });
    }

    private async Task RenderAsync(CancellationToken token)
    {
        if (store is null || SelectedFrame is not { } frame) throw new InvalidOperationException("Select a representative captured frame.");
        previewInProgress = true;
        previewLabel.Text = $"Frame {frame.Number} - generating positive preview...";
        activityContext.Text = $"Frame {frame.Number} is saved. RAW conversion can take a while, especially the first time. No need to recapture.";
        SetActivity("Starting positive preview", "Preparing the current recipe and saved input.");
        InvalidatePreview();
        ValidateRecipe();
        var exact = draft;
        var xmpHash = XmpHash(exact);
        var result = await Processor().PreviewAsync(store.Document, frame, exact,
            Path.Combine(store.RootPath, "Previews"), Progress(), token);
        token.ThrowIfCancellationRequested();
        if (XmpHash(exact) != xmpHash)
            throw new InvalidDataException("The imported XMP changed during preview. Import a stable calibrated XMP and render again.");
        if (!File.Exists(result.ImagePath) || new FileInfo(result.ImagePath).Length == 0
            || !new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(result.ImagePath).ToLowerInvariant()))
            throw new InvalidDataException("Processor did not return an existing JPG/PNG preview.");
        SetActivity("Loading preview", "Conversion finished. Loading the positive and source images into the window.");
        var bitmap = await LoadBitmapAsync(result.ImagePath);
        var negative = result.SourceImagePath is null ? null : await LoadBitmapAsync(result.SourceImagePath);
        token.ThrowIfCancellationRequested();
        positiveBitmap = bitmap;
        sourceBitmap = negative;
        displayedFrame = frame.Id;
        displayedRecipe = exact;
        sourceView.IsChecked = false;
        UpdatePreviewImage();
        preview.Opacity = 1;
        previewedRecipe = exact;
        previewedFrame = frame.Id;
        previewedXmpHash = xmpHash;
        previewInProgress = false;
        lastWarning = string.Join("\n", result.Warnings);
        message.Text = "Preview rendered for the exact current recipe. " + (PicksReady
            ? "Inspect it, then accept; any manual edit requires another preview."
            : $"Pick {RequiredPicks} in this picture; the picks apply to the whole roll.");
    }

    private static async Task<BitmapImage> LoadBitmapAsync(string path)
    {
        var bitmap = new BitmapImage();
        using var stream = File.OpenRead(path);
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        return bitmap;
    }

    private void ClearDisplayedPreview()
    {
        positiveBitmap = sourceBitmap = null;
        displayedFrame = null;
        displayedRecipe = null;
        preview.Source = null;
        previewLabel.Text = "Capture a frame to see its positive preview.";
        samplingTarget = null;
    }

    private void UpdatePreviewImage()
    {
        preview.Source = sourceView.IsChecked == true ? sourceBitmap : positiveBitmap;
        previewLabel.Text = displayedFrame is null ? "Capture a frame to see its positive preview."
            : $"{(store?.Document.IsDemo == true ? "SYNTHETIC - " : "")}Frame {SelectedFrame?.Number} - {(sourceView.IsChecked == true ? "negative / source" : "positive preview")}";
    }

    private void BeginSampling(CalibrationTarget target)
    {
        if (busy || displayedFrame != SelectedFrame?.Id || displayedRecipe != draft || sourceBitmap is null) return;
        samplingTarget = target;
        sourceView.IsChecked = true;
        samplingLabel.Text = target == CalibrationTarget.Backlight
            ? "Click an exposed backlight patch (no film). A 5 x 5 linear-input patch sets light normalization for the whole roll."
            : "Click clear/unexposed developed film, not the image or holder. A 5 x 5 linear-input patch sets the film base for the whole roll.";
    }

    private async Task AutoPickAsync(CancellationToken token)
    {
        if (store is null || SelectedFrame is not { } frame)
            throw new InvalidOperationException("Select a captured frame first.");
        if (!store.Document.IsDemo) _ = Processor();
        samplingTarget = null;
        activityContext.Text = $"Frame {frame.Number} is saved. Looking for exposed light through sprocket holes and clear film between them.";
        SetActivity("Preparing automatic picks", "Reading the uncropped linear input. Existing picks remain unchanged if detection fails.");
        var changed = await FrameCalibration.AutoSampleAsync(store.Document, frame, draft,
            Path.Combine(store.RootPath, "Working"), darktablePath, token, Progress());
        await SaveDraftAsync(changed, token);
        var picks = changed.PictureCalibration!;
        static string Rgb(PictureSample sample) => $"({sample.Red:F3}, {sample.Green:F3}, {sample.Blue:F3})";
        samplingLabel.Text = $"Automatic picks from frame {frame.Number} (linear RGB): light {Rgb(picks.Light!)}"
            + (picks.FilmBase is { } basis ? $"; film {Rgb(basis)}" : "")
            + ". Inspect the positive, then accept; manual pickers can override.";
        await RenderAsync(token);
        message.Text = "Automatic picks saved in the draft. Inspect the positive and accept this preview to apply them to the whole roll.";
    }

    private void PickPreviewPoint(object sender, PointerRoutedEventArgs args)
    {
        if (samplingTarget is not { } target || busy || faultHeld || preview.Source is not BitmapImage image
            || displayedFrame != SelectedFrame?.Id || displayedRecipe != draft) return;
        var point = args.GetCurrentPoint(preview);
        if (!point.Properties.IsLeftButtonPressed) return;
        args.Handled = true;
        if (!FrameCalibration.TryMapPoint(preview.ActualWidth, preview.ActualHeight, image.PixelWidth,
            image.PixelHeight, point.Position.X, point.Position.Y, out var x, out var y))
        {
            samplingLabel.Text = "Click inside the image, not its letterbox border.";
            return;
        }
        StartOperation(async token =>
        {
            if (store is null || SelectedFrame is not { } frame)
                throw new InvalidOperationException("Select a captured frame first.");
            if (!store.Document.IsDemo) _ = Processor();
            activityContext.Text = $"Frame {frame.Number} is saved. Measuring the patch, then updating the positive preview.";
            SetActivity("Reading picture sample", "Preparing the full-resolution linear input for the selected patch.");
            var changed = await FrameCalibration.SampleAsync(store.Document, frame, draft, target, x, y,
                Path.Combine(store.RootPath, "Working"), darktablePath, token, Progress());
            await SaveDraftAsync(changed, token);
            samplingLabel.Text = $"{target} sampled from frame {frame.Number} at {x:P0}, {y:P0}. Saved in the roll recipe.";
            await RenderAsync(token);
            if (PicksReady)
            {
                await AcceptAsync(token, keepPreview: true);
                message.Text = "Preview rendered for the exact current recipe. Light and film picks are applied to the entire roll.";
            }
        });
    }

    private Task AcceptAsync(CancellationToken token, bool keepPreview = false)
    {
        token.ThrowIfCancellationRequested();
        if (store is null || previewedRecipe != draft || previewedFrame != SelectedFrame?.Id)
            throw new InvalidOperationException("Render a successful preview of this exact recipe and selected frame first.");
        if (XmpHash(draft) != previewedXmpHash)
        {
            InvalidatePreview();
            throw new InvalidOperationException("The imported XMP changed since preview. Render it again before acceptance.");
        }
        if (!PicksReady)
            throw new InvalidOperationException("Pick exposed light and, for negatives, clear film in a captured picture first.");
        var accepted = store.SaveRecipe(draft, true);
        draft = accepted with { Accepted = false, AcceptedAt = null };
        if (keepPreview)
        {
            previewedRecipe = displayedRecipe = draft;
            previewedXmpHash = XmpHash(draft);
        }
        else InvalidatePreview();
        message.Text = $"Accepted immutable recipe {accepted.Id}, revision {accepted.Revision}. Batch Export will use this revision.";
        return Task.CompletedTask;
    }

    private DevelopRecipe? AcceptedRecipe() => store?.Document.Recipes
        .Where(r => r.Accepted && r.Id == store.Document.AcceptedRecipeId).OrderByDescending(r => r.Revision).FirstOrDefault();

    private static string? XmpHash(DevelopRecipe recipe)
    {
        if (recipe.XmpTemplatePath is null) return null;
        using var stream = File.OpenRead(recipe.XmpTemplatePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private async Task ExportAsync(bool selectedOnly, CancellationToken token)
    {
        if (store is null || AcceptedRecipe() is not { } acceptedRecipe)
            throw new InvalidOperationException("No accepted recipe. Render and accept a preview first.");
        var processor = Processor();
        var candidates = store.Document.Captures.Where(c => c.Purpose == CapturePurpose.Frame
            && (!selectedOnly || c.Id == SelectedFrame?.Id)).OrderBy(c => c.Number).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("Select a captured frame.");
        var completed = 0;
        var failed = 0;
        var warningMessages = new List<string>();
        foreach (var frame in candidates)
        {
            token.ThrowIfCancellationRequested();
            var previous = store.Document.Exports.LastOrDefault(e => e.FrameId == frame.Id
                && e.RecipeId == acceptedRecipe.Id && e.RecipeRevision == acceptedRecipe.Revision);
            if (previous?.State == ExportState.Complete)
            {
                if (ValidOutputs(previous.OutputFiles))
                {
                    completed++;
                    continue;
                }
                store.RecordExport(new(frame.Id, acceptedRecipe.Id, acceptedRecipe.Revision, ExportState.Failed,
                    [], "Saved outputs are missing or empty; regenerating in a new folder.", DateTimeOffset.UtcNow));
            }
            var outputDirectory = Path.Combine(store.RootPath, "Processing");
            FrameExport Record(ExportState state, IReadOnlyList<string> files, string? error) =>
                new(frame.Id, acceptedRecipe.Id, acceptedRecipe.Revision, state, files, error, DateTimeOffset.UtcNow);
            store.RecordExport(Record(ExportState.Processing, [], null));
            activityContext.Text = $"Batch: {completed}/{candidates.Length} complete, {failed} failed. Exporting frame {frame.Number} with accepted revision {acceptedRecipe.Revision}.";
            SetActivity("Starting frame export", $"Preparing saved frame {frame.Number}.");
            message.Text = $"Exporting frame {frame.Number}; accepted revision {acceptedRecipe.Revision}; {completed}/{candidates.Length} complete.";
            try
            {
                Directory.CreateDirectory(outputDirectory);
                var result = await processor.ExportAsync(store.Document, frame, acceptedRecipe, outputDirectory, Progress(), token);
                token.ThrowIfCancellationRequested();
                if (!ValidOutputs(result.OutputFiles))
                    throw new InvalidDataException("Processor did not produce both a nonempty TIFF and JPG.");
                SetActivity("Publishing positives", $"Moving validated frame {frame.Number} output into the positives folder.");
                var positives = PublishPositives(frame, acceptedRecipe, result.OutputFiles, token);
                store.RecordExport(Record(ExportState.Complete, positives, null));
                try { PruneGeneratedImages(Path.GetDirectoryName(result.OutputFiles[0])!); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warningMessages.Add($"Frame {frame.Number}: positive outputs are saved, but generated working images could not be removed: {ex.Message}");
                }
                warningMessages.AddRange(result.Warnings.Select(w => $"Frame {frame.Number}: {w}"));
                completed++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                store.RecordExport(Record(ExportState.Canceled, [], "Canceled by operator; partial outputs and all inputs retained."));
                throw;
            }
            catch (Exception ex) when (IsOperationalError(ex))
            {
                store.RecordExport(Record(ExportState.Failed, [], ex.Message));
                failed++;
                warningMessages.Add($"Frame {frame.Number} FAILED: {ex.Message}");
            }
            lastWarning = string.Join("\n", warningMessages);
            Refresh();
        }
        message.Text = $"Batch finished: {completed}/{candidates.Length} complete, {failed} failed. Accepted revision {acceptedRecipe.Revision}. Inputs retained; retry missing/failed jobs explicitly.";
    }

    private static bool ValidOutputs(IReadOnlyList<string> paths) =>
        paths.All(p => File.Exists(p) && new FileInfo(p).Length > 0)
        && paths.Any(p => new[] { ".tif", ".tiff" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
        && paths.Any(p => new[] { ".jpg", ".jpeg" }.Contains(Path.GetExtension(p).ToLowerInvariant()));

    private string PositiveRoot => Path.Combine(store!.RootPath, store.Document.IsDemo ? "SYNTHETIC-Positives" : "Positives");

    private void PruneGeneratedImages(string directory)
    {
        var processingRoot = Path.GetFullPath(Path.Combine(store!.RootPath, "Processing")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(directory).StartsWith(processingRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Generated-image cleanup was outside the roll's processing folder.");
        foreach (var name in new[] { "normalized-linear.tif", "developed-linear.tif", "positive-linear.tif" })
            File.Delete(Path.Combine(directory, name));
    }

    private IReadOnlyList<string> PublishPositives(RollCapture frame, DevelopRecipe recipe,
        IReadOnlyList<string> files, CancellationToken token)
    {
        if (store is null) throw new InvalidOperationException("Open a roll first.");
        var staging = Path.Combine(store.RootPath, "Processing", $".publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var names = new List<string>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".tif" or ".tiff" or ".jpg" or ".jpeg"))
                throw new InvalidDataException("Only positive TIFF/JPEG images may be published.");
            var name = $"{(store.Document.IsDemo ? "SYNTHETIC-" : "")}Frame-{frame.Number:D4}{extension}";
            File.Move(file, Path.Combine(staging, name));
            names.Add(name);
        }
        File.WriteAllText(Path.Combine(staging, "recipe.json"), JsonSerializer.Serialize(recipe));
        token.ThrowIfCancellationRequested();
        var parent = Path.Combine(PositiveRoot, $"r{recipe.Revision}-{recipe.Id.ToString("N")[..8]}");
        Directory.CreateDirectory(parent);
        var published = Path.Combine(parent, $"Frame-{frame.Number:D4}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.Move(staging, published);
        return names.Select(name => Path.Combine(published, name)).ToArray();
    }

    private async Task ReleaseCameraAsync(CancellationToken token)
    {
        if (camera is null) return;
        if (idleConfirmation.IsChecked != true)
            throw new InvalidOperationException("Confirm the camera is physically idle before releasing its lease.");
        token.ThrowIfCancellationRequested();
        var released = camera;
        string? teardownError = null;
        try { await released.DisconnectAsync(); }
        catch (Exception ex) when (IsOperationalError(ex)) { teardownError = ex.Message; }
        await released.DisposeAsync();
        camera = null;
        if (source is RealCameraCaptureSource) source = null;
        faultHeld = false;
        idleConfirmation.IsChecked = false;
        cameraReleased(released);
        message.Text = teardownError is null
            ? "Camera released after explicit idle confirmation. Roll remains open; use Connect USB camera here to reconnect."
            : $"Camera released after idle confirmation; teardown error: {teardownError}. Roll remains open; use Connect USB camera to reconnect.";
    }

    private async Task ConnectCameraAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (camera is not null || faultHeld)
            throw new InvalidOperationException("Confirm the camera is idle and release it before reconnecting.");
        if (demoSticky || store?.Document.IsDemo == true)
            throw new InvalidOperationException("A DEMO / SYNTHETIC roll cannot use a real camera.");
        message.Text = "Connect USB camera: roll remains open while camera setup is shown.";
        CameraClient? connected;
        connectingCamera = true;
        try { connected = await connectCamera(this, root.XamlRoot); }
        finally { connectingCamera = false; }
        if (connected is null)
        {
            message.Text = "Camera connection canceled. Roll remains open and unchanged; use Connect USB camera to retry.";
            return;
        }
        camera = connected;
        source = new RealCameraCaptureSource(connected);
        idleConfirmation.IsChecked = false;
        message.Text = $"{connected.Connection?.Model} connected. Roll, frames and recipe edits retained. " +
            "The saved light and film picks still apply; keep camera exposure and lighting unchanged.";
    }

    private async Task<string?> PickRollAsync(CancellationToken token)
    {
        var entries = await Task.Run(() => library.List(token), token);
        token.ThrowIfCancellationRequested();
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single, MinHeight = 120, MaxHeight = 350,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "Saved rolls");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(list, "SavedRolls");
        var details = Text(entries.Count == 0
            ? "No saved rolls yet. Create a new roll, or add an older roll below."
            : "Select a roll to resume. Pictures count complete frames, not individual RGB exposures or RAW+JPEG files.");
        var panel = new StackPanel { Spacing = 12, MinWidth = 480, MaxWidth = 680 };
        panel.Children.Add(details);
        panel.Children.Add(list);
        panel.Children.Add(Text($"New rolls are saved automatically in:\n{library.RootPath}", 14));
        var existingPath = new TextBox
        {
            Header = "Older roll stored elsewhere", PlaceholderText = "Paste the full roll folder path",
            MinHeight = 48
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(existingPath, "Existing roll folder path");
        var add = Button("Add existing roll");
        var error = Text("");
        error.Foreground = new SolidColorBrush(Colors.Orange);
        panel.Children.Add(existingPath);
        panel.Children.Add(add);
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot, Title = "Open / resume roll", Content = panel,
            PrimaryButtonText = "Resume roll", CloseButtonText = "Cancel",
            IsPrimaryButtonEnabled = false, DefaultButton = ContentDialogButton.Primary
        };
        var linking = false;
        dialog.Closing += (_, args) => args.Cancel = linking;
        void Populate(IReadOnlyList<RollLibraryEntry> rolls, string? selectedPath = null)
        {
            list.Items.Clear();
            foreach (var entry in rolls)
            {
                var document = entry.Document;
                var content = new StackPanel { Spacing = 4, Padding = new Thickness(4) };
                content.Children.Add(Text(document is null ? Path.GetFileName(entry.RootPath)
                    : $"{(document.IsDemo ? "DEMO / SYNTHETIC - " : "")}{document.Name}", 18));
                if (document is not null)
                {
                    var pictures = document.Captures.Count(c => c.Purpose == CapturePurpose.Frame);
                    var captureMode = document.Captures.FirstOrDefault()?.Mode;
                    content.Children.Add(Text($"Created {entry.CreatedAt?.ToLocalTime():g} | Updated {entry.UpdatedAt?.ToLocalTime():g}", 14));
                    content.Children.Add(Text($"{pictures} picture{(pictures == 1 ? "" : "s")} | {document.FilmKind} | " +
                        $"{(captureMode == CaptureMode.Rgb ? "RGB" : captureMode?.ToString() ?? "No captures")} | {document.Phase}", 14));
                }
                content.Children.Add(Text(entry.RootPath, 13));
                if (entry.Error is not null) content.Children.Add(Text($"Unavailable: {entry.Error}", 14));
                var item = new ListViewItem
                {
                    Content = content, Tag = entry, IsEnabled = entry.Error is null
                        && (!demoSticky || document?.IsDemo == true),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item,
                    string.Join(". ", content.Children.OfType<TextBlock>().Select(text => text.Text)));
                list.Items.Add(item);
                if (entry.RootPath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase)) list.SelectedItem = item;
            }
        }
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled =
            list.SelectedItem is ListViewItem { IsEnabled: true };
        Populate(entries);
        add.Click += async (_, _) =>
        {
            linking = true;
            add.IsEnabled = false;
            list.IsEnabled = false;
            existingPath.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;
            error.Text = "";
            try
            {
                var path = existingPath.Text.Trim().Trim('"');
                var linked = await Task.Run(() => library.Link(path), token);
                Populate(await Task.Run(() => library.List(token), token), linked.RootPath);
                details.Text = "Existing roll added. Its original files stay in their current location.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                error.Text = "Adding the roll was canceled.";
            }
            catch (Exception ex) when (IsOperationalError(ex))
            {
                error.Text = $"Cannot add roll: {ex.Message}";
            }
            finally
            {
                linking = false;
                add.IsEnabled = true;
                list.IsEnabled = true;
                existingPath.IsEnabled = true;
                dialog.IsPrimaryButtonEnabled = list.SelectedItem is ListViewItem { IsEnabled: true };
                if (token.IsCancellationRequested) dialog.Hide();
            }
        };
        using var registration = token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        var result = await dialog.ShowAsync();
        token.ThrowIfCancellationRequested();
        if (result != ContentDialogResult.Primary) return null;
        if (list.SelectedItem is not ListViewItem { IsEnabled: true, Tag: RollLibraryEntry selected })
            throw new InvalidOperationException("Select an available roll before resuming.");
        return selected.RootPath;
    }

    private async Task<string?> PickFileAsync(string extension)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private void Refresh()
    {
        refreshing = true;
        try
        {
            var document = store?.Document;
            var synthetic = demoSticky || document?.IsDemo == true;
            var lightHeld = capturing || faultHeld;
            identity.Text = $"{(synthetic ? "DEMO / SYNTHETIC - " : "REAL WORKFLOW - ")}{document?.Name ?? "No roll open"} - {phase}";
            setupRow.Visibility = (document is null || document.Captures.Count == 0) && !lightHeld
                ? Visibility.Visible : Visibility.Collapsed;
            sourceLabel.Visibility = document is null && !lightHeld ? Visibility.Visible : Visibility.Collapsed;
            warnings.Visibility = capturing ? Visibility.Collapsed : Visibility.Visible;
            message.Visibility = faultHeld ? Visibility.Collapsed : Visibility.Visible;
            sourceLabel.Text = source?.Description ?? "NO CAMERA SOURCE - use Connect USB camera here, or explicitly choose Demo camera.";
            if (synthetic) sourceLabel.Text = "DEMO / SYNTHETIC - NOT CAMERA PHOTOGRAPHS. " + sourceLabel.Text;
            foreach (var control in lockedControls) control.IsEnabled = !busy && !faultHeld;
            create.IsEnabled &= source is not null && store is null;
            open.IsEnabled &= store is null;
            demo.IsEnabled &= store is null && !demoSticky;
            filmKind.IsEnabled &= store is null;
            mode.IsEnabled &= document?.Captures.Count is null or 0;
            foreach (var item in phaseButtons)
            {
                item.Value.IsEnabled &= store is not null;
                item.Value.Opacity = item.Key == phase ? 1 : .65;
                item.Value.Visibility = lightHeld ? Visibility.Collapsed : Visibility.Visible;
            }
            cancel.IsEnabled = busy && !closing;
            close.IsEnabled = !faultHeld && !closing;
            connect.Visibility = !synthetic && camera is null ? Visibility.Visible : Visibility.Collapsed;
            connect.IsEnabled = camera is null && !synthetic && !busy && !faultHeld && !closing;
            release.Visibility = camera is not null ? Visibility.Visible : Visibility.Collapsed;
            release.IsEnabled = camera is not null && !busy && !closing && idleConfirmation.IsChecked == true;
            idleConfirmation.Visibility = camera is not null ? Visibility.Visible : Visibility.Collapsed;
            idleConfirmation.IsEnabled = camera is not null && !busy && !closing;
            capturePanel.Visibility = document is not null && phase != WorkflowPhase.Export && !lightHeld ? Visibility.Visible : Visibility.Collapsed;
            exportPanel.Visibility = phase == WorkflowPhase.Export ? Visibility.Visible : Visibility.Collapsed;
            nextFrame.Visibility = phase == WorkflowPhase.Capture ? Visibility.Visible : Visibility.Collapsed;
            nextFrame.IsEnabled &= store is not null && source is not null;
            nextFrame.Content = $"Capture frame {(document?.Captures.Where(c => c.Purpose == CapturePurpose.Frame).Select(c => c.Number).DefaultIfEmpty(0).Max() ?? 0) + 1}";
            var selected = SelectedFrame?.Id;
            var choices = document?.Captures.Where(c => c.Purpose == CapturePurpose.Frame).OrderBy(c => c.Number)
                .Select(c => new FrameChoice(c)).ToArray() ?? [];
            frames.ItemsSource = choices;
            frames.SelectedItem = choices.FirstOrDefault(c => c.Capture.Id == selected) ?? choices.FirstOrDefault();
            render.IsEnabled &= SelectedFrame is not null && store is not null;
            var canSample = displayedFrame == SelectedFrame?.Id && displayedRecipe == draft && positiveBitmap is not null;
            pickBacklight.IsEnabled &= canSample && sourceBitmap is not null;
            pickFilmBase.IsEnabled &= canSample && sourceBitmap is not null && document?.FilmKind != FilmKind.Positive;
            autoPicks.IsEnabled &= SelectedFrame is not null && store is not null;
            autoPicks.Content = document?.FilmKind == FilmKind.Positive ? "Auto-pick light" : "Auto-pick light + base";
            sourceView.IsEnabled &= sourceBitmap is not null;
            resetPicks.IsEnabled &= SelectedFrame is not null && store is not null;
            developSettings.IsEnabled &= store is not null;
            accept.IsEnabled &= previewedRecipe == draft && previewedFrame == SelectedFrame?.Id
                && previewedRecipe is not null && PicksReady;
            batch.IsEnabled &= AcceptedRecipe() is not null;
            retry.IsEnabled &= AcceptedRecipe() is not null && SelectedFrame is not null;
            showOutputs.IsEnabled &= store is not null && Directory.Exists(PositiveRoot);
            var accepted = AcceptedRecipe();
            var applied = accepted is not null && draft == (accepted with { Accepted = false, AcceptedAt = null });
            recipeLabel.Text = $"Current settings: {draft.Id}, revision {draft.Revision} ({(applied ? "applied to the whole roll" : "unaccepted until preview + accept")}).\nXMP: {draft.XmpTemplatePath ?? "not imported"}\ndarktable: {darktablePath ?? "not selected"}";
            exportLabel.Text = accepted is null ? "No accepted recipe." :
                $"{(synthetic ? "SIMULATED / SYNTHETIC EXPORTS\n" : "")}Export uses ACCEPTED {accepted.Id}, revision {accepted.Revision}.\nUnaccepted Develop edits are NEVER exported.\nPositive TIFF/JPEG only: {PositiveRoot}";
            jobs.Text = string.Join("\n", document?.Exports.Select(e =>
                $"Frame {document.Captures.FirstOrDefault(c => c.Id == e.FrameId)?.Number}: {e.State} - revision {e.RecipeRevision}{(e.Error is null ? "" : " - " + e.Error)}") ?? []);
            double[] values = [draft.ExposureEv, draft.RedGain, draft.GreenGain, draft.BlueGain, draft.FilmBaseRed,
                draft.FilmBaseGreen, draft.FilmBaseBlue, draft.BlackPoint, draft.WhitePoint,
                draft.CropLeft, draft.CropTop, draft.CropRight, draft.CropBottom];
            var index = 0;
            foreach (var pair in recipeValues) pair.Value.Text = $"{pair.Key}: {values[index++]:0.00}";
            warnings.Text = (synthetic ? "DEMO / SYNTHETIC. Simulated inputs and outputs, not real-camera processing.\n" : "") +
                (faultHeld ? "CAMERA FAULT: LIGHT AND SOURCE LEASE HELD. Confirm idle before release, navigation or closing.\n" : "") +
                (document is not null && !PicksReady ? $"Capture a picture and pick {RequiredPicks}. This draft is provisional and cannot be exported; any previously accepted revision remains available.\n" : "") + lastWarning;
            if (!synthetic)
                warnings.Text += "\nPositive previews require RAW or RAW+JPEG with UNCOMPRESSED or LOSSLESS COMPRESSED RAF and darktable 5.6.2. No separate reference photos are needed. Keep camera exposure and lighting fixed; originals remain untouched.";
            if (faultHeld)
                warnings.Text = $"CAMERA FAULT: light and lease retained. Confirm physical idle before releasing.\n{message.Text}";
        }
        finally { refreshing = false; }
    }

    private void AcquireWake()
    {
        if (wakeActive) return;
        try { wake.RequestActive(); wakeActive = true; }
        catch (System.Runtime.InteropServices.COMException ex) { message.Text = $"Cannot keep display awake: {ex.Message}"; }
    }

    private void ReleaseWake()
    {
        if (!wakeActive) return;
        try { wake.RequestRelease(); wakeActive = false; }
        catch (System.Runtime.InteropServices.COMException ex) { message.Text = $"Cannot release display wake request: {ex.Message}"; }
    }
}
