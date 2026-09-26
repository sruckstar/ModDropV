using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using CodeWalker.GameFiles;

namespace Mdv.Core.Mods;

/// <summary>A drawable to add to a ped's variations: its slot, how many textures it has and of what kind.</summary>
/// <param name="Prop">a prop (Slot = anchor) rather than a component (Slot = component)</param>
/// <param name="TextureRaces">a component's textures in letter order, each its race id (0 uni, 1 whi…); a prop's — their count matters</param>
/// <param name="Alternatives">alternative drawables it has (<c>accs_001_u_1.ydd</c>)</param>
public sealed record NewDrawable(bool Prop, int Slot, bool RaceSpecific, IReadOnlyList<int> TextureRaces, int Alternatives = 0, bool Cloth = false)
{
    public int Textures => Math.Max(1, TextureRaces.Count);
}

/// <summary>
/// A ped's variations file (<c>player_one.ymt</c>, <c>mp_m_freemode_01_mp_m_2024_02.ymt</c> — a
/// <c>CPedVariationInfo</c>): how many drawables each component and prop anchor has and how many textures
/// each of them. Read and written through CodeWalker's XML form of it; drawables are added at the end of
/// their slot (the game shows a slot with no model as nothing, so numbers that lose their files later do no harm).
/// </summary>
public static class PedVariation
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The ymt as CodeWalker's XML (the file may be compressed as on disk or inflated as an archive read gives it).</summary>
    public static XDocument Read(byte[] ymt)
    {
        var data = ymt;
        var entry = RpfFile.CreateResourceFileEntry(ref data, 0);
        long virt = Rpf.Rpf7.ResVirtualSize(entry.SystemFlags) + Rpf.Rpf7.ResVirtualSize(entry.GraphicsFlags);
        if (data.Length != virt)
        {
            try
            {
                data = ResourceBuilder.Decompress(data);
            }
            catch (InvalidDataException)
            {
                // inflated already
            }
        }
        var file = new YmtFile();
        file.Load(data, entry);
        var xml = MetaXml.GetXml(file, out _);
        if (string.IsNullOrEmpty(xml)) throw new InvalidDataException("not a ped variations file");
        var doc = XDocument.Parse(xml);
        if (doc.Root?.Name.LocalName != "CPedVariationInfo") throw new InvalidDataException($"not a ped variations file ({doc.Root?.Name})");
        return doc;
    }

    /// <summary>The ymt file (RSC, compressed) for the XML form.</summary>
    public static byte[] Write(XDocument doc)
    {
        var x = new XmlDocument();
        x.LoadXml(doc.ToString(SaveOptions.DisableFormatting));
        return XmlMeta.GetData(x, MetaFormat.RSC, "variations.ymt") ?? throw new InvalidDataException("the ped variations could not be built");
    }

    /// <summary>A new, empty variations file of an MP collection (<paramref name="dlcName"/>: <c>mp_m_mycollection</c>).</summary>
    public static XDocument Empty(string dlcName) => new(
        new XElement("CPedVariationInfo",
            Val("bHasTexVariations", "false"), Val("bHasDrawblVariations", "true"), Val("bHasLowLODs", "false"), Val("bIsSuperLOD", "false"),
            new XElement("availComp", string.Join(' ', Enumerable.Repeat("255", 12))),
            Arr("aComponentData3", "CPVComponentData"), Arr("aSelectionSets", "CPedSelectionSet"), Arr("compInfos", "CComponentInfo"),
            new XElement("propInfo", Val("numAvailProps", "0"), Arr("aPropMetaData", "CPedPropMetaData"), Arr("aAnchors", "CAnchorProps")),
            new XElement("dlcName", dlcName)));

    /// <summary>Drawables per component slot (0–11) and per prop anchor (0–10).</summary>
    public static (int[] Components, int[] Props) Counts(XDocument doc)
    {
        var root = doc.Root!;
        var comps = new int[ClothingNames.Components.Length];
        var avail = Avail(root);
        var datas = root.Element("aComponentData3")?.Elements("Item").ToList() ?? [];
        for (int s = 0; s < comps.Length; s++)
            if (avail[s] != 255 && avail[s] < datas.Count)
                comps[s] = datas[avail[s]].Element("aDrawblData3")?.Elements("Item").Count() ?? 0;
        var props = new int[ClothingNames.Anchors.Length];
        foreach (var p in root.Element("propInfo")?.Element("aPropMetaData")?.Elements("Item") ?? [])
            if (Int(p, "anchorId") is var a && a >= 0 && a < props.Length) props[a]++;
        return (comps, props);
    }

    /// <summary>
    /// Add drawables at the end of their slots; returns the number each got (in the order given).
    /// A component the ped doesn't have yet is created.
    /// </summary>
    public static List<int> Add(XDocument doc, IEnumerable<NewDrawable> drawables)
    {
        var root = doc.Root!;
        var numbers = new List<int>();
        var avail = Avail(root);
        var compArr = root.Element("aComponentData3")!;
        var bySlot = new SortedDictionary<int, XElement>();
        var datas = compArr.Elements("Item").ToList();
        for (int s = 0; s < 12; s++)
            if (avail[s] != 255 && avail[s] < datas.Count) bySlot[s] = datas[avail[s]];
        var infos = root.Element("compInfos")!;
        var propInfo = root.Element("propInfo")!;
        var propArr = propInfo.Element("aPropMetaData")!;

        foreach (var d in drawables)
        {
            if (d.Prop)
            {
                int n = propArr.Elements("Item").Count(p => Int(p, "anchorId") == d.Slot);
                propArr.Add(PropItem(d, n));
                numbers.Add(n);
                continue;
            }
            if (!bySlot.TryGetValue(d.Slot, out var comp))
                bySlot[d.Slot] = comp = new XElement("Item", Val("numAvailTex", "0"), Arr("aDrawblData3", "CPVDrawblData"));
            var list = comp.Element("aDrawblData3")!;
            int number = list.Elements("Item").Count();
            list.Add(DrawableItem(d));
            comp.Element("numAvailTex")!.SetAttributeValue("value", (Int(comp, "numAvailTex") + d.Textures).ToString(Inv));
            infos.Add(InfoItem(d.Slot, number, infos.Elements("Item").LastOrDefault(i => Int(i, "pedXml_compIdx") == d.Slot)));
            numbers.Add(number);
        }

        // components in slot order, availComp pointing at them
        compArr.RemoveNodes();
        int idx = 0;
        var newAvail = Enumerable.Repeat(255, 12).ToArray();
        foreach (var (slot, comp) in bySlot)
        {
            compArr.Add(comp);
            newAvail[slot] = idx++;
        }
        root.Element("availComp")!.Value = string.Join(' ', newAvail);
        var sortedInfos = infos.Elements("Item").OrderBy(i => Int(i, "pedXml_compIdx")).ThenBy(i => Int(i, "pedXml_drawblIdx")).ToList();
        infos.RemoveNodes();
        infos.Add(sortedInfos);

        // props in anchor order; the anchors list their props' texture counts
        var props = propArr.Elements("Item").OrderBy(p => Int(p, "anchorId")).ThenBy(p => Int(p, "propId")).ToList();
        propArr.RemoveNodes();
        propArr.Add(props);
        propInfo.Element("numAvailProps")!.SetAttributeValue("value", props.Count.ToString(Inv));
        var anchors = propInfo.Element("aAnchors")!;
        var oldAnchors = anchors.Elements("Item").ToDictionary(a => a.Element("anchor")?.Value.Trim() ?? "", a => a);
        anchors.RemoveNodes();
        foreach (var g in props.GroupBy(p => Int(p, "anchorId")).OrderBy(g => g.Key))
        {
            var id = g.Key >= 0 && g.Key < ClothingNames.AnchorIds.Length ? ClothingNames.AnchorIds[g.Key] : "ANCHOR_HEAD";
            var counts = string.Join(' ', g.Select(p => p.Element("texData")?.Elements("Item").Count() ?? 1));
            var item = oldAnchors.GetValueOrDefault(id) ?? new XElement("Item", new XElement("props"), new XElement("anchor", id));
            item.Element("props")!.Value = counts;
            anchors.Add(item);
        }
        return numbers;
    }

    private static int[] Avail(XElement root)
    {
        var parts = (root.Element("availComp")?.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var a = Enumerable.Repeat(255, 12).ToArray();
        for (int i = 0; i < Math.Min(12, parts.Length); i++) a[i] = int.TryParse(parts[i], Inv, out var v) ? v : 255;
        return a;
    }

    private static int Int(XElement e, string child) =>
        int.TryParse(e.Element(child)?.Attribute("value")?.Value, Inv, out var v) ? v : 0;

    private static XElement Val(string name, string value) => new(name, new XAttribute("value", value));
    private static XElement Arr(string name, string type) => new(name, new XAttribute("itemType", type));

    private static XElement DrawableItem(NewDrawable d) => new("Item",
        Val("propMask", d.RaceSpecific ? "17" : "1"),
        Val("numAlternatives", d.Alternatives.ToString(Inv)),
        new XElement("aTexData", new XAttribute("itemType", "CPVTextureData"),
            (d.TextureRaces.Count == 0 ? [d.RaceSpecific ? 1 : 0] : d.TextureRaces)
                .Select(r => new XElement("Item", Val("texId", r.ToString(Inv)), Val("distribution", "255")))),
        new XElement("clothData", Val("ownsCloth", d.Cloth ? "true" : "false")));

    /// <summary>A component info for a new drawable, like the slot's last one (else the defaults Rockstar's files use).</summary>
    private static XElement InfoItem(int slot, int number, XElement? like)
    {
        var item = new XElement("Item",
            new XElement("pedXml_audioID", like?.Element("pedXml_audioID")?.Value ?? "none"),
            new XElement("pedXml_audioID2", like?.Element("pedXml_audioID2")?.Value ?? "none"),
            new XElement("pedXml_expressionMods", "0 0 0 0 0"),
            Val("flags", "0"),
            new XElement("inclusions", "0"),
            new XElement("exclusions", "0"),
            new XElement("pedXml_vfxComps", like?.Element("pedXml_vfxComps")?.Value ?? "PV_COMP_HEAD"),
            Val("pedXml_flags", "0"),
            Val("pedXml_compIdx", slot.ToString(Inv)),
            Val("pedXml_drawblIdx", number.ToString(Inv)));
        return item;
    }

    private static XElement PropItem(NewDrawable d, int number) => new("Item",
        new XElement("audioId", "none"),
        new XElement("expressionMods", "0 0 0 0 0"),
        new XElement("texData", new XAttribute("itemType", "CPedPropTexData"),
            Enumerable.Range(0, d.Textures).Select(t => new XElement("Item",
                new XElement("inclusions", "0"), new XElement("exclusions", "0"), Val("texId", t.ToString(Inv)),
                Val("inclusionId", "0"), Val("exclusionId", "0"), Val("distribution", "255")))),
        new XElement("renderFlags"),
        Val("propFlags", "65536"),
        Val("flags", "0"),
        Val("anchorId", d.Slot.ToString(Inv)),
        Val("propId", number.ToString(Inv)),
        Val("stickyness", "0"));

    /// <summary>
    /// The drawables a set of clothing files makes (one per slot and number, its textures counted), in slot then
    /// number order, with the number each has in the files.
    /// </summary>
    public static List<(int Number, NewDrawable Drawable)> DrawablesOf(IEnumerable<ClothingPart> parts)
    {
        var list = new List<(int, NewDrawable)>();
        foreach (var g in parts.GroupBy(p => (p.Prop, p.Slot, p.Number)).OrderBy(g => g.Key.Prop).ThenBy(g => g.Key.Slot).ThenBy(g => g.Key.Number))
        {
            var main = g.Where(p => p.Kind == ClothingPartKind.Drawable).ToList();
            if (main.Count == 0) continue;                                   // textures of a drawable the mod doesn't have
            var tex = g.Where(p => p.Kind == ClothingPartKind.Texture)
                       .GroupBy(p => p.Letter).OrderBy(t => t.Key)
                       .Select(t => t.First().Race).ToList();
            bool race = main.Any(p => p.RaceSpecific);
            list.Add((g.Key.Number, new NewDrawable(g.Key.Prop, g.Key.Slot, race, tex,
                                                    main.Count(p => p.Alternative > 0), g.Any(p => p.Kind == ClothingPartKind.Cloth))));
        }
        return list;
    }
}
