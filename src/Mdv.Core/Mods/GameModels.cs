using System.IO.Compression;

namespace Mdv.Core.Mods;

/// <summary>
/// The names of the game's own streamed assets — models (.ydr / .yft), texture dictionaries (.ytd) and archetype
/// files (.ytyp), base game and DLCs — as the hashes the game looks them up by (data/game_models.bin). An add-on
/// prop, its textures or its .ytyp named like one of them would take the game's place.
/// </summary>
public sealed class GameModels
{
    public static readonly string FileName = "game_models.bin";

    private readonly HashSet<uint> _hashes;

    public GameModels(IEnumerable<uint> hashes) => _hashes = [.. hashes];

    public int Count => _hashes.Count;

    /// <summary>The game has an asset of this name (without extension; case doesn't matter).</summary>
    public bool Has(string name) => _hashes.Contains(Gxt2.Joaat(name));

    private static readonly Dictionary<string, GameModels> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The list shipped in <paramref name="dataDir"/>; empty when it's missing.</summary>
    public static GameModels Load(string? dataDir)
    {
        var path = Path.Combine(dataDir ?? DependencyCatalog.DefaultDataDir, FileName);
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var m)) return m;
            try
            {
                m = File.Exists(path) ? Read(File.ReadAllBytes(path)) : new([]);
            }
            catch (InvalidDataException)
            {
                m = new([]);
            }
            return Cache[path] = m;
        }
    }

    /// <summary>The file: the sorted hashes as gaps between them (LEB128), gzipped.</summary>
    public static byte[] Write(IEnumerable<uint> hashes)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            uint last = 0;
            foreach (var h in hashes.Distinct().Order())
            {
                for (uint gap = h - last; ; gap >>= 7)
                {
                    if (gap < 0x80)
                    {
                        gz.WriteByte((byte)gap);
                        break;
                    }
                    gz.WriteByte((byte)(gap & 0x7F | 0x80));
                }
                last = h;
            }
        }
        return ms.ToArray();
    }

    public static GameModels Read(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        gz.CopyTo(raw);
        var hashes = new List<uint>();
        uint last = 0, gap = 0;
        int shift = 0;
        foreach (var b in raw.GetBuffer().AsSpan(0, (int)raw.Length))
        {
            gap |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) != 0)
            {
                shift += 7;
                if (shift > 28) throw new InvalidDataException("game_models.bin is damaged.");
                continue;
            }
            hashes.Add(last += gap);
            gap = 0;
            shift = 0;
        }
        return new GameModels(hashes);
    }
}
