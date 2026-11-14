using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BitMiracle.LibTiff.Classic;
using FilmLight.Core;
using FilmLight.Processing;
using Xunit;

namespace FilmLight.Tests.Processing;

public sealed class RollProcessingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"FilmLight-processing-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExplicitRollChannelsUseReferencesAndPreserveOriginalsThroughExport()
    {
        var renderer = new FakeRenderer();
        var processor = new NegativeProcessor(renderer, Path.Combine(root, "cache"));
        var capture = Capture(RollCaptureKind.Frame);
        var references = Capture(RollCaptureKind.References);
        var prepared = await processor.PrepareAsync(capture, references, new(null, null));
        Assert.Equal(capture.Channels[LightChannel.Red][0], prepared.Sources[0].Path);
        Assert.Equal(3, prepared.References.Count);
        Assert.Equal(1, prepared.Normalization!.Value.G, 5);
        Assert.Contains(prepared.Warnings, warning => warning.Contains("experimental"));
        var settings = NegativeProcessor.Initialize(prepared, NegativeConversionSettings.Default);
        string output = await processor.ExportAsync(prepared, settings, Path.Combine(root, "outputs"));
        Assert.True(File.Exists(Path.Combine(output, "conversion.json")));
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "conversion.json")));
        Assert.Equal(6, record.RootElement.GetProperty("sourceHashes").EnumerateObject().Count());
        Assert.Equal(3, renderer.FullReads);
        using var tiff = Tiff.Open(Path.Combine(output, "positive.tif"), "r");
        Assert.Equal(16, tiff.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt());
        var profile = tiff.GetField(TiffTag.ICCPROFILE)[1].ToByteArray();
        Assert.Equal("acsp", Encoding.ASCII.GetString(profile, 36, 4));
        Assert.Equal(profile.Length, BinaryPrimitives.ReadInt32BigEndian(profile));
        Assert.All(prepared.Sources.Concat(prepared.References), source => Assert.Equal("unchanged", File.ReadAllText(source.Path)));
        _ = await processor.PrepareAsync(capture, references, new(null, null));
        Assert.Equal(6, renderer.PreviewReads);
    }

    [Fact]
    public async Task ChangedRawAndChangedLightCannotPublishAnOldPreparation()
    {
        var processor = new NegativeProcessor(new FakeRenderer(), Path.Combine(root, "cache"));
        var prepared = await processor.PrepareAsync(Capture(RollCaptureKind.White), null, new(null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ExportAsync(prepared,
            NegativeConversionSettings.Default with { Light = new(new(1, 1, 1), "changed") }, Path.Combine(root, "outputs")));
        File.AppendAllText(prepared.Sources[0].Path, "changed");
        await Assert.ThrowsAsync<IOException>(() => processor.ExportAsync(prepared,
            NegativeConversionSettings.Default, Path.Combine(root, "outputs")));
        Assert.False(Directory.Exists(Path.Combine(root, "outputs")));
    }

    [Fact]
    public async Task JpegFailureAndCancellationNeverPublishPartialOutputs()
    {
        var processor = new NegativeProcessor(new FakeRenderer(), Path.Combine(root, "cache"));
        var prepared = await processor.PrepareAsync(Capture(RollCaptureKind.Frame), null, new(null, null));
        var settings = NegativeProcessor.Initialize(prepared, NegativeConversionSettings.Default);
        string destination = Path.Combine(root, "outputs");
        string completed = await processor.ExportAsync(prepared, settings, destination);
        await Assert.ThrowsAsync<IOException>(() => processor.ExportAsync(prepared, settings, destination,
            (_, _, _) => throw new IOException("encoder failed")));
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ExportAsync(prepared, settings, destination,
            (_, _, _) => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token));
        Assert.True(File.Exists(Path.Combine(completed, "positive.tif")));
        Assert.Single(Directory.GetDirectories(destination, "Positive-*"));
        Assert.Equal(2, Directory.GetDirectories(destination, ".*.pending").Length);
    }

    [Fact]
    public async Task IncompleteDiscardedDuplicateAndJpegOnlyInputsAreNotConverted()
    {
        var processor = new NegativeProcessor(new FakeRenderer(), Path.Combine(root, "cache"));
        var frame = Capture(RollCaptureKind.Frame);
        await Assert.ThrowsAsync<ArgumentException>(() => processor.PrepareAsync(frame with { Discarded = true }, null, new(null, null)));
        frame.Channels.Remove(LightChannel.Blue);
        await Assert.ThrowsAsync<ArgumentException>(() => processor.PrepareAsync(frame, null, new(null, null)));
        var white = Capture(RollCaptureKind.White);
        white.Channels[LightChannel.White] = ["preview.jpg"];
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.PrepareAsync(white, null, new(null, null)));
        white = Capture(RollCaptureKind.White);
        white.Channels[LightChannel.White] = [white.Channels[LightChannel.White][0], white.Channels[LightChannel.White][0]];
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.PrepareAsync(white, null, new(null, null)));
    }

    [Fact]
    public void GeometryPreservesProfileAndSixteenBitSamplesWhenCroppingAndRotating()
    {
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.tif"), output = Path.Combine(root, "framed.tif");
        var image = new NegativeLinearImage(4, 2, Enumerable.Range(0, 8)
            .SelectMany(i => new[] { .1f + i * .01f, .1f + i * .01f, .1f + i * .01f }).ToArray());
        NegadoctorMath.WritePositiveTiff(image, NegadoctorParameters.Default, NegativePrintFinish.Neutral, source, default);
        TiffGeometry.Apply(source, output, new(90, false, false, new(.25, 0, .5, 1)), default);
        using var original = Tiff.Open(source, "r");
        using var framed = Tiff.Open(output, "r");
        Assert.Equal(2, framed.GetField(TiffTag.IMAGEWIDTH)[0].ToInt());
        Assert.Equal(2, framed.GetField(TiffTag.IMAGELENGTH)[0].ToInt());
        var inputRow = new byte[original.ScanlineSize()];
        var outputRow = new byte[framed.ScanlineSize()];
        Assert.True(original.ReadScanline(inputRow, 1));
        Assert.True(framed.ReadScanline(outputRow, 0));
        Assert.Equal(inputRow[6..12], outputRow[..6]);
        Assert.Equal(original.GetField(TiffTag.ICCPROFILE)[1].ToByteArray(), framed.GetField(TiffTag.ICCPROFILE)[1].ToByteArray());
    }

    private RollCapture Capture(RollCaptureKind kind)
    {
        string id = Guid.NewGuid().ToString("N");
        var channels = new Dictionary<LightChannel, string[]>();
        foreach (var channel in kind == RollCaptureKind.White
            ? new[] { LightChannel.White } : [LightChannel.Red, LightChannel.Green, LightChannel.Blue])
        {
            string directory = Path.Combine(root, id, kind.ToString(), channel.ToString());
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "frame.RAF");
            File.WriteAllText(file, "unchanged");
            channels.Add(channel, [file]);
        }
        return new(id, kind, channels);
    }

    private sealed class FakeRenderer : INegativeRenderer
    {
        public int PreviewReads { get; private set; }
        public int FullReads { get; private set; }
        public ValueTask<NegativeLabCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NegativeLabCapabilities(true, "fake", "5.6.2", null, "templates"));

        public ValueTask RenderLinearAsync(NegativeRenderRequest request, string outputPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.FullResolutionLinear) FullReads++; else PreviewReads++;
            var plane = RgbCaptureTests.Scene(32, 32);
            int channel = request.SourcePath.Contains("Red") ? 0 : request.SourcePath.Contains("Green") ? 1 : 2;
            bool reference = request.SourcePath.Contains("References");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var stream = File.Create(outputPath);
            stream.Write(Encoding.ASCII.GetBytes("PF\n32 32\n-1\n"));
            using var writer = new BinaryWriter(stream);
            for (int y = 31; y >= 0; y--)
            for (int x = 0; x < 32; x++)
            for (int c = 0; c < 3; c++)
                writer.Write((float)((reference ? .9 + .2 * x / 31d : plane.Pixels[y * 32 + x]) * (c == channel ? 1 : .1)));
            return ValueTask.CompletedTask;
        }
        public ValueTask RenderExportAsync(NegativeRenderRequest request, string outputPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("White master is not used by these tests.");
        public ValueTask RenderProofAsync(NegativeRenderRequest request, string outputPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
