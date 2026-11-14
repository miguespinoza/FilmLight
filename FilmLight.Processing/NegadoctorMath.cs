using BitMiracle.LibTiff.Classic;


namespace FilmLight.Processing;

public static class NegadoctorMath
{
    public const double TransmissionThreshold = 2.3283064365386963e-10;

    public static RgbTriple ProcessPixel(RgbTriple transmission, NegadoctorParameters parameters)
    {
        NegativeConversionValidation.Validate(parameters);
        var coefficients = new Coefficients(parameters);
        return new(Channel(transmission.R, coefficients.Base.R, coefficients.High.R, coefficients.Offset.R, coefficients),
            Channel(transmission.G, coefficients.Base.G, coefficients.High.G, coefficients.Offset.G, coefficients),
            Channel(transmission.B, coefficients.Base.B, coefficients.High.B, coefficients.Offset.B, coefficients));
    }

    public static Bgra8Image RenderPositive(NegativeLinearImage src, NegadoctorParameters p, int maxEdge = 1600)
    {
        ValidateImage(src);
        NegativeConversionValidation.Validate(p);
        return Render(src, maxEdge, p, 1);
    }

    public static Bgra8Image RenderPositive(NegativeLinearImage src, NegadoctorParameters p,
        NegativePrintFinish finish, int maxEdge = 1600)
    {
        ValidateImage(src);
        NegativeConversionValidation.Validate(p);
        NegativeConversionValidation.Validate(finish);
        return Render(src, maxEdge, p, 1, finish);
    }

    public static Bgra8Image RenderNegativeView(NegativeLinearImage src, int maxEdge = 1600)
    {
        ValidateImage(src);
        var samples = NegativeAnalysis.Collect(src, new(0, 0, 1, 1));
        double white = Math.Max(NegativeAnalysis.Percentile(samples[0], .995),
            Math.Max(NegativeAnalysis.Percentile(samples[1], .995), NegativeAnalysis.Percentile(samples[2], .995)));
        return Render(src, maxEdge, null, .9 / Math.Max(white, TransmissionThreshold));
    }

    internal static void ValidateImage(NegativeLinearImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels is null
            || (long)image.Width * image.Height * 3 != image.Pixels.LongLength)
            throw new ArgumentException("Linear image must contain Width × Height interleaved RGB samples.", nameof(image));
    }

    internal static PixelSize WritePositiveTiff(NegativeLinearImage src, NegadoctorParameters p,
        NegativePrintFinish finish, string path, CancellationToken ct)
    {
        ValidateImage(src);
        NegativeConversionValidation.Validate(p);
        NegativeConversionValidation.Validate(finish);
        using var output = Tiff.Open(path, "w") ?? throw new IOException("Cannot create RGB positive TIFF.");
        output.SetField(TiffTag.IMAGEWIDTH, src.Width);
        output.SetField(TiffTag.IMAGELENGTH, src.Height);
        output.SetField(TiffTag.BITSPERSAMPLE, 16);
        output.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        output.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        output.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        output.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
        output.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        output.SetField(TiffTag.COMPRESSION, Compression.NONE);
        output.SetField(TiffTag.ROWSPERSTRIP, output.DefaultStripSize(0));
        var profile = SrgbProfile.Create();
        output.SetField(TiffTag.ICCPROFILE, profile.Length, profile);
        var coefficients = new Coefficients(p);
        var tone = finish == NegativePrintFinish.Neutral ? null : new NegativePrintFinishMath.Prepared(finish);
        byte[] row = new byte[checked(src.Width * 6)];
        for (int y = 0; y < src.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < src.Width; x++)
            {
                int i = (y * src.Width + x) * 3;
                var rgb = LinearSrgb(
                    Channel(src.Pixels[i], coefficients.Base.R, coefficients.High.R, coefficients.Offset.R, coefficients),
                    Channel(src.Pixels[i + 1], coefficients.Base.G, coefficients.High.G, coefficients.Offset.G, coefficients),
                    Channel(src.Pixels[i + 2], coefficients.Base.B, coefficients.High.B, coefficients.Offset.B, coefficients));
                if (tone is not null) rgb = tone.Apply(rgb);
                for (int c = 0; c < 3; c++)
                {
                    double value = c == 0 ? rgb.R : c == 1 ? rgb.G : rgb.B;
                    ushort encoded = (ushort)Math.Clamp((int)Math.Round(EncodeValue(value) * 65535), 0, 65535);
                    BitConverter.TryWriteBytes(row.AsSpan(x * 6 + c * 2, 2), encoded);
                }
            }
            if (!output.WriteScanline(row, y)) throw new IOException($"Cannot write RGB positive TIFF row {y}.");
        }
        if (!output.WriteDirectory()) throw new IOException("Cannot complete RGB positive TIFF.");
        return new(src.Width, src.Height);
    }

    private readonly struct Coefficients
    {
        public readonly RgbTriple Base, High, Offset;
        public readonly double Exposure, Black, Grade, Gloss, Headroom;

        public Coefficients(NegadoctorParameters p)
        {
            Base = p.FilmType == FilmType.BlackAndWhite
                ? new(p.FilmBase.R, p.FilmBase.R, p.FilmBase.R) : p.FilmBase;
            High = new(p.HighlightsBalance.R / p.DensityRange, p.HighlightsBalance.G / p.DensityRange,
                p.HighlightsBalance.B / p.DensityRange);
            Offset = new(p.HighlightsBalance.R * p.ScanExposureBias * p.ShadowsBalance.R,
                p.HighlightsBalance.G * p.ScanExposureBias * p.ShadowsBalance.G,
                p.HighlightsBalance.B * p.ScanExposureBias * p.ShadowsBalance.B);
            Exposure = p.PrintExposure;
            Black = -p.PrintExposure * (1 + p.PaperBlack);
            Grade = p.PaperGrade;
            Gloss = p.PaperGloss;
            Headroom = 1 - p.PaperGloss;
        }
    }

    // Independently expressed Cineon density / paper-response equations, checked against release-5.6.2.
    private static double Channel(double value, double filmBase, double high, double offset, Coefficients p)
    {
        double density = Math.Log10(Math.Max(double.IsFinite(value) ? value : 0, TransmissionThreshold) / filmBase);
        double corrected = high * density + offset;
        double paper = Math.Max(-(p.Exposure * Math.Pow(10, corrected) + p.Black), 0);
        double print = Math.Pow(paper, p.Grade);
        if (print <= p.Gloss) return print;
        return p.Headroom == 0 ? 1 : p.Gloss + p.Headroom * (1 - Math.Exp(-(print - p.Gloss) / p.Headroom));
    }

    private static Bgra8Image Render(NegativeLinearImage src, int maxEdge, NegadoctorParameters? p, double scale,
        NegativePrintFinish? finish = null)
    {
        if (maxEdge <= 0) throw new ArgumentOutOfRangeException(nameof(maxEdge));
        double resize = Math.Min(1, (double)maxEdge / Math.Max(src.Width, src.Height));
        int width = Math.Max(1, (int)Math.Round(src.Width * resize));
        int height = Math.Max(1, (int)Math.Round(src.Height * resize));
        byte[] pixels = new byte[checked(width * height * 4)];
        var coefficients = p is null ? default : new Coefficients(p);
        var tone = finish is null || finish == NegativePrintFinish.Neutral ? null : new NegativePrintFinishMath.Prepared(finish);
        Parallel.For(0, height, y =>
        {
            int sy = Math.Min(src.Height - 1, (int)((y + .5) * src.Height / height));
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Min(src.Width - 1, (int)((x + .5) * src.Width / width));
                int source = (sy * src.Width + sx) * 3;
                double r = src.Pixels[source], g = src.Pixels[source + 1], b = src.Pixels[source + 2];
                if (p is not null)
                {
                    r = Channel(r, coefficients.Base.R, coefficients.High.R, coefficients.Offset.R, coefficients);
                    g = Channel(g, coefficients.Base.G, coefficients.High.G, coefficients.Offset.G, coefficients);
                    b = Channel(b, coefficients.Base.B, coefficients.High.B, coefficients.Offset.B, coefficients);
                }
                else { r *= scale; g *= scale; b *= scale; }
                int destination = (y * width + x) * 4;
                var rgb = LinearSrgb(r, g, b);
                if (tone is not null) rgb = tone.Apply(rgb);
                pixels[destination] = Encode(rgb.B);
                pixels[destination + 1] = Encode(rgb.G);
                pixels[destination + 2] = Encode(rgb.R);
                pixels[destination + 3] = 255;
            }
        });
        return new(width, height, pixels);
    }

    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    private static RgbTriple LinearSrgb(double r, double g, double b) => new(
        Clamp(1.6604910021 * r - .5876411388 * g - .0728498633 * b),
        Clamp(-.1245504745 * r + 1.1328998971 * g - .0083494226 * b),
        Clamp(-.0181507634 * r - .1005788980 * g + 1.1187296614 * b));
    private static byte Encode(double linear)
        => (byte)Math.Clamp((int)Math.Round(EncodeValue(linear) * 255), 0, 255);

    private static double EncodeValue(double linear)
    {
        linear = double.IsFinite(linear) ? Math.Clamp(linear, 0, 1) : 0;
        return linear <= .0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
    }
}

