using Mdv.Core.Index;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>A place in the game a replacement file can go.</summary>
/// <param name="GamePath">the path through the game's archives (<c>x64e.rpf/levels/gta5/vehicles.rpf/adder.ytd</c>)</param>
/// <param name="Where">what holds it ("base game", "patchday10ng", "update.rpf")</param>
/// <param name="Winner">the copy the game loads</param>
public sealed record ReplaceTarget(string GamePath, string Where, bool Winner)
{
    public override string ToString() => $"{GamePath}  ({Where}{(Winner ? "" : ", not loaded")})";
}

/// <summary>One loose file of a replacement mod and where in the game it goes.</summary>
public sealed class ReplacementFile
{
    public required string Source { get; init; }
    /// <summary>Where it is in the drop ("Adder 4K/x64e.rpf/levels/gta5/vehicles.rpf/adder.ytd").</summary>
    public required string Origin { get; init; }
    public string Name => Path.GetFileName(Source);
    /// <summary>The game path the mod's folders spell out (from the first folder named like an archive), or null.</summary>
    public string? Hint { get; init; }
    /// <summary>Places in the game it could go, the one picked by default first.</summary>
    public List<ReplaceTarget> Candidates { get; } = [];
    /// <summary>Where it goes (a candidate's game path); null: nowhere — not in the game.</summary>
    public string? Target { get; set; }
    /// <summary>Why the target was picked the way it was, when that's worth a word.</summary>
    public string? Note { get; set; }
}

/// <summary>Loose files that replace game files, with no instructions: where each goes is looked up in the game's file index.</summary>
public sealed class ReplacementPackage : ModPackage
{
    public override ModCategory Category => ModCategory.Replacement;
    public List<ReplacementFile> Files { get; } = [];
    /// <summary>The game folder the targets were looked up in (null: not yet).</summary>
    public string? ResolvedFor { get; set; }
}

/// <summary>
/// Replacements: textures, models, sounds, metas… that take the place of the game's files. Each
/// file's place comes from the game's file index — by the folders the mod mirrors
/// (<c>x64e.rpf/levels/gta5/vehicles.rpf/</c>), else by name; the copy the game actually loads
/// is the default, other places can be picked. The files go into copies of the archives in mods.
/// </summary>
public sealed class ReplacementHandler : FileModHandler
{
    public const string Prefix = "replace:";

    public override ModCategory Category => ModCategory.Replacement;
    protected override string IdPrefix => Prefix;

    /// <summary>File types a replacement can be made of.</summary>
    public static readonly HashSet<string> ReplaceableExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ydr", ".ydd", ".yft", ".ytd", ".ybn", ".ycd", ".ymap", ".ytyp", ".ynv", ".ynd", ".yld", ".ypt", ".yed", ".ymf",
        ".ymt", ".gfx", ".awc", ".rel", ".gxt2", ".meta", ".xml", ".dat", ".rpf", ".nametable", ".ide", ".ttf", ".bik", ".fxc",
    };

    /// <summary>The types that mean "this is a replacement" on their own (a lone .xml / .dat doesn't).</summary>
    private static readonly HashSet<string> StrongExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ydr", ".ydd", ".yft", ".ytd", ".ybn", ".ycd", ".ymt", ".gfx", ".awc", ".rel", ".gxt2", ".rpf", ".ypt",
    };

    /// <summary>Folders whose files belong to scripts and plugins, not to the game's archives.</summary>
    private static readonly HashSet<string> ScriptDirs = new(StringComparer.OrdinalIgnoreCase)
        { "scripts", "plugins", "menyooStuff", "ScriptHookVDotNet" };

    public override ModPackage? Analyze(DroppedSource source, DetectionReport report, HandlerEnv env)
    {
        if (report.Has(ModCategory.Package)) return null;                         // an OIV says where its files go
        if (report.Primary?.Category == ModCategory.Weapon) return null;          // weapon replace mods install as add-ons
        var files = source.Files.Where(f => !f.InBackupDir).ToList();
        if (files.Any(f => f.Name.Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase) ||
                           f.Name.Equals("__resource.lua", StringComparison.OrdinalIgnoreCase)))
            return null;                                                              // a FiveM resource: an add-on
        var picked = new List<DroppedFile>();
        foreach (var f in files.OrderBy(f => f.Depth).ThenBy(f => f.Origin, PathUtil.PathOrder))
        {
            var ext = PathUtil.SuffixLower(f.Name);
            if (!ReplaceableExt.Contains(ext)) continue;
            var dirs = f.Origin.Replace('\\', '/').Split('/').SkipLast(1).ToList();
            if (dirs.Any(ScriptDirs.Contains)) continue;
            if (ext == ".rpf" && SourceIntake.IsDlcPack(f.FullPath)) return null;  // a finished add-on pack
            if (ext is ".xml" or ".meta" && IsDocument(f.FullPath)) continue;
            picked.Add(f);
        }
        if (!picked.Any(f => StrongExt.Contains(PathUtil.SuffixLower(f.Name))) && !report.Has(ModCategory.Replacement)) return null;

        var name = SourceIntake.GuessName(source.Sources);
        var pkg = new ReplacementPackage
        {
            Name = name == "Custom Weapon" ? "Replacement" : name,
            Source = source.Sources.Count == 1 ? ModSource.Of(source.Sources[0]) : null,
        };
        var seen = new Dictionary<string, ReplacementFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in picked)
        {
            var hint = HintOf(f.Origin);
            var key = hint ?? f.Name;
            if (seen.TryGetValue(key, out var first))
            {
                pkg.Warnings.Add($"{f.Name} is in the mod more than once — {first.Origin} is used, {f.Origin} is left out " +
                                 "(drop just the folder of the version you want).");
                continue;
            }
            var rf = new ReplacementFile { Source = f.FullPath, Origin = f.Origin, Hint = hint };
            seen[key] = rf;
            pkg.Files.Add(rf);
        }
        pkg.Parts.Add($"{pkg.Files.Count} file(s) to replace: {string.Join(", ", pkg.Files.Take(4).Select(f => f.Name))}" +
                      (pkg.Files.Count > 4 ? ", …" : ""));
        if (pkg.Files.Any(f => f.Hint is not null)) pkg.Parts.Add("folders mirror the game's archives");
        return pkg;
    }

    /// <summary>An XML file that isn't game data (a readme, a script's settings saved as XML)?</summary>
    private static bool IsDocument(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length > 16 << 20) return true;
            var root = ModDetector.RootTag(TextIo.DecodeUtf8Sig(File.ReadAllBytes(path), strict: false));
            return root is null || root.Equals("html", StringComparison.OrdinalIgnoreCase) ||
                   root.Equals("package", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>"x64e.rpf/levels/gta5/vehicles.rpf/adder.ytd" from "Adder/x64e.rpf/levels/gta5/vehicles.rpf/adder.ytd".</summary>
    internal static string? HintOf(string origin)
    {
        var segs = origin.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length - 1; i++)
        {
            if (!segs[i].EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) continue;
            // the game folders the archive sits in are part of the path: update/update.rpf, update/x64/dlcpacks/<pack>/dlc.rpf
            int start = i;
            if (i >= 2 && segs[i - 2].Equals("dlcpacks", StringComparison.OrdinalIgnoreCase)) start = i - 2;
            while (start > 0 && segs[start - 1].ToLowerInvariant() is "x64" or "update") start--;
            return string.Join('/', segs[start..]);
        }
        return null;
    }

    /// <summary>Look every file up in the game's index: its candidates and the target picked by default.</summary>
    public static void Resolve(ReplacementPackage pkg, GameIndex index)
    {
        foreach (var f in pkg.Files)
        {
            f.Candidates.Clear();
            f.Target = null;
            f.Note = null;
            var byName = index.Find(f.Name);
            List<FileHit> hits = [];
            FileHit? hinted = null;
            if (f.Hint is { } hint)
            {
                var segs = hint.Split('/');
                for (int k = 0; k < segs.Length - 1 && hits.Count == 0; k++)
                    hits = index.Find(string.Join('/', segs[k..]));
                hinted = hits.FirstOrDefault(h => h.Active) ?? hits.FirstOrDefault();
                if (hinted is null && byName.Count > 0)
                    f.Note = "its folders don't match the game — placed by its name";
            }

            var groups = byName.GroupBy(GameIndex.GroupKeyOf).Select(g => g.ToList()).ToList();
            FileHit? pick = null;
            if (hinted is not null)
            {
                var group = groups.FirstOrDefault(g => g.Contains(hinted)) ?? [hinted];
                pick = group.FirstOrDefault(h => h.Winner) ?? hinted;
                if (pick != hinted)
                    f.Note = $"its folders point to {Norm(hinted)}, but the game loads {Norm(pick)} — that one is replaced";
            }
            else if (groups.Count > 0)
            {
                var winners = groups.Select(g => g.FirstOrDefault(h => h.Winner) ?? g[0]).ToList();
                pick = winners.FirstOrDefault(w => w.Role != ArchiveRole.Dlc && w.Active) ?? winners.FirstOrDefault(w => w.Active) ?? winners[0];
                if (winners.Count > 1)
                    f.Note ??= $"the game has {winners.Count} different files named {f.Name} — {Norm(pick)} is picked, another can be chosen";
            }
            if (pick is null) continue;

            var ordered = new List<FileHit> { pick };
            ordered.AddRange(groups.Select(g => g.FirstOrDefault(h => h.Winner)).OfType<FileHit>());
            ordered.AddRange(byName.Where(h => h.Active));
            foreach (var h in ordered)
            {
                var path = Norm(h);
                if (f.Candidates.Any(c => c.GamePath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                f.Candidates.Add(new ReplaceTarget(path, h.Source, h.Winner));
                if (f.Candidates.Count >= 30) break;
            }
            f.Target = f.Candidates[0].GamePath;
        }
        pkg.ResolvedFor = Path.GetFullPath(index.GameDir);
    }

    /// <summary>A hit's game path; a copy in mods counts as the game's archive it replaces.</summary>
    private static string Norm(FileHit h) => h.InMods ? h.GamePath[GameIndex.ModsPrefix.Length..] : h.GamePath;

    public override InstallPlan PlanInstall(ModPackage package, InstallTarget target)
    {
        var pkg = (ReplacementPackage)package;
        if (pkg.ResolvedFor is null || !pkg.ResolvedFor.Equals(Path.GetFullPath(target.GameDir), StringComparison.OrdinalIgnoreCase))
            Resolve(pkg, GameIndex.Open(target.GameDir, target.IndexCacheRoot));
        var id = IdFor(pkg.Name);
        var files = pkg.Files.Where(f => f.Target is not null).ToList();
        if (files.Count == 0)
            throw new InvalidOperationException(
                $"None of the mod's files ({string.Join(", ", pkg.Files.Take(5).Select(f => f.Name))}) are in this game — nothing to replace.");

        var plan = BeginInstall(id, pkg.Name, target);
        foreach (var f in files) plan.Add(new RpfPutOp(f.Target!, f.Source, id));
        foreach (var f in pkg.Files)
        {
            if (f.Target is null) plan.Warnings.Add($"{f.Name} is not a file of this game — skipped.");
            else if (f.Note is not null) plan.Warnings.Add($"{f.Name}: {f.Note}.");
        }
        plan.Warnings.AddRange(ConflictWarnings(id, target, files.Select(f => f.Target!)));
        var archives = files.Select(f => ModsOverlay.Split(f.Target!).Archive).Distinct().Count();
        plan.Add(Register(id, ModCategory.Replacement, pkg, target, $"{files.Count} file(s) in {archives} archive(s)"));
        return plan;
    }
}
