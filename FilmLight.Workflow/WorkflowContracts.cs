using FilmLight.Core;

namespace FilmLight.Workflow;

public enum FilmKind { ColorNegative, BlackAndWhiteNegative, Positive }
public enum CaptureMode { White, Rgb }
public enum CapturePurpose { LightReference, FilmBaseReference, Frame }
public enum WorkflowPhase { References, Capture, Develop, Export }
public enum ExportState { Pending, Processing, Complete, Failed, Canceled }

public sealed record CaptureFile(LightChannel Channel, string Path);
public sealed record RollCapture(Guid Id, int Number, CapturePurpose Purpose, CaptureMode Mode,
    IReadOnlyList<CaptureFile> Files, bool IsDemo, DateTimeOffset CapturedAt);

public sealed record PictureSample(Guid FrameId, double X, double Y, double Red, double Green, double Blue)
{
    // Nonzero dimensions identify full-capture coordinates; legacy picks could be crop-relative.
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public bool IsClipped { get; init; }

    internal void Validate(bool allowClipped = false)
    {
        if (IsClipped && !allowClipped)
            throw new ArgumentException("Only a backlight pick may be saved as clipped; clipped film-base picks are not usable.");
        if ((SourceWidth != 0 || SourceHeight != 0)
            && (SourceWidth <= 0 || SourceHeight <= 0 || (long)SourceWidth * SourceHeight > 60_000_000))
            throw new ArgumentException("Sample positions require valid original capture dimensions.");
        if (FrameId == Guid.Empty || !double.IsFinite(X) || !double.IsFinite(Y)
            || X < 0 || X >= 1 || Y < 0 || Y >= 1
            || new[] { Red, Green, Blue }.Any(v => !double.IsFinite(v) || v <= .0001 || (!IsClipped && v >= .99999)))
            throw new ArgumentException("Pick an unclipped, evenly lit patch inside the image, away from dark holder edges.");
    }
}

public sealed record PictureCalibration
{
    internal const string ClippedBacklightWarning =
        "Clipped backlight pick saved as a marker only; no backlight normalization or white-balance correction is applied.";

    public PictureSample? Light { get; init; }
    public PictureSample? FilmBase { get; init; }

    public bool IsComplete(FilmKind filmKind) => Light is not null && (filmKind == FilmKind.Positive || FilmBase is not null);

    internal void RequireComplete(FilmKind filmKind)
    {
        if (!IsComplete(filmKind))
            throw new InvalidOperationException("This frame needs exposed light and, for negatives, clear film samples.");
    }

    internal void Validate(RollDocument roll, CaptureMode? mode = null, Guid? frameId = null)
    {
        Light?.Validate(allowClipped: true);
        FilmBase?.Validate();
        foreach (var sample in new[] { Light, FilmBase }.OfType<PictureSample>())
        {
            if (frameId.HasValue && sample.FrameId != frameId)
                throw new InvalidOperationException("Calibration samples must come from this frame, not another picture in the roll.");
            if (!roll.Captures.Any(c => c.Id == sample.FrameId && c.Purpose == CapturePurpose.Frame
                && c.IsDemo == roll.IsDemo && (!mode.HasValue || c.Mode == mode)))
                throw new InvalidOperationException("Picture samples must come from a captured frame in this roll with the same capture mode.");
        }
    }
}

public sealed record DevelopRecipe
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Revision { get; init; } = 1;
    public FilmKind FilmKind { get; init; }
    public string? XmpTemplatePath { get; init; }
    public double ExposureEv { get; init; }
    public double RedGain { get; init; } = 1;
    public double GreenGain { get; init; } = 1;
    public double BlueGain { get; init; } = 1;
    public double FilmBaseRed { get; init; } = 1;
    public double FilmBaseGreen { get; init; } = 1;
    public double FilmBaseBlue { get; init; } = 1;
    public double BlackPoint { get; init; }
    public double WhitePoint { get; init; } = 1;
    public double CropLeft { get; init; }
    public double CropTop { get; init; }
    public double CropRight { get; init; }
    public double CropBottom { get; init; }
    public PictureCalibration? PictureCalibration { get; init; }
    public Guid? FrameId { get; init; }
    public bool InheritControls { get; init; }
    public Guid? ControlsSourceRecipeId { get; init; }
    public int ControlsSourceRevision { get; init; }

    internal DevelopRecipe WithControlsFrom(DevelopRecipe source) => this with
    {
        XmpTemplatePath = source.XmpTemplatePath,
        ExposureEv = source.ExposureEv,
        RedGain = source.RedGain, GreenGain = source.GreenGain, BlueGain = source.BlueGain,
        FilmBaseRed = source.FilmBaseRed, FilmBaseGreen = source.FilmBaseGreen, FilmBaseBlue = source.FilmBaseBlue,
        BlackPoint = source.BlackPoint, WhitePoint = source.WhitePoint,
        CropLeft = source.CropLeft, CropTop = source.CropTop, CropRight = source.CropRight, CropBottom = source.CropBottom
    };

    public DevelopRecipe ForFrame(Guid frameId)
    {
        if (frameId == Guid.Empty) throw new ArgumentException("A captured frame identity is required.", nameof(frameId));
        if (FrameId == frameId) return this;
        var local = PictureCalibration;
        if (FrameId.HasValue || local is null || (local.Light is null && local.FilmBase is null)
            || (local.Light is { } light && light.FrameId != frameId)
            || (local.FilmBase is { } basis && basis.FrameId != frameId))
            local = null;
        return this with
        {
            Id = Guid.NewGuid(), Revision = 1, FrameId = frameId, PictureCalibration = local
        };
    }
}

public sealed record FrameExport(Guid FrameId, Guid RecipeId, int RecipeRevision,
    ExportState State, IReadOnlyList<string> OutputFiles, string? Error, DateTimeOffset UpdatedAt);

public sealed record RollDocument
{
    public int SchemaVersion { get; init; } = 2;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
    public FilmKind FilmKind { get; init; }
    public bool IsDemo { get; init; }
    public WorkflowPhase Phase { get; init; } = WorkflowPhase.Capture;
    public List<RollCapture> Captures { get; init; } = [];
    public List<DevelopRecipe> Recipes { get; init; } = [];
    public List<FrameExport> Exports { get; init; } = [];
    public bool IntermediatesTrashed { get; init; }
}

public sealed record ProcessingProgress(Guid? FrameId, string Stage, string Message, int Completed = 0, int Total = 0);
public sealed record PreviewResult(string ImagePath, IReadOnlyList<string> Warnings, string? SourceImagePath = null);
public sealed record FrameProcessingResult(IReadOnlyList<string> OutputFiles, IReadOnlyList<string> Warnings);

public interface IFrameProcessor
{
    Task<PreviewResult> PreviewAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        string workDirectory, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken);
    Task<FrameProcessingResult> ExportAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
        string outputDirectory, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken);
}

public interface IWorkflowCaptureSource
{
    bool IsDemo { get; }
    string Description { get; }
    Task<IReadOnlyList<CaptureFile>> CaptureAsync(CapturePurpose purpose, CaptureMode mode,
        int frameNumber, string outputDirectory, Func<LightChannel, CancellationToken, Task> showLight,
        CancellationToken cancellationToken);
}
