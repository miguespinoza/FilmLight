using System.Runtime.Versioning;

namespace FilmLight.Workflow;

/// <summary>
/// Runs pinned darktable 5.6.2 on RAFs or qualified linear TIFFs.
/// Physical exposure, emitter/camera spectral calibration and registration remain hardware responsibilities.
/// Preview reuses content-keyed inversion results; export always develops and encodes a fresh master.
/// The roll coordinator must gate accepted exports and freeze any external XMP at acceptance.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DarktableFrameProcessor : IFrameProcessor
{
    private readonly string cliPath;
    private readonly string? cameraCacheDirectory;
    private readonly FrameSources? sources;

    public DarktableFrameProcessor(string darktableCliPath, string? cameraCacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(darktableCliPath);
        cliPath = darktableCliPath;
        this.cameraCacheDirectory = cameraCacheDirectory;
    }

    internal DarktableFrameProcessor(string darktableCliPath, FrameSources sources)
    {
        cliPath = darktableCliPath;
        this.sources = sources;
    }

    public async Task<PreviewResult> PreviewAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        string workDirectory, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        var result = await ProcessAsync(roll, frame, recipe, workDirectory, progress, cancellationToken, DevelopIntent.Preview).ConfigureAwait(false);
        return new(result.OutputFiles[0], result.Warnings,
            Path.Combine(Path.GetDirectoryName(result.OutputFiles[0])!, "source-preview.jpg"));
    }

    public Task<FrameProcessingResult> ExportAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        string outputDirectory, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        if (!recipe.FrameId.HasValue) recipe.PictureCalibration?.RequireComplete(recipe.FilmKind);
        return ProcessAsync(roll, frame, recipe, outputDirectory, progress, cancellationToken, DevelopIntent.Export);
    }

    private IReadOnlyList<IDevelopStep> Steps(DevelopIntent intent, FilmKind filmKind)
    {
        var steps = new List<IDevelopStep> { new ReconstructStep(false) };
        if (intent == DevelopIntent.Preview) steps.Add(new PreviewCropStep());
        if (filmKind == FilmKind.BlackAndWhiteNegative) steps.Add(new MonochromeBaseStep());
        steps.Add(new DarktableInversionStep(cliPath));
        steps.Add(new DevelopControlsStep());
        steps.Add(intent == DevelopIntent.Preview ? new PreviewEncodeStep() : new ExportEncodeStep());
        return steps;
    }

    private Task<FrameProcessingResult> ProcessAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        string directory, IProgress<ProcessingProgress>? progress, CancellationToken token, DevelopIntent intent)
    {
        var warnings = new List<string>
        {
            "Rendered positives, not reversible raw. RAW decoding uses a fixed standard-camera matrix and unity processing white balance, not adaptive per-frame camera WB.",
            "Each frame uses its own calibration. Camera focus mode is your choice; no camera/hardware validation is implied."
        };
        if (roll.IsDemo) warnings.Add(ProcessingImages.SyntheticLabel);
        if (recipe.PictureCalibration?.Light?.IsClipped == true)
            warnings.Add(PictureCalibration.ClippedBacklightWarning);
        if (recipe.PictureCalibration is { } picture && !picture.IsComplete(recipe.FilmKind))
            warnings.Add("Provisional / uncalibrated output for this frame: automatic calibration is incomplete. Auto-pick again or manually pick exposed light and, for negatives, clear film in this picture.");
        if (frame.Mode == CaptureMode.Rgb && recipe.PictureCalibration is not null)
            warnings.Add("RGB uses each selected primary, normalized only when the picked light is unclipped. Point samples correct global color/film base, not spatial light falloff or spectral crosstalk.");
        else if (frame.Mode == CaptureMode.Rgb)
            warnings.Add("RGB uses per-primary selected camera channel divided by its matching no-film reference, NOT HDR averaging. Spectral crosstalk/colorimetry requires measured hardware qualification; not colorimetric scanner output.");
        if (recipe.XmpTemplatePath is null && recipe.FilmKind != FilmKind.Positive)
            warnings.Add("Bootstrapped negadoctor v2 print parameters are starting values, not a calibrated film-stock profile; approve the supervised preview or supply a negadoctor-only calibrated XMP.");
        return ProcessingPublication.RunAsync(directory, frame, recipe, roll.IsDemo, warnings, intent,
            async staging =>
            {
                progress?.Report(new(frame.Id, "Checking processor", "Checking darktable before converting the image."));
                using var ownedSources = sources is null ?
                    new FrameSources(Path.Combine(cameraCacheDirectory ?? directory, "camera-input-cache"), cliPath) : null;
                var inputs = sources ?? ownedSources!;
                await inputs.GetVersionAsync(token).ConfigureAwait(false);
                var context = new DevelopContext(roll, frame, recipe, intent, inputs, staging, progress);
                var result = await DevelopPipeline.RunAsync(Steps(intent, recipe.FilmKind), context,
                    new(new(0, 0, []), [1, 1, 1], recipe), token).ConfigureAwait(false);
                return result.Image;
            }, progress, token);
    }
}
