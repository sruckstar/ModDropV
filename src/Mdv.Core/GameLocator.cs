using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Mdv.Core;

/// <summary>A GTA V installation found on this PC.</summary>
public sealed record GameInstall(string Path, GameEdition Edition);

/// <summary>
/// Looks for GTA V installations: the Rockstar launcher's registry entries, Steam libraries,
/// Epic manifests and the usual folders on every fixed drive. Read-only — nothing is touched.
/// </summary>
public static partial class GameLocator
{
    public static IReadOnlyList<GameInstall> Find()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<GameInstall>();
        foreach (var dir in Candidates())
        {
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            }
            catch
            {
                continue;
            }
            if (!seen.Add(full)) continue;
            // a folder with both executables stays out: its edition has to be chosen by hand
            if (GameEditions.Detect(full) is { } edition) found.Add(new GameInstall(full, edition));
        }
        return found;
    }

    private static IEnumerable<string> Candidates()
    {
        var dirs = new List<string>();
        if (OperatingSystem.IsWindows()) AddLauncherInstalls(dirs);
        Safe(() => dirs.AddRange(EpicManifests()));
        foreach (var drive in FixedDrives())
        {
            foreach (var parent in new[]
                     {
                         "Rockstar Games", @"Program Files\Rockstar Games", @"Program Files (x86)\Rockstar Games",
                         "Games", @"Games\Rockstar Games", "Epic Games", @"Program Files\Epic Games",
                         @"SteamLibrary\steamapps\common", @"Program Files (x86)\Steam\steamapps\common",
                     })
                Safe(() => dirs.AddRange(Children(Path.Combine(drive, parent))));
        }
        return dirs;
    }

    [SupportedOSPlatform("windows")]
    private static void AddLauncherInstalls(List<string> dirs)
    {
        Safe(() => dirs.AddRange(RockstarRegistry()));
        Safe(() => dirs.AddRange(SteamLibraries().Select(lib => Path.Combine(lib, "steamapps", "common"))
                                                 .SelectMany(Children)));
    }

    private static void Safe(Action a)
    {
        try
        {
            a();
        }
        catch
        {
            // a missing key / unreadable folder only means one place fewer to look
        }
    }

    private static IEnumerable<string> Children(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateDirectories(dir) : [];

    private static IEnumerable<string> FixedDrives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok;
            try
            {
                ok = d.DriveType == DriveType.Fixed && d.IsReady;
            }
            catch
            {
                ok = false;
            }
            if (ok) yield return d.RootDirectory.FullName;
        }
    }

    /// <summary>HKLM\SOFTWARE\WOW6432Node\Rockstar Games\*\InstallFolder (Legacy and Enhanced have their own keys).</summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RockstarRegistry()
    {
        var result = new List<string>();
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\Rockstar Games", @"SOFTWARE\Rockstar Games" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub?.GetValue("InstallFolder") is string folder && folder.Length > 0) result.Add(folder);
            }
        }
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> SteamLibraries()
    {
        var libs = new List<string>();
        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (Registry.CurrentUser, @"SOFTWARE\Valve\Steam"),
                 })
        {
            using var key = hive.OpenSubKey(path);
            var steam = key?.GetValue("InstallPath") as string ?? key?.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(steam)) continue;
            libs.Add(steam);
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        return libs;
    }

    private static IEnumerable<string> EpicManifests()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                               "Epic", "EpicGamesLauncher", "Data", "Manifests");
        var result = new List<string>();
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            Safe(() =>
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { Length: > 0 } s)
                    result.Add(s);
            });
        }
        return result;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();
}
