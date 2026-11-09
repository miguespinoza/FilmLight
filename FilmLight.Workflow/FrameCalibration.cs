using System.Runtime.Versioning;

namespace FilmLight.Workflow;

public enum CalibrationTarget { Backlight, FilmBase }

[SupportedOSPlatform("windows")]
public static class FrameCalibration
{
    public static async Task<DevelopRecipe> SampleAsync(RollDocument roll, RollCapture frame,
        DevelopRecipe recipe, CalibrationTarget target, double x, double y, string workDirectory,
        string? darktableCliPath, CancellationToken token)
    {
        if (!Enum.IsDefined(target) || !double.IsFinite(x) || !double.IsFinite(y)
            || x < 0 || x >= 1 || y < 0 || y >= 1)
            throw new ArgumentOutOfRangeException(nameof(x), "Pick a point inside the displayed image.");
        if (target == CalibrationTarget.FilmBase && recipe.FilmKind == FilmKind.Positive)
            throw new InvalidOperationException("Positive film has no negative film base to measure.");
        if (!roll.IsDemo)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(darktableCliPath);
            roll = await new CameraInputDecoder(new DarktableProcess(darktableCliPath))
                .DecodeAsync(roll, frame, workDirectory, token).ConfigureAwait(false);
            frame = roll.Captures.Single(c => c.Id == frame.Id);
        }
        return await Task.Run(() => ReferenceReconstruction.Sample(roll, frame, recipe, target, x, y, token),
            token).ConfigureAwait(false);
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
