namespace FilmLight.Processing;

public readonly record struct ColorBalanceCoordinates(double Intensity, double Warmth, double Tint);

public static class ColorBalanceControls
{
    public static ColorBalanceCoordinates ToControls(RgbTriple rgb)
    {
        NegativeConversionValidation.Range(rgb.R, .25, 2, nameof(rgb));
        NegativeConversionValidation.Range(rgb.G, .25, 2, nameof(rgb));
        NegativeConversionValidation.Range(rgb.B, .25, 2, nameof(rgb));
        double r = Math.Log2(rgb.R), g = Math.Log2(rgb.G), b = Math.Log2(rgb.B);
        return new((r + g + b) / 3, b - r, g - (r + b) / 2);
    }

    public static RgbTriple FromControls(ColorBalanceCoordinates controls)
    {
        if (!double.IsFinite(controls.Intensity) || !double.IsFinite(controls.Warmth) || !double.IsFinite(controls.Tint))
            throw new ArgumentOutOfRangeException(nameof(controls));
        return new(Gain(controls.Intensity - controls.Warmth / 2 - controls.Tint / 3),
            Gain(controls.Intensity + 2 * controls.Tint / 3),
            Gain(controls.Intensity + controls.Warmth / 2 - controls.Tint / 3));
    }

    // With q = log10(transmission/base)/Dmax + offset*wb_low < 0, increasing wb_high
    // brightens that channel, so perceptual controls reverse the raw coordinates.
    // Large positive offsets can make q > 0 and reverse the effect; no global sign covers both.
    public static ColorBalanceCoordinates ToHighlights(RgbTriple rgb) => Orient(ToControls(rgb), -1);
    public static RgbTriple FromHighlights(ColorBalanceCoordinates controls) => FromControls(Orient(controls, -1));

    public static ColorBalanceCoordinates ToShadows(RgbTriple rgb, double offset) =>
        Orient(ToControls(rgb), ShadowDirection(offset));

    public static RgbTriple FromShadows(ColorBalanceCoordinates controls, double offset) =>
        FromControls(Orient(controls, ShadowDirection(offset)));

    public static bool ShadowsHaveVisibleEffect(double offset)
    {
        NegativeConversionValidation.Range(offset, -1, 1, nameof(offset));
        return offset != 0;
    }

    private static double ShadowDirection(double offset)
    {
        NegativeConversionValidation.Range(offset, -1, 1, nameof(offset));
        return offset < 0 ? -1 : 1;
    }

    private static ColorBalanceCoordinates Orient(ColorBalanceCoordinates value, double sign) =>
        value with { Warmth = sign * value.Warmth, Tint = sign * value.Tint };
    private static double Gain(double log) => Math.Pow(2, Math.Clamp(log, -2, 1));
}

