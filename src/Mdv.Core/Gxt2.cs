using System.Buffers.Binary;
using System.Text;

namespace Mdv.Core;

/// <summary>
/// Stage 5: compile GTA V <c>.gxt2</c> text tables.
/// <code>
/// u32 magic = 0x47585432 ("2TXG" on disk)
/// u32 count
/// count x { u32 joaatHash ; u32 absoluteStringOffset }
/// u32 magic
/// u32 endOffset       (absolute end-of-file = header + string table)
/// string data         NUL-terminated UTF-8
/// </code>
/// Entries are sorted by hash ascending (the game binary-searches).
/// </summary>
public static class Gxt2
{
    public const uint Magic = 0x47585432;

    /// <summary>Jenkins one-at-a-time hash over the (lowercased) UTF-8 key.</summary>
    public static uint Joaat(string s, bool lowercase = true)
    {
        if (lowercase) s = s.ToLowerInvariant();
        uint h = 0;
        foreach (var ch in Encoding.UTF8.GetBytes(s))
        {
            h += ch;
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }

    /// <summary>
    /// Build a .gxt2 from named labels plus optional already-hashed entries (lifted out
    /// of someone else's compiled table). Named labels win on a hash collision.
    /// </summary>
    public static byte[] Build(IReadOnlyDictionary<string, string> labels,
                               IReadOnlyDictionary<uint, string>? hashed = null)
    {
        var byHash = new SortedDictionary<uint, string>();
        if (hashed is not null)
            foreach (var (h, t) in hashed) byHash[h] = t;
        foreach (var (key, text) in labels) byHash[Joaat(key)] = text;

        int count = byHash.Count;
        int headerSize = 4 + 4 + count * 8 + 4 + 4;
        var data = new MemoryStream();
        var offsets = new List<int>(count);
        foreach (var (_, text) in byHash)
        {
            offsets.Add(headerSize + (int)data.Length);
            var b = Encoding.UTF8.GetBytes(text);
            data.Write(b);
            data.WriteByte(0);
        }

        var outBuf = new byte[headerSize + data.Length];
        var span = outBuf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)count);
        int pos = 8, i = 0;
        foreach (var (h, _) in byHash)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[pos..], h);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(pos + 4)..], (uint)offsets[i++]);
            pos += 8;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(span[pos..], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(pos + 4)..], (uint)(headerSize + data.Length));
        data.ToArray().CopyTo(outBuf, headerSize);
        return outBuf;
    }

    /// <summary>Parse a .gxt2 back into {hash: text}.</summary>
    public static Dictionary<uint, string> Read(ReadOnlySpan<byte> blob)
    {
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        if (magic != Magic) throw new InvalidDataException($"bad magic 0x{magic:x}");
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]);
        int pos = 8;
        var entries = new List<(uint h, int off)>(count);
        for (int i = 0; i < count; i++)
        {
            entries.Add((BinaryPrimitives.ReadUInt32LittleEndian(blob[pos..]),
                         (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[(pos + 4)..])));
            pos += 8;
        }
        uint magic2 = BinaryPrimitives.ReadUInt32LittleEndian(blob[pos..]);
        if (magic2 != Magic) throw new InvalidDataException("bad footer magic");
        int endPos = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[(pos + 4)..]);
        if (endPos != blob.Length)
            throw new InvalidDataException(
                $"bad footer dataLength/endpos: {endPos} != actual file size {blob.Length}");

        var result = new Dictionary<uint, string>();
        foreach (var (h, off) in entries)
        {
            int rel = blob[off..endPos].IndexOf((byte)0);
            if (rel < 0) throw new InvalidDataException("unterminated string in gxt2");
            result[h] = new UTF8Encoding(false, true).GetString(blob.Slice(off, rel));
        }
        return result;
    }
}
