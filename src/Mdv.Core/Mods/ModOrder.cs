namespace Mdv.Core.Mods;

/// <summary>
/// The load order of a game's mods (<see cref="ModRegistry.Order"/>, the first one wins): where mods change the same file
/// inside the game's archives, the one higher up gives the game its version (<see cref="ModsOverlay.Restack"/>); with
/// <see cref="ModRegistry.OrderPacks"/> their add-on packs are listed in dlclist.xml the same way (a later pack wins).
/// A new mod goes on top, a reinstalled one keeps its place. A registry from before the order had one gets it from the
/// mods layer: who is on top of whom now, else the newest install higher.
/// </summary>
public static class ModOrder
{
    /// <summary>The order as it stands: the recorded one, mods it misses on top (an older ModDrop V installed them since).</summary>
    public static List<string> Of(ModRegistry reg, OverlayState? state)
    {
        var known = reg.Mods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var order = reg.Order.Where(known.Contains).Distinct().ToList();
        if (order.Count == 0) return Migrated(reg, state);
        var missing = reg.Mods.Where(m => !order.Contains(m.Id)).OrderByDescending(When).Select(m => m.Id).ToList();
        order.InsertRange(0, missing);
        return order;
    }

    /// <summary>The order of the game in <paramref name="gameDir"/> (read only).</summary>
    public static List<string> Of(string gameDir) => Of(ModRegistry.Load(gameDir), StateOf(gameDir));

    /// <summary>The mods layer's state on disk, or null.</summary>
    public static OverlayState? StateOf(string gameDir) =>
        File.Exists(ModsOverlay.StatePath(gameDir)) ? ModsOverlay.Load(gameDir).State : null;

    private static DateTime When(RegisteredMod m) => m.Updated ?? m.Installed;

    /// <summary>
    /// The first order of a registry: each time, of the mods no other unplaced mod covers in the mods layer, the newest
    /// install (a mod installed later went on top); a loop (two mods each on top somewhere) is broken the same way.
    /// </summary>
    private static List<string> Migrated(ModRegistry reg, OverlayState? state)
    {
        var mods = reg.Mods.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var above = mods.Keys.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var entry in state?.Entries.Values ?? Enumerable.Empty<OwnedEntry>())
        {
            var ids = entry.Layers.Select(l => l.Mod).Where(mods.ContainsKey).ToList();
            for (int i = 0; i < ids.Count; i++)
                for (int j = i + 1; j < ids.Count; j++)
                    if (ids[i] != ids[j]) above[ids[i]].Add(ids[j]);
        }
        var order = new List<string>();
        var left = mods.Values.OrderByDescending(When).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(m => !above[m.Id].Any(a => left.Exists(l => l.Id == a))) ?? left[0];
            order.Add(next.Id);
            left.Remove(next);
        }
        return order;
    }

    /// <summary>The order after a plan: mods it removed go, mods new to the game go on top (the last one installed highest).</summary>
    public static List<string> After(IEnumerable<string> order, IEnumerable<string> registered, IEnumerable<string> unregistered)
    {
        var gone = unregistered.ToHashSet(StringComparer.Ordinal);
        var list = order.Where(id => !gone.Contains(id)).ToList();
        foreach (var id in registered)
            if (!list.Contains(id)) list.Insert(0, id);
        return list;
    }

    /// <summary><paramref name="id"/> moved to <paramref name="position"/> (0 = top; past the end = the bottom).</summary>
    public static List<string> Moved(IEnumerable<string> order, string id, int position)
    {
        var list = order.ToList();
        if (!list.Remove(id)) throw new ArgumentException(L.T($"{id} is not an installed mod of this game."));
        list.Insert(Math.Clamp(position, 0, list.Count), id);
        return list;
    }

    /// <summary>
    /// A full order from the ids given first, then the rest as they were (a partial <c>order set</c>: the mods named go on
    /// top in that order).
    /// </summary>
    public static List<string> Put(IEnumerable<string> order, IReadOnlyList<string> first)
    {
        var list = order.ToList();
        if (first.FirstOrDefault(id => !list.Contains(id)) is { } unknown)
            throw new ArgumentException(L.T($"{unknown} is not an installed mod of this game."));
        return [.. first.Distinct(), .. list.Where(id => !first.Contains(id))];
    }

    /// <summary>
    /// The add-on pack folders of the mods → their place in the order (0 = top); null when packs don't follow it.
    /// </summary>
    /// <param name="order">the order a plan is setting, packs following it (else the registry's, if they do)</param>
    public static Dictionary<string, int>? PackRanks(string gameDir, IReadOnlyList<string>? order = null)
    {
        var reg = ModRegistry.Load(gameDir);
        if (order is null && !reg.OrderPacks) return null;
        order ??= Of(reg, StateOf(gameDir));
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < order.Count; i++) at.TryAdd(order[i], i);
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in reg.Mods)
        {
            if (!at.TryGetValue(m.Id, out var rank)) continue;
            foreach (var own in m.Owns)
                if (ModsLayout.PackFolderOf(own.TrimEnd('/') + "/dlc.rpf") is { } pack) ranks.TryAdd(pack, rank);
        }
        return ranks;
    }

    /// <summary>
    /// Installed mod → the other mods that change some of the same files in the game's archives or folder, and whether its
    /// versions are on top in all of them. The shared limits are no mod; add-on packs that raised pools of their own share
    /// gameconfig.xml without clashing — each builds on the one below. Reads only.
    /// </summary>
    public static Dictionary<string, (List<string> Others, bool OnTop)> Conflicts(string gameDir, IEnumerable<string> mods)
    {
        var result = new Dictionary<string, (List<string>, bool)>(StringComparer.Ordinal);
        var reg = ModRegistry.Load(gameDir);
        var files = GameFiles.Chains(reg.Mods.Select(m => KeyValuePair.Create(m.Id, m.Journal))).Values.Select(c => c.Mods).ToList();
        var overlay = File.Exists(ModsOverlay.StatePath(gameDir)) ? ModsOverlay.Load(gameDir) : null;
        if (overlay is null && files.Count == 0) return result;
        var raisers = reg.Mods.Where(r => r.Get("pools") == "1").Select(r => r.Id).ToHashSet();
        foreach (var id in mods)
        {
            var shared = (overlay?.PathsOf(id) ?? [])
                                .Select(p => p.Equals(GamePools.GameConfig, StringComparison.OrdinalIgnoreCase) && raisers.Contains(id)
                                    ? [.. overlay!.OwnersOf(p).Where(o => o == id || !raisers.Contains(o))]
                                    : overlay!.OwnersOf(p))
                                .Select(o => o.Where(x => x != GamePools.LimitsOwner).ToList())
                                .Concat(files.Where(f => f.Contains(id)).Select(f => f.ToList()))
                                .Where(o => o.Count > 1).ToList();
            var others = shared.SelectMany(o => o).Where(o => o != id).Distinct().ToList();
            if (others.Count > 0) result[id] = (others, shared.All(o => o[0] == id));
        }
        return result;
    }

    /// <summary>
    /// What a plan's place in the order means, for the plan: a new mod that shares files with others goes over them, a
    /// reinstalled one stays under the ones above it.
    /// </summary>
    public static List<string> Hints(InstallPlan plan, string gameDir)
    {
        var hints = new List<string>();
        if (!File.Exists(ModsOverlay.StatePath(gameDir))) return hints;
        var byMod = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var op in plan.Ops)
        {
            var (mod, path) = op switch
            {
                RpfPutOp p => (p.ModId, p.GamePath),
                RpfDeleteOp d => (d.ModId, d.GamePath),
                _ => (null, null),
            };
            if (mod is null || path is null || mod == GamePools.LimitsOwner) continue;
            if (!byMod.TryGetValue(mod, out var paths)) byMod[mod] = paths = [];
            paths.Add(path);
        }
        if (byMod.Count == 0) return hints;
        ModsOverlay overlay;
        try
        {
            overlay = ModsOverlay.Load(gameDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return hints;
        }
        var reg = ModRegistry.Load(gameDir);
        var order = Of(reg, overlay.State);
        string Name(string id) => reg.Find(id)?.Name is { Length: > 0 } n ? n : id;
        foreach (var (mod, paths) in byMod)
        {
            List<string> others;
            try
            {
                others = [.. overlay.Conflicts(mod, paths).SelectMany(c => c.Owners).Where(o => order.Contains(o)).Distinct()];
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                continue;
            }
            if (others.Count == 0) continue;
            int at = order.IndexOf(mod);
            if (at < 0)
            {
                hints.Add(L.T($"It goes on top of the load order, over {string.Join(", ", others.Select(Name))} where they change the same files — Library → Load order moves it."));
                continue;
            }
            var over = others.Where(o => order.IndexOf(o) < at).Select(Name).ToList();
            if (over.Count > 0)
                hints.Add(L.T($"It keeps its place in the load order: {string.Join(", ", over)} stay(s) over it where they change the same files — Library → Load order moves it."));
        }
        return hints;
    }
}
