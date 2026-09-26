using System.Buffers.Binary;

namespace Mdv.Core.Rpf;

/// <summary>
/// One archive file exactly as it is stored: its on-disk bytes plus the TOC words that
/// describe them. Moving it between archives verbatim keeps it valid — an entry the game
/// encrypted stays encrypted (its key depends on its name and size, not on the archive).
/// <list type="bullet">
/// <item>resource: <see cref="Data"/> = RSC7 header + DEFLATE body, X8/XC = system/graphics flags,
/// <see cref="TocSize"/> = on-disk length (0xFFFFFF for a big one);</item>
/// <item>binary: DEFLATE data, TocSize = its length, X8 = uncompressed length, XC = 1 when encrypted;</item>
/// <item>raw (nested archive, audio, oversized binary): TocSize = 0, X8 = length.</item>
/// </list>
/// </summary>
public sealed record StoredEntry(RpfEntryKind Kind, byte[] Data, uint TocSize, uint X8, uint XC)
{
    public bool IsResource => Kind == RpfEntryKind.Resource;

    /// <summary>A loose file as it would be stored. RSC7 files become resources (a Legacy model
    /// is converted for Enhanced); <paramref name="raw"/> or a <c>.rpf</c>/<c>.awc</c> is stored as is.</summary>
    public static StoredEntry FromFile(string name, byte[] content, GameEdition edition, bool raw = false)
    {
        if (content.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(content) == Rpf7.Rsc7Magic)
        {
            var p = RpfStreamBuilder.Resource(content, name, edition);
            return new StoredEntry(RpfEntryKind.Resource, p.Blob!, (uint)Math.Min(p.Blob!.Length, Rpf7.BigSize), p.A, p.B);
        }
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (!raw && ext is not (".rpf" or ".awc"))
        {
            var p = RpfStreamBuilder.Binary(content);
            if (p.Kind == RpfEntryKind.Binary)
                return new StoredEntry(RpfEntryKind.Binary, p.Blob!, (uint)p.Blob!.Length, (uint)content.Length, 0);
        }
        return new StoredEntry(RpfEntryKind.Raw, content, 0, (uint)content.Length, 0);
    }

    /// <summary>The entry's stored bytes and TOC words, verbatim (still encrypted, if it is).</summary>
    public static StoredEntry Of(RpfArchive arc, RpfEntry e)
    {
        if (e.IsDir) throw new InvalidOperationException($"{e.Name} is a directory");
        var kind = e.IsResource ? RpfEntryKind.Resource : e.StoredRaw ? RpfEntryKind.Raw : RpfEntryKind.Binary;
        long len = e.StoredRaw ? e.X8 : e.Size;
        return new StoredEntry(kind, arc.ReadAt(e.Offset, checked((int)len)), (uint)e.TocSize, e.X8, e.XC);
    }

    public bool SameAs(StoredEntry? other) =>
        other is not null && Kind == other.Kind && TocSize == other.TocSize && X8 == other.X8 && XC == other.XC &&
        Data.AsSpan().SequenceEqual(other.Data);

    // ------------------------------------------------------------ blob form (16-byte header + data)

    public byte[] ToBlob()
    {
        var b = new byte[16 + Data.Length];
        b[0] = (byte)Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), TocSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), X8);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), XC);
        Data.CopyTo(b, 16);
        return b;
    }

    public static StoredEntry FromBlob(byte[] b)
    {
        if (b.Length < 16 || b[0] > (byte)RpfEntryKind.Raw) throw new InvalidDataException("not a stored-entry blob");
        return new StoredEntry((RpfEntryKind)b[0], b[16..],
                               BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)),
                               BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)),
                               BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)));
    }
}
