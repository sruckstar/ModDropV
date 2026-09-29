using System.Text.Json.Nodes;
using System.Xml.Linq;
using CodeWalker.GameFiles;
using Mdv.Core.Index;
using Mdv.Core.Rpf;
using Mdv.Core.Textures;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>A model / texture file of the ped and where it goes in the image archive (<c>name.yft</c>, <c>name/head_000_r.ydd</c>).</summary>
public sealed record PedFile(string Source, string Image);

/// <summary>
/// What a modder's folder holds for an add-on ped: its models — <c>name.yft</c> with its components in
/// <c>name.ydd</c> / <c>name.ytd</c> (props in <c>name_p.ydd</c>), or streamed: one file per component in a folder
/// <c>name/</c> (FiveM: <c>name^head_000_r.ydd</c>, props in <c>name_p/</c>) — its variations (<c>name.ymt</c>) and
/// the metas it wrote itself (these win over the generated ones).
/// </summary>
public sealed class PedSource
{
    public required string Folder { get; init; }
    /// <summary>The peds in it: the .yft models (not hi-lods), or the ones its own peds.meta declares.</summary>
    public List<string> Peds { get; } = [];
    public List<PedFile> Files { get; } = [];
    /// <summary>The metas the modder wrote, by their data file type (PED_METADATA_FILE…) → full path.</summary>
    public Dictionary<string, string> Metas { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Its components, props and their textures, from the file names or the names inside its dictionaries.</summary>
    public List<ClothingPart> Parts { get; } = [];
    /// <summary>A finished dlc.rpf in the folder (it's already an add-on).</summary>
    public string? PrebuiltRpf { get; set; }
    /// <summary>The folder is a FiveM resource (fxmanifest.lua / __resource.lua).</summary>
    public bool IsFiveM { get; set; }
    public HashSet<GameEdition> Editions { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>The one ped it's built for; null when there is none or several.</summary>
    public string? Ped => Peds.Count == 1 ? Peds[0] : null;
    public bool OwnInit => Metas.ContainsKey(PedBuilder.InitType);
    /// <summary>Its components are files of their own in a folder named after it (<c>IsStreamedGfx</c>).</summary>
    public bool Streamed { get; set; }
    public bool HasYmt => Ped is { } p && Has(p + ".ymt");
    public bool HasProps => Parts.Any(p => p.Prop && p.Kind == ClothingPartKind.Drawable);
    /// <summary>The kind its own peds.meta says (from Pedtype); null when it has none.</summary>
    public string? OwnKind { get; set; }

    /// <summary>The folder has a file of this name at the image's root (<c>name.yft</c>).</summary>
    public bool Has(string name) => Files.Any(f => f.Image.Equals(name, StringComparison.OrdinalIgnoreCase));

    public int Drawables => Parts.Count(p => !p.Prop && p.Kind == ClothingPartKind.Drawable && p.Alternative == 0);
    public int Props => Parts.Count(p => p.Prop && p.Kind == ClothingPartKind.Drawable);
}

/// <summary>An add-on ped to build: the modder's folder, the game's ped it's based on and what the form says.</summary>
public sealed class PedBuildOptions
{
    public required string InputFolder { get; init; }
    /// <summary>The folder the pack's folder is written into.</summary>
    public required string OutDir { get; init; }
    /// <summary>The game's ped whose peds.meta entry is copied (movement, voice, personality…); may be null with its own peds.meta.</summary>
    public string? BasePed { get; init; }
    /// <summary>The add-on's model name (the files are renamed to it); null: as its files are named.</summary>
    public string? ModelName { get; init; }
    /// <summary>Write the .ymt from the components even though the folder has one.</summary>
    public bool RegenerateYmt { get; init; }
    public bool Pack { get; init; } = true;
    public GameEdition Edition { get; init; } = GameEdition.Legacy;
    public string? DataDir { get; init; }
}

/// <summary>The names an add-on ped gets and how it's made up.</summary>
/// <param name="Name">spawn name, model, its folder and variations file</param>
/// <param name="Base">the game ped its peds.meta entry comes from; null: its own peds.meta</param>
/// <param name="WriteYmt">its .ymt is written from the components</param>
public sealed record PedNames(string Name, string OldName, string? Base, string Kind, bool Streamed, bool Props, bool WriteYmt);

public sealed record PedBuildResult(string Root, string? DlcRpf, string Device, PedNames Names, IReadOnlyList<string> Generated);

/// <summary>
/// Modder's add-on ped: a game ped as the template (<see cref="PedTemplates"/>) gives its peds.meta entry — the
/// name, props and streamed flag filled in, fields the game keeps as hashes only set to the kind's defaults; with no
/// .ymt one is written from the components found (<see cref="PedVariation"/>); the models, renamed if need be, go
/// into x64/peds.rpf, a streamed ped's into x64/streamedpeds.rpf. Packed by <see cref="DlcComposer"/>. A peds.meta
/// in the folder wins over the generated one.
/// </summary>
public static class PedBuilder
{
    public const string InitType = "PED_METADATA_FILE";

    // ================================================================ reading the folder

    public static PedSource Read(string folder)
    {
        var src = new PedSource { Folder = folder };
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                             .Select(f => (Full: f, Rel: Path.GetRelativePath(folder, f).Replace('\\', '/')))
                             .OrderBy(f => f.Rel.Count(c => c == '/')).ThenBy(f => f.Rel, PathUtil.PathOrder).ToList();
        var models = new List<(string Full, string Rel, string Name)>();
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
                models.Add((full, rel, name));
                if (ResourceEditions.EditionOf(ext, DlcComposer.ResourceVersion(full)) is { } ed) src.Editions.Add(ed);
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

        // the peds: its own peds.meta's, else the .yft models (a component isn't one: FiveM's name^… and files in a ped's folder)
        if (src.Metas.TryGetValue(InitType, out var init) && AddonContent.ParseXml(TextIo.DecodeUtf8Sig(File.ReadAllBytes(init), strict: false)) is { } doc)
        {
            foreach (var item in doc.Root?.Element("InitDatas")?.Elements("Item") ?? [])
                if (item.Element("Name")?.Value.Trim() is { Length: > 0 } n && !src.Peds.Contains(n, StringComparer.OrdinalIgnoreCase))
                {
                    src.Peds.Add(n.ToLowerInvariant());
                    src.OwnKind ??= KindOfType(item.Element("Pedtype")?.Value.Trim());
                }
        }
        else
            foreach (var m in models.Where(m => PathUtil.SuffixLower(m.Name) == ".yft" && !m.Name.Contains('^')))
            {
                var stem = Path.GetFileNameWithoutExtension(m.Name).ToLowerInvariant();
                var dir = m.Rel.Contains('/') ? m.Rel[..m.Rel.LastIndexOf('/')].Split('/')[^1] : "";
                if (!stem.EndsWith("_hi", StringComparison.Ordinal) && !src.Peds.Contains(stem) && !models.Any(o => o.Name.Equals(dir + ".yft", StringComparison.OrdinalIgnoreCase)))
                    src.Peds.Add(stem);
            }
        if (src.Ped is not { } ped)
        {
            foreach (var m in models) src.Files.Add(new PedFile(m.Full, m.Name.ToLowerInvariant().Replace('^', '/')));
            return src;
        }

        // where each file goes: the ped's own files at the image's root, its components in its folder
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool ownDictionary = models.Any(m => m.Name.Equals(ped + ".ydd", StringComparison.OrdinalIgnoreCase));
        foreach (var (full, rel, name) in models)
        {
            var lower = name.ToLowerInvariant();
            var dir = rel.Contains('/') ? rel[..rel.LastIndexOf('/')].Split('/')[^1].ToLowerInvariant() : "";
            string? image;
            if (lower.Contains('^')) image = lower.Replace('^', '/');                                  // FiveM: ped^head_000_r.ydd
            else if (dir == ped || dir == ped + "_p") image = $"{dir}/{lower}";                          // laid out in its folder
            else if (Path.GetFileNameWithoutExtension(lower) is var stem && (stem == ped || stem == ped + "_p" || stem == ped + "_hi"))
                image = lower;
            else if (ClothingNames.Parse(lower) is { } part && !ownDictionary)
                image = $"{(part.Prop ? ped + "_p" : ped)}/{lower}";                                    // loose components of a streamed ped
            else image = null;
            if (image is null)
            {
                src.Warnings.Add(L.T($"{rel} belongs to none of the ped’s files — left out."));
                continue;
            }
            if (seen.TryGetValue(image, out var had))
            {
                src.Warnings.Add(L.T($"{name} is in the folder twice — {had} is used, {rel} is left out."));
                continue;
            }
            seen[image] = rel;
            src.Files.Add(new PedFile(full, image));
        }
        src.Streamed = src.Files.Any(f => f.Image.StartsWith(ped + "/", StringComparison.OrdinalIgnoreCase)) && !ownDictionary;
        ReadParts(src, ped);

        if (!src.Has(ped + ".yft")) src.Warnings.Add(L.T($"There is no {ped}.yft in the folder — the ped’s model."));
        if (!ownDictionary && !src.Streamed)
            src.Warnings.Add(L.T($"There is no {ped}.ydd and no folder of components — there is nothing to show."));
        if (!src.HasYmt && src.Drawables == 0)
            src.Warnings.Add(L.T($"There is no {ped}.ymt and no component ModDrop V can name (head_000_r, uppr_000_u…) to write one from."));
        if (src.Editions.Count > 1)
            src.Warnings.Add(L.T("The models are a mix of Legacy and Enhanced files — each is converted for the edition the add-on is built for."));
        return src;
    }

    private static string? KindOfType(string? pedType) => pedType?.ToUpperInvariant() switch
    {
        null or "" => null,
        "ANIMAL" => "animal",
        var t when t.Contains("FEMALE") || t == "PROSTITUTE" => "female",
        _ => "male",
    };

    /// <summary>
    /// The components and props: a streamed ped's from its files' names, a ped with dictionaries from the names of
    /// the drawables in <c>name.ydd</c> / <c>name_p.ydd</c> and the textures in <c>name.ytd</c> / <c>name_p.ytd</c>.
    /// </summary>
    private static void ReadParts(PedSource src, string ped)
    {
        foreach (var f in src.Files)
        {
            var slash = f.Image.IndexOf('/');
            if (slash < 0) continue;
            if (ClothingNames.Parse(f.Image[(slash + 1)..]) is { } part) src.Parts.Add(part);
        }
        foreach (var stem in new[] { ped, ped + "_p" })
        {
            if (src.Files.FirstOrDefault(f => f.Image == stem + ".ydd") is { } ydd)
                foreach (var n in DrawableNames(ydd.Source, src.Warnings))
                    if (ClothingNames.Parse(n + ".ydd") is { } part) src.Parts.Add(part);
            if (src.Files.FirstOrDefault(f => f.Image == stem + ".ytd") is { } ytd)
            {
                try
                {
                    foreach (var t in Ytd.List(File.ReadAllBytes(ytd.Source), Path.GetFileName(ytd.Source)))
                        if (ClothingNames.Parse(t.Name + ".ytd") is { } part) src.Parts.Add(part);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
                {
                    src.Warnings.Add(L.T($"{Path.GetFileName(ytd.Source)}: can’t read its textures ({ex.Message})."));
                }
            }
        }
    }

    /// <summary>The drawable names a ped's dictionary holds (<c>head_000_r</c>, <c>p_head_000</c>) — from their hashes.</summary>
    public static List<string> DrawableNames(string path, List<string>? warnings = null)
    {
        var names = new List<string>();
        try
        {
            var ydd = ResourceEditions.Read(File.ReadAllBytes(path), Path.GetFileName(path), (data, entry) =>
            {
                var f = new YddFile();
                f.Load(data, entry);
                return f;
            });
            foreach (var h in ydd.DrawableDict?.Hashes ?? [])
                if (KnownDrawables.Value.TryGetValue(h, out var n)) names.Add(n);
                else warnings?.Add(L.T($"{Path.GetFileName(path)}: a drawable of no known name (hash {h:X8}) — not in the generated .ymt."));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or IndexOutOfRangeException or ArgumentException or NullReferenceException)
        {
            warnings?.Add(L.T($"{Path.GetFileName(path)}: can’t read its models ({ex.Message})."));
        }
        return names;
    }

    private static readonly Lazy<Dictionary<uint, string>> KnownDrawables = new(() =>
    {
        var map = new Dictionary<uint, string>();
        foreach (var c in ClothingNames.Components)
            for (int i = 0; i < 256; i++)
                foreach (var s in new[] { "r", "u" })
                {
                    var n = $"{c}_{i:000}_{s}";
                    map[JenkHash.GenHash(n)] = n;
                    for (int alt = 1; alt < 4; alt++) map[JenkHash.GenHash($"{n}_{alt}")] = $"{n}_{alt}";
                }
        foreach (var a in ClothingNames.Anchors)
            for (int i = 0; i < 256; i++) map[JenkHash.GenHash($"p_{a}_{i:000}")] = $"p_{a}_{i:000}";
        return map;
    });

    // ================================================================ names

    /// <summary>The names the add-on gets, with what stops the build (Error) — a name the game has, no base, several peds.</summary>
    public static (PedNames? Names, string? Error) Plan(PedSource src, PedBuildOptions o, PedTemplates lib, VanillaModels vanilla)
    {
        if (src.PrebuiltRpf is not null)
            return (null, L.T($"The folder already has a finished pack ({Path.GetFileName(src.PrebuiltRpf)}) — there is nothing to build."));
        if (src.Peds.Count == 0) return (null, L.T("There is no ped model (.yft) in the folder."));
        if (src.Peds.Count > 1)
            return (null, L.T($"The folder holds several peds ({string.Join(", ", src.Peds)}) — build them one at a time, each from its own folder."));
        var old = src.Peds[0];
        if (o.Edition == GameEdition.Legacy && src.Editions.Contains(GameEdition.Enhanced))
            return (null, L.T("The models are Enhanced files — GTA V Legacy can’t load them. Build the add-on for Enhanced."));
        var t = lib.Find(o.BasePed);
        if (t is null && !src.OwnInit) return (null, L.T("Pick the game’s ped the add-on is based on."));

        var name = VehicleBuilder.CleanModel(o.ModelName) ?? old;
        if (vanilla.IsPed(name))
            return (null, L.T($"«{name}» is the name of one of the game’s peds — give the add-on a name of its own (its files are renamed to it)."));
        if (name.Length > 32) return (null, L.T("The model name is too long — 32 characters at most."));
        if (!src.Has(old + ".yft")) return (null, L.T($"There is no {old}.yft in the folder — the ped’s model."));

        bool writeYmt = !src.HasYmt || o.RegenerateYmt;
        if (writeYmt && src.Drawables == 0)
            return (null, src.HasYmt
                ? L.T("No component ModDrop V can name (head_000_r, uppr_000_u…) — keep the folder’s own .ymt.")
                : L.T($"There is no {old}.ymt and no component ModDrop V can name (head_000_r, uppr_000_u…) to write one from."));
        var kind = src.OwnInit ? src.OwnKind ?? t?.Kind ?? "male" : t!.Kind;
        return (new PedNames(name, old, src.OwnInit ? null : t!.Name, kind, src.Streamed, src.HasProps, writeYmt), null);
    }

    /// <summary>A file of the ped under its new name: old.yft → new.yft, old_p.ydd → new_p.ydd, old/head_000_r.ydd → new/head_000_r.ydd.</summary>
    public static string Rename(string image, string oldName, string newName)
    {
        var lower = image.ToLowerInvariant();
        if (oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)) return lower;
        var first = lower.Split('/')[0];
        var stem = first.Contains('.') ? first[..first.IndexOf('.')] : first;
        if (stem != oldName && stem != oldName + "_p" && stem != oldName + "_hi") return lower;
        return newName + lower[oldName.Length..];
    }

    // ================================================================ metas

    /// <summary>
    /// Fields the game keeps as hashes only (empty in the templates) and what a ped of the kind gets instead — the
    /// values of the entries written for players' peds that come without one (<see cref="PedMeta"/>).
    /// </summary>
    private static readonly Dictionary<string, (string Male, string Female)> Defaults = new()
    {
        ["ClipDictionaryName"] = ("move_m@generic", "move_f@generic"),
        ["ExpressionSetName"] = ("expr_set_ambient_male", "expr_set_ambient_female"),
        ["ExpressionDictionaryName"] = ("null", "null"),
        ["ExpressionName"] = ("null", "null"),
        ["MovementClipSet"] = ("move_m@generic", "move_f@generic"),
        ["DefaultGestureClipSet"] = ("ANIM_GROUP_GESTURE_M_GENERIC", "ANIM_GROUP_GESTURE_F_GENERIC"),
        ["FacialClipsetGroupName"] = ("facial_clipset_group_gen_male", "facial_clipset_group_gen_female"),
        ["DefaultVisemeClipSet"] = ("ANIM_GROUP_VISEMES_M_LO", "ANIM_GROUP_VISEMES_F_LO"),
        ["SidestepClipSet"] = ("CLIP_SET_ID_INVALID", "CLIP_SET_ID_INVALID"),
        ["PoseMatcherName"] = ("Male", "Male"),
        ["PoseMatcherProneName"] = ("Male_prone", "Male_prone"),
        ["CreatureMetadataName"] = ("null", "null"),
        ["Personality"] = ("YOUNGAVERAGEWEAKMAN", "YOUNGRICHWOMAN"),
        ["PedVoiceGroup"] = ("MALE_CLUB_R2PVG", "FEMALE_CLUB_R2PVG"),
        ["VfxInfoName"] = ("VFXPEDINFO_HUMAN_GENERIC", "VFXPEDINFO_HUMAN_GENERIC"),
    };

    /// <summary>
    /// The base's peds.meta entry for the add-on: its name, props, streamed flag. A field the template has as
    /// <c>&lt;X&gt;&lt;/X&gt;</c> is a name the game keeps as a hash only — a human gets the kind's default, an animal
    /// (whose clips and sounds are its own) keeps the game's default (empty); returns the fields filled so.
    /// </summary>
    public static (string Xml, List<string> Filled) PedsMeta(PedTemplate t, PedNames n)
    {
        var item = XElement.Parse(t.Init);
        var filled = new List<string>();
        foreach (var e in item.Elements().Where(e => !e.IsEmpty && !e.HasElements && !e.HasAttributes && e.Value.Length == 0).ToList())
        {
            if (t.IsAnimal || !Defaults.TryGetValue(e.Name.LocalName, out var d)) continue;
            e.Value = t.IsFemale ? d.Female : d.Male;
            filled.Add(e.Name.LocalName);
        }
        // a list of movement clip sets names only the ones known; hashed ones are dropped
        foreach (var list in item.Elements().Where(e => e.HasElements && e.Elements("Item").Any()))
            foreach (var i in list.Elements("Item").Where(i => !i.HasElements && !i.IsEmpty && i.Value.Length == 0).ToList()) i.Remove();
        Set(item, "Name", n.Name);
        Set(item, "PropsName", n.Props ? n.Name + "_p" : "null");
        item.Element("IsStreamedGfx")?.SetAttributeValue("value", n.Streamed ? "true" : "false");
        var root = new XElement("CPedModelInfo__InitDataList",
            new XElement("residentTxd", "comp_peds_generic"),
            new XElement("residentAnims"),
            new XElement("InitDatas", item),
            new XElement("txdRelationships"),
            new XElement("multiTxdRelationships"));
        return (Xml(root), filled);
    }

    /// <summary>Its own peds.meta with the ped's entry under the new name (and its props and streamed flag as the folder has them).</summary>
    public static string PatchPedsMeta(string text, PedNames n)
    {
        if (AddonContent.ParseXml(text)?.Root is not { } root) return text;
        foreach (var item in root.Element("InitDatas")?.Elements("Item") ?? [])
        {
            if (!n.OldName.Equals(item.Element("Name")?.Value.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            Set(item, "Name", n.Name);
            if (item.Element("PropsName")?.Value.Trim() is { } props && !props.Equals("null", StringComparison.OrdinalIgnoreCase)) Set(item, "PropsName", n.Name + "_p");
        }
        foreach (var e in root.Descendants().Where(e => !e.HasElements && e.Value.Trim().Equals(n.OldName, StringComparison.OrdinalIgnoreCase)))
            e.Value = n.Name;
        return Xml(root);
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

    /// <summary>
    /// The ped's variations written from its parts: every component's drawables numbered as the files are (a number
    /// with no model is kept as an empty one, so the rest keep theirs), textures by letter with their race, props by
    /// anchor. Returns the gaps filled so.
    /// </summary>
    public static (XDocument Ymt, List<string> Gaps) Ymt(IReadOnlyCollection<ClothingPart> parts)
    {
        var doc = PedVariation.ForPed();
        var gaps = new List<string>();
        var drawables = PedVariation.DrawablesOf(parts);
        var list = new List<NewDrawable>();
        foreach (var g in drawables.GroupBy(d => (d.Drawable.Prop, d.Drawable.Slot)))
        {
            int next = 0;
            foreach (var (number, d) in g.OrderBy(x => x.Number))
            {
                for (; next < number; next++)
                {
                    list.Add(new NewDrawable(d.Prop, d.Slot, false, [0]));
                    gaps.Add(d.Prop ? $"p_{ClothingNames.Anchors[d.Slot]}_{next:000}" : $"{ClothingNames.Components[d.Slot]}_{next:000}");
                }
                list.Add(d);
                next = number + 1;
            }
        }
        PedVariation.Add(doc, list);
        return (doc, gaps);
    }

    // ================================================================ build

    /// <summary>
    /// Build the add-on into <c>OutDir/&lt;name&gt;</c>: dlc.rpf (or loose folders) and manifest.json saying how to install it.
    /// What stops it (no base, a name the game has…) is an <see cref="IntakeException"/>.
    /// </summary>
    public static PedBuildResult Build(PedBuildOptions o, Action<string> log)
    {
        var lib = PedTemplates.Load(o.DataDir);
        if (lib.All.Count == 0) throw new IntakeException(L.T($"The ped templates ({PedTemplates.FileName}) are missing from ModDrop V’s data folder."));
        var vanilla = VanillaModels.Load(o.DataDir);
        var src = Read(o.InputFolder);
        var (names, error) = Plan(src, o, lib, vanilla);
        if (names is null) throw new IntakeException(error!);
        var t = lib.Find(names.Base);
        foreach (var w in src.Warnings) log($"[!] {w}");
        log(t is not null
            ? L.T($"Ped {names.Name} on the base of {t.Name} ({t.KindName}), for {o.Edition.DisplayName()}.")
            : L.T($"Ped {names.Name} with its own peds.meta, for {o.Edition.DisplayName()}."));
        if (!names.OldName.Equals(names.Name, StringComparison.OrdinalIgnoreCase))
            log(L.T($"    Files renamed: {names.OldName}* → {names.Name}*"));

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

            foreach (var (type, path) in src.Metas)
            {
                var name = Path.GetFileName(path);
                var origin = Path.GetRelativePath(o.InputFolder, path).Replace('\\', '/');
                if (type == InitType)
                {
                    var patched = Path.Combine(Directory.CreateDirectory(Path.Combine(work, "own")).FullName, name);
                    TextIo.WriteText(patched, PatchPedsMeta(TextIo.DecodeUtf8Sig(File.ReadAllBytes(path), strict: false), names));
                    Add(patched, origin, name);
                    log(L.T($"    {name}: the folder’s own ({type}) — with the name from the form."));
                    continue;
                }
                Add(path, origin, name);
                log(L.T($"    {name}: the folder’s own ({type}) — used as it is."));
            }
            if (t is not null)
            {
                var (xml, filled) = PedsMeta(t, names);
                Generate("peds.meta", xml);
                log(L.T($"    peds.meta written from {t.Name} ({(names.Streamed ? L.T("streamed") : L.T("components in its dictionary"))}" +
                        $"{(names.Props ? L.T(", with props") : "")})."));
                if (filled.Count > 0)
                    log(L.T($"    {string.Join(", ", filled)}: the game names them for {t.Name} by hash only — the {t.KindName} defaults are written."));
            }

            foreach (var f in src.Files)
            {
                if (names.WriteYmt && f.Image.Equals(names.OldName + ".ymt", StringComparison.OrdinalIgnoreCase)) continue;
                var image = Rename(f.Image, names.OldName, names.Name);
                Add(f.Source, Path.GetRelativePath(o.InputFolder, f.Source).Replace('\\', '/'), image.Replace('/', '^'));
            }
            if (names.WriteYmt)
            {
                var (ymt, gaps) = Ymt(src.Parts);
                var path = Path.Combine(work, names.Name + ".ymt");
                File.WriteAllBytes(path, PedVariation.Write(ymt));
                Add(path, names.Name + ".ymt", names.Name + ".ymt");
                generated.Add(names.Name + ".ymt");
                var (comps, props) = PedVariation.Counts(ymt);
                log(L.T($"    {names.Name}.ymt written: {comps.Sum()} drawable(s) in {comps.Count(c => c > 0)} component(s)" +
                        $"{(props.Sum() > 0 ? L.T($", {props.Sum()} prop(s)") : "")}."));
                if (gaps.Count > 0)
                    log("[!] " + L.T($"No model for {string.Join(", ", gaps.Take(6))}{(gaps.Count > 6 ? ", …" : "")} — left empty in the .ymt, " +
                                     $"so the numbers after them stay right."));
            }

            var spec = DlcComposer.FromDrop(drop) ?? throw new IntakeException(L.T("Nothing to pack — the folder has no ped."));
            foreach (var w in spec.Warnings) log($"[!] {w}");
            var device = "dlc_" + names.Name;
            var root = Path.Combine(o.OutDir, names.Name);
            if (o.Pack && Directory.Exists(root)) PathUtil.DeleteDir(root);
            var result = DlcComposer.Compose(spec, device, root, o.Edition, log, pack: o.Pack);
            WriteReadme(root, names, o, generated);
            log(L.T($"output: {root}"));
            return new PedBuildResult(root, o.Pack ? result : null, device, names, generated);
        }
        finally
        {
            PathUtil.TryDeleteDir(work);
        }
    }

    private static void WriteReadme(string root, PedNames n, PedBuildOptions o, List<string> generated)
    {
        var manifest = new JsonObject
        {
            ["ped"] = n.Name,
            ["base"] = n.Base,
            ["kind"] = n.Kind,
            ["device"] = "dlc_" + n.Name,
            ["streamed"] = n.Streamed,
            ["props"] = n.Props,
            ["target"] = o.Edition.TargetLabel(),
            ["packed"] = o.Pack,
            ["generated"] = new JsonArray([.. generated.Select(g => JsonValue.Create(g))]),
            ["install"] = $"Copy the '{n.Name}' folder (with dlc.rpf inside) to mods\\update\\x64\\dlcpacks and add " +
                          $"<Item>dlcpacks:/{n.Name}/</Item> to dlclist.xml — or drop the folder into ModDrop V. Spawn name: {n.Name}.",
        };
        TextIo.WriteText(Path.Combine(root, "manifest.json"), TextIo.ToJson(manifest));
    }
}
