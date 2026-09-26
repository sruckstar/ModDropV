using System.Buffers.Binary;
using CodeWalker.GameFiles;

namespace Mdv.Core.Rpf;

/// <summary>
/// Legacy ↔ Enhanced (gen9) resource handling. The RPF container is identical for both
/// editions; the RSC7 resources inside are not: Enhanced drawables/texture dictionaries
/// use a different block layout and their own version numbers (CodeWalker's
/// <c>GetVersion(gen9)</c>). Legacy resources are converted with CodeWalker's gen9
/// writer; the reverse direction does not exist, so a gen9 model can't go into a
/// Legacy pack.
/// </summary>
public static class ResourceEditions
{
    // (legacy version, gen9 versions) per resource type — CodeWalker Y*File.GetVersion
    private static readonly Dictionary<string, (int Legacy, int[] Gen9)> Versions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ydr"] = (165, [159, 154]),
        [".ydd"] = (165, [159, 154]),
        [".ytd"] = (13, [5]),
        [".yft"] = (162, [171]),
    };

    /// <summary>CodeWalker's gen9 switch is a process-wide static — conversions run one at a time.</summary>
    private static readonly Lock Gate = new();

    public static int Version(ReadOnlySpan<byte> rsc7) =>
        rsc7.Length >= 8 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(rsc7[4..]) : -1;

    /// <summary>The edition a resource was built for, or null when its version isn't a known one.</summary>
    public static GameEdition? EditionOf(string ext, int version)
    {
        if (!Versions.TryGetValue(ext, out var v)) return null;
        if (version == v.Legacy) return GameEdition.Legacy;
        if (v.Gen9.Contains(version)) return GameEdition.Enhanced;
        return null;
    }

    public static bool NeedsConversion(string name, ReadOnlySpan<byte> rsc7, GameEdition target)
    {
        var ext = Path.GetExtension(name);
        if (!Versions.ContainsKey(ext)) return false;
        return EditionOf(ext, Version(rsc7)) != target;
    }

    /// <summary>
    /// A loose RSC7 resource (compressed or not) as the on-disk blob for
    /// <paramref name="target"/>: header + body compressed exactly once, converted to
    /// gen9 when an Enhanced pack gets a Legacy model.
    /// </summary>
    /// <exception cref="InvalidOperationException">a gen9 model for a Legacy pack, or a failed conversion</exception>
    public static byte[] ForEdition(byte[] raw, string name, GameEdition target)
    {
        var (sysf, gfxf) = Rpf7.ReadRsc7Flags(raw, name);
        var blob = Rpf7.ResourceBlob(raw, sysf, gfxf);
        var ext = Path.GetExtension(name);
        if (!Versions.TryGetValue(ext, out var known)) return blob;

        int version = Version(blob);
        var edition = EditionOf(ext, version);
        if (edition == target) return blob;

        if (target == GameEdition.Legacy)
        {
            if (edition == GameEdition.Enhanced)
                throw new InvalidOperationException(
                    $"{name} is a GTA V Enhanced (gen9) resource (version {version}) — GTA V Legacy " +
                    "can't load it, and gen9 models can't be converted back. Use the Legacy version of " +
                    "the mod, or build for GTA V Enhanced.");
            return blob;                                  // unknown version: ship untouched, as before
        }
        return ToGen9(blob, name, ext, version, known.Gen9);
    }

    private static byte[] ToGen9(byte[] blob, string name, string ext, int version, int[] gen9Versions)
    {
        byte[]? converted;
        lock (Gate)
        {
            bool was = RpfManager.IsGen9;
            RpfManager.IsGen9 = true;
            try
            {
                converted = ext.ToLowerInvariant() switch
                {
                    ".ytd" => Load(new YtdFile(), f => f.Load(blob), f => f.TextureDict, f => f.Save()),
                    ".ydr" => Load(new YdrFile(), f => f.Load(blob), f => f.Drawable, f => f.Save()),
                    ".ydd" => Load(new YddFile(), f => f.Load(blob), f => f.DrawableDict, f => f.Save()),
                    ".yft" => Load(new YftFile(), f => f.Load(blob), f => f.Fragment, f => f.Save()),
                    _ => null,
                };
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"{name}: conversion to GTA V Enhanced (gen9) failed — {ex.Message}", ex);
            }
            finally
            {
                RpfManager.IsGen9 = was;
            }
        }

        if (converted is null)
            throw new InvalidOperationException(
                $"{name}: CodeWalker could not read this resource (version {version}), so it can't be " +
                "converted for GTA V Enhanced. Re-export the model with CodeWalker/OpenIV and try again.");

        // CodeWalker's reader swallows parse errors in release builds — make sure the result
        // is a well-formed gen9 resource before it goes anywhere near the game.
        int outVersion = Version(converted);
        if (!gen9Versions.Contains(outVersion))
            throw new InvalidOperationException(
                $"{name}: gen9 conversion produced version {outVersion}, expected {gen9Versions[0]}.");
        var (sysf, gfxf) = Rpf7.ReadRsc7Flags(converted, name);
        long virt = Rpf7.ResVirtualSize(sysf) + Rpf7.ResVirtualSize(gfxf);
        long inflated = Rpf7.InflatedLength(converted, 16, converted.Length - 16, stopAfter: virt);
        if (inflated != virt)
            throw new InvalidOperationException(
                $"{name}: gen9 conversion produced a corrupt resource ({inflated} bytes of pages, flags say {virt}).");
        return converted;
    }

    /// <summary>
    /// Parse a loose RSC7 resource — compressed as on disk, or inflated as an archive read
    /// returns it — with CodeWalker: <paramref name="load"/> gets the page data and its entry.
    /// The reader is told whether the resource is gen9 through the same process-wide static
    /// the converter flips.
    /// </summary>
    public static T Read<T>(byte[] raw, string name, Func<byte[], RpfFileEntry, T> load)
    {
        bool gen9 = EditionOf(Path.GetExtension(name), Version(raw)) == GameEdition.Enhanced;
        var data = raw;
        var entry = RpfFile.CreateResourceFileEntry(ref data, 0);
        long virt = Rpf7.ResVirtualSize(entry.SystemFlags) + Rpf7.ResVirtualSize(entry.GraphicsFlags);
        if (data.Length != virt) data = ResourceBuilder.Decompress(data);
        lock (Gate)
        {
            bool was = RpfManager.IsGen9;
            RpfManager.IsGen9 = gen9;
            try
            {
                return load(data, entry);
            }
            finally
            {
                RpfManager.IsGen9 = was;
            }
        }
    }

    private static byte[]? Load<T>(T file, Action<T> load, Func<T, object?> root, Func<T, byte[]> save)
    {
        load(file);
        return root(file) is null ? null : save(file);
    }
}
