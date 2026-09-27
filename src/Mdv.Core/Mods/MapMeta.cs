using Mdv.Core;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using CodeWalker.GameFiles;

namespace Mdv.Core.Mods;

/// <summary>What a map placement file (<c>.ymap</c>) puts into the world.</summary>
/// <param name="Name">the file's name without the extension</param>
/// <param name="Archetypes">the models its entities are, each once (lower case)</param>
/// <param name="Entities">how many entities it places</param>
/// <param name="Center">the middle of what it places (x, y, z), when it places anything</param>
public sealed record YmapInfo(string Name, IReadOnlyList<string> Archetypes, int Entities, (float X, float Y, float Z)? Center);

/// <summary>
/// The map files of GTA V — placements (<c>.ymap</c>, <c>CMapData</c>), archetypes (<c>.ytyp</c>, <c>CMapTypes</c>)
/// and the pack manifest (<c>_manifest.ymf</c>, <c>CPackFileMetaData</c>) — read and written through
/// CodeWalker's XML form of them. Their binary form is the same in Legacy and Enhanced (RSC v2 meta).
/// </summary>
public static class MapMeta
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The file as CodeWalker's XML; <paramref name="name"/> (<c>x.ymap</c>) says what it is. The data may be compressed or inflated.</summary>
    public static XDocument Read(byte[] data, string name)
    {
        if (IsPso(data))
        {
            // a pack manifest in the game's PSO form (what map tools and Rockstar write)
            var ymf = new YmfFile();
            try
            {
                ymf.Load(data, new RpfBinaryFileEntry { Name = name });
                var pso = MetaXml.GetXml(ymf, out _);
                if (!string.IsNullOrEmpty(pso)) return XDocument.Parse(pso);
            }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                throw new InvalidDataException(L.T($"{name} could not be read: {ex.Message}"), ex);
            }
            throw new InvalidDataException(L.T($"{name} could not be read."));
        }
        if (data.Length < 16 || BitConverter.ToUInt32(data) != Rpf.Rpf7.Rsc7Magic)
        {
            // an XML form of it already (CodeWalker / OpenIV export)
            var text = Util.TextIo.DecodeUtf8Sig(data, strict: false);
            return AddonContent.ParseXml(text) ?? throw new InvalidDataException(L.T($"{name} is neither a map file nor its XML form."));
        }
        var raw = data;
        var entry = RpfFile.CreateResourceFileEntry(ref raw, 0);
        long virt = Rpf.Rpf7.ResVirtualSize(entry.SystemFlags) + Rpf.Rpf7.ResVirtualSize(entry.GraphicsFlags);
        if (raw.Length != virt)
        {
            try
            {
                raw = ResourceBuilder.Decompress(raw);
            }
            catch (InvalidDataException)
            {
                // inflated already
            }
        }
        entry.Name = name;
        string? xml;
        try
        {
            xml = Path.GetExtension(name).ToLowerInvariant() switch
            {
                ".ymap" => MetaXml.GetXml(Load<YmapFile>(raw, entry), out _),
                ".ytyp" => MetaXml.GetXml(Load<YtypFile>(raw, entry), out _),
                ".ymf" => MetaXml.GetXml(Load<YmfFile>(raw, entry), out _),
                ".ymt" => MetaXml.GetXml(Load<YmtFile>(raw, entry), out _),
                _ => throw new InvalidDataException(L.T($"{name}: not a map file.")),
            };
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException(L.T($"{name} could not be read: {ex.Message}"), ex);
        }
        if (string.IsNullOrEmpty(xml)) throw new InvalidDataException(L.T($"{name} could not be read."));
        return XDocument.Parse(xml);
    }

    private static T Load<T>(byte[] data, RpfResourceFileEntry entry) where T : PackedFile, new()
    {
        var f = new T();
        f.Load(data, entry);
        return f;
    }

    /// <summary>
    /// The binary file for the XML form; <paramref name="name"/> is the file's name. Placements and archetypes are
    /// RSC meta (compressed); a pack manifest (<c>.ymf</c>) is written as PSO, as the game's own are.
    /// </summary>
    public static byte[] Write(XDocument doc, string name)
    {
        var x = new XmlDocument();
        x.LoadXml(doc.ToString(SaveOptions.DisableFormatting));
        var format = name.EndsWith(".ymf", StringComparison.OrdinalIgnoreCase) ? MetaFormat.PSO : MetaFormat.RSC;
        return XmlMeta.GetData(x, format, name) ?? throw new InvalidDataException(L.T($"{name} could not be built."));
    }

    /// <summary>Is the data a map file's XML form (CodeWalker / OpenIV export) rather than the binary file?</summary>
    public static bool IsXml(byte[] data) => (data.Length < 4 || BitConverter.ToUInt32(data) != Rpf.Rpf7.Rsc7Magic) && !IsPso(data);

    private static bool IsPso(byte[] data) => data.Length >= 8 && data[0] == 'P' && data[1] == 'S' && data[2] == 'I' && data[3] == 'N';

    /// <summary>What a placement file places.</summary>
    public static YmapInfo Ymap(XDocument doc, string name)
    {
        var archetypes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        double sx = 0, sy = 0, sz = 0;
        foreach (var item in doc.Root?.Element("entities")?.Elements("Item") ?? [])
        {
            n++;
            var arch = item.Element("archetypeName")?.Value.Trim();
            if (arch is { Length: > 0 } && seen.Add(arch)) archetypes.Add(arch.ToLowerInvariant());
            if (item.Element("position") is { } p)
            {
                sx += F(p, "x");
                sy += F(p, "y");
                sz += F(p, "z");
            }
        }
        return new YmapInfo(Path.GetFileNameWithoutExtension(name), archetypes, n,
                            n > 0 ? ((float)(sx / n), (float)(sy / n), (float)(sz / n)) : null);
    }

    /// <summary>The archetypes (model names) an archetypes file defines, lower case.</summary>
    public static List<string> Archetypes(XDocument doc) =>
        [.. (doc.Root?.Element("archetypes")?.Elements("Item") ?? [])
            .Select(i => i.Element("name")?.Value.Trim().ToLowerInvariant())
            .OfType<string>().Where(s => s.Length > 0).Distinct()];

    /// <summary>The name of the archetypes file itself (CMapTypes.name), as the game looks it up.</summary>
    public static string? TypesName(XDocument doc) => doc.Root?.Element("name")?.Value.Trim() is { Length: > 0 } s ? s : null;

    /// <summary>
    /// A pack manifest that makes each placement file load the archetypes files it needs
    /// (<c>imapDependencies_2</c>: ymap → ytyp names).
    /// </summary>
    public static XDocument Manifest(IEnumerable<(string Ymap, IReadOnlyList<string> Ytyps)> deps) =>
        new(new XElement("CPackFileMetaData",
            new XElement("MapDataGroups", new XAttribute("itemType", "CMapDataGroup")),
            new XElement("HDTxdBindingArray", new XAttribute("itemType", "CHDTxdAssetBinding")),
            new XElement("imapDependencies", new XAttribute("itemType", "CImapDependency")),
            new XElement("imapDependencies_2", new XAttribute("itemType", "CImapDependencies"),
                deps.Where(d => d.Ytyps.Count > 0).Select(d => new XElement("Item",
                    new XElement("imapName", d.Ymap),
                    new XElement("manifestFlags"),
                    new XElement("itypDepArray", d.Ytyps.Select(t => new XElement("Item", t)))))),
            new XElement("itypDependencies_2", new XAttribute("itemType", "CItypDependencies")),
            new XElement("Interiors", new XAttribute("itemType", "CInteriorBoundsFiles"))));

    /// <summary>The hash a name stands for in the game's files: <c>hash_E0D5DB6B</c> as it is, a plain name hashed (lower case).</summary>
    public static uint Hash(string name) =>
        name.StartsWith("hash_", StringComparison.OrdinalIgnoreCase) &&
        uint.TryParse(name.AsSpan(5), NumberStyles.HexNumber, Inv, out var h)
            ? h
            : Gxt2.Joaat(name);

    /// <summary>Teach CodeWalker names (file stems of a mod or pack), so the XML it writes says them instead of hashes.</summary>
    public static void Know(IEnumerable<string> names)
    {
        foreach (var n in names)
            if (n.Length > 0) JenkIndex.Ensure(n.ToLowerInvariant());
    }

    private static double F(XElement e, string attr) =>
        double.TryParse(e.Attribute(attr)?.Value, NumberStyles.Float, Inv, out var v) ? v : 0;
}
