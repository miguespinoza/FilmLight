namespace FilmLight.Workflow;

internal sealed record CalibrationState(PictureCalibration Picture)
{
    public SampleEvidence? LightEvidence { get; init; }
    public SampleEvidence? FilmBaseEvidence { get; init; }
}

internal sealed record SampleEvidence(double HalfWidth, double HalfHeight, int MeasuredLongEdge, double Peak);

internal sealed record CalibrationContext(RollDocument Roll, RollCapture Frame, FilmKind FilmKind,
    FrameSource Source, Action<string> Diagnostic, IProgress<ProcessingProgress>? Progress);

internal interface ICalibrationStep : IStep<CalibrationContext, CalibrationState>;

internal static class CalibrationPipeline
{
    internal static Task<CalibrationState> RunAsync(IReadOnlyList<ICalibrationStep> steps,
        CalibrationContext context, CalibrationState initial, CancellationToken token) =>
        StepPipeline.RunAsync<CalibrationContext, CalibrationState>(steps, context, initial, token);
}
