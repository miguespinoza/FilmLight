using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilmLight;

internal sealed record AppSettings(int Temperature = 5000, int Interval = 5, bool Repeat = false,
    string? SdkDirectory = null, string? OutputDirectory = null, string? ActiveRollDirectory = null)
{
    [JsonIgnore] public string SdkPath => SdkDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SDK13410");
    [JsonIgnore] public string OutputPath => OutputDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FilmLight");
    private static readonly string Folder = Environment.GetEnvironmentVariable("FILMLIGHT_APPDATA_ROOT") is { Length: > 0 } isolated
        ? Path.GetFullPath(isolated)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilmLight");
    private static string FileName => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        if (!File.Exists(FileName))
            return new();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FileName))
            ?? throw new InvalidDataException("Settings file is empty.");
        if (settings.Temperature is < 3000 or > 7500 || settings.Interval is < 1 or > 120)
            throw new InvalidDataException("Saved settings are outside the supported range.");
        if ((settings.SdkDirectory is not null && !Path.IsPathFullyQualified(settings.SdkDirectory))
            || (settings.OutputDirectory is not null && !Path.IsPathFullyQualified(settings.OutputDirectory))
            || (settings.ActiveRollDirectory is not null && !Path.IsPathFullyQualified(settings.ActiveRollDirectory)))
            throw new InvalidDataException("Saved SDK/output folder paths must be absolute.");
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temporary = FileName + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, FileName, true);
    }
}
