using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mdv.Core.Index;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// What a modder's folder holds for an add-on vehicle: its models (<c>name.yft</c>, <c>name_hi.yft</c>,
/// <c>name.ytd</c>, <c>name+hi.ytd</c>, tuning parts <c>name_*.yft</c>), the metas they wrote themselves (these
/// win over the generated ones) and custom sounds.
/// </summary>
public sealed class VehicleSource
{
    public required string Folder { get; init; }
    /// <summary>The vehicles in it: the main .yft models (not hi-lods, not parts of another), or the ones its own vehicles.meta declares.</summary>
    public List<string> Models { get; } = [];
    /// <summary>Everything that goes into the pack's vehicles.rpf (full paths).</summary>
    public List<string> Files { get; } = [];
    /// <summary>The vehicle's other models (<c>adder_wing_1</c>, <c>adder_livery1</c>…) — tuning parts and liveries.</summary>
    public List<string> Parts { get; } = [];
    /// <summary>The metas the modder wrote, by their data file type (VEHICLE_METADATA_FILE…) → full path.</summary>
    public Dictionary<string, string> Metas { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Sounds (.awc banks, .rel data) — full paths.</summary>
    public List<string> Audio { get; } = [];
    /// <summary>A finished dlc.rpf in the folder (it's already an add-on).</summary>
    public string? PrebuiltRpf { get; set; }
    /// <summary>The folder is a FiveM resource (fxmanifest.lua / __resource.lua).</summary>
    public bool IsFiveM { get; set; }
    public HashSet<GameEdition> Editions { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>The one vehicle it's built for; null when there is none or several.</summary>
    public string? Model => Models.Count == 1 ? Models[0] : null;
    public bool OwnInit => Metas.ContainsKey(VehicleBuilder.InitType);
    public bool OwnHandling => Metas.ContainsKey(VehicleBuilder.HandlingType);
    public bool OwnVariation => Metas.ContainsKey(VehicleBuilder.VariationType);
    public bool OwnCarcols => Metas.ContainsKey(VehicleBuilder.CarcolsType);

    /// <summary>The handling its own vehicles.meta names for the vehicle (then a generated handling.meta gets this name).</summary>
    public string? OwnHandlingId { get; set; }
    /// <summary>What its own vehicles.meta says: the name label, the make label, the sound, the class (null: none there).</summary>
    public string? OwnGameName { get; set; }
    public string? OwnMakeKey { get; set; }
    public string? OwnSound { get; set; }
    public string? OwnClass { get; set; }
    /// <summary>Texts its FiveM scripts give labels (<c>AddTextEntry('f450ambo', 'Ford F-450 Ambulance')</c>): label → text.</summary>
    public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder has a model file of this name (<c>adder</c> + <c>_hi.yft</c>).</summary>
    public bool Has(string name) => Files.Any(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A vehicle add-on to build: the modder's folder, the game's vehicle it's based on and what the form says.</summary>
public sealed class VehicleBuildOptions
{
    public required string InputFolder { get; init; }
    /// <summary>The folder the pack's folder is written into.</summary>
    public required string OutDir { get; init; }
    /// <summary>The game's vehicle the metas are copied from (handling, layout, cameras, class, sound…).</summary>
    public required string BaseModel { get; init; }
    /// <summary>The add-on's model name (the files are renamed to it); null: as its files are named.</summary>
    public string? ModelName { get; init; }
    /// <summary>The name shown in the game ("Bugatti Chiron"); null: the base's.</summary>
    public string? DisplayName { get; init; }
    /// <summary>The make shown with it; null: the base's.</summary>
    public string? Make { get; init; }
    /// <summary>VC_SUPER…; null: the base's.</summary>
    public string? VehicleClass { get; init; }
    /// <summary>The game vehicle whose engine sound it uses; null: the base's.</summary>
    public string? Sound { get; init; }
    /// <summary>Give it a modkit (engine / brakes / gearbox / armour upgrades in the tuning shops).</summary>
    public bool Modkit { get; init; } = true;
    /// <summary>The modkit id; null: a free one from its name.</summary>
    public int? KitId { get; init; }
    public bool Pack { get; init; } = true;
    public GameEdition Edition { get; init; } = GameEdition.Legacy;
    public string? DataDir { get; init; }
}

/// <summary>The names an add-on vehicle gets — its identity in the game.</summary>
/// <param name="Model">spawn name, model and texture dictionary</param>
/// <param name="GameName">its name label (≤ 11 characters, as the game keeps it)</param>
/// <param name="MakeKey">the make label; null: none</param>
/// <param name="MakeText">the make's text when it's a label of our own; null: the game's label</param>
/// <param name="Sound">vehicles.meta audioNameHash — the game vehicle it sounds like</param>
public sealed record VehicleNames(string Model, string OldModel, string GameName, string Handling, string? MakeKey, string? MakeText,
                                  string? KitName, int? KitId, string Sound, string? Class, string DisplayName);

public sealed record VehicleBuildResult(string Root, string? DlcRpf, string Device, VehicleNames Names, IReadOnlyList<string> Generated);

/// <summary>
/// Modder's add-on vehicle: a game vehicle as the template (<see cref="VehicleTemplates"/>) gives vehicles.meta,
/// handling.meta (a handling of its own), carvariations.meta and — for tuning — a carcols.meta modkit with a free id;
/// the name and make go into global.gxt2 in every language; the models, renamed if need be, into x64/vehicles.rpf.
/// The pack is laid out and packed by <see cref="DlcComposer"/> — the same engine that packs players' FiveM
/// resources. Metas in the folder win over the generated ones.
/// </summary>
public static partial class VehicleBuilder
{
    public const string InitType = "VEHICLE_METADATA_FILE";
    public const string HandlingType = "HANDLING_FILE";
    public const string VariationType = "VEHICLE_VARIATION_FILE";
    public const string CarcolsType = "CARCOLS_FILE";

    [GeneratedRegex(@"[^a-z0-9_]+")] private static partial Regex NonNameRe();
    [GeneratedRegex(@"^hash_[0-9A-Fa-f]{8}$")] private static partial Regex HashRe();
    [GeneratedRegex(@"AddTextEntry\s*\(\s*(?:GetHashKey\s*\(\s*)?[""']([^""']+)[""']\s*\)?\s*,\s*[""']([^""']*)[""']")] private static partial Regex TextEntryRe();

    /// <summary>A model name from anything typed: lower case latin, digits and _ ("Bugatti Chiron" → bugatti_chiron); null when nothing is left.</summary>
    public static string? CleanModel(string? raw)
    {
        if (raw is null) return null;
        var s = NonNameRe().Replace(raw.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_'), "").Trim('_');
        return s.Length == 0 ? null : s;
    }

    // ================================================================ reading the folder

    public static VehicleSource Read(string folder)
    {
        var src = new VehicleSource { Folder = folder };
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                             .Select(f => (Full: f, Rel: Path.GetRelativePath(folder, f).Replace('\\', '/')))
                             .OrderBy(f => f.Rel.Count(c => c == '/')).ThenBy(f => f.Rel, PathUtil.PathOrder).ToList();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (full, rel) in files)
        {
            var name = Path.GetFileName(full);
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
            if (GameIndex.StreamedExts.Contains(ext))
            {
                if (seen.TryGetValue(name, out var had))
                {
                    src.Warnings.Add(L.T($"{name} is in the folder twice — {had} is used, {rel} is left out."));
                    continue;
                }
                seen[name] = rel;
                src.Files.Add(full);
                if (ResourceEditions.EditionOf(ext, DlcComposer.ResourceVersion(full)) is { } ed) src.Editions.Add(ed);
                continue;
            }
            if (ext is ".awc" or ".rel")
            {
                src.Audio.Add(full);
                continue;
            }
            if (ext == ".lua" && new FileInfo(full).Length < (1 << 20))
            {
                foreach (Match m in TextEntryRe().Matches(File.ReadAllText(full)))
                    if (m.Groups[2].Value.Trim() is { Length: > 0 } text) src.Texts.TryAdd(m.Groups[1].Value, text);
                continue;
            }
            if (ext is ".meta" or ".xml" && new FileInfo(full).Length < (16 << 20))
            {
                if (name.Equals("content.xml", StringComparison.OrdinalIgnoreCase) || name.Equals("setup2.xml", StringComparison.OrdinalIgnoreCase))
                {
                    src.Warnings.Add(L.T($"{rel}: ModDrop V writes the pack’s own {name} — this one is left out."));
                    continue;
                }
                var text = TextIo.DecodeUtf8Sig(File.ReadAllBytes(full), strict: false);
                if (ModDetector.RootTag(text) is not { } root || !AddonContent.TypeByRoot.TryGetValue(root, out var type)) continue;
                if (!src.Metas.TryAdd(type, full))
                    src.Warnings.Add(L.T($"{rel}: a second {name} — left out (keep one per kind in the folder)."));
            }
        }

        if (src.Metas.TryGetValue(InitType, out var init) && AddonContent.ParseXml(TextIo.DecodeUtf8Sig(File.ReadAllBytes(init), strict: false)) is { } doc)
        {
            foreach (var item in doc.Root?.Element("InitDatas")?.Elements("Item") ?? [])
                if (item.Element("modelName")?.Value.Trim() is { Length: > 0 } m && !src.Models.Contains(m, StringComparer.OrdinalIgnoreCase))
                {
                    src.Models.Add(m.ToLowerInvariant());
                    src.OwnHandlingId ??= item.Element("handlingId")?.Value.Trim();
                    src.OwnGameName ??= Val(item, "gameName");
                    src.OwnMakeKey ??= Val(item, "vehicleMakeName");
                    src.OwnSound ??= Val(item, "audioNameHash");
                    src.OwnClass ??= Val(item, "vehicleClass");
                }
        }
        else
        {
            var stems = src.Files.Where(f => PathUtil.SuffixLower(f) == ".yft")
                           .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
                           .Where(s => !s.EndsWith("_hi", StringComparison.Ordinal)).Distinct().ToList();
            foreach (var s in stems)
                if (!stems.Any(o => o != s && s.StartsWith(o + "_", StringComparison.Ordinal))) src.Models.Add(s);
        }
        foreach (var f in src.Files.Where(f => PathUtil.SuffixLower(f) == ".yft"))
        {
            var stem = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            if (!stem.EndsWith("_hi", StringComparison.Ordinal) && !src.Models.Contains(stem) &&
                src.Models.Any(m => stem.StartsWith(m + "_", StringComparison.Ordinal)))
                src.Parts.Add(stem);
        }
        if (src.Model is { } model)
        {
            if (!src.Has(model + ".yft")) src.Warnings.Add(L.T($"There is no {model}.yft — the vehicle’s model."));
            if (!src.Has(model + ".ytd"))
                src.Warnings.Add(L.T($"There is no {model}.ytd — only the textures inside {model}.yft and the game’s shared ones are used."));
            if (!src.Has(model + "_hi.yft"))
                src.Warnings.Add(L.T($"There is no {model}_hi.yft (the close-up model) — the game shows {model}.yft up close too."));
        }
        if (src.Editions.Count > 1)
            src.Warnings.Add(L.T("The models are a mix of Legacy and Enhanced files — each is converted for the edition the add-on is built for."));
        return src;
    }

    private static string? Val(XElement item, string name) =>
        item.Element(name)?.Value.Trim() is { Length: > 0 } v && !v.Equals("null", StringComparison.OrdinalIgnoreCase) ? v : null;

    /// <summary>The make its own vehicles.meta names, as text: its scripts' or a game make's; null when there is none or it's unknown.</summary>
    public static string? OwnMakeText(VehicleSource src, VehicleTemplates lib) =>
        src.OwnMakeKey is not { } key ? null
        : src.Texts.TryGetValue(key, out var text) ? text
        : lib.All.FirstOrDefault(x => x.Make is not null && key.Equals(Tag(x.Init, "vehicleMakeName"), StringComparison.OrdinalIgnoreCase))?.Make;

    /// <summary>The name its own vehicles.meta gives it, as text: its scripts' for the name label, or a game vehicle's; null when unknown.</summary>
    public static string? OwnDisplayName(VehicleSource src, VehicleTemplates lib) =>
        src.OwnGameName is not { } key ? null
        : src.Texts.TryGetValue(key, out var text) ? text
        : lib.All.FirstOrDefault(x => key.Equals(Tag(x.Init, "gameName"), StringComparison.OrdinalIgnoreCase))?.Label;

    // ================================================================ names

    /// <summary>
    /// The names the add-on gets, with what stops the build (Error) — a model name the game has, no base, several vehicles.
    /// </summary>
    public static (VehicleNames? Names, string? Error) Plan(VehicleSource src, VehicleBuildOptions o, VehicleTemplates lib, VanillaModels vanilla)
    {
        if (src.PrebuiltRpf is not null)
            return (null, L.T($"The folder already has a finished pack ({Path.GetFileName(src.PrebuiltRpf)}) — there is nothing to build."));
        if (src.Models.Count == 0) return (null, L.T("There is no vehicle model (.yft) in the folder."));
        if (src.Models.Count > 1)
            return (null, L.T($"The folder holds several vehicles ({string.Join(", ", src.Models)}) — build them one at a time, each from its own folder."));
        var old = src.Models[0];
        if (o.Edition == GameEdition.Legacy && src.Editions.Contains(GameEdition.Enhanced))
            return (null, L.T("The models are Enhanced files — GTA V Legacy can’t load them. Build the add-on for Enhanced."));
        if (lib.Find(o.BaseModel) is not { } t) return (null, L.T("Pick the game’s vehicle the add-on is based on."));

        var model = CleanModel(o.ModelName) ?? old;
        if (vanilla.IsVehicle(model))
            return (null, L.T($"«{model}» is the name of one of the game’s vehicles — give the add-on a name of its own (its files are renamed to it)."));
        if (model.Length > 32) return (null, L.T("The model name is too long — 32 characters at most."));

        // the name label: the game keeps 11 characters; a key the game uses would rename its vehicle too.
        // Its own vehicles.meta's label stays while it's one of its own and the model keeps its name.
        var gameName = src.OwnGameName is { Length: <= 11 } own && model == old && !lib.IsGameName(own) ? own : model.ToUpperInvariant();
        if (gameName.Length > 11) gameName = gameName[..11];
        for (int n = 2; lib.IsGameName(gameName) && n < 100; n++)
            gameName = (gameName.Length > 9 ? gameName[..9] : gameName) + n;

        var handling = src.OwnHandlingId ?? model.ToUpperInvariant();
        for (int n = 2; !src.OwnInit && lib.IsHandling(handling) && n < 100; n++) handling = model.ToUpperInvariant() + "_" + n;

        // the make: its own vehicles.meta's (or the base's) unless another is typed
        string? makeKey = src.OwnInit ? src.OwnMakeKey : Tag(t.Init, "vehicleMakeName");
        if (makeKey is { } mk && mk.Equals("null", StringComparison.OrdinalIgnoreCase)) makeKey = null;
        var knownMake = src.OwnInit ? OwnMakeText(src, lib) : t.Make;
        string? makeText = null;
        var make = o.Make?.Trim();
        if (!string.IsNullOrEmpty(make) && !make.Equals(knownMake, StringComparison.OrdinalIgnoreCase))
        {
            makeKey = "MK_" + (gameName.Length > 8 ? gameName[..8] : gameName);
            makeText = make;
        }
        else if (o.Make is not null && make?.Length == 0) makeKey = null;           // cleared: no make shown

        bool kit = o.Modkit && !src.OwnVariation && !src.OwnCarcols;
        int? kitId = kit ? o.KitId ?? FreeKitId(model, vanilla) : null;
        var sound = lib.Find(o.Sound)?.Sound ?? (src.OwnInit ? src.OwnSound : null) ?? t.Sound;
        var display = !string.IsNullOrWhiteSpace(o.DisplayName) ? o.DisplayName.Trim()
                      : (src.OwnInit ? OwnDisplayName(src, lib) : null) ?? t.Label ?? model;
        return (new VehicleNames(model, old, gameName, handling, makeKey, makeText, kit ? $"{kitId}_{model}_modkit" : null, kitId,
                                 sound, o.VehicleClass ?? (src.OwnInit ? src.OwnClass : null) ?? t.Class, display), null);
    }

    /// <summary>A modkit id the game doesn't use, the same for the same name (≥ 1000; installed add-ons are checked on install).</summary>
    public static int FreeKitId(string model, VanillaModels vanilla)
    {
        int id = 1000 + (int)(Gxt2.Joaat(model) % 9000);
        while (vanilla.IsKit(id)) id++;
        return id;
    }

    private static string? Tag(string xml, string name) =>
        Regex.Match(xml, $@"<{name}>\s*([^<]*?)\s*</{name}>") is { Success: true, Groups: var g } && g[1].Value.Length > 0 ? g[1].Value : null;

    /// <summary>A file of the vehicle under its new name: adder+hi.ytd → mycar+hi.ytd, adder_wing_1.yft → mycar_wing_1.yft.</summary>
    public static string Rename(string file, string oldModel, string newModel)
    {
        var name = file.ToLowerInvariant();
        if (oldModel.Equals(newModel, StringComparison.OrdinalIgnoreCase) || !name.StartsWith(oldModel.ToLowerInvariant(), StringComparison.Ordinal))
            return name;
        var rest = name[oldModel.Length..];
        return rest.Length > 0 && rest[0] is '.' or '_' or '+' ? newModel + rest : name;
    }

    // ================================================================ metas

    public static string VehiclesMeta(VehicleTemplate t, VehicleNames n)
    {
        var item = XElement.Parse(t.Init);
        Set(item, "modelName", n.Model);
        Set(item, "txdName", n.Model);
        Set(item, "handlingId", n.Handling);
        Set(item, "gameName", n.GameName);
        Set(item, "vehicleMakeName", n.MakeKey ?? "");
        Set(item, "audioNameHash", n.Sound);
        if (n.Class is { } cls) Set(item, "vehicleClass", cls);
        var root = new XElement("CVehicleModelInfo__InitDataList",
            new XElement("residentTxd", "vehshare"),
            new XElement("residentAnims"),
            new XElement("InitDatas", item),
            new XElement("txdRelationships",
                t.TxdParent is { } p ? new XElement("Item", new XElement("parent", p), new XElement("child", n.Model)) : null));
        return Xml(root);
    }

    /// <summary>
    /// Its own vehicles.meta with what the form says: the vehicle's entry gets the new model name, name label, make,
    /// sound and class; the rest of it stays as the modder wrote it.
    /// </summary>
    public static string PatchVehiclesMeta(string text, VehicleNames n)
    {
        if (AddonContent.ParseXml(text)?.Root is not { } root) return text;
        foreach (var item in root.Element("InitDatas")?.Elements("Item") ?? [])
        {
            if (!n.OldModel.Equals(item.Element("modelName")?.Value.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            Set(item, "modelName", n.Model);
            if (n.OldModel.Equals(item.Element("txdName")?.Value.Trim(), StringComparison.OrdinalIgnoreCase)) Set(item, "txdName", n.Model);
            Set(item, "gameName", n.GameName);
            Set(item, "vehicleMakeName", n.MakeKey ?? "");
            Set(item, "audioNameHash", n.Sound);
            if (n.Class is { } cls) Set(item, "vehicleClass", cls);
        }
        foreach (var e in root.Element("txdRelationships")?.Elements("Item").SelectMany(i => i.Elements()) ?? [])
            if (e.Value.Trim().Equals(n.OldModel, StringComparison.OrdinalIgnoreCase)) e.Value = n.Model;
        return Xml(root);
    }

    /// <summary>Its own carvariations.meta with the vehicle's entry under the new model name.</summary>
    public static string PatchVariationsMeta(string text, VehicleNames n)
    {
        if (AddonContent.ParseXml(text)?.Root is not { } root) return text;
        foreach (var e in root.Descendants("modelName"))
            if (e.Value.Trim().Equals(n.OldModel, StringComparison.OrdinalIgnoreCase)) e.Value = n.Model;
        return Xml(root);
    }

    public static string HandlingMeta(VehicleTemplate t, VehicleNames n)
    {
        var item = XElement.Parse(t.Handling);
        Set(item, "handlingName", n.Handling);
        return Xml(new XElement("CHandlingDataMgr", new XElement("HandlingData", item)));
    }

    public static string VariationsMeta(VehicleTemplate t, VehicleNames n)
    {
        var item = t.Variation is { } v ? XElement.Parse(v) : new XElement("Item",
            new XElement("modelName"),
            new XElement("colors", new XElement("Item",
                new XElement("indices", new XAttribute("content", "char_array"), "0 0 0 156"),
                new XElement("liveries", Enumerable.Range(0, 8).Select(_ => new XElement("Item", new XAttribute("value", "false")))))),
            new XElement("kits"),
            new XElement("windowsWithExposedEdges"),
            new XElement("plateProbabilities", new XElement("Probabilities")),
            new XElement("lightSettings", new XAttribute("value", "1")),
            new XElement("sirenSettings", new XAttribute("value", "0")));
        Set(item, "modelName", n.Model);
        if (item.Element("kits") is not { } kits) item.Add(kits = new XElement("kits"));
        kits.RemoveNodes();
        kits.Add(new XElement("Item", n.KitName ?? "0_default_modkit"));
        // plates the base game names by hash only: its own are left to the game's default
        foreach (var plate in item.Descendants("Name").Where(e => HashRe().IsMatch(e.Value.Trim())).ToList())
            plate.Parent?.Remove();
        return Xml(new XElement("CVehicleModelInfoVariation", new XElement("variationData", item)));
    }

    /// <summary>The modkit: the base's upgrades (engine, brakes, gearbox, armour, horns), no visible parts.</summary>
    public static string CarcolsMeta(VehicleTemplate t, VehicleTemplates lib, VehicleNames n)
    {
        var kitXml = t.Kit ?? lib.Find("adder")?.Kit ?? lib.All.FirstOrDefault(x => x.Kit is not null)?.Kit;
        var kit = kitXml is not null ? XElement.Parse(kitXml) : new XElement("Item",
            new XElement("kitName"), new XElement("id"), new XElement("kitType", "MKT_SPECIAL"),
            new XElement("visibleMods"), new XElement("linkMods"), new XElement("statMods"), new XElement("slotNames"),
            new XElement("liveryNames"));
        Set(kit, "kitName", n.KitName!);
        kit.Element("id")?.SetAttributeValue("value", n.KitId);
        foreach (var stat in kit.Element("statMods")?.Elements("Item").ToList() ?? [])
            if (stat.Element("identifier")?.Value.Trim() is { } id && HashRe().IsMatch(id)) stat.Remove();
        return Xml(new XElement("CVehicleModelInfoVarGlobal", new XElement("Kits", kit), new XElement("Lights")));
    }

    private static void Set(XElement item, string name, string value)
    {
        if (item.Element(name) is { } e)
        {
            e.RemoveAttributes();
            e.Value = value;
        }
        else item.Add(new XElement(name, value));
    }

    private static string Xml(XElement root) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + root.ToString() + "\n";

    // ================================================================ build

    /// <summary>
    /// Build the add-on into <c>OutDir/&lt;model&gt;</c>: dlc.rpf (or loose folders) and manifest.json saying how to install it.
    /// What stops it (no base, a name the game has…) is an <see cref="IntakeException"/>.
    /// </summary>
    public static VehicleBuildResult Build(VehicleBuildOptions o, Action<string> log)
    {
        var lib = VehicleTemplates.Load(o.DataDir);
        if (lib.All.Count == 0) throw new IntakeException(L.T($"The vehicle templates ({VehicleTemplates.FileName}) are missing from ModDrop V’s data folder."));
        var vanilla = VanillaModels.Load(o.DataDir);
        var src = Read(o.InputFolder);
        var (names, error) = Plan(src, o, lib, vanilla);
        if (names is null) throw new IntakeException(error!);
        var t = lib.Find(o.BaseModel)!;
        foreach (var w in src.Warnings) log($"[!] {w}");
        log(L.T($"Vehicle {names.Model} «{names.DisplayName}» on the base of {t.Title} ({t.Model}), for {o.Edition.DisplayName()}."));
        if (!names.OldModel.Equals(names.Model, StringComparison.OrdinalIgnoreCase))
            log(L.T($"    Files renamed: {names.OldModel}* → {names.Model}*"));

        var work = PathUtil.MakeTempDir();
        try
        {
            var drop = new DroppedSource { WorkDir = work };
            drop.Sources.Add(o.InputFolder);
            void Add(string full, string origin, string name) =>
                drop.Files.Add(new DroppedFile(full, origin, name, false, origin.Count(c => c == '/')));
            var generated = new List<string>();
            void Generate(string file, string text)
            {
                var path = Path.Combine(work, file);
                TextIo.WriteText(path, text);
                Add(path, file, file);
                generated.Add(file);
            }

            bool renamed = !names.OldModel.Equals(names.Model, StringComparison.OrdinalIgnoreCase);
            foreach (var (type, path) in src.Metas)
            {
                var name = Path.GetFileName(path);
                var origin = Path.GetRelativePath(o.InputFolder, path).Replace('\\', '/');
                Func<string, VehicleNames, string>? patch = type == InitType ? PatchVehiclesMeta
                                                          : type == VariationType && renamed ? PatchVariationsMeta : null;
                if (patch is not null)
                {
                    var patched = Path.Combine(Directory.CreateDirectory(Path.Combine(work, "own")).FullName, name);
                    TextIo.WriteText(patched, patch(TextIo.DecodeUtf8Sig(File.ReadAllBytes(path), strict: false), names));
                    Add(patched, origin, name);
                    log(L.T($"    {name}: the folder’s own ({type}) — with the names from the form."));
                    continue;
                }
                Add(path, origin, name);
                log(L.T($"    {name}: the folder’s own ({type}) — used as it is."));
            }
            if (!src.OwnInit) Generate("vehicles.meta", VehiclesMeta(t, names));
            if (!src.OwnHandling) Generate("handling.meta", HandlingMeta(t, names));
            if (!src.OwnVariation) Generate("carvariations.meta", VariationsMeta(t, names));
            if (names.KitName is not null) Generate("carcols.meta", CarcolsMeta(t, lib, names));
            if (generated.Count > 0)
                log(L.T($"    Written from {t.Model}: {string.Join(", ", generated)} (handling {names.Handling}" +
                        $"{(names.KitName is { } k ? L.T($", modkit {k}") : "")}, sound of {names.Sound})."));

            foreach (var f in src.Files)
                Add(f, Path.GetRelativePath(o.InputFolder, f).Replace('\\', '/'), Rename(Path.GetFileName(f), names.OldModel, names.Model));
            foreach (var f in src.Audio) Add(f, Path.GetRelativePath(o.InputFolder, f).Replace('\\', '/'), Path.GetFileName(f));

            var spec = DlcComposer.FromDrop(drop) ?? throw new IntakeException(L.T("Nothing to pack — the folder has no vehicle."));
            foreach (var w in spec.Warnings) log($"[!] {w}");
            // in content.xml as Rockstar's vehicle packs list them: layouts, handling, vehicles, modkits, variations, the models
            var sorted = DlcComposer.OrderVehicleData(spec.Data);
            spec.Data.Clear();
            spec.Data.AddRange(sorted);
            spec.Content.Labels[Gxt2.Joaat(names.GameName)] = names.DisplayName;
            if (names.MakeText is { } mt && names.MakeKey is { } mk) spec.Content.Labels[Gxt2.Joaat(mk)] = mt;
            else if (names.MakeKey is { } ownKey && src.Texts.TryGetValue(ownKey, out var ownMake))
                spec.Content.Labels[Gxt2.Joaat(ownKey)] = ownMake;         // its scripts' make: FiveM sets it at run time
            if (src.Parts.Count > 0 && names.KitName is not null)
            {
                var parts = string.Join(", ", src.Parts.Take(6)) + (src.Parts.Count > 6 ? ", …" : "");
                log("[!] " + L.T($"{parts}: in the pack, but the generated modkit lists no parts — put your own carcols.meta in the folder " +
                                 $"to have them in the tuning shops."));
            }

            var device = "dlc_" + names.Model;
            var root = Path.Combine(o.OutDir, names.Model);
            if (o.Pack && Directory.Exists(root)) PathUtil.DeleteDir(root);
            var result = DlcComposer.Compose(spec, device, root, o.Edition, log, pack: o.Pack);
            WriteReadme(root, names, t, o, generated);
            log(L.T($"output: {root}"));
            return new VehicleBuildResult(root, o.Pack ? result : null, device, names, generated);
        }
        finally
        {
            PathUtil.TryDeleteDir(work);
        }
    }

    private static void WriteReadme(string root, VehicleNames n, VehicleTemplate t, VehicleBuildOptions o, List<string> generated)
    {
        var manifest = new JsonObject
        {
            ["model"] = n.Model,
            ["name"] = n.DisplayName,
            ["base"] = t.Model,
            ["device"] = "dlc_" + n.Model,
            ["handling"] = n.Handling,
            ["gameName"] = n.GameName,
            ["modkit"] = n.KitId,
            ["sound"] = n.Sound,
            ["class"] = n.Class,
            ["target"] = o.Edition.TargetLabel(),
            ["packed"] = o.Pack,
            ["generated"] = new JsonArray([.. generated.Select(g => JsonValue.Create(g))]),
            ["install"] = $"Copy the '{n.Model}' folder (with dlc.rpf inside) to mods\\update\\x64\\dlcpacks and add " +
                          $"<Item>dlcpacks:/{n.Model}/</Item> to dlclist.xml — or drop the folder into ModDrop V. Spawn name: {n.Model}.",
        };
        TextIo.WriteText(Path.Combine(root, "manifest.json"), TextIo.ToJson(manifest));
    }
}
