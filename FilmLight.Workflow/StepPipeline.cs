namespace FilmLight.Workflow;

internal interface IStep<TContext, TState>
{
    Task<TState> ApplyAsync(TContext context, TState state, CancellationToken token);
}

internal static class StepPipeline
{
    internal static async Task<TState> RunAsync<TContext, TState>(IReadOnlyList<IStep<TContext, TState>> steps,
        TContext context, TState initial, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = initial;
        foreach (var step in steps)
        {
            token.ThrowIfCancellationRequested();
            state = await step.ApplyAsync(context, state, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        return state;
    }
}
