using FilmLight.Core;

namespace FilmLight.Fujifilm;

public sealed record CameraConnection(string Model, string Serial, string Firmware, string SdkVersion, int ImageQuality);
public sealed record ImageInfo(int Format, int Size);
public enum CameraStage { Loading, Searching, Detected, Configuring, Recovering, Ready, Busy, Checking, Settling, Triggering, WaitingForImage, Downloading, Saving, Disconnecting, Disconnected, Faulted }
public sealed record CameraActivity(CameraStage Stage, string Message, LightChannel? Channel = null);

public interface INativeCamera : IDisposable
{
    CameraConnection Connect();
    void ValidateConfiguration();
    void CheckConnection();
    ImageInfo ReadImageInfo();
    byte[] ReadImage(ImageInfo info);
    void HalfPress();
    void ReleaseHalfPress();
    void FullPress();
}

public sealed class CameraSdkException(string message, int errorCode = 0) : IOException(message)
{
    public int ErrorCode { get; } = errorCode;
    public bool IsBusy => ErrorCode is 0x1006 or 0x1016 or 0x1017;
}

public sealed record CameraRequest(string Id, string Command, string? SdkDirectory = null,
    string? OutputDirectory = null, LightChannel Channel = LightChannel.Red, string? ScanId = null);
public sealed record CameraResponse(string Id, CameraConnection? Connection = null,
    string[]? FilePaths = null, string? Error = null, bool Canceled = false, bool Faulted = false,
    CameraActivity? Activity = null, bool Busy = false);
