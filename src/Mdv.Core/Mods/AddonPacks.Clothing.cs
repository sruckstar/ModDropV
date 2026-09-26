using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// Clothing: an MP clothes pack (a finished dlc.rpf with shop metas — sub-packs included — or a FiveM clothing
/// resource, packed as Rockstar lays its collections out) installs as a pack of its own. Loose models become either
/// an add-on collection of their own for an MP ped (numbered from 0, ymt and shop meta written for them) or a
/// replacement in a ped's folder — the story characters' clothes can only be replaced, with new slots added to their
/// ymt for models they have no slot for (<see cref="ReplacementHandler"/>).
/// </summary>
public sealed partial class AddonPackHandler
{
    /// <summary>Loose clothing models: an add-on collection for the MP ped they are for, and the Replace version.</summary>
    private AddonPackage? LooseClothing(DroppedSource source, List<DroppedFile> files, string name, ModSource? src, HandlerEnv env)
    {
        var clothing = files.Where(f => ClothingNames.IsClothingFile(f.Name)).ToList();
        if (clothing.Count == 0) return null;
        var (wearer, from) = GuessWearer(source, files, clothing);
        var ped = wearer is { IsMp: true } ? wearer.Ped : ClothingNames.MpMale;
        if (DlcComposer.FromClothingModels(source, ped) is not { } spec) return null;
        var replace = ClothingReplace(files, clothing, name, src, wearer ?? new Wearer(ped), from);
        var (packName, packFrom) = PackNameOf(source, null, null);
        var pkg = new AddonPackage(Category)
        {
            Name = name, Source = src, Compose = spec, DataDir = env.DataDir, PackName = packName, PackNameFrom = packFrom, Replace = replace,
            // a story character's clothes, or clothes laid out for an existing collection: in place of what is there
            UseReplace = replace.Wearer is { IsMp: false } or { Collection: not null },
        };
        pkg.Warnings.AddRange(spec.Warnings);
        var coll = pkg.LooseClothing!;
        var models = coll.Parts.Where(p => p.Part.Kind == ClothingPartKind.Drawable).Select(p => p.Part).ToList();
        pkg.Parts.Add($"{models.Count(p => !p.Prop)} model(s){(models.Any(p => p.Prop) ? $", {models.Count(p => p.Prop)} prop(s)" : "")}: " +
                      string.Join(", ", models.GroupBy(p => p.Prop ? "prop " + ClothingNames.Anchors[p.Slot] : ClothingNames.Components[p.Slot])
                                              .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)));
        pkg.Parts.Add(wearer is null ? "the mod doesn't say whose clothes these are — taken for MP male"
                                     : $"for {wearer.Label}{(from is null ? "" : $" ({from})")}");
        return pkg;
    }

    /// <summary>The Replace version of a clothing resource / pack: clothing files outside it (an [SP] folder next to [FiveM]).</summary>
    private static ReplacementPackage? ClothingReplaceVersion(DroppedSource source, List<DroppedFile> files, string name, ModSource? src, ComposeSpec spec)
    {
        var used = spec.Files.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = files.Where(f => DlcComposer.IsManifest(f.Name)).Select(f => Path.GetDirectoryName(f.FullPath)!).ToList();
        var loose = files.Where(f => ClothingNames.IsClothingFile(f.Name) && !f.Name.Contains('^') && !used.Contains(f.FullPath) &&
                                     !roots.Any(r => f.FullPath.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                         .ToList();
        if (!loose.Any(f => ClothingNames.Parse(f.Name) is { Kind: ClothingPartKind.Drawable })) return null;
        var (wearer, from) = GuessWearer(source, files, loose);
        // with nothing said, the ped of the resource's own collection
        wearer ??= spec.Content.Collections.FirstOrDefault() is { } c ? new Wearer(c.Ped) : new Wearer(ClothingNames.MpMale);
        return ClothingReplace(files, loose, name + " (replace)", src, wearer, from);
    }

    private static ReplacementPackage ClothingReplace(List<DroppedFile> all, List<DroppedFile> clothing, string name, ModSource? src, Wearer wearer, string? from)
    {
        // the ped's own ymt, when the mod ships it, goes with its clothes
        var picked = clothing.Concat(all.Where(f => f.Name.Equals(wearer.Ymt, StringComparison.OrdinalIgnoreCase))).ToList();
        var rp = ReplacementHandler.Build(picked, name, src);
        rp.Kind = ModCategory.Clothing;
        rp.Wearer = wearer;
        rp.WearerFrom = from;
        rp.Parts.Clear();
        rp.Parts.Add($"{rp.Files.Count(f => f.Part is { Kind: ClothingPartKind.Drawable })} model(s) in place of {wearer.Label}'s own");
        return rp;
    }

    /// <summary>
    /// Whose clothes a mod's files are: a ped folder / FiveM prefix / ymt of a ped (<c>player_one</c>,
    /// <c>mp_m_freemode_01_mp_m_x^…</c>), a folder named after one (Franklin, Female), the readme, the mod's name.
    /// </summary>
    public static (Wearer? Wearer, string? From) GuessWearer(DroppedSource source, IEnumerable<DroppedFile> all, IEnumerable<DroppedFile> clothing)
    {
        var list = clothing.ToList();
        foreach (var f in all.Where(f => ClothingNames.Peds.Any(p => f.Name.Equals(p + ".ymt", StringComparison.OrdinalIgnoreCase))))
            return (new Wearer(Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant()), $"its {f.Name}");
        foreach (var f in list)
        {
            var dirs = f.Origin.Replace('\\', '/').Split('/').SkipLast(1).Reverse().ToList();
            if (f.Name.Contains('^')) dirs.Insert(0, f.Name[..f.Name.LastIndexOf('^')]);
            foreach (var d in dirs)
                if (ClothingNames.WearerOfFolder(d) is { } w) return (w, "the mod's folders");
        }
        foreach (var f in list)
            foreach (var d in f.Origin.Replace('\\', '/').Split('/').SkipLast(1).Reverse())
                if (ClothingNames.WearerIn(d) is { } w) return (w, "the mod's folders");
        foreach (var f in all.Where(f => PathUtil.SuffixLower(f.Name) == ".txt" && new FileInfo(f.FullPath).Length < 256 * 1024))
            if (ClothingNames.WearerIn(TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.FullPath), strict: false), genderWords: false) is { } w)
                return (w, "its readme");
        foreach (var s in source.Sources)
            if (ClothingNames.WearerIn(Path.GetFileNameWithoutExtension(s)) is { } w) return (w, "its name");
        return (null, null);
    }

    /// <summary>
    /// Whose clothes the player says they are: the Replace version goes to that ped's folder; loose models packed as an
    /// add-on collection are for that MP ped (the story characters take a replacement only).
    /// </summary>
    public static void SetWearer(AddonPackage pkg, Wearer wearer)
    {
        if (pkg.Replace is { } r && r.Wearer != wearer)
        {
            r.Wearer = wearer;
            r.WearerFrom = null;
            r.ResolvedFor = null;
            r.Parts.Clear();
            r.Parts.Add($"{r.Files.Count(f => f.Part is { Kind: ClothingPartKind.Drawable })} model(s) in place of {wearer.Label}'s own");
        }
        if (wearer.IsMp && pkg.LooseClothing is { } c && c.Ped != wearer.Ped)
        {
            c.Ped = wearer.Ped;
            pkg.Checks = null;
        }
        if (pkg.ReplaceOnly) pkg.UseReplace = true;
    }

    /// <summary>The collections a clothing add-on brings, with the name loose models get in this pack.</summary>
    public static List<AddonCollection> CollectionsOf(AddonPackage pkg)
    {
        var list = pkg.Content.Collections.ToList();
        foreach (var n in pkg.Compose?.NewCollections.Where(n => n.Parts.Count > 0) ?? [])
            list.Add(new AddonCollection(n.Ped, n.NameFor(pkg.Device)));
        return list;
    }
}
