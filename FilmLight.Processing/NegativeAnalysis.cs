namespace FilmLight.Processing;

public static class NegativeAnalysis
{
    private const int MaxSamples = 100000;

    public static LightSourceSample SampleLightSource(NegativeLinearImage src, NormalizedRect region)
    {
        var samples = Collect(src, region);
        var all = Collect(src, new(0, 0, 1, 1));
        var warnings = new List<LightSourceSampleWarning>();
        bool clipped = false;
        var usable = new double[3][];
        for (int c = 0; c < 3; c++)
        {
            double maximum = all[c][^1];
            double tolerance = Math.Max(1e-7, Math.Abs(maximum) * .001);
            // A sensor-clipped plateau remains at the image maximum after colorin; a fixed
            // .98 threshold cannot detect it because camera WB and colorin rescale channels.
            bool imagePlateau = all[c].Count(v => v >= maximum - tolerance) >= Math.Max(8, all[c].Length * .001);
            bool plateau = samples[c].Count(v => v >= maximum - tolerance) >= samples[c].Length * .95
                && CoefficientOfVariation(samples[c]) < .001 && imagePlateau;
            clipped |= maximum > 0 && plateau;
            usable[c] = imagePlateau && !plateau ? samples[c].Where(v => v > 0 && v < maximum - tolerance).ToArray()
                : samples[c].Where(v => v > 0).ToArray();
            if (usable[c].Length == 0) usable[c] = samples[c];
        }
        if (clipped) warnings.Add(LightSourceSampleWarning.ClippedLight);
        RgbTriple value = new(Percentile(usable[0], .5), Percentile(usable[1], .5), Percentile(usable[2], .5));
        double cv = samples.Max(CoefficientOfVariation);
        if (cv > .06) warnings.Add(LightSourceSampleWarning.NonUniform);
        if (value.R + value.G + value.B < 1.1 * all.Sum(c => Percentile(c, .95)))
            warnings.Add(LightSourceSampleWarning.NotLight);
        return new(region, value, cv, warnings);
    }

    public static NegativeConversionSettings RemeasureFilmBase(NegativeLinearImage basis, NegativeConversionSettings settings)
    {
        var sample = settings.BaseSample is { IsEstimate: false } stored
            ? SampleFilmBase(basis, stored.Region) : EstimateFilmBase(basis, settings.Frame.Crop);
        return settings with { BaseSample = sample, Conversion = settings.Conversion with { FilmBase = sample.Value } };
    }

    public static FilmBaseSample SampleFilmBase(NegativeLinearImage src, NormalizedRect region)
    {
        double[][] samples = Collect(src, region);
        RgbTriple value = new(Percentile(samples[0], .5), Percentile(samples[1], .5), Percentile(samples[2], .5));
        var warnings = new List<FilmBaseSampleWarning>();
        if (HasClippedPixel(src, region)) warnings.Add(FilmBaseSampleWarning.ClippedLight);
        double cv = samples.Max(CoefficientOfVariation);
        if (cv > .06) warnings.Add(FilmBaseSampleWarning.NonUniform);
        var all = Collect(src, new(0, 0, 1, 1));
        if (value.R < Percentile(all[0], .99) || value.G < Percentile(all[1], .99)
            || value.B < Percentile(all[2], .99))
            warnings.Add(FilmBaseSampleWarning.ProbablyPictureContent);
        return new(region, ClampBase(value), cv, warnings, false);
    }

    public static FilmBaseSample EstimateFilmBase(NegativeLinearImage src, NormalizedRect? frameRect = null)
    {
        if (frameRect is { } frame) NegativeFrameGeometry.ValidateCrop(frame);
        double[][] samples = Collect(src, new(0, 0, 1, 1), frameRect, true);
        // No usable rebate is not an error: fall back to non-clipped picture transmission.
        if (samples[0].Length == 0) samples = Collect(src, new(0, 0, 1, 1), null, true);
        if (samples[0].Length == 0) throw new ArgumentException("No finite, non-clipped pixels are available for a film-base estimate.", nameof(src));
        return new(new(0, 0, 1, 1), ClampBase(new(Percentile(samples[0], .995),
            Percentile(samples[1], .995), Percentile(samples[2], .995))),
            samples.Max(CoefficientOfVariation), Array.Empty<FilmBaseSampleWarning>(), true);
    }

    public static NegadoctorParameters AutoTone(NegativeLinearImage src, NegadoctorParameters p, NormalizedRect region)
    {
        NegativeConversionValidation.Validate(p);
        var samples = Collect(src, region);
        double[] low = samples.Select(c => Percentile(c, .005)).ToArray();
        double[] high = samples.Select(c => Percentile(c, .995)).ToArray();
        double[] b = Components(p.FilmBase), h = Components(p.HighlightsBalance), s = Components(p.ShadowsBalance);
        double dmax = Enumerable.Range(0, 3).Max(c => Density(b[c], low[c]));
        p = p with { DensityRange = Safe(dmax, .1, 6, p.DensityRange) };
        double offset = Enumerable.Range(0, 3).Min(c => Density(b[c], high[c]) / p.DensityRange);
        p = p with { ScanExposureBias = Safe(offset, -1, 1, p.ScanExposureBias) };
        double black = Enumerable.Range(0, 3).Max(c =>
            Math.Pow(10, h[c] * (-Density(b[c], high[c]) / p.DensityRange + s[c] * p.ScanExposureBias)) - .9);
        p = p with { PaperBlack = Safe(black, -.5, .5, p.PaperBlack) };
        // The 5.6.2 exposure picker intentionally does NOT multiply the offset term by wb_high.
        double exposure = Enumerable.Range(0, 3).Min(c =>
            .96 / (1 - Math.Pow(10, -Density(b[c], low[c]) * h[c] / p.DensityRange + s[c] * p.ScanExposureBias) + p.PaperBlack));
        return p with { PrintExposure = Safe(exposure, .5, 2, p.PrintExposure) };
    }

    public static NegadoctorParameters NeutralizeHighlights(NegativeLinearImage src, NegadoctorParameters p, NormalizedRect region)
    {
        NegativeConversionValidation.Validate(p);
        double[] mean = Collect(src, region).Select(c => c.Average()).ToArray();
        double[] b = Components(p.FilmBase), s = Components(p.ShadowsBalance);
        double[] inverse = Enumerable.Range(0, 3).Select(c =>
            Math.Abs(-1 / (p.ScanExposureBias * s[c] - Density(b[c], mean[c]) / p.DensityRange))).ToArray();
        double min = inverse.Min();
        return p with { HighlightsBalance = Ratios(inverse, min, p.HighlightsBalance, false) };
    }

    public static NegadoctorParameters NeutralizeShadows(NegativeLinearImage src, NegadoctorParameters p, NormalizedRect region)
    {
        NegativeConversionValidation.Validate(p);
        double[] mean = Collect(src, region).Select(c => c.Average()).ToArray();
        double[] b = Components(p.FilmBase);
        double[] density = Enumerable.Range(0, 3).Select(c => Density(b[c], mean[c]) / p.DensityRange).ToArray();
        return p with { ShadowsBalance = Ratios(density, density.Min(), p.ShadowsBalance, true) };
    }

    public static (NormalizedRect Rect, double Confidence)? DetectFrame(NegativeLinearImage src)
    {
        NegadoctorMath.ValidateImage(src);
        int width = Math.Min(256, src.Width), height = Math.Min(256, src.Height);
        var rows = new List<double>[height];
        var columns = new List<double>[width];
        for (int y = 0; y < height; y++) rows[y] = new();
        for (int x = 0; x < width; x++) columns[x] = new();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = ((y * src.Height / height) * src.Width + x * src.Width / width) * 3;
            double r = src.Pixels[i], g = src.Pixels[i + 1], b = src.Pixels[i + 2];
            if (!double.IsFinite(r + g + b) || Math.Max(r, Math.Max(g, b)) >= .98 || Math.Min(r, Math.Min(g, b)) <= 0) continue;
            double log = Math.Log((r + g + b) / 3);
            rows[y].Add(log);
            columns[x].Add(log);
        }
        double[] rp = rows.Select(MedianOrNaN).ToArray(), cp = columns.Select(MedianOrNaN).ToArray();
        var horizontal = InteriorRun(cp);
        var vertical = InteriorRun(rp);
        if (horizontal is null || vertical is null) return null;
        var (left, right, hc) = horizontal.Value;
        var (top, bottom, vc) = vertical.Value;
        double x0 = (double)left / width, y0 = (double)top / height;
        double w = (double)(right - left + 1) / width, hgt = (double)(bottom - top + 1) / height;
        double confidence = Math.Min(hc, vc);
        if (confidence < .35 || w < .2 || hgt < .2) return null;
        return (new(x0 + w * .01, y0 + hgt * .01, w * .98, hgt * .98), confidence);
    }

    public static NegativeHistogram Histogram(Bgra8Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels is null
            || (long)image.Width * image.Height * 4 != image.Pixels.LongLength)
            throw new ArgumentException("BGRA image dimensions and pixels do not match.", nameof(image));
        long[] red = new long[256], green = new long[256], blue = new long[256], luma = new long[256];
        long shadows = 0, highlights = 0;
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            byte b = image.Pixels[i], g = image.Pixels[i + 1], r = image.Pixels[i + 2];
            red[r]++; green[g]++; blue[b]++;
            luma[(int)Math.Round(.2126 * r + .7152 * g + .0722 * b)]++;
            if (r == 0 || g == 0 || b == 0) shadows++;
            if (r == 255 || g == 255 || b == 255) highlights++;
        }
        return new(red, green, blue, luma, shadows, highlights, (long)image.Width * image.Height);
    }

    internal static double[][] Collect(NegativeLinearImage src, NormalizedRect region,
        NormalizedRect? exclude = null, bool nonClippedOnly = false)
    {
        NegadoctorMath.ValidateImage(src);
        NegativeFrameGeometry.ValidateCrop(region);
        int x0 = Math.Min(src.Width - 1, (int)(region.X * src.Width));
        int y0 = Math.Min(src.Height - 1, (int)(region.Y * src.Height));
        int x1 = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * src.Width), x0 + 1, src.Width);
        int y1 = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * src.Height), y0 + 1, src.Height);
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)(x1 - x0) * (y1 - y0) / MaxSamples)));
        var samples = new[] { new List<double>(), new List<double>(), new List<double>() };
        for (int y = y0; y < y1; y += step)
        for (int x = x0; x < x1; x += step)
        {
            double nx = (x + .5) / src.Width, ny = (y + .5) / src.Height;
            if (exclude is { } rect && nx >= rect.X && nx < rect.X + rect.Width && ny >= rect.Y && ny < rect.Y + rect.Height) continue;
            int i = (y * src.Width + x) * 3;
            double r = src.Pixels[i], g = src.Pixels[i + 1], b = src.Pixels[i + 2];
            if (!double.IsFinite(r + g + b)) continue;
            if (nonClippedOnly && (Math.Max(r, Math.Max(g, b)) >= .98 || Math.Min(r, Math.Min(g, b)) <= 0)) continue;
            samples[0].Add(r); samples[1].Add(g); samples[2].Add(b);
        }
        if (!nonClippedOnly && samples[0].Count == 0) throw new ArgumentException("Sampling region contains no finite pixels.", nameof(region));
        return samples.Select(c => { var a = c.ToArray(); Array.Sort(a); return a; }).ToArray();
    }

    internal static double Percentile(double[] sorted, double quantile)
    {
        if (sorted.Length == 0) throw new ArgumentException("No samples.");
        double index = (sorted.Length - 1) * quantile;
        int lower = (int)index;
        return sorted[lower] + (sorted[Math.Min(lower + 1, sorted.Length - 1)] - sorted[lower]) * (index - lower);
    }

    private static double CoefficientOfVariation(double[] samples)
    {
        double mean = samples.Average();
        double sigma = Math.Sqrt(samples.Average(v => (v - mean) * (v - mean)));
        return sigma / Math.Max(Math.Abs(mean), NegadoctorMath.TransmissionThreshold);
    }

    private static bool HasClippedPixel(NegativeLinearImage src, NormalizedRect region)
    {
        int x0 = (int)(region.X * src.Width), y0 = (int)(region.Y * src.Height);
        int x1 = Math.Min(src.Width, (int)Math.Ceiling((region.X + region.Width) * src.Width));
        int y1 = Math.Min(src.Height, (int)Math.Ceiling((region.Y + region.Height) * src.Height));
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int i = (y * src.Width + x) * 3;
            if (src.Pixels[i] >= .98 || src.Pixels[i + 1] >= .98 || src.Pixels[i + 2] >= .98) return true;
        }
        return false;
    }

    private static double MedianOrNaN(List<double> samples)
    {
        if (samples.Count == 0) return double.NaN;
        samples.Sort();
        return Percentile(samples.ToArray(), .5);
    }

    private static (int Start, int End, double Confidence)? InteriorRun(double[] profile)
    {
        double[] valid = profile.Where(double.IsFinite).Order().ToArray();
        if (valid.Length < 8) return null;
        double plateau = Percentile(valid, .9), dark = Percentile(valid, .1);
        double separation = plateau - dark;
        if (separation < .15) return null;
        double threshold = plateau - Math.Max(.1, separation * .3);
        int bestStart = -1, bestEnd = -1, start = -1;
        for (int i = 0; i <= profile.Length; i++)
        {
            if (i < profile.Length && double.IsFinite(profile[i]) && profile[i] < threshold)
            { if (start < 0) start = i; }
            else if (start >= 0)
            {
                if (i - start > bestEnd - bestStart + 1) { bestStart = start; bestEnd = i - 1; }
                start = -1;
            }
        }
        if (bestStart <= 0 || bestEnd >= profile.Length - 1 || bestEnd < bestStart) return null;
        // Both borders must have a real density gradient, not just one noisy row.
        double gradient = Math.Min(profile[bestStart - 1] - profile[bestStart],
            profile[bestEnd + 1] - profile[bestEnd]);
        if (!double.IsFinite(gradient) || gradient < .08) return null;
        return (bestStart, bestEnd, Math.Clamp(Math.Min(separation, gradient) / .7, 0, 1));
    }

    private static RgbTriple Ratios(double[] values, double min, RgbTriple fallback, bool reciprocal)
    {
        double[] old = Components(fallback);
        double Ratio(int c) => Safe(reciprocal ? min / values[c] : values[c] / min, .25, 2, old[c]);
        return new(Ratio(0), Ratio(1), Ratio(2));
    }

    private static double[] Components(RgbTriple rgb) => [rgb.R, rgb.G, rgb.B];
    private static double Density(double filmBase, double transmission) =>
        Math.Log10(filmBase / Math.Max(transmission, NegadoctorMath.TransmissionThreshold));
    private static double Safe(double value, double min, double max, double fallback) =>
        double.IsNaN(value) ? fallback : Math.Clamp(value, min, max);
    private static RgbTriple ClampBase(RgbTriple rgb) =>
        new(Math.Clamp(rgb.R, .00001, 1.5), Math.Clamp(rgb.G, .00001, 1.5), Math.Clamp(rgb.B, .00001, 1.5));
}

