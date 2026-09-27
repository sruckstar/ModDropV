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

    private static (Dictionary<string, int> Pools, Dictionary<string, int> Settings) Table(GameEdition edition, LimitsProfile profile) =>
        edition == GameEdition.Enhanced ? (EnhancedPools, EnhancedSettings)
        : profile == LimitsProfile.Large ? (LargePools, LargeSettings) : (StandardPools, StandardSettings);

    private static Regex SettingRe(string name) => new($@"(<{Regex.Escape(name)}\s+value\s*=\s*"")(\d+)("")");

    /// <summary>A plain limit of gameconfig.xml (<c>&lt;ArchiveCount value="4440"/&gt;</c>), its first occurrence; null when absent.</summary>
    public static int? Setting(string xml, string name) =>
        SettingRe(name).Match(xml) is { Success: true } m && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : null;

    /// <summary>
    /// What the limits for <paramref name="edition"/> and <paramref name="profile"/> raise in this gameconfig.xml: the name,
    /// its size now (null: a pool the file doesn't list yet) and the new size. Plain limits the file doesn't have are left alone.
    /// </summary>
    public static List<(string Name, int? Now, int To)> Missing(string xml, GameEdition edition, LimitsProfile profile)
    {
        var (pools, settings) = Table(edition, profile);
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
    /// AudioHeap 250, CGameScriptResource 3089, fwArchetypePooledMap 65521).
    /// </summary>
    public static List<string> Over(string xml, GameEdition edition, LimitsProfile profile, string? own = null)
    {
        var (pools, settings) = Table(edition, profile);
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
    public static byte[]? RaiseLimits(byte[]? data, GameEdition edition, LimitsProfile profile, Action<string> log)
    {
        if (data is null) return null;
        var xml = TextIo.DecodeUtf8Sig(data, strict: false);
        var missing = Missing(xml, edition, profile);
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
    /// <paramref name="edition"/> takes are taken back and raised again to its own.
    /// </summary>
    public static PlanOp? LimitsOp(string gameDir, GameEdition edition, LimitsProfile profile)
    {
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
                if (RaiseLimits(below, edition, profile, ctx.Log) is { } raised && !ReferenceEquals(raised, below))
                    ctx.Overlay.Put(LimitsOwner, GameConfig, raised);
            });
        var missing = Missing(xml, edition, profile);
        if (missing.Count == 0) return null;
        return new RpfEditOp(GameConfig, LimitsOwner,
            edition == GameEdition.Enhanced
                ? L.T($"Raise the game's limits in gameconfig.xml for mods ({missing.Count} values: model and texture stores, MetaDataStore, interiors)")
                : profile == LimitsProfile.Large
                    ? L.T($"Raise the game's limits in gameconfig.xml for a big mod ({missing.Count} values: model and texture stores, add-on vehicles, peds and weapons, archives, map and world pools)")
                    : L.T($"Raise the game's limits in gameconfig.xml for mods ({missing.Count} values: model and texture stores, add-on vehicles, peds and weapons, archives)"),
            (d, log) => RaiseLimits(d, edition, profile, log));
    }

    /// <summary>
    /// The limits step put into an install plan, right after it makes the game load the mods folder (a plan that doesn't
    /// use the mods folder gets none). A big mod gets the higher limits. A mod that brings its own gameconfig.xml keeps it.
    /// </summary>
    public static InstallPlan WithLimits(InstallPlan plan, InstallTarget target)
    {
        int at = plan.Ops.FindIndex(o => o is EnsureModsLoaderOp);
        if (at < 0) return plan;
        var ops = new List<PlanOp>();
        if (plan.Ops.Any(o => o is RpfPutOp put && put.GamePath.Equals(GameConfig, StringComparison.OrdinalIgnoreCase)))
            plan.Warnings.Add(L.T("This mod brings its own gameconfig.xml — it replaces the raised limits ModDrop V keeps for mods."));
        else if (LimitsOp(target.GameDir, target.Edition, ProfileFor(plan, target)) is { } op)
            ops.Add(op);
        // before the mod's own steps: the plugins aren't part of it and stay when it is removed
        if (LimitAdjusters.Op(target.GameDir, target.Edition, PluginsDir(target)) is { } adjusters) ops.Add(adjusters);
        plan.Ops.InsertRange(at + 1, ops);
        return plan;
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
