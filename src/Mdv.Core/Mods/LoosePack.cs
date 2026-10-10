using System.Text.RegularExpressions;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// A DLC pack shipped unpacked — a folder with <c>content.xml</c> and <c>setup2.xml</c>, as OpenIV exports one
/// (<c>dlcpacks\mycar\dlc.rpf\…</c>) or a modder lays it out (<c>mycar\content.xml</c>…). It is packed into a
/// <c>dlc.rpf</c> when the drop is gathered, so everything after sees a finished pack.
/// </summary>
public static partial class LoosePack
{
    [GeneratedRegex(@"^dlc(\d+)\.rpf$", RegexOptions.IgnoreCase)]
    private static partial Regex SubPackRe();

    /// <summary>
    /// <paramref name="dir"/> is an unpacked pack: <c>content.xml</c> and <c>setup2.xml</c> at its top and no
    /// <c>dlc.rpf</c> file next to them. A folder inside another game archive (<paramref name="origin"/> passes
    /// through a <c>*.rpf</c> folder, <c>update.rpf\…\dlcpacks\mpheist\dlc.rpf</c>) is a replacement of the game's
    /// files, not a pack of its own.
    /// </summary>
    public static bool IsPack(string dir, string origin)
    {
        if (origin.Replace('\\', '/').Split('/').SkipLast(1).Any(s => s.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)))
            return false;
        try
        {
            return File.Exists(Path.Combine(dir, "content.xml")) && File.Exists(Path.Combine(dir, "setup2.xml"))
                && !File.Exists(Path.Combine(dir, "dlc.rpf"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The sub-pack folders (<c>dlc1.rpf</c>…) next to an unpacked <c>dlc.rpf</c> folder, by number.</summary>
    public static List<string> SubPacks(string packDir)
    {
        if (!Path.GetFileName(packDir).Equals("dlc.rpf", StringComparison.OrdinalIgnoreCase)) return [];
        var parent = Path.GetDirectoryName(packDir);
        if (parent is null) return [];
        return [.. Directory.GetDirectories(parent).Where(d => SubPackRe().IsMatch(Path.GetFileName(d)))
                            .OrderBy(d => int.Parse(SubPackRe().Match(Path.GetFileName(d)).Groups[1].Value))];
    }

    /// <summary>
    /// Pack <paramref name="dir"/> into <paramref name="outRpf"/>: its <c>*.rpf</c> folders become archives inside
    /// (deepest first), models stay in the edition they are in (a finished pack is converted for Enhanced when it
    /// is installed). The result is self-checked like every pack ModDrop V builds; throws
    /// <see cref="InvalidDataException"/> naming the broken resources.
    /// </summary>
    public static RpfBuildInfo Pack(string dir, string outRpf)
    {
        dir = Path.GetFullPath(dir);
        var temps = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outRpf))!, Path.GetFileName(outRpf) + ".inner");
        var archives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int n = 0;
            foreach (var inner in Directory.EnumerateDirectories(dir, "*.rpf", SearchOption.AllDirectories)
                                           .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)).ToList())
            {
                Directory.CreateDirectory(temps);
                var packed = Path.Combine(temps, $"{++n}.rpf");
                RpfPacker.PackFolder(inner, packed, null, archives);
                archives[Path.GetFullPath(inner)] = packed;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outRpf))!);
            var info = RpfPacker.PackFolder(dir, outRpf, null, archives);
            var problems = RpfTools.VerifyResources(outRpf);
            if (problems.Count > 0)
            {
                File.Delete(outRpf);
                throw new InvalidDataException(L.T($"{problems.Count} broken resource(s): {string.Join("; ", problems.Take(3))}"));
            }
            return info;
        }
        finally
        {
            PathUtil.TryDeleteDir(temps);
        }
    }
}
