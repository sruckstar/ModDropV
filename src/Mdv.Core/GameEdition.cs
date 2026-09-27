namespace Mdv.Core;

/// <summary>
/// Which GTA V build a pack targets. Both read the same RPF7 container (OPEN archives
/// in the mods folder); they differ in the resources inside it — Enhanced (gen9)
/// drawables and texture dictionaries have their own layout and RSC7 versions, so
/// Legacy models must be converted before Enhanced can stream them.
/// </summary>
public enum GameEdition
{
    Legacy,
    Enhanced,
}

public static class GameEditions
{
    public const string LegacyExe = "GTA5.exe";
    public const string EnhancedExe = "GTA5_Enhanced.exe";

    public static string ExeName(this GameEdition e) => e == GameEdition.Enhanced ? EnhancedExe : LegacyExe;

    public static string DisplayName(this GameEdition e) =>
        e == GameEdition.Enhanced ? "GTA V Enhanced" : "GTA V Legacy";

    /// <summary>The manifest's "target" line.</summary>
    public static string TargetLabel(this GameEdition e) =>
        e == GameEdition.Enhanced ? L.T("GTA V Enhanced (OPEN, gen9 resources)") : L.T("GTA V Legacy (OPEN)");

    /// <summary>
    /// The edition of the game installed in <paramref name="gameDir"/>, decided by its
    /// executable; null if the folder has neither (or both — a mixed folder is ambiguous).
    /// </summary>
    public static GameEdition? Detect(string gameDir)
    {
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir)) return null;
        bool legacy = File.Exists(Path.Combine(gameDir, LegacyExe));
        bool enhanced = File.Exists(Path.Combine(gameDir, EnhancedExe));
        if (legacy == enhanced) return null;
        return enhanced ? GameEdition.Enhanced : GameEdition.Legacy;
    }

    /// <summary>True when the folder holds both executables (edition can't be told apart).</summary>
    public static bool IsAmbiguous(string gameDir) =>
        File.Exists(Path.Combine(gameDir, LegacyExe)) && File.Exists(Path.Combine(gameDir, EnhancedExe));

    /// <summary>Parse "legacy" / "enhanced" (also "gen8"/"gen9", "le"/"ee"); null for "auto" or empty.</summary>
    public static GameEdition? Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => null,
        "legacy" or "gen8" or "le" => GameEdition.Legacy,
        "enhanced" or "gen9" or "ee" => GameEdition.Enhanced,
        _ => throw new ArgumentException(L.T($"Unknown game edition '{s}' — use legacy, enhanced or auto.")),
    };
}
