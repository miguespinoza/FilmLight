using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using BitMiracle.LibTiff.Classic;
using FilmLight.Core;
using FilmLight.Workflow;
using Xunit;

namespace FilmLight.Tests;

[SupportedOSPlatform("windows")]
public sealed class FrameSourceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FilmLight-camera-decoder-" + Guid.NewGuid().ToString("N"));
    private static readonly string Cli = Environment.GetEnvironmentVariable("FILMLIGHT_TEST_DARKTABLE")
        ?? @"C:\Program Files\darktable\bin\darktable-cli.exe";

    [Fact]
    public async Task QualifiedInputsReadOnlyMatchingReferencesWithoutChangingTheRoll()
    {
        var (roll, frame) = QualifiedRoll();
        var latestLight = Capture(CapturePurpose.LightReference, Write("latest-light.tif"), frame.CapturedAt.AddSeconds(-1));
        var future = Capture(CapturePurpose.LightReference, "missing-future.RAF", frame.CapturedAt.AddSeconds(1));
        var wrongMode = Capture(CapturePurpose.LightReference, "missing-rgb.RAF", frame.CapturedAt.AddSeconds(-1)) with { Mode = CaptureMode.Rgb };
        var wrongProvenance = Capture(CapturePurpose.FilmBaseReference, "missing-demo.RAF", frame.CapturedAt.AddSeconds(-1)) with { IsDemo = true };
        var unrelated = Capture(CapturePurpose.Frame, "missing-other.RAF", frame.CapturedAt.AddSeconds(-1));
        roll.Captures[0] = roll.Captures[0] with { Files = [new(LightChannel.White, "missing-obsolete.RAF")] };
        roll.Captures.AddRange([latestLight, future, wrongMode, wrongProvenance, unrelated]);
        string before = JsonSerializer.Serialize(roll);
        using var sources = Sources();
        var image = await ReferenceReconstruction.ReconstructAsync(sources, roll, frame, new(), false,
            FrameSources.FullResolution, default);
        Assert.Equal(4, image.Image.Width);
        Assert.Equal(before, JsonSerializer.Serialize(roll));
        Assert.False(Directory.Exists(Path.Combine(root, "camera-input-cache")));
    }

    [Fact]
    public async Task PictureSamplesDecodeOnlyTheirOwnFrameWithoutReferencesOrOtherOrigins()
    {
        var (roll, frame) = QualifiedRoll();
        var origin = Capture(CapturePurpose.Frame, Write("picked-frame.tif"), frame.CapturedAt.AddSeconds(-1));
        roll = roll with { Captures = [frame, origin,
            Capture(CapturePurpose.LightReference, "obsolete-missing-reference.RAF", frame.CapturedAt.AddSeconds(-2))] };
        var recipe = new DevelopRecipe
        {
            PictureCalibration = new()
            {
                Light = new(frame.Id, .1, .2, .8, .7, .6),
                FilmBase = new(frame.Id, .3, .4, .6, .3, .2)
            }
        };
        using var sources = Sources();
        var image = await ReferenceReconstruction.ReconstructAsync(sources, roll, frame, recipe, false,
            FrameSources.FullResolution, default);
        Assert.Equal(4, image.Image.Width);
        Assert.False(Directory.Exists(Path.Combine(root, "camera-input-cache")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReferenceReconstruction.ReconstructAsync(sources, roll, frame,
            recipe with { PictureCalibration = new() { Light = new(Guid.NewGuid(), .1, .2, .8, .7, .6) } }, false,
            FrameSources.FullResolution, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReferenceReconstruction.ReconstructAsync(sources, roll, frame,
            recipe with { PictureCalibration = new() { Light = new(origin.Id, .1, .2, .8, .7, .6) } }, false,
            FrameSources.FullResolution, default));
    }

    [Fact]
    public async Task DemoQualificationAndProvenanceArePreserved()
    {
        var (roll, frame) = QualifiedRoll(demo: true);
        using var sources = Sources();
        Assert.Equal(4, (await sources.Open(roll, frame).GetImageAsync(FrameSources.FullResolution, default)).Width);
        var real = roll with { IsDemo = false, Captures = roll.Captures.Select(c => c with { IsDemo = false }).ToList() };
        await Assert.ThrowsAsync<InvalidDataException>(() => sources.Open(real, real.Captures[2]).GetImageAsync(FrameSources.FullResolution, default));
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
        using var sources = Sources();
        var result = await sources.Open(roll, frame).GetImageAsync(FrameSources.FullResolution, default);
        Assert.All(result.Pixels, value => Assert.Equal(.2f, value));
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
        using var sources = Sources();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ReferenceReconstruction.ReconstructAsync(sources, roll, frame, new(), false, FrameSources.FullResolution, default));
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
        using var sources = Sources();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ReferenceReconstruction.ReconstructAsync(sources, roll, frame, new(), false, FrameSources.FullResolution, default));
        Assert.StartsWith(label + ", White:", error.Message);
        Assert.Contains("FINE+RAW", error.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "camera-input-cache")));
    }

    [Fact]
    public async Task PositiveRequiresOnlyLightReferenceAndCancellationDoesNotStartCli()
    {
        var (roll, frame) = QualifiedRoll();
        roll = roll with { FilmKind = FilmKind.Positive, Captures = roll.Captures.Where(c => c.Purpose != CapturePurpose.FilmBaseReference).ToList() };
        using var sources = Sources();
        _ = await ReferenceReconstruction.ReconstructAsync(sources, roll, frame, new() { FilmKind = FilmKind.Positive },
            false, FrameSources.FullResolution, default);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sources.Open(roll, frame).GetImageAsync(1600, cancel.Token));
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
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli);
        await sources.Open(roll, frame).GetImageAsync(1600, default, cachedProgress);
        Assert.Contains(cachedProgress.Events, p => p.Stage == "Loading cached RAW");
        Assert.DoesNotContain(cachedProgress.Events, p => p.Stage == "Decoding RAW");
        using var image = System.Drawing.Image.FromFile(result.ImagePath);
        Assert.InRange(Math.Max(image.Width, image.Height), 1, 1600);
        Assert.True(File.Exists(result.SourceImagePath));
        Assert.Contains(result.Warnings, w => w.Contains("Provisional"));
    }

    [RawFact("FILMLIGHT_TEST_SPROCKET_RAF")]
    public async Task RealSprocketsWithOutOfRangeBacklightStillProduceFrameLocalMarkerAndFilmBase()
    {
        var frame = Capture(CapturePurpose.Frame, Environment.GetEnvironmentVariable("FILMLIGHT_TEST_SPROCKET_RAF")!,
            DateTimeOffset.UtcNow);
        var roll = new RollDocument { FilmKind = FilmKind.ColorNegative, Captures = [frame] };
        var recipe = await FrameCalibration.AutoSampleAsync(roll, frame,
            new() { FilmKind = roll.FilmKind, FrameId = frame.Id }, root, Cli, default);
        Assert.True(recipe.PictureCalibration!.IsComplete(roll.FilmKind));
        Assert.True(recipe.PictureCalibration.Light!.IsClipped);
        Assert.Equal(frame.Id, recipe.PictureCalibration.Light.FrameId);
        Assert.Equal(frame.Id, recipe.PictureCalibration.FilmBase!.FrameId);
        Assert.False(recipe.PictureCalibration.FilmBase.IsClipped);
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
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli);
        var image = await sources.Open(roll, captures[2]).GetImageAsync(FrameSources.FullResolution, default);
        string output = Assert.Single(Directory.GetFiles(root, "linear.pfm", SearchOption.AllDirectories));
        string firstReceipt = Path.Combine(Path.GetDirectoryName(output)!, "receipt.json");
        Assert.Equal(2, Directory.GetFiles(root, "receipt.json", SearchOption.AllDirectories).Length);
        Assert.Null(JsonNode.Parse(await File.ReadAllTextAsync(firstReceipt))!["Sha256"]);
        Assert.Equal(originals, JsonSerializer.Serialize(roll));
        Assert.Equal(before, await Hash(raw));
        Assert.NotEqual(raw, output);
        Assert.All(captures, c => Assert.Equal(raw, c.Files[1].Path));
        string proxy = Assert.Single(Directory.GetFiles(root, "linear.tif", SearchOption.AllDirectories));
        CameraInputDecoder.ValidateFloatProfile(proxy);
        using (var tif = Tiff.Open(proxy, "r"))
            Assert.NotNull(tif.GetField(TiffTag.EXIFIFD));
        Assert.True(image.Width > 100 && image.Height > 100);
        Assert.True(image.Pixels.Max() > .01);
        int commands = Directory.GetFiles(root, "decode.command.txt", SearchOption.AllDirectories).Length;
        Assert.Equal(2, commands);
        using (var pfm = File.OpenRead(output))
        {
            for (int line = 0; line < 3; line++)
                while (pfm.ReadByte() is var value && value != '\n')
                    Assert.NotEqual(-1, value);
            long start = pfm.Position;
            byte[] sample = new byte[4];
            foreach (int y in new[] { 0, image.Height / 2, image.Height - 1 })
            foreach (int x in new[] { 0, image.Width / 2, image.Width - 1 })
            for (int channel = 0; channel < 3; channel++)
            {
                pfm.Position = start + (((long)image.Height - 1 - y) * image.Width * 3 + x * 3 + channel) * 4;
                pfm.ReadExactly(sample);
                Assert.Equal(BinaryPrimitives.ReadSingleLittleEndian(sample), image.Pixels[(y * image.Width + x) * 3 + channel]);
            }
        }
        var again = await sources.Open(roll, captures[2]).GetImageAsync(FrameSources.FullResolution, default);
        Assert.Equal(image.Pixels, again.Pixels);
        Assert.Equal(commands, Directory.GetFiles(root, "decode.command.txt", SearchOption.AllDirectories).Length);

        string receiptPath = firstReceipt;
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!;
        receipt["Sha256"] = "forensic hash is deliberately not consulted on reads";
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        using (var lockedRaw = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var disk = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli))
            Assert.Equal(image.Pixels, (await disk.Open(roll, captures[2]).GetImageAsync(FrameSources.FullResolution, default)).Pixels);
        foreach (string field in new[] { "Length", "LastWriteUtcTicks", "Width", "Height" })
        {
            long value = receipt[field]!.GetValue<long>();
            receipt[field] = value + 1;
            await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
            using var invalid = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                invalid.Open(roll, captures[2]).GetImageAsync(FrameSources.FullResolution, default));
            receipt[field] = value;
        }
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        Assert.Equal(commands, Directory.GetFiles(root, "decode.command.txt", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.GetDirectories(root, "decode-*.partial", SearchOption.AllDirectories));

        receipt["Exposure"]!["Iso"] = receipt["Exposure"]!["Iso"]!.GetValue<int>() + 1;
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        using (var invalid = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli))
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                invalid.Open(roll, captures[2]).GetImageAsync(FrameSources.FullResolution, default));
        receipt["Exposure"]!["Iso"] = receipt["Exposure"]!["Iso"]!.GetValue<int>() - 1;
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());

        long firstSample;
        byte[] originalSample = new byte[4];
        using (var file = File.OpenRead(output))
        {
            for (int line = 0; line < 3; line++)
                while (file.ReadByte() is var value && value != '\n')
                    Assert.NotEqual(-1, value);
            firstSample = file.Position;
            file.ReadExactly(originalSample);
        }
        foreach (float nonfinite in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            using (var writable = File.OpenWrite(output))
            {
                writable.Position = firstSample;
                byte[] sample = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(sample, nonfinite);
                writable.Write(sample);
            }
            await RejectPfmAsync();
        }
        using (var writable = File.OpenWrite(output))
        {
            writable.Position = firstSample;
            writable.Write(originalSample);
        }
        byte[] header = new byte[checked((int)firstSample)];
        using (var file = File.OpenRead(output)) file.ReadExactly(header);
        int scaleDigit = Array.LastIndexOf(header, (byte)'\n', header.Length - 2) + 2;
        foreach (var (offset, value) in new[] { (0, (byte)'X'), (scaleDigit, (byte)'2') })
        {
            using (var writable = File.OpenWrite(output))
            {
                writable.Position = offset;
                writable.WriteByte(value);
            }
            await RejectPfmAsync();
            using var restore = File.OpenWrite(output);
            restore.Position = offset;
            restore.WriteByte(header[offset]);
        }
        File.SetLastWriteTimeUtc(output, new DateTime(receipt["LastWriteUtcTicks"]!.GetValue<long>(), DateTimeKind.Utc));

        await File.AppendAllTextAsync(output, "cache corruption");
        using var reopened = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli);
        Assert.NotNull(await reopened.Open(roll, captures[2]).GetExposureAsync(default));
        Assert.NotNull(await sources.Open(roll, captures[2]).GetExposureAsync(default));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            reopened.Open(roll, captures[2]).GetImageAsync(FrameSources.FullResolution, default));
        Assert.Contains("Corrupt", error.Message);

        async Task RejectPfmAsync()
        {
            File.SetLastWriteTimeUtc(output, new DateTime(receipt["LastWriteUtcTicks"]!.GetValue<long>(), DateTimeKind.Utc));
            using var invalid = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                invalid.Open(roll, captures[2]).GetImageAsync(FrameSources.FullResolution, default));
        }
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
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli);
        var image = await sources.Open(roll, captures[2]).GetImageAsync(FrameSources.FullResolution, default);
        Assert.Equal(before, await Hash(raw));
        Assert.Equal(2, Directory.GetFiles(root, "receipt.json", SearchOption.AllDirectories).Length);
        string decoded = Assert.Single(Directory.GetFiles(root, "linear.pfm", SearchOption.AllDirectories));
        string directory = Path.GetDirectoryName(decoded)!;
        CameraInputDecoder.ValidateHistory(File.ReadAllText(Path.Combine(directory, "decode.stdout.log")),
            File.ReadAllText(Path.Combine(directory, "decode.stderr.log")), raw: true);
        Assert.All(image.Pixels, value => Assert.True(float.IsFinite(value)));
        Assert.Contains(image.Pixels, value => value < 0);
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
        using var sources = Sources();
        Assert.Equal(-.015f, (await sources.Open(roll, frame).GetImageAsync(FrameSources.FullResolution, default)).Pixels[0]);
    }

    [Fact]
    public async Task ProxyIsAreaAveragedFullIsUnchangedAndReturnedImagesCannotCorruptTheCache()
    {
        var (roll, frame) = QualifiedRoll(demo: true);
        const int width = 3072, height = 1536;
        var pixels = new float[width * height * 3];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        for (int c = 0; c < 3; c++)
            pixels[(y * width + x) * 3 + c] = x % 2 == 0 ? .2f : .8f;
        ProcessingImages.WriteTiff(frame.Files[0].Path, new(width, height, pixels), true,
            ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel, default, ProcessingImages.LinearRec2020Profile());
        using var sources = Sources();
        var source = sources.Open(roll, frame);
        var progress = new RecordingProcessingProgress();
        var proxy = await source.GetImageAsync(1600, default, progress);
        Assert.Equal(2560, proxy.Width);
        Assert.Equal(1280, proxy.Height);
        Assert.Equal(.3f, proxy.Pixels[0], 5);
        Assert.Equal(.6f, proxy.Pixels[3], 5);
        proxy.Pixels[0] = 123;
        var again = await source.GetImageAsync(1600, default, progress);
        Assert.Equal(.3f, again.Pixels[0], 5);
        Assert.Equal(proxy.Width, again.Width);
        Assert.Single(progress.Events, p => p.Stage == "Reading linear input");
        var full = await source.GetImageAsync(FrameSources.FullResolution, default);
        Assert.Equal(width, full.Width);
        Assert.Equal(height, full.Height);
        Assert.Equal(pixels, full.Pixels);
        full.Pixels[0] = 456;
        Assert.Equal(.2f, (await source.GetImageAsync(FrameSources.FullResolution, default)).Pixels[0]);
        var stats = await source.MeasureAsync(.5, .5, 2, default);
        Assert.Equal(width, stats.SourceWidth);
        Assert.Equal(height, stats.SourceHeight);
        Assert.Equal(25, stats.PixelCount);
        Assert.Equal(.44, stats.Mean[0], 6);
        Assert.Equal(.2f, stats.Minimum[0]);
        Assert.Equal(.8f, stats.Maximum[0]);
        Assert.False(stats.IsClipped);
        Assert.False(stats.IsDarkOrInvalid);
        Assert.Null(await source.GetExposureAsync(default));
    }

    [Fact]
    public async Task RgbCompositionSelectsOnlyTheMatchingCameraPrimary()
    {
        var (roll, frame) = QualifiedRoll();
        var channels = new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue };
        var files = channels.Select(c => new CaptureFile(c, Write(c + ".tif"))).ToArray();
        for (int c = 0; c < 3; c++)
        {
            var pixels = Enumerable.Repeat(9f, 4 * 3 * 3).ToArray();
            for (int i = c; i < pixels.Length; i += 3) pixels[i] = (c + 1) * .1f;
            ProcessingImages.WriteTiff(files[c].Path, new(4, 3, pixels), true, ProcessingImages.LinearLabel,
                default, ProcessingImages.LinearRec2020Profile());
        }
        frame = frame with { Mode = CaptureMode.Rgb, Files = files };
        roll.Captures[2] = frame;
        using var sources = Sources();
        var image = await sources.Open(roll, frame).GetImageAsync(1600, default);
        for (int i = 0; i < image.Pixels.Length; i++) Assert.Equal((i % 3 + 1) * .1f, image.Pixels[i]);
    }

    [Fact]
    public async Task QueuedProxiesHavePriorityAndCancelingOneWaiterDoesNotPoisonSharedWork()
    {
        var (roll, frame) = QualifiedRoll();
        using var sources = Sources();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        int reads = 0;
        var blockerProgress = new RecordingProcessingProgress(p =>
        {
            if (p.Stage != "Reading linear input") return;
            Interlocked.Increment(ref reads);
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Test did not release its decode queue.");
        });
        var source = sources.Open(roll, frame);
        var canceled = source.GetImageAsync(1600, cancel.Token, blockerProgress);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var survivor = source.GetImageAsync(1600, default);
        var order = new List<Guid?>();
        var progress = new RecordingProcessingProgress(p =>
        {
            if (p.Stage == "Reading linear input") order.Add(p.FrameId);
        });
        var queuedFull = sources.Open(roll, roll.Captures[0]).GetImageAsync(FrameSources.FullResolution, default, progress);
        var queuedProxy = sources.Open(roll, roll.Captures[1]).GetImageAsync(1600, default, progress);
        try
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        }
        finally { release.Set(); }
        await Task.WhenAll(survivor, queuedFull, queuedProxy);
        Assert.Equal(1, reads);
        Assert.Equal(new Guid?[] { roll.Captures[1].Id, roll.Captures[0].Id }, order);
        Assert.Equal(.2f, (await survivor).Pixels[0]);
    }

    [Fact]
    public async Task PatchStatisticsIncludeSensorEdgeClippingDarkAndSignedValues()
    {
        var (roll, frame) = QualifiedRoll();
        var image = new LinearImage(4, 3, Enumerable.Repeat(.2f, 4 * 3 * 3).ToArray());
        image.Pixels[0] = -.02f;
        image.Pixels[1] = 1.01f;
        ProcessingImages.WriteTiff(frame.Files[0].Path, image, true, ProcessingImages.LinearLabel,
            default, ProcessingImages.LinearRec2020Profile());
        using var sources = Sources();
        var stats = await sources.Open(roll, frame).MeasureAsync(0, 0, 2, default);
        Assert.Equal(9, stats.PixelCount);
        Assert.True(stats.IsClipped);
        Assert.True(stats.IsDarkOrInvalid);
        Assert.Equal(-.02f, stats.Minimum[0]);
        Assert.Equal(1.01f, stats.Maximum[1]);
    }

    [Fact]
    public async Task ManualPickUsesCropFractionsAndMeasuresAcrossCropEdges()
    {
        var (roll, frame) = QualifiedRoll();
        var pixels = Enumerable.Repeat(.2f, 4 * 3 * 3).ToArray();
        for (int y = 0; y < 3; y++) pixels[y * 4 * 3] = .4f;
        ProcessingImages.WriteTiff(frame.Files[0].Path, new(4, 3, pixels), true, ProcessingImages.LinearLabel,
            default, ProcessingImages.LinearRec2020Profile());
        var recipe = new DevelopRecipe { PictureCalibration = new(), CropLeft = .5, CropTop = .1, CropBottom = .2 };
        using var sources = Sources();
        var changed = await FrameCalibration.SampleAsync(sources, roll, frame, recipe,
            CalibrationTarget.Backlight, 0, .5, default);
        var sample = changed.PictureCalibration!.Light!;
        Assert.Equal(.5, sample.X);
        Assert.Equal(.45, sample.Y, 10);
        Assert.Equal(.25, sample.Red, 6);
        Assert.Equal(.2, sample.Green, 6);
        Assert.Equal(4, sample.SourceWidth);
        Assert.Equal(3, sample.SourceHeight);
    }

    [RawFact("FILMLIGHT_TEST_RAF", "FILMLIGHT_TEST_OTHER_RAF")]
    public async Task DifferentFrameExposuresDoNotConflictWhenEachUsesItsOwnSamples()
    {
        var frame = Capture(CapturePurpose.Frame, Environment.GetEnvironmentVariable("FILMLIGHT_TEST_RAF")!, DateTimeOffset.UtcNow);
        var origin = Capture(CapturePurpose.Frame, Environment.GetEnvironmentVariable("FILMLIGHT_TEST_OTHER_RAF")!, frame.CapturedAt.AddSeconds(-1));
        var roll = new RollDocument { Captures = [frame, origin] };
        string cache = Path.Combine(root, "camera-input-cache");
        string originReceipt;
        using (var sources = new FrameSources(cache, Cli))
        {
            await sources.Open(roll, origin).GetImageAsync(1600, default);
            originReceipt = Assert.Single(Directory.GetFiles(root, "receipt.json", SearchOption.AllDirectories));
            await sources.Open(roll, frame).GetImageAsync(1600, default);
        }
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(originReceipt))!;
        receipt["Exposure"]!["Iso"] = receipt["Exposure"]!["Iso"]!.GetValue<int>() + 1;
        await File.WriteAllTextAsync(originReceipt, receipt.ToJsonString());
        using var reopened = new FrameSources(cache, Cli);
        var recipe = new DevelopRecipe { PictureCalibration = new() { Light = new(origin.Id, .5, .5, .8, .7, .6) } };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReferenceReconstruction.ReconstructAsync(reopened, roll, frame, recipe, false, 1600, default));
        Assert.Contains("this frame", error.Message);
        foreach (var picture in new[] { frame, origin })
        {
            var local = new DevelopRecipe
            {
                FrameId = picture.Id, PictureCalibration = new() { Light = new(picture.Id, .5, .5, .8, .7, .6) }
            };
            Assert.NotNull(await ReferenceReconstruction.ReconstructAsync(reopened, roll, picture, local, false, 1600, default));
        }
        var commands = Directory.GetFiles(root, "decode.command.txt", SearchOption.AllDirectories);
        Assert.Equal(2, commands.Length);
        Assert.All(commands, path => Assert.Contains("--width" + Environment.NewLine + "2560", File.ReadAllText(path)));
        Assert.Empty(Directory.GetDirectories(root, "decode-*.partial", SearchOption.AllDirectories));
    }

    [CliFact]
    public async Task VersionCheckIsSharedAndDoesNotLeavePartialJobs()
    {
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli);
        var versions = await Task.WhenAll(sources.GetVersionAsync(default), sources.GetVersionAsync(default));
        Assert.Equal(versions[0], versions[1]);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task FailedVersionCheckIsRetriedWithinTheSameInstance()
    {
        using var sources = Sources();
        var first = sources.GetVersionAsync(default);
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => first);
        var retry = sources.GetVersionAsync(default);
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => retry);
        Assert.NotSame(first, retry);
        Assert.False(Directory.Exists(root));
    }

    [CliFact]
    public async Task FailedRawDecodeRetainsDiagnosticsWithoutLeavingAPartialJob()
    {
        Directory.CreateDirectory(root);
        string raw = Path.Combine(root, "invalid.RAF");
        await File.WriteAllTextAsync(raw, "This is intentionally not a camera RAW.");
        var frame = Capture(CapturePurpose.Frame, raw, DateTimeOffset.UtcNow);
        var roll = new RollDocument { Captures = [frame] };
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            sources.Open(roll, frame).GetImageAsync(1600, default));
        Assert.Contains("no qualified input was published", error.Message);
        Assert.Empty(Directory.GetDirectories(root, "decode-*.partial", SearchOption.AllDirectories));
        string failed = Assert.Single(Directory.GetDirectories(root, "decode-*.failed", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(failed, "failure.txt")));
        Assert.True(File.Exists(Path.Combine(failed, "decode.command.txt")));
        Assert.Empty(Directory.GetFiles(root, "receipt.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MemoryCacheEvictsTheOldestProxyAndRetainsOnlyOneFullCapture()
    {
        var frames = Enumerable.Range(0, 9).Select(i =>
            Capture(CapturePurpose.Frame, Write($"lru-{i}.tif"), DateTimeOffset.UtcNow)).ToList();
        var roll = new RollDocument { Captures = frames };
        using var sources = Sources();
        var progress = new RecordingProcessingProgress();
        foreach (var frame in frames) await sources.Open(roll, frame).GetImageAsync(1600, default, progress);
        await sources.Open(roll, frames[1]).GetImageAsync(1600, default, progress);
        Assert.Equal(9, progress.Events.Count);
        await sources.Open(roll, frames[0]).GetImageAsync(1600, default, progress);
        Assert.Equal(10, progress.Events.Count);
        await sources.Open(roll, frames[0]).GetImageAsync(FrameSources.FullResolution, default, progress);
        await sources.Open(roll, frames[1]).GetImageAsync(FrameSources.FullResolution, default, progress);
        await sources.Open(roll, frames[0]).GetImageAsync(FrameSources.FullResolution, default, progress);
        Assert.Equal(13, progress.Events.Count);
    }

    [Fact]
    public void ConstructionRemovesOnlyOldPartialJobsAndBoundsFailedDiagnostics()
    {
        string cache = Path.Combine(root, "camera-input-cache");
        string old = Path.Combine(cache, "decode-old.partial"), recent = Path.Combine(cache, "decode-recent.partial");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(recent);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        for (int i = 0; i < 12; i++)
        {
            string failed = Path.Combine(cache, $"decode-{i}.failed");
            Directory.CreateDirectory(failed);
            Directory.SetLastWriteTimeUtc(failed, DateTime.UtcNow.AddMinutes(-i));
        }
        using var sources = Sources();
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.Equal(10, Directory.GetDirectories(cache, "decode-*.failed").Length);
        Assert.False(Directory.Exists(Path.Combine(cache, "decode-11.failed")));
    }

    [Theory]
    [InlineData("partial", false)]
    [InlineData("partial", true)]
    [InlineData("failed", false)]
    [InlineData("failed", true)]
    public async Task ResumedPreviewIgnoresInaccessibleAbandonedDecodeJobs(string extension, bool locked)
    {
        var (roll, frame) = QualifiedRoll();
        string cache = Path.Combine(root, "camera-input-cache");
        string abandoned = Path.Combine(cache, $"decode-abandoned.{extension}");
        Directory.CreateDirectory(abandoned);
        string input = Path.Combine(abandoned, "input.RAF");
        File.WriteAllText(input, "abandoned working copy");
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-2));
        if (extension == "failed")
            for (int i = 0; i < 10; i++)
                Directory.CreateDirectory(Path.Combine(cache, $"decode-newer-{i}.failed"));
        string removable = Path.Combine(cache, $"decode-removable.{extension}");
        Directory.CreateDirectory(removable);
        Directory.SetLastWriteTimeUtc(removable, DateTime.UtcNow.AddHours(-3));
        using var held = locked ? new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var attributes = File.GetAttributes(abandoned);
        if (!locked) File.SetAttributes(abandoned, attributes | FileAttributes.ReadOnly);
        try
        {
            using var sources = Sources();
            var image = await sources.Open(roll, frame).GetImageAsync(1600, default);
            Assert.Equal(4, image.Width);
            Assert.True(Directory.Exists(abandoned));
            if (locked) Assert.True(File.Exists(input));
            Assert.False(Directory.Exists(removable));
        }
        finally
        {
            if (Directory.Exists(abandoned)) File.SetAttributes(abandoned, attributes);
        }
    }

    [RawFact("FILMLIGHT_TEST_RAF")]
    public async Task RealRafInputsReachNegadoctorAndValidatedPositiveExports()
    {
        string raw = Environment.GetEnvironmentVariable("FILMLIGHT_TEST_RAF")!;
        var time = DateTimeOffset.UtcNow;
        var captures = new[] { CapturePurpose.LightReference, CapturePurpose.FilmBaseReference, CapturePurpose.Frame }
            .Select((purpose, i) => Capture(purpose, raw, time.AddSeconds(i))).ToList();
        var roll = new RollDocument { Captures = captures };
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli);
        var decoded = await sources.Open(roll, captures[2]).GetImageAsync(FrameSources.FullResolution, default);
        var crop = UsableCrop(decoded);
        var recipe = new DevelopRecipe
        {
            FilmKind = FilmKind.ColorNegative,
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

    private static (double Left, double Top, double Right, double Bottom) UsableCrop(LinearImage image)
    {
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
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            sources.Open(roll, frame).GetImageAsync(FrameSources.FullResolution, default));
        Assert.Contains("LOSSLESS COMPRESSED", error.Message);
        Assert.Empty(Directory.GetFiles(root, "receipt.json", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(root, "decode.command.txt", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(root, "failure.txt", SearchOption.AllDirectories));
        string log = File.ReadAllText(Assert.Single(Directory.GetFiles(root, "decode.stdout.log", SearchOption.AllDirectories)));
        Assert.Contains("[rawspeed]", log);
        Assert.Contains("compressed RAF header check", log);
    }

    private FrameSources Sources() => new(Path.Combine(root, "camera-input-cache"), Path.Combine(root, "must-not-run.exe"));

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
        public RawFactAttribute(params string[] variables)
        {
            if (!File.Exists(Cli) || variables.Any(variable => !File.Exists(Environment.GetEnvironmentVariable(variable))))
                Skip = $"Requires darktable 5.6.2 and RAF fixtures via {string.Join(", ", variables)}.";
        }
    }
}
