using System.Buffers.Binary;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

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
        // particles carry their own textures and drawables: a Legacy one crashes Enhanced when streamed in
        // (texture fixup on a gen8 header — Liberty City's lc_core.ypt)
        [".ypt"] = (68, [71]),
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
                    ".ytd" => Load(new YtdFile(), f => f.Load(blob), f => f.TextureDict, f => { FixRenderTargets(f); return f.Save(); }),
                    ".ydr" => Load(new YdrFile(), f => f.Load(blob), f => f.Drawable, f => { FixRenderTargets(f); return f.Save(); }),
                    ".ydd" => Load(new YddFile(), f => f.Load(blob), f => f.DrawableDict, f => { FixRenderTargets(f); return f.Save(); }),
                    ".yft" => Load(new YftFile(), f => f.Load(blob), f => f.Fragment, f => { FixRenderTargets(f); return f.Save(); }),
                    ".ypt" => Load(new YptFile(), f => f.Load(blob), f => f.PtfxList, f => { FixRenderTargets(f); return f.Save(); }),
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
        converted = FixGen9Resource(converted, name);
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

    /// <summary>Build a resource with CodeWalker for one edition (its writer reads the same process-wide gen9 switch).</summary>
    public static byte[] Write(GameEdition edition, Func<byte[]> save)
    {
        lock (Gate)
        {
            bool was = RpfManager.IsGen9;
            RpfManager.IsGen9 = edition == GameEdition.Enhanced;
            try
            {
                return save();
            }
            finally
            {
                RpfManager.IsGen9 = was;
            }
        }
    }

    /// <summary>
    /// What CodeWalker's gen9 writer leaves that GTA V Enhanced reads differently from Rockstar's own models, set the way
    /// Rockstar has it (each broke Liberty City's buildings and LODs — invisible, then crashing):
    /// <list type="bullet">
    /// <item>the pointer to a shader's "unknown" parameters stays 0 when it has none (CodeWalker points it at the samplers,
    /// which Enhanced then fixes up as a pointer);</item>
    /// <item>byte 6 of a shader's parameter infos is 0 (CodeWalker writes 1);</item>
    /// <item>a model nested in a dictionary or fragment has no page map of its own (pgBase +0x08 = 0): CodeWalker keeps the
    /// small ones Legacy tools left there, and Enhanced writes the whole resource's page list into each, over the data
    /// after it.</item>
    /// <item>a compressed script render target is unpacked (<see cref="FixRenderTargets"/>; the resource is rebuilt by CodeWalker).</item>
    /// </list>
    /// <paramref name="rsc7"/> is a gen9 resource, compressed or inflated; returned as is (the same array) when nothing
    /// needed fixing, else compressed.
    /// </summary>
    public static byte[] FixGen9Resource(byte[] rsc7, string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is not (".ytd" or ".ydr" or ".ydd" or ".yft" or ".ypt") || EditionOf(ext, Version(rsc7)) != GameEdition.Enhanced) return rsc7;
        rsc7 = ResaveRenderTargets(rsc7, ext) ?? rsc7;
        if (ext == ".ytd") return rsc7;
        var data = rsc7;
        var entry = RpfFile.CreateResourceFileEntry(ref data, 0);
        long virt = Rpf7.ResVirtualSize(entry.SystemFlags);
        if (data.Length != virt + Rpf7.ResVirtualSize(entry.GraphicsFlags)) data = ResourceBuilder.Decompress(data);
        var groups = new List<ShaderGroup>();
        var nested = new List<ulong>();                    // positions of models inside the root object
        void Add(DrawableBase? d) { if (d?.ShaderGroup is { } g) groups.Add(g); }
        void Nested(IEnumerable<ulong>? ptrs) { if (ptrs is not null) nested.AddRange(ptrs); }
        lock (Gate)
        {
            bool was = RpfManager.IsGen9;
            RpfManager.IsGen9 = true;
            try
            {
                switch (ext)
                {
                    case ".ydr": { var f = new YdrFile(); f.Load(data, entry); Add(f.Drawable); break; }
                    case ".ydd":
                    {
                        var f = new YddFile(); f.Load(data, entry);
                        foreach (var d in f.Drawables ?? []) Add(d);
                        Nested(f.DrawableDict?.Drawables?.data_pointers);
                        break;
                    }
                    case ".ypt":
                    {
                        var f = new YptFile(); f.Load(data, entry);
                        foreach (var d in f.PtfxList?.DrawableDictionary?.Drawables?.data_items ?? []) Add(d);
                        Nested(f.PtfxList?.DrawableDictionary?.Drawables?.data_pointers);
                        break;
                    }
                    case ".yft":
                    {
                        var f = new YftFile(); f.Load(data, entry);
                        var fr = f.Fragment;
                        Add(fr?.Drawable); Add(fr?.DrawableCloth);
                        foreach (var d in fr?.DrawableArray?.data_items ?? []) Add(d);
                        if (fr is not null) Nested([fr.DrawablePointer, fr.DrawableClothPointer]);
                        Nested(fr?.DrawableArray?.data_pointers);
                        foreach (var lod in new[] { fr?.PhysicsLODGroup?.PhysicsLOD1, fr?.PhysicsLODGroup?.PhysicsLOD2, fr?.PhysicsLODGroup?.PhysicsLOD3 })
                            foreach (var c in lod?.Children?.data_items ?? [])
                            {
                                Add(c?.Drawable1); Add(c?.Drawable2);
                                if (c is not null) Nested([c.Drawable1Pointer, c.Drawable2Pointer]);
                            }
                        break;
                    }
                }
            }
            finally
            {
                RpfManager.IsGen9 = was;
            }
        }
        const ulong SystemBase = 0x50000000;
        long At(ulong ptr) => ptr >= SystemBase && ptr - SystemBase < (ulong)virt ? (long)(ptr - SystemBase) : -1;
        bool changed = false;
        foreach (var g in groups.Distinct())
        {
            var ptrs = g.Shaders?.data_pointers;
            var items = g.Shaders?.data_items;
            if (ptrs is null || items is null) continue;
            for (int i = 0; i < Math.Min(ptrs.Length, items.Length); i++)
            {
                var s = items[i];
                if (s?.G9_ParamInfos is not { } infos) continue;
                long sp = At(ptrs[i]);
                if (sp >= 0 && sp + 0x20 <= virt && infos.NumUnknowns == 0 && BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)sp + 0x18)) != 0)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan((int)sp + 0x18), 0);   // ShaderFX gen9 +0x18: unknown params
                    changed = true;
                }
                long ip = At(s.G9_ParamInfosPointer);
                if (ip >= 0 && ip + 8 <= virt && data[ip + 6] != 0)
                {
                    data[ip + 6] = 0;
                    changed = true;
                }
            }
        }
        foreach (var p in nested.Distinct())
        {
            long o = At(p);
            if (o <= 0 || o + 0x10 > virt || BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)o + 8)) == 0) continue;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan((int)o + 8), 0);   // pgBase +0x08: page map, the root's only
            changed = true;
        }
        return changed ? ResourceBuilder.AddResourceHeader(entry, ResourceBuilder.Compress(data)) : rsc7;
    }

    /// <summary>A gen9 resource with a compressed script render target, rebuilt by CodeWalker with <see cref="FixRenderTargets"/>; null when it has none.</summary>
    private static byte[]? ResaveRenderTargets(byte[] rsc7, string ext)
    {
        var data = rsc7;
        var entry = RpfFile.CreateResourceFileEntry(ref data, 0);
        if (data.Length != Rpf7.ResVirtualSize(entry.SystemFlags) + Rpf7.ResVirtualSize(entry.GraphicsFlags)) data = ResourceBuilder.Decompress(data);
        lock (Gate)
        {
            bool was = RpfManager.IsGen9;
            RpfManager.IsGen9 = true;
            try
            {
                switch (ext)
                {
                    case ".ytd": { var f = new YtdFile(); f.Load(data, entry); return FixRenderTargets(f) ? f.Save() : null; }
                    case ".ydr": { var f = new YdrFile(); f.Load(data, entry); return FixRenderTargets(f) ? f.Save() : null; }
                    case ".ydd": { var f = new YddFile(); f.Load(data, entry); return FixRenderTargets(f) ? f.Save() : null; }
                    case ".yft": { var f = new YftFile(); f.Load(data, entry); return FixRenderTargets(f) ? f.Save() : null; }
                    case ".ypt": { var f = new YptFile(); f.Load(data, entry); return FixRenderTargets(f) ? f.Save() : null; }
                    default: return null;
                }
            }
            finally
            {
                RpfManager.IsGen9 = was;
            }
        }
    }

    /// <summary>
    /// Script render targets (<c>script_rt_*</c>, e.g. a car's dashboard dials) that are block-compressed are unpacked to
    /// A8R8G8B8 with one level, as Rockstar's own are: CodeWalker's gen9 writer flags them as render targets, and GTA V
    /// Enhanced asserts (int3) on a compressed render target when the model streams in — Legacy didn't care.
    /// </summary>
    /// <returns>whether a texture was changed</returns>
    internal static bool FixRenderTargets(object file)
    {
        bool changed = false;
        foreach (var td in TextureDicts(file).Distinct())
            foreach (var t in td?.Textures?.data_items ?? [])
            {
                if (t?.Data?.FullData is null || !(t.Name?.StartsWith("script_rt_", StringComparison.OrdinalIgnoreCase) ?? false)) continue;
                if (!DDSIO.DXTex.IsCompressed(DDSIO.GetDXGIFormat(t.Format))) continue;
                var px = DDSIO.GetPixels(t, 0);                  // BGRA
                if (px is null || px.Length < t.Width * t.Height * 4) continue;   // BC7: CodeWalker can't unpack it
                t.Format = TextureFormat.D3DFMT_A8R8G8B8;
                t.G9_Format = 0;                                 // the writer derives it from Format
                t.G9_BlockCount = 0;
                t.Levels = 1;
                t.Stride = (ushort)(t.Width * 4);
                t.Data.FullData = px.AsSpan(0, t.Width * t.Height * 4).ToArray();
                changed = true;
            }
        return changed;
    }

    private static IEnumerable<TextureDictionary?> TextureDicts(object file)
    {
        IEnumerable<TextureDictionary?> Of(DrawableBase? d) => [d?.ShaderGroup?.TextureDictionary];
        switch (file)
        {
            case YtdFile f: return [f.TextureDict];
            case YdrFile f: return Of(f.Drawable);
            case YddFile f: return (f.Drawables ?? []).SelectMany(Of);
            case YftFile f:
            {
                var fr = f.Fragment;
                var all = new List<TextureDictionary?>();
                all.AddRange(Of(fr?.Drawable)); all.AddRange(Of(fr?.DrawableCloth));
                foreach (var d in fr?.DrawableArray?.data_items ?? []) all.AddRange(Of(d));
                foreach (var lod in new[] { fr?.PhysicsLODGroup?.PhysicsLOD1, fr?.PhysicsLODGroup?.PhysicsLOD2, fr?.PhysicsLODGroup?.PhysicsLOD3 })
                    foreach (var c in lod?.Children?.data_items ?? []) { all.AddRange(Of(c?.Drawable1)); all.AddRange(Of(c?.Drawable2)); }
                return all;
            }
            case YptFile f:
                return [f.PtfxList?.TextureDictionary, .. (f.PtfxList?.DrawableDictionary?.Drawables?.data_items ?? []).SelectMany(Of)];
            default: return [];
        }
    }

    private static byte[]? Load<T>(T file, Action<T> load, Func<T, object?> root, Func<T, byte[]> save)
    {
        load(file);
        return root(file) is null ? null : save(file);
    }
}
