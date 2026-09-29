using System.Text;
using System.Xml.Linq;
using Mdv.Core.Rpf;

namespace Mdv.Core.Index;

/// <summary>
/// One file inside a game archive. <see cref="Dir"/> indexes the archive's
/// <see cref="IndexedArchive.Dirs"/>; a nested archive is listed as a file too (and its
/// content under a directory of the same path). <see cref="Size"/> is the stored length
/// (a binary's uncompressed length); <see cref="Version"/> is the RSC7 version of a resource.
/// </summary>
public readonly record struct IndexedFile(int Dir, string Name, long Size, RpfEntryKind Kind, byte Version)
{
    public bool IsArchive => Kind == RpfEntryKind.Raw && Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a DLC pack's <c>setup2.xml</c> says about when it loads.</summary>
public sealed record DlcSetup(string DeviceName, int Order, int SubPackCount);

/// <summary>
/// The file list of one top-level archive of the game (<c>x64e.rpf</c>, <c>update/update.rpf</c>,
/// <c>update/x64/dlcpacks/mpbattle/dlc.rpf</c>, <c>mods/update/update.rpf</c> …), nested archives
/// included. Directory paths are '/'-separated inside the archive, "" is its root; a nested
/// archive is a directory named like the file (<c>levels/gta5/vehicles.rpf</c>).
/// </summary>
public sealed class IndexedArchive
{
    /// <summary>Path relative to the game folder, '/'-separated, as on disk.</summary>
    public required string RelPath { get; init; }
    public long Length { get; init; }
    public long LastWriteUtcTicks { get; init; }
    public required string[] Dirs { get; init; }
    public required IndexedFile[] Files { get; init; }
    /// <summary>The root <c>setup2.xml</c> of a DLC pack, if the archive has one.</summary>
    public DlcSetup? Setup { get; init; }
    /// <summary>Pack names listed by the <c>dlclist.xml</c> this archive carries (lower case), if any.</summary>
    public string[]? DlcList { get; init; }
    /// <summary>Why the archive (or part of it) couldn't be read; its readable part is still listed.</summary>
    public string? Error { get; init; }

    public string PathOf(in IndexedFile f)
    {
        var dir = Dirs[f.Dir];
        return dir.Length == 0 ? f.Name : $"{dir}/{f.Name}";
    }

    /// <summary>Does the archive on disk still match this scan?</summary>
    public bool IsCurrent(FileInfo fi) =>
        fi.Exists && fi.Length == Length && fi.LastWriteTimeUtc.Ticks == LastWriteUtcTicks;

    /// <summary>
    /// Read the TOC of <paramref name="path"/> and of every archive nested in it. Only headers
    /// and tables are read, plus the tiny <c>setup2.xml</c> / <c>dlclist.xml</c> that decide what
    /// the game loads. A broken nested archive is skipped and noted in <see cref="Error"/>; the
    /// rest of the archive is still listed.
    /// </summary>
    public static IndexedArchive Scan(string path, string relPath, GameCrypto? crypto, CancellationToken ct = default)
    {
        var fi = new FileInfo(path);
        var dirs = new List<string>();
        var files = new List<IndexedFile>();
        var errors = new List<string>();
        DlcSetup? setup = null;
        string[]? dlcList = null;
        try
        {
            using var rpf = RpfArchive.Open(path, crypto: crypto);
            Walk(rpf, "", dirs, files, errors, ct);
            setup = ReadXml(rpf, "setup2.xml", ParseSetup, errors);
            dlcList = ReadXml(rpf, "common/data/dlclist.xml", ParseDlcList, errors)
                      ?? ReadXml(rpf, "data/dlclist.xml", ParseDlcList, errors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            errors.Add(ex.Message);
        }
        return new IndexedArchive
        {
            RelPath = relPath,
            Length = fi.Length,
            LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
            Dirs = [.. dirs],
            Files = [.. files],
            Setup = setup,
            DlcList = dlcList,
            Error = errors.Count == 0 ? null : string.Join("; ", errors),
        };
    }

    /// <summary>
    /// The loose files under a folder (Onigiri's <c>onigiri\common</c>, <c>onigiri\platform</c>) listed like an archive's —
    /// the archives among them are listed on their own (<see cref="Scan"/>), not walked into. Not cached: only file
    /// headers are read. The folder's <c>data\dlclist.xml</c> is read too.
    /// </summary>
    public static IndexedArchive ScanLoose(string dir, string relPath, CancellationToken ct = default)
    {
        var dirs = new List<string>();
        var dirIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var files = new List<IndexedFile>();
        var errors = new List<string>();
        int DirId(string rel)
        {
            if (dirIds.TryGetValue(rel, out var id)) return id;
            dirs.Add(rel);
            return dirIds[rel] = dirs.Count - 1;
        }
        DirId("");
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(dir, "*", opts))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (rel.StartsWith('.') || rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(rel);
            var parent = rel.Length > name.Length ? rel[..(rel.Length - name.Length - 1)] : "";
            try
            {
                var fi = new FileInfo(file);
                var kind = RpfEntryKind.Raw;
                byte version = 0;
                if (!name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) && fi.Length >= 16)
                {
                    Span<byte> h = stackalloc byte[16];
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) fs.ReadExactly(h);
                    if (BitConverter.ToUInt32(h) == Rpf7.Rsc7Magic)
                    {
                        kind = RpfEntryKind.Resource;
                        version = (byte)BitConverter.ToUInt32(h[4..]);
                    }
                    else kind = RpfEntryKind.Binary;
                }
                files.Add(new IndexedFile(DirId(parent), name, fi.Length, kind, version));
            }
            catch (IOException ex)
            {
                errors.Add($"{rel}: {ex.Message}");
            }
        }
        string[]? dlcList = null;
        var list = Path.Combine(dir, "data", "dlclist.xml");
        if (File.Exists(list))
        {
            try
            {
                dlcList = ParseDlcList(XDocument.Parse(File.ReadAllText(list).TrimStart('﻿', '\0')).Root!);
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
            {
                errors.Add($"data/dlclist.xml: {ex.Message}");
            }
        }
        return new IndexedArchive
        {
            RelPath = relPath, Dirs = [.. dirs], Files = [.. files], DlcList = dlcList,
            Error = errors.Count == 0 ? null : string.Join("; ", errors),
        };
    }

    private static void Walk(RpfArchive rpf, string prefix, List<string> dirs, List<IndexedFile> files,
                             List<string> errors, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var seen = new HashSet<int>();
        var stack = new Stack<(int Entry, string Path)>();
        stack.Push((0, prefix));
        var nested = new List<(RpfEntry Entry, string Path)>();
        while (stack.Count > 0)
        {
            var (idx, dirPath) = stack.Pop();
            if (idx < 0 || idx >= rpf.Entries.Count || !seen.Add(idx)) continue;
            dirs.Add(dirPath);
            int dirId = dirs.Count - 1;
            var d = rpf.Entries[idx];
            for (int c = d.FirstChild; c < d.FirstChild + d.ChildCount && c < rpf.Entries.Count; c++)
            {
                var e = rpf.Entries[c];
                string full = dirPath.Length == 0 ? e.Name : $"{dirPath}/{e.Name}";
                if (e.IsDir)
                {
                    stack.Push((c, full));
                    continue;
                }
                var kind = e.IsResource ? RpfEntryKind.Resource : e.StoredRaw ? RpfEntryKind.Raw : RpfEntryKind.Binary;
                // RSC7 version from the TOC flags (CodeWalker GetVersionFromFlags); a binary's size is X8
                byte version = e.IsResource ? (byte)((((e.X8 >> 28) & 0xF) << 4) | ((e.XC >> 28) & 0xF)) : (byte)0;
                long size = e.IsResource ? e.Size : e.X8;
                files.Add(new IndexedFile(dirId, e.Name, size, kind, version));
                if (kind == RpfEntryKind.Raw && e.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                    nested.Add((e, full));
            }
        }

        foreach (var (e, full) in nested)
        {
            try
            {
                using var child = rpf.OpenNested(e);
                Walk(child, full, dirs, files, errors, ct);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                errors.Add($"{full}: {ex.Message}");
            }
        }
    }

    private static T? ReadXml<T>(RpfArchive rpf, string innerPath, Func<XElement, T?> parse, List<string> errors)
        where T : class
    {
        var e = rpf.Locate(innerPath);
        if (e is null) return null;
        try
        {
            var text = Encoding.UTF8.GetString(rpf.ReadContent(e)).TrimStart('﻿', '\0');
            return parse(XDocument.Parse(text).Root!);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException)
        {
            errors.Add($"{innerPath}: {ex.Message}");
            return null;
        }
    }

    internal static DlcSetup? ParseSetup(XElement root)
    {
        static int IntAttr(XElement r, string name) =>
            int.TryParse(r.Element(name)?.Attribute("value")?.Value, out var v) ? v : 0;
        return new DlcSetup(root.Element("deviceName")?.Value.Trim() ?? "", IntAttr(root, "order"),
                            IntAttr(root, "subPackCount"));
    }

    /// <summary><c>&lt;Paths&gt;&lt;Item&gt;dlcpacks:/mpBattle/&lt;/Item&gt;</c> → "mpbattle".</summary>
    internal static string[]? ParseDlcList(XElement root)
    {
        var paths = root.Element("Paths");
        if (paths is null) return null;
        return [.. paths.Elements("Item")
            .Select(i => DlcNameOf(i.Value))
            .Where(n => n.Length > 0)];
    }

    internal static string DlcNameOf(string item)
    {
        var parts = item.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "" : parts[^1].ToLowerInvariant();
    }
}
