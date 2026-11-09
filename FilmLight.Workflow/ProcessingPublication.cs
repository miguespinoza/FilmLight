using System.Runtime.Versioning;
using System.Text.Json;

namespace FilmLight.Workflow;

[SupportedOSPlatform("windows")]
internal static class ProcessingPublication
{
    internal static async Task<FrameProcessingResult> RunAsync(string directory, RollCapture frame,
        DevelopRecipe recipe, bool synthetic, IReadOnlyList<string> warnings,
        Func<string, Task<LinearImage>> render,
        Func<string, LinearImage, string, string, Task> encode,
        IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        string name = $"{(synthetic ? "SYNTHETIC-" : "")}frame-{frame.Number:D4}-r{recipe.Revision}-{Guid.NewGuid():N}";
        string staging = Path.Combine(directory, $".process-{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(staging, "recipe.json"),
                JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true }), token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, "processing.json"),
                JsonSerializer.Serialize(new { Synthetic = synthetic, FrameId = frame.Id, Warnings = warnings }), token).ConfigureAwait(false);
            progress?.Report(new(frame.Id, "Preparing conversion", synthetic
                ? "Preparing synthetic input; this is not a real-camera conversion."
                : "Preparing the positive conversion. Original captures remain untouched."));
            var image = await render(staging).ConfigureAwait(false);
            string tiff = Path.Combine(staging, synthetic ? "SYNTHETIC-master.tif" : "master.tif");
            string jpeg = Path.Combine(staging, synthetic ? "SYNTHETIC-preview.jpg" : "preview.jpg");
            progress?.Report(new(frame.Id, "Saving positive images", "Writing the TIFF master and JPEG companion."));
            await encode(staging, image, tiff, jpeg).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            progress?.Report(new(frame.Id, "Validating output", "Checking the saved TIFF and JPEG before publishing."));
            ProcessingImages.ValidateMaster(tiff, image.Width, image.Height, !synthetic, token);
            ProcessingImages.ValidateJpeg(jpeg, image.Width, image.Height, !synthetic);
            token.ThrowIfCancellationRequested();
            string published = Path.Combine(directory, name);
            progress?.Report(new(frame.Id, "Publishing output", "Making the completed positive images available."));
            Directory.Move(staging, published);
            return new(new[] { Path.Combine(published, Path.GetFileName(tiff)), Path.Combine(published, Path.GetFileName(jpeg)) }, warnings);
        }
        catch (Exception error)
        {
            // Keep the entire private job (including logs/XMP) for diagnosis, never touch source captures.
            File.WriteAllText(Path.Combine(staging, "failure.txt"), error.ToString());
            throw;
        }
    }
}
