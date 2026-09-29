using Mdv.Core;
using Mdv.Core.Index;

namespace Mdv.Core.Mods;

/// <summary>A ped whose clothes a mod can go on, how many of the mod's files it has, and whether it can get new slots.</summary>
public sealed record WearerChoice(Wearer Wearer, int Fits, bool CanAddSlots)
{
    public override string ToString() => Wearer.Label + (Fits > 0 ? L.T($"  ({Fits} matching)") : "");
}

/// <summary>
/// Clothing replacements: the files go into their wearer's folder only (<c>player_one/uppr_005_u.ydd</c> — never another
/// ped's file of the same name). A model the wearer has no slot for — or every model, when the player asks for new
/// clothes — goes in as a new slot: its drawable is added to a copy of the wearer's ymt and its files are renamed to the
/// new number. That works for the story characters and for add-on collections in mods (not Rockstar's MP collections,
/// whose drawable numbers online outfits and other DLCs count on).
/// </summary>
public sealed partial class ReplacementHandler
{
    /// <summary>Look the files up in their wearer's folders; models without a slot get new ones where the wearer allows it.</summary>
    private static void ResolveClothing(ReplacementPackage pkg, GameIndex index)
    {
        var w = pkg.Wearer!;
        pkg.SlotsYmt = null;
        pkg.SlotsProblem = null;
        foreach (var f in pkg.Files)
        {
            f.Candidates.Clear();
            f.Target = null;
            f.Note = null;
            f.NewNumber = null;
            List<FileHit> hits = f.Part is { } part
                ? index.Find($"{(part.Prop ? w.PropFolder : w.Folder)}/{part.Name}")
                : f.Name.Equals(w.Ymt, StringComparison.OrdinalIgnoreCase) ? index.Find(f.Name) : [];
            if (hits.Count == 0 || (pkg.AsNew && f.Part is not null && !pkg.OwnYmt)) continue;
            var pick = hits.FirstOrDefault(h => h.Winner) ?? hits.FirstOrDefault(h => h.Active) ?? hits[0];
            f.Candidates.Add(new ReplaceTarget(Norm(pick), pick.Source, pick.Winner));
            f.Target = f.Candidates[0].GamePath;
        }

        // models with no place of their own: new slots in the wearer's ymt
        var missing = pkg.Files.Where(f => f.Target is null && f.Part is { Kind: ClothingPartKind.Drawable }).Select(f => Key(f.Part!)).ToHashSet();
        if (missing.Count == 0) return;
        if (pkg.OwnYmt)
        {
            pkg.SlotsProblem = L.T($"the mod brings its own {w.Ymt}, so it decides the slots");
            return;
        }
        var ymt = index.Find(w.Ymt).FirstOrDefault(h => h.Winner);
        if (ymt is null)
        {
            pkg.SlotsProblem = L.T($"the game has no {w.Ymt}");
            return;
        }
        if (!CanAddSlots(w, ymt, index.GameDir))
        {
            pkg.SlotsProblem = L.T($"{w.Label} is one of Rockstar's own collections — its drawable numbers are what other DLCs and outfits count on; " +
                               $"install the clothes as an add-on instead");
            return;
        }
        int[] comps, props;
        try
        {
            var data = ModsOverlay.Load(index.GameDir).Read(Norm(ymt)) ?? throw new InvalidDataException(L.T($"{w.Ymt} could not be read"));
            (comps, props) = PedVariation.Counts(PedVariation.Read(data));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            pkg.SlotsProblem = L.T($"{w.Ymt} could not be read: {ex.Message}");
            return;
        }
        pkg.SlotsYmt = Norm(ymt);
        var dir = pkg.SlotsYmt[..pkg.SlotsYmt.LastIndexOf('/')];
        foreach (var key in missing.OrderBy(k => k.Prop).ThenBy(k => k.Slot).ThenBy(k => k.Number))
        {
            int n = key.Prop ? props[key.Slot]++ : comps[key.Slot]++;
            foreach (var f in pkg.Files.Where(f => f.Part is { } p && Key(p) == key))
            {
                var name = ClothingNames.Renumber(f.Part!.Name, n);
                f.NewNumber = n;
                f.Target = $"{dir}/{(key.Prop ? w.PropFolder : w.Folder)}/{name}";
                f.Candidates.Add(new ReplaceTarget(f.Target, L.T($"new slot {(key.Prop ? "prop " + ClothingNames.Anchors[key.Slot] : ClothingNames.Components[key.Slot])} {n}"), true));
                if (pkg.AsNew && f.Part.Kind == ClothingPartKind.Drawable)
                    f.Note = L.T($"new clothes for {w.Label} — it goes in as {ClothingNames.Renumber(f.Part.Name, n)}");
                else if (n != key.Number && f.Part.Kind == ClothingPartKind.Drawable)
                    f.Note = L.T($"{w.Label} has no free {f.Part.Describe()} — it goes in as {ClothingNames.Renumber(f.Part.Name, n)}");
            }
        }
    }

    private static (bool Prop, int Slot, int Number) Key(ClothingPart p) => (p.Prop, p.Slot, p.Number);

    /// <summary>
    /// New slots are fine for the story characters and for add-on collections in mods; not for Rockstar's MP sets, whose
    /// numbers the game's outfits and later collections count on.
    /// </summary>
    private static bool CanAddSlots(Wearer w, FileHit ymt, string gameDir)
    {
        if (!w.IsMp) return true;
        // a pack in mods/update/x64/dlcpacks (onigiri/dlcpacks) — an add-on unless the game has a pack of that name
        return ymt.InstalledPack is { } pack && !Directory.Exists(Path.Combine(gameDir, "update", "x64", "dlcpacks", pack));
    }

    /// <summary>
    /// The peds a clothing mod can go on: the story characters, the MP peds and every MP collection the game has (add-on
    /// ones in mods included) — those with the most of the mod's files first.
    /// </summary>
    public static List<WearerChoice> WearerChoices(ReplacementPackage pkg, GameIndex index)
    {
        var wearers = new List<Wearer> { new(ClothingNames.Michael), new(ClothingNames.Franklin), new(ClothingNames.Trevor),
                                         new(ClothingNames.MpMale), new(ClothingNames.MpFemale) };
        var ymts = new List<FileHit>();
        foreach (var h in index.Find("mp_?_freemode_01_*.ymt", 5000).Where(h => h.Winner))
            if (ClothingNames.WearerOfYmt(h.File.Name) is { Collection: not null } c && !wearers.Contains(c))
            {
                wearers.Add(c);
                ymts.Add(h);
            }
        var parts = pkg.Files.Select(f => f.Part).OfType<ClothingPart>().Where(p => p.Kind == ClothingPartKind.Drawable).ToList();
        var list = new List<WearerChoice>();
        foreach (var w in wearers)
        {
            int fits = parts.Count(p => index.Find($"{(p.Prop ? w.PropFolder : w.Folder)}/{p.Name}", 1).Count > 0);
            bool slots = !w.IsMp || (ymts.FirstOrDefault(y => y.File.Name.Equals(w.Ymt, StringComparison.OrdinalIgnoreCase)) is { } y &&
                                     CanAddSlots(w, y, index.GameDir));
            list.Add(new WearerChoice(w, fits, slots));
        }
        // the characters and the MP peds keep their place; collections — the matching ones first
        return [.. list.Take(5), .. list.Skip(5).OrderByDescending(c => c.Fits).ThenByDescending(c => c.CanAddSlots)
                                               .ThenBy(c => c.Wearer.Folder, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"3 replaced, 2 new slot(s)".</summary>
    private static string ClothingWhere(ReplacementPackage pkg)
    {
        int replaced = pkg.Files.Count(f => f.Target is not null && f.NewNumber is null && f.Part is { Kind: ClothingPartKind.Drawable });
        int added = pkg.NewSlotFiles.Count(f => f.Part is { Kind: ClothingPartKind.Drawable });
        var parts = new List<string>();
        if (replaced > 0) parts.Add(L.T($"{replaced} model(s) replaced"));
        if (added > 0) parts.Add(L.T($"{added} new slot(s)"));
        return parts.Count == 0 ? L.T($"{pkg.Files.Count(f => f.Target is not null)} file(s)") : string.Join(", ", parts);
    }

    /// <summary>
    /// Add the new slots to the wearer's ymt (the version the game has now — another mod may have added some since the
    /// plan was made) and put the models in under the numbers they got.
    /// </summary>
    private static PlanOp NewSlotsOp(ReplacementPackage pkg, string id, List<ReplacementFile> files)
    {
        var w = files[0].SlotWearer ?? pkg.Wearer!;
        var ymtPath = files[0].SlotYmt ?? pkg.SlotsYmt!;
        // the model as the mod numbers it (its own name — a pack's collections are renumbered into one row)
        var models = files.Where(f => f.Part!.Kind == ClothingPartKind.Drawable)
                          .Select(f => $"{(ClothingNames.Parse(Path.GetFileName(f.Origin)) ?? f.Part!).Describe()} → {f.NewNumber}").Distinct().ToList();
        return new ActionOp(L.T($"Add {models.Count} new slot(s) to {w.Label}'s {w.Ymt} ({string.Join(", ", models.Take(4))}{(models.Count > 4 ? ", …" : "")}) " +
                            $"and put the models in (in copies of the archives under mods)"), ctx =>
        {
            var doc = PedVariation.Read(ctx.Overlay.Read(ymtPath) ?? throw new InvalidDataException(L.T($"{ymtPath} is not in the game.")));
            var dir = ymtPath[..ymtPath.LastIndexOf('/')];
            var drawables = PedVariation.DrawablesOf(files.Select(f => f.Part!));
            var numbers = PedVariation.Add(doc, drawables.Select(d => d.Drawable));
            ctx.Overlay.Put(id, ymtPath, PedVariation.Write(doc));
            for (int i = 0; i < drawables.Count; i++)
            {
                var (number, d) = drawables[i];
                foreach (var f in files.Where(f => f.Part!.Prop == d.Prop && f.Part.Slot == d.Slot && f.Part.Number == number))
                {
                    var name = ClothingNames.Renumber(f.Part!.Name, numbers[i]);
                    ctx.Overlay.Put(id, $"{dir}/{(d.Prop ? w.PropFolder : w.Folder)}/{name}", File.ReadAllBytes(f.Source));
                }
                var model = files.First(f => f.Part!.Prop == d.Prop && f.Part.Slot == d.Slot && f.Part.Number == number);
                var origin = ClothingNames.Parse(Path.GetFileName(model.Origin)) ?? model.Part!;
                ctx.Log(L.T($"    {w.Label}: {(d.Prop ? "prop " + ClothingNames.Anchors[d.Slot] : ClothingNames.Components[d.Slot])} {numbers[i]} " +
                        $"added ({d.Textures} texture(s)) — from the mod's {origin.Number:000}.") +
                        (model.TrainerOffset is { } offset ? L.T($" In a trainer: {ClothingNames.InTrainer(d.Prop, d.Slot, offset + numbers[i])}.") : ""));
            }
        });
    }
}
