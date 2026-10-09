using Mdv.Core;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Mdv.Core.Index;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// One instruction of an OIV package, flattened: <see cref="Path"/> is game-relative for a loose
/// file (<c>scripts/foo.ini</c>) or a game path through archives when <see cref="InArchive"/>
/// (<c>update/update.rpf/common/data/dlclist.xml</c>).
/// </summary>
public abstract record OivStep(string Path, bool InArchive);

/// <summary><c>&lt;add source="x"&gt;path&lt;/add&gt;</c>: put a file of the package (<see cref="Source"/>, full path) there.</summary>
public sealed record OivAdd(string Path, bool InArchive, string Source) : OivStep(Path, InArchive);

/// <summary><c>&lt;delete&gt;path&lt;/delete&gt;</c>.</summary>
public sealed record OivDelete(string Path, bool InArchive) : OivStep(Path, InArchive);

/// <summary><c>&lt;archive path="…" createIfNotExist="True"&gt;</c> — the steps inside it follow with longer paths.</summary>
public sealed record OivArchive(string Path, bool InArchive, bool Create) : OivStep(Path, InArchive);

/// <summary><c>&lt;xml path="…"&gt;</c> with its add / replace / remove edits.</summary>
public sealed record OivXml(string Path, bool InArchive, IReadOnlyList<OivXmlEdit> Edits) : OivStep(Path, InArchive);

/// <param name="Content">the elements to add / put in place (as XML text)</param>
/// <param name="First">add as the first children instead of the last</param>
public sealed record OivXmlEdit(XmlPatchMode Mode, string XPath, IReadOnlyList<string> Content, bool First = false);

/// <summary><c>&lt;text path="…" createIfNotExist="…"&gt;</c> with its line edits.</summary>
public sealed record OivText(string Path, bool InArchive, bool Create, IReadOnlyList<OivTextEdit> Edits) : OivStep(Path, InArchive);

/// <summary>
/// Put a file of the mod (<see cref="Source"/>) over the game's file of that name, wherever it is — <see cref="OivStep.Path"/>
/// is what to look it up by (<c>x.meta</c>, <c>common/data/x.meta</c>); only in packages read from a game-folder layout
/// (<see cref="DlcPackSet"/>), found when installing.
/// </summary>
public sealed record OivLocate(string Path, bool InArchive, string Source) : OivStep(Path, InArchive);

public enum OivTextMode { Add, InsertBefore, InsertAfter, Replace, Delete }

/// <param name="Line">the line an insert / replace / delete looks for (whitespace around it and case don't matter)</param>
/// <param name="Text">the text added / put in place</param>
public sealed record OivTextEdit(OivTextMode Mode, string? Line, string Text);

/// <summary>An OIV package (OpenIV's install format): what it says about itself and its instructions.</summary>
public sealed class OivPackage : ModPackage
{
    public override ModCategory Category => ModCategory.Package;
    /// <summary>The folder holding assembly.xml (its files are under <c>content/</c>).</summary>
    public required string Root { get; init; }
    /// <summary>Package format (<c>2.2</c>).</summary>
    public string FormatVersion { get; init; } = "";
    public string? Link { get; init; }
    public string? IconPath { get; init; }
    /// <summary>The package's header colour (<c>#AARRGGBB</c>) and whether its text is meant to be black on it.</summary>
    public string? HeaderColor { get; init; }
    public bool BlackText { get; init; }
    public List<OivStep> Steps { get; } = [];
    /// <summary>A mod shipped as several packages ("Part ONE" … "Part FIVE"): their names, in install order.</summary>
    public List<string> PartNames { get; } = [];
    /// <summary>The map World Travel (Liberty City Preservation Project) runs with, when the package has it (<see cref="WorldTravel"/>).</summary>
    public WorldTravelMap WorldTravelMap { get; set; } = WorldTravelMap.StoryMode;
    /// <summary>No assembly.xml: read from finished packs laid out like the game folder (<see cref="DlcPackSet"/>).</summary>
    public bool FromLayout { get; init; }
}

/// <summary>
/// Big mods come as several OIV packages meant to go in one after another ("X - Part ONE.oiv" … "X - Part FIVE.oiv",
/// "1. X.oiv", "X part 2.oiv"): they are told apart from unrelated packages (a Legacy and an Enhanced build) by one
/// base name and a distinct part number each, and installed as one mod.
/// </summary>
public static partial class OivParts
{
    private static readonly string[] Words =
        ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];

    [GeneratedRegex(@"(?<![\p{L}\d])(?:part|pt|teil|parte|partie|часть|ч)\.?\s*[-_#№.]?\s*(\d{1,3}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)(?![\p{L}\d])",
                    RegexOptions.IgnoreCase)]
    private static partial Regex PartRe();

    /// <summary>"1. X", "01 - X", "2) X".</summary>
    [GeneratedRegex(@"^\s*(\d{1,2})\s*[.)\-_]?\s+(?=\S)")] private static partial Regex LeadingNumberRe();

    /// <summary>"Liberty City Installer - Part ONE" → (1, "Liberty City Installer"); no number → null.</summary>
    public static (int Part, string Base)? Parse(string name)
    {
        int? part = null;
        var rest = name;
        if (PartRe().Match(rest) is { Success: true } m)
        {
            var n = m.Groups[1].Value;
            part = int.TryParse(n, out var d) ? d : Array.IndexOf(Words, n.ToLowerInvariant()) + 1;
            rest = rest.Remove(m.Index, m.Length);
        }
        if (LeadingNumberRe().Match(rest) is { Success: true } lead)
        {
            part ??= int.Parse(lead.Groups[1].Value);
            rest = rest[lead.Length..];
        }
        if (part is null or < 1) return null;
        var clean = Regex.Replace(rest, @"[\s_]+", " ").Trim(' ', '-', '–', '—', ':', '.', ',', '(', ')', '[', ']');
        return clean.Length == 0 ? null : (part.Value, clean);
    }

    /// <summary>
    /// The packages in part order when they are parts of one mod — by their own names, else by their file / folder
    /// names — with their part numbers and the mod's name; null when they are not.
    /// </summary>
    public static (List<int> Order, List<int> Numbers, string Name)? Find(IReadOnlyList<string> names, IReadOnlyList<string> fileNames)
    {
        if (names.Count < 2) return null;
        foreach (var labels in new[] { names, fileNames })
        {
            var parsed = labels.Select(Parse).ToList();
            if (parsed.Any(p => p is null)) continue;
            var bases = parsed.Select(p => Key(p!.Value.Base)).Distinct().Count();
            var parts = parsed.Select(p => p!.Value.Part).ToList();
            if (bases != 1 || parts.Distinct().Count() != parts.Count) continue;
            var order = Enumerable.Range(0, labels.Count).OrderBy(i => parts[i]).ToList();
            return (order, [.. order.Select(i => parts[i])], parsed[order[0]]!.Value.Base);
        }
        return null;
    }

    private static string Key(string s) => Regex.Replace(s.ToLowerInvariant(), @"[\s_\-–—.]+", " ").Trim();

    /// <summary>The parts as one package: the first one's looks, every part's steps in order.</summary>
    public static OivPackage Merge(IReadOnlyList<OivPackage> parts, string name, IReadOnlyList<int> numbers)
    {
        var first = parts[0];
        var pkg = new OivPackage
        {
            Name = name,
            Author = first.Author,
            Version = first.Version,
            Description = first.Description,
            Link = first.Link,
            Root = first.Root,
            FormatVersion = first.FormatVersion,
            IconPath = first.IconPath,
            HeaderColor = first.HeaderColor,
            BlackText = first.BlackText,
        };
        foreach (var p in parts)
        {
            pkg.PartNames.Add(p.Name);
            pkg.Steps.AddRange(p.Steps);
            pkg.Warnings.AddRange(p.Warnings.Select(w => $"{p.Name}: {w}"));
        }
        var gaps = Enumerable.Range(1, numbers.Max()).Except(numbers).ToList();
        if (gaps.Count > 0)
            pkg.Warnings.Insert(0, L.T($"Part(s) {string.Join(", ", gaps)} of «{name}» are not in the drop — the mod may need them. " +
                                       $"Drop all its parts together."));

        int adds = pkg.Steps.Count(s => s is OivAdd), edits = pkg.Steps.Count(s => s is OivXml or OivText);
        int deletes = pkg.Steps.Count(s => s is OivDelete);
        var what = new List<string>();
        if (adds > 0) what.Add(L.T($"{adds} file(s) to put in"));
        if (edits > 0) what.Add(L.T($"{edits} file(s) to edit"));
        if (deletes > 0) what.Add(L.T($"{deletes} to delete"));
        pkg.Parts.Add(pkg.Author is { } a ? L.T($"{parts.Count} OIV packages installed as one, by {a}")
                                          : L.T($"{parts.Count} OIV packages installed as one"));
        pkg.Parts.Add(string.Join(", ", what));
        return pkg;
    }
}

/// <summary>Reads an OIV package's <c>assembly.xml</c> (format 2.x) into an <see cref="OivPackage"/>.</summary>
public static partial class OivReader
{
    public const string AssemblyFile = "assembly.xml";

    /// <summary>Is this XML text an OIV package description?</summary>
    public static bool IsAssembly(string text) =>
        ModDetector.RootTag(text) is { } root && root.Equals("package", StringComparison.OrdinalIgnoreCase) &&
        text.Contains("<metadata", StringComparison.OrdinalIgnoreCase);

    /// <summary>Read the package whose assembly.xml is <paramref name="assemblyXml"/>. Throws <see cref="IntakeException"/> when it can't be used.</summary>
    public static OivPackage Read(string assemblyXml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(TextIo.DecodeUtf8Sig(File.ReadAllBytes(assemblyXml), strict: false));
        }
        catch (XmlException ex)
        {
            throw new IntakeException(L.T($"The OIV package's assembly.xml is not valid XML ({ex.Message})."), ex);
        }
        var root = doc.Root!;
        var dir = Path.GetDirectoryName(Path.GetFullPath(assemblyXml))!;
        var meta = El(root, "metadata");
        var format = Attr(root, "version") ?? "";
        var target = Attr(root, "target");
        if (target is not null && !target.Equals("Five", StringComparison.OrdinalIgnoreCase))
            throw new IntakeException(L.T($"This OIV package is for another game («{target}»), not GTA V."));

        var name = Text(El(meta, "name"));
        var author = El(meta, "author");
        var colors = El(root, "colors");
        var header = El(colors, "headerBackground");
        var icon = Path.Combine(dir, "icon.png");
        var pkg = new OivPackage
        {
            Name = name is { Length: > 0 } ? name : L.T("OIV package"),
            Author = Text(El(author, "displayName")),
            Version = VersionOf(El(meta, "version")),
            Description = Text(El(meta, "description")),
            Link = Text(El(author, "actionLink")) ?? Attr(El(meta, "description"), "footerLink"),
            Root = dir,
            FormatVersion = format,
            IconPath = File.Exists(icon) ? icon : null,
            HeaderColor = ColorOf(Text(header)),
            BlackText = Attr(header, "useBlackTextColor") is { } b && b.Equals("true", StringComparison.OrdinalIgnoreCase),
        };
        if (format.Length > 0 && !format.StartsWith('2'))
            pkg.Warnings.Add(L.T($"The package is in OIV format {format} — ModDrop V reads format 2; some steps may be skipped."));

        var content = El(root, "content") ?? throw new IntakeException(L.T("The OIV package has no <content> — nothing to install."));
        Walk(pkg, content, null);
        if (pkg.Steps.Count == 0) throw new IntakeException(L.T("The OIV package has no install instructions."));

        int adds = pkg.Steps.Count(s => s is OivAdd), edits = pkg.Steps.Count(s => s is OivXml or OivText);
        int deletes = pkg.Steps.Count(s => s is OivDelete);
        var parts = new List<string>();
        if (adds > 0) parts.Add(L.T($"{adds} file(s) to put in"));
        if (edits > 0) parts.Add(L.T($"{edits} file(s) to edit"));
        if (deletes > 0) parts.Add(L.T($"{deletes} to delete"));
        pkg.Parts.Add(L.T($"OIV package{(pkg.Version is { } v ? " v" + v : "")}{(pkg.Author is { } a ? " by " + a : "")}"));
        pkg.Parts.Add(string.Join(", ", parts));
        return pkg;
    }

    private static void Walk(OivPackage pkg, XElement container, string? archive)
    {
        foreach (var e in container.Elements())
        {
            switch (e.Name.LocalName.ToLowerInvariant())
            {
                case "add":
                {
                    var path = Target(pkg, e.Value, archive);
                    var src = Attr(e, "source");
                    if (path is null || src is null) break;
                    var rel = Norm(src);
                    var file = rel is null ? null : Path.GetFullPath(Path.Combine(pkg.Root, "content", rel));
                    if (file is null || !file.StartsWith(pkg.Root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                    {
                        pkg.Warnings.Add(L.T($"The package's file content/{src} is missing — {path} is skipped."));
                        break;
                    }
                    pkg.Steps.Add(new OivAdd(path, archive is not null, file));
                    break;
                }
                case "delete":
                    if (Target(pkg, e.Value, archive) is { } del) pkg.Steps.Add(new OivDelete(del, archive is not null));
                    break;
                case "archive":
                {
                    if (Target(pkg, Attr(e, "path") ?? "", archive) is not { } path) break;
                    if (Attr(e, "type") is { } type && !type.StartsWith("RPF", StringComparison.OrdinalIgnoreCase))
                    {
                        pkg.Warnings.Add(L.T($"{path}: archives of type {type} aren't supported — its steps are skipped."));
                        break;
                    }
                    if (!path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                    {
                        pkg.Warnings.Add(L.T($"{path} is not an .rpf archive — its steps are skipped."));
                        break;
                    }
                    pkg.Steps.Add(new OivArchive(path, archive is not null, IsTrue(Attr(e, "createIfNotExist"))));
                    Walk(pkg, e, path);
                    break;
                }
                case "xml":
                {
                    if (Target(pkg, Attr(e, "path") ?? "", archive) is not { } path) break;
                    var edits = new List<OivXmlEdit>();
                    foreach (var x in e.Elements())
                    {
                        var mode = x.Name.LocalName.ToLowerInvariant() switch
                        {
                            "add" => XmlPatchMode.Add,
                            "replace" => XmlPatchMode.Replace,
                            "remove" => XmlPatchMode.Remove,
                            _ => (XmlPatchMode?)null,
                        };
                        var xpath = Attr(x, "xpath");
                        if (mode is null || string.IsNullOrWhiteSpace(xpath))
                        {
                            pkg.Warnings.Add(L.T($"{path}: <{x.Name.LocalName}> isn't an XML edit ModDrop V knows — skipped."));
                            continue;
                        }
                        edits.Add(new OivXmlEdit(mode.Value, xpath.Trim(),
                                                 [.. x.Elements().Select(c => c.ToString(SaveOptions.DisableFormatting))],
                                                 Attr(x, "append") is { } ap && ap.Equals("First", StringComparison.OrdinalIgnoreCase)));
                    }
                    if (edits.Count > 0) pkg.Steps.Add(new OivXml(path, archive is not null, edits));
                    break;
                }
                case "text":
                {
                    if (Target(pkg, Attr(e, "path") ?? "", archive) is not { } path) break;
                    var edits = new List<OivTextEdit>();
                    foreach (var x in e.Elements())
                    {
                        var line = Attr(x, "line");
                        switch (x.Name.LocalName.ToLowerInvariant())
                        {
                            case "add":
                                edits.Add(new OivTextEdit(OivTextMode.Add, null, x.Value));
                                break;
                            case "insert" when line is not null:
                                bool before = Attr(x, "where") is { } w && w.Equals("Before", StringComparison.OrdinalIgnoreCase);
                                edits.Add(new OivTextEdit(before ? OivTextMode.InsertBefore : OivTextMode.InsertAfter, line, x.Value));
                                break;
                            case "replace" when line is not null:
                                edits.Add(new OivTextEdit(OivTextMode.Replace, line, x.Value));
                                break;
                            case "delete":
                                edits.Add(new OivTextEdit(OivTextMode.Delete, line ?? x.Value, ""));
                                break;
                            default:
                                pkg.Warnings.Add(L.T($"{path}: <{x.Name.LocalName}> isn't a text edit ModDrop V knows — skipped."));
                                break;
                        }
                    }
                    if (edits.Count > 0) pkg.Steps.Add(new OivText(path, archive is not null, IsTrue(Attr(e, "createIfNotExist")), edits));
                    break;
                }
                default:
                    pkg.Warnings.Add(L.T($"The package's <{e.Name.LocalName}> instruction isn't supported — skipped."));
                    break;
            }
        }
    }

    /// <summary>A step's path: inside the archive, or game-relative; null (with a warning) when it points outside the game.</summary>
    private static string? Target(OivPackage pkg, string raw, string? archive)
    {
        var rel = Norm(raw);
        if (rel is null)
        {
            if (raw.Trim().Length > 0) pkg.Warnings.Add(L.T($"«{raw.Trim()}» points outside the game folder — skipped."));
            return null;
        }
        return archive is null ? rel : $"{archive}/{rel}";
    }

    /// <summary>
    /// '/'-separated relative path without "." segments; null for an empty, escaping or drive path. A leading '\' is
    /// the root of where the step is (the archive, else the game folder) — OpenIV writes paths that way.
    /// </summary>
    internal static string? Norm(string raw)
    {
        var p = raw.Trim().Replace('\\', '/').TrimStart('/');
        if (p.Length == 0 || p.Contains(':')) return null;
        var segs = p.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToList();
        if (segs.Count == 0 || segs.Any(s => s == "..")) return null;
        return string.Join('/', segs);
    }

    private static XElement? El(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Attr(XElement? e, string name) =>
        e?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? Text(XElement? e) => e?.Value.Trim() is { Length: > 0 } t ? t : null;

    private static bool IsTrue(string? v) => v is not null && v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>"1.2 Beta" from &lt;version&gt;&lt;major&gt;1&lt;/major&gt;&lt;minor&gt;2&lt;/minor&gt;&lt;tag&gt;Beta&lt;/tag&gt;&lt;/version&gt;.</summary>
    private static string? VersionOf(XElement? v)
    {
        if (v is null) return null;
        var major = Text(El(v, "major"));
        if (major is null) return Text(v);
        var s = major + (Text(El(v, "minor")) is { } minor ? "." + minor : "");
        return Text(El(v, "tag")) is { } tag ? $"{s} {tag}" : s;
    }

    [GeneratedRegex(@"^[$#]?([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")] private static partial Regex ColorRe();

    /// <summary>OpenIV's <c>$AARRGGBB</c> → <c>#AARRGGBB</c>.</summary>
    private static string? ColorOf(string? c) => c is not null && ColorRe().Match(c.Trim()) is { Success: true } m ? "#" + m.Groups[1].Value : null;
}

/// <summary>The XML and text edits of OIV packages, on file content.</summary>
public static class OivEdits
{
    /// <summary>Apply XML edits; an XPath that matches nothing is reported and skipped.</summary>
    public static byte[] ApplyXml(byte[] content, IEnumerable<OivXmlEdit> edits, Action<string> log, string what)
    {
        bool bom = HasBom(content);
        var doc = XDocument.Parse(TextIo.DecodeUtf8Sig(content, strict: false), LoadOptions.PreserveWhitespace);
        foreach (var edit in edits)
        {
            List<XElement> hits;
            try
            {
                hits = doc.XPathSelectElements(edit.XPath).ToList();
            }
            catch (XPathException ex)
            {
                log(L.T($"    [!] {what}: the XPath {edit.XPath} is not valid ({ex.Message}) — skipped."));
                continue;
            }
            if (hits.Count == 0)
            {
                log(L.T($"    [!] {what}: nothing matches {edit.XPath} — that edit is skipped."));
                continue;
            }
            foreach (var e in hits)
            {
                var items = edit.Content.Select(c => XElement.Parse(c)).ToList();
                switch (edit.Mode)
                {
                    case XmlPatchMode.Add when edit.First: e.AddFirst(items); break;
                    case XmlPatchMode.Add: e.Add(items); break;
                    case XmlPatchMode.Replace: e.ReplaceWith(items); break;
                    default: e.Remove(); break;
                }
            }
        }
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings
               {
                   OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.None, ConformanceLevel = ConformanceLevel.Auto,
               }))
        {
            foreach (var n in doc.Nodes()) n.WriteTo(w);
        }
        var body = sb.ToString();
        var decl = doc.Declaration is null ? "" : doc.Declaration + (body.Length > 0 && char.IsWhiteSpace(body[0]) ? "" : "\n");
        return Encode(decl + body, bom);
    }

    /// <summary>Apply line edits (lines are matched ignoring the whitespace around them and case).</summary>
    public static byte[] ApplyText(byte[]? content, IEnumerable<OivTextEdit> edits, Action<string> log, string what)
    {
        bool bom = content is not null && HasBom(content);
        var text = content is null ? "" : TextIo.DecodeUtf8Sig(content, strict: false);
        var nl = text.Contains("\r\n", StringComparison.Ordinal) || content is null ? "\r\n" : "\n";
        var lines = text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        bool Is(string line, string? find) => find is not null && line.Trim().Equals(find.Trim(), StringComparison.OrdinalIgnoreCase);
        foreach (var e in edits)
        {
            var added = e.Text.Replace("\r\n", "\n").Split('\n');
            switch (e.Mode)
            {
                case OivTextMode.Add:
                    lines.AddRange(added);
                    break;
                case OivTextMode.InsertBefore or OivTextMode.InsertAfter:
                    int at = lines.FindIndex(l => Is(l, e.Line));
                    if (at < 0)
                    {
                        log(L.T($"    [!] {what}: the line «{e.Line}» is not there — the text is added at the end."));
                        lines.AddRange(added);
                    }
                    else lines.InsertRange(e.Mode == OivTextMode.InsertBefore ? at : at + 1, added);
                    break;
                case OivTextMode.Replace:
                    int n = 0;
                    for (int i = lines.Count - 1; i >= 0; i--)
                    {
                        if (!Is(lines[i], e.Line)) continue;
                        lines.RemoveAt(i);
                        lines.InsertRange(i, added);
                        n++;
                    }
                    if (n == 0) log(L.T($"    [!] {what}: the line «{e.Line}» is not there — nothing replaced."));
                    break;
                case OivTextMode.Delete:
                    if (lines.RemoveAll(l => Is(l, e.Line)) == 0) log(L.T($"    [!] {what}: the line «{e.Line}» is not there — nothing deleted."));
                    break;
            }
        }
        return Encode(string.Join(nl, lines) + nl, bom);
    }

    /// <summary>The <c>dlcpacks:/name/</c> packs a dlclist.xml text lists, in order.</summary>
    public static List<string> DlcPacks(string xml) =>
        [.. Regex.Matches(xml, @"<Item>\s*dlcpacks:[\\/]+([^<\\/]+?)[\\/]*\s*</Item>", RegexOptions.IgnoreCase)
                 .Select(m => m.Groups[1].Value.Trim())];

    private static bool HasBom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;

    private static byte[] Encode(string text, bool bom)
    {
        var body = Encoding.UTF8.GetBytes(text);
        return bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }
}

/// <summary>
/// OIV packages: every instruction becomes a plan step that works through the mods folder — files
/// inside game archives go into copies of the archives under mods (the mods layer), new archives
/// are built and put there, new dlcpacks are listed in dlclist.xml the same way add-on weapons are,
/// loose files (.asi, scripts, .ini) go into the game folder and are taken back on removal.
/// The game's own archives are never touched.
/// </summary>
public sealed class OivHandler : FileModHandler
{
    public const string Prefix = "oiv:";

    public override ModCategory Category => ModCategory.Package;
    protected override string IdPrefix => Prefix;

    public const string DlclistPath = "update/update.rpf/" + GameInstaller.DlclistInner;

    /// <summary>
    /// The OIV package of the drop (its assembly.xml), or null. Several packages that are parts of one mod
    /// (<see cref="OivParts"/>) make one package; otherwise the first is installed.
    /// </summary>
    public override ModPackage? Analyze(DroppedSource source, DetectionReport report, HandlerEnv env)
    {
        List<DroppedFile> assemblies = !report.Has(ModCategory.Package) ? [] : source.Files
            .Where(f => f.Name.Equals(OivReader.AssemblyFile, StringComparison.OrdinalIgnoreCase) &&
                        new FileInfo(f.FullPath).Length < 16 << 20 &&
                        OivReader.IsAssembly(TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.FullPath), strict: false)))
            .OrderBy(f => f.Depth).ThenBy(f => f.Origin, PathUtil.PathOrder).ToList();
        if (assemblies.Count == 0) return PackSet(source, env);
        var pkg = assemblies.Count > 1 ? ReadParts(assemblies) : null;
        if (pkg is null)
        {
            pkg = OivReader.Read(assemblies[0].FullPath);
            if (assemblies.Count > 1)
                pkg.Warnings.Add(L.T($"The drop holds {assemblies.Count} OIV packages — «{pkg.Name}» ({assemblies[0].Origin}) is the one installed; " +
                                 $"drop the others one at a time."));
        }
        AddBesides(source, assemblies.Select(a => Path.GetDirectoryName(a.FullPath)!), pkg, env);
        pkg.Source = source.Sources.Count == 1 ? ModSource.Of(source.Sources[0]) : null;
        return pkg;
    }

    /// <summary>Finished packs laid out like the game folder, with no assembly.xml (<see cref="DlcPackSet"/>), or null.</summary>
    private static OivPackage? PackSet(DroppedSource source, HandlerEnv env)
    {
        if (DlcPackSet.Read(source) is not { } pkg) return null;
        var packDirs = pkg.Steps.OfType<OivAdd>().Where(a => !a.InArchive).Select(a => Path.GetDirectoryName(a.Source)!);
        AddBesides(source, packDirs, pkg, env);
        pkg.Source = source.Sources.Count == 1 ? ModSource.Of(source.Sources[0]) : null;
        return pkg;
    }

    /// <summary>
    /// Plugins and scripts the drop holds next to the package, which it doesn't install itself ("then put
    /// SourceMotionCore.asi into the game folder"): they join the package's steps, so they go in and out with it.
    /// Copies of the package's files laid out by archive ("Manual Install/common.rpf/…") stay out.
    /// </summary>
    private static void AddBesides(DroppedSource source, IEnumerable<string> packageDirs, OivPackage pkg, HandlerEnv env)
    {
        var roots = packageDirs.Select(d => d + Path.DirectorySeparatorChar).ToList();
        var rest = new DroppedSource { WorkDir = source.WorkDir };
        rest.Sources.AddRange(source.Sources);
        rest.Files.AddRange(source.Files.Where(f =>
            !roots.Any(r => f.FullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase)) &&
            !f.Origin.Split('/').SkipLast(1).Any(d => d.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))));
        var report = ModDetector.Detect(rest);
        if (!report.Has(ModCategory.Script)) return;
        ScriptPackage? scripts;
        try
        {
            scripts = new ScriptHandler().Analyze(rest, report, env) as ScriptPackage;
        }
        catch (IntakeException)
        {
            return;                                                               // nothing of use there (a 32-bit plugin…)
        }
        if (scripts is null) return;
        var own = pkg.Steps.OfType<OivAdd>().Where(a => !a.InArchive).Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var f in scripts.Files.Where(f => !f.Shared && own.Add(f.Dest)))
        {
            pkg.Steps.Add(new OivAdd(f.Dest, false, f.Source));
            added.Add(f.Dest);
        }
        pkg.Warnings.AddRange(scripts.Warnings);
        if (added.Count > 0)
            pkg.Parts.Add(L.T($"Next to the package: {string.Join(", ", added.Take(4))}{(added.Count > 4 ? ", …" : "")} — installed with it"));
    }

    /// <summary>The drop's packages as one, when they are parts of one mod; else null.</summary>
    private static OivPackage? ReadParts(List<DroppedFile> assemblies)
    {
        var read = new List<(OivPackage Pkg, DroppedFile File)>();
        foreach (var a in assemblies)
        {
            try
            {
                read.Add((OivReader.Read(a.FullPath), a));
            }
            catch (IntakeException)
            {
                return null;                                   // an unreadable one: not a set to install as a whole
            }
        }
        // the package's own file / folder: ".../X - Part ONE.oiv/assembly.xml" → "X - Part ONE"
        static string FileName(DroppedFile f)
        {
            var segs = f.Origin.Split('/');
            var own = segs.Length >= 2 ? segs[^2] : segs[0];
            return Regex.Replace(own, @"\.(oiv|zip|rar|7z)$", "", RegexOptions.IgnoreCase);
        }
        if (OivParts.Find(read.Select(r => r.Pkg.Name).ToList(), read.Select(r => FileName(r.File)).ToList()) is not { } set) return null;
        var ordered = set.Order.Select(i => read[i].Pkg).ToList();
        return OivParts.Merge(ordered, set.Name, set.Numbers);
    }

    public override InstallPlan PlanInstall(ModPackage package, InstallTarget target)
    {
        var pkg = (OivPackage)package;
        var id = IdFor(pkg.Name);
        var plan = BeginInstall(id, pkg.Name, target);
        var overlay = ModsOverlay.Load(target.GameDir);

        // archives the package names: there, to be made, or missing (their steps are skipped)
        var made = new List<string>();
        var missing = new List<string>();
        var old = ModRegistry.Load(target.GameDir).Find(id);                 // reinstalling: what the old version made goes first
        foreach (var a in pkg.Steps.OfType<OivArchive>())
        {
            if (made.Concat(missing).Any(p => Under(a.Path, p))) continue;
            var placed = a.InArchive ? null : ModsLayout.ArchiveRel(target.GameDir, a.Path);
            bool exists = a.InArchive ? overlay.Exists(a.Path) && !MadeBy(overlay, id, a.Path)
                : (placed is not null && File.Exists(Path.Combine(target.GameDir, placed)) &&
                   !(old?.Journal.Any(s => s is CreatedFile f && f.Path.Equals(placed, StringComparison.OrdinalIgnoreCase)) ?? false))
                  || File.Exists(Path.Combine(target.GameDir, a.Path));
            if (exists) continue;
            if (a.Create) made.Add(a.Path);
            else
            {
                missing.Add(a.Path);
                plan.Warnings.Add(L.T($"{a.Path} is not in this game, and the package doesn't create it — its steps are skipped."));
            }
        }

        var putPaths = new List<string>();
        var adjusters = new List<string>();
        var swapped = new List<string>();
        int files = 0;
        var ownPacks = pkg.Steps.OfType<OivAdd>().Where(a => !a.InArchive && a.Path.EndsWith("/dlc.rpf", StringComparison.OrdinalIgnoreCase))
                                .Select(a => a.Path[..^"/dlc.rpf".Length]).ToList();
        GameIndex? index = null;
        foreach (var step in pkg.Steps)
        {
            if (step is OivLocate locate)
            {
                index ??= GameIndex.Open(target.GameDir, target.IndexCacheRoot);
                var (at, why) = DlcPackSet.Locate(index, locate, ownPacks);
                if (at is null)
                {
                    plan.Warnings.Add(why!);
                    continue;
                }
                plan.Add(new RpfPutOp(at, locate.Source, id));
                putPaths.Add(at);
                files++;
                continue;
            }
            if (missing.Any(m => Under(step.Path, m) || step.Path.Equals(m, StringComparison.OrdinalIgnoreCase))) continue;
            if (made.FirstOrDefault(m => Under(step.Path, m)) is not null) continue;        // built with its archive
            if (step is OivArchive arc)
            {
                if (made.Contains(arc.Path))
                {
                    var placed = arc.InArchive ? null : ModsLayout.ArchiveRel(target.GameDir, arc.Path);
                    if (!arc.InArchive && placed is null)
                    {
                        plan.Warnings.Add(L.T($"The package creates {arc.Path} — Onigiri can't load a new archive there; skipped."));
                        continue;
                    }
                    plan.Add(new BuildArchiveOp(arc.Path, arc.InArchive, pkg.Steps.Where(s => Under(s.Path, arc.Path)).ToList(),
                                                pkg.Root, id, target.Edition) { Placed = placed });
                    if (arc.InArchive) putPaths.Add(arc.Path);
                    files++;
                }
                continue;
            }
            files++;
            if (step.InArchive) AddArchiveStep(plan, step, id, overlay, putPaths);
            else AddLooseStep(plan, step, target, adjusters, swapped);
        }

        if (adjusters.Count > 0)
            plan.Warnings.Add(L.T($"{string.Join(", ", adjusters)}: limit adjusters for GTA V Legacy — ModDrop V puts its own GTA V Enhanced ones instead."));
        if (swapped.Count > 0)
            plan.Warnings.Add(L.T($"The package’s World Travel plugins are made for GTA V Legacy — ModDrop V puts its GTA V Enhanced builds instead ({string.Join(", ", swapped)})."));
        if (target.Edition == GameEdition.Enhanced && LegacyDataReplaced(pkg, overlay, adjusters.Count > 0) is { Count: > 0 } data)
            plan.Warnings.Add(L.T($"This package looks made for GTA V Legacy and replaces {data.Count} of the game’s own data files as a whole ({string.Join(", ", data.Take(5))}{(data.Count > 5 ? ", …" : "")}). GTA V Enhanced has its own versions of such files — with Legacy’s it may not start. If it doesn’t, remove the package."));
        if (pkg.FromLayout && ownPacks.Where(p => File.Exists(Path.Combine(target.GameDir, p, "dlc.rpf"))).ToList() is { Count: > 0 } theirs)
            plan.Warnings.Add(L.T($"The game has {theirs.Count} of these packs itself ({string.Join(", ", theirs.Take(4).Select(Path.GetFileName))}{(theirs.Count > 4 ? ", …" : "")}) — the mod’s copies take their place while it is installed."));
        WorldTravel.AddSteps(plan, pkg, target);
        plan.Warnings.AddRange(ConflictWarnings(id, target, putPaths));
        plan.Add(Register(id, ModCategory.Package, pkg, target,
                          pkg.FromLayout ? L.T($"DLC packs · {files} change(s)") : L.T($"OIV · {files} change(s)"),
                          pkg.Link is { } link ? new() { ["link"] = link } : null));
        return plan;
    }

    /// <summary>An archive inside another that only this mod put there (it goes when the mod does).</summary>
    private static bool MadeBy(ModsOverlay overlay, string id, string path) =>
        overlay.State.Entries.TryGetValue(overlay.KeyFor(path), out var e) && e.Base == ModsOverlay.Absent &&
        e.Layers.All(l => l.Mod == id);

    private static bool Under(string path, string archive) =>
        path.Length > archive.Length + 1 && path.StartsWith(archive + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsDlclist(string path) => path.Equals(DlclistPath, StringComparison.OrdinalIgnoreCase);

    private static void AddArchiveStep(InstallPlan plan, OivStep step, string id, ModsOverlay overlay, List<string> putPaths)
    {
        switch (step)
        {
            case OivAdd add when IsDlclist(add.Path):
            {
                // a whole dlclist.xml would drop every pack other mods listed — only its new packs are added
                var theirs = OivEdits.DlcPacks(TextIo.DecodeUtf8Sig(File.ReadAllBytes(add.Source), strict: false));
                var current = CurrentPacks(overlay);
                var added = theirs.Where(p => !current.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var p in added) plan.Add(new DlclistAddOp(p));
                plan.Warnings.Add(added.Count > 0
                    ? L.T($"The package replaces the whole dlclist.xml — ModDrop V adds just its new packs ({string.Join(", ", added)}), so the packs other mods listed stay.")
                    : L.T("The package replaces the whole dlclist.xml — ModDrop V adds just its new packs (none — every pack in it is listed already), so the packs other mods listed stay."));
                break;
            }
            case OivXml xml when IsDlclist(xml.Path) && DlclistEdits(xml) is { } lines:
                foreach (var (pack, add) in lines) plan.Add(add ? new DlclistAddOp(pack) : new DlclistRemoveOp(pack));
                break;
            case OivAdd add:
                plan.Add(new RpfPutOp(add.Path, add.Source, id));
                putPaths.Add(add.Path);
                break;
            case OivDelete del:
                plan.Add(new RpfDeleteOp(del.Path, id));
                putPaths.Add(del.Path);
                break;
            case OivXml xml:
                plan.Add(new RpfEditOp(xml.Path, id, L.T($"Edit {xml.Path} ({xml.Edits.Count} XML change(s))"), (content, log) =>
                {
                    if (content is null)
                    {
                        log(L.T($"    [!] {xml.Path} is not there — its XML edits are skipped."));
                        return null;
                    }
                    return OivEdits.ApplyXml(content, xml.Edits, log, xml.Path);
                }));
                putPaths.Add(xml.Path);
                if (IsDlclist(xml.Path))
                    plan.Warnings.Add(L.T("The package edits dlclist.xml in its own way — ModDrop V makes the edit on the current list and keeps only the packs it adds or removes, so the packs other mods listed stay."));
                break;
            case OivText text:
                plan.Add(new RpfEditOp(text.Path, id, L.T($"Edit {text.Path} ({text.Edits.Count} line change(s))"), (content, log) =>
                {
                    if (content is null && !text.Create)
                    {
                        log(L.T($"    [!] {text.Path} is not there — its line edits are skipped."));
                        return null;
                    }
                    return OivEdits.ApplyText(content, text.Edits, log, text.Path);
                }));
                putPaths.Add(text.Path);
                break;
        }
    }

    /// <param name="adjusters">Legacy limit adjusters left out (one warning names them all)</param>
    /// <param name="swapped">plugins put in as ModDrop V's own GTA V Enhanced builds</param>
    private static void AddLooseStep(InstallPlan plan, OivStep step, InstallTarget target, List<string> adjusters, List<string> swapped)
    {
        // archives the game reads go into mods (a whole dlc.rpf, a replaced .rpf) — or onigiri; plugins, scripts and their
        // files into the game folder
        string? Place(string path) =>
            path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
                ? ModsLayout.ArchiveRel(target.GameDir, path) : path;
        bool enhanced = target.Edition == GameEdition.Enhanced;
        switch (step)
        {
            case OivAdd add when enhanced && LegacyLimitAdjuster(add):
                adjusters.Add(add.Path);
                break;
            case OivAdd add when enhanced && EnhancedBuild(add, target) is { } ours:
                plan.Add(new CopyFileOp(ours.Source, ours.Name));
                swapped.Add(ours.Name);
                break;
            case OivAdd add:
                if (Place(add.Path) is not { } to)
                    plan.Warnings.Add(L.T($"The package replaces the game's {add.Path} as a whole — Onigiri can't load that; skipped."));
                else if (enhanced && to.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) plan.Add(new ConvertArchiveOp(add.Source, to));
                else
                {
                    if (enhanced && IsLegacyPatcher(add))
                        plan.Warnings.Add(L.T($"{add.Path} patches the memory of GTA V Legacy and may not work in GTA V Enhanced — if the game misbehaves, remove it first."));
                    plan.Add(new CopyFileOp(add.Source, to));
                }
                break;
            case OivDelete del:
                var at = Place(del.Path);
                if (at is null || (at != del.Path && !File.Exists(Path.Combine(target.GameDir, at))))
                    plan.Warnings.Add(L.T($"The package deletes the game's {del.Path} — ModDrop V never deletes the game's own archives; skipped."));
                else plan.Add(new DeleteFileOp(at));
                break;
            case OivXml xml:
                plan.Add(new FileEditOp(xml.Path, L.T($"Edit <game>/{xml.Path} ({xml.Edits.Count} XML change(s))"), (content, log) =>
                {
                    if (content is null)
                    {
                        log(L.T($"    [!] <game>/{xml.Path} is not there — its XML edits are skipped."));
                        return null;
                    }
                    return OivEdits.ApplyXml(content, xml.Edits, log, xml.Path);
                }));
                break;
            case OivText text:
                plan.Add(new FileEditOp(text.Path, L.T($"Edit <game>/{text.Path} ({text.Edits.Count} line change(s))"), (content, log) =>
                {
                    if (content is null && !text.Create)
                    {
                        log(L.T($"    [!] <game>/{text.Path} is not there — its line edits are skipped."));
                        return null;
                    }
                    return OivEdits.ApplyText(content, text.Edits, log, text.Path);
                }));
                break;
        }
    }

    

    // data files the two editions each have their own version of (Liberty City Preservation Project put Legacy's
    // content.xml, dlc_patch/mpheist4/content.xml — without Enhanced's 76 *_bvh.rpf entries —, sounds.dat54.rel… into
    // Enhanced, and it crashed while loading); models and textures are converted, so they don't count
    private static readonly HashSet<string> DataExts = new(StringComparer.OrdinalIgnoreCase)
        { ".xml", ".meta", ".dat", ".rel", ".ymt", ".ipl", ".nametable", ".gfx" };

    /// <summary>
    /// The game's own data files a package made for Legacy replaces as a whole (their file names); empty when it doesn't
    /// look made for Legacy — no Legacy limit adjuster (<paramref name="legacyAdjusters"/>), memory patcher or model.
    /// </summary>
    private static List<string> LegacyDataReplaced(OivPackage pkg, ModsOverlay overlay, bool legacyAdjusters)
    {
        var adds = pkg.Steps.OfType<OivAdd>().ToList();
        bool legacy = legacyAdjusters || adds.Any(a => !a.InArchive && IsLegacyPatcher(a)) || adds.Any(a => a.InArchive && IsLegacyModel(a.Source));
        if (!legacy) return [];
        var names = new List<string>();
        foreach (var a in adds.Where(a => a.InArchive && DataExts.Contains(Path.GetExtension(a.Path)) && !IsDlclist(a.Path)))
        {
            bool gameHasIt;
            try
            {
                gameHasIt = overlay.ReadOriginal(a.Path) is not null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or RpfFormatException or RpfEncryptedException or ArgumentException)
            {
                gameHasIt = false;
            }
            if (gameHasIt) names.Add(Path.GetFileName(a.Path));
        }
        return names;
    }

    private static bool IsLegacyModel(string source)
    {
        try
        {
            using var fs = File.OpenRead(source);
            Span<byte> head = stackalloc byte[8];
            return fs.Read(head) == 8 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Rpf7.Rsc7Magic
                   && ResourceEditions.EditionOf(Path.GetExtension(source), ResourceEditions.Version(head)) == GameEdition.Legacy;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // World Travel (Liberty City Preservation Project's level switcher, GPL-3, github.com/Splatcrafter/worldTravelASI):
    // the package's WorldTravel.asi hooks Legacy code by signature and crashes Enhanced at start, WorldTravelPatches.asi
    // writes Legacy addresses into Enhanced's memory. ModDrop V ships builds for Enhanced (data/plugins/worldtravel-enhanced):
    // the fork's WorldTravel.asi (skips the hooks there, calms Liberty City's ocean) and its own port of the patches.
    private static readonly (string Theirs, string Ours)[] EnhancedBuilds =
    [
        ("WorldTravel.asi", "WorldTravel.asi"),
        ("WorldTravelPatches.asi", "WorldTravelPatchesEnhanced.asi"),
        ("WorldTravelPatches.ini", "WorldTravelPatchesEnhanced.ini"),
    ];

    /// <summary>ModDrop V's GTA V Enhanced build of a plugin the package puts into the game folder, or null.</summary>
    private static (string Source, string Name)? EnhancedBuild(OivAdd add, InstallTarget target)
    {
        if (add.Path.Contains('/')) return null;
        foreach (var (theirs, ours) in EnhancedBuilds)
        {
            if (!add.Path.Equals(theirs, StringComparison.OrdinalIgnoreCase)) continue;
            var src = Path.Combine(GamePools.PluginsDir(target), "worldtravel-enhanced", ours);
            return File.Exists(src) ? (src, ours) : null;
        }
        return null;
    }

    // limit adjusters packages bring for Legacy (Liberty City Preservation Project: HeapAdjuster, PackfileLimitAdjuster,
    // WeaponLimitsAdjuster); in Enhanced ModDrop V's own ones (LimitAdjusters) do their job
    private static readonly string[] LimitAdjusterStems = ["HeapAdjuster", "PackfileLimitAdjuster", "WeaponLimitsAdjuster"];

    /// <summary>A Legacy limit adjuster in the game folder, or its .ini (the .asi decides: one made for Enhanced stays).</summary>
    private static bool LegacyLimitAdjuster(OivAdd add)
    {
        if (add.Path.Contains('/')) return false;
        var stem = Path.GetFileNameWithoutExtension(add.Path);
        if (!LimitAdjusterStems.Any(s => stem.StartsWith(s, StringComparison.OrdinalIgnoreCase))) return false;
        var asi = Path.ChangeExtension(add.Source, ".asi");
        return Path.GetExtension(add.Path).ToLowerInvariant() is ".asi" or ".ini"
               && !(File.Exists(asi) && LimitAdjusters.ForEnhanced(asi));
    }

    /// <summary>
    /// An .asi in the game folder that patches the game's memory itself — no ScriptHookV, no word of the Enhanced
    /// executable (WorldTravelPatches.asi); a ScriptHookV script (WorldTravel.asi) works in either edition.
    /// </summary>
    private static bool IsLegacyPatcher(OivAdd add)
    {
        if (add.Path.Contains('/') || !add.Path.EndsWith(".asi", StringComparison.OrdinalIgnoreCase) || !File.Exists(add.Source)) return false;
        if (LimitAdjusters.ForEnhanced(add.Source)) return false;
        return File.ReadAllBytes(add.Source).AsSpan().IndexOf("ScriptHookV"u8) < 0;
    }

    private static HashSet<string> CurrentPacks(ModsOverlay overlay)
    {
        byte[]? xml = null;
        try
        {
            xml = overlay.Read(DlclistPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or RpfEncryptedException)
        {
        }
        return xml is null ? [] : OivEdits.DlcPacks(TextIo.DecodeUtf8Sig(xml, strict: false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// dlclist.xml edits that only add &lt;Item&gt;dlcpacks:/x/&lt;/Item&gt; lines or remove them by an XPath naming
    /// the pack — as (pack, add) — or null when the edit is anything else.
    /// </summary>
    internal static List<(string Pack, bool Add)>? DlclistEdits(OivXml xml)
    {
        var list = new List<(string, bool)>();
        foreach (var e in xml.Edits)
        {
            if (e.Mode == XmlPatchMode.Add)
            {
                foreach (var c in e.Content)
                {
                    var packs = OivEdits.DlcPacks(c);
                    if (packs.Count != 1) return null;
                    list.Add((packs[0], true));
                }
            }
            else if (e.Mode == XmlPatchMode.Remove &&
                     Regex.Match(e.XPath, @"dlcpacks:[\\/]+([^'""\\/\]]+)", RegexOptions.IgnoreCase) is { Success: true } m)
                list.Add((m.Groups[1].Value, false));
            else return null;
        }
        return list;
    }
}

/// <summary>
/// An archive an OIV package creates (<c>createIfNotExist</c> and not in the game): its files are
/// laid out in a folder, edited, packed (archives inside it first) — then put into mods (a
/// top-level archive, e.g. a new dlcpack) or into its parent archive through the mods layer.
/// </summary>
public sealed class BuildArchiveOp(string path, bool inArchive, IReadOnlyList<OivStep> steps, string root, string modId, GameEdition edition)
    : PlanOp
{
    /// <summary>The archive it creates (a game path when <see cref="InArchive"/>, else under mods).</summary>
    public string ArchivePath => path;
    public bool InArchive => inArchive;
    /// <summary>A top-level archive's place, relative to the game (<c>mods/…</c>, <c>onigiri/…</c> — <see cref="ModsLayout.ArchiveRel"/>).</summary>
    public string? Placed { get; init; }

    public override string Describe() =>
        inArchive ? L.T($"Create {path} ({steps.Count(s => s is OivAdd)} file(s)) inside its archive (the game's own stays untouched)")
                  : L.T($"Create {Placed ?? "mods/" + path} ({steps.Count(s => s is OivAdd)} file(s))");

    public override void Execute(InstallContext ctx)
    {
        var work = Path.Combine(root, ".mdv-build", Guid.NewGuid().ToString("N")[..8]);
        var stage = Path.Combine(work, "stage");
        Directory.CreateDirectory(stage);
        try
        {
            string Local(string p) => Path.Combine(stage, p[(path.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));
            foreach (var s in steps)
            {
                var local = Local(s.Path);
                switch (s)
                {
                    case OivArchive:
                        Directory.CreateDirectory(local);
                        break;
                    case OivAdd add:
                        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                        File.Copy(add.Source, local, overwrite: true);
                        break;
                    case OivDelete:
                        if (File.Exists(local)) File.Delete(local);
                        break;
                    case OivXml xml when File.Exists(local):
                        File.WriteAllBytes(local, OivEdits.ApplyXml(File.ReadAllBytes(local), xml.Edits, ctx.Log, xml.Path));
                        break;
                    case OivText text when File.Exists(local) || text.Create:
                        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                        File.WriteAllBytes(local, OivEdits.ApplyText(File.Exists(local) ? File.ReadAllBytes(local) : null,
                                                                     text.Edits, ctx.Log, text.Path));
                        break;
                }
            }
            // archives inside it, innermost first, become files
            foreach (var dir in Directory.EnumerateDirectories(stage, "*.rpf", SearchOption.AllDirectories)
                                         .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)).ToList())
            {
                var packed = dir + ".packed";
                RpfPacker.PackFolder(dir, packed, edition);
                PathUtil.DeleteDir(dir);
                File.Move(packed, dir);
            }
            var output = Path.Combine(work, Path.GetFileName(path));
            RpfPacker.PackFolder(stage, output, edition);
            if (inArchive)
            {
                ctx.Overlay.Put(modId, path, File.ReadAllBytes(output));
                ctx.Log(L.T($"    created {path}"));
            }
            else new CopyFileOp(output, Placed ?? ModsLayout.ArchiveRel(ctx.GameDir, path)
                                        ?? throw new NotSupportedException(L.T($"The package creates {path} — Onigiri can't load a new archive there; skipped."))).Execute(ctx);
        }
        finally
        {
            PathUtil.TryDeleteDir(work);
        }
    }
}

/// <summary>
/// A whole archive an OIV package puts into GTA V Enhanced (a dlcpack's dlc.rpf): its Legacy models converted to the
/// gen9 format on the way (the game can't load them otherwise), everything else byte for byte; one with none is copied.
/// </summary>
public sealed class ConvertArchiveOp(string source, string gameRel) : CopyFileOp(source, gameRel)
{
    public override string Describe() =>
        L.T($"Copy {Path.GetFileName(Source)} to <game>/{GameRel}, its Legacy models converted to the GTA V Enhanced (gen9) format");

    public override void Execute(InstallContext ctx)
    {
        var n = RpfRetarget.Mismatched(Source, GameEdition.Enhanced).Count;
        if (n == 0)
        {
            base.Execute(ctx);
            return;
        }
        var dst = ctx.Abs(GameRel);
        var dir = Path.GetDirectoryName(dst)!;
        EnsureDir(ctx, dir);
        // built next to its place: the nested archives of a map pack are gigabytes, more than the temp drive may hold
        var tmp = dst + ".mdvconv";
        var name = Path.GetFileName(Source);
        ctx.Log(L.T($"    Converting {n} Legacy model(s) in {name} to the GTA V Enhanced (gen9) format…"));
        var clock = Stopwatch.StartNew();
        long shownAt = -1000;
        int logged = 0;
        var gate = new object();
        void Converted(int done)
        {
            lock (gate)
            {
                if (clock.ElapsedMilliseconds - shownAt < 500 && done < n) return;
                shownAt = clock.ElapsedMilliseconds;
                var left = Left(clock.Elapsed, done, n);
                ctx.StepProgress((double)done / n, left is null ? L.T($"{name}: {done} of {n} models converted")
                                                                : L.T($"{name}: {done} of {n} models converted · {left}"));
                // the log (and the command line) hears of it every tenth
                if (done * 10 / n > logged)
                {
                    logged = done * 10 / n;
                    ctx.Log(L.T($"    {done} of {n} models converted ({logged * 10}%)"));
                }
            }
        }
        try
        {
            // repairGen9: gen9 models the package already has, made with CodeWalker, get the same fixes as converted ones
            RpfRetarget.Convert(Source, tmp, GameEdition.Enhanced, dir, repairGen9: true, Converted, ctx.Token);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
        if (File.Exists(dst)) ctx.Journal.MoveAside(dst, keep: !Shared);
        else ctx.Journal.FileCreated(dst);
        File.Move(tmp, dst);
        ctx.Log($"    {name} -> {dst}");
    }

    /// <summary>"about 12 min left" from the pace so far; null while there is too little to judge by.</summary>
    internal static string? Left(TimeSpan spent, int done, int total)
    {
        if (done <= 0 || done >= total || spent.TotalSeconds < 5) return null;
        var left = TimeSpan.FromSeconds(spent.TotalSeconds / done * (total - done));
        return left.TotalMinutes < 1 ? L.T("less than a minute left")
             : left.TotalMinutes < 90 ? L.T($"about {(int)Math.Ceiling(left.TotalMinutes)} min left")
             : L.T($"about {left.TotalHours:0.#} h left");
    }
}
