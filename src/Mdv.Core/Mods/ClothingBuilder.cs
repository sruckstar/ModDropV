using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>Where a model goes: the MP ped (male / female) and a component slot or a prop anchor.</summary>
public sealed record ClothingSlot(bool Female, bool Prop, int Slot)
{
    public string Ped => Female ? ClothingNames.MpFemale : ClothingNames.MpMale;
    /// <summary>"jbib", "p_head" — as the files are named.</summary>
    public string Id => Prop ? "p_" + ClothingNames.Anchors[Slot] : ClothingNames.Components[Slot];
    /// <summary>"Tops", "Hats" — as trainers' wardrobes name it.</summary>
    public string Label => L.T(Prop ? ClothingNames.TrainerAnchors[Slot] : ClothingNames.TrainerComponents[Slot]);
    /// <summary>A slot of the other ped.</summary>
    public ClothingSlot WithFemale(bool female) => this with { Female = female };
}

/// <summary>
/// A clothing model of the folder (<c>jbib_000_u.ydd</c>, or any name: <c>hoodie.ydd</c>) with the textures, alternatives
/// and cloth that go with it.
/// </summary>
public sealed class ClothingItem
{
    /// <summary>Its path in the folder (the key its slot is assigned by).</summary>
    public required string Key { get; init; }
    public required string File { get; init; }
    /// <summary>Its file name (a FiveM <c>collection^</c> prefix left out).</summary>
    public required string Name { get; init; }
    /// <summary>What its name says, when it is named as the game's clothes are.</summary>
    public ClothingPart? Part { get; init; }
    /// <summary>The ped its folder / FiveM prefix names (null: neither says).</summary>
    public bool? Female { get; set; }
    /// <summary>The slot its name gives (or its words suggest: <see cref="SlotGuessed"/>); null: none.</summary>
    public (bool Prop, int Slot)? Slot { get; set; }
    public bool SlotGuessed { get; set; }
    /// <summary>Its textures in variant order, each with its race id (0 uni, 1 whi…).</summary>
    public List<(string File, int Race)> Textures { get; } = [];
    /// <summary>Alternative drawables (<c>accs_001_u_1.ydd</c>), in order.</summary>
    public List<string> Alternatives { get; } = [];
    /// <summary>Its cloth simulation (<c>.yld</c>).</summary>
    public string? Cloth { get; set; }
    public bool RaceSpecific => Part?.RaceSpecific ?? false;
    /// <summary>Its number in the files (the order models of a slot keep); arbitrary names come after.</summary>
    public int Number => Part?.Number ?? int.MaxValue;
}

/// <summary>What a modder's folder holds for MP clothes: models of any name, their textures, FiveM's stream folders.</summary>
public sealed class ClothingSource
{
    public required string Folder { get; init; }
    public List<ClothingItem> Items { get; } = [];
    public string? PrebuiltRpf { get; set; }
    public bool IsFiveM { get; set; }
    /// <summary>Its own ymt / shop meta / creature metadata — not used: they're written for the new numbers.</summary>
    public List<string> Ignored { get; } = [];
    public HashSet<GameEdition> Editions { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>MP clothes to build: the modder's folder and what the form says.</summary>
public sealed class ClothingBuildOptions
{
    public required string InputFolder { get; init; }
    public required string OutDir { get; init; }
    /// <summary>The pack's and collections' name (<c>mp_m_&lt;name&gt;</c>, <c>mp_f_&lt;name&gt;</c>); null: the folder's name.</summary>
    public string? Name { get; init; }
    /// <summary>The ped of models whose folder names neither.</summary>
    public bool DefaultFemale { get; init; }
    /// <summary>Slots set by hand, by <see cref="ClothingItem.Key"/> — win over what names and folders say.</summary>
    public Dictionary<string, ClothingSlot> Assign { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Pack { get; init; } = true;
    public GameEdition Edition { get; init; } = GameEdition.Legacy;
    public string? DataDir { get; init; }
}

/// <summary>A model in the collection: its slot and the number it gets there.</summary>
public sealed record ClothingEntry(ClothingItem Item, ClothingSlot Slot, int Number)
{
    /// <summary>Its drawable's name in the pack (<c>jbib_003_u.ydd</c>, <c>p_head_001.ydd</c>).</summary>
    public string NewName => Slot.Prop ? $"p_{ClothingNames.Anchors[Slot.Slot]}_{Number:000}.ydd"
                                       : $"{Slot.Id}_{Number:000}_{(Item.RaceSpecific ? 'r' : 'u')}.ydd";
    /// <summary>"Tops 3" — where it shows in the collection.</summary>
    public string Where => $"{Slot.Label} {Number}";
}

/// <param name="Name">the pack's name (dlcpacks folder, dlc_&lt;name&gt;) and the collections' ending</param>
public sealed record ClothingPlan(string Name, IReadOnlyList<ClothingEntry> Entries)
{
    public static string CollectionOf(string name, bool female) => (female ? "mp_f_" : "mp_m_") + name;
    /// <summary>The collections it makes (one per ped with models), female last.</summary>
    public IEnumerable<(bool Female, string Collection)> Collections =>
        Entries.Select(e => e.Slot.Female).Distinct().Order().Select(f => (f, CollectionOf(Name, f)));
}

public sealed record ClothingBuildResult(string Root, string? DlcRpf, string Device, ClothingPlan Plan);

/// <summary>
/// Modder's MP clothes: models of the folder (named as the game's are — <c>jbib_000_u.ydd</c>, FiveM's
/// <c>mp_m_freemode_01_x^jbib_000_u.ydd</c> — or anything, the slot then picked by hand) go into an add-on collection of
/// each MP ped, numbered from 0 per slot; its ymt (<c>CPedVariationInfo</c>, as Rockstar's collections) and shop meta
/// are written for them and the pack is laid out as Rockstar's clothing DLCs (<c>clothes_male.rpf</c> /
/// <c>clothes_female.rpf</c>) by <see cref="DlcComposer"/>.
/// </summary>
public static partial class ClothingBuilder
{
    private static readonly HashSet<string> ModelExts = new(StringComparer.OrdinalIgnoreCase) { ".ydd", ".ytd", ".yld" };

    /// <summary>Texture variants a drawable can have (a…z).</summary>
    public const int MaxTextures = 26;

    [GeneratedRegex(@"[a-z]+")] private static partial Regex WordRe();

    /// <summary>Words of a file name → the slot they suggest (English, as mods name their files).</summary>
    private static readonly (string[] Words, bool Prop, int Slot)[] SlotWords =
    [
        (["hat", "hats", "cap", "caps", "helmet", "beanie", "beret", "headwear"], true, 0),
        (["glasses", "sunglasses", "goggles", "eyewear"], true, 1),
        (["earring", "earrings", "earpiece"], true, 2),
        (["watch", "watches"], true, 6),
        (["bracelet", "bracelets"], true, 7),
        (["mask", "masks", "balaclava", "beard"], false, 1),
        (["hair", "hairstyle"], false, 2),
        (["gloves", "glove", "arms", "torso"], false, 3),
        (["pants", "jeans", "trousers", "shorts", "skirt", "legs", "joggers"], false, 4),
        (["bag", "backpack", "parachute"], false, 5),
        (["shoes", "boots", "sneakers", "feet", "heels", "trainers"], false, 6),
        (["chain", "chains", "necklace", "tie", "scarf", "accessory"], false, 7),
        (["undershirt", "tshirt"], false, 8),
        (["vest", "armor", "armour", "kevlar"], false, 9),
        (["decal", "decals", "badge", "logo"], false, 10),
        (["jacket", "hoodie", "top", "tops", "shirt", "coat", "sweater", "jersey", "suit", "blazer"], false, 11),
    ];

    /// <summary>A slot by its file-name id (<c>jbib</c>, <c>p_head</c>); null: not one.</summary>
    public static (bool Prop, int Slot)? SlotById(string id)
    {
        id = id.Trim().ToLowerInvariant();
        if (id.StartsWith("p_", StringComparison.Ordinal) && Array.IndexOf(ClothingNames.Anchors, id[2..]) is >= 0 and var a) return (true, a);
        return Array.IndexOf(ClothingNames.Components, id) is >= 0 and var c ? (false, c) : null;
    }

    /// <summary>The slot the words of a name suggest (<c>black_hoodie.ydd</c> → jbib); null: none, or two different.</summary>
    public static (bool Prop, int Slot)? GuessSlot(string name)
    {
        var words = WordRe().Matches(Path.GetFileNameWithoutExtension(name).ToLowerInvariant()).Select(m => m.Value).ToHashSet();
        var hits = SlotWords.Where(s => s.Words.Any(words.Contains)).Select(s => (s.Prop, s.Slot)).Distinct().ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    // ================================================================ reading the folder

    public static ClothingSource Read(string folder)
    {
        var src = new ClothingSource { Folder = folder };
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                             .Select(f => (Full: f, Rel: Path.GetRelativePath(folder, f).Replace('\\', '/')))
                             .OrderBy(f => f.Rel.Count(c => c == '/')).ThenBy(f => f.Rel, PathUtil.PathOrder).ToList();
        // a file's context: its folder and FiveM prefix — the files of one drawable share it
        var parsed = new List<(string Full, string Rel, string Context, ClothingPart Part, bool? Female)>();
        var loose = new List<(string Full, string Rel, string Dir, string Name)>();
        foreach (var (full, rel) in files)
        {
            var fileName = Path.GetFileName(full);
            var name = fileName.Split('^')[^1];
            var ext = PathUtil.SuffixLower(name);
            if (DlcComposer.IsManifest(name))
            {
                src.IsFiveM = true;
                continue;
            }
            if (ext == ".rpf")
            {
                src.PrebuiltRpf ??= full;
                continue;
            }
            if (ext == ".ymt" || name.EndsWith("_shop.meta", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("shop_", StringComparison.OrdinalIgnoreCase) && ext == ".meta")
            {
                src.Ignored.Add(rel);
                continue;
            }
            if (!ModelExts.Contains(ext)) continue;
            var dir = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
            var prefix = fileName.Contains('^') ? fileName[..fileName.LastIndexOf('^')] : "";
            if (ResourceEditions.EditionOf(ext, DlcComposer.ResourceVersion(full)) is { } ed) src.Editions.Add(ed);
            if (ClothingNames.Parse(name) is { } part)
                parsed.Add((full, rel, (dir + "|" + prefix).ToLowerInvariant(), part, GenderOf(src, dir + " " + prefix, rel)));
            else loose.Add((full, rel, dir, name));
        }

        // drawables named as the game's: one item per context and number, textures / alternatives / cloth to it
        var byKey = new Dictionary<(string Context, bool Prop, int Slot, int Number), ClothingItem>();
        foreach (var f in parsed.Where(p => p.Part.Kind == ClothingPartKind.Drawable && p.Part.Alternative == 0))
        {
            var k = (f.Context, f.Part.Prop, f.Part.Slot, f.Part.Number);
            if (byKey.TryGetValue(k, out var had))
            {
                src.Warnings.Add(L.T($"{f.Part.Name} is in the folder twice — {had.Key} is used, {f.Rel} is left out."));
                continue;
            }
            var item = new ClothingItem { Key = f.Rel, File = f.Full, Name = f.Part.Name, Part = f.Part, Female = f.Female, Slot = (f.Part.Prop, f.Part.Slot) };
            byKey[k] = item;
            src.Items.Add(item);
        }
        foreach (var f in parsed.Where(p => p.Part.Kind != ClothingPartKind.Drawable || p.Part.Alternative > 0)
                                .OrderBy(p => p.Part.Letter).ThenBy(p => p.Part.Race).ThenBy(p => p.Part.Alternative))
        {
            var item = byKey.GetValueOrDefault((f.Context, f.Part.Prop, f.Part.Slot, f.Part.Number));
            if (item is null)
            {
                // in another folder than its model: the one model of that number (of the same ped, if it says)
                var others = byKey.Where(kv => kv.Key.Prop == f.Part.Prop && kv.Key.Slot == f.Part.Slot && kv.Key.Number == f.Part.Number &&
                                               (f.Female is null || kv.Value.Female is null || kv.Value.Female == f.Female))
                                  .Select(kv => kv.Value).ToList();
                if (others.Count == 1) item = others[0];
            }
            if (item is null)
            {
                src.Warnings.Add(L.T($"{f.Rel}: no model of {f.Part.Describe()} to go with it — left out."));
                continue;
            }
            switch (f.Part.Kind)
            {
                case ClothingPartKind.Texture:
                    if (item.Textures.Any(t => ClothingNames.Parse(Path.GetFileName(t.File).Split('^')[^1]) is { } had && had.Letter == f.Part.Letter))
                        src.Warnings.Add(L.T($"{f.Rel}: {item.Name} has a texture {f.Part.Letter} already — left out."));
                    else item.Textures.Add((f.Full, f.Part.Race));
                    break;
                case ClothingPartKind.Cloth:
                    item.Cloth ??= f.Full;
                    break;
                default:
                    item.Alternatives.Add(f.Full);
                    break;
            }
        }

        // models of any other name: textures of the same name beside them (hoodie.ytd, hoodie_a.ytd, hoodie_black.ytd)
        var usedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in loose.Where(l => PathUtil.SuffixLower(l.Name) == ".ydd"))
        {
            var stem = Path.GetFileNameWithoutExtension(m.Name);
            var item = new ClothingItem
            {
                Key = m.Rel, File = m.Full, Name = m.Name, Female = GenderOf(src, m.Dir, m.Rel), Slot = GuessSlot(m.Name),
            };
            item.SlotGuessed = item.Slot is not null;
            bool Mine(string n) => Path.GetFileNameWithoutExtension(n) is var s &&
                                   (s.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                                    s.Length > stem.Length && s.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && s[stem.Length] is '_' or '-' or ' ' or '.');
            foreach (var t in loose.Where(l => l.Dir == m.Dir && PathUtil.SuffixLower(l.Name) == ".ytd" && Mine(l.Name))
                                   .OrderBy(l => l.Name, PathUtil.PathOrder))
                if (usedTextures.Add(t.Full)) item.Textures.Add((t.Full, 0));
            item.Cloth = loose.FirstOrDefault(l => l.Dir == m.Dir && PathUtil.SuffixLower(l.Name) == ".yld" && Mine(l.Name)).Full;
            src.Items.Add(item);
        }
        foreach (var t in loose.Where(l => PathUtil.SuffixLower(l.Name) == ".ytd" && !usedTextures.Contains(l.Full)))
            src.Warnings.Add(L.T($"{t.Rel}: no model of its name beside it — left out (name it after the model: hoodie.ydd → hoodie_a.ytd)."));

        foreach (var i in src.Items)
            if (i.Textures.Count > MaxTextures)
            {
                src.Warnings.Add(L.T($"{i.Name} has {i.Textures.Count} textures — a model takes {MaxTextures} (a…z) at most: the rest are left out."));
                i.Textures.RemoveRange(MaxTextures, i.Textures.Count - MaxTextures);
            }
        if (src.Editions.Count > 1)
            src.Warnings.Add(L.T("The models are a mix of Legacy and Enhanced files — each is converted for the edition the add-on is built for."));
        return src;
    }

    /// <summary>Which MP ped a folder / FiveM prefix names (null: neither); a story character's clothes get a word.</summary>
    private static bool? GenderOf(ClothingSource src, string text, string rel)
    {
        if (ClothingNames.WearerIn(text.Replace('/', ' ')) is not { } w) return null;
        if (w.IsMp) return w.IsFemale;
        var note = L.T($"The folder names {w.Label} — these are clothes of a story character; the MP peds’ bodies differ, so they may not fit.");
        if (!src.Warnings.Contains(note)) src.Warnings.Add(note);
        return null;
    }

    // ================================================================ plan

    /// <summary>A name as the game's are spelt; an <c>mp_m_</c> / <c>mp_f_</c> in front is the collection's, not the name's.</summary>
    public static string? CleanName(string? raw)
    {
        var s = VehicleBuilder.CleanModel(raw);
        if (s is null) return null;
        foreach (var p in new[] { "mp_m_", "mp_f_", "mp_" })
            if (s.StartsWith(p, StringComparison.Ordinal) && s.Length > p.Length) return s[p.Length..];
        return s;
    }

    /// <summary>Where a model goes: set by hand, else what its name and folder say (the ped: the default one if they don't).</summary>
    public static ClothingSlot? SlotOf(ClothingItem item, ClothingBuildOptions o) =>
        o.Assign.TryGetValue(item.Key, out var set) ? set
        : item.Slot is { } s ? new ClothingSlot(item.Female ?? o.DefaultFemale, s.Prop, s.Slot) : null;

    /// <summary>Every model's slot and number, and what stops the build (Error): no model, no slot, a name the game has, too many.</summary>
    public static (ClothingPlan? Plan, string? Error) Plan(ClothingSource src, ClothingBuildOptions o, GameModels game)
    {
        if (src.PrebuiltRpf is not null)
            return (null, L.T($"The folder already has a finished pack ({Path.GetFileName(src.PrebuiltRpf)}) — there is nothing to build."));
        if (src.Items.Count == 0) return (null, L.T("There is no clothing model (.ydd) in the folder."));
        if (o.Edition == GameEdition.Legacy && src.Editions.Contains(GameEdition.Enhanced))
            return (null, L.T("The models are Enhanced files — GTA V Legacy can’t load them. Build the add-on for Enhanced."));
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(src.Folder)));
        var name = CleanName(o.Name) ?? CleanName(folderName) ?? "clothes";
        if (name.Length > 24) return (null, L.T("The name is too long — 24 characters at most."));

        var unset = src.Items.Where(i => SlotOf(i, o) is null).ToList();
        if (unset.Count > 0)
            return (null, L.T($"Pick the slot of {string.Join(", ", unset.Take(4).Select(i => i.Name))}{(unset.Count > 4 ? ", …" : "")} — its name doesn’t say where it goes."));

        var entries = new List<ClothingEntry>();
        foreach (var g in src.Items.Select(i => (Item: i, Slot: SlotOf(i, o)!)).GroupBy(x => x.Slot)
                               .OrderBy(g => g.Key.Female).ThenBy(g => g.Key.Prop).ThenBy(g => g.Key.Slot))
        {
            var list = g.OrderBy(x => x.Item.Number).ThenBy(x => x.Item.Key, PathUtil.PathOrder).ToList();
            int max = g.Key.Prop ? ReplacementHandler.MaxPropsPerAnchor : ReplacementHandler.MaxPerType;
            if (list.Count > max)
                return (null, L.T($"{list.Count} models for {g.Key.Label} of {ClothingNames.PedLabel(g.Key.Ped)} — a collection takes {max} at most."));
            for (int n = 0; n < list.Count; n++) entries.Add(new ClothingEntry(list[n].Item, g.Key, n));
        }
        var plan = new ClothingPlan(name, entries);
        foreach (var (female, coll) in plan.Collections)
        {
            var ped = female ? ClothingNames.MpFemale : ClothingNames.MpMale;
            if (game.Has($"{ped}_{coll}"))
                return (null, L.T($"The game has a collection {coll} already — only one of the two would load. Pick another name."));
        }
        return (plan, null);
    }

    // ================================================================ build

    /// <summary>
    /// Build the pack into <c>OutDir/&lt;name&gt;</c>: dlc.rpf (or loose folders) and manifest.json listing where every
    /// model went. What stops it is an <see cref="IntakeException"/>.
    /// </summary>
    public static ClothingBuildResult Build(ClothingBuildOptions o, Action<string> log)
    {
        var game = GameModels.Load(o.DataDir);
        if (game.Count == 0) log("[!] " + L.T($"The list of the game’s models ({GameModels.FileName}) is missing — names aren’t checked against the game."));
        var src = Read(o.InputFolder);
        var (plan, error) = Plan(src, o, game);
        if (plan is null) throw new IntakeException(error!);
        foreach (var w in src.Warnings) log($"[!] {w}");
        if (src.Ignored.Count > 0)
            log(L.T($"    Not used (written anew for the new numbers): {string.Join(", ", src.Ignored.Select(Path.GetFileName))}"));
        log(L.T($"MP clothes {plan.Name}: {plan.Entries.Count} model(s), for {o.Edition.DisplayName()}."));

        var spec = new ComposeSpec();
        foreach (var (female, coll) in plan.Collections)
        {
            var c = new NewCollection(female ? ClothingNames.MpFemale : ClothingNames.MpMale) { DlcName = coll };
            foreach (var e in plan.Entries.Where(e => e.Slot.Female == female))
            {
                foreach (var (file, part) in PartsOf(e)) c.Parts.Add((file, part));
                log($"      {e.Item.Key} → {coll} {e.NewName} — {e.Where}, " +
                    (e.Item.Textures.Count == 0 ? L.T("no texture") : L.T($"{e.Item.Textures.Count} texture(s)")));
                if (e.Item.Textures.Count == 0)
                    log("[!] " + L.T($"{e.Item.Name} has no texture in the folder — the game shows it untextured."));
            }
            foreach (var p in c.Parts) spec.Content.Streamed.Add(p.Part.Name);
            spec.NewCollections.Add(c);
        }
        spec.Content.DataTypes.Add("SHOP_PED_APPAREL_META_FILE");

        var device = "dlc_" + plan.Name;
        var root = Path.Combine(o.OutDir, plan.Name);
        if (o.Pack && Directory.Exists(root)) PathUtil.DeleteDir(root);
        var result = DlcComposer.Compose(spec, device, root, o.Edition, log, pack: o.Pack);
        WriteManifest(root, plan, o);
        log(L.T($"Collections: {string.Join(", ", plan.Collections.Select(c => c.Collection))} — in a trainer’s wardrobe after the game’s own clothes."));
        log(L.T($"output: {root}"));
        return new ClothingBuildResult(root, o.Pack ? result : null, device, plan);
    }

    /// <summary>A model's files under the names of its new number: drawable, alternatives, cloth, textures lettered a, b, c…</summary>
    public static List<(string File, ClothingPart Part)> PartsOf(ClothingEntry e)
    {
        var list = new List<(string, ClothingPart)>();
        var s = e.Slot;
        var n = e.Number.ToString("000");
        var ur = e.Item.RaceSpecific ? "r" : "u";
        void Add(string file, string name) => list.Add((file, ClothingNames.Parse(name) ?? throw new InvalidOperationException(name)));
        Add(e.Item.File, e.NewName);
        if (!s.Prop)
        {
            for (int a = 0; a < e.Item.Alternatives.Count; a++) Add(e.Item.Alternatives[a], $"{s.Id}_{n}_{ur}_{a + 1}.ydd");
            if (e.Item.Cloth is { } cloth) Add(cloth, $"{s.Id}_{n}_{ur}.yld");
        }
        for (int t = 0; t < e.Item.Textures.Count; t++)
        {
            var (file, race) = e.Item.Textures[t];
            var letter = (char)('a' + t);
            Add(file, s.Prop ? $"{s.Id}_diff_{n}_{letter}.ytd" : $"{s.Id}_diff_{n}_{letter}_{ClothingNames.Races[race]}.ytd");
        }
        return list;
    }

    private static void WriteManifest(string root, ClothingPlan plan, ClothingBuildOptions o)
    {
        var manifest = new JsonObject
        {
            ["pack"] = plan.Name,
            ["device"] = "dlc_" + plan.Name,
            ["collections"] = new JsonArray([.. plan.Collections.Select(c => JsonValue.Create($"{(c.Female ? ClothingNames.MpFemale : ClothingNames.MpMale)}_{c.Collection}"))]),
            ["models"] = new JsonArray([.. plan.Entries.Select(e => (JsonNode)new JsonObject
            {
                ["file"] = e.Item.Key,
                ["ped"] = e.Slot.Ped,
                ["slot"] = e.Slot.Id,
                ["number"] = e.Number,
                ["textures"] = e.Item.Textures.Count,
            })]),
            ["target"] = o.Edition.TargetLabel(),
            ["packed"] = o.Pack,
            ["install"] = $"Copy the '{plan.Name}' folder (with dlc.rpf inside) to mods\\update\\x64\\dlcpacks and add " +
                          $"<Item>dlcpacks:/{plan.Name}/</Item> to dlclist.xml — or drop the folder into ModDrop V, which adds the clothes " +
                          $"as new slots of the game's last collection (the game crashes loading the MP ped when it has more collections " +
                          $"than it has room for). Pick them in a trainer's wardrobe.",
        };
        TextIo.WriteText(Path.Combine(root, "manifest.json"), TextIo.ToJson(manifest));
    }
}
