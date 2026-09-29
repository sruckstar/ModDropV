using Mdv.Core;
using Mdv.Core.Index;

namespace Mdv.Core.Mods;

/// <summary>One of the game's own MP clothing collections: its wearer, its ymt (a game path) and when the game loads it.</summary>
public sealed record GameCollection(Wearer Wearer, string Ymt, int Order);

/// <summary>
/// New MP clothes as new slots of the game's collections. A ped can only carry so many streaming dependencies (collection
/// ymts, creature metadata, cloth) and the game's own DLCs have used them up: one collection more — ModDrop V's, mpclothes',
/// any — and the game crashes as the MP ped loads (docs.gta.clothing, "Game Limits and Crashes"). So new clothes go in at
/// the end of the last collection the game loads for the ped — whatever game version it is — which moves no one's numbers;
/// a collection with no room left (128 models of a type) passes them to the one before.
/// </summary>
public sealed partial class ReplacementHandler
{
    /// <summary>Models of one type a collection's ymt can hold.</summary>
    public const int MaxPerType = 128;
    /// <summary>Props of one anchor a ped can have in all (an 8-bit index in single player).</summary>
    public const int MaxPropsPerAnchor = 255;

    /// <summary>
    /// The game's own collections of <paramref name="ped"/> (not add-on packs in mods), in the order the game loads them —
    /// the order trainers number the clothes in. A collection is the game's once a shop meta names it (its
    /// <c>dlcName</c>); a ymt no shop meta names isn't loaded (patchday5ng's copy of the heist clothes as
    /// <c>mp_m_heist_01</c>). The order is that of the packs carrying the shop metas: dlclist sorted by setup2
    /// <c>&lt;order&gt;</c> — the base packs in x64w.rpf too (beach 0, christmas 1, valentines 2, business 3…) — then
    /// the pack's content.xml. Checked against the game (Enhanced 1158): the texture counts of all 639 MP male tops.
    /// </summary>
    public static List<GameCollection> GameCollections(GameIndex index, string ped)
    {
        var registered = RegisteredCollections(index, ped);
        var list = new List<(GameCollection Coll, (int, int, int) Key)>();
        foreach (var g in index.Find($"{ped}_*.ymt", 5000).GroupBy(h => h.File.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (ClothingNames.WearerOfYmt(g.Key) is not { Collection: { } coll } w || !w.Ped.Equals(ped, StringComparison.OrdinalIgnoreCase)) continue;
            if (!g.Any(h => h.Active && Own(h, index.GameDir)) || g.FirstOrDefault(h => h.Winner) is not { } winner) continue;
            if (!registered.TryGetValue(coll, out var key)) continue;
            list.Add((new GameCollection(w, Norm(winner), 0), key));
        }
        return [.. list.OrderBy(c => c.Key).ThenBy(c => c.Coll.Wearer.Folder, StringComparer.OrdinalIgnoreCase)
                       .Select((c, i) => c.Coll with { Order = i })];
    }

    /// <summary>Not a file of an add-on pack in mods (onigiri) — a copy of a game pack is the game's.</summary>
    private static bool Own(FileHit h, string gameDir) => !(h.Installed && h.Role == ArchiveRole.Dlc && !GamePack(h, gameDir));

    /// <summary>
    /// Collections of <paramref name="ped"/> the game's shop metas register (<c>&lt;pedName&gt;</c> + <c>&lt;dlcName&gt;</c>),
    /// each with where it is registered first: (rank of the pack, setup2 order, place in the pack's content.xml).
    /// </summary>
    private static Dictionary<string, (int, int, int)> RegisteredCollections(GameIndex index, string ped)
    {
        var overlay = ModsOverlay.Load(index.GameDir);
        var packs = new Dictionary<string, (int Order, string Content)>(StringComparer.OrdinalIgnoreCase);
        (int Order, string Content) PackOf(string root)
        {
            if (packs.TryGetValue(root, out var known)) return known;
            string Text(string file)
            {
                try
                {
                    return overlay.Read($"{root}/{file}") is { } b ? System.Text.Encoding.UTF8.GetString(b) : "";
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                                 or NotSupportedException or Rpf.RpfFormatException)
                {
                    return "";
                }
            }
            var order = SetupOrderRe().Match(Text("setup2.xml")) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
            return packs[root] = (order, Text("content.xml"));
        }

        var result = new Dictionary<string, (int, int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in index.Find($"{ped}_*.meta", 5000).Where(h => h.Active && Own(h, index.GameDir)))
        {
            string text;
            try
            {
                text = overlay.Read(Norm(h)) is { } b ? System.Text.Encoding.UTF8.GetString(b) : "";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                             or NotSupportedException or Rpf.RpfFormatException)
            {
                continue;
            }
            if (!PedNameRe().Match(text).Groups[1].Value.Equals(ped, StringComparison.OrdinalIgnoreCase) ||
                DlcNameRe().Match(text) is not { Success: true } dlc) continue;
            var path = Norm(h);
            int at = path.IndexOf("/common/data/", StringComparison.OrdinalIgnoreCase);
            var (order, content) = at < 0 ? (0, "") : PackOf(path[..at]);
            int inContent = content.IndexOf(h.File.Name, StringComparison.OrdinalIgnoreCase);
            var key = (h.Rank, order, inContent < 0 ? int.MaxValue : inContent);
            var name = dlc.Groups[1].Value;
            if (!result.TryGetValue(name, out var cur) || key.CompareTo(cur) < 0) result[name] = key;
        }
        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<order\s+value=""(\d+)""")]
    private static partial System.Text.RegularExpressions.Regex SetupOrderRe();
    [System.Text.RegularExpressions.GeneratedRegex(@"<pedName>\s*([^<]*?)\s*</pedName>")]
    private static partial System.Text.RegularExpressions.Regex PedNameRe();
    [System.Text.RegularExpressions.GeneratedRegex(@"<dlcName>\s*([^<]*?)\s*</dlcName>")]
    private static partial System.Text.RegularExpressions.Regex DlcNameRe();

    /// <summary>A pack in mods (onigiri) that is a copy of one the game has (not an add-on).</summary>
    private static bool GamePack(FileHit h, string gameDir) =>
        h.InstalledPack is { } pack && Directory.Exists(Path.Combine(gameDir, "update", "x64", "dlcpacks", pack));

    /// <summary>Give every model a new slot in the last collection of its ped that has room for it.</summary>
    private static void ResolveSlots(ReplacementPackage pkg, GameIndex index)
    {
        pkg.SlotsYmt = null;
        pkg.SlotsProblem = null;
        pkg.SlotNotes.Clear();
        foreach (var f in pkg.Files)
        {
            f.Candidates.Clear();
            f.Target = null;
            f.Note = null;
            f.NewNumber = null;
            f.SlotWearer = null;
            f.SlotYmt = null;
        }
        var overlay = ModsOverlay.Load(index.GameDir);
        var counts = new Dictionary<string, (int[] Comps, int[] Props)?>(StringComparer.OrdinalIgnoreCase);
        (int[] Comps, int[] Props)? CountsOf(GameCollection c)
        {
            if (counts.TryGetValue(c.Ymt, out var known)) return known;
            try
            {
                var data = overlay.Read(c.Ymt);
                known = data is null ? null : PedVariation.Counts(PedVariation.Read(data));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Rpf.RpfFormatException)
            {
                known = null;
            }
            return counts[c.Ymt] = known;
        }

        foreach (var byPed in pkg.Files.Where(f => f.Part is not null).GroupBy(f => f.SlotPed ?? pkg.Wearer?.Ped ?? ClothingNames.MpMale))
        {
            var ped = byPed.Key;
            var files = byPed.ToList();
            var models = files.Where(f => f.Part!.Kind == ClothingPartKind.Drawable).Select(f => Key(f.Part!)).Distinct()
                              .OrderBy(k => k.Prop).ThenBy(k => k.Slot).ThenBy(k => k.Number).ToList();
            if (models.Count == 0) continue;
            var colls = GameCollections(index, ped);
            if (colls.Count == 0)
            {
                pkg.SlotsProblem = L.T($"the game has no {ClothingNames.PedLabel(ped)} clothing collections — is it complete?");
                continue;
            }
            var need = models.GroupBy(k => (k.Prop, k.Slot)).ToDictionary(g => g.Key, g => g.Count());
            GameCollection? pick = null;
            (int[] Comps, int[] Props) have = default;
            for (int i = colls.Count - 1; i >= 0 && pick is null; i--)
            {
                if (CountsOf(colls[i]) is not { } c) continue;
                if (need.All(n => (n.Key.Prop ? c.Props[n.Key.Slot] : c.Comps[n.Key.Slot]) + n.Value <= MaxPerType))
                    (pick, have) = (colls[i], c);
            }
            if (pick is null)
            {
                pkg.SlotsProblem = L.T($"no {ClothingNames.PedLabel(ped)} collection of the game has room for {models.Count} more model(s) of these types");
                continue;
            }
            if (pick != colls[^1])
                pkg.SlotNotes.Add(L.T($"{colls[^1].Wearer.Label} has no room for these clothes — they go into {pick.Wearer.Label}. The collections " +
                                      $"after it get new numbers: outfits saved in a trainer with their clothes may show other ones."));
            if (need.Keys.Any(k => k.Prop))
            {
                var total = new int[ClothingNames.Anchors.Length];
                foreach (var c in colls)
                    if (CountsOf(c) is { } cc)
                        for (int a = 0; a < total.Length; a++) total[a] += cc.Props[a];
                foreach (var (k, n) in need.Where(n => n.Key.Prop && total[n.Key.Slot] + n.Value > MaxPropsPerAnchor))
                    pkg.SlotNotes.Add(L.T($"{ClothingNames.PedLabel(ped)} has {total[k.Slot]} {ClothingNames.Anchors[k.Slot]} props already — past " +
                                          $"{MaxPropsPerAnchor} the game doesn't show them without a prop limit patch."));
            }

            // what trainers count before the collection: the ped's own set, then the collections loaded earlier
            (int[] Comps, int[] Props)? before = null;
            if (index.Find($"{ped}.ymt").FirstOrDefault(h => h.Winner) is { } own &&
                CountsOf(new GameCollection(new Wearer(ped), Norm(own), -1)) is { } baseCounts)
            {
                var (bc, bp) = ((int[])baseCounts.Comps.Clone(), (int[])baseCounts.Props.Clone());
                foreach (var c in colls.TakeWhile(c => c != pick))
                {
                    if (CountsOf(c) is not { } cc)
                    {
                        (bc, bp) = (null!, null!);
                        break;
                    }
                    for (int i = 0; i < bc.Length; i++) bc[i] += cc.Comps[i];
                    for (int i = 0; i < bp.Length; i++) bp[i] += cc.Props[i];
                }
                if (bc is not null) before = (bc, bp);
            }

            var w = pick.Wearer;
            var dir = pick.Ymt[..pick.Ymt.LastIndexOf('/')];
            var comps = (int[])have.Comps.Clone();
            var props = (int[])have.Props.Clone();
            foreach (var key in models)
            {
                int n = key.Prop ? props[key.Slot]++ : comps[key.Slot]++;
                int? offset = before is { } b ? (key.Prop ? b.Props[key.Slot] : b.Comps[key.Slot]) : null;
                foreach (var f in files.Where(f => Key(f.Part!) == key))
                {
                    f.NewNumber = n;
                    f.TrainerOffset = offset;
                    f.SlotWearer = w;
                    f.SlotYmt = pick.Ymt;
                    f.Target = $"{dir}/{(key.Prop ? w.PropFolder : w.Folder)}/{ClothingNames.Renumber(f.Part!.Name, n)}";
                    f.Candidates.Add(new ReplaceTarget(f.Target, L.T($"new slot {(key.Prop ? "prop " + ClothingNames.Anchors[key.Slot] : ClothingNames.Components[key.Slot])} {n} of {w.Label}"), true));
                    if (f.Part.Kind == ClothingPartKind.Drawable)
                        f.Note = f.TrainerNumber is { } tn
                            ? L.T($"new clothes for {w.Label} — it goes in as {ClothingNames.Renumber(f.Part.Name, n)}; in a trainer: {ClothingNames.InTrainer(key.Prop, key.Slot, tn)}")
                            : L.T($"new clothes for {w.Label} — it goes in as {ClothingNames.Renumber(f.Part.Name, n)}");
                }
            }
        }
    }

    /// <summary>"MP male · mp_m_2026_01: 1 new slot(s) — in a trainer: Tops (component 11) 653" for the library.</summary>
    private static string SlotsWhere(ReplacementPackage pkg) =>
        string.Join("; ", pkg.NewSlotFiles.Where(f => f.Part is { Kind: ClothingPartKind.Drawable })
                             .GroupBy(f => f.SlotWearer!.Label)
                             .Select(g =>
                             {
                                 var models = g.DistinctBy(f => (f.Part!.Prop, f.Part.Slot, f.NewNumber)).ToList();
                                 var inTrainer = TrainerList(models);
                                 return L.T($"{g.Key}: {models.Count} new slot(s)") + (inTrainer.Length > 0 ? L.T($" — in a trainer: {inTrainer}") : "");
                             }));

    /// <summary>"Tops (component 11) 653, Hats (prop 0) 201, …" — the first few models as trainers number them.</summary>
    public static string TrainerList(IEnumerable<ReplacementFile> models)
    {
        var list = models.Where(f => f.TrainerNumber is not null && f.Part is { Kind: ClothingPartKind.Drawable })
                         .DistinctBy(f => (f.Part!.Prop, f.Part.Slot, f.TrainerNumber))
                         .Select(f => ClothingNames.InTrainer(f.Part!.Prop, f.Part.Slot, f.TrainerNumber!.Value)).ToList();
        return string.Join(", ", list.Take(3)) + (list.Count > 3 ? ", …" : "");
    }
}
