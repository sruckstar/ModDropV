using System.Globalization;
using System.Security.Cryptography;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>How a problem of an installed mod is put right.</summary>
public enum RepairKind
{
    /// <summary>Nothing ModDrop V can do (only worth knowing).</summary>
    None,
    /// <summary>Install the mod again from its file (<see cref="ModSource.Path"/>).</summary>
    Reinstall,
    /// <summary>List its pack in dlclist.xml again.</summary>
    Dlclist,
    /// <summary>Take a fresh copy of a game archive in mods and put the mods' changes back (<see cref="ModsOverlay.Refresh"/>).</summary>
    Refresh,
}

/// <summary>Something wrong with an installed mod.</summary>
/// <param name="Path">the file (game-relative), the file inside an archive, or dlclist.xml</param>
/// <param name="Broken">false: worth knowing, but nothing is broken (a setting changed since the install)</param>
/// <param name="Archive">a <see cref="RepairKind.Refresh"/>: the copy to refresh</param>
public sealed record ModProblem(string Path, string What, RepairKind Fix, bool Broken = true, string? Archive = null);

/// <summary>How an installed mod stands (<see cref="ModCheck.Verify"/>).</summary>
/// <param name="Fingerprinted">its files' contents were recorded when it went in (else only their being there is checked)</param>
/// <param name="SourcePath">the file / folder it was installed from, when recorded</param>
/// <param name="Report">its last install report, when there is one</param>
public sealed record ModHealth(string Id, string Name, bool Enabled, IReadOnlyList<ModProblem> Problems, bool Fingerprinted,
                               string? SourcePath, string? Report)
{
    public bool Broken => Problems.Any(p => p.Broken);
    /// <summary>A broken file only installing it again can bring back.</summary>
    public bool NeedsReinstall => Problems.Any(p => p.Broken && p.Fix == RepairKind.Reinstall);
    public bool SourceThere => SourcePath is not null && (File.Exists(SourcePath) || Directory.Exists(SourcePath));
}

/// <summary>
/// Verify / repair: is every installed mod still the way its install left it? An install records each file it brought into
/// the game folder (length and SHA-256, <see cref="RegisteredMod.Files"/>); a check compares them, looks at the mod's versions
/// in the mods layer (the copy there, its entries, the versions kept aside), its packs in dlclist.xml, the limits it raised
/// and whether the copies are older than the game. What a plan can fix in place (dlclist lines, stale copies) goes into
/// <see cref="PlanRepair"/>; a broken file needs the mod installed again from its file.
/// </summary>
public static class ModCheck
{
    /// <summary>Settings files: a change to one is likely the player's own, not damage.</summary>
    private static readonly HashSet<string> Settings = new(StringComparer.OrdinalIgnoreCase)
        { ".ini", ".xml", ".json", ".txt", ".cfg", ".conf", ".config", ".toml", ".yaml", ".yml", ".log" };

    /// <summary><c>&lt;length&gt;:&lt;sha256&gt;</c> of a file, or null when it isn't there.</summary>
    public static string? FingerprintOf(string file)
    {
        if (!File.Exists(file)) return null;
        using var fs = File.OpenRead(file);
        var sha = SHA256.HashData(fs);
        return fs.Length.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexStringLower(sha);
    }

    private static long LengthOf(string fingerprint) =>
        long.TryParse(fingerprint.AsSpan(0, Math.Max(0, fingerprint.IndexOf(':'))), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;

    private static Dictionary<string, GameFiles.Chain> ChainsOf(ModRegistry reg) =>
        GameFiles.Chains(reg.Mods.Select(m => KeyValuePair.Create(m.Id, m.Journal)));

    /// <summary>
    /// The files a mod brought into the game folder, game-relative: the ones it created or replaced and the files in the folders
    /// it created — not other mods' files there, not ModDrop V's own (the mods layer's copies and stashes), not files it only edited.
    /// </summary>
    internal static List<string> OwnPaths(string gameDir, RegisteredMod mod, IEnumerable<RegisteredMod> all)
    {
        var home = GameFiles.KeyOf(ModsLayout.HomeRel(gameDir)) + "/";
        var root = GameFiles.KeyOf(ModsLayout.RootRel(gameDir));
        var theirs = all.Where(m => m.Id != mod.Id).SelectMany(m => m.Journal).Select(s => s switch
        {
            CreatedFile f => f.Path,
            MovedAside a => a.Path,
            _ => null,
        }).OfType<string>().Select(GameFiles.KeyOf).ToHashSet(StringComparer.Ordinal);
        var copies = CopiesOf(gameDir);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var edited = new HashSet<string>(StringComparer.Ordinal);
        void Add(string path)
        {
            var key = GameFiles.KeyOf(path);
            if (!key.StartsWith(home, StringComparison.Ordinal) && !copies.Contains(key)) paths.TryAdd(key, path.Replace('\\', '/'));
        }
        foreach (var s in mod.Journal)
            switch (s)
            {
                case CreatedFile f:
                    Add(f.Path);
                    break;
                case MovedAside { Keep: true, Edit: false } m:
                    Add(m.Path);
                    break;
                case MovedAside { Edit: true } m when !paths.ContainsKey(GameFiles.KeyOf(m.Path)):
                    edited.Add(GameFiles.KeyOf(m.Path));
                    break;
                case CreatedDir d when GameFiles.KeyOf(d.Path) != root:     // the mods folder itself is everyone's
                    var dir = InstallJournal.Abs(gameDir, d.Path);
                    if (!Directory.Exists(dir)) break;
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(gameDir, file).Replace('\\', '/');
                        var key = GameFiles.KeyOf(rel);
                        if (!theirs.Contains(key)) Add(rel);
                    }
                    break;
            }
        return [.. paths.Where(kv => !edited.Contains(kv.Key)).Select(kv => kv.Value)];
    }

    /// <summary>The mods layer's archive copies (keys): shared by every mod, never one mod's file.</summary>
    private static HashSet<string> CopiesOf(string gameDir)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(ModsOverlay.StatePath(gameDir))) return set;
        var overlay = ModsOverlay.Load(gameDir);
        foreach (var top in overlay.State.Copies.Keys) set.Add(GameFiles.KeyOf(overlay.Shown(top)));
        return set;
    }

    /// <summary>The fingerprints of a mod's files as they are now (its versions — in the file, or in a stash under another mod's).</summary>
    public static Dictionary<string, string> Fingerprint(string gameDir, RegisteredMod mod, ModRegistry reg) =>
        Fingerprint(gameDir, mod, reg, ChainsOf(reg));

    private static Dictionary<string, string> Fingerprint(string gameDir, RegisteredMod mod, ModRegistry reg,
                                                          IReadOnlyDictionary<string, GameFiles.Chain> chains)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in OwnPaths(gameDir, mod, reg.Mods))
            if (FingerprintOf(InstallJournal.Abs(gameDir, GameFiles.VersionOf(chains, mod.Id, path))) is { } fp)
                files[path] = fp;
        return files;
    }

    /// <summary>
    /// After a plan went through: fingerprint the mods it installed (<paramref name="fresh"/>), and take the new content of
    /// the files its steps touched for the other mods' records (a shared pack rebuilt, a ped's variants switched). Returns
    /// whether a record changed.
    /// </summary>
    public static bool Record(string gameDir, ModRegistry reg, IEnumerable<string> fresh, IReadOnlyList<JournalStep> steps)
    {
        var ids = fresh.ToHashSet(StringComparer.Ordinal);
        var touched = Touched(steps);
        if (ids.Count == 0 && touched.Count == 0) return false;
        var chains = ChainsOf(reg);
        bool changed = false;
        foreach (var m in reg.Mods)
        {
            if (ids.Contains(m.Id))
            {
                m.Files = Fingerprint(gameDir, m, reg, chains);
                changed = true;
                continue;
            }
            if (m.Files is not { Count: > 0 } files || touched.Count == 0) continue;
            foreach (var path in files.Keys.ToList())
            {
                var key = GameFiles.KeyOf(path);
                if (!touched.Any(t => key == t || key.StartsWith(t + "/", StringComparison.Ordinal))) continue;
                if (FingerprintOf(InstallJournal.Abs(gameDir, GameFiles.VersionOf(chains, m.Id, path))) is { } fp && fp != files[path])
                {
                    files[path] = fp;
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>Game paths (keys) a transaction wrote, moved or deleted.</summary>
    private static List<string> Touched(IReadOnlyList<JournalStep> steps) =>
        [.. steps.SelectMany(s => s switch
        {
            CreatedFile f => new[] { f.Path },
            CreatedDir d => [d.Path],
            MovedAside m => [m.Path],
            Moved mv => [mv.From, mv.To],
            _ => [],
        }).Select(GameFiles.KeyOf).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Check the installed mods (<paramref name="ids"/>, or all). <paramref name="quick"/> compares only the files' lengths
    /// (no hashing — fast enough to run by itself after a game update). Reads only.
    /// </summary>
    /// <param name="progress">called before each mod: the part done (0…1) and the mod's name</param>
    public static List<ModHealth> Verify(string gameDir, IReadOnlyCollection<string>? ids = null, bool quick = false,
                                         Action<double, string>? progress = null)
    {
        var reg = ModRegistry.Load(gameDir);
        var chains = ChainsOf(reg);
        var overlay = File.Exists(ModsOverlay.StatePath(gameDir)) ? ModsOverlay.Load(gameDir) : null;
        Dictionary<string, string> stale = [];
        try
        {
            stale = overlay?.Status().Where(s => s.Stale is not null)
                           .GroupBy(s => s.Archive, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Stale!) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the copies can't be looked at: their entries are checked one by one below
        }
        var unlisted = DlclistGuard.Missing(gameDir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mods = reg.Mods.Where(m => ids is null || ids.Contains(m.Id)).ToList();
        var list = new List<ModHealth>();
        for (int i = 0; i < mods.Count; i++)
        {
            var m = mods[i];
            progress?.Invoke(i / (double)mods.Count, m.Name);
            var problems = new List<ModProblem>();
            if (m.Enabled)
            {
                CheckFiles(gameDir, m, reg, chains, quick, problems);
                CheckJournal(gameDir, m, unlisted, problems);
            }
            if (overlay is not null)
            {
                foreach (var (key, what) in overlay.Check(m.Id))
                    problems.Add(new ModProblem(key, what, RepairKind.Reinstall));
                if (m.Enabled)
                    foreach (var place in overlay.PlacesOf(m.Id))
                        if (stale.TryGetValue(place, out var why))
                            problems.Add(new ModProblem(overlay.Shown(place), L.T($"the copy is out of date: {why}"), RepairKind.Refresh, Archive: place));
            }
            list.Add(new ModHealth(m.Id, m.Name, m.Enabled, problems, m.Files is not null, m.Source?.Path, m.Report));
        }
        progress?.Invoke(1, "");
        return list;
    }

    private static void CheckFiles(string gameDir, RegisteredMod m, ModRegistry reg, IReadOnlyDictionary<string, GameFiles.Chain> chains,
                                   bool quick, List<ModProblem> problems)
    {
        // installed before fingerprints were kept: only whether its files are there
        var files = m.Files ?? OwnPaths(gameDir, m, reg.Mods).ToDictionary(p => p, _ => "", StringComparer.Ordinal);
        foreach (var (path, fp) in files)
        {
            var abs = InstallJournal.Abs(gameDir, GameFiles.VersionOf(chains, m.Id, path));
            if (!File.Exists(abs))
            {
                problems.Add(new ModProblem(path, L.T("missing"), RepairKind.Reinstall));
                continue;
            }
            if (fp.Length == 0) continue;
            bool same = new FileInfo(abs).Length == LengthOf(fp) && (quick || FingerprintOf(abs) == fp);
            if (same) continue;
            problems.Add(Settings.Contains(Path.GetExtension(path))
                ? new ModProblem(path, L.T("changed since it was installed — your own settings, maybe"), RepairKind.None, Broken: false)
                : new ModProblem(path, L.T("changed since it was installed — something wrote over it"), RepairKind.Reinstall));
        }
    }

    private static void CheckJournal(string gameDir, RegisteredMod m, HashSet<string> unlisted, List<ModProblem> problems)
    {
        foreach (var s in m.Journal)
            switch (s)
            {
                case MovedAside { Keep: true } a when !File.Exists(InstallJournal.Abs(gameDir, a.Stash))
                                                      && !Directory.Exists(InstallJournal.Abs(gameDir, a.Stash)):
                    problems.Add(new ModProblem(a.Path, L.T("the file it replaced, kept to bring back when it is removed, is gone"),
                                                RepairKind.None, Broken: false));
                    break;
                case DlclistAdded p when unlisted.Contains(p.Pack):
                    problems.Add(new ModProblem("dlclist.xml", L.T($"doesn't list its pack {p.Pack} — the game doesn't load it"), RepairKind.Dlclist));
                    break;
                case IniKeySet k:
                    var ini = InstallJournal.Abs(gameDir, k.Path);
                    if (!File.Exists(ini))
                        problems.Add(new ModProblem(k.Path, L.T($"is gone — {k.Key} = {k.New} isn't set any more"), RepairKind.Reinstall));
                    else if (LimitAdjusters.ReadInt(TextIo.ReadText(ini), k.Section, k.Key) is var now && (now ?? int.MinValue) < k.New)
                        problems.Add(new ModProblem(k.Path, L.T($"{k.Key} is {now?.ToString(CultureInfo.InvariantCulture) ?? L.T("not set")} — it was raised to {k.New}"),
                                                    RepairKind.None, Broken: false));
                    break;
            }
    }

    /// <summary>
    /// The steps that fix in place what <paramref name="health"/> found broken: the packs dlclist.xml lost, the copies older
    /// than the game. Null when there are none — the rest needs the mods installed again (<see cref="ModHealth.NeedsReinstall"/>).
    /// </summary>
    public static InstallPlan? PlanRepair(IEnumerable<ModHealth> health)
    {
        var problems = health.SelectMany(h => h.Problems).Where(p => p.Broken).ToList();
        var plan = new InstallPlan { Title = L.T("Repairing mods") };
        if (problems.Any(p => p.Fix == RepairKind.Dlclist)) plan.Add(DlclistGuard.RelistOp());
        var archives = problems.Where(p => p.Fix == RepairKind.Refresh && p.Archive is not null).Select(p => p.Archive!)
                               .Distinct(StringComparer.Ordinal).ToList();
        if (archives.Count > 0) plan.Add(new RefreshCopiesOp(archives));
        return plan.Ops.Count == 0 ? null : plan;
    }
}
