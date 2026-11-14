namespace FilmLight.Processing;

public enum FilmType { BlackAndWhite = 0, Color = 1 }

public readonly record struct RgbTriple(double R, double G, double B);
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height);

public sealed record NegadoctorParameters(
    FilmType FilmType, RgbTriple FilmBase, RgbTriple HighlightsBalance, RgbTriple ShadowsBalance,
    double DensityRange, double ScanExposureBias, double PaperBlack, double PaperGrade,
    double PaperGloss, double PrintExposure)
{
    public static NegadoctorParameters Default { get; } = new(
        FilmType.Color, new(1, .45, .25), new(1, 1, 1), new(1, 1, 1),
        2.046, -.05, .0755, 4, .75, .9245);
}

public sealed record LightBalance(RgbTriple? Coefficients, string? Source);

public sealed record NegativeFrameGeometry(
    double RotationDegrees, bool FlipHorizontal, bool FlipVertical, NormalizedRect Crop)
{
    public static NegativeFrameGeometry Default { get; } = new(0, false, false, new(0, 0, 1, 1));

    public PhotoEditTransform ToPhotoEditTransform(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        if (!double.IsFinite(RotationDegrees)) throw new ArgumentOutOfRangeException(nameof(RotationDegrees));
        ValidateCrop(Crop);
        // Photo edits crop in oriented source space, then rotate and mirror; do not swap axes here.
        int x = Math.Clamp((int)Math.Round(Crop.X * width), 0, width - 1);
        int y = Math.Clamp((int)Math.Round(Crop.Y * height), 0, height - 1);
        return new(new(x, y, Math.Clamp((int)Math.Round(Crop.Width * width), 1, width - x),
            Math.Clamp((int)Math.Round(Crop.Height * height), 1, height - y)),
            RotationDegrees, FlipHorizontal, FlipVertical);
    }

    internal static void ValidateCrop(NormalizedRect crop)
    {
        if (!double.IsFinite(crop.X) || !double.IsFinite(crop.Y)
            || !double.IsFinite(crop.Width) || !double.IsFinite(crop.Height)
            || crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0
            || crop.X + crop.Width > 1 || crop.Y + crop.Height > 1)
            throw new ArgumentOutOfRangeException(nameof(crop), "Frame Crop must be finite, positive and inside the image.");
    }
}

public enum FilmBaseSampleWarning { ClippedLight, NonUniform, ProbablyPictureContent }

public sealed record FilmBaseSample(NormalizedRect Region, RgbTriple Value, double Uniformity,
    IReadOnlyList<FilmBaseSampleWarning> Warnings, bool IsEstimate);

public enum LightSourceSampleWarning { ClippedLight, NotLight, NonUniform }

public sealed record LightSourceSample(NormalizedRect Region, RgbTriple Value, double Uniformity,
    IReadOnlyList<LightSourceSampleWarning> Warnings);

public sealed record LightSourceMeasurement(LightBalance Light, LightSourceSample Sample,
    double Residual, int Iterations)
{
    public bool IsBlocked => Sample.Warnings.Contains(LightSourceSampleWarning.ClippedLight);
}

public sealed record NegativePrintFinish(double ExposureEv, double Highlights, double Shadows)
{
    public static NegativePrintFinish Neutral { get; } = new(0, 0, 0);
}

public sealed record NegativeConversionSettings(
    int Version, Guid? FilmProfileId, LightBalance Light, NegadoctorParameters Conversion,
    NegativeFrameGeometry Frame, FilmBaseSample? BaseSample)
{
    public const int CurrentVersion = 3;
    [System.Text.Json.Serialization.JsonRequired]
    public NegativePrintFinish Finish { get; init; } = NegativePrintFinish.Neutral;
    public static NegativeConversionSettings Default { get; } = new(
        CurrentVersion, null, new(null, null), NegadoctorParameters.Default, NegativeFrameGeometry.Default, null);
}

public sealed record NegativeToneDefaults(double DensityRange, double ScanExposureBias,
    double PaperBlack, double PaperGrade, double PaperGloss, double PrintExposure);

public sealed record FilmProfile(Guid Id, string Name, FilmType FilmType, LightBalance Light,
    RgbTriple FilmBase, RgbTriple HighlightsBalance, RgbTriple ShadowsBalance, double DensityRange,
    NegativeToneDefaults? Tone, string? Notes, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

public sealed record NegativeLinearImage(int Width, int Height, float[] Pixels);
public enum RgbCaptureChannel { Red, Green, Blue }
public sealed record NegativeChannelImage(int Width, int Height, float[] Pixels);
// Shifts are sampling offsets in the oriented basis, not corrections in RAW sensor coordinates.
public sealed record RgbAlignment(int Width, int Height, RgbChannelShift Red, RgbChannelShift Blue);
public sealed record RgbChannelShift(double X, double Y, double Quality, double Residual);
public sealed record Bgra8Image(int Width, int Height, byte[] Pixels);
public sealed record NegativeLabCapabilities(bool IsSupported, string? DarktablePath,
    string? DarktableVersion, string? UnsupportedReason, string? TemplateHash = null);

public sealed record NegativeHistogram(long[] Red, long[] Green, long[] Blue, long[] Luma,
    long ClippedShadows, long ClippedHighlights, long PixelCount);

