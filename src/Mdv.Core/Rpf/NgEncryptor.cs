using CodeWalker.GameFiles;

namespace Mdv.Core.Rpf;

/// <summary>
/// NG encryption straight from the game's decrypt tables. A decrypt round makes each 32-bit word as
/// <c>T[a][x_a] ^ T[b][x_b] ^ T[c][x_c] ^ T[d][x_d] ^ key</c>; every table is one-to-one onto its own 8-dimensional
/// subspace (plus a constant) and the four subspaces add up to all 32 bits. So a word splits back uniquely: project it
/// onto the four subspaces (one 32×32 GF(2) matrix) and look each part up in the table's inverse. That takes
/// milliseconds, where CodeWalker's encrypt tables take minutes to compute and aren't loaded from the executable.
/// </summary>
internal sealed class NgEncryptor
{
    // the input bytes behind each output word: rounds 0, 1, 16 — and 2…15
    private static readonly int[][] GroupsA = [[0, 1, 2, 3], [4, 5, 6, 7], [8, 9, 10, 11], [12, 13, 14, 15]];
    private static readonly int[][] GroupsB = [[0, 7, 10, 13], [1, 4, 11, 14], [2, 5, 8, 15], [3, 6, 9, 12]];

    private sealed class Word
    {
        public required int[] Inputs;              // byte positions of the round's input
        public uint Constant;                      // XOR of the four tables' entry 0
        public readonly uint[] Project = new uint[32];   // row i: which coordinates (bits of 4×8) bit i of the word adds
        public readonly uint[][] Basis = new uint[4][];  // the 8 basis vectors of each table's subspace
        public readonly Dictionary<uint, byte>[] Inverse = new Dictionary<uint, byte>[4];
    }

    private readonly Word[][] _rounds;

    private NgEncryptor(uint[][][] tables)
    {
        _rounds = new Word[17][];
        for (int r = 0; r < 17; r++)
        {
            var groups = r is < 2 or 16 ? GroupsA : GroupsB;
            _rounds[r] = groups.Select(g => Build(tables[r], g)).ToArray();
        }
    }

    private static NgEncryptor? _instance;
    private static readonly Lock Gate = new();

    /// <summary>Built from the NG decrypt tables loaded by <see cref="GameCrypto.ForGame"/>.</summary>
    public static NgEncryptor Instance
    {
        get
        {
            lock (Gate)
                return _instance ??= new NgEncryptor(GTA5Keys.PC_NG_DECRYPT_TABLES
                                                     ?? throw new InvalidOperationException("The game's NG tables are not loaded."));
        }
    }

    private static Word Build(uint[][] tables, int[] inputs)
    {
        var w = new Word { Inputs = inputs };
        var columns = new uint[32];                        // the basis vectors, 8 per table, as the matrix columns
        for (int k = 0; k < 4; k++)
        {
            var t = tables[inputs[k]];
            w.Constant ^= t[0];
            w.Inverse[k] = new Dictionary<uint, byte>(256);
            for (int x = 0; x < 256; x++)
                if (!w.Inverse[k].TryAdd(t[x] ^ t[0], (byte)x))
                    throw new InvalidDataException("An NG table is not one-to-one.");
            w.Basis[k] = SpanBasis(Enumerable.Range(0, 256).Select(x => t[x] ^ t[0]));
            if (w.Basis[k].Length != 8) throw new InvalidDataException("An NG table doesn't span 8 bits.");
            w.Basis[k].CopyTo(columns, 8 * k);
        }
        // invert the matrix whose column j is columns[j]: Project[i] then gives, for word bit i, the coordinates it flips
        var rows = new ulong[32];                          // augmented [A | I], row i = bit i of every column
        for (int i = 0; i < 32; i++)
        {
            ulong row = 0;
            for (int j = 0; j < 32; j++)
                if ((columns[j] >> i & 1) != 0) row |= 1UL << j;
            rows[i] = row | (1UL << (32 + i));
        }
        for (int c = 0; c < 32; c++)
        {
            int p = Array.FindIndex(rows, c, r => (r >> c & 1) != 0);
            if (p < 0) throw new InvalidDataException("The NG tables' subspaces don't cover the word.");
            (rows[c], rows[p]) = (rows[p], rows[c]);
            for (int i = 0; i < 32; i++)
                if (i != c && (rows[i] >> c & 1) != 0) rows[i] ^= rows[c];
        }
        // rows[c] = coordinate c as a combination of word bits; turn it around: word bit i → coordinates
        for (int c = 0; c < 32; c++)
            for (int i = 0; i < 32; i++)
                if ((rows[c] >> (32 + i) & 1) != 0) w.Project[i] |= 1u << c;
        return w;
    }

    private static uint[] SpanBasis(IEnumerable<uint> values)
    {
        var pivots = new uint[32];
        var basis = new List<uint>();
        foreach (var v0 in values)
        {
            var v = v0;
            for (int b = 31; b >= 0 && v != 0; b--)
            {
                if ((v >> b & 1) == 0) continue;
                if (pivots[b] == 0)
                {
                    pivots[b] = v;
                    basis.Add(v0);
                    break;
                }
                v ^= pivots[b];
            }
        }
        return [.. basis];
    }

    /// <summary>Encrypt as the game decrypts with <paramref name="key"/> (272 bytes: 17 round keys); a tail under 16 bytes stays plain.</summary>
    public byte[] Encrypt(byte[] data, byte[] key)
    {
        var k = new uint[key.Length / 4];
        Buffer.BlockCopy(key, 0, k, 0, k.Length * 4);
        var result = (byte[])data.Clone();
        var block = new byte[16];
        var prev = new byte[16];
        for (int at = 0; at + 16 <= data.Length; at += 16)
        {
            Array.Copy(data, at, block, 0, 16);
            for (int r = 16; r >= 0; r--)
            {
                (block, prev) = (prev, block);
                Undo(r, prev, block, k);
            }
            Array.Copy(block, 0, result, at, 16);
        }
        return result;
    }

    /// <summary>The input of decrypt round <paramref name="r"/> that gives <paramref name="output"/>.</summary>
    private void Undo(int r, byte[] output, byte[] input, uint[] key)
    {
        for (int wi = 0; wi < 4; wi++)
        {
            var w = _rounds[r][wi];
            uint y = BitConverter.ToUInt32(output, 4 * wi) ^ key[4 * r + wi] ^ w.Constant;
            uint coords = 0;
            for (int i = 0; i < 32; i++)
                if ((y >> i & 1) != 0) coords ^= w.Project[i];
            for (int t = 0; t < 4; t++)
            {
                uint part = 0;
                for (int j = 0; j < 8; j++)
                    if ((coords >> (8 * t + j) & 1) != 0) part ^= w.Basis[t][j];
                input[w.Inputs[t]] = w.Inverse[t][part];
            }
        }
    }
}
