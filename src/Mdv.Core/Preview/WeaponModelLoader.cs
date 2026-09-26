using System.Numerics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Mdv.Core.Rpf;
using Mdv.Core.Util;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace Mdv.Core.Preview;

/// <summary>
/// Reads a weapon's drawables and textures with CodeWalker and assembles them for the
/// preview: the main model (its hi-lod when there is one), every magazine / attachment
/// hung on the weapon bone it belongs to, diffuse textures decoded (weapon palette tint
/// applied), everything transformed into weapon space.
/// </summary>
public static class WeaponModelLoader
{
    /// <summary>Largest texture edge kept — enough for a preview, a quarter of the memory of 2048.</summary>
    private const int MaxTextureSize = 1024;
    /// <summary>Stop reading a finished pack past this many bytes of models (a merged pack can be huge).</summary>
    private const long MaxRpfBytes = 512L << 20;

    /// <summary>
    /// The weapon in a source folder: its loose .ydr/.ytd files, or the models inside the
    /// finished dlc.rpf it holds. Null when there is no model to show.
    /// </summary>
    public static WeaponModel? Load(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return null;
        var files = Overrides.FindPrebuiltRpf(folder) is { } rpf ? FromRpf(rpf, ct) : FromFolder(folder);
        return Build(files, ct);
    }

    /// <summary>Model files and metas by name → their raw bytes (a loose file or an archive entry).</summary>
    private static Dictionary<string, Func<byte[]>> FromFolder(string folder)
    {
        var files = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in PathUtil.SortedFiles(folder))
        {
            var ext = f.Extension.ToLowerInvariant();
            if (ext is ".ydr" or ".ytd" or ".meta") files.TryAdd(f.Name, () => File.ReadAllBytes(f.FullName));
        }
        return files;
    }

    private static Dictionary<string, Func<byte[]>> FromRpf(string path, CancellationToken ct)
    {
        var files = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        using var arc = RpfArchive.Open(path);
        Walk(arc, 0);
        return files;

        void Walk(RpfArchive a, int depth)
        {
            foreach (var e in a.Files())
            {
                ct.ThrowIfCancellationRequested();
                if (total > MaxRpfBytes) return;
                var ext = PathUtil.SuffixLower(e.Name);
                if (ext == ".rpf" && depth < 3)
                {
                    using var nested = a.OpenNested(e);
                    Walk(nested, depth + 1);
                }
                else if (ext is ".ydr" or ".ytd" or ".meta" && !files.ContainsKey(e.Name))
                {
                    var bytes = a.ReadContent(e);    // the archive closes after the walk: read now
                    total += bytes.Length;
                    files[e.Name] = () => bytes;
                }
            }
        }
    }

    // ------------------------------------------------------------------ assembly

    private sealed class Asset
    {
        public required string Stem { get; init; }
        public required string Role { get; init; }
        public string? Ydr { get; set; }
        public string? HiYdr { get; set; }
    }

    private static WeaponModel? Build(Dictionary<string, Func<byte[]>> files, CancellationToken ct)
    {
        // group the drawables by stem; a *_hi drawable is the better-looking twin of its parent
        var assets = new Dictionary<string, Asset>();
        foreach (var name in files.Keys.Where(n => n.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var key = InputScanner.NormalizeKey(Path.GetFileNameWithoutExtension(name));
            var (role, _) = InputScanner.RoleOf(key);
            if (role == "hi")
            {
                var parent = key[..^3];
                var a = Get(parent);
                a.HiYdr ??= name;
            }
            else Get(key).Ydr ??= name;

            Asset Get(string stem)
            {
                if (!assets.TryGetValue(stem, out var a))
                    assets[stem] = a = new Asset { Stem = stem, Role = InputScanner.RoleOf(stem).Role };
                return a;
            }
        }

        var mains = assets.Values.Where(a => a.Role == "main").OrderBy(a => a.Stem.Length).ThenBy(a => a.Stem, StringComparer.Ordinal).ToList();
        var main = mains.FirstOrDefault(a => InputScanner.ClassOf(a.Stem) is not null) ?? mains.FirstOrDefault();
        if (main is null) return null;

        var textures = new TextureLibrary(files);
        var model = new WeaponModel { Name = main.Stem };

        var weapon = ReadDrawable(files, main, model);
        if (weapon is null) return null;
        var bones = BoneTable(weapon);
        model.Pieces.Add(MakePiece(weapon, main.Stem, $"Weapon ({main.Stem})", "weapon", null, true, true, Matrix4x4.Identity, textures));

        // Components: w_at_* / *_magN by name, and anything else named after the weapon that
        // says what it is (w_x_supp, w_x_mag_ap — packs often name them so). Variant bodies
        // (w_x_luxe) stay separate models.
        var parts = new List<(Asset Asset, string Kind)>();
        foreach (var a in assets.Values.Where(a => a != main))
        {
            var kind = KindOf(a.Stem, a.Role);
            if (kind == "model" && a.Stem.StartsWith(main.Stem + "_", StringComparison.Ordinal))
            {
                var own = KindOf(a.Stem[(main.Stem.Length + 1)..], "attachment");
                if (own != "attach") kind = own;
            }
            parts.Add((a, kind));
        }
        // the default clip: the weapon's own _mag1, else the plainest magazine
        var defaultMag = parts.Where(p => p.Kind == "mag")
                              .OrderBy(p => p.Asset.Stem == main.Stem + "_mag1" ? 0 : 1)
                              .ThenBy(p => p.Asset.Stem.Contains("luxe", StringComparison.Ordinal) ? 1 : 0)
                              .ThenBy(p => p.Asset.Stem.Length).ThenBy(p => p.Asset.Stem, StringComparer.Ordinal)
                              .Select(p => p.Asset).FirstOrDefault();
        var order = new[] { "mag", "supp", "scope", "flash", "grip", "barrel", "attach", "model" };
        foreach (var (a, kind) in parts.OrderBy(p => Array.IndexOf(order, p.Kind)).ThenBy(p => p.Asset.Stem, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var d = ReadDrawable(files, a, model);
            if (d is null) continue;
            bool isComponent = kind != "model";
            string? bone = isComponent ? AttachBone(d, kind, bones) : null;
            bool attached = bone is not null && bones.ContainsKey(bone);
            var attach = attached ? bones[bone!] : Matrix4x4.Identity;
            bool visible = a == defaultMag && attached;
            string label = !isComponent ? $"Model ({a.Stem})"
                : a.Role == "attachment" || a.Role.StartsWith("mag", StringComparison.Ordinal) ? InputScanner.ComponentLabel(a.Stem, a.Role)
                : $"{KindNames[kind]} ({a.Stem})";
            model.Pieces.Add(MakePiece(d, a.Stem, label, kind, bone, attached, visible, attach, textures));
        }

        AddTints(model, files, main.Stem);
        model.TextureCount = textures.Decoded;
        if (textures.Missing.Count > 0)
        {
            var names = textures.Missing.Order(StringComparer.OrdinalIgnoreCase).ToList();
            model.Warnings.Add($"{names.Count} texture(s) not found, shown untextured: {string.Join(", ", names.Take(5))}{(names.Count > 5 ? "…" : "")}");
        }
        return model;
    }

    private static Drawable? ReadDrawable(Dictionary<string, Func<byte[]>> files, Asset a, WeaponModel model)
    {
        foreach (var name in new[] { a.HiYdr, a.Ydr })
        {
            if (name is null) continue;
            try
            {
                var raw = files[name]();
                var ydr = ResourceEditions.Read(raw, name, (data, entry) =>
                {
                    var f = new YdrFile();
                    f.Load(data, entry);
                    return f;
                });
                if (ydr.Drawable is { } d) return d;
            }
            catch (Exception ex)
            {
                model.Warnings.Add($"{name}: can't read the model ({ex.Message}).");
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ tints

    /// <summary>The game's tints of an ordinary weapon (weapon tint index 0‥7).</summary>
    private static readonly string[] StandardTints = ["Normal", "Green", "Gold", "Pink", "Army", "LSPD", "Orange", "Platinum"];

    /// <summary>The 32 tints of the Mk II weapons.</summary>
    private static readonly string[] Mk2Tints =
    [
        "Classic Black", "Classic Gray", "Classic Two-Tone", "Classic White", "Classic Beige", "Classic Green",
        "Classic Blue", "Classic Earth", "Classic Brown & Black", "Red Contrast", "Blue Contrast", "Yellow Contrast",
        "Orange Contrast", "Bold Pink", "Bold Purple & Yellow", "Bold Orange", "Bold Green & Purple", "Bold Red Features",
        "Bold Green Features", "Bold Cyan Features", "Bold Yellow Features", "Bold Red & White", "Bold Blue & White",
        "Metal Gold", "Metal Platinum", "Metal Gray & Lilac", "Metal Purple & Lime", "Metal Red", "Metal Green",
        "Metal Blue", "Metal White & Aqua", "Metal Red & Yellow",
    ];

    /// <summary>
    /// The tints a palette-coloured weapon comes in: the TintSpecValues its weapons.meta
    /// entry refers to (one per item, named by the comment packs put on it), else the
    /// game's own set — 8 tints, 32 for a Mk II.
    /// </summary>
    private static void AddTints(WeaponModel model, Dictionary<string, Func<byte[]>> files, string modelName)
    {
        // the swatches come from the weapon's largest palette texture
        var tinted = model.Pieces.Where(p => p.Kind == "weapon").SelectMany(p => p.Meshes)
                          .Select(m => m.Tinted).OfType<TintedTexture>().Distinct()
                          .MaxBy(t => t.TexelCount)
                     ?? model.Pieces.SelectMany(p => p.Meshes).Select(m => m.Tinted).OfType<TintedTexture>().FirstOrDefault();
        if (tinted is null) return;

        var (specRef, names) = TintSpec(files, modelName);
        if (names is null)
        {
            bool mk2 = (specRef ?? modelName).Contains("mk2", StringComparison.OrdinalIgnoreCase);
            names = [.. mk2 ? Mk2Tints : StandardTints];
        }
        int count = Math.Min(names.Count, TintedTexture.PaletteRows);
        if (count < 2) return;

        // the swatch shows the palette column the texture uses most among those the tints change
        var use = tinted.ColumnUse();
        int column = Enumerable.Range(0, 128).Where(c => use[c] > 0)
                               .OrderByDescending(c => Enumerable.Range(1, count - 1).Any(t => tinted.PaletteColor(t, c) != tinted.PaletteColor(0, c)))
                               .ThenByDescending(c => use[c])
                               .DefaultIfEmpty(0).First();
        for (int t = 0; t < count; t++)
            model.Tints.Add(new WeaponTint(t, names[t] ?? $"Tint {t + 1}", tinted.PaletteColor(t, column) | 0xFF000000u));
    }

    /// <summary>
    /// The TintSpecValues the weapon drawn by <paramref name="modelName"/> refers to, and the
    /// names of its tints when a meta of the source defines it (null names: not defined here).
    /// </summary>
    private static (string? Ref, List<string?>? Names) TintSpec(Dictionary<string, Func<byte[]>> files, string modelName)
    {
        var docs = new List<XDocument>();
        foreach (var (name, read) in files)
        {
            if (!name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var text = System.Text.Encoding.UTF8.GetString(read());
                if (text.Contains("TintSpecValues", StringComparison.Ordinal)) docs.Add(XDocument.Parse(text));
            }
            catch (Exception)
            {
                // not an XML meta — nothing to learn from it
            }
        }

        string? specRef = null;
        foreach (var doc in docs)
        {
            var weapon = doc.Descendants("Item").FirstOrDefault(i =>
                string.Equals(i.Element("Model")?.Value.Trim(), modelName, StringComparison.OrdinalIgnoreCase));
            specRef = weapon?.Element("TintSpecValues")?.Attribute("ref")?.Value;
            if (specRef is not null) break;
        }
        if (specRef is null) return (null, null);

        foreach (var doc in docs)
        {
            var spec = doc.Descendants("TintSpecValues").Elements("Item").FirstOrDefault(i =>
                string.Equals(i.Element("Name")?.Value.Trim(), specRef, StringComparison.OrdinalIgnoreCase));
            if (spec?.Element("Tints") is not { } tints) continue;
            var names = tints.Elements("Item").Select(TintName).ToList();
            return (specRef, names.Count > 0 ? names : null);
        }
        return (specRef, null);

        // packs label the tints with a comment: <Item> <!--Blue--> … or <!-- Blue --> <Item>
        static string? TintName(XElement item)
        {
            var comment = item.Nodes().OfType<XComment>().FirstOrDefault()
                          ?? item.NodesBeforeSelf().LastOrDefault() as XComment;
            // "0 Black", "1 - Green": the index is shown anyway
            var text = System.Text.RegularExpressions.Regex.Replace(comment?.Value ?? "", @"^\s*\d+\s*[-:.)]?\s*", "").Trim();
            return text.Length is 0 or > 40 ? null : text;
        }
    }

    // ------------------------------------------------------------------ bones

    internal static Matrix4x4 M(SharpDX.Matrix t) =>
        new(t.M11, t.M12, t.M13, t.M14, t.M21, t.M22, t.M23, t.M24, t.M31, t.M32, t.M33, t.M34, t.M41, t.M42, t.M43, t.M44);

    private static Dictionary<string, Matrix4x4> BoneTable(Drawable d)
    {
        var table = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        if (d.Skeleton?.Bones?.Items is { } items)
            foreach (var b in items)
                if (!string.IsNullOrEmpty(b.Name)) table.TryAdd(b.Name, M(b.AbsTransform));
        return table;
    }

    private static readonly Dictionary<string, string> KindNames = new()
    {
        ["mag"] = "Magazine", ["supp"] = "Suppressor", ["scope"] = "Scope", ["flash"] = "Flashlight",
        ["grip"] = "Grip", ["barrel"] = "Barrel", ["attach"] = "Attachment", ["model"] = "Model",
    };

    /// <summary>What a model is, from its role and the words in its name.</summary>
    private static string KindOf(string stem, string role)
    {
        if (role.StartsWith("mag", StringComparison.Ordinal)) return "mag";
        if (role != "attachment") return "model";
        var s = stem.ToLowerInvariant();
        if (s.Contains("supp") || s.Contains("muzzle") || s.Contains("comp")) return "supp";
        if (s.Contains("flsh") || s.Contains("flash") || s.Contains("lasr") || s.Contains("laser")) return "flash";
        if (s.Contains("scop") || s.Contains("sight") || s.Contains("holo") || s.Contains("reddot")) return "scope";
        if (s.Contains("grip")) return "grip";
        if (s.Contains("barrel")) return "barrel";
        if (s.Contains("clip") || s.Contains("mag")) return "mag";
        return "attach";
    }

    /// <summary>
    /// The weapon bone a component hangs on. Components name their own root after it
    /// (AAPClip ↔ WAPClip, AAPSupp ↔ WAPSupp); failing that the kind decides.
    /// </summary>
    private static string? AttachBone(Drawable d, string kind, Dictionary<string, Matrix4x4> weaponBones)
    {
        var root = d.Skeleton?.Bones?.Items?.FirstOrDefault()?.Name;
        if (root is { Length: > 3 } && root.StartsWith("AAP", StringComparison.OrdinalIgnoreCase))
        {
            var wap = "WAP" + root[3..];
            if (weaponBones.ContainsKey(wap)) return wap;
            var near = weaponBones.Keys.Where(k => k.StartsWith(wap, StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(k => k.Length).FirstOrDefault();
            if (near is not null) return near;
        }
        string[] candidates = kind switch
        {
            "mag" => ["WAPClip"],
            "supp" => ["WAPSupp", "WAPSupp_2", "Gun_Muzzle"],
            "flash" => ["WAPFlshLasr", "WAPFlsh", "WAPFlshLasr_2"],
            "scope" => ["WAPScop", "WAPScop_2"],
            "grip" => ["WAPGrip", "WAPGrip_2"],
            "barrel" => ["WAPBarrel"],
            _ => [],
        };
        return candidates.FirstOrDefault(weaponBones.ContainsKey) ?? candidates.FirstOrDefault();
    }

    // ------------------------------------------------------------------ geometry

    /// <param name="skeletonTransforms">place unskinned models by their bone (false: the drawable is placed by <paramref name="attach"/> alone)</param>
    internal static PreviewPiece MakePiece(DrawableBase d, string id, string label, string kind, string? bone, bool attached,
                                          bool visible, Matrix4x4 attach, TextureLibrary textures, bool skeletonTransforms = true)
    {
        var piece = new PreviewPiece { Id = id, Label = label, Kind = kind, Bone = bone, Attached = attached, DefaultVisible = visible };
        var bones = skeletonTransforms ? d.Skeleton?.Bones?.Items : null;
        var models = d.DrawableModels?.High ?? d.AllModels ?? [];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var m in models)
        {
            if (m?.Geometries is null) continue;
            var xf = m.HasSkin == 0 && bones is not null && m.BoneIndex < bones.Length ? M(bones[m.BoneIndex].AbsTransform) : Matrix4x4.Identity;
            xf *= attach;
            foreach (var g in m.Geometries)
            {
                var mesh = MakeMesh(g, xf, textures);
                if (mesh is null) continue;
                piece.Meshes.Add(mesh);
                for (int i = 0; i < mesh.Positions.Length; i += 3)
                {
                    var p = new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
        }
        piece.Min = min;
        piece.Max = max;
        return piece;
    }

    private static PreviewMesh? MakeMesh(DrawableGeometry g, Matrix4x4 xf, TextureLibrary textures)
    {
        var vd = g.VertexData;
        var ib = g.IndexBuffer?.Indices;
        if (vd?.Info is null || vd.VertexBytes is null || ib is null || ib.Length < 3) return null;
        var info = vd.Info;
        int n = vd.VertexCount;
        if (n <= 0) return null;

        var posType = info.GetComponentType((int)VertexSemantics.Position);
        var nrmType = info.GetComponentType((int)VertexSemantics.Normal);
        // a vehicle's livery is a second layer with its own mapping: shown instead of the paint under it when asked for
        var layer = textures.LayerFor(g.Shader);
        int uvSem = (int)VertexSemantics.TexCoord0;
        if (layer is not null && info.GetComponentType((int)VertexSemantics.TexCoord1) is VertexComponentType.Float2 or VertexComponentType.Half2)
            uvSem = (int)VertexSemantics.TexCoord1;
        var uvType = info.GetComponentType(uvSem);
        if (posType is not (VertexComponentType.Float3 or VertexComponentType.Float4)) return null;

        var pos = new float[n * 3];
        var nrm = new float[n * 3];
        var uv = new float[n * 2];
        bool haveNormals = nrmType is VertexComponentType.Float3 or VertexComponentType.Float4
                                   or VertexComponentType.RGBA8SNorm or VertexComponentType.Half4;
        for (int v = 0; v < n; v++)
        {
            var p = vd.GetVector3(v, (int)VertexSemantics.Position);
            var tp = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), xf);
            pos[v * 3] = tp.X;
            pos[v * 3 + 1] = tp.Y;
            pos[v * 3 + 2] = tp.Z;

            if (haveNormals)
            {
                Vector3 nv = nrmType switch
                {
                    VertexComponentType.RGBA8SNorm => Xyz(vd.GetRGBA8SNorm(v, (int)VertexSemantics.Normal)),
                    VertexComponentType.Half4 => HalfXyz(vd.GetHalf4(v, (int)VertexSemantics.Normal)),
                    _ => V3(vd.GetVector3(v, (int)VertexSemantics.Normal)),
                };
                nv = Vector3.TransformNormal(nv, xf);
                float len = nv.Length();
                nv = len > 1e-6f ? nv / len : Vector3.UnitZ;
                nrm[v * 3] = nv.X;
                nrm[v * 3 + 1] = nv.Y;
                nrm[v * 3 + 2] = nv.Z;
            }

            switch (uvType)
            {
                case VertexComponentType.Float2:
                    var t = vd.GetVector2(v, uvSem);
                    uv[v * 2] = t.X;
                    uv[v * 2 + 1] = t.Y;
                    break;
                case VertexComponentType.Half2:
                    var h = vd.GetHalf2(v, uvSem);
                    uv[v * 2] = h.X;
                    uv[v * 2 + 1] = h.Y;
                    break;
            }
        }

        var idx = new int[ib.Length / 3 * 3];
        int k = 0;
        for (int i = 0; i + 2 < ib.Length; i += 3)
        {
            if (ib[i] >= n || ib[i + 1] >= n || ib[i + 2] >= n) continue;
            idx[k++] = ib[i];
            idx[k++] = ib[i + 1];
            idx[k++] = ib[i + 2];
        }
        if (k == 0) return null;
        if (k < idx.Length) Array.Resize(ref idx, k);
        if (!haveNormals) FaceNormals(pos, idx, nrm);

        var (texture, tinted, bucket) = layer is { } l ? (l, null, g.Shader?.RenderBucket ?? 0) : textures.ForShader(g.Shader);
        return new PreviewMesh
        {
            Positions = pos,
            Normals = nrm,
            Uvs = uv,
            Indices = idx,
            Texture = texture,
            Tinted = tinted,
            // render buckets: 0 opaque, 1 alpha, 2 decal, 3 cutout
            Kind = bucket is 1 or 2 && texture is { HasAlpha: true } ? SurfaceKind.Decal : SurfaceKind.Opaque,
        };
    }

    private static Vector3 V3(SharpDX.Vector3 v) => new(v.X, v.Y, v.Z);
    private static Vector3 Xyz(SharpDX.Vector4 v) => new(v.X, v.Y, v.Z);
    private static Vector3 HalfXyz(SharpDX.Half4 v) => new(v.X, v.Y, v.Z);

    /// <summary>Smooth normals from the faces, for geometry that ships none.</summary>
    private static void FaceNormals(float[] pos, int[] idx, float[] nrm)
    {
        for (int i = 0; i < idx.Length; i += 3)
        {
            int a = idx[i] * 3, b = idx[i + 1] * 3, c = idx[i + 2] * 3;
            var pa = new Vector3(pos[a], pos[a + 1], pos[a + 2]);
            var n = Vector3.Cross(new Vector3(pos[b], pos[b + 1], pos[b + 2]) - pa, new Vector3(pos[c], pos[c + 1], pos[c + 2]) - pa);
            foreach (var o in new[] { a, b, c })
            {
                nrm[o] += n.X;
                nrm[o + 1] += n.Y;
                nrm[o + 2] += n.Z;
            }
        }
        for (int o = 0; o < nrm.Length; o += 3)
        {
            var n = new Vector3(nrm[o], nrm[o + 1], nrm[o + 2]);
            float len = n.Length();
            n = len > 1e-9f ? n / len : Vector3.UnitZ;
            nrm[o] = n.X;
            nrm[o + 1] = n.Y;
            nrm[o + 2] = n.Z;
        }
    }

    // ------------------------------------------------------------------ textures

    /// <summary>Every texture of the source by name, decoded on first use.</summary>
    internal sealed class TextureLibrary
    {
        private readonly Dictionary<string, Func<byte[]>> _files;
        private Dictionary<string, Texture>? _byName;
        private readonly Dictionary<(Texture, Texture?), (PreviewTexture? Texture, TintedTexture? Tinted)> _decoded = [];

        public TextureLibrary(Dictionary<string, Func<byte[]>> files) => _files = files;

        public HashSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>A stand-in for a texture the source doesn't have (null: the surface is drawn plain).</summary>
        public Func<string, PreviewTexture?>? Fallback { get; init; }
        public int Decoded => _decoded.Values.Count(t => t.Texture is not null);

        /// <summary>
        /// Second-layer textures (a vehicle's livery, <c>DiffuseSampler2</c>) to show instead of the surface under them: gets the
        /// texture the shader names, gives the one to show (another livery of the set), or null to leave the surface as it is.
        /// </summary>
        public Func<string, string?>? Layer { get; init; }

        private static readonly uint Diffuse2 = (uint)ShaderParamNames.DiffuseSampler2;

        /// <summary>The second-layer texture of a shader that <see cref="Layer"/> asks for, decoded; null when there is none.</summary>
        public PreviewTexture? LayerFor(ShaderFX? shader)
        {
            if (Layer is null || shader?.ParametersList is not { Parameters: { } ps, Hashes: { } hs }) return null;
            for (int i = 0; i < ps.Length && i < hs.Length; i++)
            {
                if ((uint)hs[i] != Diffuse2 || ps[i].Data is not TextureBase tb || tb.Name is not { Length: > 0 } name ||
                    Layer(name) is not { } shown) continue;
                var tex = shown.Equals(name, StringComparison.OrdinalIgnoreCase) ? Resolve(tb) : Library().GetValueOrDefault(shown) ?? Resolve(tb);
                if (tex is null) return null;
                if (!_decoded.TryGetValue((tex, null), out var decoded)) _decoded[(tex, null)] = decoded = Decode(tex, null);
                return decoded.Texture;
            }
            return null;
        }

        private static readonly uint Diffuse = (uint)ShaderParamNames.DiffuseSampler;
        private static readonly uint Plain = (uint)ShaderParamNames.TextureSampler;
        private static readonly uint Palette = (uint)ShaderParamNames.TextureSamplerDiffPal;

        public (PreviewTexture? Texture, TintedTexture? Tinted, int Bucket) ForShader(ShaderFX? shader)
        {
            if (shader?.ParametersList is not { Parameters: { } ps, Hashes: { } hs }) return (null, null, 0);
            TextureBase? diffuse = null, plain = null, palette = null;
            for (int i = 0; i < ps.Length && i < hs.Length; i++)
            {
                if (ps[i].DataType != 0 || ps[i].Data is not TextureBase tb) continue;
                uint h = (uint)hs[i];
                if (h == Diffuse) diffuse ??= tb;
                else if (h == Plain) plain ??= tb;
                else if (h == Palette) palette ??= tb;
            }
            var tex = Resolve(diffuse ?? plain);
            if (tex is null)
                return (Fallback is { } stand && (diffuse ?? plain)?.Name is { Length: > 0 } name ? stand(name) : null, null, shader.RenderBucket);
            var pal = Resolve(palette);
            if (!_decoded.TryGetValue((tex, pal), out var decoded))
                _decoded[(tex, pal)] = decoded = Decode(tex, pal);
            return (decoded.Texture, decoded.Tinted, shader.RenderBucket);
        }

        private Texture? Resolve(TextureBase? tb)
        {
            if (tb is null) return null;
            if (tb is Texture { Data: not null } embedded) return embedded;
            var name = tb.Name ?? "";
            if (Library().TryGetValue(name, out var t)) return t;
            if (name.Length > 0) Missing.Add(name);
            return null;
        }

        private Dictionary<string, Texture> Library()
        {
            if (_byName is not null) return _byName;
            _byName = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, read) in _files)
            {
                if (!name.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var raw = read();
                    var ytd = ResourceEditions.Read(raw, name, (data, entry) =>
                    {
                        var f = new YtdFile();
                        f.Load(data, entry);
                        return f;
                    });
                    foreach (var t in ytd.TextureDict?.Textures?.data_items ?? [])
                    {
                        if (t?.Name is null || t.Data is null) continue;
                        // the same name in a +hi dictionary is the sharper copy
                        if (!_byName.TryGetValue(t.Name, out var had) || had.Width < t.Width) _byName[t.Name] = t;
                    }
                }
                catch (Exception)
                {
                    // an unreadable dictionary only costs its textures
                }
            }
            return _byName;
        }

        private static (PreviewTexture?, TintedTexture?) Decode(Texture t, Texture? palette)
        {
            try
            {
                var (px, w, h) = Pixels(t, MaxTextureSize);
                if (px is null) return (null, null);
                if (palette is not null && Pixels(palette, int.MaxValue) is ({ } pal, var pw, var ph))
                {
                    var tinted = new TintedTexture(px, w, h, pal, pw, ph);
                    return (tinted.Get(0), tinted);
                }
                return (new PreviewTexture(w, h, px), null);
            }
            catch (Exception)
            {
                return (null, null);
            }
        }

        /// <summary>BGRA texels of the first mip no larger than <paramref name="maxEdge"/>.</summary>
        private static (uint[]? Pixels, int Width, int Height) Pixels(Texture t, int maxEdge)
        {
            int mip = 0;
            while (mip + 1 < t.Levels && Math.Max(t.Width >> mip, t.Height >> mip) > maxEdge) mip++;
            int w = Math.Max(1, t.Width >> mip), h = Math.Max(1, t.Height >> mip);
            var bytes = DDSIO.GetPixels(t, mip);
            if (bytes is null || bytes.Length < w * h * 4) return (null, 0, 0);
            return (MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(0, w * h * 4)).ToArray(), w, h);
        }
    }
}
