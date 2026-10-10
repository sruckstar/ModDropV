namespace Mdv.Core.Mods;

/// <summary>Where a shared file is.</summary>
public enum ConflictArea
{
    /// <summary>Inside a game archive (the mods layer, <see cref="ModsOverlay"/>).</summary>
    Archive,
    /// <summary>In the game folder: an .asi, an .ini, a script (<see cref="GameFiles"/>).</summary>
    GameFolder,
    /// <summary>A dlclist.xml line several mods list — shown only: the pack is one, whoever listed it.</summary>
    Dlclist,
}

/// <summary>A file several installed mods have a version of.</summary>
/// <param name="Key">the mods layer key, the game-folder path (lower case) or <c>dlcpacks:/&lt;pack&gt;/</c></param>
/// <param name="Path">as the player reads it</param>
/// <param name="Mods">mod ids, the one whose version the game gets first</param>
/// <param name="Pinned">the mod the player picked for it (null: the load order decides)</param>
/// <param name="Fixed">the winner can't be changed: a mod edited the version under it (or it is a dlclist.xml line)</param>
public sealed record ContestedFile(ConflictArea Area, string Key, string Path, IReadOnlyList<string> Mods, string? Pinned, bool Fixed)
{
    public string Winner => Mods[0];
}

/// <summary>
/// Every file installed mods share, the winner of each and who else has a version: the game's archives (the mods layer), the
/// game folder (the mods' journals) and dlclist.xml lines. A winner can be picked per file (<see cref="PinFileOp"/>): it is
/// stronger than the load order, survives reorders, and goes when the pinned mod is removed (the order decides again).
/// </summary>
public static class FileConflicts
{
    /// <summary>The shared files of a game (read only), archive files first, then by path.</summary>
    public static List<ContestedFile> Of(string gameDir)
    {
        var reg = ModRegistry.Load(gameDir);
        var installed = reg.Mods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        string? Pin(Dictionary<string, string> pins, string key) => pins.TryGetValue(key, out var p) && installed.Contains(p) ? p : null;
        var list = new List<ContestedFile>();

        if (File.Exists(ModsOverlay.StatePath(gameDir)))
        {
            var overlay = ModsOverlay.Load(gameDir);
            string? config = null;
            try
            {
                config = overlay.KeyFor(GamePools.GameConfig);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // Onigiri has no place for it
            }
            foreach (var (key, entry) in overlay.State.Entries)
            {
                var mods = entry.Layers.Select(l => l.Mod).Where(m => m != GamePools.LimitsOwner).Reverse().Distinct().ToList();
                if (mods.Count < 2) continue;
                list.Add(new ContestedFile(ConflictArea.Archive, key, key, mods, Pin(reg.Pins, key),
                                          key == config || entry.Layers.Any(l => l.Edit)));
            }
        }

        foreach (var chain in GameFiles.Chains(reg.Mods.Select(m => KeyValuePair.Create(m.Id, m.Journal))).Values)
            list.Add(new ContestedFile(ConflictArea.GameFolder, chain.Key, chain.Path, chain.Mods, Pin(reg.FilePins, chain.Key), chain.Fixed));

        var order = ModOrder.Of(reg, ModOrder.StateOf(gameDir));
        int Place(string id) => order.IndexOf(id) is >= 0 and var i ? i : int.MaxValue;
        foreach (var g in reg.Mods.SelectMany(m => m.Journal.OfType<DlclistAdded>().Select(a => (Pack: a.Pack.Trim('/', '\\'), m.Id)))
                                  .GroupBy(x => x.Pack.ToLowerInvariant()))
        {
            var mods = g.Select(x => x.Id).Distinct().OrderBy(Place).ToList();
            if (mods.Count < 2) continue;
            list.Add(new ContestedFile(ConflictArea.Dlclist, $"dlcpacks:/{g.Key}/", $"dlclist.xml: dlcpacks:/{g.First().Pack}/", mods, null, true));
        }
        return [.. list.OrderBy(c => c.Area).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>
/// Give the game <paramref name="mod"/>'s version of one shared file whatever the load order says (null: back to the order).
/// The versions change places at the end of the plan, with the order (<see cref="InstallExecutor"/>).
/// </summary>
public sealed class PinFileOp(ConflictArea area, string key, string? mod, string? summary = null) : PlanOp
{
    public ConflictArea Area { get; } = area;
    public string Key { get; } = key;
    public string? Mod { get; } = mod;

    public override string Describe() => summary ?? (Mod is null
        ? L.T($"{Key}: back to the load order")
        : L.T($"{Key}: give the game {Mod}'s version"));

    public override void Execute(InstallContext ctx)
    {
        if (Area == ConflictArea.Dlclist) throw new NotSupportedException(L.T("A dlclist.xml line has no versions to pick from."));
        ctx.Pin(files: Area == ConflictArea.GameFolder, Key, Mod);
        if (Area == ConflictArea.Archive) _ = ctx.Overlay;        // the archives are restacked with the order at the end
    }
}
