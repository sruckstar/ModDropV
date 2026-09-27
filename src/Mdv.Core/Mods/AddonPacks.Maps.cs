using Mdv.Core;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// Maps and props: a finished map / props pack, a FiveM map resource or loose placement (.ymap) and archetype (.ytyp)
/// files — binary or as XML — install as a pack of their own (<see cref="DlcComposer"/> lays a map out as Rockstar's
/// DLCs do: a level pack whose map data archive holds the placements and a manifest binding them to their archetypes).
/// A map saved by Menyoo / Map Editor goes to the tool's folder (<see cref="PlacementHandler"/>) — on its own, or as
/// the other way in when the mod ships a placement file of the same map too. What else the mod asks the player to
/// install by hand — game files it replaces (heightmap.dat, a scenario .ymt), its scripts — comes along as its parts.
/// </summary>
public sealed partial class AddonPackHandler
{
    /// <summary>Map / prop parts of the drop: the Menyoo / Map Editor way in, and the other parts of the mod.</summary>
    private void AttachMapParts(AddonPackage pkg, DroppedSource source, DetectionReport report, HandlerEnv env, string? packDir)
    {
        if (Category == ModCategory.Map) pkg.Placement = PlacementHandler.Read(source, pkg.Name);
        if (pkg.Placement is { } pl) pkg.Parts.Add(L.T($"also as {string.Join(" / ", pl.Files.Select(f => f.Tool == PlacementTool.Menyoo ? L.T("a Menyoo map") : L.T("a Map Editor map")).Distinct())} ({string.Join(", ", pl.Files.Select(f => f.Name))})"));

        // what else the mod has the player install: game files it replaces, its scripts
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pkg.Compose is { } spec) foreach (var f in spec.Files) used.Add(f.Source);
        foreach (var f in pkg.Placement?.Files ?? []) used.Add(f.Source);
        var roots = source.Files.Where(f => DlcComposer.IsManifest(f.Name)).Select(f => Path.GetDirectoryName(f.FullPath)!).ToList();
        if (packDir is not null) roots.Add(packDir);
        var rest = source.Files.Where(f => !f.InBackupDir && !used.Contains(f.FullPath) &&
                                           !roots.Any(r => f.FullPath.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) &&
                                           !DlcComposer.IsMapFile(f.Name) && !IsMapAsset(f.Name))
                               .ToList();
        var picked = ReplacementHandler.Pick(rest, out bool dlc);
        if (!dlc && picked.Count > 0)
        {
            var rp = ReplacementHandler.Build(picked, pkg.Name + L.T(" — game files"), pkg.Source);
            rp.Parts.Clear();
            rp.Parts.Add(L.T($"replaces {string.Join(", ", rp.Files.Select(f => f.Name))} in the game"));
            pkg.Extras.Add(rp);
        }
        if (new ScriptHandler().Analyze(source, report, env) is ScriptPackage sp && sp.Files.Any(f => f.IsEntry))
        {
            sp.Name = pkg.Name + " — scripts";
            foreach (var v in sp.Variants) v.Files.RemoveAll(f => !BelongsToScripts(f, v.Files));
            pkg.Extras.Add(sp);
        }
        foreach (var e in pkg.Extras)
            pkg.Parts.Add(e is ScriptPackage s
                ? L.T($"with its scripts ({string.Join(", ", s.Files.Where(f => f.IsEntry).Select(f => f.Name))})")
                : L.T($"with game file changes ({string.Join(", ", ((ReplacementPackage)e).Files.Select(f => f.Name))})"));
    }

    /// <summary>
    /// A file a map's scripts need: the scripts themselves, the libraries / hooks they use, and their own files beside
    /// them — not the notes and the "if it doesn't work, install these" folder map authors put next to them.
    /// </summary>
    private static bool BelongsToScripts(ScriptFile f, List<ScriptFile> all)
    {
        if (f.IsEntry || f.DependencyId is not null) return true;
        if (PathUtil.SuffixLower(f.Name) is ".txt" or ".md" or ".pdf" or ".url") return false;
        var dir = Path.GetDirectoryName(f.Source);
        return all.Any(e => e.IsEntry && Path.GetDirectoryName(e.Source) is { } ed &&
                            (ed.Equals(dir, StringComparison.OrdinalIgnoreCase) ||
                             (dir is not null && dir.StartsWith(Path.Combine(ed, Path.GetFileNameWithoutExtension(e.Name)), StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Models, textures and collisions — a map's assets, never a replacement part next to it.</summary>
    private static bool IsMapAsset(string name) =>
        PathUtil.SuffixLower(name) is ".ydr" or ".ydd" or ".yft" or ".ytd" or ".ybn" or ".ymf" or ".ypt" or ".ycd" or ".rpf";

    /// <summary>
    /// Loose map / props files: an add-on of their own (a map when there are placements, else props), with the
    /// Menyoo / Map Editor way in when the mod has that too; only a Menyoo / Map Editor map: that. Props that come
    /// as models with no archetypes file can't be spawned — said so.
    /// </summary>
    private ModPackage? LooseMap(DroppedSource source, DetectionReport report, HandlerEnv env, string name, ModSource? src)
    {
        var spec = DlcComposer.FromMapFiles(source);
        if (spec is not null && spec.Content.Kind == Category)
        {
            var (packName, from) = PackNameOf(source, null, null);
            var pkg = new AddonPackage(Category)
            {
                Name = name, Source = src, Compose = spec, DataDir = env.DataDir, PackName = packName, PackNameFrom = from,
            };
            pkg.Warnings.AddRange(spec.Warnings);
            Describe(pkg, Category == ModCategory.Map ? L.T("loose map files — packed into a dlc.rpf") : L.T("loose props with their archetypes — packed into a dlc.rpf"));
            AttachMapParts(pkg, source, report, env, null);
            return pkg;
        }
        if (Category == ModCategory.Map && spec is null && PlacementHandler.Read(source, name) is { } placements)
            return placements;
        if (Category == ModCategory.Prop && spec is null && report.Primary?.Category == ModCategory.Prop &&
            source.Files.Any(f => PathUtil.SuffixLower(f.Name) == ".ydr"))
            throw new IntakeException(L.T("Props that come as models only (.ydr) with no archetypes file (.ytyp) — the game can't spawn them. " +
                                      "If they replace the game's own props, their names must match the game's; add-on props need a .ytyp " +
                                      "(ModDrop V's modder tools will write one in a later version)."));
        return null;
    }

    /// <summary>"The map loads with the game — look around -1 234, 567." / "spawn the props by name: …".</summary>
    private static string MapDone(AddonPackage pkg)
    {
        if (pkg.Kind == ModCategory.Prop)
        {
            var props = pkg.Content.Archetypes;
            return props.Count == 0 ? L.T("Props installed.")
                : L.T($"Props installed — spawn them with Menyoo's Object Spooner or Map Editor by name: {string.Join(", ", props.Take(6))}{(props.Count > 6 ? ", …" : "")}.");
        }
        var at = pkg.Content.Maps.FirstOrDefault(m => m.Center is not null)?.Center;
        return at is { } c
            ? L.T($"Map installed — it loads with the game in story mode, around {c.X:0}, {c.Y:0} (z {c.Z:0}).")
            : L.T("Map installed — it loads with the game in story mode.");
    }

    /// <summary>The parts of the mod (game files, scripts) the player keeps on, each installed with it as a mod of its own.</summary>
    private static void AddExtras(InstallPlan plan, AddonPackage pkg, InstallTarget target)
    {
        foreach (var e in pkg.Extras.Where(e => !pkg.SkippedExtras.Contains(e)))
        {
            var part = ModLibrary.HandlerFor(e.Category).PlanInstall(e, target);
            // the mods folder is made ready once; what the game keeps of its own is no news here
            plan.Ops.AddRange(part.Ops.Where(o => o is not EnsureModsLoaderOp || !plan.Ops.Any(p => p is EnsureModsLoaderOp)));
            plan.Warnings.AddRange(part.Warnings.Where(w => !w.EndsWith("— it is kept.", StringComparison.Ordinal)).Select(w => $"{e.Name}: {w}"));
        }
    }
}
