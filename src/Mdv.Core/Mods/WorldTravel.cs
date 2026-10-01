using System.Text;
using Mdv.Core.Index;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>Which map the game runs Liberty City Preservation Project's World Travel with.</summary>
public enum WorldTravelMap
{
    /// <summary>The story mode map — what the game loads by itself.</summary>
    StoryMode,
    /// <summary>The online (MP) map, loaded in story mode as well.</summary>
    Online,
}

/// <summary>
/// World Travel, Liberty City Preservation Project's level switcher (WorldTravel.asi, GPL-3,
/// github.com/Splatcrafter/worldTravelASI): what ModDrop V settles for it on install.
/// <list type="bullet">
/// <item>The map: its <c>Levels/*/IPLsSP.txt</c> and <c>IPLsMP.txt</c> list the map files of the story mode and the
/// online map, unloaded on the way to Liberty City. On Legacy the package's WorldTravelPatches.asi starts the game on the
/// online map with <c>DefaultGroupMap=GROUP_MAP_MP</c>; on Enhanced ModDrop V's build of WorldTravel.asi does it with
/// <c>[Enhanced] Map=MP</c> (and takes <c>SP</c> as said instead of guessing by a Legacy interior id).</item>
/// <item>Los Santos's list: the lists name the map files of the game as it was when the package was made; map files
/// of later updates would stay loaded in Liberty City. The game's DLC packs no list knows go into
/// <c>Levels/Los Santos/IPLs.txt</c> (World Travel unloads them only when active and brings back only those).</item>
/// </list>
/// </summary>
public static class WorldTravel
{
    // static readonly, not const: they go into translated texts
    public static readonly string Asi = "WorldTravel.asi";
    public static readonly string Ini = "WorldTravel.ini";
    public static readonly string PatchesAsi = "WorldTravelPatches.asi";
    public static readonly string PatchesIni = "WorldTravelPatches.ini";
    public static readonly string LosSantosList = "Levels/Los Santos/IPLs.txt";
    private const string LevelsDir = "Levels/";

    /// <summary>Does the package put World Travel into the game folder?</summary>
    public static bool In(OivPackage pkg) => LooseAdd(pkg, Asi) is not null;

    private static OivAdd? LooseAdd(OivPackage pkg, string path) =>
        pkg.Steps.OfType<OivAdd>().FirstOrDefault(a => !a.InArchive && a.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The steps after the package's own files: the map setting and the newer map files in Los Santos's list.</summary>
    public static void AddSteps(InstallPlan plan, OivPackage pkg, InstallTarget target)
    {
        if (!In(pkg)) return;
        bool online = pkg.WorldTravelMap == WorldTravelMap.Online;
        var mapName = online ? L.T("the online map") : L.T("the story mode map");
        if (target.Edition == GameEdition.Enhanced)
        {
            var value = online ? "MP" : "SP";
            plan.Add(new FileEditOp(Ini, L.T($"World Travel runs with {mapName}: [Enhanced] Map={value} in <game>/{Ini}"),
                                    (content, _) => Encode(SetIni(Decode(content), "Enhanced", "Map", value), content)));
        }
        else if (LooseAdd(pkg, PatchesAsi) is not null)
        {
            var value = online ? "GROUP_MAP_MP" : "GROUP_MAP_SP";
            plan.Add(new FileEditOp(PatchesIni, L.T($"World Travel runs with {mapName}: DefaultGroupMap={value} in <game>/{PatchesIni}"),
                                    (content, _) => Encode(SetIni(Decode(content), "WorldTravelPatches", "DefaultGroupMap", value), content)));
        }

        if (LooseAdd(pkg, LosSantosList) is null) return;
        var lists = pkg.Steps.OfType<OivAdd>()
            .Where(a => !a.InArchive && a.Path.StartsWith(LevelsDir, StringComparison.OrdinalIgnoreCase) &&
                        a.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Source).ToList();
        plan.Add(new FileEditOp(LosSantosList, L.T($"Add the map files of game updates newer than the package to World Travel’s Los Santos list (<game>/{LosSantosList})"),
            (content, log) =>
            {
                if (content is null) return null;
                var known = KnownNames(lists.Where(File.Exists).Select(File.ReadAllBytes));
                var added = NewMapFiles(GameMapFiles(target.GameDir, log), known);
                if (added.Count == 0)
                {
                    log(L.T("    The lists know every map file of the game's updates — nothing to add."));
                    return null;
                }
                log(L.T($"    {added.Sum(p => p.Names.Count)} map file(s) of {string.Join(", ", added.Select(p => p.Pack))} added."));
                return AppendLines(content, added.SelectMany(p => p.Names));
            }));
    }

    // ------------------------------------------------------------------ level lists

    /// <summary>Every name World Travel's lists hold (lower case).</summary>
    internal static HashSet<string> KnownNames(IEnumerable<byte[]> lists)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists)
            foreach (var line in TextIo.DecodeUtf8Sig(list, strict: false).Split('\n'))
                if (line.Trim() is { Length: > 0 } name) known.Add(name);
        return known;
    }

    /// <summary>
    /// The map files of DLC packs none of whose map files a list names — packs newer than the lists — that no list
    /// names, by pack in the given order. A pack the lists know some of is left as it is: what they leave out of it is
    /// left out on purpose (Rockstar's patch packs bring new versions of older map files, and the lists name those).
    /// </summary>
    internal static List<(string Pack, List<string> Names)> NewMapFiles(IEnumerable<(string Pack, string Name)> gameMaps, HashSet<string> known)
    {
        var result = new List<(string Pack, List<string> Names)>();
        var seen = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        foreach (var pack in gameMaps.GroupBy(m => m.Pack, StringComparer.OrdinalIgnoreCase))
        {
            if (pack.Any(m => known.Contains(m.Name))) continue;
            var names = pack.Select(m => m.Name).Where(seen.Add).ToList();
            if (names.Count > 0) result.Add((pack.Key, names));
        }
        return result;
    }

    /// <summary>
    /// The map files (.ymap names) of the game's own DLC packs (<c>update\x64\dlcpacks</c>, not mods), with their pack,
    /// in pack order. Only archive tables are read.
    /// </summary>
    internal static List<(string Pack, string Name)> GameMapFiles(string gameDir, Action<string> log)
    {
        var list = new List<(string, string)>();
        var root = Path.Combine(gameDir, "update", "x64", "dlcpacks");
        if (!Directory.Exists(root)) return list;
        GameCrypto? crypto = null;                              // no executable: only OPEN archives can be read
        if (File.Exists(Path.Combine(gameDir, GameEditions.LegacyExe)) || File.Exists(Path.Combine(gameDir, GameEditions.EnhancedExe)))
            try
            {
                crypto = GameCrypto.ForGame(gameDir);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                log(L.T($"    [!] The game's archive keys can't be read ({ex.Message}) — its packs aren't looked through."));
                return list;
            }
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var pack = Path.GetFileName(dir);
            foreach (var rpf in Directory.GetFiles(dir, "*.rpf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var scan = IndexedArchive.Scan(rpf, Path.GetRelativePath(gameDir, rpf).Replace('\\', '/'), crypto);
                foreach (var f in scan.Files)
                    if (f.Name.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase))
                        list.Add((pack, Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant()));
            }
        }
        return list;
    }

    /// <summary>The list with the names added at its end, in its own line breaks.</summary>
    internal static byte[] AppendLines(byte[] content, IEnumerable<string> names)
    {
        var text = Decode(content);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var sb = new StringBuilder(text);
        if (sb.Length > 0 && text[^1] != '\n') sb.Append(nl);
        foreach (var n in names) sb.Append(n).Append(nl);
        return Encode(sb.ToString(), content);
    }

    // ------------------------------------------------------------------ .ini

    /// <summary>
    /// <paramref name="text"/> with <c>key=value</c> in <c>[section]</c>: the key's line replaced (spaces around '=' and
    /// case don't matter), or added at the end of the section, or a new section at the end of the file.
    /// </summary>
    internal static string SetIni(string text, string section, string key, string value)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();
        bool trailing = lines.Count > 0 && lines[^1].Length == 0;
        if (trailing) lines.RemoveAt(lines.Count - 1);
        var entry = $"{key}={value}";

        int start = lines.FindIndex(l => IsSection(l, out var s) && s.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add(entry);
        }
        else
        {
            int end = start + 1;
            while (end < lines.Count && !IsSection(lines[end], out _)) end++;
            int at = -1;
            for (int i = start + 1; i < end; i++)
            {
                var l = lines[i].TrimStart();
                int eq = l.IndexOf('=');
                if (eq > 0 && l[0] is not (';' or '#') && l[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) at = i;
            }
            if (at >= 0) lines[at] = entry;
            else
            {
                int last = end - 1;                              // after the section's last setting, before blank lines / comments
                while (last > start && (lines[last].Trim().Length == 0 || lines[last].TrimStart()[0] is ';' or '#')) last--;
                lines.Insert(last + 1, entry);
            }
        }
        return string.Join(nl, lines) + (trailing || start < 0 ? nl : "");
    }

    private static bool IsSection(string line, out string name)
    {
        var t = line.Trim();
        name = t.Length > 2 && t[0] == '[' && t[^1] == ']' ? t[1..^1].Trim() : "";
        return name.Length > 0;
    }

    private static string Decode(byte[]? content) => content is null ? "" : TextIo.DecodeUtf8Sig(content, strict: false);

    /// <summary>The text in the file's own encoding: UTF-8, with the BOM when the file had one.</summary>
    private static byte[] Encode(string text, byte[]? like)
    {
        var bytes = TextIo.Utf8NoBom.GetBytes(text);
        return like is [0xEF, 0xBB, 0xBF, ..] ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes;
    }
}
