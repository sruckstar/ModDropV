using System.Numerics;
using System.Text.RegularExpressions;
using Mdv.Core.Mods;
using Mdv.Core.Rpf;
using Mdv.Core.Util;
using CodeWalker.GameFiles;

namespace Mdv.Core.Preview;

/// <summary>
/// The 3D preview of an add-on vehicle or ped (or a vehicle / ped replacement), in the same form as a
/// weapon's (<see cref="WeaponModel"/>, drawn by the same view):
/// <list type="bullet">
/// <item>vehicle: the fragment's body (its hi-lod when there is one) with every part on its bone, plus the
/// wheels — the game keeps one front and one rear wheel and copies them onto every wheel bone, turning the
/// right-hand ones round (as CodeWalker does);</item>
/// <item>ped: the drawables of its .ydd (one per component slot, the first variation shown; others can be
/// picked — one per slot, as in the game), skinned in their bind pose.</item>
/// </list>
/// Textures come from the mod's .ytd files and the drawables' own; shared game textures (vehshare) are
/// not there, those surfaces are drawn plain.
/// </summary>
public static partial class AddonModelLoader
{
    private const long MaxBytes = 512L << 20;

    /// <summary>The first vehicle / ped of an add-on package, or null when it has no model to show.</summary>
    public static WeaponModel? Load(AddonPackage pkg, CancellationToken ct = default)
    {
        var files = pkg.Finished is { } f ? FromRpf(f.Path, ct) : FromSpec(pkg.Compose!);
        var names = pkg.Content.SpawnNames.ToList();
        return pkg.Kind == ModCategory.Ped ? Ped(files, names, ct) : Vehicle(files, names, ct);
    }

    /// <summary>The vehicle / ped a replacement's loose models make.</summary>
    public static WeaponModel? Load(ReplacementPackage rp, CancellationToken ct = default)
    {
        var files = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in rp.Files)
            if (PathUtil.SuffixLower(f.Name) is ".yft" or ".ydd" or ".ytd" or ".ydr") files.TryAdd(f.Name, () => File.ReadAllBytes(f.Source));
        var stems = files.Keys.Where(k => PathUtil.SuffixLower(k) is ".yft" or ".ydd")
                         .Select(k => Regex.Replace(Path.GetFileNameWithoutExtension(k), "_hi$", "", RegexOptions.IgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return rp.Kind == ModCategory.Ped ? Ped(files, stems, ct) : Vehicle(files, stems, ct);
    }

    /// <summary>
    /// A game vehicle / installed add-on wearing a livery: its model from the game, its dictionaries with the livery's
    /// pictures put in (see <see cref="LiveryResolution.Painted"/>). Null when the livery isn't looked up in a game yet.
    /// </summary>
    public static WeaponModel? Load(LiveryPackage pkg, CancellationToken ct = default)
    {
        if (pkg.Resolved is not { } r || r.ModelPaths.Count == 0) return null;
        var overlay = ModsOverlay.Load(r.GameDir);
        var files = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in r.ModelPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (overlay.Read(path) is { } bytes) files[Path.GetFileName(path)] = () => bytes;
        }
        foreach (var (name, bytes) in r.Painted(pkg)) files[name] = () => bytes;
        ct.ThrowIfCancellationRequested();
        // the livery the mod paints is shown: the model names its first one (…_sign_1), the mod may bring …_sign_3
        var painted = pkg.Textures.Select(t => t.Slot).OfType<string>().Where(LiveryHandler.IsLiveryLike).ToList();
        return Vehicle(files, [r.Vehicle], ct, layer: name =>
        {
            if (!LiveryHandler.IsLiveryLike(name)) return null;
            if (painted.Count == 0 || painted.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;
            var stem = Regex.Replace(name, @"\d+$", "");
            return painted.FirstOrDefault(p => stem.Length < name.Length && p.StartsWith(stem, StringComparison.OrdinalIgnoreCase) &&
                                               Regex.IsMatch(p[stem.Length..], @"^\d+$")) ?? name;
        });
    }

    // ------------------------------------------------------------------ files

    private static readonly HashSet<string> ModelExt = new(StringComparer.OrdinalIgnoreCase) { ".yft", ".ydd", ".ytd", ".ydr" };

    /// <summary>The models of a finished pack's image archives, by their path inside the image (ped/head_000_r.ydd).</summary>
    private static Dictionary<string, Func<byte[]>> FromRpf(string path, CancellationToken ct)
    {
        var files = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        using var arc = RpfArchive.Open(path);
        Walk(arc, 0);
        return files;

        void Walk(RpfArchive a, int depth)
        {
            foreach (var t in a.Tree())
            {
                ct.ThrowIfCancellationRequested();
                if (t.IsDir || total > MaxBytes) continue;
                var e = t.Entry;
                var ext = PathUtil.SuffixLower(e.Name);
                if (ext == ".rpf" && e.StoredRaw && depth < 3 && !t.Path.Contains("/lang/", StringComparison.OrdinalIgnoreCase))
                {
                    using var nested = a.OpenNested(e);
                    Walk(nested, depth + 1);
                }
                else if (depth > 0 && ModelExt.Contains(ext) && e.IsResource && !files.ContainsKey(t.Path))
                {
                    var bytes = a.ReadContent(e);        // the archive closes after the walk: read now
                    total += bytes.Length;
                    files[t.Path] = () => bytes;
                }
            }
        }
    }

    private static Dictionary<string, Func<byte[]>> FromSpec(ComposeSpec spec)
    {
        var files = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in spec.Files)
        {
            int i = f.PackPath.IndexOf(".rpf/", StringComparison.OrdinalIgnoreCase);
            if (i < 0 || !ModelExt.Contains(PathUtil.SuffixLower(f.PackPath))) continue;
            files.TryAdd(f.PackPath[(i + 5)..], () => File.ReadAllBytes(f.Source));
        }
        return files;
    }

    private static T? Read<T>(Dictionary<string, Func<byte[]>> files, string name, Func<byte[], RpfFileEntry, T> load, WeaponModel model)
        where T : class
    {
        try
        {
            return ResourceEditions.Read(files[name](), name, load);
        }
        catch (Exception ex)
        {
            model.Warnings.Add($"{Path.GetFileName(name)}: can't read the model ({ex.Message}).");
            return null;
        }
    }

    private static void Finish(WeaponModel model, WeaponModelLoader.TextureLibrary textures)
    {
        model.TextureCount = textures.Decoded;
        if (textures.Missing.Count == 0) return;
        var names = textures.Missing.Order(StringComparer.OrdinalIgnoreCase).ToList();
        model.Warnings.Add($"{names.Count} texture(s) not in the mod (shared game textures), shown plain: " +
                           $"{string.Join(", ", names.Take(5))}{(names.Count > 5 ? "…" : "")}");
    }

    // ------------------------------------------------------------------ vehicles

    /// <summary>Right-hand wheel bones (their wheels are the left ones turned round); every wheel bone by tag.</summary>
    private static readonly HashSet<ushort> RightWheels = [26418, 5857, 5858, 5859, 26398];
    private static readonly HashSet<ushort> FrontWheels = [27922, 26418];
    private static readonly HashSet<ushort> Wheels = [27922, 26418, 29921, 29922, 29923, 27902, 5857, 5858, 5859, 26398];

    /// <param name="layer">second-layer textures (liveries) to show instead of the paint under them (see <see cref="WeaponModelLoader.TextureLibrary.Layer"/>)</param>
    private static WeaponModel? Vehicle(Dictionary<string, Func<byte[]>> files, List<string> names, CancellationToken ct,
                                        Func<string, string?>? layer = null)
    {
        string? pick = null;
        foreach (var n in names.Concat(files.Keys.Where(k => PathUtil.SuffixLower(k) == ".yft").Select(Path.GetFileNameWithoutExtension)).OfType<string>())
        {
            pick = new[] { $"{n}_hi.yft", $"{n}.yft" }.FirstOrDefault(files.ContainsKey);
            if (pick is not null) break;
        }
        if (pick is null) return null;
        var name = Regex.Replace(Path.GetFileNameWithoutExtension(pick), "_hi$", "", RegexOptions.IgnoreCase);
        var model = new WeaponModel { Name = name };
        var yft = Read(files, pick, (data, entry) =>
        {
            var f = new YftFile();
            f.Load(data, entry);
            return f;
        }, model);
        if (yft?.Fragment is not { Drawable: { } body } frag) return model.Warnings.Count > 0 ? model : null;
        ct.ThrowIfCancellationRequested();

        var textures = new WeaponModelLoader.TextureLibrary(files) { Fallback = SharedTexture, Layer = layer };
        var bodyPiece = WeaponModelLoader.MakePiece(body, "body", $"Body ({name})", "body", null, true, true, Matrix4x4.Identity, textures);

        // the physics children: breakable parts drawn on their own, and the wheels
        var lod = frag.PhysicsLODGroup?.PhysicsLOD1;
        var children = lod?.Children?.data_items ?? [];
        var xforms = lod?.FragTransforms?.Matrices ?? [];
        var offset = lod is null ? Vector3.Zero : new Vector3(lod.PositionOffset.X, lod.PositionOffset.Y, lod.PositionOffset.Z);
        FragDrawable? front = null, rear = null;
        foreach (var c in children)
            if (c?.Drawable1 is { AllModels.Length: > 0 } d && Wheels.Contains(c.BoneTag))
            {
                if (FrontWheels.Contains(c.BoneTag)) front ??= d;
                else rear ??= d;
            }
        var wheels = new PreviewPiece { Id = "wheels", Label = "Wheels", Kind = "wheels", DefaultVisible = true };
        for (int i = 0; i < children.Length; i++)
        {
            var c = children[i];
            if (c is null || i >= xforms.Length) continue;
            var xf = WeaponModelLoader.M(xforms[i]);
            bool isWheel = Wheels.Contains(c.BoneTag);
            DrawableBase? d = isWheel ? (FrontWheels.Contains(c.BoneTag) ? front ?? rear : rear ?? front)
                        : c.Drawable1 is { AllModels.Length: > 0 } own ? own : null;
            if (d is null) continue;
            if (isWheel && RightWheels.Contains(c.BoneTag))
                xf = new Matrix4x4(-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, xf.M41, xf.M42, xf.M43, 1);
            xf.Translation += offset;
            var part = WeaponModelLoader.MakePiece(d, $"part{i}", "", "part", null, true, true, xf, textures, skeletonTransforms: false);
            Merge(isWheel ? wheels : bodyPiece, part);
        }
        model.Pieces.Add(bodyPiece);
        if (!wheels.IsEmpty) model.Pieces.Add(wheels);
        foreach (var p in model.Pieces) p.Note = $"{p.Triangles:#,0} triangles";
        Finish(model, textures);
        return model;
    }

    private static readonly PreviewTexture Rubber = new(1, 1, [0xFF2B2B2Du]);
    private static readonly PreviewTexture Black = new(1, 1, [0xFF141416u]);
    private static readonly PreviewTexture Glass = new(1, 1, [0x66283038u]);

    /// <summary>The look of the game's shared vehicle textures the mod doesn't carry: tyres dark, black parts black, glass see-through.</summary>
    private static PreviewTexture? SharedTexture(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("glass") || n.Contains("window")) return Glass;
        if (n.Contains("tyre") || n.Contains("tire") || n.Contains("rubber")) return Rubber;
        if (n == "black" || n.EndsWith("_black", StringComparison.Ordinal)) return Black;
        return null;
    }

    private static void Merge(PreviewPiece into, PreviewPiece part)
    {
        into.Meshes.AddRange(part.Meshes);
        if (part.IsEmpty) return;
        into.Min = Vector3.Min(into.Min, part.Min);
        into.Max = Vector3.Max(into.Max, part.Max);
    }

    // ------------------------------------------------------------------ peds

    private static readonly string[] Slots = ["head", "berd", "hair", "uppr", "lowr", "hand", "feet", "teef", "accs", "task", "decl", "jbib"];

    private static readonly Dictionary<string, string> SlotNames = new()
    {
        ["head"] = "head", ["berd"] = "beard / mask", ["hair"] = "hair", ["uppr"] = "upper body", ["lowr"] = "legs",
        ["hand"] = "hands", ["feet"] = "feet", ["teef"] = "neck / teeth", ["accs"] = "accessory", ["task"] = "gear",
        ["decl"] = "decals", ["jbib"] = "jacket",
    };

    private static readonly Lazy<Dictionary<uint, string>> DrawableNames = new(() =>
    {
        var map = new Dictionary<uint, string>();
        foreach (var s in Slots)
            for (int i = 0; i < 64; i++)
                foreach (var suffix in new[] { "r", "u", "m" })
                {
                    var n = $"{s}_{i:000}_{suffix}";
                    map[JenkHash.GenHash(n)] = n;
                }
        return map;
    });

    [GeneratedRegex(@"^(head|berd|hair|uppr|lowr|hand|feet|teef|accs|task|decl|jbib)_(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex SlotRe();

    private static WeaponModel? Ped(Dictionary<string, Func<byte[]>> files, List<string> names, CancellationToken ct)
    {
        var name = names.FirstOrDefault(n => files.ContainsKey(n + ".ydd") || files.Keys.Any(k => k.StartsWith(n + "/", StringComparison.OrdinalIgnoreCase)))
                   ?? files.Keys.Where(k => PathUtil.SuffixLower(k) == ".ydd").Select(k => k.Split('/')[0].Split('.')[0]).FirstOrDefault();
        if (name is null) return null;
        var model = new WeaponModel { Name = name };
        var textures = new WeaponModelLoader.TextureLibrary(files);

        // a component ped: one dictionary of drawables; a streamed one: a dictionary per drawable in its folder
        var drawables = new List<(string Name, Drawable D)>();
        foreach (var key in files.Keys.Where(k => PathUtil.SuffixLower(k) == ".ydd" &&
                                                  (k.Equals(name + ".ydd", StringComparison.OrdinalIgnoreCase) ||
                                                   k.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)))
                                      .Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var ydd = Read(files, key, (data, entry) =>
            {
                var f = new YddFile();
                f.Load(data, entry);
                return f;
            }, model);
            if (ydd?.DrawableDict is not { } dict) continue;
            var hashes = dict.Hashes ?? [];
            var items = dict.Drawables?.data_items ?? [];
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] is not { } d) continue;
                var n = i < hashes.Length && DrawableNames.Value.TryGetValue(hashes[i], out var known) ? known
                      : key.Contains('/') ? Path.GetFileNameWithoutExtension(key) : $"drawable_{i}";
                drawables.Add((n, d));
            }
        }
        var firstOfSlot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, d) in drawables.OrderBy(x => SlotRe().Match(x.Name) is { Success: true } m ? Array.IndexOf(Slots, m.Groups[1].Value.ToLowerInvariant()) : 99)
                                        .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var m = SlotRe().Match(n);
            var slot = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "part";
            bool visible = firstOfSlot.Add(slot);
            var piece = WeaponModelLoader.MakePiece(d, n, n, slot, m.Success ? slot : null, m.Success, visible, Matrix4x4.Identity, textures);
            piece.Note = $"{(m.Success ? SlotNames[slot] : "part")} · {piece.Triangles:#,0} triangles";
            model.Pieces.Add(piece);
        }
        if (model.Pieces.Count == 0) return model.Warnings.Count > 0 ? model : null;
        Finish(model, textures);
        return model;
    }
}
