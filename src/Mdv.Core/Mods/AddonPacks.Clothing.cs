using Mdv.Core;
using Mdv.Core.Rpf;
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
        pkg.Parts.Add(L.T($"{models.Count(p => !p.Prop)} model(s){(models.Any(p => p.Prop) ? $", {models.Count(p => p.Prop)} prop(s)" : "")}: ") +
                      string.Join(", ", models.GroupBy(p => p.Prop ? "prop " + ClothingNames.Anchors[p.Slot] : ClothingNames.Components[p.Slot])
                                              .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)));
        pkg.Parts.Add(wearer is null ? L.T("the mod doesn't say whose clothes these are — taken for MP male")
                                     : L.T($"for {wearer.Label}{(from is null ? "" : $" ({from})")}"));
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
        rp.Parts.Add(L.T($"{rp.Files.Count(f => f.Part is { Kind: ClothingPartKind.Drawable })} model(s) in place of {wearer.Label}'s own"));
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
            return (new Wearer(Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant()), L.T($"its {f.Name}"));
        foreach (var f in list)
        {
            var dirs = f.Origin.Replace('\\', '/').Split('/').SkipLast(1).Reverse().ToList();
            if (f.Name.Contains('^')) dirs.Insert(0, f.Name[..f.Name.LastIndexOf('^')]);
            foreach (var d in dirs)
                if (ClothingNames.WearerOfFolder(d) is { } w) return (w, L.T("the mod's folders"));
        }
        foreach (var f in list)
            foreach (var d in f.Origin.Replace('\\', '/').Split('/').SkipLast(1).Reverse())
                if (ClothingNames.WearerIn(d) is { } w) return (w, L.T("the mod's folders"));
        foreach (var f in all.Where(f => PathUtil.SuffixLower(f.Name) == ".txt" && new FileInfo(f.FullPath).Length < 256 * 1024))
            if (ClothingNames.WearerIn(TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.FullPath), strict: false), genderWords: false) is { } w)
                return (w, L.T("its readme"));
        foreach (var s in source.Sources)
            if (ClothingNames.WearerIn(Path.GetFileNameWithoutExtension(s)) is { } w) return (w, L.T("its name"));
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
            r.Parts.Add(L.T($"{r.Files.Count(f => f.Part is { Kind: ClothingPartKind.Drawable })} model(s) in place of {wearer.Label}'s own"));
        }
        if (wearer.IsMp && pkg.LooseClothing is { } c && c.Ped != wearer.Ped) SetLoosePed(pkg, wearer.Ped);
        if (pkg.ReplaceOnly) pkg.UseReplace = true;
    }

    /// <summary>
    /// The clothes of a clothing add-on as new slots of the game's collections — how they go in as an add-on (a collection
    /// of their own crashes the game: <see cref="ReplacementHandler.GameCollections"/>). Models from a pack's archives are
    /// taken out into the drop's work folder; several collections of the mod are numbered in one row per ped and type.
    /// Null: no MP clothes in it.
    /// </summary>
    private static ReplacementPackage? SlotsVersion(AddonPackage pkg, DroppedSource source)
    {
        var found = new List<(string Source, string Origin, ClothingPart Part, string Ped, string Coll)>();
        if (pkg.LooseClothing is { } loose)
            foreach (var (s, part) in loose.Parts)
                found.Add((s, source.Files.FirstOrDefault(f => f.FullPath == s)?.Origin ?? Path.GetFileName(s), part, loose.Ped, ""));
        else if (pkg.Compose is { } spec)
            foreach (var f in spec.Files)
            {
                var segs = f.PackPath.Split('/');
                if (segs.Length >= 2 && ClothingNames.WearerOfFolder(segs[^2]) is { IsMp: true, Collection: { } coll } w &&
                    ClothingNames.Parse(segs[^1]) is { } part)
                    found.Add((f.Source, source.Files.FirstOrDefault(d => d.FullPath == f.Source)?.Origin ?? f.PackPath, part, w.Ped, coll));
            }
        else if (pkg.Finished is { } fin)
        {
            var dest = Path.Combine(source.WorkDir, "slots", Guid.NewGuid().ToString("N")[..8]);
            foreach (var rpf in new[] { fin.Path }.Concat(fin.SubPacks))
            {
                try
                {
                    using var arc = RpfArchive.Open(rpf);
                    TakeOutClothes(arc, Path.GetFileName(rpf) + "/", dest, found);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or RpfFormatException)
                {
                    pkg.Warnings.Add(L.T($"{Path.GetFileName(rpf)} could not be read: {ex.Message}"));
                }
            }
        }
        if (!found.Any(f => f.Part.Kind == ClothingPartKind.Drawable)) return null;

        // one row of numbers per ped and type: the mod's collections one after another
        var numbers = new Dictionary<(string Ped, string Coll, bool Prop, int Slot, int Number), int>();
        foreach (var g in found.Where(f => f.Part.Kind == ClothingPartKind.Drawable)
                               .Select(f => (f.Ped, f.Coll, f.Part.Prop, f.Part.Slot, f.Part.Number)).Distinct()
                               .GroupBy(k => (k.Ped, k.Prop, k.Slot)))
        {
            int next = 0;
            foreach (var k in g.OrderBy(k => k.Coll, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Number)) numbers[k] = next++;
        }
        var slots = new ReplacementPackage { Kind = ModCategory.Clothing, Name = pkg.Name, Source = pkg.Source, LastCollection = true, AsNew = true };
        int orphans = 0;
        foreach (var f in found)
        {
            if (!numbers.TryGetValue((f.Ped, f.Coll, f.Part.Prop, f.Part.Slot, f.Part.Number), out var n))
            {
                orphans++;                                                   // a texture of a model the mod doesn't have
                continue;
            }
            var name = ClothingNames.Renumber(f.Part.Name, n);
            slots.Files.Add(new ReplacementFile { Source = f.Source, Origin = f.Origin, Part = ClothingNames.Parse(name), SlotPed = f.Ped });
        }
        if (orphans > 0) slots.Warnings.Add(L.T($"{orphans} texture(s) belong to models the mod doesn't have — left out."));
        var peds = found.Select(f => f.Ped).Distinct().ToList();
        slots.Wearer = new Wearer(peds[0]);
        var models = slots.Files.Where(f => f.Part is { Kind: ClothingPartKind.Drawable }).ToList();
        slots.Parts.Add(L.T($"{models.Count} model(s) for {string.Join(" / ", peds.Select(ClothingNames.PedLabel))} — new slots at the end of the game's last collection"));
        return slots;
    }

    /// <summary>The files of MP collection folders in an archive (nested ones too), written out under <paramref name="dest"/>.</summary>
    private static void TakeOutClothes(RpfArchive arc, string at, string dest,
                                       List<(string Source, string Origin, ClothingPart Part, string Ped, string Coll)> found)
    {
        foreach (var t in arc.Tree())
        {
            if (t.IsDir) continue;
            if (t.Path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) && t.Entry.StoredRaw)
            {
                using var nested = arc.OpenNested(t.Entry);
                TakeOutClothes(nested, at + t.Path + "/", dest, found);
                continue;
            }
            var segs = t.Path.Split('/');
            if (segs.Length < 2 || ClothingNames.WearerOfFolder(segs[^2]) is not { IsMp: true, Collection: { } coll } w ||
                ClothingNames.Parse(segs[^1]) is not { } part) continue;
            var file = Path.Combine(dest, found.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), segs[^1]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, arc.ReadContent(t.Entry));
            found.Add((file, at + t.Path, part, w.Ped, coll));
        }
    }

    /// <summary>Loose MP clothes are for this MP ped (mp_m_freemode_01 / mp_f_freemode_01).</summary>
    public static void SetLoosePed(AddonPackage pkg, string ped)
    {
        if (pkg.LooseClothing is not { } c) return;
        c.Ped = ped;
        pkg.Checks = null;
        if (pkg.Slots is { } slots) SetSlotsPed(slots, ped);
    }

    private static void SetSlotsPed(ReplacementPackage slots, string ped)
    {
        foreach (var f in slots.Files) f.SlotPed = ped;
        slots.Wearer = new Wearer(ped);
        slots.ResolvedFor = null;
        slots.Parts.Clear();
        slots.Parts.Add(L.T($"{slots.Files.Count(f => f.Part is { Kind: ClothingPartKind.Drawable })} model(s) for {ClothingNames.PedLabel(ped)} — new slots at the end of the game's last collection"));
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
