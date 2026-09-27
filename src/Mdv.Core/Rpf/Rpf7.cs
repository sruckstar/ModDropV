using System.Buffers.Binary;
using System.IO.Compression;

namespace Mdv.Core.Rpf;

/// <summary>A file is not a readable RPF7-OPEN archive (wrong magic, encrypted TOC…).</summary>
public class RpfFormatException(string message) : IOException(message);

/// <summary>The archive (or one of its entries) is encrypted by the game and no keys were supplied.</summary>
public sealed class RpfEncryptedException(string message) : RpfFormatException(message);

/// <summary>How a file entry is stored in an RPF7 archive.</summary>
public enum RpfEntryKind
{
    /// <summary>RSC7 resource: header verbatim + DEFLATE body; a/b = system/graphics flags.</summary>
    Resource,
    /// <summary>Binary, raw-DEFLATE compressed; a = uncompressed size.</summary>
    Binary,
    /// <summary>Stored uncompressed (nested archive, or a binary too big for the u24 size field).</summary>
    Raw,
}

/// <summary>
/// RPF7 (OPEN / unencrypted) format primitives, verified against R*-produced and
/// OpenIV/CodeWalker-produced archives.
/// <code>
/// Header (16 bytes, LE): u32 magic 0x52504637 ("7FPR") | u32 entryCount | u32 namesLength | u32 0x4E45504F ("OPEN")
/// TOC: entryCount x 16 bytes.
///   Directory: u32 nameOffset | u32 0x7FFFFF00 | u32 firstChildIndex | u32 childCount
///   File:      u16 nameOffset | u24 size | u24 sector offset (top bit of byte 7 = resource) | u32 a | u32 b
///     resource: a/b = RSC7 system/graphics flags; binary: a = uncompressed size, b = 0 (encryption)
/// Name table: leading NUL (root), then NUL-terminated names, padded to 16.
/// Children of every directory are contiguous and sorted ordinally (CodeWalker's CompareOrdinal).
/// Header + TOC + names are padded to a 512-byte sector; each file is 512-aligned.
/// </code>
/// Large entries: a resource whose on-disk length is &gt;= 0xFFFFFF carries the 0xFFFFFF
/// marker in the TOC and its real length in bytes 2/5/7/14 of its archived header; a
/// binary whose DEFLATE output exceeds 0xFFFFFF is stored raw.
/// </summary>
public static class Rpf7
{
    public const uint Magic = 0x52504637;
    public const uint EncOpen = 0x4E45504F;
    public const uint DirMarker = 0x7FFFFF00;
    public const int Sector = 512;
    public const uint Rsc7Magic = 0x37435352;
    public const int BigSize = 0xFFFFFF;
    public const long MaxBlock = 0x7FFFFF;

    public static readonly string[] ResourceExts = [".ydr", ".ytd", ".ydd", ".yft"];

    public static bool IsResourceExt(string ext) => ResourceExts.Contains(ext.ToLowerInvariant());

    /// <summary>
    /// Streamed file types the game reads only as RSC7 resources (models, collisions, clips, particles, paths,
    /// placements, archetypes): one that isn't stops the game's streaming at loading (ERR_STR_PACK).
    /// </summary>
    public static readonly string[] StreamedResourceExts = [.. ResourceExts, ".ybn", ".ycd", ".ypt", ".ynv", ".ynd", ".ymap", ".ytyp", ".yld"];

    public static bool MustBeResource(string ext) => StreamedResourceExts.Contains(ext.ToLowerInvariant());

    public static long Align(long n, long a) => (n + a - 1) / a * a;

    // ------------------------------------------------------------- DEFLATE

    /// <summary>Raw DEFLATE (no zlib header), level 9 — CodeWalker's CompressBytes format.</summary>
    public static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.Length / 2 + 64);
        using (var ds = new DeflateStream(ms, new ZLibCompressionOptions
               {
                   CompressionLevel = 9,
                   CompressionStrategy = ZLibCompressionStrategy.Default,
               }, leaveOpen: true))
        {
            ds.Write(data);
        }
        return ms.ToArray();
    }

    /// <summary>Raw DEFLATE decompress.</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var ds = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        ds.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Raw DEFLATE decompress from an array segment without copying the input.</summary>
    public static byte[] Inflate(byte[] data, int offset, int count)
    {
        using var input = new MemoryStream(data, offset, count, writable: false);
        using var ds = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        ds.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Length a raw DEFLATE stream inflates to, without keeping the output. Stops
    /// counting once <paramref name="stopAfter"/> is exceeded (the result is then
    /// just "more than that"). Throws <see cref="InvalidDataException"/> on corrupt data.
    /// </summary>
    public static long InflatedLength(Stream compressed, long stopAfter = long.MaxValue)
    {
        using var ds = new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        var buf = new byte[1 << 16];
        long total = 0;
        int n;
        while ((n = ds.Read(buf, 0, buf.Length)) > 0)
        {
            total += n;
            if (total > stopAfter) break;
        }
        return total;
    }

    public static long InflatedLength(byte[] data, int offset, int count, long stopAfter = long.MaxValue)
    {
        using var ms = new MemoryStream(data, offset, count, writable: false);
        return InflatedLength(ms, stopAfter);
    }

    // ----------------------------------------------------------- resources

    /// <summary>
    /// Virtual (page-padded) size encoded in an RSC7 System/Graphics flags word —
    /// CodeWalker's RpfResourceFileEntry.GetSizeFromFlags.
    /// </summary>
    public static long ResVirtualSize(uint flags)
    {
        long s0 = ((flags >> 27) & 0x1) << 0;
        long s1 = ((flags >> 26) & 0x1) << 1;
        long s2 = ((flags >> 25) & 0x1) << 2;
        long s3 = ((flags >> 24) & 0x1) << 3;
        long s4 = ((flags >> 17) & 0x7F) << 4;
        long s5 = ((flags >> 11) & 0x3F) << 5;
        long s6 = ((flags >> 7) & 0xF) << 6;
        long s7 = ((flags >> 5) & 0x3) << 7;
        long s8 = ((flags >> 4) & 0x1) << 8;
        long baseSize = 0x200L << (int)(flags & 0xF);
        return baseSize * (s0 + s1 + s2 + s3 + s4 + s5 + s6 + s7 + s8);
    }

    /// <summary>
    /// On-disk payload for an RSC7 resource: the 16-byte header verbatim, followed by
    /// the page image compressed EXACTLY ONCE.
    /// <para>
    /// Loose .ydr/.ytd files exported by CodeWalker/OpenIV already hold a DEFLATE body
    /// that inflates to exactly <c>virtual</c> bytes; that body must be stored verbatim.
    /// Compressing it again (the double-compression bug) leaves the game's page buffer
    /// full of still-compressed garbage: streaming stops and the game crashes. A raw
    /// page image is exactly <c>virtual</c> bytes long, so the length guard runs before
    /// any inflate attempt and a raw image can never be mistaken for a compressed one.
    /// </para>
    /// </summary>
    public static byte[] ResourceBlob(byte[] raw, uint sysf, uint gfxf)
    {
        long virt = ResVirtualSize(sysf) + ResVirtualSize(gfxf);
        int bodyLen = raw.Length - 16;
        if (bodyLen < virt)
        {
            try
            {
                if (InflatedLength(raw, 16, bodyLen, stopAfter: virt) == virt)
                    return raw;                         // already compressed -> verbatim
            }
            catch (InvalidDataException) { }
        }
        var body = Deflate(raw.AsSpan(16));
        var blob = new byte[16 + body.Length];
        Buffer.BlockCopy(raw, 0, blob, 0, 16);
        Buffer.BlockCopy(body, 0, blob, 16, body.Length);
        return blob;
    }

    /// <summary>(systemFlags, graphicsFlags) from an RSC7 header.</summary>
    public static (uint Sys, uint Gfx) ReadRsc7Flags(ReadOnlySpan<byte> header, string name)
    {
        if (header.Length < 16 ||
            !(header[..4].SequenceEqual("RSC7"u8) || header[..4].SequenceEqual("7CSR"u8)))
        {
            var sig = header.Length >= 4 ? Convert.ToHexString(header[..4]) : "<short>";
            throw new RpfFormatException($"{name}: not an RSC7 resource (signature 0x{sig})");
        }
        return (BinaryPrimitives.ReadUInt32LittleEndian(header[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[12..]));
    }

    public static (uint Sys, uint Gfx) ReadRsc7Flags(string path)
    {
        Span<byte> hdr = stackalloc byte[16];
        using var fs = File.OpenRead(path);
        int n = fs.ReadAtLeast(hdr, 16, throwOnEndOfStream: false);
        return ReadRsc7Flags(hdr[..n], Path.GetFileName(path));
    }

    /// <summary>Canonical RSC7 header rebuilt from TOC flags (CodeWalker GetVersionFromFlags).</summary>
    public static byte[] Rsc7Header(uint sysf, uint gfxf)
    {
        uint ver = (((sysf >> 28) & 0xF) << 4) | ((gfxf >> 28) & 0xF);
        var h = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(h, Rsc7Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), ver);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), sysf);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), gfxf);
        return h;
    }

    /// <summary>
    /// For a resource blob of &gt;= 0xFFFFFF bytes write its real length into header
    /// bytes 7/14/5/2 (the TOC then carries the 0xFFFFFF marker). Modifies in place.
    /// </summary>
    public static byte[] StampBigSize(byte[] blob)
    {
        long n = blob.Length;
        if (n < BigSize) return blob;
        blob[7] = (byte)(n & 0xFF);
        blob[14] = (byte)((n >> 8) & 0xFF);
        blob[5] = (byte)((n >> 16) & 0xFF);
        blob[2] = (byte)((n >> 24) & 0xFF);
        return blob;
    }

    /// <summary>16-byte TOC record of a file entry stored at byte <paramref name="offset"/>.</summary>
    public static byte[] FileRecord(int nameOffset, long offset, RpfEntryKind kind, long length, uint a, uint b)
    {
        long block = offset / Sector;
        if (block > MaxBlock)
            throw new InvalidOperationException(
                $"archive exceeds the RPF7 4 GiB offset limit (sector 0x{block:x})");
        if (nameOffset > 0xFFFF)
            throw new InvalidOperationException(
                "archive name table exceeds 64 KiB — too many files for one RPF7 directory level");
        long size = kind switch
        {
            RpfEntryKind.Resource => Math.Min(length, BigSize),
            RpfEntryKind.Binary => length,
            _ => 0,
        };
        var rec = new byte[16];
        rec[0] = (byte)(nameOffset & 0xFF);
        rec[1] = (byte)((nameOffset >> 8) & 0xFF);
        rec[2] = (byte)(size & 0xFF);
        rec[3] = (byte)((size >> 8) & 0xFF);
        rec[4] = (byte)((size >> 16) & 0xFF);
        rec[5] = (byte)(block & 0xFF);
        rec[6] = (byte)((block >> 8) & 0xFF);
        rec[7] = (byte)((int)((block >> 16) & 0x7F) | (kind == RpfEntryKind.Resource ? 0x80 : 0x00));
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), a);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(12), b);
        return rec;
    }

    /// <summary>Directory TOC record.</summary>
    public static byte[] DirRecord(uint nameOffset, uint first, uint count)
    {
        var rec = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(rec, nameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), DirMarker);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), first);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(12), count);
        return rec;
    }

    /// <summary>Header (16 bytes) of an archive.</summary>
    public static byte[] Header(int count, int namesLen)
    {
        var h = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(h, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), (uint)namesLen);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), EncOpen);
        return h;
    }

    /// <summary>Encode an entry name. Like Python's <c>str.encode("latin1")</c> this refuses
    /// characters outside Latin-1 instead of silently mangling them.</summary>
    public static byte[] EncodeName(string name)
    {
        var b = new byte[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c > 0xFF)
                throw new InvalidOperationException(
                    $"File name '{name}' contains characters that cannot be stored in an RPF archive " +
                    "(only Latin letters, digits and basic symbols are allowed).");
            b[i] = (byte)c;
        }
        return b;
    }

    public static string DecodeName(ReadOnlySpan<byte> names, int offset)
    {
        if (offset < 0 || offset >= names.Length) return "";
        int end = names[offset..].IndexOf((byte)0);
        var slice = end < 0 ? names[offset..] : names.Slice(offset, end);
        var chars = new char[slice.Length];
        for (int i = 0; i < slice.Length; i++) chars[i] = (char)slice[i];
        return new string(chars);
    }
}
