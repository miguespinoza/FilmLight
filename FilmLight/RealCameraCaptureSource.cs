using FilmLight.Core;
using FilmLight.Fujifilm;
using FilmLight.Workflow;

namespace FilmLight;

/// <summary>Uses the camera's own exposure settings; ownership is leased by RollWorkflowWindow.</summary>
public sealed class RealCameraCaptureSource(CameraClient camera) : IWorkflowCaptureSource
{
    private int capturing;
    public bool IsDemo => false;
    public string Description => $"REAL CAMERA - {camera.Connection?.Model ?? "disconnected"}; exposure set on camera";

    public async Task<IReadOnlyList<CaptureFile>> CaptureAsync(CapturePurpose purpose, CaptureMode mode,
        int frameNumber, string outputDirectory, Func<LightChannel, CancellationToken, Task> showLight,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(purpose) || !Enum.IsDefined(mode) || frameNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(mode), "Capture purpose, mode and frame number must be valid.");
        if (Interlocked.Exchange(ref capturing, 1) != 0)
            throw new InvalidOperationException("A camera transaction is already pending.");
        try
        {
            if (camera.Connection is null || camera.Faulted)
                throw new InvalidOperationException("Camera is not healthy. Confirm idle and release the camera before reconnecting.");
            camera.BeginScan();
            LightChannel[] channels = mode == CaptureMode.White
                ? [LightChannel.White] : [LightChannel.Red, LightChannel.Green, LightChannel.Blue];
            var files = new List<CaptureFile>();
            foreach (var channel in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await showLight(channel, cancellationToken);
                // CameraClient drains an already-triggered transaction before cancellation completes.
                var result = await camera.CaptureAsync(channel, cancellationToken);
                files.AddRange(result.FilePaths.Select(path => new CaptureFile(channel, path)));
                cancellationToken.ThrowIfCancellationRequested();
            }
            return files;
        }
        finally { Volatile.Write(ref capturing, 0); }
    }
}
