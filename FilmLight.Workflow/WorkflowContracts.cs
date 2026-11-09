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
    public bool Accepted { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
}

public sealed record FrameExport(Guid FrameId, Guid RecipeId, int RecipeRevision,
    ExportState State, IReadOnlyList<string> OutputFiles, string? Error, DateTimeOffset UpdatedAt);

public sealed record RollDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
    public FilmKind FilmKind { get; init; }
    public bool IsDemo { get; init; }
    public WorkflowPhase Phase { get; init; }
    public List<RollCapture> Captures { get; init; } = [];
    public List<DevelopRecipe> Recipes { get; init; } = [];
    public List<FrameExport> Exports { get; init; } = [];
    public Guid? AcceptedRecipeId { get; init; }
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
