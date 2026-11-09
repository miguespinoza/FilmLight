using FilmLight.Core;
using FilmLight.Fujifilm;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private readonly Button workflowButton = MakeButton("Roll workflow", 190);
    private RollWorkflowWindow? workflowWindow;
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
            if (workflowWindow is not null) return;
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
        cameraButton.Click += async (_, _) => await ShowCameraAsync();
        setupButton.Click += async (_, _) => await ShowSetupAsync();
        controls.Children.Add(setupButton);
        controls.Children.Add(workflowButton);
        workflowButton.Click += (_, _) => OpenRollWorkflow();
        toolbar.Children.Add(new ScrollViewer
        {
            Content = controls,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        toolbar.Children.Add(error);
        Content = root;

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += async (_, args) =>
        {
            args.Handled = true;
            if (workflowWindow is not null) return;
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
            if (workflowWindow is not null) return;
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
            if (workflowWindow is not null)
            {
                ShowError("Close the roll workflow before closing Film Light. The workflow owns the camera.");
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
        repeat.IsOn = settings.Repeat;
        initializing = false;
        status.Text = "Uniform white \u00b7 nominal 5000 K";
        Refresh();
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
        if (workflowWindow is not null) return;
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
        if (workflowWindow is not null) return;
        var locked = running || connecting || camera?.Faulted == true || cameraActivity.Stage == CameraStage.Busy;
        workflowButton.IsEnabled = !locked && !checkingCamera && activeCapture is null && !closeRequested;
        white.IsEnabled = rgb.IsEnabled = reset.IsEnabled = !locked;
        repeat.IsEnabled = !locked;
        cameraButton.IsEnabled = !running && !connecting;
        setupButton.IsEnabled = !locked;
        cameraButton.Content = connecting ? "Connecting..." : camera?.Faulted == true ? "Re-arm" : camera is null ? "Camera" : "X-T5";
        repeat.Visibility = rgbMode ? Visibility.Visible : Visibility.Collapsed;
        start.Visibility = rgbMode || camera is not null ? Visibility.Visible : Visibility.Collapsed;
        start.IsEnabled = !connecting && camera?.Faulted != true && (running || cameraActivity.Stage != CameraStage.Busy);
        valueLabel.Text = rgbMode ? camera is null ? "SECONDS / COLOR" : "USB TETHERED" : "NOMINAL WHITE";
        value.Text = rgbMode ? camera is null ? $"{settings.Interval} s" : "AUTO" : $"{settings.Temperature} K";
        minus.IsEnabled = !locked && (rgbMode ? camera is null && settings.Interval > 1 : settings.Temperature > 3000);
        plus.IsEnabled = !locked && (rgbMode ? camera is null && settings.Interval < 120 : settings.Temperature < 7500);
        white.Background = new SolidColorBrush(rgbMode ? Color.FromArgb(255, 40, 43, 49) : Color.FromArgb(255, 65, 76, 90));
        rgb.Background = new SolidColorBrush(rgbMode ? Color.FromArgb(255, 65, 76, 90) : Color.FromArgb(255, 40, 43, 49));
        start.Content = running ? "Pause" : rgbMode ? "Start RGB" : "Capture white";
        if (!rgbMode)
            light.Background = new SolidColorBrush(WhiteColor(settings.Temperature));
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

    private async Task StartAsync()
    {
        if (workflowWindow is not null) return;
        if (captureCancellation is not null || connecting || camera?.Faulted == true || (!rgbMode && camera is null)) return;
        using var cancellation = new CancellationTokenSource();
        captureCancellation = cancellation;
        running = true;
        downloadedFiles = 0;
        camera?.BeginScan();
        Refresh();
        try
        {
            if (!rgbMode && camera is not null)
            {
                status.Text = "White \u00b7 exposing and downloading...";
                CaptureCompleted(LightChannel.White, await camera.CaptureAsync(LightChannel.White, cancellation.Token));
                status.Text = $"White complete \u00b7 {downloadedFiles} files saved";
                return;
            }
            await new ScanSequence(camera: camera).RunAsync(new(settings.Interval, settings.Repeat),
                progress =>
                {
                    status.Text = progress.Phase switch
                    {
                        ScanPhase.Countdown => $"Starting in {Math.Ceiling(progress.Remaining.TotalSeconds):0} s \u00b7 " +
                            (camera is null ? "start your intervalometer" : "USB camera ready"),
                        ScanPhase.Holding => $"{progress.Channel} \u00b7 set {progress.SetNumber} \u00b7 " +
                            (camera is null ? $"{progress.Remaining.TotalSeconds:0.0} s" : $"exposing / downloading \u00b7 {downloadedFiles} files saved"),
                        _ => $"Set {progress.SetNumber} complete \u00b7 blue held" + (camera is null ? "" : $" \u00b7 {downloadedFiles} files saved")
                    };
                },
                (channel, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    ShowChannel(channel);
                    return Task.CompletedTask;
                }, CaptureCompleted, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (camera is not null) status.Text = "Paused \u00b7 camera transaction finished; restart begins a fresh set.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException)
        {
            ShowError($"Scan stopped: {ex.Message}");
            status.Text = "Scan failed. Light held.";
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
        downloadedFiles += result.FilePaths.Count;
        status.Text = $"{channel} downloaded \u00b7 {downloadedFiles} files saved to {settings.OutputPath}";
    }

    private void UpdateCameraActivity(CameraActivity activity)
    {
        if (closed) return;
        cameraActivity = activity;
        workflowWindow?.UpdateCameraActivity(activity);
        cameraStatus.Text = $"CAMERA \u00b7 {(activity.Channel.HasValue ? $"{activity.Channel} \u00b7 " : "")}{activity.Message}";
        cameraIndicator.Background = new SolidColorBrush(activity.Stage switch
        {
            CameraStage.Ready => Colors.LightGreen,
            CameraStage.Disconnected => Colors.Gray,
            CameraStage.Faulted => Colors.OrangeRed,
            _ => Colors.Gold
        });
    }

    private async Task CheckCameraAsync()
    {
        if (workflowWindow is not null || closed || closeRequested || checkingCamera || connecting || running || camera is null || camera.Faulted) return;
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
        if (workflowWindow is not null) return;
        if (running || connecting) return;
        if (camera is not null)
        {
            var confirm = new CheckBox { Content = "I confirm the camera is idle (no exposure or processing)." };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = camera.Faulted
                    ? "Camera connection faulted. Keep the light held until the camera is idle. Resolve any exposure/USB problem before disconnecting; reconnect will preserve queued images in Recovery."
                    : $"{camera.Connection?.Model}\nFirmware: {camera.Connection?.Firmware}\nSDK: {camera.Connection?.SdkVersion}\nPhotos: {settings.OutputPath}"
            });
            content.Children.Add(confirm);
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot, RequestedTheme = ElementTheme.Dark,
                Title = camera.Faulted ? "Re-arm camera" : "USB camera connected",
                Content = content, PrimaryButtonText = "Disconnect", CloseButtonText = "Done",
                IsPrimaryButtonEnabled = false
            };
            confirm.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            confirm.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            connecting = true;
            Refresh();
            try
            {
                try { await camera.DisconnectAsync(); }
                finally { await camera.DisposeAsync(); }
                camera = null;
                UpdateCameraActivity(new(CameraStage.Disconnected, "USB camera disconnected \u00b7 timer mode available"));
                status.Text = "Camera disconnected \u00b7 timer mode available";
            }
            catch (CameraSdkException ex)
            {
                // The operator confirmed idle; the helper is disposed even if SDK teardown failed.
                camera = null;
                UpdateCameraActivity(new(CameraStage.Disconnected, "USB camera disconnected \u00b7 teardown error; see details"));
                ShowError($"Camera disconnected with a teardown error: {ex.Message}");
            }
            finally { connecting = false; Refresh(); }
            return;
        }

        try { await ConnectCameraAsync(this, root.XamlRoot); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError($"Camera connection failed: {ex.Message}");
        }
    }

    private async Task<CameraClient?> ConnectCameraAsync(Window owner, XamlRoot dialogRoot)
    {
        if (camera is not null || connecting || running)
            throw new InvalidOperationException("Release the current camera and wait for pending operations before connecting.");
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Connect the X-T5 by USB in USB TETHER SHOOTING AUTO mode. Set M focus and S drive. Choose shutter speed, aperture, ISO and white balance on the camera; Film Light uses camera-priority triggering and never sets exposure values. Keep settings fixed within each RGB set.\n\nSelect RAW, JPEG or RAW+JPEG on the camera (not HEIF). Use a writable card and close other tether apps. Photos download automatically; existing queued images are preserved in Recovery. Do not press the camera shutter during app captures."
        });
        var sdkPath = new TextBlock { Text = settings.SdkPath, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var outputPath = new TextBlock { Text = settings.OutputPath, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var pickerError = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Orange) };
        var sdkFolder = MakeButton("SDK folder", 160);
        var outputFolder = MakeButton("Save folder", 160);
        sdkFolder.Click += async (_, _) =>
        {
            var path = await PickFolderAsync(owner, pickerError);
            if (path is null) return;
            settings = settings with { SdkDirectory = path };
            sdkPath.Text = path;
            Persist(pickerError);
        };
        outputFolder.Click += async (_, _) =>
        {
            var path = await PickFolderAsync(owner, pickerError);
            if (path is null) return;
            settings = settings with { OutputDirectory = path };
            outputPath.Text = path;
            Persist(pickerError);
        };
        panel.Children.Add(sdkFolder);
        panel.Children.Add(sdkPath);
        panel.Children.Add(outputFolder);
        panel.Children.Add(outputPath);
        panel.Children.Add(pickerError);
        var ready = new CheckBox { Content = "Camera is idle and configured; no other tether app is running." };
        panel.Children.Add(ready);
        var connectDialog = new ContentDialog
        {
            XamlRoot = dialogRoot, RequestedTheme = ElementTheme.Dark, Title = "Connect Fujifilm X-T5",
            Content = new ScrollViewer { Content = panel, MaxHeight = 500 },
            PrimaryButtonText = "Connect USB", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false
        };
        ready.Checked += (_, _) => connectDialog.IsPrimaryButtonEnabled = true;
        ready.Unchecked += (_, _) => connectDialog.IsPrimaryButtonEnabled = false;
        if (await connectDialog.ShowAsync() != ContentDialogResult.Primary) return null;
        connecting = true;
        UpdateCameraActivity(new(CameraStage.Loading, "Starting USB detection"));
        Refresh();
        CameraClient? pending = null;
        try
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "CameraHost", "FilmLight.CameraHost.exe");
            if (!File.Exists(helper)) throw new FileNotFoundException("Camera helper is missing. Build/publish with tools\\Publish.ps1.");
            pending = new(helper, settings.OutputPath, UpdateCameraActivity);
            await pending.ConnectAsync(settings.SdkPath);
            camera = pending;
            pending = null;
            error.Text = "";
            status.Text = $"{camera.Connection?.Model} connected \u00b7 firmware {camera.Connection?.Firmware} \u00b7 captures saved to tablet + card";
            return camera;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (cameraActivity.Stage is not (CameraStage.Disconnected or CameraStage.Faulted))
                UpdateCameraActivity(new(CameraStage.Faulted, "Connection failed \u00b7 see details"));
            status.Text = "Camera not connected \u00b7 timer mode unchanged";
            throw;
        }
        finally
        {
            if (pending is not null) await pending.DisposeAsync();
            connecting = false;
            Refresh();
        }
    }

    private async Task<string?> PickFolderAsync(Window? owner = null, TextBlock? pickerError = null)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner ?? this));
        try { return (await picker.PickSingleFolderAsync())?.Path; }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            var message = $"Could not open folder picker: {ex.Message}";
            if (pickerError is not null) pickerError.Text = message;
            else ShowError(message);
            return null;
        }
    }

    private void Persist(TextBlock? settingsError = null)
    {
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var message = $"Could not save settings: {ex.Message}";
            if (settingsError is not null) settingsError.Text = message;
            else ShowError(message);
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
        if (workflowWindow is not null) return;
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

    private void OpenRollWorkflow()
    {
        if (workflowWindow is not null || running || activeCapture is not null || connecting || checkingCamera
            || closeRequested || camera?.Faulted == true || cameraActivity.Stage == CameraStage.Busy) return;
        cameraMonitor.Stop();
        var child = new RollWorkflowWindow(camera?.Connection is not null ? camera : null, released =>
        {
            if (ReferenceEquals(camera, released))
            {
                camera = null;
                UpdateCameraActivity(new(CameraStage.Disconnected, "Camera released by roll workflow; reconnect in the open roll or using Camera."));
            }
        }, ConnectCameraAsync);
        workflowWindow = child;
        root.IsHitTestVisible = false;
        child.Closed += (_, _) =>
        {
            workflowWindow = null;
            root.IsHitTestVisible = true;
            cameraMonitor.Start();
            Refresh();
            AppWindow.Show();
            Activate();
        };
        ReleaseWake();
        AppWindow.Hide();
        child.Activate();
    }
}
