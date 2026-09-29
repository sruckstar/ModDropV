using System.Text;

namespace Mdv.Core.Mods;

/// <summary>
/// The plugins GTA V Enhanced needs next to the raised gameconfig.xml limits: without Heap Adjuster and Packfile Limit
/// Adjuster (their Enhanced builds, MIT, shipped in data/plugins/limits-enhanced) the game crashes with them. Weapon Limits
/// Adjuster (ModDrop V's own port, plugins/WeaponLimitsAdjusterEnhanced) makes room for weapon components past the game's
/// 470. They go in with the limits and stay when the last mod is removed — without mods they change nothing that matters.
/// A plugin the game has is kept, unless it is a build for Legacy (Heap Adjuster has the same file name in both); any
/// Enhanced WeaponLimitsAdjuster*.asi counts as the weapon one — two of them would patch the same code.
/// </summary>
public static class LimitAdjusters
{
    public const string Folder = "limits-enhanced";

    public const string WeaponAsi = "WeaponLimitsAdjusterEnhanced.asi";

    /// <summary>The plugins; <c>Present</c> is the file pattern of the ones that count as already there.</summary>
    public static readonly (string Name, string Asi, string Ini, string Present)[] Plugins =
    [
        ("Heap Adjuster", "HeapAdjuster.asi", "HeapAdjuster.ini", "HeapAdjuster.asi"),
        ("Packfile Limit Adjuster", "PackfileLimitAdjusterEnhanced.asi", "PackfileLimitAdjusterEnhanced.ini", "PackfileLimitAdjusterEnhanced.asi"),
        ("Weapon Limits Adjuster", WeaponAsi, "WeaponLimitsAdjusterEnhanced.ini", "WeaponLimitsAdjuster*.asi"),
    ];

    private static readonly byte[] EnhancedMark = Encoding.ASCII.GetBytes(GameEditions.EnhancedExe);
    private static readonly byte[] EnhancedMarkWide = Encoding.Unicode.GetBytes(GameEditions.EnhancedExe);

    /// <summary>An .asi made for GTA V Enhanced: it looks for the Enhanced executable by name (in ASCII or UTF-16).</summary>
    public static bool ForEnhanced(string asi)
    {
        try
        {
            var bytes = File.ReadAllBytes(asi).AsSpan();
            return bytes.IndexOf(EnhancedMark) >= 0 || bytes.IndexOf(EnhancedMarkWide) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;                                 // can't tell: leave it alone
        }
    }

    /// <summary>
    /// The files to copy into the game folder (source, game-relative): each plugin it lacks or has only for Legacy, with
    /// its settings when it has none or the plugin is replaced. Empty for Legacy or when the bundled files are missing.
    /// </summary>
    public static List<(string Source, string GameRel)> Missing(string gameDir, GameEdition edition, string pluginsDir)
    {
        var list = new List<(string, string)>();
        if (edition != GameEdition.Enhanced) return list;
        var dir = Path.Combine(pluginsDir, Folder);
        foreach (var (_, asi, ini, present) in Plugins)
        {
            if (!File.Exists(Path.Combine(dir, asi))) continue;
            var there = Path.Combine(gameDir, asi);
            if (Directory.Exists(gameDir) && Directory.EnumerateFiles(gameDir, present).Any(ForEnhanced)) continue;
            list.Add((Path.Combine(dir, asi), asi));
            if (File.Exists(Path.Combine(dir, ini)) && (File.Exists(there) || !File.Exists(Path.Combine(gameDir, ini))))
                list.Add((Path.Combine(dir, ini), ini));
        }
        return list;
    }

    /// <summary>The step that installs them — null when the game has them (or isn't Enhanced).</summary>
    public static PlanOp? Op(string gameDir, GameEdition edition, string pluginsDir)
    {
        var files = Missing(gameDir, edition, pluginsDir);
        if (files.Count == 0) return null;
        var names = Plugins.Where(p => files.Any(f => f.GameRel == p.Asi)).Select(p => p.Name);
        return new CopyFilesOp(files, L.T($"Install {string.Join(" + ", names)} for GTA V Enhanced — with raised limits the game crashes without them"));
    }
}
