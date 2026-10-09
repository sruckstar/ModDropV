using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// The game's pool sizes in gameconfig.xml (<c>&lt;PoolName&gt;MetaDataStore&lt;/PoolName&gt;&lt;PoolSize value="3200"/&gt;</c>).
/// With every DLC up to 2026 the game's own files fill some of them to the last place — Rockstar's crash log
/// (<c>%LOCALAPPDATA%\Rockstar Games\GTAV\CrashLogs\crashcontext.log</c>, "ASSET STORES INFO") showed
/// MetaDataStore 3200 / 3200 on Legacy and 3199 / 3200 on Enhanced: one more .ymt (every add-on ped has one)
/// and the game crashes on loading. Such a pool is raised in the copy of gameconfig.xml under mods.
/// </summary>
public enum LimitsProfile
{
    /// <summary>For any mod ("For Less Mods").</summary>
    Standard,
    /// <summary>For a big mod — a total conversion, a graphics pack ("For More Mods").</summary>
    Large,
}

public static partial class GamePools
{
    public const string GameConfig = "update/update.rpf/common/data/gameconfig.xml";
    public const string MetaDataStore = "MetaDataStore";
    /// <summary>What MetaDataStore is raised to: half as much again as the game's 3200.</summary>
    public const int MetaDataStoreTarget = 4800;
    /// <summary>
    /// The owner of the raised limits in the mods layer: not a mod — set up by the first install that uses the mods
    /// folder, put back when the last installed mod is removed.
    /// </summary>
    public const string LimitsOwner = "moddropv:limits";
    /// <summary>A mod changing this many files inside game archives is a big one (a total conversion, a graphics pack)…</summary>
    public const int BigModFiles = 100;
    /// <summary>…or one bringing this much.</summary>
    public const long BigModBytes = 1L << 30;

    // The limits raised for mods, from the "Stock Traffic" configs of GTAV Config v37 (Legacy 3889): "For Less Mods" for
    // any mod, "For More Mods" for a big one. Only ever raised; MetaDataStore to at least MetaDataStoreTarget.
    private static readonly Dictionary<string, int> StandardPools = new()
    {
        ["AttachmentExtension"] = 460, ["AudioHeap"] = 250, ["CGameScriptResource"] = 3089, ["DrawableStore"] = 479600,
        ["DwdStore"] = 420500, ["FragmentStore"] = 1187500, ["TxdStore"] = 380000, ["HandlingData"] = 17500,
        ["fwArchetypePooledMap"] = 65521, ["CAIHandlingInfo"] = 18, ["CAICurvePoint"] = 180, [MetaDataStore] = MetaDataStoreTarget,
    };

    private static readonly Dictionary<string, int> StandardSettings = new()
    {
        ["MaxMloModelInfos"] = 12220, ["MaxPedModelInfos"] = 12800, ["MaxVehicleModelInfos"] = 15320, ["MaxWeaponModelInfos"] = 12115,
        ["MaxExtraPedModelInfos"] = 12430, ["MaxExtraVehicleModelInfos"] = 17630, ["MaxExtraWeaponModelInfos"] = 12610,
        ["ArchiveCount"] = 16736,
    };

    private static readonly Dictionary<string, int> LargePools = new(StandardPools)
    {
        ["AnimatedBuilding"] = 1200, ["BlendshapeStore"] = 125, ["Building"] = 65000, ["CGameScriptResource"] = 6168,
        ["CScenarioInfo"] = 750, ["CScenarioPointExtraData"] = 720, ["DecoratorExtension"] = 900, ["Dummy Object"] = 15050,
        ["InteriorInst"] = 300, ["InteriorProxy"] = 5850, ["MaxLoadedInfo"] = 39768, ["MaxLoadRequestedInfo"] = 13000,
        ["ActiveLoadedInfo"] = 13000, ["ActivePersistentLoadedInfo"] = 14390, ["LightEntity"] = 6750, ["MapDataLoadedNode"] = 6300,
        ["MapDataStore"] = 60120, ["MapTypesStore"] = 3400, [MetaDataStore] = 6400, ["NavMeshes"] = 13100, ["phInstGta"] = 15050,
        ["PortalInst"] = 400, ["PtFxSortedEntity"] = 768, ["PtFxAssetStore"] = 800, ["ScaleformStore"] = 850, ["StaticBounds"] = 44700,
        ["fwLodNode"] = 17500, ["CMoveObject"] = 600, ["CMoveAnimatedBuilding"] = 483, ["fwDynamicArchetypeComponent"] = 65521,
        ["fwDynamicEntityComponent"] = 32768, ["ScenarioCarGensPerRegion"] = 320, ["ScenarioPointsAndEdgesPerRegion"] = 2000,
        ["ScenarioPoint"] = 1000, ["ScenarioPointEntity"] = 700, ["ScenarioPointWorld"] = 2800,
        ["MaxNonRegionScenarioPointSpatialObjects"] = 1800,
    };

    private static readonly Dictionary<string, int> LargeSettings = new(StandardSettings)
    {
        ["MaxMloInstances"] = 36000, ["MaxMloModelInfos"] = 25000, ["MaxPedModelInfos"] = 18000, ["MaxVehicleModelInfos"] = 18320,
        ["MaxExtraPedModelInfos"] = 13430, ["MaxExtraVehicleModelInfos"] = 18630, ["ArchiveCount"] = 25104,
    };

    // GTA V Enhanced allocates its stores up front from a smaller heap: with the Legacy values above (DrawableStore 479600,
    // FragmentStore 1187500…) it fails on start-up (crashcontext.log: "CGameScriptResource, NULL", frame 0), Heap Adjuster
    // or not. Its own limits are raised by half — FragmentStore was 41726 / 42000 with a few add-ons — and MetaDataStore.
    private static readonly Dictionary<string, int> EnhancedPools = new()
    {
        ["DrawableStore"] = 121500, ["DwdStore"] = 30750, ["FragmentStore"] = 63000, ["TxdStore"] = 120300,
        [MetaDataStore] = MetaDataStoreTarget,
        // interiors (MLOs) of add-on maps: the game sizes these for its own (InteriorProxy 1250, InteriorInst 150, PortalInst
        // 200) and fails loading with a null write when a map adds one (moreo_cafecutev2) — the Legacy "More Mods" values
        ["InteriorProxy"] = 2500, ["InteriorInst"] = 300, ["PortalInst"] = 400,
    };

    // MaxMloModelInfos 220 in the game's own file (Legacy's v37 values give 12220): raised enough for add-on interiors
    private static readonly Dictionary<string, int> EnhancedSettings = new() { ["MaxMloModelInfos"] = 1220 };

    /// <summary>
    /// How many mods ahead the limits leave room for when mods can go into the running game (<see cref="LiveInstall"/>):
    /// a pack loaded there takes what is free since the game started — nothing is raised until it starts again.
    /// </summary>
    public const int LiveReserveMods = 30;

    /// <summary>
    /// What one add-on of usual size takes (a car, a ped, a weapon, a small map): its archives, its model and texture
    /// files, the add-on vehicles, peds and weapons it declares. <see cref="LiveBudget"/> weighs a pack by these.
    /// </summary>
    internal static readonly Dictionary<string, int> PerModPools = new()
    {
        ["DrawableStore"] = 60, ["DwdStore"] = 40, ["FragmentStore"] = 20, ["TxdStore"] = 60, ["HandlingData"] = 4, [MetaDataStore] = 4,
    };

    internal static readonly Dictionary<string, int> PerModSettings = new()
    {
        ["MaxExtraVehicleModelInfos"] = 4, ["MaxExtraPedModelInfos"] = 4, ["MaxExtraWeaponModelInfos"] = 4, ["ArchiveCount"] = 6,
    };

    /// <summary>The heap one usual add-on can take while it loads (MB) — its declarations, the metas parsed.</summary>
    public const int PerModHeapMb = 8;

    /// <summary>The heap Heap Adjuster should give (MB): <see cref="LimitAdjusters.HeapMb"/>, and the live reserve on top.</summary>
    public static int HeapNeed(bool reserve) => LimitAdjusters.HeapMb + (reserve ? LiveReserveMods * PerModHeapMb : 0);

    /// <summary>The ArchiveCount the limits give (0: they don't set it — Enhanced keeps the game's).</summary>
    public static int ArchivesNeed(GameEdition edition, LimitsProfile profile, bool reserve) =>
        Table(edition, profile, reserve).Settings.GetValueOrDefault("ArchiveCount");

    private static (Dictionary<string, int> Pools, Dictionary<string, int> Settings) Table(GameEdition edition, LimitsProfile profile,
                                                                                         bool reserve = false)
    {
        var (pools, settings) = edition == GameEdition.Enhanced ? (EnhancedPools, EnhancedSettings)
            : profile == LimitsProfile.Large ? (LargePools, LargeSettings) : (StandardPools, StandardSettings);
        // the live reserve: GTA V Legacy only, as installs into the running game
        if (!reserve || edition == GameEdition.Enhanced) return (pools, settings);
        return (pools.ToDictionary(kv => kv.Key, kv => kv.Value + LiveReserveMods * PerModPools.GetValueOrDefault(kv.Key)),
                settings.ToDictionary(kv => kv.Key, kv => kv.Value + LiveReserveMods * PerModSettings.GetValueOrDefault(kv.Key)));
    }

    /// <summary>Does this gameconfig.xml hold the live reserve on top of GTA V Legacy's limits for mods?</summary>
    public static bool HasLiveReserve(string xml)
    {
        var (pools, settings) = Table(GameEdition.Legacy, LimitsProfile.Standard, reserve: true);
        return PerModPools.Keys.All(k => Size(xml, k) is { } n && n >= pools[k])
               && PerModSettings.Keys.All(k => Setting(xml, k) is not { } n || n >= settings[k]);   // plain limits it lacks stay the game's
    }

    private static Regex SettingRe(string name) => new($@"(<{Regex.Escape(name)}\s+value\s*=\s*"")(\d+)("")");

    /// <summary>A plain limit of gameconfig.xml (<c>&lt;ArchiveCount value="4440"/&gt;</c>), its first occurrence; null when absent.</summary>
    public static int? Setting(string xml, string name) =>
        SettingRe(name).Match(xml) is { Success: true } m && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : null;

    /// <summary>
    /// What the limits for <paramref name="edition"/> and <paramref name="profile"/> raise in this gameconfig.xml: the name,
    /// its size now (null: a pool the file doesn't list yet) and the new size. Plain limits the file doesn't have are left alone.
    /// With <paramref name="reserve"/> GTA V Legacy's are raised <see cref="LiveReserveMods"/> mods further.
    /// </summary>
    public static List<(string Name, int? Now, int To)> Missing(string xml, GameEdition edition, LimitsProfile profile, bool reserve = false)
    {
        var (pools, settings) = Table(edition, profile, reserve);
        var list = new List<(string, int?, int)>();
        foreach (var (name, to) in pools)
            if (Size(xml, name) is not { } now || now < to) list.Add((name, Size(xml, name), to));
        foreach (var (name, to) in settings)
            if (Setting(xml, name) is { } now && now < to) list.Add((name, now, to));
        return list;
    }

    /// <summary>
    /// Limits in this gameconfig.xml above what <paramref name="edition"/> gets: its own ones raised further, or the Legacy
    /// values (GTAV Config v37) an earlier ModDrop V build put into GTA V Enhanced. A value the game's own file
    /// (<paramref name="own"/>) has is never too high: Enhanced ships four of the v37 ones (AttachmentExtension 460,
    /// AudioHeap 250, CGameScriptResource 3089, fwArchetypePooledMap 65521). The live reserve is never too high either.
    /// </summary>
    public static List<string> Over(string xml, GameEdition edition, LimitsProfile profile, string? own = null)
    {
        var (pools, settings) = Table(edition, profile, reserve: true);
        var list = new List<string>();
        foreach (var name in StandardPools.Keys.Union(LargePools.Keys))
            if (Size(xml, name) is { } now && (own is null || Size(own, name) != now)
                && (pools.TryGetValue(name, out var to) ? now > to
                    : now == StandardPools.GetValueOrDefault(name) || now == LargePools.GetValueOrDefault(name)))
                list.Add(name);
        foreach (var name in StandardSettings.Keys.Union(LargeSettings.Keys))
            if (Setting(xml, name) is { } now && (own is null || Setting(own, name) != now)
                && (settings.TryGetValue(name, out var to) ? now > to
                    : now == StandardSettings.GetValueOrDefault(name) || now == LargeSettings.GetValueOrDefault(name)))
                list.Add(name);
        return list;
    }

    /// <summary>
    /// The edit for the limits step: every limit for <paramref name="edition"/> and <paramref name="profile"/> raised, pools
    /// the file doesn't list added to its pool list, the rest of the file byte for byte. Null when there is no file.
    /// </summary>
    public static byte[]? RaiseLimits(byte[]? data, GameEdition edition, LimitsProfile profile, Action<string> log, bool reserve = false)
    {
        if (data is null) return null;
        var xml = TextIo.DecodeUtf8Sig(data, strict: false);
        var missing = Missing(xml, edition, profile, reserve);
        if (missing.Count == 0) return data;
        foreach (var (name, now, to) in missing)
            xml = now is null ? AddPool(xml, name, to)
                : Size(xml, name) is not null ? WithSize(xml, name, to)
                : SettingRe(name).Replace(xml, m => m.Groups[1].Value + to.ToString(CultureInfo.InvariantCulture) + m.Groups[3].Value, 1);
        log(L.T($"    gameconfig.xml: limits raised — {string.Join(", ", missing.Select(m => $"{m.Name} {m.Now?.ToString(CultureInfo.InvariantCulture) ?? "(new)"} → {m.To}"))}."));
        return new UTF8Encoding(false).GetBytes(xml);
    }

    /// <summary>A pool the file doesn't list, added at the end of its pool list (as GTAV Config does).</summary>
    private static string AddPool(string xml, string name, int size)
    {
        int list = xml.IndexOf("<PoolSizes>", StringComparison.Ordinal);
        int end = list < 0 ? -1 : xml.IndexOf("</Entries>", list, StringComparison.Ordinal);
        if (end < 0) return xml;
        int line = xml.LastIndexOf('\n', end) + 1;
        var indent = xml[line..end];
        if (indent.Trim().Length > 0) indent = "";
        var n = size.ToString(CultureInfo.InvariantCulture);
        return xml.Insert(line, $"{indent}    <Item>\n{indent}        <PoolName>{name}</PoolName>\n{indent}        <PoolSize value=\"{n}\"/>\n{indent}    </Item>\n");
    }

    /// <summary>
    /// The step that raises the game's limits for mods, put before an install that uses the mods folder — null when they
    /// are that high already or there is no gameconfig.xml to raise them in. Limits ModDrop V raised higher than
    /// <paramref name="edition"/> takes are taken back and raised again to its own. With mods going into the running
    /// game (<see cref="LiveInstall.Reserving"/>, unless <paramref name="reserve"/> says) GTA V Legacy's are raised
    /// <see cref="LiveReserveMods"/> mods further.
    /// </summary>
    public static PlanOp? LimitsOp(string gameDir, GameEdition edition, LimitsProfile profile, bool? reserve = null)
    {
        bool extra = reserve ?? LiveInstall.Reserving(edition);
        byte[]? data, own = null;
        bool mineOnTop;
        try
        {
            var overlay = ModsOverlay.Load(gameDir);
            data = overlay.Read(GameConfig);
            mineOnTop = overlay.OwnersOf(GameConfig).FirstOrDefault() == LimitsOwner;
            if (mineOnTop) own = overlay.ReadOriginal(GameConfig);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or RpfFormatException or ArgumentException)
        {
            return null;
        }
        if (data is null) return null;
        var xml = TextIo.DecodeUtf8Sig(data, strict: false);
        var ownXml = own is null ? null : TextIo.DecodeUtf8Sig(own, strict: false);
        if (mineOnTop && Over(xml, edition, profile, ownXml) is { Count: > 0 } over)
            return new ActionOp(L.T($"Set the game's limits in gameconfig.xml again — {over.Count} of the ones raised before are too high for {edition.DisplayName()}"), ctx =>
            {
                ctx.Overlay.RemoveMod(LimitsOwner, keepCopies: true);
                var below = ctx.Overlay.Read(GameConfig);
                if (RaiseLimits(below, edition, profile, ctx.Log, extra) is { } raised && !ReferenceEquals(raised, below))
                    ctx.Overlay.Put(LimitsOwner, GameConfig, raised);
            });
        var missing = Missing(xml, edition, profile, extra);
        if (missing.Count == 0) return null;
        return new RpfEditOp(GameConfig, LimitsOwner,
            edition == GameEdition.Enhanced
                ? L.T($"Raise the game's limits in gameconfig.xml for mods ({missing.Count} values: model and texture stores, MetaDataStore, interiors)")
                : extra
                    ? L.T($"Raise the game's limits in gameconfig.xml for mods and {LiveReserveMods} more installed while the game runs ({missing.Count} values: model and texture stores, add-on vehicles, peds and weapons, archives)")
                : profile == LimitsProfile.Large
                    ? L.T($"Raise the game's limits in gameconfig.xml for a big mod ({missing.Count} values: model and texture stores, add-on vehicles, peds and weapons, archives, map and world pools)")
                    : L.T($"Raise the game's limits in gameconfig.xml for mods ({missing.Count} values: model and texture stores, add-on vehicles, peds and weapons, archives)"),
            (d, log) => RaiseLimits(d, edition, profile, log, extra));
    }

    /// <summary>
    /// The limit plugins of the edition and their settings (<see cref="LimitAdjusters"/>) — for any mod, whether it uses
    /// the mods folder or not. Empty when the game has them set high enough.
    /// </summary>
    public static List<PlanOp> AdjusterOps(InstallTarget target, LimitsProfile profile, bool? reserve = null)
    {
        bool extra = reserve ?? LiveInstall.Reserving(target.Edition);
        var ops = new List<PlanOp>();
        if (LimitAdjusters.Op(target.GameDir, target.Edition, PluginsDir(target)) is { } adjusters) ops.Add(adjusters);
        if (LimitAdjusters.SettingsOp(target.GameDir, target.Edition, PluginsDir(target), HeapNeed(extra),
                                      ArchivesNeed(target.Edition, profile, extra)) is { } settings) ops.Add(settings);
        return ops;
    }

    /// <summary>
    /// The limits steps put into an install plan: the limit plugins first (any mod gets them), and for a plan that makes
    /// the game load the mods folder the gameconfig.xml limits right after that step. A big mod gets the higher limits. A
    /// mod's own gameconfig.xml is merged into the game's (<see cref="MergeLimits"/>) instead of replacing it.
    /// </summary>
    /// <param name="reserve">leave room for mods installed while the game runs (null: when <see cref="LiveInstall.Reserving"/>)</param>
    public static InstallPlan WithLimits(InstallPlan plan, InstallTarget target, bool? reserve = null)
    {
        int at = plan.Ops.FindIndex(o => o is EnsureModsLoaderOp);
        var edition = target.Edition;
        var profile = ProfileFor(plan, target);
        var ops = new List<PlanOp>();
        if (at >= 0)
        {
            for (int i = 0; i < plan.Ops.Count; i++)
                if (plan.Ops[i] is RpfPutOp put && put.GamePath.Equals(GameConfig, StringComparison.OrdinalIgnoreCase))
                {
                    plan.Ops[i] = new RpfEditOp(GameConfig, put.ModId,
                        L.T("Raise the game's limits in gameconfig.xml to the mod's own (the rest of the game's file stays)"),
                        (d, log) => MergeLimits(d, File.ReadAllBytes(put.Source), log, edition));
                    if (edition == GameEdition.Enhanced)
                        plan.Warnings.Add(L.T("The mod brings its own gameconfig.xml — in GTA V Enhanced ModDrop V takes its pool sizes, except the ones Enhanced can’t hold (the biggest model stores stay the game’s)."));
                }
            if (LimitsOp(target.GameDir, edition, profile, reserve) is { } op) ops.Add(op);
        }
        // before the mod's own steps: the plugins aren't part of it and stay when it is removed
        ops.AddRange(AdjusterOps(target, profile, reserve));
        plan.Warnings.AddRange(LimitAdjusters.Notes(target.GameDir, edition, PluginsDir(target), HeapNeed(reserve ?? LiveInstall.Reserving(edition)),
                                                    ArchivesNeed(edition, profile, reserve ?? LiveInstall.Reserving(edition))));
        plan.Ops.InsertRange(at + 1, ops);
        return plan;
    }

    /// <summary>
    /// Ceilings for a mod's values in GTA V Enhanced (0: the game's value stays). The model stores are allocated at start,
    /// Legacy-sized ones crash it at Game Init; the ModelInfo arrays have no bounds check in Legacy either, but the
    /// numbers mods set there (18000 cars) only cost memory. Liberty City (276 cars, 72 peds, 10.7k map models) uses a
    /// fraction of each ceiling.
    /// </summary>
    private static readonly Dictionary<string, int> EnhancedCaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DrawableStore"] = 0, ["DwdStore"] = 0, ["FragmentStore"] = 0, ["TxdStore"] = 0, ["ScaleformStore"] = 0,
        ["CScriptEntityExtension"] = 0,
        ["MapDataStore"] = 20000, ["StaticBounds"] = 30000, ["HandlingData"] = 4000,
        ["MaxMloModelInfos"] = 5000, ["MaxExtraPedModelInfos"] = 1000, ["MaxExtraVehicleModelInfos"] = 1500,
        ["MaxPedModelInfos"] = 0, ["MaxVehicleModelInfos"] = 0, ["MaxWeaponModelInfos"] = 0, ["MaxExtraWeaponModelInfos"] = 0,
    };

    // Every pool and plain limit of the mod's file, first occurrence (the Build=Any section, as Size / Setting read them).
    private static readonly Regex AnyPoolRe = new(@"<PoolName>\s*([^<]+?)\s*</PoolName>\s*<PoolSize\s+value\s*=\s*""(\d+)""");
    private static readonly Regex AnySettingRe = new(@"<([A-Za-z_][A-Za-z0-9_]*)\s+value\s*=\s*""(\d+)""");

    /// <summary>
    /// A mod's own gameconfig.xml merged into the one the game reads now: every pool and plain limit the mod's file sets
    /// higher is raised to it, pools the game's file doesn't list are added, everything else stays the game's. A mod's
    /// gameconfig is written for the game build of its day — Liberty City Preservation Project (December 2024) has no
    /// RosGameServerVersionNumber and a smaller ScriptStore / AnimStore / ScaleformStore than Legacy 3889 fills, and
    /// put in as it is, the game hangs on a black screen after the intro. Online-service values (Ros…, SteamAppId) and
    /// values the game's file has more than once (per-build ones) are never taken from the mod.
    /// </summary>
    /// <remarks>
    /// GTA V Enhanced gets less: its pools live in a heap of their own (raised by Pool Heap Adjuster) and it allocates the
    /// stores up front — with Legacy-sized model stores (Liberty City: DrawableStore 479600, FragmentStore 1187500) it
    /// crashes at Game Init. So there only pools its file lists are raised, a few with a ceiling (<see cref="EnhancedCaps"/>),
    /// and of the plain limits only the Max… counts. What Liberty City Preservation Project needs fits them (checked in
    /// the game, 2026-09-30).
    /// </remarks>
    public static byte[]? MergeLimits(byte[]? data, byte[] mod, Action<string> log, GameEdition edition = GameEdition.Legacy)
    {
        if (data is null) return edition == GameEdition.Enhanced ? null : mod;
        bool enhanced = edition == GameEdition.Enhanced;
        var xml = TextIo.DecodeUtf8Sig(data, strict: false);
        var modXml = TextIo.DecodeUtf8Sig(mod, strict: false);
        var changes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AnyPoolRe.Matches(modXml))
        {
            var name = m.Groups[1].Value;
            if (!seen.Add(name) || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var to)) continue;
            var now = Size(xml, name);
            if (enhanced)
            {
                if (now is null) continue;                       // pools Enhanced's file doesn't list it doesn't read
                if (EnhancedCaps.TryGetValue(name, out var cap)) to = Math.Min(to, Math.Max(cap, now.Value));
            }
            if (now is null) xml = AddPool(xml, name, to);
            else if (now < to) xml = WithSize(xml, name, to);
            else continue;
            changes.Add($"{name} {now?.ToString(CultureInfo.InvariantCulture) ?? "(new)"} → {to}");
        }
        seen.Clear();
        foreach (Match m in AnySettingRe.Matches(modXml))
        {
            var name = m.Groups[1].Value;
            if (!seen.Add(name) || name == "PoolSize" || name.StartsWith("Ros", StringComparison.Ordinal) || name == "SteamAppId") continue;
            int limit = int.MaxValue;
            if (enhanced)
            {
                if (!name.StartsWith("Max", StringComparison.Ordinal)) continue;
                if (EnhancedCaps.TryGetValue(name, out var cap)) limit = cap;
            }
            // a value set per build / platform (PhysicalStreamingBuffer) is paired section by section — only when both
            // files have it as many times; otherwise only one the game's file has once
            var re = SettingRe(name);
            var mine = re.Matches(xml);
            var theirs = re.Matches(modXml);
            if (mine.Count == 0 || mine.Count != theirs.Count && mine.Count != 1) continue;
            int i = 0;
            xml = re.Replace(xml, x =>
            {
                var t = theirs[i++].Groups[2].Value;
                if (!int.TryParse(x.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var now)
                    || !int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var to)) return x.Value;
                to = Math.Min(to, Math.Max(limit, now));
                if (now >= to) return x.Value;
                var v = to.ToString(CultureInfo.InvariantCulture);
                changes.Add($"{name} {x.Groups[2].Value} → {v}");
                return x.Groups[1].Value + v + x.Groups[3].Value;
            });
        }
        if (changes.Count == 0) return data;
        log(L.T($"    gameconfig.xml: the mod's limits merged into the game's — {string.Join(", ", changes)}."));
        return new UTF8Encoding(false).GetBytes(xml);
    }

    public static string PluginsDir(InstallTarget target) =>
        target.PluginsDir ?? Path.Combine(AppContext.BaseDirectory, "data", "plugins");

    /// <summary>A big mod (hundreds of game files changed, gigabytes brought) gets the higher limits.</summary>
    public static LimitsProfile ProfileFor(InstallPlan plan, InstallTarget target)
    {
        var fp = plan.Footprint(target);
        return fp.InArchives >= BigModFiles || fp.Files >= BigModBytes ? LimitsProfile.Large : LimitsProfile.Standard;
    }

    private static Regex PoolRe(string pool) =>
        new($@"(<PoolName>\s*{Regex.Escape(pool)}\s*</PoolName>\s*<PoolSize\s+value\s*=\s*"")(\d+)("")", RegexOptions.IgnoreCase);

    public static int? Size(string xml, string pool) =>
        PoolRe(pool).Match(xml) is { Success: true } m && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : null;

    /// <summary>The same gameconfig.xml with one pool's size changed; everything else stays byte for byte.</summary>
    public static string WithSize(string xml, string pool, int size) =>
        PoolRe(pool).Replace(xml, m => m.Groups[1].Value + size.ToString(CultureInfo.InvariantCulture) + m.Groups[3].Value, 1);

    /// <summary>A pool's size in the gameconfig.xml the game reads now (the copy in mods, else its own); null when unknown.</summary>
    public static int? Read(string gameDir, string pool)
    {
        try
        {
            return ModsOverlay.Load(gameDir).Read(GameConfig) is { } data ? Size(TextIo.DecodeUtf8Sig(data, strict: false), pool) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or RpfFormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The edit for <see cref="RpfEditOp"/>: <paramref name="pool"/> raised to <paramref name="size"/>. When it is that big
    /// already, the file as it is — so every mod that needs the pool holds a version of it in the mods layer, and it stays
    /// raised until the last of them is removed. Null when there is no file or no such pool.
    /// </summary>
    public static byte[]? Raise(byte[]? data, string pool, int size, Action<string> log)
    {
        if (data is null) return null;
        var xml = TextIo.DecodeUtf8Sig(data, strict: false);
        if (Size(xml, pool) is not { } now) return null;
        if (now >= size) return data;
        log(L.T($"    gameconfig.xml: {pool} {now} → {size}."));
        return new UTF8Encoding(false).GetBytes(WithSize(xml, pool, size));
    }
}
