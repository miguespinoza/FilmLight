using FilmLight.Processing;
using System.Text;
using System.Text.Json;

internal static class CoreTests
{
    public static (string Name, Func<ValueTask> Test)[] GetTests() =>
    [
        ("Film stock codes match negadoctor v2", Sync(FilmStockCodes)),
        ("Cineon hand-computed density vector", Sync(HandComputedDensity)),
        ("Paper grade and soft clip hand-computed vectors", Sync(PaperResponse)),
        ("Threshold and monochrome base match source", Sync(ThresholdAndMonochrome)),
        ("Positive display converts Rec2020 to encoded BGRA", Sync(DisplayEncoding)),
        ("Display encoding matches reference OETF across all grayscale levels", Sync(DisplayQuantization)),
        ("Density math matches the reference expression across parameter ranges", Sync(RandomMathVectors)),
        ("Negative display rescales underexposed capture", Sync(NegativeDisplay)),
        ("PFM reads bottom-up little-endian RGB and scale", Sync(PfmLittleEndian)),
        ("PFM reads big-endian grayscale and CRLF", Sync(PfmBigEndian)),
        ("PFM rejects malformed and truncated data", Sync(PfmInvalid)),
        ("Settings JSON preserves values and stable hash", Sync(SettingsRoundTrip)),
        ("Settings reject non-finite out-of-bounds and unknown versions", Sync(InvalidSettings)),
        ("Settings reject incomplete null malformed and unknown JSON", Sync(InvalidJson)),
        ("Film Profiles validate optional tone and all shared groups", Sync(FilmProfiles)),
        ("Film base median detects clipping variation and picture content", Sync(FilmBaseWarnings)),
        ("Film base estimate excludes crop and clipped pixels", Sync(FilmBaseEstimate)),
        ("Auto tone follows Dmax offset black exposure source order", Sync(AutoToneOrder)),
        ("Auto exposure preserves the source asymmetric offset term", Sync(AutoExposureOffset)),
        ("Neutral pickers reproduce source ratios and tolerate singularities", Sync(NeutralPickers)),
        ("Detect frame finds synthetic interior and rejects flat capture", Sync(FrameDetection)),
        ("Frame geometry crops oriented source before rotation", Sync(Geometry)),
        ("Color balance coordinates are a log2 bijection", Sync(ColorBijection)),
        ("Highlights warmth and tint have empirical positive signs", Sync(HighlightSigns)),
        ("Highlight signs reverse outside the normal density regime", Sync(HighlightSignLimit)),
        ("Shadows signs follow both offset signs and zero has no effect", Sync(ShadowSigns)),
        ("Histogram counts RGB luma and clipped pixels", Sync(Histogram)),
        ("Analysis and render reject invalid images", Sync(InvalidImages))
    ];

    private static Func<ValueTask> Sync(Action action) => () => { action(); return ValueTask.CompletedTask; };
    private static NegadoctorParameters Simple => new(FilmType.Color, new(1, 1, 1), new(1, 1, 1),
        new(1, 1, 1), 1, 0, 0, 1, 1, 1);

    private static void FilmStockCodes()
    {
        Equal(1, (int)FilmType.Color); Equal(0, (int)FilmType.BlackAndWhite);
    }

    private static void HandComputedDensity()
    {
        var result = NegadoctorMath.ProcessPixel(new(.1, .01, 1), Simple);
        Near(.9, result.R); Near(.99, result.G); Near(0, result.B);
        result = NegadoctorMath.ProcessPixel(new(.25, .25, .25), Simple with { DensityRange = 2 });
        Near(.5, result.R);
    }

    private static void PaperResponse()
    {
        var p = Simple with { PaperGrade = 2, PrintExposure = .5, PaperBlack = .1 };
        Near(.0625, NegadoctorMath.ProcessPixel(new(.6, .6, .6), p).R);
        p = Simple with { PaperGloss = .5 };
        Near(.5 + .5 * (1 - Math.Exp(-.8)), NegadoctorMath.ProcessPixel(new(.1, .1, .1), p).R);
        Near(1, NegadoctorMath.ProcessPixel(new(0, 0, 0), Simple).R, 1e-9);
    }

    private static void ThresholdAndMonochrome()
    {
        var p = Simple with { FilmBase = new(.8, .4, .2), FilmType = FilmType.BlackAndWhite };
        var result = NegadoctorMath.ProcessPixel(new(.2, .2, .2), p);
        Near(result.R, result.G); Near(result.G, result.B); Near(.75, result.R);
        var below = NegadoctorMath.ProcessPixel(new(-1, 0, NegadoctorMath.TransmissionThreshold), Simple);
        Near(below.R, below.B); Near(below.G, below.B);
        result = NegadoctorMath.ProcessPixel(new(.2, .2, .2), p with { HighlightsBalance = new(1, 1.2, .8) });
        Check(result.R != result.G, "B&W must retain channel balances.");
    }

    private static void DisplayEncoding()
    {
        var result = NegadoctorMath.RenderPositive(Image(1, 1, (_, _) => new(.5, .5, .5)), Simple);
        Equal(188, (int)result.Pixels[0]); Equal(188, (int)result.Pixels[1]);
        Equal(188, (int)result.Pixels[2]); Equal(255, (int)result.Pixels[3]);
        result = NegadoctorMath.RenderPositive(Image(8, 4, (_, _) => new(.1, .1, .1)), Simple, 4);
        Equal(4, result.Width); Equal(2, result.Height);
        result = NegadoctorMath.RenderPositive(Image(1, 1, (_, _) => new(0, 1, 1)), Simple);
        Equal(255, (int)result.Pixels[2]); Equal(0, (int)result.Pixels[1]); Equal(0, (int)result.Pixels[0]);
    }

    private static void NegativeDisplay()
    {
        var result = NegadoctorMath.RenderNegativeView(Image(2, 2, (_, _) => new(.005, .005, .005)));
        Check(result.Pixels[0] >= 243, "Six-stop-under capture should be visible.");
    }

    private static void DisplayQuantization()
    {
        const int count = 65536;
        var source = Image(count, 1, (x, _) => new((double)x / (count - 1), (double)x / (count - 1), (double)x / (count - 1)));
        var result = NegadoctorMath.RenderPositive(source, Simple, count);
        for (int x = 0; x < count; x++)
        {
            double value = NegadoctorMath.ProcessPixel(new(source.Pixels[x * 3], source.Pixels[x * 3], source.Pixels[x * 3]), Simple).R;
            byte r = ReferenceEncode(1.6604910021 * value - .5876411388 * value - .0728498633 * value);
            byte g = ReferenceEncode(-.1245504745 * value + 1.1328998971 * value - .0083494226 * value);
            byte b = ReferenceEncode(-.0181507634 * value - .1005788980 * value + 1.1187296614 * value);
            Equal(r, result.Pixels[x * 4 + 2]); Equal(g, result.Pixels[x * 4 + 1]); Equal(b, result.Pixels[x * 4]);
        }
    }

    private static byte ReferenceEncode(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        double encoded = linear <= .0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
        return (byte)Math.Clamp((int)Math.Round(encoded * 255), 0, 255);
    }

    private static void RandomMathVectors()
    {
        var random = new Random(1729);
        double In(double low, double high) => low + random.NextDouble() * (high - low);
        for (int i = 0; i < 1000; i++)
        {
            var p = new NegadoctorParameters(FilmType.Color, new(In(.00001, 1.5), 1, 1),
                new(In(.25, 2), 1, 1), new(In(.25, 2), 1, 1), In(.1, 6), In(-1, 1),
                In(-.5, .5), In(1, 8), In(.0001, 1), In(.5, 2));
            double transmission = i % 10 == 0 ? -1 : In(0, 1.5);
            double density = Math.Log10(Math.Max(transmission, NegadoctorMath.TransmissionThreshold) / p.FilmBase.R);
            double corrected = p.HighlightsBalance.R / p.DensityRange * density
                + p.HighlightsBalance.R * p.ScanExposureBias * p.ShadowsBalance.R;
            double paper = Math.Max(-(p.PrintExposure * Math.Pow(10, corrected) - p.PrintExposure * (1 + p.PaperBlack)), 0);
            double print = Math.Pow(paper, p.PaperGrade);
            double expected = print <= p.PaperGloss ? print
                : p.PaperGloss + (1 - p.PaperGloss) * (1 - Math.Exp(-(print - p.PaperGloss) / (1 - p.PaperGloss)));
            Near(expected, NegadoctorMath.ProcessPixel(new(transmission, .1, .1), p).R, 1e-10);
        }
    }

    private static void PfmLittleEndian()
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("PF\n1 2\n-2\n"));
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
            foreach (float v in new float[] { 4, 5, 6, 1, 2, 3 }) writer.Write(v);
        stream.Position = 0;
        var image = PfmReader.Read(stream);
        Equal(1, image.Width); Equal(2, image.Height);
        for (int i = 0; i < 6; i++) Near((i + 1) * 2, image.Pixels[i]);
    }

    private static void PfmBigEndian()
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("Pf\r\n# comment\r\n1 1\r\n1\r\n"));
        byte[] bytes = BitConverter.GetBytes(.25f); Array.Reverse(bytes); stream.Write(bytes);
        stream.Position = 0;
        var image = PfmReader.Read(stream);
        foreach (float value in image.Pixels) Near(.25, value);
    }

    private static void PfmInvalid()
    {
        foreach (string header in new[] { "XX\n1 1\n-1\n", "PF\n0 1\n-1\n", "PF\n1 1\n0\n", "PF\n1 1\nNaN\n" })
        {
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes(header));
            Throws<InvalidDataException>(() => PfmReader.Read(stream));
        }
        using var shortStream = new MemoryStream(Encoding.ASCII.GetBytes("PF\n1 1\n-1\n"));
        Throws<EndOfStreamException>(() => PfmReader.Read(shortStream));
    }

    private static void SettingsRoundTrip()
    {
        var settings = NegativeConversionSettings.Default with
        {
            FilmProfileId = Guid.NewGuid(),
            Light = new(new(2.123456789, 1, 1.23456789), "capture"),
            Conversion = Simple with { PaperBlack = -.123456789 },
            Frame = new(90, true, false, new(.1, .2, .8, .6)),
            BaseSample = new(new(.1, .1, .1, .1), new(.5, .3, .2), .023,
                new[] { FilmBaseSampleWarning.NonUniform }, false)
        };
        string json = NegativeConversionValidation.Serialize(settings);
        var roundtrip = NegativeConversionValidation.Deserialize(json);
        Equal(settings.Conversion, roundtrip.Conversion); Equal(settings.Frame, roundtrip.Frame);
        Equal(settings.Light, roundtrip.Light); Near(.023, roundtrip.BaseSample!.Uniformity);
        Equal(json, NegativeConversionValidation.Serialize(roundtrip));
        Equal(NegativeConversionValidation.SettingsHash(settings), NegativeConversionValidation.SettingsHash(roundtrip));
        Check(NegativeConversionValidation.SettingsHash(settings) !=
            NegativeConversionValidation.SettingsHash(settings with { Frame = NegativeFrameGeometry.Default }), "Crop changes hash.");
    }

    private static void InvalidSettings()
    {
        foreach (var p in new[]
        {
            Simple with { FilmType = (FilmType)99 }, Simple with { PaperGrade = double.NaN },
            Simple with { FilmBase = new(0, 1, 1) }, Simple with { HighlightsBalance = new(3, 1, 1) },
            Simple with { ShadowsBalance = new(.1, 1, 1) }, Simple with { DensityRange = double.PositiveInfinity },
            Simple with { ScanExposureBias = -2 }, Simple with { PaperGloss = 0 }, Simple with { PrintExposure = 3 },
            Simple with { PaperBlack = 1 }
        }) Throws<ArgumentException>(() => NegativeConversionValidation.Validate(p));
        Throws<ArgumentException>(() => NegativeConversionValidation.Validate(NegativeConversionSettings.Default with { Version = 99 }));
        Throws<ArgumentException>(() => NegativeConversionValidation.Validate(NegativeConversionSettings.Default with { Light = new(new(1, 2, 1), null) }));
        Throws<ArgumentException>(() => NegativeConversionValidation.Validate(NegativeConversionSettings.Default with { Frame = new(0, false, false, new(.9, 0, .2, 1)) }));
    }

    private static void InvalidJson()
    {
        foreach (string json in new[] { "null", "{}", "{broken", "{\"version\":1}", NegativeConversionValidation.Serialize(NegativeConversionSettings.Default).Replace("\"paperGrade\":4", "\"paperGrade\":\"NaN\"") })
            Throws<JsonException>(() => NegativeConversionValidation.Deserialize(json));
        string valid = NegativeConversionValidation.Serialize(NegativeConversionSettings.Default);
        Throws<JsonException>(() => NegativeConversionValidation.Deserialize(valid[..^1] + ",\"unknown\":1}"));
        Throws<JsonException>(() => NegativeConversionValidation.Deserialize(valid.Replace("\"filmBase\":{\"r\":1,\"g\":0.45,\"b\":0.25}", "\"filmBase\":{}")));
        Throws<JsonException>(() => NegativeConversionValidation.Deserialize(valid.Replace("\"crop\":{\"x\":0,\"y\":0,\"width\":1,\"height\":1}", "\"crop\":{\"width\":1,\"height\":1}")));
        Throws<ArgumentException>(() => NegativeConversionValidation.Deserialize(valid.Replace($"\"version\":{NegativeConversionSettings.CurrentVersion}", "\"version\":99")));
        Throws<ArgumentException>(() => NegativeConversionValidation.Deserialize(valid.Replace("\"light\":{\"coefficients\":null,\"source\":null}", "\"light\":null")));
    }

    private static void FilmProfiles()
    {
        var p = NegadoctorParameters.Default;
        var now = DateTimeOffset.UtcNow;
        var profile = new FilmProfile(Guid.NewGuid(), "Empirical film", p.FilmType, new(null, "as shot"),
            p.FilmBase, p.HighlightsBalance, p.ShadowsBalance, p.DensityRange, null, null, now, now);
        NegativeConversionValidation.Validate(profile);
        var tone = new NegativeToneDefaults(p.DensityRange, p.ScanExposureBias, p.PaperBlack,
            p.PaperGrade, p.PaperGloss, p.PrintExposure);
        NegativeConversionValidation.Validate(profile with { Tone = tone, Light = new(new(2, 1, 1.5), "pinned") });
        foreach (var invalid in new[]
        {
            profile with { Id = Guid.Empty }, profile with { Name = " " }, profile with { Name = null! },
            profile with { Light = null! }, profile with { Light = new(new(1, 2, 1), null) },
            profile with { Light = new(new(double.NaN, 1, 1), null) },
            profile with { FilmType = (FilmType)99 }, profile with { FilmBase = new(0, 1, 1) },
            profile with { HighlightsBalance = new(1, 1, 3) }, profile with { ShadowsBalance = new(.1, 1, 1) },
            profile with { DensityRange = double.PositiveInfinity },
            profile with { Tone = tone with { DensityRange = 0 } },
            profile with { Tone = tone with { ScanExposureBias = double.NaN } },
            profile with { Tone = tone with { PaperBlack = 1 } },
            profile with { Tone = tone with { PaperGrade = 0 } },
            profile with { Tone = tone with { PaperGloss = 0 } },
            profile with { Tone = tone with { PrintExposure = double.PositiveInfinity } }
        }) Throws<ArgumentException>(() => NegativeConversionValidation.Validate(invalid));
        Throws<ArgumentNullException>(() => NegativeConversionValidation.Validate((FilmProfile)null!));
        Throws<ArgumentNullException>(() => NegativeConversionValidation.Validate(NegativeConversionSettings.Default with { Light = null! }));
        Throws<ArgumentNullException>(() => NegativeConversionValidation.Validate(NegativeConversionSettings.Default with { Conversion = null! }));
        Throws<ArgumentNullException>(() => NegativeConversionValidation.Validate(NegativeConversionSettings.Default with { Frame = null! }));
    }

    private static void FilmBaseWarnings()
    {
        var src = Image(20, 10, (x, _) => x < 10 ? new(.2, .2, .2) : new(.9, .9, .9));
        var sample = NegativeAnalysis.SampleFilmBase(src, new(0, 0, .5, 1));
        Near(.2, sample.Value.R, 1e-6);
        Check(sample.Warnings.Contains(FilmBaseSampleWarning.ProbablyPictureContent), "Picture sample warning missing.");
        src = Image(10, 10, (x, _) => x == 0 ? new(1, .5, .5) : new(.4, .4, .4));
        sample = NegativeAnalysis.SampleFilmBase(src, new(0, 0, 1, 1));
        Check(sample.Warnings.Contains(FilmBaseSampleWarning.ClippedLight), "Clipping warning missing.");
        Check(sample.Warnings.Contains(FilmBaseSampleWarning.NonUniform), "Uniformity warning missing.");
        Check(sample.Uniformity > .06 && !sample.IsEstimate, "Wrong sample status.");
    }

    private static void FilmBaseEstimate()
    {
        var src = Image(30, 30, (x, y) => x == 0 ? new(1, 1, 1) :
            x >= 5 && x < 25 && y >= 5 && y < 25 ? new(.1, .1, .1) : new(.6, .3, .2));
        var sample = NegativeAnalysis.EstimateFilmBase(src, new(5d / 30, 5d / 30, 20d / 30, 20d / 30));
        Check(sample.IsEstimate, "Estimate must be labeled."); Near(.6, sample.Value.R, 1e-6);
        Near(.3, sample.Value.G, 1e-6); Near(.2, sample.Value.B, 1e-6);
    }

    private static void AutoToneOrder()
    {
        var src = Image(200, 1, (x, _) => x < 100 ? new(.1, .1, .1) : new(.5, .5, .5));
        var result = NegativeAnalysis.AutoTone(src, Simple, new(0, 0, 1, 1));
        Near(1, result.DensityRange, 1e-6); Near(Math.Log10(2), result.ScanExposureBias, 1e-6);
        Near(.1, result.PaperBlack, 1e-6); Near(.96 / .9, result.PrintExposure, 1e-6);
    }

    private static void AutoExposureOffset()
    {
        var src = Image(200, 1, (x, _) => x < 100 ? new(.1, .1, .1) : new(.5, .5, .5));
        var p = Simple with { HighlightsBalance = new(1.5, 1.5, 1.5) };
        var result = NegativeAnalysis.AutoTone(src, p, new(0, 0, 1, 1));
        double expected = .96 / (1 - Math.Pow(10, -1.5 + Math.Log10(2)) + .1);
        Near(expected, result.PrintExposure, 1e-6);
    }

    private static void NeutralPickers()
    {
        var src = Image(2, 2, (_, _) => new(.1, .01, .001));
        var p = Simple with { ScanExposureBias = -.1 };
        var shadow = NegativeAnalysis.NeutralizeShadows(src, p, new(0, 0, 1, 1));
        Near(1, shadow.ShadowsBalance.R, 1e-6); Near(.5, shadow.ShadowsBalance.G, 1e-6);
        Near(1d / 3, shadow.ShadowsBalance.B, 1e-6);
        var highlight = NegativeAnalysis.NeutralizeHighlights(src, p, new(0, 0, 1, 1));
        Near(2, highlight.HighlightsBalance.R); Near(3.1 / 2.1, highlight.HighlightsBalance.G, 1e-6);
        Near(1, highlight.HighlightsBalance.B);
        src = Image(1, 1, (_, _) => new(1, 1, 1));
        NegativeConversionValidation.Validate(NegativeAnalysis.NeutralizeShadows(src, Simple, new(0, 0, 1, 1)));
        NegativeConversionValidation.Validate(NegativeAnalysis.NeutralizeHighlights(src, Simple, new(0, 0, 1, 1)));
    }

    private static void FrameDetection()
    {
        var src = Image(100, 80, (x, y) => x >= 15 && x < 85 && y >= 12 && y < 68
            ? new(.15, .1, .08) : new(.8, .6, .4));
        var found = NegativeAnalysis.DetectFrame(src);
        Check(found.HasValue, "Synthetic frame not detected.");
        Near(.157, found!.Value.Rect.X, .015); Near(.157, found.Value.Rect.Y, .015);
        Near(.686, found.Value.Rect.Width, .025); Check(found.Value.Confidence > .5, "Weak frame confidence.");
        Check(NegativeAnalysis.DetectFrame(Image(100, 80, (_, _) => new(.4, .4, .4))) is null, "Flat image must not produce crop.");
    }

    private static void Geometry()
    {
        var geometry = new NegativeFrameGeometry(90, true, false, new(.1, .2, .5, .6));
        var transform = geometry.ToPhotoEditTransform(1000, 800);
        Equal(new PixelRect(100, 160, 500, 480), transform.CropBounds);
        Near(90, transform.RotationDegrees); Check(transform.FlipHorizontal && !transform.FlipVertical, "Mirror lost.");
        Equal(new PixelRect(0, 0, 1, 1), NegativeFrameGeometry.Default.ToPhotoEditTransform(1, 1).CropBounds);
        Throws<ArgumentException>(() => geometry.ToPhotoEditTransform(0, 10));
    }

    private static void ColorBijection()
    {
        foreach (var rgb in new[] { new RgbTriple(.25, 2, .5), new RgbTriple(1.25, .75, 1.5), new RgbTriple(1, 1, 1) })
        {
            TripleNear(rgb, ColorBalanceControls.FromControls(ColorBalanceControls.ToControls(rgb)));
            TripleNear(rgb, ColorBalanceControls.FromHighlights(ColorBalanceControls.ToHighlights(rgb)));
            foreach (double offset in new[] { -.2, 0, .2 })
                TripleNear(rgb, ColorBalanceControls.FromShadows(ColorBalanceControls.ToShadows(rgb, offset), offset));
        }
        var c = ColorBalanceControls.ToControls(new(1, 1, 1));
        Near(c.Intensity, ColorBalanceControls.ToControls(ColorBalanceControls.FromControls(c with { Warmth = .2, Tint = .1 })).Intensity);
        var clamped = ColorBalanceControls.FromControls(new(0, 20, -20));
        Check(clamped.R >= .25 && clamped.G <= 2 && clamped.B <= 2, "Controls must clamp coefficients.");
    }

    private static void HighlightSigns()
    {
        var c = new ColorBalanceCoordinates(0, 0, 0);
        var p = Simple with { ScanExposureBias = -.1 };
        var baseline = PositiveRgb(p);
        var warm = PositiveRgb(p with { HighlightsBalance = ColorBalanceControls.FromHighlights(c with { Warmth = .2 }) });
        var tint = PositiveRgb(p with { HighlightsBalance = ColorBalanceControls.FromHighlights(c with { Tint = .2 }) });
        Check(warm.R - warm.B > baseline.R - baseline.B, "Positive highlights warmth is reversed.");
        Check((tint.R + tint.B) / 2 - tint.G > (baseline.R + baseline.B) / 2 - baseline.G, "Positive highlights tint is reversed.");
    }

    private static void ShadowSigns()
    {
        var c = new ColorBalanceCoordinates(0, 0, 0);
        foreach (double offset in new[] { -.1, .1, 0 })
        {
            var p = Simple with { ScanExposureBias = offset };
            var baseline = PositiveRgb(p);
            var warm = PositiveRgb(p with { ShadowsBalance = ColorBalanceControls.FromShadows(c with { Warmth = .2 }, offset) });
            var tint = PositiveRgb(p with { ShadowsBalance = ColorBalanceControls.FromShadows(c with { Tint = .2 }, offset) });
            if (offset == 0)
            {
                Equal(baseline, warm); Equal(baseline, tint);
                Check(!ColorBalanceControls.ShadowsHaveVisibleEffect(offset), "Zero offset must disable visible shadow effect.");
            }
            else
            {
                Check(warm.R - warm.B > baseline.R - baseline.B, $"Shadow warmth reversed for offset {offset}.");
                Check((tint.R + tint.B) / 2 - tint.G > (baseline.R + baseline.B) / 2 - baseline.G, $"Shadow tint reversed for offset {offset}.");
            }
        }

    }

    private static void HighlightSignLimit()
    {
        // q = log10(.3) + .6 > 0; positive PaperBlack keeps this counterexample visible.
        var p = Simple with { ScanExposureBias = .6, PaperBlack = .5 };
        var baseline = PositiveRgb(p);
        var controls = new ColorBalanceCoordinates(0, 0, 0);
        var warm = PositiveRgb(p with { HighlightsBalance = ColorBalanceControls.FromHighlights(controls with { Warmth = .2 }) });
        var tint = PositiveRgb(p with { HighlightsBalance = ColorBalanceControls.FromHighlights(controls with { Tint = .2 }) });
        Check(baseline.R > 0 && baseline.R < 255, "Counterexample must not be clipped.");
        Check(warm.R - warm.B < baseline.R - baseline.B, "Positive q must reverse the highlight warmth direction.");
        Check((tint.R + tint.B) / 2 - tint.G < (baseline.R + baseline.B) / 2 - baseline.G,
            "Positive q must reverse the highlight tint direction.");
    }

    private static RgbTriple PositiveRgb(NegadoctorParameters p)
    {
        var result = NegadoctorMath.RenderPositive(Image(1, 1, (_, _) => new(.3, .3, .3)), p);
        return new(result.Pixels[2], result.Pixels[1], result.Pixels[0]);
    }

    private static void Histogram()
    {
        var hist = NegativeAnalysis.Histogram(new(3, 1, [0, 0, 0, 255, 255, 255, 255, 255, 10, 20, 30, 255]));
        Equal(3L, hist.PixelCount); Equal(1L, hist.ClippedShadows); Equal(1L, hist.ClippedHighlights);
        Equal(1L, hist.Red[30]); Equal(1L, hist.Green[20]); Equal(1L, hist.Blue[10]);
        Equal(3L, hist.Luma.Sum());
    }

    private static void InvalidImages()
    {
        Throws<ArgumentException>(() => NegadoctorMath.RenderPositive(new(0, 1, []), Simple));
        Throws<ArgumentException>(() => NegativeAnalysis.SampleFilmBase(new(2, 1, [1, 1, 1]), new(0, 0, 1, 1)));
        Throws<ArgumentException>(() => NegadoctorMath.RenderNegativeView(Image(1, 1, (_, _) => new(1, 1, 1)), 0));
    }

    private static NegativeLinearImage Image(int width, int height, Func<int, int, RgbTriple> pixel)
    {
        float[] pixels = new float[width * height * 3];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var rgb = pixel(x, y); int i = (y * width + x) * 3;
            pixels[i] = (float)rgb.R; pixels[i + 1] = (float)rgb.G; pixels[i + 2] = (float)rgb.B;
        }
        return new(width, height, pixels);
    }

    private static void TripleNear(RgbTriple expected, RgbTriple actual)
    { Near(expected.R, actual.R); Near(expected.G, actual.G); Near(expected.B, actual.B); }
    private static void Near(double expected, double actual, double tolerance = 1e-10) =>
        Check(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, actual {actual}.");
    private static void Equal<T>(T expected, T actual) where T : notnull =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, actual {actual}.");
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
