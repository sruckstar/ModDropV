using System.Xml.Linq;
using CodeWalker.GameFiles;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// Animation dictionaries (.ycd): the name scripts load one by (<c>REQUEST_ANIM_DICT("natureheroes@flight")</c> — the
/// file's name), the clips in it, and the game's form of one that comes as CodeWalker's XML (<c>x.ycd.xml</c>).
/// Legacy and Enhanced read the same .ycd (version 46), so one never needs converting.
/// </summary>
public static class AnimDicts
{
    public const string XmlSuffix = ".ycd.xml";

    /// <summary>A .ycd or its XML form.</summary>
    public static bool IsAnimFile(string name) =>
        name.EndsWith(".ycd", StringComparison.OrdinalIgnoreCase) || name.EndsWith(XmlSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool IsXml(string name) => name.EndsWith(XmlSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>"natureheroes@flight" for natureheroes@flight.ycd and natureheroes@flight.ycd.xml.</summary>
    public static string DictName(string name) =>
        IsXml(name) ? name[..^XmlSuffix.Length] : Path.GetFileNameWithoutExtension(name);

    /// <summary>The clip names of a dictionary (empty when it can't be read).</summary>
    public static List<string> Clips(string path)
    {
        try
        {
            if (IsXml(path))
                return [.. XDocument.Load(path).Root?.Element("Clips")?.Elements("Item")
                                     .Select(i => ShortName(i.Element("Name")?.Value ?? i.Element("Hash")?.Value))
                                     .OfType<string>() ?? []];
            var ycd = Load(File.ReadAllBytes(path), Path.GetFileName(path));
            return [.. (ycd.ClipMapEntries ?? []).Select(c => ShortName(c.Clip?.Name) ?? c.Hash.ToString()).Order(StringComparer.Ordinal)];
        }
        catch (Exception)
        {
            return [];                    // CodeWalker throws plain exceptions on a broken file; the names are only shown
        }
    }

    /// <summary>"hover_idle" for "pack:/hover_idle.clip".</summary>
    private static string? ShortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().Replace('\\', '/');
        n = n[(n.LastIndexOf('/') + 1)..];
        return n.EndsWith(".clip", StringComparison.OrdinalIgnoreCase) ? n[..^5] : n;
    }

    /// <summary>The game's form (.ycd) of a dictionary's CodeWalker XML.</summary>
    /// <exception cref="InvalidDataException">not a clip dictionary's XML</exception>
    public static byte[] Build(string xmlPath)
    {
        try
        {
            var text = TextIo.DecodeUtf8Sig(File.ReadAllBytes(xmlPath), strict: false);
            if (ModDetector.RootTag(text) != "ClipDictionary")
                throw new InvalidDataException(L.T($"{Path.GetFileName(xmlPath)} is not an animation dictionary in XML form."));
            return XmlYcd.GetYcd(text).Save();
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException(L.T($"{Path.GetFileName(xmlPath)} could not be built into a .ycd: {ex.Message}"), ex);
        }
    }

    private static YcdFile Load(byte[] raw, string name)
    {
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
        var ycd = new YcdFile();
        ycd.Load(raw, entry);
        if (ycd.LoadException is { } why) throw new InvalidDataException(why);
        return ycd;
    }
}
