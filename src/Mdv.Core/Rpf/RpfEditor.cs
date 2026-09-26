using System.Buffers.Binary;

namespace Mdv.Core.Rpf;

/// <summary>
/// Edits an RPF7 archive file in place — files at any depth of nested archives can be
/// replaced, added and deleted (what OpenIV's edit mode does to a copy in the mods folder).
/// <para>
/// Until <see cref="Commit"/> nothing the archive's current tables point at is touched: new
/// data goes only into space that was free on disk when the editor opened (holes, slack at
/// the end of a nested archive) or past the end of the file, and a nested archive that has
/// to grow is extended into free space after it or copied, with some slack, to a new place
/// in its parent. <see cref="Commit"/> then writes the tables, innermost archive first —
/// that is the only step that overwrites anything. Disposing without a commit leaves the
/// archive as it was (the file is cut back to its old length).
/// </para>
/// <para>
/// Every archive whose table is rewritten becomes OPEN (unencrypted table), as OpenIV and
/// CodeWalker do in the mods folder; entries the game encrypted one by one are kept as they
/// are. Space freed by a replaced or deleted entry is reused by the next editor; when too much
/// of the file is holes, <see cref="Compact"/> writes a tight copy.
/// </para>
/// </summary>
public sealed class RpfEditor : IDisposable
{
    private const int Sector = Rpf7.Sector;

    private abstract class Node
    {
        public required string Name;
        public DNode? Parent;
    }

    private sealed class DNode : Node
    {
        public readonly List<Node> Kids = [];
    }

    private sealed class FNode : Node
    {
        public long Block;
        /// <summary>Stored bytes (a nested archive: its length).</summary>
        public long Length;
        public RpfEntryKind Kind;
        public uint TocSize, X8, XC;
        public Arc? Child;

        public long Blocks => Blk(Length);
        public bool IsArchive => Kind == RpfEntryKind.Raw && XC != 1 && Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One archive of the tree: the file itself or a nested one (a window of its parent).</summary>
    private sealed class Arc
    {
        public required string Name;
        public required DNode Root;
        public Arc? Parent;
        public FNode? Slot;
        public int Depth;
        /// <summary>Absolute offset of its header in the file.</summary>
        public long Start;
        /// <summary>Extent in 512-byte blocks.</summary>
        public long Blocks;
        public uint Encryption;
        /// <summary>Blocks the header occupies on disk (and may use once rewritten).</summary>
        public long HeaderBlocks;
        /// <summary>Block ranges [S, E) free on disk at open (or claimed fresh this session) and not yet used.</summary>
        public readonly List<(long S, long E)> Free = [];
        public bool Dirty;
        public long NeedCache = -1;
    }

    private readonly FileStream _fs;
    private readonly GameCrypto? _crypto;
    private readonly long _openLength;
    private readonly Arc _top;
    private readonly List<Arc> _arcs = [];
    private bool _done;

    public string FilePath { get; }

    /// <summary>Something was changed (a commit will rewrite tables).</summary>
    public bool Changed => _arcs.Any(a => a.Dirty);

    private RpfEditor(string path, FileStream fs, GameCrypto? crypto)
    {
        FilePath = path;
        _fs = fs;
        _crypto = crypto;
        _openLength = fs.Length;
        var ra = RpfArchive.OnStream(fs, 0, fs.Length, Path.GetFileName(path), crypto);
        _top = Load(ra, null, null, Blk(fs.Length));
    }

    /// <summary>
    /// Open an archive for writing. A file just written is often held for a moment by someone else (an antivirus
    /// scanning it, a program reading it): a sharing violation is retried for about two seconds before giving up.
    /// </summary>
    internal static FileStream OpenForWrite(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
            }
            catch (IOException ex) when (attempt < 10 && (ex.HResult & 0xFFFF) is 32 or 33)     // ERROR_SHARING_VIOLATION / LOCK_VIOLATION
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <param name="crypto">keys for archives the game encrypted (a fresh copy of a game archive)</param>
    public static RpfEditor Open(string path, GameCrypto? crypto = null)
    {
        var fs = OpenForWrite(path);
        try
        {
            return new RpfEditor(path, fs, crypto);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private static long Blk(long bytes) => (bytes + Sector - 1) / Sector;

    // ================================================================ loading

    private Arc Load(RpfArchive ra, Arc? parent, FNode? slot, long blocks)
    {
        var arc = new Arc
        {
            Name = ra.Name, Root = new DNode { Name = "" }, Parent = parent, Slot = slot,
            Depth = parent is null ? 0 : parent.Depth + 1, Start = ra.BaseOffset, Blocks = blocks,
            Encryption = ra.Encryption, HeaderBlocks = Blk(16 + 16L * ra.Entries.Count + ra.NamesLength),
        };
        var files = new List<FNode>();
        if (ra.Entries.Count > 0) Fill(ra, ra.Entries[0], arc.Root, files, [0]);

        long cur = arc.HeaderBlocks;
        foreach (var f in files.OrderBy(f => f.Block))
        {
            if (f.Block > cur) arc.Free.Add((cur, f.Block));
            cur = Math.Max(cur, f.Block + f.Blocks);
        }
        if (arc.Blocks > cur) arc.Free.Add((cur, arc.Blocks));
        _arcs.Add(arc);
        return arc;
    }

    private static void Fill(RpfArchive ra, RpfEntry dir, DNode node, List<FNode> files, HashSet<int> seen)
    {
        for (int c = dir.FirstChild; c < dir.FirstChild + dir.ChildCount && c < ra.Entries.Count; c++)
        {
            if (!seen.Add(c)) continue;
            var e = ra.Entries[c];
            if (e.IsDir)
            {
                var d = new DNode { Name = e.Name, Parent = node };
                node.Kids.Add(d);
                Fill(ra, e, d, files, seen);
                continue;
            }
            var f = new FNode
            {
                Name = e.Name, Parent = node, Block = (e.Offset - ra.BaseOffset) / Sector,
                Length = e.StoredRaw ? e.X8 : e.Size,
                Kind = e.IsResource ? RpfEntryKind.Resource : e.StoredRaw ? RpfEntryKind.Raw : RpfEntryKind.Binary,
                TocSize = (uint)e.TocSize, X8 = e.X8, XC = e.XC,
            };
            node.Kids.Add(f);
            files.Add(f);
        }
    }

    private Arc ChildOf(Arc arc, FNode f)
    {
        if (f.Child is not null) return f.Child;
        var ra = RpfArchive.OnStream(_fs, arc.Start + f.Block * Sector, f.X8, f.Name, _crypto);
        return f.Child = Load(ra, arc, f, Blk(f.X8));
    }

    private IEnumerable<Arc> Below(Arc arc) => _arcs.Where(a => a != arc && IsUnder(a, arc));

    private static bool IsUnder(Arc a, Arc ancestor)
    {
        for (var p = a.Parent; p is not null; p = p.Parent)
            if (p == ancestor) return true;
        return false;
    }

    private void Forget(FNode f)
    {
        if (f.Child is not { } c) return;
        _arcs.RemoveAll(a => a == c || IsUnder(a, c));
        f.Child = null;
    }

    // ================================================================ paths

    private static string[] Split(string innerPath) =>
        innerPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static Node? Kid(DNode dir, string name) =>
        dir.Kids.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Children stay sorted ordinally (the game binary-searches them); a new one goes in its place.</summary>
    private static void Insert(DNode dir, Node node)
    {
        int i = dir.Kids.FindIndex(k => string.CompareOrdinal(k.Name, node.Name) > 0);
        node.Parent = dir;
        if (i < 0) dir.Kids.Add(node);
        else dir.Kids.Insert(i, node);
    }

    private (Arc Arc, DNode Dir, string Name)? Walk(string innerPath, bool create)
    {
        var parts = Split(innerPath);
        if (parts.Length == 0) throw new ArgumentException("An archive path is empty.", nameof(innerPath));
        var arc = _top;
        var dir = _top.Root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (Kid(dir, parts[i]))
            {
                case DNode d:
                    dir = d;
                    continue;
                case FNode { IsArchive: true } f:
                    arc = ChildOf(arc, f);
                    dir = arc.Root;
                    continue;
                case FNode:
                    throw new InvalidOperationException($"{string.Join('/', parts[..(i + 1)])} is a file, not a folder.");
            }
            if (!create) return null;
            if (parts[i].EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{string.Join('/', parts[..(i + 1)])} doesn't exist — files can't be added to an archive that isn't there.");
            var nd = new DNode { Name = parts[i].ToLowerInvariant() };
            Insert(dir, nd);
            Touch(arc);
            dir = nd;
        }
        return (arc, dir, parts[^1]);
    }

    private FNode? FindFile(string innerPath, out Arc arc)
    {
        arc = _top;
        var w = Walk(innerPath, create: false);
        if (w is null) return null;
        arc = w.Value.Arc;
        return Kid(w.Value.Dir, w.Value.Name) as FNode;
    }

    // ================================================================ public operations

    public bool Exists(string innerPath) => FindFile(innerPath, out _) is not null;

    /// <summary>The entry at <paramref name="innerPath"/> as stored (with this session's changes), or null.</summary>
    public StoredEntry? Get(string innerPath)
    {
        var f = FindFile(innerPath, out var arc);
        if (f is null) return null;
        if (f.Child is { } c && (c.Dirty || Below(c).Any(a => a.Dirty)))
            throw new InvalidOperationException($"{innerPath} has unsaved changes inside — commit them first.");
        return new StoredEntry(f.Kind, Read(arc.Start + f.Block * Sector, f.Length), f.TocSize, f.X8, f.XC);
    }

    /// <summary>Replace the entry at <paramref name="innerPath"/>, or add it (with any missing folders).</summary>
    public void Put(string innerPath, StoredEntry data)
    {
        Check();
        var (arc, dir, name) = Walk(innerPath, create: true)!.Value;
        var f = Kid(dir, name) switch
        {
            FNode existing => existing,
            DNode => throw new InvalidOperationException($"{innerPath} is a folder."),
            _ => null,
        };
        if (f is null)
        {
            f = new FNode { Name = name.ToLowerInvariant() };
            Insert(dir, f);
        }
        Forget(f);
        f.Length = 0;
        Touch(arc);                                        // the header may grow by this entry: count it before allocating
        long blocks = Math.Max(1, Blk(data.Data.Length));
        long blk = Allocate(arc, blocks);
        WriteBlocks(arc.Start + blk * Sector, data.Data, blocks);
        f.Block = blk;
        f.Length = data.Data.Length;
        f.Kind = data.Kind;
        f.TocSize = data.TocSize;
        f.X8 = data.X8;
        f.XC = data.XC;
    }

    /// <summary>Remove the entry at <paramref name="innerPath"/>; false when it isn't there.</summary>
    public bool Delete(string innerPath)
    {
        Check();
        var f = FindFile(innerPath, out var arc);
        if (f is null) return false;
        Forget(f);
        f.Parent!.Kids.Remove(f);
        Touch(arc);
        return true;
    }

    /// <summary>Write the tables of every changed archive (innermost first) and close the file.</summary>
    public void Commit()
    {
        Check();
        // an archive with a changed child is rewritten too, so the whole chain down to it is OPEN
        foreach (var a in _arcs.Where(a => a.Dirty).ToList())
            for (var p = a.Parent; p is not null; p = p.Parent) p.Dirty = true;
        while (_arcs.Where(a => a.Dirty).OrderByDescending(a => a.Depth).FirstOrDefault() is { } arc)
        {
            EnsureHeaderSpace(arc);
            if (arc.Encryption == GameCrypto.EncAes) DecryptAesEntries(arc);
            WriteHeader(arc);
            arc.Dirty = false;
        }
        long len = _top.Blocks * Sector;
        if (_fs.Length < len) _fs.SetLength(len);
        _fs.Flush(flushToDisk: true);
        _done = true;
        _fs.Dispose();
    }

    /// <summary>Close without committing: the archive stays as it was.</summary>
    public void Dispose()
    {
        if (_done) return;
        _done = true;
        try
        {
            if (_fs.Length > _openLength) _fs.SetLength(_openLength);
        }
        catch (IOException) { /* the data past the old end is unused anyway */ }
        _fs.Dispose();
    }

    private void Check() => ObjectDisposedException.ThrowIf(_done, this);

    /// <summary>
    /// File length and the bytes actually used by headers and entries, nested archives included
    /// (their own holes count as unused) — the difference is what <see cref="Compact"/> would win.
    /// </summary>
    public (long Length, long Used) Measure() => (_top.Blocks * Sector, Used(_top) * Sector);

    private long Used(Arc arc)
    {
        long used = HeaderNeed(arc);
        foreach (var f in Files(arc.Root))
            used += f.IsArchive ? Used(ChildOf(arc, f)) : f.Blocks;
        return used;
    }

    private static IEnumerable<FNode> Files(DNode dir)
    {
        foreach (var k in dir.Kids)
        {
            if (k is FNode f) yield return f;
            else foreach (var g in Files((DNode)k)) yield return g;
        }
    }

    // ================================================================ space

    private static void Touch(Arc arc)
    {
        arc.Dirty = true;
        arc.NeedCache = -1;
    }

    /// <summary>Room a moved nested archive gets to grow into: an eighth of it, 1 MB .. 256 MB.</summary>
    private static long Slack(long blocks) => Math.Clamp(blocks / 8, 2048, 524288);

    /// <summary>
    /// Blocks for <paramref name="count"/> in <paramref name="arc"/>: the best-fitting hole, else at its
    /// end (or always at the end: a nested archive placed last can keep growing where it is).
    /// </summary>
    private long Allocate(Arc arc, long count, bool atEnd = false)
    {
        long floor = HeaderNeed(arc) + 1;                  // leave the header a block to grow into
        int best = -1;
        long bestStart = 0, bestSize = long.MaxValue;
        for (int i = 0; i < arc.Free.Count && !atEnd; i++)
        {
            var (s, e) = arc.Free[i];
            s = Math.Max(s, floor);
            long size = e - s;
            if (size >= count && size < bestSize)
            {
                best = i;
                bestStart = s;
                bestSize = size;
            }
        }
        if (best >= 0)
        {
            Carve(arc, bestStart, bestStart + count);
            return bestStart;
        }
        long start = arc.Blocks;
        foreach (var (s, e) in arc.Free)
            if (e == arc.Blocks) start = Math.Min(start, s);
        start = Math.Max(start, floor);
        GrowTo(arc, start + count);
        Carve(arc, start, start + count);
        return start;
    }

    /// <summary>Remove [s, e) from the free list (parts of it that are free).</summary>
    private static void Carve(Arc arc, long s, long e)
    {
        for (int i = arc.Free.Count - 1; i >= 0; i--)
        {
            var (fs, fe) = arc.Free[i];
            if (fe <= s || fs >= e) continue;
            arc.Free.RemoveAt(i);
            if (fs < s) arc.Free.Add((fs, s));
            if (fe > e) arc.Free.Add((e, fe));
        }
    }

    private static void AddFree(Arc arc, long s, long e)
    {
        if (e <= s) return;
        int tail = arc.Free.FindIndex(r => r.E == s);
        if (tail >= 0)
        {
            s = arc.Free[tail].S;
            arc.Free.RemoveAt(tail);
        }
        arc.Free.Add((s, e));
    }

    /// <summary>Extend an archive to <paramref name="blocks"/>; the new tail is free space.</summary>
    private void GrowTo(Arc arc, long blocks)
    {
        if (blocks <= arc.Blocks) return;
        if (arc.Parent is null)
        {
            if (blocks - 1 > Rpf7.MaxBlock)
                throw new InvalidOperationException(
                    $"{arc.Name}: no room to grow — the archive would pass the RPF7 4 GiB limit.");
            AddFree(arc, arc.Blocks, blocks);
            arc.Blocks = blocks;
            return;
        }
        var p = arc.Parent;
        var slot = arc.Slot!;
        long old = arc.Blocks;
        if (ExtendInPlace(p, slot.Block + old, slot.Block + blocks))
        {
            AddFree(arc, old, blocks);
            SetExtent(arc, blocks);
        }
        else Relocate(arc, blocks + Slack(blocks));
    }

    /// <summary>Claim [from, to) of <paramref name="p"/> — free space right after a nested archive, or past p's end.</summary>
    private bool ExtendInPlace(Arc p, long from, long to)
    {
        long inside = Math.Min(to, p.Blocks);
        if (inside > from && !p.Free.Any(r => r.S <= from && r.E >= inside)) return false;
        if (to > p.Blocks)
        {
            if (from > p.Blocks) return false;
            GrowTo(p, to);
        }
        Carve(p, from, to);
        return true;
    }

    private void SetExtent(Arc arc, long blocks)
    {
        arc.Blocks = blocks;
        var slot = arc.Slot!;
        slot.Length = slot.X8 = checked((uint)(blocks * Sector));
        Touch(arc.Parent!);
        arc.Dirty = true;                                  // its table is rewritten as OPEN (NG keys depend on the length)
    }

    /// <summary>Copy a nested archive to the end of its parent, taking <paramref name="blocks"/> there.</summary>
    private void Relocate(Arc arc, long blocks)
    {
        var p = arc.Parent!;
        long dst = Allocate(p, blocks, atEnd: true);       // may move p (and arc with it): read positions after
        long from = arc.Start;
        long to = p.Start + dst * Sector;
        Copy(from, to, arc.Blocks * Sector);
        arc.Start = to;
        foreach (var a in Below(arc)) a.Start += to - from;
        arc.Slot!.Block = dst;
        AddFree(arc, arc.Blocks, blocks);
        SetExtent(arc, blocks);
    }

    /// <summary>Move an entry out of the way of a growing header.</summary>
    private void Move(Arc arc, FNode f)
    {
        if (f.IsArchive)
        {
            Relocate(ChildOf(arc, f), Blk(f.Length));
            return;
        }
        long dst = Allocate(arc, f.Blocks);
        Copy(arc.Start + f.Block * Sector, arc.Start + dst * Sector, f.Blocks * Sector);
        f.Block = dst;
        Touch(arc);
    }

    // ================================================================ tables

    private (List<Node> Order, Dictionary<DNode, (int First, int Count)> Dirs, byte[] Names, Dictionary<string, int> Offsets)
        Layout(Arc arc)
    {
        var order = new List<Node> { arc.Root };
        var dirs = new Dictionary<DNode, (int, int)>();
        for (int i = 0; i < order.Count; i++)
            if (order[i] is DNode d)
            {
                dirs[d] = (order.Count, d.Kids.Count);
                order.AddRange(d.Kids);
            }
        var names = new MemoryStream();
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = 0 };
        names.WriteByte(0);
        foreach (var n in order.Skip(1))
        {
            if (offsets.ContainsKey(n.Name)) continue;
            offsets[n.Name] = (int)names.Length;
            names.Write(Rpf7.EncodeName(n.Name));
            names.WriteByte(0);
        }
        names.SetLength(Rpf7.Align(names.Length, 16));
        return (order, dirs, names.ToArray(), offsets);
    }

    private long HeaderNeed(Arc arc)
    {
        if (arc.NeedCache >= 0) return arc.NeedCache;
        var (order, _, names, _) = Layout(arc);
        return arc.NeedCache = Math.Max(1, Blk(16 + 16L * order.Count + names.Length));
    }

    /// <summary>Make the header region big enough for the new table, moving entries that sit in the way.</summary>
    private void EnsureHeaderSpace(Arc arc)
    {
        long need = HeaderNeed(arc);
        if (need <= arc.HeaderBlocks) return;
        if (need > arc.Blocks) GrowTo(arc, need);
        Carve(arc, 0, need);
        foreach (var f in Files(arc.Root).Where(f => f.Block < need && f.Blocks > 0).ToList())
            Move(arc, f);
        arc.HeaderBlocks = need;
    }

    private void WriteHeader(Arc arc)
    {
        var (order, dirs, names, offsets) = Layout(arc);
        var toc = new byte[order.Count * 16];
        for (int i = 0; i < order.Count; i++)
        {
            int off = i == 0 ? 0 : offsets[order[i].Name];
            byte[] rec;
            if (order[i] is DNode d)
            {
                var (first, count) = dirs[d];
                rec = Rpf7.DirRecord((uint)off, (uint)first, (uint)count);
            }
            else rec = FileRecord(off, (FNode)order[i], arc);
            rec.CopyTo(toc, i * 16);
        }
        long size = 16 + toc.Length + names.Length;
        if (size > arc.HeaderBlocks * Sector)
            throw new InvalidOperationException($"{arc.Name}: header space was not reserved");   // EnsureHeaderSpace ran first
        var header = new byte[arc.HeaderBlocks * Sector];
        Rpf7.Header(order.Count, names.Length).CopyTo(header, 0);
        toc.CopyTo(header, 16);
        names.CopyTo(header, 16 + toc.Length);
        Write(arc.Start, header);
        arc.Encryption = Rpf7.EncOpen;
    }

    private static byte[] FileRecord(int nameOffset, FNode f, Arc arc)
    {
        if (nameOffset > 0xFFFF)
            throw new InvalidOperationException(
                $"{arc.Name}: the name table outgrew 64 KiB — too many files in one archive.");
        if (f.Block > Rpf7.MaxBlock)
            throw new InvalidOperationException($"{arc.Name}: past the RPF7 4 GiB offset limit.");
        var rec = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(rec, (ushort)nameOffset);
        rec[2] = (byte)(f.TocSize & 0xFF);
        rec[3] = (byte)((f.TocSize >> 8) & 0xFF);
        rec[4] = (byte)((f.TocSize >> 16) & 0xFF);
        rec[5] = (byte)(f.Block & 0xFF);
        rec[6] = (byte)((f.Block >> 8) & 0xFF);
        rec[7] = (byte)((int)((f.Block >> 16) & 0x7F) | (f.Kind == RpfEntryKind.Resource ? 0x80 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), f.X8);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(12), f.XC);
        return rec;
    }

    /// <summary>An AES archive turned OPEN: its encrypted entries are decrypted (the game would try NG keys on them).</summary>
    private void DecryptAesEntries(Arc arc)
    {
        foreach (var f in Files(arc.Root).Where(f => f.Kind == RpfEntryKind.Binary && f.XC == 1))
        {
            if (_crypto is null) throw new RpfEncryptedException($"{arc.Name}: the game's keys are needed to rewrite it.");
            long at = arc.Start + f.Block * Sector;
            Write(at, _crypto.DecryptEntry(Read(at, f.Length), GameCrypto.EncAes, f.Name, f.X8));
            f.XC = 0;
        }
    }

    // ================================================================ raw IO

    private byte[] Read(long at, long count)
    {
        var buf = new byte[checked((int)count)];
        _fs.Position = at;
        _fs.ReadExactly(buf);
        return buf;
    }

    private void Write(long at, ReadOnlySpan<byte> data)
    {
        _fs.Position = at;
        _fs.Write(data);
    }

    private void WriteBlocks(long at, byte[] data, long blocks)
    {
        Write(at, data);
        long pad = blocks * Sector - data.Length;
        if (pad > 0) _fs.Write(new byte[pad]);
    }

    private void Copy(long from, long to, long count)
    {
        var buf = new byte[1 << 20];
        while (count > 0)
        {
            int n = (int)Math.Min(count, buf.Length);
            _fs.Position = from;
            _fs.ReadExactly(buf, 0, n);
            _fs.Position = to;
            _fs.Write(buf, 0, n);
            from += n;
            to += n;
            count -= n;
        }
    }

    // ================================================================ compaction

    /// <summary>
    /// Write a tight copy of <paramref name="path"/> to <paramref name="outPath"/>: same tables and
    /// entries (byte for byte, in the same order), no holes. A nested archive with holes of its own
    /// is rebuilt too (into <paramref name="tempDir"/> first); the rest are copied as they are, with
    /// the free space at their end they grow into.
    /// </summary>
    public static long Compact(string path, string outPath, string tempDir, GameCrypto? crypto = null)
    {
        using var arc = RpfArchive.Open(path, crypto: crypto);
        var temps = new List<string>();
        try
        {
            return Rebuild(arc, outPath, tempDir, temps);
        }
        finally
        {
            foreach (var t in temps)
                try { File.Delete(t); } catch (IOException) { /* best effort */ }
        }
    }

    private static long Rebuild(RpfArchive arc, string outPath, string tempDir, List<string> temps)
    {
        var nodes = arc.Entries.Select(e => new RpfStreamBuilder.Node
        {
            Name = e.Name, IsDir = e.IsDir, First = e.FirstChild, Count = e.ChildCount,
            Produce = e.IsDir ? null : () => Produce(arc, e, tempDir, temps),
        }).ToList();
        return RpfStreamBuilder.Write(outPath, nodes, dedupeNames: true);
    }

    private static RpfStreamBuilder.Payload Produce(RpfArchive arc, RpfEntry e, string tempDir, List<string> temps)
    {
        if (e.StoredRaw && !e.IsEncrypted && e.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
        {
            // holes inside it are won back; free space at its end is slack it grows into — kept
            using var nested = arc.OpenNested(e);
            long header = Rpf7.Align(16 + 16L * nested.Entries.Count + nested.NamesLength, Sector);
            var files = nested.Files().Select(f => (Start: f.Offset - nested.BaseOffset,
                                                   Size: Rpf7.Align(f.StoredRaw ? f.X8 : f.Size, Sector))).ToList();
            long end = files.Select(f => f.Start + f.Size).DefaultIfEmpty(header).Max();
            if (end - header - files.Sum(f => f.Size) > 1 << 20)
            {
                Directory.CreateDirectory(tempDir);
                var tmp = Path.Combine(tempDir, $"compact_{Guid.NewGuid():N}.rpf");
                temps.Add(tmp);
                long tight = Rebuild(nested, tmp, tempDir, temps);
                using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Write))
                    fs.SetLength(tight + Math.Max(0, e.X8 - end));            // the slack stays
                return RpfStreamBuilder.RawFileStream(tmp);
            }
        }
        var kind = e.IsResource ? RpfEntryKind.Resource : e.StoredRaw ? RpfEntryKind.Raw : RpfEntryKind.Binary;
        return new RpfStreamBuilder.Payload
        {
            Kind = kind, Range = arc, RangeOffset = e.Offset, Length = e.StoredRaw ? e.X8 : e.Size, A = e.X8, B = e.XC,
        };
    }
}
