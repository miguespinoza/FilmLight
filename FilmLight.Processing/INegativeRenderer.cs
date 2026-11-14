namespace FilmLight.Processing;

public sealed record NegativeRenderRequest(
    string SourcePath, LightBalance Light, NegadoctorParameters Conversion, int MaxEdge = 1600)
{
    public bool SixteenBitProof { get; init; }
    public bool CameraChannels { get; init; }
    public bool FullResolutionLinear { get; init; }
}

public interface INegativeRenderer
{
    ValueTask<NegativeLabCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);
    ValueTask RenderLinearAsync(NegativeRenderRequest request, string outputPath, CancellationToken cancellationToken);
    ValueTask RenderProofAsync(NegativeRenderRequest request, string outputPath, CancellationToken cancellationToken);
    ValueTask RenderExportAsync(NegativeRenderRequest request, string outputPath, CancellationToken cancellationToken);
}

