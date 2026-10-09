using System.Text.RegularExpressions;
using Mdv.Core.Rpf;

namespace Mdv.Core.Mods;

/// <summary>
/// Where a changed game file lives in the onigiri folder.
/// </summary>
/// <param name="Top">the place relative to the game: an archive (<c>onigiri/dlcpacks/mpbiker/dlc.rpf</c>,
/// <c>onigiri/platform/levels/gta5/vehicles.rpf</c>) — or, for a loose file, its root (<c>onigiri/common</c>,
/// <c>onigiri/platform</c>)</param>
/// <param name="Inner">the path inside it</param>
/// <param name="Loose">the file lies loose under <see cref="Top"/> (no archive around it)</param>
/// <param name="Logical">where the game sees <see cref="Top"/>: <c>x64/levels/gta5/vehicles.rpf</c>, <c>common</c>, <c>x64</c>,
/// <c>dlc_patch/mpbeach</c> (update.rpf's patch of a pack); null for a dlcpack (<c>update/x64/dlcpacks/…</c>)</param>
public sealed record OnigiriSpot(string Top, string Inner, bool Loose, string? Logical);

/// <summary>
/// Game paths → places in the onigiri folder. The game sees <c>update.rpf/x64/…</c> over the base archives' <c>x64/…</c>
/// (that is how title updates patch them: <c>update.rpf/x64/data/cdimages/scaleform_platform_pc.rpf</c> stands for the one
/// in <c>x64b.rpf</c>), and <c>onigiri\platform</c> is <c>update.rpf\x64</c> — so any file of <c>x64a–w.rpf</c>,
/// <c>common.rpf</c>, the loose <c>x64\…\*.rpf</c> and update.rpf has a place there:
/// <list type="bullet">
/// <item>a file inside an archive nested in them goes into a copy of that archive, lying loose in onigiri
/// (<c>x64e.rpf/levels/gta5/vehicles.rpf/adder.yft</c> → <c>onigiri/platform/levels/gta5/vehicles.rpf</c> + <c>adder.yft</c>);</item>
/// <item>any other file lies loose (<c>update/update.rpf/common/data/gameconfig.xml</c> → <c>onigiri/common/data/gameconfig.xml</c>);</item>
/// <item>a dlcpack's archive is copied whole (<c>update/x64/dlcpacks/mpbiker/dlc.rpf</c> → <c>onigiri/dlcpacks/mpbiker/dlc.rpf</c>);</item>
/// <item>a file of a pack's patch in update.rpf goes the same ways into <c>onigiri/dlc_patch/&lt;pack&gt;</c>
/// (<c>update/update.rpf/dlc_patch/mpbeach/x64/…</c> → <c>onigiri/dlc_patch/mpbeach/x64/…</c>). Onigiri doesn't read that
/// folder — our plugin <see cref="ModsLayout.OnigiriDlcPatchAsi"/> mounts it over the patch.</item>
/// </list>
/// <c>update2.rpf</c> has no Onigiri counterpart.
/// </summary>
public static partial class OnigiriPaths
{
    [GeneratedRegex(@"^x64[a-z]?\.rpf$")] private static partial Regex BaseX64Re();

    /// <summary>
    /// The place of <paramref name="gamePath"/> (as <c>mdvctl find</c> prints it; <c>mods/…</c> and <c>onigiri/…</c> paths work too).
    /// Lower case.
    /// </summary>
    /// <exception cref="NotSupportedException">Onigiri has no place for it</exception>
    public static OnigiriSpot Map(string gamePath)
    {
        var p = gamePath.Replace('\\', '/').Trim('/').ToLowerInvariant();
        if (p.StartsWith("mods/", StringComparison.Ordinal)) p = p[5..];
        var s = p.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // dlcpacks: the pack's archive is the place
        if (s.Length >= 5 && s[0] == "update" && s[1] == "x64" && s[2] == "dlcpacks" && s[4].EndsWith(".rpf", StringComparison.Ordinal))
            return new($"{OnigiriRoot}/dlcpacks/{s[3]}/{s[4]}", string.Join('/', s[5..]), false, null);
        if (s.Length >= 4 && s[0] == OnigiriRoot && s[1] == "dlcpacks" && s[3].EndsWith(".rpf", StringComparison.Ordinal))
            return new($"{OnigiriRoot}/dlcpacks/{s[2]}/{s[3]}", string.Join('/', s[4..]), false, null);

        if (s.Length >= 3 && s[0] == OnigiriRoot && s[1] is "platform" or "common")
            return Logical(s[1] == "platform" ? "x64" : "common", s[2..], gamePath, fromUpdate: false);
        if (s.Length >= 4 && s[0] == OnigiriRoot && s[1] == "update" && s[2] is "x64" or "common")
            return Logical(s[2], s[3..], gamePath, fromUpdate: true);
        if (s.Length >= 4 && s[0] == "update" && s[1] == "update.rpf" && s[2] is "x64" or "common")
            return Logical(s[2], s[3..], gamePath);
        // a pack's patch: update.rpf/dlc_patch/<pack>/… is laid over the pack, onigiri/dlc_patch/<pack> over that
        if (s.Length >= 4 && s[0] == "update" && s[1] == "update.rpf" && s[2] == "dlc_patch")
            return Logical($"dlc_patch/{s[3]}", s[4..], gamePath);
        if (s.Length >= 3 && s[0] == OnigiriRoot && s[1] == "dlc_patch")
            return Logical($"dlc_patch/{s[2]}", s[3..], gamePath);
        if (s.Length >= 2 && s[0] == "update" && s[1] == "update2.rpf")
            throw new NotSupportedException(L.T($"{gamePath}: update2.rpf has no counterpart in Onigiri — it loads over onigiri\\platform."));
        if (s.Length >= 2 && s[0] == "update")
            throw new NotSupportedException(L.T($"{gamePath}: Onigiri has no place for this part of update.rpf (only its common, x64 and dlc_patch folders)."));
        if (s.Length >= 2 && BaseX64Re().IsMatch(s[0])) return Logical("x64", s[1..], gamePath);
        if (s.Length >= 2 && s[0] == "common.rpf") return Logical("common", s[1..], gamePath);
        // the game's loose archives: x64\audio\sfx\PAIN.rpf is platform:/audio/sfx/pain.rpf
        if (s.Length >= 3 && s[0] == "x64" && s[..^1].Any(x => x.EndsWith(".rpf", StringComparison.Ordinal)))
            return Logical("x64", s[1..], gamePath);
        throw new NotSupportedException(L.T($"{gamePath} is not a file Onigiri can load (it takes update.rpf, the base archives and dlcpacks)."));
    }

    private const string OnigiriRoot = ModsLayout.OnigiriRoot;

    /// <summary><paramref name="rest"/> seen by the game under <paramref name="root"/> (<c>x64</c> / <c>common</c> / <c>dlc_patch/&lt;pack&gt;</c>).</summary>
    /// <param name="fromUpdate">the place is under onigiri\update (null: when the game reads it from update:/)</param>
    private static OnigiriSpot Logical(string root, string[] rest, string gamePath, bool? fromUpdate = null)
    {
        if (rest.Length == 0) throw new NotSupportedException(L.T($"{gamePath} is a folder, not a file."));
        var folder = root switch { "x64" => "platform", "common" => "common", _ => root };
        if (root is "x64" or "common" && (fromUpdate ?? ReadFromUpdate(root, rest))) folder = $"update/{root}";
        for (int i = 0; i < rest.Length - 1; i++)
            if (rest[i].EndsWith(".rpf", StringComparison.Ordinal))
            {
                var archive = string.Join('/', rest[..(i + 1)]);
                return new($"{OnigiriRoot}/{folder}/{archive}", string.Join('/', rest[(i + 1)..]), false, $"{root}/{archive}");
            }
        return new($"{OnigiriRoot}/{folder}", string.Join('/', rest), true, root);
    }

    /// <summary>
    /// What the game reads straight from update:/ (update.rpf), not through platform:/ or common:/ — Onigiri's folders don't
    /// reach it, a copy in onigiri\platform loses to update.rpf's (Rebalanced Dispatch Enhanced's hud.gfx: the HUD kept
    /// update.rpf's scaleform_generic.rpf). These go into <c>onigiri\update\x64|common</c>, which our plugin
    /// <see cref="ModsLayout.OnigiriDlcPatchAsi"/> mounts over update:/. The list: update.rpf's content.xml and the update:/
    /// reads seen while Enhanced 1158 loads story mode. Logical paths (<c>x64/…</c>, <c>common/…</c>): an archive or a file.
    /// </summary>
    private static readonly string[] UpdateReads =
    [
        "common/data/extratitleupdatedata.meta", "common/data/carcols_gen9.meta", "common/data/carmodcols_gen9.meta",
        "common/data/communitystats.meta", "common/data/gen9_exclusive_assets_peds.meta",
        "common/data/gen9_exclusive_assets_vehicles.meta", "common/data/levels/gta5/heightmapheistisland.dat",
        "common/data/levels/gta5/water_heistisland.xml", "common/data/mpstatscharactermappingdata.xml",
        "common/data/shop_vehicle.meta", "common/data/textpatch.meta",
        "x64/audio/occlusion.rpf",
        "x64/data/cdimages/carrec.rpf", "x64/data/cdimages/moviesubs.rpf", "x64/data/cdimages/scaleform_frontend.rpf",
        "x64/data/cdimages/scaleform_frontend_gen9.rpf", "x64/data/cdimages/scaleform_generic.rpf",
        "x64/data/cdimages/scaleform_generic_2.rpf", "x64/data/cdimages/scaleform_platform_pc.rpf",
        "x64/levels/gta5/lodlights.rpf", "x64/levels/gta5/paths.rpf", "x64/levels/gta5/waypointrec.rpf",
        "x64/levels/gta5/streaming/mic_1_mcs_1_srl.ymt", "x64/models/cdimages/ped_mugshot_txds.rpf",
        "x64/patch/anim/expressions.rpf", "x64/patch/anim/networkdefs.rpf",
        "x64/patch/data/cdimages/scaleform_minigames.rpf", "x64/patch/data/cdimages/scaleform_minimap.rpf",
        "x64/patch/data/cdimages/scaleform_web.rpf", "x64/patch/data/effects/ptfx.rpf", "x64/patch/data/effects/ptfx_hi.rpf",
        "x64/textures/script_txds.rpf", "x64/textures/vehicle_paint_ramps.rpf", "x64/textures/waterfog_txds.rpf",
        "x64/textures/waterfog_txds_dx.rpf",
    ];

    /// <summary>Does the game read the file at <paramref name="root"/>/<paramref name="rest"/> (or the archive holding it) from update:/?</summary>
    public static bool ReadFromUpdate(string root, string[] rest)
    {
        var path = $"{root}/{string.Join('/', rest)}".ToLowerInvariant();
        foreach (var r in UpdateReads)
            if (path == r || r.EndsWith(".rpf", StringComparison.Ordinal) && path.StartsWith(r + "/", StringComparison.Ordinal))
                return true;
        // the patch's text archives, one per language: x64/patch/data/lang/american_rel.rpf, …
        return rest.Length >= 5 && root == "x64" && rest[0] == "patch" && rest[1] == "data" && rest[2] == "lang" &&
               rest[3].EndsWith(".rpf", StringComparison.Ordinal);
    }

    /// <summary>Is <paramref name="top"/> a loose root (<c>onigiri/common</c>, <c>onigiri/platform</c>,
    /// <c>onigiri/update/common|x64</c>, <c>onigiri/dlc_patch/&lt;pack&gt;</c>)?</summary>
    public static bool IsLooseRoot(string top) =>
        top.Equals(ModsLayout.OnigiriCommon, StringComparison.OrdinalIgnoreCase) ||
        top.Equals(ModsLayout.OnigiriPlatform, StringComparison.OrdinalIgnoreCase) ||
        top.Equals(ModsLayout.OnigiriUpdateCommon, StringComparison.OrdinalIgnoreCase) ||
        top.Equals(ModsLayout.OnigiriUpdateX64, StringComparison.OrdinalIgnoreCase) ||
        IsPatchRoot(top);

    /// <summary>Is <paramref name="top"/> a place under <c>onigiri/update</c> (read only with <see cref="ModsLayout.OnigiriDlcPatchAsi"/>)?</summary>
    public static bool IsUpdate(string top) =>
        top.Replace('\\', '/').StartsWith(ModsLayout.OnigiriUpdate + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Does the file <paramref name="inner"/> of the loose root <paramref name="top"/> go into our pack
    /// (<see cref="OnigiriReplacePack"/>) instead of lying loose: a streamed resource of onigiri\platform — models,
    /// textures, map files and the scenario regions (<c>levels/gta5/scenario/*.ymt</c>; the other .ymt are data the game
    /// reads by name).
    /// </summary>
    public static bool InReplacePack(string top, string inner)
    {
        if (!top.Equals(ModsLayout.OnigiriPlatform, StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(inner);
        if (Rpf7.MustBeResource(ext)) return true;
        return ext.Equals(".ymt", StringComparison.OrdinalIgnoreCase) &&
               inner.Replace('\\', '/').Contains("/scenario/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Is <paramref name="top"/> the folder over a pack's patch, <c>onigiri/dlc_patch/&lt;pack&gt;</c>?</summary>
    public static bool IsPatchRoot(string top) =>
        top.StartsWith(ModsLayout.OnigiriDlcPatch + "/", StringComparison.OrdinalIgnoreCase) &&
        top.IndexOf('/', ModsLayout.OnigiriDlcPatch.Length + 1) < 0;

    /// <summary>Is <paramref name="top"/> a place under <c>onigiri/dlc_patch</c> (read only with <see cref="ModsLayout.OnigiriDlcPatchAsi"/>)?</summary>
    public static bool IsPatch(string top) =>
        top.Replace('\\', '/').StartsWith(ModsLayout.OnigiriDlcPatch + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where the game has the file at logical <paramref name="logical"/> (<c>x64/data/…</c>, <c>common/data/…</c>), in the order
    /// it looks: update2.rpf, update.rpf, then the base archives — the game's loose <c>x64\…\*.rpf</c>, <c>x64w.rpf</c> … <c>x64a.rpf</c>
    /// (a later one over an earlier), <c>common.rpf</c>. A pack's patch (<c>dlc_patch/&lt;pack&gt;/…</c>) is only in update.rpf.
    /// Game paths; whether each is really there is for the caller to check.
    /// </summary>
    public static IEnumerable<string> Candidates(string gameDir, string logical)
    {
        var l = logical.Trim('/').ToLowerInvariant();
        if (l.StartsWith("dlc_patch/", StringComparison.Ordinal))
        {
            yield return $"update/update.rpf/{l}";
            yield break;
        }
        yield return $"update/update2.rpf/{l}";
        yield return $"update/update.rpf/{l}";
        if (l.StartsWith("common/", StringComparison.Ordinal))
        {
            yield return $"common.rpf/{l[7..]}";
            yield break;
        }
        if (!l.StartsWith("x64/", StringComparison.Ordinal)) yield break;
        var rest = l[4..].Split('/');
        for (int i = 0; i < rest.Length; i++)
            if (rest[i].EndsWith(".rpf", StringComparison.Ordinal) &&
                File.Exists(Path.Combine(gameDir, "x64", Path.Combine(rest[..(i + 1)]))))
                yield return l;                                   // x64/audio/sfx/pain.rpf(/…) — a loose archive of the game
        foreach (var arc in BaseArchives(gameDir)) yield return $"{arc}/{l[4..]}";
    }

    /// <summary>x64a–w.rpf of the game, the one loaded last first.</summary>
    private static IEnumerable<string> BaseArchives(string gameDir)
    {
        if (!Directory.Exists(gameDir)) return [];
        return Directory.EnumerateFiles(gameDir, "x64*.rpf", SearchOption.TopDirectoryOnly)
                        .Select(f => Path.GetFileName(f).ToLowerInvariant())
                        .Where(n => BaseX64Re().IsMatch(n))
                        .OrderByDescending(n => n, StringComparer.Ordinal);
    }
}
