using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mdv.Core.Util;

namespace Mdv.Core;

/// <summary>A weapon component that made it into the build.</summary>
public sealed class EffComponent
{
    public required string TplName { get; init; }      // COMPONENT_PISTOL_CLIP_01
    public required string NewName { get; init; }      // COMPONENT_PISTOL_CLIP_01_AW…
    public required string CType { get; init; }
    public required string Role { get; init; }         // clip / suppressor / flashlight / scope / variant / other
    public string? InputStem { get; init; }            // w_pi_vintage_pistol_mag1
    public string? NewModel { get; init; }
    public bool Default { get; set; }
    public required string Bone { get; init; }
    public required string RawXml { get; init; }
}

/// <summary>
/// Stage 4: render the DLC meta stack that makes GET_NUM_DLC_WEAPONS see the weapon
/// (weapon / weaponcomponents / weaponarchetypes / shop_weapon / contentunlocks /
/// weaponanimations / loadouts / pedpersonality / dlctext). Template components the
/// input has no asset for are pruned together with their attach points.
/// </summary>
public sealed partial class MetaGenerator
{
    public const string XmlDecl = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\n";

    [GeneratedRegex("key=\"[^\"]+\"")] private static partial Regex KeyAttrRe();

    private readonly TemplateData _tpl;
    private readonly ScanResult _scan;
    private readonly NamePlan _plan;
    private readonly Dictionary<string, int> _componentPrices;

    public int Price { get; }
    public int AmmoCost { get; }
    public string WeaponName { get; }
    public string WeaponDesc { get; }
    public string? MainStem { get; }
    public SortedDictionary<int, string> Mags { get; } = [];
    public List<string> Attachments { get; } = [];
    public List<string> HiStems { get; } = [];
    public List<EffComponent> Components { get; }

    public MetaGenerator(TemplateData template, ScanResult scan, NamePlan plan,
                         int price = 5000, int ammoCost = 100,
                         string weaponName = "Custom Weapon", string weaponDesc = "An add-on weapon.",
                         IReadOnlyDictionary<string, int>? componentPrices = null,
                         List<EffComponent>? componentsOverride = null)
    {
        _tpl = template;
        _scan = scan;
        _plan = plan;
        Price = price;
        AmmoCost = ammoCost;
        WeaponName = weaponName;
        WeaponDesc = weaponDesc;
        // per-component price overrides keyed by input asset stem, case-insensitive
        _componentPrices = new Dictionary<string, int>();
        foreach (var (k, v) in componentPrices ?? new Dictionary<string, int>())
            _componentPrices[k.ToLowerInvariant()] = v;

        MainStem = scan.MainModel;
        foreach (var g in scan.Groups)
        {
            if (g.Role.StartsWith("mag", StringComparison.Ordinal)) Mags[int.Parse(g.Role[3..])] = g.Stem;
            else if (g.Role == "attachment") Attachments.Add(g.Stem);
            else if (g.Role == "hi") HiStems.Add(g.Stem);
        }

        // A supplied weaponcomponents.meta IS the component set — matching template
        // components against file names would only invent a second, conflicting set.
        Components = componentsOverride ?? MatchComponents();
    }

    // ------------------------------------------------------------ matching

    public static string RoleOfComponent(string name, string ctype)
    {
        var n = name.ToUpperInvariant();
        if (n.Contains("CLIP") || ctype == "CWeaponComponentClipInfo") return "clip";
        if (n.Contains("SUPP") || ctype == "CWeaponComponentSuppressorInfo") return "suppressor";
        if (n.Contains("FLSH") || n.Contains("FLASH") || ctype == "CWeaponComponentFlashLightInfo") return "flashlight";
        if (n.Contains("SCOPE") || n.Contains("SCOP") || ctype == "CWeaponComponentScopeInfo") return "scope";
        if (n.Contains("VARMOD") || n.Contains("LUXE") || ctype == "CWeaponComponentVariantModelInfo") return "variant";
        return "other";
    }

    private static string? InputAttachmentFor(string role, List<string> attachments)
    {
        string[] keys = role switch
        {
            "suppressor" => ["supp"],
            "flashlight" => ["flsh", "flash"],
            "scope" => ["scope", "scop"],
            _ => [],
        };
        foreach (var stem in attachments)
        {
            var s = stem.ToLowerInvariant();
            if (keys.Any(s.Contains)) return stem;
        }
        return null;
    }

    private List<EffComponent> MatchComponents()
    {
        var eff = new List<EffComponent>();
        var usedMags = Mags.Keys.ToList();
        int magCursor = 0;
        foreach (var (bone, comps) in _tpl.Weapon.Attach)
        {
            foreach (var c in comps)
            {
                if (c.Name is null || !_tpl.Components.TryGetValue(c.Name, out var cdata)) continue;
                var role = RoleOfComponent(c.Name, cdata.CType);
                string? inputStem;
                if (role == "clip")
                {
                    if (magCursor >= usedMags.Count) continue;     // no more input mags -> prune
                    inputStem = Mags[usedMags[magCursor++]];
                }
                else if (role is "suppressor" or "flashlight" or "scope")
                {
                    inputStem = InputAttachmentFor(role, Attachments);
                    if (inputStem is null) continue;               // input lacks it -> prune
                }
                else
                {
                    continue;                                      // variants / mk2 / markers -> prune
                }
                eff.Add(new EffComponent
                {
                    TplName = c.Name, NewName = _plan.ComponentMap[c.Name], CType = cdata.CType,
                    Role = role, InputStem = inputStem,
                    NewModel = _plan.ModelMap.GetValueOrDefault(inputStem),
                    Default = c.Default, Bone = bone, RawXml = cdata.RawXml,
                });
            }
        }
        // guarantee exactly one default clip
        var clips = eff.Where(e => e.Role == "clip").ToList();
        if (clips.Count > 0 && !clips.Any(e => e.Default)) clips[0].Default = true;
        return eff;
    }

    /// <summary>
    /// Shop cost of a matched component: a user price (keyed by input stem, new model or
    /// template name) wins; else default clip 1, extended clip 400, anything else 500.
    /// </summary>
    public int ComponentCost(EffComponent e)
    {
        foreach (var key in new[] { e.InputStem, e.NewModel, e.TplName })
            if (!string.IsNullOrEmpty(key) && _componentPrices.TryGetValue(key.ToLowerInvariant(), out var p))
                return p;
        if (e.Role == "clip") return e.Default ? 1 : 400;
        return 500;
    }

    private string NewMainModel() =>
        MainStem is not null && _plan.ModelMap.TryGetValue(MainStem, out var m) ? m : MainStem ?? "";

    // ------------------------------------------------------------ renderers

    public string WeaponMeta()
    {
        var el = EtXml.Parse(_tpl.Weapon.RawXml);
        EtXml.SetChildText(el, "Name", _plan.WeaponHash);
        EtXml.SetChildText(el, "Model", NewMainModel());
        EtXml.SetChildText(el, "Slot", _plan.Slot);
        EtXml.SetChildText(el, "HumanNameHash", _plan.LabelName);
        var ap = el.Element("AttachPoints");
        if (ap is not null)
        {
            EtXml.RemoveChildElements(ap);
            var byBone = new List<KeyValuePair<string, List<EffComponent>>>();
            foreach (var e in Components)
            {
                int i = byBone.FindIndex(kv => kv.Key == e.Bone);
                if (i < 0) byBone.Add(new(e.Bone, [e]));
                else byBone[i].Value.Add(e);
            }
            foreach (var (bone, comps) in byBone)
            {
                var item = EtXml.SubElement(ap, "Item");
                EtXml.SubElement(item, "AttachBone").Add(new XText(bone));
                var cs = EtXml.SubElement(item, "Components");
                foreach (var e in comps)
                {
                    var ci = EtXml.SubElement(cs, "Item");
                    EtXml.SubElement(ci, "Name").Add(new XText(e.NewName));
                    EtXml.SubElement(ci, "Default").SetAttributeValue("value", e.Default ? "true" : "false");
                }
            }
        }
        var body = EtXml.ToString(el);
        var slot = _plan.Slot;
        return XmlDecl +
               "<CWeaponInfoBlob>\n" +
               "  <SlotNavigateOrder>\n" +
               "    <Item><WeaponSlots><Item>\n" +
               $"      <OrderNumber value=\"350\" /><Entry>{slot}</Entry>\n" +
               "    </Item></WeaponSlots></Item>\n" +
               "    <Item><WeaponSlots><Item>\n" +
               $"      <OrderNumber value=\"350\" /><Entry>{slot}</Entry>\n" +
               "    </Item></WeaponSlots></Item>\n" +
               "  </SlotNavigateOrder>\n" +
               "  <SlotBestOrder><WeaponSlots><Item>\n" +
               $"    <OrderNumber value=\"10\" /><Entry>{slot}</Entry>\n" +
               "  </Item></WeaponSlots></SlotBestOrder>\n" +
               "  <TintSpecValues /><FiringPatternAliases /><UpperBodyFixupExpressionData />\n" +
               "  <AimingInfos />\n" +
               "  <Infos><Item><Infos /></Item><Item><Infos>\n" +
               $"    {body}\n" +
               "  </Infos></Item><Item><Infos /></Item><Item><Infos /></Item></Infos>\n" +
               "  <VehicleWeaponInfos /><WeaponGroupDamageForArmouredVehicleGlass />\n" +
               $"  <Name>{_plan.Slug} (AWB)</Name>\n" +
               "</CWeaponInfoBlob>\n";
    }

    public string WeaponComponentsMeta()
    {
        var infos = new List<string>();
        foreach (var e in Components)
        {
            var cel = EtXml.Parse(e.RawXml);
            EtXml.SetChildText(cel, "Name", e.NewName);
            if (cel.Element("Model") is not null && !string.IsNullOrEmpty(e.NewModel))
                EtXml.SetChildText(cel, "Model", e.NewModel);
            infos.Add("    " + EtXml.ToString(cel).Trim());
        }
        return XmlDecl +
               "<CWeaponComponentInfoBlob>\n  <Data>\n  </Data>\n  <Infos>\n" +
               string.Join("\n", infos) +
               $"\n  </Infos>\n  <InfoBlobName>{_plan.Slug} (AWB)</InfoBlobName>\n" +
               "</CWeaponComponentInfoBlob>\n";
    }

    public string WeaponArchetypesMeta()
    {
        // Every asset we ship needs a model->txd row; each ships its own ytd, so txd == model.
        // Hi-lod (*_hi) models are intentionally NOT listed — the game resolves <model>_hi itself.
        var rows = new List<string>();
        var seen = new HashSet<string>();
        void Add(string newModel)
        {
            if (seen.Add(newModel)) rows.Add(newModel);
        }
        Add(NewMainModel());
        foreach (var e in Components)
            if (!string.IsNullOrEmpty(e.NewModel)) Add(e.NewModel);
        var body = string.Join("\n", rows.Select(m =>
            $"\t\t<Item>\n\t\t\t<modelName>{m}</modelName>\n" +
            $"\t\t\t<txdName>{m}</txdName>\n" +
            "\t\t\t<ptfxAssetName>NULL</ptfxAssetName>\n" +
            "\t\t\t<lodDist value=\"50\"/>\n\t\t</Item>"));
        return XmlDecl +
               "<CWeaponModelInfo__InitDataList>\n\t<InitDatas>\n" +
               body + "\n\t</InitDatas>\n</CWeaponModelInfo__InitDataList>\n";
    }

    /// <summary>THE file GET_NUM_DLC_WEAPONS enumerates.</summary>
    public string ShopWeaponMeta()
    {
        var items = new List<string>();
        foreach (var e in Components)
        {
            items.Add(ShopComponentItem(e.NewName, ComponentCost(e), ComponentLabel(e.Role, e.Default)));
        }
        return ShopWeaponMeta([ShopWeaponItem(_plan.Unlock, _plan.WeaponHash, Price, AmmoCost, _plan.LabelName,
                                              _plan.LabelDesc, _plan.LabelTt, _plan.LabelUpper, _plan.ShopId, items)]);
    }

    /// <summary>The built-in magazine reads "Default Clip" (WCT_CLIP1), the extended one WCT_CLIP2.</summary>
    public static string ComponentLabel(string role, bool isDefault) =>
        role == "clip"
            ? (isDefault ? "WCT_CLIP1" : "WCT_CLIP2")
            : role switch
            {
                "suppressor" => "WCT_SUPP",
                "flashlight" => "WCT_FLASH",
                "scope" => "WCT_SCOPE",
                _ => "WCT_INVALID",
            };

    public static string ShopComponentItem(string name, int cost, string label) =>
        "\t\t\t\t<Item>\n" +
        $"\t\t\t\t\t<componentName>{name}</componentName>\n" +
        $"\t\t\t\t\t<cost value=\"{cost}\"/>\n" +
        $"\t\t\t\t\t<textLabel>{label}</textLabel>\n" +
        "\t\t\t\t\t<componentDesc>INVALID</componentDesc>\n" +
        "\t\t\t\t</Item>";

    public static string ShopWeaponItem(string unlock, string weaponHash, int price, int ammoCost,
                                        string labelName, string labelDesc, string labelTt, string labelUpper,
                                        int shopId, IReadOnlyList<string> componentItems) =>
        "\t\t<Item>\n" +
        $"\t\t\t<lockHash>{unlock}</lockHash>\n" +
        $"\t\t\t<nameHash>{weaponHash}</nameHash>\n" +
        $"\t\t\t<cost value=\"{price}\"/>\n" +
        $"\t\t\t<ammoCost value=\"{ammoCost}\"/>\n" +
        $"\t\t\t<textLabel>{labelName}</textLabel>\n" +
        $"\t\t\t<weaponDesc>{labelDesc}</weaponDesc>\n" +
        $"\t\t\t<weaponTT>{labelTt}</weaponTT>\n" +
        $"\t\t\t<weaponUppercase>{labelUpper}</weaponUppercase>\n" +
        $"\t\t\t<id value=\"{shopId}\"/>\n" +
        "\t\t\t<weaponComponents>\n" + string.Join("\n", componentItems) + "\n\t\t\t</weaponComponents>\n" +
        "\t\t</Item>";

    public static string ShopWeaponMeta(IEnumerable<string> weaponItems) =>
        XmlDecl +
        "<WeaponShopItemArray>\n\t<weaponShopItems>\n" +
        string.Join("\n", weaponItems) +
        "\n\t</weaponShopItems>\n</WeaponShopItemArray>\n";

    public string ContentUnlocksMeta() => ContentUnlocksMeta([_plan.Unlock]);

    public static string ContentUnlocksMeta(IEnumerable<string> unlocks) =>
        XmlDecl +
        "<SContentUnlocks>\n  <listOfUnlocks>\n" +
        string.Concat(unlocks.Select(u => $"\t<Item>{u}</Item>\n")) +
        "  </listOfUnlocks>\n</SContentUnlocks>\n";

    public string WeaponAnimationsMeta()
    {
        var setItems = new List<string>();
        foreach (var (setKey, entryXml) in _tpl.Animations)
        {
            var entry = KeyAttrRe().Replace(entryXml, _ => $"key=\"{_plan.WeaponHash}\"", 1);
            setItems.Add($"\t\t<Item key=\"{setKey}\">\n" +
                         "\t\t\t<WeaponAnimations>\n" +
                         $"\t\t\t\t{entry.Trim()}\n" +
                         "\t\t\t</WeaponAnimations>\n\t\t</Item>");
        }
        return XmlDecl +
               "<CWeaponAnimationsSets>\n\t<WeaponAnimationsSets>\n" +
               string.Join("\n", setItems) +
               "\n\t</WeaponAnimationsSets>\n</CWeaponAnimationsSets>\n";
    }

    public static string LoadoutsMeta() =>
        XmlDecl + "<CPedInventoryLoadOutManager>\n  <LoadOuts />\n</CPedInventoryLoadOutManager>\n";

    public static string PedPersonalityMeta() =>
        XmlDecl +
        "<CPedModelInfo__PersonalityDataList>\n" +
        "\t<MovementModeUnholsterData />\n" +
        "\t<PedPersonalities />\n" +
        "</CPedModelInfo__PersonalityDataList>\n";

    public static string DlcTextMeta() =>
        XmlDecl +
        "<CExtraTextMetaFile>\n\t<hasGlobalTextFile value=\"true\"/>\n" +
        "\t<hasAdditionalText value=\"false\"/>\n" +
        "\t<isTitleUpdate value=\"false\"/>\n</CExtraTextMetaFile>\n";

    /// <summary>Text labels for the GXT table (stage 5).</summary>
    public Dictionary<string, string> GxtLabels() => new()
    {
        [_plan.LabelName] = WeaponName,
        [_plan.LabelUpper] = WeaponName.ToUpperInvariant(),
        [_plan.LabelDesc] = WeaponDesc,
        [_plan.LabelTt] = WeaponDesc,
    };

    /// <summary>All metas, keyed by their canonical slot name.</summary>
    public Dictionary<string, string> GenerateAll() => new()
    {
        ["weapon.meta"] = WeaponMeta(),
        ["weaponcomponents.meta"] = WeaponComponentsMeta(),
        ["weaponarchetypes.meta"] = WeaponArchetypesMeta(),
        ["shop_weapon.meta"] = ShopWeaponMeta(),
        ["contentunlocks.meta"] = ContentUnlocksMeta(),
        ["weaponanimations.meta"] = WeaponAnimationsMeta(),
        ["loadouts.meta"] = LoadoutsMeta(),
        ["pedpersonality.meta"] = PedPersonalityMeta(),
        ["dlctext.meta"] = DlcTextMeta(),
    };

    /// <summary>
    /// Confirm every matched component landed in the three XML files that reference it
    /// (definition, attach points, shop list). <paramref name="checkSlots"/> limits the
    /// check to generated files: a modder's own meta is theirs to decide.
    /// </summary>
    public static List<string> VerifyComponentMetas(IReadOnlyDictionary<string, string> metas,
                                                    IEnumerable<EffComponent> components,
                                                    ISet<string>? checkSlots = null)
    {
        var slots = new (string Slot, string Where)[]
        {
            ("weaponcomponents.meta", "weaponcomponents.meta"),
            ("weapon.meta", "weapon.meta AttachPoints"),
            ("shop_weapon.meta", "shop_weapon.meta"),
        };
        var problems = new List<string>();
        var list = components.ToList();
        foreach (var (slot, where) in slots)
        {
            if (checkSlots is not null && !checkSlots.Contains(slot)) continue;
            var text = metas.GetValueOrDefault(slot, "");
            foreach (var e in list)
                if (!text.Contains(e.NewName, StringComparison.Ordinal))
                    problems.Add($"{e.NewName}: missing from {where}");
        }
        return problems;
    }
}
