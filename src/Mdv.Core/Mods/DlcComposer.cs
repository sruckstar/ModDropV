using System.Text.RegularExpressions;
using Mdv.Core.Index;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>A file going into a composed pack; <see cref="PackPath"/> may run through archives (<c>x64/vehicles.rpf/adder.yft</c>).</summary>
public sealed record ComposeFile(string Source, string PackPath);

/// <summary>A content.xml entry of a composed pack: the path inside it (without the device) and its data file type.</summary>
public sealed record ComposeData(string PackPath, string Type, bool Persistent = false);

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
                              .Where(g => g.Key is not null).ToList();
            bool several = groups.Count > 1;
            foreach (var g in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                AddResource(spec, g.Key!, [.. g], several, streamed);
        }
        else AddLoose(spec, files, streamed);

        if (spec.Content.Kind is null && spec.Data.Count == 0) return null;
        AddImages(spec);
        return spec;
    }

    public const string PedImage = "x64/peds.rpf";
    public const string StreamedPedImage = "x64/streamedpeds.rpf";

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
                spec.Warnings.Add($"«{name}» has no {name}.ymt (the list of its clothes) — the game can't dress it: it may be invisible or crash.");
            if (!Has(".ydd") && parts.Count == 0)
                spec.Warnings.Add($"«{name}» has no {name}.ydd and no folder of components — there is nothing to show.");
            var ped = new NewPed(name, isStreamed, own.Any(f => f.Name.Equals(name + "_p.ydd", StringComparison.OrdinalIgnoreCase)), PedMeta.Guess(name));
            spec.NewPeds.Add(ped);
            spec.Content.Peds.Add(new AddonPed(name, null));
        }
        foreach (var f in files.Where(f => !used.Contains(f) && PathUtil.SuffixLower(f.Name) is ".yft" or ".ydd"))
            spec.Warnings.Add($"{f.Origin} belongs to none of its peds — left out.");

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

        foreach (Match m in DataFileRe().Matches(text))
        {
            var (type, pattern) = (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.Replace('\\', '/').TrimStart('.', '/'));
            var hits = Glob(byRel, pattern);
            if (hits.Count == 0 && (AudioConfigTypes.Contains(type) || type == "AUDIO_WAVEPACK"))
                hits = byRel.Where(kv => kv.Key.StartsWith(pattern.ToLowerInvariant().TrimEnd('/'), StringComparison.Ordinal)).Select(kv => kv.Value).ToList();
            if (hits.Count == 0)
            {
                spec.Warnings.Add($"{name}: fxmanifest names {pattern} ({type}), but there is no such file — left out.");
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
        if (!metas.Any(m => m.Type is "VEHICLE_METADATA_FILE" or "PED_METADATA_FILE")) return;
        var seenMeta = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, type) in metas)
        {
            if (!seenMeta.Add(type + "|" + Path.GetFileNameWithoutExtension(f.Name)))
            {
                spec.Warnings.Add($"{f.Origin}: a second {f.Name} — left out (drop the folder of the version you want).");
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
        files.Any(f => PathUtil.SuffixLower(f.Name) == ".yft" && !f.Name.Contains('^')) &&
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
                AddStreamed(spec, f, spec.Content.Kind ?? ModCategory.Vehicle, streamed);
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
        if (streamed.TryGetValue(inner, out var first))
        {
            if (!SameFile(first.FullPath, f.FullPath))
                spec.Warnings.Add($"{f.Name} is in the mod more than once — {first.Origin} is used, {f.Origin} is left out.");
            return;
        }
        streamed[inner] = f;
        spec.Files.Add(new ComposeFile(f.FullPath, path));
        spec.Content.Streamed.Add(inner);
        if (ResourceEditions.EditionOf(ext, ResourceVersion(f.FullPath)) is { } ed) spec.Content.ModelEditions.Add(ed);
    }

    private static int ResourceVersion(string path)
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
    /// language when there are labels. Legacy models are converted for Enhanced on the way.
    /// </summary>
    public static string Compose(ComposeSpec spec, string device, string outDir, GameEdition edition, Action<string> log)
    {
        var tree = PathUtil.MakeTempDir();
        try
        {
            foreach (var f in spec.Files)
            {
                var dst = Path.Combine(tree, f.PackPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                PathUtil.Copy2(f.Source, dst);
            }
            if (spec.NewPeds.Count > 0)
            {
                Directory.CreateDirectory(Path.Combine(tree, "common", "data"));
                TextIo.WriteText(Path.Combine(tree, "common", "data", "peds.meta"), PedMeta.Build(spec.NewPeds));
                log($"    peds.meta written for {string.Join(", ", spec.NewPeds.Select(p => $"{p.Name} ({p.Gender.ToString().ToLowerInvariant()}{(p.Streamed ? ", streamed" : "")})"))}.");
            }
            var data = spec.Data.ToList();
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
                log($"    {spec.Content.Labels.Count} text label(s) → global.gxt2 in {DlcAssembler.Langs.Length} languages.");
            }
            var changeset = device.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase) ? device[4..] + "_AUTOGEN" : device + "_AUTOGEN";
            TextIo.WriteText(Path.Combine(tree, "content.xml"),
                             DlcAssembler.ContentXml(device, changeset, data.Select(d => (d.PackPath.Replace("x64/", "%PLATFORM%/"), d.Type, d.Persistent))));
            TextIo.WriteText(Path.Combine(tree, "setup2.xml"), DlcAssembler.Setup2Xml(device, changeset));

            // inner archives first (deepest first), each packed in place of its folder
            foreach (var dir in Directory.EnumerateDirectories(tree, "*.rpf", SearchOption.AllDirectories)
                                         .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)).ToList())
            {
                var tmp = dir + ".packing";
                var info = RpfPacker.PackFolder(dir, tmp, edition);
                PathUtil.DeleteDir(dir);
                File.Move(tmp, dir);
                var rel = Path.GetRelativePath(tree, dir).Replace('\\', '/');
                if (!rel.Contains("/lang/", StringComparison.Ordinal)) log($"    {rel}: {info.Files} file(s).");
            }
            Directory.CreateDirectory(outDir);
            var dlc = Path.Combine(outDir, "dlc.rpf");
            var packed = RpfPacker.PackFolder(tree, dlc, edition);
            log($"    Packed dlc.rpf ({MergedPack.FmtSize(packed.Size)}, {packed.Files} file(s)).");
            return dlc;
        }
        finally
        {
            PathUtil.TryDeleteDir(tree);
        }
    }
}
