using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core;

/// <summary>Result of a build, whichever route produced it.</summary>
public sealed class BuildResult
{
    public required string Root { get; init; }
    public string? DlcRpf { get; init; }
    public bool Packed { get; init; }
    public bool Merged { get; init; }
    public bool Prebuilt { get; init; }
    public List<(string Src, string Dst)> Assets { get; init; } = [];
    public JsonObject Manifest { get; init; } = new();
    public List<string> WeaponsInPack { get; init; } = [];
    public List<string> Packs { get; init; } = [];
    public string? InstalledTo { get; set; }
    /// <summary>
    /// Built for a game install: the dlcpacks to put into the game, in order (an unchanged
    /// shared pack only needs its dlclist.xml entry). Installed by the weapon handler's plan.
    /// </summary>
    public List<PackInstall> Installs { get; } = [];
    /// <summary>Shared-pack builds: the weapon's key in the pack manifest and the pack it landed in.</summary>
    public string? WeaponSuffix { get; set; }
    public string? WeaponPack { get; set; }
}

/// <summary>One dlcpack a build hands over for installing.</summary>
/// <param name="Changed">false: the game already has this exact pack — only its dlclist.xml entry is ensured</param>
public sealed record PackInstall(string Folder, string DlcRpf, bool Changed);

/// <summary>
/// Stages 6-7: assemble the complete dlcpack tree (setup2/content, common/data metas,
/// x64 weapons.rpf + per-language global.gxt2) and pack it into a single dlc.rpf.
/// </summary>
public sealed partial class DlcAssembler
{
    public static readonly string[] Langs =
    [
        "american", "french", "german", "italian", "spanish", "portuguese",
        "polish", "russian", "korean", "chinese", "japanese", "mexican",
    ];

    /// <summary>Order the data files are listed in content.xml (cosmetic).</summary>
    public static readonly string[] Order =
    [
        "loadouts.meta", "weaponanimations.meta", "weapon.meta",
        "weaponcomponents.meta", "dlctext.meta", "contentunlocks.meta",
        "pedpersonality.meta", "shop_weapon.meta", "weaponarchetypes.meta",
    ];

    [GeneratedRegex("[^a-z0-9_]")] private static partial Regex NonSlugRe();

    private readonly ScanResult _scan;
    private readonly NamePlan _plan;
    private readonly IReadOnlyDictionary<string, string> _meta;
    private readonly Dictionary<string, string> _labels;
    private readonly string _input;
    private readonly Dictionary<string, string> _configs;
    private readonly Dictionary<string, string> _fileNames;

    public DlcAssembler(ScanResult scan, NamePlan plan, IReadOnlyDictionary<string, string> metaFiles,
                        Dictionary<string, string> gxtLabels, string inputFolder, SourceMetas? sourceMetas = null)
    {
        _scan = scan;
        _plan = plan;
        _meta = metaFiles;
        _labels = gxtLabels;
        _input = inputFolder;
        // The modder's content.xml / setup2.xml ship verbatim, and every meta they
        // supplied keeps the file name it arrived under (their content.xml refers to it).
        _configs = sourceMetas?.Configs ?? [];
        _fileNames = sourceMetas is null ? [] : new Dictionary<string, string>(sourceMetas.Names);
    }

    /// <summary>The file name a meta slot is written under inside the pack.</summary>
    public string FileName(string slot)
    {
        if (_fileNames.TryGetValue(slot, out var n)) return n;
        if (slot == "weapon.meta") return $"weapon_{_plan.Suffix}.meta";    // unique per weapon
        return slot;
    }

    /// <summary>(path inside the pack, fileType, persistent) for every enabled data file.</summary>
    public List<(string Rel, string FType, bool Persistent)> DataFiles()
    {
        var list = new List<(string, string, bool)>();
        foreach (var slot in Order)
        {
            if (!_meta.ContainsKey(slot)) continue;
            var (ftype, sub) = Overrides.FileTypes[slot];
            var baseDir = sub == "ai" ? "common/data/ai" : "common/data";
            list.Add(($"{baseDir}/{FileName(slot)}", ftype, slot == "dlctext.meta"));
        }
        list.Add(("%PLATFORM%/models/cdimages/weapons.rpf", "RPF_FILE", true));
        return list;
    }

    public static string ContentXml(string device, string changeset,
                                    IEnumerable<(string Rel, string FType, bool Persistent)> files)
    {
        var list = files.Select(f => ($"{device}:/{f.Rel}", f.FType, f.Persistent)).ToList();
        var items = string.Join("\n", list.Select(f =>
            "    <Item>\n" +
            $"      <filename>{f.Item1}</filename>\n" +
            $"      <fileType>{f.FType}</fileType>\n" +
            "      <overlay value=\"false\" />\n" +
            "      <disabled value=\"true\" />\n" +
            $"      <persistent value=\"{(f.Persistent ? "true" : "false")}\" />\n" +
            "    </Item>"));
        var enable = string.Join("\n", list.Select(f => $"        <Item>{f.Item1}</Item>"));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
               "<CDataFileMgr__ContentsOfDataFileXml>\n" +
               "  <disabledFiles />\n  <includedXmlFiles />\n  <includedDataFiles />\n" +
               "  <dataFiles>\n" + items + "\n  </dataFiles>\n" +
               "  <contentChangeSets>\n    <Item>\n" +
               $"      <changeSetName>{changeset}</changeSetName>\n" +
               "      <mapChangeSetData />\n      <filesToInvalidate />\n      <filesToDisable />\n" +
               "      <filesToEnable>\n" + enable + "\n      </filesToEnable>\n" +
               "      <txdToLoad />\n      <txdToUnload />\n      <residentResources />\n" +
               "      <unregisterResources />\n" +
               "      <requiresLoadingScreen value=\"false\" />\n    </Item>\n" +
               "  </contentChangeSets>\n  <patchFiles />\n" +
               "</CDataFileMgr__ContentsOfDataFileXml>\n";
    }

    public static string Setup2Xml(string device, string changeset) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<SSetupData>\n" +
        $"  <deviceName>{device}</deviceName>\n" +
        "  <datFile>content.xml</datFile>\n" +
        "  <timeStamp>01/01/2026 00:00:00</timeStamp>\n" +
        $"  <nameHash>{device}</nameHash>\n" +
        "  <author>ModDrop V</author>\n" +
        "  <contentChangeSets />\n" +
        "  <contentChangeSetGroups>\n    <Item>\n" +
        "      <NameHash>GROUP_STARTUP</NameHash>\n" +
        "      <ContentChangeSets>\n" +
        $"        <Item>{changeset}</Item>\n" +
        "      </ContentChangeSets>\n    </Item>\n" +
        "  </contentChangeSetGroups>\n" +
        "  <startupScript />\n  <scriptCallstackSize value=\"0\" />\n" +
        "  <type>EXTRACONTENT_COMPAT_PACK</type>\n" +
        "  <order value=\"9999\" />\n  <minorOrder value=\"0\" />\n" +
        "  <isLevelPack value=\"false\" />\n  <dependencyPackHash />\n" +
        "  <requiredVersion />\n  <subPackCount value=\"0\" />\n</SSetupData>\n";

    public string ContentXml() =>
        _configs.TryGetValue("content.xml", out var c) ? c : ContentXml(_plan.Device, _plan.Changeset, DataFiles());

    public string Setup2Xml() =>
        _configs.TryGetValue("setup2.xml", out var s) ? s : Setup2Xml(_plan.Device, _plan.Changeset);

    // --------------------------------------------------------------- assets

    /// <summary>Rename an asset file, keeping a '+hi' marker and the extension:
    /// ('w_pi_x+hi.ytd', 'w_pi_x', 'w_pi_x_aw') -&gt; 'w_pi_x_aw+hi.ytd'.</summary>
    public static string RenameFile(string filename, string oldStem, string newStem)
    {
        string b, ext;
        int dot = filename.LastIndexOf('.');
        if (dot >= 0) { b = filename[..dot]; ext = filename[dot..]; }
        else { b = filename; ext = ""; }
        string suffix = "", core = b;
        int plus = core.IndexOf('+');
        if (plus >= 0)
        {
            suffix = core[plus..];
            core = core[..plus];
        }
        if (string.Equals(core.ToLowerInvariant(), oldStem.ToLowerInvariant(), StringComparison.Ordinal))
            core = newStem;
        return core + suffix + ext;
    }

    /// <summary>
    /// (src file, dst file) for every shipped asset. Hi-lods are renamed
    /// '&lt;renamed parent&gt;_hi' so the game's automatic hi-lod lookup keeps working.
    /// </summary>
    public List<(string Src, string Dst)> AssetRenames()
    {
        var stemNew = new Dictionary<string, string>();
        foreach (var g in _scan.Groups)
        {
            if (g.Role == "hi")
            {
                var parent = g.Stem.ToLowerInvariant().EndsWith("_hi", StringComparison.Ordinal) ? g.Stem[..^3] : g.Stem;
                var newParent = _plan.ModelMap.GetValueOrDefault(parent, parent);
                stemNew[g.Stem] = $"{newParent}_hi";
            }
            else
            {
                stemNew[g.Stem] = _plan.ModelMap.GetValueOrDefault(g.Stem, g.Stem);
            }
        }
        var list = new List<(string, string)>();
        foreach (var g in _scan.Groups)
            foreach (var f in g.Files)
                list.Add((f, RenameFile(f, g.Stem, stemNew[g.Stem])));
        return list;
    }

    /// <summary>
    /// Copy a model into a loose (unpacked) tree: verbatim when it already suits the
    /// edition, converted to gen9 when a Legacy model goes into an Enhanced tree.
    /// </summary>
    private static void CopyAsset(string src, string dst, GameEdition edition)
    {
        if (Rpf7.IsResourceExt(Path.GetExtension(src)))
        {
            var raw = File.ReadAllBytes(src);
            if (ResourceEditions.NeedsConversion(src, raw, edition))
            {
                File.WriteAllBytes(dst, ResourceEditions.ForEdition(raw, Path.GetFileName(src), edition));
                return;
            }
        }
        PathUtil.Copy2(src, dst);
    }

    public static string SlugFolder(string slug) => NonSlugRe().Replace(slug.ToLowerInvariant(), "");

    // --------------------------------------------------------------- build

    public BuildResult Build(string outRoot, bool packRpf = true, GameEdition edition = GameEdition.Legacy)
    {
        var slug = SlugFolder(_plan.Slug);
        var root = Path.Combine(outRoot, slug);
        if (Directory.Exists(root)) PathUtil.DeleteDir(root);
        Directory.CreateDirectory(root);

        // when packing, lay the tree out in a staging dir and pack it into dlc.rpf
        var content = packRpf ? Path.Combine(root, "_dlc_contents") : root;
        var data = Path.Combine(content, "common", "data");
        var ai = Path.Combine(data, "ai");
        var models = Path.Combine(content, "x64", "models", "cdimages");
        var langRoot = Path.Combine(content, "x64", "data", "lang");
        foreach (var d in new[] { ai, models, langRoot }) Directory.CreateDirectory(d);

        TextIo.WriteText(Path.Combine(content, "content.xml"), ContentXml());
        TextIo.WriteText(Path.Combine(content, "setup2.xml"), Setup2Xml());

        foreach (var slot in Order)
        {
            if (!_meta.TryGetValue(slot, out var text)) continue;
            var (_, sub) = Overrides.FileTypes[slot];
            TextIo.WriteText(Path.Combine(sub == "ai" ? ai : data, FileName(slot)), text);
        }

        var renames = AssetRenames();
        var gxt = Gxt2.Build(_labels);
        var copied = new List<(string, string)>();
        bool packed;
        RpfBuildInfo? rpfInfo = null;

        if (packRpf)
        {
            var staging = PathUtil.MakeTempDir();
            try
            {
                foreach (var (src, dst) in renames)
                {
                    var sp = Path.Combine(_input, src);
                    if (File.Exists(sp))
                    {
                        PathUtil.Copy2(sp, Path.Combine(staging, dst));
                        copied.Add((src, dst));
                    }
                }
                new RpfWriter(edition).AddFolder(staging).Build(Path.Combine(models, "weapons.rpf"));
            }
            finally
            {
                PathUtil.TryDeleteDir(staging);
            }
            foreach (var lang in Langs)
                new RpfWriter().AddBinary("global.gxt2", gxt).Build(Path.Combine(langRoot, $"{lang}dlc.rpf"));
            rpfInfo = RpfPacker.PackFolder(content, Path.Combine(root, "dlc.rpf"), edition);
            PathUtil.TryDeleteDir(content);
            packed = true;
        }
        else
        {
            var rpfDir = Path.Combine(models, "weapons.rpf");
            Directory.CreateDirectory(rpfDir);
            foreach (var (src, dst) in renames)
            {
                var sp = Path.Combine(_input, src);
                if (File.Exists(sp))
                {
                    CopyAsset(sp, Path.Combine(rpfDir, dst), edition);
                    copied.Add((src, dst));
                }
            }
            foreach (var lang in Langs)
            {
                var ld = Path.Combine(langRoot, $"{lang}dlc.rpf");
                Directory.CreateDirectory(ld);
                File.WriteAllBytes(Path.Combine(ld, "global.gxt2"), gxt);
            }
            packed = false;
        }

        var renamesObj = new JsonObject();
        foreach (var (s, d) in copied) renamesObj[s] = d;
        var manifest = new JsonObject
        {
            ["device"] = _plan.Device,
            ["weapon_hash"] = _plan.WeaponHash,
            ["shop_id"] = _plan.ShopId,
            ["packed"] = packed,
            ["target"] = edition.TargetLabel(),
            ["output"] = packed ? $"{slug}/dlc.rpf" : $"{slug}/ (loose)",
            ["asset_renames"] = renamesObj,
            ["install"] = $"Copy the '{slug}' folder (with dlc.rpf inside) to your " +
                          $"dlcpacks path and add 'dlcpacks:/{slug}/' to dlclist.xml.",
        };
        if (packed && rpfInfo is not null)
        {
            manifest["dlc_rpf_size"] = rpfInfo.Size;
            manifest["dlc_rpf_entries"] = new JsonObject { ["dirs"] = rpfInfo.Dirs, ["files"] = rpfInfo.Files };
        }
        else
        {
            manifest["pack_to_rpf"] = "Build with pack_rpf=True to get a single dlc.rpf, " +
                                      "or pack the tree via CodeWalker.";
        }
        TextIo.WriteText(Path.Combine(root, "manifest.json"), TextIo.ToJson(manifest));

        return new BuildResult
        {
            Root = root, Assets = copied, Manifest = manifest, Packed = packed,
            DlcRpf = packed ? Path.Combine(root, "dlc.rpf") : null,
        };
    }
}
