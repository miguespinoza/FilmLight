using System.Runtime.Versioning;

namespace FilmLight.Workflow;

/// <summary>
/// A synthetic workflow renderer, not a live-camera processing fallback.
/// FilmBase* multiply measured clear-film transmissions; crop values are fractional edge removals.
/// Exposure/gains/levels apply to positive linear samples, identically in preview and export.
/// Acceptance and durable recipe/reference snapshots are the roll coordinator's responsibility.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DemoFrameProcessor : IFrameProcessor
{
    public async Task<PreviewResult> PreviewAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        string workDirectory, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        var result = await ProcessAsync(roll, frame, recipe, workDirectory, progress, cancellationToken, true).ConfigureAwait(false);
        return new(result.OutputFiles[1], result.Warnings,
            Path.Combine(Path.GetDirectoryName(result.OutputFiles[1])!, "source-preview.jpg"));
    }

    public Task<FrameProcessingResult> ExportAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        string outputDirectory, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) =>
        ProcessAsync(roll, frame, recipe, outputDirectory, progress, cancellationToken);

    private static Task<FrameProcessingResult> ProcessAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        string directory, IProgress<ProcessingProgress>? progress, CancellationToken token, bool forPreview = false)
    {
        // Validate provenance before creating a job, even when callers accidentally choose the demo processor.
        if (!roll.IsDemo || !frame.IsDemo)
            throw new InvalidOperationException("DemoFrameProcessor rejects non-demo rolls and inputs; it is not a fallback for darktable.");
        string[] warnings = [ProcessingImages.SyntheticLabel, "Simplified synthetic renderer; no real-camera, film-color or darktable validation."];
        return ProcessingPublication.RunAsync(directory, frame, recipe, true, warnings,
            staging => Task.Run(() =>
            {
                var reconstruction = ReferenceReconstruction.Reconstruct(roll, frame, recipe, true, token);
                var image = forPreview ? ProcessingImages.CropForPreview(reconstruction.Image, recipe, token) : reconstruction.Image;
                var controls = forPreview ? recipe with { CropLeft = 0, CropTop = 0, CropRight = 0, CropBottom = 0 } : recipe;
                if (forPreview) ProcessingImages.WriteSourcePreview(Path.Combine(staging, "source-preview.jpg"), image, controls, token);
                if (recipe.FilmKind != FilmKind.Positive)
                {
                    for (int i = 0; i < image.Pixels.Length; i++)
                    {
                        if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                        double baseValue = reconstruction.FilmBase[i % 3];
                        image.Pixels[i] = (float)Math.Clamp(Math.Log10(baseValue / Math.Max(image.Pixels[i], 1e-10)) / 1.6, 0, 1);
                    }
                    if (recipe.FilmKind == FilmKind.BlackAndWhiteNegative)
                        for (int i = 0; i < image.Pixels.Length; i += 3)
                        {
                            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                            float gray = (image.Pixels[i] + image.Pixels[i + 1] + image.Pixels[i + 2]) / 3;
                            image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = gray;
                        }
                }
                return ReferenceReconstruction.ApplyControls(image, controls, token);
            }, token),
            (_, image, tiff, jpeg) => Task.Run(() =>
            {
                ProcessingImages.WriteTiff(tiff, image, false, ProcessingImages.SyntheticLabel, token);
                ProcessingImages.WriteJpeg(jpeg, image, token);
            }, token), progress, token);
    }
}
