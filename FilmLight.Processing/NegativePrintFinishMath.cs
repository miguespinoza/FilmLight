namespace FilmLight.Processing;

public static class NegativePrintFinishMath
{
    public static RgbTriple Apply(RgbTriple linearSrgb, NegativePrintFinish finish)
    {
        NegativeConversionValidation.Validate(finish);
        NegativeConversionValidation.Range(linearSrgb.R, 0, 1, nameof(linearSrgb));
        NegativeConversionValidation.Range(linearSrgb.G, 0, 1, nameof(linearSrgb));
        NegativeConversionValidation.Range(linearSrgb.B, 0, 1, nameof(linearSrgb));
        if (finish == NegativePrintFinish.Neutral) return linearSrgb;
        double y = Luminance(linearSrgb);
        return Gain(linearSrgb, y, Tone(y, finish));
    }

    private static double Luminance(RgbTriple rgb) => .2126 * rgb.R + .7152 * rgb.G + .0722 * rgb.B;

    private static double Tone(double y, NegativePrintFinish finish)
    {
        if (y == 0) return 0;
        double stops = Math.Log2(y / .18);
        double distance = Math.Abs(stops);
        // Smoothstep across three stops: maximum log-curve derivative is .75 < 1.
        double t = Math.Min(1, distance / 3);
        double weight = t * t * (3 - 2 * t);
        double ev = finish.ExposureEv + 1.5 * weight *
            (stops < 0 ? finish.Shadows : finish.Highlights) / 100;
        double target = y * Math.Pow(2, ev);
        // Shadow-only adjustments leave the upper half exactly alone (including the shoulder).
        if (finish.ExposureEv == 0 && finish.Highlights == 0) return target;
        return target <= .8 ? target : .8 + .2 * (target - .8) / (.2 + target - .8);
    }

    private static RgbTriple Gain(RgbTriple rgb, double y, double target)
    {
        if (y == 0) return rgb;
        double gain = target / y;
        double r = rgb.R * gain, g = rgb.G * gain, b = rgb.B * gain;
        double peak = Math.Max(r, Math.Max(g, b));
        if (peak > 1)
        {
            double saturation = (1 - target) / (peak - target);
            r = target + saturation * (r - target);
            g = target + saturation * (g - target);
            b = target + saturation * (b - target);
        }
        return new(r, g, b);
    }

    internal sealed class Prepared
    {
        private const int Steps = 16384;
        private readonly double[] _curve = new double[Steps + 1];

        public Prepared(NegativePrintFinish finish)
        {
            for (int i = 0; i <= Steps; i++) _curve[i] = Tone((double)i / Steps, finish);
        }

        public RgbTriple Apply(RgbTriple rgb)
        {
            double y = Luminance(rgb);
            double position = Math.Min(Steps, y * Steps);
            int index = (int)position;
            double target = _curve[index] + (position - index) * (_curve[Math.Min(Steps, index + 1)] - _curve[index]);
            return Gain(rgb, y, target);
        }
    }
}

