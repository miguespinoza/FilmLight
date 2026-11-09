using FilmLight.Core;

namespace FilmLight.Workflow;

/// <summary>Produces labelled synthetic linear TIFFs, never RAFs or camera downloads.</summary>
public sealed class DemoCaptureSource : IWorkflowCaptureSource
{
    public bool IsDemo => true;
    public string Description => ProcessingImages.SyntheticLabel;

    public async Task<IReadOnlyList<CaptureFile>> CaptureAsync(CapturePurpose purpose, CaptureMode mode,
        int frameNumber, string outputDirectory, Func<LightChannel, CancellationToken, Task> showLight,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(showLight);
        if (!Enum.IsDefined(purpose) || !Enum.IsDefined(mode) || frameNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(frameNumber));
        cancellationToken.ThrowIfCancellationRequested();
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        string staging = Path.Combine(outputDirectory, $".synthetic-{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(staging);
        var files = new List<CaptureFile>();
        LightChannel[] channels = mode == CaptureMode.White ? [LightChannel.White] :
            [LightChannel.Red, LightChannel.Green, LightChannel.Blue];
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await showLight(channel, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var image = Create(purpose, channel, frameNumber, cancellationToken);
            string path = Path.Combine(staging, $"SYNTHETIC-{purpose}-{frameNumber:D4}-{channel}.tif");
            ProcessingImages.WriteTiff(path, image, false,
                $"{ProcessingImages.SyntheticLabel}; {ProcessingImages.LinearLabel}; purpose={purpose}; channel={channel}; frame={frameNumber}",
                cancellationToken, ProcessingImages.LinearRec2020Profile());
            _ = ProcessingImages.ReadLinear(path, cancellationToken);
            files.Add(new(channel, path));
        }
        cancellationToken.ThrowIfCancellationRequested();
        string published = Path.Combine(outputDirectory, $"SYNTHETIC-{purpose}-{frameNumber:D4}-{Guid.NewGuid():N}");
        Directory.Move(staging, published);
        return files.Select(f => f with { Path = Path.Combine(published, Path.GetFileName(f.Path)) }).ToArray();
    }

    private static LinearImage Create(CapturePurpose purpose, LightChannel channel, int number, CancellationToken token)
    {
        const int width = 384, height = 256;
        var image = new LinearImage(width, height, new float[width * height * 3]);
        double[] filmBase = [.78, .43, .23];
        int primary = channel switch { LightChannel.Red => 0, LightChannel.Green => 1, LightChannel.Blue => 2, _ => -1 };
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            for (int c = 0; c < 3; c++)
            {
                double nx = (x - width / 2.0) / width, ny = (y - height / 2.0) / height;
                double light = .82 * (1 - .5 * (nx * nx + ny * ny));
                double transmission = 1;
                if (purpose != CapturePurpose.LightReference)
                {
                    transmission = filmBase[c];
                    if (purpose == CapturePurpose.Frame && x < 8) transmission = 1;
                    if (purpose == CapturePurpose.Frame && x > 18 && x < width - 19 && y > 18 && y < height - 19)
                    {
                        double scene = .12 + .72 * ((x + number * 17 + c * 53) % (width - 38)) / (width - 38);
                        scene = Math.Clamp(scene + .10 * Math.Sin((y + c * 19) * .045), .04, .96);
                        // Binary frame-number bars in the picture make adjacent captures unmistakable.
                        if (y is > 30 and < 55 && x is > 30 and < 150)
                            scene = ((number >> ((x - 30) / 12)) & 1) == 1 ? .9 : .08;
                        transmission *= Math.Pow(10, -1.6 * scene);
                    }
                }
                double sensitivity = primary < 0 || primary == c ? 1 : .025;
                image.Pixels[(y * width + x) * 3 + c] = (float)(light * sensitivity * transmission);
            }
        }
        return image;
    }
}
