using System.Buffers.Binary;
using Mdv.Core.Util;

namespace Mdv.Core.Rpf;

public sealed record RpfBuildInfo(string Path, int Entries, long Size, int Dirs = 1, int Files = 0)
{
    /// <summary>gen9 models repaired by <see cref="RpfRetarget.Convert"/> with repairGen9.</summary>
    public int Gen9Repaired { get; init; }
}

/// <summary>
/// Streams an RPF7-OPEN archive to disk (the container is the same for GTA V Legacy
/// and Enhanced — only the resources inside differ, see <see cref="ResourceEditions"/>). Every file's on-disk blob is produced and
/// written one at a time (data region first, header + TOC last), so packing a
/// multi-gigabyte pack never holds more than one file in memory.
/// </summary>
internal sealed class RpfStreamBuilder
{
    /// <summary>A node of the archive tree in TOC order.</summary>
    internal sealed class Node
    {
        public required string Name;
        public bool IsDir;
        public int First, Count;               // directory children span
        public Func<Payload>? Produce;          // file payload
        public int NameOffset;
    }

    internal sealed class Payload
    {
        public RpfEntryKind Kind;
        public byte[]? Blob;                    // in-memory blob
        public string? RawFile;                 // or: stream this file verbatim (Kind = Raw)
        public RpfArchive? Range;               // or: copy Length bytes at RangeOffset of this archive's stream
        public long RangeOffset;
        public long Length;
        public uint A, B;
    }

    /// <param name="dedupeNames">store each distinct name once (as CodeWalker does) — keeps big archives under the 64 KiB name limit</param>
    public static long Write(string outPath, IReadOnlyList<Node> nodes, bool dedupeNames = false)
    {
        // name table: root uses offset 0 (the leading NUL)
        var names = new MemoryStream();
        names.WriteByte(0);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 1; i < nodes.Count; i++)
        {
            if (dedupeNames && seen.TryGetValue(nodes[i].Name, out var at))
            {
                nodes[i].NameOffset = at;
                continue;
            }
            nodes[i].NameOffset = seen[nodes[i].Name] = (int)names.Length;
            names.Write(Rpf7.EncodeName(nodes[i].Name));
            names.WriteByte(0);
        }
        int namesLen = (int)Rpf7.Align(names.Length, 16);
        names.SetLength(namesLen);

        int count = nodes.Count;
        long headerRegion = 16 + (long)count * 16 + namesLen;
        long dataStart = Rpf7.Align(headerRegion, Rpf7.Sector);

        var toc = new byte[count * 16];
        var tmp = outPath + ".tmp";
        long total;
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                long cur = dataStart;
                for (int i = 0; i < count; i++)
                {
                    var n = nodes[i];
                    if (n.IsDir)
                    {
                        Rpf7.DirRecord(i == 0 ? 0u : (uint)n.NameOffset, (uint)n.First, (uint)n.Count)
                            .CopyTo(toc, i * 16);
                        continue;
                    }
                    var p = n.Produce!();
                    long len;
                    fs.Position = cur;
                    if (p.RawFile is not null)
                    {
                        using var src = File.OpenRead(p.RawFile);
                        src.CopyTo(fs, 1 << 20);
                        len = src.Length;
                    }
                    else if (p.Range is not null)
                    {
                        p.Range.CopyTo(fs, p.RangeOffset, p.Length);
                        len = p.Length;
                    }
                    else
                    {
                        fs.Write(p.Blob!);
                        len = p.Blob!.Length;
                    }
                    Rpf7.FileRecord(n.NameOffset, cur, p.Kind, len, p.A, p.B).CopyTo(toc, i * 16);
                    cur = Rpf7.Align(cur + len, Rpf7.Sector);
                }
                total = Math.Max(cur, dataStart);
                fs.SetLength(total);

                fs.Position = 0;
                fs.Write(Rpf7.Header(count, namesLen));
                fs.Write(toc);
                fs.Write(names.GetBuffer(), 0, namesLen);
            }
            File.Move(tmp, outPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
        return total;
    }

    // ---- payload producers --------------------------------------------------

    public static Payload ResourceFromFile(string path, GameEdition edition) =>
        Resource(File.ReadAllBytes(path), Path.GetFileName(path), edition);

    /// <summary>A loose RSC7 file (compressed or not) as a resource entry, its models left in the edition they are in.</summary>
    public static Payload ResourceAsIs(string path)
    {
        var name = Path.GetFileName(path);
        var raw = File.ReadAllBytes(path);
        var (s, g) = Rpf7.ReadRsc7Flags(raw, name);
        var blob = Rpf7.ResourceBlob(raw, s, g);
        var (sysf, gfxf) = Rpf7.ReadRsc7Flags(blob, name);
        return new Payload { Kind = RpfEntryKind.Resource, Blob = Rpf7.StampBigSize(blob), A = sysf, B = gfxf };
    }

    /// <summary>A loose RSC7 file (compressed or not) as a resource entry for <paramref name="edition"/>.</summary>
    public static Payload Resource(byte[] raw, string name, GameEdition edition)
    {
        var blob = ResourceEditions.ForEdition(raw, name, edition);
        var (sysf, gfxf) = Rpf7.ReadRsc7Flags(blob, name);     // before the big-size stamp scatters bytes 2/5/7/14
        return new Payload { Kind = RpfEntryKind.Resource, Blob = Rpf7.StampBigSize(blob), A = sysf, B = gfxf };
    }

    public static Payload Binary(byte[] data)
    {
        var compressed = Rpf7.Deflate(data);
        if (compressed.Length > Rpf7.BigSize)           // too big for the u24 field -> store raw
            return new Payload { Kind = RpfEntryKind.Raw, Blob = data, A = (uint)data.Length };
        return new Payload { Kind = RpfEntryKind.Binary, Blob = compressed, A = (uint)data.Length };
    }

    public static Payload RawFileStream(string path)
    {
        long len = new FileInfo(path).Length;
        if (len > uint.MaxValue)
            throw new InvalidOperationException($"{Path.GetFileName(path)} is larger than 4 GiB");
        return new Payload { Kind = RpfEntryKind.Raw, RawFile = path, A = (uint)len };
    }
}

/// <summary>
/// Builds a flat (single-directory) RPF7-OPEN archive: RSC7 resources (.ydr/.ytd,
/// header kept verbatim, body compressed exactly once) and binary files (DEFLATE,
/// real length in FileUncompressedSize).
/// </summary>
public sealed class RpfWriter(GameEdition edition = GameEdition.Legacy)
{
    private readonly List<(string Name, Func<RpfStreamBuilder.Payload> Produce)> _entries = [];

    /// <summary>The game the resources are packed for (Enhanced converts Legacy models to gen9).</summary>
    public GameEdition Edition { get; } = edition;

    /// <summary>Add an RSC7 resource file (flags are validated now, the blob is built at <see cref="Build"/>).</summary>
    public RpfWriter AddFile(string path)
    {
        Rpf7.ReadRsc7Flags(path);                        // fail early on a non-RSC7 file
        _entries.Add((Path.GetFileName(path), () => RpfStreamBuilder.ResourceFromFile(path, Edition)));
        return this;
    }

    public RpfWriter AddBinary(string name, byte[] data)
    {
        var payload = RpfStreamBuilder.Binary(data);
        _entries.Add((name, () => payload));
        return this;
    }

    public RpfWriter AddFolder(string folder, IEnumerable<string>? exts = null)
    {
        var allowed = new HashSet<string>(exts ?? Rpf7.ResourceExts, StringComparer.OrdinalIgnoreCase);
        foreach (var f in PathUtil.SortedFiles(folder))
            if (allowed.Contains(f.Extension.ToLowerInvariant()))
                AddFile(f.FullName);
        return this;
    }

    public RpfBuildInfo Build(string outPath)
    {
        var files = _entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        var nodes = new List<RpfStreamBuilder.Node>
        {
            new() { Name = "", IsDir = true, First = 1, Count = files.Count },
        };
        foreach (var (name, produce) in files)
            nodes.Add(new RpfStreamBuilder.Node { Name = name, Produce = produce });
        long size = RpfStreamBuilder.Write(outPath, nodes);
        return new RpfBuildInfo(outPath, files.Count, size, 1, files.Count);
    }
}

public static class RpfPacker
{
    /// <summary>
    /// Pack an entire folder tree into a single nested RPF7-OPEN archive (the
    /// canonical dlc.rpf). Resources (streamed types — <see cref="Rpf7.StreamedResourceExts"/> — and any file with an RSC7 header) keep their
    /// RSC7 header with a once-compressed body; nested <c>.rpf</c> children and <c>.awc</c>
    /// audio banks are stored raw; everything else
    /// (xml/meta/gxt2/json) is DEFLATE-compressed. Directory children are laid out
    /// breadth-first so every directory's children are contiguous and sorted.
    /// </summary>
    /// <param name="edition">the game the models are packed for; null: as they are</param>
    /// <param name="archives">folders packed already (full path → its .rpf): stored raw in their place</param>
    public static RpfBuildInfo PackFolder(string src, string outPath, GameEdition? edition = GameEdition.Legacy,
                                          IReadOnlyDictionary<string, string>? archives = null)
    {
        var nodes = new List<RpfStreamBuilder.Node>();
        var paths = new List<string>();
        nodes.Add(new RpfStreamBuilder.Node { Name = "", IsDir = true });
        paths.Add(src);
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!nodes[i].IsDir) continue;
            var dir = new DirectoryInfo(paths[i]);
            var kids = dir.EnumerateFileSystemInfos().OrderBy(k => k.Name, StringComparer.Ordinal).ToList();
            nodes[i].First = nodes.Count;
            nodes[i].Count = kids.Count;
            foreach (var k in kids)
            {
                bool isDir = k is DirectoryInfo;
                string? packed = null;
                if (isDir && archives?.TryGetValue(k.FullName, out packed) == true) isDir = false;
                var node = new RpfStreamBuilder.Node { Name = k.Name, IsDir = isDir };
                if (packed is not null)
                    node.Produce = () => RpfStreamBuilder.RawFileStream(packed);
                else if (!isDir)
                {
                    var full = k.FullName;
                    var ext = Path.GetExtension(k.Name).ToLowerInvariant();
                    // streamed resources by their type (a broken one fails here, not in the game); any other RSC7 file (.ymt…) by its header
                    if (Rpf7.MustBeResource(ext) || IsRsc7(full))
                    {
                        Rpf7.ReadRsc7Flags(full);
                        node.Produce = edition is { } ed ? () => RpfStreamBuilder.ResourceFromFile(full, ed) : () => RpfStreamBuilder.ResourceAsIs(full);
                    }
                    else if (ext is ".rpf" or ".awc")          // audio banks are streamed from the archive as they are
                        node.Produce = () => RpfStreamBuilder.RawFileStream(full);
                    else
                        node.Produce = () => RpfStreamBuilder.Binary(File.ReadAllBytes(full));
                }
                nodes.Add(node);
                paths.Add(k.FullName);
            }
        }
        long size = RpfStreamBuilder.Write(outPath, nodes);
        int dirs = nodes.Count(n => n.IsDir);
        return new RpfBuildInfo(outPath, nodes.Count - dirs, size, dirs, nodes.Count - dirs);
    }

    private static bool IsRsc7(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[16];
        return fs.Read(head) == 16 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Rpf7.Rsc7Magic;
    }
}

/// <summary>
/// Moves a finished archive between editions: every model resource that doesn't match
/// the target is converted, everything else (metas, other resources, encryption flags,
/// TOC order) is carried over byte for byte. Nested .rpf archives are handled recursively.
/// </summary>
public static class RpfRetarget
{
    /// <summary>RSC7 version from the TOC flags — valid for big resources too, whose archived header is scattered.</summary>
    private static int FlagsVersion(RpfEntry e) => (int)((((e.X8 >> 28) & 0xF) << 4) | ((e.XC >> 28) & 0xF));

    private static bool Mismatch(RpfEntry e, GameEdition target)
    {
        var ext = Path.GetExtension(e.Name);
        var edition = ResourceEditions.EditionOf(ext, FlagsVersion(e));
        return edition is not null && edition != target;
    }

    private static bool IsNestedArchive(RpfEntry e) =>
        e.StoredRaw && !e.IsEncrypted && e.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Inner paths of the model resources (recursively) built for the other edition.</summary>
    public static List<string> Mismatched(string rpfPath, GameEdition target)
    {
        using var arc = RpfArchive.Open(rpfPath);
        var found = new List<string>();
        Walk(arc, "", target, found);
        return found;
    }

    private static void Walk(RpfArchive arc, string prefix, GameEdition target, List<string> found)
    {
        foreach (var item in arc.Tree())
        {
            if (item.IsDir) continue;
            var e = item.Entry;
            if (e.IsResource && Mismatch(e, target)) found.Add(prefix + item.Path);
            else if (IsNestedArchive(e))
            {
                using var nested = arc.OpenNested(e);
                Walk(nested, prefix + item.Path + "/", target, found);
            }
        }
    }

    /// <summary>Rewrite <paramref name="srcRpf"/> for <paramref name="target"/> into <paramref name="outRpf"/>.</summary>
    /// <param name="tempDir">where nested archives are rebuilt (default: the temp folder) — a big pack's are gigabytes</param>
    /// <param name="repairGen9">also repair the gen9 models already in it that CodeWalker wrote
    /// (<see cref="ResourceEditions.FixGen9Resource"/>) — for packs converted before that fix</param>
    /// <param name="converted">called (from worker threads) with the number of models converted so far</param>
    /// <param name="cancel">stops the rewrite between models</param>
    public static RpfBuildInfo Convert(string srcRpf, string outRpf, GameEdition target, string? tempDir = null, bool repairGen9 = false,
                                       Action<int>? converted = null, CancellationToken cancel = default)
    {
        using var arc = RpfArchive.Open(srcRpf);
        var job = new Job(target, tempDir ?? Path.GetTempPath(), repairGen9 && target == GameEdition.Enhanced)
        {
            Converted = converted, Cancel = cancel,
        };
        try
        {
            long size = Rebuild(arc, outRpf, job);
            int dirs = arc.Entries.Count(e => e.IsDir);
            return new RpfBuildInfo(outRpf, arc.Entries.Count - dirs, size, dirs, arc.Entries.Count - dirs) { Gen9Repaired = job.Fixed };
        }
        finally
        {
            foreach (var t in job.Temps)
                try { File.Delete(t); } catch { /* best effort */ }
        }
    }

    private sealed class Job(GameEdition target, string tempDir, bool repairGen9)
    {
        public GameEdition Target { get; } = target;
        public string TempDir { get; } = tempDir;
        public bool RepairGen9 { get; } = repairGen9;
        public List<string> Temps { get; } = [];
        public int Fixed;
        public int Done;
        public Action<int>? Converted { get; init; }
        public CancellationToken Cancel { get; init; }
    }

    private static long Rebuild(RpfArchive arc, string outPath, Job job)
    {
        var nodes = arc.Entries.Select(e => new RpfStreamBuilder.Node
        {
            Name = e.Name, IsDir = e.IsDir, First = e.FirstChild, Count = e.ChildCount,
            Produce = e.IsDir ? null : () => Produce(arc, e, job),
        }).ToList();
        return RpfStreamBuilder.Write(outPath, nodes);
    }

    private static RpfStreamBuilder.Payload Produce(RpfArchive arc, RpfEntry e, Job job)
    {
        if (e.IsResource)
        {
            if (Mismatch(e, job.Target))
            {
                job.Cancel.ThrowIfCancellationRequested();
                var payload = RpfStreamBuilder.Resource(arc.ReadContent(e), e.Name, job.Target);
                job.Converted?.Invoke(Interlocked.Increment(ref job.Done));
                return payload;
            }
            if (job.RepairGen9 && ResourceEditions.EditionOf(Path.GetExtension(e.Name), FlagsVersion(e)) == GameEdition.Enhanced)
            {
                var content = arc.ReadContent(e);
                var fixedRes = ResourceEditions.FixGen9Resource(content, e.Name);
                if (!ReferenceEquals(fixedRes, content))
                {
                    Interlocked.Increment(ref job.Fixed);
                    return RpfStreamBuilder.Resource(fixedRes, e.Name, job.Target);
                }
            }
            return new RpfStreamBuilder.Payload
            {
                Kind = RpfEntryKind.Resource, Blob = arc.ReadAt(e.Offset, (int)e.Size), A = e.X8, B = e.XC,
            };
        }
        if (IsNestedArchive(e))
        {
            var tmp = Path.Combine(job.TempDir, $"mdv_{Guid.NewGuid():N}.rpf");
            job.Temps.Add(tmp);
            using (var nested = arc.OpenNested(e))
                Rebuild(nested, tmp, job);
            return RpfStreamBuilder.RawFileStream(tmp);
        }
        var stored = arc.ReadAt(e.Offset, (int)(e.StoredRaw ? e.X8 : e.Size));   // verbatim, flags kept
        return new RpfStreamBuilder.Payload
        {
            Kind = e.StoredRaw ? RpfEntryKind.Raw : RpfEntryKind.Binary, Blob = stored, A = e.X8, B = e.XC,
        };
    }
}

internal static class BinaryExt
{
    public static uint U32(this byte[] b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off));
}
