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

    [Theory]
    [InlineData(3, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    public void CapturesPartnersWithShortOrLongShotNumbers(ushort rawType, ushort jpegType)
    {
        var native = new FakeCamera
        {
            Shot = [(1, Image(36543, 1, rawType)), (7, Image(36543, 7, jpegType))]
        };
        using var session = Create(native);
        session.Connect();
        var result = session.Capture(LightChannel.White, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.Equal(2, result.FilePaths.Count);
        Assert.All(result.FilePaths, file => Assert.Equal("White-00036543", Path.GetFileNameWithoutExtension(file)));
        Assert.False(session.Faulted);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(7, 1)]
    public void RejectsUnsupportedShotNumberTypesAndCounts(ushort type, uint count)
    {
        var image = Image(36543, 7);
        U16(image, 72, type);
        U32(image, 74, count);
        Assert.Throws<InvalidDataException>(() => ShotIdentity.Read(image, 7));
    }

    private CameraSession Create(FakeCamera camera)
    {
        var clock = new FakeTimeProvider();
        return new(camera, output, clock, clock.Advance);
    }

    private static byte[] Image(uint shot, int format, ushort shotType = 4)
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
        Tag(tiff, 58, 0x1438, shotType, 1, shot);
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
        public CameraConnection Connect() => new("X-T5", "test", "test", "test", Quality);
        public void ValidateConfiguration() { }
        public CameraSdkException? ConnectionError { get; set; }
        public void CheckConnection()
        {
            if (ConnectionError is not null) throw ConnectionError;
        }
        public ImageInfo ReadImageInfo()
        {
            if (BusyReads-- > 0) throw new CameraSdkException("Busy", 0x1006);
            if (Queue.Count == 0) return new(5, 0);
            if (Queue.Peek().Format == 5) { Queue.Dequeue(); return new(5, 0); }
            var head = Queue.Peek();
            return new(head.Format, head.Bytes.Length);
        }
        public byte[] ReadImage(ImageInfo info) { Downloads++; return Queue.Dequeue().Bytes; }
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
