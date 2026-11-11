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

    [Theory]
    [InlineData(CaptureMode.White)]
    [InlineData(CaptureMode.Rgb)]
    public async Task PictureSamplesAreAbsoluteAndStayWithTheirFrameWithoutCompounding(CaptureMode mode)
    {
        var first = await Capture(CapturePurpose.Frame, mode, 1);
        var second = await Capture(CapturePurpose.Frame, mode, 2);
        var roll = new RollDocument { IsDemo = true, FilmKind = FilmKind.ColorNegative, Captures = [first, second] };
        var recipe = new DevelopRecipe { FilmKind = roll.FilmKind, PictureCalibration = new() };
        var processor = new DemoFrameProcessor();
        var provisional = await processor.PreviewAsync(roll, first, recipe, root, null, default);
        Assert.True(File.Exists(provisional.ImagePath));
        Assert.True(File.Exists(provisional.SourceImagePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ExportAsync(roll, first, recipe, root, null, default));
        recipe = await FrameCalibration.SampleAsync(roll, first, recipe, CalibrationTarget.FilmBase, 12.0 / 384, .5, root, null, default);
        recipe = await FrameCalibration.SampleAsync(roll, first, recipe, CalibrationTarget.Backlight, 3.0 / 384, .5, root, null, default);
        Assert.True(recipe.PictureCalibration!.IsComplete(roll.FilmKind));
        var repeated = await FrameCalibration.SampleAsync(roll, first, recipe, CalibrationTarget.Backlight, 3.0 / 384, .5, root, null, default);
        Assert.Equal(recipe, repeated);
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), null);
        var a = await ReferenceReconstruction.ReconstructAsync(sources, roll, first, recipe, true, FrameSources.FullResolution, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReferenceReconstruction.ReconstructAsync(sources, roll, second, recipe, true, FrameSources.FullResolution, default));
        Assert.Equal(recipe.PictureCalibration.FilmBase!.Red / recipe.PictureCalibration.Light!.Red, a.FilmBase[0], 5);
        var preview = await processor.PreviewAsync(roll, first, recipe, root, null, default);
        var export = await processor.ExportAsync(roll, first, recipe, root, null, default);
        Assert.EndsWith(".png", preview.ImagePath);
        ProcessingImages.ValidatePng(preview.ImagePath, a.Image.Width, a.Image.Height, true);
        Assert.EndsWith(".jpg", export.OutputFiles[1]);
    }

    [Theory]
    [InlineData(CaptureMode.White, false, FilmKind.ColorNegative)]
    [InlineData(CaptureMode.White, true, FilmKind.ColorNegative)]
    [InlineData(CaptureMode.Rgb, false, FilmKind.ColorNegative)]
    [InlineData(CaptureMode.White, false, FilmKind.Positive)]
    public async Task ClippedBacklightPickPersistsWithMarkerAndSkipsNormalization(
        CaptureMode mode, bool partial, FilmKind filmKind)
    {
        var (store, frame, recipe) = await ClippedBacklightFrame(mode, partial, filmKind);
        var roll = store.Document;
        var changed = await FrameCalibration.SampleAsync(roll, frame, recipe,
            CalibrationTarget.Backlight, 3.0 / 384, .5, root, null, default);
        Assert.Null(recipe.PictureCalibration!.Light);
        Assert.True(changed.PictureCalibration!.IsComplete(roll.FilmKind));
        var light = Assert.IsType<PictureSample>(changed.PictureCalibration.Light);
        Assert.True(light.IsClipped);
        Assert.True(FrameCalibration.TryMapSample(light, frame.Id, changed, 384, 256, 384, 256, out var x, out var y));
        Assert.Equal(3, x, 5);
        Assert.Equal(128, y, 5);
        var accepted = store.SaveRecipe(changed);
        var reopened = RollStore.Open(store.RootPath);
        Assert.Equal(accepted, Assert.Single(reopened.Document.Recipes));
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), null);
        var baseline = await ReferenceReconstruction.ReconstructAsync(sources, roll, frame, recipe, true, FrameSources.FullResolution, default);
        var actual = await ReferenceReconstruction.ReconstructAsync(sources, reopened.Document, frame, accepted, true, FrameSources.FullResolution, default);
        Assert.Equal(baseline.Image.Pixels, actual.Image.Pixels);
        Assert.Equal(baseline.FilmBase, actual.FilmBase);
        var export = await new DemoFrameProcessor().ExportAsync(reopened.Document, frame, accepted, root, null, default);
        Assert.Contains(export.Warnings, w => w.Contains("clipped", StringComparison.OrdinalIgnoreCase));
        var corrected = await FrameCalibration.SampleAsync(roll, frame, changed,
            CalibrationTarget.Backlight, 3.0 / 384, .4, root, null, default);
        Assert.False(corrected.PictureCalibration!.Light!.IsClipped);
        var normalized = await ReferenceReconstruction.ReconstructAsync(sources, roll, frame, corrected, true, FrameSources.FullResolution, default);
        Assert.NotEqual(baseline.Image.Pixels[1], normalized.Image.Pixels[1]);
    }

    [InstalledDarktableFact]
    public async Task ClippedBacklightDoesNotChangeDarktableInputOrRecipe()
    {
        var (store, frame, recipe) = await ClippedBacklightFrame(CaptureMode.White, false);
        var roll = store.Document;
        var changed = await FrameCalibration.SampleAsync(roll, frame, recipe,
            CalibrationTarget.Backlight, 3.0 / 384, .5, root, null, default);
        var processor = new DarktableFrameProcessor(Cli);
        var baseline = await processor.PreviewAsync(roll, frame, recipe, root, null, default);
        var actual = await processor.PreviewAsync(roll, frame, changed, root, null, default);
        string before = Path.GetDirectoryName(baseline.ImagePath)!, after = Path.GetDirectoryName(actual.ImagePath)!;
        Assert.Equal(FloatPixels(Path.Combine(before, "normalized-linear.tif")),
            FloatPixels(Path.Combine(after, "normalized-linear.tif")));
        Assert.Equal(File.ReadAllText(Path.Combine(before, "develop.xmp")),
            File.ReadAllText(Path.Combine(after, "develop.xmp")));
        Assert.False(File.Exists(Path.Combine(before, "output-positive.xmp")));
        Assert.False(File.Exists(Path.Combine(after, "output-positive.xmp")));
        Assert.Equal(File.ReadAllBytes(baseline.ImagePath), File.ReadAllBytes(actual.ImagePath));
        Assert.Contains(actual.Warnings, w => w.Contains("clipped", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(actual.Warnings, w => w.Contains("Provisional"));
    }

    [Theory]
    [InlineData(CalibrationTarget.Backlight, 0)]
    [InlineData(CalibrationTarget.FilmBase, 0)]
    [InlineData(CalibrationTarget.FilmBase, 1)]
    [InlineData(CalibrationTarget.FilmBase, 1.05f)]
    public async Task PicturePickerRejectsDarkOrInvalidPatchesAndClippedFilmBase(CalibrationTarget target, float value)
    {
        var frame = await Capture(CapturePurpose.Frame, CaptureMode.White, 1);
        var roll = new RollDocument { IsDemo = true, FilmKind = FilmKind.ColorNegative, Captures = [frame] };
        var recipe = new DevelopRecipe { FilmKind = roll.FilmKind, PictureCalibration = new() };
        var path = frame.Files[0].Path;
        var image = ProcessingImages.ReadLinear(path, default, true);
        Array.Fill(image.Pixels, value);
        ProcessingImages.WriteTiff(path, image, true, ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel,
            default, ProcessingImages.LinearRec2020Profile());
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCalibration.SampleAsync(roll, frame, recipe,
            target, .5, .5, root, null, default));
    }

    [Theory]
    [InlineData(CaptureMode.White, false)]
    [InlineData(CaptureMode.White, true)]
    [InlineData(CaptureMode.Rgb, false)]
    [InlineData(CaptureMode.Rgb, true)]
    public async Task AutoPicksMeasureSprocketsAndInterHoleFilmInUncroppedLinearChannels(CaptureMode mode, bool vertical)
    {
        var frame = await Capture(CapturePurpose.Frame, mode, 1);
        foreach (var file in frame.Files)
        {
            var image = PerforatedFilm(vertical);
            // The unselected channels of an RGB exposure must not affect its samples.
            int selected = file.Channel switch { LightChannel.Red => 0, LightChannel.Green => 1, _ => 2 };
            if (mode == CaptureMode.Rgb)
                for (int i = 0; i < image.Pixels.Length; i++)
                    if (i % 3 != selected) image.Pixels[i] = .01f;
            ProcessingImages.WriteTiff(file.Path, image, true,
                ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel, default,
                ProcessingImages.LinearRec2020Profile());
        }
        var roll = new RollDocument { IsDemo = true, FilmKind = FilmKind.ColorNegative, Captures = [frame] };
        var recipe = new DevelopRecipe
        {
            FrameId = frame.Id, FilmKind = roll.FilmKind, PictureCalibration = new(), CropLeft = .2, CropTop = .2,
            CropRight = .2, CropBottom = .2, ExposureEv = .5, RedGain = 1.1, BlueGain = .9,
            FilmBaseRed = 1.1
        };
        var changed = await FrameCalibration.AutoSampleAsync(roll, frame, recipe, root, null, default);
        var light = Assert.IsType<PictureSample>(changed.PictureCalibration!.Light);
        var basis = Assert.IsType<PictureSample>(changed.PictureCalibration.FilmBase);
        Assert.Equal(frame.Id, light.FrameId);
        Assert.Equal(frame.Id, basis.FrameId);
        Assert.Equal(vertical ? 256 : 384, light.SourceWidth);
        Assert.Equal(vertical ? 384 : 256, light.SourceHeight);
        Assert.Equal(light.SourceWidth, basis.SourceWidth);
        Assert.Equal(light.SourceHeight, basis.SourceHeight);
        Assert.Equal(.8, light.Red, 5);
        Assert.Equal(.7, light.Green, 5);
        Assert.Equal(.6, light.Blue, 5);
        Assert.Equal(.5, basis.Red, 5);
        Assert.Equal(.25, basis.Green, 5);
        Assert.Equal(.1, basis.Blue, 5);
        Assert.True(vertical ? light.X < .2 || light.X > .8 : light.Y < .2 || light.Y > .8);
        Assert.Equal(recipe with { PictureCalibration = changed.PictureCalibration }, changed);
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), null);
        var reconstructed = await ReferenceReconstruction.ReconstructAsync(sources, roll, frame, changed, true, FrameSources.FullResolution, default);
        Assert.Equal(.5 / .8 * recipe.FilmBaseRed, reconstructed.FilmBase[0], 5);
        Assert.Equal(.25 / .7, reconstructed.FilmBase[1], 5);
        Assert.Equal(.1 / .6, reconstructed.FilmBase[2], 5);
        Assert.Equal(changed, await FrameCalibration.AutoSampleAsync(roll, frame, changed, root, null, default));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("two")]
    [InlineData("conflicting")]
    [InlineData("uneven")]
    [InlineData("no-film")]
    [InlineData("marked-film")]
    public void AutoPicksRejectUnreliableGeometryAndMeasurements(string problem)
    {
        var image = PerforatedFilm(false, problem);
        var error = Assert.Throws<InvalidDataException>(() =>
            SprocketCalibration.Detect(image, Guid.NewGuid(), true, default));
        Assert.Contains("Existing picks have not changed", error.Message);
        Assert.Contains("manual pickers", error.Message);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.004f)]
    public async Task AutomaticClippedBacklightUsesTheSameMarkerOnlyBehaviorAsManualPicking(float peak)
    {
        var frame = await Capture(CapturePurpose.Frame, CaptureMode.White, 1);
        var image = PerforatedFilm(false, "clipped");
        for (int i = 0; i < image.Pixels.Length; i += 3)
            if (image.Pixels[i] == 1) image.Pixels[i] = peak;
        ProcessingImages.WriteTiff(frame.Files[0].Path, image, true,
            ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel, default,
            ProcessingImages.LinearRec2020Profile());
        var roll = new RollDocument { IsDemo = true, Captures = [frame] };
        var recipe = new DevelopRecipe { FrameId = frame.Id };
        var manual = await FrameCalibration.SampleAsync(roll, frame, recipe,
            CalibrationTarget.Backlight, 44.0 / 384, 29.0 / 256, root, null, default);
        Assert.True(manual.PictureCalibration!.Light!.IsClipped);
        var automatic = await FrameCalibration.AutoSampleAsync(roll, frame, recipe, root, null, default);
        Assert.True(automatic.PictureCalibration!.IsComplete(roll.FilmKind));
        Assert.True(automatic.PictureCalibration.Light!.IsClipped);
        Assert.False(automatic.PictureCalibration.FilmBase!.IsClipped);
        var output = await new DemoFrameProcessor().PreviewAsync(roll, frame, automatic, root, null, default);
        Assert.Contains(PictureCalibration.ClippedBacklightWarning, output.Warnings);
    }

    [Fact]
    public void AutoPicksRetainOutOfRangeBacklightAsMarkerOnly()
    {
        var image = PerforatedFilm(false);
        for (int i = 0; i < image.Pixels.Length; i += 3)
            if (image.Pixels[i] == .8f)
            {
                image.Pixels[i] = .98f;
                image.Pixels[i + 1] = 1.01f;
                image.Pixels[i + 2] = .9f;
            }
        var picture = SprocketCalibration.Detect(image, Guid.NewGuid(), true, default);
        Assert.True(picture.Light!.IsClipped);
        Assert.NotNull(picture.FilmBase);
    }

    [Theory]
    [InlineData("marked-film", "FilmBase")]
    public async Task RejectedAutoPicksSaveMeasurementDiagnosticsWithoutChangingExistingPicks(string problem, string target)
    {
        var frame = await Capture(CapturePurpose.Frame, CaptureMode.White, 1);
        ProcessingImages.WriteTiff(frame.Files[0].Path, PerforatedFilm(false, problem), true,
            ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel, default,
            ProcessingImages.LinearRec2020Profile());
        var roll = new RollDocument { IsDemo = true, FilmKind = FilmKind.ColorNegative, Captures = [frame] };
        var picture = new PictureCalibration
        {
            Light = new(frame.Id, .1, .1, .8, .7, .6), FilmBase = new(frame.Id, .2, .1, .5, .25, .1)
        };
        var recipe = new DevelopRecipe { FilmKind = roll.FilmKind, PictureCalibration = picture };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            FrameCalibration.AutoSampleAsync(roll, frame, recipe, root, null, default));
        string logPath = Assert.Single(Directory.GetFiles(Path.Combine(root, "calibration-diagnostics"), "*.log"));
        string log = await File.ReadAllTextAsync(logPath);
        Assert.Contains(logPath, error.Message);
        Assert.Contains(frame.Id.ToString(), log);
        Assert.Contains("candidate-holes=", log);
        Assert.Contains("row-hypotheses=", log);
        Assert.Contains($"Rejected {target} patch", log);
        Assert.Contains("RGB median=", log);
        Assert.Contains("Result: rejected", log);
        Assert.Same(picture, recipe.PictureCalibration);
    }

    [Fact]
    public async Task AutoPicksLeaveOriginalRecipeUntouchedOnFailureAndHonorCancellation()
    {
        var frame = await Capture(CapturePurpose.Frame, CaptureMode.White, 1);
        ProcessingImages.WriteTiff(frame.Files[0].Path, PerforatedFilm(false, "missing"), true,
            ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel, default,
            ProcessingImages.LinearRec2020Profile());
        var roll = new RollDocument { IsDemo = true, FilmKind = FilmKind.ColorNegative, Captures = [frame] };
        var picture = new PictureCalibration
        {
            Light = new(frame.Id, .1, .1, .8, .7, .6), FilmBase = new(frame.Id, .2, .1, .5, .25, .1)
        };
        var recipe = new DevelopRecipe
        {
            FilmKind = roll.FilmKind, PictureCalibration = picture
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FrameCalibration.AutoSampleAsync(roll, frame, recipe, root, null, default));
        Assert.Same(picture, recipe.PictureCalibration);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FrameCalibration.AutoSampleAsync(roll, frame, recipe, root, null, cancel.Token));
    }

    [Fact]
    public void AutoPicksForPositiveFilmNeedOnlyUnclippedHoleInteriors()
    {
        var picture = SprocketCalibration.Detect(PerforatedFilm(false, "marked-film"), Guid.NewGuid(), false, default);
        Assert.NotNull(picture.Light);
        Assert.Null(picture.FilmBase);
        Assert.True(picture.IsComplete(FilmKind.Positive));
    }

    [Fact]
    public void AutoPicksMapReducedDetectionToFullResolutionAndAcceptNeutralFilmBase()
    {
        var small = PerforatedFilm(true);
        for (int i = 0; i < small.Pixels.Length; i += 3)
            if (small.Pixels[i] == .5f)
            {
                small.Pixels[i] = .4f;
                small.Pixels[i + 1] = .35f;
                small.Pixels[i + 2] = .3f;
            }
        var full = new LinearImage(small.Width * 5, small.Height * 5, new float[small.Pixels.Length * 25]);
        for (int y = 0; y < full.Height; y++)
        for (int x = 0; x < full.Width; x++)
            Array.Copy(small.Pixels, (y / 5 * small.Width + x / 5) * 3, full.Pixels, (y * full.Width + x) * 3, 3);
        var picture = SprocketCalibration.Detect(full, Guid.NewGuid(), true, default);
        var light = picture.Light!;
        var basis = picture.FilmBase!;
        Assert.Equal(.8, light.Red, 5);
        Assert.Equal(.7, light.Green, 5);
        Assert.Equal(.6, light.Blue, 5);
        Assert.Equal(.5, basis.Red / light.Red, 5);
        Assert.Equal(.5, basis.Green / light.Green, 5);
        Assert.Equal(.5, basis.Blue / light.Blue, 5);
        int lightIndex = ((int)(light.Y * full.Height) * full.Width + (int)(light.X * full.Width)) * 3;
        int baseIndex = ((int)(basis.Y * full.Height) * full.Width + (int)(basis.X * full.Width)) * 3;
        Assert.Equal(light.Red, full.Pixels[lightIndex], 5);
        Assert.Equal(basis.Red, full.Pixels[baseIndex], 5);
    }

    [InstalledDarktableFact]
    public async Task AutoPicksFeedMeasuredTransmissionToNegadoctorWithoutReferences()
    {
        var frame = await Capture(CapturePurpose.Frame, CaptureMode.White, 1);
        ProcessingImages.WriteTiff(frame.Files[0].Path, PerforatedFilm(false), true,
            ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel, default,
            ProcessingImages.LinearRec2020Profile());
        var roll = new RollDocument { IsDemo = true, FilmKind = FilmKind.ColorNegative, Captures = [frame] };
        var recipe = await FrameCalibration.AutoSampleAsync(roll, frame,
            new() { FilmKind = roll.FilmKind, CropTop = .25, CropBottom = .25 }, root, null, default);
        var preview = await new DarktableFrameProcessor(Cli).PreviewAsync(roll, frame, recipe, root, null, default);
        string job = Path.GetDirectoryName(preview.ImagePath)!;
        var dt = System.Xml.Linq.XNamespace.Get("http://darktable.sf.net/");
        var module = System.Xml.Linq.XDocument.Load(Path.Combine(job, "develop.xmp"))
            .Descendants().Single(e => (string?)e.Attribute(dt + "operation") == "negadoctor");
        var parameters = Convert.FromHexString(module.Attribute(dt + "params")!.Value);
        Assert.Equal(.5 / .8, BitConverter.ToSingle(parameters, 4), 5);
        Assert.Equal(.25 / .7, BitConverter.ToSingle(parameters, 8), 5);
        Assert.Equal(.1 / .6, BitConverter.ToSingle(parameters, 12), 5);
        Assert.Contains("negadoctor", File.ReadAllText(Path.Combine(job, "develop.stdout.log")));
        Assert.True(File.Exists(preview.ImagePath));
    }

    [Fact]
    public void AutoPicksHandleSprocketHolesCutByTheImageBoundary()
    {
        var source = PerforatedFilm(false);
        var cropped = new LinearImage(source.Width, 200, new float[source.Width * 200 * 3]);
        Array.Copy(source.Pixels, 28 * source.Width * 3, cropped.Pixels, 0, cropped.Pixels.Length);
        var picture = SprocketCalibration.Detect(cropped, Guid.NewGuid(), true, default);
        Assert.True(picture.Light!.Y < .05 || picture.Light.Y > .95);
        Assert.Equal(.8, picture.Light.Red, 5);
        Assert.Equal(.5, picture.FilmBase!.Red, 5);
        var locations = SprocketCalibration.Locate(cropped, default);
        Assert.Equal(picture.Light.X, locations.LightX);
        Assert.Equal(picture.FilmBase.X, locations.FilmBaseX);
    }

    [Fact]
    public void PreviewLocalizationDoesNotQualifyClippedColorsAsRawCalibration()
    {
        var image = PerforatedFilm(false, "clipped");
        var locations = SprocketCalibration.Locate(image, default);
        Assert.True(locations.LightY < .2 || locations.LightY > .8);
        Assert.NotEqual(locations.LightX, locations.FilmBaseX);
        Assert.True(SprocketCalibration.Detect(image, Guid.NewGuid(), true, default).Light!.IsClipped);
    }

    [Fact]
    public void AutoPicksUseHigherThresholdsForLowContrastClearFilm()
    {
        var image = PerforatedFilm(false);
        for (int i = 0; i < image.Pixels.Length; i += 3)
            if (image.Pixels[i] == .5f)
            {
                image.Pixels[i] = .77f;
                image.Pixels[i + 1] = .67f;
                image.Pixels[i + 2] = .57f;
            }
        var picture = SprocketCalibration.Detect(image, Guid.NewGuid(), true, default);
        Assert.Equal(.8, picture.Light!.Red, 5);
        Assert.Equal(.77, picture.FilmBase!.Red, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProxyCalibrationSkipsFullTierUnlessNearClipNeedsVerification(bool hiddenClipping)
    {
        var frame = await Capture(CapturePurpose.Frame, CaptureMode.White, 1);
        var small = PerforatedFilm(false);
        const int scale = 10;
        var full = new LinearImage(small.Width * scale, small.Height * scale, new float[small.Pixels.Length * scale * scale]);
        for (int y = 0; y < full.Height; y++)
        for (int x = 0; x < full.Width; x++)
        {
            int source = (y / scale * small.Width + x / scale) * 3;
            int destination = (y * full.Width + x) * 3;
            Array.Copy(small.Pixels, source, full.Pixels, destination, 3);
            if (hiddenClipping && small.Pixels[source] == .8f)
                full.Pixels[destination] = (x + y) % 2 == 0 ? 1 : .88f;
        }
        ProcessingImages.WriteTiff(frame.Files[0].Path, full, true,
            ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel, default,
            ProcessingImages.LinearRec2020Profile());
        var roll = new RollDocument { IsDemo = true, Captures = [frame] };
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), null);
        var progress = new RecordingProcessingProgress();
        var proxy = await sources.Open(roll, frame).GetImageAsync(SprocketDetectionStep.DetectionEdge, default, progress);
        var detected = SprocketCalibration.DetectWithEvidence(proxy, frame.Id, true, default);
        Assert.Equal(2560, proxy.Width);
        Assert.Equal(proxy.Width, detected.Picture.Light!.SourceWidth);
        Assert.Equal(hiddenClipping, detected.LightEvidence!.Peak >= ClippingVerificationStep.HeadroomThreshold);
        var recipe = new DevelopRecipe { FrameId = frame.Id };
        if (hiddenClipping)
        {
            var result = await FrameCalibration.AutoSampleAsync(sources, roll, frame, recipe, root, default, progress);
            Assert.True(result.PictureCalibration!.Light!.IsClipped);
            Assert.Equal(2, progress.Events.Count(p => p.Stage == "Reading linear input"));
        }
        else
        {
            var result = await FrameCalibration.AutoSampleAsync(sources, roll, frame, recipe, root, default, progress);
            Assert.Equal(detected.Picture, result.PictureCalibration);
            Assert.Single(progress.Events, p => p.Stage == "Reading linear input");
            Assert.True(FrameCalibration.TryMapSample(result.PictureCalibration!.Light, frame.Id, result,
                800, 600, 1600, 1066, out var x, out var y));
            Assert.Equal(detected.Picture.Light.X * 800, x, 5);
            Assert.Equal((600 - 800.0 * 1066 / 1600) / 2 + detected.Picture.Light.Y * 800 * 1066 / 1600, y, 5);
            var fullPicture = SprocketCalibration.Detect(full, frame.Id, true, default);
            foreach (var (proxySample, fullSample) in new[]
            {
                (result.PictureCalibration.Light!, fullPicture.Light!),
                (result.PictureCalibration.FilmBase!, fullPicture.FilmBase!)
            })
            {
                Assert.All(new[] { proxySample.Red / fullSample.Red, proxySample.Green / fullSample.Green,
                    proxySample.Blue / fullSample.Blue }, ratio => Assert.InRange(Math.Abs(ratio - 1), 0, .01));
                double dx = (proxySample.X - fullSample.X) * full.Width;
                double dy = (proxySample.Y - fullSample.Y) * full.Height;
                Assert.InRange(Math.Sqrt(dx * dx + dy * dy) / Math.Max(full.Width, full.Height), 0, .005);
            }
        }
    }

    [Fact]
    public void AutoPicksKeepTheSamePatchAndProportionalRgbAcrossExposureScales()
    {
        var image = SingleSprocketRow(Enumerable.Repeat(36, 8).ToArray(),
            [.492f, .492f, .492f, .492f, .508f, .508f, .508f, .508f]);
        var frameId = Guid.NewGuid();
        var a = SprocketCalibration.Detect(Scaled(image, .5f), frameId, true, default);
        var b = SprocketCalibration.Detect(Scaled(image, .8f), frameId, true, default);
        foreach (var (x, y) in new[] { (a.Light!, b.Light!), (a.FilmBase!, b.FilmBase!) })
        {
            Assert.Equal(x.X, y.X);
            Assert.Equal(x.Y, y.Y);
            Assert.All(new[] { x.Red / y.Red, x.Green / y.Green, x.Blue / y.Blue },
                ratio => Assert.InRange(Math.Abs(ratio / (.5 / .8) - 1), 0, .005));
        }
    }

    [Fact]
    public void AutoPicksExcludeANonuniformHoleRegardlessOfBorderlineMembership()
    {
        var pictures = new List<PictureCalibration>();
        var frameId = Guid.NewGuid();
        foreach (int borderlineWidth in new[] { 29, 30 })
        {
            var image = SingleSprocketRow([40, 40, 40, 40, 40, 40, 40, borderlineWidth],
                Enumerable.Repeat(.6f, 8).ToArray());
            for (int y = 26; y < 54; y++)
            for (int x = 35 + 7 * 65; x < 35 + 7 * 65 + borderlineWidth; x++)
            {
                float blue = x % 4 < 2 ? .2f : .6f;
                image.Pixels[(y * image.Width + x) * 3 + 2] = blue;
                // Keep segmentation luminance constant while making the color patch nonuniform.
                image.Pixels[(y * image.Width + x) * 3] = .8f + (.6f - blue) * (.0593f / .2627f);
            }
            var diagnostics = new List<string>();
            pictures.Add(SprocketCalibration.Detect(image, frameId, true, default, diagnostics.Add));
            if (borderlineWidth == 30) Assert.Contains(diagnostics, s => s.StartsWith("Excluded hole"));
        }
        Assert.Equal(pictures[0], pictures[1]);
    }

    [Fact]
    public void AutoPicksCompareOverlappingHypothesesAsOnePhysicalRow()
    {
        var image = SingleSprocketRow([30, 30, 30, 30, 36, 43, 43, 43, 43],
            [.45f, .45f, .45f, .45f, .5f, .55f, .55f, .55f, .55f]);
        var diagnostics = new List<string>();
        var picture = SprocketCalibration.Detect(image, Guid.NewGuid(), true, default, diagnostics.Add);
        Assert.Equal(.5, picture.Light!.Blue, 5);
        Assert.Single(diagnostics, s => s.StartsWith("Accepted physical row:"));
        Assert.Contains(diagnostics, s => s.Contains("physical-rows=1"));
    }

    private static LinearImage Scaled(LinearImage image, float scale) =>
        new(image.Width, image.Height, image.Pixels.Select(v => v * scale).ToArray());

    private static LinearImage SingleSprocketRow(int[] widths, float[] blue)
    {
        const int width = 640, height = 400;
        var image = new LinearImage(width, height, new float[width * height * 3]);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float[] color = y is >= 12 and < 68 ? [.4f, .2f, .1f] : [.08f, .09f, .07f];
            for (int n = 0; n < widths.Length; n++)
                if (y is >= 26 and < 54 && x >= 35 + n * 65 && x < 35 + n * 65 + widths[n])
                    color = [.8f, .7f, blue[n]];
            Array.Copy(color, 0, image.Pixels, (y * width + x) * 3, 3);
        }
        return image;
    }

    private static LinearImage PerforatedFilm(bool vertical, string problem = "")
    {
        const int width = 384, height = 256;
        var image = new LinearImage(vertical ? height : width, vertical ? width : height, new float[width * height * 3]);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            bool strip = y is >= 8 and < 52 or >= 204 and < 248;
            float[] color = strip ? [.5f, .25f, .1f] : [.08f, .09f, .07f];
            if (problem == "no-film" && strip) color = [.8f, .7f, .6f];
            if (problem == "conflicting" && y >= 204 && strip) color = [.3f, .15f, .06f];
            if (problem == "marked-film" && strip && x % 4 < 2) color = [.01f, .01f, .01f];
            int count = problem == "missing" ? 0 : problem == "two" ? 2 : 9;
            for (int n = 0; n < count; n++)
            {
                int left = 35 + n * 34 + (problem == "uneven" && n % 2 == 0 ? 9 : 0);
                if (x >= left && x < left + 18 && (y is >= 22 and < 36 or >= 220 and < 234))
                    color = problem == "clipped" ? [1, .7f, .6f] : [.8f, .7f, .6f];
            }
            // Isolated dust should not bias medians; a bright scene patch is not a perforation.
            if (x == 43 && y == 28) color = [.01f, .01f, .01f];
            if (x is >= 80 and < 300 && y is >= 90 and < 140) color = [.8f, .7f, .6f];
            int i = vertical ? (x * height + y) * 3 : (y * width + x) * 3;
            Array.Copy(color, 0, image.Pixels, i, 3);
        }
        return image;
    }

    [InstalledDarktableFact]
    public async Task DarktableCanRenderPicturePicksWithoutAnyReferenceCaptures()
    {
        var frame = await Capture(CapturePurpose.Frame, CaptureMode.White, 1);
        var roll = new RollDocument { IsDemo = true, FilmKind = FilmKind.ColorNegative, Captures = [frame] };
        var recipe = new DevelopRecipe { FilmKind = roll.FilmKind, PictureCalibration = new() };
        var processor = new DarktableFrameProcessor(Cli);
        var progress = new RecordingProcessingProgress();
        var provisional = await processor.PreviewAsync(roll, frame, recipe, root, progress, default);
        string[] stages = ["Preparing conversion", "Checking processor", "Reading linear input", "Normalizing film",
            "Preparing preview", "Preparing positive conversion", "Inverting negative", "Applying develop settings",
            "Saving positive images", "Validating output", "Publishing output"];
        Assert.Equal(stages, progress.Events.Select(p => p.Stage));
        Assert.All(progress.Events, p =>
        {
            Assert.Equal(frame.Id, p.FrameId);
            Assert.False(string.IsNullOrWhiteSpace(p.Message));
        });
        Assert.Contains(provisional.Warnings, w => w.Contains("Provisional"));
        Assert.True(File.Exists(provisional.SourceImagePath));
        recipe = await FrameCalibration.SampleAsync(roll, frame, recipe, CalibrationTarget.Backlight, 3.0 / 384, .5, root, null, default);
        recipe = await FrameCalibration.SampleAsync(roll, frame, recipe, CalibrationTarget.FilmBase, 12.0 / 384, .5, root, null, default);
        var export = await processor.ExportAsync(roll, frame, recipe, root, null, default);
        Assert.Equal(2, export.OutputFiles.Count);
        Assert.All(export.OutputFiles, p => Assert.True(File.Exists(p)));
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
    [InlineData(800, 600, 400, 200)]
    [InlineData(600, 800, 200, 400)]
    public void CalibrationMarkersFollowScaledLetterboxedImages(double viewportWidth, double viewportHeight,
        int imageWidth, int imageHeight)
    {
        var id = Guid.NewGuid();
        var sample = new PictureSample(id, .25, .25, .8, .7, .6)
        {
            SourceWidth = imageWidth, SourceHeight = imageHeight
        };
        Assert.True(FrameCalibration.TryMapSample(sample, id, new(), viewportWidth, viewportHeight,
            imageWidth, imageHeight, out var x, out var y));
        Assert.Equal(200, x);
        Assert.Equal(200, y);
    }

    [Fact]
    public void CalibrationMarkersUseExactPixelCropRatherThanUnroundedFractions()
    {
        var id = Guid.NewGuid();
        var sample = new PictureSample(id, (14 + 86.25) / 403, (19 + 111.75) / 207, .8, .7, .6)
        {
            SourceWidth = 403, SourceHeight = 207
        };
        var recipe = new DevelopRecipe { CropLeft = .037, CropRight = .111, CropTop = .093, CropBottom = .19 };
        Assert.True(FrameCalibration.TryMapSample(sample, id, recipe, 900, 700, 345, 149, out var x, out var y));
        Assert.Equal(225, x, 8);
        Assert.Equal(447.17391304347825, y, 8);
    }

    [Fact]
    public void CalibrationMarkersRejectOtherFramesCroppedOutSamplesAndUnknownLegacyGeometry()
    {
        var id = Guid.NewGuid();
        var sample = new PictureSample(id, .1, .5, .8, .7, .6) { SourceWidth = 400, SourceHeight = 200 };
        Assert.False(Map(sample, Guid.NewGuid(), new()));
        Assert.False(Map(sample, id, new() { CropLeft = .2 }));
        Assert.False(Map(sample with { X = .5 }, id, new() { CropRight = .5 }));
        Assert.False(Map(sample with { SourceWidth = 0, SourceHeight = 0 }, id, new()));
        Assert.False(Map(null, id, new()));
        Assert.False(FrameCalibration.TryMapSample(sample, id, new(), 0, 600, 400, 200, out _, out _));
        Assert.False(FrameCalibration.TryMapSample(sample, id, new(), double.NaN, 600, 400, 200, out _, out _));

        static bool Map(PictureSample? point, Guid frame, DevelopRecipe recipe) =>
            FrameCalibration.TryMapSample(point, frame, recipe, 800, 600, 400, 200, out _, out _);
    }

    [Fact]
    public async Task CroppedManualPicksRetainOriginalCoordinatesForMarkersAfterCropEdits()
    {
        var frame = await Capture(CapturePurpose.Frame, CaptureMode.White, 1);
        var roll = new RollDocument { IsDemo = true, FilmKind = FilmKind.ColorNegative, Captures = [frame] };
        var recipe = new DevelopRecipe
        {
            FilmKind = roll.FilmKind, PictureCalibration = new(),
            CropLeft = .1, CropRight = .15, CropTop = .2, CropBottom = .1
        };
        var changed = await FrameCalibration.SampleAsync(roll, frame, recipe,
            CalibrationTarget.Backlight, .3, .4, root, null, default);
        var sample = changed.PictureCalibration!.Light!;
        Assert.Equal(384, sample.SourceWidth);
        Assert.Equal(256, sample.SourceHeight);
        Assert.Equal(.1 + .3 * .75, sample.X, 10);
        Assert.Equal(.2 + .4 * .7, sample.Y, 10);
        Assert.True(FrameCalibration.TryMapSample(sample, frame.Id, recipe, 800, 600, 289, 180, out var x, out var y));
        Assert.True(FrameCalibration.TryMapPoint(800, 600, 289, 180, x, y, out var u, out var v));
        Assert.Equal((sample.X * 384 - 38) / 289, u, 10);
        Assert.Equal((sample.Y * 256 - 51) / 180, v, 10);
        Assert.True(FrameCalibration.TryMapSample(sample, frame.Id, new(), 800, 600, 384, 256, out x, out y));
        Assert.True(FrameCalibration.TryMapPoint(800, 600, 384, 256, x, y, out u, out v));
        Assert.Equal(sample.X, u, 10);
        Assert.Equal(sample.Y, v, 10);
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
        var sample = changed.PictureCalibration!.Light!;
        Assert.Equal(frame.Id, changed.FrameId);
        Assert.Equal(frame.Id, sample.FrameId);
        Assert.Equal(1.5, sample.Green / sample.Red, 5);
        Assert.Equal(3, sample.Green / sample.Blue, 5);
        Assert.Equal(recipe.RedGain, changed.RedGain);
        Assert.Equal(recipe.GreenGain, changed.GreenGain);
        Assert.Equal(recipe.BlueGain, changed.BlueGain);
        Assert.Equal(recipe.FilmBaseRed, changed.FilmBaseRed);
    }

    [Theory]
    [InlineData(CaptureMode.White)]
    [InlineData(CaptureMode.Rgb)]
    public async Task FilmBasePickerMapsCropWithoutCompoundingOrChangingDevelopMultipliers(CaptureMode mode)
    {
        var (roll, frame, recipe) = await Roll(mode);
        recipe = recipe with { CropLeft = .1, CropRight = .1, CropTop = .1, CropBottom = .1,
            FilmBaseRed = 1.3, FilmBaseGreen = .8, FilmBaseBlue = 1.2 };
        var changed = await FrameCalibration.SampleAsync(roll, frame, recipe,
            CalibrationTarget.FilmBase, .3, .4, root, null, default);
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), null);
        var reconstruction = await ReferenceReconstruction.ReconstructAsync(sources, roll, frame, changed, true, FrameSources.FullResolution, default);
        var (left, top, width, height) = ReferenceReconstruction.Crop(reconstruction.Image, recipe);
        int px = left + (int)(.3 * width), py = top + (int)(.4 * height);
        for (int c = 0; c < 3; c++)
        {
            double average = 0;
            for (int y = py - 2; y <= py + 2; y++)
            for (int x = px - 2; x <= px + 2; x++)
                average += reconstruction.Image.Pixels[(y * reconstruction.Image.Width + x) * 3 + c] / 25;
            double multiplier = new[] { recipe.FilmBaseRed, recipe.FilmBaseGreen, recipe.FilmBaseBlue }[c];
            Assert.Equal(average * multiplier, reconstruction.FilmBase[c], 5);
        }
        var repeated = await FrameCalibration.SampleAsync(roll, frame, changed,
            CalibrationTarget.FilmBase, .3, .4, root, null, default);
        Assert.Equal(changed, repeated);
        Assert.Equal(recipe.RedGain, changed.RedGain);
    }

    [Theory]
    [InlineData(CaptureMode.White)]
    [InlineData(CaptureMode.Rgb)]
    public async Task AutomaticCalibrationIsMeasuredIndependentlyForDifferentFrameExposures(CaptureMode mode)
    {
        var first = await Capture(CapturePurpose.Frame, mode, 1);
        var second = await Capture(CapturePurpose.Frame, mode, 2);
        foreach (var frame in new[] { first, second })
        foreach (var file in frame.Files)
        {
            var image = PerforatedFilm(false);
            if (frame == second)
                for (int i = 0; i < image.Pixels.Length; i++) image.Pixels[i] *= .5f;
            ProcessingImages.WriteTiff(file.Path, image, true,
                ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel, default,
                ProcessingImages.LinearRec2020Profile());
        }
        var roll = new RollDocument { IsDemo = true, FilmKind = FilmKind.ColorNegative, Captures = [first, second] };
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), null);
        var a = await FrameCalibration.PrepareAsync(sources, roll, first, new(), root, default);
        var b = await FrameCalibration.PrepareAsync(sources, roll, second, a.Recipe, root, default);
        Assert.Null(a.Warning);
        Assert.Null(b.Warning);
        Assert.NotEqual(a.Recipe.Id, b.Recipe.Id);
        Assert.Equal(first.Id, a.Recipe.FrameId);
        Assert.Equal(second.Id, b.Recipe.FrameId);
        Assert.Equal(first.Id, a.Recipe.PictureCalibration!.Light!.FrameId);
        Assert.Equal(second.Id, b.Recipe.PictureCalibration!.FilmBase!.FrameId);
        Assert.Equal(.8, a.Recipe.PictureCalibration.Light.Red, 5);
        Assert.Equal(.4, b.Recipe.PictureCalibration.Light!.Red, 5);
        var original = await ReferenceReconstruction.ReconstructAsync(sources, roll, first, a.Recipe, true,
            FrameSources.FullResolution, default);
        var changed = await ReferenceReconstruction.ReconstructAsync(sources, roll, second, b.Recipe, true,
            FrameSources.FullResolution, default);
        Assert.Equal(original.Image.Pixels, changed.Image.Pixels);
        Assert.Equal(original.FilmBase, changed.FilmBase);
        var repicked = await FrameCalibration.SampleAsync(sources, roll, second, a.Recipe,
            CalibrationTarget.Backlight, .1, .1, default);
        Assert.Equal(second.Id, repicked.PictureCalibration!.Light!.FrameId);
        Assert.Null(repicked.PictureCalibration.FilmBase);
        Assert.Equal(first.Id, a.Recipe.PictureCalibration.FilmBase!.FrameId);
    }

    [Fact]
    public async Task MissingSprocketsProduceAnExplicitFrameLocalProvisionalPreview()
    {
        var frame = await Capture(CapturePurpose.Frame, CaptureMode.White, 1);
        var image = new LinearImage(384, 256, Enumerable.Repeat(.2f, 384 * 256 * 3).ToArray());
        ProcessingImages.WriteTiff(frame.Files[0].Path, image, true,
            ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel, default,
            ProcessingImages.LinearRec2020Profile());
        var roll = new RollDocument { IsDemo = true, Captures = [frame] };
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), null);
        var prepared = await FrameCalibration.PrepareAsync(sources, roll, frame, new(), root, default);
        Assert.Contains("Automatic calibration failed", prepared.Warning);
        Assert.Equal(frame.Id, prepared.Recipe.FrameId);
        Assert.False(prepared.Recipe.PictureCalibration!.IsComplete(roll.FilmKind));
        var result = await new DemoFrameProcessor(sources).PreviewAsync(roll, frame, prepared.Recipe, root, null, default);
        Assert.True(File.Exists(result.SourceImagePath));
        Assert.Contains(result.Warnings, warning => warning.Contains("uncalibrated", StringComparison.OrdinalIgnoreCase));
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
    public async Task PreviewAndExportUseSameRecipeAndValidatePngJpegAnd16BitRgbTiff()
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
        Assert.EndsWith(".png", preview.ImagePath);
        ProcessingImages.ValidatePng(preview.ImagePath, 346, 205, true);
        ProcessingImages.ValidateJpeg(export.OutputFiles[1], 346, 205, false);
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
        Assert.DoesNotContain(Directory.GetDirectories(root), d => File.Exists(Path.Combine(d, "SYNTHETIC-preview.png")));
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
    public async Task CancelingAtOutputProgressRetainsDiagnosticsWithoutReportingPublication()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.White);
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProcessingProgress(p =>
        {
            if (p.Stage == "Saving positive images") cancel.Cancel();
        });
        var directory = Path.Combine(root, "canceled-progress");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DemoFrameProcessor()
            .PreviewAsync(roll, frame, recipe, directory, progress, cancel.Token));
        Assert.DoesNotContain(progress.Events, p => p.Stage == "Publishing output");
        var job = Assert.Single(Directory.GetDirectories(directory));
        Assert.EndsWith(".partial", job);
        Assert.True(File.Exists(Path.Combine(job, "failure.txt")));
    }

    [Fact]
    public async Task MissingOrFailingCliNeverPublishesSuccess()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.White);
        string output = Path.Combine(root, "failed");
        var progress = new RecordingProcessingProgress();
        await Assert.ThrowsAnyAsync<Exception>(() => new DarktableFrameProcessor(
            Path.Combine(root, "missing-darktable-cli.exe")).ExportAsync(roll, frame, recipe, output, progress, default));
        Assert.Equal(["Preparing conversion", "Checking processor"], progress.Events.Select(p => p.Stage));
        string job = Assert.Single(Directory.GetDirectories(output, ".process-*.partial"));
        Assert.EndsWith(".partial", job);
        Assert.True(File.Exists(Path.Combine(job, "failure.txt")));

        // A real, harmless Windows process exits nonzero; no invented CLI success path.
        string failExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        await Assert.ThrowsAsync<IOException>(() => new DarktableFrameProcessor(failExe).ExportAsync(
            roll, frame, recipe, Path.Combine(root, "nonzero"), null, default));
        Assert.All(Directory.GetDirectories(Path.Combine(root, "nonzero"), ".process-*.partial"), directory =>
        {
            Assert.EndsWith(".partial", directory);
            Assert.True(File.Exists(Path.Combine(directory, "failure.txt")));
        });
        Assert.False(Directory.Exists(Path.Combine(root, "nonzero", "camera-input-cache")));
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
    public async Task PreviewCachesOnlyInversionAcrossProcessorsAndExportAlwaysRunsOneConversion()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.White);
        using var sources = new FrameSources(Path.Combine(root, "camera-input-cache"), Cli);
        Task<PreviewResult> Preview(DevelopRecipe settings) =>
            new DarktableFrameProcessor(Cli, sources).PreviewAsync(roll, frame, settings, root, null, default);
        static int Conversions(string path) => Directory.GetFiles(path, "*.command.txt").Length;
        var cold = await Preview(recipe);
        string coldJob = Path.GetDirectoryName(cold.ImagePath)!;
        Assert.Equal(1, Conversions(coldJob));
        Assert.False(File.Exists(Path.Combine(coldJob, "SYNTHETIC-master.tif")));
        Assert.False(File.Exists(Path.Combine(coldJob, "positive-linear.tif")));
        ProcessingImages.ValidatePng(cold.ImagePath, 384, 256, true);
        var same = await Preview(recipe);
        Assert.Equal(0, Conversions(Path.GetDirectoryName(same.ImagePath)!));
        Assert.Equal(File.ReadAllBytes(cold.ImagePath), File.ReadAllBytes(same.ImagePath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(same.ImagePath)!, "develop.xmp")));
        var changed = await Preview(recipe with
        {
            ExposureEv = .5, RedGain = 1.1, GreenGain = .9, BlueGain = 1.2, BlackPoint = .01, WhitePoint = .9
        });
        Assert.Equal(0, Conversions(Path.GetDirectoryName(changed.ImagePath)!));
        Assert.NotEqual(File.ReadAllBytes(cold.ImagePath), File.ReadAllBytes(changed.ImagePath));
        var baseChanged = await Preview(recipe with { FilmBaseRed = 1.1 });
        Assert.Equal(1, Conversions(Path.GetDirectoryName(baseChanged.ImagePath)!));
        var calibrated = await FrameCalibration.SampleAsync(roll, frame, recipe,
            CalibrationTarget.Backlight, 3.0 / 384, .5, root, null, default);
        calibrated = await FrameCalibration.SampleAsync(roll, frame, calibrated,
            CalibrationTarget.FilmBase, 12.0 / 384, .5, root, null, default);
        var calibrationChanged = await Preview(calibrated);
        Assert.Equal(1, Conversions(Path.GetDirectoryName(calibrationChanged.ImagePath)!));
        string[] entries = Directory.GetFiles(Path.Combine(root, ".develop-cache"), "*.pfm");
        Assert.Equal(3, entries.Length);
        foreach (string entry in entries) File.WriteAllBytes(entry, [0]);
        var repaired = await Preview(recipe);
        Assert.Equal(1, Conversions(Path.GetDirectoryName(repaired.ImagePath)!));
        Assert.Equal(File.ReadAllBytes(cold.ImagePath), File.ReadAllBytes(repaired.ImagePath));
        var beforeExport = Directory.GetFiles(Path.Combine(root, ".develop-cache"), "*.pfm")
            .ToDictionary(path => path, File.GetLastWriteTimeUtc);
        var export = await new DarktableFrameProcessor(Cli, sources).ExportAsync(roll, frame, recipe, root, null, default);
        string exportJob = Path.GetDirectoryName(export.OutputFiles[0])!;
        Assert.Equal(1, Conversions(exportJob));
        Assert.False(File.Exists(Path.Combine(exportJob, "positive-linear.tif")));
        Assert.False(File.Exists(Path.Combine(exportJob, "output-positive.xmp")));
        Assert.Equal(beforeExport.OrderBy(p => p.Key), Directory.GetFiles(Path.Combine(root, ".develop-cache"), "*.pfm")
            .ToDictionary(path => path, File.GetLastWriteTimeUtc).OrderBy(p => p.Key));
    }

    [InstalledDarktableFact]
    public async Task LosslessPreviewColorMatchesDarktableSrgbWithinOneLevelAtP99()
    {
        var (roll, frame, recipe) = await Roll(CaptureMode.White);
        var result = await new DarktableFrameProcessor(Cli).PreviewAsync(roll, frame, recipe, root, null, default);
        string job = Path.GetDirectoryName(result.ImagePath)!;
        var image = ReferenceReconstruction.ApplyControls(
            ProcessingImages.ReadLinear(Path.Combine(job, "developed-linear.tif"), default), recipe, default);
        ProcessingImages.WriteTiff(Path.Combine(job, "positive-linear.tif"), image, true, ProcessingImages.LinearLabel,
            default, ProcessingImages.LinearRec2020Profile());
        NegadoctorXmp.Write(Path.Combine(job, "output-positive.xmp"), recipe, [1, 1, 1], false);
        await new DarktableProcess(Cli).RunAsync(["positive-linear.tif", "output-positive.xmp", "reference.png",
            "--icc-type", "SRGB", "--icc-intent", "RELATIVE_COLORIMETRIC", "--apply-custom-presets", "0",
            "--core", "--configdir", Path.Combine(job, "reference-config"), "--cachedir", Path.Combine(job, "reference-cache"),
            "--disable-opencl", "--conf", "plugins/imageio/format/png/bpp=8", "--conf", "write_sidecar_files=never"],
            job, "reference", default);
        using var expected = new Bitmap(Path.Combine(job, "reference.png"));
        using var actual = new Bitmap(result.ImagePath);
        var errors = new List<int>(image.Width * image.Height * 3);
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            var a = actual.GetPixel(x, y); var b = expected.GetPixel(x, y);
            errors.Add(Math.Abs(a.R - b.R)); errors.Add(Math.Abs(a.G - b.G)); errors.Add(Math.Abs(a.B - b.B));
        }
        errors.Sort();
        Assert.True(errors.Average() <= .5, $"Mean: {errors.Average()}");
        Assert.True(errors[(int)(errors.Count * .99)] <= 1, $"p99: {errors[(int)(errors.Count * .99)]}");
        Assert.True(errors[^1] <= 3, $"Max: {errors[^1]}");
    }

    [Fact]
    public void PreviewPngValidationRejectsWrongGeometryMissingProfileAndCorruption()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "preview.png");
        ProcessingImages.WritePreviewPng(path, new(2, 1, [0, .1f, 1, .2f, .5f, .8f]), default);
        ProcessingImages.ValidatePng(path, 2, 1, true);
        Assert.Throws<InvalidDataException>(() => ProcessingImages.ValidatePng(path, 3, 1, true));
        byte[] valid = File.ReadAllBytes(path);
        // IHDR occupies 25 bytes after the signature; remove the following complete iCCP chunk.
        int profileLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(valid.AsSpan(33)) + 12;
        string unprofiled = Path.Combine(root, "unprofiled.png");
        File.WriteAllBytes(unprofiled, valid[..33].Concat(valid[(33 + profileLength)..]).ToArray());
        ProcessingImages.ValidatePng(unprofiled, 2, 1, false);
        Assert.Throws<InvalidDataException>(() => ProcessingImages.ValidatePng(unprofiled, 2, 1, true));
        valid[0] = 0;
        File.WriteAllBytes(path, valid);
        Assert.Throws<InvalidDataException>(() => ProcessingImages.ValidatePng(path, 2, 1, true));
        valid[0] = 137;
        valid[^5] ^= 1;
        File.WriteAllBytes(path, valid);
        Assert.Throws<InvalidDataException>(() => ProcessingImages.ValidatePng(path, 2, 1, true));
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
        string job = Assert.Single(Directory.GetDirectories(output, ".process-*.partial"));
        Assert.EndsWith(".partial", job);
        Assert.True(File.Exists(Path.Combine(job, "failure.txt")));
    }

    private async Task<(RollStore Store, RollCapture Frame, DevelopRecipe Recipe)> ClippedBacklightFrame(
        CaptureMode mode, bool partial, FilmKind filmKind = FilmKind.ColorNegative)
    {
        var capture = await Capture(CapturePurpose.Frame, mode, 1);
        foreach (var file in capture.Files)
        {
            var image = ProcessingImages.ReadLinear(file.Path, default, true);
            for (int y = 126; y <= 130; y++)
            for (int x = 1; x <= 5; x++)
            {
                if (partial && (x != 3 || y != 128)) continue;
                image.Pixels[(y * image.Width + x) * 3] = 1.05f;
            }
            ProcessingImages.WriteTiff(file.Path, image, true,
                ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel,
                default, ProcessingImages.LinearRec2020Profile());
        }
        var store = RollStore.Create(Path.Combine(root, "roll"), "Clipped WB", filmKind, true);
        var frame = store.AddCapture(CapturePurpose.Frame, mode, capture.Files, true);
        var recipe = new DevelopRecipe { FilmKind = filmKind, PictureCalibration = new() };
        if (filmKind != FilmKind.Positive)
            recipe = await FrameCalibration.SampleAsync(store.Document, frame, recipe,
                CalibrationTarget.FilmBase, 12.0 / 384, .5, root, null, default);
        return (store, frame, recipe);
    }

    private async Task<(RollDocument Roll, RollCapture Frame, DevelopRecipe Recipe)> Roll(CaptureMode mode)
    {
        var light = await Capture(CapturePurpose.LightReference, mode, 0);
        var basis = await Capture(CapturePurpose.FilmBaseReference, mode, 0);
        var frame = await Capture(CapturePurpose.Frame, mode, 7);
        return (new RollDocument { IsDemo = true, FilmKind = FilmKind.ColorNegative, Captures = [light, basis, frame] },
            frame, new DevelopRecipe { FilmKind = FilmKind.ColorNegative });
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

internal sealed class RecordingProcessingProgress(Action<ProcessingProgress>? onReport = null) : IProgress<ProcessingProgress>
{
    internal List<ProcessingProgress> Events { get; } = [];
    public void Report(ProcessingProgress value)
    {
        Events.Add(value);
        onReport?.Invoke(value);
    }
}
