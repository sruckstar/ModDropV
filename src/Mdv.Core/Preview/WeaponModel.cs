using System.Numerics;

namespace Mdv.Core.Preview;

/// <summary>
/// A decoded texture with its mip chain, ready for software sampling. Texels are packed
/// 0xAARRGGBB (BGRA bytes in memory), straight alpha.
/// </summary>
public sealed class PreviewTexture
{
    public PreviewTexture(int width, int height, uint[] level0)
    {
        var levels = new List<uint[]> { level0 };
        var widths = new List<int> { width };
        var heights = new List<int> { height };
        int w = width, h = height;
        var src = level0;
        while (w > 1 || h > 1)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var dst = new uint[nw * nh];
            for (int y = 0; y < nh; y++)
            {
                int y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1);
                    dst[y * nw + x] = Average(src[y0 * w + x0], src[y0 * w + x1], src[y1 * w + x0], src[y1 * w + x1]);
                }
            }
            levels.Add(dst);
            widths.Add(nw);
            heights.Add(nh);
            src = dst;
            w = nw;
            h = nh;
        }
        Width = width;
        Height = height;
        Levels = levels.ToArray();
        LevelWidths = widths.ToArray();
        LevelHeights = heights.ToArray();
        foreach (var t in level0)
        {
            if (t >> 24 >= 250) continue;
            HasAlpha = true;
            break;
        }
    }

    public int Width { get; }
    public int Height { get; }
    public uint[][] Levels { get; }
    public int[] LevelWidths { get; }
    public int[] LevelHeights { get; }
    /// <summary>Some texels are (partly) transparent: cut-outs and decals need them.</summary>
    public bool HasAlpha { get; }

    private static uint Average(uint a, uint b, uint c, uint d)
    {
        uint Ch(int shift) => (((a >> shift) & 0xFF) + ((b >> shift) & 0xFF) + ((c >> shift) & 0xFF) + ((d >> shift) & 0xFF) + 2) / 4;
        return (Ch(24) << 24) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }
}

/// <summary>
/// A texture recoloured by a weapon tint palette (the *_dpal of the palette shaders): the
/// diffuse alpha picks a palette column (alpha − 32 on a 128-wide palette), the tint picks
/// the row, and the colour is multiplied by it — as the game and CodeWalker's BasicPS do.
/// Each tint is decoded on first use; a few recent ones stay cached.
/// </summary>
public sealed class TintedTexture
{
    /// <summary>
    /// A palette holds 32 tint rows however tall it is drawn: the game's own are 128×32,
    /// packs often upscale them (512×128 — a tint is then a band of 4 rows).
    /// </summary>
    public const int PaletteRows = 32;
    private const int KeepDecoded = 4;

    private readonly uint[] _texels;
    private readonly int _width, _height;
    private readonly uint[] _palette;
    private readonly int _palWidth, _palHeight;
    private readonly Dictionary<int, PreviewTexture> _decoded = [];
    private readonly List<int> _recent = [];
    private readonly Lock _gate = new();
    private int[]? _columnUse;

    public TintedTexture(uint[] texels, int width, int height, uint[] palette, int palWidth, int palHeight)
    {
        _texels = texels;
        _width = width;
        _height = height;
        _palette = palette;
        _palWidth = palWidth;
        _palHeight = palHeight;
    }

    /// <summary>The texture in tint <paramref name="tint"/> (0 = the default).</summary>
    public PreviewTexture Get(int tint)
    {
        tint = Math.Clamp(tint, 0, PaletteRows - 1);
        lock (_gate)
        {
            if (!_decoded.TryGetValue(tint, out var t))
            {
                var px = (uint[])_texels.Clone();
                int row = Row(tint) * _palWidth;
                for (int i = 0; i < px.Length; i++)
                {
                    uint c = px[i];
                    uint p = _palette[row + PaletteColumn(c)];
                    uint r = ((c >> 16) & 0xFF) * ((p >> 16) & 0xFF) / 255;
                    uint g = ((c >> 8) & 0xFF) * ((p >> 8) & 0xFF) / 255;
                    uint b = (c & 0xFF) * (p & 0xFF) / 255;
                    px[i] = 0xFF000000u | (r << 16) | (g << 8) | b;
                }
                _decoded[tint] = t = new PreviewTexture(_width, _height, px);
            }
            // the default tint stays; of the others only the last few
            _recent.Remove(tint);
            _recent.Add(tint);
            while (_recent.Count > KeepDecoded)
            {
                if (_recent[0] != 0) _decoded.Remove(_recent[0]);
                _recent.RemoveAt(0);
            }
            return t;
        }
    }

    /// <summary>The palette colour (0xAARRGGBB) of <paramref name="column"/> (0‥127) in a tint.</summary>
    public uint PaletteColor(int tint, int column) =>
        _palette[Row(Math.Clamp(tint, 0, PaletteRows - 1)) * _palWidth + Math.Clamp(column, 0, 127) * _palWidth / 128];

    /// <summary>How many texels use each of the 128 palette columns.</summary>
    public int[] ColumnUse()
    {
        lock (_gate)
        {
            if (_columnUse is not null) return _columnUse;
            var use = new int[128];
            foreach (var c in _texels) use[Math.Clamp((int)(c >> 24) - 32, 0, 127)]++;
            return _columnUse = use;
        }
    }

    public int TexelCount => _texels.Length;

    private int Row(int tint) => Math.Min(_palHeight - 1, (int)((tint + 0.5f) * _palHeight / PaletteRows));

    private int PaletteColumn(uint c) => Math.Min(_palWidth - 1, Math.Clamp((int)(c >> 24) - 32, 0, 127) * _palWidth / 128);
}

/// <summary>A weapon tint — what SET_PED_WEAPON_TINT_INDEX picks: a row of the tint palette.</summary>
/// <param name="Swatch">The colour the tint gives the weapon's main surface (0xAARRGGBB).</param>
public sealed record WeaponTint(int Index, string Name, uint Swatch);

/// <summary>How a surface is composited.</summary>
public enum SurfaceKind
{
    /// <summary>Solid; texels at or below ~1/3 alpha are cut out, as the game does.</summary>
    Opaque,
    /// <summary>Alpha-blended over what is behind it (markings, stickers, glass).</summary>
    Decal,
}

/// <summary>One geometry of a model in weapon space: flat arrays, triangle list.</summary>
public sealed class PreviewMesh
{
    public required float[] Positions { get; init; }   // x, y, z per vertex
    public required float[] Normals { get; init; }     // x, y, z per vertex, unit length
    public required float[] Uvs { get; init; }         // u, v per vertex
    public required int[] Indices { get; init; }
    /// <summary>The texture in the default tint.</summary>
    public PreviewTexture? Texture { get; init; }
    /// <summary>Set when a tint palette colours the texture.</summary>
    public TintedTexture? Tinted { get; init; }
    public SurfaceKind Kind { get; init; }
    public PreviewTexture? TextureFor(int tint) => tint > 0 && Tinted is { } t ? t.Get(tint) : Texture;
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>A model file shown in the preview: the weapon itself or one of its components.</summary>
public sealed class PreviewPiece
{
    /// <summary>The asset stem (w_pi_pistol_mag1) — unique within a model.</summary>
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>weapon / mag / supp / scope / flash / grip / barrel / attach / model.</summary>
    public required string Kind { get; init; }
    /// <summary>The weapon bone it hangs on (WAPClip…); a bone holds one component at a time.</summary>
    public string? Bone { get; init; }
    /// <summary>False when the weapon has no bone for it — it sits at the weapon's origin.</summary>
    public bool Attached { get; init; } = true;
    public bool DefaultVisible { get; init; }
    /// <summary>What the parts list says about it instead of where it hangs (vehicle bodies, ped components).</summary>
    public string? Note { get; set; }
    public List<PreviewMesh> Meshes { get; } = [];
    public Vector3 Min { get; set; } = new(float.MaxValue);
    public Vector3 Max { get; set; } = new(float.MinValue);
    public int Triangles => Meshes.Sum(m => m.TriangleCount);
    public bool IsEmpty => Meshes.Count == 0;
}

/// <summary>A weapon and its components, assembled in weapon space for the 3D preview.</summary>
public sealed class WeaponModel
{
    public required string Name { get; init; }
    public List<PreviewPiece> Pieces { get; } = [];
    public List<string> Warnings { get; } = [];
    /// <summary>The weapon's tints; empty when no palette colours it.</summary>
    public List<WeaponTint> Tints { get; } = [];
    public int TextureCount { get; set; }
    public int Triangles => Pieces.Sum(p => p.Triangles);

    /// <summary>Bounds of the given pieces (all of them when null); zero-size when empty.</summary>
    public (Vector3 Min, Vector3 Max) Bounds(IEnumerable<PreviewPiece>? pieces = null)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in pieces ?? Pieces)
        {
            if (p.IsEmpty) continue;
            min = Vector3.Min(min, p.Min);
            max = Vector3.Max(max, p.Max);
        }
        return min.X > max.X ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }
}
