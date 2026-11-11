using System.Runtime.Versioning;

namespace FilmLight.Workflow;

/// <summary>
/// A synthetic workflow renderer, not a live-camera processing fallback.
/// FilmBase* multiply measured clear-film transmissions; crop values are fractional edge removals.
/// Exposure/gains/levels apply to positive linear samples, identically in preview and export.
/// Durable frame settings and sample snapshots are the roll coordinator's responsibility.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DemoFrameProcessor : IFrameProcessor
{
    private readonly FrameSources? sources;

    public DemoFrameProcessor() { }
    internal DemoFrameProcessor(FrameSources sources) => this.sources = sources;

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

    private Task<FrameProcessingResult> ProcessAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        string directory, IProgress<ProcessingProgress>? progress, CancellationToken token, DevelopIntent intent)
    {
        // Validate provenance before creating a job, even when callers accidentally choose the demo processor.
        if (!roll.IsDemo || !frame.IsDemo)
            throw new InvalidOperationException("DemoFrameProcessor rejects non-demo rolls and inputs; it is not a fallback for darktable.");
        var warnings = new List<string>
        {
            ProcessingImages.SyntheticLabel, "Simplified synthetic renderer; no real-camera, film-color or darktable validation."
        };
        if (recipe.PictureCalibration?.Light?.IsClipped == true)
            warnings.Add(PictureCalibration.ClippedBacklightWarning);
        if (recipe.PictureCalibration is { } picture && !picture.IsComplete(recipe.FilmKind))
            warnings.Add("Uncalibrated/provisional output for this frame. Auto-pick again or use its manual pickers.");
        return ProcessingPublication.RunAsync(directory, frame, recipe, true, warnings, intent,
            async staging =>
            {
                using var ownedSources = sources is null ? new FrameSources(Path.Combine(directory, "camera-input-cache"), null) : null;
                progress?.Report(new(frame.Id, "Rendering synthetic positive", "Applying roll settings to the simulated input."));
                var context = new DevelopContext(roll, frame, recipe, intent, sources ?? ownedSources!, staging, progress);
                var result = await DevelopPipeline.RunAsync(Steps(intent), context,
                    new(new(0, 0, []), [1, 1, 1], recipe), token).ConfigureAwait(false);
                return result.Image;
            }, progress, token);
    }

    private static IReadOnlyList<IDevelopStep> Steps(DevelopIntent intent) => intent == DevelopIntent.Preview
        ? [new ReconstructStep(true), new PreviewCropStep(), new DemoInversionStep(), new DevelopControlsStep(), new PreviewEncodeStep()]
        : [new ReconstructStep(true), new DemoInversionStep(), new DevelopControlsStep(), new DemoExportEncodeStep()];
}
