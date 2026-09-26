using System.Diagnostics;
using System.Text.RegularExpressions;
using Mdv.Core.Rpf;

namespace Mdv.Core.Index;

/// <summary>Progress of an index build: archives done / total and the one just finished.</summary>
public readonly record struct IndexProgress(int Done, int Total, string Current, bool FromCache);

/// <summary>What part of the game a top-level archive is.</summary>
public enum ArchiveRole
{
    /// <summary><c>x64a–w.rpf</c>, <c>common.rpf</c>, loose <c>x64\…\*.rpf</c> — the base game.</summary>
    Base,
    /// <summary><c>update\update.rpf</c> / <c>update2.rpf</c> — title-update patches (and <c>dlc_patch\</c>).</summary>
    Update,
    /// <summary><c>update\x64\dlcpacks\&lt;pack&gt;\dlc*.rpf</c>.</summary>
    Dlc,
}

/// <summary>
/// One place a file lives. <see cref="GamePath"/> is where it physically is
/// (<c>x64e.rpf/levels/gta5/vehicles.rpf/adder.yft</c>); <see cref="LogicalPath"/> is where the
/// game sees it (<c>x64/levels/gta5/vehicles.rpf/adder.yft</c>, <c>dlcpacks/mpbattle/…</c>), which is
/// what patches line up on. <see cref="Winner"/>: this is the copy the game actually loads.
/// </summary>
public sealed record FileHit(
    IndexedArchive Archive,
    IndexedFile File,
    string InnerPath,
    string LogicalPath,
    ArchiveRole Role,
    string Source,
    int Rank,
    string? Inactive,
    bool Winner)
{
    public string GamePath => $"{Archive.RelPath}/{InnerPath}";
    public bool Active => Inactive is null;
    public bool InMods => Archive.RelPath.StartsWith(GameIndex.ModsPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Catalogue of every file in the game's archives — where it lives and which copy the game
/// loads. The same rules as the game (and CodeWalker) apply:
/// <list type="bullet">
/// <item>by path: <c>update.rpf</c> (then <c>update2.rpf</c>) patches the base archives
/// (<c>x64*.rpf</c> → <c>x64/…</c>, <c>common.rpf</c> → <c>common/…</c>); <c>update.rpf/dlc_patch/&lt;pack&gt;/</c>
/// patches that DLC pack;</item>
/// <item>streamed resources (<c>.yft/.ytd/.ydr/…</c> inside an image archive) are registered by
/// file name: the last one mounted wins — base, then the title update, then DLC packs in their
/// <c>setup2.xml</c> order (only the packs <c>dlclist.xml</c> lists are mounted);</item>
/// <item>an archive copied into <c>mods\</c> replaces the game's one as a whole.</item>
/// </list>
/// Built from the TOCs only; cached per archive, so a rebuild after a game update rereads
/// just the archives that changed (<see cref="GameIndexCache"/>).
/// </summary>
public sealed class GameIndex
{
    public const string ModsPrefix = "mods/";

    /// <summary>Extensions the streaming system registers by name (when they sit in an image archive).</summary>
    public static readonly HashSet<string> StreamedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ydr", ".ydd", ".yft", ".ytd", ".ybn", ".ycd", ".ymap", ".ytyp", ".ynv", ".ynd", ".yld", ".ypt",
        ".yed", ".ymf", ".ypdb", ".ywr", ".yvr", ".ymt", ".ysc", ".gfx", ".cut",
    };

    private sealed record ArchiveInfo(ArchiveRole Role, string GameRel, bool InMods, string? Dlc, int Rank,
                                      string Source, string? Inactive, string LogicalRoot);

    private readonly ArchiveInfo[] _info;
    private readonly Dictionary<string, int> _dlcPos;
    private readonly Lazy<Dictionary<string, List<(int Arc, int File)>>> _byName;

    public string GameDir { get; }
    public GameEdition? Edition { get; }
    /// <summary>Executable name + file version the index was built against.</summary>
    public string ExeVersion { get; }
    public IReadOnlyList<IndexedArchive> Archives { get; }
    /// <summary>DLC packs the game mounts, in load order (lower case).</summary>
    public IReadOnlyList<string> LoadedDlcs { get; }
    /// <summary>Where the effective <c>dlclist.xml</c> came from (null: none found — every pack counts as loaded).</summary>
    public string? DlcListSource { get; }
    public int FileCount { get; }
    /// <summary>Archives read in this run / taken from the cache.</summary>
    public int Scanned { get; init; }
    public int Reused { get; init; }
    public TimeSpan Elapsed { get; init; }

    internal GameIndex(string gameDir, string exeVersion, IReadOnlyList<IndexedArchive> archives)
    {
        GameDir = gameDir;
        Edition = GameEditions.Detect(gameDir);
        ExeVersion = exeVersion;
        Archives = archives;
        FileCount = archives.Sum(a => a.Files.Length);
        (_info, LoadedDlcs, DlcListSource) = Resolve(archives);
        _dlcPos = LoadedDlcs.Select((n, i) => (n, i)).ToDictionary(t => t.n, t => t.i, StringComparer.OrdinalIgnoreCase);
        _byName = new Lazy<Dictionary<string, List<(int, int)>>>(BuildNameMap);
    }

    // ------------------------------------------------------------------ build / open

    /// <summary>
    /// The index of <paramref name="gameDir"/>: taken from the cache in <paramref name="cacheRoot"/>
    /// where the archives haven't changed, the rest read from disk (then the cache is updated).
    /// A different game executable version drops the whole cache. <paramref name="cacheRoot"/>
    /// null: no cache, everything is read.
    /// </summary>
    public static GameIndex Open(string gameDir, string? cacheRoot = null, Action<IndexProgress>? progress = null,
                                 Action<string>? log = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        gameDir = Path.GetFullPath(gameDir);
        if (!Directory.Exists(gameDir)) throw new DirectoryNotFoundException($"No such folder: {gameDir}");
        var exe = ExeSignature(gameDir);
        var files = ArchiveFiles(gameDir);

        Dictionary<string, IndexedArchive> cached = [];
        string? cacheFile = cacheRoot is null ? null : GameIndexCache.FileFor(cacheRoot, gameDir);
        if (cacheFile is not null)
        {
            var loaded = GameIndexCache.TryLoad(cacheFile, out var cachedExe);
            if (loaded is not null && cachedExe != exe)
                log?.Invoke($"The game was updated ({cachedExe} → {exe}) — rebuilding the file index.");
            else if (loaded is not null)
                cached = loaded.ToDictionary(a => a.RelPath, StringComparer.OrdinalIgnoreCase);
        }

        var result = new IndexedArchive[files.Count];
        var todo = new List<int>();
        int done = 0;
        for (int i = 0; i < files.Count; i++)
        {
            var (full, rel) = files[i];
            // an archive that failed last time is read again (e.g. the keys couldn't be loaded then)
            if (cached.TryGetValue(rel, out var hit) && hit.Error is null && hit.IsCurrent(new FileInfo(full)))
            {
                result[i] = hit;
                progress?.Invoke(new IndexProgress(++done, files.Count, rel, true));
            }
            else todo.Add(i);
        }

        if (todo.Count > 0)
        {
            var crypto = TryCrypto(gameDir, log);
            if (cached.Count > 0) log?.Invoke($"Updating the file index: {todo.Count} archive(s) changed.");
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, i =>
            {
                var (full, rel) = files[i];
                result[i] = IndexedArchive.Scan(full, rel, crypto, ct);
                progress?.Invoke(new IndexProgress(Interlocked.Increment(ref done), files.Count, rel, false));
            });
            if (cacheFile is not null)
            {
                try
                {
                    GameIndexCache.Save(cacheFile, exe, result);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"Could not save the file index cache: {ex.Message}");
                }
            }
        }
        foreach (var i in todo.Where(i => result[i].Error is not null))
            log?.Invoke($"  {result[i].RelPath}: {result[i].Error}");

        return new GameIndex(gameDir, exe, result)
        {
            Scanned = todo.Count,
            Reused = files.Count - todo.Count,
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>
    /// The archives the game mounts — <c>*.rpf</c> in the game root, under <c>x64\</c> and
    /// <c>update\</c> — plus every <c>*.rpf</c> in <c>mods\</c>. Paths relative to the game, '/'-separated.
    /// </summary>
    public static List<(string Full, string Rel)> ArchiveFiles(string gameDir)
    {
        var list = new List<string>();
        list.AddRange(Directory.EnumerateFiles(gameDir, "*.rpf", SearchOption.TopDirectoryOnly));
        foreach (var sub in new[] { "x64", "update", "mods" })
        {
            var d = Path.Combine(gameDir, sub);
            if (Directory.Exists(d)) list.AddRange(Directory.EnumerateFiles(d, "*.rpf", SearchOption.AllDirectories));
        }
        return [.. list.Select(f => (f, Path.GetRelativePath(gameDir, f).Replace('\\', '/')))
                       .OrderBy(t => t.Item2, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"GTA5.exe 1.0.3586.0" — which build of the game the index belongs to.</summary>
    public static string ExeSignature(string gameDir)
    {
        foreach (var name in new[] { GameEditions.EnhancedExe, GameEditions.LegacyExe })
        {
            var exe = Path.Combine(gameDir, name);
            if (!File.Exists(exe)) continue;
            var fi = new FileInfo(exe);
            var ver = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            return string.IsNullOrWhiteSpace(ver) ? $"{name} ({fi.Length} bytes)" : $"{name} {ver.Trim()}";
        }
        return "(no game executable)";
    }

    private static GameCrypto? TryCrypto(string gameDir, Action<string>? log)
    {
        if (!File.Exists(Path.Combine(gameDir, GameEditions.LegacyExe)) &&
            !File.Exists(Path.Combine(gameDir, GameEditions.EnhancedExe)))
            return null;                                    // only OPEN archives can be read
        try
        {
            return GameCrypto.ForGame(gameDir, log);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            log?.Invoke($"Encrypted game archives will be skipped: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ load order

    private static (ArchiveInfo[] Info, List<string> Loaded, string? DlcListSource) Resolve(IReadOnlyList<IndexedArchive> archives)
    {
        var gameRels = new HashSet<string>(archives.Select(a => a.RelPath), StringComparer.OrdinalIgnoreCase);
        var basic = archives.Select(a => Classify(a.RelPath, gameRels)).ToArray();

        // the dlclist.xml the game reads: the highest-ranked active archive that carries one
        string? listSource = null;
        string[]? list = null;
        int best = int.MinValue;
        for (int i = 0; i < archives.Count; i++)
        {
            var b = basic[i];
            if (archives[i].DlcList is null || b.Shadowed || b.Role == ArchiveRole.Dlc) continue;
            if (b.Rank > best)
            {
                best = b.Rank;
                list = archives[i].DlcList;
                listSource = archives[i].RelPath;
            }
        }

        // packs: dlclist order, then stable-sorted by setup2 <order> (CodeWalker does the same)
        var packs = new Dictionary<string, DlcSetup?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < archives.Count; i++)
        {
            if (basic[i].Dlc is not { } d || basic[i].Shadowed) continue;
            if (!packs.TryGetValue(d, out var cur) || cur is null) packs[d] = archives[i].Setup;   // dlc.rpf has it, dlc1.rpf not
        }
        var listed = list is null
            ? packs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()
            : list.Where(packs.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var loaded = listed.Select((name, pos) => (name, pos))
                           .OrderBy(t => packs[t.name]?.Order ?? 0).ThenBy(t => t.pos)
                           .Select(t => t.name.ToLowerInvariant()).ToList();
        var dlcRank = loaded.Select((n, i) => (n, i)).ToDictionary(t => t.n, t => 1000 + t.i * 10, StringComparer.OrdinalIgnoreCase);

        var info = new ArchiveInfo[archives.Count];
        for (int i = 0; i < archives.Count; i++)
        {
            var b = basic[i];
            int rank = b.Rank;
            string? inactive = b.Shadowed ? $"replaced by {ModsPrefix}{b.GameRel}" : null;
            if (b.Dlc is { } d)
            {
                if (dlcRank.TryGetValue(d, out var r)) rank = r;
                else inactive ??= list is null ? "not mounted" : "not in dlclist.xml";
            }
            info[i] = new ArchiveInfo(b.Role, b.GameRel, b.InMods, b.Dlc, rank, b.Source, inactive, b.LogicalRoot);
        }
        return (info, loaded, listSource);
    }

    private sealed record Basic(ArchiveRole Role, string GameRel, bool InMods, bool Shadowed, string? Dlc, int Rank,
                                string Source, string LogicalRoot);

    private static Basic Classify(string rel, HashSet<string> allRels)
    {
        bool inMods = rel.StartsWith(ModsPrefix, StringComparison.OrdinalIgnoreCase);
        var gameRel = inMods ? rel[ModsPrefix.Length..] : rel;
        bool shadowed = !inMods && allRels.Contains(ModsPrefix + rel);
        var lower = gameRel.ToLowerInvariant();
        var parts = lower.Split('/');
        string where = inMods ? " (mods)" : "";

        if (lower is "update/update.rpf" or "update/update2.rpf")
            return new Basic(ArchiveRole.Update, gameRel, inMods, shadowed, null, lower == "update/update.rpf" ? 100 : 200,
                             Path.GetFileName(lower) + where, "");
        if (parts.Length == 5 && parts[0] == "update" && parts[1] == "x64" && parts[2] == "dlcpacks" &&
            Regex.IsMatch(parts[4], @"^dlc\d*\.rpf$"))
            return new Basic(ArchiveRole.Dlc, gameRel, inMods, shadowed, parts[3], 1000,
                             $"DLC {parts[3]}" + where, $"dlcpacks/{parts[3]}/");
        // base: x64a..w.rpf are mounted at x64/, common.rpf at common/, a loose archive at its own path
        string root = parts.Length == 1
            ? (lower.StartsWith("x64", StringComparison.Ordinal) ? "x64/" : lower.StartsWith("common", StringComparison.Ordinal) ? "common/" : lower + "/")
            : lower + "/";
        return new Basic(ArchiveRole.Base, gameRel, inMods, shadowed, null, 0, "base game" + where, root);
    }

    /// <summary>Logical path + rank of one file (a <c>dlc_patch</c> entry belongs to its pack, just above it).</summary>
    private (string Logical, int Rank, string Source, string? Inactive) Place(int arc, string innerLower)
    {
        var info = _info[arc];
        if (info.Role != ArchiveRole.Update || !innerLower.StartsWith("dlc_patch/", StringComparison.Ordinal))
            return (info.LogicalRoot + innerLower, info.Rank, info.Source, info.Inactive);
        int slash = innerLower.IndexOf('/', 10);
        if (slash < 0) return (innerLower, info.Rank, info.Source, info.Inactive);
        var pack = innerLower[10..slash];
        int pos = _dlcPos.GetValueOrDefault(pack, -1);
        return ($"dlcpacks/{pack}/{innerLower[(slash + 1)..]}",
                pos < 0 ? info.Rank : 1000 + pos * 10 + 5,
                $"{info.Source} → {pack}",
                info.Inactive ?? (pos < 0 ? $"patches {pack}, which isn't mounted" : null));
    }

    /// <summary>
    /// Files with the same key are versions of each other; the highest-ranked active one wins.
    /// A streamed resource is keyed by its name inside its image archive — the file name, or
    /// folder/name for ped components (<c>player_zero/uppr_000_u.ydd</c>); anything else by its logical path.
    /// </summary>
    private static string GroupKey(string innerLower, string logical, string nameLower)
    {
        int image = innerLower.LastIndexOf(".rpf/", StringComparison.Ordinal);
        return image > 0 && StreamedExts.Contains(Path.GetExtension(nameLower))
            ? "#" + innerLower[(image + 5)..]
            : logical;
    }

    // ------------------------------------------------------------------ queries

    private Dictionary<string, List<(int Arc, int File)>> BuildNameMap()
    {
        var map = new Dictionary<string, List<(int, int)>>(StringComparer.OrdinalIgnoreCase);
        for (int a = 0; a < Archives.Count; a++)
        {
            var files = Archives[a].Files;
            for (int f = 0; f < files.Length; f++)
            {
                if (!map.TryGetValue(files[f].Name, out var list)) map[files[f].Name] = list = [];
                list.Add((a, f));
            }
        }
        return map;
    }

    /// <summary>
    /// Every copy of the file(s) matching <paramref name="query"/>, the copy the game loads first
    /// in each group. The query is a file name (<c>adder.yft</c>), a name without extension
    /// (<c>adder</c>), a name with <c>*</c>/<c>?</c> wildcards, or the tail of a path
    /// (<c>player_zero/uppr_000_u.ydd</c>, <c>common/data/dlclist.xml</c>,
    /// <c>x64e.rpf/levels/gta5/vehicles.rpf/adder.yft</c>; <c>vehicles/adder.yft</c> matches
    /// <c>vehicles.rpf/adder.yft</c>). Case doesn't matter; '\' works as well as '/'.
    /// </summary>
    public List<FileHit> Find(string query, int limit = 1000)
    {
        var segs = query.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segs.Length == 0) return [];
        var name = segs[^1];
        var map = _byName.Value;

        IEnumerable<string> names;
        if (name.Contains('*') || name.Contains('?'))
        {
            var rx = new Regex("^" + Regex.Escape(name).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            names = map.Keys.Where(k => rx.IsMatch(k));
        }
        else if (map.ContainsKey(name)) names = [name];
        else if (!name.Contains('.'))
            names = map.Keys.Where(k => string.Equals(Path.GetFileNameWithoutExtension(k), name, StringComparison.OrdinalIgnoreCase));
        else names = [];

        var hits = new List<FileHit>();
        var winners = new Dictionary<string, (int Arc, int File)?>();
        foreach (var n in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (arc, file) in map[n])
            {
                var a = Archives[arc];
                var inner = a.PathOf(a.Files[file]);
                if (segs.Length > 1 && !TailMatches(segs, $"{a.RelPath}/{inner}") &&
                    !TailMatches(segs, Place(arc, inner.ToLowerInvariant()).Logical))
                    continue;
                hits.Add(MakeHit(arc, file, winners));
                if (hits.Count >= limit) break;
            }
            if (hits.Count >= limit) break;
        }
        return [.. hits.GroupBy(h => GroupKeyOf(h))
                       .OrderBy(g => g.Key, StringComparer.Ordinal)
                       .SelectMany(g => g.OrderByDescending(h => h.Winner).ThenByDescending(h => h.Active)
                                         .ThenByDescending(h => h.Rank).ThenBy(h => h.GamePath, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>The copy of <paramref name="query"/> the game loads (see <see cref="Find"/>), or null.
    /// With several different files matching, the first group's winner.</summary>
    public FileHit? Resolve(string query) => Find(query).FirstOrDefault(h => h.Winner);

    private static string GroupKeyOf(FileHit h) =>
        GroupKey(h.InnerPath.ToLowerInvariant(), h.LogicalPath, h.File.Name.ToLowerInvariant());

    private FileHit MakeHit(int arc, int file, Dictionary<string, (int, int)?> winners)
    {
        var a = Archives[arc];
        var f = a.Files[file];
        var inner = a.PathOf(f);
        var innerLower = inner.ToLowerInvariant();
        var (logical, rank, source, inactive) = Place(arc, innerLower);
        var key = GroupKey(innerLower, logical, f.Name.ToLowerInvariant());
        if (!winners.TryGetValue(key, out var win)) winners[key] = win = WinnerOf(key, f.Name);
        return new FileHit(a, f, inner, logical, _info[arc].Role, source, rank, inactive, win == (arc, file));
    }

    private (int, int)? WinnerOf(string key, string name)
    {
        (int, int)? best = null;
        int bestRank = int.MinValue;
        foreach (var (arc, file) in _byName.Value[name])
        {
            var a = Archives[arc];
            var innerLower = a.PathOf(a.Files[file]).ToLowerInvariant();
            var (logical, rank, _, inactive) = Place(arc, innerLower);
            if (inactive is not null || GroupKey(innerLower, logical, name.ToLowerInvariant()) != key) continue;
            if (rank >= bestRank)                  // ties: the later archive (x64g over x64d), like a later mount
            {
                bestRank = rank;
                best = (arc, file);
            }
        }
        return best;
    }

    /// <summary>Segment-wise tail match; a query segment "vehicles" also matches "vehicles.rpf".</summary>
    private static bool TailMatches(string[] segs, string path)
    {
        var parts = path.Split('/');
        if (parts.Length < segs.Length) return false;
        for (int i = 1; i <= segs.Length; i++)
        {
            var q = segs[^i];
            var p = parts[^i];
            if (string.Equals(p, q, StringComparison.OrdinalIgnoreCase)) continue;
            if (i > 1 && p.Length == q.Length + 4 && p.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) &&
                p.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                continue;
            return false;
        }
        return true;
    }
}
