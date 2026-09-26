using System.Runtime.InteropServices;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using Mdv.Core.Rpf;

namespace Mdv.Core.Textures;

/// <summary>One texture of a texture dictionary.</summary>
public sealed record TextureInfo(string Name, int Width, int Height, string Format, int Levels)
{
    public override string ToString() => $"{Name}  {Width}×{Height} {Format}";
}

/// <summary>BGRA pixels (one uint per pixel) for a preview.</summary>
public sealed record TexturePixels(uint[] Bgra, int Width, int Height);

/// <summary>
/// A texture to put into a dictionary under <paramref name="Name"/>: made from an image or a DDS file, or taken
/// from another dictionary (<paramref name="Source"/> a .ytd, <see cref="Inner"/> the texture in it).
/// </summary>
public sealed record TextureEdit(string Name, string Source)
{
    public string? Inner { get; init; }
}

/// <summary>
/// Texture dictionaries (.ytd) of either edition: list their textures, read one for a preview, and
/// replace / add textures — the dictionary is written back in the edition it came in (Legacy v13 /
/// Enhanced gen9 v5).
/// </summary>
public static class Ytd
{
    public static List<TextureInfo> List(byte[] ytd, string name = "file.ytd") =>
        [.. Textures(Load(ytd, name)).Select(Info).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];

    public static TextureInfo Info(Texture t) =>
        new(t.Name ?? $"0x{t.NameHash:x8}", t.Width, t.Height, FormatName(t.Format), t.Levels);

    /// <summary>"DXT1", "DXT5", "BC7", "A8R8G8B8".</summary>
    public static string FormatName(TextureFormat f) => f.ToString().Replace("D3DFMT_", "");

    /// <summary>The edition a dictionary was built for (null: a version ModDrop V doesn't know).</summary>
    public static GameEdition? EditionOf(byte[] ytd) => ResourceEditions.EditionOf(".ytd", ResourceEditions.Version(ytd));

    /// <summary>The pixels of a texture (its first mip no larger than <paramref name="maxEdge"/>), or null when it isn't there / can't be decoded.</summary>
    public static TexturePixels? Pixels(byte[] ytd, string texture, int maxEdge = 512, string name = "file.ytd")
    {
        var t = Textures(Load(ytd, name)).FirstOrDefault(x => string.Equals(x.Name, texture, StringComparison.OrdinalIgnoreCase));
        return t is null ? null : Pixels(t, maxEdge);
    }

    internal static TexturePixels? Pixels(Texture t, int maxEdge)
    {
        try
        {
            int mip = 0;
            while (mip + 1 < t.Levels && Math.Max(t.Width >> mip, t.Height >> mip) > maxEdge) mip++;
            int w = Math.Max(1, t.Width >> mip), h = Math.Max(1, t.Height >> mip);
            var bytes = DDSIO.GetPixels(t, mip);
            if (bytes is null || bytes.Length < w * h * 4) return null;
            return new TexturePixels(MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(0, w * h * 4)).ToArray(), w, h);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The dictionary with <paramref name="edits"/> applied: a texture of the same name is replaced (keeping how the
    /// game uses it), a new name is added. Images are compressed like the texture they replace (<see cref="TextureImages"/>).
    /// Returns the dictionary as a compressed resource of the edition it came in.
    /// </summary>
    public static byte[] Edit(byte[] ytd, IReadOnlyList<TextureEdit> edits, string name = "file.ytd", Action<string>? log = null)
    {
        var edition = EditionOf(ytd) ?? throw new InvalidDataException($"{name} is not a texture dictionary ModDrop V can edit (version {ResourceEditions.Version(ytd)}).");
        var file = Load(ytd, name);
        var dict = file.TextureDict ?? throw new InvalidDataException($"{name} holds no texture dictionary.");
        var textures = Textures(file).ToList();
        foreach (var edit in edits)
        {
            var old = textures.FirstOrDefault(t => string.Equals(t.Name, edit.Name, StringComparison.OrdinalIgnoreCase));
            var tex = edit.Inner is { } inner ? Take(edit.Source, inner, edition) : TextureImages.ToTexture(edit.Source, old?.Format);
            tex.Name = old?.Name ?? edit.Name;
            tex.NameHash = JenkHash.GenHash(tex.Name.ToLowerInvariant());
            if (old is not null)
            {
                tex.Usage = old.Usage;
                tex.UsageFlags = old.UsageFlags;
                tex.Unknown_32h = old.Unknown_32h;
                textures.Remove(old);
            }
            else tex.Usage = TextureUsage.DIFFUSE;
            textures.Add(tex);
            log?.Invoke($"    {name}: {(old is null ? "added" : "replaced")} {tex.Name} ({tex.Width}×{tex.Height} {FormatName(tex.Format)}, " +
                        $"from {Path.GetFileName(edit.Source)}).");
        }
        dict.BuildFromTextureList(textures);
        return ResourceEditions.Write(edition, file.Save);
    }

    /// <summary>A texture out of another dictionary on disk (a gen9 one can't go into a Legacy dictionary).</summary>
    private static Texture Take(string ytdPath, string texture, GameEdition into)
    {
        var raw = File.ReadAllBytes(ytdPath);
        var name = Path.GetFileName(ytdPath);
        if (EditionOf(raw) == GameEdition.Enhanced && into == GameEdition.Legacy)
            throw new InvalidOperationException($"{name} is a GTA V Enhanced (gen9) dictionary — its textures can't go into GTA V Legacy.");
        return Textures(Load(raw, name)).FirstOrDefault(t => string.Equals(t.Name, texture, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidDataException($"{name} has no texture {texture}.");
    }

    private static IEnumerable<Texture> Textures(YtdFile f) => f.TextureDict?.Textures?.data_items?.Where(t => t is not null) ?? [];

    private static YtdFile Load(byte[] ytd, string name)
    {
        try
        {
            return ResourceEditions.Read(ytd, name, (data, entry) =>
            {
                var f = new YtdFile();
                f.Load(data, entry);
                return f;
            });
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"{name} could not be read as a texture dictionary: {ex.Message}", ex);
        }
    }
}
