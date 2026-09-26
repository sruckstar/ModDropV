using System.Text.Json;
using System.Text.Json.Serialization;
using Mdv.Core.Index;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>The game's own vehicle / ped names and modkit ids (data/vanilla_models.json) — add-ons must not reuse them.</summary>
public sealed class VanillaModels
{
    [JsonPropertyName("vehicles")] public List<string> Vehicles { get; set; } = [];
    [JsonPropertyName("peds")] public List<string> Peds { get; set; } = [];
    [JsonPropertyName("modkits")] public List<int> Modkits { get; set; } = [];

    private HashSet<string>? _vehicles, _peds;
    private HashSet<int>? _kits;

    public bool IsVehicle(string model) => (_vehicles ??= new(Vehicles, StringComparer.OrdinalIgnoreCase)).Contains(model);
    public bool IsPed(string name) => (_peds ??= new(Peds, StringComparer.OrdinalIgnoreCase)).Contains(name);
    public bool IsKit(int id) => (_kits ??= [.. Modkits]).Contains(id);

    private static readonly Dictionary<string, VanillaModels> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static VanillaModels Load(string? dataDir)
    {
        var path = Path.Combine(dataDir ?? DependencyCatalog.DefaultDataDir, "vanilla_models.json");
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var v)) return v;
            try
            {
                v = File.Exists(path) ? JsonSerializer.Deserialize<VanillaModels>(File.ReadAllText(path)) ?? new() : new();
            }
            catch (JsonException)
            {
                v = new();
            }
            return Cache[path] = v;
        }
    }
}

public enum CheckLevel { Ok, Info, Warn, Block }

/// <summary>One finding of the checks against the game.</summary>
/// <param name="Link">where to get what fixes it</param>
public sealed record AddonCheck(CheckLevel Level, string Title, string Detail, string? Link = null);

/// <summary>A modkit whose id another pack (or the game) already uses, and the free id it gets on install.</summary>
public sealed record KitFix(AddonKit Kit, int NewId, string TakenBy);

/// <summary>What the checks against one game found.</summary>
public sealed class AddonCheckReport
{
    public required string GameDir { get; init; }
    public GameEdition Edition { get; init; }
    public List<AddonCheck> Items { get; } = [];
    public List<KitFix> KitFixes { get; } = [];
    /// <summary>A free dlcpacks folder name, when the one asked for is taken by the game or another pack.</summary>
    public string? FreePackName { get; set; }
    /// <summary>Registry ids of earlier installs of the same pack (same device) under another folder — replaced on install.</summary>
    public List<string> Supersedes { get; } = [];
    /// <summary>Add-on packs in mods after this one is in.</summary>
    public int AddonPacks { get; set; }
    /// <summary>
    /// The game's MetaDataStore size when the pack brings .ymt files and the pool needs raising in gameconfig.xml — below
    /// <see cref="GamePools.MetaDataStoreTarget"/>, or raised already by another pack that holds it (null: leave it).
    /// </summary>
    public int? MetaDataStoreNow { get; set; }
    public string? Blocking => Items.FirstOrDefault(i => i.Level == CheckLevel.Block)?.Detail;
}

/// <summary>
/// Checks an add-on pack against the game it goes into: models built for the other edition, a spawn name
/// the game or another pack already has, the same pack installed under another folder, the dlcpacks folder
/// name, modkit ids another pack uses (the known cause of "someone else's tuning parts" — a free id is
/// proposed and written into the pack on install), and the add-on limits many packs run into.
/// </summary>
public static class AddonChecks
{
    /// <summary>From this many add-on packs on, the limits the game has for DLC are worth a word.</summary>
    public const int ManyPacks = 25;

    public const string PackfileLimitLink = "https://www.gta5-mods.com/tools/packfile-limit-adjuster";
    public const string HeapLink = "https://www.gta5-mods.com/tools/heapadjuster";

    /// <summary>What another add-on pack in mods declares (its folder, device, spawn names, kits).</summary>
    private sealed record OtherPack(string Folder, bool On, string? Device, List<string> Names, List<AddonKit> Kits, List<string> Collections);

    public static AddonCheckReport Run(AddonPackage pkg, InstallTarget target, GameIndex? index = null)
    {
        var game = target.GameDir;
        var r = new AddonCheckReport { GameDir = Path.GetFullPath(game), Edition = target.Edition };
        var vanilla = VanillaModels.Load(pkg.DataDir);
        var content = pkg.Content;
        var reg = ModRegistry.Load(game);
        var handler = new AddonPackHandler(pkg.Kind);

        // ---- the models' edition
        var eds = content.ModelEditions;
        if (target.Edition == GameEdition.Legacy && eds.Contains(GameEdition.Enhanced))
            r.Items.Add(new AddonCheck(CheckLevel.Block, "Built for GTA V Enhanced",
                "Its models are in the Enhanced (gen9) format, which GTA V Legacy can't load, and they can't be converted back. " +
                "Use the Legacy version of the mod."));
        else if (target.Edition == GameEdition.Enhanced && eds.Contains(GameEdition.Legacy))
            r.Items.Add(new AddonCheck(CheckLevel.Info, "Converted for GTA V Enhanced",
                "Its models are Legacy ones — they are converted to the Enhanced (gen9) format while installing."));

        // ---- earlier installs of the same pack; the dlcpacks folder
        var mine = reg.Mods.Where(m => handler.Owns(m.Id)).ToList();
        foreach (var old in mine.Where(m => pkg.Device.Equals(m.Get("device"), StringComparison.OrdinalIgnoreCase) &&
                                            !pkg.PackName.Equals(m.Get("pack"), StringComparison.OrdinalIgnoreCase)))
        {
            r.Supersedes.Add(old.Id);
            r.Items.Add(new AddonCheck(CheckLevel.Info, "Replaces the installed version",
                $"«{old.Name}» is the same pack (dlcpacks\\{old.Get("pack")}) — it is removed and this one takes its place."));
        }
        var ownFolders = new HashSet<string>(mine.Where(m => r.Supersedes.Contains(m.Id) ||
                                                             pkg.PackName.Equals(m.Get("pack"), StringComparison.OrdinalIgnoreCase))
                                                 .Select(m => m.Get("pack") ?? ""), StringComparer.OrdinalIgnoreCase);
        var others = OtherPacks(game, ownFolders);
        var gameFolders = GameDlcFolders(game);
        if (gameFolders.Contains(pkg.PackName) || others.Any(o => o.Folder.Equals(pkg.PackName, StringComparison.OrdinalIgnoreCase)
                                                                   && !pkg.Device.Equals(o.Device, StringComparison.OrdinalIgnoreCase)))
        {
            var free = FreeName(pkg.PackName, n => gameFolders.Contains(n) || others.Any(o => o.Folder.Equals(n, StringComparison.OrdinalIgnoreCase)));
            r.FreePackName = free;
            r.Items.Add(new AddonCheck(CheckLevel.Info, "Pack folder renamed",
                gameFolders.Contains(pkg.PackName)
                    ? $"The game has a pack named «{pkg.PackName}» of its own — this one goes in as «{free}»."
                    : $"Another pack is already in mods\\update\\x64\\dlcpacks\\{pkg.PackName} — this one goes in as «{free}»."));
        }
        else if (others.FirstOrDefault(o => o.Folder.Equals(pkg.PackName, StringComparison.OrdinalIgnoreCase)) is { } same)
            r.Items.Add(new AddonCheck(CheckLevel.Info, "Already in the game",
                $"This pack is already in mods\\update\\x64\\dlcpacks\\{same.Folder} (not put there by ModDrop V) — it is replaced by this copy."));
        foreach (var o in others.Where(o => o.On && pkg.Device.Equals(o.Device, StringComparison.OrdinalIgnoreCase) &&
                                            !o.Folder.Equals(pkg.PackName, StringComparison.OrdinalIgnoreCase)))
            r.Items.Add(new AddonCheck(CheckLevel.Warn, "Installed twice",
                $"The same pack ({pkg.Device}) is already installed as dlcpacks\\{o.Folder} — the game would load it twice. " +
                "Remove that copy (or keep it and skip this install)."));

        // ---- spawn names
        var names = content.SpawnNames.ToList();
        var dupes = new List<string>();
        foreach (var n in names)
        {
            bool ownGame = pkg.Kind == ModCategory.Vehicle ? vanilla.IsVehicle(n) : vanilla.IsPed(n);
            var other = others.FirstOrDefault(o => o.On && o.Names.Contains(n, StringComparer.OrdinalIgnoreCase));
            if (ownGame)
                dupes.Add($"«{n}» is a {(pkg.Kind == ModCategory.Vehicle ? "vehicle" : "ped")} of the game itself — the add-on overrides it, " +
                          "which often crashes the game or breaks the original");
            else if (other is not null)
                dupes.Add($"«{n}» is already in the add-on pack «{other.Folder}» — only one of the two loads");
            else if (index is not null && pkg.Kind == ModCategory.Vehicle && index.Find(n + ".yft").FirstOrDefault(h => h.Active && !InFolder(h, ownFolders)) is { } hit)
                dupes.Add($"the game already has a model «{n}» ({hit.Source}) — only one of the two loads");
        }
        if (dupes.Count > 0)
            foreach (var d in dupes) r.Items.Add(new AddonCheck(CheckLevel.Warn, "Spawn name taken", d + "."));
        else if (names.Count > 0)
            r.Items.Add(new AddonCheck(CheckLevel.Ok, names.Count == 1 ? "Spawn name is free" : "Spawn names are free",
                string.Join(", ", names.Take(6)) + (names.Count > 6 ? ", …" : "")));

        // ---- clothing collections: a name another pack (or the game) has means one of the two doesn't load
        if (pkg.Kind == ModCategory.Clothing)
        {
            var colls = AddonPackHandler.CollectionsOf(pkg);
            var clashes = new List<string>();
            foreach (var c in colls)
            {
                var other = others.FirstOrDefault(o => o.On && o.Collections.Contains(c.FullName, StringComparer.OrdinalIgnoreCase));
                if (other is not null)
                    clashes.Add($"«{c.FullName}» is already in the add-on pack «{other.Folder}» — only one of the two loads, the other's clothes are lost");
                else if (index?.Find(c.FullName + ".ymt").FirstOrDefault(h => h.Active && !InFolder(h, ownFolders)) is { } hit)
                    clashes.Add($"the game already has a collection «{c.FullName}» ({hit.Source}) — only one of the two loads");
            }
            foreach (var d in clashes) r.Items.Add(new AddonCheck(CheckLevel.Warn, "Collection name taken", d + "."));
            if (clashes.Count == 0 && colls.Count > 0)
                r.Items.Add(new AddonCheck(CheckLevel.Ok, colls.Count == 1 ? "Collection name is free" : "Collection names are free",
                    string.Join(", ", colls.Select(c => c.FullName))));
        }

        // ---- modkit ids
        var taken = new Dictionary<int, string>();
        foreach (var o in others)
            foreach (var k in o.Kits) taken.TryAdd(k.Id, $"the add-on pack «{o.Folder}»");
        var assigned = new HashSet<int>();
        var ownIds = new HashSet<int>();
        foreach (var k in content.Kits)
        {
            string? by = vanilla.IsKit(k.Id) ? "the game itself" : taken.GetValueOrDefault(k.Id);
            if (by is null && !ownIds.Add(k.Id)) by = "another kit of this pack";
            if (by is null) continue;
            int id = 1000;
            while (vanilla.IsKit(id) || taken.ContainsKey(id) || content.Kits.Any(x => x.Id == id) || assigned.Contains(id)) id++;
            assigned.Add(id);
            r.KitFixes.Add(new KitFix(k, id, by));
        }
        foreach (var f in r.KitFixes)
            r.Items.Add(new AddonCheck(CheckLevel.Warn, "Modkit id taken",
                $"Modkit {f.Kit.Name} has id {f.Kit.Id}, which {f.TakenBy} uses too — its tuning parts would show on the wrong " +
                $"vehicles. {(pkg.FixKits ? $"It gets the free id {f.NewId} when installed." : $"Id {f.NewId} is free (fixing is switched off).")}"));
        if (content.Kits.Count > 0 && r.KitFixes.Count == 0)
            r.Items.Add(new AddonCheck(CheckLevel.Ok, content.Kits.Count == 1 ? "Modkit id is free" : "Modkit ids are free",
                string.Join(", ", content.Kits.Select(k => k.Id).Distinct())));

        // ---- limits
        // every .ymt takes a place in MetaDataStore, which the game's own files fill to the last one
        int ymts = content.Streamed.Count(s => s.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase)) +
                   (pkg.Compose?.NewCollections.Count(n => n.Parts.Count > 0) ?? 0);
        if (ymts > 0 && GamePools.Read(game, GamePools.MetaDataStore) is { } store)
        {
            bool heldByOthers = reg.Mods.Any(m => m.Get("pools") == "1" && !ownFolders.Contains(m.Get("pack") ?? ""));
            if (store < GamePools.MetaDataStoreTarget)
            {
                r.MetaDataStoreNow = store;
                r.Items.Add(new AddonCheck(CheckLevel.Info, "Game limit raised",
                    $"The game's own .ymt files take all {store} places of its MetaDataStore (gameconfig.xml); with this pack's " +
                    $"{(ymts == 1 ? "one" : ymts.ToString(System.Globalization.CultureInfo.InvariantCulture))} more it would crash on loading. " +
                    $"The limit is raised to {GamePools.MetaDataStoreTarget} in the copy of gameconfig.xml under mods — removing the pack puts it back."));
            }
            else if (heldByOthers)
            {
                r.MetaDataStoreNow = store;
                r.Items.Add(new AddonCheck(CheckLevel.Info, "Game limit",
                    $"MetaDataStore (gameconfig.xml) is already raised to {store} for another add-on — this pack keeps it raised while it is installed."));
            }
        }

        // the others that stay in, and this one (its earlier versions aren't among the others)
        r.AddonPacks = others.Count(o => o.On && !o.Folder.Equals(pkg.PackName, StringComparison.OrdinalIgnoreCase)) + 1;
        if (r.AddonPacks >= ManyPacks)
        {
            if (target.Edition == GameEdition.Legacy)
            {
                if (!File.Exists(Path.Combine(game, "PackfileLimitAdjuster.asi")))
                    r.Items.Add(new AddonCheck(CheckLevel.Warn, "Many add-on packs",
                        $"{r.AddonPacks} add-on packs — past a few dozen the game hits its archive limit and crashes on loading. " +
                        "Packfile Limit Adjuster raises it.", PackfileLimitLink));
                if (!File.Exists(Path.Combine(game, "HeapAdjuster.asi")))
                    r.Items.Add(new AddonCheck(CheckLevel.Info, "Memory for add-ons",
                        "With many add-on vehicles the game can run out of streaming memory (ERR_MEM_EMBEDDEDALLOC). " +
                        "Heap Adjuster gives it more; a gameconfig.xml made for add-on vehicles raises the vehicle limits.", HeapLink));
            }
            else
                r.Items.Add(new AddonCheck(CheckLevel.Info, "Many add-on packs",
                    $"{r.AddonPacks} add-on packs — if the game crashes on loading, its limits (packfiles, streaming memory, " +
                    "gameconfig.xml) need raising with tools made for GTA V Enhanced."));
        }
        return r;
    }

    private static bool InFolder(FileHit h, HashSet<string> folders) =>
        folders.Any(f => h.Archive.RelPath.StartsWith($"mods/update/x64/dlcpacks/{f}/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The game's own DLC folder names (update\x64\dlcpacks) — a pack in mods under one of them would replace it.</summary>
    private static HashSet<string> GameDlcFolders(string game)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(game, "update", "x64", "dlcpacks");
        if (Directory.Exists(dir))
            foreach (var d in Directory.EnumerateDirectories(dir)) set.Add(Path.GetFileName(d));
        return set;
    }

    /// <summary>Add-on packs in mods (on and switched off), except <paramref name="skip"/>: what each declares.</summary>
    private static List<OtherPack> OtherPacks(string game, HashSet<string> skip)
    {
        var list = new List<OtherPack>();
        var gameFolders = GameDlcFolders(game);
        foreach (var (root, on) in new[] { (GameInstaller.DlcpacksDir(game), true), (GameInstaller.DisabledDlcpacksDir(game), false) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var folder = Path.GetFileName(dir);
                if (skip.Contains(folder) || gameFolders.Contains(folder)) continue;     // ours, or a patched game pack
                var rpf = Path.Combine(dir, "dlc.rpf");
                if (!File.Exists(rpf)) continue;
                try
                {
                    var p = AddonContent.ReadPack(rpf);
                    list.Add(new OtherPack(folder, on, p.Device, [.. p.Content.SpawnNames], p.Content.Kits, [.. p.Content.Collections.Select(c => c.FullName)]));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or RpfEncryptedException)
                {
                    list.Add(new OtherPack(folder, on, null, [], [], []));                    // can't look inside — still a folder taken
                }
            }
        }
        return list;
    }

    private static string FreeName(string name, Func<string, bool> taken)
    {
        for (int n = 2; ; n++)
        {
            var candidate = $"{name}{n}";
            if (!taken(candidate)) return candidate;
        }
    }
}
