using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mdv.Core.Mods;

/// <summary>
/// The limit plugins a game gets with any mod, all of them its edition has (shipped in data/plugins/limits):
/// Heap Adjuster and Packfile Limit Adjuster (Chiheb-Bacha's builds — one .asi for Legacy and Enhanced) for both; Weapon
/// Limits Adjuster — alexguirre's for Legacy, ModDrop V's own port (plugins/WeaponLimitsAdjusterEnhanced) for Enhanced,
/// room for weapon components past the game's; Pool Heap Adjuster (own, plugins/PoolHeapAdjusterEnhanced) for Enhanced
/// only — it raises the 136 MB budget Enhanced keeps for its pools apart from the game heap, so raised gameconfig.xml pool
/// sizes fit (and the radio station limit, 96 → 255). The raised gameconfig.xml limits are allocated from the game heap at
/// start: without Heap Adjuster GTA V Legacy stops on loading with "Out of memory", and Enhanced crashes.
/// They stay when the last mod is removed — without mods they change nothing that matters.
/// A plugin the game has is kept (its .ini too), unless it can't work there: in Enhanced a Legacy-only build (Heap
/// Adjuster has the same file name in both) is replaced. Any build of a plugin for the edition counts, whatever its name
/// (NaturalVision brings GTAV.HeapAdjuster.asi) — two of them would patch the same code. Their settings are only ever
/// raised, and only where ModDrop V can read them; an uninstall puts back a value it set only while it is still there.
/// </summary>
public static class LimitAdjusters
{
    public const string Folder = "limits";

    public const string WeaponAsi = "WeaponLimitsAdjusterEnhanced.asi";
    public const string LegacyWeaponAsi = "WeaponLimitsAdjuster.asi";

    /// <summary>The heap Heap Adjuster gives with mods (MB): the game's own is 478 in Legacy, 648 in Enhanced.</summary>
    public const int HeapMb = 1024;
    /// <summary>Heap Adjuster multiplies its HEAP_SIZE into a 32-bit number of bytes: from 2048 MB on it wraps around.</summary>
    public const int MaxHeapMb = 2047;

    /// <summary>
    /// The plugins: the editions each is for, its files, and <c>Present</c> — the file pattern of the ones that count as
    /// already there.
    /// </summary>
    public static readonly (string Name, bool Legacy, bool Enhanced, string Asi, string Ini, string Present)[] Plugins =
    [
        ("Heap Adjuster", true, true, "HeapAdjuster.asi", "HeapAdjuster.ini", HeapPresent),
        ("Packfile Limit Adjuster", true, true, "PackfileLimitAdjusterEnhanced.asi", "PackfileLimitAdjusterEnhanced.ini", PackfilePresent),
        ("Weapon Limits Adjuster", true, false, LegacyWeaponAsi, "WeaponLimitsAdjuster.ini", "*WeaponLimitsAdjuster*.asi"),
        ("Weapon Limits Adjuster", false, true, WeaponAsi, "WeaponLimitsAdjusterEnhanced.ini", "*WeaponLimitsAdjuster*.asi"),
        ("Pool Heap Adjuster", false, true, "PoolHeapAdjusterEnhanced.asi", "PoolHeapAdjusterEnhanced.ini", "*PoolHeapAdjuster*.asi"),
    ];

    private const string HeapPresent = "*HeapAdjuster*.asi";
    private const string PackfilePresent = "*PackfileLimitAdjuster*.asi";

    /// <summary>
    /// The plugins in the game folder <paramref name="present"/> matches (a Pool Heap Adjuster is no Heap Adjuster); only
    /// <c>.asi</c> files — not one switched off as <c>.asi.off</c>.
    /// </summary>
    internal static List<string> Found(string gameDir, string present)
    {
        if (!Directory.Exists(gameDir)) return [];
        var re = new Regex("^" + Regex.Escape(present).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase);
        return [.. Directory.EnumerateFiles(gameDir, "*.asi")
                            .Where(f => Path.GetExtension(f).Equals(".asi", StringComparison.OrdinalIgnoreCase))
                            .Where(f => re.IsMatch(Path.GetFileName(f)))
                            .Where(f => present != HeapPresent || !Path.GetFileName(f).Contains("PoolHeap", StringComparison.OrdinalIgnoreCase))
                            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static readonly byte[] EnhancedMark = Encoding.ASCII.GetBytes(GameEditions.EnhancedExe);
    private static readonly byte[] EnhancedMarkWide = Encoding.Unicode.GetBytes(GameEditions.EnhancedExe);

    /// <summary>An .asi made for GTA V Enhanced: it looks for the Enhanced executable by name (in ASCII or UTF-16).</summary>
    public static bool ForEnhanced(string asi)
    {
        try
        {
            var bytes = File.ReadAllBytes(asi).AsSpan();
            return bytes.IndexOf(EnhancedMark) >= 0 || bytes.IndexOf(EnhancedMarkWide) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;                                 // can't tell: leave it alone
        }
    }

    /// <summary>
    /// Does a plugin in the game folder do this job in <paramref name="edition"/>? In Enhanced only builds that know it
    /// (Legacy ones patch the wrong code); in Legacy any build — Heap Adjuster and Packfile Limit Adjuster for Enhanced work
    /// there too — but the weapon one only when made for Legacy (ModDrop V's Enhanced port knows Enhanced alone).
    /// </summary>
    private static bool Counts(string asi, GameEdition edition, string present) =>
        edition == GameEdition.Enhanced ? ForEnhanced(asi)
        : !present.Contains("WeaponLimits", StringComparison.OrdinalIgnoreCase) || !ForEnhanced(asi);

    /// <summary>
    /// The files to copy into the game folder (source, game-relative): each plugin of the edition it lacks (or, in
    /// Enhanced, has only for Legacy), with its settings when it has none or the plugin is replaced. Empty when the
    /// bundled files are missing.
    /// </summary>
    public static List<(string Source, string GameRel)> Missing(string gameDir, GameEdition edition, string pluginsDir)
    {
        var list = new List<(string, string)>();
        var dir = Path.Combine(pluginsDir, Folder);
        foreach (var (_, legacy, enhanced, asi, ini, present) in Plugins)
        {
            if (!(edition == GameEdition.Enhanced ? enhanced : legacy) || !File.Exists(Path.Combine(dir, asi))) continue;
            var there = Path.Combine(gameDir, asi);
            if (Found(gameDir, present).Any(a => Counts(a, edition, present))) continue;
            list.Add((Path.Combine(dir, asi), asi));
            if (File.Exists(Path.Combine(dir, ini)) && (File.Exists(there) || !File.Exists(Path.Combine(gameDir, ini))))
                list.Add((Path.Combine(dir, ini), ini));
        }
        return list;
    }

    /// <summary>The step that installs them — null when the game has them.</summary>
    public static PlanOp? Op(string gameDir, GameEdition edition, string pluginsDir)
    {
        var files = Missing(gameDir, edition, pluginsDir);
        if (files.Count == 0) return null;
        var names = Plugins.Where(p => files.Any(f => f.GameRel == p.Asi)).Select(p => p.Name).Distinct();
        return new CopyFilesOp(files, L.T($"Install {string.Join(" + ", names)} for {edition.DisplayName()} — the raised limits need them, or the game runs out of memory on loading"))
        {
            LimitPlugins = true,
        };
    }

    // ================================================================ settings

    /// <summary>A number in a plugin's .ini: the build of ours it belongs to, its section and key, and what it is when absent.</summary>
    private sealed record Setting(string Asi, string Section, string Key, int Default);

    private static readonly Setting Heap = new("HeapAdjuster.asi", "HEAP_SETTINGS", "HEAP_SIZE", HeapMb);
    // Packfile Limit Adjuster puts its size in place of the gameconfig.xml ArchiveCount the game reads: it must not be smaller
    private static readonly Setting Packfiles = new("PackfileLimitAdjusterEnhanced.asi", "PACKFILE_SETTINGS", "PACKFILEARRAY_SIZE", 12000);
    private static readonly Setting OldPackfiles = new("PackfileLimitAdjuster.asi", "SETTINGS", "packfile_list_size", 0);

    /// <summary>The heap HEAP_SIZE gives (MB): what the 32-bit product wraps to from 2048 on, 0 when that is nothing.</summary>
    public static int EffectiveHeapMb(long heapSize) => Math.Max(0, unchecked((int)(heapSize * 1024 * 1024)) / (1024 * 1024));

    /// <summary>A setting to raise: the .ini (game-relative), what it holds now (null: not there) and what it needs.</summary>
    private sealed record Low(string Ini, Setting Setting, int? Now, int To);

    /// <summary>
    /// The plugin settings that are lower than the limits need (only ever raised): Heap Adjuster's heap to
    /// <paramref name="heapMb"/>, Packfile Limit Adjuster's archive count to <paramref name="archives"/> (the game's
    /// ArchiveCount, which it replaces). For the plugins in the game folder, or about to be copied there
    /// (<paramref name="copied"/>: their settings are read from the bundled files).
    /// </summary>
    public static List<(string Ini, string Key, int? Now, int To)> LowSettings(string gameDir, int heapMb, int archives,
        IReadOnlyList<(string Source, string GameRel)>? copied = null) =>
        [.. Check(gameDir, heapMb, archives, copied).Low.Select(l => (l.Ini, l.Setting.Key, l.Now, l.To))];

    /// <summary>What a plan says about the plugins' settings it leaves alone (a heap it can't read as the player meant it, two Heap Adjusters).</summary>
    public static List<string> Notes(string gameDir, GameEdition edition, string pluginsDir, int heapMb, int archives) =>
        Check(gameDir, heapMb, archives, Missing(gameDir, edition, pluginsDir)).Notes;

    /// <summary>
    /// Each plugin of the job (any build — <see cref="Found"/>, or ours about to be copied) with the .ini beside it, read as
    /// the setting its contents show. A build of its own whose .ini ModDrop V can't read is left alone; so is a heap of 2048
    /// MB or more — Chiheb-Bacha's build wraps it around (3000 → 0 MB), but another may take it as it is: never lowered.
    /// </summary>
    private static (List<Low> Low, List<string> Notes) Check(string gameDir, int heapMb, int archives,
        IReadOnlyList<(string Source, string GameRel)>? copied)
    {
        var low = new List<Low>();
        var notes = new List<string>();
        foreach (var (present, need, settings) in new[] { (HeapPresent, Math.Min(heapMb, MaxHeapMb), new[] { Heap }),
                                                         (PackfilePresent, archives, new[] { Packfiles, OldPackfiles }) })
        {
            if (need <= 0) continue;
            var ours = copied?.Select(c => c.GameRel).Where(r => settings.Any(x => x.Asi.Equals(r, StringComparison.OrdinalIgnoreCase))) ?? [];
            var asis = Found(gameDir, present).Select(f => Path.GetFileName(f)).Concat(ours).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (present == HeapPresent && asis.Count > 1)
                notes.Add(L.T($"The game has {asis.Count} Heap Adjusters ({string.Join(", ", asis)}) — they patch the same code; keep one of them."));
            foreach (var asi in asis)
            {
                var iniRel = Path.ChangeExtension(asi, ".ini");
                var ini = copied?.FirstOrDefault(c => c.GameRel.Equals(iniRel, StringComparison.OrdinalIgnoreCase)).Source
                          ?? Path.Combine(gameDir, iniRel);
                var text = File.Exists(ini) ? File.ReadAllText(ini) : null;
                // the setting its .ini holds; none: ours by its name (its default applies), else a build ModDrop V can't read
                var s = settings.FirstOrDefault(x => text is not null && ReadInt(text, x.Section, x.Key) is not null)
                        ?? settings.FirstOrDefault(x => x.Asi.Equals(asi, StringComparison.OrdinalIgnoreCase));
                if (s is null) continue;
                int? now = text is null ? null : ReadInt(text, s.Section, s.Key);
                if (s == Heap && now is > MaxHeapMb and var big)
                {
                    notes.Add(L.T($"{iniRel}: HEAP_SIZE = {big}. Chiheb-Bacha’s Heap Adjuster takes 2048 and more as a wrapped-around number ({big} → {EffectiveHeapMb(big)} MB); left as it is — if the game runs out of memory, set it to {need}."));
                    continue;
                }
                if ((now ?? s.Default) < need) low.Add(new Low(iniRel, s, now, need));
            }
        }
        return (low, notes);
    }

    /// <summary>The step that raises them — null when they are high enough.</summary>
    public static PlanOp? SettingsOp(string gameDir, GameEdition edition, string pluginsDir, int heapMb, int archives)
    {
        var low = LowSettings(gameDir, heapMb, archives, Missing(gameDir, edition, pluginsDir));
        if (low.Count == 0) return null;
        return new ActionOp(L.T($"Raise the limit plugins’ settings — {string.Join(", ", low.Select(l => $"{l.Key} → {l.To}"))}"), ctx =>
        {
            foreach (var l in Check(ctx.GameDir, heapMb, archives, null).Low)
            {
                var path = ctx.Abs(l.Ini);
                bool created = !File.Exists(path);
                var text = created ? "" : File.ReadAllText(path);
                File.WriteAllText(path, WithInt(text, l.Setting.Section, l.Setting.Key, l.To));
                // an uninstall puts the old value back only if this one is still there (the player may have set their own)
                ctx.Journal.Steps.Add(new IniKeySet(ctx.Journal.Rel(path), l.Setting.Section, l.Setting.Key, l.Now, l.To, created));
                ctx.Log($"    {l.Ini}: {l.Setting.Key} = {l.To}");
            }
        });
    }

    /// <summary>
    /// The .ini text with a value ModDrop V set taken back (<see cref="IniKeySet"/>): the old value, or the key gone when
    /// there was none — "" when the file ModDrop V made is left with nothing else. Null: leave it (the file is gone, or the
    /// player changed the value since).
    /// </summary>
    internal static string? Undone(string? text, IniKeySet k)
    {
        if (text is null || ReadInt(text, k.Section, k.Key) != k.New) return null;
        if (k.Old is { } old) return WithInt(text, k.Section, k.Key, old);
        var (start, end) = SectionSpan(text, k.Section);
        var line = new Regex($@"^[ \t]*{Regex.Escape(k.Key)}[ \t]*=[^\n]*(\n|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)
            .Match(text[..end], start);
        var rest = line.Success ? text.Remove(line.Index, line.Length) : text;
        bool empty = Regex.Replace(rest, $@"^\s*\[\s*{Regex.Escape(k.Section)}\s*\]\s*$", "", RegexOptions.IgnoreCase | RegexOptions.Multiline).Trim().Length == 0;
        return k.Created && empty ? "" : rest;
    }

    private static Regex KeyRe(string key) =>
        new($@"^(\s*{Regex.Escape(key)}\s*=\s*)(-?\d*)(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>A key's number in an .ini section (as GetPrivateProfileInt reads it); null when it isn't there.</summary>
    internal static int? ReadInt(string text, string section, string key)
    {
        var (start, end) = SectionSpan(text, section);
        if (start < 0) return null;
        var m = KeyRe(key).Match(text[start..end]);
        return m.Success && int.TryParse(m.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>The .ini with the key set to <paramref name="value"/>: in place, else added to its section, else a new section.</summary>
    internal static string WithInt(string text, string section, string key, int value)
    {
        var v = value.ToString(CultureInfo.InvariantCulture);
        var (start, end) = SectionSpan(text, section);
        if (start < 0)
            return text + (text.Length == 0 || text.EndsWith('\n') ? "" : "\r\n") + $"[{section}]\r\n{key} = {v}\r\n";
        var body = text[start..end];
        var m = KeyRe(key).Match(body);
        if (m.Success)
            return text[..(start + m.Groups[2].Index)] + v + text[(start + m.Groups[2].Index + m.Groups[2].Length)..];
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insert = (start == text.Length || text[start - 1] == '\n' ? "" : nl) + $"{key} = {v}{nl}";
        return text.Insert(start, insert);
    }

    /// <summary>Where a section's lines are (after its header up to the next one); -1 when there is no such section.</summary>
    private static (int Start, int End) SectionSpan(string text, string section)
    {
        var head = new Regex($@"^\s*\[\s*{Regex.Escape(section)}\s*\][^\n]*(\n|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline).Match(text);
        if (!head.Success) return (-1, -1);
        int start = head.Index + head.Length;
        var next = new Regex(@"^\s*\[", RegexOptions.Multiline).Match(text, start);
        return (start, next.Success ? next.Index : text.Length);
    }
}
