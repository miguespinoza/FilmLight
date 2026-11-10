namespace FilmLight.Workflow;

internal enum DevelopIntent { Preview, Export }

internal sealed record DevelopContext(RollDocument Roll, RollCapture Frame, DevelopRecipe Recipe, DevelopIntent Intent,
    FrameSources Sources, string Staging, IProgress<ProcessingProgress>? Progress);

internal sealed record DevelopState(LinearImage Image, float[] FilmBase, DevelopRecipe Controls);

internal interface IDevelopStep : IStep<DevelopContext, DevelopState>;

internal static class DevelopPipeline
{
    internal static Task<DevelopState> RunAsync(IReadOnlyList<IDevelopStep> steps,
        DevelopContext context, DevelopState initial, CancellationToken token) =>
        StepPipeline.RunAsync<DevelopContext, DevelopState>(steps, context, initial, token);
}
