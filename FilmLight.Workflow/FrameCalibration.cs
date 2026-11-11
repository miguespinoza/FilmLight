using System.Runtime.Versioning;

namespace FilmLight.Workflow;

public enum CalibrationTarget { Backlight, FilmBase }

[SupportedOSPlatform("windows")]
public static class FrameCalibration
{
    private static readonly IReadOnlyList<ICalibrationStep> AutomaticSteps =
        [new SprocketDetectionStep(), new ClippingVerificationStep()];

    internal static Task<(DevelopRecipe Recipe, string? Warning)> PrepareAsync(FrameSources sources,
        RollDocument roll, RollCapture frame, DevelopRecipe recipe, string workDirectory, CancellationToken token,
        IProgress<ProcessingProgress>? progress = null) =>
        PrepareAsync(AutomaticSteps, sources, roll, frame, recipe, workDirectory, token, progress);

    internal static async Task<(DevelopRecipe Recipe, string? Warning)> PrepareAsync(IReadOnlyList<ICalibrationStep> steps,
        FrameSources sources, RollDocument roll, RollCapture frame, DevelopRecipe recipe, string workDirectory,
        CancellationToken token, IProgress<ProcessingProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var local = recipe.ForFrame(frame.Id);
        if (local.PictureCalibration is not null) return (local, null);
        try
        {
            return (await AutoSampleAsync(steps, sources, roll, frame, local, workDirectory, token, progress).ConfigureAwait(false), null);
        }
        catch (InvalidDataException error)
        {
            return (local with { PictureCalibration = new() },
                $"Automatic calibration failed for frame {frame.Number}: {error.Message} " +
                "Showing an uncalibrated preview. Auto-pick again or use this frame's manual pickers.");
        }
    }

    public static async Task<DevelopRecipe> AutoSampleAsync(RollDocument roll, RollCapture frame,
        DevelopRecipe recipe, string workDirectory, string? darktableCliPath, CancellationToken token,
        IProgress<ProcessingProgress>? progress = null)
    {
        using var sources = new FrameSources(Path.Combine(workDirectory, "camera-input-cache"), darktableCliPath);
        return await AutoSampleAsync(sources, roll, frame, recipe, workDirectory, token, progress).ConfigureAwait(false);
    }

    internal static Task<DevelopRecipe> AutoSampleAsync(FrameSources sources, RollDocument roll, RollCapture frame,
        DevelopRecipe recipe, string workDirectory, CancellationToken token, IProgress<ProcessingProgress>? progress = null) =>
        AutoSampleAsync(AutomaticSteps, sources, roll, frame, recipe, workDirectory, token, progress);

    private static async Task<DevelopRecipe> AutoSampleAsync(IReadOnlyList<ICalibrationStep> steps, FrameSources sources,
        RollDocument roll, RollCapture frame, DevelopRecipe recipe, string workDirectory, CancellationToken token,
        IProgress<ProcessingProgress>? progress)
    {
        token.ThrowIfCancellationRequested();
        ReferenceReconstruction.ValidateRecipe(recipe);
        var pending = recipe.ForFrame(frame.Id) with { PictureCalibration = new() };
        ReferenceReconstruction.ValidateFrame(roll, frame, pending, roll.IsDemo);
        string logPath = Path.GetFullPath(Path.Combine(workDirectory, "calibration-diagnostics",
            $"auto-{frame.Id:N}-{Guid.NewGuid():N}.log"));
        var diagnostics = new List<string>
        {
            $"Auto-pick frame {frame.Id}; mode={frame.Mode}; film={recipe.FilmKind}; time={DateTimeOffset.UtcNow:O}",
            "Sampling the uncropped linear input, before develop exposure or color adjustments."
        };
        DevelopRecipe result;
        try
        {
            var context = new CalibrationContext(roll, frame, recipe.FilmKind, sources.Open(roll, frame), diagnostics.Add, progress);
            var state = await CalibrationPipeline.RunAsync(steps, context, new(new()), token).ConfigureAwait(false);
            if (!state.Picture.IsComplete(recipe.FilmKind))
                throw new InvalidDataException("Automatic calibration did not produce exposed light and the required clear film samples.");
            result = pending with { PictureCalibration = state.Picture };
        }
        catch (InvalidDataException error)
        {
            diagnostics.Add("Result: rejected. " + error.Message);
            await SaveDiagnosticsAsync().ConfigureAwait(false);
            throw new InvalidDataException($"{error.Message} Diagnostics: {logPath}", error);
        }
        diagnostics.Add("Result: measured for this frame.");
        await SaveDiagnosticsAsync().ConfigureAwait(false);
        return result;

        async Task SaveDiagnosticsAsync()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            await File.WriteAllLinesAsync(logPath, diagnostics, token).ConfigureAwait(false);
        }
    }

    public static async Task<DevelopRecipe> SampleAsync(RollDocument roll, RollCapture frame,
        DevelopRecipe recipe, CalibrationTarget target, double x, double y, string workDirectory,
        string? darktableCliPath, CancellationToken token, IProgress<ProcessingProgress>? progress = null)
    {
        using var sources = new FrameSources(Path.Combine(workDirectory, "camera-input-cache"), darktableCliPath);
        return await SampleAsync(sources, roll, frame, recipe, target, x, y, token, progress).ConfigureAwait(false);
    }

    internal static async Task<DevelopRecipe> SampleAsync(FrameSources sources, RollDocument roll, RollCapture frame,
        DevelopRecipe recipe, CalibrationTarget target, double x, double y, CancellationToken token,
        IProgress<ProcessingProgress>? progress = null)
    {
        if (!Enum.IsDefined(target) || !double.IsFinite(x) || !double.IsFinite(y)
            || x < 0 || x >= 1 || y < 0 || y >= 1)
            throw new ArgumentOutOfRangeException(nameof(x), "Pick a point inside the displayed image.");
        if (target == CalibrationTarget.FilmBase && recipe.FilmKind == FilmKind.Positive)
            throw new InvalidOperationException("Positive film has no negative film base to measure.");
        token.ThrowIfCancellationRequested();
        progress?.Report(new(frame.Id, "Sampling picture", "Measuring the selected patch in the full-resolution linear input."));
        var local = recipe.ForFrame(frame.Id);
        local = local with { PictureCalibration = local.PictureCalibration ?? new() };
        ReferenceReconstruction.ValidateFrame(roll, frame, local, roll.IsDemo);
        var context = new CalibrationContext(roll, frame, local.FilmKind, sources.Open(roll, frame),
            message => System.Diagnostics.Trace.WriteLine(message), progress);
        IReadOnlyList<ICalibrationStep> steps = [new ManualPickStep(target, x, y, local)];
        var state = await CalibrationPipeline.RunAsync(steps, context, new(local.PictureCalibration), token).ConfigureAwait(false);
        return local with { PictureCalibration = state.Picture };
    }

    public static bool TryMapPoint(double viewportWidth, double viewportHeight, double imageWidth,
        double imageHeight, double pointerX, double pointerY, out double x, out double y)
    {
        x = y = 0;
        if (!TryImageBounds(viewportWidth, viewportHeight, imageWidth, imageHeight, out var bounds)
            || !double.IsFinite(pointerX) || !double.IsFinite(pointerY))
            return false;
        x = (pointerX - bounds.Left) / bounds.Width;
        y = (pointerY - bounds.Top) / bounds.Height;
        return x >= 0 && x < 1 && y >= 0 && y < 1;
    }

    public static bool TryMapSample(PictureSample? sample, Guid frameId, DevelopRecipe recipe,
        double viewportWidth, double viewportHeight, double imageWidth, double imageHeight,
        out double x, out double y)
    {
        x = y = 0;
        if (sample is null || sample.FrameId != frameId || sample.SourceWidth <= 0 || sample.SourceHeight <= 0
            || (long)sample.SourceWidth * sample.SourceHeight > 60_000_000
            || !double.IsFinite(sample.X) || !double.IsFinite(sample.Y)
            || sample.X < 0 || sample.X >= 1 || sample.Y < 0 || sample.Y >= 1
            || new[] { recipe.CropLeft, recipe.CropTop, recipe.CropRight, recipe.CropBottom }
                .Any(v => !double.IsFinite(v) || v < 0)
            || recipe.CropLeft + recipe.CropRight >= 1 || recipe.CropTop + recipe.CropBottom >= 1
            || !TryImageBounds(viewportWidth, viewportHeight, imageWidth, imageHeight, out var bounds))
            return false;
        var (left, top, width, height) = ReferenceReconstruction.Crop(sample.SourceWidth, sample.SourceHeight, recipe);
        double u = (sample.X * sample.SourceWidth - left) / width;
        double v = (sample.Y * sample.SourceHeight - top) / height;
        if (u < 0 || u >= 1 || v < 0 || v >= 1) return false;
        x = bounds.Left + u * bounds.Width;
        y = bounds.Top + v * bounds.Height;
        return true;
    }

    private static bool TryImageBounds(double viewportWidth, double viewportHeight, double imageWidth,
        double imageHeight, out (double Left, double Top, double Width, double Height) bounds)
    {
        bounds = default;
        if (new[] { viewportWidth, viewportHeight, imageWidth, imageHeight }.Any(v => !double.IsFinite(v) || v <= 0))
            return false;
        double scale = Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight);
        double width = imageWidth * scale, height = imageHeight * scale;
        bounds = ((viewportWidth - width) / 2, (viewportHeight - height) / 2, width, height);
        return true;
    }
}
