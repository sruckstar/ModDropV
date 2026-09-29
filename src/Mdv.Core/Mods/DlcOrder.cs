using System.Text.RegularExpressions;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// The order add-on packs load in. A pack whose weapons use components another pack defines has to be listed after it in
/// dlclist.xml: the game looks a variant's extra components up while it reads the pack, and one it doesn't have yet is a
/// fatal error on loading (Vom Feuer SMG listed before the SharedAttachments it builds on). The mods' own packs are
/// re-ordered among the places they already take; the game's packs stay where they are.
/// </summary>
public static partial class DlcOrder
{
    /// <summary>What a pack defines and uses, as read from its dlc.rpf.</summary>
    public sealed record Components(HashSet<string> Defined, HashSet<string> Used)
    {
        /// <summary>The components it needs from somewhere else.</summary>
        public IEnumerable<string> Needed => Used.Where(c => !Defined.Contains(c));
    }

    private static readonly Components None = new([], []);

    // dlc.rpf path → (write time, length, components): packs are read again only when they change
    private static readonly Dictionary<string, (DateTime Time, long Length, Components Comps)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> GameCache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"<Item>\s*dlcpacks:/([^/<\s]+)/?\s*</Item>", RegexOptions.IgnoreCase)] private static partial Regex EntryRe();
    [GeneratedRegex(@"<Item\s+type=""CWeaponComponent\w*Info""[^>]*>(?:\s|<!--.*?-->)*<Name>\s*([^<\s]+)\s*</Name>", RegexOptions.Singleline)]
    private static partial Regex InfoNameRe();

    /// <summary>The components of an installed pack (none when it can't be read).</summary>
    public static Components Of(string dlcRpf)
    {
        var fi = new FileInfo(dlcRpf);
        if (!fi.Exists) return None;
        lock (Cache)
            if (Cache.TryGetValue(fi.FullName, out var c) && c.Time == fi.LastWriteTimeUtc && c.Length == fi.Length) return c.Comps;
        Components comps;
        try
        {
            var p = AddonContent.ReadPack(fi.FullName, maps: false);
            comps = new(p.Content.ComponentsDefined, p.Content.ComponentsUsed);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or RpfEncryptedException)
        {
            comps = None;
        }
        lock (Cache) Cache[fi.FullName] = (fi.LastWriteTimeUtc, fi.Length, comps);
        return comps;
    }

    /// <summary>The game's own weapon components (data/weaponcomponents.meta).</summary>
    public static HashSet<string> GameComponents(string? dataDir)
    {
        var path = Path.Combine(dataDir ?? DependencyCatalog.DefaultDataDir, "weaponcomponents.meta");
        lock (GameCache)
        {
            if (GameCache.TryGetValue(path, out var set)) return set;
            set = new(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
                foreach (Match m in InfoNameRe().Matches(File.ReadAllText(path))) set.Add(m.Groups[1].Value);
            return GameCache[path] = set;
        }
    }

    /// <summary>
    /// The components of a weapon mod before it is built: its finished dlc.rpf, or the weaponcomponents / weapons metas
    /// among its files.
    /// </summary>
    public static Components OfWeaponFolder(string folder)
    {
        if (Overrides.FindPrebuiltRpf(folder) is { } rpf) return Of(rpf);
        var content = new AddonContent();
        foreach (var f in Directory.EnumerateFiles(folder, "*.meta", SearchOption.AllDirectories))
        {
            var text = TextIo.DecodeUtf8Sig(File.ReadAllBytes(f), strict: false);
            if (text.Contains("<CWeaponComponentInfoBlob", StringComparison.Ordinal)) content.AddData("WEAPONCOMPONENTSINFO_FILE", text, f);
            else if (text.Contains("<CWeaponInfoBlob", StringComparison.Ordinal)) content.AddData("WEAPONINFO_FILE", text, f);
        }
        return new(content.ComponentsDefined, content.ComponentsUsed);
    }

    /// <summary>
    /// GTA V Enhanced's pool and array of weapon components (build 1158) hold 470, the game fills 465: 5 more load, the
    /// 6th is a fatal error while loading (SharedAttachments adds 25). Weapon Limits Adjuster raises the 470.
    /// </summary>
    public const int EnhancedComponentLimit = 470, EnhancedGameComponents = 465;
    public static readonly int EnhancedFreeComponents = EnhancedComponentLimit - EnhancedGameComponents;

    /// <summary>The size Weapon Limits Adjuster gives when its .ini doesn't say.</summary>
    public const int WeaponLimitsDefault = 1024;

    /// <summary>A Weapon Limits Adjuster for this edition in the game folder.</summary>
    public static bool HasWeaponLimitsAdjuster(string gameDir, GameEdition edition) =>
        Directory.Exists(gameDir) &&
        Directory.EnumerateFiles(gameDir, "WeaponLimitsAdjuster*.asi").Any(a => LimitAdjusters.ForEnhanced(a) == (edition == GameEdition.Enhanced));

    /// <summary>The component limit the Enhanced Weapon Limits Adjuster in the game folder sets (CWeaponComponentInfo in its .ini).</summary>
    public static int WeaponLimitsSize(string gameDir)
    {
        foreach (var asi in Directory.EnumerateFiles(gameDir, "WeaponLimitsAdjuster*.asi").Where(LimitAdjusters.ForEnhanced))
        {
            var ini = Path.ChangeExtension(asi, ".ini");
            if (!File.Exists(ini)) continue;
            try
            {
                foreach (var line in File.ReadLines(ini))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0 && line[..eq].Trim().Equals("CWeaponComponentInfo", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(line[(eq + 1)..].Trim(), out var n) && n > 0)
                        return n;
                }
            }
            catch (IOException) { }
        }
        return WeaponLimitsDefault;
    }

    /// <summary>
    /// What a pack about to be installed needs of other mods: the packs it uses components of (it is listed after them),
    /// and a warning for components that neither the game nor an installed pack has — the game stops loading without them.
    /// On Enhanced, also a warning when the mods together add more components than the game has room for.
    /// </summary>
    /// <param name="skip">pack folders that are this mod's own (an earlier install of it)</param>
    public static List<AddonCheck> Checks(string gameDir, GameEdition edition, string? dataDir, Components own, ISet<string> skip)
    {
        var list = new List<AddonCheck>();
        var game = GameComponents(dataDir);
        var packs = new List<(string Folder, bool On, Components Comps)>();
        foreach (var (root, on) in new[] { (GameInstaller.DlcpacksDir(gameDir), true), (GameInstaller.DisabledDlcpacksDir(gameDir), false) })
            if (Directory.Exists(root))
                foreach (var dir in Directory.EnumerateDirectories(root))
                    if (!skip.Contains(Path.GetFileName(dir)))
                        packs.Add((Path.GetFileName(dir), on, Of(Path.Combine(dir, "dlc.rpf"))));

        // ---- the game's limit on components: every one the mods define on top of its own takes a place
        if (edition == GameEdition.Enhanced && own.Defined.Any(c => !game.Contains(c)))
        {
            var added = new HashSet<string>(own.Defined.Where(c => !game.Contains(c)), StringComparer.OrdinalIgnoreCase);
            foreach (var p in packs.Where(p => p.On)) added.UnionWith(p.Comps.Defined.Where(c => !game.Contains(c)));
            if (HasWeaponLimitsAdjuster(gameDir, edition))
            {
                var size = WeaponLimitsSize(gameDir);
                if (added.Count > size - EnhancedGameComponents)
                    list.Add(new AddonCheck(CheckLevel.Warn, L.T("Too many weapon components"),
                        L.T($"With the installed mods that makes {added.Count} new weapon components, and Weapon Limits Adjuster makes room " +
                            $"for {size - EnhancedGameComponents} — past that the game crashes while loading. Raise CWeaponComponentInfo " +
                            $"in WeaponLimitsAdjusterEnhanced.ini in the game folder (now {size}).")));
            }
            else if (added.Count > EnhancedFreeComponents)
                // it isn't there yet: it goes in with this install, together with the game's limits
                list.Add(new AddonCheck(CheckLevel.Info, L.T("Weapon Limits Adjuster"),
                    L.T($"With the installed mods that makes {added.Count} new weapon components, and GTA V Enhanced has room for only " +
                        $"{EnhancedFreeComponents} — ModDrop V puts Weapon Limits Adjuster in the game folder with this mod, " +
                        $"which makes room for {WeaponLimitsDefault - EnhancedGameComponents}.")));
        }

        var needed = own.Needed.Where(c => !game.Contains(c)).ToList();
        if (needed.Count == 0) return list;
        var from = packs.Where(p => p.On && needed.Any(p.Comps.Defined.Contains)).Select(p => p.Folder).ToList();
        var missing = needed.Where(c => !packs.Any(p => p.On && p.Comps.Defined.Contains(c))).ToList();
        var off = packs.Where(p => !p.On && missing.Any(p.Comps.Defined.Contains)).Select(p => p.Folder).ToList();
        if (from.Count > 0)
            list.Add(new AddonCheck(CheckLevel.Ok, L.T("Uses another mod’s parts"),
                L.T($"Its weapons use components of «{string.Join("», «", from)}» — it is listed after that pack in dlclist.xml, so they are there when it loads.")));
        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Take(5)) + (missing.Count > 5 ? ", …" : "");
            list.Add(new AddonCheck(CheckLevel.Warn, L.T("Needs another mod"),
                (off.Count > 0
                    ? L.T($"Its weapons use components of «{string.Join("», «", off)}», which is switched off: {names}.")
                    : L.T($"Its weapons use components that neither the game nor an installed mod has: {names}.")) +
                " " + L.T("The game stops loading without them — install the mod they come from (its readme usually names it) as well.")));
        }
        return list;
    }

    /// <summary>
    /// dlclist.xml text with the mods' packs ordered so every pack comes after the packs whose components it uses (otherwise
    /// as they were), or null when the order is already right.
    /// </summary>
    public static string? Sorted(string dlclist, string gameDir, Action<string> log)
    {
        var dir = GameInstaller.DlcpacksDir(gameDir);
        if (!Directory.Exists(dir)) return null;
        var gamePacks = Path.Combine(gameDir, "update", "x64", "dlcpacks");
        var entries = EntryRe().Matches(dlclist)
            .Where(m => File.Exists(Path.Combine(dir, m.Groups[1].Value, "dlc.rpf")) &&
                        !Directory.Exists(Path.Combine(gamePacks, m.Groups[1].Value)))      // a patched game pack stays put
            .ToList();
        if (entries.Count < 2) return null;

        var comps = entries.Select(m => Of(Path.Combine(dir, m.Groups[1].Value, "dlc.rpf"))).ToList();
        var providers = Providers(comps);
        var order = Order(providers);
        if (order.SequenceEqual(Enumerable.Range(0, entries.Count))) return null;

        foreach (var (i, pos) in order.Select((i, pos) => (i, pos)))
            if (pos > i && providers[i].Count > 0)
                log(L.T($"    dlclist.xml: '{entries[i].Groups[1].Value}' moved after '{string.Join("', '", providers[i].Select(j => entries[j].Groups[1].Value))}' — it uses its weapon components."));

        var sb = new System.Text.StringBuilder();
        int at = 0;
        for (int k = 0; k < entries.Count; k++)
        {
            sb.Append(dlclist, at, entries[k].Index - at).Append(entries[order[k]].Value);
            at = entries[k].Index + entries[k].Length;
        }
        return sb.Append(dlclist, at, dlclist.Length - at).ToString();
    }

    /// <summary>
    /// The weapons of a shared AddonWeapons pack in the order their metas go into its content.xml: one that uses another's
    /// components after it (the same rule as for packs), otherwise as added.
    /// </summary>
    public static List<PackWeapon> Ordered(IEnumerable<PackWeapon> weapons)
    {
        var list = weapons.ToList();
        var comps = list.Select(w =>
        {
            var c = new AddonContent();
            foreach (var f in w.Files.Where(f => f.FType is "WEAPONCOMPONENTSINFO_FILE" or "WEAPONINFO_FILE"))
                c.AddData(f.FType, f.Content, f.Name);
            return new Components(c.ComponentsDefined, c.ComponentsUsed);
        }).ToList();
        return [.. Order(Providers(comps)).Select(i => list[i])];
    }

    /// <summary>providers[i]: the items whose components item i uses.</summary>
    private static List<HashSet<int>> Providers(List<Components> comps) =>
        [.. comps.Select((c, i) => Enumerable.Range(0, comps.Count).Where(j => j != i && c.Needed.Any(comps[j].Defined.Contains)).ToHashSet())];

    /// <summary>Stable: each time the earliest item whose providers are all placed; a cycle keeps its first item where it is.</summary>
    private static List<int> Order(List<HashSet<int>> providers)
    {
        var order = new List<int>();
        var left = Enumerable.Range(0, providers.Count).ToList();
        while (left.Count > 0)
        {
            int next = left.FirstOrDefault(i => providers[i].All(order.Contains), left[0]);
            order.Add(next);
            left.Remove(next);
        }
        return order;
    }
}
