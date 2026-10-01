using System.Text.RegularExpressions;
using Mdv.Core.Index;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// A mod laid out like the game folder with no OIV instructions — several finished packs in
/// <c>update/x64/dlcpacks/&lt;name&gt;/dlc.rpf</c> ("put these into your game folder"), maybe with game files to
/// replace by hand ("Replace with OpenIV/gen9_exclusive_assets_vehicles.meta"). It is read as an OIV package of its
/// own making: every pack goes into mods and into dlclist.xml, each loose game file over the game's file of that name,
/// so the whole mod goes in and out as one — not as one of its packs taken for a weapon, a car or a prop.
/// </summary>
public static partial class DlcPackSet
{
    // the folder a pack sits in: ".../dlcpacks/<name>/dlc.rpf"
    [GeneratedRegex(@"(?:^|/)dlcpacks/([^/]+)/dlc\.rpf$", RegexOptions.IgnoreCase)]
    private static partial Regex PackRe();

    // game files a mod has players put in place by hand (models, textures, data); readmes, pictures and scripts aren't
    private static readonly HashSet<string> GameFileExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".meta", ".xml", ".ymt", ".dat", ".rel", ".awc", ".gxt2", ".ymf", ".ytyp", ".ymap", ".ybn", ".ypt", ".ycd", ".ynv",
        ".ydr", ".ydd", ".yft", ".ytd", ".gfx", ".nametable",
    };

    /// <summary>The drop as a package, or null when it holds fewer than two packs in dlcpacks folders.</summary>
    public static OivPackage? Read(DroppedSource source)
    {
        var packs = new List<(string Name, DroppedFile File)>();
        foreach (var f in source.Files.Where(f => !f.InBackupDir).OrderBy(f => f.Origin, PathUtil.PathOrder))
        {
            if (PackRe().Match(f.Origin) is not { Success: true } m) continue;
            var name = m.Groups[1].Value.ToLowerInvariant();
            if (packs.Any(p => p.Name == name) || !SourceIntake.IsDlcPack(f.FullPath)) continue;
            packs.Add((name, f));
        }
        if (packs.Count < 2) return null;

        var pkg = new OivPackage { Name = SourceIntake.GuessName(source.Sources), Root = source.WorkDir, FromLayout = true };
        foreach (var (name, f) in packs)
            pkg.Steps.Add(new OivAdd($"update/x64/dlcpacks/{name}/dlc.rpf", false, f.FullPath));
        pkg.Steps.Add(new OivXml(OivHandler.DlclistPath, true,
            [new OivXmlEdit(XmlPatchMode.Add, "/SMandatoryPacksData/Paths", [.. packs.Select(p => $"<Item>dlcpacks:/{p.Name}/</Item>")])]));
        var names = packs.Select(p => p.Name).ToList();
        pkg.Parts.Add(L.T($"{packs.Count} finished DLC packs ({string.Join(", ", names.Take(4))}{(names.Count > 4 ? ", …" : "")}) — into mods, listed in dlclist.xml"));

        // the game files it has players replace: found in the game by name when installing
        var packDirs = packs.Select(p => Path.GetDirectoryName(p.File.FullPath)! + Path.DirectorySeparatorChar).ToList();
        var loose = source.Files.Where(f => !f.InBackupDir && GameFileExts.Contains(Path.GetExtension(f.Name)) &&
                                            !packDirs.Any(d => f.FullPath.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                                .OrderBy(f => f.Origin, PathUtil.PathOrder).ToList();
        foreach (var f in loose.DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            pkg.Steps.Add(new OivLocate(Tail(f.Origin), true, f.FullPath));
        if (loose.Count > 0)
            pkg.Parts.Add(L.T($"replaces the game’s {string.Join(", ", loose.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(4))}{(loose.Count > 4 ? ", …" : "")} (found in the game by name)"));
        return pkg;
    }

    /// <summary>
    /// What to look the file up by: its name, with the folders after the last archive-named one when it is laid out by
    /// archive (<c>update.rpf/common/data/x.meta</c> → <c>common/data/x.meta</c>).
    /// </summary>
    private static string Tail(string origin)
    {
        var segs = origin.Split('/');
        int rpf = segs.Length - 2;
        while (rpf >= 0 && !segs[rpf].EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) rpf--;
        return rpf < 0 ? segs[^1] : string.Join('/', segs[(rpf + 1)..]);
    }

    /// <summary>
    /// The game path of the file a <see cref="OivLocate"/> replaces: the one file of that name the game has (outside the
    /// <paramref name="ownPacks"/> being installed), else null and why.
    /// </summary>
    public static (string? Path, string? Why) Locate(GameIndex index, OivLocate step, IReadOnlyCollection<string> ownPacks)
    {
        var groups = index.Find(step.Path)
            .Where(h => !h.InOnigiri && !ownPacks.Any(p => h.TargetPath.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(GameIndex.GroupKeyOf).ToList();
        var name = Path.GetFileName(step.Path);
        if (groups.Count == 0) return (null, L.T($"{name}: the game has no file of that name — not put in."));
        if (groups.Count > 1)
            return (null, L.T($"{name}: the game has {groups.Count} different files of that name ({string.Join(", ", groups.Take(3).Select(g => g.First().TargetPath))}) — not put in; replace the right one with the Replace panel."));
        var g0 = groups[0];
        return ((g0.FirstOrDefault(h => !h.Installed) ?? g0.First()).TargetPath, null);
    }
}
