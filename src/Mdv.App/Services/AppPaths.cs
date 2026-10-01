namespace Mdv.App.Services;

/// <summary>Where the app reads its bundled data and keeps its own state.</summary>
public static class AppPaths
{
    public const string AppName = "ModDrop V";

    /// <summary>Folder the executable (and its bundled data/) lives in.</summary>
    public static string Root => AppContext.BaseDirectory;

    public static string Data => Path.Combine(Root, "data");
    public static string Templates => Path.Combine(Data, "templates");

    /// <summary>%LOCALAPPDATA%\ModDropV — logs, settings, dropped sources and the staged
    /// AddonWeapons packs.</summary>
    public static string AppData
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(baseDir, "ModDropV");
        }
    }

    public static string LogDir => Ensure(Path.Combine(AppData, "logs"));

    /// <summary>
    /// Persistent scratch folder a dlcpack is staged in before it is copied into the game
    /// (player flow). Stable across runs so a merged AddonWeapons pack can be extended.
    /// </summary>
    public static string StagingDir => Ensure(Path.Combine(AppData, "staging"));

    /// <summary>
    /// Staging per game edition: Legacy uses <see cref="StagingDir"/>; Enhanced gets its own, so a player with both games
    /// doesn't get one game's weapons pushed into the other.
    /// </summary>
    public static string StagingFor(Mdv.Core.GameEdition edition) =>
        edition == Mdv.Core.GameEdition.Enhanced ? Ensure(Path.Combine(AppData, "staging-enhanced")) : StagingDir;

    /// <summary>
    /// Scratch space for a dropped source (player flow): unpacked archives plus the flat
    /// input folder the pipeline reads. Only the current drop is kept.
    /// </summary>
    public static string SourcesDir => Ensure(Path.Combine(AppData, "sources"));

    /// <summary>
    /// Where a drop may be unpacked when <see cref="SourcesDir"/>'s drive has no room for it (a mod of many gigabytes):
    /// <c>X:\ModDropV.tmp</c> on the game's drive, then on the drives of what was dropped.
    /// </summary>
    public static List<string> SpareSourceRoots(string? gameFolder, IEnumerable<string> dropped) =>
        [.. new[] { gameFolder }.Concat(dropped).Select(Mdv.Core.SourceIntake.SpareRoot).OfType<string>()
                                .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Every spare unpack folder there is now (on any ready fixed drive).</summary>
    public static IEnumerable<string> ExistingSpareSourceRoots()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }
        foreach (var d in drives)
        {
            string? dir = null;
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady) dir = Path.Combine(d.RootDirectory.FullName, Mdv.Core.SourceIntake.SpareFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            if (dir is not null && Directory.Exists(dir)) yield return dir;
        }
    }

    public static string SettingsFile => Path.Combine(Ensure(AppData), "settings.json");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
