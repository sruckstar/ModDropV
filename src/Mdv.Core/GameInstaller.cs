using System.Text;
using System.Text.RegularExpressions;
using Mdv.Core.Mods;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core;

/// <summary>
/// Installs a finished dlc.rpf into the GTA V game folder, mirroring a manual Add-On
/// install through the mods folder:
/// <code>
/// &lt;game&gt;/RageOpenV.asi + dinput8.dll | xinput1_4.dll   a mods-folder plugin + ASI loader (installed if none is there)
/// &lt;game&gt;/mods/                                   created if missing
/// &lt;game&gt;/mods/update/update.rpf                     copied from the game on first install
/// &lt;game&gt;/mods/update/update.rpf/common/data/dlclist.xml   gets &lt;Item&gt;dlcpacks:/&lt;DLC&gt;/&lt;/Item&gt;
/// &lt;game&gt;/mods/update/x64/dlcpacks/&lt;DLC&gt;/dlc.rpf    the build is copied here
/// </code>
/// update.rpf may be an unpacked folder (the file is edited on disk) or a real RPF7
/// archive (dlclist.xml is rewritten in place inside it). A fresh copy of the game's
/// update.rpf is still encrypted by the game: it is switched to OPEN on the first edit,
/// with the keys read from the game executable — the same thing OpenIV/CodeWalker do.
/// </summary>
public static partial class GameInstaller
{
    public const string DlclistInner = "common/data/dlclist.xml";

    /// <summary>Plugins that give the game a mods folder — any one of them is enough.</summary>
    public static readonly string[] ModFolderPlugins = ["OpenIV.asi", "DSOUND.dll", "OpenRPF.asi", "RageOpenV.asi"];

    /// <summary>
    /// Proxy DLLs that load *.asi plugins (ScriptHookV / Ultimate ASI Loader names).
    /// dsound.dll is not among them: in a GTA V folder that name is the DSOUND mods loader.
    /// </summary>
    public static readonly string[] AsiLoaders =
        ["dinput8.dll", "xinput1_4.dll", "version.dll", "winmm.dll", "winhttp.dll", "d3d11.dll"];

    /// <summary>
    /// The mods-folder plugin ModDrop V installs: RageOpenV.asi for both editions. It isn't shipped
    /// (its author asks not to redistribute it) — <see cref="RageOpenV"/> downloads the latest release.
    /// </summary>
    public static string BundledPlugin(GameEdition e) => RageOpenV.FileName;

    /// <summary>The ASI loader shipped in data/plugins for an edition (Alexander Blade's GTA V / GTA V Enhanced loader).</summary>
    public static string BundledAsiLoader(GameEdition e) => e == GameEdition.Enhanced ? "xinput1_4.dll" : "dinput8.dll";

    /// <summary>
    /// Mods loaders ModDrop V replaces with RageOpenV: DSOUND.dll (Simple Mods Loader) chokes on big
    /// archives — the game doesn't start with a 2 GB+ mods\update\update.rpf.
    /// </summary>
    public static readonly string[] Superseded = ["DSOUND.dll"];

    internal static bool IsSuperseded(string plugin) => Superseded.Contains(plugin, StringComparer.OrdinalIgnoreCase);

    /// <summary>Plugins that can't serve an edition's mods folder (OpenIV.asi predates Enhanced; DSOUND.dll is superseded).</summary>
    internal static bool Serves(string plugin, GameEdition e) =>
        !(e == GameEdition.Enhanced && plugin.Equals("OpenIV.asi", StringComparison.OrdinalIgnoreCase)) && !IsSuperseded(plugin);

    [GeneratedRegex(@"([ \t]*)</Paths>")] private static partial Regex PathsCloseRe();
    [GeneratedRegex(@"\n([ \t]*)<Item>")] private static partial Regex ItemIndentRe();

    // ================================================================ game preparation

    /// <summary>
    /// The edition to build for when installing into <paramref name="gameDir"/>: the
    /// requested one (with a warning if the folder runs the other), else the one its
    /// executable names. A folder with no executable (a bare mods tree) counts as Legacy.
    /// </summary>
    /// <param name="requested">forced edition, or null to take it from the executable</param>
    public static GameEdition ResolveEdition(string gameDir, GameEdition? requested, Action<string> log)
    {
        if (!Directory.Exists(gameDir))
            throw new DirectoryNotFoundException(L.T($"Game folder not found: {gameDir}"));

        var detected = GameEditions.Detect(gameDir);
        GameEdition edition;
        if (requested is { } r)
        {
            edition = r;
            if (detected is { } d && d != r)
                log(L.T($"    [!] The game folder runs {d.DisplayName()} ({d.ExeName()}), but the pack is " +
                    $"built for {r.DisplayName()} — the game won't load it correctly."));
        }
        else if (detected is { } d)
            edition = d;
        else if (GameEditions.IsAmbiguous(gameDir))
            throw new InvalidOperationException(
                L.T($"The game folder holds both {GameEditions.LegacyExe} and {GameEditions.EnhancedExe} — " +
                $"choose the game version (Legacy / Enhanced) explicitly."));
        else
            edition = GameEdition.Legacy;               // not a recognisable install (e.g. a bare mods tree)
        return edition;
    }

    /// <summary>
    /// Make the game able to take add-on packs: a mods-folder plugin (RageOpenV.asi + ASI
    /// loader when the game has none), the mods folder, and mods/update/update.rpf copied
    /// from the game. Idempotent.
    /// </summary>
    /// <param name="pluginsDir">folder with the bundled plugins (data/plugins)</param>
    /// <param name="journal">records the plugins it installs; the mods folder and the plain copy of
    /// update.rpf are left in place on a rollback — neither changes what the game loads</param>
    public static void PrepareGame(string gameDir, GameEdition edition, string pluginsDir, Action<string> log,
                                   InstallJournal? journal = null)
    {
        // only touch real game installs: a folder without the executable gets no plugin
        if (GameEditions.Detect(gameDir) is not null || GameEditions.IsAmbiguous(gameDir))
            EnsureModFolderPlugin(gameDir, edition, pluginsDir, log, journal);
        EnsureModsFolder(gameDir, log);
        EnsureUpdateRpf(gameDir, log);
    }

    /// <summary>
    /// Install RageOpenV.asi when the game has none of <see cref="ModFolderPlugins"/> that
    /// works for its edition — the ones it replaces (DSOUND.dll, OpenIV.asi in Enhanced) are
    /// moved out of the game folder, as RageOpenV asks — and the bundled ASI loader when
    /// nothing in the folder would load it.
    /// </summary>
    public static void EnsureModFolderPlugin(string gameDir, GameEdition edition, string pluginsDir, Action<string> log,
                                             InstallJournal? journal = null)
    {
        var present = ModFolderPlugins.Where(p => File.Exists(Path.Combine(gameDir, p))).ToList();
        var usable = present.Where(p => Serves(p, edition)).ToList();
        if (usable.Count == 0)
        {
            var plugin = BundledPlugin(edition);
            var src = PluginSource(pluginsDir, plugin, log);
            if (src is null)
            {
                // offline with nothing cached: a loader that fails on big archives still beats none
                if (present.FirstOrDefault(IsSuperseded) is { } old)
                {
                    log(L.T($"    [!] {old} stays for now — it can fail on big archives. ModDrop V replaces it " +
                        $"with {plugin} once it can download it ({RageOpenV.Page})."));
                    return;
                }
                throw new FileNotFoundException(
                    L.T($"The game has no mods-folder plugin, and {plugin} couldn't be downloaded. Check the internet " +
                    $"connection, or download it from {RageOpenV.Page} and put {plugin} into the game folder."));
            }
            foreach (var old in present)
            {
                log(IsSuperseded(old)
                    ? L.T($"    {old} fails on big archives — moved it out of the game folder.")
                    : L.T($"    {old} can't load the mods folder of {edition.DisplayName()} — moved it out of the game folder."));
                MoveOut(Path.Combine(gameDir, old), journal);
            }
            CopyInto(gameDir, src, plugin, journal);
            var tag = RageOpenV.TagOf(src) is { } t ? $" {t}" : "";
            log(L.T($"    Installed {plugin}{tag} into the game folder — it lets {edition.DisplayName()} load the mods folder."));
            usable.Add(plugin);
        }

        // an .asi does nothing on its own: something has to load it
        bool selfLoading = usable.Any(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (selfLoading || AsiLoaders.Any(l => File.Exists(Path.Combine(gameDir, l)))) return;

        var loader = BundledAsiLoader(edition);
        if (!File.Exists(Path.Combine(pluginsDir, loader)))
        {
            log(L.T($"    [!] No ASI loader in the game folder — {string.Join(" / ", usable)} will not be loaded " +
                $"and the game will ignore the mods folder. Install ScriptHookV or Ultimate ASI Loader " +
                $"({loader} for {edition.DisplayName()})."));
            return;
        }
        CopyInto(gameDir, Path.Combine(pluginsDir, loader), loader, journal);
        log(L.T($"    Installed the ASI loader {loader} — it loads {string.Join(" / ", usable)} into {edition.DisplayName()}."));
    }

    /// <summary>A plugin from data/plugins, or — RageOpenV, which isn't shipped — its latest download.</summary>
    private static string? PluginSource(string pluginsDir, string name, Action<string> log)
    {
        var local = Path.Combine(pluginsDir, name);
        if (File.Exists(local)) return local;
        return name.Equals(RageOpenV.FileName, StringComparison.OrdinalIgnoreCase) ? RageOpenV.Latest(log) : null;
    }

    private static void CopyInto(string gameDir, string src, string name, InstallJournal? journal)
    {
        var dst = Path.Combine(gameDir, name);
        bool existed = File.Exists(dst);
        if (existed && journal is not null) journal.MoveAside(dst, keep: true);
        PathUtil.Copy2(src, dst);
        if (!existed) journal?.FileCreated(dst);
    }

    /// <summary>Take a replaced plugin out of the game folder: into the journal's stash (an uninstall
    /// puts it back), or renamed to &lt;name&gt;.bak without one.</summary>
    private static void MoveOut(string path, InstallJournal? journal)
    {
        if (journal is not null) journal.MoveAside(path, keep: true);
        else File.Move(path, path + ".bak", overwrite: true);
    }

    public static void EnsureModsFolder(string gameDir, Action<string> log)
    {
        var mods = Path.Combine(gameDir, "mods");
        if (Directory.Exists(mods)) return;
        Directory.CreateDirectory(mods);
        log(L.T($"    Created the mods folder: {mods}"));
    }

    /// <summary>Copy the game's update\update.rpf into mods (first install only).</summary>
    public static void EnsureUpdateRpf(string gameDir, Action<string> log)
    {
        var modsUpd = Path.Combine(gameDir, "mods", "update", "update.rpf");
        if (File.Exists(modsUpd) || Directory.Exists(modsUpd)) return;

        var gameUpd = Path.Combine(gameDir, "update", "update.rpf");
        if (!File.Exists(gameUpd))
            throw new FileNotFoundException(
                L.T($"update.rpf not found: neither in mods ({modsUpd}) nor in the game ({gameUpd}).\n" +
                $"Check that the selected folder is the GTA V game folder."));

        long size = new FileInfo(gameUpd).Length;
        var root = Path.GetPathRoot(Path.GetFullPath(modsUpd));
        if (!string.IsNullOrEmpty(root))
        {
            try
            {
                long free = new DriveInfo(root).AvailableFreeSpace;
                if (free < size + (256L << 20))
                    throw new IOException(
                        L.T($"Not enough disk space to copy update.rpf into mods: it needs {MergedPack.FmtSize(size)}, " +
                        $"{MergedPack.FmtSize(free)} free on {root}."));
            }
            catch (ArgumentException) { /* not a local drive — just try */ }
        }

        log(L.T($"    Copying update\\update.rpf into mods ({MergedPack.FmtSize(size)}) — first install only, this takes a while…"));
        Directory.CreateDirectory(Path.GetDirectoryName(modsUpd)!);
        var tmp = modsUpd + ".tmp";
        try
        {
            File.Copy(gameUpd, tmp, overwrite: true);
            File.Move(tmp, modsUpd);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
        log(L.T($"    update.rpf copied -> {modsUpd}"));
    }

    // ================================================================ dlclist.xml

    /// <summary>dlclist.xml text with the pack entry added, or null if already listed.</summary>
    public static string? DlclistWithEntry(string text, string dlcName, Action<string> log)
    {
        var entryPath = $"dlcpacks:/{dlcName}/";
        var entryLine = $"<Item>{entryPath}</Item>";

        if (Regex.IsMatch(text, @"<Item>\s*" + Regex.Escape(entryPath) + @"\s*</Item>"))
        {
            log(L.T($"    dlclist.xml: '{entryPath}' already present — skipping."));
            return null;
        }

        var m = PathsCloseRe().Match(text);
        if (!m.Success)
            throw new InvalidDataException(
                L.T("Could not find the <Paths></Paths> section in dlclist.xml — " +
                "the file doesn't look like dlclist.xml."));

        var im = ItemIndentRe().Match(text);
        var indent = im.Success ? im.Groups[1].Value : m.Groups[1].Value + "  ";
        var insertion = $"{indent}{entryLine}\n{m.Groups[1].Value}</Paths>";
        log(L.T($"    dlclist.xml: added '{entryLine}'."));
        return text[..m.Index] + insertion + text[(m.Index + m.Length)..];
    }

    /// <summary>dlclist.xml text with the pack entry removed, or null if it wasn't listed.</summary>
    public static string? DlclistWithoutEntry(string text, string dlcName, Action<string> log)
    {
        var entryPath = $"dlcpacks:/{dlcName}/";
        // the whole line: its indent and line break go with it
        var re = new Regex(@"^[ \t]*<Item>\s*" + Regex.Escape(entryPath) + @"\s*</Item>[ \t]*\r?\n?",
                           RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!re.IsMatch(text))
        {
            log(L.T($"    dlclist.xml: '{entryPath}' not listed — nothing to remove."));
            return null;
        }
        log($"    dlclist.xml: removed '<Item>{entryPath}</Item>'.");
        return re.Replace(text, "");
    }

    private static void EditDlclistFile(string dlclist, Func<string, string?> edit)
    {
        var updated = edit(TextIo.ReadText(dlclist));
        if (updated is not null) TextIo.WriteText(dlclist, updated);
    }

    /// <summary>Run <paramref name="op"/> without keys first; load them from the game only when the archive needs them.</summary>
    private static T WithKeys<T>(string gameDir, Action<string> log, ref GameCrypto? crypto, Func<GameCrypto?, T> op)
    {
        try
        {
            return op(crypto);
        }
        catch (RpfEncryptedException) when (crypto is null)
        {
            crypto = GameCrypto.ForGame(gameDir, log);
            return op(crypto);
        }
    }

    private static void EditDlclistRpf(string gameDir, string updateRpf, Func<string, string?> edit, Action<string> log)
    {
        GameCrypto? crypto = null;
        var raw = WithKeys(gameDir, log, ref crypto, c => RpfTools.ReadInnerFile(updateRpf, DlclistInner, c));
        var updated = edit(TextIo.DecodeUtf8Sig(raw));
        if (updated is null) return;

        var info = RpfTools.PatchInnerFile(updateRpf, DlclistInner, Encoding.UTF8.GetBytes(updated), crypto);
        if (info.Opened)
            log(L.T("    update.rpf switched from the game's encryption to OPEN (as OpenIV does on the first edit)."));
        log(info.Moved
            ? L.T($"    dlclist.xml rewritten in update.rpf ({info.Uncompressed} bytes, {info.OnDisk} compressed, moved to the end of the archive).")
            : L.T($"    dlclist.xml rewritten in update.rpf ({info.Uncompressed} bytes, {info.OnDisk} compressed)."));
    }

    /// <summary>The game's own dlclist.xml, read out of its encrypted update\update.rpf.</summary>
    private static string GameDlclist(string gameDir, Action<string> log)
    {
        var gameUpd = Path.Combine(gameDir, "update", "update.rpf");
        if (!File.Exists(gameUpd))
            throw new FileNotFoundException(L.T($"update.rpf not found in the game: {gameUpd}"));
        GameCrypto? crypto = null;
        var raw = WithKeys(gameDir, log, ref crypto, c => RpfTools.ReadInnerFile(gameUpd, DlclistInner, c));
        return TextIo.DecodeUtf8Sig(raw);
    }

    /// <summary>
    /// Add <c>dlcpacks:/&lt;dlcName&gt;/</c> to the game's dlclist.xml (no-op if listed).
    /// Separate from the copy so an unchanged pack can be re-registered cheaply.
    /// </summary>
    /// <returns>true when the entry was added (false: already listed)</returns>
    /// <exception cref="FileNotFoundException">mods, update.rpf or dlclist.xml is missing</exception>
    public static bool RegisterInDlclist(string gameDir, string dlcName, Action<string> log, InstallJournal? journal = null)
    {
        bool changed = false;
        EditDlclist(gameDir, text => Changed(DlclistWithEntry(text, dlcName, log), ref changed), createFromGame: true, log);
        if (changed) journal?.DlclistAdd(dlcName);
        return changed;
    }

    /// <summary>Remove <c>dlcpacks:/&lt;dlcName&gt;/</c> from the game's dlclist.xml (no-op if not listed).</summary>
    /// <returns>true when the entry was removed (false: it wasn't listed)</returns>
    public static bool UnregisterFromDlclist(string gameDir, string dlcName, Action<string> log, InstallJournal? journal = null)
    {
        bool changed = false;
        EditDlclist(gameDir, text => Changed(DlclistWithoutEntry(text, dlcName, log), ref changed), createFromGame: false, log);
        if (changed) journal?.DlclistRemove(dlcName);
        return changed;
    }

    private static string? Changed(string? updated, ref bool changed)
    {
        changed = updated is not null;
        return updated;
    }

    /// <param name="edit">new dlclist.xml text, or null to leave it untouched</param>
    /// <param name="createFromGame">a loose update.rpf folder without its own dlclist.xml gets the game's copy first</param>
    private static void EditDlclist(string gameDir, Func<string, string?> edit, bool createFromGame, Action<string> log)
    {
        var mods = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(mods))
            throw new FileNotFoundException(
                L.T($"'mods' folder not found in the game folder: {mods}\n" +
                $"Add-On installation is only supported via the mods folder."));

        var updateRpf = Path.Combine(mods, "update", "update.rpf");
        if (Directory.Exists(updateRpf))
        {
            var dlclist = Path.Combine(updateRpf, "common", "data", "dlclist.xml");
            if (!File.Exists(dlclist))
            {
                if (!createFromGame) return;                 // nothing of ours can be listed there
                // a loose-file update.rpf (override folder) without its own dlclist.xml:
                // start it from the game's copy
                if (!File.Exists(Path.Combine(gameDir, "update", "update.rpf")))
                    throw new FileNotFoundException(
                        L.T($"dlclist.xml file not found: {dlclist}\n" +
                        $"Check that update.rpf was unpacked into mods via OpenIV."));
                var text = GameDlclist(gameDir, log);
                Directory.CreateDirectory(Path.GetDirectoryName(dlclist)!);
                TextIo.WriteText(dlclist, text);
                log(L.T($"    dlclist.xml taken from the game's update.rpf -> {dlclist}"));
            }
            EditDlclistFile(dlclist, edit);
        }
        else if (File.Exists(updateRpf))
        {
            log(L.T($"    update.rpf is an archive, editing dlclist.xml inside: {updateRpf}"));
            EditDlclistRpf(gameDir, updateRpf, edit, log);
        }
        else
        {
            throw new FileNotFoundException(
                L.T($"update.rpf not found (neither folder nor archive): {updateRpf}\n" +
                $"Check the mods/update structure in the game folder."));
        }
    }

    /// <summary>Install a built dlc.rpf into the game; returns the installed path.</summary>
    /// <param name="journal">records every change, so a failed transaction can take it back</param>
    /// <param name="done">the last log line (what to look for in the game)</param>
    /// <param name="subPacks">sub-packs (dlc1.rpf…) that go into the pack folder next to its dlc.rpf</param>
    public static string InstallToGame(string gameDir, string dlcRpf, string dlcName, Action<string> log,
                                       InstallJournal? journal = null,
                                       string? done = null,
                                       IReadOnlyList<string>? subPacks = null)
    {
        log(L.T($"Installing into game: {gameDir}"));
        RegisterInDlclist(gameDir, dlcName, log, journal);
        var destDir = PackDir(gameDir, dlcName);
        bool newDir = !Directory.Exists(destDir);
        Directory.CreateDirectory(destDir);
        if (newDir) journal?.DirCreated(destDir);
        var dest = Path.Combine(destDir, "dlc.rpf");
        if (!newDir && journal is not null)
        {
            if (File.Exists(dest)) journal.MoveAside(dest, keep: false);
            else journal.FileCreated(dest);
        }
        PathUtil.Copy2(dlcRpf, dest);
        log(L.T($"    dlc.rpf copied -> {dest}"));
        foreach (var sub in subPacks ?? [])
        {
            var subDest = Path.Combine(destDir, Path.GetFileName(sub));
            if (!newDir && journal is not null)
            {
                if (File.Exists(subDest)) journal.MoveAside(subDest, keep: false);
                else journal.FileCreated(subDest);
            }
            PathUtil.Copy2(sub, subDest);
            log(L.T($"    {Path.GetFileName(sub)} (sub-pack) copied -> {subDest}"));
        }
        // a switched-off copy of an earlier version is superseded by this one
        var parked = DisabledPackDir(gameDir, dlcName);
        if (Directory.Exists(parked))
        {
            Discard(parked, journal);
            log(L.T($"    Removed the switched-off earlier copy: {parked}"));
        }
        log(done ?? L.T("Add-On installed. Launch the game and check the weapon in the shop."));
        return dest;
    }

    // ================================================================ switching packs off / removing them

    /// <summary><c>&lt;game&gt;/mods/update/x64/dlcpacks</c></summary>
    public static string DlcpacksDir(string gameDir) => Path.Combine(gameDir, "mods", "update", "x64", "dlcpacks");

    /// <summary>Where a switched-off pack is parked: beside dlcpacks, so nothing that scans it finds the pack.</summary>
    public static string DisabledDlcpacksDir(string gameDir) =>
        Path.Combine(gameDir, "mods", "update", "x64", "dlcpacks_disabled");

    public static string PackDir(string gameDir, string dlcName) => Path.Combine(DlcpacksDir(gameDir), dlcName);
    public static string DisabledPackDir(string gameDir, string dlcName) => Path.Combine(DisabledDlcpacksDir(gameDir), dlcName);

    /// <summary>
    /// Switch an installed dlcpack off without deleting it: its dlclist.xml entry is
    /// removed, and the pack folder is moved from dlcpacks to dlcpacks_disabled — the
    /// DSOUND.dll loader adds every folder in dlcpacks to the list on its own.
    /// </summary>
    public static void DisablePack(string gameDir, string dlcName, Action<string> log, InstallJournal? journal = null)
    {
        log(L.T($"Switching off '{dlcName}'…"));
        UnregisterFromDlclist(gameDir, dlcName, log, journal);
        var live = PackDir(gameDir, dlcName);
        if (!Directory.Exists(live)) return;
        var parked = DisabledPackDir(gameDir, dlcName);
        if (Directory.Exists(parked)) Discard(parked, journal);
        Directory.CreateDirectory(DisabledDlcpacksDir(gameDir));
        Directory.Move(live, parked);
        journal?.MovedWithin(live, parked);
        log(L.T($"    Pack moved aside (kept for switching back on) -> {parked}"));
    }

    /// <summary>Undo <see cref="DisablePack"/>: the pack goes back to dlcpacks and into dlclist.xml.</summary>
    public static void EnablePack(string gameDir, string dlcName, Action<string> log, InstallJournal? journal = null)
    {
        log(L.T($"Switching on '{dlcName}'…"));
        var live = PackDir(gameDir, dlcName);
        var parked = DisabledPackDir(gameDir, dlcName);
        if (Directory.Exists(parked))
        {
            if (Directory.Exists(live)) Discard(live, journal);
            Directory.CreateDirectory(DlcpacksDir(gameDir));
            Directory.Move(parked, live);
            journal?.MovedWithin(parked, live);
            log(L.T($"    Pack moved back -> {live}"));
        }
        if (!File.Exists(Path.Combine(live, "dlc.rpf")))
            throw new FileNotFoundException(L.T($"The switched-off pack '{dlcName}' is gone from the game folder — install it again."));
        RegisterInDlclist(gameDir, dlcName, log, journal);
    }

    /// <summary>Remove a dlcpack from the game entirely: dlclist.xml entry and its folder (on or off).</summary>
    public static void UninstallPack(string gameDir, string dlcName, Action<string> log, InstallJournal? journal = null)
    {
        log(L.T($"Removing '{dlcName}' from the game…"));
        UnregisterFromDlclist(gameDir, dlcName, log, journal);
        foreach (var dir in new[] { PackDir(gameDir, dlcName), DisabledPackDir(gameDir, dlcName) })
            if (Directory.Exists(dir))
            {
                Discard(dir, journal);
                log(L.T($"    Deleted {dir}"));
            }
    }

    /// <summary>Delete a folder — or, inside a transaction, move it aside until the commit.</summary>
    private static void Discard(string dir, InstallJournal? journal)
    {
        if (journal is not null) journal.MoveAside(dir, keep: false);
        else PathUtil.DeleteDir(dir);
    }
}
