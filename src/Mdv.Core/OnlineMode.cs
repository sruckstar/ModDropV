using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Mdv.Core.Mods;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core;

/// <summary>Why a file or folder of the game folder counts as part of the mods.</summary>
public enum StashKind
{
    /// <summary>The mods folder: add-on packs, archive copies, ModDrop V's registry.</summary>
    ModsFolder,
    /// <summary>What makes the game load mods: ASI loaders (proxy DLLs) and mods-folder plugins.</summary>
    Loader,
    /// <summary>Script hooks and what runs on them: ScriptHookV, ScriptHookVDotNet, RAGE Plugin Hook, scripts\, plugins\.</summary>
    ScriptHook,
    /// <summary>.asi plugins.</summary>
    Plugin,
    /// <summary>Anything else: binaries the game doesn't come with, their settings, ReShade/ENB, folders with code.</summary>
    Other,
}

/// <summary>One top-level entry of the game folder that goes into the stash.</summary>
public sealed record StashItem(string Name, bool IsFolder, StashKind Kind, string Why);

/// <summary>What <see cref="OnlineMode.Scan"/> found: what to put away, and what putting away can't fix.</summary>
public sealed class OnlineScan
{
    public List<StashItem> Items { get; } = [];
    /// <summary>The game's own archives that were edited in place (OPEN instead of the game's encryption).</summary>
    public List<string> EditedArchives { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>The stash's manifest: <c>&lt;game&gt;\ModDropV-Stash\stash.json</c>.</summary>
public sealed class StashManifest
{
    [JsonPropertyName("format")] public int Format { get; set; } = 1;
    [JsonPropertyName("created")] public DateTime Created { get; set; }
    [JsonPropertyName("edition")] public string Edition { get; set; } = "";
    [JsonPropertyName("items")] public List<StashManifestItem> Items { get; set; } = [];
}

public sealed class StashManifestItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("folder")] public bool Folder { get; set; }
    [JsonPropertyName("kind")] public StashKind Kind { get; set; }
}

/// <summary>What <see cref="OnlineMode.Restore"/> did.</summary>
/// <param name="Restored">top-level entries put back</param>
/// <param name="Left">paths (stash-relative) that stayed in the stash: the game folder has that file again</param>
public sealed record RestoreResult(List<string> Restored, List<string> Left);

/// <summary>
/// "Play GTA Online": every mod in the game folder — the mods folder, ASI loaders and mods-folder
/// plugins, ScriptHookV / ScriptHookVDotNet / RAGE Plugin Hook, .asi plugins, scripts\ and plugins\,
/// ReShade/ENB, any DLL or EXE the game doesn't come with — is moved into
/// <c>&lt;game&gt;\ModDropV-Stash</c>, and one click moves it all back. Moving stays on the same drive,
/// so even a 3 GB mods folder goes in an instant; nothing is copied or deleted, and the game's own
/// files are never touched.
/// <para>
/// What counts as a mod: the game's own binaries are all signed (Rockstar, NVIDIA, AMD, Microsoft,
/// RAD — checked on Legacy and Enhanced), mod binaries aren't. So a root .dll/.exe goes when it's a
/// known hook name (proxy DLLs load whatever their name says) or carries no signature; .asi always
/// goes. Settings and logs named like a moved binary go with it; folders go when they are known mod
/// folders, are named like a moved binary, or hold code.
/// </para>
/// </summary>
public static class OnlineMode
{
    public static readonly string StashName = "ModDropV-Stash";
    public const string ManifestName = "stash.json";

    public static string StashDir(string gameDir) => Path.Combine(gameDir, StashName);
    private static string ManifestPath(string gameDir) => Path.Combine(StashDir(gameDir), ManifestName);

    /// <summary>The mods are put away (the stash has its manifest).</summary>
    public static bool IsOn(string gameDir) => File.Exists(ManifestPath(gameDir));

    public static StashManifest? Manifest(string gameDir)
    {
        try
        {
            return File.Exists(ManifestPath(gameDir)) ? TextIo.FromJson<StashManifest>(File.ReadAllText(ManifestPath(gameDir))) : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new StashManifest();
        }
    }

    // ---------------------------------------------------------------- what the game comes with

    /// <summary>Folders of the game (and its stores) — never moved.</summary>
    private static readonly HashSet<string> GameFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "x64", "update", "BattlEye", "Redistributables", "D3D12-REDIST", ".tmp", ".egstore", "_CommonRedist",
        "Installers", "__Installer", StashName,
    };

    /// <summary>Binaries of the game and its stores, in case a copy comes unsigned (checked on Legacy 3889 and Enhanced 1158).</summary>
    private static readonly HashSet<string> GameBinaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "GTA5.exe", "GTA5_BE.exe", "GTA5_Enhanced.exe", "GTA5_Enhanced_BE.exe", "PlayGTAV.exe", "GTAVLauncher.exe",
        "GTAVLanguageSelect.exe", "uninstall.exe",
        "bink2w64.dll", "d3dcompiler_46.dll", "d3dcsx_46.dll", "GFSDK_ShadowLib.win64.dll", "GFSDK_TXAA.win64.dll",
        "GFSDK_TXAA_AlphaResolve.win64.dll", "GPUPerfAPIDX11-x64.dll", "NvPmApi.Core.win64.dll", "GFSDK_Aftermath_Lib.x64.dll",
        "XCurl.dll", "fvad.dll", "libcurl.dll", "libtox.dll", "opus.dll", "opusenc.dll", "zlib1.dll", "oo2core_5_win64.dll",
        "amd_ags_x64.dll", "amd_fidelityfx_dx12.dll", "dstorage.dll", "dstoragecore.dll", "nvngx_dlss.dll", "nvngx_dlssg.dll",
        "sl.common.dll", "sl.dlss.dll", "sl.dlss_g.dll", "sl.interposer.dll", "sl.pcl.dll", "sl.reflex.dll",
        "steam_api64.dll", "EOSSDK-Win64-Shipping.dll",
    };

    /// <summary>Data files of the game and its launcher — never taken for a mod's settings.</summary>
    private static readonly HashSet<string> GameData = new(StringComparer.OrdinalIgnoreCase)
    {
        "index.bin", "rpf.cache", GameInstaller.RpfCacheSwitch, "title.rgl", "version.txt", "versioninfo.txt", "commandline.txt", "args.txt",
    };

    // ---------------------------------------------------------------- what mods look like

    /// <summary>
    /// Proxy DLLs the game picks up from its folder instead of the system's — ASI loaders, the DSOUND
    /// mods loader, ReShade/ENB. Moved even when signed.
    /// </summary>
    private static readonly HashSet<string> HookDlls = new(
        GameInstaller.AsiLoaders.Concat(GameInstaller.ModFolderPlugins.Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            .Concat(["dsound.dll", "dxgi.dll", "d3d9.dll", "d3d10.dll", "d3d12.dll", "opengl32.dll", "dinput.dll", "xinput1_3.dll",
                     "xinput9_1_0.dll", "xinput1_2.dll", "xinput1_1.dll", "wininet.dll", "dbghelp.dll", "binkw64.dll"]),
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ScriptHookFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "ScriptHookV.dll", "ScriptHookVDotNet.asi", "ScriptHookVDotNet2.dll", "ScriptHookVDotNet3.dll", "RAGEPluginHook.exe",
        "RAGEPluginHook.dll",
    };

    /// <summary>Folders only mods make.</summary>
    private static readonly Dictionary<string, StashKind> ModFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mods"] = StashKind.ModsFolder, [ModsLayout.OnigiriRoot] = StashKind.ModsFolder,     // onigiri: Onigiri's loose files (NaturalVision Enhanced)
        ["scripts"] = StashKind.ScriptHook, ["plugins"] = StashKind.ScriptHook, ["lspdfr"] = StashKind.ScriptHook,
        ["menyooStuff"] = StashKind.Other, ["RampageFiles"] = StashKind.Other, ["reshade-shaders"] = StashKind.Other,
        ["reshade-presets"] = StashKind.Other, ["enbseries"] = StashKind.Other, ["enbcache"] = StashKind.Other,
    };

    /// <summary>Settings of mods that aren't named after a binary.</summary>
    private static readonly HashSet<string> ModSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "ReShade.ini", "ReShadePreset.ini", "enbseries.ini", "enblocal.ini", "trainerv.ini",
    };

    private static readonly string[] AlwaysModExt = [".asi", ".addon", ".addon32", ".addon64"];
    private static readonly string[] BinaryExt = [".dll", ".exe"];
    /// <summary>A folder holding one of these is code, not data.</summary>
    private static readonly string[] CodeExt = [".asi", ".dll", ".exe", ".cs", ".vb", ".lua", ".addon64", ".addon32", ".fx"];

    // ---------------------------------------------------------------- scan

    /// <summary>What putting the mods away would move (the folder is only read).</summary>
    public static OnlineScan Scan(string gameDir)
    {
        if (!Directory.Exists(gameDir)) throw new DirectoryNotFoundException(L.T($"Game folder not found: {gameDir}"));
        var scan = new OnlineScan();
        var files = SafeEntries(() => Directory.EnumerateFiles(gameDir)).Select(Path.GetFileName).OfType<string>().ToList();
        var dirs = SafeEntries(() => Directory.EnumerateDirectories(gameDir)).Select(Path.GetFileName).OfType<string>().ToList();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Take(string name, bool folder, StashKind kind, string why)
        {
            if (taken.Add(name)) scan.Items.Add(new StashItem(name, folder, kind, why));
        }

        // binaries first: the rest follows them by name
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f);
            bool disabled = ext.Equals(".disabled", StringComparison.OrdinalIgnoreCase);     // switched off by ModDrop V
            var inner = disabled ? Path.GetExtension(Path.GetFileNameWithoutExtension(f)) : ext;
            if (ScriptHookFiles.Contains(f) || f.StartsWith("ScriptHookVDotNet", StringComparison.OrdinalIgnoreCase) && Is(inner, BinaryExt))
                Take(f, false, StashKind.ScriptHook, L.T("script hook"));
            else if (GameInstaller.ModFolderPlugins.Contains(f, StringComparer.OrdinalIgnoreCase) || HookDlls.Contains(f))
                Take(f, false, StashKind.Loader, L.T("loads mods into the game"));
            else if (Is(inner, AlwaysModExt))
                Take(f, false, StashKind.Plugin, disabled ? L.T("switched-off plugin") : L.T("plugin"));
            else if (Is(inner, BinaryExt) && !GameBinaries.Contains(disabled ? Path.GetFileNameWithoutExtension(f) : f)
                     && (disabled || !IsSigned(Path.Combine(gameDir, f))))
                Take(f, false, StashKind.Other, L.T("not a file of the game (unsigned)"));
        }

        // what ModDrop V installed into the game folder itself
        foreach (var top in OwnedTopLevel(gameDir))
        {
            bool folder = dirs.Contains(top, StringComparer.OrdinalIgnoreCase);
            if (!folder && !files.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
            if (folder && GameFolders.Contains(top) || !folder && GameBinaries.Contains(top)) continue;
            Take(top, folder, ModFolders.TryGetValue(top, out var k) ? k : StashKind.Other, L.T("installed by ModDrop V"));
        }

        // settings, logs and folders named like a moved binary (HeapAdjuster.ini, ScriptHookVDotNet.pdb, MDEngineV\)
        var owners = new Dictionary<string, StashItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in scan.Items.Where(i => !i.IsFolder)) owners.TryAdd(Stem(i.Name), i);
        foreach (var f in files)
            if (owners.TryGetValue(Stem(f), out var owner) && !GameBinaries.Contains(f) && !GameData.Contains(f)
                && !f.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                Take(f, false, owner.Kind, L.T($"goes with {owner.Name}"));
            else if (ModSettings.Contains(f))
                Take(f, false, StashKind.Other, L.T("mod settings"));

        foreach (var d in dirs)
        {
            if (GameFolders.Contains(d)) continue;
            if (ModFolders.TryGetValue(d, out var kind))
                Take(d, true, kind, kind == StashKind.ModsFolder ? L.T("mods folder") : L.T("mod folder"));
            else if (owners.TryGetValue(d, out var owner) || owners.TryGetValue(Stem(d), out owner))
                Take(d, true, owner.Kind, L.T($"goes with {owner.Name}"));
            else if (HoldsCode(Path.Combine(gameDir, d)))
                Take(d, true, StashKind.Other, L.T("folder with code"));
        }

        // add-on packs put straight into the game's own dlcpacks (not through mods): the game's dlclist.xml doesn't list them
        foreach (var pack in ForeignPacks(gameDir, scan.Warnings))
            Take(DlcpacksRel + "/" + pack, true, StashKind.Other, L.T("add-on pack in the game's own dlcpacks"));

        scan.Items.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        scan.EditedArchives.AddRange(EditedArchives(gameDir).Where(a => !scan.Items.Any(i => i.IsFolder &&
            a.StartsWith(i.Name + "/", StringComparison.OrdinalIgnoreCase))));
        if (scan.EditedArchives.Count > 0)
            scan.Warnings.Add(L.T($"Game archives were edited in place (not through mods): {string.Join(", ", scan.EditedArchives.Take(6))}") +
                              (scan.EditedArchives.Count > 6 ? L.T($" and {scan.EditedArchives.Count - 6} more") : "") +
                              L.T(". Putting mods away can't undo that — verify the game files in the launcher before playing online."));
        return scan;
    }

    private const string DlcpacksRel = "update/x64/dlcpacks";

    /// <summary>Folders in update\x64\dlcpacks that the game's own dlclist.xml (in its update.rpf) doesn't list.</summary>
    private static List<string> ForeignPacks(string gameDir, List<string> warnings)
    {
        var dir = Path.Combine(gameDir, "update", "x64", "dlcpacks");
        var packs = SafeEntries(() => Directory.EnumerateDirectories(dir)).Select(Path.GetFileName).OfType<string>().ToList();
        if (packs.Count == 0 || !File.Exists(Path.Combine(gameDir, "update", "update.rpf"))) return [];
        HashSet<string> game;
        try
        {
            var xml = GameInstaller.GameDlclist(gameDir, _ => { });
            game = Regex.Matches(xml, @"<Item>\s*dlcpacks:[\\/]+([^<]+?)[\\/]*\s*</Item>", RegexOptions.IgnoreCase)
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            warnings.Add(L.T($"Couldn't read the game's dlclist.xml ({ex.Message}) — add-on packs in update\\x64\\dlcpacks aren't checked."));
            return [];
        }
        if (game.Count == 0) return [];
        return packs.Where(p => !game.Contains(p)).ToList();
    }

    /// <summary>"ScriptHookVDotNet.asi" → "ScriptHookVDotNet"; "Menyoo.asi.disabled" → "Menyoo".</summary>
    private static string Stem(string name)
    {
        var s = name;
        if (s.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) s = s[..^".disabled".Length];
        return Path.GetFileNameWithoutExtension(s);
    }

    private static bool Is(string ext, string[] set) => set.Contains(ext, StringComparer.OrdinalIgnoreCase);

    /// <summary>The binary carries an Authenticode signature (every file the game ships with does).</summary>
    internal static bool IsSigned(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            return pe.PEHeaders.PEHeader is { CertificateTableDirectory.Size: > 0 };
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HoldsCode(string dir)
    {
        try
        {
            int seen = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 }))
            {
                if (Is(Path.GetExtension(f), CodeExt)) return true;
                if (++seen > 20_000) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    /// <summary>First segments of the game-folder paths ModDrop V's mods own ("scripts", "Foo.asi").</summary>
    private static IEnumerable<string> OwnedTopLevel(string gameDir)
    {
        if (!File.Exists(ModRegistry.PathFor(gameDir))) return [];
        try
        {
            return ModRegistry.Load(gameDir).Mods.SelectMany(m => m.Owns)
                .Select(p => p.Replace('\\', '/').TrimStart('/').Split('/')[0])
                .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// The game's own archives outside mods that are OPEN: the game ships every one encrypted (NG,
    /// 179 of 179 on Legacy and Enhanced), so an OPEN one was edited in place by a tool.
    /// </summary>
    public static List<string> EditedArchives(string gameDir)
    {
        var list = new List<string>();
        var roots = new[] { gameDir, Path.Combine(gameDir, "x64"), Path.Combine(gameDir, "update") };
        for (int r = 0; r < roots.Length; r++)
        {
            if (!Directory.Exists(roots[r])) continue;
            var opts = new EnumerationOptions { RecurseSubdirectories = r > 0, IgnoreInaccessible = true };
            foreach (var f in SafeEntries(() => Directory.EnumerateFiles(roots[r], "*.rpf", opts)))
            {
                try
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    Span<byte> h = stackalloc byte[16];
                    if (fs.Read(h) < 16 || BitConverter.ToUInt32(h) != Rpf7.Magic) continue;
                    if (BitConverter.ToUInt32(h[12..]) == Rpf7.EncOpen)
                        list.Add(Path.GetRelativePath(gameDir, f).Replace('\\', '/'));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return list;
    }

    private static List<string> SafeEntries(Func<IEnumerable<string>> list)
    {
        try
        {
            return list().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ---------------------------------------------------------------- the game must be closed

    private static readonly string[] GameProcesses =
        ["GTA5", "GTA5_Enhanced", "GTA5_BE", "GTA5_Enhanced_BE", "PlayGTAV", "GTAVLauncher", "RAGEPluginHook"];

    /// <summary>GTA V processes that are running (files in the game folder are in use while they are).</summary>
    public static List<string> RunningGame()
    {
        var found = new List<string>();
        foreach (var name in GameProcesses)
        {
            try
            {
                var ps = Process.GetProcessesByName(name);
                if (ps.Length > 0) found.Add(name + ".exe");
                foreach (var p in ps) p.Dispose();
            }
            catch (Exception)
            {
            }
        }
        return found;
    }

    /// <summary>The game itself runs (not just its launcher): it has its archives open.</summary>
    public static bool GameRuns() =>
        RunningGame().Any(n => n.Equals(GameEditions.LegacyExe, StringComparison.OrdinalIgnoreCase)
                               || n.Equals(GameEditions.EnhancedExe, StringComparison.OrdinalIgnoreCase));

    private static void EnsureClosed()
    {
        var running = RunningGame();
        if (running.Count > 0)
            throw new InvalidOperationException(L.T($"GTA V is running ({string.Join(", ", running)}) — close the game first."));
    }

    // ---------------------------------------------------------------- put away / bring back

    /// <summary>
    /// Move everything <see cref="Scan"/> finds into the stash. All or nothing: a file in use stops
    /// it and whatever moved goes back.
    /// </summary>
    /// <returns>the items moved (empty: the folder had no mods)</returns>
    public static List<StashItem> PutAway(string gameDir, GameEdition? edition, Action<string> log, bool checkGame = true)
    {
        if (IsOn(gameDir)) throw new InvalidOperationException(L.T("The mods are put away already."));
        if (checkGame) EnsureClosed();
        var scan = Scan(gameDir);
        if (scan.Items.Count == 0)
        {
            log(L.T("Nothing to put away — the game folder has no mods."));
            BattlEye.TurnOn(gameDir, log);
            return [];
        }
        var stash = StashDir(gameDir);
        bool made = !Directory.Exists(stash);
        Directory.CreateDirectory(stash);
        // anything an earlier restore left in the stash would be taken for part of these mods
        var old = SafeEntries(() => Directory.EnumerateFileSystemEntries(stash)).Select(Path.GetFileName).OfType<string>()
            .Where(n => !n.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (old.Count > 0)
            throw new IOException(L.T($"{StashName} still holds files from an earlier time ({string.Join(", ", old.Take(4))}) — " +
                                      $"move or delete them first."));

        // the manifest goes first: if the app dies halfway, the stash is still known and comes back whole
        var manifest = new StashManifest
        {
            Created = DateTime.Now, Edition = (edition ?? GameEditions.Detect(gameDir) ?? GameEdition.Legacy).ToString(),
            Items = scan.Items.Select(i => new StashManifestItem { Name = i.Name, Folder = i.IsFolder, Kind = i.Kind }).ToList(),
        };
        TextIo.WriteJson(ManifestPath(gameDir), manifest);

        var moved = new List<StashItem>();
        try
        {
            foreach (var item in scan.Items)
            {
                var from = Path.Combine(gameDir, item.Name);
                var to = Path.Combine(stash, item.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (item.IsFolder) Directory.Move(from, to);
                else File.Move(from, to);
                moved.Add(item);
                log(L.T($"  put away {item.Name}{(item.IsFolder ? "\\" : "")} — {item.Why}"));
            }
        }
        catch (Exception ex)
        {
            log(L.T($"[!] {ex.Message} — putting back what was moved."));
            foreach (var item in Enumerable.Reverse(moved))
            {
                try
                {
                    var from = Path.Combine(stash, item.Name);
                    var to = Path.Combine(gameDir, item.Name);
                    if (item.IsFolder) Directory.Move(from, to);
                    else File.Move(from, to);
                }
                catch (Exception back)
                {
                    log(L.T($"[!] {item.Name} stays in {StashName}: {back.Message}"));
                }
            }
            PruneEmpty(stash);
            if (StashEmpty(gameDir))
            {
                TryDelete(ManifestPath(gameDir));
                if (made) TryDeleteDir(stash);
            }
            throw new IOException(Friendly(ex), ex);
        }
        log(L.T($"{moved.Count} item(s) moved into {StashName}. The game starts without mods."));
        BattlEye.TurnOn(gameDir, log);
        return moved;
    }

    /// <summary>
    /// Move everything in the stash back where it was. A file the game folder has again in the
    /// meantime wins; the stashed one stays in the stash (and is reported).
    /// </summary>
    public static RestoreResult Restore(string gameDir, Action<string> log, bool checkGame = true)
    {
        var stash = StashDir(gameDir);
        if (!IsOn(gameDir)) throw new InvalidOperationException(L.T("No mods are put away in this game."));
        if (checkGame) EnsureClosed();
        var restored = new List<string>();
        var left = new List<string>();
        foreach (var entry in SafeEntries(() => Directory.EnumerateFileSystemEntries(stash)))
        {
            var name = Path.GetFileName(entry);
            if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
            MoveBack(entry, Path.Combine(gameDir, name), name, left, log);
            restored.Add(name);
            log(L.T($"  brought back {name}"));
        }
        File.Delete(ManifestPath(gameDir));
        if (left.Count == 0) TryDeleteDir(stash);
        else log(L.T($"[!] Kept in {StashName} — the game folder has these again: {string.Join(", ", left)}"));
        log(L.T($"{restored.Count} item(s) back in the game folder."));
        BattlEye.TurnOff(gameDir, log);
        return new RestoreResult(restored, left);
    }

    /// <summary>A file / folder goes back; into a folder that exists again, entry by entry.</summary>
    private static void MoveBack(string from, string to, string rel, List<string> left, Action<string> log)
    {
        if (Directory.Exists(from))
        {
            if (!Directory.Exists(to) && !File.Exists(to))
            {
                Directory.Move(from, to);
                return;
            }
            if (File.Exists(to))
            {
                left.Add(rel + "\\");
                return;
            }
            foreach (var e in Directory.EnumerateFileSystemEntries(from).ToList())
                MoveBack(e, Path.Combine(to, Path.GetFileName(e)), rel + "\\" + Path.GetFileName(e), left, log);
            if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
            return;
        }
        if (File.Exists(to) || Directory.Exists(to))
        {
            left.Add(rel);
            return;
        }
        File.Move(from, to);
    }

    /// <summary>Remove the empty folders under <paramref name="dir"/> (what nested items leave behind).</summary>
    private static void PruneEmpty(string dir)
    {
        foreach (var sub in SafeEntries(() => Directory.EnumerateDirectories(dir)))
        {
            PruneEmpty(sub);
            TryDeleteDir(sub);
        }
    }

    private static bool StashEmpty(string gameDir) =>
        !SafeEntries(() => Directory.EnumerateFileSystemEntries(StashDir(gameDir)))
            .Any(e => !Path.GetFileName(e).Equals(ManifestName, StringComparison.OrdinalIgnoreCase));

    private static string Friendly(Exception ex) => ex is UnauthorizedAccessException or IOException
        ? L.T($"{ex.Message} Is the game or a tool still using its files? Nothing was changed.")
        : ex.Message;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
