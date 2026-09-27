using System.Text;
using System.Xml.Linq;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core;

/// <summary>The .meta/.xml files an input folder ships, resolved to their slots.</summary>
public sealed class SourceMetas
{
    /// <summary>slot -> file text</summary>
    public Dictionary<string, string> Texts { get; } = [];
    /// <summary>slot -> file name it came in as</summary>
    public Dictionary<string, string> Names { get; } = [];

    public bool Any => Texts.Count > 0;

    /// <summary>Slots shipped as data files (content.xml / setup2.xml configure the pack).</summary>
    public Dictionary<string, string> DataFiles =>
        Texts.Where(kv => !Overrides.ConfigKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

    public Dictionary<string, string> Configs =>
        Texts.Where(kv => Overrides.ConfigKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>A supplied meta that names models pins those names — renaming would break it.</summary>
    public bool ForbidsRenaming() => Overrides.ModelBoundKeys.Any(Texts.ContainsKey);
}

/// <summary>A meta lifted out of a prebuilt dlc.rpf.</summary>
public sealed record ImportedMeta(string Slot, string Name, string Sub, string FType, string Content);

/// <summary>A prebuilt dlc.rpf taken apart into the pieces a merged pack needs.</summary>
public sealed class ImportedPack
{
    public List<ImportedMeta> Metas { get; } = [];
    /// <summary>file name -> resource bytes (loose RSC7)</summary>
    public Dictionary<string, byte[]> Assets { get; } = [];
    /// <summary>joaat hash -> text</summary>
    public Dictionary<uint, string> LabelHashes { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Ship what the input folder already provides instead of inventing it: loose
/// metas are classified by their XML root tag and shipped verbatim (verbatim mode:
/// models keep their names); a prebuilt dlc.rpf is installed as-is or unpacked
/// into the shared pack.
/// </summary>
public static class Overrides
{
    /// <summary>XML root tag -> canonical slot.</summary>
    public static readonly Dictionary<string, string> RootTags = new()
    {
        ["CWeaponInfoBlob"] = "weapon.meta",
        ["CWeaponComponentInfoBlob"] = "weaponcomponents.meta",
        ["CWeaponModelInfo__InitDataList"] = "weaponarchetypes.meta",
        ["WeaponShopItemArray"] = "shop_weapon.meta",
        ["SContentUnlocks"] = "contentunlocks.meta",
        ["CWeaponAnimationsSets"] = "weaponanimations.meta",
        ["CPedInventoryLoadOutManager"] = "loadouts.meta",
        ["CPedModelInfo__PersonalityDataList"] = "pedpersonality.meta",
        ["CExtraTextMetaFile"] = "dlctext.meta",
        ["CDataFileMgr__ContentsOfDataFileXml"] = "content.xml",
        ["SSetupData"] = "setup2.xml",
    };

    /// <summary>slot -> (content.xml fileType, subdirectory under common/data)</summary>
    public static readonly Dictionary<string, (string FType, string Sub)> FileTypes = new()
    {
        ["weapon.meta"] = ("WEAPONINFO_FILE", "ai"),
        ["weaponanimations.meta"] = ("WEAPON_ANIMATIONS_FILE", "ai"),
        ["weaponcomponents.meta"] = ("WEAPONCOMPONENTSINFO_FILE", "ai"),
        ["loadouts.meta"] = ("LOADOUTS_FILE", "ai"),
        ["weaponarchetypes.meta"] = ("WEAPON_METADATA_FILE", "data"),
        ["shop_weapon.meta"] = ("WEAPON_SHOP_INFO_METADATA_FILE", "data"),
        ["contentunlocks.meta"] = ("CONTENT_UNLOCKING_META_FILE", "data"),
        ["dlctext.meta"] = ("TEXTFILE_METAFILE", "data"),
        ["pedpersonality.meta"] = ("PED_PERSONALITY_FILE", "data"),
    };

    public static readonly string[] ConfigKeys = ["content.xml", "setup2.xml"];
    public static readonly string[] ModelBoundKeys = ["weapon.meta", "weaponcomponents.meta", "weaponarchetypes.meta"];

    /// <summary>The slot a piece of XML fills, from its root tag; null if unknown/unparseable.</summary>
    public static string? ClassifyXml(string text)
    {
        var root = EtXml.TryParse(text);
        return root is null ? null : RootTags.GetValueOrDefault(root.Name.LocalName);
    }

    /// <summary>
    /// Every recognised .meta/.xml in the input folder (recursively), keyed by slot.
    /// The first file to claim a slot keeps it; unrecognised XML is ignored.
    /// </summary>
    public static SourceMetas Collect(string folder)
    {
        var result = new SourceMetas();
        if (!Directory.Exists(folder)) return result;
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                             .Where(p =>
                             {
                                 var ext = PathUtil.SuffixLower(p);
                                 return ext is ".meta" or ".xml";
                             })
                             .OrderBy(p => Path.GetRelativePath(folder, p), PathUtil.PathOrder);
        foreach (var p in files)
        {
            string text;
            try
            {
                text = TextIo.ReadTextStrict(p);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                continue;
            }
            var key = ClassifyXml(text);
            if (key is not null && !result.Texts.ContainsKey(key))
            {
                result.Texts[key] = text;
                result.Names[key] = Path.GetFileName(p);
            }
        }
        return result;
    }

    private static XElement? FirstItemOfType(XElement root, string type) =>
        EtXml.Iter(root, "Item").FirstOrDefault(i => (string?)i.Attribute("type") == type);

    /// <summary>
    /// Identifiers the generated companion files must agree with, pulled out of the
    /// metas the modder supplied. Every key is optional.
    /// </summary>
    public static Dictionary<string, string> PlanHints(IReadOnlyDictionary<string, string> ov)
    {
        var hints = new Dictionary<string, string>();

        if (ov.TryGetValue("weapon.meta", out var wm) && !string.IsNullOrEmpty(wm))
        {
            var root = EtXml.TryParse(wm);
            var info = root is null ? null : FirstItemOfType(root, "CWeaponInfo");
            if (info is not null)
                foreach (var (tag, key) in new[] { ("Name", "weapon_hash"), ("Slot", "slot"),
                                                   ("Model", "model"), ("HumanNameHash", "label_name") })
                {
                    var v = EtXml.ChildTextNonEmpty(info, tag);
                    if (v is not null) hints[key] = v;
                }
        }

        if (ov.TryGetValue("shop_weapon.meta", out var sw) && !string.IsNullOrEmpty(sw))
        {
            var root = EtXml.TryParse(sw);
            var item = root is null ? null : EtXml.Find(root, ".//weaponShopItems/Item");
            if (item is not null)
                foreach (var (tag, key) in new[] { ("nameHash", "weapon_hash"), ("lockHash", "unlock"),
                                                   ("textLabel", "label_name"), ("weaponDesc", "label_desc"),
                                                   ("weaponTT", "label_tt"), ("weaponUppercase", "label_upper") })
                {
                    var v = EtXml.ChildTextNonEmpty(item, tag);
                    if (v is not null) hints[key] = v;
                }
        }

        if (ov.TryGetValue("contentunlocks.meta", out var cu) && !string.IsNullOrEmpty(cu))
        {
            var root = EtXml.TryParse(cu);
            var v = root is null ? null : EtXml.ChildTextNonEmpty(root.Element("listOfUnlocks"), "Item");
            if (v is not null) hints["unlock"] = v;
        }

        if (ov.TryGetValue("setup2.xml", out var s2) && !string.IsNullOrEmpty(s2))
        {
            var root = EtXml.TryParse(s2);
            var v = root is null ? null : EtXml.ChildTextNonEmpty(root, "deviceName");
            if (v is not null) hints["device"] = v;
        }

        if (ov.TryGetValue("content.xml", out var cx) && !string.IsNullOrEmpty(cx))
        {
            var root = EtXml.TryParse(cx);
            var item = root is null ? null : EtXml.Find(root, ".//contentChangeSets/Item");
            var v = EtXml.ChildTextNonEmpty(item, "changeSetName");
            if (v is not null) hints["changeset"] = v;
        }

        return hints;
    }

    /// <summary>
    /// The effective component list from a supplied weaponcomponents.meta (+ the
    /// AttachPoints of a supplied weapon.meta for bone and default flag), so a generated
    /// shop_weapon.meta lists exactly the modder's components. Null when none supplied.
    /// </summary>
    public static List<EffComponent>? ComponentsFromOverrides(IReadOnlyDictionary<string, string> ov)
    {
        if (!ov.TryGetValue("weaponcomponents.meta", out var wc) || string.IsNullOrEmpty(wc)) return null;
        var root = EtXml.TryParse(wc);
        if (root is null) return null;

        var attach = new Dictionary<string, (string Bone, bool Default)>();
        if (ov.TryGetValue("weapon.meta", out var wm) && !string.IsNullOrEmpty(wm))
        {
            var wroot = EtXml.TryParse(wm);
            var info = wroot is null ? null : FirstItemOfType(wroot, "CWeaponInfo");
            var ap = info?.Element("AttachPoints");
            foreach (var item in ap?.Elements("Item") ?? [])
            {
                var bone = EtXml.ChildTextNonEmpty(item, "AttachBone") ?? "WAPClip";
                foreach (var c in EtXml.FindAll(item, "Components/Item"))
                {
                    var cn = EtXml.ChildTextNonEmpty(c, "Name");
                    var cd = c.Element("Default");
                    if (cn is not null)
                        attach[cn] = (bone, cd is not null && (string?)cd.Attribute("value") == "true");
                }
            }
        }

        var result = new List<EffComponent>();
        foreach (var el in EtXml.Iter(root, "Item"))
        {
            var ctype = (string?)el.Attribute("type") ?? "";
            if (!ctype.StartsWith("CWeaponComponent", StringComparison.Ordinal)) continue;
            var name = EtXml.ChildTextNonEmpty(el, "Name");
            if (name is null) continue;
            var (bone, def) = attach.TryGetValue(name, out var a) ? a : ("WAPClip", false);
            result.Add(new EffComponent
            {
                TplName = name, NewName = name, CType = ctype,
                Role = MetaGenerator.RoleOfComponent(name, ctype), InputStem = null,
                NewModel = EtXml.ChildTextNonEmpty(el, "Model"), Default = def, Bone = bone,
                RawXml = EtXml.ToString(el, withTail: true),
            });
        }
        return result.Count > 0 ? result : null;
    }

    // --------------------------------------------------------- prebuilt dlc.rpf

    /// <summary>
    /// A finished pack in the input folder instead of raw models: a file literally named
    /// dlc.rpf, otherwise the only *.rpf present (several archives = ambiguous = null).
    /// </summary>
    public static string? FindPrebuiltRpf(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var rpfs = PathUtil.SortedFiles(folder).Where(f => f.Extension.Equals(".rpf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (rpfs.Count == 0) return null;
        var dlc = rpfs.FirstOrDefault(f => f.Name.Equals("dlc.rpf", StringComparison.OrdinalIgnoreCase));
        if (dlc is not null) return dlc.FullName;
        return rpfs.Count == 1 ? rpfs[0].FullName : null;
    }

    /// <summary>
    /// Unpack a finished dlc.rpf into mergeable parts: every recognised meta, every weapon
    /// model and the GXT label table (kept keyed by hash — label names are not recoverable).
    /// </summary>
    public static ImportedPack ImportDlcRpf(string dlcRpf)
    {
        var result = new ImportedPack();
        using var arc = RpfArchive.Open(dlcRpf);
        foreach (var item in arc.Tree())
        {
            if (item.IsDir) continue;
            var path = item.Path;
            var low = path.ToLowerInvariant();
            var fname = path[(path.LastIndexOf('/') + 1)..];
            var ext = PathUtil.SuffixLower(fname);

            if (ext is ".meta" or ".xml")
            {
                string text;
                try
                {
                    text = TextIo.DecodeUtf8Sig(arc.ReadContent(item.Entry), strict: true);
                }
                catch (DecoderFallbackException)
                {
                    result.Warnings.Add(L.T($"{path}: not valid UTF-8 — skipped."));
                    continue;
                }
                var slot = ClassifyXml(text);
                if (slot is null)
                {
                    result.Warnings.Add(L.T($"{path}: unrecognised XML root — skipped."));
                    continue;
                }
                if (ConfigKeys.Contains(slot) || slot == "dlctext.meta") continue;   // regenerated for the shared pack
                var (ftype, sub) = FileTypes[slot];
                result.Metas.Add(new ImportedMeta(slot, fname, sub, ftype, text));
            }
            else if (ext == ".rpf" && low.Contains("models/cdimages"))
            {
                using var nested = arc.OpenNested(item.Entry);
                foreach (var f in nested.Files())
                    if (Rpf7.IsResourceExt(PathUtil.SuffixLower(f.Name)))
                        result.Assets[f.Name] = nested.ReadContent(f);
            }
            else if (ext == ".rpf" && low.Contains("/lang/") && result.LabelHashes.Count == 0)
            {
                using var nested = arc.OpenNested(item.Entry);
                foreach (var f in nested.Files())
                {
                    if (!f.Name.EndsWith(".gxt2", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        foreach (var (h, t) in Gxt2.Read(nested.ReadContent(f)))
                            result.LabelHashes[h] = t;
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add(L.T($"{path}/{f.Name}: unreadable GXT ({ex.Message})."));
                    }
                }
            }
        }
        if (result.Assets.Count == 0)
            result.Warnings.Add(L.T("No weapon models found in the archive (expected a nested " +
                                "x64/models/cdimages/weapons.rpf)."));
        return result;
    }

    /// <summary>
    /// Give every weapon of an imported pack that has no shop_weapon.meta entry one
    /// (plus its content unlock and name labels): many "singleplayer add-on" archives
    /// ship only weapon.meta, so the weapon spawns by name but GET_NUM_DLC_WEAPONS —
    /// and every menu built on it — never sees it. Returns the weapon names completed.
    /// </summary>
    public static List<string> CompleteShopEntries(ImportedPack imported, string tag, string? displayName,
                                                   string desc, int price, int ammoCost, int shopId)
    {
        var inShop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in imported.Metas.Where(m => m.Slot == "shop_weapon.meta"))
            if (EtXml.TryParse(m.Content) is { } root)
                foreach (var item in EtXml.FindAll(root, ".//weaponShopItems/Item"))
                    if (EtXml.ChildTextNonEmpty(item, "nameHash") is { } nh) inShop.Add(nh);

        var ctypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in imported.Metas.Where(m => m.Slot == "weaponcomponents.meta"))
            if (EtXml.TryParse(m.Content) is { } root)
                foreach (var el in EtXml.Iter(root, "Item"))
                    if (EtXml.ChildTextNonEmpty(el, "Name") is { } cn && (string?)el.Attribute("type") is { } ct)
                        ctypes[cn] = ct;

        var missing = new List<XElement>();
        foreach (var m in imported.Metas.Where(m => m.Slot == "weapon.meta"))
            if (EtXml.TryParse(m.Content) is { } root)
                foreach (var info in EtXml.Iter(root, "Item").Where(i => (string?)i.Attribute("type") == "CWeaponInfo"))
                {
                    var name = EtXml.ChildTextNonEmpty(info, "Name");
                    // vehicle weapons and model-less entries are never sold
                    if (name is null || EtXml.ChildTextNonEmpty(info, "Model") is null ||
                        name.StartsWith("VEHICLE_", StringComparison.OrdinalIgnoreCase) || inShop.Contains(name))
                        continue;
                    inShop.Add(name);
                    missing.Add(info);
                }
        if (missing.Count == 0) return [];

        var items = new List<string>();
        var unlocks = new List<string>();
        var done = new List<string>();
        for (int i = 0; i < missing.Count; i++)
        {
            var info = missing[i];
            var name = EtXml.ChildTextNonEmpty(info, "Name")!;
            var t = missing.Count == 1 ? tag : $"{tag}_{i + 1}";
            var shown = missing.Count == 1 && !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim() : PrettyName(name);

            // the weapon's own name label: keep it if the pack translates it or it is one of
            // the game's (WT_*); otherwise it is a placeholder with no text, so give it one
            var label = EtXml.ChildTextNonEmpty(info, "HumanNameHash");
            if (label is null || label.Equals("WT_INVALID", StringComparison.OrdinalIgnoreCase))
                label = $"AWN_{t}";
            if (!imported.LabelHashes.ContainsKey(Gxt2.Joaat(label)) &&
                !label.StartsWith("WT_", StringComparison.OrdinalIgnoreCase))
                imported.LabelHashes[Gxt2.Joaat(label)] = shown;
            var (upper, descL, tt) = ($"AWU_{t}", $"AWD_{t}", $"AWT_{t}");
            imported.LabelHashes[Gxt2.Joaat(upper)] = shown.ToUpperInvariant();
            imported.LabelHashes[Gxt2.Joaat(descL)] = desc;
            imported.LabelHashes[Gxt2.Joaat(tt)] = desc;

            var comps = new List<string>();
            foreach (var ap in EtXml.FindAll(info, "AttachPoints/Item"))
                foreach (var c in EtXml.FindAll(ap, "Components/Item"))
                {
                    var cn = EtXml.ChildTextNonEmpty(c, "Name");
                    if (cn is null) continue;
                    bool def = (string?)c.Element("Default")?.Attribute("value") == "true";
                    var role = MetaGenerator.RoleOfComponent(cn, ctypes.GetValueOrDefault(cn, ""));
                    int cost = role == "clip" ? (def ? 1 : 400) : 500;
                    comps.Add(MetaGenerator.ShopComponentItem(cn, cost, MetaGenerator.ComponentLabel(role, def)));
                }

            var unlock = $"CU_{name}";
            unlocks.Add(unlock);
            items.Add(MetaGenerator.ShopWeaponItem(unlock, name, price, ammoCost, label, descL, tt, upper, shopId, comps));
            done.Add(name);
        }

        AddGenerated(imported, "shop_weapon.meta", MetaGenerator.ShopWeaponMeta(items));
        AddGenerated(imported, "contentunlocks.meta", MetaGenerator.ContentUnlocksMeta(unlocks));
        return done;
    }

    /// <summary>Add a generated meta under a file name none of the archive's own metas use.</summary>
    private static void AddGenerated(ImportedPack imported, string slot, string content)
    {
        var stem = Path.GetFileNameWithoutExtension(slot);
        var name = slot;
        for (int n = 2; imported.Metas.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{stem}{n}.meta";
        var (ftype, sub) = FileTypes[slot];
        imported.Metas.Add(new ImportedMeta(slot, name, sub, ftype, content));
    }

    /// <summary>WEAPON_VG_1_5 -> "VG 1 5": a readable fallback name for a weapon hash.</summary>
    private static string PrettyName(string weaponHash)
    {
        var s = weaponHash.StartsWith("WEAPON_", StringComparison.OrdinalIgnoreCase) ? weaponHash[7..] : weaponHash;
        s = s.Replace('_', ' ').Trim();
        return s.Length > 0 ? s : weaponHash;
    }
}
