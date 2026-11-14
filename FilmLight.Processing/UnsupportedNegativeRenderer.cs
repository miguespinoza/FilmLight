namespace FilmLight.Processing;

public sealed class UnsupportedNegativeRenderer(string reason = "darktable 5.6 was not found.") : INegativeRenderer
{
    public ValueTask<NegativeLabCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new NegativeLabCapabilities(false, null, null, reason));
    }

    public ValueTask RenderLinearAsync(NegativeRenderRequest request, string outputPath, CancellationToken cancellationToken) => Fail(cancellationToken);
    public ValueTask RenderProofAsync(NegativeRenderRequest request, string outputPath, CancellationToken cancellationToken) => Fail(cancellationToken);
    public ValueTask RenderExportAsync(NegativeRenderRequest request, string outputPath, CancellationToken cancellationToken) => Fail(cancellationToken);

    private ValueTask Fail(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException(new NotSupportedException(reason));
    }
}

