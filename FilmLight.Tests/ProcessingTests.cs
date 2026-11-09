using System.Drawing;
using System.Runtime.Versioning;
using BitMiracle.LibTiff.Classic;
using FilmLight.Core;
using FilmLight.Workflow;
using Xunit;

namespace FilmLight.Tests;

[SupportedOSPlatform("windows")]
public sealed class ProcessingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FilmLight-processing-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly string Cli = Environment.GetEnvironmentVariable("FILMLIGHT_TEST_DARKTABLE")
        ?? @"C:\Program Files\darktable\bin\darktable-cli.exe";

    [Fact]
    public async Task SyntheticTripletIsDecodableDistinctAndCallsEachLight()
    {
        var source = new DemoCaptureSource();
        var lights = new List<LightChannel>();
        var files = await source.CaptureAsync(CapturePurpose.Frame, CaptureMode.Rgb, 7, root,
            (channel, _) => { lights.Add(channel); return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(source.IsDemo);
        Assert.Contains("SYNTHETIC", source.Description);
        Assert.Equal([LightChannel.Red, LightChannel.Green, LightChannel.Blue], lights);
        Assert.Equal(lights, files.Select(f => f.Channel));
        foreach (var file in files)
        {
            Assert.Contains(file.Channel.ToString(), Path.GetFileName(file.Path));
            using var tif = Tiff.Open(file.Path, "r");
            Assert.NotNull(tif);
            Assert.Equal(16, tif.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt());
            Assert.Contains("SYNTHETIC", tif.GetField(TiffTag.IMAGEDESCRIPTION)[0].ToString());
            using var windowsDecoded = Image.FromFile(file.Path);
            Assert.Equal(384, windowsDecoded.Width);
        }
        Assert.NotEqual(File.ReadAllBytes(files[0].Path), File.ReadAllBytes(files[1].Path));
        var next = await Capture(CapturePurpose.Frame, CaptureMode.Rgb, 8);
        Assert.NotEqual(File.ReadAllBytes(files[0].Path), File.ReadAllBytes(next.Files[0].Path));
    }

    [Fact]
    public async Task WhiteAndSelectedPrimaryReconstructionAgreeAndRemoveVignette()
    {
        var (whiteRoll, whiteFrame, recipe) = await Roll(CaptureMode.White);
        var (rgbRoll, rgbFrame, _) = await Roll(CaptureMode.Rgb);
        var processor = new DemoFrameProcessor();
        var white = await processor.ExportAsync(whiteRoll, whiteFrame, recipe, Path.Combine(root, "white"), null, default);
        var rgb = await processor.ExportAsync(rgbRoll, rgbFrame, recipe, Path.Combine(root, "rgb"), null, default);
        var a = Pixels(white.OutputFiles[0]);
        var b = Pixels(rgb.OutputFiles[0]);
        Assert.Equal(a.Length, b.Length);
        Assert.True(a.Zip(b).Max(pair => Math.Abs(pair.First - pair.Second)) < .001);
        Assert.True(a.Take(3).All(v => v < .001)); // The smooth vignette disappears even on the clear-film border.
        Assert.True(a.Max() > .5);
    }

    [Fact]
    public async Task CropExcludesDarkFilmHolderBordersFromReferenceValidationAndBaseMeasurement()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.White);
        foreach (var capture in roll.Captures)
        {
            var path = Assert.Single(capture.Files).Path;
            var rows = new List<byte[]>();
            string description;
            byte[] profile;
            using (var tif = Tiff.Open(path, "r"))
            {
                description = tif.GetField(TiffTag.IMAGEDESCRIPTION)[0].ToString();
                profile = tif.GetField(TiffTag.ICCPROFILE)[1].ToByteArray();
                for (int y = 0; y < 256; y++)
                {
                    var row = new byte[tif.ScanlineSize()];
                    Assert.True(tif.ReadScanline(row, y));
                    Array.Clear(row, 0, 30 * 3 * 2);
                    rows.Add(row);
                }
            }
            using var masked = Tiff.Open(path, "w");
            masked.SetField(TiffTag.IMAGEWIDTH, 384);
            masked.SetField(TiffTag.IMAGELENGTH, 256);
            masked.SetField(TiffTag.BITSPERSAMPLE, 16);
            masked.SetField(TiffTag.SAMPLESPERPIXEL, 3);
            masked.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
            masked.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            masked.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
            masked.SetField(TiffTag.IMAGEDESCRIPTION, description);
            masked.SetField(TiffTag.ICCPROFILE, profile.Length, profile);
            for (int y = 0; y < rows.Count; y++) Assert.True(masked.WriteScanline(rows[y], y));
            Assert.True(masked.WriteDirectory());
        }
        var processor = new DemoFrameProcessor();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            processor.PreviewAsync(roll, frame, recipe, Path.Combine(root, "uncropped"), null, default));
        var cropped = await processor.ExportAsync(roll, frame, recipe with { CropLeft = .1 },
            Path.Combine(root, "cropped"), null, default);
        using var master = Tiff.Open(cropped.OutputFiles[0], "r");
        Assert.Equal(346, master.GetField(TiffTag.IMAGEWIDTH)[0].ToInt());
        Assert.True(Pixels(cropped.OutputFiles[0]).Max() > .5);
    }

    [Fact]
    public void PreviewPointMappingRejectsLetterboxesAndMapsScaledImageCoordinates()
    {
        Assert.False(FrameCalibration.TryMapPoint(800, 600, 400, 200, 400, 50, out _, out _));
        Assert.True(FrameCalibration.TryMapPoint(800, 600, 400, 200, 200, 200, out var x, out var y));
        Assert.Equal(.25, x);
        Assert.Equal(.25, y);
        Assert.False(FrameCalibration.TryMapPoint(600, 800, 200, 400, 50, 400, out _, out _));
        Assert.True(FrameCalibration.TryMapPoint(600, 800, 200, 400, 300, 400, out x, out y));
        Assert.Equal(.5, x);
        Assert.Equal(.5, y);
        Assert.False(FrameCalibration.TryMapPoint(0, 600, 400, 200, 0, 0, out _, out _));
        Assert.False(FrameCalibration.TryMapPoint(800, 600, 400, 200, 800, 300, out _, out _));
    }

    [Theory]
    [InlineData(CaptureMode.White)]
    [InlineData(CaptureMode.Rgb)]
    public async Task BacklightPickerUsesLinearSourceChannelsRatherThanPositiveJpeg(CaptureMode mode)
    {
        var (roll, frame, recipe) = await Roll(mode);
        foreach (var file in frame.Files)
        {
            var image = ProcessingImages.ReadLinear(file.Path, default, true);
            var light = ProcessingImages.ReadLinear(roll.Captures.Single(c => c.Purpose == CapturePurpose.LightReference)
                .Files.Single(f => f.Channel == file.Channel).Path, default, true);
            for (int i = 0; i < image.Pixels.Length; i += 3)
            {
                image.Pixels[i] = light.Pixels[i] * .2f;
                image.Pixels[i + 1] = light.Pixels[i + 1] * .3f;
                image.Pixels[i + 2] = light.Pixels[i + 2] * .1f;
            }
            ProcessingImages.WriteTiff(file.Path, image, true,
                ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel, default,
                ProcessingImages.LinearRec2020Profile());
        }
        var changed = await FrameCalibration.SampleAsync(roll, frame, recipe,
            CalibrationTarget.Backlight, .5, .5, root, null, default);
        Assert.Equal(1.5, changed.RedGain, 5);
        Assert.Equal(1, changed.GreenGain);
        Assert.Equal(3, changed.BlueGain, 5);
        Assert.False(changed.Accepted);
        Assert.Equal(recipe.FilmBaseRed, changed.FilmBaseRed);
    }

    [Theory]
    [InlineData(CaptureMode.White)]
    [InlineData(CaptureMode.Rgb)]
    public async Task FilmBasePickerMapsCropAndReplacesPreviousMultipliersWithoutCompounding(CaptureMode mode)
    {
        var (roll, frame, recipe) = await Roll(mode);
        recipe = recipe with { CropLeft = .1, CropRight = .1, CropTop = .1, CropBottom = .1,
            FilmBaseRed = 1.3, FilmBaseGreen = .8, FilmBaseBlue = 1.2 };
        var changed = await FrameCalibration.SampleAsync(roll, frame, recipe,
            CalibrationTarget.FilmBase, .3, .4, root, null, default);
        var reconstruction = ReferenceReconstruction.Reconstruct(roll, frame, changed, true, default);
        var (left, top, width, height) = ReferenceReconstruction.Crop(reconstruction.Image, recipe);
        int px = left + (int)(.3 * width), py = top + (int)(.4 * height);
        for (int c = 0; c < 3; c++)
        {
            double average = 0;
            for (int y = py - 2; y <= py + 2; y++)
            for (int x = px - 2; x <= px + 2; x++)
                average += reconstruction.Image.Pixels[(y * reconstruction.Image.Width + x) * 3 + c] / 25;
            Assert.Equal(average, reconstruction.FilmBase[c], 5);
        }
        var repeated = await FrameCalibration.SampleAsync(roll, frame, changed,
            CalibrationTarget.FilmBase, .3, .4, root, null, default);
        Assert.Equal(changed, repeated);
        Assert.False(changed.Accepted);
        Assert.Equal(recipe.RedGain, changed.RedGain);
    }

    [Fact]
    public async Task PickerRejectsInvalidPointsClippedPatchesAndCancellation()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.White);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => FrameCalibration.SampleAsync(
            roll, frame, recipe, CalibrationTarget.FilmBase, double.NaN, .5, root, null, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => FrameCalibration.SampleAsync(
            roll, frame, recipe, CalibrationTarget.FilmBase, 1, .5, root, null, default));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FrameCalibration.SampleAsync(
            roll, frame, recipe, CalibrationTarget.Backlight, .5, .5, root, null, cancel.Token));
        var path = frame.Files[0].Path;
        var image = ProcessingImages.ReadLinear(path, default, true);
        for (int y = 124; y <= 132; y++)
        for (int x = 188; x <= 196; x++)
            image.Pixels[(y * image.Width + x) * 3] = 0;
        ProcessingImages.WriteTiff(path, image, true, ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel,
            default, ProcessingImages.LinearRec2020Profile());
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCalibration.SampleAsync(
            roll, frame, recipe, CalibrationTarget.Backlight, .5, .5, root, null, default));
    }

    [Fact]
    public async Task PreviewAndExportUseSameRecipeAndValidateJpegAnd16BitRgbTiff()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.White);
        var processor = new DemoFrameProcessor();
        var baseline = await processor.ExportAsync(roll, frame, recipe, Path.Combine(root, "baseline"), null, default);
        var edit = recipe with { Revision = 2, ExposureEv = .7, RedGain = 1.2, BlueGain = .8,
            FilmBaseGreen = 1.1, BlackPoint = .03, WhitePoint = .9, CropLeft = .1, CropBottom = .2 };
        var preview = await processor.PreviewAsync(roll, frame, edit, Path.Combine(root, "preview"), null, default);
        var export = await processor.ExportAsync(roll, frame, edit, Path.Combine(root, "export"), null, default);
        Assert.Contains(preview.Warnings, w => w.Contains("SYNTHETIC"));
        Assert.Contains("SYNTHETIC", preview.ImagePath);
        using var jpeg = Image.FromFile(preview.ImagePath);
        Assert.NotNull(preview.SourceImagePath);
        using var source = Image.FromFile(preview.SourceImagePath);
        Assert.Equal(jpeg.Width, source.Width);
        Assert.Equal(jpeg.Height, source.Height);
        Assert.NotEqual(File.ReadAllBytes(preview.ImagePath), File.ReadAllBytes(preview.SourceImagePath));
        Assert.Equal(346, jpeg.Width);
        Assert.Equal(205, jpeg.Height);
        Assert.Equal(File.ReadAllBytes(preview.ImagePath), File.ReadAllBytes(export.OutputFiles[1]));
        using var master = Tiff.Open(export.OutputFiles[0], "r");
        Assert.Equal(16, master.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt());
        Assert.Equal(3, master.GetField(TiffTag.SAMPLESPERPIXEL)[0].ToInt());
        Assert.NotEqual(Pixels(baseline.OutputFiles[0]).Average(), Pixels(export.OutputFiles[0]).Average());
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(preview.ImagePath)!, "recipe.json")));
        Assert.All(frame.Files, f => Assert.True(File.Exists(f.Path)));
    }

    [Fact]
    public async Task MissingReferencesAndRealInputsAreRejectedNotRenderedAsDemo()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.Rgb);
        var processor = new DemoFrameProcessor();
        var incomplete = roll with { Captures = roll.Captures.Where(c => c.Purpose != CapturePurpose.LightReference).ToList() };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.PreviewAsync(incomplete, frame, recipe, root, null, default));
        Assert.Contains("LightReference", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.PreviewAsync(roll with { IsDemo = false }, frame with { IsDemo = false }, recipe, root, null, default));
        var missingBase = roll with { Captures = roll.Captures.Where(c => c.Purpose != CapturePurpose.FilmBaseReference).ToList() };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.PreviewAsync(missingBase, frame, recipe, root, null, default));
        Assert.DoesNotContain(Directory.GetDirectories(root), d => File.Exists(Path.Combine(d, "SYNTHETIC-preview.jpg")));
    }

    [Fact]
    public async Task PositiveSkipsInversionAndBlackAndWhiteIsNeutral()
    {
        var (negative, frame, recipe) = await Roll(CaptureMode.White);
        var processor = new DemoFrameProcessor();
        var positive = negative with { FilmKind = FilmKind.Positive, Captures = negative.Captures.Where(c => c.Purpose != CapturePurpose.FilmBaseReference).ToList() };
        var pos = await processor.ExportAsync(positive, frame, recipe with { FilmKind = FilmKind.Positive }, root, null, default);
        var neg = await processor.ExportAsync(negative, frame, recipe, root, null, default);
        Assert.True(Pixels(pos.OutputFiles[0])[0] > .7);
        Assert.True(Pixels(neg.OutputFiles[0])[0] < .001);
        var bw = await processor.ExportAsync(negative with { FilmKind = FilmKind.BlackAndWhiteNegative }, frame,
            recipe with { FilmKind = FilmKind.BlackAndWhiteNegative }, root, null, default);
        var samples = Pixels(bw.OutputFiles[0]);
        for (int i = 0; i < samples.Length; i += 3)
        {
            Assert.Equal(samples[i], samples[i + 1]);
            Assert.Equal(samples[i], samples[i + 2]);
        }
    }

    [Theory]
    [InlineData("ExposureEv")]
    [InlineData("RedGain")]
    [InlineData("GreenGain")]
    [InlineData("BlueGain")]
    [InlineData("FilmBaseRed")]
    [InlineData("FilmBaseGreen")]
    [InlineData("FilmBaseBlue")]
    [InlineData("BlackPoint")]
    [InlineData("WhitePoint")]
    public async Task EachRecipeControlChangesTheRenderedSamples(string control)
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.Rgb);
        var edit = control switch
        {
            "ExposureEv" => recipe with { ExposureEv = .5 },
            "RedGain" => recipe with { RedGain = 1.3 },
            "GreenGain" => recipe with { GreenGain = 1.3 },
            "BlueGain" => recipe with { BlueGain = 1.3 },
            "FilmBaseRed" => recipe with { FilmBaseRed = 1.2 },
            "FilmBaseGreen" => recipe with { FilmBaseGreen = 1.2 },
            "FilmBaseBlue" => recipe with { FilmBaseBlue = 1.2 },
            "BlackPoint" => recipe with { BlackPoint = .1 },
            "WhitePoint" => recipe with { WhitePoint = .8 },
            _ => throw new ArgumentOutOfRangeException(nameof(control))
        };
        var processor = new DemoFrameProcessor();
        var baseline = await processor.ExportAsync(roll, frame, recipe, root, null, default);
        var changed = await processor.ExportAsync(roll, frame, edit, root, null, default);
        Assert.True(Pixels(baseline.OutputFiles[0]).Zip(Pixels(changed.OutputFiles[0]))
            .Max(pair => Math.Abs(pair.First - pair.Second)) > .01);
    }

    [Fact]
    public async Task IncompleteTripletMalformedProfileAndInvalidRecipesFail()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.Rgb);
        var processor = new DemoFrameProcessor();
        var missingBlue = frame with { Files = frame.Files.Take(2).ToArray() };
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.PreviewAsync(
            roll, missingBlue, recipe, root, null, default));
        // A syntactically valid TIFF with an sRGB/missing ICC is not a linear processing input.
        string invalid = Path.Combine(root, "unqualified.tif");
        WriteUnqualifiedTiff(invalid);
        var unqualified = frame with { Files = frame.Files.Select((f, index) => index == 0 ? f with { Path = invalid } : f).ToArray() };
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.PreviewAsync(
            roll, unqualified, recipe, root, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => processor.PreviewAsync(
            roll, frame, recipe with { ExposureEv = double.NaN }, root, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => processor.PreviewAsync(
            roll, frame, recipe with { CropLeft = .8, CropRight = .8 }, root, null, default));
    }

    [Fact]
    public async Task CancellationDuringTripletDoesNotPublishPartialCapture()
    {
        using var cancel = new CancellationTokenSource();
        var seen = new List<LightChannel>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DemoCaptureSource().CaptureAsync(
            CapturePurpose.Frame, CaptureMode.Rgb, 1, root, (channel, _) =>
            {
                seen.Add(channel);
                if (channel == LightChannel.Green) cancel.Cancel();
                return Task.CompletedTask;
            }, cancel.Token));
        Assert.Equal([LightChannel.Red, LightChannel.Green], seen);
        Assert.All(Directory.GetDirectories(root), directory => Assert.EndsWith(".partial", directory));
        var (roll, frame, recipe) = await Roll(CaptureMode.White);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DemoFrameProcessor().PreviewAsync(
            roll, frame, recipe, root, null, cancel.Token));
    }

    [Fact]
    public async Task MissingOrFailingCliNeverPublishesSuccess()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.White);
        string output = Path.Combine(root, "failed");
        await Assert.ThrowsAnyAsync<Exception>(() => new DarktableFrameProcessor(
            Path.Combine(root, "missing-darktable-cli.exe")).ExportAsync(roll, frame, recipe, output, null, default));
        string job = Assert.Single(Directory.GetDirectories(output));
        Assert.EndsWith(".partial", job);
        Assert.True(File.Exists(Path.Combine(job, "failure.txt")));

        // A real, harmless Windows process exits nonzero; no invented CLI success path.
        string failExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        await Assert.ThrowsAsync<IOException>(() => new DarktableFrameProcessor(failExe).ExportAsync(
            roll, frame, recipe, Path.Combine(root, "nonzero"), null, default));
        Assert.All(Directory.GetDirectories(Path.Combine(root, "nonzero")), directory =>
        {
            Assert.EndsWith(".partial", directory);
            Assert.True(File.Exists(Path.Combine(directory, "version.stderr.log")));
        });
    }

    [Fact]
    public Task CapturePreviewsAreBoundedWhileExportsKeepFullCroppedResolution() =>
        AssertBoundedPreview(new DemoFrameProcessor());

    [InstalledDarktableFact]
    public Task DarktableCapturePreviewsAreBoundedWhileExportsKeepFullCroppedResolution() =>
        AssertBoundedPreview(new DarktableFrameProcessor(Cli));

    private async Task AssertBoundedPreview(IFrameProcessor processor)
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.White);
        foreach (var capture in roll.Captures)
        {
            var path = capture.Files[0].Path;
            var source = ProcessingImages.ReadLinear(path, default, true);
            var large = new LinearImage(2200, 100, new float[2200 * 100 * 3]);
            for (int y = 0; y < large.Height; y++)
            for (int x = 0; x < large.Width; x++)
                Array.Copy(source.Pixels, (y * source.Width + x % source.Width) * 3,
                    large.Pixels, (y * large.Width + x) * 3, 3);
            ProcessingImages.WriteTiff(path, large, true, ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel,
                default, ProcessingImages.LinearRec2020Profile());
        }
        recipe = recipe with { CropLeft = .1, CropRight = .1, CropTop = .1, CropBottom = .1 };
        var preview = await processor.PreviewAsync(roll, frame, recipe, root, null, default);
        using var image = Image.FromFile(preview.ImagePath);
        using var sourcePreview = Image.FromFile(preview.SourceImagePath!);
        Assert.Equal(1600, image.Width);
        Assert.Equal(72, image.Height);
        Assert.Equal(image.Size, sourcePreview.Size);
        var export = await processor.ExportAsync(roll, frame, recipe, root, null, default);
        using var full = Image.FromFile(export.OutputFiles[1]);
        Assert.Equal(1760, full.Width);
        Assert.Equal(80, full.Height);
    }

    [InstalledDarktableFact]
    public async Task InstalledDarktableActuallyRunsNegadoctorAndEmbedsProfiles()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.Rgb);
        var result = await new DarktableFrameProcessor(Cli).ExportAsync(roll, frame,
            recipe with { ExposureEv = .4, CropLeft = .1 }, root, null, default);
        Assert.Equal(2, result.OutputFiles.Count);
        using var tif = Tiff.Open(result.OutputFiles[0], "r");
        Assert.Equal(16, tif.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt());
        Assert.True(tif.GetField(TiffTag.ICCPROFILE)[0].ToInt() >= 128);
        Assert.Equal(346, tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt());
        using var jpeg = Image.FromFile(result.OutputFiles[1]);
        Assert.Equal(346, jpeg.Width);
        Assert.Contains(0x8773, jpeg.PropertyIdList);
        string job = Path.GetDirectoryName(result.OutputFiles[0])!;
        Assert.Contains("negadoctor", File.ReadAllText(Path.Combine(job, "develop.stdout.log")));
        Assert.Contains("negadoctor", File.ReadAllText(Path.Combine(job, "develop.xmp")));
        Assert.Contains(result.Warnings, w => w.Contains("SYNTHETIC"));
        var developed = FloatPixels(Path.Combine(job, "developed-linear.tif"));
        Assert.True(developed.Skip(38 * 3).Take(3).Average() < .02); // Clear film inside the accepted crop becomes black.
        Assert.True(developed.Max() > .1);

        var calibrated = recipe with { XmpTemplatePath = Path.Combine(job, "develop.xmp"), FilmBaseRed = 1.1 };
        var preview = await new DarktableFrameProcessor(Cli).PreviewAsync(roll, frame, calibrated, root, null, default);
        string snapshot = Path.Combine(Path.GetDirectoryName(preview.ImagePath)!, "develop.xmp");
        Assert.NotEqual(File.ReadAllText(calibrated.XmpTemplatePath), File.ReadAllText(snapshot));
        Assert.Contains("negadoctor", File.ReadAllText(snapshot));
    }

    [InstalledDarktableFact]
    public async Task InstalledDarktablePositiveAndMonochromeAreNotNegativeFallbacks()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.White);
        var processor = new DarktableFrameProcessor(Cli);
        var slide = await processor.ExportAsync(roll with { FilmKind = FilmKind.Positive }, frame,
            recipe with { FilmKind = FilmKind.Positive }, root, null, default);
        string slideJob = Path.GetDirectoryName(slide.OutputFiles[0])!;
        Assert.DoesNotContain("negadoctor", File.ReadAllText(Path.Combine(slideJob, "develop.xmp")));
        var positive = FloatPixels(Path.Combine(slideJob, "developed-linear.tif"));
        Assert.True(positive[0] > .7);

        var monochrome = await processor.ExportAsync(roll with { FilmKind = FilmKind.BlackAndWhiteNegative }, frame,
            recipe with { FilmKind = FilmKind.BlackAndWhiteNegative }, root, null, default);
        string bwJob = Path.GetDirectoryName(monochrome.OutputFiles[0])!;
        var bw = FloatPixels(Path.Combine(bwJob, "developed-linear.tif"));
        Assert.True(bw.Take(3).Average() < .02);
        for (int i = 0; i < bw.Length; i += 3)
            Assert.True(Math.Abs(bw[i] - bw[i + 1]) < .001 && Math.Abs(bw[i] - bw[i + 2]) < .001);
    }

    [InstalledDarktableFact]
    public async Task InstalledDarktableCancellationLeavesDiagnosticsAndNoPublishedPair()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.Rgb);
        string output = Path.Combine(root, "cancel-cli");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DarktableFrameProcessor(Cli)
            .ExportAsync(roll, frame, recipe, output, null, cancel.Token));
        string job = Assert.Single(Directory.GetDirectories(output));
        Assert.EndsWith(".partial", job);
        Assert.True(File.Exists(Path.Combine(job, "failure.txt")));
    }

    private async Task<(RollDocument Roll, RollCapture Frame, DevelopRecipe Recipe)> Roll(CaptureMode mode)
    {
        var light = await Capture(CapturePurpose.LightReference, mode, 0);
        var basis = await Capture(CapturePurpose.FilmBaseReference, mode, 0);
        var frame = await Capture(CapturePurpose.Frame, mode, 7);
        return (new RollDocument { IsDemo = true, FilmKind = FilmKind.ColorNegative, Captures = [light, basis, frame] },
            frame, new DevelopRecipe { FilmKind = FilmKind.ColorNegative, Accepted = true });
    }

    private async Task<RollCapture> Capture(CapturePurpose purpose, CaptureMode mode, int number)
    {
        var files = await new DemoCaptureSource().CaptureAsync(purpose, mode, number, root,
            (_, _) => Task.CompletedTask, default);
        return new(Guid.NewGuid(), number, purpose, mode, files, true, DateTimeOffset.UtcNow);
    }

    private static float[] Pixels(string path)
    {
        using var tif = Tiff.Open(path, "r");
        int width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt(), height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        var pixels = new float[width * height * 3];
        var row = new byte[tif.ScanlineSize()];
        for (int y = 0; y < height; y++)
        {
            Assert.True(tif.ReadScanline(row, y));
            for (int x = 0; x < width * 3; x++) pixels[y * width * 3 + x] = BitConverter.ToUInt16(row, x * 2) / 65535f;
        }
        return pixels;
    }

    private static float[] FloatPixels(string path)
    {
        using var tif = Tiff.Open(path, "r");
        Assert.Equal(32, tif.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt());
        int width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt(), height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        var pixels = new float[width * height * 3];
        var row = new byte[tif.ScanlineSize()];
        for (int y = 0; y < height; y++)
        {
            Assert.True(tif.ReadScanline(row, y));
            for (int x = 0; x < width * 3; x++) pixels[y * width * 3 + x] = BitConverter.ToSingle(row, x * 4);
        }
        return pixels;
    }

    private static void WriteUnqualifiedTiff(string path)
    {
        using var tif = Tiff.Open(path, "w");
        tif.SetField(TiffTag.IMAGEWIDTH, 384);
        tif.SetField(TiffTag.IMAGELENGTH, 256);
        tif.SetField(TiffTag.BITSPERSAMPLE, 16);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.IMAGEDESCRIPTION, "SYNTHETIC Film Light - not a camera capture; FilmLight linear Rec2020; black-subtracted; exposure-locked");
        for (int y = 0; y < 256; y++) Assert.True(tif.WriteScanline(new byte[384 * 6], y));
        Assert.True(tif.WriteDirectory());
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("FILMLIGHT_KEEP_TEST_ARTIFACTS") != "1" && Directory.Exists(root))
            Directory.Delete(root, true);
    }

    public sealed class InstalledDarktableFactAttribute : FactAttribute
    {
        public InstalledDarktableFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(Cli))
                Skip = "Requires installed darktable 5.6.2; set FILMLIGHT_TEST_DARKTABLE to its CLI path.";
        }
    }
}
