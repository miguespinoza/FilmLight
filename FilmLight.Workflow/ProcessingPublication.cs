using System.Runtime.Versioning;
using System.Text.Json;

namespace FilmLight.Workflow;

[SupportedOSPlatform("windows")]
internal static class ProcessingPublication
{
    internal static async Task<FrameProcessingResult> RunAsync(string directory, RollCapture frame,
        DevelopRecipe recipe, bool synthetic, IReadOnlyList<string> warnings,
        DevelopIntent intent, Func<string, Task<LinearImage>> render,
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
            var paths = Paths(staging, synthetic, intent);
            token.ThrowIfCancellationRequested();
            progress?.Report(new(frame.Id, "Validating output", intent == DevelopIntent.Preview
                ? "Checking the saved PNG before publishing." : "Checking the saved TIFF and JPEG before publishing."));
            if (intent == DevelopIntent.Preview)
                ProcessingImages.ValidatePng(paths[0], image.Width, image.Height, true);
            else
            {
                ProcessingImages.ValidateMaster(paths[0], image.Width, image.Height, !synthetic, token);
                ProcessingImages.ValidateJpeg(paths[1], image.Width, image.Height, !synthetic);
            }
            token.ThrowIfCancellationRequested();
            string published = Path.Combine(directory, name);
            progress?.Report(new(frame.Id, "Publishing output", "Making the completed positive images available."));
            Directory.Move(staging, published);
            return new(paths.Select(path => Path.Combine(published, Path.GetFileName(path))).ToArray(), warnings);
        }
        catch (Exception error)
        {
            // Keep the entire private job (including logs/XMP) for diagnosis, never touch source captures.
            File.WriteAllText(Path.Combine(staging, "failure.txt"), error.ToString());
            throw;
        }

    }

    internal static string[] Paths(string directory, bool synthetic, DevelopIntent intent)
    {
        string prefix = synthetic ? "SYNTHETIC-" : "";
        return intent == DevelopIntent.Preview
            ? [Path.Combine(directory, prefix + "preview.png")]
            : [Path.Combine(directory, prefix + "master.tif"), Path.Combine(directory, prefix + "preview.jpg")];
    }
}
