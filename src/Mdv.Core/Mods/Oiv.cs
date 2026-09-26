using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
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
            throw new IntakeException($"The OIV package's assembly.xml is not valid XML ({ex.Message}).", ex);
        }
        var root = doc.Root!;
        var dir = Path.GetDirectoryName(Path.GetFullPath(assemblyXml))!;
        var meta = El(root, "metadata");
        var format = Attr(root, "version") ?? "";
        var target = Attr(root, "target");
        if (target is not null && !target.Equals("Five", StringComparison.OrdinalIgnoreCase))
            throw new IntakeException($"This OIV package is for another game («{target}»), not GTA V.");

        var name = Text(El(meta, "name"));
        var author = El(meta, "author");
        var colors = El(root, "colors");
        var header = El(colors, "headerBackground");
        var icon = Path.Combine(dir, "icon.png");
        var pkg = new OivPackage
        {
            Name = name is { Length: > 0 } ? name : "OIV package",
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
            pkg.Warnings.Add($"The package is in OIV format {format} — ModDrop V reads format 2; some steps may be skipped.");

        var content = El(root, "content") ?? throw new IntakeException("The OIV package has no <content> — nothing to install.");
        Walk(pkg, content, null);
        if (pkg.Steps.Count == 0) throw new IntakeException("The OIV package has no install instructions.");

        int adds = pkg.Steps.Count(s => s is OivAdd), edits = pkg.Steps.Count(s => s is OivXml or OivText);
        int deletes = pkg.Steps.Count(s => s is OivDelete);
        var parts = new List<string>();
        if (adds > 0) parts.Add($"{adds} file(s) to put in");
        if (edits > 0) parts.Add($"{edits} file(s) to edit");
        if (deletes > 0) parts.Add($"{deletes} to delete");
        pkg.Parts.Add($"OIV package{(pkg.Version is { } v ? " v" + v : "")}{(pkg.Author is { } a ? " by " + a : "")}");
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
                        pkg.Warnings.Add($"The package's file content/{src} is missing — {path} is skipped.");
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
                        pkg.Warnings.Add($"{path}: archives of type {type} aren't supported — its steps are skipped.");
                        break;
                    }
                    if (!path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                    {
                        pkg.Warnings.Add($"{path} is not an .rpf archive — its steps are skipped.");
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
                            pkg.Warnings.Add($"{path}: <{x.Name.LocalName}> isn't an XML edit ModDrop V knows — skipped.");
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
                                pkg.Warnings.Add($"{path}: <{x.Name.LocalName}> isn't a text edit ModDrop V knows — skipped.");
                                break;
                        }
                    }
                    if (edits.Count > 0) pkg.Steps.Add(new OivText(path, archive is not null, IsTrue(Attr(e, "createIfNotExist")), edits));
                    break;
                }
                default:
                    pkg.Warnings.Add($"The package's <{e.Name.LocalName}> instruction isn't supported — skipped.");
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
            if (raw.Trim().Length > 0) pkg.Warnings.Add($"«{raw.Trim()}» points outside the game folder — skipped.");
            return null;
        }
        return archive is null ? rel : $"{archive}/{rel}";
    }

    /// <summary>'/'-separated relative path without "." segments; null for an empty, absolute or escaping one.</summary>
    internal static string? Norm(string raw)
    {
        var p = raw.Trim().Replace('\\', '/');
        if (p.Length == 0 || p.Contains(':') || p.StartsWith('/')) return null;
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
                log($"    [!] {what}: the XPath {edit.XPath} is not valid ({ex.Message}) — skipped.");
                continue;
            }
            if (hits.Count == 0)
            {
                log($"    [!] {what}: nothing matches {edit.XPath} — that edit is skipped.");
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
                        log($"    [!] {what}: the line «{e.Line}» is not there — the text is added at the end.");
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
                    if (n == 0) log($"    [!] {what}: the line «{e.Line}» is not there — nothing replaced.");
                    break;
                case OivTextMode.Delete:
                    if (lines.RemoveAll(l => Is(l, e.Line)) == 0) log($"    [!] {what}: the line «{e.Line}» is not there — nothing deleted.");
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

    private const string DlclistPath = "update/update.rpf/" + GameInstaller.DlclistInner;

    /// <summary>The first OIV package of the drop (its assembly.xml), or null.</summary>
    public override ModPackage? Analyze(DroppedSource source, DetectionReport report, HandlerEnv env)
    {
        if (!report.Has(ModCategory.Package)) return null;
        var assemblies = source.Files
            .Where(f => f.Name.Equals(OivReader.AssemblyFile, StringComparison.OrdinalIgnoreCase) &&
                        new FileInfo(f.FullPath).Length < 16 << 20 &&
                        OivReader.IsAssembly(TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.FullPath), strict: false)))
            .OrderBy(f => f.Depth).ToList();
        if (assemblies.Count == 0) return null;
        var pkg = OivReader.Read(assemblies[0].FullPath);
        pkg.Source = source.Sources.Count == 1 ? ModSource.Of(source.Sources[0]) : null;
        if (assemblies.Count > 1)
            pkg.Warnings.Add($"The drop holds {assemblies.Count} OIV packages — «{pkg.Name}» ({assemblies[0].Origin}) is the one installed; " +
                             "drop the others one at a time.");
        return pkg;
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
            bool exists = a.InArchive ? overlay.Exists(a.Path) && !MadeBy(overlay, id, a.Path)
                : (File.Exists(Path.Combine(target.ModsDir, a.Path)) &&
                   !(old?.Journal.Any(s => s is CreatedFile f && f.Path.Equals("mods/" + a.Path, StringComparison.OrdinalIgnoreCase)) ?? false))
                  || File.Exists(Path.Combine(target.GameDir, a.Path));
            if (exists) continue;
            if (a.Create) made.Add(a.Path);
            else
            {
                missing.Add(a.Path);
                plan.Warnings.Add($"{a.Path} is not in this game, and the package doesn't create it — its steps are skipped.");
            }
        }

        var putPaths = new List<string>();
        int files = 0;
        foreach (var step in pkg.Steps)
        {
            if (missing.Any(m => Under(step.Path, m) || step.Path.Equals(m, StringComparison.OrdinalIgnoreCase))) continue;
            if (made.FirstOrDefault(m => Under(step.Path, m)) is not null) continue;        // built with its archive
            if (step is OivArchive arc)
            {
                if (made.Contains(arc.Path))
                {
                    plan.Add(new BuildArchiveOp(arc.Path, arc.InArchive, pkg.Steps.Where(s => Under(s.Path, arc.Path)).ToList(),
                                                pkg.Root, id, target.Edition));
                    if (arc.InArchive) putPaths.Add(arc.Path);
                    files++;
                }
                continue;
            }
            files++;
            if (step.InArchive) AddArchiveStep(plan, step, id, overlay, putPaths);
            else AddLooseStep(plan, step, target);
        }

        plan.Warnings.AddRange(ConflictWarnings(id, target, putPaths));
        plan.Add(Register(id, ModCategory.Package, pkg, target, $"OIV · {files} change(s)",
                          pkg.Link is { } link ? new() { ["link"] = link } : null));
        return plan;
    }

    /// <summary>An archive inside another that only this mod put there (it goes when the mod does).</summary>
    private static bool MadeBy(ModsOverlay overlay, string id, string path) =>
        overlay.State.Entries.TryGetValue(ModsOverlay.KeyOf(path), out var e) && e.Base == ModsOverlay.Absent &&
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
                plan.Warnings.Add("The package replaces the whole dlclist.xml — ModDrop V adds just its new packs " +
                                  (added.Count > 0 ? $"({string.Join(", ", added)})" : "(none — every pack in it is listed already)") +
                                  ", so the packs other mods listed stay.");
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
                plan.Add(new RpfEditOp(xml.Path, id, $"Edit {xml.Path} ({Count(xml.Edits.Count, "XML change")})", (content, log) =>
                {
                    if (content is null)
                    {
                        log($"    [!] {xml.Path} is not there — its XML edits are skipped.");
                        return null;
                    }
                    return OivEdits.ApplyXml(content, xml.Edits, log, xml.Path);
                }));
                putPaths.Add(xml.Path);
                if (IsDlclist(xml.Path))
                    plan.Warnings.Add("The package edits dlclist.xml in a way ModDrop V can't read as pack lines — the edit is made as is.");
                break;
            case OivText text:
                plan.Add(new RpfEditOp(text.Path, id, $"Edit {text.Path} ({Count(text.Edits.Count, "line change")})", (content, log) =>
                {
                    if (content is null && !text.Create)
                    {
                        log($"    [!] {text.Path} is not there — its line edits are skipped.");
                        return null;
                    }
                    return OivEdits.ApplyText(content, text.Edits, log, text.Path);
                }));
                putPaths.Add(text.Path);
                break;
        }
    }

    private static void AddLooseStep(InstallPlan plan, OivStep step, InstallTarget target)
    {
        // archives the game reads go into mods (a whole dlc.rpf, a replaced .rpf); plugins, scripts and their files into the game folder
        static string Place(string path) =>
            path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
                ? "mods/" + path : path;
        switch (step)
        {
            case OivAdd add:
                plan.Add(new CopyFileOp(add.Source, Place(add.Path)));
                break;
            case OivDelete del:
                var at = Place(del.Path);
                if (at != del.Path && !File.Exists(Path.Combine(target.GameDir, at)))
                    plan.Warnings.Add($"The package deletes the game's {del.Path} — ModDrop V never deletes the game's own archives; skipped.");
                else plan.Add(new DeleteFileOp(at));
                break;
            case OivXml xml:
                plan.Add(new FileEditOp(xml.Path, $"Edit <game>/{xml.Path} ({Count(xml.Edits.Count, "XML change")})", (content, log) =>
                {
                    if (content is null)
                    {
                        log($"    [!] <game>/{xml.Path} is not there — its XML edits are skipped.");
                        return null;
                    }
                    return OivEdits.ApplyXml(content, xml.Edits, log, xml.Path);
                }));
                break;
            case OivText text:
                plan.Add(new FileEditOp(text.Path, $"Edit <game>/{text.Path} ({Count(text.Edits.Count, "line change")})", (content, log) =>
                {
                    if (content is null && !text.Create)
                    {
                        log($"    [!] <game>/{text.Path} is not there — its line edits are skipped.");
                        return null;
                    }
                    return OivEdits.ApplyText(content, text.Edits, log, text.Path);
                }));
                break;
        }
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

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
    public override string Describe() =>
        inArchive ? $"Create {path} ({steps.Count(s => s is OivAdd)} file(s)) inside its archive (in a copy under mods)"
                  : $"Create mods/{path} ({steps.Count(s => s is OivAdd)} file(s))";

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
                ctx.Log($"    created {path}");
            }
            else new CopyFileOp(output, "mods/" + path).Execute(ctx);
        }
        finally
        {
            PathUtil.TryDeleteDir(work);
        }
    }
}
