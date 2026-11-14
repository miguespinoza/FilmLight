using FilmLight.Processing;
using System.Text.Json;
using System.Text.Json.Nodes;
using BitMiracle.LibTiff.Classic;

internal static class IterationTwoTests
{
    public static (string Name, Func<ValueTask> Body)[] All =>
    [
        ("Version 1 upgrades explicitly; Version 2 requires a bounded finish", Sync(Upgrade)),
        ("Print Finish is identity, monotonic, region isolated and hue preserving", Sync(Finish)),
        ("Print Finish has zero allocation per pixel", Sync(Allocation)),
        ("Print Finish TIFF preserves precision and applies linear-light exposure", Sync(TiffFinish)),
        ("Light Source Sample detects rescaled clipping and warns on film and dust", Sync(LightSample)),
        ("Light Balance remeasures stored film base and reestimates estimates", Sync(Remeasure)),
    ];

    private static Func<ValueTask> Sync(Action action) => () => { action(); return ValueTask.CompletedTask; };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, double tolerance = 1e-9) =>
        Check(Math.Abs(expected - actual) <= tolerance, $"Expected {expected:R}, got {actual:R}.");

    private static void Upgrade()
    {
        var settings = NegativeConversionSettings.Default with { Finish = new(1, -50, 40) };
        var node = JsonNode.Parse(NegativeConversionValidation.Serialize(settings))!;
        node["version"] = 1;
        node.AsObject().Remove("finish");
        node.AsObject().Remove("rgbCapture");
        var upgraded = NegativeConversionValidation.Deserialize(node.ToJsonString());
        Check(upgraded.Version == NegativeConversionSettings.CurrentVersion && upgraded.Finish == NegativePrintFinish.Neutral, "Explicit v1 upgrade.");
        Check(upgraded.Conversion == settings.Conversion && upgraded.Light == settings.Light && upgraded.Frame == settings.Frame, "v1 values preserved.");
        node["version"] = 2;
        Reject<JsonException>(() => NegativeConversionValidation.Deserialize(node.ToJsonString()));
        node["version"] = 99;
        Reject<ArgumentException>(() => NegativeConversionValidation.Deserialize(node.ToJsonString()));
        foreach (var finish in new[] { new NegativePrintFinish(4, 0, 0), new(0, -101, 0), new(0, 0, double.NaN) })
            Reject<ArgumentException>(() => NegativeConversionValidation.Validate(settings with { Finish = finish }));
        Check(NegativeConversionValidation.SettingsHash(settings) != NegativeConversionValidation.SettingsHash(upgraded), "Finish changes identity.");
    }

    private static void Finish()
    {
        var input = new RgbTriple(.18, .18, .18);
        Check(NegativePrintFinishMath.Apply(input, NegativePrintFinish.Neutral) == input, "Bit-identical identity.");
        var source = new NegativeLinearImage(1, 1, [.1f, .2f, .3f]);
        Check(NegadoctorMath.RenderPositive(source, NegadoctorParameters.Default).Pixels.SequenceEqual(
            NegadoctorMath.RenderPositive(source, NegadoctorParameters.Default, NegativePrintFinish.Neutral).Pixels), "Neutral preview bytes.");
        Near(.36, NegativePrintFinishMath.Apply(input, new(1, 0, 0)).R);
        foreach (double ev in new[] { -3d, -1.2, 0, 1, 3 })
        foreach (double highlights in new[] { -100d, -37, 0, 43, 100 })
        foreach (double shadows in new[] { -100d, -41, 0, 67, 100 })
        {
            var finish = new NegativePrintFinish(ev, highlights, shadows);
            double previous = 0;
            for (int i = 1; i <= 10000; i++)
            {
                double y = i / 10000d;
                var result = NegativePrintFinishMath.Apply(new(y, y, y), finish);
                Check(result.R > previous && result.R <= 1, $"Nonmonotonic curve at {y}, {finish}.");
                previous = result.R;
            }
        }
        for (int i = 1; i <= 100; i++)
        {
            double high = .18 + .82 * i / 100, low = .18 * i / 100;
            Near(high, NegativePrintFinishMath.Apply(new(high, high, high), new(0, 0, 100)).R);
            Near(low, NegativePrintFinishMath.Apply(new(low, low, low), new(0, 100, 0)).R);
        }
        var colored = NegativePrintFinishMath.Apply(new(.2, .1, .05), new(1, 0, 0));
        Near(2, colored.R / colored.G); Near(2, colored.G / colored.B);
        var saturated = NegativePrintFinishMath.Apply(new(.9, .4, .2), new(3, 100, 0));
        Near((.9 - .4) / (.4 - .2), (saturated.R - saturated.G) / (saturated.G - saturated.B));
        Check(saturated.R <= 1 && saturated.B >= 0, "Hue-preserving gamut fit.");
        Check(NegativePrintFinishMath.Apply(new(.99, .99, .99), new(3, 0, 0)).R < 1, "Soft shoulder.");
    }

    private static void Allocation()
    {
        var finish = new NegativePrintFinish(1, -50, 40);
        var input = new RgbTriple(.2, .1, .05);
        for (int i = 0; i < 100; i++) NegativePrintFinishMath.Apply(input, finish);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) NegativePrintFinishMath.Apply(input, finish);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Per-pixel allocation.");
    }

    private static void TiffFinish()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "finish-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.tif"), output = Path.Combine(root, "finish.tif");
            WriteTiff(source, 2, 2);
            NegativePrintFinishTiff.Apply(source, output, new(1, 0, 0), default);
            using var tiff = Tiff.Open(output, "r")!;
            Check(tiff.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt() == 16, "16-bit output.");
            var row = new byte[tiff.ScanlineSize()];
            Check(tiff.ReadScanline(row, 0), "Output decodes.");
            double encoded = BitConverter.ToUInt16(row) / 65535d;
            Near(.36, Math.Pow((encoded + .055) / 1.055, 2.4), .00004);
            using var ct = new CancellationTokenSource();
            ct.Cancel();
            Reject<OperationCanceledException>(() => NegativePrintFinishTiff.Apply(source, Path.Combine(root, "cancel.tif"), new(1, 0, 0), ct.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static void WriteTiff(string path, int width, int height)
    {
        using var tiff = Tiff.Open(path, "w")!;
        tiff.SetField(TiffTag.IMAGEWIDTH, width); tiff.SetField(TiffTag.IMAGELENGTH, height);
        tiff.SetField(TiffTag.BITSPERSAMPLE, 16); tiff.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB); tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.ROWSPERSTRIP, height);
        var row = new byte[width * 6];
        ushort sample = (ushort)Math.Round((1.055 * Math.Pow(.18, 1 / 2.4) - .055) * 65535);
        for (int i = 0; i < row.Length; i += 2) BitConverter.TryWriteBytes(row.AsSpan(i, 2), sample);
        for (int y = 0; y < height; y++) Check(tiff.WriteScanline(row, y), "Fixture TIFF write.");
        Check(tiff.WriteDirectory(), "Fixture TIFF directory.");
    }

    private static NegativeLinearImage Image(Func<int, RgbTriple> read) => new(100, 100,
        Enumerable.Range(0, 10000).SelectMany(i => { var p = read(i % 100); return new[] { (float)p.R, (float)p.G, (float)p.B }; }).ToArray());

    private static void LightSample()
    {
        var plateau = Image(x => x < 80 ? new(.1, .06, .04) : new(.35, .42, .29));
        var sample = NegativeAnalysis.SampleLightSource(plateau, new(.85, .1, .1, .8));
        Check(sample.Warnings.Contains(LightSourceSampleWarning.ClippedLight), "Clipping below .98 detected.");
        var noisy = Image(x => x < 80 ? new(.1, .06, .04) : new(.3 + (x % 5) * .003, .4 + (x % 5) * .003, .25 + (x % 5) * .003));
        sample = NegativeAnalysis.SampleLightSource(noisy, new(.8, .1, .2, .8));
        Check(!sample.Warnings.Contains(LightSourceSampleWarning.ClippedLight), "Non-clipped colored light.");
        sample = NegativeAnalysis.SampleLightSource(noisy, new(.1, .1, .2, .8));
        Check(sample.Warnings.Contains(LightSourceSampleWarning.NotLight), "Film warning.");
        sample = NegativeAnalysis.SampleLightSource(noisy, new(.7, .1, .25, .8));
        Check(sample.Warnings.Contains(LightSourceSampleWarning.NonUniform), "Edge/dust warning.");
    }

    private static void Remeasure()
    {
        var image = Image(x => x < 50 ? new(.2, .15, .1) : new(.6, .4, .3));
        var sample = new FilmBaseSample(new(.1, .1, .2, .2), new(.1, .1, .1), .1, [], false);
        var result = NegativeAnalysis.RemeasureFilmBase(image, NegativeConversionSettings.Default with { BaseSample = sample, Finish = new(1, -50, 40) });
        Near(.2, result.Conversion.FilmBase.R, 1e-6);
        Check(result.BaseSample!.Region == sample.Region && result.Finish == new NegativePrintFinish(1, -50, 40), "Stored region and finish preserved.");
        result = NegativeAnalysis.RemeasureFilmBase(image, result with { BaseSample = sample with { IsEstimate = true } });
        Check(result.BaseSample!.IsEstimate, "Estimate is re-estimated.");
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
