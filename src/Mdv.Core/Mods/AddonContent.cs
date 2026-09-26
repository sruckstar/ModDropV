using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>A vehicle an add-on declares (vehicles.meta).</summary>
/// <param name="Model">the spawn name (modelName)</param>
/// <param name="GameName">its text label (gameName)</param>
/// <param name="Make">the maker's text label (vehicleMakeName)</param>
/// <param name="Class">VC_SPORT, VC_MOTORCYCLE…</param>
public sealed record AddonVehicle(string Model, string? GameName, string? Make, string? Class);

/// <summary>A ped an add-on declares (peds.meta).</summary>
public sealed record AddonPed(string Name, string? PedType);

/// <summary>An MP clothing collection an add-on brings (<c>mp_m_freemode_01_mp_m_ftbmodels_arai</c>).</summary>
/// <param name="Ped">mp_m_freemode_01 / mp_f_freemode_01</param>
/// <param name="DlcName">the collection's own name (<c>mp_m_ftbmodels_arai</c>)</param>
public sealed record AddonCollection(string Ped, string DlcName)
{
    public string FullName => $"{Ped}_{DlcName}";
    public bool IsFemale => Ped.StartsWith("mp_f_", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A vehicle modkit an add-on declares (carcols.meta) — its id must be unique in the game.</summary>
/// <param name="File">the carcols.meta it is in (path inside the pack / the mod)</param>
public sealed record AddonKit(int Id, string Name, string File);

/// <summary>
/// What an add-on (a finished pack or files still to be packed) declares: its vehicles, peds and
/// modkits, the model files it streams, the text labels it brings, and the data file types.
/// </summary>
public sealed partial class AddonContent
{
    public List<AddonVehicle> Vehicles { get; } = [];
    public List<AddonPed> Peds { get; } = [];
    public List<AddonKit> Kits { get; } = [];
    /// <summary>MP clothing collections (from their ymt and shop metas).</summary>
    public List<AddonCollection> Collections { get; } = [];
    /// <summary>Full names of the collections a shop meta registers (<c>mp_m_freemode_01_mp_m_x</c>).</summary>
    public HashSet<string> Shops { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Streamed file names (adder.yft, adder+hi.ytd, a_m_y_x/head_000_r.ydd).</summary>
    public List<string> Streamed { get; } = [];
    /// <summary>Text labels by hash (gxt2, AddTextEntry in a FiveM script).</summary>
    public Dictionary<uint, string> Labels { get; } = [];
    public HashSet<string> DataTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The editions its models are built for (Legacy / Enhanced resource versions found).</summary>
    public HashSet<GameEdition> ModelEditions { get; } = [];
    /// <summary>The edition its models are built for, when they all say the same (null: unknown, none or mixed).</summary>
    public GameEdition? ModelsEdition => ModelEditions.Count == 1 ? ModelEditions.First() : null;

    /// <summary>The kind of add-on: vehicles first, then peds, then clothes (null: none of them).</summary>
    public ModCategory? Kind =>
        Vehicles.Count > 0 || DataTypes.Contains("VEHICLE_METADATA_FILE") ? ModCategory.Vehicle
        : Peds.Count > 0 || DataTypes.Contains("PED_METADATA_FILE") ? ModCategory.Ped
        : Collections.Count > 0 || DataTypes.Contains("SHOP_PED_APPAREL_META_FILE") ? ModCategory.Clothing
        : null;

    public void AddCollection(string ped, string dlcName)
    {
        if (!Collections.Any(c => c.Ped.Equals(ped, StringComparison.OrdinalIgnoreCase) && c.DlcName.Equals(dlcName, StringComparison.OrdinalIgnoreCase)))
            Collections.Add(new AddonCollection(ped.ToLowerInvariant(), dlcName.ToLowerInvariant()));
    }

    /// <summary>Models (drawables) and props streamed for a collection.</summary>
    public (int Models, int Props) CountsOf(AddonCollection c)
    {
        int models = 0, props = 0;
        foreach (var s in Streamed)
        {
            var segs = s.Split('/');
            if (segs.Length < 2 || !s.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase)) continue;
            var dir = segs[^2];
            if (dir.Equals(c.FullName, StringComparison.OrdinalIgnoreCase)) models++;
            else if (dir.Equals($"{c.Ped}_p_{c.DlcName}", StringComparison.OrdinalIgnoreCase)) props++;
        }
        return (models, props);
    }

    /// <summary>The spawn names, as trainers take them.</summary>
    public IEnumerable<string> SpawnNames => Vehicles.Count > 0 ? Vehicles.Select(v => v.Model) : Peds.Select(p => p.Name);

    public string? Label(string? key) =>
        key is { Length: > 0 } && Labels.TryGetValue(Gxt2.Joaat(key), out var t) && t.Length > 0 ? t : null;

    /// <summary>"Toyota Innova" — the in-game name with its maker, when the labels have them.</summary>
    public string? DisplayName(AddonVehicle v)
    {
        var name = Label(v.GameName);
        if (name is null) return null;
        var make = Label(v.Make);
        return make is null || name.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? name : $"{make} {name}";
    }

    /// <summary>"innovabcm (Toyota Innova)" per vehicle, the ped names, or the clothing collections.</summary>
    public IEnumerable<string> Describe() =>
        Vehicles.Count > 0
            ? Vehicles.Select(v => DisplayName(v) is { } d ? $"{d} ({v.Model})" : v.Model)
            : Peds.Count > 0 || Collections.Count == 0 ? Peds.Select(p => p.Name)
            : Collections.Select(c => $"{ClothingNames.PedLabel(c.Ped)} · {c.DlcName}");

    // ------------------------------------------------------------------ data files

    /// <summary>Game data file types by their XML root tag.</summary>
    public static readonly Dictionary<string, string> TypeByRoot = new(StringComparer.Ordinal)
    {
        ["CVehicleModelInfo__InitDataList"] = "VEHICLE_METADATA_FILE",
        ["CHandlingDataMgr"] = "HANDLING_FILE",
        ["CVehicleModelInfoVariation"] = "VEHICLE_VARIATION_FILE",
        ["CVehicleModelInfoVarGlobal"] = "CARCOLS_FILE",
        ["CVehicleMetadataMgr"] = "VEHICLE_LAYOUTS_FILE",
        ["CPedModelInfo__InitDataList"] = "PED_METADATA_FILE",
        ["CPedModelInfo__PersonalityDataList"] = "PED_PERSONALITY_FILE",
        ["CExtraTextMetaFile"] = "TEXTFILE_METAFILE",
        ["CContentUnlocks"] = "CONTENT_UNLOCKING_META_FILE",
        ["CVehicleShopData"] = "VEHICLE_SHOP_DLC_FILE",
        ["CVehicleModelInfoVarGlobalOverride"] = "CARCOLS_FILE",
        ["ShopPedApparel"] = "SHOP_PED_APPAREL_META_FILE",
    };

    /// <summary>Read what a data file of type <paramref name="type"/> declares.</summary>
    /// <param name="file">where it is (shown in messages)</param>
    public void AddData(string type, string text, string file)
    {
        DataTypes.Add(type);
        switch (type.ToUpperInvariant())
        {
            case "VEHICLE_METADATA_FILE": ReadVehicles(text); break;
            case "PED_METADATA_FILE": ReadPeds(text); break;
            case "CARCOLS_FILE": ReadKits(text, file); break;
            case "SHOP_PED_APPAREL_META_FILE": ReadShop(text); break;
        }
    }

    /// <summary>A shop meta: the collection it registers.</summary>
    private void ReadShop(string text)
    {
        var root = ParseXml(text)?.Root;
        var ped = root?.Element("pedName")?.Value.Trim();
        var dlc = root?.Element("dlcName")?.Value.Trim();
        var full = root?.Element("fullDlcName")?.Value.Trim();
        if (ped is not { Length: > 0 } || dlc is not { Length: > 0 }) return;
        Shops.Add(full is { Length: > 0 } ? full : $"{ped}_{dlc}");
        if (ped.StartsWith("mp_", StringComparison.OrdinalIgnoreCase)) AddCollection(ped, dlc);
    }

    /// <summary>A data file's XML, forgiving of what hand-edited metas carry (a BOM, junk before the root).</summary>
    public static XDocument? ParseXml(string text)
    {
        int start = text.IndexOf('<');
        if (start < 0) return null;
        try
        {
            return XDocument.Parse(text[start..]);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<modelName>\s*([^<\s]+)\s*</modelName>", RegexOptions.IgnoreCase)] private static partial Regex ModelNameRe();
    [GeneratedRegex(@"<Item[^>]*>\s*<Name>\s*([^<\s]+)\s*</Name>")] private static partial Regex PedNameRe();
    [GeneratedRegex(@"<kitName>\s*([^<]+?)\s*</kitName>\s*<id\s+value=""(\d+)""", RegexOptions.IgnoreCase)] private static partial Regex KitRe();

    private static string? Val(XElement item, string name) =>
        item.Element(name)?.Value.Trim() is { Length: > 0 } v ? v : null;

    private void ReadVehicles(string text)
    {
        if (ParseXml(text)?.Root?.Element("InitDatas") is { } list)
        {
            foreach (var item in list.Elements("Item"))
                if (Val(item, "modelName") is { } model)
                    Add(new AddonVehicle(model, Val(item, "gameName"), Val(item, "vehicleMakeName"), Val(item, "vehicleClass")));
            return;
        }
        foreach (Match m in ModelNameRe().Matches(text))          // broken XML: the names at least
            Add(new AddonVehicle(m.Groups[1].Value, null, null, null));

        void Add(AddonVehicle v)
        {
            if (!Vehicles.Any(x => x.Model.Equals(v.Model, StringComparison.OrdinalIgnoreCase))) Vehicles.Add(v);
        }
    }

    private void ReadPeds(string text)
    {
        if (ParseXml(text)?.Root?.Element("InitDatas") is { } list)
        {
            foreach (var item in list.Elements("Item"))
                if (Val(item, "Name") is { } name) Add(new AddonPed(name, Val(item, "Pedtype")));
            return;
        }
        int i0 = text.IndexOf("<InitDatas", StringComparison.Ordinal), i1 = text.IndexOf("</InitDatas>", StringComparison.Ordinal);
        if (i0 >= 0 && i1 > i0)
            foreach (Match m in PedNameRe().Matches(text[i0..i1])) Add(new AddonPed(m.Groups[1].Value, null));

        void Add(AddonPed p)
        {
            if (!Peds.Any(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) Peds.Add(p);
        }
    }

    private void ReadKits(string text, string file)
    {
        foreach (var k in KitsIn(text, file))
            if (!Kits.Any(x => x.Id == k.Id && x.Name == k.Name)) Kits.Add(k);
    }

    /// <summary>The modkits a carcols.meta declares.</summary>
    public static IEnumerable<AddonKit> KitsIn(string text, string file)
    {
        if (ParseXml(text)?.Root?.Element("Kits") is { } kits)
        {
            foreach (var item in kits.Elements("Item"))
                if (int.TryParse(item.Element("id")?.Attribute("value")?.Value, out int id))
                    yield return new AddonKit(id, Val(item, "kitName") ?? "", file);
            yield break;
        }
        foreach (Match m in KitRe().Matches(text))
            yield return new AddonKit(int.Parse(m.Groups[2].Value), m.Groups[1].Value, file);
    }

    /// <summary>carcols.meta with a kit's id changed (the kit name, which carvariations.meta refers to, stays).</summary>
    public static string WithKitId(string text, AddonKit kit, int newId)
    {
        var re = new Regex(@"(<kitName>\s*" + Regex.Escape(kit.Name) + @"\s*</kitName>\s*<id\s+value="")" + kit.Id + @"("")",
                           RegexOptions.IgnoreCase);
        var updated = re.Replace(text, m => m.Groups[1].Value + newId + m.Groups[2].Value, 1);
        if (updated == text)
            throw new InvalidDataException($"{kit.File}: the modkit {kit.Name} (id {kit.Id}) could not be found to renumber.");
        return updated;
    }

    // ------------------------------------------------------------------ labels

    [GeneratedRegex(@"AddTextEntry\s*\(\s*['""]([^'""]+)['""]\s*,\s*['""]([^'""]*)['""]")] private static partial Regex AddTextEntryRe();
    [GeneratedRegex(@"AddTextEntryByHash\s*\(\s*(0x[0-9a-fA-F]+|\d+)\s*,\s*['""]([^'""]*)['""]")] private static partial Regex AddTextEntryByHashRe();

    /// <summary>Text labels a FiveM script sets (AddTextEntry('LABEL', 'Text')), by label hash.</summary>
    public static Dictionary<uint, string> LuaLabels(string lua)
    {
        var labels = new Dictionary<uint, string>();
        foreach (Match m in AddTextEntryRe().Matches(lua)) labels[Gxt2.Joaat(m.Groups[1].Value)] = m.Groups[2].Value;
        foreach (Match m in AddTextEntryByHashRe().Matches(lua))
        {
            var s = m.Groups[1].Value;
            uint h = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt32(s[2..], 16)
                : uint.TryParse(s, out var u) ? u : 0;
            if (h != 0) labels[h] = m.Groups[2].Value;
        }
        return labels;
    }

    // ------------------------------------------------------------------ finished packs

    /// <summary>A content.xml data file entry: its type and path inside the pack (device and %PLATFORM% resolved).</summary>
    public sealed record PackDataFile(string Type, string Path);

    [GeneratedRegex(@"<Item>\s*<filename>\s*([^<]+?)\s*</filename>\s*<fileType>\s*([A-Z0-9_]+)\s*</fileType>", RegexOptions.IgnoreCase)]
    private static partial Regex ContentItemRe();
    [GeneratedRegex(@"<deviceName>\s*([^<]+?)\s*</deviceName>")] private static partial Regex DeviceRe();
    [GeneratedRegex(@"<nameHash>\s*([^<]+?)\s*</nameHash>")] private static partial Regex NameHashRe();
    [GeneratedRegex(@"<subPackCount\s+value=""(\d+)""")] private static partial Regex SubPackRe();

    /// <summary>The data files a content.xml lists.</summary>
    public static List<PackDataFile> ContentFiles(string contentXml)
    {
        var list = new List<PackDataFile>();
        foreach (Match m in ContentItemRe().Matches(contentXml))
        {
            var name = m.Groups[1].Value.Replace('\\', '/');
            int colon = name.IndexOf(":/", StringComparison.Ordinal);
            var rel = colon >= 0 ? name[(colon + 2)..] : name;
            rel = rel.Replace("%PLATFORM%", "x64", StringComparison.OrdinalIgnoreCase).TrimStart('/');
            list.Add(new PackDataFile(m.Groups[2].Value.ToUpperInvariant(), rel));
        }
        return list;
    }

    /// <summary>What a finished add-on pack (dlc.rpf with setup2.xml) holds.</summary>
    public sealed class FinishedPack
    {
        public required string Path { get; init; }
        /// <summary>dlc_innovabcm — the name the game mounts it under.</summary>
        public string? Device { get; init; }
        public string? NameHash { get; init; }
        public List<PackDataFile> DataFiles { get; } = [];
        /// <summary>Its sub-packs (<c>dlc1.rpf</c>… next to it, as setup2.xml's subPackCount says) — they go into the game with it.</summary>
        public List<string> SubPacks { get; } = [];
        public AddonContent Content { get; } = new();
        public List<string> Warnings { get; } = [];
    }

    /// <summary>Read a finished pack: setup2.xml, the content.xml data files, the metas that declare things, labels, streamed files.</summary>
    public static FinishedPack ReadPack(string dlcRpf)
    {
        using var arc = RpfArchive.Open(dlcRpf);
        var tree = arc.Tree().Where(t => !t.IsDir).ToList();
        string? Text(string inner)
        {
            var hit = tree.FirstOrDefault(t => t.Path.Equals(inner, StringComparison.OrdinalIgnoreCase));
            return hit is null ? null : TextIo.DecodeUtf8Sig(arc.ReadContent(hit.Entry), strict: false);
        }

        var setup = Text("setup2.xml") ?? throw new InvalidDataException($"{System.IO.Path.GetFileName(dlcRpf)} has no setup2.xml — not an add-on pack.");
        var pack = new FinishedPack
        {
            Path = dlcRpf,
            Device = DeviceRe().Match(setup) is { Success: true } d ? d.Groups[1].Value : null,
            NameHash = NameHashRe().Match(setup) is { Success: true } n ? n.Groups[1].Value : null,
        };
        if (Text("content.xml") is { } content) pack.DataFiles.AddRange(ContentFiles(content));
        foreach (var f in pack.DataFiles)
        {
            pack.Content.DataTypes.Add(f.Type);
            if (f.Type is not ("VEHICLE_METADATA_FILE" or "PED_METADATA_FILE" or "CARCOLS_FILE" or "SHOP_PED_APPAREL_META_FILE")) continue;
            if (Text(f.Path) is { } text) pack.Content.AddData(f.Type, text, f.Path);
            else pack.Warnings.Add($"content.xml lists {f.Path}, but the pack has no such file.");
        }

        Walk(arc, "", 0);
        // sub-packs: dlc1.rpf… mounted with it under the same device
        if (SubPackRe().Match(setup) is { Success: true } sp && int.TryParse(sp.Groups[1].Value, out int subs))
            for (int i = 1; i <= Math.Min(subs, 16); i++)
            {
                var sub = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dlcRpf)!, $"dlc{i}.rpf");
                if (!File.Exists(sub))
                {
                    pack.Warnings.Add($"setup2.xml says the pack has {subs} sub-pack(s), but dlc{i}.rpf is not next to its dlc.rpf — the game can crash without it.");
                    continue;
                }
                pack.SubPacks.Add(sub);
                using var s = RpfArchive.Open(sub);
                Walk(s, "", 0);
            }
        // the collections whose ymt it streams
        foreach (var st in pack.Content.Streamed.Where(s => s.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase)))
            if (ClothingNames.WearerOfYmt(st.Split('/')[^1]) is { IsMp: true, Collection: { } coll } w) pack.Content.AddCollection(w.Ped, coll);
        return pack;

        void Walk(RpfArchive a, string prefix, int depth)
        {
            foreach (var t in a.Tree())
            {
                if (t.IsDir) continue;
                var e = t.Entry;
                var ext = PathUtil.SuffixLower(e.Name);
                if (ext == ".rpf" && e.StoredRaw && depth < 3)
                {
                    using var nested = a.OpenNested(e);
                    bool lang = prefix.Length == 0 && t.Path.Contains("/lang/", StringComparison.OrdinalIgnoreCase);
                    if (lang && !e.Name.StartsWith("american", StringComparison.OrdinalIgnoreCase)) continue;
                    Walk(nested, prefix + t.Path + "/", depth + 1);
                }
                else if (ext == ".gxt2")
                {
                    try
                    {
                        foreach (var (h, s) in Gxt2.Read(a.ReadContent(e))) pack.Content.Labels.TryAdd(h, s);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
                    {
                        // an unreadable text table only costs the in-game names
                    }
                }
                else if (e.IsResource && depth > 0)
                {
                    pack.Content.Streamed.Add(t.Path);
                    if (ResourceEditions.EditionOf(ext, (int)((((e.X8 >> 28) & 0xF) << 4) | ((e.XC >> 28) & 0xF))) is { } ed)
                        pack.Content.ModelEditions.Add(ed);
                }
            }
        }
    }
}
