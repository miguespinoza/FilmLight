using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace FilmLight.Workflow;

/// <summary>
/// Runs pinned darktable 5.6.2 on RAFs or qualified linear TIFFs.
/// RAWs are decoded with fixed input settings and matching physical exposures before normalization.
/// Physical exposure, emitter/camera spectral calibration and registration remain hardware responsibilities.
/// FilmBase* multiply measured clear-film transmissions; fractional edge crop and positive-linear
/// exposure/gains/levels are shared by preview/export. Each job saves recipe.json and effective develop.xmp.
/// The roll coordinator must gate accepted exports and freeze any external XMP at acceptance.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DarktableFrameProcessor : IFrameProcessor
{
    private readonly DarktableProcess cli;
    private readonly string? cameraCacheDirectory;

    public DarktableFrameProcessor(string darktableCliPath, string? cameraCacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(darktableCliPath);
        cli = new(darktableCliPath);
        this.cameraCacheDirectory = cameraCacheDirectory;
    }

    public async Task<PreviewResult> PreviewAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        string workDirectory, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        var result = await ProcessAsync(roll, frame, recipe, workDirectory, progress, cancellationToken, true).ConfigureAwait(false);
        return new(result.OutputFiles[1], result.Warnings,
            Path.Combine(Path.GetDirectoryName(result.OutputFiles[1])!, "source-preview.jpg"));
    }

    public Task<FrameProcessingResult> ExportAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        string outputDirectory, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        recipe.PictureCalibration?.RequireComplete(recipe.FilmKind);
        return ProcessAsync(roll, frame, recipe, outputDirectory, progress, cancellationToken);
    }

    private Task<FrameProcessingResult> ProcessAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        string directory, IProgress<ProcessingProgress>? progress, CancellationToken token, bool forPreview = false)
    {
        var warnings = new List<string>
        {
            "Rendered positives, not reversible raw. RAW decoding uses a fixed standard-camera matrix and unity processing white balance, not adaptive per-frame camera WB.",
            "Keep camera exposure, focus and illumination fixed across the roll. No camera/hardware validation is implied."
        };
        if (roll.IsDemo) warnings.Add(ProcessingImages.SyntheticLabel);
        if (recipe.PictureCalibration is { } picture && !picture.IsComplete(recipe.FilmKind))
            warnings.Add("Provisional positive preview: pick exposed light and, for negatives, clear film in this picture. These picks will be used for every frame.");
        if (frame.Mode == CaptureMode.Rgb && recipe.PictureCalibration is not null)
            warnings.Add("RGB uses each selected primary divided by the picked light channel. Point samples correct global color/film base, not spatial light falloff or spectral crosstalk.");
        else if (frame.Mode == CaptureMode.Rgb)
            warnings.Add("RGB uses per-primary selected camera channel divided by its matching no-film reference, NOT HDR averaging. Spectral crosstalk/colorimetry requires measured hardware qualification; not colorimetric scanner output.");
        if (recipe.XmpTemplatePath is null && recipe.FilmKind != FilmKind.Positive)
            warnings.Add("Bootstrapped negadoctor v2 print parameters are starting values, not a calibrated film-stock profile; approve the supervised preview or supply a negadoctor-only calibrated XMP.");
        return ProcessingPublication.RunAsync(directory, frame, recipe, roll.IsDemo, warnings,
            async staging =>
            {
                void Report(string stage, string message) => progress?.Report(new(frame.Id, stage, message));
                Report("Checking processor", "Checking darktable before converting the image.");
                string version = await cli.RunAsync(["--version"], staging, "version", token).ConfigureAwait(false);
                if (!Regex.IsMatch(version, @"(?m)^darktable 5\.6\.2(?:\s|$)"))
                    throw new NotSupportedException("This negadoctor wire recipe is validated only for darktable 5.6.2. Install/select that version; no default-history fallback is permitted.");
                var decoded = await new CameraInputDecoder(cli).DecodeAsync(roll, frame,
                    cameraCacheDirectory ?? directory, token, recipe, progress).ConfigureAwait(false);
                var decodedFrame = decoded.Captures.Single(c => c.Id == frame.Id);
                Report("Normalizing film", "Applying the roll's light and film settings to the linear input.");
                var reconstruction = await Task.Run(() =>
                    ReferenceReconstruction.Reconstruct(decoded, decodedFrame, recipe, false, token), token).ConfigureAwait(false);
                var controls = recipe;
                if (forPreview)
                {
                    Report("Preparing preview", "Cropping and sizing the image, then preparing the source view.");
                    reconstruction = reconstruction with { Image = ProcessingImages.CropForPreview(reconstruction.Image, recipe, token) };
                    controls = recipe with { CropLeft = 0, CropTop = 0, CropRight = 0, CropBottom = 0 };
                    ProcessingImages.WriteSourcePreview(Path.Combine(staging, "source-preview.jpg"), reconstruction.Image, controls, token);
                }
                if (recipe.FilmKind == FilmKind.BlackAndWhiteNegative)
                {
                    // Normalize substrate color before monochrome inversion; negadoctor's B&W mode
                    // otherwise uses only Dmin[0] even when the measured scanner channels differ.
                    for (int i = 0; i < reconstruction.Image.Pixels.Length; i += 3)
                    {
                        if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                        float gray = (reconstruction.Image.Pixels[i] / reconstruction.FilmBase[0] +
                            reconstruction.Image.Pixels[i + 1] / reconstruction.FilmBase[1] +
                            reconstruction.Image.Pixels[i + 2] / reconstruction.FilmBase[2]) / 3;
                        reconstruction.Image.Pixels[i] = reconstruction.Image.Pixels[i + 1] = reconstruction.Image.Pixels[i + 2] = gray;
                    }
                    reconstruction = reconstruction with { FilmBase = [1, 1, 1] };
                }
                string input = Path.Combine(staging, "normalized-linear.tif");
                Report("Preparing positive conversion", "Writing normalized input and the develop recipe.");
                ProcessingImages.WriteTiff(input, reconstruction.Image, true,
                    (roll.IsDemo ? ProcessingImages.SyntheticLabel + "; " : "") + ProcessingImages.LinearLabel,
                    token, ProcessingImages.LinearRec2020Profile());
                string xmp = Path.Combine(staging, "develop.xmp");
                NegadoctorXmp.Write(xmp, recipe, reconstruction.FilmBase, recipe.FilmKind != FilmKind.Positive);
                // Freeze the effective XMP (including any external calibration) in this job before processing.
                string rendered = Path.Combine(staging, "developed-linear.tif");
                Report(recipe.FilmKind == FilmKind.Positive ? "Developing positive" : "Inverting negative",
                    recipe.FilmKind == FilmKind.Positive
                        ? "darktable is developing the positive image."
                        : "darktable is converting the negative to a positive with negadoctor.");
                string log = await ConvertAsync(input, xmp, rendered, "LIN_REC2020", 32, staging, "develop", token).ConfigureAwait(false);
                if (recipe.FilmKind != FilmKind.Positive && !Regex.IsMatch(log, @"(?im)^\s*\u25cf\s+negadoctor\b"))
                    throw new InvalidDataException("darktable did not report enabled negadoctor in its verbose history. No positive export will be published; inspect develop logs/XMP.");
                Report("Applying develop settings", "Reading the converted image and applying exposure, color and levels.");
                var image = ProcessingImages.ReadLinear(rendered, token);
                if (image.Width != reconstruction.Image.Width || image.Height != reconstruction.Image.Height)
                    throw new InvalidDataException("Unexpected darktable geometry; disable crop/orientation in the calibrated XMP.");
                return ReferenceReconstruction.ApplyControls(image, controls, token);
            },
            async (staging, image, tiff, jpeg) =>
            {
                progress?.Report(new(frame.Id, "Preparing positive output", "Writing the developed linear image for output encoding."));
                string positive = Path.Combine(staging, "positive-linear.tif");
                ProcessingImages.WriteTiff(positive, image, true,
                    (roll.IsDemo ? ProcessingImages.SyntheticLabel + "; " : "") + ProcessingImages.LinearLabel,
                    token, ProcessingImages.LinearRec2020Profile());
                string xmp = Path.Combine(staging, "output-positive.xmp");
                NegadoctorXmp.Write(xmp, recipe, [1, 1, 1], false);
                progress?.Report(new(frame.Id, "Encoding TIFF", "darktable is saving the 16-bit ProPhoto RGB master."));
                await ConvertAsync(positive, xmp, tiff, "PROPHOTO_RGB", 16, staging, "tiff", token).ConfigureAwait(false);
                progress?.Report(new(frame.Id, "Encoding JPEG", "darktable is saving the sRGB preview companion."));
                await ConvertAsync(positive, xmp, jpeg, "SRGB", 16, staging, "jpeg", token).ConfigureAwait(false);
                // Require profiles even on synthetic darktable output; the CLI path has the same guarantees.
                progress?.Report(new(frame.Id, "Checking color profiles", "Validating the encoded master and preview."));
                ProcessingImages.ValidateMaster(tiff, image.Width, image.Height, true, token);
                ProcessingImages.ValidateJpeg(jpeg, image.Width, image.Height, true);
            }, progress, token);
    }

    private Task<string> ConvertAsync(string input, string xmp, string output, string profile,
        int bits, string directory, string log, CancellationToken token)
    {
        string config = Path.Combine(directory, "config");
        Directory.CreateDirectory(config);
        // darktable treats backslashes in its output filename as pattern escapes. Relative job-local
        // filenames avoid that parser entirely without changing Windows path semantics.
        return cli.RunAsync([Path.GetFileName(input), Path.GetFileName(xmp), Path.GetFileName(output),
            "--icc-type", profile, "--icc-intent", "RELATIVE_COLORIMETRIC",
            "--apply-custom-presets", "0", "--verbose", "--core", "--configdir", config,
            "--cachedir", Path.Combine(directory, "cache"), "--disable-opencl",
            "--conf", $"plugins/imageio/format/tiff/bpp={bits}",
            "--conf", "plugins/imageio/format/tiff/compress=0",
            "--conf", "plugins/imageio/format/tiff/shortfile=0",
            "--conf", "plugins/imageio/format/jpeg/quality=95",
            "--conf", "write_sidecar_files=never"], directory, log, token);
    }
}
