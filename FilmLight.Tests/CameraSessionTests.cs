using FilmLight.Core;
using FilmLight.Fujifilm;
using Microsoft.Extensions.Time.Testing;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Xunit;

namespace FilmLight.Tests;

public sealed class CameraSessionTests : IDisposable
{
    private readonly string output = Path.Combine(Path.GetTempPath(), $"FilmLight-test-{Guid.NewGuid():N}");

    [Fact]
    public void DownloadsBothPartnersDespiteEmptyObservationBetweenThem()
    {
        var native = new FakeCamera();
        native.Shot = [(1, Image(42, 1)), (5, []), (7, Image(42, 7))];
        using var session = Create(native);
        session.Connect();
        var result = session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.Equal(2, result.FilePaths.Count);
        Assert.All(result.FilePaths, file => Assert.True(File.Exists(file)));
        Assert.Equal("Red-00000042", Path.GetFileNameWithoutExtension(result.FilePaths[0]));
        Assert.Equal("Red-00000042", Path.GetFileNameWithoutExtension(result.FilePaths[1]));
        Assert.Equal(2, native.Downloads);
        Assert.False(session.Faulted);
    }

    [Fact]
    public void ReportsRealCaptureStagesInOrderWithoutCallingDownloadCompleteEarly()
    {
        var native = new FakeCamera();
        var progress = new List<CameraActivity>();
        var clock = new FakeTimeProvider();
        using var session = new CameraSession(native, output, clock, clock.Advance, progress.Add);
        session.Connect();
        progress.Clear();
        session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.Equal(new[]
        {
            CameraStage.Checking, CameraStage.Settling, CameraStage.Triggering,
            CameraStage.WaitingForImage, CameraStage.Downloading, CameraStage.Saving,
            CameraStage.WaitingForImage, CameraStage.Downloading, CameraStage.Saving,
            CameraStage.Saving, CameraStage.Ready
        }, progress.Select(activity => activity.Stage));
        Assert.All(progress, activity => Assert.Equal(LightChannel.Red, activity.Channel));
        Assert.Equal(2, native.Downloads);
    }

    [Fact]
    public void IdleMonitoringDoesNotTriggerAndDistinguishesBusyFromDisconnected()
    {
        var native = new FakeCamera();
        using var session = Create(native);
        session.Connect();
        Assert.True(session.CheckConnection());
        native.ConnectionError = new CameraSdkException("Busy", 0x1006);
        Assert.False(session.CheckConnection());
        Assert.False(session.Faulted);
        native.ConnectionError = new CameraSdkException("Disconnected", 0x2001);
        Assert.Throws<CameraSdkException>(() => session.CheckConnection());
        Assert.True(session.Faulted);
        Assert.Equal(0, native.Triggers);
        Assert.Equal(0, native.Downloads);
    }

    [Theory]
    [InlineData(1, 1, ".RAF")]
    [InlineData(2, 7, ".JPG")]
    public void SingleFormatCaptureWaitsForAndPublishesExactlyOneFile(int quality, int format, string extension)
    {
        var native = new FakeCamera { Quality = quality, Shot = [(format, Image(20, format))] };
        using var session = Create(native);
        session.Connect();
        var result = session.Capture(LightChannel.White, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.Single(result.FilePaths);
        Assert.Equal(extension, Path.GetExtension(result.FilePaths[0]));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(result.FilePaths[0])!, "complete.json")));
    }

    [Fact]
    public void CancellationAfterShutterFinishesDownloadsIntoRecoveryBeforeReturning()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new FakeCamera { OnFullPress = cancellation.Cancel };
        using var session = Create(native);
        session.Connect();
        Assert.Throws<OperationCanceledException>(() =>
            session.Capture(LightChannel.Blue, Guid.NewGuid().ToString("N"), cancellation.Token));
        Assert.Equal(2, native.Downloads);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(output, "Recovery"), "*.*", SearchOption.AllDirectories)
            .Count(file => Path.GetExtension(file) is ".RAF" or ".JPG"));
        Assert.Empty(Directory.GetDirectories(output, "Scan-*"));
        Assert.False(session.Faulted);
    }

    [Fact]
    public void CancellationBetweenPressesReleasesHalfPressWithoutExposing()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new FakeCamera { OnHalfPress = cancellation.Cancel };
        using var session = Create(native);
        session.Connect();
        Assert.Throws<OperationCanceledException>(() =>
            session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), cancellation.Token));
        Assert.Equal(0, native.Triggers);
        Assert.Equal(1, native.HalfReleases);
        Assert.False(session.Faulted);
    }

    [Fact]
    public void MismatchedPairFaultsAndPreservesFilesWithoutPublishingSuccess()
    {
        var native = new FakeCamera { Shot = [(1, Image(1, 1)), (7, Image(2, 7))] };
        using var session = Create(native);
        session.Connect();
        Assert.Throws<CameraSdkException>(() =>
            session.Capture(LightChannel.Green, Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.True(session.Faulted);
        Assert.Empty(Directory.GetDirectories(output, "Scan-*"));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(output, ".pending"), "*.*", SearchOption.AllDirectories)
            .Count(file => Path.GetExtension(file) is ".RAF" or ".JPG"));
        Assert.Throws<InvalidOperationException>(() =>
            session.Capture(LightChannel.Blue, Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.Equal(1, native.Triggers);
    }

    [Fact]
    public void PreexistingImagesArePreservedSeparatelyBeforeNewCapture()
    {
        var native = new FakeCamera();
        native.Queue.Enqueue((1, Image(99, 1)));
        using var session = Create(native);
        session.Connect();
        Assert.Single(Directory.GetFiles(Path.Combine(output, "Recovery")));
        var result = session.Capture(LightChannel.White, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.Equal(2, result.FilePaths.Count);
        Assert.All(result.FilePaths, file => Assert.Contains("White-00000001", file));
    }

    [Fact]
    public void MissingPartnerTimesOutInsteadOfReturningPartialPaths()
    {
        var native = new FakeCamera { Shot = [(1, Image(1, 1))] };
        using var session = Create(native);
        session.Connect();
        Assert.Throws<CameraSdkException>(() =>
            session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.True(session.Faulted);
        Assert.Empty(Directory.GetDirectories(output, "Scan-*"));
    }

    [Fact]
    public void DuplicateShotIdentityCannotBeAssignedToAnotherChannel()
    {
        var native = new FakeCamera();
        using var session = Create(native);
        session.Connect();
        var scanId = Guid.NewGuid().ToString("N");
        session.Capture(LightChannel.Red, scanId, CancellationToken.None);
        Assert.Throws<CameraSdkException>(() => session.Capture(LightChannel.Green, scanId, CancellationToken.None));
        Assert.True(session.Faulted);
    }

    [Fact]
    public void BusyImageInfoIsRetriedWithoutRetriggering()
    {
        var native = new FakeCamera { BusyReads = 2 };
        using var session = Create(native);
        session.Connect();
        session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.Equal(1, native.Triggers);
    }

    [Fact]
    public void CaptureWaitsForShutterToBecomeIdleAfterLastDownloadBeforeNextChannel()
    {
        var native = new FakeCamera();
        var clock = new FakeTimeProvider();
        using var session = new CameraSession(native, output, clock, clock.Advance);
        session.Connect();
        native.OnDownload = () => native.ReleaseStatus = 0x200;
        var waits = 0;
        native.OnValidate = () =>
        {
            if (native.Downloads == 2 && ++waits >= 3) native.ReleaseStatus = 0;
        };
        session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.True(waits >= 3);
        Assert.False(session.Faulted);
        Assert.Equal(1, native.Triggers);

        native.OnDownload = null;
        native.Shot = [(1, Image(2, 1)), (7, Image(2, 7))];
        session.Capture(LightChannel.Green, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.Equal(2, native.Triggers);
        Assert.Equal(4, native.Downloads);
    }

    [Fact]
    public void IdleWindowRestartsWhenShutterBecomesBusyAgainAfterDownloads()
    {
        var native = new FakeCamera();
        var clock = new FakeTimeProvider();
        using var session = new CameraSession(native, output, clock, clock.Advance);
        session.Connect();
        DateTimeOffset? lastBusy = null;
        var checks = 0;
        native.OnValidate = () =>
        {
            if (native.Downloads != 2) return;
            checks++;
            native.ReleaseStatus = checks is 2 or 4 ? 0x200 : 0;
            if (native.ReleaseStatus != 0) lastBusy = clock.GetUtcNow();
        };
        session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.True(checks > 4, "Capture completed before observing the camera's delayed busy status.");
        Assert.True(clock.GetUtcNow() - lastBusy >= TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, native.Triggers);
    }

    [Fact]
    public void CancellationDuringPreflightBusyWaitNeverTriggersOrFaults()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new FakeCamera();
        using var session = Create(native);
        session.Connect();
        native.OnValidate = () =>
        {
            cancellation.Cancel();
            throw new CameraSdkException("Busy", 0x1006);
        };
        Assert.Throws<OperationCanceledException>(() =>
            session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), cancellation.Token));
        Assert.Equal(0, native.Triggers);
        Assert.False(session.Faulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistentActiveShutterTimesOutWithoutRetriggeringOrPublishing(bool afterTrigger)
    {
        var native = new FakeCamera();
        var clock = new FakeTimeProvider();
        using var session = new CameraSession(native, output, clock, clock.Advance);
        session.Connect();
        var started = clock.GetUtcNow();
        if (afterTrigger) native.OnDownload = () => native.ReleaseStatus = 0x200;
        else native.ReleaseStatus = 0x200;
        var error = Assert.Throws<CameraSdkException>(() =>
            session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.Contains("deadline", error.Message);
        Assert.InRange(clock.GetUtcNow() - started,
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        Assert.Equal(afterTrigger ? 1 : 0, native.Triggers);
        Assert.True(session.Faulted);
        Assert.Empty(Directory.GetDirectories(output, "Scan-*"));
    }

    [Theory]
    [InlineData(0x1006)]
    [InlineData(0x1016)]
    [InlineData(0x1017)]
    public void BusyFullPressIsNeverRetriedBecauseExposureMayHaveStarted(int errorCode)
    {
        var native = new FakeCamera { OnFullPress = () => throw new CameraSdkException("FullPress failed", errorCode) };
        using var session = Create(native);
        session.Connect();
        Assert.Throws<CameraSdkException>(() =>
            session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.Equal(1, native.Triggers);
        Assert.True(session.Faulted);
        Assert.Empty(Directory.GetDirectories(output, "Scan-*"));
    }

    [Fact]
    public void CancellationAfterDownloadsStillWaitsForIdleAndPreservesCapture()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new FakeCamera();
        var clock = new FakeTimeProvider();
        using var session = new CameraSession(native, output, clock, clock.Advance);
        session.Connect();
        native.OnDownload = () =>
        {
            if (native.Downloads == 2)
            {
                native.ReleaseStatus = 0x200;
                cancellation.Cancel();
            }
        };
        var checks = 0;
        native.OnValidate = () =>
        {
            if (native.Downloads == 2 && ++checks >= 3) native.ReleaseStatus = 0;
        };
        Assert.Throws<OperationCanceledException>(() =>
            session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), cancellation.Token));
        Assert.True(checks >= 3);
        Assert.Equal(2, native.Downloads);
        Assert.Equal(1, native.Triggers);
        Assert.False(session.Faulted);
        Assert.Empty(Directory.GetDirectories(output, "Scan-*"));
        Assert.Single(Directory.GetFiles(Path.Combine(output, "Recovery"), "complete.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void IdleMonitoringReportsActiveShutterAsBusyWithoutFaulting()
    {
        var native = new FakeCamera();
        using var session = Create(native);
        session.Connect();
        native.ReleaseStatus = 0x200;
        Assert.False(session.CheckConnection());
        Assert.False(session.Faulted);
        native.ReleaseStatus = 0;
        Assert.True(session.CheckConnection());
        Assert.Equal(0, native.Triggers);
    }

    [Theory]
    [InlineData(0x100)]
    [InlineData(0x200)]
    [InlineData(0x400)]
    public void TransientActiveShutterBeforeTriggerWaitsForStableIdle(int status)
    {
        var clock = new FakeTimeProvider();
        DateTimeOffset? idleAt = null;
        var native = new FakeCamera
        {
            ReleaseStatus = status,
            OnHalfPress = () => Assert.True(clock.GetUtcNow() - idleAt >= TimeSpan.FromMilliseconds(500))
        };
        var progress = new List<CameraActivity>();
        using var session = new CameraSession(native, output, clock, clock.Advance, progress.Add);
        session.Connect();
        var checks = 0;
        native.OnValidate = () =>
        {
            if (++checks == 3)
            {
                native.ReleaseStatus = 0;
                idleAt = clock.GetUtcNow();
            }
        };
        session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.Equal(1, native.Triggers);
        Assert.Equal(2, native.Downloads);
        Assert.False(session.Faulted);
        Assert.Contains(progress, activity => activity.Stage == CameraStage.Busy);
    }

    [Fact]
    public void BusyQueueReadRestartsIdleWindowBeforeReturningCapture()
    {
        var native = new FakeCamera();
        var clock = new FakeTimeProvider();
        using var session = new CameraSession(native, output, clock, clock.Advance);
        session.Connect();
        DateTimeOffset? busyAt = null;
        var checks = 0;
        native.OnValidate = () =>
        {
            if (native.Downloads == 2 && ++checks == 3)
            {
                native.BusyReads = 1;
                busyAt = clock.GetUtcNow();
            }
        };
        session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.NotNull(busyAt);
        Assert.True(clock.GetUtcNow() - busyAt >= TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, native.Triggers);
    }

    [Fact]
    public void LateUnsolicitedImageDuringIdleWindowFaultsWithoutPublishing()
    {
        var native = new FakeCamera();
        using var session = Create(native);
        session.Connect();
        var checks = 0;
        native.OnValidate = () =>
        {
            if (native.Downloads == 2 && ++checks == 3) native.Queue.Enqueue((7, Image(2, 7)));
        };
        Assert.Throws<CameraSdkException>(() =>
            session.Capture(LightChannel.Red, Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.True(session.Faulted);
        Assert.Equal(1, native.Triggers);
        Assert.Equal(2, native.Downloads);
        Assert.Empty(Directory.GetDirectories(output, "Scan-*"));
        Assert.Single(native.Queue);
    }

    [Fact]
    public void NativeImageAndDeviceLayoutsMatchPackedSdkAbi()
    {
        Assert.Equal(IntPtr.Size == 8 ? 64 : 60, Marshal.SizeOf<NativeCamera.NativeImageInfo>());
        Assert.Equal(56, Marshal.OffsetOf<NativeCamera.NativeImageInfo>(nameof(NativeCamera.NativeImageInfo.Image)).ToInt32());
        Assert.Equal(1857, Marshal.SizeOf<NativeCamera.NativeDeviceInfo>());
        Assert.Equal(1792, Marshal.OffsetOf<NativeCamera.NativeDeviceInfo>(nameof(NativeCamera.NativeDeviceInfo.DeviceId)).ToInt32());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void ReadsMakerNoteIdentityAndRejectsMalformedOffsets(int format)
    {
        var image = Image(123456, format);
        Assert.Equal(123456u, ShotIdentity.Read(image, format));
        var tiffStart = format == 1 ? 104 : 12;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tiffStart + 4, 4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => ShotIdentity.Read(image, format));
    }

    private CameraSession Create(FakeCamera camera)
    {
        var clock = new FakeTimeProvider();
        return new(camera, output, clock, clock.Advance);
    }

    private static byte[] Image(uint shot, int format)
    {
        var tiff = new byte[74];
        "II"u8.CopyTo(tiff);
        U16(tiff, 2, 42);
        U32(tiff, 4, 8);
        U16(tiff, 8, 1);
        Tag(tiff, 10, 0x8769, 4, 1, 26);
        U16(tiff, 26, 1);
        Tag(tiff, 28, 0x927C, 7, 30, 44);
        "FUJIFILM"u8.CopyTo(tiff.AsSpan(44));
        U32(tiff, 52, 12);
        U16(tiff, 56, 1);
        Tag(tiff, 58, 0x1438, 4, 1, shot);
        var jpeg = new byte[88];
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 }.CopyTo(jpeg, 0);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(4, 2), 82);
        "Exif\0\0"u8.CopyTo(jpeg.AsSpan(6));
        tiff.CopyTo(jpeg, 12);
        jpeg[^2] = 0xFF;
        jpeg[^1] = 0xD9;
        if (format == 7) return jpeg;
        var raf = new byte[92 + jpeg.Length];
        "FUJIFILMCCD-RAW"u8.CopyTo(raf);
        BinaryPrimitives.WriteUInt32BigEndian(raf.AsSpan(84, 4), 92);
        BinaryPrimitives.WriteUInt32BigEndian(raf.AsSpan(88, 4), (uint)jpeg.Length);
        jpeg.CopyTo(raf, 92);
        return raf;
    }

    private static void Tag(byte[] data, int offset, ushort tag, ushort type, uint count, uint value)
    {
        U16(data, offset, tag); U16(data, offset + 2, type);
        U32(data, offset + 4, count); U32(data, offset + 8, value);
    }
    private static void U16(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
    private static void U32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    public void Dispose()
    {
        if (Directory.Exists(output)) Directory.Delete(output, true);
    }

    private sealed class FakeCamera : INativeCamera
    {
        public Queue<(int Format, byte[] Bytes)> Queue { get; } = new();
        public (int Format, byte[] Bytes)[] Shot { get; set; } = [(1, Image(1, 1)), (7, Image(1, 7))];
        public Action? OnFullPress { get; init; }
        public Action? OnHalfPress { get; init; }
        public int Downloads { get; private set; }
        public int Triggers { get; private set; }
        public int HalfReleases { get; private set; }
        public int BusyReads { get; set; }
        public int Quality { get; init; } = 4;
        public int ReleaseStatus { get; set; }
        public Action? OnValidate { get; set; }
        public Action? OnDownload { get; set; }
        public CameraConnection Connect() => new("X-T5", "test", "test", "test", Quality);
        public void ValidateConfiguration()
        {
            OnValidate?.Invoke();
            NativeCamera.ValidateReleaseStatus(ReleaseStatus);
        }
        public CameraSdkException? ConnectionError { get; set; }
        public void CheckConnection()
        {
            if (ConnectionError is not null) throw ConnectionError;
            NativeCamera.ValidateReleaseStatus(ReleaseStatus);
        }
        public ImageInfo ReadImageInfo()
        {
            if (BusyReads-- > 0) throw new CameraSdkException("Busy", 0x1006);
            if (Queue.Count == 0) return new(5, 0);
            if (Queue.Peek().Format == 5) { Queue.Dequeue(); return new(5, 0); }
            var head = Queue.Peek();
            return new(head.Format, head.Bytes.Length);
        }
        public byte[] ReadImage(ImageInfo info)
        {
            Downloads++;
            OnDownload?.Invoke();
            return Queue.Dequeue().Bytes;
        }
        public void HalfPress() => OnHalfPress?.Invoke();
        public void ReleaseHalfPress() => HalfReleases++;
        public void FullPress()
        {
            Triggers++;
            foreach (var image in Shot) Queue.Enqueue(image);
            OnFullPress?.Invoke();
        }
        public void Dispose() { }
    }
}
