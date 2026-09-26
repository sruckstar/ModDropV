using System.Text.RegularExpressions;
using Mdv.Core.Index;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// An add-on vehicle / ped: a finished pack (dlc.rpf with setup2.xml) or files still to be packed
/// (a FiveM resource, loose models with their metas) — installed as a dlcpack of its own. When the mod
/// also ships a Replace version (loose models of a game vehicle next to the pack), that is offered too.
/// </summary>
public sealed class AddonPackage : ModPackage
{
    public AddonPackage(ModCategory kind) => Kind = kind;

    public ModCategory Kind { get; }
    public override ModCategory Category => Kind;

    /// <summary>A finished pack (null: <see cref="Compose"/> it).</summary>
    public AddonContent.FinishedPack? Finished { get; init; }
    public ComposeSpec? Compose { get; init; }
    public AddonContent Content => Finished?.Content ?? Compose!.Content;

    /// <summary>The dlcpacks folder it goes into (mods\update\x64\dlcpacks\&lt;PackName&gt;).</summary>
    public string PackName { get; set; } = "addon";
    /// <summary>Where the pack name came from ("the mod's readme", "the pack's folder"…).</summary>
    public string? PackNameFrom { get; init; }
    /// <summary>The name the game mounts it under: a finished pack's own, else dlc_&lt;pack&gt;.</summary>
    public string Device => Finished?.Device is { Length: > 0 } d ? d : "dlc_" + PackName;

    /// <summary>The Replace version the mod ships too (loose models of a game vehicle), if any.</summary>
    public ReplacementPackage? Replace { get; init; }
    /// <summary>Install the Replace version instead of the add-on.</summary>
    public bool UseReplace { get; set; }

    /// <summary>Renumber modkits another pack uses (on by default).</summary>
    public bool FixKits { get; set; } = true;
    /// <summary>The last checks against a game (null: not checked yet).</summary>
    public AddonCheckReport? Checks { get; set; }
    /// <summary>ModDrop V's data folder (the game's own names and ids).</summary>
    public string? DataDir { get; init; }
}

/// <summary>
/// Add-on vehicles and peds: a finished pack is installed as it is (its models converted for Enhanced,
/// clashing modkit ids renumbered), a FiveM resource or loose models with metas are packed first
/// (<see cref="DlcComposer"/>); either way it becomes mods\update\x64\dlcpacks\&lt;pack&gt; plus a
/// dlclist.xml line, and is switched off / removed like any pack. Models of peds the game doesn't have,
/// with no peds.meta, get one written from a template (<see cref="PedMeta"/>) and are packed the same way.
/// Loose models of a game vehicle or ped with no metas are a replacement: they go through the mods layer
/// (<see cref="ReplacementHandler"/>) and are listed as a vehicle / ped.
/// </summary>
public sealed partial class AddonPackHandler(ModCategory kind) : IModHandler
{
    public const string VehiclePrefix = "vehicle:";
    public const string PedPrefix = "ped:";
    private const string StagedKey = "addon.staged";

    public ModCategory Category { get; } = kind;
    private string Prefix => Category == ModCategory.Ped ? PedPrefix : VehiclePrefix;
    private string Noun => Category == ModCategory.Ped ? "ped" : "vehicle";

    public bool Owns(string modId) => modId.StartsWith(Prefix, StringComparison.Ordinal);

    public string IdFor(string pack) => Prefix + pack.ToLowerInvariant();

    // ================================================================ analyse

    public ModPackage? Analyze(DroppedSource source, DetectionReport report, HandlerEnv env)
    {
        if (!report.Has(Category)) return null;
        var files = source.Files.Where(f => !f.InBackupDir).ToList();
        var name = NameOf(source);
        var src = source.Sources.Count == 1 ? ModSource.Of(source.Sources[0]) : null;

        // 1. a finished pack
        var packs = new List<(DroppedFile File, AddonContent.FinishedPack Pack)>();
        foreach (var f in files.Where(f => PathUtil.SuffixLower(f.Name) == ".rpf" && SourceIntake.IsDlcPack(f.FullPath))
                               .OrderBy(f => f.Depth).ThenBy(f => f.Origin, PathUtil.PathOrder))
        {
            try
            {
                var p = AddonContent.ReadPack(f.FullPath);
                if (p.Content.Kind == Category) packs.Add((f, p));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or RpfFormatException)
            {
                // not a pack we can read — another handler may know it
            }
        }
        if (packs.Count > 0)
        {
            var (file, pack) = packs[0];
            var (packName, from) = PackNameOf(source, file, pack);
            var pkg = new AddonPackage(Category)
            {
                Name = name, Source = src, Finished = pack, DataDir = env.DataDir, PackName = packName, PackNameFrom = from,
                Replace = ReplaceVersion(files, name, src, Path.GetDirectoryName(file.FullPath)!, env.DataDir),
            };
            pkg.Warnings.AddRange(pack.Warnings);
            foreach (var (other, _) in packs.Skip(1))
                pkg.Warnings.Add($"Several add-on packs found — installing «{file.Origin}», not «{other.Origin}». Drop the others separately.");
            Describe(pkg, $"finished pack {file.Origin}");
            return pkg;
        }

        // 2. a FiveM resource / loose models with their metas
        if (DlcComposer.FromDrop(source) is { } spec && spec.Content.Kind == Category)
        {
            var (packName, from) = PackNameOf(source, null, null);
            if (spec.Resources.Count == 1 && from is null) (packName, from) = (Clean(spec.Resources[0]) ?? packName, "the FiveM resource's folder");
            var pkg = new AddonPackage(Category)
            {
                Name = name, Source = src, Compose = spec, DataDir = env.DataDir, PackName = packName, PackNameFrom = from,
            };
            pkg.Warnings.AddRange(spec.Warnings);
            Describe(pkg, spec.Resources.Count > 0
                ? $"FiveM resource{(spec.Resources.Count > 1 ? "s" : "")} {string.Join(", ", spec.Resources)} — packed into a dlc.rpf"
                : "loose models and metas — packed into a dlc.rpf");
            return pkg;
        }

        if (report.Primary?.Category != Category) return null;
        var vanilla = VanillaModels.Load(env.DataDir);

        // 3. models of peds the game doesn't have, with no peds.meta: an add-on with one written for them
        if (Category == ModCategory.Ped && DlcComposer.FromPedModels(source, vanilla.IsPed) is { } peds)
        {
            var (packName, from) = PackNameOf(source, null, null);
            if (from is null && peds.NewPeds.Count == 1 && Clean(peds.NewPeds[0].Name) is { Length: >= 3 } own) (packName, from) = (own, "the ped's name");
            var pkg = new AddonPackage(Category)
            {
                Name = name, Source = src, Compose = peds, DataDir = env.DataDir, PackName = packName, PackNameFrom = from,
            };
            pkg.Warnings.AddRange(peds.Warnings);
            Describe(pkg, "models only, no peds.meta — one is written for it, all packed into a dlc.rpf");
            return pkg;
        }

        // 4. models of a game vehicle / ped with no metas: a replacement
        if (new ReplacementHandler().Analyze(source, report, env) is not ReplacementPackage rp) return null;
        rp.Kind = Category;
        var targets = ReplacedModels(rp, vanilla);
        rp.Replaces.AddRange(targets);
        rp.Parts.Insert(0, targets.Count > 0
            ? $"replaces the game's {Noun}{(targets.Count > 1 ? "s" : "")} {string.Join(", ", targets)}"
            : $"no vehicles.meta / peds.meta — its models replace the game's ones of the same name");
        return rp;
    }

    /// <summary>The game's vehicles / peds a replacement's models are named after.</summary>
    private List<string> ReplacedModels(ReplacementPackage rp, VanillaModels vanilla) =>
        [.. rp.Files.Select(f => Regex.Replace(Path.GetFileNameWithoutExtension(f.Name), @"(_hi|\+hi)$", "", RegexOptions.IgnoreCase))
                    .Where(n => Category == ModCategory.Ped ? vanilla.IsPed(n) : vanilla.IsVehicle(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];

    private void Describe(AddonPackage pkg, string what)
    {
        var c = pkg.Content;
        var things = c.Describe().ToList();
        pkg.Parts.Add(things.Count == 0
            ? what
            : $"{things.Count} {Noun}{(things.Count > 1 ? "s" : "")}: {string.Join(", ", things.Take(3))}{(things.Count > 3 ? ", …" : "")}");
        if (things.Count > 0) pkg.Parts.Add(what);
        if (pkg.Replace is { } r) pkg.Parts.Add($"a Replace version too ({r.Files.Count} file(s))");

        // what the game needs to spawn them: every declared model has its model file
        var streamed = new HashSet<string>(c.Streamed.Select(s => Path.GetFileNameWithoutExtension(s.Split('/')[^1])), StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(c.Streamed.Where(s => s.Contains('/')).Select(s => s.Split('/')[^2]), StringComparer.OrdinalIgnoreCase);
        foreach (var n in c.SpawnNames.Where(n => !streamed.Contains(n) && !folders.Contains(n)))
            pkg.Warnings.Add($"«{n}» is declared, but the mod has no model for it ({n}.yft) — the game can crash spawning it.");
    }

    /// <summary>The Replace version a mod ships next to its add-on pack: loose models outside the pack's folder.</summary>
    private ReplacementPackage? ReplaceVersion(List<DroppedFile> files, string name, ModSource? src, string packDir, string? dataDir)
    {
        var loose = files.Where(f => InputScanner.ResourceExt.Contains(PathUtil.SuffixLower(f.Name)) &&
                                     !Path.GetDirectoryName(f.FullPath)!.StartsWith(packDir, StringComparison.OrdinalIgnoreCase))
                         .ToList();
        if (loose.Count == 0 || !loose.Any(f => PathUtil.SuffixLower(f.Name) is ".yft" or ".ydd")) return null;
        var rp = ReplacementHandler.Build(ReplacementHandler.Pick(loose, out _), name + " (replace)", src);
        rp.Kind = Category;
        rp.Replaces.AddRange(ReplacedModels(rp, VanillaModels.Load(dataDir)));
        return rp;
    }

    private static string NameOf(DroppedSource source)
    {
        var n = SourceIntake.GuessName(source.Sources);
        return n == "Custom Weapon" ? "Add-on" : n;
    }

    [GeneratedRegex(@"dlcpacks:[\\/]+([A-Za-z0-9_\-]+)[\\/]", RegexOptions.IgnoreCase)] private static partial Regex ReadmePackRe();
    [GeneratedRegex(@"[^a-z0-9_]+")] private static partial Regex NonSlugRe();

    /// <summary>Folder names that say nothing about the pack ("Main Files", "Add-On", "dlc").</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "main files", "main", "files", "addon", "add-on", "add on", "dlc", "dlcpacks", "sp", "[sp]", "singleplayer", "single player",
        "install", "installation", "mod", "mods", "update", "x64", "legacy", "enhanced", "optional", "fivem", "[fivem]",
    };

    /// <summary>A dlcpacks folder name from a raw one: lower case latin, digits and _, at most 32; null when nothing is left.</summary>
    public static string? Clean(string raw)
    {
        var s = NonSlugRe().Replace(raw.ToLowerInvariant(), "");
        if (s.Length > 32) s = s[..32];
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// The dlcpacks folder: the one the mod's readme tells players to create (<c>dlcpacks:/innovabcm/</c>),
    /// else the finished pack's own folder, else its name in setup2.xml, else the mod's name.
    /// </summary>
    private static (string Name, string? From) PackNameOf(DroppedSource source, DroppedFile? packFile, AddonContent.FinishedPack? pack)
    {
        foreach (var f in source.Files.Where(f => PathUtil.SuffixLower(f.Name) == ".txt" && new FileInfo(f.FullPath).Length < 256 * 1024))
        {
            var text = TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.FullPath), strict: false);
            var names = ReadmePackRe().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 1 && Clean(names[0]) is { } n) return (n, "the mod's readme");
        }
        if (packFile is not null)
        {
            var dir = Path.GetFileName(Path.GetDirectoryName(packFile.Origin.Replace('/', Path.DirectorySeparatorChar)) ?? "");
            if (dir.Length > 0 && !Generic.Contains(dir) && Regex.IsMatch(dir, @"^[A-Za-z0-9_\-]+$") && Clean(dir) is { } d)
                return (d, "the pack's folder");
        }
        if (pack?.NameHash is { } nh && Clean(nh) is { } h) return (h, "the pack's setup2.xml");
        if (pack?.Device is { } dev && Clean(Regex.Replace(dev, "^dlc_", "", RegexOptions.IgnoreCase)) is { } dv) return (dv, "the pack's setup2.xml");
        return (Clean(NameOf(source).Replace(' ', '_')) is { Length: >= 3 } m ? m : "addon", null);
    }

    // ================================================================ install

    /// <summary>Check the package against a game (see <see cref="AddonChecks"/>); a taken folder name is changed to a free one.</summary>
    public static AddonCheckReport Check(AddonPackage pkg, InstallTarget target, GameIndex? index = null)
    {
        var report = AddonChecks.Run(pkg, target, index);
        if (report.FreePackName is { } free) pkg.PackName = free;
        pkg.Checks = report;
        return report;
    }

    public InstallPlan PlanInstall(ModPackage package, InstallTarget target)
    {
        if (package is ReplacementPackage rp) return new ReplacementHandler().PlanInstall(rp, target);
        var pkg = (AddonPackage)package;
        if (pkg.UseReplace && pkg.Replace is { } replace) return new ReplacementHandler().PlanInstall(replace, target);

        if (pkg.Checks is null || !pkg.Checks.GameDir.Equals(Path.GetFullPath(target.GameDir), StringComparison.OrdinalIgnoreCase) ||
            pkg.Checks.Edition != target.Edition)
            Check(pkg, target, TryIndex(target));
        var checks = pkg.Checks!;
        if (checks.Blocking is { } why) throw new InvalidOperationException(why);

        var pack = pkg.PackName;
        var id = IdFor(pack);
        var plan = new InstallPlan { Title = $"Installing «{pkg.Name}»" };
        var reg = ModRegistry.Load(target.GameDir);
        foreach (var oldId in checks.Supersedes)
            if (reg.Find(oldId) is { } old && old.Get("pack") is { } oldPack)
                plan.Add(new ActionOp($"Remove the installed «{old.Name}» (dlcpacks\\{oldPack}) — the same pack", ctx =>
                {
                    GameInstaller.UninstallPack(ctx.GameDir, oldPack, ctx.Log, ctx.Journal);
                    if (old.Get("overlay") == "1") ctx.Overlay.RemoveMod(oldId);
                    ctx.Unregistered.Add(oldId);
                }));
        if (reg.Find(id) is { } again)
            plan.Warnings.Add($"«{again.Name}» is already installed — the installed version is replaced.");

        var plugins = target.PluginsDir ?? Path.Combine(AppContext.BaseDirectory, "data", "plugins");
        plan.Add(new EnsureModsLoaderOp(plugins));
        bool raise = checks.MetaDataStoreNow is not null;
        if (raise)
            plan.Add(new RpfEditOp(GamePools.GameConfig, id,
                checks.MetaDataStoreNow < GamePools.MetaDataStoreTarget
                    ? $"Raise the game's MetaDataStore limit in gameconfig.xml from {checks.MetaDataStoreNow} to {GamePools.MetaDataStoreTarget} " +
                      "(the game's own .ymt files fill it; one more crashes the game on loading)"
                    : $"Keep the game's MetaDataStore limit in gameconfig.xml at {checks.MetaDataStoreNow} while this pack is installed",
                (data, log) => GamePools.Raise(data, GamePools.MetaDataStore, GamePools.MetaDataStoreTarget, log)));
        var fixes = pkg.FixKits ? checks.KitFixes : [];
        var convert = target.Edition == GameEdition.Enhanced && pkg.Content.ModelEditions.Contains(GameEdition.Legacy);
        if (pkg.Finished is { } finished && !convert && fixes.Count == 0)
            plan.Add(new InstallDlcPackOp(finished.Path, pack, Done(pkg)));
        else
        {
            plan.Add(new ActionOp(StageDescription(pkg, convert, fixes), ctx => Stage(ctx, pkg, fixes, convert)));
            plan.Add(new ActionOp($"Install the add-on pack '{pack}' (mods\\update\\x64\\dlcpacks\\{pack}) and add it to dlclist.xml", ctx =>
            {
                var staged = (string)ctx.Items[StagedKey];
                try
                {
                    GameInstaller.InstallToGame(ctx.GameDir, staged, pack, ctx.Log, ctx.Journal, Done(pkg));
                }
                finally
                {
                    PathUtil.TryDeleteDir(Path.GetDirectoryName(staged)!);
                }
            }));
        }
        // a raised limit belongs to the mod (the copy of gameconfig.xml in the mods layer): removing it puts it back
        bool overlay = raise || reg.Find(id)?.Get("overlay") == "1";
        plan.Add(new ActionOp("", ctx => ctx.Registered.Add(Record(pkg, id, target, overlay, raise))) { Hidden = true });

        foreach (var c in checks.Items.Where(c => c.Level is CheckLevel.Warn && !c.Title.StartsWith("Modkit", StringComparison.Ordinal)))
            plan.Warnings.Add(c.Detail);
        foreach (var f in checks.KitFixes.Where(_ => !pkg.FixKits))
            plan.Warnings.Add($"Modkit {f.Kit.Name} keeps id {f.Kit.Id}, which {f.TakenBy} uses too.");
        return plan;
    }

    /// <summary>"Add-On installed — spawn it with a trainer by name: innovabcm."</summary>
    private static string Done(AddonPackage pkg)
    {
        var names = pkg.Content.SpawnNames.ToList();
        return names.Count == 0 ? "Add-On installed."
            : $"Add-On installed — spawn it with a trainer by name: {string.Join(", ", names.Take(6))}{(names.Count > 6 ? ", …" : "")}.";
    }

    private static GameIndex? TryIndex(InstallTarget target)
    {
        try
        {
            return GameIndex.Open(target.GameDir, target.IndexCacheRoot);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or RpfFormatException)
        {
            return null;                                              // the checks go without the index
        }
    }

    private string StageDescription(AddonPackage pkg, bool convert, List<KitFix> fixes)
    {
        var parts = new List<string>();
        if (pkg.Compose is { NewPeds.Count: > 0 } made)
            parts.Add($"Write peds.meta for {string.Join(", ", made.NewPeds.Select(p => $"{p.Name} ({p.Gender.ToString().ToLowerInvariant()})"))} " +
                      "and pack it with the models into a dlc.rpf");
        else if (pkg.Compose is { } spec)
            parts.Add(spec.Resources.Count > 0
                ? $"Pack the FiveM resource{(spec.Resources.Count > 1 ? "s" : "")} {string.Join(", ", spec.Resources)} into a dlc.rpf"
                : "Pack the models and metas into a dlc.rpf");
        else parts.Add("Prepare a copy of the pack");
        if (convert) parts.Add("convert its Legacy models to the GTA V Enhanced (gen9) format");
        foreach (var f in fixes) parts.Add($"give modkit {f.Kit.Name} the free id {f.NewId}");
        return string.Join(", ", parts);
    }

    /// <summary>Build the pack to install into a temporary folder: composed, converted, modkits renumbered.</summary>
    private static void Stage(InstallContext ctx, AddonPackage pkg, List<KitFix> fixes, bool convert)
    {
        var tmp = PathUtil.MakeTempDir();
        try
        {
            string rpf;
            var edition = ctx.Target.Edition;
            if (pkg.Compose is { } spec)
            {
                var used = spec;
                if (fixes.Count > 0) used = WithKitFixes(spec, fixes, tmp, ctx.Log);
                rpf = DlcComposer.Compose(used, pkg.Device, Path.Combine(tmp, "pack"), edition, ctx.Log);
            }
            else
            {
                var finished = pkg.Finished!;
                rpf = Path.Combine(tmp, "pack", "dlc.rpf");
                Directory.CreateDirectory(Path.GetDirectoryName(rpf)!);
                if (convert)
                {
                    var n = RpfRetarget.Mismatched(finished.Path, edition).Count;
                    ctx.Log($"    Converting {n} Legacy model(s) to the GTA V Enhanced (gen9) format; everything else stays byte for byte…");
                    RpfRetarget.Convert(finished.Path, rpf, edition);
                }
                else PathUtil.Copy2(finished.Path, rpf);
                if (fixes.Count > 0) FixKitsInPack(rpf, fixes, edition, ctx.Log);
            }
            Pipeline.VerifyPack(rpf, ctx.Log);
            ctx.Items[StagedKey] = rpf;
        }
        catch
        {
            PathUtil.TryDeleteDir(tmp);
            throw;
        }
    }

    /// <summary>The composed spec with the carcols.meta files that hold clashing kits replaced by renumbered copies.</summary>
    private static ComposeSpec WithKitFixes(ComposeSpec spec, List<KitFix> fixes, string tmp, Action<string> log)
    {
        var copy = new ComposeSpec();
        copy.Data.AddRange(spec.Data);
        copy.Resources.AddRange(spec.Resources);
        copy.NewPeds.AddRange(spec.NewPeds);
        foreach (var (h, s) in spec.Content.Labels) copy.Content.Labels[h] = s;
        foreach (var f in spec.Files)
        {
            var mine = fixes.Where(x => PathUtil.SuffixLower(f.Source) is ".meta" or ".xml" or ".txt" &&
                                        spec.Data.Any(d => d.Type == "CARCOLS_FILE" && d.PackPath == f.PackPath) &&
                                        AddonContent.KitsIn(TextIo.ReadText(f.Source), "").Any(k => k.Id == x.Kit.Id && k.Name == x.Kit.Name))
                            .ToList();
            if (mine.Count == 0)
            {
                copy.Files.Add(f);
                continue;
            }
            var text = TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.Source), strict: false);
            foreach (var fix in mine)
            {
                text = AddonContent.WithKitId(text, fix.Kit, fix.NewId);
                log($"    Modkit {fix.Kit.Name}: id {fix.Kit.Id} → {fix.NewId} ({fix.TakenBy} uses {fix.Kit.Id}).");
            }
            var fixedFile = Path.Combine(tmp, "kits", Guid.NewGuid().ToString("N")[..8], Path.GetFileName(f.PackPath));
            Directory.CreateDirectory(Path.GetDirectoryName(fixedFile)!);
            File.WriteAllText(fixedFile, text, TextIo.Utf8NoBom);
            copy.Files.Add(f with { Source = fixedFile });
        }
        return copy;
    }

    /// <summary>Renumber clashing modkits inside a finished pack (its carcols.meta rewritten in place).</summary>
    private static void FixKitsInPack(string rpf, List<KitFix> fixes, GameEdition edition, Action<string> log)
    {
        foreach (var byFile in fixes.GroupBy(f => f.Kit.File, StringComparer.OrdinalIgnoreCase))
        {
            string text;
            using (var arc = RpfArchive.Open(rpf))
            {
                var e = arc.Locate(byFile.Key) ?? throw new InvalidDataException($"{byFile.Key} is not in the pack.");
                text = TextIo.DecodeUtf8Sig(arc.ReadContent(e), strict: false);
            }
            foreach (var fix in byFile)
            {
                text = AddonContent.WithKitId(text, fix.Kit, fix.NewId);
                log($"    Modkit {fix.Kit.Name}: id {fix.Kit.Id} → {fix.NewId} ({fix.TakenBy} uses {fix.Kit.Id}).");
            }
            using var ed = RpfEditor.Open(rpf);
            ed.Put(byFile.Key, StoredEntry.FromFile(Path.GetFileName(byFile.Key), TextIo.Utf8NoBom.GetBytes(text), edition));
            ed.Commit();
        }
    }

    private RegisteredMod Record(AddonPackage pkg, string id, InstallTarget target, bool overlay, bool pools)
    {
        var c = pkg.Content;
        var names = c.SpawnNames.ToList();
        var record = new RegisteredMod
        {
            Id = id, Category = Category, Name = pkg.Name, Edition = WeaponHandler.EditionKey(target.Edition),
            Installed = DateTime.UtcNow, Source = pkg.Source,
            Owns = [$"mods/update/x64/dlcpacks/{pkg.PackName}/"],
            Data = new()
            {
                ["kind"] = "addon", ["pack"] = pkg.PackName, ["device"] = pkg.Device,
                ["models"] = string.Join(",", names),
                ["where"] = names.Count == 0 ? $"dlcpacks\\{pkg.PackName}"
                    : $"spawn: {string.Join(", ", names.Take(4))}{(names.Count > 4 ? ", …" : "")} · dlcpacks\\{pkg.PackName}",
            },
        };
        if (pkg.Version is { Length: > 0 } v) record.Data["version"] = v;
        if (overlay) record.Data["overlay"] = "1";
        if (pools) record.Data["pools"] = "1";                 // holds a raised gameconfig.xml pool
        return record;
    }

    // ================================================================ installed

    public IEnumerable<InstalledMod> List(InstallTarget target, ModRegistry registry)
    {
        var game = target.GameDir;
        foreach (var m in registry.Mods.Where(m => Owns(m.Id)))
        {
            var folder = m.Get("pack") ?? m.Id[Prefix.Length..];
            bool on = File.Exists(Path.Combine(GameInstaller.PackDir(game, folder), "dlc.rpf"));
            if (!on && !File.Exists(Path.Combine(GameInstaller.DisabledPackDir(game, folder), "dlc.rpf"))) continue;   // deleted by hand
            yield return new InstalledMod(m.Id, m.Name.Length > 0 ? m.Name : folder, ModKind.Pack, m.Get("where") ?? folder, on)
            {
                Category = Category, Installed = m.Installed, Source = m.Source?.Name,
                Folder = on ? GameInstaller.PackDir(game, folder) : GameInstaller.DisabledPackDir(game, folder),
            };
        }
    }

    public InstallPlan PlanChanges(InstallTarget target, ModRegistry registry, IReadOnlyList<ModChange> changes)
    {
        var plan = new InstallPlan { Title = $"Updating installed {Category.PluralName().ToLowerInvariant()}" };
        var game = target.GameDir;
        foreach (var c in changes)
        {
            var m = registry.Find(c.Id) ?? throw new ArgumentException($"«{c.Id}» is not installed.");
            var folder = m.Get("pack") ?? c.Id[Prefix.Length..];
            var name = m.Name.Length > 0 ? m.Name : folder;
            if (c.Remove)
            {
                plan.Add(new ActionOp($"Remove «{name}» (dlcpack {folder})", ctx =>
                {
                    GameInstaller.UninstallPack(game, folder, ctx.Log, ctx.Journal);
                    ctx.Unregistered.Add(c.Id);
                }));
                if (m.Get("overlay") == "1") plan.Add(new OverlayRemoveOp(c.Id, name));
            }
            else if (c.Enable)
                plan.Add(new ActionOp($"Switch on «{name}» (dlcpack {folder})", ctx =>
                {
                    GameInstaller.EnablePack(game, folder, ctx.Log, ctx.Journal);
                    ctx.Switched[c.Id] = true;
                }));
            else
                plan.Add(new ActionOp($"Switch off «{name}» (dlcpack {folder})", ctx =>
                {
                    GameInstaller.DisablePack(game, folder, ctx.Log, ctx.Journal);
                    ctx.Switched[c.Id] = false;
                }));
        }
        return plan;
    }
}
