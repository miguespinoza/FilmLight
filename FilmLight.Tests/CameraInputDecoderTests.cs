using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using BitMiracle.LibTiff.Classic;
using FilmLight.Core;
using FilmLight.Workflow;
using Xunit;

namespace FilmLight.Tests;

[SupportedOSPlatform("windows")]
public sealed class CameraInputDecoderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FilmLight-camera-decoder-" + Guid.NewGuid().ToString("N"));
    private static readonly string Cli = Environment.GetEnvironmentVariable("FILMLIGHT_TEST_DARKTABLE")
        ?? @"C:\Program Files\darktable\bin\darktable-cli.exe";

    [Fact]
    public async Task QualifiedInputsPassThroughInDetachedSnapshotAndOnlyMatchingReferencesAreRead()
    {
        var (roll, frame) = QualifiedRoll();
        var latestLight = Capture(CapturePurpose.LightReference, Write("latest-light.tif"), frame.CapturedAt.AddSeconds(-1));
        var future = Capture(CapturePurpose.LightReference, "missing-future.RAF", frame.CapturedAt.AddSeconds(1));
        var wrongMode = Capture(CapturePurpose.LightReference, "missing-rgb.RAF", frame.CapturedAt.AddSeconds(-1)) with { Mode = CaptureMode.Rgb };
        var wrongProvenance = Capture(CapturePurpose.FilmBaseReference, "missing-demo.RAF", frame.CapturedAt.AddSeconds(-1)) with { IsDemo = true };
        var unrelated = Capture(CapturePurpose.Frame, "missing-other.RAF", frame.CapturedAt.AddSeconds(-1));
        roll.Captures[0] = roll.Captures[0] with { Files = [new(LightChannel.White, "missing-obsolete.RAF")] };
        roll.Captures.AddRange([latestLight, future, wrongMode, wrongProvenance, unrelated]);
        var snapshot = await Decoder().DecodeAsync(roll, frame, root, default);
        Assert.NotSame(roll, snapshot);
        Assert.NotSame(roll.Captures, snapshot.Captures);
        Assert.NotSame(roll.Recipes, snapshot.Recipes);
        Assert.NotSame(roll.Exports, snapshot.Exports);
        Assert.Equal(roll.Captures.Select(c => c.Id), snapshot.Captures.Select(c => c.Id));
        foreach (var original in roll.Captures)
        {
            var copy = snapshot.Captures.Single(c => c.Id == original.Id);
            Assert.Equal(original.Files, copy.Files);
            Assert.NotSame(original.Files, copy.Files);
            Assert.Equal(original.CapturedAt, copy.CapturedAt);
        }
        Assert.False(Directory.Exists(Path.Combine(root, "camera-input-cache")));
    }

    [Fact]
    public async Task PictureSamplesDecodeOnlyTheFrameAndSampleOriginsWithoutReferences()
    {
        var (roll, frame) = QualifiedRoll();
        var origin = Capture(CapturePurpose.Frame, Write("picked-frame.tif"), frame.CapturedAt.AddSeconds(-1));
        roll = roll with { Captures = [frame, origin,
            Capture(CapturePurpose.LightReference, "obsolete-missing-reference.RAF", frame.CapturedAt.AddSeconds(-2))] };
        var recipe = new DevelopRecipe
        {
            PictureCalibration = new()
            {
                Light = new(origin.Id, .1, .2, .8, .7, .6),
                FilmBase = new(origin.Id, .3, .4, .6, .3, .2)
            }
        };
        var snapshot = await Decoder().DecodeAsync(roll, frame, root, default, recipe);
        Assert.Equal(frame.Files, snapshot.Captures.Single(c => c.Id == frame.Id).Files);
        Assert.Equal(origin.Files, snapshot.Captures.Single(c => c.Id == origin.Id).Files);
        Assert.False(Directory.Exists(Path.Combine(root, "camera-input-cache")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Decoder().DecodeAsync(roll, frame, root, default,
            recipe with { PictureCalibration = new() { Light = new(Guid.NewGuid(), .1, .2, .8, .7, .6) } }));
    }

    [Fact]
    public async Task DemoQualificationAndProvenanceArePreserved()
    {
        var (roll, frame) = QualifiedRoll(demo: true);
        var result = await Decoder().DecodeAsync(roll, frame, root, default);
        Assert.True(result.IsDemo);
        Assert.All(result.Captures, c => Assert.True(c.IsDemo));
        await Assert.ThrowsAsync<InvalidDataException>(() => Decoder().DecodeAsync(
            roll with { IsDemo = false, Captures = roll.Captures.Select(c => c with { IsDemo = false }).ToList() },
            frame with { IsDemo = false }, root, default));
    }

    [Fact]
    public async Task QualifiedRgbTripletsKeepOneFilePerPrimaryAndIgnoreWhiteReferences()
    {
        var (roll, frame) = QualifiedRoll();
        roll = roll with
        {
            Captures = roll.Captures.Select(c => c with
            {
                Mode = CaptureMode.Rgb,
                Files = new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue }
                    .Select(channel => new CaptureFile(channel, Write($"{c.Id}-{channel}.tif"))).ToArray()
            }).ToList()
        };
        frame = roll.Captures[2];
        roll.Captures.Add(Capture(CapturePurpose.LightReference, "missing-white.RAF", frame.CapturedAt.AddSeconds(-1)));
        var result = await Decoder().DecodeAsync(roll, frame, root, default);
        foreach (var capture in result.Captures.Where(c => c.Mode == CaptureMode.Rgb))
            Assert.Equal([LightChannel.Red, LightChannel.Green, LightChannel.Blue], capture.Files.Select(f => f.Channel));
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("duplicate-raw")]
    [InlineData("unqualified")]
    [InlineData("gamma-profile")]
    [InlineData("nonfinite")]
    [InlineData("wrong-dimensions")]
    [InlineData("missing-channel")]
    public async Task UnsafeInputsAreRejectedWithoutCallingCli(string kind)
    {
        var (roll, frame) = QualifiedRoll();
        var files = kind switch
        {
            "jpeg" => new[] { new CaptureFile(LightChannel.White, "camera.JPG") },
            "duplicate-raw" => [new(LightChannel.White, "a.RAF"), new(LightChannel.White, "b.RAF")],
            "unqualified" => [new(LightChannel.White, Write("unqualified.tif", description: ""))],
            "gamma-profile" => [new(LightChannel.White, Write("gamma.tif", profile: GammaProfile()))],
            "nonfinite" => [new(LightChannel.White, WriteNonfinite())],
            "wrong-dimensions" => [new(LightChannel.White, Write("wide.tif", width: 5))],
            _ => Array.Empty<CaptureFile>()
        };
        frame = frame with { Files = files };
        roll.Captures[2] = frame;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Decoder().DecodeAsync(roll, frame, root, default));
        if (kind == "jpeg")
        {
            Assert.Contains("FINE+RAW", error.Message);
            Assert.Contains("LOSSLESS COMPRESSED", error.Message);
        }
        Assert.False(Directory.Exists(Path.Combine(root, "camera-input-cache")));
    }

    [Theory]
    [InlineData(CapturePurpose.Frame, "Frame 1")]
    [InlineData(CapturePurpose.LightReference, "NO-FILM light reference")]
    [InlineData(CapturePurpose.FilmBaseReference, "FILM BASE reference")]
    public async Task JpegErrorsIdentifyTheAffectedFrameOrReference(CapturePurpose purpose, string label)
    {
        var (roll, frame) = QualifiedRoll();
        int index = roll.Captures.FindIndex(c => c.Purpose == purpose);
        roll.Captures[index] = roll.Captures[index] with { Files = [new(LightChannel.White, "camera.JPG")] };
        frame = roll.Captures.Single(c => c.Purpose == CapturePurpose.Frame);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Decoder().DecodeAsync(roll, frame, root, default));
        Assert.StartsWith(label + ", White:", error.Message);
        Assert.Contains("FINE+RAW", error.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "camera-input-cache")));
    }

    [Fact]
    public async Task PositiveRequiresOnlyLightReferenceAndCancellationDoesNotStartCli()
    {
        var (roll, frame) = QualifiedRoll();
        roll = roll with { FilmKind = FilmKind.Positive, Captures = roll.Captures.Where(c => c.Purpose != CapturePurpose.FilmBaseReference).ToList() };
        _ = await Decoder().DecodeAsync(roll, frame, root, default);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Decoder().DecodeAsync(roll, frame, root, cancel.Token));
    }

    [Theory]
    [InlineData("\u25cf input color profile\n\u25cf exposure", "")]
    [InlineData("\u25cf input color profile", "color matrix not found")]
    [InlineData("\u25cf input color profile\n\u25cf negadoctor", "")]
    public void UnexpectedPipelineOrProfileFallbackIsRejected(string stdout, string stderr) =>
        Assert.Throws<InvalidDataException>(() => CameraInputDecoder.ValidateHistory(stdout, stderr, raw: false));

    [CliFact]
    public async Task RealCliIdentityConvertsEmbeddedLinearRec709ToLinearRec2020WithoutToneMapping()
    {
        Directory.CreateDirectory(root);
        float[] input = Enumerable.Range(0, 4 * 3).SelectMany(_ => new[] { .2f, .4f, .6f }).ToArray();
        ProcessingImages.WriteTiff(Path.Combine(root, "input.tif"), new(4, 3, input), true, "color conversion test",
            default, Rec709Profile());
        CameraInputDecoder.WriteIdentityXmp(Path.Combine(root, "identity.xmp"), raw: false);
        string log = await CameraInputDecoder.ConvertAsync(new(Cli), root, "input.tif", "output.tif", default);
        CameraInputDecoder.ValidateHistory(log, File.ReadAllText(Path.Combine(root, "decode.stderr.log")), raw: false);
        CameraInputDecoder.ValidateFloatProfile(Path.Combine(root, "output.tif"));
        var result = ProcessingImages.ReadLinear(Path.Combine(root, "output.tif"), default);
        Assert.Equal(4, result.Width);
        Assert.Equal(3, result.Height);
        float[] expected = [.28318f, .38846f, .57584f];
        for (int i = 0; i < result.Pixels.Length; i++)
            Assert.InRange(Math.Abs(result.Pixels[i] - expected[i % 3]), 0, .0003);
    }

    [RawFact("FILMLIGHT_TEST_RAF")]
    public async Task SingleRafCanPreviewWithoutReferenceCapturesOrPicks()
    {
        var frame = Capture(CapturePurpose.Frame, Environment.GetEnvironmentVariable("FILMLIGHT_TEST_RAF")!, DateTimeOffset.UtcNow);
        var roll = new RollDocument { FilmKind = FilmKind.ColorNegative, Captures = [frame] };
        var progress = new RecordingProcessingProgress();
        var result = await new DarktableFrameProcessor(Cli).PreviewAsync(roll, frame,
            new DevelopRecipe { FilmKind = roll.FilmKind, PictureCalibration = new() }, root, progress, default);
        var stages = progress.Events.Select(p => p.Stage).ToArray();
        Assert.Contains("Decoding RAW", stages);
        Assert.Contains("Validating decoded RAW", stages);
        Assert.Contains("Saving RAW cache", stages);
        Assert.Contains("Inverting negative", stages);
        Assert.Equal("Publishing output", stages[^1]);
        var cachedProgress = new RecordingProcessingProgress();
        await new CameraInputDecoder(new(Cli)).DecodeAsync(roll, frame, root, default,
            new DevelopRecipe { FilmKind = roll.FilmKind, PictureCalibration = new() }, cachedProgress);
        Assert.Contains(cachedProgress.Events, p => p.Stage == "Loading cached RAW");
        Assert.DoesNotContain(cachedProgress.Events, p => p.Stage == "Decoding RAW");
        using var image = System.Drawing.Image.FromFile(result.ImagePath);
        Assert.InRange(Math.Max(image.Width, image.Height), 1, 1600);
        Assert.True(File.Exists(result.SourceImagePath));
        Assert.Contains(result.Warnings, w => w.Contains("Provisional"));
    }

    [RawFact("FILMLIGHT_TEST_RAF")]
    public async Task RealRafPairPrefersRawCachesByContentAndPreservesOriginals()
    {
        string raw = Environment.GetEnvironmentVariable("FILMLIGHT_TEST_RAF")!;
        byte[] before = await Hash(raw);
        var time = DateTimeOffset.UtcNow;
        var captures = new[] { CapturePurpose.LightReference, CapturePurpose.FilmBaseReference, CapturePurpose.Frame }
            .Select((purpose, i) => Capture(purpose, raw, time.AddSeconds(i)) with
            {
                Files = [new(LightChannel.White, Path.Combine(root, "nonexistent-camera-preview.JPG")), new(LightChannel.White, raw)]
            }).ToList();
        var roll = new RollDocument { Captures = captures };
        string originals = JsonSerializer.Serialize(roll);
        var decoder = new CameraInputDecoder(new(Cli));
        var snapshot = await decoder.DecodeAsync(roll, captures[2], root, default);
        Assert.Equal(originals, JsonSerializer.Serialize(roll));
        Assert.Equal(before, await Hash(raw));
        string output = Assert.Single(snapshot.Captures[2].Files).Path;
        Assert.NotEqual(raw, output);
        Assert.All(snapshot.Captures, c => Assert.Equal(output, Assert.Single(c.Files).Path));
        CameraInputDecoder.ValidateFloatProfile(output);
        var image = ProcessingImages.ReadLinear(output, default, requireQualification: true);
        Assert.True(image.Width > 100 && image.Height > 100);
        Assert.True(image.Pixels.Max() > .01);
        int commands = Directory.GetFiles(root, "decode.command.txt", SearchOption.AllDirectories).Length;
        Assert.Equal(1, commands);
        var again = await decoder.DecodeAsync(roll, captures[2], root, default);
        Assert.Equal(output, Assert.Single(again.Captures[2].Files).Path);
        Assert.Equal(commands, Directory.GetFiles(root, "decode.command.txt", SearchOption.AllDirectories).Length);

        await File.AppendAllTextAsync(output, "cache corruption");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => decoder.DecodeAsync(roll, captures[2], root, default));
        Assert.Contains("Corrupt", error.Message);
    }

    [RawFact("FILMLIGHT_TEST_SIGNED_RAF")]
    public async Task RealSignedCameraProfileOutputIsPreservedWithoutClippingOrOffset()
    {
        string raw = Environment.GetEnvironmentVariable("FILMLIGHT_TEST_SIGNED_RAF")!;
        byte[] before = await Hash(raw);
        var time = DateTimeOffset.UtcNow;
        var captures = new[] { CapturePurpose.LightReference, CapturePurpose.FilmBaseReference, CapturePurpose.Frame }
            .Select((purpose, i) => Capture(purpose, raw, time.AddSeconds(i))).ToList();
        var roll = new RollDocument { Captures = captures };
        var snapshot = await new CameraInputDecoder(new(Cli)).DecodeAsync(roll, captures[2], root, default);
        Assert.Equal(before, await Hash(raw));
        Assert.Single(Directory.GetFiles(root, "receipt.json", SearchOption.AllDirectories));
        string decoded = Assert.Single(snapshot.Captures[2].Files).Path;
        CameraInputDecoder.ValidateFloatProfile(decoded);
        string directory = Path.GetDirectoryName(decoded)!;
        CameraInputDecoder.ValidateHistory(File.ReadAllText(Path.Combine(directory, "decode.stdout.log")),
            File.ReadAllText(Path.Combine(directory, "decode.stderr.log")), raw: true);
        using var tif = Tiff.Open(decoded, "r");
        int width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        var row = new byte[tif.ScanlineSize()];
        long negatives = 0;
        for (int y = 0; y < height; y++)
        {
            Assert.True(tif.ReadScanline(row, y));
            for (int x = 0; x < width * 3; x++)
            {
                float value = BitConverter.ToSingle(row, x * 4);
                Assert.True(float.IsFinite(value));
                if (value < 0) negatives++;
            }
        }
        Assert.True(negatives > 0);
    }

    [Fact]
    public async Task QualifiedSignedLinearSamplesAreKeptWithoutCallingCli()
    {
        var (roll, frame) = QualifiedRoll();
        var path = frame.Files[0].Path;
        var pixels = Enumerable.Repeat(.2f, 4 * 3 * 3).ToArray();
        pixels[0] = -.015f;
        ProcessingImages.WriteTiff(path, new(4, 3, pixels), true, ProcessingImages.LinearLabel,
            default, ProcessingImages.LinearRec2020Profile());
        var decoded = await Decoder().DecodeAsync(roll, frame, root, default);
        Assert.Equal(-.015f, ProcessingImages.ReadLinear(decoded.Captures[2].Files[0].Path, default).Pixels[0]);
    }

    [RawFact("FILMLIGHT_TEST_RAF")]
    public async Task RealRafInputsReachNegadoctorAndValidatedPositiveExports()
    {
        string raw = Environment.GetEnvironmentVariable("FILMLIGHT_TEST_RAF")!;
        var time = DateTimeOffset.UtcNow;
        var captures = new[] { CapturePurpose.LightReference, CapturePurpose.FilmBaseReference, CapturePurpose.Frame }
            .Select((purpose, i) => Capture(purpose, raw, time.AddSeconds(i))).ToList();
        var roll = new RollDocument { Captures = captures };
        var decoded = await new CameraInputDecoder(new(Cli)).DecodeAsync(roll, captures[2], root, default);
        var crop = UsableCrop(decoded.Captures[2].Files[0].Path);
        var recipe = new DevelopRecipe
        {
            FilmKind = FilmKind.ColorNegative, Accepted = true,
            CropLeft = crop.Left, CropTop = crop.Top, CropRight = crop.Right, CropBottom = crop.Bottom
        };
        var result = await new DarktableFrameProcessor(Cli, root).ExportAsync(roll, captures[2], recipe,
            Path.Combine(root, "export"), null, default);
        using var master = Tiff.Open(result.OutputFiles[0], "r");
        Assert.Equal(16, master.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt());
        Assert.Equal(32, master.GetField(TiffTag.IMAGEWIDTH)[0].ToInt());
        Assert.Equal(32, master.GetField(TiffTag.IMAGELENGTH)[0].ToInt());
        Assert.True(master.GetField(TiffTag.ICCPROFILE)[0].ToInt() >= 128);
        Assert.Contains("negadoctor", File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.OutputFiles[0])!, "develop.stdout.log")));
        Assert.All(captures, capture => Assert.Equal(raw, capture.Files[0].Path));
    }

    private static (double Left, double Top, double Right, double Bottom) UsableCrop(string path)
    {
        var image = ProcessingImages.ReadLinear(path, default);
        const int size = 32;
        for (int top = 0; top <= image.Height - size; top += size)
        for (int left = 0; left <= image.Width - size; left += size)
        {
            bool usable = true;
            for (int y = top; usable && y < top + size; y++)
            for (int x = left; usable && x < left + size; x++)
            for (int c = 0; c < 3; c++)
            {
                var sample = image.Pixels[(y * image.Width + x) * 3 + c];
                if (sample <= .0001f || sample >= .99999f) { usable = false; break; }
            }
            if (usable) return ((left + .125) / image.Width, (top + .125) / image.Height,
                (image.Width - left - size + .125) / image.Width, (image.Height - top - size + .125) / image.Height);
        }
        throw new InvalidDataException("Public RAW fixture has no unclipped patch suitable for a pipeline-only test.");
    }

    [RawFact("FILMLIGHT_TEST_LOSSY_RAF")]
    public async Task RealUnsupportedLossyRafFailsWithoutJpegFallbackOrPublishedCache()
    {
        string raw = Environment.GetEnvironmentVariable("FILMLIGHT_TEST_LOSSY_RAF")!;
        var (roll, frame) = QualifiedRoll();
        frame = frame with { Files = [new(LightChannel.White, raw), new(LightChannel.White, Write("preview.jpg"))] };
        roll.Captures[2] = frame;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CameraInputDecoder(new(Cli)).DecodeAsync(roll, frame, root, default));
        Assert.Contains("LOSSLESS COMPRESSED", error.Message);
        Assert.Empty(Directory.GetFiles(root, "receipt.json", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(root, "decode.command.txt", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(root, "failure.txt", SearchOption.AllDirectories));
        string log = File.ReadAllText(Assert.Single(Directory.GetFiles(root, "decode.stdout.log", SearchOption.AllDirectories)));
        Assert.Contains("[rawspeed]", log);
        Assert.Contains("compressed RAF header check", log);
    }

    private CameraInputDecoder Decoder() => new(new(Path.Combine(root, "must-not-run.exe")));

    private (RollDocument Roll, RollCapture Frame) QualifiedRoll(bool demo = false)
    {
        var time = DateTimeOffset.UtcNow;
        var captures = new[] { CapturePurpose.LightReference, CapturePurpose.FilmBaseReference, CapturePurpose.Frame }
            .Select((purpose, i) => Capture(purpose, Write(purpose + ".tif", demo: demo), time.AddSeconds(i)) with { IsDemo = demo }).ToList();
        return (new() { IsDemo = demo, Captures = captures }, captures[2]);
    }

    private static RollCapture Capture(CapturePurpose purpose, string path, DateTimeOffset time) =>
        new(Guid.NewGuid(), 1, purpose, CaptureMode.White, [new(LightChannel.White, path)], false, time);

    private string Write(string name, bool demo = false, string? description = null, byte[]? profile = null, int width = 4)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, name);
        ProcessingImages.WriteTiff(path, new(width, 3, Enumerable.Repeat(.2f, width * 3 * 3).ToArray()), true,
            description ?? (demo ? ProcessingImages.SyntheticLabel + "; " : "") + ProcessingImages.LinearLabel,
            default, profile ?? ProcessingImages.LinearRec2020Profile());
        return path;
    }

    private string WriteNonfinite()
    {
        string path = Write("nonfinite.tif");
        using var tif = Tiff.Open(path, "r+");
        var row = new byte[tif.ScanlineSize()];
        Assert.True(tif.ReadScanline(row, 0));
        BitConverter.TryWriteBytes(row.AsSpan(0, 4), float.NaN);
        Assert.True(tif.WriteScanline(row, 0));
        return path;
    }

    private static byte[] GammaProfile()
    {
        var data = ProcessingImages.LinearRec2020Profile();
        ChangeTags(data, (tag, offset) =>
        {
            if (tag.EndsWith("TRC", StringComparison.Ordinal))
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 12), 563);
        });
        return data;
    }

    private static byte[] Rec709Profile()
    {
        var data = ProcessingImages.LinearRec2020Profile();
        double[][] matrix = [[.43607, .22250, .01393], [.38515, .71687, .09708], [.14307, .06061, .71410]];
        ChangeTags(data, (tag, offset) =>
        {
            int channel = tag switch { "rXYZ" => 0, "gXYZ" => 1, "bXYZ" => 2, _ => -1 };
            if (channel >= 0)
                for (int c = 0; c < 3; c++)
                    BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset + 8 + c * 4), (int)Math.Round(matrix[channel][c] * 65536));
        });
        return data;
    }

    private static void ChangeTags(byte[] data, Action<string, int> change)
    {
        int count = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(128));
        for (int i = 0; i < count; i++)
        {
            int entry = 132 + i * 12;
            change(Encoding.ASCII.GetString(data.AsSpan(entry, 4)), BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(entry + 4)));
        }
    }

    private static async Task<byte[]> Hash(string path)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream);
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("FILMLIGHT_KEEP_TEST_ARTIFACTS") != "1" && Directory.Exists(root))
            Directory.Delete(root, true);
    }

    private sealed class CliFactAttribute : FactAttribute
    {
        public CliFactAttribute()
        {
            if (!File.Exists(Cli)) Skip = "Set FILMLIGHT_TEST_DARKTABLE to darktable-cli 5.6.2.";
        }
    }

    private sealed class RawFactAttribute : FactAttribute
    {
        public RawFactAttribute(string variable)
        {
            if (!File.Exists(Cli) || !File.Exists(Environment.GetEnvironmentVariable(variable)))
                Skip = $"Requires darktable 5.6.2 and a legitimate public RAF fixture via {variable}.";
        }
    }
}
