using FilmLight.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FilmLight.Fujifilm;

public sealed class CameraClient : ICameraCapture, IAsyncDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim requests = new(1);
    private readonly SemaphoreSlim writes = new(1);
    private readonly Task logging;
    private readonly string output;
    private string scanId = Guid.NewGuid().ToString("N");
    private string? loggingError;
    private bool disposed;
    private readonly Action<CameraActivity>? report;
    public CameraActivity Activity { get; private set; } = new(CameraStage.Disconnected, "Not connected");
    public CameraConnection? Connection { get; private set; }
    public bool Faulted { get; private set; }
    public string LogPath { get; }

    public CameraClient(string helperPath, string outputDirectory, Action<CameraActivity>? report = null)
    {
        this.report = report;
        output = Path.GetFullPath(outputDirectory);
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilmLight", "Logs");
        Directory.CreateDirectory(logs);
        LogPath = Path.Combine(logs, $"Camera-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        process = new()
        {
            StartInfo = new(helperPath)
            {
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(helperPath))!,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        if (!process.Start()) throw new IOException("Could not start the x64 camera helper.");
        logging = LogErrorsAsync();
    }

    public async Task ConnectAsync(string sdkDirectory)
    {
        var response = await SendAsync(new(Guid.NewGuid().ToString("N"), "connect",
            sdkDirectory, output), CancellationToken.None);
        Connection = response.Connection ?? throw new InvalidDataException("Camera helper returned no connection information.");
        Report(new(CameraStage.Ready, $"{Connection.Model} ready \u00b7 exposure controlled on camera"));
    }

    public void BeginScan() => scanId = Guid.NewGuid().ToString("N");

    public void BeginScan(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid scan identifier.", nameof(id));
        scanId = id;
    }

    public async Task CheckConnectionAsync()
    {
        if (Connection is null || Faulted) throw new CameraSdkException("Camera is not connected.");
        Report(new(CameraStage.Checking, $"{Connection.Model} \u00b7 checking USB connection"));
        var response = await SendAsync(new(Guid.NewGuid().ToString("N"), "status"), CancellationToken.None);
        Report(new(response.Busy ? CameraStage.Busy : CameraStage.Ready,
            response.Busy ? $"{Connection.Model} busy \u00b7 wait for camera" : $"{Connection.Model} ready \u00b7 exposure controlled on camera"));
    }

    public async Task<CaptureResult> CaptureAsync(LightChannel channel, CancellationToken cancellationToken)
    {
        if (Connection is null || Faulted)
            throw new CameraSdkException("Camera is not ready. Disconnect/re-arm before scanning.");
        var response = await SendAsync(new(Guid.NewGuid().ToString("N"), "capture",
            Channel: channel, ScanId: scanId), cancellationToken);
        if (response.FilePaths is not { } files)
        {
            Faulted = true;
            throw new InvalidDataException("Camera returned no downloaded files.");
        }
        var expected = Connection.ImageQuality is 4 or 5 or 7 ? 2 : 1;
        if (files.Length != expected || files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected)
        {
            Faulted = true;
            throw new InvalidDataException("Camera returned an incomplete image set.");
        }
        foreach (var file in files)
        {
            if (!Path.GetFullPath(file).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(file) || new FileInfo(file).Length == 0)
            {
                Faulted = true;
                throw new InvalidDataException("Downloaded camera file is missing or outside the capture folder.");
            }
        }
        return new(files);
    }

    public async Task DisconnectAsync()
    {
        if (!Faulted && !process.HasExited)
            await SendAsync(new(Guid.NewGuid().ToString("N"), "disconnect"), CancellationToken.None);
        Connection = null;
        Report(new(CameraStage.Disconnected, "USB camera disconnected"));
    }

    private async Task<CameraResponse> SendAsync(CameraRequest request, CancellationToken cancellationToken)
    {
        await requests.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Faulted && request.Command is "capture" or "status")
                throw new CameraSdkException("Camera connection faulted before this operation; re-arm when idle.");
            await WriteAsync(request);
            var responseTask = ReadResponseAsync(request.Id);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => canceled.TrySetResult());
            var watchdog = Task.Delay(TimeSpan.FromMinutes(3));
            var first = await Task.WhenAny(responseTask, canceled.Task, watchdog);
            if (first == canceled.Task)
            {
                await WriteAsync(new(request.Id, "cancel"));
                // Wait without the canceled token: the camera must finish exposing/downloading.
                await Task.WhenAny(responseTask, watchdog);
            }
            if (!responseTask.IsCompleted)
                throw new TimeoutException("Camera helper is unresponsive (3 minutes). Confirm the camera is idle before re-arming.");
            var response = await responseTask;
            Faulted |= response.Faulted;
            if (response.Error is not null) throw new CameraSdkException(response.Error);
            if (loggingError is not null) throw new IOException(loggingError);
            if (response.Canceled)
            {
                if (!cancellationToken.IsCancellationRequested)
                    throw new IOException("Camera capture was unexpectedly canceled.");
                if (!response.Faulted) Report(new(CameraStage.Ready, "Paused \u00b7 camera transaction finished"));
                throw new OperationCanceledException(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or JsonException or InvalidOperationException)
        {
            Faulted = true;
            Report(request.Command == "connect" && Activity.Stage == CameraStage.Disconnected
                ? new(CameraStage.Disconnected, "No USB camera detected \u00b7 check cable and tether mode")
                : new(CameraStage.Faulted, "Camera error \u00b7 see details and re-arm when idle"));
            if (ex is TimeoutException && !process.HasExited) process.Kill();
            throw new CameraSdkException($"{ex.Message} Camera connection faulted; confirm it is idle before disconnecting. Log: {LogPath}");
        }
        finally { requests.Release(); }
    }

    private async Task<CameraResponse> ReadResponseAsync(string requestId)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync();
            if (line is null) throw new IOException($"Camera helper exited. See {LogPath}.");
            var response = JsonSerializer.Deserialize<CameraResponse>(line)
                ?? throw new InvalidDataException("Camera returned an empty response.");
            if (response.Id != requestId)
                throw new InvalidDataException("Camera response identifier does not match its request.");
            if (response.Activity is null) return response;
            if (response.Error is not null || response.FilePaths is not null || response.Connection is not null
                || response.Canceled || response.Faulted || !Enum.IsDefined(response.Activity.Stage))
                throw new InvalidDataException("Invalid camera progress message.");
            Report(response.Activity);
        }
    }

    private void Report(CameraActivity activity)
    {
        Activity = activity;
        report?.Invoke(activity);
    }

    private async Task WriteAsync(CameraRequest request)
    {
        await writes.WaitAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            await process.StandardInput.FlushAsync();
        }
        finally { writes.Release(); }
    }

    private async Task LogErrorsAsync()
    {
        try
        {
            await using var file = new StreamWriter(LogPath, false, Encoding.UTF8) { AutoFlush = true };
            while (await process.StandardError.ReadLineAsync() is { } line)
                await file.WriteLineAsync(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            loggingError = $"Camera log could not be written: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await requests.WaitAsync();
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException)
                {
                    process.Kill();
                    await process.WaitForExitAsync();
                }
            }
            await logging;
            process.Dispose();
            disposed = true;
        }
        finally { requests.Release(); }
    }
}
