using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BitMiracle.LibTiff.Classic;
using FilmLight.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace FilmLight.Tests;

[SupportedOSPlatform("windows")]
[Collection("Export encoding")]
public sealed class ExportEncodingTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FilmLight-export-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly string Cli = Environment.GetEnvironmentVariable("FILMLIGHT_TEST_DARKTABLE")
        ?? @"C:\Program Files\darktable\bin\darktable-cli.exe";

    [CliFact]
    public async Task SyntheticExportMatchesDarktableIncludingSignedOutOfGamutAndBoundaryValues()
    {
        Directory.CreateDirectory(root);
        const int width = 257, height = 129;
        var image = new LinearImage(width, height, new float[width * height * 3]);
        float[] boundaries = [-.1f, 0, .000001f, .0001f, .001f, .0031308f, .01f, .18f, .5f, .9999f, 1, 1.1f];
        var random = new Random(582);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        for (int c = 0; c < 3; c++)
            image.Pixels[(y * width + x) * 3 + c] = y < 24
                ? boundaries[(x / 8 + c * (y / 8)) % boundaries.Length]
                : y < 64 ? (float)random.NextDouble() * 1.2f - .1f
                : (x + c * y / 3f) / (width + height / 3f);
        await Baseline(root, image, new());
        Compare(image, root);
    }

    [Fact]
    public void ExportEncodingHandlesOddGeometryAndRejectsCancellationAndNonfiniteSamples()
    {
        Directory.CreateDirectory(root);
        var image = new LinearImage(3, 2, Enumerable.Repeat(.2f, 18).ToArray());
        string tiff = Path.Combine(root, "master.tif"), jpeg = Path.Combine(root, "preview.jpg");
        ProcessingImages.WriteProPhotoTiff(tiff, image, true, default);
        ProcessingImages.WriteExportJpeg(jpeg, image, default);
        ProcessingImages.ValidateMaster(tiff, 3, 2, true, default);
        ProcessingImages.ValidateJpeg(jpeg, 3, 2, true);
        Assert444(jpeg);
        using (var tif = Tiff.Open(tiff, "r"))
            Assert.Contains(ProcessingImages.SyntheticLabel, tif.GetField(TiffTag.IMAGEDESCRIPTION)[0].ToString());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ProcessingImages.WriteProPhotoTiff(tiff, image, false, cancel.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ProcessingImages.WriteExportJpeg(jpeg, image, cancel.Token));
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            image.Pixels[0] = invalid;
            Assert.Throws<InvalidDataException>(() => ProcessingImages.WriteProPhotoTiff(tiff, image, false, default));
            Assert.Throws<InvalidDataException>(() => ProcessingImages.WriteExportJpeg(jpeg, image, default));
        }
    }

    [RafFact]
    public async Task RealRafExportMatchesDarktableAndProfilesBeforeAndAfter()
    {
        Directory.CreateDirectory(root);
        var frame = new RollCapture(Guid.NewGuid(), 1, CapturePurpose.Frame, CaptureMode.White,
            [new(FilmLight.Core.LightChannel.White, Environment.GetEnvironmentVariable("FILMLIGHT_TEST_RAF")!)], false, DateTimeOffset.UtcNow);
        var roll = new RollDocument { Captures = [frame] };
        var recipe = new DevelopRecipe { FrameId = frame.Id, PictureCalibration = new() };
        using var sources = new FrameSources(Path.Combine(root, "camera-cache"), Cli);
        var clock = Stopwatch.StartNew();
        await sources.Open(roll, frame).GetImageAsync(FrameSources.FullResolution, default);
        output.WriteLine($"Cold full-resolution source: {clock.Elapsed.TotalSeconds:F3}s");
        clock.Restart();
        LinearImage? positiveImage = null;
        var result = await ProcessingPublication.RunAsync(root, frame, recipe, false, [], DevelopIntent.Export,
            async staging =>
            {
                var context = new DevelopContext(roll, frame, recipe, DevelopIntent.Export, sources, staging, new ProgressRecorder(output));
                var state = new DevelopState(new(0, 0, []), [1, 1, 1], recipe);
                foreach (var step in new IDevelopStep[] { new ReconstructStep(false), new DarktableInversionStep(Cli), new DevelopControlsStep() })
                {
                    var stepClock = Stopwatch.StartNew();
                    state = await step.ApplyAsync(context, state, default);
                    output.WriteLine($"{step.GetType().Name}: {stepClock.Elapsed.TotalSeconds:F3}s");
                }
                var encodeClock = Stopwatch.StartNew();
                await Baseline(staging, state.Image, recipe);
                output.WriteLine($"Baseline encoding total: {encodeClock.Elapsed.TotalSeconds:F3}s");
                positiveImage = state.Image;
                return state.Image;
            }, new ProgressRecorder(output), default);
        output.WriteLine($"Baseline warm-source export: {clock.Elapsed.TotalSeconds:F3}s; {result.OutputFiles[0]}");
        Compare(positiveImage!, Path.GetDirectoryName(result.OutputFiles[0])!);
        clock.Restart();
        var after = await new DarktableFrameProcessor(Cli, sources).ExportAsync(roll, frame, recipe,
            root, new ProgressRecorder(output), default);
        output.WriteLine($"In-process warm-source export: {clock.Elapsed.TotalSeconds:F3}s");
        string afterJob = Path.GetDirectoryName(after.OutputFiles[0])!;
        Assert.Single(Directory.GetFiles(afterJob, "*.command.txt"));
        Assert.False(File.Exists(Path.Combine(afterJob, "positive-linear.tif")));
        Assert.False(File.Exists(Path.Combine(afterJob, "output-positive.xmp")));
    }

    private async Task Baseline(string directory, LinearImage image, DevelopRecipe recipe)
    {
        var clock = Stopwatch.StartNew();
        string positive = Path.Combine(directory, "positive-linear.tif");
        ProcessingImages.WriteTiff(positive, image, true, ProcessingImages.LinearLabel, default, ProcessingImages.LinearRec2020Profile());
        output.WriteLine($"positive-linear.tif write: {clock.Elapsed.TotalSeconds:F3}s");
        string xmp = Path.Combine(directory, "output-positive.xmp");
        NegadoctorXmp.Write(xmp, recipe, [1, 1, 1], false);
        var conversion = new DarktableConversion(Cli);
        clock.Restart();
        await conversion.RunAsync(positive, xmp, Path.Combine(directory, "master.tif"), "PROPHOTO_RGB", 16, directory, "tiff", default);
        output.WriteLine($"TIFF encode launch: {clock.Elapsed.TotalSeconds:F3}s");
        clock.Restart();
        await conversion.RunAsync(positive, xmp, Path.Combine(directory, "preview.jpg"), "SRGB", 16, directory, "jpeg", default);
        output.WriteLine($"JPEG encode launch: {clock.Elapsed.TotalSeconds:F3}s");
        clock.Restart();
        ProcessingImages.ValidateMaster(Path.Combine(directory, "master.tif"), image.Width, image.Height, true, default);
        ProcessingImages.ValidateJpeg(Path.Combine(directory, "preview.jpg"), image.Width, image.Height, true);
        output.WriteLine($"Encode validation: {clock.Elapsed.TotalSeconds:F3}s");
    }

    private void Compare(LinearImage image, string baseline)
    {
        string master = Path.Combine(baseline, "inprocess.tif"), jpeg = Path.Combine(baseline, "inprocess.jpg");
        var clock = Stopwatch.StartNew();
        ProcessingImages.WriteProPhotoTiff(master, image, false, default);
        output.WriteLine($"In-process TIFF: {clock.Elapsed.TotalSeconds:F3}s");
        clock.Restart();
        ProcessingImages.WriteExportJpeg(jpeg, image, default);
        output.WriteLine($"In-process JPEG: {clock.Elapsed.TotalSeconds:F3}s");
        clock.Restart();
        Parallel.Invoke(
            () => ProcessingImages.WriteProPhotoTiff(master, image, false, default),
            () => ProcessingImages.WriteExportJpeg(jpeg, image, default));
        output.WriteLine($"In-process parallel TIFF + JPEG: {clock.Elapsed.TotalSeconds:F3}s");
        ProcessingImages.ValidateMaster(master, image.Width, image.Height, true, default);
        ProcessingImages.ValidateJpeg(jpeg, image.Width, image.Height, true);
        var actualTables = Assert444(jpeg);
        var expectedTables = Assert444(Path.Combine(baseline, "preview.jpg"));
        Assert.Equal(expectedTables.Keys.Order(), actualTables.Keys.Order());
        foreach (var (id, table) in expectedTables) Assert.Equal(table, actualTables[id]);
        using (var expected = Tiff.Open(Path.Combine(baseline, "master.tif"), "r"))
        using (var actual = Tiff.Open(master, "r"))
        {
            var expectedTags = Tags(expected.GetField(TiffTag.ICCPROFILE)[1].ToByteArray());
            var actualTags = Tags(actual.GetField(TiffTag.ICCPROFILE)[1].ToByteArray());
            foreach (string tag in new[] { "rXYZ", "gXYZ", "bXYZ", "wtpt" })
            for (int i = 0; i < 3; i++)
                Assert.InRange(Math.Abs(BinaryPrimitives.ReadInt32BigEndian(expectedTags[tag].AsSpan(8 + i * 4)) / 65536.0 -
                    BinaryPrimitives.ReadInt32BigEndian(actualTags[tag].AsSpan(8 + i * 4)) / 65536.0), 0, .0001);
            foreach (string tag in new[] { "rTRC", "gTRC", "bTRC" })
            {
                Assert.Equal("curv", System.Text.Encoding.ASCII.GetString(expectedTags[tag], 0, 4));
                Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(expectedTags[tag].AsSpan(8)));
                Assert.InRange(Math.Abs(BinaryPrimitives.ReadUInt16BigEndian(expectedTags[tag].AsSpan(12)) / 256.0 -
                    BinaryPrimitives.ReadUInt16BigEndian(actualTags[tag].AsSpan(12)) / 256.0), 0, .0001);
            }

            Assert.Equal((int)Compression.NONE, actual.GetFieldDefaulted(TiffTag.COMPRESSION)[0].ToInt());
            var histogram = Histograms(65536);
            byte[] a = new byte[actual.ScanlineSize()], b = new byte[expected.ScanlineSize()];
            for (int y = 0; y < image.Height; y++)
            {
                Assert.True(actual.ReadScanline(a, y)); Assert.True(expected.ReadScanline(b, y));
                for (int i = 0; i < image.Width * 3; i++)
                    histogram[i % 3][Math.Abs(BitConverter.ToUInt16(a, i * 2) - BitConverter.ToUInt16(b, i * 2))]++;
            }
            CheckErrors("TIFF", histogram, 2, 8, 64, true);
        }
        using var expectedJpeg = new Bitmap(Path.Combine(baseline, "preview.jpg"));
        using var actualJpeg = new Bitmap(jpeg);
        var rectangle = new Rectangle(0, 0, image.Width, image.Height);
        var left = expectedJpeg.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var right = actualJpeg.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var histogram = Histograms(256);
            byte[] a = new byte[image.Width * 3], b = new byte[a.Length];
            for (int y = 0; y < image.Height; y++)
            {
                Marshal.Copy(left.Scan0 + y * left.Stride, a, 0, a.Length);
                Marshal.Copy(right.Scan0 + y * right.Stride, b, 0, b.Length);
                for (int i = 0; i < a.Length; i++) histogram[2 - i % 3][Math.Abs(a[i] - b[i])]++;
            }
            CheckErrors("JPEG", histogram, .75, 3, 12, false);
        }
        finally { expectedJpeg.UnlockBits(left); actualJpeg.UnlockBits(right); }
    }

    private static long[][] Histograms(int size) => [new long[size], new long[size], new long[size]];

    private void CheckErrors(string name, long[][] histograms, double meanLimit, int p99Limit, int maxLimit, bool perChannel)
    {
        long[] combined = new long[histograms[0].Length];
        for (int i = 0; i < combined.Length; i++) combined[i] = histograms.Sum(h => h[i]);
        for (int c = 0; c < 4; c++)
        {
            long[] histogram = c == 3 ? combined : histograms[c];
            long count = histogram.Sum(), sum = 0, cumulative = 0;
            int p99 = -1, max = 0;
            for (int i = 0; i < histogram.Length; i++)
            {
                long n = histogram[i]; sum += i * n; cumulative += n;
                if (n != 0) max = i;
                if (p99 < 0 && cumulative >= Math.Ceiling(count * .99)) p99 = i;
            }
            double mean = (double)sum / count;
            string channel = c == 3 ? "combined" : "RGB"[c].ToString();
            output.WriteLine($"{name} {channel} mean={mean:F6}, p99={p99}, max={max}");
            if (perChannel || c == 3)
                Assert.True(mean <= meanLimit && p99 <= p99Limit && max <= maxLimit,
                    $"{name} {channel}: mean={mean}, p99={p99}, max={max}");
        }
    }

    private static Dictionary<int, byte[]> Assert444(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var tables = new Dictionary<int, byte[]>();
        bool baseline = false, profile = false;
        Assert.Equal(0xff, bytes[0]); Assert.Equal(0xd8, bytes[1]);
        for (int offset = 2; offset + 4 < bytes.Length;)
        {
            Assert.Equal(0xff, bytes[offset++]);
            int marker = bytes[offset++];
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));
            Assert.InRange(length, 2, bytes.Length - offset);
            if (marker == 0xc0)
            {
                Assert.Equal(8, bytes[offset + 2]);
                Assert.Equal(3, bytes[offset + 7]);
                for (int c = 0; c < 3; c++) Assert.Equal(0x11, bytes[offset + 9 + c * 3]);
                baseline = true;
            }
            if (marker == 0xe2 && bytes.AsSpan(offset + 2, length - 2).StartsWith("ICC_PROFILE\0"u8))
                profile = true;
            if (marker == 0xdb)
                for (int i = offset + 2; i < offset + length; i += 65)
                {
                    Assert.Equal(0, bytes[i] >> 4);
                    tables.Add(bytes[i] & 15, bytes.AsSpan(i + 1, 64).ToArray());
                }
            if (marker == 0xda)
            {
                Assert.True(baseline, "JPEG has no baseline SOF0.");
                Assert.True(profile, "JPEG has no ICC APP2.");
                Assert.NotEmpty(tables);
                return tables;
            }
            offset += length;
        }
        Assert.Fail("JPEG has no baseline SOF0.");
        return tables;
    }

    private static Dictionary<string, byte[]> Tags(byte[] profile)
    {
        var tags = new Dictionary<string, byte[]>();
        int count = BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(128));
        for (int i = 0; i < count; i++)
        {
            var entry = profile.AsSpan(132 + i * 12, 12);
            int offset = BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
            int size = BinaryPrimitives.ReadInt32BigEndian(entry[8..]);
            tags.Add(System.Text.Encoding.ASCII.GetString(entry[..4]), profile.AsSpan(offset, size).ToArray());
        }
        return tags;
    }

    private sealed class ProgressRecorder(ITestOutputHelper output) : IProgress<ProcessingProgress>
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        public void Report(ProcessingProgress value) => output.WriteLine($"Progress {clock.Elapsed.TotalSeconds:F3}s: {value.Stage}");
    }

    private sealed class RafFactAttribute : FactAttribute
    {
        public RafFactAttribute()
        {
            if (!File.Exists(Cli) || !File.Exists(Environment.GetEnvironmentVariable("FILMLIGHT_TEST_RAF")))
                Skip = "Requires installed darktable and FILMLIGHT_TEST_RAF.";
        }
    }

    private sealed class CliFactAttribute : FactAttribute
    {
        public CliFactAttribute()
        {
            if (!File.Exists(Cli)) Skip = "Requires installed darktable.";
        }
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("FILMLIGHT_KEEP_TEST_ARTIFACTS") != "1" && Directory.Exists(root))
            Directory.Delete(root, true);
    }
}

[CollectionDefinition("Export encoding", DisableParallelization = true)]
public sealed class ExportEncodingCollection;
