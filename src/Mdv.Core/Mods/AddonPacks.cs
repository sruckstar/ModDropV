using Mdv.Core;
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

    /// <summary>The Replace version the mod ships too (loose models of a game vehicle, clothes in place of a ped's own), if any.</summary>
    public ReplacementPackage? Replace { get; init; }
    /// <summary>
    /// Loose MP clothing models: packed as a collection of their own for this ped (mp_m_freemode_01 / mp_f_freemode_01);
    /// null for anything else.
    /// </summary>
    public NewCollection? LooseClothing => Compose?.NewCollections.FirstOrDefault(c => c.Parts.Count > 0);
    /// <summary>The Replace version is the only way in (loose clothes of a story character — they can't have add-on collections).</summary>
    public bool ReplaceOnly => LooseClothing is not null && Replace?.Wearer is { IsMp: false };
    /// <summary>Install the Replace version instead of the add-on.</summary>
    public bool UseReplace { get; set; }
    /// <summary>
    /// Clothing: how the add-on goes in — its MP clothes as new slots of the game's last collections (the game can't take
    /// a collection more); null: it has none.
    /// </summary>
    public ReplacementPackage? Slots { get; set; }

    /// <summary>Renumber modkits another pack uses (on by default).</summary>
    public bool FixKits { get; set; } = true;
    /// <summary>The last checks against a game (null: not checked yet).</summary>
    public AddonCheckReport? Checks { get; set; }

    // ---- maps
    /// <summary>The same map saved by Menyoo / Map Editor, when the mod ships that too (the other way in).</summary>
    public PlacementPackage? Placement { get; set; }
    /// <summary>Install the Menyoo / Map Editor map instead of the add-on pack.</summary>
    public bool UsePlacement { get; set; }
    /// <summary>
    /// Other parts of the mod its readme has players install by hand — game files it replaces, its scripts: each goes in
    /// with the pack as a mod of its own (so it can be switched off / removed on its own too).
    /// </summary>
    public List<ModPackage> Extras { get; } = [];
    /// <summary>Parts the player left out.</summary>
    public HashSet<ModPackage> SkippedExtras { get; } = [];
    /// <summary>Models the map places that neither it nor the game has (from the last checks).</summary>
    public List<string> MissingModels { get; } = [];
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
    public const string ClothingPrefix = "clothing:";
    public const string MapPrefix = "map:";
    public const string PropPrefix = "prop:";
    private const string StagedKey = "addon.staged";

    public ModCategory Category { get; } = kind;
    private string Prefix => Category switch
    {
        ModCategory.Ped => PedPrefix, ModCategory.Clothing => ClothingPrefix, ModCategory.Map => MapPrefix, ModCategory.Prop => PropPrefix,
        _ => VehiclePrefix,
    };
    private string Noun => Category switch
    {
        ModCategory.Ped => L.T("ped"), ModCategory.Clothing => L.T("clothing collection"), ModCategory.Map => L.T("placement file"), ModCategory.Prop => L.T("prop"),
        _ => L.T("vehicle"),
    };
    private string NounPlural => Category switch
    {
        ModCategory.Ped => L.T("peds"), ModCategory.Clothing => L.T("clothing collections"), ModCategory.Map => L.T("placement files"), ModCategory.Prop => L.T("props"),
        _ => L.T("vehicles"),
    };
    private bool IsMapKind => Category is ModCategory.Map or ModCategory.Prop;

    public bool Owns(string modId) => modId.StartsWith(Prefix, StringComparison.Ordinal);

    public string IdFor(string pack) => Prefix + pack.ToLowerInvariant();

    // ================================================================ analyse

    public ModPackage? Analyze(DroppedSource source, DetectionReport report, HandlerEnv env)
    {
        var result = AnalyzePack(source, report, env);
        if (result is AddonPackage { Kind: ModCategory.Clothing } clothes && SlotsVersion(clothes, source) is { } slots)
        {
            slots.Warnings.InsertRange(0, clothes.Warnings);
            clothes.Slots = slots;
        }
        return result;
    }

    private ModPackage? AnalyzePack(DroppedSource source, DetectionReport report, HandlerEnv env)
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
            // a mod with a version for each edition (Legacy\dlc.rpf + Enhanced\dlc.rpf): the one for this game
            GameEdition? variantFor = null;
            if (env.Edition is { } ed && packs.Count > 1 && packs.Any(p => EditionOf(p) != EditionOf(packs[0])))
            {
                packs = packs.OrderByDescending(p => EditionFit(p, ed)).ToList();
                variantFor = ed;
            }
            var (file, pack) = packs[0];
            var (packName, from) = PackNameOf(source, file, pack);
            var pkg = new AddonPackage(Category)
            {
                Name = name, Source = src, Finished = pack, DataDir = env.DataDir, PackName = packName, PackNameFrom = from,
                Replace = ReplaceVersion(files, name, src, Path.GetDirectoryName(file.FullPath)!, env.DataDir),
            };
            pkg.Warnings.AddRange(pack.Warnings);
            foreach (var other in packs.Skip(1).Where(p => variantFor is null || EditionOf(p) == EditionOf((file, pack))))
                pkg.Warnings.Add(L.T($"Several add-on packs found — installing «{file.Origin}», not «{other.File.Origin}». Drop the others separately."));
            Describe(pkg, L.T($"finished pack {file.Origin}"));
            if (variantFor is { } v) pkg.Parts.Add(L.T($"the version for {v.DisplayName()}"));
            if (IsMapKind) AttachMapParts(pkg, source, report, env, Path.GetDirectoryName(file.FullPath));
            return pkg;
        }

        // 2. a FiveM resource / loose models with their metas
        if (DlcComposer.FromDrop(source) is { } spec && spec.Content.Kind == Category)
        {
            var (packName, from) = PackNameOf(source, null, null);
            if (spec.Resources.Count == 1 && from is null) (packName, from) = (Clean(spec.Resources[0]) ?? packName, L.T("the FiveM resource's folder"));
            var pkg = new AddonPackage(Category)
            {
                Name = name, Source = src, Compose = spec, DataDir = env.DataDir, PackName = packName, PackNameFrom = from,
                Replace = Category == ModCategory.Clothing ? ClothingReplaceVersion(source, files, name, src, spec) : null,
            };
            pkg.Warnings.AddRange(spec.Warnings);
            Describe(pkg, spec.Resources.Count > 0
                ? L.T($"FiveM resource(s) {string.Join(", ", spec.Resources)} — packed into a dlc.rpf")
                : L.T("loose models and metas — packed into a dlc.rpf"));
            if (IsMapKind) AttachMapParts(pkg, source, report, env, null);
            return pkg;
        }

        if (IsMapKind) return LooseMap(source, report, env, name, src);
        if (report.Primary?.Category != Category) return null;
        if (Category == ModCategory.Clothing) return LooseClothing(source, files, name, src, env);
        var vanilla = VanillaModels.Load(env.DataDir);

        // 3. models of peds the game doesn't have, with no peds.meta: an add-on with one written for them
        if (Category == ModCategory.Ped && DlcComposer.FromPedModels(source, vanilla.IsPed) is { } peds)
        {
            var (packName, from) = PackNameOf(source, null, null);
            if (from is null && peds.NewPeds.Count == 1 && Clean(peds.NewPeds[0].Name) is { Length: >= 3 } own) (packName, from) = (own, L.T("the ped's name"));
            var pkg = new AddonPackage(Category)
            {
                Name = name, Source = src, Compose = peds, DataDir = env.DataDir, PackName = packName, PackNameFrom = from,
            };
            pkg.Warnings.AddRange(peds.Warnings);
            Describe(pkg, L.T("models only, no peds.meta — one is written for it, all packed into a dlc.rpf"));
            return pkg;
        }

        // 4. models of a game vehicle / ped with no metas: a replacement
        if (new ReplacementHandler().Analyze(source, report, env) is not ReplacementPackage rp) return null;
        rp.Kind = Category;
        var targets = ReplacedModels(rp, vanilla);
        rp.Replaces.AddRange(targets);
        rp.Parts.Insert(0, targets.Count > 0
            ? L.T($"replaces the game's {(targets.Count > 1 ? NounPlural : Noun)} {string.Join(", ", targets)}")
            : L.T($"no vehicles.meta / peds.meta — its models replace the game's ones of the same name"));
        return rp;
    }

    /// <summary>The game's vehicles / peds a replacement's models are named after.</summary>
    private List<string> ReplacedModels(ReplacementPackage rp, VanillaModels vanilla) =>
        [.. rp.Files.Select(f => Regex.Replace(Path.GetFileNameWithoutExtension(f.Name), @"(_hi|\+hi)$", "", RegexOptions.IgnoreCase))
                    .Where(n => Category == ModCategory.Ped ? vanilla.IsPed(n) : vanilla.IsVehicle(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The edition a finished pack is made for: its models' format, else a folder named after one
    /// (Enhanced, gen9 / Legacy, gen8); null when nothing tells.
    /// </summary>
    private static GameEdition? EditionOf((DroppedFile File, AddonContent.FinishedPack Pack) p)
    {
        if (p.Pack.Content.ModelsEdition is { } m) return m;
        var dirs = p.File.Origin.Replace('\\', '/').Split('/').SkipLast(1).Select(d => d.ToLowerInvariant()).ToList();
        if (dirs.Any(d => d.Contains("enhanced") || d.Contains("gen9"))) return GameEdition.Enhanced;
        if (dirs.Any(d => d.Contains("legacy") || d.Contains("gen8"))) return GameEdition.Legacy;
        return null;
    }

    /// <summary>How well a pack suits the game: its own edition best; Legacy models do for Enhanced (converted), not the other way.</summary>
    private static int EditionFit((DroppedFile File, AddonContent.FinishedPack Pack) p, GameEdition game) => EditionOf(p) switch
    {
        null => 1,
        var e when e == game => 2,
        GameEdition.Legacy => 0,
        _ => -1,
    };

    private void Describe(AddonPackage pkg, string what)
    {
        var c = pkg.Content;
        var things = c.Describe().ToList();
        pkg.Parts.Add(things.Count == 0
            ? what
            : L.T($"{(things.Count > 1 ? NounPlural : Noun)} ({things.Count}): {string.Join(", ", things.Take(3))}{(things.Count > 3 ? ", …" : "")}"));
        if (things.Count > 0) pkg.Parts.Add(what);
        if (pkg.Replace is { } r) pkg.Parts.Add(L.T($"a Replace version too ({r.Files.Count} file(s))"));

        // what the game needs to spawn them: every declared model has its model file
        var streamed = new HashSet<string>(c.Streamed.Select(s => Path.GetFileNameWithoutExtension(s.Split('/')[^1])), StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(c.Streamed.Where(s => s.Contains('/')).Select(s => s.Split('/')[^2]), StringComparer.OrdinalIgnoreCase);
        foreach (var n in c.SpawnNames.Where(n => !streamed.Contains(n) && !folders.Contains(n)))
            pkg.Warnings.Add(L.T($"«{n}» is declared, but the mod has no model for it ({n}.yft) — the game can crash spawning it."));
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
            if (names.Count == 1 && Clean(names[0]) is { } n) return (n, L.T("the mod's readme"));
        }
        if (packFile is not null)
        {
            var dir = Path.GetFileName(Path.GetDirectoryName(packFile.Origin.Replace('/', Path.DirectorySeparatorChar)) ?? "");
            if (dir.Length > 0 && !Generic.Contains(dir) && Regex.IsMatch(dir, @"^[A-Za-z0-9_\-]+$") && Clean(dir) is { } d)
                return (d, L.T("the pack's folder"));
        }
        if (pack?.NameHash is { } nh && Clean(nh) is { } h) return (h, L.T("the pack's setup2.xml"));
        if (pack?.Device is { } dev && Clean(Regex.Replace(dev, "^dlc_", "", RegexOptions.IgnoreCase)) is { } dv) return (dv, L.T("the pack's setup2.xml"));
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
        if (package is PlacementPackage pp) return new PlacementHandler().PlanInstall(pp, target);
        var pkg = (AddonPackage)package;
        if (pkg.UseReplace && pkg.Replace is { } replace) return new ReplacementHandler().PlanInstall(replace, target);
        var plan = pkg.Slots is { } slots ? new ReplacementHandler().PlanInstall(slots, target)
            : pkg.UsePlacement && pkg.Placement is { } placement
            ? new PlacementHandler().PlanInstall(placement, target)
            : PlanPack(pkg, target);
        AddExtras(plan, pkg, target);
        return plan;
    }

    private InstallPlan PlanPack(AddonPackage pkg, InstallTarget target)
    {
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
            {
                plan.Add(ForgetChanges(oldPack, reinstall: oldPack.Equals(pack, StringComparison.OrdinalIgnoreCase)));
                plan.Add(new ActionOp(L.T($"Remove the installed «{old.Name}» (dlcpacks\\{oldPack}) — the same pack"), ctx =>
                {
                    GameInstaller.UninstallPack(ctx.GameDir, oldPack, ctx.Log, ctx.Journal);
                    if (old.Get("overlay") == "1") ctx.Overlay.RemoveMod(oldId);
                    ctx.Unregistered.Add(oldId);
                }));
            }
        if (reg.Find(id) is { } again)
            plan.Warnings.Add(L.T($"«{again.Name}» is already installed — the installed version is replaced."));

        var plugins = target.PluginsDir ?? Path.Combine(AppContext.BaseDirectory, "data", "plugins");
        plan.Add(new EnsureModsLoaderOp(plugins));
        if (reg.Find(id) is not null && !checks.Supersedes.Contains(id)) plan.Add(ForgetChanges(pack, reinstall: true));
        var fixes = pkg.FixKits ? checks.KitFixes : [];
        var convert = target.Edition == GameEdition.Enhanced && pkg.Content.ModelEditions.Contains(GameEdition.Legacy);
        if (pkg.Finished is { } finished && !convert && fixes.Count == 0)
            plan.Add(new InstallDlcPackOp(finished.Path, pack, Done(pkg), finished.SubPacks));
        else
        {
            plan.Add(new ActionOp(StageDescription(pkg, convert, fixes), ctx => Stage(ctx, pkg, fixes, convert)));
            plan.Add(new ActionOp(L.T($"Install the add-on pack '{pack}' (mods\\update\\x64\\dlcpacks\\{pack}) and add it to dlclist.xml"), ctx =>
            {
                var staged = (string)ctx.Items[StagedKey];
                try
                {
                    GameInstaller.InstallToGame(ctx.GameDir, staged, pack, ctx.Log, ctx.Journal, Done(pkg),
                                                pkg.Finished?.SubPacks.Select(s => Path.Combine(Path.GetDirectoryName(staged)!, Path.GetFileName(s))).ToList());
                }
                finally
                {
                    PathUtil.TryDeleteDir(Path.GetDirectoryName(staged)!);
                }
            }));
        }
        bool overlay = reg.Find(id)?.Get("overlay") == "1";
        plan.Add(new ActionOp("", ctx => ctx.Registered.Add(Record(pkg, id, target, overlay))) { Hidden = true });

        foreach (var c in checks.Items.Where(c => c.Level is CheckLevel.Warn && !c.Title.StartsWith("Modkit", StringComparison.Ordinal)))
            plan.Warnings.Add(c.Detail);
        foreach (var f in checks.KitFixes.Where(_ => !pkg.FixKits))
            plan.Warnings.Add(L.T($"Modkit {f.Kit.Name} keeps id {f.Kit.Id}, which {f.TakenBy} uses too."));
        return plan;
    }

    /// <summary>
    /// Changes other mods made inside the pack (liveries) are forgotten when it is replaced or removed as a whole — their
    /// saved versions belong to the old pack.
    /// </summary>
    private static ActionOp ForgetChanges(string pack, bool reinstall) => new("", ctx =>
    {
        if (!File.Exists(ModsOverlay.StatePath(ctx.GameDir))) return;
        var mods = ctx.Overlay.ForgetArchive($"update/x64/dlcpacks/{pack}/dlc.rpf");
        if (mods.Count == 0) return;
        var reg = ModRegistry.Load(ctx.GameDir);
        var names = string.Join(", ", mods.Select(m => $"«{reg.Find(m)?.Name ?? m}»"));
        ctx.Log(reinstall
            ? L.T($"    [!] {names} changed this pack — the new version doesn't have those changes; install them again.")
            : L.T($"    [!] {names} changed this pack — they go with it."));
    }) { Hidden = true };

    /// <summary>"Add-On installed — spawn it with a trainer by name: innovabcm."</summary>
    private static string Done(AddonPackage pkg)
    {
        if (pkg.Kind is ModCategory.Map or ModCategory.Prop) return MapDone(pkg);
        if (pkg.Kind == ModCategory.Clothing)
        {
            var peds = CollectionsOf(pkg).Select(c => ClothingNames.PedLabel(c.Ped)).Distinct().ToList();
            return L.T($"Clothes installed — pick them for {(peds.Count == 0 ? L.T("the MP characters") : string.Join(" / ", peds))} in a trainer's wardrobe " +
                   $"(they come after the game's own).");
        }
        var names = pkg.Content.SpawnNames.ToList();
        return names.Count == 0 ? L.T("Add-On installed.")
            : L.T($"Add-On installed — spawn it with a trainer by name: {string.Join(", ", names.Take(6))}{(names.Count > 6 ? ", …" : "")}.");
    }

    /// <summary>Which archetypes files (name hashes) the game has, for checking a map's manifest; null without the index.</summary>
    private static Func<uint, bool>? GameTypes(InstallTarget target)
    {
        if (TryIndex(target) is not { } index) return null;
        var set = index.Find("*.ytyp", int.MaxValue).Where(h => h.Active)
                       .Select(h => MapMeta.Hash(Path.GetFileNameWithoutExtension(h.InnerPath.Split('/')[^1]))).ToHashSet();
        return set.Contains;
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
        if (pkg.LooseClothing is { } loose)
            parts.Add(L.T($"Pack the clothes as the collection {loose.Ped}_{loose.NameFor(pkg.Device)} (numbered from 0, ymt and shop meta written) into a dlc.rpf"));
        else if (pkg.Compose is { NewPeds.Count: > 0 } made)
            parts.Add(L.T($"Write peds.meta for {string.Join(", ", made.NewPeds.Select(p => $"{p.Name} ({p.Gender.ToString().ToLowerInvariant()})"))} " +
                      $"and pack it with the models into a dlc.rpf"));
        else if (pkg.Compose is { } map && pkg.Kind is ModCategory.Map or ModCategory.Prop)
            parts.Add(map.Resources.Count > 0
                ? (pkg.Kind == ModCategory.Map ? L.T($"Pack the FiveM resource(s) {string.Join(", ", map.Resources)} into a dlc.rpf as a map pack") : L.T($"Pack the FiveM resource(s) {string.Join(", ", map.Resources)} into a dlc.rpf as a props pack"))
                : pkg.Kind == ModCategory.Map
                    ? L.T($"Pack the map ({map.Files.Count} file(s): placements, archetypes, models) into a dlc.rpf") +
                      (map.Content.HasManifest ? "" : L.T(" with a manifest that loads its archetypes with its placements"))
                    : L.T($"Pack the props ({map.Files.Count} file(s)) into a dlc.rpf, their archetypes loaded for good"));
        else if (pkg.Compose is { } spec)
            parts.Add(spec.Resources.Count > 0
                ? L.T($"Pack the FiveM resource(s) {string.Join(", ", spec.Resources)} into a dlc.rpf")
                : L.T("Pack the models and metas into a dlc.rpf"));
        else parts.Add(L.T("Prepare a copy of the pack"));
        if (convert) parts.Add(L.T("convert its Legacy models to the GTA V Enhanced (gen9) format"));
        foreach (var f in fixes) parts.Add(L.T($"give modkit {f.Kit.Name} the free id {f.NewId}"));
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
                rpf = DlcComposer.Compose(used, pkg.Device, Path.Combine(tmp, "pack"), edition, ctx.Log,
                                          pkg.Kind == ModCategory.Map ? GameTypes(ctx.Target) : null);
            }
            else
            {
                var finished = pkg.Finished!;
                rpf = Path.Combine(tmp, "pack", "dlc.rpf");
                Directory.CreateDirectory(Path.GetDirectoryName(rpf)!);
                if (convert)
                {
                    var n = RpfRetarget.Mismatched(finished.Path, edition).Count;
                    ctx.Log(L.T($"    Converting {n} Legacy model(s) to the GTA V Enhanced (gen9) format; everything else stays byte for byte…"));
                    RpfRetarget.Convert(finished.Path, rpf, edition);
                }
                else PathUtil.Copy2(finished.Path, rpf);
                foreach (var sub in finished.SubPacks)
                {
                    var subDst = Path.Combine(Path.GetDirectoryName(rpf)!, Path.GetFileName(sub));
                    if (convert && RpfRetarget.Mismatched(sub, edition).Count > 0) RpfRetarget.Convert(sub, subDst, edition);
                    else PathUtil.Copy2(sub, subDst);
                }
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
                log(L.T($"    Modkit {fix.Kit.Name}: id {fix.Kit.Id} → {fix.NewId} ({fix.TakenBy} uses {fix.Kit.Id})."));
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
                var e = arc.Locate(byFile.Key) ?? throw new InvalidDataException(L.T($"{byFile.Key} is not in the pack."));
                text = TextIo.DecodeUtf8Sig(arc.ReadContent(e), strict: false);
            }
            foreach (var fix in byFile)
            {
                text = AddonContent.WithKitId(text, fix.Kit, fix.NewId);
                log(L.T($"    Modkit {fix.Kit.Name}: id {fix.Kit.Id} → {fix.NewId} ({fix.TakenBy} uses {fix.Kit.Id})."));
            }
            using var ed = RpfEditor.Open(rpf);
            ed.Put(byFile.Key, StoredEntry.FromFile(Path.GetFileName(byFile.Key), TextIo.Utf8NoBom.GetBytes(text), edition));
            ed.Commit();
        }
    }

    private RegisteredMod Record(AddonPackage pkg, string id, InstallTarget target, bool overlay)
    {
        var c = pkg.Content;
        var names = pkg.Kind switch
        {
            ModCategory.Clothing => CollectionsOf(pkg).Select(x => x.FullName).ToList(),
            ModCategory.Map => c.Ymaps.Select(y => Path.GetFileNameWithoutExtension(y.Split('/')[^1])).ToList(),
            ModCategory.Prop => c.Archetypes.ToList(),
            _ => c.SpawnNames.ToList(),
        };
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
                    : pkg.Kind == ModCategory.Map
                        ? $"{(c.Maps.Count > 0 ? L.T($"{c.Maps.Sum(m => m.Entities)} objects") : string.Join(", ", names.Take(3)))} · dlcpacks\\{pkg.PackName}"
                    : pkg.Kind == ModCategory.Prop
                        ? L.T($"props: {string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", …" : "")}") + $" · dlcpacks\\{pkg.PackName}"
                    : pkg.Kind == ModCategory.Clothing
                        ? $"{string.Join(", ", CollectionsOf(pkg).Take(3).Select(x => $"{ClothingNames.PedLabel(x.Ped)} · {x.DlcName}"))} · dlcpacks\\{pkg.PackName}"
                    : L.T($"spawn: {string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", …" : "")}") + $" · dlcpacks\\{pkg.PackName}",
            },
        };
        if (pkg.Version is { Length: > 0 } v) record.Data["version"] = v;
        if (overlay) record.Data["overlay"] = "1";
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
        var plan = new InstallPlan { Title = L.T($"Updating installed {Category.PluralName().ToLowerInvariant()}") };
        var game = target.GameDir;
        foreach (var c in changes)
        {
            var m = registry.Find(c.Id) ?? throw new ArgumentException(L.T($"«{c.Id}» is not installed."));
            var folder = m.Get("pack") ?? c.Id[Prefix.Length..];
            var name = m.Name.Length > 0 ? m.Name : folder;
            if (c.Remove)
            {
                plan.Add(ForgetChanges(folder, reinstall: false));
                plan.Add(new ActionOp(L.T($"Remove «{name}» (dlcpack {folder})"), ctx =>
                {
                    GameInstaller.UninstallPack(game, folder, ctx.Log, ctx.Journal);
                    ctx.Unregistered.Add(c.Id);
                }));
                if (m.Get("overlay") == "1") plan.Add(new OverlayRemoveOp(c.Id, name));
            }
            else if (c.Enable)
                plan.Add(new ActionOp(L.T($"Switch on «{name}» (dlcpack {folder})"), ctx =>
                {
                    GameInstaller.EnablePack(game, folder, ctx.Log, ctx.Journal);
                    ctx.Switched[c.Id] = true;
                }));
            else
                plan.Add(new ActionOp(L.T($"Switch off «{name}» (dlcpack {folder})"), ctx =>
                {
                    GameInstaller.DisablePack(game, folder, ctx.Log, ctx.Journal);
                    ctx.Switched[c.Id] = false;
                }));
        }
        return plan;
    }
}
