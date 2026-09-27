using Mdv.Core.Mods;

namespace Mdv.Core;

/// <summary>How an installed weapon reached the game.</summary>
public enum ModKind
{
    /// <summary>Its own dlcpack (mods/update/x64/dlcpacks/&lt;name&gt;).</summary>
    Pack,
    /// <summary>A weapon inside the shared AddonWeapons[N] pack.</summary>
    Merged,
}

/// <summary>One mod ModDrop V installed into a game, as the installed list shows it.</summary>
/// <param name="Id">registry id, the key for <see cref="ModChange"/> (weapons: <c>pack:&lt;folder&gt;</c> or <c>merged:&lt;suffix&gt;</c>)</param>
/// <param name="Pack">the dlcpack folder that holds it</param>
public sealed record InstalledMod(string Id, string Name, ModKind Kind, string Pack, bool Enabled)
{
    public ModCategory Category { get; init; } = ModCategory.Weapon;
    public DateTime? Installed { get; init; }
    /// <summary>The dropped file / folder it was installed from, when recorded.</summary>
    public string? Source { get; init; }
    /// <summary>Set when the record came from another tool (AddonWeapons Builder).</summary>
    public string? ImportedFrom { get; init; }
    /// <summary>The folder in the game that holds it (for "open folder"), when it has one.</summary>
    public string? Folder { get; init; }
    /// <summary>It can be switched off and on again (false: only removed).</summary>
    public bool CanSwitch { get; init; } = true;
}

/// <summary>A drop, analysed: what the detector saw, the installable packages, and per kind why it can't be installed.</summary>
public sealed record DropAnalysis(DetectionReport Report, List<ModPackage> Packages, Dictionary<ModCategory, string> Problems)
{
    public void Deconstruct(out DetectionReport report, out List<ModPackage> packages) =>
        (report, packages) = (Report, Packages);
}

/// <summary>What to do with one installed mod: switch it on/off, or remove it (wins over the switch).</summary>
public sealed record ModChange(string Id, bool Enable, bool Remove = false);

/// <summary>
/// Everything ModDrop V installed into a game, and switching it on/off or removing it. Each
/// mod type's handler (<see cref="IModHandler"/>) lists its own mods and plans its changes;
/// a batch of changes runs as one transaction (<see cref="InstallExecutor"/>).
/// </summary>
public static class ModLibrary
{
    public static IReadOnlyList<IModHandler> Handlers { get; } =
    [
        new WeaponHandler(), new OivHandler(), new ReplacementHandler(), new ScriptHandler(),
        new AddonPackHandler(ModCategory.Vehicle), new AddonPackHandler(ModCategory.Ped), new LiveryHandler(),
        new AddonPackHandler(ModCategory.Clothing), new AddonPackHandler(ModCategory.Map), new AddonPackHandler(ModCategory.Prop),
        new PlacementHandler(),
    ];

    public static IModHandler HandlerFor(ModCategory category) =>
        Handlers.FirstOrDefault(h => h.Category == category)
        ?? throw new NotSupportedException(L.T($"{category.DisplayName()} mods can't be installed yet."));

    /// <summary>
    /// What a drop holds: the detector's report, a package for every handler that found its
    /// kind, and why a handler whose kind was detected could not use the drop.
    /// </summary>
    public static DropAnalysis Analyze(DroppedSource source, HandlerEnv env)
    {
        var report = ModDetector.Detect(source);
        var result = new DropAnalysis(report, [], []);
        foreach (var h in Handlers)
        {
            try
            {
                if (h.Analyze(source, report, env) is { } pkg) result.Packages.Add(pkg);
            }
            catch (IntakeException ex)
            {
                result.Problems[h.Category] = ex.Message;
            }
        }
        // a vehicle / ped / clothing handler that took the drop (as an add-on or a replacement of the game's one) says
        // more than the plain file replacement of the same files
        if (result.Packages.Any(p => p.Category is ModCategory.Vehicle or ModCategory.Ped or ModCategory.Clothing or ModCategory.Map or ModCategory.Prop))
            result.Packages.RemoveAll(p => p.Category == ModCategory.Replacement);
        // an OIV package says what to do with every file in it — what other handlers see inside it is its content
        if (result.Packages.Any(p => p.Category == ModCategory.Package))
            result.Packages.RemoveAll(p => p.Category != ModCategory.Package);
        // the parts a map comes with (its scripts, the game files it changes) go in with it, not on their own
        var parts = result.Packages.OfType<AddonPackage>().SelectMany(a => a.Extras).Select(e => e.Category).ToHashSet();
        result.Packages.RemoveAll(p => parts.Contains(p.Category) && p is not AddonPackage);
        // the kind the detector is surest of first — it is the one picked for the player
        int Rank(ModPackage p)
        {
            int i = report.Found.FindIndex(d => d.Category == p.Category);
            return i < 0 ? int.MaxValue : i;
        }
        var ordered = result.Packages.OrderBy(Rank).ToList();
        result.Packages.Clear();
        result.Packages.AddRange(ordered);
        return result;
    }

    /// <summary>Everything installed into the target game (the game is only read).</summary>
    public static List<InstalledMod> List(InstallTarget target)
    {
        if (!Directory.Exists(target.ModsDir)) return [];
        var reg = ModRegistry.Load(target.GameDir);
        return Handlers.SelectMany(h => h.List(target, reg))
                       .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The steps that would apply these changes (for showing before applying).</summary>
    public static InstallPlan PlanChanges(InstallTarget target, IReadOnlyCollection<ModChange> changes)
    {
        if (changes.FirstOrDefault(c => !Handlers.Any(h => h.Owns(c.Id))) is { } unknown)
            throw new ArgumentException(L.T($"Unknown installed mod id: {unknown.Id}"));
        var reg = ModRegistry.Load(target.GameDir);
        var plan = new InstallPlan { Title = L.T("Applying changes to installed mods") };
        foreach (var h in Handlers)
        {
            var mine = changes.Where(c => h.Owns(c.Id)).ToList();
            if (mine.Count == 0) continue;
            var part = h.PlanChanges(target, reg, mine);
            plan.Ops.AddRange(part.Ops);
            plan.Warnings.AddRange(part.Warnings);
        }
        // the last mod out: the game's limits go back to its own
        var removed = changes.Where(c => c.Remove).Select(c => c.Id).ToHashSet();
        if (removed.Count > 0 && reg.Mods.All(m => removed.Contains(m.Id)) && File.Exists(ModsOverlay.StatePath(target.GameDir))
            && ModsOverlay.Load(target.GameDir).PathsOf(GamePools.LimitsOwner).Count > 0)
            plan.Add(new ActionOp(L.T("Put the game's own limits back in gameconfig.xml — no mods are left"),
                                  ctx => ctx.Overlay.RemoveMod(GamePools.LimitsOwner)));
        return plan;
    }

    /// <summary>Apply switches and removals — all of them, or (on a failure) none.</summary>
    public static void Apply(InstallTarget target, IReadOnlyCollection<ModChange> changes, Action<string> log, PlanRun? run = null)
    {
        if (changes.Count == 0) return;
        InstallExecutor.Run(PlanChanges(target, changes), target, log, run);
    }

    /// <summary>Install an analysed package through its handler's plan.</summary>
    public static InstallContext Install(ModPackage package, InstallTarget target, Action<string> log, PlanRun? run = null) =>
        InstallExecutor.Run(PlanInstall(package, target), target, log, run);

    /// <summary>The install plan of an analysed package: its handler's, with the game's limits raised for mods first.</summary>
    public static InstallPlan PlanInstall(ModPackage package, InstallTarget target) =>
        GamePools.WithLimits(HandlerFor(package.Category).PlanInstall(package, target), target);
}

/// <summary>
/// The installed-weapons view the app started with, kept as a shortcut over
/// <see cref="ModLibrary"/>: standalone packs are recorded in <c>mods/ModDropV.json</c>,
/// weapons of the shared AddonWeapons pack in the staged pack's manifest.
/// </summary>
public static class InstalledMods
{
    public const string RegistryFile = ModRegistry.FileName;

    public static string RegistryPath(string gameDir) => ModRegistry.PathFor(gameDir);

    /// <summary>Record a standalone pack installed outside a plan (replacing an earlier record of it).</summary>
    public static void RegisterPack(string gameDir, string dlcName, string displayName, GameEdition edition)
    {
        if (MergedPack.FolderIndex(dlcName) is not null) return;       // the shared pack keeps its own record
        var reg = ModRegistry.Load(gameDir);
        reg.Upsert(new RegisteredMod
        {
            Id = WeaponIds.Pack(dlcName), Category = ModCategory.Weapon, Name = displayName,
            Edition = WeaponHandler.EditionKey(edition), Installed = DateTime.UtcNow,
            Owns = [$"mods/update/x64/dlcpacks/{dlcName}/"],
            Data = new() { ["kind"] = "pack", ["pack"] = dlcName },
        });
        reg.Save(gameDir);
    }

    public static List<InstalledMod> List(string gameDir, string stagingDir) =>
        ModLibrary.List(new InstallTarget(gameDir, GameEdition.Legacy, stagingDir));

    public static void Apply(string gameDir, string stagingDir, GameEdition edition,
                             IReadOnlyCollection<ModChange> changes, Action<string> log) =>
        ModLibrary.Apply(new InstallTarget(gameDir, edition, stagingDir), changes, log);
}
