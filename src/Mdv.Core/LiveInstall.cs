using Mdv.Core.Mods;

namespace Mdv.Core;

/// <summary>
/// Early access: installing an add-on into the running game. The game holds mods\update\update.rpf for as long as it
/// runs, so the install copies the pack into dlcpacks and the loader (<see cref="HotLoad"/>) mounts it right away; what
/// has to go into update.rpf — the pack's dlclist.xml line, the game's raised limits — waits in
/// <see cref="PendingFile"/> until the game closes (<see cref="Finish"/>). Should the game start again before that, the
/// loader mounts the waiting packs itself.
/// </summary>
public static class LiveInstall
{
    /// <summary>In the game folder; the loader reads its <c>pack &lt;name&gt;</c> lines when the game starts.</summary>
    public const string PendingFile = "ModDropV.HotLoad.pending";

    internal const string LimitsKey = "live.limits";

    /// <summary>Does the game run (tests stand in for it)?</summary>
    internal static Func<bool> GameRuns = OnlineMode.GameRuns;

    /// <summary>Is the early-access loader next to ModDrop V (tests stand in for it)?</summary>
    internal static Func<bool> Unlocked = () => HotLoad.IsUnlocked(AppContext.BaseDirectory);

    /// <summary>
    /// Mods can go into the running game: the limits leave room for <see cref="GamePools.LiveReserveMods"/> more
    /// (<see cref="LiveBudget"/>). GTA V Legacy only, as the loader.
    /// </summary>
    public static bool Reserving(GameEdition edition) => edition == GameEdition.Legacy && Unlocked();

    public static string PendingPath(string gameDir) => Path.Combine(gameDir, PendingFile);

    public static bool HasPending(string gameDir) => File.Exists(PendingPath(gameDir));

    /// <summary>Installs go into the running game: the early-access loader is here, the game is Legacy and runs.</summary>
    public static bool Possible(string appDir, GameEdition edition) =>
        edition == GameEdition.Legacy && HotLoad.IsUnlocked(appDir) && GameRuns();

    /// <summary>What waits for the game to close.</summary>
    public sealed class Pending
    {
        public List<string> Packs { get; } = [];
        public LimitsProfile? Limits { get; set; }

        public static Pending Read(string gameDir)
        {
            var p = new Pending();
            if (!HasPending(gameDir)) return p;
            foreach (var raw in File.ReadAllLines(PendingPath(gameDir)))
            {
                var line = raw.Trim();
                if (line.StartsWith("pack ", StringComparison.Ordinal) && line[5..].Trim() is { Length: > 0 } pack
                    && !p.Packs.Contains(pack, StringComparer.OrdinalIgnoreCase))
                    p.Packs.Add(pack);
                else if (line.StartsWith("limits ", StringComparison.Ordinal) && Enum.TryParse<LimitsProfile>(line[7..].Trim(), out var l))
                    p.Limits = p.Limits is { } had && had > l ? had : l;
            }
            return p;
        }

        public void Write(string gameDir)
        {
            var path = PendingPath(gameDir);
            if (Packs.Count == 0 && Limits is null)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var lines = new List<string> { "# ModDrop V: installed while GTA V ran — written into update.rpf once it closes" };
            lines.AddRange(Packs.Select(p => "pack " + p));
            if (Limits is { } l) lines.Add("limits " + l.ToString());
            File.WriteAllLines(path, lines);
        }
    }

    /// <summary>
    /// Install an add-on package into the running game: its pack goes into dlcpacks and is loaded into the game; its
    /// dlclist.xml line and the game's limits wait for <see cref="Finish"/>.
    /// </summary>
    public static HotLoadOutcome Install(string appDir, ModPackage package, InstallTarget target, Action<string> log, PlanRun? run = null)
    {
        var plan = ModLibrary.PlanInstall(package, target);
        var ctx = InstallExecutor.Run(plan, target, log, run, live: true);
        var pending = Pending.Read(target.GameDir);
        var packs = ctx.Journal.DeferredPacks ?? [];
        foreach (var p in packs.Where(p => !pending.Packs.Contains(p, StringComparer.OrdinalIgnoreCase))) pending.Packs.Add(p);
        if (ctx.Items.TryGetValue(LimitsKey, out var profile) && profile is LimitsProfile l)
            pending.Limits = pending.Limits is { } had && had > l ? had : l;
        pending.Write(target.GameDir);
        var outcome = HotLoad.Load(appDir, target.GameDir, target.Edition, packs);
        log(outcome.Message);
        return outcome;
    }

    /// <summary>
    /// The game is closed: write what installs into the running game left waiting — the packs into dlclist.xml (each in
    /// its mod's record, so removing the mod takes it out again; packs of mods removed meanwhile are skipped) and the
    /// raised limits. Nothing to do while the game still runs.
    /// </summary>
    /// <returns>true when something was written</returns>
    public static bool Finish(InstallTarget target, Action<string> log)
    {
        var game = target.GameDir;
        if (!HasPending(game) || GameRuns()) return false;
        var pending = Pending.Read(game);
        var reg = ModRegistry.Load(game);
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in pending.Packs)
            if (OwnerOf(reg, pack) is { } id && File.Exists(Path.Combine(GameInstaller.PackDir(game, pack), "dlc.rpf")))
                owners[pack] = id;
        var plan = new InstallPlan { Title = L.T("Finishing the mods installed while the game ran") };
        var profile = pending.Limits ?? LimitsProfile.Standard;
        if (pending.Limits is not null && GamePools.LimitsOp(game, target.Edition, profile) is { } limits) plan.Add(limits);
        plan.Ops.AddRange(GamePools.AdjusterOps(target, profile));
        foreach (var pack in owners.Keys) plan.Add(new DlclistAddOp(pack));
        if (plan.Ops.Count > 0)
        {
            log(L.T($"Writing what the mods installed while the game ran left for later ({owners.Count} pack(s) into dlclist.xml)."));
            var ctx = InstallExecutor.Run(plan, target, log);
            reg = ModRegistry.Load(game);
            foreach (var added in ctx.Journal.Steps.OfType<DlclistAdded>())
                if (owners.TryGetValue(added.Pack, out var id) && reg.Find(id) is { } mod)
                    mod.Journal.Add(added);
            reg.Save(game);
        }
        File.Delete(PendingPath(game));
        return plan.Ops.Count > 0;
    }

    /// <summary>
    /// The game is closed and mods can go into it once it runs again: make the room for them now — the limits
    /// (gameconfig.xml, with <see cref="GamePools.LiveReserveMods"/> mods ahead), the limit plugins and their heap.
    /// Only for a game that has mods from ModDrop V (the limits go back to the game's own with the last one).
    /// </summary>
    /// <returns>true when something was written</returns>
    public static bool Prepare(InstallTarget target, Action<string> log)
    {
        var game = target.GameDir;
        if (!Reserving(target.Edition) || GameRuns() || OnlineMode.IsOn(game) || ModRegistry.Load(game).Mods.Count == 0) return false;
        var plan = new InstallPlan { Title = L.T("Making room for mods installed while the game runs") };
        // the gameconfig.xml limits where the game loads the mods folder already (not a copy of update.rpf for a script mod)
        bool modsFolder = File.Exists(ModsOverlay.StatePath(game)) || File.Exists(Path.Combine(ModsLayout.Root(game), "update", "update.rpf"));
        if (modsFolder && GamePools.LimitsOp(game, target.Edition, LimitsProfile.Standard) is { } limits)
            plan.Add(limits);
        plan.Ops.AddRange(GamePools.AdjusterOps(target, LimitsProfile.Standard));
        if (plan.Ops.Count == 0) return false;
        log(L.T($"Making room in the game for {GamePools.LiveReserveMods} more mods installed while it runs."));
        InstallExecutor.Run(plan, target, log);
        return true;
    }

    /// <summary>The registered mod whose install created the pack's folder.</summary>
    private static string? OwnerOf(ModRegistry reg, string pack)
    {
        var suffix = "/dlcpacks/" + pack;
        return reg.Mods.FirstOrDefault(m => string.Equals(m.Get("pack"), pack, StringComparison.OrdinalIgnoreCase)
                                            || m.Journal.OfType<CreatedDir>().Any(d => d.Path.Replace('\\', '/')
                                                   .EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))?.Id;
    }
}
