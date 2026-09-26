using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>One kind of mod found in a drop, with how sure the detector is and why.</summary>
public sealed record Detection(ModCategory Category, int Score, IReadOnlyList<string> Evidence);

/// <summary>Everything the detector found in a drop — several kinds can share one archive.</summary>
public sealed class DetectionReport
{
    /// <summary>Most likely first.</summary>
    public List<Detection> Found { get; } = [];

    public Detection? Primary => Found.Count > 0 ? Found[0] : null;

    public bool Has(ModCategory c) => Found.Any(d => d.Category == c);

    /// <summary>"Script / plugin (ScriptHookVDotNet script MyMod.dll), Vehicle (…)" — for messages.</summary>
    public string Summary() => Found.Count == 0
        ? "nothing ModDrop V recognises"
        : string.Join("; ", Found.Select(d => $"{d.Category.DisplayName()} ({string.Join(", ", d.Evidence.Take(2))})"));
}

/// <summary>
/// Detector v1: tells what kind of mod a drop holds by its content, not by the archive name —
/// file types, RAGE resource and PE headers, the assemblies a .NET plugin references, the root
/// tag of XML data files, the data files a finished dlc.rpf declares, fxmanifest entries, OIV
/// packages and folder names that mirror the game's archives. Each clue adds to a score per
/// category; everything that scored is reported, strongest first.
/// </summary>
public static partial class ModDetector
{
    /// <summary>Extensions worth unpacking from an archive just so the detector can look at them.</summary>
    public static readonly HashSet<string> SniffedExt =
    [
        ".asi", ".dll", ".cs", ".vb", ".ini",
        ".ymap", ".ytyp", ".ymf", ".ybn", ".ymt", ".yld", ".ycd", ".ynd", ".ynv", ".ypt",
        ".awc", ".rel", ".gfx",
    ];

    private const long MaxSniffBytes = 16L << 20;

    [GeneratedRegex(@"^(head|berd|hair|uppr|lowr|hand|feet|teef|accs|task|decl|jbib)_\d{3}_[a-z]", RegexOptions.IgnoreCase)]
    private static partial Regex PedComponentRe();

    [GeneratedRegex(@"^p_(head|eyes|ears|mouth|lhand|rhand|lwrist|rwrist|hip|lfoot|rfoot)_\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex PedPropRe();

    [GeneratedRegex(@"^(a_[mfc]_|s_[mf]_|u_[mf]_|g_[mf]_|csb_|cs_|ig_|hc_|mp_[mfgs]_|player_(zero|one|two))", RegexOptions.IgnoreCase)]
    private static partial Regex PedModelRe();

    [GeneratedRegex(@"(^|[\\/])(mp_[mf]_freemode_01|player_(zero|one|two))([\\/^_]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ClothingOwnerRe();

    [GeneratedRegex(@"data_file\s*\(?\s*['""]([A-Z0-9_]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex FxDataFileRe();

    [GeneratedRegex(@"<\s*([A-Za-z_][\w.:-]*)")]
    private static partial Regex FirstTagRe();

    [GeneratedRegex(@"<fileType>\s*([A-Z0-9_]+)\s*</fileType>")]
    private static partial Regex FileTypeRe();

    /// <summary>content.xml / fxmanifest data file types and the mod kind they belong to.</summary>
    private static readonly Dictionary<string, ModCategory> DataFileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WEAPONINFO_FILE"] = ModCategory.Weapon,
        ["WEAPON_METADATA_FILE"] = ModCategory.Weapon,
        ["WEAPONCOMPONENTSINFO_FILE"] = ModCategory.Weapon,
        ["VEHICLE_METADATA_FILE"] = ModCategory.Vehicle,
        ["HANDLING_FILE"] = ModCategory.Vehicle,
        ["VEHICLE_VARIATION_FILE"] = ModCategory.Vehicle,
        ["CARCOLS_FILE"] = ModCategory.Vehicle,
        ["PED_METADATA_FILE"] = ModCategory.Ped,
        ["SHOP_PED_APPAREL_META_FILE"] = ModCategory.Clothing,
        ["PED_PERSONALITY_FILE"] = ModCategory.Ped,
        ["DLC_ITYP_REQUEST"] = ModCategory.Prop,
    };

    /// <summary>XML root tags of game data files (weapon ones come from <see cref="Overrides.RootTags"/>).</summary>
    private static readonly Dictionary<string, (ModCategory Cat, int Score, string What)> RootTags = new(StringComparer.Ordinal)
    {
        ["CVehicleModelInfo__InitDataList"] = (ModCategory.Vehicle, 4, "vehicles.meta"),
        ["CHandlingDataMgr"] = (ModCategory.Vehicle, 3, "handling.meta"),
        ["CVehicleModelInfoVariation"] = (ModCategory.Vehicle, 2, "carvariations.meta"),
        ["CVehicleModelInfoVarGlobal"] = (ModCategory.Vehicle, 2, "carcols.meta"),
        ["CPedModelInfo__InitDataList"] = (ModCategory.Ped, 4, "peds.meta"),
        ["ShopPedApparel"] = (ModCategory.Clothing, 4, "shop meta"),
        ["CPedVariationInfo"] = (ModCategory.Clothing, 2, "ped variations (.ymt as XML)"),
        ["CMapTypes"] = (ModCategory.Prop, 4, "archetypes (.ytyp as XML)"),
        ["CMapData"] = (ModCategory.Map, 4, "map placement (.ymap as XML)"),
        ["SpoonerPlacements"] = (ModCategory.Map, 5, "Menyoo map"),
    };

    /// <summary>Menu / helper libraries scripts ship with — a dependency, not a script of their own.</summary>
    private static readonly string[] ScriptLibraries = ["LemonUI", "NativeUI", "iFruitAddon2", "RAGENativeUI", "NAudio", "Newtonsoft.Json"];

    private static readonly HashSet<string> WeaponSlots =
        ["weapon.meta", "weaponcomponents.meta", "weaponarchetypes.meta", "shop_weapon.meta", "weaponanimations.meta"];

    private sealed class Scores
    {
        private readonly Dictionary<ModCategory, (int Score, List<string> Evidence)> _s = [];

        public void Add(ModCategory c, int score, string evidence)
        {
            if (!_s.TryGetValue(c, out var e)) _s[c] = e = (0, []);
            if (!e.Evidence.Contains(evidence)) e.Evidence.Add(evidence);
            _s[c] = (e.Score + score, e.Evidence);
        }

        public DetectionReport Report()
        {
            var r = new DetectionReport();
            r.Found.AddRange(_s.OrderByDescending(kv => kv.Value.Score).ThenBy(kv => kv.Key)
                               .Select(kv => new Detection(kv.Key, kv.Value.Score, kv.Value.Evidence)));
            return r;
        }
    }

    public static DetectionReport Detect(DroppedSource src) =>
        Detect(src.Files.Where(f => !f.InBackupDir).Select(f => (f.FullPath, f.Origin)));

    /// <summary>Detect over plain files (Origin = the path shown as evidence and used for folder hints).</summary>
    public static DetectionReport Detect(IEnumerable<(string FullPath, string Origin)> files)
    {
        var s = new Scores();
        int counted = 0;
        foreach (var (full, origin) in files)
        {
            var name = Path.GetFileName(origin);
            var ext = PathUtil.SuffixLower(name);
            try
            {
                Look(s, full, origin, name, ext);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
                                           or InvalidDataException)
            {
                // an unreadable file is no clue
            }
            if (++counted > 20000) break;
        }
        return s.Report();
    }

    private static void Look(Scores s, string full, string origin, string name, string ext)
    {
        var stem = Path.GetFileNameWithoutExtension(name);

        // what lives in a scripts / plugins folder is a script's own data, whatever its format
        var dirs = origin.Replace('\\', '/').Split('/').SkipLast(1);
        if (ext is not (".asi" or ".dll" or ".cs" or ".vb") &&
            dirs.Any(d => d.Equals("scripts", StringComparison.OrdinalIgnoreCase) || d.Equals("plugins", StringComparison.OrdinalIgnoreCase)))
            return;

        // a folder named like a game archive: loose files meant to replace what is inside it
        if (origin.Replace('\\', '/').Split('/').SkipLast(1).Any(seg => seg.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            && (InputScanner.ResourceExt.Contains(ext) || ext is ".awc" or ".meta" or ".xml" or ".gxt2" or ".ymt"))
            s.Add(ModCategory.Replacement, 4, $"folders mirror the game's archives ({DirOf(origin)})");

        switch (ext)
        {
            case ".ydr" or ".ytd" or ".ydd" or ".yft":
                if (!IsResource(full)) return;
                LookResource(s, origin, name, stem, ext);
                return;
            case ".rpf":
                LookRpf(s, full, name);
                return;
            case ".asi":
                if (IsPe(full)) s.Add(ModCategory.Script, 4, $"ASI plugin {name}");
                return;
            case ".dll":
                LookDll(s, full, name);
                return;
            case ".cs" or ".vb":
                var code = ReadText(full);
                if (code is not null && (code.Contains("using GTA", StringComparison.Ordinal) ||
                                         code.Contains("Imports GTA", StringComparison.OrdinalIgnoreCase)))
                    s.Add(ModCategory.Script, 4, $"ScriptHookVDotNet script {name}");
                return;
            case ".ytyp":
                s.Add(ModCategory.Prop, 3, $"archetypes {name}");
                return;
            case ".ymap":
                s.Add(ModCategory.Map, 3, $"map placement {name}");
                return;
            case ".ymf":
                s.Add(ModCategory.Map, 1, $"map manifest {name}");
                return;
            case ".awc" or ".rel":
                s.Add(ModCategory.Replacement, 2, $"game audio {name}");
                return;
            case ".lua" when stem.Equals("fxmanifest", StringComparison.OrdinalIgnoreCase)
                             || stem.Equals("__resource", StringComparison.OrdinalIgnoreCase):
                LookFxManifest(s, full, name);
                return;
            case ".meta" or ".xml" or ".txt" or ".ymt":
                LookXml(s, full, name);
                return;
        }
    }

    private static void LookResource(Scores s, string origin, string name, string stem, string ext)
    {
        if (name.StartsWith("w_", StringComparison.OrdinalIgnoreCase))
        {
            s.Add(ModCategory.Weapon, 3, "weapon models (w_*)");
            return;
        }
        bool pedPart = PedComponentRe().IsMatch(stem.Split('^')[^1]) || PedPropRe().IsMatch(stem.Split('^')[^1]);
        if (ClothingOwnerRe().IsMatch(origin) || (stem.Contains('^') && pedPart))
        {
            s.Add(ModCategory.Clothing, 3, pedPart ? $"clothing parts ({name})" : $"character files ({name})");
            return;
        }
        if (pedPart)
        {
            s.Add(ModCategory.Ped, 2, $"ped components ({name})");
            return;
        }
        if (PedModelRe().IsMatch(stem))
        {
            s.Add(ModCategory.Ped, 3, $"ped model {name}");
            return;
        }
        if (ext == ".yft")
        {
            s.Add(ModCategory.Vehicle, 3, $"vehicle model {name}");
            return;
        }
        if (ext == ".ytd" && (stem.Contains("livery", StringComparison.OrdinalIgnoreCase) ||
                              stem.Contains("_sign_", StringComparison.OrdinalIgnoreCase)))
        {
            s.Add(ModCategory.Livery, 3, $"livery texture {name}");
            return;
        }
        if (ext == ".ydr") s.Add(ModCategory.Prop, 1, $"model {name}");
    }

    private static void LookRpf(Scores s, string full, string name)
    {
        using var arc = RpfArchive.Open(full);
        var tree = arc.Tree();
        var content = tree.FirstOrDefault(t => !t.IsDir && t.Path.Equals("content.xml", StringComparison.OrdinalIgnoreCase));
        if (content is null || !tree.Any(t => !t.IsDir && t.Path.Equals("setup2.xml", StringComparison.OrdinalIgnoreCase)))
        {
            s.Add(ModCategory.Replacement, 2, $"game archive {name}");
            return;
        }
        var text = TextIo.DecodeUtf8Sig(arc.ReadContent(content.Entry), strict: false);
        bool any = false;
        foreach (Match m in FileTypeRe().Matches(text))
            if (DataFileTypes.TryGetValue(m.Groups[1].Value, out var cat))
            {
                s.Add(cat, 4, $"add-on pack {name} ({m.Groups[1].Value})");
                any = true;
            }
        if (!any)
        {
            if (tree.Any(t => t.Path.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)))
                s.Add(ModCategory.Map, 3, $"add-on pack {name} (map)");
            else
                s.Add(ModCategory.Replacement, 1, $"add-on pack {name}");
        }
    }

    private static void LookDll(Scores s, string full, string name)
    {
        if (name.Equals("ScriptHookV.dll", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ScriptHookVDotNet", StringComparison.OrdinalIgnoreCase))
        {
            s.Add(ModCategory.Script, 1, $"script hook {name}");
            return;
        }
        if (ScriptLibraries.Any(l => name.StartsWith(l, StringComparison.OrdinalIgnoreCase)))
        {
            s.Add(ModCategory.Script, 1, $"script library {name}");
            return;
        }
        using var fs = File.OpenRead(full);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata) return;                                      // a native helper library
        var md = pe.GetMetadataReader();
        var refs = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
        if (refs.Any(r => r.StartsWith("ScriptHookVDotNet", StringComparison.OrdinalIgnoreCase)))
            s.Add(ModCategory.Script, 4, $"ScriptHookVDotNet script {name}");
        else if (refs.Any(r => r.Equals("RagePluginHookSDK", StringComparison.OrdinalIgnoreCase) ||
                               r.Equals("RagePluginHook", StringComparison.OrdinalIgnoreCase)))
            s.Add(ModCategory.Script, 4, $"RAGE Plugin Hook plugin {name}");
    }

    private static void LookFxManifest(Scores s, string full, string name)
    {
        var text = ReadText(full);
        if (text is null) return;
        foreach (Match m in FxDataFileRe().Matches(text))
            if (DataFileTypes.TryGetValue(m.Groups[1].Value, out var cat))
                s.Add(cat, 3, $"FiveM resource ({m.Groups[1].Value})");
    }

    private static void LookXml(Scores s, string full, string name)
    {
        var text = ReadText(full);
        if (text is null) return;
        var root = RootTag(text);
        if (root is null) return;

        if (root.Equals("package", StringComparison.OrdinalIgnoreCase) && text.Contains("<metadata", StringComparison.OrdinalIgnoreCase))
        {
            s.Add(ModCategory.Package, 6, $"OIV package ({name})");
            return;
        }
        if (Overrides.RootTags.TryGetValue(root, out var slot))
        {
            if (WeaponSlots.Contains(slot)) s.Add(ModCategory.Weapon, 3, $"weapon config {name}");
            else if (slot == "content.xml")
                foreach (Match m in FileTypeRe().Matches(text))
                    if (DataFileTypes.TryGetValue(m.Groups[1].Value, out var cat))
                        s.Add(cat, 2, $"content.xml ({m.Groups[1].Value})");
            return;
        }
        if (RootTags.TryGetValue(root, out var hit))
        {
            s.Add(hit.Cat, hit.Score, $"{hit.What} ({name})");
            return;
        }
        if (root == "Map" && text.Contains("<Objects", StringComparison.Ordinal))
            s.Add(ModCategory.Map, 5, $"Map Editor map ({name})");
    }

    /// <summary>The first element name of an XML text (after the declaration and comments), or null.</summary>
    internal static string? RootTag(string text)
    {
        int i = 0;
        while (true)
        {
            i = text.IndexOf('<', i);
            if (i < 0 || i + 1 >= text.Length) return null;
            char c = text[i + 1];
            if (c == '?') { i = text.IndexOf("?>", i, StringComparison.Ordinal); }
            else if (text.AsSpan(i).StartsWith("<!--")) { i = text.IndexOf("-->", i, StringComparison.Ordinal); }
            else if (c == '!') { i = text.IndexOf('>', i); }
            else
            {
                var m = FirstTagRe().Match(text, i);
                return m.Success && m.Index == i ? m.Groups[1].Value : null;
            }
            if (i < 0) return null;
        }
    }

    private static string? ReadText(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length > MaxSniffBytes) return null;
        return TextIo.DecodeUtf8Sig(File.ReadAllBytes(path), strict: false);
    }

    private static bool IsResource(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return fs.Read(head) == 4 && BitConverter.ToUInt32(head) == Rpf7.Rsc7Magic;
    }

    private static bool IsPe(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[2];
        return fs.Read(head) == 2 && head[0] == (byte)'M' && head[1] == (byte)'Z';
    }

    private static string DirOf(string origin)
    {
        var o = origin.Replace('\\', '/');
        int i = o.LastIndexOf('/');
        return i < 0 ? "." : o[..i];
    }
}
