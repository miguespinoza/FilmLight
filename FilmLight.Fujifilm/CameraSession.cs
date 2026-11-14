using FilmLight.Core;
using System.Text.Json;

namespace FilmLight.Fujifilm;

public sealed class CameraSession(INativeCamera camera, string outputDirectory,
    TimeProvider? timeProvider = null, Action<TimeSpan>? wait = null, Action<CameraActivity>? report = null) : IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Action<TimeSpan> delay = wait ?? Thread.Sleep;
    private readonly string output = Path.GetFullPath(outputDirectory);
    private readonly HashSet<uint> completedShots = [];
    private CameraConnection? connection;
    public bool Faulted { get; private set; }

    public CameraConnection Connect()
    {
        Directory.CreateDirectory(output);
        connection = camera.Connect();
        PreservePending();
        report?.Invoke(new(CameraStage.Ready, $"{connection.Model} ready \u00b7 exposure controlled on camera"));
        return connection;
    }

    public bool CheckConnection()
    {
        if (connection is null || Faulted) throw new CameraSdkException("Camera is not connected.");
        try { camera.CheckConnection(); return true; }
        catch (CameraSdkException ex) when (ex.IsBusy) { return false; }
        catch (CameraSdkException) { Faulted = true; throw; }
    }

    public CaptureResult Capture(LightChannel channel, string scanId, CancellationToken cancellationToken)
    {
        if (connection is null || Faulted)
            throw new InvalidOperationException("Camera must be reconnected before capturing.");
        if (!Enum.IsDefined(channel) || !Guid.TryParseExact(scanId, "N", out _))
            throw new ArgumentException("Invalid channel or scan identifier.");
        var staged = Path.Combine(output, ".pending", Guid.NewGuid().ToString("N"));
        var triggerAttempted = false;
        var halfHeld = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            report?.Invoke(new(CameraStage.Checking, "Checking camera and image queue", channel));
            var preflight = clock.GetTimestamp();
            ValidateUntilReady(preflight, channel, cancellationToken);
            if (ReadInfoUntilReady(preflight, cancellationToken).Format != 5)
                throw new CameraSdkException("Unexpected queued image. Reconnect to preserve it in Recovery before capturing.");
            Directory.CreateDirectory(staged);
            WriteDurably(Path.Combine(staged, "transaction.json"), JsonSerializer.SerializeToUtf8Bytes(
                new { channel, scanId, connection, started = clock.GetUtcNow() }));
            report?.Invoke(new(CameraStage.Settling, "Settling light", channel));
            delay(TimeSpan.FromMilliseconds(250));
            cancellationToken.ThrowIfCancellationRequested();
            WaitForIdle(preflight, channel, false, cancellationToken);
            halfHeld = true;
            report?.Invoke(new(CameraStage.Triggering, "Triggering shutter \u00b7 camera exposure settings", channel));
            camera.HalfPress();
            cancellationToken.ThrowIfCancellationRequested();
            triggerAttempted = true;
            camera.FullPress();
            halfHeld = false;

            // After shutter acceptance, cancellation waits for the complete download. Never
            // relinquish the light while an exposure or RAW/JPEG partner may still be pending.
            var expected = connection.ImageQuality == 1 ? new HashSet<int> { 1 }
                : connection.ImageQuality is 4 or 5 or 7 ? [1, 7] : [7];
            uint? shot = null;
            var formats = new HashSet<int>();
            var files = new List<string>();
            var started = preflight;
            var waitingReported = false;
            while (formats.Count < expected.Count)
            {
                EnsureDeadline(started);
                if (!waitingReported)
                {
                    report?.Invoke(new(CameraStage.WaitingForImage,
                        $"Waiting for exposure / processing \u00b7 {formats.Count}/{expected.Count} files saved", channel));
                    waitingReported = true;
                }
                var info = ReadInfoUntilReady(started);
                if (info.Format == 5)
                {
                    delay(TimeSpan.FromMilliseconds(100));
                    continue;
                }
                report?.Invoke(new(CameraStage.Downloading,
                    $"Downloading {(info.Format == 1 ? "RAW" : info.Format == 7 ? "JPEG" : "image")} \u00b7 {formats.Count + 1}/{expected.Count}", channel));
                var bytes = camera.ReadImage(info);
                if (bytes.Length != info.Size)
                    throw new InvalidDataException("Camera returned an incomplete image.");
                var extension = Extension(info.Format);
                var temporaryName = $"{channel}-{formats.Count}{extension}";
                report?.Invoke(new(CameraStage.Saving, $"Saving {temporaryName}", channel));
                WriteDurably(Path.Combine(staged, temporaryName), bytes);
                if (!expected.Contains(info.Format) || !formats.Add(info.Format))
                    throw new InvalidDataException("Unexpected or duplicate image format; files retained in .pending.");
                var identity = ShotIdentity.Read(bytes, info.Format);
                if ((shot.HasValue && shot != identity) || completedShots.Contains(identity))
                    throw new InvalidDataException("RAW/JPEG shot identities do not match or an old shot was received.");
                shot = identity;
                var fileName = $"{channel}-{identity:D8}{extension}";
                File.Move(Path.Combine(staged, temporaryName), Path.Combine(staged, fileName));
                files.Add(fileName);
                waitingReported = false;
            }
            WaitForIdle(started, channel, true);
            completedShots.Add(shot!.Value);
            var canceled = cancellationToken.IsCancellationRequested;
            report?.Invoke(new(CameraStage.Saving, canceled ? "Preserving canceled capture in Recovery" : "Committing complete image set", channel));
            WriteDurably(Path.Combine(staged, "complete.json"), JsonSerializer.SerializeToUtf8Bytes(
                new { channel, scanId, shot, files, canceled, finished = clock.GetUtcNow() }));
            var parent = canceled ? Path.Combine(output, "Recovery") : Path.Combine(output, $"Scan-{scanId}");
            Directory.CreateDirectory(parent);
            var final = Path.Combine(parent, $"{channel}-{shot:D8}-{Guid.NewGuid():N}");
            Directory.Move(staged, final);
            cancellationToken.ThrowIfCancellationRequested();
            report?.Invoke(new(CameraStage.Ready, $"{connection.Model} ready \u00b7 {files.Count} files saved", channel));
            return new(files.Select(file => Path.Combine(final, file)).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or TimeoutException)
        {
            Faulted = true;
            report?.Invoke(new(CameraStage.Faulted, "Camera fault \u00b7 keep light held and re-arm when idle", channel));
            throw new CameraSdkException($"{ex.Message} Light must remain held until the camera is idle. " +
                $"Files, if any, are retained under {output}. Trigger attempted: {triggerAttempted}.",
                ex is CameraSdkException sdkError ? sdkError.ErrorCode : 0);
        }
        finally
        {
            if (halfHeld)
            {
                try { camera.ReleaseHalfPress(); }
                catch (CameraSdkException) { Faulted = true; throw; }
            }
        }
    }

    private void PreservePending()
    {
        var started = clock.GetTimestamp();
        for (var count = 0; ; count++)
        {
            EnsureDeadline(started);
            var info = ReadInfoUntilReady(started);
            if (info.Format == 5) return;
            if (count >= 64) throw new CameraSdkException("Too many pending images; stop and recover the camera queue.");
            report?.Invoke(new(CameraStage.Recovering, $"Preserving preexisting image {count + 1} in Recovery"));
            var bytes = camera.ReadImage(info);
            var folder = Path.Combine(output, "Recovery");
            Directory.CreateDirectory(folder);
            WriteDurably(Path.Combine(folder, $"Preexisting-{Guid.NewGuid():N}{Extension(info.Format)}"), bytes);
        }
    }

    private ImageInfo ReadInfoUntilReady(long started, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDeadline(started);
            try { return camera.ReadImageInfo(); }
            catch (CameraSdkException ex) when (ex.IsBusy) { delay(TimeSpan.FromMilliseconds(100)); }
        }
    }

    private void ValidateUntilReady(long started, LightChannel channel, CancellationToken cancellationToken)
    {
        var busyReported = false;
        string? lastBusy = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDeadline(started, lastBusy);
            try { camera.ValidateConfiguration(); return; }
            catch (CameraSdkException ex) when (ex.IsBusy)
            {
                lastBusy = ex.Message;
                if (!busyReported)
                {
                    report?.Invoke(new(CameraStage.Busy, $"Waiting for camera before triggering \u00b7 {ex.Message}", channel));
                    busyReported = true;
                }
                delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }

    private void WaitForIdle(long started, LightChannel channel, bool triggered, CancellationToken cancellationToken = default)
    {
        long? idleSince = null;
        string? lastBusy = null;
        var busyReported = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDeadline(started, lastBusy);
            try
            {
                camera.ValidateConfiguration();
                if (camera.ReadImageInfo().Format != 5)
                    throw new InvalidDataException("Unexpected queued image. Reconnect to preserve it before scanning.");
                idleSince ??= clock.GetTimestamp();
                if (clock.GetElapsedTime(idleSince.Value) >= TimeSpan.FromMilliseconds(500)) return;
            }
            catch (CameraSdkException ex) when (ex.IsBusy)
            {
                idleSince = null;
                lastBusy = ex.Message;
                if (!busyReported)
                {
                    report?.Invoke(new(triggered ? CameraStage.WaitingForImage : CameraStage.Busy,
                        $"Waiting for camera to become idle \u00b7 light held \u00b7 {ex.Message}", channel));
                    busyReported = true;
                }
            }
            delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private void EnsureDeadline(long started, string? lastBusy = null)
    {
        if (clock.GetElapsedTime(started) >= TimeSpan.FromMinutes(2))
            throw new TimeoutException("Camera capture/readiness/transfer deadline exceeded (2 minutes). Do not reshoot until idle." +
                (lastBusy is null ? "" : $" Last camera status: {lastBusy}"));
    }

    private static string Extension(int format) => format switch
    {
        1 => ".RAF", 7 => ".JPG", 0x12 => ".HIF", 4 => ".live.JPG", _ => ".bin"
    };

    private static void WriteDurably(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(true);
    }

    public void Dispose() => camera.Dispose();
}
