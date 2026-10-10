using System.Globalization;

namespace Mdv.Core.Mods;

/// <summary>
/// The files of the game folder (an .asi, its .ini, a script) that several mods brought: the mods layer of the game folder.
/// There is no stack kept anywhere — it is read from the mods' journals: the mod that created the file is at the bottom, each
/// mod that came later moved the version before it aside (<see cref="MovedAside"/>), so its stash holds the version of the
/// mod under it and the file itself is the top one's. Stashes are named by transaction (time) and number, which gives the
/// order of the links.
/// <para>
/// Rechaining moves those versions between the stashes and the file so the links stand in another order (<see cref="Restack"/>
/// — the load order and the player's pins) or without a mod (<see cref="HandOver"/> — taking a mod out from under others,
/// which used to delete the upper mod's file). The mods' journals are rewritten to match (<see cref="InstallContext.Journals"/>).
/// </para>
/// </summary>
public static class GameFiles
{
    /// <summary>One mod's version of a file: the step that put it there (what was under it), and its later steps on the same file.</summary>
    /// <param name="Edit">the mod edited the version under it rather than bringing its own</param>
    public sealed record Link(string Mod, JournalStep First, IReadOnlyList<JournalStep> Rest, bool Edit);

    /// <summary>A file of the game folder that several mods have a version of, bottom first.</summary>
    /// <param name="Path">game-relative, as the journals have it</param>
    public sealed record Chain(string Path, IReadOnlyList<Link> Links)
    {
        /// <summary>The key pins and lists use: lower case, '/'.</summary>
        public string Key => KeyOf(Path);
        /// <summary>Mods, the one the game gets first.</summary>
        public IReadOnlyList<string> Mods => [.. Links.Select(l => l.Mod).Reverse()];
        /// <summary>A mod above the bottom edited the version under it: its version can't change places.</summary>
        public bool Fixed => Links.Skip(1).Any(l => l.Edit);
    }

    public static string KeyOf(string path) => path.Replace('\\', '/').Trim('/').ToLowerInvariant();

    private static string? PathOf(JournalStep s) => s switch
    {
        CreatedFile f => f.Path,
        MovedAside { Keep: true } m => m.Path,
        _ => null,
    };

    /// <summary>The files more than one of <paramref name="journals"/> (mod id → its journal) has a version of, by key.</summary>
    public static Dictionary<string, Chain> Chains(IEnumerable<KeyValuePair<string, List<JournalStep>>> journals)
    {
        var byPath = new Dictionary<string, List<Link>>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (mod, steps) in journals)
        {
            foreach (var g in steps.Where(s => PathOf(s) is not null).GroupBy(s => KeyOf(PathOf(s)!)))
            {
                var first = g.First();
                if (!byPath.TryGetValue(g.Key, out var links)) byPath[g.Key] = links = [];
                names.TryAdd(g.Key, PathOf(first)!);
                links.Add(new Link(mod, first, [.. g.Skip(1)], first is MovedAside { Edit: true }));
            }
        }
        var chains = new Dictionary<string, Chain>(StringComparer.Ordinal);
        foreach (var (key, links) in byPath)
        {
            // two mods that each created it (one was gone when the other came): no telling what is under what
            if (links.Count < 2 || links.Count(l => l.First is CreatedFile) > 1) continue;
            chains[key] = new Chain(names[key], [.. links.OrderBy(l => Rank(l.First), StashOrder.Instance)]);
        }
        return chains;
    }

    /// <summary>Where a link stands: a created file is the bottom, the rest by their stash (transaction, then number).</summary>
    private static (string Txn, long No) Rank(JournalStep first)
    {
        if (first is not MovedAside m) return ("", -1);
        var stash = m.Stash.Replace('\\', '/');
        var name = System.IO.Path.GetFileName(stash);
        var txn = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(stash) ?? "") ?? "";
        int digits = 0;
        while (digits < name.Length && char.IsAsciiDigit(name[digits])) digits++;
        long no = digits > 0 && long.TryParse(name.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return (txn, no);
    }

    private sealed class StashOrder : IComparer<(string Txn, long No)>
    {
        public static readonly StashOrder Instance = new();
        public int Compare((string Txn, long No) x, (string Txn, long No) y)
        {
            int c = string.CompareOrdinal(x.Txn, y.Txn);
            return c != 0 ? c : x.No.CompareTo(y.No);
        }
    }

    /// <summary>The game's chains as a plan sees them now: the registry's journals with the plan's changes.</summary>
    public static Dictionary<string, Chain> Chains(InstallContext ctx) => Chains(ctx.Journals());

    /// <summary>The game's chains from its registry (read only).</summary>
    public static Dictionary<string, Chain> Chains(string gameDir) =>
        Chains(ModRegistry.Load(gameDir).Mods.Select(m => KeyValuePair.Create(m.Id, m.Journal)));

    /// <summary>
    /// Where <paramref name="mod"/>'s version of <paramref name="path"/> is now (game-relative): the file itself, or — when
    /// another mod's version is above it in a chain — the stash of the link above.
    /// </summary>
    public static string VersionOf(IReadOnlyDictionary<string, Chain> chains, string mod, string path)
    {
        if (!chains.TryGetValue(KeyOf(path), out var chain)) return path;
        var links = chain.Links;
        for (int i = 0; i < links.Count - 1; i++)
            if (links[i].Mod == mod && links[i + 1].First is MovedAside above)
                return above.Stash;
        return path;
    }

    /// <summary>
    /// Put the versions of every shared file in <paramref name="order"/> (top first), a pinned mod (<paramref name="pins"/>,
    /// key → mod) on top whatever the order says. A file a mod edited stays as it is. Returns the files whose links moved.
    /// </summary>
    public static int Restack(InstallContext ctx, IReadOnlyList<string> order, IReadOnlyDictionary<string, string> pins)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        int moved = 0;
        foreach (var chain in Chains(ctx).Values)
        {
            var now = chain.Links.Select(l => l.Mod).ToList();
            // bottom first: the lowest in the order goes lowest, mods the order doesn't name stay under them
            var want = now.OrderByDescending(m => rank.GetValueOrDefault(m, int.MaxValue)).ToList();
            if (pins.TryGetValue(chain.Key, out var pinned) && want.Remove(pinned)) want.Add(pinned);
            if (want.SequenceEqual(now)) continue;
            if (chain.Fixed)
            {
                ctx.Log(L.T($"    [!] <game>/{chain.Path}: a mod edited the version under it — the order there stays."));
                continue;
            }
            Rechain(ctx, chain, want);
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// A mod is being taken out: where other mods have versions of the same files, its links leave their chains (the version
    /// above it gets what was under it), and the folders it created that hold other mods' files go to one of them. Returns
    /// its steps that are still its own to take back.
    /// </summary>
    public static List<JournalStep> HandOver(InstallContext ctx, string modId, IReadOnlyList<JournalStep> steps)
    {
        var others = ctx.Journals().Where(kv => kv.Key != modId).ToList();
        var mine = new List<KeyValuePair<string, List<JournalStep>>> { KeyValuePair.Create(modId, steps.ToList()) };
        var chains = Chains(others.Concat(mine));
        var gone = new HashSet<JournalStep>();
        foreach (var chain in chains.Values)
        {
            int at = chain.Links.ToList().FindIndex(l => l.Mod == modId);
            if (at < 0) continue;
            var link = chain.Links[at];
            gone.Add(link.First);
            foreach (var s in link.Rest) gone.Add(s);
            if (at + 1 < chain.Links.Count && chain.Links[at + 1].Edit)
                ctx.Log(L.T($"    [!] <game>/{chain.Path}: the mod above edited this one’s version — its changes stay in the file."));
            Rechain(ctx, chain, [.. chain.Links.Where(l => l.Mod != modId).Select(l => l.Mod)]);
        }
        // a folder it created where other mods put files too stays theirs (it used to go with everything in it)
        foreach (var dir in steps.OfType<CreatedDir>())
        {
            var prefix = KeyOf(dir.Path) + "/";
            var heir = others.FirstOrDefault(kv => kv.Value.Any(s => s switch
            {
                CreatedFile f => KeyOf(f.Path).StartsWith(prefix, StringComparison.Ordinal),
                CreatedDir d => KeyOf(d.Path).StartsWith(prefix, StringComparison.Ordinal),
                MovedAside m => KeyOf(m.Path).StartsWith(prefix, StringComparison.Ordinal),
                _ => false,
            }));
            if (heir.Key is null) continue;
            gone.Add(dir);
            ctx.Journals(heir.Key).Insert(0, dir);
        }
        return [.. steps.Where(s => !gone.Contains(s))];
    }

    /// <summary>
    /// Stand <paramref name="chain"/>'s links as <paramref name="mods"/> (bottom first, some of its mods): each version moves
    /// to the stash of the mod now above it, the top one's into the file; what was under the old bottom goes under the new
    /// one, versions of mods left out are dropped. Every move is journalled for a rollback; the mods' journals follow.
    /// </summary>
    private static void Rechain(InstallContext ctx, Chain chain, IReadOnlyList<string> mods)
    {
        var links = chain.Links;
        var file = ctx.Abs(chain.Path);
        string Abs(string rel) => ctx.Abs(rel);
        // where each version is now: under the bottom — the bottom's stash (null: no file before), a mod's — the stash of the one above it
        string? below = links[0].First is MovedAside b ? Abs(b.Stash) : null;
        var version = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < links.Count; i++)
            version[links[i].Mod] = i + 1 < links.Count ? Abs(((MovedAside)links[i + 1].First).Stash) : file;
        var byMod = links.ToDictionary(l => l.Mod, StringComparer.Ordinal);

        // the new stashes, bottom first (their numbers keep that order for the next read)
        var steps = new Dictionary<string, JournalStep>(StringComparer.Ordinal);
        for (int k = 0; k < mods.Count; k++)
        {
            var from = k == 0 ? below : version[mods[k - 1]];
            if (from is null)
            {
                steps[mods[k]] = new CreatedFile(chain.Path);
                continue;
            }
            var stash = ctx.Journal.NextStash(file);
            Move(from, stash);
            ctx.Journal.MovedWithin(from, stash);
            steps[mods[k]] = new MovedAside(chain.Path, ctx.Journal.Rel(stash), Keep: true, Edit: byMod[mods[k]].Edit);
        }
        // versions nobody keeps any more: the left-out mods', and their own in-between ones
        foreach (var l in links)
        {
            if (!mods.Contains(l.Mod) && version[l.Mod] is var v && v != file && Exists(v)) ctx.Journal.MoveAside(v, keep: false);
            foreach (var r in l.Rest.OfType<MovedAside>())
                if (Exists(Abs(r.Stash))) ctx.Journal.MoveAside(Abs(r.Stash), keep: false);
        }
        // the file: the new top's version
        var top = version[mods[^1]];
        if (top != file)
        {
            if (Exists(file)) ctx.Journal.MoveAside(file, keep: false);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Move(top, file);
            ctx.Journal.MovedWithin(top, file);
        }
        // the journals: each mod's steps on the file give way to its new one, where its first one was (a left-out mod's
        // journal is its remover's business)
        foreach (var l in links.Where(l => steps.ContainsKey(l.Mod)))
        {
            var journal = ctx.Journals(l.Mod);
            var drop = l.Rest.Prepend(l.First).ToHashSet();
            int at = journal.FindIndex(drop.Contains);
            journal.RemoveAll(drop.Contains);
            journal.Insert(Math.Max(0, at), steps[l.Mod]);
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Move(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }
}
