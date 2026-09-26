using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using SkiaSharp;

namespace Mdv.Core.Textures;

/// <summary>
/// Pictures a mod brings (.dds, or .png / .jpg / .bmp / .webp) as game textures. A DDS goes in as it is;
/// other images are block-compressed (BCnEncoder.NET) like the texture they replace — DXT1 / DXT5 / BC7 —
/// with a full chain of mip levels down to 4 px, so they look right at every distance.
/// </summary>
public static class TextureImages
{
    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };

    /// <summary>Everything that can become a texture.</summary>
    public static bool IsTexture(string path) => ImageExt.Contains(Path.GetExtension(path)) ||
                                                 Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase);

    /// <summary>The largest edge ModDrop V makes a texture from an image (the game's own liveries are 1–2K).</summary>
    public const int MaxEdge = 4096;

    /// <summary>
    /// A texture from <paramref name="path"/>. An image is compressed to <paramref name="like"/>'s format when that is a
    /// block format (DXT1 turns DXT5 when the image has transparency), else DXT5 / DXT1 by its transparency.
    /// </summary>
    public static Texture ToTexture(string path, TextureFormat? like = null)
    {
        if (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
            return DDSIO.GetTexture(File.ReadAllBytes(path)) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a DDS file ModDrop V can read.");
        var (rgba, w, h) = DecodeRgba(path);
        bool alpha = HasAlpha(rgba);
        var format = like switch
        {
            TextureFormat.D3DFMT_BC7 => CompressionFormat.Bc7,
            TextureFormat.D3DFMT_DXT1 when !alpha => CompressionFormat.Bc1,
            TextureFormat.D3DFMT_DXT1 or TextureFormat.D3DFMT_DXT3 or TextureFormat.D3DFMT_DXT5 => CompressionFormat.Bc3,
            _ => alpha ? CompressionFormat.Bc3 : CompressionFormat.Bc1,
        };
        return Encode(rgba, w, h, format);
    }

    internal static Texture Encode(byte[] rgba, int w, int h, CompressionFormat format)
    {
        var encoder = new BcEncoder(format);
        encoder.OutputOptions.GenerateMipMaps = true;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        var mips = encoder.EncodeToRawBytes(rgba, w, h, PixelFormat.Rgba32);
        // the game's own textures stop at 4 px — smaller mips are only wasted blocks
        int levels = 1;
        while (levels < mips.Length && Math.Min(w >> levels, h >> levels) >= 4) levels++;
        var data = mips.Take(levels).SelectMany(m => m).ToArray();

        var tf = format switch
        {
            CompressionFormat.Bc1 => TextureFormat.D3DFMT_DXT1,
            CompressionFormat.Bc7 => TextureFormat.D3DFMT_BC7,
            _ => TextureFormat.D3DFMT_DXT5,
        };
        DDSIO.DXTex.ComputePitch(DDSIO.GetDXGIFormat(tf), w, h, out int rowPitch, out _, 0);
        return new Texture
        {
            Width = (ushort)w, Height = (ushort)h, Depth = 1, Levels = (byte)levels, Format = tf, Stride = (ushort)rowPitch,
            Data = new TextureData { FullData = data },
        };
    }

    /// <summary>
    /// An image as RGBA bytes, its size rounded up to a multiple of 4 (block compression needs whole blocks) and
    /// brought down to <see cref="MaxEdge"/>.
    /// </summary>
    public static (byte[] Rgba, int Width, int Height) DecodeRgba(string path)
    {
        using var src = SKBitmap.Decode(path) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not an image ModDrop V can read.");
        int w = src.Width, h = src.Height;
        double scale = Math.Min(1.0, (double)MaxEdge / Math.Max(w, h));
        int tw = Round4((int)Math.Round(w * scale)), th = Round4((int)Math.Round(h * scale));
        var info = new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var dst = new SKBitmap(info);
        if (tw == w && th == h)
        {
            using var canvas = new SKCanvas(dst);
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(src, 0, 0, paint);
        }
        else if (!src.ScalePixels(dst, new SKSamplingOptions(SKCubicResampler.Mitchell)))
            throw new InvalidDataException($"{Path.GetFileName(path)} could not be resized.");
        return (dst.Bytes, tw, th);
    }

    private static int Round4(int v) => Math.Max(4, (v + 3) & ~3);

    private static bool HasAlpha(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] < 250) return true;
        return false;
    }

    /// <summary>The pixels of an image or DDS file for a preview (null when it can't be read).</summary>
    public static TexturePixels? Preview(string path, int maxEdge = 512)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
                return DDSIO.GetTexture(File.ReadAllBytes(path)) is { } t ? Ytd.Pixels(t, maxEdge) : null;
            using var src = SKBitmap.Decode(path);
            if (src is null) return null;
            double scale = Math.Min(1.0, (double)maxEdge / Math.Max(src.Width, src.Height));
            int w = Math.Max(1, (int)(src.Width * scale)), h = Math.Max(1, (int)(src.Height * scale));
            using var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            if (!src.ScalePixels(dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))) return null;
            var px = new uint[w * h];
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst.Bytes.AsSpan()).CopyTo(px);
            return new TexturePixels(px, w, h);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
