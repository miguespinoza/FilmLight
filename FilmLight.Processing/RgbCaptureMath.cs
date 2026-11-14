using System.Numerics;


namespace FilmLight.Processing;

/// <summary>Prototype RGB Capture Set grouping, camera-channel detection and translation alignment.</summary>
public static class RgbCaptureMath
{
    public static RgbCaptureChannel? Classify(NegativeLinearImage image)
    {
        NegadoctorMath.ValidateImage(image);
        var samples = NegativeAnalysis.Collect(image, new(0, 0, 1, 1));
        double[] values = samples.Select(c => NegativeAnalysis.Percentile(c, .5)).ToArray();
        int dominant = Array.IndexOf(values, values.Max());
        return values[dominant] > 0 && values.Where((_, i) => i != dominant)
            .All(v => values[dominant] >= 2 * Math.Max(v, 0)) ? (RgbCaptureChannel)dominant : null;
    }

    public static NegativeChannelImage Channel(NegativeLinearImage image, RgbCaptureChannel channel)
    {
        NegadoctorMath.ValidateImage(image);
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        float[] pixels = new float[checked(image.Width * image.Height)];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = image.Pixels[i * 3 + (int)channel];
        return new(image.Width, image.Height, pixels);
    }

    public static RgbAlignment Align(NegativeChannelImage red, NegativeChannelImage green,
        NegativeChannelImage blue, CancellationToken ct = default) =>
        new(green.Width, green.Height, PhaseCorrelation(green, red, ct), PhaseCorrelation(green, blue, ct));

    public static RgbChannelShift PhaseCorrelation(NegativeChannelImage reference, NegativeChannelImage moving,
        CancellationToken ct = default)
    {
        Validate(reference); Validate(moving);
        RequireSameSize(reference, moving);
        int width = PowerOfTwo(reference.Width), height = PowerOfTwo(reference.Height);
        var a = Spectrum(reference, width, height, ct);
        var b = Spectrum(moving, width, height, ct);
        double maximum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            b[i] *= Complex.Conjugate(a[i]);
            maximum = Math.Max(maximum, b[i].Magnitude);
        }
        if (maximum <= 1e-15) throw new InvalidDataException("RGB alignment needs image detail, not a uniform capture.");
        for (int i = 0; i < a.Length; i++)
            a[i] = b[i].Magnitude > maximum * 1e-10 ? b[i] / b[i].Magnitude : Complex.Zero;
        Transform2D(a, width, height, true, ct);
        int peak = 0;
        for (int i = 1; i < a.Length; i++) if (a[i].Real > a[peak].Real) peak = i;
        double x = peak % width, y = peak / width;
        if (x > width / 2) x -= width;
        if (y > height / 2) y -= height;
        // Fit the residual phase plane around the integer peak. Low frequencies avoid
        // phase wrapping; power weighting suppresses noise rather than trusting empty bins.
        double xx = 0, yy = 0, xy = 0, xp = 0, yp = 0, weightSum = 0;
        for (int row = 0; row < height; row++)
        for (int col = 0; col < width; col++)
        {
            double fx = Frequency(col, width), fy = Frequency(row, height);
            if (Math.Abs(fx) > .2 || Math.Abs(fy) > .2 || fx == 0 && fy == 0) continue;
            var spectrum = b[row * width + col];
            double weight = spectrum.Magnitude;
            if (weight < maximum * 1e-8) continue;
            double phase = (spectrum * Complex.FromPolarCoordinates(1, 2 * Math.PI * (fx * x + fy * y))).Phase / (-2 * Math.PI);
            xx += weight * fx * fx; yy += weight * fy * fy; xy += weight * fx * fy;
            xp += weight * fx * phase; yp += weight * fy * phase; weightSum += weight;
        }
        double determinant = xx * yy - xy * xy;
        if (determinant <= 1e-20) throw new InvalidDataException("RGB alignment needs detail in both image directions.");
        x += Math.Clamp((xp * yy - yp * xy) / determinant, -.75, .75);
        y += Math.Clamp((yp * xx - xp * xy) / determinant, -.75, .75);
        double error = 0, coherent = 0;
        for (int row = 0; row < height; row++)
        for (int col = 0; col < width; col++)
        {
            double fx = Frequency(col, width), fy = Frequency(row, height);
            if (Math.Abs(fx) > .2 || Math.Abs(fy) > .2 || fx == 0 && fy == 0) continue;
            var spectrum = b[row * width + col];
            double weight = spectrum.Magnitude;
            if (weight < maximum * 1e-8) continue;
            double phase = (spectrum * Complex.FromPolarCoordinates(1, 2 * Math.PI * (fx * x + fy * y))).Phase;
            error += weight * phase * phase;
            coherent += weight * Math.Cos(phase);
        }
        ct.ThrowIfCancellationRequested();
        return new(x, y, Math.Clamp(coherent / weightSum, 0, 1),
            Math.Sqrt(error / (4 * Math.PI * Math.PI * (xx + yy))));
    }

    public static NegativeLinearImage Merge(NegativeChannelImage red, NegativeChannelImage green,
        NegativeChannelImage blue, RgbAlignment alignment, LightSourceSample? light = null,
        CancellationToken ct = default)
    {
        Validate(red); Validate(green); Validate(blue);
        RequireSameSize(red, green); RequireSameSize(blue, green);
        float[] pixels = new float[checked(green.Width * green.Height * 3)];
        var image = new NegativeLinearImage(green.Width, green.Height, pixels);
        WriteChannel(image, red, RgbCaptureChannel.Red, alignment.Red, alignment, ct);
        WriteChannel(image, green, RgbCaptureChannel.Green, new(0, 0, 1, 0), alignment, ct);
        WriteChannel(image, blue, RgbCaptureChannel.Blue, alignment.Blue, alignment, ct);
        Normalize(image, light?.Value, ct);
        return image;
    }

    internal static void WriteChannel(NegativeLinearImage destination, NegativeChannelImage source,
        RgbCaptureChannel channel, RgbChannelShift shift, RgbAlignment alignment, CancellationToken ct)
    {
        double dx = shift.X * source.Width / alignment.Width, dy = shift.Y * source.Height / alignment.Height;
        for (int y = 0; y < source.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < source.Width; x++)
                destination.Pixels[(y * source.Width + x) * 3 + (int)channel] = Sample(source, x + dx, y + dy);
        }
    }

    public static RgbTriple Normalize(NegativeLinearImage image, RgbTriple? light = null, CancellationToken ct = default)
    {
        NegadoctorMath.ValidateImage(image);
        var samples = light is null ? NegativeAnalysis.Collect(image, new(0, 0, 1, 1)) : null;
        var value = light ?? new RgbTriple(NegativeAnalysis.Percentile(samples![0], .999),
            NegativeAnalysis.Percentile(samples[1], .999), NegativeAnalysis.Percentile(samples[2], .999));
        if (!double.IsFinite(value.R) || !double.IsFinite(value.G) || !double.IsFinite(value.B)
            || value.R <= 0 || value.G <= 0 || value.B <= 0)
            throw new InvalidDataException("RGB normalization needs positive light in every channel.");
        for (int i = 0; i < image.Pixels.Length; i += 3)
        {
            if (i % 12288 == 0) ct.ThrowIfCancellationRequested();
            image.Pixels[i] /= (float)value.R; image.Pixels[i + 1] /= (float)value.G; image.Pixels[i + 2] /= (float)value.B;
        }
        return value;
    }

    internal static void Validate(NegativeChannelImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width < 1 || image.Height < 1 || image.Pixels is null
            || (long)image.Width * image.Height != image.Pixels.LongLength || image.Pixels.Any(p => !float.IsFinite(p)))
            throw new ArgumentException("A camera channel needs finite Width x Height samples.");
    }

    private static float Sample(NegativeChannelImage image, double x, double y)
    {
        x = Math.Clamp(x, 0, image.Width - 1); y = Math.Clamp(y, 0, image.Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
        double tx = x - x0, ty = y - y0;
        return (float)((1 - ty) * ((1 - tx) * image.Pixels[y0 * image.Width + x0] + tx * image.Pixels[y0 * image.Width + x1])
            + ty * ((1 - tx) * image.Pixels[y1 * image.Width + x0] + tx * image.Pixels[y1 * image.Width + x1]));
    }

    private static void RequireSameSize(NegativeChannelImage a, NegativeChannelImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new InvalidDataException("RGB captures must have identical oriented dimensions.");
    }

    private static int PowerOfTwo(int value)
    {
        int result = 1;
        while (result < value) result = checked(result * 2);
        return result;
    }

    private static double Frequency(int index, int size) => (index <= size / 2 ? index : index - size) / (double)size;

    private static Complex[] Spectrum(NegativeChannelImage image, int width, int height, CancellationToken ct)
    {
        var values = new Complex[checked(width * height)];
        double mean = image.Pixels.Average(p => Math.Log(Math.Max(p, 1e-10)));
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            double window = Math.Pow(Math.Sin(Math.PI * (x + .5) / image.Width), 2)
                * Math.Pow(Math.Sin(Math.PI * (y + .5) / image.Height), 2);
            values[y * width + x] = (Math.Log(Math.Max(image.Pixels[y * image.Width + x], 1e-10)) - mean) * window;
        }
        Transform2D(values, width, height, false, ct);
        return values;
    }

    private static void Transform2D(Complex[] values, int width, int height, bool inverse, CancellationToken ct)
    {
        for (int y = 0; y < height; y++) { ct.ThrowIfCancellationRequested(); Fft(values.AsSpan(y * width, width), inverse); }
        var column = new Complex[height];
        for (int x = 0; x < width; x++)
        {
            ct.ThrowIfCancellationRequested();
            for (int y = 0; y < height; y++) column[y] = values[y * width + x];
            Fft(column, inverse);
            for (int y = 0; y < height; y++) values[y * width + x] = column[y];
        }
    }

    private static void Fft(Span<Complex> values, bool inverse)
    {
        int n = values.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, (inverse ? 2 : -2) * Math.PI / length);
            for (int start = 0; start < n; start += length)
            {
                var factor = Complex.One;
                for (int j = 0; j < length / 2; j++)
                {
                    var even = values[start + j]; var odd = factor * values[start + j + length / 2];
                    values[start + j] = even + odd; values[start + j + length / 2] = even - odd;
                    factor *= step;
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) values[i] /= n;
    }
}

