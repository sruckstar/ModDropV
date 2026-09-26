using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>Registry ids of installed weapons (the same ids the installed list always used).</summary>
public static class WeaponIds
{
    public const string PackPrefix = "pack:";
    public const string MergedPrefix = "merged:";

    public static string Pack(string folder) => PackPrefix + folder;
    public static string Merged(string suffix) => MergedPrefix + suffix;
}

/// <summary>An add-on weapon ready to build: the build settings (game-independent part).</summary>
public sealed class WeaponPackage : ModPackage
{
    public override ModCategory Category => ModCategory.Weapon;
    /// <summary>Build settings; the install target fills in the game, staging folder and edition.</summary>
    public required BuildOptions Options { get; set; }
    public IntakeResult? Intake { get; init; }
}

/// <summary>
/// Weapons through the common install model. The weapon mechanics are untouched: the build
/// is <see cref="Pipeline"/>, the shared pack <see cref="MergedPack"/>, the game side
/// <see cref="GameInstaller"/> — this handler only puts them into plan steps, so an install
/// or change is one transaction with a journal and a registry record.
/// </summary>
public sealed class WeaponHandler : IModHandler
{
    private const string BuildKey = "weapon.build";

    public ModCategory Category => ModCategory.Weapon;

    public bool Owns(string modId) =>
        modId.StartsWith(WeaponIds.PackPrefix, StringComparison.Ordinal) ||
        modId.StartsWith(WeaponIds.MergedPrefix, StringComparison.Ordinal);

    // ================================================================ analyse

    /// <summary>
    /// The weapon in a drop (<see cref="SourceIntake.PrepareWeapon"/>) with default settings:
    /// the name from the archive, the shared AddonWeapons pack. A drop the detector took for a
    /// weapon that has no usable models throws <see cref="IntakeException"/> saying so.
    /// </summary>
    public ModPackage? Analyze(DroppedSource source, DetectionReport report, HandlerEnv env)
    {
        if (!report.Has(ModCategory.Weapon)) return null;
        var r = SourceIntake.PrepareWeapon(source, env.TemplatesDir);
        var pkg = new WeaponPackage
        {
            Name = r.DisplayName, Intake = r,
            Options = new BuildOptions
            {
                InputFolder = r.InputFolder, OutDir = "", TemplatesDir = env.TemplatesDir, DataDir = env.DataDir,
                Name = r.DisplayName, MergePack = true,
                SourcePath = source.Sources.Count == 1 ? source.Sources[0] : null,
            },
        };
        if (r.PrebuiltRpf is { } pre) pkg.Parts.Add($"finished pack {pre.Origin}");
        else
        {
            pkg.Parts.Add($"{r.Models.Count} model / texture file(s)");
            if (r.Configs.Count > 0) pkg.Parts.Add($"own config: {string.Join(", ", r.Configs.Select(c => c.Name))}");
        }
        pkg.Warnings.AddRange(r.Warnings);
        return pkg;
    }

    // ================================================================ install

    /// <summary>The base weapon couldn't be determined — the build returned nothing to install.</summary>
    private sealed class NotBuiltException() : Exception("the base weapon could not be determined");

    /// <summary>
    /// <see cref="Pipeline.BuildAddon"/> with a game folder: resolve the edition, then build
    /// and install as one transaction. Null when the base weapon can't be determined (the
    /// game is left as it was).
    /// </summary>
    internal static BuildResult? InstallBuild(BuildOptions o, Action<string> log)
    {
        if (!o.PackRpf && !o.MergePack)
            throw new ArgumentException("Installing into GTA V requires a packed dlc.rpf — enable RPF packing.");
        var game = o.InstallGameDir!;
        var edition = GameInstaller.ResolveEdition(game, o.Edition, log);
        log(Pipeline.TargetLine(edition));
        var target = new InstallTarget(game, edition, o.OutDir, o.PluginsDir ?? Path.Combine(o.DataDir, "plugins"));
        var plan = new WeaponHandler().PlanInstall(new WeaponPackage { Name = o.Name, Options = o }, target);
        try
        {
            var ctx = InstallExecutor.Run(plan, target, log);
            return (BuildResult)ctx.Items[BuildKey];
        }
        catch (NotBuiltException)
        {
            return null;
        }
    }

    public InstallPlan PlanInstall(ModPackage package, InstallTarget target)
    {
        var pkg = (WeaponPackage)package;
        var o = pkg.Options with
        {
            InstallGameDir = target.GameDir, OutDir = target.StagingDir, Edition = target.Edition,
            PluginsDir = target.PluginsDir ?? pkg.Options.PluginsDir,
        };
        var plugins = o.PluginsDir ?? Path.Combine(o.DataDir, "plugins");
        var t = target with { PluginsDir = plugins };
        var name = pkg.Name;
        return new InstallPlan { Title = $"Installing «{name}»" }
            .Add(new EnsureModsLoaderOp(plugins))
            .Add(new ActionOp(o.MergePack
                                  ? $"Build «{name}» and add it to the shared AddonWeapons pack"
                                  : $"Build «{name}» as an add-on pack of its own",
                              ctx => Build(ctx, t, o)))
            .Add(new ActionOp("Install the built pack into mods\\update\\x64\\dlcpacks and add it to dlclist.xml",
                              ctx => InstallBuilt(ctx, pkg, o)));
    }

    private static void Build(InstallContext ctx, InstallTarget t, BuildOptions o)
    {
        WeaponStaging.Sync(t, ctx.Log);
        BuildResult? r;
        try
        {
            r = Pipeline.Build(o, t.Edition, ctx.Log);
        }
        catch
        {
            if (o.MergePack) GuardStaging(ctx, t.StagingDir, includeNext: true);     // it may have got half-way
            throw;
        }
        if (r is null) throw new NotBuiltException();                                  // nothing was staged
        // recorded before any install step, so a rollback restores the game's pack first
        if (r.WeaponSuffix is not null) GuardStaging(ctx, t.StagingDir, includeNext: false);
        ctx.Items[BuildKey] = r;
    }

    /// <summary>Let a rollback restore the staged shared packs (and drop one the build may start).</summary>
    private static void GuardStaging(InstallContext ctx, string stagingDir, bool includeNext)
    {
        var indices = new SortedSet<int> { 1 };
        if (Directory.Exists(stagingDir))
            foreach (var d in Directory.EnumerateDirectories(stagingDir))
                if (MergedPack.FolderIndex(Path.GetFileName(d)) is int i) indices.Add(i);
        if (includeNext) indices.Add(indices.Max + 1);
        foreach (var i in indices)
        {
            var folder = MergedPack.PackFolder(i);
            ctx.Journal.StagingChanged(Path.Combine(stagingDir, folder),
                                       Path.Combine(GameInstaller.PackDir(ctx.GameDir, folder), "dlc.rpf"));
        }
    }

    private static void InstallBuilt(InstallContext ctx, WeaponPackage pkg, BuildOptions o)
    {
        var r = (BuildResult)ctx.Items[BuildKey];
        foreach (var i in r.Installs)
        {
            if (i.Changed)
                GameInstaller.InstallToGame(ctx.GameDir, i.DlcRpf, i.Folder, ctx.Log, ctx.Journal);
            else
            {
                ctx.Log($"'{i.Folder}' unchanged — leaving the installed copy in place.");
                GameInstaller.RegisterInDlclist(ctx.GameDir, i.Folder, ctx.Log, ctx.Journal);
            }
        }
        r.InstalledTo = ctx.GameDir;

        var record = new RegisteredMod
        {
            Category = ModCategory.Weapon, Name = pkg.Name, Edition = EditionKey(ctx.Target.Edition),
            Installed = DateTime.UtcNow, Source = pkg.Source ?? ModSource.Of(o.SourcePath ?? o.InputFolder),
        };
        if (r.WeaponSuffix is { } suffix)
        {
            record.Id = WeaponIds.Merged(suffix);
            record.Data = new() { ["kind"] = "merged", ["pack"] = r.WeaponPack ?? MergedPack.Folder, ["suffix"] = suffix };
        }
        else
        {
            var folder = r.Installs[0].Folder;
            if (MergedPack.FolderIndex(folder) is not null) return;     // the shared pack keeps its own record
            record.Id = WeaponIds.Pack(folder);
            record.Owns = [$"mods/update/x64/dlcpacks/{folder}/"];
            record.Data = new() { ["kind"] = "pack", ["pack"] = folder };
        }
        ctx.Registered.Add(record);
    }

    public static string EditionKey(GameEdition e) => e == GameEdition.Enhanced ? "enhanced" : "legacy";

    // ================================================================ installed

    private static bool PackOn(string gameDir, string dlcName) =>
        File.Exists(Path.Combine(GameInstaller.PackDir(gameDir, dlcName), "dlc.rpf"));

    private static bool PackOff(string gameDir, string dlcName) =>
        File.Exists(Path.Combine(GameInstaller.DisabledPackDir(gameDir, dlcName), "dlc.rpf"));

    /// <summary>
    /// Standalone packs from the registry that are still on disk (on or off), and the weapons
    /// of each staged shared pack that is installed in this game. The staging is first
    /// synced with the game (<see cref="WeaponStaging.Sync"/>); the game is only read.
    /// </summary>
    public IEnumerable<InstalledMod> List(InstallTarget target, ModRegistry registry)
    {
        var game = target.GameDir;
        var list = new List<InstalledMod>();
        foreach (var m in registry.Mods.Where(m => m.Get("kind") == "pack"))
        {
            var folder = m.Get("pack") ?? m.Id[WeaponIds.PackPrefix.Length..];
            bool on = PackOn(game, folder);
            if (!on && !PackOff(game, folder)) continue;                // deleted by hand
            list.Add(new InstalledMod(m.Id, m.Name.Length > 0 ? m.Name : folder, ModKind.Pack, folder, on)
            {
                Installed = m.Installed, Source = m.Source?.Name, ImportedFrom = m.ImportedFrom,
                Folder = on ? GameInstaller.PackDir(game, folder) : GameInstaller.DisabledPackDir(game, folder),
            });
        }

        WeaponStaging.Sync(target, _ => { });
        if (Directory.Exists(target.StagingDir))
        {
            foreach (var pack in new MergedPackSet(target.StagingDir).AllPacks())
            {
                if (!PackOn(game, pack.FolderName)) continue;            // staged for another game folder
                foreach (var (suffix, w) in pack.Data.Weapons)
                {
                    var rec = registry.Find(WeaponIds.Merged(suffix));
                    list.Add(new InstalledMod(WeaponIds.Merged(suffix), w.Name.Length > 0 ? w.Name : suffix, ModKind.Merged,
                                              pack.FolderName, !w.Disabled)
                    {
                        Installed = rec?.Installed, Source = rec?.Source?.Name, ImportedFrom = rec?.ImportedFrom,
                        Folder = GameInstaller.PackDir(game, pack.FolderName),
                    });
                }
            }
        }
        return list;
    }

    // ================================================================ switch / remove

    public InstallPlan PlanChanges(InstallTarget target, ModRegistry registry, IReadOnlyList<ModChange> changes)
    {
        var plan = new InstallPlan { Title = "Updating installed weapons" };
        var game = target.GameDir;
        var merged = new List<ModChange>();
        foreach (var c in changes)
        {
            if (c.Id.StartsWith(WeaponIds.MergedPrefix, StringComparison.Ordinal))
            {
                merged.Add(c);
                continue;
            }
            var folder = c.Id[WeaponIds.PackPrefix.Length..];
            var name = registry.Find(c.Id)?.Name is { Length: > 0 } n ? n : folder;
            if (c.Remove)
                plan.Add(new ActionOp($"Remove «{name}» (dlcpack {folder})", ctx =>
                {
                    GameInstaller.UninstallPack(game, folder, ctx.Log, ctx.Journal);
                    ctx.Unregistered.Add(c.Id);
                }));
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

        if (merged.Count > 0)
        {
            int off = merged.Count(c => !c.Remove && !c.Enable), on = merged.Count(c => !c.Remove && c.Enable);
            int remove = merged.Count(c => c.Remove);
            var what = new[] { (off, "switch off"), (on, "switch on"), (remove, "remove") }
                       .Where(x => x.Item1 > 0).Select(x => $"{x.Item2} {x.Item1}");
            plan.Add(new ActionOp($"Shared AddonWeapons pack: {string.Join(", ", what)} weapon(s), then rebuild and reinstall it",
                                  ctx => ApplyMerged(ctx, merged)));
        }
        return plan;
    }

    private static void ApplyMerged(InstallContext ctx, List<ModChange> changes)
    {
        var (game, log) = (ctx.GameDir, ctx.Log);
        var ms = new MergedPackSet(ctx.Target.StagingDir, log);
        ms.SyncWithGame(game);
        GuardStaging(ctx, ctx.Target.StagingDir, includeNext: false);
        var emptied = new HashSet<MergedPack>();
        foreach (var c in changes)
        {
            var suffix = c.Id[WeaponIds.MergedPrefix.Length..];
            var pack = ms.OwnerOf(suffix);
            if (pack is null)
            {
                log($"    [!] Weapon '{suffix}' is no longer in any AddonWeapons pack — skipped.");
                continue;
            }
            if (c.Remove)
            {
                pack.RemoveWeapon(suffix);
                if (pack.IsEmpty) emptied.Add(pack);
                ctx.Unregistered.Add(c.Id);
            }
            else
            {
                pack.SetEnabled(suffix, c.Enable);
                ctx.Switched[c.Id] = c.Enable;
            }
        }

        // a pack left with no weapons at all goes from the game and from staging
        foreach (var pack in emptied)
        {
            GameInstaller.UninstallPack(game, pack.FolderName, log, ctx.Journal);
            PathUtil.TryDeleteDir(pack.Root);
        }

        foreach (var b in ms.BuildAll(ctx.Target.Edition))
        {
            log($"Packed '{b.Folder}' into a single dlc.rpf ({b.Size} bytes, {b.Weapons.Count} weapon(s)).");
            Pipeline.VerifyPack(b.DlcRpf, log);
            GameInstaller.InstallToGame(game, b.DlcRpf, b.Folder, log, ctx.Journal);
        }
    }
}

/// <summary>
/// Keeps the staged shared packs in step with what the game has. A shared pack installed in
/// the game but missing from our staging (a fresh ModDrop V next to AddonWeapons Builder, a
/// cleared app-data folder) is picked up — so the next weapon extends the installed pack
/// instead of replacing it with a new one:
/// <list type="number">
///   <item>from another tool's staging (AddonWeapons Builder) when it holds exactly the
///   installed pack's weapons — a copy, the other tool's folder is not touched;</item>
///   <item>else from the installed dlc.rpf itself: it is copied into staging and
///   <see cref="MergedPack"/> recovers the pack from the manifest embedded in it.</item>
/// </list>
/// </summary>
public static class WeaponStaging
{
    /// <summary>AddonWeapons Builder's staging for an edition (%LOCALAPPDATA%\AddonWeaponsBuilder\staging[-enhanced]).</summary>
    public static IReadOnlyList<string> AwbStagingDirs(GameEdition edition)
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir)) return [];
        return [Path.Combine(baseDir, "AddonWeaponsBuilder", edition == GameEdition.Enhanced ? "staging-enhanced" : "staging")];
    }

    public static void Sync(InstallTarget target, Action<string> log)
    {
        var dlcpacks = GameInstaller.DlcpacksDir(target.GameDir);
        if (!Directory.Exists(dlcpacks)) return;
        foreach (var dir in Directory.EnumerateDirectories(dlcpacks))
        {
            var folder = Path.GetFileName(dir);
            var installed = Path.Combine(dir, "dlc.rpf");
            if (MergedPack.FolderIndex(folder) is null || !File.Exists(installed)) continue;
            var staged = Path.Combine(target.StagingDir, folder);
            if (File.Exists(Path.Combine(staged, "_src", "_pack.json")) || File.Exists(Path.Combine(staged, "dlc.rpf"))) continue;
            var state = EmbeddedState(installed);
            if (state is null) continue;                                 // not a pack we built

            var twin = (target.ImportStagingDirs ?? []).Select(d => Path.Combine(d, folder))
                                                       .FirstOrDefault(d => SameWeapons(StagedState(d), state));
            if (twin is not null)
            {
                CopyDir(twin, staged);
                log($"Picked up the '{folder}' pack ({state.Weapons.Count} weapon(s)) from {ModRegistry.AwbName}'s staging: {twin}");
            }
            else
            {
                Directory.CreateDirectory(staged);
                PathUtil.Copy2(installed, Path.Combine(staged, "dlc.rpf"));
                log($"No staged copy of the installed '{folder}' pack — it will be recovered from the game ({state.Weapons.Count} weapon(s)).");
            }
        }
    }

    /// <summary>The pack manifest embedded in a built shared pack, or null.</summary>
    internal static PackState? EmbeddedState(string dlcRpf)
    {
        try
        {
            using var arc = RpfArchive.Open(dlcRpf);
            var entry = arc.Tree().FirstOrDefault(e => !e.IsDir && e.Path == "_pack.json");
            return entry is null ? null : TextIo.FromJson<PackState>(TextIo.DecodeUtf8Sig(arc.ReadContent(entry.Entry)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static PackState? StagedState(string packRoot)
    {
        var path = Path.Combine(packRoot, "_src", "_pack.json");
        try
        {
            return File.Exists(path) ? TextIo.FromJson<PackState>(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SameWeapons(PackState? a, PackState b) =>
        a is not null && a.Weapons.Count == b.Weapons.Count &&
        a.Weapons.All(kv => b.Weapons.TryGetValue(kv.Key, out var w) && w.Disabled == kv.Value.Disabled);

    private static void CopyDir(string src, string dst)
    {
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            PathUtil.Copy2(f, target);
        }
    }
}
