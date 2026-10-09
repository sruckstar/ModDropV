using Mdv.Core;
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
            r.Items.Add(new AddonCheck(CheckLevel.Block, L.T("Built for GTA V Enhanced"),
                L.T("Its models are in the Enhanced (gen9) format, which GTA V Legacy can't load, and they can't be converted back. " +
                "Use the Legacy version of the mod.")));
        else if (target.Edition == GameEdition.Enhanced && eds.Contains(GameEdition.Legacy))
            r.Items.Add(new AddonCheck(CheckLevel.Info, L.T("Converted for GTA V Enhanced"),
                L.T("Its models are Legacy ones — they are converted to the Enhanced (gen9) format while installing.")));

        // ---- what its vehicles name that neither the mod nor the game has
        if (pkg.Gaps.Count > 0)
        {
            var what = string.Join(", ", pkg.Gaps.Select(g => $"{g.Text} ({g.Model})"));
            r.Items.Add(AddonPackHandler.GapBaseOf(pkg) is { } from
                ? new AddonCheck(CheckLevel.Info, L.T("Missing parts taken from a game vehicle"),
                    L.T($"Its vehicles.meta names {what}, which neither the mod nor the game has — the game would crash spawning it. " +
                        $"They are taken from {from.Title} ({from.Model}); pick another vehicle if one fits better."))
                : new AddonCheck(CheckLevel.Block, L.T("Missing parts"),
                    L.T($"Its vehicles.meta names {what}, which neither the mod nor the game has — the game would crash spawning it. " +
                        $"Pick a game vehicle to take them from.")));
        }

        // ---- earlier installs of the same pack; the dlcpacks folder
        var mine = reg.Mods.Where(m => handler.Owns(m.Id)).ToList();
        foreach (var old in mine.Where(m => pkg.Device.Equals(m.Get("device"), StringComparison.OrdinalIgnoreCase) &&
                                            !pkg.PackName.Equals(m.Get("pack"), StringComparison.OrdinalIgnoreCase)))
        {
            r.Supersedes.Add(old.Id);
            r.Items.Add(new AddonCheck(CheckLevel.Info, L.T("Replaces the installed version"),
                L.T($"«{old.Name}» is the same pack (dlcpacks\\{old.Get("pack")}) — it is removed and this one takes its place.")));
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
            r.Items.Add(new AddonCheck(CheckLevel.Info, L.T("Pack folder renamed"),
                gameFolders.Contains(pkg.PackName)
                    ? L.T($"The game has a pack named «{pkg.PackName}» of its own — this one goes in as «{free}».")
                    : L.T($"Another pack is already in {ModsLayout.DlcpacksShown(target.GameDir)}\\{pkg.PackName} — this one goes in as «{free}».")));
        }
        else if (others.FirstOrDefault(o => o.Folder.Equals(pkg.PackName, StringComparison.OrdinalIgnoreCase)) is { } same)
            r.Items.Add(new AddonCheck(CheckLevel.Info, L.T("Already in the game"),
                L.T($"This pack is already in {ModsLayout.DlcpacksShown(target.GameDir)}\\{same.Folder} (not put there by ModDrop V) — it is replaced by this copy.")));
        foreach (var o in others.Where(o => o.On && pkg.Device.Equals(o.Device, StringComparison.OrdinalIgnoreCase) &&
                                            !o.Folder.Equals(pkg.PackName, StringComparison.OrdinalIgnoreCase)))
            r.Items.Add(new AddonCheck(CheckLevel.Warn, L.T("Installed twice"),
                L.T($"The same pack ({pkg.Device}) is already installed as dlcpacks\\{o.Folder} — the game would load it twice. " +
                $"Remove that copy (or keep it and skip this install).")));

        // ---- spawn names
        var names = content.SpawnNames.ToList();
        var dupes = new List<string>();
        foreach (var n in names)
        {
            bool ownGame = pkg.Kind == ModCategory.Vehicle ? vanilla.IsVehicle(n) : vanilla.IsPed(n);
            var other = others.FirstOrDefault(o => o.On && o.Names.Contains(n, StringComparer.OrdinalIgnoreCase));
            if (ownGame)
                dupes.Add(L.T($"«{n}» is a {(pkg.Kind == ModCategory.Vehicle ? "vehicle" : "ped")} of the game itself — the add-on overrides it, " +
                          $"which often crashes the game or breaks the original"));
            else if (other is not null)
                dupes.Add(L.T($"«{n}» is already in the add-on pack «{other.Folder}» — only one of the two loads"));
            else if (index is not null && pkg.Kind == ModCategory.Vehicle && index.Find(n + ".yft").FirstOrDefault(h => h.Active && !InFolder(h, ownFolders)) is { } hit)
                dupes.Add(L.T($"the game already has a model «{n}» ({hit.Source}) — only one of the two loads"));
        }
        if (dupes.Count > 0)
            foreach (var d in dupes) r.Items.Add(new AddonCheck(CheckLevel.Warn, L.T("Spawn name taken"), d + "."));
        else if (names.Count > 0)
            r.Items.Add(new AddonCheck(CheckLevel.Ok, names.Count == 1 ? L.T("Spawn name is free") : L.T("Spawn names are free"),
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
                    clashes.Add(L.T($"«{c.FullName}» is already in the add-on pack «{other.Folder}» — only one of the two loads, the other's clothes are lost"));
                else if (index?.Find(c.FullName + ".ymt").FirstOrDefault(h => h.Active && !InFolder(h, ownFolders)) is { } hit)
                    clashes.Add(L.T($"the game already has a collection «{c.FullName}» ({hit.Source}) — only one of the two loads"));
            }
            foreach (var d in clashes) r.Items.Add(new AddonCheck(CheckLevel.Warn, L.T("Collection name taken"), d + "."));
            if (clashes.Count == 0 && colls.Count > 0)
                r.Items.Add(new AddonCheck(CheckLevel.Ok, colls.Count == 1 ? L.T("Collection name is free") : L.T("Collection names are free"),
                    string.Join(", ", colls.Select(c => c.FullName))));
        }

        // ---- animation dictionaries: one named like another's — the game's or another pack's — stands in for it
        if (pkg.Kind == ModCategory.Animation)
        {
            var dicts = content.Anims.ToList();
            var gameAnims = GameModels.LoadAnims(pkg.DataDir);
            var clashes = new List<string>();
            foreach (var d in dicts)
            {
                if (gameAnims.Has(d))
                    clashes.Add(L.T($"«{d}» is an animation dictionary of the game itself — the add-on's takes its place"));
                else if (index?.Find(d + ".ycd").FirstOrDefault(h => h.Active && !InFolder(h, ownFolders)) is { } hit)
                    clashes.Add(L.T($"«{d}» is already in the game ({hit.Source}) — only one of the two loads"));
            }
            foreach (var c in clashes) r.Items.Add(new AddonCheck(CheckLevel.Warn, L.T("Animation name taken"), c + "."));
            if (clashes.Count == 0 && dicts.Count > 0)
                r.Items.Add(new AddonCheck(CheckLevel.Ok, dicts.Count == 1 ? L.T("Animation name is free") : L.T("Animation names are free"),
                    string.Join(", ", dicts.Take(6)) + (dicts.Count > 6 ? ", …" : "")));
        }

        // ---- weapon components from another mod: listed after it, or the game stops loading without it
        r.Items.AddRange(DlcOrder.Checks(game, target.Edition, pkg.DataDir, new(content.ComponentsDefined, content.ComponentsUsed), ownFolders));

        // ---- maps: what they place must exist; placements named like the game's replace them
        if (pkg.Kind == ModCategory.Map && index is not null) MapChecks(pkg, index, ownFolders, r);

        // ---- modkit ids
        var taken = new Dictionary<int, string>();
        foreach (var o in others)
            foreach (var k in o.Kits) taken.TryAdd(k.Id, L.T($"the add-on pack «{o.Folder}»"));
        var assigned = new HashSet<int>();
        var ownIds = new HashSet<int>();
        foreach (var k in content.Kits)
        {
            string? by = vanilla.IsKit(k.Id) ? L.T("the game itself") : taken.GetValueOrDefault(k.Id);
            if (by is null && !ownIds.Add(k.Id)) by = L.T("another kit of this pack");
            if (by is null) continue;
            int id = 1000;
            while (vanilla.IsKit(id) || taken.ContainsKey(id) || content.Kits.Any(x => x.Id == id) || assigned.Contains(id)) id++;
            assigned.Add(id);
            r.KitFixes.Add(new KitFix(k, id, by));
        }
        foreach (var f in r.KitFixes)
            r.Items.Add(new AddonCheck(CheckLevel.Warn, L.T("Modkit id taken"),
                L.T($"Modkit {f.Kit.Name} has id {f.Kit.Id}, which {f.TakenBy} uses too — its tuning parts would show on the wrong " +
                $"vehicles. {(pkg.FixKits ? L.T($"It gets the free id {f.NewId} when installed.") : L.T($"Id {f.NewId} is free (fixing is switched off)."))}")));
        if (content.Kits.Count > 0 && r.KitFixes.Count == 0)
            r.Items.Add(new AddonCheck(CheckLevel.Ok, content.Kits.Count == 1 ? L.T("Modkit id is free") : L.T("Modkit ids are free"),
                string.Join(", ", content.Kits.Select(k => k.Id).Distinct())));

        // ---- limits
        // the others that stay in, and this one (its earlier versions aren't among the others)
        r.AddonPacks = others.Count(o => o.On && !o.Folder.Equals(pkg.PackName, StringComparison.OrdinalIgnoreCase)) + 1;
        // the limit plugins come with any install (GamePools.AdjusterOps), in both editions
        if (r.AddonPacks >= ManyPacks)
            r.Items.Add(new AddonCheck(CheckLevel.Info, L.T("Many add-on packs"),
                L.T($"{r.AddonPacks} add-on packs — ModDrop V raises the game's limits for them (gameconfig.xml, Heap Adjuster and " +
                $"Packfile Limit Adjuster). If the game still crashes on loading, raise the values in HeapAdjuster.ini and " +
                $"PackfileLimitAdjusterEnhanced.ini in the game folder.")));
        return r;
    }

    /// <summary>
    /// A map's placements against the game: models it places that neither the mod nor the game has (they don't show —
    /// another mod has them), and placement files named like the game's own (the pack's version is used instead).
    /// </summary>
    private static void MapChecks(AddonPackage pkg, GameIndex index, HashSet<string> ownFolders, AddonCheckReport r)
    {
        var c = pkg.Content;
        var mine = new HashSet<uint>(c.Archetypes.Select(MapMeta.Hash));
        foreach (var s in c.Streamed.Where(s => PathUtil.SuffixLower(s) is ".ydr" or ".ydd" or ".yft"))
            mine.Add(Gxt2.Joaat(Path.GetFileNameWithoutExtension(s.Split('/')[^1])));
        var models = index.Models;
        var missing = c.Maps.SelectMany(m => m.Archetypes).Distinct()
                       .Where(a => !mine.Contains(MapMeta.Hash(a)) && !models.ContainsKey(MapMeta.Hash(a))).ToList();
        pkg.MissingModels.Clear();
        pkg.MissingModels.AddRange(missing);
        if (missing.Count > 0)
            r.Items.Add(new AddonCheck(CheckLevel.Warn, L.T("Models not in the game"), PlacementHandler.MissingText(missing)));
        else if (c.Maps.Count > 0)
            r.Items.Add(new AddonCheck(CheckLevel.Ok, L.T("Every model is there"),
                L.T($"{c.Maps.Sum(m => m.Entities)} objects of {c.Maps.SelectMany(m => m.Archetypes).Distinct().Count()} kinds — from the mod or the game.")));
        var replaced = c.Ymaps.Select(y => y.Split('/')[^1])
                        .Where(y => index.Find(y).Any(h => h.Active && !InFolder(h, ownFolders)))
                        .Select(Path.GetFileNameWithoutExtension).ToList();
        if (replaced.Count > 0)
            r.Items.Add(new AddonCheck(CheckLevel.Info, L.T("Changes the game's map"),
                L.T($"Parts of the game's own map: {string.Join(", ", replaced.Take(5))}{(replaced.Count > 5 ? ", …" : "")} — the pack's version is used instead while it is installed.")));
    }

    private static bool InFolder(FileHit h, HashSet<string> folders) =>
        h.InstalledPack is { } pack && folders.Contains(pack);

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
                    var p = AddonContent.ReadPack(rpf, maps: false);
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
