using System.Runtime.Versioning;

namespace FilmLight.Workflow;

public enum CalibrationTarget { Backlight, FilmBase }

[SupportedOSPlatform("windows")]
public static class FrameCalibration
{
    public static async Task<DevelopRecipe> AutoSampleAsync(RollDocument roll, RollCapture frame,
        DevelopRecipe recipe, string workDirectory, string? darktableCliPath, CancellationToken token,
        IProgress<ProcessingProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        ReferenceReconstruction.ValidateRecipe(recipe);
        var pending = recipe with { PictureCalibration = new() };
        (roll, frame) = await PrepareAsync(roll, frame, pending, workDirectory, darktableCliPath, token, progress)
            .ConfigureAwait(false);
        progress?.Report(new(frame.Id, "Finding sprocket samples",
            "Detecting repeated perforations in the uncropped linear input and measuring light / clear film."));
        return await Task.Run(() => ReferenceReconstruction.AutoSample(roll, frame, pending, token), token)
            .ConfigureAwait(false);
    }

    public static async Task<DevelopRecipe> SampleAsync(RollDocument roll, RollCapture frame,
        DevelopRecipe recipe, CalibrationTarget target, double x, double y, string workDirectory,
        string? darktableCliPath, CancellationToken token, IProgress<ProcessingProgress>? progress = null)
    {
        if (!Enum.IsDefined(target) || !double.IsFinite(x) || !double.IsFinite(y)
            || x < 0 || x >= 1 || y < 0 || y >= 1)
            throw new ArgumentOutOfRangeException(nameof(x), "Pick a point inside the displayed image.");
        if (target == CalibrationTarget.FilmBase && recipe.FilmKind == FilmKind.Positive)
            throw new InvalidOperationException("Positive film has no negative film base to measure.");
        (roll, frame) = await PrepareAsync(roll, frame, recipe, workDirectory, darktableCliPath, token, progress)
            .ConfigureAwait(false);
        progress?.Report(new(frame.Id, "Sampling picture", "Measuring the selected patch in the full-resolution linear input."));
        return await Task.Run(() => ReferenceReconstruction.Sample(roll, frame, recipe, target, x, y, token),
            token).ConfigureAwait(false);
    }

    private static async Task<(RollDocument Roll, RollCapture Frame)> PrepareAsync(RollDocument roll,
        RollCapture frame, DevelopRecipe recipe, string workDirectory, string? darktableCliPath,
        CancellationToken token, IProgress<ProcessingProgress>? progress)
    {
        token.ThrowIfCancellationRequested();
        if (!roll.IsDemo)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(darktableCliPath);
            roll = await new CameraInputDecoder(new DarktableProcess(darktableCliPath))
                .DecodeAsync(roll, frame, workDirectory, token, recipe, progress).ConfigureAwait(false);
            frame = roll.Captures.Single(c => c.Id == frame.Id);
        }
        return (roll, frame);
    }

    public static bool TryMapPoint(double viewportWidth, double viewportHeight, double imageWidth,
        double imageHeight, double pointerX, double pointerY, out double x, out double y)
    {
        x = y = 0;
        if (new[] { viewportWidth, viewportHeight, imageWidth, imageHeight }.Any(v => !double.IsFinite(v) || v <= 0)
            || !double.IsFinite(pointerX) || !double.IsFinite(pointerY))
            return false;
        double scale = Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight);
        double width = imageWidth * scale, height = imageHeight * scale;
        x = (pointerX - (viewportWidth - width) / 2) / width;
        y = (pointerY - (viewportHeight - height) / 2) / height;
        return x >= 0 && x < 1 && y >= 0 && y < 1;
    }
}
