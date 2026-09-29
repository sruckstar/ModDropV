using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CodeWalker.GameFiles;
using Mdv.Core.Rpf;
using Mdv.Core.Textures;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>A prop model of the folder (<c>name.ydr</c>, or a fragment <c>name.yft</c>) and what its archetype needs from it.</summary>
/// <param name="Min">its bounding box (as the archetype's bbMin / bbMax)</param>
/// <param name="Centre">its bounding sphere (bsCentre / bsRadius)</param>
/// <param name="Collision">it carries its collision (a drawable's embedded bounds, a fragment's physics)</param>
/// <param name="OwnTextures">textures embedded in the model</param>
/// <param name="Textures">the textures it uses that it doesn't hold — from a texture dictionary (lower case)</param>
public sealed record PropModel(string Name, string File, bool Fragment, Vector3 Min, Vector3 Max, Vector3 Centre, float Radius,
                               bool Collision, int OwnTextures, IReadOnlyList<string> Textures)
{
    public Vector3 Size => Max - Min;
}

/// <summary>An archetypes file (.ytyp) of the modder's own: its archetypes are packed as it defines them.</summary>
public sealed record PropTypes(string File, string Name, XDocument Doc, IReadOnlyList<string> Archetypes);

/// <summary>
/// What a modder's folder holds for add-on props: models (.ydr, fragments .yft), texture dictionaries (.ytd),
/// collisions (.ybn), animations (.ycd) and archetypes files (.ytyp) of its own — FiveM's <c>stream/</c> too.
/// </summary>
public sealed class PropSource
{
    public required string Folder { get; init; }
    public List<PropModel> Models { get; } = [];
    /// <summary>Every file that goes into the pack, by its name (lower case) → full path.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Its texture dictionaries: name → the textures in it (lower case).</summary>
    public Dictionary<string, HashSet<string>> Dictionaries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PropTypes> OwnTypes { get; } = [];
    /// <summary>A finished dlc.rpf in the folder (it's already an add-on).</summary>
    public string? PrebuiltRpf { get; set; }
    public bool IsFiveM { get; set; }
    public HashSet<GameEdition> Editions { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>One of its own archetypes files defines the prop.</summary>
    public bool Defined(string name) => OwnTypes.Any(t => t.Archetypes.Any(a => MapMeta.Hash(a) == MapMeta.Hash(name)));

    /// <summary>The models whose archetype is written (the ones no .ytyp of its own defines).</summary>
    public IEnumerable<PropModel> NewModels => Models.Where(m => !Defined(m.Name));

    public bool Has(string name) => Files.ContainsKey(name);
}

/// <summary>Add-on props to build: the modder's folder and what the form says.</summary>
public sealed class PropBuildOptions
{
    public required string InputFolder { get; init; }
    /// <summary>The folder the pack's folder is written into.</summary>
    public required string OutDir { get; init; }
    /// <summary>The pack's name (its dlcpacks folder, dlc_&lt;name&gt; and its .ytyp); null: the folder's name.</summary>
    public string? PackName { get; init; }
    /// <summary>Put in front of every file's name (and so the props' names) — for files named like the game's.</summary>
    public string? Prefix { get; init; }
    /// <summary>The props move when hit — pushed, knocked over, falling; false: fixed where they're put.</summary>
    public bool Dynamic { get; init; } = true;
    /// <summary>How far away they're drawn, metres; null: by each prop's size, as the game's props are.</summary>
    public float? LodDist { get; init; }
    public bool Pack { get; init; } = true;
    public GameEdition Edition { get; init; } = GameEdition.Legacy;
    public string? DataDir { get; init; }
}

/// <summary>A prop's archetype: its spawn name and the dictionaries it names.</summary>
/// <param name="Model">the model it's written from; null for one of the folder's own archetypes</param>
public sealed record PropEntry(string Name, string OldName, PropModel? Model, string? Txd, string? Physics, string? Clip, float LodDist, float HdDist);

/// <param name="Pack">dlcpacks folder and the device's name (dlc_&lt;pack&gt;)</param>
/// <param name="Types">the .ytyp written for the props (empty when every prop has an archetype of its own)</param>
/// <param name="Own">the archetypes the folder's own .ytyp files define, under the new names</param>
public sealed record PropNames(string Pack, string Types, string Prefix, bool Dynamic, IReadOnlyList<PropEntry> Props, IReadOnlyList<string> Own)
{
    /// <summary>What trainers spawn them by (Menyoo: Object Spooner → Spawn by name).</summary>
    public IEnumerable<string> SpawnNames => Props.Select(p => p.Name).Concat(Own);
}

public sealed record PropBuildResult(string Root, string? DlcRpf, string Device, PropNames Names, IReadOnlyList<string> Generated);

/// <summary>
/// Modder's add-on props: every model of the folder gets an archetype (<c>CBaseArchetypeDef</c>) in a .ytyp written
/// for the pack — its bounding box and sphere read from the model, its texture dictionary found by the textures it
/// uses, its collision (embedded, as props need it), a draw distance by its size and flags as the game's props have
/// them. The pack loads the .ytyp for good (<c>DLC_ITYP_REQUEST</c>) so trainers spawn the props by name; packed
/// by <see cref="DlcComposer"/> as players' prop packs are. A .ytyp of the folder's own wins for what it defines.
/// </summary>
public static class PropBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The files a prop pack is made of (placements are a map's, not a prop's).</summary>
    private static readonly HashSet<string> PropExts = new(StringComparer.OrdinalIgnoreCase)
        { ".ydr", ".yft", ".ydd", ".ytd", ".ybn", ".ycd", ".ypt" };

    /// <summary>Archetype flags as the game's props have them: dynamic (0x20000) or static (0x20), with ambient scale (0x20000000).</summary>
    public const uint DynamicFlags = 0x20020000, StaticFlags = 0x20000020;

    // ================================================================ reading the folder

    public static PropSource Read(string folder)
    {
        var src = new PropSource { Folder = folder };
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                             .Select(f => (Full: f, Rel: Path.GetRelativePath(folder, f).Replace('\\', '/')))
                             .OrderBy(f => InStream(f.Rel) ? 0 : 1)             // FiveM streams stream/ only: its copy wins
                             .ThenBy(f => f.Rel.Count(c => c == '/')).ThenBy(f => f.Rel, PathUtil.PathOrder).ToList();
        var types = new List<(string Full, string Rel, string Name)>();
        foreach (var (full, rel) in files)
        {
            var name = Path.GetFileName(full).Split('^')[^1].ToLowerInvariant();
            var ext = PathUtil.SuffixLower(name);
            var mapName = AddonContent.MapFileName(name);
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
            if (mapName.EndsWith(".ymap", StringComparison.Ordinal))
            {
                src.Warnings.Add(L.T($"{rel} places things on the map — a map’s, not a prop’s: left out (players install maps as they are)."));
                continue;
            }
            if (mapName.EndsWith(".ytyp", StringComparison.Ordinal))
            {
                types.Add((full, rel, mapName));
                continue;
            }
            if (!PropExts.Contains(ext)) continue;
            if (seen.TryGetValue(name, out var had))
            {
                src.Warnings.Add(L.T($"{name} is in the folder twice — {had} is used, {rel} is left out."));
                continue;
            }
            seen[name] = rel;
            src.Files[name] = full;
            if (ResourceEditions.EditionOf(ext, DlcComposer.ResourceVersion(full)) is { } ed) src.Editions.Add(ed);
        }

        // a .ytyp names its archetypes by hash: the folder's file names make them read as names the models match
        MapMeta.Know(src.Files.Keys.Select(n => n[..n.IndexOf('.')]).Concat(types.Select(t => Path.GetFileNameWithoutExtension(t.Name))));
        foreach (var (full, rel, name) in types) ReadTypes(src, full, rel, name);

        foreach (var (name, full) in src.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var ext = PathUtil.SuffixLower(name);
            var stem = Path.GetFileNameWithoutExtension(name);
            if (ext == ".ytd") ReadDictionary(src, full, name);
            if (ext is not (".ydr" or ".yft") || stem.EndsWith("_hi", StringComparison.Ordinal)) continue;
            if (src.Models.FirstOrDefault(m => m.Name == stem) is { } twin)
            {
                src.Warnings.Add(L.T($"{twin.File} and {name} are the same prop — {twin.File} is used."));
                continue;
            }
            try
            {
                src.Models.Add(ReadModel(full, name));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or IndexOutOfRangeException or ArgumentException or NullReferenceException)
            {
                src.Warnings.Add(L.T($"{name}: can’t read the model ({ex.Message}) — left out."));
            }
        }

        foreach (var m in src.NewModels)
        {
            if (!m.Collision)
                src.Warnings.Add(L.T($"{m.Name} has no collision in its model — players and cars go through it. Embed one (Sollumz, CodeWalker) if it should be solid."));
            if (m.Size.X <= 0 && m.Size.Y <= 0 && m.Size.Z <= 0)
                src.Warnings.Add(L.T($"{m.Name}: the model has no size (its bounds are empty) — the game may never draw it."));
        }
        if (src.Editions.Count > 1)
            src.Warnings.Add(L.T("The models are a mix of Legacy and Enhanced files — each is converted for the edition the add-on is built for."));
        return src;
    }

    private static bool InStream(string rel) => rel.Split('/').SkipLast(1).Any(s => s.Equals("stream", StringComparison.OrdinalIgnoreCase));

    private static void ReadTypes(PropSource src, string full, string rel, string name)
    {
        try
        {
            var doc = MapMeta.Read(File.ReadAllBytes(full), name);
            var stem = Path.GetFileNameWithoutExtension(name);
            if (src.OwnTypes.Any(t => t.Name == stem))
            {
                src.Warnings.Add(L.T($"{name} is in the folder twice — {rel} is left out."));
                return;
            }
            src.OwnTypes.Add(new PropTypes(full, stem, doc, MapMeta.Archetypes(doc)));
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            src.Warnings.Add(L.T($"{rel}: can’t read it ({ex.Message}) — left out; its props get archetypes written."));
        }
    }

    private static void ReadDictionary(PropSource src, string full, string name)
    {
        try
        {
            src.Dictionaries[Path.GetFileNameWithoutExtension(name)] =
                [.. Ytd.List(File.ReadAllBytes(full), name).Select(t => t.Name.ToLowerInvariant())];
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
        {
            src.Warnings.Add(L.T($"{name}: can’t read its textures ({ex.Message})."));
        }
    }

    /// <summary>A prop model: its bounds, collision and the textures it uses.</summary>
    public static PropModel ReadModel(string path, string name)
    {
        var raw = File.ReadAllBytes(path);
        bool fragment = PathUtil.SuffixLower(name) == ".yft";
        DrawableBase? d;
        bool collision;
        if (fragment)
        {
            var yft = ResourceEditions.Read(raw, name, (data, entry) =>
            {
                var f = new YftFile();
                f.Load(data, entry);
                return f;
            });
            d = yft.Fragment?.Drawable;
            collision = yft.Fragment?.PhysicsLODGroup?.PhysicsLOD1?.Bound is not null || yft.Fragment?.Drawable?.Bound is not null;
        }
        else
        {
            var ydr = ResourceEditions.Read(raw, name, (data, entry) =>
            {
                var f = new YdrFile();
                f.Load(data, entry);
                return f;
            });
            d = ydr.Drawable;
            collision = ydr.Drawable?.Bound is not null;
        }
        if (d is null) throw new InvalidDataException(L.T("there is no model in it"));
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in d.ShaderGroup?.TextureDictionary?.Textures?.data_items ?? [])
            if (t?.Name is { Length: > 0 } n) own.Add(n.ToLowerInvariant());
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var s in d.ShaderGroup?.Shaders?.data_items ?? [])
            foreach (var p in s?.ParametersList?.Parameters ?? [])
                if (p.Data is TextureBase { Name: { Length: > 0 } tn } && !own.Contains(tn)) used.Add(tn.ToLowerInvariant());
        static Vector3 V(SharpDX.Vector3 v) => new(v.X, v.Y, v.Z);
        return new PropModel(Path.GetFileNameWithoutExtension(name), name, fragment, V(d.BoundingBoxMin), V(d.BoundingBoxMax),
                             V(d.BoundingCenter), d.BoundingSphereRadius, collision, own.Count, [.. used]);
    }

    // ================================================================ names

    /// <summary>A name prefix as the game's names are spelt ([a-z0-9_], kept as typed at the end: <c>my_</c>).</summary>
    public static string CleanPrefix(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = VehicleBuilder.CleanModel(raw) ?? "";
        return s.Length > 0 && (raw.EndsWith('_') || raw.EndsWith(' ')) ? s + "_" : s;
    }

    /// <summary>A file's stem with the prefix (once).</summary>
    public static string Rename(string stem, string prefix) =>
        prefix.Length == 0 || stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? stem.ToLowerInvariant() : prefix + stem.ToLowerInvariant();

    /// <summary>A file of the folder under its new name (the prefix before its stem: <c>chair.ydr</c> → <c>my_chair.ydr</c>).</summary>
    public static string RenameFile(string name, string prefix)
    {
        int dot = name.IndexOf('.');
        return dot < 0 ? Rename(name, prefix) : Rename(name[..dot], prefix) + name[dot..].ToLowerInvariant();
    }

    /// <summary>How far away the game draws a prop of this size (its bounding sphere's radius) — as its own props are.</summary>
    public static float AutoLod(float radius) => radius switch
    {
        < 0.5f => 40,
        < 1f => 50,
        < 1.5f => 75,
        < 5f => 100,
        < 8f => 120,
        < 20f => 150,
        _ => MathF.Max(220, MathF.Round(radius * 10)),
    };

    /// <summary>Up to where the prop's high-detail textures are used.</summary>
    public static float AutoHd(float radius) => radius < 1f ? 5 : 15;

    /// <summary>
    /// The texture dictionary a model's archetype names: the one of its own name, else the one holding most of the
    /// textures it uses; null when it holds all its textures itself (or none of the folder's has them).
    /// </summary>
    public static string? TextureDictionary(PropSource src, PropModel m)
    {
        if (src.Dictionaries.ContainsKey(m.Name)) return m.Name;
        if (m.Textures.Count == 0) return null;
        return src.Dictionaries.Select(d => (d.Key, Hits: m.Textures.Count(d.Value.Contains)))
                  .Where(d => d.Hits > 0).OrderByDescending(d => d.Hits).ThenBy(d => d.Key, StringComparer.Ordinal)
                  .Select(d => d.Key).FirstOrDefault();
    }

    /// <summary>The names the props get and what stops the build (Error): no model, a name the game has, models for Enhanced.</summary>
    public static (PropNames? Names, string? Error) Plan(PropSource src, PropBuildOptions o, GameModels game)
    {
        if (src.PrebuiltRpf is not null)
            return (null, L.T($"The folder already has a finished pack ({Path.GetFileName(src.PrebuiltRpf)}) — there is nothing to build."));
        if (src.Models.Count == 0 && src.OwnTypes.Count == 0) return (null, L.T("There is no prop model (.ydr / .yft) in the folder."));
        if (o.Edition == GameEdition.Legacy && src.Editions.Contains(GameEdition.Enhanced))
            return (null, L.T("The models are Enhanced files — GTA V Legacy can’t load them. Build the add-on for Enhanced."));
        var prefix = CleanPrefix(o.Prefix);
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(src.Folder)));
        var pack = VehicleBuilder.CleanModel(o.PackName) ?? VehicleBuilder.CleanModel(folderName) ?? "props";
        if (pack.Length > 32) return (null, L.T("The pack name is too long — 32 characters at most."));

        var props = new List<PropEntry>();
        foreach (var m in src.NewModels)
        {
            var txd = TextureDictionary(src, m);
            var physics = m.Collision && !m.Fragment ? m.Name : !m.Collision && src.Has(m.Name + ".ybn") ? m.Name : null;
            var clip = src.Has(m.Name + ".ycd") ? m.Name : null;
            var lod = o.LodDist is { } l && l > 0 ? l : AutoLod(m.Radius);
            props.Add(new PropEntry(Rename(m.Name, prefix), m.Name, m,
                                    txd is null ? null : Rename(txd, prefix), physics is null ? null : Rename(physics, prefix),
                                    clip is null ? null : Rename(clip, prefix), lod, MathF.Min(AutoHd(m.Radius), lod)));
        }
        var own = src.OwnTypes.SelectMany(t => t.Archetypes).Distinct().Select(a => Rename(a, prefix)).ToList();

        // names the game has: a prop, its textures or its .ytyp would take the game's place
        var stems = src.Files.Keys.Where(n => PathUtil.SuffixLower(n) is ".ydr" or ".yft" or ".ytd")
                       .Select(n => Path.GetFileNameWithoutExtension(n)).Where(s => !s.EndsWith("_hi", StringComparison.Ordinal))
                       .Concat(src.OwnTypes.Select(t => t.Name)).Concat(src.OwnTypes.SelectMany(t => t.Archetypes))
                       .Select(s => Rename(s, prefix)).Distinct().Order(StringComparer.Ordinal).ToList();
        var taken = stems.Where(game.Has).ToList();
        if (taken.Count > 0)
            return (null, L.T($"The game has {string.Join(", ", taken.Take(5))}{(taken.Count > 5 ? ", …" : "")} already — an add-on of the same name " +
                              $"would take the game’s place. Give the files a name prefix (e.g. {pack}_)."));
        var dup = props.Select(p => p.Name).Concat(own).GroupBy(n => n).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) return (null, L.T($"Two props would be named {dup.Key} — rename one of the files."));

        // the .ytyp written for them: the pack's name, unless a file of the pack or the game has it
        var types = "";
        if (props.Count > 0)
        {
            types = pack;
            for (int i = 2; game.Has(types) || stems.Contains(types) || src.OwnTypes.Any(t => Rename(t.Name, prefix) == types); i++)
                types = i == 2 ? pack + "_props" : $"{pack}_props{i}";
        }
        return (new PropNames(pack, types, prefix, o.Dynamic, props, own), null);
    }

    // ================================================================ the .ytyp

    /// <summary>The archetypes file for the props (CodeWalker's XML form of <c>CMapTypes</c>).</summary>
    public static XDocument Ytyp(PropNames n)
    {
        var flags = n.Dynamic ? DynamicFlags : StaticFlags;
        var items = n.Props.Where(p => p.Model is not null).Select(p =>
        {
            var m = p.Model!;
            var f = flags | (p.Clip is not null ? 0x200u : 0);                     // has an animation (.ycd)
            return new XElement("Item", new XAttribute("type", "CBaseArchetypeDef"),
                Val("lodDist", p.LodDist),
                new XElement("flags", new XAttribute("value", f.ToString(Inv))),
                new XElement("specialAttribute", new XAttribute("value", "0")),
                Vec("bbMin", m.Min), Vec("bbMax", m.Max), Vec("bsCentre", m.Centre),
                Val("bsRadius", m.Radius),
                Val("hdTextureDist", p.HdDist),
                new XElement("name", p.Name),
                new XElement("textureDictionary", p.Txd ?? ""),
                new XElement("clipDictionary", p.Clip ?? ""),
                new XElement("drawableDictionary"),
                new XElement("physicsDictionary", p.Physics ?? ""),
                new XElement("assetType", m.Fragment ? "ASSET_TYPE_FRAGMENT" : "ASSET_TYPE_DRAWABLE"),
                new XElement("assetName", p.Name),
                new XElement("extensions"));
        });
        return new XDocument(new XElement("CMapTypes",
            new XElement("extensions"),
            new XElement("archetypes", items),
            new XElement("name", n.Types),
            new XElement("dependencies"),
            new XElement("compositeEntityTypes", new XAttribute("itemType", "CCompositeEntityType"))));
    }

    private static XElement Val(string name, float v) => new(name, new XAttribute("value", v.ToString("R", Inv)));

    private static XElement Vec(string name, Vector3 v) =>
        new(name, new XAttribute("x", v.X.ToString("R", Inv)), new XAttribute("y", v.Y.ToString("R", Inv)), new XAttribute("z", v.Z.ToString("R", Inv)));

    /// <summary>The folder's own .ytyp with the prefix in front of the names of the folder's files it refers to (and its own name).</summary>
    public static XDocument PatchTypes(PropTypes t, PropSource src, string prefix)
    {
        var doc = new XDocument(t.Doc);
        if (prefix.Length == 0) return doc;
        var stems = src.Files.Keys.Select(n => n[..n.IndexOf('.')]).Concat(src.OwnTypes.SelectMany(o => o.Archetypes))
                       .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in doc.Root?.Element("archetypes")?.Elements("Item") ?? [])
            foreach (var e in new[] { "name", "assetName", "textureDictionary", "clipDictionary", "drawableDictionary", "physicsDictionary" })
                if (item.Element(e) is { } x && x.Value.Trim() is { Length: > 0 } v && stems.Contains(v))
                    x.Value = Rename(v, prefix);
        if (doc.Root?.Element("name") is { } name) name.Value = Rename(t.Name, prefix);
        return doc;
    }

    // ================================================================ build

    /// <summary>
    /// Build the pack into <c>OutDir/&lt;pack&gt;</c>: dlc.rpf (or loose folders) and manifest.json listing the props'
    /// spawn names. What stops it (no model, a name the game has…) is an <see cref="IntakeException"/>.
    /// </summary>
    public static PropBuildResult Build(PropBuildOptions o, Action<string> log)
    {
        var game = GameModels.Load(o.DataDir);
        if (game.Count == 0) log("[!] " + L.T($"The list of the game’s models ({GameModels.FileName}) is missing — names aren’t checked against the game."));
        var src = Read(o.InputFolder);
        var (names, error) = Plan(src, o, game);
        if (names is null) throw new IntakeException(error!);
        foreach (var w in src.Warnings) log($"[!] {w}");
        log(L.T($"Props pack {names.Pack}: {names.Props.Count + names.Own.Count} prop(s), for {o.Edition.DisplayName()}."));
        if (names.Prefix.Length > 0) log(L.T($"    Files renamed: {names.Prefix}…"));

        var work = PathUtil.MakeTempDir();
        try
        {
            var drop = new DroppedSource { WorkDir = work };
            drop.Sources.Add(o.InputFolder);
            void Add(string full, string name) => drop.Files.Add(new DroppedFile(full, name, name, false, 0));
            var generated = new List<string>();

            foreach (var (name, full) in src.Files) Add(full, RenameFile(name, names.Prefix));
            foreach (var t in src.OwnTypes)
            {
                var file = Rename(t.Name, names.Prefix) + ".ytyp";
                var path = Path.Combine(work, file);
                File.WriteAllBytes(path, MapMeta.Write(PatchTypes(t, src, names.Prefix), file));
                Add(path, file);
                log(L.T($"    {file}: the folder’s own — {t.Archetypes.Count} archetype(s) used as they are."));
            }
            if (names.Props.Count > 0)
            {
                var file = names.Types + ".ytyp";
                var path = Path.Combine(work, file);
                MapMeta.Know(names.Props.SelectMany(p => new[] { p.Name, p.Txd, p.Physics, p.Clip }).OfType<string>().Append(names.Types));
                File.WriteAllBytes(path, MapMeta.Write(Ytyp(names), file));
                Add(path, file);
                generated.Add(file);
                log(L.T($"    {file} written: {names.Props.Count} archetype(s), {(names.Dynamic ? L.T("dynamic — they move when hit") : L.T("static — fixed in place"))}."));
                foreach (var p in names.Props)
                {
                    var m = p.Model!;
                    var what = new List<string> { L.T($"{m.Size.X:0.##} × {m.Size.Y:0.##} × {m.Size.Z:0.##} m"), L.T($"drawn to {p.LodDist:0} m") };
                    what.Add(p.Txd is not null ? L.T($"textures {p.Txd}.ytd") : m.OwnTextures > 0 ? L.T("own textures") : L.T("no textures"));
                    what.Add(m.Collision ? (m.Fragment ? L.T("fragment") : L.T("collision")) : L.T("no collision"));
                    log($"      {p.Name}: {string.Join(", ", what)}");
                    if (p.Txd is null && m.Textures.Count > 0)
                        log("[!] " + L.T($"{p.Name} uses {string.Join(", ", m.Textures.Take(4))}{(m.Textures.Count > 4 ? ", …" : "")} — no .ytd of the folder has them; " +
                                         $"they show only if the game has them."));
                }
            }

            var spec = DlcComposer.FromMapFiles(drop) ?? throw new IntakeException(L.T("Nothing to pack — the folder has no prop."));
            foreach (var w in spec.Warnings) log($"[!] {w}");
            var device = "dlc_" + names.Pack;
            var root = Path.Combine(o.OutDir, names.Pack);
            if (o.Pack && Directory.Exists(root)) PathUtil.DeleteDir(root);
            var result = DlcComposer.Compose(spec, device, root, o.Edition, log, pack: o.Pack);
            WriteReadme(root, names, o, generated);
            log(L.T($"Spawn names (Menyoo: Object Spooner → Spawn by name): {string.Join(", ", names.SpawnNames)}"));
            log(L.T($"output: {root}"));
            return new PropBuildResult(root, o.Pack ? result : null, device, names, generated);
        }
        finally
        {
            PathUtil.TryDeleteDir(work);
        }
    }

    private static void WriteReadme(string root, PropNames n, PropBuildOptions o, List<string> generated)
    {
        var manifest = new JsonObject
        {
            ["pack"] = n.Pack,
            ["device"] = "dlc_" + n.Pack,
            ["props"] = new JsonArray([.. n.SpawnNames.Select(s => JsonValue.Create(s))]),
            ["dynamic"] = n.Dynamic,
            ["target"] = o.Edition.TargetLabel(),
            ["packed"] = o.Pack,
            ["generated"] = new JsonArray([.. generated.Select(g => JsonValue.Create(g))]),
            ["install"] = $"Copy the '{n.Pack}' folder (with dlc.rpf inside) to mods\\update\\x64\\dlcpacks and add " +
                          $"<Item>dlcpacks:/{n.Pack}/</Item> to dlclist.xml — or drop the folder into ModDrop V. " +
                          $"Spawn the props by name in a trainer (Menyoo: Object Spooner → Spawn by name).",
        };
        TextIo.WriteText(Path.Combine(root, "manifest.json"), TextIo.ToJson(manifest));
    }
}
