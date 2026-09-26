using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Mdv.Core.Util;

/// <summary>
/// XML helpers that reproduce Python ElementTree semantics, so metas cloned from the
/// template library stay byte-faithful to what the original builder produced:
/// <list type="bullet">
///   <item><c>text</c> = the text before the first child element, <c>tail</c> = the text
///   after an element up to its next sibling (comments are dropped, as ET drops them);</item>
///   <item>serialization writes <c>&lt;Tag /&gt;</c> for empty elements, escapes
///   <c>&amp; &lt; &gt;</c> in text and additionally <c>" \r \n \t</c> in attributes.</item>
/// </list>
/// </summary>
public static class EtXml
{
    /// <summary>ET.fromstring — the root element, whitespace preserved.</summary>
    public static XElement Parse(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var sr = new StringReader(text);
        using var xr = XmlReader.Create(sr, settings);
        return XElement.Load(xr, LoadOptions.PreserveWhitespace);
    }

    /// <summary>ET.parse(path).getroot().</summary>
    public static XElement Load(string path)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var xr = XmlReader.Create(path, settings);
        return XElement.Load(xr, LoadOptions.PreserveWhitespace);
    }

    /// <summary>ET.fromstring that returns null instead of raising a ParseError.</summary>
    public static XElement? TryParse(string text)
    {
        try { return Parse(text); }
        catch (XmlException) { return null; }
    }

    // ------------------------------------------------------------ navigation

    /// <summary>el.find(path) for simple child paths ("A", "A/B") and ".//A".</summary>
    public static XElement? Find(XElement? el, string path) => el is null ? null : FindAll(el, path).FirstOrDefault();

    /// <summary>el.findall(path) for simple child paths ("A", "A/B") and ".//A".</summary>
    public static IEnumerable<XElement> FindAll(XElement el, string path)
    {
        if (path.StartsWith(".//", StringComparison.Ordinal))
        {
            var rest = path[3..];
            var parts = rest.Split('/');
            IEnumerable<XElement> cur = el.Descendants(parts[0]);
            foreach (var p in parts.Skip(1))
                cur = cur.SelectMany(c => c.Elements(p));
            return cur;
        }
        IEnumerable<XElement> nodes = new[] { el };
        foreach (var p in path.Split('/'))
            nodes = nodes.SelectMany(c => c.Elements(p));
        return nodes;
    }

    /// <summary>el.iter(tag): the element itself and every descendant, in document order.</summary>
    public static IEnumerable<XElement> Iter(XElement el, string tag) =>
        el.DescendantsAndSelf(tag);

    /// <summary>ET's <c>.text</c>: text before the first child element, or null.</summary>
    public static string? Text(XElement? el)
    {
        if (el is null) return null;
        StringBuilder? sb = null;
        foreach (var n in el.Nodes())
        {
            if (n is XElement) break;
            if (n is XText t)
                (sb ??= new StringBuilder()).Append(t.Value);
        }
        return sb?.ToString();
    }

    /// <summary>
    /// The Python idiom <c>e.text.strip() if e is not None and e.text else None</c>
    /// (note: whitespace-only text yields "" rather than null, as in Python).
    /// </summary>
    public static string? StrippedText(XElement? el)
    {
        var t = Text(el);
        return string.IsNullOrEmpty(t) ? null : t.Trim();
    }

    /// <summary>
    /// <c>_text(el, tag)</c> from overrides.py: stripped child text, null when empty.
    /// </summary>
    public static string? ChildTextNonEmpty(XElement? el, string tag)
    {
        var t = Text(Find(el, tag));
        if (string.IsNullOrEmpty(t)) return null;
        var s = t.Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>ET's <c>e.text = value</c>: replaces the text before the first child.</summary>
    public static void SetText(XElement el, string value)
    {
        var lead = new List<XNode>();
        foreach (var n in el.Nodes())
        {
            if (n is XElement) break;
            lead.Add(n);
        }
        foreach (var n in lead)
            if (n is XText or XComment) n.Remove();
        el.AddFirst(new XText(value));
    }

    /// <summary><c>_set_text(el, tag, value)</c>: sets the first direct child's text, if any.</summary>
    public static void SetChildText(XElement el, string tag, string value)
    {
        var e = el.Element(tag);
        if (e is not null) SetText(e, value);
    }

    /// <summary>
    /// ET's <c>list(el)</c> + <c>el.remove(child)</c> for every child: drops the child
    /// elements and their tails but keeps <c>el.text</c>.
    /// </summary>
    public static void RemoveChildElements(XElement el)
    {
        bool seenElement = false;
        foreach (var n in el.Nodes().ToList())
        {
            if (n is XElement) { seenElement = true; n.Remove(); }
            else if (seenElement && n is XText or XComment) n.Remove();
        }
    }

    /// <summary>ET.SubElement(parent, tag) — appended without any whitespace.</summary>
    public static XElement SubElement(XElement parent, string tag)
    {
        var e = new XElement(tag);
        parent.Add(e);
        return e;
    }

    // ---------------------------------------------------------- serialization

    /// <summary>ET.tostring(el, encoding="unicode"). With <paramref name="withTail"/>
    /// the element's tail is appended, as ET does for any element that has one.</summary>
    public static string ToString(XElement el, bool withTail = false)
    {
        var sb = new StringBuilder();
        Write(sb, el);
        if (withTail)
            sb.Append(EscapeText(Tail(el) ?? ""));
        return sb.ToString();
    }

    /// <summary>ET's <c>.tail</c>: text after the element up to its next element sibling.</summary>
    public static string? Tail(XElement el)
    {
        StringBuilder? sb = null;
        for (var n = el.NextNode; n is not null && n is not XElement; n = n.NextNode)
            if (n is XText t) (sb ??= new StringBuilder()).Append(t.Value);
        return sb?.ToString();
    }

    private static void Write(StringBuilder sb, XElement el)
    {
        var tag = el.Name.LocalName;
        sb.Append('<').Append(tag);
        foreach (var a in el.Attributes())
        {
            if (a.IsNamespaceDeclaration) continue;
            sb.Append(' ').Append(a.Name.LocalName).Append("=\"").Append(EscapeAttrib(a.Value)).Append('"');
        }

        var text = Text(el);
        bool hasChildren = el.Elements().Any();
        if (!string.IsNullOrEmpty(text) || hasChildren)
        {
            sb.Append('>');
            if (!string.IsNullOrEmpty(text)) sb.Append(EscapeText(text));
            foreach (var child in el.Elements())
            {
                Write(sb, child);
                var tail = Tail(child);
                if (!string.IsNullOrEmpty(tail)) sb.Append(EscapeText(tail));
            }
            sb.Append("</").Append(tag).Append('>');
        }
        else
        {
            sb.Append(" />");
        }
    }

    private static string EscapeText(string s)
    {
        if (s.IndexOfAny(['&', '<', '>']) < 0) return s;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string EscapeAttrib(string s)
    {
        if (s.IndexOfAny(['&', '<', '>', '"', '\r', '\n', '\t']) < 0) return s;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("\r", "&#13;").Replace("\n", "&#10;")
                .Replace("\t", "&#09;");
    }
}
