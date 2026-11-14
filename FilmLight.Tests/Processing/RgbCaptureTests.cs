using FilmLight.Processing;
using System.Text.Json;
using System.Text.Json.Nodes;
using BitMiracle.LibTiff.Classic;

internal static class RgbCaptureTests
{
    public static (string Name, Func<ValueTask> Body)[] All =>
    [
        ("RGB classification uses each capture's dominant camera channel at 2x", Sync(Classification)),
        ("RGB phase correlation recovers synthetic translations within 0.1 pixel", Sync(Alignment)),
        ("RGB merge scales shifts, resamples and normalizes sampled or estimated light", Sync(Normalization)),
        ("RGB native TIFF preserves 16-bit precision, Print Finish and Live parity", Sync(NativeTiff)),
    ];

    private static Func<ValueTask> Sync(Action action) => () => { action(); return ValueTask.CompletedTask; };
    internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, double tolerance, string name) =>
        Check(Math.Abs(expected - actual) < tolerance, $"{name}: expected {expected:R}, got {actual:R}; tolerance {tolerance}.");
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Classification()
    {
        NegativeLinearImage Image(params float[] channels) => new(1, 1, channels);
        Check(RgbCaptureMath.Classify(Image(.4f, .2f, .1f)) == RgbCaptureChannel.Red, "Inclusive 2x threshold.");
        Check(RgbCaptureMath.Classify(Image(.1f, .8f, .2f)) == RgbCaptureChannel.Green, "Green.");
        Check(RgbCaptureMath.Classify(Image(.1f, .2f, .8f)) == RgbCaptureChannel.Blue, "Blue.");
        Check(RgbCaptureMath.Classify(Image(.3f, .2f, .1f)) is null, "Ambiguous.");
        Check(RgbCaptureMath.Classify(Image(0, 0, 0)) is null, "Black has no classification.");
        var image = new NegativeLinearImage(101, 1, Enumerable.Range(0, 303).Select(i => i % 3 == 1 ? .4f : .1f).ToArray());
        image.Pixels[0] = 100;
        Check(RgbCaptureMath.Classify(image) == RgbCaptureChannel.Green, "Hot pixels do not determine classification.");
    }

    internal static NegativeChannelImage Scene(int width = 128, int height = 128, double dx = 0, double dy = 0)
    {
        var random = new Random(1701);
        var spots = Enumerable.Range(0, 90).Select(_ => (
            X: random.NextDouble() * width, Y: random.NextDouble() * height,
            Size: 1.5 + random.NextDouble() * 3, Value: random.NextDouble() * .8 - .4)).ToArray();
        float[] pixels = new float[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double log = -2;
            foreach (var spot in spots)
                log += spot.Value * Math.Exp(-((x - dx - spot.X) * (x - dx - spot.X)
                    + (y - dy - spot.Y) * (y - dy - spot.Y)) / (2 * spot.Size * spot.Size));
            pixels[y * width + x] = (float)Math.Exp(log);
        }
        return new(width, height, pixels);
    }

    private static void Alignment()
    {
        var reference = Scene();
        foreach (var (dx, dy) in new[] { (.4, -1.2), (-2.35, .65), (5.2, 3.4), (0d, 0d) })
        {
            var shift = RgbCaptureMath.PhaseCorrelation(reference, Scene(dx: dx, dy: dy));
            Console.WriteLine($"RGB shift ({dx},{dy}) -> ({shift.X:F4},{shift.Y:F4}), quality={shift.Quality:F4}, residual={shift.Residual:F4}");
            Near(dx, shift.X, .1, "Recovered X"); Near(dy, shift.Y, .1, "Recovered Y");
            Check(shift.Quality > .9 && shift.Residual < .5, "Good alignment is reported honestly.");
        }
        Reject<InvalidDataException>(() => RgbCaptureMath.PhaseCorrelation(reference, new(128, 128, new float[128 * 128])));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Reject<OperationCanceledException>(() => RgbCaptureMath.PhaseCorrelation(reference, reference, cancelled.Token));
    }

    private static void Normalization()
    {
        var plane = new NegativeChannelImage(4, 2, [1, 2, 3, 4, 1, 2, 3, 4]);
        var alignment = new RgbAlignment(2, 1, new(.25, 0, 1, 0), new(-.25, 0, 1, 0));
        var sample = new LightSourceSample(new(0, 0, 1, 1), new(4, 4, 4), 0, []);
        var merged = RgbCaptureMath.Merge(plane, plane, plane, alignment, sample);
        Near(1.5 / 4, merged.Pixels[0], 1e-7, "Scaled +0.5 px bilinear shift");
        Near(1d / 4, merged.Pixels[1], 1e-7, "Green reference");
        Near(1d / 4, merged.Pixels[2], 1e-7, "Border clamped");
        var raw = new NegativeLinearImage(2, 1, [.1f, .2f, .4f, .2f, .4f, .8f]);
        var values = RgbCaptureMath.Normalize(raw);
        Near(.1999, values.R, 1e-7, "99.9th percentile");
        Near(raw.Pixels[0], raw.Pixels[1], 1e-7, "Independent green normalization");
        Near(raw.Pixels[0], raw.Pixels[2], 1e-7, "Independent blue normalization");
        Reject<InvalidDataException>(() => RgbCaptureMath.Normalize(raw, new(0, 1, 1)));
    }

    private static void NativeTiff()
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"rgb-{Guid.NewGuid():N}.tif");
        var plane = Scene(16, 16);
        var image = new NegativeLinearImage(16, 16, plane.Pixels.SelectMany(v => new[] { v, v * .7f, v * .4f }).ToArray());
        var finish = new NegativePrintFinish(.5, -25, 30);
        var preview = NegadoctorMath.RenderPositive(image, NegadoctorParameters.Default, finish, 16);
        try
        {
            NegadoctorMath.WritePositiveTiff(image, NegadoctorParameters.Default, finish, path, default);
            using var tiff = Tiff.Open(path, "r")!;
            Check(tiff.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt() == 16, "16-bit RGB positive.");
            var row = new byte[tiff.ScanlineSize()];
            bool precision = false;
            for (int y = 0; y < image.Height; y++)
            {
                Check(tiff.ReadScanline(row, y), "Native TIFF row decodes.");
                for (int x = 0; x < image.Width; x++)
                for (int c = 0; c < 3; c++)
                {
                    ushort value = BitConverter.ToUInt16(row, x * 6 + c * 2);
                    Near(preview.Pixels[(y * image.Width + x) * 4 + 2 - c], value / 257d, .51, "Live/export parity");
                    precision |= value % 257 != 0;
                }
            }
            Check(precision, "Not an 8-bit preview inflated to 16-bit.");
        }
        finally { File.Delete(path); }
    }
}
