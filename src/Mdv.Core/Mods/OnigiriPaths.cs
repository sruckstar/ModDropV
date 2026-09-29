using System.Text.RegularExpressions;

namespace Mdv.Core.Mods;

/// <summary>
/// Where a changed game file lives in the onigiri folder.
/// </summary>
/// <param name="Top">the place relative to the game: an archive (<c>onigiri/dlcpacks/mpbiker/dlc.rpf</c>,
/// <c>onigiri/platform/levels/gta5/vehicles.rpf</c>) — or, for a loose file, its root (<c>onigiri/common</c>,
/// <c>onigiri/platform</c>)</param>
/// <param name="Inner">the path inside it</param>
/// <param name="Loose">the file lies loose under <see cref="Top"/> (no archive around it)</param>
/// <param name="Logical">where the game sees <see cref="Top"/>: <c>x64/levels/gta5/vehicles.rpf</c>, <c>common</c>, <c>x64</c>;
/// null for a dlcpack (<c>update/x64/dlcpacks/…</c>)</param>
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
/// <item>a dlcpack's archive is copied whole (<c>update/x64/dlcpacks/mpbiker/dlc.rpf</c> → <c>onigiri/dlcpacks/mpbiker/dlc.rpf</c>).</item>
/// </list>
/// Not every place has an Onigiri counterpart: <c>update2.rpf</c> and <c>update.rpf/dlc_patch</c> don't.
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
            return Logical(s[1] == "platform" ? "x64" : "common", s[2..], gamePath);
        if (s.Length >= 4 && s[0] == "update" && s[1] == "update.rpf" && s[2] is "x64" or "common")
            return Logical(s[2], s[3..], gamePath);
        if (s.Length >= 2 && s[0] == "update" && s[1] == "update2.rpf")
            throw new NotSupportedException(L.T($"{gamePath}: update2.rpf has no counterpart in Onigiri — it loads over onigiri\\platform."));
        if (s.Length >= 2 && s[0] == "update")
            throw new NotSupportedException(L.T($"{gamePath}: Onigiri has no place for this part of update.rpf (only its common and x64 folders)."));
        if (s.Length >= 2 && BaseX64Re().IsMatch(s[0])) return Logical("x64", s[1..], gamePath);
        if (s.Length >= 2 && s[0] == "common.rpf") return Logical("common", s[1..], gamePath);
        // the game's loose archives: x64\audio\sfx\PAIN.rpf is platform:/audio/sfx/pain.rpf
        if (s.Length >= 3 && s[0] == "x64" && s[..^1].Any(x => x.EndsWith(".rpf", StringComparison.Ordinal)))
            return Logical("x64", s[1..], gamePath);
        throw new NotSupportedException(L.T($"{gamePath} is not a file Onigiri can load (it takes update.rpf, the base archives and dlcpacks)."));
    }

    private const string OnigiriRoot = ModsLayout.OnigiriRoot;

    /// <summary><paramref name="rest"/> seen by the game under <paramref name="root"/> (<c>x64</c> / <c>common</c>).</summary>
    private static OnigiriSpot Logical(string root, string[] rest, string gamePath)
    {
        if (rest.Length == 0) throw new NotSupportedException(L.T($"{gamePath} is a folder, not a file."));
        var folder = root == "x64" ? "platform" : "common";
        for (int i = 0; i < rest.Length - 1; i++)
            if (rest[i].EndsWith(".rpf", StringComparison.Ordinal))
            {
                var archive = string.Join('/', rest[..(i + 1)]);
                return new($"{OnigiriRoot}/{folder}/{archive}", string.Join('/', rest[(i + 1)..]), false, $"{root}/{archive}");
            }
        return new($"{OnigiriRoot}/{folder}", string.Join('/', rest), true, root);
    }

    /// <summary>Is <paramref name="top"/> a loose root (<c>onigiri/common</c>, <c>onigiri/platform</c>)?</summary>
    public static bool IsLooseRoot(string top) =>
        top.Equals(ModsLayout.OnigiriCommon, StringComparison.OrdinalIgnoreCase) ||
        top.Equals(ModsLayout.OnigiriPlatform, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where the game has the file at logical <paramref name="logical"/> (<c>x64/data/…</c>, <c>common/data/…</c>), in the order
    /// it looks: update2.rpf, update.rpf, then the base archives — the game's loose <c>x64\…\*.rpf</c>, <c>x64w.rpf</c> … <c>x64a.rpf</c>
    /// (a later one over an earlier), <c>common.rpf</c>. Game paths; whether each is really there is for the caller to check.
    /// </summary>
    public static IEnumerable<string> Candidates(string gameDir, string logical)
    {
        var l = logical.Trim('/').ToLowerInvariant();
        yield return $"update/update2.rpf/{l}";
        yield return $"update/update.rpf/{l}";
        if (l.StartsWith("common/", StringComparison.Ordinal))
        {
            yield return $"common.rpf/{l[7..]}";
            yield break;
        }
        if (!l.StartsWith("x64/", StringComparison.Ordinal)) yield break;
        var rest = l[4..].Split('/');
        for (int i = 0; i < rest.Length - 1; i++)
            if (rest[i].EndsWith(".rpf", StringComparison.Ordinal) &&
                File.Exists(Path.Combine(gameDir, "x64", Path.Combine(rest[..(i + 1)]))))
                yield return l;                                   // x64/audio/sfx/pain.rpf/… — a loose archive of the game
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
