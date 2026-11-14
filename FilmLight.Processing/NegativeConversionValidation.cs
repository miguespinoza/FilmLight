using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;


namespace FilmLight.Processing;

public static class NegativeConversionValidation
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    // Required constructor parameters alone do not cover positional record structs.
                    if (info.Type == typeof(RgbTriple) || info.Type == typeof(NormalizedRect))
                        foreach (var property in info.Properties) property.IsRequired = true;
                }
            }
        }
    };

    public static void Validate(NegadoctorParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!Enum.IsDefined(parameters.FilmType)) throw new ArgumentOutOfRangeException(nameof(parameters.FilmType));
        Triple(parameters.FilmBase, .00001, 1.5, nameof(parameters.FilmBase));
        Triple(parameters.HighlightsBalance, .25, 2, nameof(parameters.HighlightsBalance));
        Triple(parameters.ShadowsBalance, .25, 2, nameof(parameters.ShadowsBalance));
        Range(parameters.DensityRange, .1, 6, nameof(parameters.DensityRange));
        Range(parameters.ScanExposureBias, -1, 1, nameof(parameters.ScanExposureBias));
        Range(parameters.PaperBlack, -.5, .5, nameof(parameters.PaperBlack));
        Range(parameters.PaperGrade, 1, 8, nameof(parameters.PaperGrade));
        Range(parameters.PaperGloss, .0001, 1, nameof(parameters.PaperGloss));
        Range(parameters.PrintExposure, .5, 2, nameof(parameters.PrintExposure));
    }

    public static void Validate(NegativeConversionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Version != NegativeConversionSettings.CurrentVersion)
            throw new ArgumentException($"Unsupported Negative Conversion Settings version {settings.Version}; expected {NegativeConversionSettings.CurrentVersion}.", nameof(settings));
        Validate(settings.Conversion);
        Validate(settings.Finish);
        ValidateLight(settings.Light);
        ArgumentNullException.ThrowIfNull(settings.Frame);
        Range(settings.Frame.RotationDegrees, -360, 360, nameof(settings.Frame.RotationDegrees));
        NegativeFrameGeometry.ValidateCrop(settings.Frame.Crop);
        if (settings.BaseSample is { } sample)
        {
            NegativeFrameGeometry.ValidateCrop(sample.Region);
            Triple(sample.Value, .00001, 1.5, nameof(sample.Value));
            Range(sample.Uniformity, 0, double.MaxValue, nameof(sample.Uniformity));
            ArgumentNullException.ThrowIfNull(sample.Warnings);
            if (sample.Warnings.Any(warning => !Enum.IsDefined(warning)))
                throw new ArgumentException("Unknown Film Base Sample warning.", nameof(settings));
        }
    }

    public static void Validate(NegativePrintFinish finish)
    {
        ArgumentNullException.ThrowIfNull(finish);
        Range(finish.ExposureEv, -3, 3, nameof(finish.ExposureEv));
        Range(finish.Highlights, -100, 100, nameof(finish.Highlights));
        Range(finish.Shadows, -100, 100, nameof(finish.Shadows));
    }

    public static void Validate(FilmProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty) throw new ArgumentException("Film Profile ID must not be empty.", nameof(profile));
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);
        ValidateLight(profile.Light);
        var parameters = NegadoctorParameters.Default with
        {
            FilmType = profile.FilmType,
            FilmBase = profile.FilmBase,
            HighlightsBalance = profile.HighlightsBalance,
            ShadowsBalance = profile.ShadowsBalance,
            DensityRange = profile.DensityRange
        };
        Validate(parameters);
        if (profile.Tone is { } tone)
        {
            Validate(parameters with
            {
                DensityRange = tone.DensityRange,
                ScanExposureBias = tone.ScanExposureBias,
                PaperBlack = tone.PaperBlack,
                PaperGrade = tone.PaperGrade,
                PaperGloss = tone.PaperGloss,
                PrintExposure = tone.PrintExposure
            });
        }
    }

    public static string Serialize(NegativeConversionSettings settings)
    {
        Validate(settings);
        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    public static NegativeConversionSettings Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("version", out var versionProperty)
            || !versionProperty.TryGetInt32(out int version))
            throw new JsonException("Negative Conversion Settings require an integer version.");
        if (version is not (1 or 2 or NegativeConversionSettings.CurrentVersion))
            throw new ArgumentException($"Unsupported Negative Conversion Settings version {version}.");
        NegativeConversionSettings settings;
        if (version == 1)
        {
            var old = JsonSerializer.Deserialize<VersionOneSettings>(json, JsonOptions)
                ?? throw new JsonException("Negative Conversion Settings cannot be null.");
            settings = new(NegativeConversionSettings.CurrentVersion, old.FilmProfileId, old.Light,
                old.Conversion, old.Frame, old.BaseSample);
        }
        else if (version == 2)
        {
            var old = JsonSerializer.Deserialize<VersionTwoSettings>(json, JsonOptions)
                ?? throw new JsonException("Negative Conversion Settings cannot be null.");
            settings = new(NegativeConversionSettings.CurrentVersion, old.FilmProfileId, old.Light,
                old.Conversion, old.Frame, old.BaseSample) { Finish = old.Finish };
        }
        else
        {
            settings = JsonSerializer.Deserialize<NegativeConversionSettings>(json, JsonOptions)
                ?? throw new JsonException("Negative Conversion Settings cannot be null.");
            settings = settings with { Version = NegativeConversionSettings.CurrentVersion };
        }
        Validate(settings);
        return settings;
    }

    private sealed record VersionOneSettings(int Version, Guid? FilmProfileId, LightBalance Light,
        NegadoctorParameters Conversion, NegativeFrameGeometry Frame, FilmBaseSample? BaseSample);

    private sealed record VersionTwoSettings(int Version, Guid? FilmProfileId, LightBalance Light,
        NegadoctorParameters Conversion, NegativeFrameGeometry Frame, FilmBaseSample? BaseSample)
    {
        [JsonRequired]
        public NegativePrintFinish Finish { get; init; } = NegativePrintFinish.Neutral;
    }

    public static string SettingsHash(NegativeConversionSettings settings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(settings)))).ToLowerInvariant();

    internal static void Range(double value, double min, double max, string name)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            throw new ArgumentOutOfRangeException(name, value, $"Must be finite and between {min} and {max}.");
    }

    private static void Triple(RgbTriple value, double min, double max, string name)
    {
        Range(value.R, min, max, name);
        Range(value.G, min, max, name);
        Range(value.B, min, max, name);
    }

    private static void ValidateLight(LightBalance light)
    {
        ArgumentNullException.ThrowIfNull(light);
        if (light.Coefficients is not { } coefficients) return;
        Triple(coefficients, double.Epsilon, double.MaxValue, nameof(light));
        if (coefficients.G != 1)
            throw new ArgumentException("Pinned Light Balance coefficients must be normalized to G = 1.", nameof(light));
    }
}

