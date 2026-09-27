using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mdv.App.Services;

/// <summary>Small per-user preferences (theme, mode, last folders).</summary>
public sealed class Settings
{
    public string Theme { get; set; } = "dark";
    /// <summary>Interface language ("ru-RU"…); empty = the system's.</summary>
    public string Language { get; set; } = "";
    public string Mode { get; set; } = "modder";
    public string? LastOutput { get; set; }
    public string? LastGame { get; set; }
    /// <summary>Game build the modder flow packs for: "legacy" / "enhanced".</summary>
    public string Edition { get; set; } = "legacy";
    /// <summary>The add-on type the modder flow builds: "weapon", "vehicle", …</summary>
    public string AddonType { get; set; } = "weapon";

    /// <summary>Where <see cref="Save"/> writes; null (a fresh instance) = in-memory only.</summary>
    [JsonIgnore] public string? FilePath { get; private set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            var s = File.Exists(AppPaths.SettingsFile)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new()
                : new Settings();
            s.FilePath = AppPaths.SettingsFile;
            return s;
        }
        catch (Exception ex)
        {
            AppLog.Error("could not read settings", ex);
        }
        return new Settings { FilePath = AppPaths.SettingsFile };
    }

    public void Save()
    {
        if (FilePath is null) return;
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            AppLog.Error("could not save settings", ex);
        }
    }
}
