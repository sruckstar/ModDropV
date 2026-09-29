using Mdv.Core;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mdv.Core.Index;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>A file going into a composed pack; <see cref="PackPath"/> may run through archives (<c>x64/vehicles.rpf/adder.yft</c>).</summary>
public sealed record ComposeFile(string Source, string PackPath);

/// <summary>A content.xml entry of a composed pack: the path inside it (without the device) and its data file type.</summary>
/// <param name="Contents">what an archive holds, when the game must know (<c>CONTENTS_DLC_MAP_DATA</c>)</param>
public sealed record ComposeData(string PackPath, string Type, bool Persistent = false, string? Contents = null);

/// <summary>An add-on still to be packed: its files, the content.xml entries and what it declares.</summary>
public sealed class ComposeSpec
{
    public List<ComposeFile> Files { get; } = [];
    public List<ComposeData> Data { get; } = [];
    public AddonContent Content { get; } = new();
    public List<string> Warnings { get; } = [];
    /// <summary>The FiveM resources it was made of (folder names; empty for loose files).</summary>
    public List<string> Resources { get; } = [];
    /// <summary>Peds the mod has models of but no peds.meta for: their entries are written on packing (<see cref="PedMeta"/>).</summary>
    public List<NewPed> NewPeds { get; } = [];
    /// <summary>MP clothing collections whose shop meta (and, for loose models, the ymt) is written on packing.</summary>
    public List<NewCollection> NewCollections { get; } = [];
    /// <summary>The resource being read streams a map / props (its archetype requests go into the map's archives).</summary>
    internal ModCategory? MapKind { get; set; }
    /// <summary>
    /// Handling / layouts files a fxmanifest names that the resource doesn't have — whether its vehicles need them is
    /// told by <see cref="VehicleGaps"/>.
    /// </summary>
    public List<MissingFile> MissingFiles { get; } = [];

    /// <summary>The same add-on with other files and content.xml entries.</summary>
    public ComposeSpec With(List<ComposeFile> files, List<ComposeData> data)
    {
        var c = new ComposeSpec { MapKind = MapKind };
        c.Files.AddRange(files);
        c.Data.AddRange(data);
        c.Warnings.AddRange(Warnings);
        c.Resources.AddRange(Resources);
        c.NewPeds.AddRange(NewPeds);
        c.NewCollections.AddRange(NewCollections);
        c.MissingFiles.AddRange(MissingFiles);
        foreach (var (h, s) in Content.Labels) c.Content.Labels[h] = s;
        c.Content.DataTypes.UnionWith(Content.DataTypes);
        c.Content.Vehicles.AddRange(Content.Vehicles);
        c.Content.Peds.AddRange(Content.Peds);
        c.Content.Streamed.AddRange(Content.Streamed);
        c.Content.ModelEditions.UnionWith(Content.ModelEditions);
        return c;
    }
}

/// <summary>A data file a fxmanifest names (<c>data_file 'HANDLING_FILE' 'handling.meta'</c>) that isn't in the resource.</summary>
public sealed record MissingFile(string Resource, string Pattern, string Type);

/// <summary>
/// An MP clothing collection written on packing: the shop meta it lacks, and — made of loose models — its files laid
/// out under a collection of its own (numbered from 0 per slot) with a ymt written for them (<see cref="PedVariation"/>).
/// </summary>
/// <param name="Ped">mp_m_freemode_01 / mp_f_freemode_01</param>
public sealed class NewCollection(string ped)
{
    /// <summary>The ped it is for — loose models can be switched between the two MP peds before packing.</summary>
    public string Ped { get; set; } = ped;
    /// <summary>The collection's name (<c>mp_m_ftbmodels_arai</c>); null: named after the pack on packing (<c>mp_m_&lt;pack&gt;</c>).</summary>
    public string? DlcName { get; init; }
    /// <summary>Loose clothing files to lay out, with their ymt written (empty: the mod has the ymt, only the shop meta is missing).</summary>
    public List<(string Source, ClothingPart Part)> Parts { get; } = [];
    public bool IsFemale => Ped.StartsWith("mp_f_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The collection name used in a pack mounted as <paramref name="device"/>.</summary>
    public string NameFor(string device) =>
        DlcName ?? (IsFemale ? "mp_f_" : "mp_m_") + DlcComposer.Slug(device.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase) ? device[4..] : device);

    /// <summary>Each file's number in the collection: the mod's numbers per slot, in order, from 0.</summary>
    public Dictionary<(bool Prop, int Slot, int Number), int> Numbering()
    {
        var map = new Dictionary<(bool, int, int), int>();
        foreach (var g in Parts.Select(p => p.Part).Where(p => p.Kind == ClothingPartKind.Drawable).GroupBy(p => (p.Prop, p.Slot)))
        {
            int n = 0;
            foreach (var number in g.Select(p => p.Number).Distinct().Order()) map[(g.Key.Prop, g.Key.Slot, number)] = n++;
        }
        return map;
    }
}

/// <summary>
/// Makes a dlc.rpf out of files that aren't a pack yet — a FiveM resource (<c>fxmanifest.lua</c> +
/// <c>stream/</c> + metas) or loose models with their metas. The manifest's <c>data_file</c> lines (or the
/// metas' root tags) give the content.xml entries; streamed files go into an image archive
/// (<c>x64/vehicles.rpf</c>, <c>x64/peds.rpf</c>), a FiveM <c>name^file</c> into a folder of it; audio into
/// <c>x64/audio/config</c> and <c>x64/audio/sfx</c>; text labels a script sets (<c>AddTextEntry</c>) into
/// the pack's global.gxt2. The same composer is the base of the modder tools (session 12).
/// </summary>
public static partial class DlcComposer
{
    /// <summary>Data file types whose file is a meta that goes to common/data.</summary>
    private static readonly HashSet<string> AudioConfigTypes = new(StringComparer.OrdinalIgnoreCase)
        { "AUDIO_GAMEDATA", "AUDIO_SOUNDDATA", "AUDIO_SYNTHDATA", "AUDIO_SPEECHDATA", "AUDIO_DYNAMIXDATA", "AUDIO_CATEGORIESDATA", "AUDIO_CURVEDATA" };

    [GeneratedRegex(@"data_file\s*\(?\s*['""]([A-Za-z0-9_]+)['""]\s*\)?\s*\(?\s*['""]([^'""]+)['""]")] private static partial Regex DataFileRe();
    [GeneratedRegex(@"\.dat(\d+)\.rel$", RegexOptions.IgnoreCase)] private static partial Regex RelRe();
    [GeneratedRegex(@"[^a-z0-9_]+")] private static partial Regex NonSlugRe();

    public static bool IsManifest(string name) =>
        name.Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase) || name.Equals("__resource.lua", StringComparison.OrdinalIgnoreCase);

    /// <summary>"x64/audio/config/myengine_game.dat151.rel" → the audio data type its number stands for.</summary>
    private static string? AudioTypeOf(string name) => RelRe().Match(name) is { Success: true } m
        ? m.Groups[1].Value switch
        {
            "151" => "AUDIO_GAMEDATA",
            "54" => "AUDIO_SOUNDDATA",
            "10" => "AUDIO_SYNTHDATA",
            "4" => "AUDIO_SPEECHDATA",
            "15" => "AUDIO_DYNAMIXDATA",
            "16" => "AUDIO_CURVEDATA",
            "22" => "AUDIO_CATEGORIESDATA",
            _ => null,
        }
        : null;

    private static string DirOf(string origin)
    {
        var o = origin.Replace('\\', '/');
        int i = o.LastIndexOf('/');
        return i < 0 ? "" : o[..i];
    }

    private static string Rel(string root, string origin) =>
        root.Length == 0 ? origin.Replace('\\', '/') : origin.Replace('\\', '/')[(root.Length + 1)..];

    public static string Slug(string s)
    {
        var slug = NonSlugRe().Replace(s.ToLowerInvariant(), "_").Trim('_');
        return slug.Length > 24 ? slug[..24].TrimEnd('_') : slug;
    }

    // ================================================================ reading a drop

    /// <summary>
    /// The add-on in a drop's FiveM resources, or (with none) its loose models and metas; null when
    /// neither declares a vehicle / ped / anything packable.
    /// </summary>
    public static ComposeSpec? FromDrop(DroppedSource src)
    {
        var files = src.Files.Where(f => !f.InBackupDir).ToList();
        var roots = files.Where(f => IsManifest(f.Name)).Select(f => DirOf(f.Origin)).Distinct()
                         .OrderByDescending(r => r.Length).ToList();
        var spec = new ComposeSpec();
        var streamed = new Dictionary<string, DroppedFile>(StringComparer.OrdinalIgnoreCase);     // name in the image → file
        if (roots.Count > 0)
        {
            var groups = files.GroupBy(f => roots.FirstOrDefault(r => r.Length == 0 || f.Origin.Replace('\\', '/').StartsWith(r + "/", StringComparison.OrdinalIgnoreCase)))
                              .Where(g => g.Key is not null).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            // versions of one add-on side by side (FSeriesAmbo, FSeriesAmbo (Optimized)): the same vehicles twice in a pack
            // crash the game — the first one goes in
            var kept = new List<IGrouping<string?, DroppedFile>>();
            var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);         // model → the resource declaring it
            foreach (var g in groups)
            {
                var name = g.Key!.Length == 0 ? "resource" : g.Key.Split('/')[^1];
                var models = DeclaredModels(g);
                if (models.Count > 0 && models.All(declared.ContainsKey))
                {
                    spec.Warnings.Add(L.T($"«{name}» declares the same {string.Join(", ", models.Take(3))} as «{declared[models[0]]}» — " +
                                          $"another version of it, left out. To install this one instead, drop its folder on its own."));
                    continue;
                }
                foreach (var m in models) declared.TryAdd(m, name);
                kept.Add(g);
            }
            foreach (var g in kept) AddResource(spec, g.Key!, [.. g], kept.Count > 1, streamed);
        }
        else AddLoose(spec, files, streamed);

        if (spec.Content.Kind is null && spec.Data.Count == 0) return null;
        AddImages(spec);
        AddMissingShops(spec);
        AddTypeRequests(spec);
        ReadMaps(spec);
        CheckStreamed(spec, src);
        return spec;
    }

    /// <summary>The vehicles / peds a resource's vehicles.meta / peds.meta files declare.</summary>
    private static List<string> DeclaredModels(IEnumerable<DroppedFile> files)
    {
        var c = new AddonContent();
        foreach (var f in files.Where(f => PathUtil.SuffixLower(f.Name) is ".meta" or ".xml" && new FileInfo(f.FullPath).Length < (16 << 20)))
        {
            var text = TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.FullPath), strict: false);
            if (ModDetector.RootTag(text) is { } root && AddonContent.TypeByRoot.TryGetValue(root, out var type) &&
                type is VehicleBuilder.InitType or "PED_METADATA_FILE")
                c.AddData(type, text, f.Origin);
        }
        return [.. c.Vehicles.Select(v => v.Model).Concat(c.Peds.Select(p => p.Name))];
    }

    /// <summary>
    /// Everything a map is made of — placements, archetypes, the pack manifest, models, collisions — goes into one archive,
    /// as the SP map packs that work in the game keep it (Water Park): placements in an archive of their own flagged as
    /// map data and mounted by the map changeset never showed in the game.
    /// </summary>
    public const string MapImage = "x64/levels/gta5/map.rpf";
    public const string ManifestName = "_manifest.ymf";

    /// <summary>File types a map / props add-on is made of.</summary>
    private static readonly HashSet<string> MapExts = new(StringComparer.OrdinalIgnoreCase)
        { ".ymap", ".ytyp", ".ymf", ".ydr", ".ydd", ".yft", ".ytd", ".ybn", ".ypt", ".ycd", ".ynv", ".ynd" };

    /// <summary>A placement / archetype file or its XML form (<c>x.ymap</c>, <c>x.ymap.xml</c>, <c>x.ytyp.xml</c>).</summary>
    public static bool IsMapFile(string name)
    {
        var n = AddonContent.MapFileName(name);
        return n.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Map / props files with no manifest to say so — placements (.ymap), archetypes (.ytyp) and the models, textures
    /// and collisions they use, binary or as XML: an add-on of their own. Null when there is neither a placement nor
    /// an archetype file. Files of scripts / trainers (scripts\, menyooStuff\) are not the map's.
    /// </summary>
    public static ComposeSpec? FromMapFiles(DroppedSource src)
    {
        var files = src.Files.Where(f => !f.InBackupDir && (MapExts.Contains(PathUtil.SuffixLower(f.Name)) || IsMapFile(f.Name)) &&
                                         !f.Origin.Replace('\\', '/').Split('/').SkipLast(1).Any(d =>
                                             d.Equals("scripts", StringComparison.OrdinalIgnoreCase) ||
                                             d.Equals("menyooStuff", StringComparison.OrdinalIgnoreCase) ||
                                             d.Equals("plugins", StringComparison.OrdinalIgnoreCase) ||
                                             d.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)))
                             .OrderBy(f => f.Depth).ThenBy(f => f.Origin, PathUtil.PathOrder).ToList();
        if (!files.Any(f => IsMapFile(f.Name))) return null;
        var spec = new ComposeSpec();
        var kind = files.Any(f => AddonContent.MapFileName(f.Name).EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)) ? ModCategory.Map : ModCategory.Prop;
        var streamed = new Dictionary<string, DroppedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) AddStreamed(spec, f, kind, streamed);
        AddImages(spec);
        AddTypeRequests(spec);
        ReadMaps(spec);
        CheckStreamed(spec, src);
        return spec;
    }

    /// <summary>
    /// Streamed files the game reads only as RSC7 resources that aren't one — encrypted by FiveM's asset escrow (the
    /// resource has a .fxap), or broken. Packed as they are, they stop the game at loading (ERR_STR_PACK). A placement /
    /// archetype file in its XML form is fine: it's built into the game's form on packing.
    /// </summary>
    private static void CheckStreamed(ComposeSpec spec, DroppedSource src)
    {
        var bad = spec.Files.Where(f => Rpf7.MustBeResource(PathUtil.SuffixLower(f.PackPath)) && !IsResource(f.Source) && !ReadableMap(f))
                            .Select(f => Path.GetFileName(f.PackPath)).ToList();
        if (bad.Count == 0) return;
        var list = string.Join(", ", bad.Take(5)) + (bad.Count > 5 ? L.T($" and {bad.Count - 5} more") : "");
        if (src.Files.Any(f => PathUtil.SuffixLower(f.Name) == ".fxap"))
            throw new IntakeException(L.T($"This FiveM resource is locked by Cfx asset escrow (it has a .fxap file): its files ({list}) are encrypted, " +
                                          $"only a FiveM server can open them. Story mode can't use them — the game would stop at loading " +
                                          $"(ERR_STR_PACK). Ask its author for the open version."));
        throw new IntakeException(L.T($"Not in the game's form: {list} — broken or encrypted. Packed as they are, they would stop the game " +
                                      $"at loading (ERR_STR_PACK)."));
    }

    private static bool IsResource(string path) => ResourceVersion(path) >= 0;

    /// <summary>A placement / archetype file that isn't a resource but reads as its XML form (built on packing).</summary>
    private static bool ReadableMap(ComposeFile f)
    {
        if (!IsMapFile(f.PackPath)) return false;
        try
        {
            MapMeta.Read(File.ReadAllBytes(f.Source), Path.GetFileName(f.PackPath));
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>What the spec's placements place and the props its archetype files define (read from the mod's files).</summary>
    private static void ReadMaps(ComposeSpec spec)
    {
        if (spec.Content.Kind is not (ModCategory.Map or ModCategory.Prop)) return;
        var files = spec.Files.Where(f => f.PackPath.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase) ||
                                          f.PackPath.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase))
                              .Select(f => (Path.GetFileName(f.PackPath), File.ReadAllBytes(f.Source)));
        spec.Content.ReadMaps(files, spec.Warnings);
    }

    /// <summary>
    /// Archetype files a props pack must load for good (so trainers can spawn its props): a DLC_ITYP_REQUEST each. A map's
    /// archetypes load with its placements — through its own manifest, or the one written on packing.
    /// </summary>
    private static void AddTypeRequests(ComposeSpec spec)
    {
        if (spec.Content.Kind != ModCategory.Prop) return;
        foreach (var f in spec.Files.Where(f => f.PackPath.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)))
            if (!spec.Data.Any(d => d.PackPath.Equals(f.PackPath, StringComparison.OrdinalIgnoreCase)))
                spec.Data.Add(new ComposeData(f.PackPath, "DLC_ITYP_REQUEST"));
        spec.Content.DataTypes.Add("DLC_ITYP_REQUEST");
    }

    public const string PedImage = "x64/peds.rpf";
    public const string StreamedPedImage = "x64/streamedpeds.rpf";
    public const string CreatureImage = "x64/anim/creaturemetadata.rpf";

    /// <summary>The image archive of MP clothes of one gender (props go in it too, as in the slot packs players use).</summary>
    public static string ClothesImage(bool female) => $"x64/models/cdimages/clothes_{(female ? "female" : "male")}.rpf";

    /// <summary>
    /// Where a streamed file of MP clothes goes in the pack: a collection's ymt and files (FiveM spells the collection
    /// folder with ^: <c>mp_m_freemode_01_mp_m_x^jbib_000_u.ydd</c>) into the clothes archive of its gender, creature
    /// metadata into <see cref="CreatureImage"/>. Null: not clothes.
    /// </summary>
    private static string? ClothingPath(DroppedFile f)
    {
        var name = f.Name.ToLowerInvariant();
        if (name.StartsWith("mp_creaturemetadata_", StringComparison.Ordinal) && name.EndsWith(".ymt", StringComparison.Ordinal))
            return $"{CreatureImage}/{name}";
        if (name.Contains('^'))
        {
            var prefix = name[..name.LastIndexOf('^')];
            return ClothingNames.WearerOfFolder(prefix) is { IsMp: true } w
                ? $"{ClothesImage(w.IsFemale)}/{prefix}/{name[(name.LastIndexOf('^') + 1)..]}" : null;
        }
        if (ClothingNames.WearerOfYmt(name) is { IsMp: true, Collection: not null } y) return $"{ClothesImage(y.IsFemale)}/{name}";
        if (!ClothingNames.IsClothingFile(name)) return null;
        // laid out as in a pack: …/mp_m_freemode_01_mp_m_x/jbib_000_u.ydd (props may sit one folder deeper, p_head/)
        foreach (var dir in DirOf(f.Origin).Split('/').Reverse().Take(2))
            if (ClothingNames.WearerOfFolder(dir) is { IsMp: true, Collection: not null } wf)
                return $"{ClothesImage(wf.IsFemale)}/{dir.ToLowerInvariant()}/{name}";
        return null;
    }

    /// <summary>An MP collection among the streamed files has no shop meta: one is written for it on packing.</summary>
    private static void AddMissingShops(ComposeSpec spec)
    {
        foreach (var c in spec.Content.Collections)
            if (!spec.Content.Shops.Contains(c.FullName) && !spec.NewCollections.Any(n => n.DlcName == c.DlcName))
                spec.NewCollections.Add(new NewCollection(c.Ped) { DlcName = c.DlcName });
    }

    /// <summary>
    /// The content.xml entries of the image archives. A streamed ped (its components in a folder of its own,
    /// <c>name/head_000_r.ydd</c>) goes with its <c>name.yft</c> / <c>name.ymt</c> into an archive of its own
    /// registered as PEDSTREAM_FILE — as Rockstar's DLCs keep them (<c>…_cutspeds.rpf</c>); every other image
    /// archive is an RPF_FILE.
    /// </summary>
    private static void AddImages(ComposeSpec spec)
    {
        var prefix = PedImage + "/";
        var streamedPeds = spec.Files.Where(f => f.PackPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.PackPath[prefix.Length..].Contains('/'))
                                     .Select(f => f.PackPath[prefix.Length..].Split('/')[0])
                                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (streamedPeds.Count > 0)
            for (int i = 0; i < spec.Files.Count; i++)
            {
                var f = spec.Files[i];
                if (!f.PackPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var inner = f.PackPath[prefix.Length..];
                var owner = inner.Contains('/') ? inner.Split('/')[0] : Path.GetFileNameWithoutExtension(inner);
                if (streamedPeds.Contains(owner)) spec.Files[i] = f with { PackPath = $"{StreamedPedImage}/{inner}" };
            }
        foreach (var rpf in spec.Files.Select(f => ImageOf(f.PackPath)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            spec.Data.Add(rpf.Equals(StreamedPedImage, StringComparison.OrdinalIgnoreCase)
                ? new ComposeData(rpf, "PEDSTREAM_FILE", Persistent: true)
                : rpf.Equals(MapImage, StringComparison.OrdinalIgnoreCase)
                    ? new ComposeData(rpf, "RPF_FILE", Persistent: true, Contents: "CONTENTS_DLC_MAP_DATA")
                    : new ComposeData(rpf, "RPF_FILE", Persistent: true));
    }

    /// <summary>
    /// Peds that come as models only — <c>name.yft</c> + <c>name.ymt</c> with the drawables in <c>name.ydd</c>
    /// or in a folder <c>name/</c> (FiveM: <c>name^head_000_r.ydd</c>), props in <c>name_p.ydd</c> — whose names
    /// the game doesn't have (<paramref name="isGamePed"/>): an add-on with a peds.meta written for them.
    /// Null when there is no such ped (models of the game's own peds are a replacement).
    /// </summary>
    public static ComposeSpec? FromPedModels(DroppedSource src, Func<string, bool> isGamePed)
    {
        var files = src.Files.Where(f => !f.InBackupDir && GameIndex.StreamedExts.Contains(PathUtil.SuffixLower(f.Name)))
                             .OrderBy(f => f.Depth).ThenBy(f => f.Origin, PathUtil.PathOrder).ToList();
        var names = files.Where(f => PathUtil.SuffixLower(f.Name) == ".yft" && !f.Name.Contains('^'))
                         .Select(f => Path.GetFileNameWithoutExtension(f.Name))
                         .Where(n => !n.EndsWith("_hi", StringComparison.OrdinalIgnoreCase) && !isGamePed(n))
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return null;

        var spec = new ComposeSpec();
        var streamed = new Dictionary<string, DroppedFile>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<DroppedFile>();
        foreach (var name in names)
        {
            // its own files (name.yft, name.ymt, name.ydd, name_p.ydd…), then a streamed ped's components:
            // in a folder named after it, or FiveM's name^component
            var own = files.Where(f => Path.GetFileNameWithoutExtension(f.Name) is var b &&
                                       (b.Equals(name, StringComparison.OrdinalIgnoreCase) || b.Equals(name + "_p", StringComparison.OrdinalIgnoreCase)))
                           .ToList();
            var parts = files.Where(f => !own.Contains(f) &&
                                         (f.Name.StartsWith(name + "^", StringComparison.OrdinalIgnoreCase) ||
                                          DirOf(f.Origin).Split('/')[^1].Equals(name, StringComparison.OrdinalIgnoreCase)))
                             .ToList();
            bool isStreamed = parts.Count > 0 && !own.Any(f => f.Name.Equals(name + ".ydd", StringComparison.OrdinalIgnoreCase));
            foreach (var f in own) Add(f, f.Name);
            foreach (var f in parts)
                Add(f, f.Name.Contains('^') ? f.Name : $"{name}^{f.Name}");        // AddStreamed turns ^ into a folder

            bool Has(string ext) => own.Any(f => f.Name.Equals(name + ext, StringComparison.OrdinalIgnoreCase));
            if (!Has(".ymt"))
                spec.Warnings.Add(L.T($"«{name}» has no {name}.ymt (the list of its clothes) — the game can't dress it: it may be invisible or crash."));
            if (!Has(".ydd") && parts.Count == 0)
                spec.Warnings.Add(L.T($"«{name}» has no {name}.ydd and no folder of components — there is nothing to show."));
            var ped = new NewPed(name, isStreamed, own.Any(f => f.Name.Equals(name + "_p.ydd", StringComparison.OrdinalIgnoreCase)), PedMeta.Guess(name));
            spec.NewPeds.Add(ped);
            spec.Content.Peds.Add(new AddonPed(name, null));
        }
        foreach (var f in files.Where(f => !used.Contains(f) && PathUtil.SuffixLower(f.Name) is ".yft" or ".ydd"))
            spec.Warnings.Add(L.T($"{f.Origin} belongs to none of its peds — left out."));

        spec.Data.Add(new ComposeData("common/data/peds.meta", "PED_METADATA_FILE"));
        spec.Content.DataTypes.Add("PED_METADATA_FILE");
        AddImages(spec);
        return spec;

        void Add(DroppedFile f, string nameInImage)
        {
            if (!used.Add(f)) return;
            AddStreamed(spec, f with { Name = nameInImage }, ModCategory.Ped, streamed);
        }
    }

    /// <summary>
    /// Loose MP clothing models (<c>jbib_000_u.ydd</c>, <c>jbib_diff_000_a_uni.ytd</c>, <c>p_head_001.ydd</c>…) with no
    /// ymt: an add-on collection of their own for <paramref name="ped"/> — laid out and numbered from 0 per slot, the
    /// ymt and shop meta written on packing. Null when there is no clothing model.
    /// </summary>
    public static ComposeSpec? FromClothingModels(DroppedSource src, string ped)
    {
        var files = src.Files.Where(f => !f.InBackupDir && ClothingNames.IsClothingFile(f.Name))
                             .OrderBy(f => f.Depth).ThenBy(f => f.Origin, PathUtil.PathOrder).ToList();
        var spec = new ComposeSpec();
        var coll = new NewCollection(ped);
        var seen = new Dictionary<string, DroppedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var part = ClothingNames.Parse(f.Name)!;
            if (seen.TryGetValue(part.Name, out var first))
            {
                if (!SameFile(first.FullPath, f.FullPath))
                    spec.Warnings.Add(L.T($"{part.Name} is in the mod more than once — {first.Origin} is used, {f.Origin} is left out."));
                continue;
            }
            seen[part.Name] = f;
            coll.Parts.Add((f.FullPath, part));
            if (ResourceEditions.EditionOf(PathUtil.SuffixLower(f.Name), ResourceVersion(f.FullPath)) is { } ed) spec.Content.ModelEditions.Add(ed);
        }
        var parts = coll.Parts.Select(p => p.Part).ToList();
        var drawables = parts.Where(p => p.Kind == ClothingPartKind.Drawable).Select(p => (p.Prop, p.Slot, p.Number)).ToHashSet();
        if (drawables.Count == 0) return null;
        foreach (var t in parts.Where(p => p.Kind != ClothingPartKind.Drawable && !drawables.Contains((p.Prop, p.Slot, p.Number))))
            spec.Warnings.Add(L.T($"{t.Name} belongs to {t.Describe()}, which the mod has no model for — left out."));
        coll.Parts.RemoveAll(p => p.Part.Kind != ClothingPartKind.Drawable && !drawables.Contains((p.Part.Prop, p.Part.Slot, p.Part.Number)));
        foreach (var d in drawables.Where(d => !parts.Any(p => p.Kind == ClothingPartKind.Texture && (p.Prop, p.Slot, p.Number) == d)))
            spec.Warnings.Add(L.T($"{parts.First(p => (p.Prop, p.Slot, p.Number) == d && p.Kind == ClothingPartKind.Drawable).Describe()} has no texture " +
                              $"in the mod — the game shows it untextured."));
        spec.NewCollections.Add(coll);
        foreach (var f in coll.Parts) spec.Content.Streamed.Add(f.Part.Name);
        spec.Content.DataTypes.Add("SHOP_PED_APPAREL_META_FILE");
        return spec;
    }

    /// <summary>"x64/vehicles.rpf" for a streamed file's pack path.</summary>
    private static string? ImageOf(string packPath)
    {
        int i = packPath.IndexOf(".rpf/", StringComparison.OrdinalIgnoreCase);
        return i < 0 || packPath.Contains("/audio/", StringComparison.OrdinalIgnoreCase) ? null : packPath[..(i + 4)];
    }

    private static void AddResource(ComposeSpec spec, string root, List<DroppedFile> files, bool several, Dictionary<string, DroppedFile> streamed)
    {
        var name = root.Length == 0 ? "resource" : root.Split('/')[^1];
        spec.Resources.Add(name);
        var manifest = files.First(f => IsManifest(f.Name) && DirOf(f.Origin).Equals(root, StringComparison.OrdinalIgnoreCase));
        var text = TextIo.DecodeUtf8Sig(File.ReadAllBytes(manifest.FullPath), strict: false);
        var byRel = files.ToDictionary(f => Rel(root, f.Origin).ToLowerInvariant(), f => f);
        var sub = several ? Slug(name) + "/" : "";
        var used = new HashSet<DroppedFile>();
        var streamKind = KindByStream(files.Where(f => Rel(root, f.Origin).Split('/').SkipLast(1)
                                                         .Any(s => s.Equals("stream", StringComparison.OrdinalIgnoreCase))));
        spec.MapKind = streamKind is ModCategory.Map or ModCategory.Prop ? streamKind : null;

        foreach (Match m in DataFileRe().Matches(text))
        {
            var (type, pattern) = (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.Replace('\\', '/').TrimStart('.', '/'));
            var hits = Glob(byRel, pattern);
            if (hits.Count == 0 && (AudioConfigTypes.Contains(type) || type == "AUDIO_WAVEPACK"))
                hits = byRel.Where(kv => kv.Key.StartsWith(pattern.ToLowerInvariant().TrimEnd('/'), StringComparison.Ordinal)).Select(kv => kv.Value).ToList();
            if (hits.Count == 0)
            {
                if (type is VehicleBuilder.HandlingType or "VEHICLE_LAYOUTS_FILE") spec.MissingFiles.Add(new MissingFile(name, pattern, type));
                else spec.Warnings.Add(L.T($"{name}: fxmanifest names {pattern} ({type}), but there is no such file — left out."));
                continue;
            }
            foreach (var f in hits)
                if (used.Add(f)) AddData(spec, f, type, sub, streamed);
        }

        // what FiveM streams: everything under stream/
        var kind = spec.Content.Kind ?? KindByStream(files);
        foreach (var f in files.OrderBy(f => f.Origin, PathUtil.PathOrder))
        {
            var rel = Rel(root, f.Origin);
            if (used.Contains(f) || !rel.Split('/').SkipLast(1).Any(s => s.Equals("stream", StringComparison.OrdinalIgnoreCase))) continue;
            AddStreamed(spec, f, kind, streamed);
        }
        foreach (var f in files.Where(f => PathUtil.SuffixLower(f.Name) == ".lua" && !IsManifest(f.Name)))
            foreach (var (h, s) in AddonContent.LuaLabels(TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.FullPath), strict: false)))
                spec.Content.Labels.TryAdd(h, s);
    }

    private static void AddLoose(ComposeSpec spec, List<DroppedFile> files, Dictionary<string, DroppedFile> streamed)
    {
        var metas = new List<(DroppedFile File, string Type)>();
        foreach (var f in files.OrderBy(f => f.Depth).ThenBy(f => f.Origin, PathUtil.PathOrder))
        {
            var ext = PathUtil.SuffixLower(f.Name);
            if (ext is ".meta" or ".xml" or ".txt" && new FileInfo(f.FullPath).Length < (16 << 20))
            {
                var root = ModDetector.RootTag(TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.FullPath), strict: false));
                if (root is not null && AddonContent.TypeByRoot.TryGetValue(root, out var type)) metas.Add((f, type));
            }
        }
        // only a mod that declares a vehicle / ped is an add-on; loose models alone are a replacement
        if (!metas.Any(m => m.Type is "VEHICLE_METADATA_FILE" or "PED_METADATA_FILE" or "SHOP_PED_APPAREL_META_FILE")) return;
        var seenMeta = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, type) in metas)
        {
            if (!seenMeta.Add(type + "|" + Path.GetFileNameWithoutExtension(f.Name)))
            {
                spec.Warnings.Add(L.T($"{f.Origin}: a second {f.Name} — left out (drop the folder of the version you want)."));
                continue;
            }
            AddData(spec, f, type, "", streamed);
        }
        var kind = spec.Content.Kind ?? ModCategory.Vehicle;
        foreach (var f in files.OrderBy(f => f.Depth).ThenBy(f => f.Origin, PathUtil.PathOrder))
        {
            var ext = PathUtil.SuffixLower(f.Name);
            if (ext == ".awc") AddData(spec, f, "AUDIO_WAVEPACK", "", streamed);
            else if (ext == ".rel" && AudioTypeOf(f.Name) is { } at) AddData(spec, f, at, "", streamed);
            else if (GameIndex.StreamedExts.Contains(ext)) AddStreamed(spec, f, kind, streamed);
            else if (ext == ".lua")
                foreach (var (h, s) in AddonContent.LuaLabels(TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.FullPath), strict: false)))
                    spec.Content.Labels.TryAdd(h, s);
        }
    }

    private static ModCategory KindByStream(IEnumerable<DroppedFile> files) =>
        files.Any(f => AddonContent.MapFileName(f.Name).EndsWith(".ymap", StringComparison.OrdinalIgnoreCase))
            ? ModCategory.Map
        : files.Any(f => AddonContent.MapFileName(f.Name).EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)) &&
        !files.Any(f => PathUtil.SuffixLower(f.Name) is ".yft" or ".ydd" or ".ymt")
            ? ModCategory.Prop
        : files.Any(f => ClothingNames.WearerOfYmt(f.Name) is { IsMp: true, Collection: not null } ||
                       (f.Name.Contains('^') && ClothingNames.WearerOfFolder(f.Name[..f.Name.LastIndexOf('^')]) is { IsMp: true }))
            ? ModCategory.Clothing
        : files.Any(f => PathUtil.SuffixLower(f.Name) == ".yft" && !f.Name.Contains('^')) &&
        !files.Any(f => PathUtil.SuffixLower(f.Name) is ".ydd" or ".ymt")
            ? ModCategory.Vehicle : ModCategory.Ped;

    private static void AddData(ComposeSpec spec, DroppedFile f, string type, string sub, Dictionary<string, DroppedFile> streamed)
    {
        var ext = PathUtil.SuffixLower(f.Name);
        string path;
        switch (type)
        {
            case "AUDIO_WAVEPACK":
                if (ext != ".awc") return;
                var bank = DirOf(f.Origin).Split('/')[^1];
                if (bank.Length == 0) bank = "addon_sfx";
                path = $"x64/audio/sfx/{bank.ToLowerInvariant()}/{f.Name.ToLowerInvariant()}";
                if (!spec.Data.Any(d => d.Type == type && d.PackPath.Equals($"x64/audio/sfx/{bank.ToLowerInvariant()}", StringComparison.OrdinalIgnoreCase)))
                    spec.Data.Add(new ComposeData($"x64/audio/sfx/{bank.ToLowerInvariant()}", type));
                break;
            case var t when AudioConfigTypes.Contains(t):
                if (ext != ".rel") return;
                path = $"x64/audio/config/{f.Name.ToLowerInvariant()}";
                spec.Data.Add(new ComposeData(RelRe().Replace(path, ".dat"), type));        // the game adds "151.rel"
                break;
            case "DLC_ITYP_REQUEST":
                AddStreamed(spec, f, spec.Content.Kind ?? spec.MapKind ?? ModCategory.Vehicle, streamed);
                if (spec.Files.LastOrDefault() is { } ytyp && ytyp.Source == f.FullPath)
                    spec.Data.Add(new ComposeData(ytyp.PackPath, type));
                return;
            default:
                if (ext is not (".meta" or ".xml" or ".txt" or ".ymt")) return;
                var fileName = ext == ".txt" ? Path.GetFileNameWithoutExtension(f.Name) : f.Name;
                if (!fileName.Contains('.')) fileName += ".meta";
                path = Unique(spec, $"common/data/{sub}{fileName.ToLowerInvariant()}");
                spec.Data.Add(new ComposeData(path, type, Persistent: type == "TEXTFILE_METAFILE"));
                if (ext != ".ymt") spec.Content.AddData(type, TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.FullPath), strict: false), f.Origin);
                break;
        }
        if (spec.Files.Any(x => x.PackPath.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        spec.Files.Add(new ComposeFile(f.FullPath, path));
    }

    private static string Unique(ComposeSpec spec, string path)
    {
        var p = path;
        for (int n = 2; spec.Files.Any(f => f.PackPath.Equals(p, StringComparison.OrdinalIgnoreCase)); n++)
            p = $"{Path.ChangeExtension(path, null)}_{n}{Path.GetExtension(path)}";
        return p;
    }

    private static void AddStreamed(ComposeSpec spec, DroppedFile f, ModCategory kind, Dictionary<string, DroppedFile> streamed)
    {
        var ext = PathUtil.SuffixLower(f.Name);
        if (kind is ModCategory.Map or ModCategory.Prop && (GameIndex.StreamedExts.Contains(ext) || IsMapFile(f.Name)))
        {
            var name = AddonContent.MapFileName(f.Name.Split('^')[^1]).ToLowerInvariant();
            if (name.EndsWith(".ymf", StringComparison.Ordinal)) spec.Content.HasManifest = true;
            if (streamed.TryGetValue(name, out var had))
            {
                if (!SameFile(had.FullPath, f.FullPath))
                    spec.Warnings.Add(L.T($"{name} is in the mod more than once — {had.Origin} is used, {f.Origin} is left out."));
                return;
            }
            streamed[name] = f;
            spec.Files.Add(new ComposeFile(f.FullPath, $"{MapImage}/{name}"));
            spec.Content.Streamed.Add(name);
            if (ResourceEditions.EditionOf(PathUtil.SuffixLower(name), ResourceVersion(f.FullPath)) is { } med) spec.Content.ModelEditions.Add(med);
            return;
        }
        if (!GameIndex.StreamedExts.Contains(ext) && ext is not ".awc") return;
        if (ext == ".awc")
        {
            AddData(spec, f, "AUDIO_WAVEPACK", "", streamed);
            return;
        }
        // FiveM spells a folder of the image archive with ^: a_m_y_x^head_000_r.ydd → a_m_y_x/head_000_r.ydd
        var inner = f.Name.ToLowerInvariant().Replace('^', '/');
        var image = kind == ModCategory.Ped ? "x64/peds.rpf" : "x64/vehicles.rpf";
        var path = $"{image}/{inner}";
        if (ClothingPath(f) is { } clothing)
        {
            path = clothing;
            inner = clothing[(ImageOf(clothing)!.Length + 1)..];
            if (ClothingNames.WearerOfYmt(f.Name) is { IsMp: true, Collection: not null } w) spec.Content.AddCollection(w.Ped, w.Collection);
        }
        if (streamed.TryGetValue(inner, out var first))
        {
            if (!SameFile(first.FullPath, f.FullPath))
                spec.Warnings.Add(L.T($"{f.Name} is in the mod more than once — {first.Origin} is used, {f.Origin} is left out."));
            return;
        }
        streamed[inner] = f;
        spec.Files.Add(new ComposeFile(f.FullPath, path));
        spec.Content.Streamed.Add(inner);
        if (ResourceEditions.EditionOf(ext, ResourceVersion(f.FullPath)) is { } ed) spec.Content.ModelEditions.Add(ed);
    }

    internal static int ResourceVersion(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            return fs.Read(head) == 8 && BitConverter.ToUInt32(head) == Rpf7.Rsc7Magic ? (int)BitConverter.ToUInt32(head[4..]) : -1;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    /// <summary>The files of a resource a manifest pattern names (<c>data/**/vehicles.meta</c>, <c>*.meta</c>, a plain path).</summary>
    private static List<DroppedFile> Glob(Dictionary<string, DroppedFile> byRel, string pattern)
    {
        var p = pattern.ToLowerInvariant().TrimEnd('/');
        if (byRel.TryGetValue(p, out var exact)) return [exact];
        if (!p.Contains('*') && !p.Contains('?'))
            return [.. byRel.Where(kv => kv.Key.StartsWith(p + "/", StringComparison.Ordinal)).Select(kv => kv.Value)];   // a folder
        var re = new Regex("^" + Regex.Escape(p).Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$");
        return [.. byRel.Where(kv => re.IsMatch(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value)];
    }

    // ================================================================ packing

    /// <summary>
    /// Lay the pack out and pack it into <paramref name="outDir"/>/dlc.rpf: content.xml and setup2.xml for
    /// <paramref name="device"/> (<c>dlc_mycar</c>), the files, the image archives, global.gxt2 in every
    /// language when there are labels. Legacy models are converted for Enhanced on the way. Not
    /// <paramref name="pack"/>ed: the tree is laid out in <paramref name="outDir"/> itself, its archives as folders.
    /// </summary>
    public static string Compose(ComposeSpec spec, string device, string outDir, GameEdition edition, Action<string> log,
                                 Func<uint, bool>? gameTypes = null, bool pack = true)
    {
        var tree = PathUtil.MakeTempDir();
        try
        {
            foreach (var f in spec.Files)
            {
                var dst = Path.Combine(tree, f.PackPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (IsMapFile(f.PackPath) && (f.Source.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || !IsResource(f.Source)))
                {
                    // a placement / archetype file as XML (CodeWalker / OpenIV export, sometimes saved as .ymap): built into the game's form
                    var name = Path.GetFileName(f.PackPath);
                    File.WriteAllBytes(dst, MapMeta.Write(MapMeta.Read(File.ReadAllBytes(f.Source), name), name));
                    log(L.T($"    {name}: built from its XML form."));
                }
                else PathUtil.Copy2(f.Source, dst);
            }
            if (spec.NewPeds.Count > 0)
            {
                Directory.CreateDirectory(Path.Combine(tree, "common", "data"));
                TextIo.WriteText(Path.Combine(tree, "common", "data", "peds.meta"), PedMeta.Build(spec.NewPeds));
                log(L.T($"    peds.meta written for {string.Join(", ", spec.NewPeds.Select(p => $"{p.Name} ({p.Gender.ToString().ToLowerInvariant()}{(p.Streamed ? ", streamed" : "")})"))}."));
            }
            var data = spec.Data.ToList();
            foreach (var c in spec.NewCollections) WriteCollection(spec, c, device, tree, data, log);
            if (spec.Content.Labels.Count > 0 && !data.Any(d => d.Type == "TEXTFILE_METAFILE"))
            {
                var gxt = Gxt2.Build(new Dictionary<string, string>(), spec.Content.Labels);
                foreach (var lang in DlcAssembler.Langs)
                {
                    var dir = Path.Combine(tree, "x64", "data", "lang", $"{lang}dlc.rpf");
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, "global.gxt2"), gxt);
                }
                TextIo.WriteText(Path.Combine(tree, "common", "data", "dlctext.meta"),
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<CExtraTextMetaFile>\n  <hasGlobalTextFile value=\"true\" />\n" +
                    "  <hasAdditionalText value=\"false\" />\n  <isTitleUpdate value=\"false\" />\n</CExtraTextMetaFile>\n");
                data.Add(new ComposeData("common/data/dlctext.meta", "TEXTFILE_METAFILE", Persistent: true));
                log(L.T($"    {spec.Content.Labels.Count} text label(s) → global.gxt2 in {DlcAssembler.Langs.Length} languages."));
            }
            var changeset = device.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase) ? device[4..] + "_AUTOGEN" : device + "_AUTOGEN";
            if (spec.Content.Kind == ModCategory.Map)
            {
                WriteManifest(spec, tree, data, gameTypes, log);
                FixInteriors(tree, log);
                TextIo.WriteText(Path.Combine(tree, "content.xml"), MapContentXml(device, changeset, data));
                TextIo.WriteText(Path.Combine(tree, "setup2.xml"), MapSetup2Xml(device, changeset));
            }
            else
            {
                TextIo.WriteText(Path.Combine(tree, "content.xml"),
                                 DlcAssembler.ContentXml(device, changeset, data.Select(d => (d.PackPath.Replace("x64/", "%PLATFORM%/"), d.Type, d.Persistent))));
                TextIo.WriteText(Path.Combine(tree, "setup2.xml"), DlcAssembler.Setup2Xml(device, changeset));
            }

            if (!pack)
            {
                // loose folders (the *.rpf ones to pack with CodeWalker): the models already in the edition's form
                foreach (var file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
                {
                    if (!Rpf7.IsResourceExt(Path.GetExtension(file))) continue;
                    var raw = File.ReadAllBytes(file);
                    if (ResourceEditions.NeedsConversion(file, raw, edition))
                        File.WriteAllBytes(file, ResourceEditions.ForEdition(raw, Path.GetFileName(file), edition));
                }
                if (Directory.Exists(outDir)) PathUtil.DeleteDir(outDir);
                foreach (var file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
                {
                    var dst = Path.Combine(outDir, Path.GetRelativePath(tree, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(file, dst);
                }
                log(L.T($"    Laid out unpacked in {outDir} — pack its *.rpf folders with CodeWalker."));
                return outDir;
            }

            // inner archives first (deepest first), each packed in place of its folder
            foreach (var dir in Directory.EnumerateDirectories(tree, "*.rpf", SearchOption.AllDirectories)
                                         .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)).ToList())
            {
                var tmp = dir + ".packing";
                var info = RpfPacker.PackFolder(dir, tmp, edition);
                PathUtil.DeleteDir(dir);
                File.Move(tmp, dir);
                var rel = Path.GetRelativePath(tree, dir).Replace('\\', '/');
                if (!rel.Contains("/lang/", StringComparison.Ordinal)) log(L.T($"    {rel}: {info.Files} file(s)."));
            }
            Directory.CreateDirectory(outDir);
            var dlc = Path.Combine(outDir, "dlc.rpf");
            var packed = RpfPacker.PackFolder(tree, dlc, edition);
            log(L.T($"    Packed dlc.rpf ({MergedPack.FmtSize(packed.Size)}, {packed.Files} file(s))."));
            return dlc;
        }
        finally
        {
            PathUtil.TryDeleteDir(tree);
        }
    }

    /// <summary>
    /// An MP collection's files, ymt and shop meta into the pack tree: loose models laid out under the collection's
    /// folders with the numbers it gives them, the ymt written for them; a shop meta when the mod has none.
    /// </summary>
    private static void WriteCollection(ComposeSpec spec, NewCollection c, string device, string tree, List<ComposeData> data, Action<string> log)
    {
        var dlc = c.NameFor(device);
        var full = $"{c.Ped}_{dlc}";
        if (c.Parts.Count > 0)
        {
            var image = ClothesImage(c.IsFemale);
            var numbering = c.Numbering();
            var laid = new List<ClothingPart>();
            foreach (var (source, part) in c.Parts)
            {
                if (!numbering.TryGetValue((part.Prop, part.Slot, part.Number), out var n)) continue;
                var name = ClothingNames.Renumber(part.Name, n);
                var dst = Path.Combine(tree, image.Replace('/', Path.DirectorySeparatorChar), part.Prop ? $"{c.Ped}_p_{dlc}" : full, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                PathUtil.Copy2(source, dst);
                laid.Add(ClothingNames.Parse(name)!);
            }
            var ymt = PedVariation.Empty(dlc);
            PedVariation.Add(ymt, PedVariation.DrawablesOf(laid).Select(d => d.Drawable));
            File.WriteAllBytes(Path.Combine(tree, image.Replace('/', Path.DirectorySeparatorChar), full + ".ymt"), PedVariation.Write(ymt));
            if (!data.Any(d => d.PackPath.Equals(image, StringComparison.OrdinalIgnoreCase)))
                data.Add(new ComposeData(image, "RPF_FILE", Persistent: true));
            var (comps, props) = PedVariation.Counts(ymt);
            log(L.T($"    Collection {full}: {comps.Sum()} model(s){(props.Sum() > 0 ? L.T($", {props.Sum()} prop(s)") : "")} — ymt written."));
        }
        if (spec.Content.Shops.Contains(full)) return;
        var creature = spec.Files.Select(f => Path.GetFileNameWithoutExtension(f.PackPath))
                           .Where(n => n.StartsWith("mp_creaturemetadata_", StringComparison.OrdinalIgnoreCase)).ToList();
        var creatureName = creature.Count == 1 ? "MP_CreatureMetadata_" + creature[0]["mp_creaturemetadata_".Length..] : "MP_CreatureMetadata_independence";
        var shop = $"common/data/{full}_shop.meta";
        var shopFile = Path.Combine(tree, shop.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(shopFile)!);
        TextIo.WriteText(shopFile, ShopMeta(c.Ped, dlc, creatureName));
        data.Add(new ComposeData(shop, "SHOP_PED_APPAREL_META_FILE"));
        log(L.T($"    {full}_shop.meta written (the mod has none)."));
    }

    // ================================================================ maps

    /// <summary>
    /// The pack manifest of a map: each placement loads the archetype files that define what it places (matched by the
    /// models' hashes). Archetype files no placement needs are requested for good instead. A manifest the mod comes with
    /// is made for FiveM, where every DLC is loaded — it's kept, but checked against the pack and the game
    /// (<paramref name="gameTypes"/>: the game has an archetypes file of that name hash; null — not known): entries for
    /// placements / archetype files the pack doesn't have are dropped, as are links to archetype files neither the pack
    /// nor the game has (a link the game can't resolve stops it at loading), and placements get the links they need.
    /// </summary>
    private static void WriteManifest(ComposeSpec spec, string tree, List<ComposeData> data, Func<uint, bool>? gameTypes, Action<string> log)
    {
        var typeFiles = new List<(string Name, string PackPath, HashSet<uint> Defines)>();
        foreach (var f in spec.Files.Where(f => f.PackPath.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)))
        {
            var doc = MapMeta.Read(File.ReadAllBytes(f.Source), Path.GetFileName(f.PackPath));
            typeFiles.Add((Path.GetFileNameWithoutExtension(f.PackPath), f.PackPath,
                           [.. MapMeta.Archetypes(doc).Select(MapMeta.Hash)]));
        }
        var deps = new List<(string Ymap, IReadOnlyList<string> Ytyps)>();
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in spec.Files.Where(f => f.PackPath.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(f.PackPath);
            var info = MapMeta.Ymap(MapMeta.Read(File.ReadAllBytes(f.Source), name), name);
            var uses = info.Archetypes.Select(MapMeta.Hash).ToHashSet();
            var types = typeFiles.Where(t => t.Defines.Overlaps(uses)).Select(t => t.Name).ToList();
            foreach (var t in types) needed.Add(t);
            deps.Add((info.Name, types));
        }
        // an archetype file a placement needs loads with it (the manifest); one no placement needs is requested for good
        data.RemoveAll(d => d.Type == "DLC_ITYP_REQUEST" && needed.Contains(Path.GetFileNameWithoutExtension(d.PackPath)));
        foreach (var t in typeFiles.Where(t => !needed.Contains(t.Name) && !data.Any(d => d.PackPath.Equals(t.PackPath, StringComparison.OrdinalIgnoreCase))))
            data.Add(new ComposeData(t.PackPath, "DLC_ITYP_REQUEST"));
        var path = Path.Combine(tree, MapImage.Replace('/', Path.DirectorySeparatorChar), ManifestName);
        if (!File.Exists(path))
        {
            if (deps.All(d => d.Ytyps.Count == 0)) return;                  // only the game's own props: nothing to bind
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, MapMeta.Write(MapMeta.Manifest(deps), ManifestName));
            log(L.T($"    {ManifestName} written: {string.Join(", ", deps.Where(d => d.Ytyps.Count > 0).Select(d => $"{d.Ymap} → {string.Join(" + ", d.Ytyps)}"))}."));
            return;
        }

        MapMeta.Know(typeFiles.Select(t => t.Name).Concat(deps.Select(d => d.Ymap)));
        var manifest = MapMeta.Read(File.ReadAllBytes(path), ManifestName);
        var root = manifest.Root!;
        var ymaps = deps.Select(d => MapMeta.Hash(d.Ymap)).ToHashSet();
        var packTypes = typeFiles.Select(t => MapMeta.Hash(t.Name)).ToHashSet();
        bool Resolves(uint h) => packTypes.Contains(h) || gameTypes is null || gameTypes(h);
        static string NameOf(XElement e) => e.Value.Trim();
        var dropped = new List<string>();
        int added = 0;

        void Check(string list, string key, Func<uint, bool> keep)
        {
            foreach (var item in root.Element(list)?.Elements("Item").ToList() ?? [])
            {
                if (item.Element(key) is not { } k || !keep(MapMeta.Hash(NameOf(k))))
                {
                    dropped.Add(item.Element(key) is { } n ? NameOf(n) : list);
                    item.Remove();
                    continue;
                }
                foreach (var dep in item.Element("itypDepArray")?.Elements("Item").ToList() ?? [])
                    if (!Resolves(MapMeta.Hash(NameOf(dep))))
                    {
                        dropped.Add(NameOf(dep));
                        dep.Remove();
                    }
            }
        }
        Check("imapDependencies_2", "imapName", ymaps.Contains);
        Check("itypDependencies_2", "itypName", packTypes.Contains);

        if (root.Element("imapDependencies_2") is not { } imaps)
            root.Add(imaps = new XElement("imapDependencies_2", new XAttribute("itemType", "CImapDependencies")));
        foreach (var (ymap, types) in deps.Where(d => d.Ytyps.Count > 0))
        {
            var item = imaps.Elements("Item").FirstOrDefault(i => MapMeta.Hash(NameOf(i.Element("imapName")!)) == MapMeta.Hash(ymap));
            if (item is null)
                imaps.Add(item = new XElement("Item", new XElement("imapName", ymap), new XElement("manifestFlags"), new XElement("itypDepArray")));
            if (item.Element("itypDepArray") is not { } arr) item.Add(arr = new XElement("itypDepArray"));
            foreach (var t in types.Where(t => !arr.Elements("Item").Any(i => MapMeta.Hash(NameOf(i)) == MapMeta.Hash(t))))
            {
                arr.Add(new XElement("Item", t));
                added++;
            }
        }
        if (dropped.Count == 0 && added == 0) return;
        File.WriteAllBytes(path, MapMeta.Write(manifest, ManifestName));
        if (dropped.Count > 0)
            log(L.T($"    {ManifestName}: dropped what neither the pack nor the game has (the game would stop at loading): {string.Join(", ", dropped.Distinct().Take(8))}{(dropped.Distinct().Count() > 8 ? ", …" : "")}."));
        if (added > 0) log(L.T($"    {ManifestName}: {added} missing link(s) from placements to their archetype files added."));
    }

    /// <summary>
    /// The interiors (MLOs) in the pack manifest, as Rockstar's interior archives name them: <c>Interiors</c> — each
    /// interior → its bounds files (<c>X.ybn</c>, <c>hi@X.ybn</c> of its name / physics dictionary), and the archetypes
    /// file that defines it flagged <c>INTERIOR_DATA</c>. Written from the archetypes files themselves: the manifest a
    /// FiveM map comes with is often stale there (moreo_cafecutev2 names an interior renamed since).
    /// </summary>
    private static void FixInteriors(string tree, Action<string> log)
    {
        var image = Path.Combine(tree, MapImage.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(image)) return;
        var types = Directory.EnumerateFiles(image, "*.ytyp").ToList();
        var bounds = Directory.EnumerateFiles(image, "*.ybn").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        MapMeta.Know(bounds.Concat(types.Select(Path.GetFileNameWithoutExtension).OfType<string>()));
        var interiors = new List<(string Name, List<string> Bounds)>();
        var interiorTypes = new List<string>();
        foreach (var t in types)
        {
            var doc = MapMeta.Read(File.ReadAllBytes(t), Path.GetFileName(t));
            var mlos = (doc.Root?.Element("archetypes")?.Elements("Item") ?? [])
                       .Where(i => (string?)i.Attribute("type") == "CMloArchetypeDef").ToList();
            if (mlos.Count == 0) continue;
            interiorTypes.Add(Path.GetFileNameWithoutExtension(t));
            foreach (var mlo in mlos)
            {
                var name = mlo.Element("name")?.Value.Trim() ?? "";
                var keys = new[] { name, mlo.Element("physicsDictionary")?.Value.Trim() ?? "" }
                           .Where(s => s.Length > 0).Select(MapMeta.Hash).ToHashSet();
                var own = bounds.Where(b => keys.Contains(MapMeta.Hash(b)) ||
                                            b.StartsWith("hi@", StringComparison.OrdinalIgnoreCase) && keys.Contains(MapMeta.Hash(b[3..])))
                                .OrderBy(b => b.StartsWith("hi@", StringComparison.OrdinalIgnoreCase)).ToList();
                if (name.Length > 0) interiors.Add((name, own));
            }
        }
        if (interiorTypes.Count == 0) return;

        var path = Path.Combine(image, ManifestName);
        var manifest = File.Exists(path) ? MapMeta.Read(File.ReadAllBytes(path), ManifestName) : MapMeta.Manifest([]);
        var root = manifest.Root!;
        if (root.Element("itypDependencies_2") is not { } deps)
            root.Add(deps = new XElement("itypDependencies_2", new XAttribute("itemType", "CItypDependencies")));
        foreach (var stem in interiorTypes)
        {
            var item = deps.Elements("Item").FirstOrDefault(i => MapMeta.Hash(i.Element("itypName")?.Value.Trim() ?? "") == MapMeta.Hash(stem));
            if (item is null)
                deps.Add(item = new XElement("Item", new XElement("itypName", stem), new XElement("manifestFlags"), new XElement("itypDepArray")));
            item.SetElementValue("manifestFlags", "INTERIOR_DATA");
        }
        if (root.Element("Interiors") is not { } list)
            root.Add(list = new XElement("Interiors", new XAttribute("itemType", "CInteriorBoundsFiles")));
        list.RemoveNodes();
        list.Add(interiors.Where(i => i.Bounds.Count > 0).Select(i => new XElement("Item",
            new XElement("Name", i.Name),
            new XElement("Bounds", i.Bounds.Select(b => new XElement("Item", b))))));
        File.WriteAllBytes(path, MapMeta.Write(manifest, ManifestName));
        log(L.T($"    Interiors: {string.Join(", ", interiors.Select(i => i.Bounds.Count > 0 ? $"{i.Name} → {string.Join(" + ", i.Bounds)}" : i.Name))} — laid out with their collisions."));
    }

    /// <summary>
    /// content.xml of a map pack, as Rockstar mounts the map parts of its DLCs (mpapartment's APA_GTA5_CITYE_SUNSET): a map
    /// changeset (<c>mapChangeSetData</c>, <c>associatedMap MO_JIM_L11</c>) run with the map (<see cref="MapSetup2Xml"/>:
    /// GROUP_MAP), so the archive comes after the DLCs' own map parts — a FiveM interior's manifest binds it to their
    /// archetypes files (apa_int_mp_h_03, santamon_metadata_001_strm…): mounted at startup, before them, the game stopped
    /// loading with a fatal error (moreo_cafecutev2 on Enhanced). The archive is map data and overlays the game's files: a
    /// map that remodels a building ships the game's model / collision under its own name.
    /// </summary>
    public static string MapContentXml(string device, string changeset, IEnumerable<ComposeData> data)
    {
        var list = data.Select(d => (Name: $"{device}:/{d.PackPath.Replace("x64/", "%PLATFORM%/")}", d)).ToList();
        var items = string.Join("\n", list.Select(x =>
            "    <Item>\n" +
            $"      <filename>{x.Name}</filename>\n" +
            $"      <fileType>{x.d.Type}</fileType>\n" +
            $"      <overlay value=\"{(x.d.Type == "RPF_FILE" ? "true" : "false")}\" />\n" +
            "      <disabled value=\"true\" />\n" +
            $"      <persistent value=\"{(x.d.Persistent ? "true" : "false")}\" />\n" +
            (x.d.Contents is { } c ? $"      <contents>{c}</contents>\n" : "") +
            "    </Item>"));
        var enable = string.Join("\n", list.Select(x => $"            <Item>{x.Name}</Item>"));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
               "<CDataFileMgr__ContentsOfDataFileXml>\n" +
               "  <disabledFiles />\n  <includedXmlFiles />\n  <includedDataFiles />\n" +
               "  <dataFiles>\n" + items + "\n  </dataFiles>\n" +
               "  <contentChangeSets>\n    <Item>\n" +
               $"      <changeSetName>{changeset}</changeSetName>\n" +
               "      <mapChangeSetData>\n        <Item>\n" +
               "          <associatedMap>MO_JIM_L11</associatedMap>\n" +
               "          <filesToInvalidate />\n          <filesToDisable />\n" +
               "          <filesToEnable>\n" + enable + "\n          </filesToEnable>\n" +
               "        </Item>\n      </mapChangeSetData>\n" +
               "      <requiresLoadingScreen value=\"true\" />\n" +
               "      <loadingScreenContext>LOADINGSCREEN_CONTEXT_LAST_FRAME</loadingScreenContext>\n" +
               "    </Item>\n" +
               "  </contentChangeSets>\n  <patchFiles />\n" +
               "</CDataFileMgr__ContentsOfDataFileXml>\n";
    }

    /// <summary>setup2.xml of a map pack: an add-on (compat) pack like the others, its map changeset run with the map.</summary>
    public static string MapSetup2Xml(string device, string changeset) =>
        DlcAssembler.Setup2Xml(device, changeset).Replace("<NameHash>GROUP_STARTUP</NameHash>", "<NameHash>GROUP_MAP</NameHash>");

    /// <summary>A shop meta that registers a collection and lists no shop items (the clothes are picked in trainers / menus).</summary>
    public static string ShopMeta(string ped, string dlc, string creature) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ShopPedApparel>\n" +
        $"  <pedName>{ped}</pedName>\n  <dlcName>{dlc}</dlcName>\n  <fullDlcName>{ped}_{dlc}</fullDlcName>\n" +
        "  <eCharacter>SCR_CHAR_MULTIPLAYER</eCharacter>\n" +
        $"  <creatureMetaData>{creature}</creatureMetaData>\n" +
        "  <pedOutfits />\n  <pedComponents />\n  <pedProps />\n</ShopPedApparel>\n";
}
