using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FilmLight.Core;

namespace FilmLight.Processing;

public sealed record ScanSource(string Path, long SizeBytes, DateTime LastWriteUtc)
{
    internal static ScanSource Read(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0) throw new IOException($"Capture RAW is missing or empty: {path}");
        return new(file.FullName, file.Length, file.LastWriteTimeUtc);
    }

    internal void Verify()
    {
        if (Read(Path) != this) throw new IOException($"Capture RAW changed. Prepare the frame again: {Path}");
    }
}

public sealed record PreparedNegative(string CaptureId, RollCaptureKind Kind, NegativeLinearImage Basis,
    IReadOnlyList<ScanSource> Sources, IReadOnlyList<ScanSource> References, RgbAlignment? Alignment,
    RgbTriple? Normalization, LightBalance Light, NegativeLabCapabilities Renderer, IReadOnlyList<string> Warnings);

public sealed class NegativeProcessor(INegativeRenderer renderer, string cacheDirectory)
{
    private readonly string cache = Path.GetFullPath(cacheDirectory);
    private readonly SemaphoreSlim basisGate = new(1, 1);
    private const int PreviewEdge = 1600;

    public async Task<PreparedNegative> PrepareAsync(RollCapture capture, RollCapture? references,
        LightBalance light, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sources = Sources(capture);
        if (capture.Kind == RollCaptureKind.References)
            throw new ArgumentException("Light-only references are calibration, not film frames.", nameof(capture));
        var capabilities = await renderer.GetCapabilitiesAsync(ct);
        if (!capabilities.IsSupported) throw new NotSupportedException(capabilities.UnsupportedReason);
        if (capture.Kind == RollCaptureKind.White)
        {
            var basis = await BasisAsync(sources[0], light, false, capabilities, ct);
            return new(capture.Id, capture.Kind, basis, sources, [], null, null, light, capabilities, []);
        }

        var planes = new NegativeChannelImage[3];
        for (int c = 0; c < 3; c++)
        {
            var image = await BasisAsync(sources[c], new(new(1, 1, 1), null), true, capabilities, ct);
            if (RgbCaptureMath.Classify(image) != (RgbCaptureChannel)c)
                throw new InvalidDataException($"The saved {(RgbCaptureChannel)c} capture does not have an unambiguous matching camera channel.");
            planes[c] = RgbCaptureMath.Channel(image, (RgbCaptureChannel)c);
        }
        var alignment = await Task.Run(() => RgbCaptureMath.Align(planes[0], planes[1], planes[2], ct), ct);
        var warnings = new List<string>
        {
            "RGB reconstruction is experimental: camera channels are not a calibrated film-density color space. Keep the RAWs and compare with a white-light capture."
        };
        if (alignment.Red.Residual > .5 || alignment.Blue.Residual > .5
            || alignment.Red.Quality < .9 || alignment.Blue.Quality < .9)
            warnings.Add("RGB registration is uncertain (low coherence or residual above 0.5 px). Inspect color edges before exporting.");
        ScanSource[] referenceSources = [];
        RgbTriple? normalization = null;
        if (references is not null)
        {
            if (references.Kind != RollCaptureKind.References) throw new ArgumentException("Expected a roll reference set.", nameof(references));
            referenceSources = Sources(references);
            var values = new double[3];
            for (int c = 0; c < 3; c++)
            {
                var image = await BasisAsync(referenceSources[c], new(new(1, 1, 1), null), true, capabilities, ct);
                var samples = NegativeAnalysis.Collect(image, new(.1, .1, .8, .8))[c];
                values[c] = NegativeAnalysis.Percentile(samples, .5);
                if (values[c] <= 0 || !double.IsFinite(values[c]))
                    throw new InvalidDataException($"The {(RgbCaptureChannel)c} reference has no measurable light.");
                double maximum = samples[^1], tolerance = Math.Max(1e-7, Math.Abs(maximum) * .001);
                if (samples.Count(v => v >= maximum - tolerance) > samples.Length * .95)
                    throw new InvalidDataException($"The {(RgbCaptureChannel)c} reference is a saturated or unmeasurably flat plateau. Retake references without clipping.");
                if ((NegativeAnalysis.Percentile(samples, .95) - NegativeAnalysis.Percentile(samples, .05)) / values[c] > .2)
                    warnings.Add($"{(RgbCaptureChannel)c} reference illumination is uneven; scalar normalization is not a flat-field correction.");
            }
            normalization = new(values[0], values[1], values[2]);
        }
        else warnings.Add("No RGB references: normalization is estimated from this frame's 99.9th percentile, not calibrated light.");
        var merged = await Task.Run(() =>
        {
            var result = new NegativeLinearImage(planes[1].Width, planes[1].Height,
                new float[checked(planes[1].Width * planes[1].Height * 3)]);
            for (int c = 0; c < 3; c++)
            {
                if (planes[c].Width != result.Width || planes[c].Height != result.Height)
                    throw new InvalidDataException("RGB captures must have identical oriented dimensions.");
                RgbCaptureMath.WriteChannel(result, planes[c], (RgbCaptureChannel)c,
                    c == 0 ? alignment.Red : c == 2 ? alignment.Blue : new(0, 0, 1, 0), alignment, ct);
            }
            normalization = RgbCaptureMath.Normalize(result, normalization, ct);
            return result;
        }, ct);
        foreach (var source in sources.Concat(referenceSources)) source.Verify();
        return new(capture.Id, capture.Kind, merged, sources, referenceSources, alignment,
            normalization, new(new(1, 1, 1), "RGB camera channels"), capabilities, warnings);
    }

    public static NegativeConversionSettings Initialize(PreparedNegative prepared, NegativeConversionSettings settings)
    {
        var measured = NegativeAnalysis.RemeasureFilmBase(prepared.Basis, settings);
        return measured with
        {
            Light = prepared.Light,
            Conversion = NegativeAnalysis.AutoTone(prepared.Basis, measured.Conversion, measured.Frame.Crop)
        };
    }

    public async Task<LightSourceMeasurement> MeasureLightAsync(RollCapture capture, LightBalance light,
        NormalizedRect region, CancellationToken ct = default)
    {
        if (capture.Kind != RollCaptureKind.White)
            throw new ArgumentException("Use a white-light capture of the bare light source to measure Light Balance.");
        var coefficients = light.Coefficients ?? new RgbTriple(1, 1, 1);
        var current = light with { Coefficients = coefficients };
        var prepared = await PrepareAsync(capture, null, current, ct);
        var captured = NegativeAnalysis.SampleLightSource(prepared.Basis, region);
        if (captured.Warnings.Contains(LightSourceSampleWarning.ClippedLight))
            return new(current, captured, Residual(captured.Value), 0);
        var sample = captured;
        int iteration = 0;
        while (Residual(sample.Value) > .005 && iteration < 6)
        {
            ct.ThrowIfCancellationRequested();
            if (Math.Min(sample.Value.R, Math.Min(sample.Value.G, sample.Value.B)) <= 0)
                throw new InvalidDataException("The selected light source has no positive transmission in every channel.");
            coefficients = new(Math.Clamp(coefficients.R * sample.Value.G / sample.Value.R, .01, 8), 1,
                Math.Clamp(coefficients.B * sample.Value.G / sample.Value.B, .01, 8));
            current = new(coefficients, $"Light source: {capture.Id}");
            prepared = await PrepareAsync(capture, null, current, ct);
            sample = NegativeAnalysis.SampleLightSource(prepared.Basis, region);
            iteration++;
        }
        return new(current, captured, Residual(sample.Value), iteration);
    }

    public async Task<string> ExportAsync(PreparedNegative prepared, NegativeConversionSettings settings,
        string destination, Func<string, string, CancellationToken, Task>? encodeJpeg = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        NegativeConversionValidation.Validate(settings);
        if (prepared.Kind == RollCaptureKind.White && settings.Light != prepared.Light)
            throw new InvalidOperationException("Light Balance changed. Prepare the frame again before export.");
        foreach (var source in prepared.Sources.Concat(prepared.References)) source.Verify();
        var capabilities = await renderer.GetCapabilitiesAsync(ct);
        if (!capabilities.IsSupported || capabilities.DarktableVersion != prepared.Renderer.DarktableVersion
            || capabilities.TemplateHash != prepared.Renderer.TemplateHash)
            throw new InvalidOperationException("The darktable renderer changed. Prepare the frame again.");
        string root = Path.GetFullPath(destination);
        foreach (var source in prepared.Sources.Concat(prepared.References))
            if (root.Equals(source.Path, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose an output directory, not an original RAW.");
        Directory.CreateDirectory(root);
        string name = $"Positive-{prepared.CaptureId}-{Guid.NewGuid():N}";
        string stage = Path.Combine(root, "." + name + ".pending");
        Directory.CreateDirectory(stage);
        try
        {
            string rawMaster = Path.Combine(stage, "unframed.tif"), finished = Path.Combine(stage, "finished.tif");
            string master = Path.Combine(stage, "positive.tif");
            if (prepared.Kind == RollCaptureKind.White)
            {
                await renderer.RenderExportAsync(new(prepared.Sources[0].Path, settings.Light, settings.Conversion), rawMaster, ct);
                if (settings.Finish != NegativePrintFinish.Neutral)
                {
                    await Task.Run(() => NegativePrintFinishTiff.Apply(rawMaster, finished, settings.Finish, ct), ct);
                    File.Delete(rawMaster);
                    File.Move(finished, rawMaster);
                }
            }
            else
            {
                var full = await FullRgbAsync(prepared, ct);
                await Task.Run(() => NegadoctorMath.WritePositiveTiff(full, settings.Conversion, settings.Finish, rawMaster, ct), ct);
            }
            await Task.Run(() => TiffGeometry.Apply(rawMaster, master, settings.Frame, ct), ct);
            File.Delete(rawMaster);
            var dimensions = DarktableCliNegativeRenderer.ValidateOutput(master, false, true, ct);
            if (encodeJpeg is not null)
            {
                string jpeg = Path.Combine(stage, "positive.jpg");
                await encodeJpeg(master, jpeg, ct);
                if (DarktableCliNegativeRenderer.ValidateOutput(jpeg, false, false, ct) != dimensions)
                    throw new InvalidDataException("JPEG dimensions do not match the positive master.");
            }
            var sourceHashes = new Dictionary<string, string>();
            foreach (var source in prepared.Sources.Concat(prepared.References))
            {
                source.Verify();
                await using var input = File.OpenRead(source.Path);
                sourceHashes.Add(source.Path, Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)));
                source.Verify();
            }
            await File.WriteAllTextAsync(Path.Combine(stage, "conversion.json"), JsonSerializer.Serialize(new
            {
                version = 1, prepared.CaptureId, prepared.Kind, prepared.Sources, prepared.References,
                sourceHashes, prepared.Alignment, prepared.Normalization, prepared.Renderer, prepared.Warnings,
                outputProfile = "sRGB", masterBitsPerChannel = 16,
                settings = JsonSerializer.Deserialize<JsonElement>(NegativeConversionValidation.Serialize(settings)),
                createdUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }), ct);
            ct.ThrowIfCancellationRequested();
            string published = Path.Combine(root, name);
            Directory.Move(stage, published);
            return published;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Canceled conversion retained, unpublished: {0}", stage);
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new IOException($"Conversion failed. Incomplete files retained in {stage}. {error.Message}", error);
        }
    }

    internal static ScanSource[] Sources(RollCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Discarded || !capture.Complete || !Enum.IsDefined(capture.Kind))
            throw new ArgumentException("Choose a completed, non-discarded capture.", nameof(capture));
        var channels = capture.Kind == RollCaptureKind.White
            ? new[] { LightChannel.White } : [LightChannel.Red, LightChannel.Green, LightChannel.Blue];
        if (capture.Channels.Count != channels.Length)
            throw new InvalidDataException("A capture contains unexpected illumination channels.");
        return channels.Select(channel =>
        {
            var files = capture.Channels[channel] ?? throw new InvalidDataException($"Missing {channel} files.");
            var raws = files.Where(path => Path.GetExtension(path).Equals(".raf", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (raws.Length != 1)
                throw new InvalidDataException($"{channel} needs exactly one RAF; camera JPEGs and embedded previews are not conversion inputs.");
            return ScanSource.Read(raws[0]);
        }).ToArray();
    }

    private async Task<NegativeLinearImage> BasisAsync(ScanSource source, LightBalance light,
        bool cameraChannels, NegativeLabCapabilities capabilities, CancellationToken ct)
    {
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            source, light.Coefficients, cameraChannels, capabilities.DarktableVersion, capabilities.TemplateHash, PreviewEdge
        }))));
        await basisGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(cache);
            string path = Path.Combine(cache, key + ".pfm");
            if (!File.Exists(path))
            {
                await renderer.RenderLinearAsync(new NegativeRenderRequest(source.Path, light, NegadoctorParameters.Default, PreviewEdge)
                    { CameraChannels = cameraChannels }, path, ct);
            }
            source.Verify();
            return await PfmReader.ReadAsync(path, ct);
        }
        finally { basisGate.Release(); }
    }

    private async Task<NegativeLinearImage> FullRgbAsync(PreparedNegative prepared, CancellationToken ct)
    {
        var alignment = prepared.Alignment ?? throw new InvalidDataException("RGB frame has no prepared alignment.");
        NegativeLinearImage? merged = null;
        for (int c = 0; c < 3; c++)
        {
            string path = Path.Combine(cache, $"full-{Guid.NewGuid():N}.pfm");
            try
            {
                await renderer.RenderLinearAsync(new NegativeRenderRequest(prepared.Sources[c].Path, new(new(1, 1, 1), null), NegadoctorParameters.Default)
                    { CameraChannels = true, FullResolutionLinear = true }, path, ct);
                var plane = await Task.Run(() => PfmReader.ReadChannel(path, (RgbCaptureChannel)c, ct), ct);
                merged ??= new(plane.Width, plane.Height, new float[checked(plane.Width * plane.Height * 3)]);
                if (plane.Width != merged.Width || plane.Height != merged.Height)
                    throw new InvalidDataException("RGB captures must have identical full-resolution oriented dimensions.");
                await Task.Run(() => RgbCaptureMath.WriteChannel(merged, plane, (RgbCaptureChannel)c,
                    c == 0 ? alignment.Red : c == 2 ? alignment.Blue : new(0, 0, 1, 0), alignment, ct), ct);
            }
            finally { File.Delete(path); }
        }
        RgbCaptureMath.Normalize(merged!, prepared.Normalization
            ?? throw new InvalidDataException("RGB frame has no prepared normalization."), ct);
        return merged!;
    }

    private static double Residual(RgbTriple rgb) => rgb.G > 0
        ? Math.Max(Math.Abs(rgb.R / rgb.G - 1), Math.Abs(rgb.B / rgb.G - 1)) : double.MaxValue;
}
