using System.Text.RegularExpressions;

namespace Mdv.Core;

/// <summary>
/// Where a game's mods go. Normally the <c>mods</c> folder: a mods-folder plugin (OpenIV.asi, OpenRPF, RageOpenV) makes the
/// game read copies of whole archives there. A game that runs <b>Onigiri</b> (onigiri.asi — the loader NaturalVision
/// Enhanced brings, GTA V Enhanced only) reads the <c>onigiri</c> folder instead, and it holds loose files, not archive copies:
/// <code>
/// onigiri\common    = update\update.rpf\common   (and so over common.rpf)
/// onigiri\platform  = update\update.rpf\x64      (and so over x64a–w.rpf and the game's loose x64\*.rpf)
/// onigiri\dlcpacks  = update\x64\dlcpacks
/// </code>
/// Onigiri doesn't read the mods folder, and mods-folder plugins don't read onigiri. ModDrop V keeps its own records
/// (<c>ModDropV.json</c>, <c>.moddropv</c>) in whichever of the two the game reads.
/// </summary>
public static partial class ModsLayout
{
    public const string ModsRoot = "mods";
    public const string OnigiriRoot = "onigiri";
    public const string OnigiriAsi = "onigiri.asi";
    public const string OnigiriIni = "onigiri.ini";
    /// <summary>The loose dlclist.xml Onigiri reads instead of the one in update.rpf.</summary>
    public const string OnigiriDlclist = "onigiri/common/data/dlclist.xml";
    public const string OnigiriCommon = "onigiri/common";
    public const string OnigiriPlatform = "onigiri/platform";
    public const string OnigiriDlcpacks = "onigiri/dlcpacks";

    /// <summary>The game runs Onigiri: onigiri.asi in the game folder (a Legacy game can't — the loader is Enhanced's).</summary>
    public static bool UsesOnigiri(string gameDir) =>
        File.Exists(Path.Combine(gameDir, OnigiriAsi)) && GameEditions.Detect(gameDir) != GameEdition.Legacy;

    /// <summary><c>mods</c> or <c>onigiri</c> — the folder the game reads mods from.</summary>
    public static string RootRel(string gameDir) => UsesOnigiri(gameDir) ? OnigiriRoot : ModsRoot;

    public static string Root(string gameDir) => Path.Combine(gameDir, RootRel(gameDir));

    /// <summary>ModDrop V's housekeeping folder (stashes, the mods layer), relative to the game.</summary>
    public static string HomeRel(string gameDir) => RootRel(gameDir) + "/.moddropv";

    /// <summary>Where add-on packs go, relative to the game: <c>mods/update/x64/dlcpacks</c> or <c>onigiri/dlcpacks</c>.</summary>
    public static string DlcpacksRel(string gameDir) => UsesOnigiri(gameDir) ? OnigiriDlcpacks : "mods/update/x64/dlcpacks";

    /// <summary>Where switched-off packs wait, beside dlcpacks.</summary>
    public static string DisabledDlcpacksRel(string gameDir) =>
        UsesOnigiri(gameDir) ? "onigiri/dlcpacks_disabled" : "mods/update/x64/dlcpacks_disabled";

    /// <summary>Where add-on packs go, as the player reads it: <c>mods\update\x64\dlcpacks</c>, <c>onigiri\dlcpacks</c>.</summary>
    public static string DlcpacksShown(string gameDir) => DlcpacksRel(gameDir).Replace('/', '\\');

    /// <summary>What an installed add-on pack owns in the game folder (its pack folder).</summary>
    public static string PackOwns(string gameDir, string pack) => $"{DlcpacksRel(gameDir)}/{pack}/";

    /// <summary>
    /// The pack folder of an archive in a loader's dlcpacks (<c>mods/update/x64/dlcpacks/&lt;pack&gt;/…</c> or
    /// <c>onigiri/dlcpacks/&lt;pack&gt;/…</c>, relative to the game), or null.
    /// </summary>
    public static string? PackFolderOf(string relPath)
    {
        var m = PackFolderRe().Match(relPath.Replace('\\', '/'));
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^(?:mods/update/x64/dlcpacks|onigiri/dlcpacks)/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex PackFolderRe();

    /// <summary>
    /// Where a whole game archive a mod brings goes (a new dlcpack, a replaced loose archive), relative to the game:
    /// <c>mods/&lt;path&gt;</c>, or its place in onigiri. Null: Onigiri has no place for it (a top-level archive such as
    /// x64e.rpf or update.rpf can't be swapped whole — only the files in it).
    /// </summary>
    public static string? ArchiveRel(string gameDir, string archive)
    {
        var p = archive.Replace('\\', '/').Trim('/');
        if (p.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        if (!UsesOnigiri(gameDir)) return "mods/" + p;
        if (p.StartsWith("onigiri/", StringComparison.OrdinalIgnoreCase)) return p;
        try
        {
            var spot = Mods.OnigiriPaths.Map(p + "/x");
            return spot.Loose || spot.Inner != "x" ? null : spot.Top;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
