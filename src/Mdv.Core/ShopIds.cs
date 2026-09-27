using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core;

/// <summary>
/// Free Shop ID selection: reads the <c>&lt;id value="N"/&gt;</c> numbers used by add-on
/// packs installed in the game and packs already built into an output folder, and
/// hands back the lowest free id at/above a base (1000). The pipeline currently uses a
/// fixed id (the game ignores it — see <see cref="Pipeline.DefaultShopId"/>); this is
/// kept for callers that want unique ids anyway.
/// </summary>
public static partial class ShopIds
{
    public const int DefaultBase = 1000;
    public const string ShopMetaInner = "common/data/shop_weapon.meta";

    [GeneratedRegex("<id\\s+value=\"(-?\\d+)\"", RegexOptions.IgnoreCase)] private static partial Regex IdRe();

    private static readonly string[] DlcpacksSubdirs =
    [
        Path.Combine("mods", "update", "x64", "dlcpacks"),
        Path.Combine("update", "x64", "dlcpacks"),
        Path.Combine("mods", "x64", "dlcpacks"),
        Path.Combine("x64", "dlcpacks"),
    ];

    public static HashSet<int> ParseShopIds(string metaText)
    {
        var ids = new HashSet<int>();
        foreach (Match m in IdRe().Matches(metaText))
            if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                ids.Add(v);
        return ids;
    }

    /// <summary>Shop ids of one dlcpack folder: packed dlc.rpf, unpacked dlc.rpf folder, or loose.</summary>
    public static HashSet<int> ReadPackShopIds(string packDir)
    {
        var ids = new HashSet<int>();
        var rpf = Path.Combine(packDir, "dlc.rpf");
        try
        {
            if (File.Exists(rpf))
            {
                var raw = RpfTools.ReadInnerFile(rpf, ShopMetaInner);
                ids.UnionWith(ParseShopIds(TextIo.DecodeUtf8Sig(raw, strict: false)));
            }
            else
            {
                foreach (var b in new[] { rpf, packDir })
                {
                    var meta = Path.Combine(b, "common", "data", "shop_weapon.meta");
                    if (File.Exists(meta)) ids.UnionWith(ParseShopIds(TextIo.ReadText(meta)));
                }
            }
        }
        catch (Exception)
        {
            // no shop_weapon.meta / encrypted archive / anything odd: skip, don't fail
        }
        return ids;
    }

    /// <summary>dlcpacks directories under (or equal to) a game folder.</summary>
    public static List<string> FindDlcpacksDirs(string gameDir)
    {
        var result = new List<string>();
        foreach (var sub in DlcpacksSubdirs)
        {
            var d = Path.Combine(gameDir, sub);
            if (Directory.Exists(d)) result.Add(d);
        }
        if (Directory.Exists(gameDir) && !result.Contains(gameDir))
        {
            bool looksLike;
            try
            {
                looksLike = Path.GetFileName(Path.TrimEndingDirectorySeparator(gameDir)).Equals("dlcpacks", StringComparison.OrdinalIgnoreCase)
                            || Directory.EnumerateDirectories(gameDir).Any(c => File.Exists(Path.Combine(c, "dlc.rpf")));
            }
            catch (IOException) { looksLike = false; }
            catch (UnauthorizedAccessException) { looksLike = false; }
            if (looksLike) result.Add(gameDir);
        }
        return result;
    }

    public static HashSet<int> ScanGameShopIds(string gameDir, string? exclude, Action<string> log)
    {
        var ids = new HashSet<int>();
        int packs = 0;
        foreach (var dp in FindDlcpacksDirs(gameDir))
        {
            foreach (var pack in Directory.EnumerateDirectories(dp).OrderBy(p => p, PathUtil.PathOrder))
            {
                if (exclude is not null && Path.GetFileName(pack).Equals(exclude, StringComparison.OrdinalIgnoreCase)) continue;
                var got = ReadPackShopIds(pack);
                if (got.Count > 0)
                {
                    packs++;
                    ids.UnionWith(got);
                }
            }
        }
        if (packs > 0) log(L.T($"    scanned game folder: {packs} add-on pack(s) with shop ids."));
        return ids;
    }

    public static HashSet<int> ScanOutputShopIds(string outDir, string? exclude)
    {
        var ids = new HashSet<int>();
        if (!Directory.Exists(outDir)) return ids;
        foreach (var pack in Directory.EnumerateDirectories(outDir))
        {
            if (exclude is not null && Path.GetFileName(pack).Equals(exclude, StringComparison.OrdinalIgnoreCase)) continue;
            var mf = Path.Combine(pack, "manifest.json");
            if (File.Exists(mf))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(mf))?["shop_id"] is JsonValue v && v.TryGetValue<int>(out var sid))
                    {
                        ids.Add(sid);
                        continue;
                    }
                }
                catch (Exception) { }
            }
            ids.UnionWith(ReadPackShopIds(pack));
        }
        return ids;
    }

    public static int PickFreeShopId(IEnumerable<int> used, int @base = DefaultBase)
    {
        var taken = new HashSet<int>(used);
        int cand = @base;
        while (taken.Contains(cand)) cand++;
        return cand;
    }

    public static int ResolveShopId(string? scanDir, string? outDir, string? excludeSlug,
                                    Action<string> log, int @base = DefaultBase)
    {
        var used = new HashSet<int>();
        if (scanDir is not null) used.UnionWith(ScanGameShopIds(scanDir, excludeSlug, log));
        if (outDir is not null) used.UnionWith(ScanOutputShopIds(outDir, excludeSlug));
        int chosen = PickFreeShopId(used, @base);
        var shown = used.Count > 0 ? string.Join(", ", used.OrderBy(i => i)) : "none";
        log(L.T($"Auto Shop ID: picked {chosen} (already in use: {shown})."));
        return chosen;
    }
}
