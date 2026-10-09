using System.Diagnostics;
using System.Globalization;
using Mdv.Core.Index;
using Mdv.Core.Mods;

namespace Mdv.Core;

/// <summary>How one line of the game status stands.</summary>
public enum StatusLevel
{
    /// <summary>In place and fine.</summary>
    Ok,
    /// <summary>Neutral: not there, but nothing is wrong (ModDrop V sets it up when needed).</summary>
    Info,
    /// <summary>Something the player should know or fix.</summary>
    Warning,
}

/// <summary>One line of the game status: what it is, its state in a few words, and more on hover.</summary>
public sealed record StatusItem(string Key, string Label, string Value, StatusLevel Level, string? Detail = null);

/// <summary>The DLC packs the game mounts, as its file index worked them out.</summary>
/// <param name="Source">where the effective dlclist.xml came from</param>
public sealed record MountedDlcs(int Count, string? Source)
{
    public static MountedDlcs Of(GameIndex index) => new(index.LoadedDlcs.Count, index.DlcListSource);
}

/// <summary>What <see cref="GameStatus.Read"/> found in a game folder.</summary>
public sealed class GameStatusReport
{
    public List<StatusItem> Items { get; } = [];
    /// <summary>Archive copies in mods (from <see cref="ModsOverlay.Status"/>).</summary>
    public List<CopyStatus> Copies { get; } = [];

    /// <summary>Copies that went stale with a game update — <see cref="RefreshCopiesOp"/> renews them.</summary>
    public List<string> StaleCopies => Copies.Where(c => c.Stale is not null).Select(c => c.Archive).ToList();

    public StatusItem? this[string key] => Items.FirstOrDefault(i => i.Key == key);

    /// <summary>The worst level of all lines.</summary>
    public StatusLevel Worst => Items.Count == 0 ? StatusLevel.Info : Items.Max(i => i.Level);
}

/// <summary>
/// The state of a GTA V folder as far as mods go: the game build, the mods folder and what
/// loads it, the ASI loader, ScriptHookV (and whether it still matches the game build),
/// ScriptHookVDotNet, the DLC packs the game mounts and the archive copies in mods that a
/// game update left behind. The folder is only read.
/// </summary>
public static class GameStatus
{
    public const string GameKey = "game", ModsKey = "mods", LoaderKey = "loader", AsiKey = "asi",
                        ShvKey = "shv", ShvdnKey = "shvdn", RphKey = "rph", DlcKey = "dlc", CopiesKey = "copies",
                        OnlineKey = "online", RpfCacheKey = "rpfcache", BattlEyeKey = "battleye";

    /// <param name="dlcs">from the game's file index, once built: the number of DLC packs mounted</param>
    public static GameStatusReport Read(string gameDir, GameEdition? edition = null, MountedDlcs? dlcs = null)
    {
        var r = new GameStatusReport();
        var detected = GameEditions.Detect(gameDir);
        var e = edition ?? detected ?? GameEdition.Legacy;
        var exe = Path.Combine(gameDir, e.ExeName());
        var gameVersion = File.Exists(exe) ? Version(exe) : null;

        // the game itself
        if (!File.Exists(exe))
            r.Items.Add(new(GameKey, L.T("Game"), L.T($"no {e.ExeName()}"), StatusLevel.Warning,
                            L.T($"This folder has no {GameEditions.LegacyExe} / {GameEditions.EnhancedExe} — is it the GTA V folder?")));
        else if (GameEditions.IsAmbiguous(gameDir))
            r.Items.Add(new(GameKey, L.T("Game"), $"{e.DisplayName()} {gameVersion}", StatusLevel.Warning,
                            L.T($"Both {GameEditions.LegacyExe} and {GameEditions.EnhancedExe} are in this folder.")));
        else
            r.Items.Add(new(GameKey, L.T("Game"), $"{e.DisplayName()} {gameVersion}".TrimEnd(), StatusLevel.Ok, exe));

        // mods put away for GTA Online: the rest of the lines would only say what's missing
        if (OnlineMode.IsOn(gameDir))
        {
            var items = OnlineMode.Manifest(gameDir)?.Items ?? [];
            r.Items.Add(new(OnlineKey, L.T("GTA Online"), L.T("mods put away"), StatusLevel.Ok,
                            L.T($"{items.Count} item(s) wait in {OnlineMode.StashName}: ") +
                            string.Join(", ", items.Select(i => i.Folder ? i.Name + "\\" : i.Name)) +
                            L.T(". The game starts clean; bring the mods back when you're done with GTA Online.")));
            return r;
        }

        // the mods folder and what makes the game read it
        var mods = Path.Combine(gameDir, "mods");
        var modsUpdate = Path.Combine(mods, "update", "update.rpf");
        bool onigiri = ModsLayout.UsesOnigiri(gameDir);
        if (onigiri) OnigiriItems(gameDir, r);
        else if (File.Exists(modsUpdate) || Directory.Exists(modsUpdate))
            r.Items.Add(new(ModsKey, L.T("Mods folder"), L.T("set up"), StatusLevel.Ok, L.T("mods\\update\\update.rpf is in place.")));
        else if (Directory.Exists(mods))
            r.Items.Add(new(ModsKey, L.T("Mods folder"), L.T("no update.rpf copy yet"), StatusLevel.Info,
                            L.T("mods\\update\\update.rpf is copied from the game on the next install.")));
        else
            r.Items.Add(new(ModsKey, L.T("Mods folder"), L.T("not set up yet"), StatusLevel.Info,
                            L.T("The mods folder is created on the first install — the game's own files stay untouched.")));

        var plugins = GameInstaller.ModFolderPlugins.Where(p => File.Exists(Path.Combine(gameDir, p))).ToList();
        var usable = plugins.Where(p => GameInstaller.Serves(p, e)).ToList();
        if (onigiri) { }                                  // OnigiriItems said it
        else if (usable.Count > 0)
            r.Items.Add(new(LoaderKey, L.T("Mods loader"), string.Join(", ", usable), StatusLevel.Ok,
                            L.T("Lets the game load the files in the mods folder instead of its own.")));
        else if (plugins.Count > 0 && plugins.All(GameInstaller.IsSuperseded))
            r.Items.Add(new(LoaderKey, L.T("Mods loader"), L.T($"{string.Join(", ", plugins)} — fails on big archives"), StatusLevel.Warning,
                            L.T($"{string.Join(", ", plugins)} can keep the game from starting when an archive in mods is big " +
                            $"(a 2 GB+ update.rpf); ModDrop V replaces it with {GameInstaller.BundledPlugin(e)} on the next install.")));
        else if (plugins.Count > 0)
            r.Items.Add(new(LoaderKey, L.T("Mods loader"), L.T($"{string.Join(", ", plugins)} — not for {e.DisplayName()}"), StatusLevel.Warning,
                            L.T($"{string.Join(", ", plugins)} can't load the mods folder of {e.DisplayName()}; " +
                            $"ModDrop V adds {GameInstaller.BundledPlugin(e)} on the next install.")));
        else
            r.Items.Add(new(LoaderKey, L.T("Mods loader"), L.T("none yet"), StatusLevel.Info,
                            L.T($"ModDrop V adds {GameInstaller.BundledPlugin(e)} on the first install.")));

        // Enhanced's RPF cache: while it's on, the game can take the archives from it and pass over the mods folder
        if (e == GameEdition.Enhanced && !onigiri)
        {
            var sw = GameInstaller.RpfCacheSwitch;
            if (File.Exists(Path.Combine(gameDir, sw)))
                r.Items.Add(new(RpfCacheKey, L.T("RPF cache"), L.T("off"), StatusLevel.Ok,
                                L.T($"{sw} is in the game folder: the game reads the archives in mods, not its cached copy of them.")));
            else
                r.Items.Add(new(RpfCacheKey, L.T("RPF cache"), L.T($"on — no {sw}"),
                                Directory.Exists(mods) ? StatusLevel.Warning : StatusLevel.Info,
                                L.T($"With the cache on, the game can ignore the mods folder. ModDrop V adds the empty {sw} on the next install " +
                                $"(or create it in the game folder yourself).")));
        }

        // BattlEye keeps ASI loaders and script hooks out of the game
        if (BattlEye.IsOff(gameDir))
            r.Items.Add(new(BattlEyeKey, L.T("BattlEye"), L.T("off"), StatusLevel.Ok,
                            L.T($"{BattlEye.Switch} is in {BattlEye.FileName}: story mode starts with mods. “Play GTA Online” switches BattlEye back on.")));
        else
            r.Items.Add(new(BattlEyeKey, L.T("BattlEye"), L.T("on"), StatusLevel.Info,
                            L.T($"The anti-cheat keeps mod loaders and script hooks out of the game — unless it is switched off in the Rockstar Games " +
                                $"Launcher or in Steam’s launch options. ModDrop V puts {BattlEye.Switch} into {BattlEye.FileName} on the next install.")));

        // ASI loader
        var loaders = GameInstaller.AsiLoaders.Where(l => File.Exists(Path.Combine(gameDir, l))).ToList();
        int asiCount = SafeCount(gameDir, "*.asi");
        if (loaders.Count > 0)
            r.Items.Add(new(AsiKey, L.T("ASI loader"), string.Join(", ", loaders), StatusLevel.Ok,
                            string.Join("\n", loaders.Select(l => Describe(Path.Combine(gameDir, l)))) +
                            (asiCount > 0 ? L.T($"\n{asiCount} .asi plugin(s) in the game folder.") : "")));
        else if (asiCount > 0)
            r.Items.Add(new(AsiKey, L.T("ASI loader"), L.T("missing"), StatusLevel.Warning,
                            L.T($"{asiCount} .asi plugin(s) in the game folder, but nothing loads them. " +
                            $"Install ScriptHookV (it comes with one) or Ultimate ASI Loader.")));
        else
            r.Items.Add(new(AsiKey, L.T("ASI loader"), L.T("not installed"), StatusLevel.Info,
                            L.T("Only needed for .asi plugins and scripts.")));

        // ScriptHookV: made for a game build; an older one than the game stops scripts from starting
        var shv = Path.Combine(gameDir, "ScriptHookV.dll");
        if (File.Exists(shv))
            r.Items.Add(ScriptHookItem(Version(shv), gameVersion, e));
        else
            r.Items.Add(new(ShvKey, "ScriptHookV", L.T("not installed"), StatusLevel.Info,
                            L.T("Most script mods need it: dev-c.com/gtav/scripthookv")));

        var shvdn = Path.Combine(gameDir, "ScriptHookVDotNet.asi");
        if (File.Exists(shvdn))
        {
            var apis = new[] { "2", "3" }.Where(v => File.Exists(Path.Combine(gameDir, $"ScriptHookVDotNet{v}.dll")))
                                          .Select(v => "v" + v).ToList();
            var ver = Version(shvdn);
            r.Items.Add(new(ShvdnKey, "ScriptHookVDotNet", ver is null ? L.T("installed") : "v" + Short(ver),
                            File.Exists(shv) ? StatusLevel.Ok : StatusLevel.Warning,
                            (apis.Count > 0 ? L.T($"Script APIs: {string.Join(", ", apis)}.") : L.T("No ScriptHookVDotNet2/3.dll next to it.")) +
                            (File.Exists(shv) ? "" : L.T(" It needs ScriptHookV to run."))));
        }
        else
            r.Items.Add(new(ShvdnKey, "ScriptHookVDotNet", L.T("not installed"), StatusLevel.Info,
                            L.T("Needed by .NET script mods (.dll / .cs in scripts).")));

        // RAGE Plugin Hook: only worth a line when it's there (LSPDFR and the plugins in plugins\ need it)
        var rph = Path.Combine(gameDir, "RAGEPluginHook.exe");
        if (File.Exists(rph))
        {
            var ver = Version(rph);
            int rphPlugins = Directory.Exists(Path.Combine(gameDir, "plugins")) ? SafeCount(Path.Combine(gameDir, "plugins"), "*.dll") : 0;
            bool lspdfr = File.Exists(Path.Combine(gameDir, "plugins", "LSPD First Response.dll"));
            r.Items.Add(new(RphKey, L.T("RAGE Plugin Hook"), ver is null ? L.T("installed") : "v" + Short(ver),
                            e == GameEdition.Enhanced ? StatusLevel.Warning : StatusLevel.Ok,
                            L.T($"{rphPlugins} plugin(s) in plugins\\{(lspdfr ? L.T(", LSPD First Response among them") : "")}. Start the game through RAGEPluginHook.exe.") +
                            (e == GameEdition.Enhanced ? L.T(" Its Enhanced support is early — not every plugin works, LSPDFR doesn't yet.") : "")));
        }

        // DLC packs: what the game mounts (from the index), and the add-on packs in mods
        var dlcpacks = GameInstaller.DlcpacksDir(gameDir);
        int addons = Directory.Exists(dlcpacks)
            ? Directory.EnumerateDirectories(dlcpacks).Count(d => File.Exists(Path.Combine(d, "dlc.rpf")) &&
                                                                  !Directory.Exists(Path.Combine(gameDir, "update", "x64", "dlcpacks", Path.GetFileName(d))))
            : 0;                                              // a copy of one of the game's packs isn't an add-on
        var addonText = onigiri
            ? addons == 1 ? L.T("1 add-on pack in onigiri") : L.T($"{addons} add-on packs in onigiri")
            : addons == 1 ? L.T("1 add-on pack in mods") : L.T($"{addons} add-on packs in mods");
        r.Items.Add(dlcs is null
            ? new(DlcKey, L.T("DLC packs"), addonText, StatusLevel.Info, L.T("Reading the game's files for the full count…"))
            : new(DlcKey, L.T("DLC packs"), L.T($"{dlcs.Count} mounted · {addons} add-on"), StatusLevel.Ok,
                  L.T($"dlclist.xml ({dlcs.Source ?? L.T("none found")}) lists {dlcs.Count} packs the game mounts; {addonText}.")));

        // archive copies in mods, and which a game update left behind
        try
        {
            r.Copies.AddRange(ModsOverlay.Load(gameDir).Status());
        }
        catch (Exception ex)
        {
            r.Items.Add(new(CopiesKey, L.T("Copies in mods"), "can't read", StatusLevel.Warning, ex.Message));
            return r;
        }
        var stale = r.Copies.Where(c => c.Stale is not null).ToList();
        var copiesLabel = onigiri ? L.T("Copies in onigiri") : L.T("Copies in mods");
        if (r.Copies.Count == 0)
            r.Items.Add(new(CopiesKey, copiesLabel, L.T("none"), StatusLevel.Info,
                            onigiri ? L.T("Game archives are copied into onigiri only when a mod changes a file inside one; other files lie there loose.")
                                    : L.T("Game archives are copied into mods only when a mod changes them.")));
        else if (stale.Count > 0)
            r.Items.Add(new(CopiesKey, copiesLabel, L.T($"{stale.Count} of {r.Copies.Count} outdated"), StatusLevel.Warning,
                            L.T("Out of date (the game was updated after they were copied, or a pack is missing from a list) — a common reason for crashes:\n") +
                            string.Join("\n", stale.Select(c => $"{c.Shown}: {c.Stale}"))));
        else
            r.Items.Add(new(CopiesKey, copiesLabel, r.Copies.Count == 1 ? L.T("1, up to date") : L.T($"{r.Copies.Count}, up to date"),
                            StatusLevel.Ok, string.Join("\n", r.Copies.Select(c => c.Shown))));
        return r;
    }

    /// <summary>
    /// A game with Onigiri: the onigiri folder is where mods go (the mods line), onigiri.asi is what loads it (the loader
    /// line) — with a warning when a mods-folder plugin sits beside it, or ModDrop V's earlier mods wait in a mods folder
    /// the game no longer reads.
    /// </summary>
    private static void OnigiriItems(string gameDir, GameStatusReport r)
    {
        var root = Path.Combine(gameDir, ModsLayout.OnigiriRoot);
        var oldRegistry = Path.Combine(gameDir, "mods", ModRegistry.FileName);
        if (File.Exists(oldRegistry))
            r.Items.Add(new(ModsKey, L.T("Mods folder"), L.T("onigiri — mods folder not read"), StatusLevel.Warning,
                            L.T("The game runs Onigiri, which reads the onigiri folder and not the mods folder: what ModDrop V installed " +
                            "into the mods folder before doesn't show in the game. Install those mods again — they go into onigiri now.")));
        else
            r.Items.Add(new(ModsKey, L.T("Mods folder"), Directory.Exists(root) ? "onigiri" : L.T("onigiri, not made yet"), StatusLevel.Ok,
                            L.T("The game runs Onigiri: mods go into the onigiri folder as loose files (onigiri\\common = update.rpf\\common, " +
                            "onigiri\\platform = update.rpf\\x64, onigiri\\dlcpacks = update\\x64\\dlcpacks) — the game's archives aren't copied.")));

        var others = GameInstaller.ModFolderPlugins.Where(p => File.Exists(Path.Combine(gameDir, p))).ToList();
        if (others.Count > 0)
            r.Items.Add(new(LoaderKey, L.T("Mods loader"), $"{ModsLayout.OnigiriAsi} + {string.Join(", ", others)}", StatusLevel.Warning,
                            L.T($"Onigiri isn't made to run with a mods-folder plugin ({string.Join(", ", others)}) — NaturalVision " +
                            $"Enhanced warns about it. If the game misbehaves, take the plugin out.")));
        else
            r.Items.Add(new(LoaderKey, L.T("Mods loader"), ModsLayout.OnigiriAsi, StatusLevel.Ok,
                            L.T("Onigiri (it comes with NaturalVision Enhanced) loads the loose files in the onigiri folder over the game's.")));
    }

    /// <summary>
    /// ScriptHookV's file version names the builds it supports: "3889.0.1158.13" = Legacy
    /// 3889, Enhanced 1158 (older releases: "1.0.&lt;legacy build&gt;.0"). A hook older than the
    /// game build won't start scripts; a newer one is fine.
    /// </summary>
    internal static StatusItem ScriptHookItem(string? shvVersion, string? gameVersion, GameEdition e)
    {
        if (shvVersion is null)
            return new(ShvKey, "ScriptHookV", L.T("installed"), StatusLevel.Ok, L.T("ScriptHookV.dll has no version information."));
        int? made = null;
        var p = Parts(shvVersion);
        if (p.Length >= 3)
        {
            if (p[0] >= 1000) made = e == GameEdition.Enhanced ? p[2] : p[0];
            else if (p[0] == 1 && p[1] == 0 && e == GameEdition.Legacy) made = p[2];
        }
        var g = gameVersion is null ? [] : Parts(gameVersion);
        int? build = g.Length >= 3 ? g[2] : null;
        var shown = p.Length >= 4 && p[0] >= 1000 ? $"v{p[0]}.{p[1]} / {p[2]}.{p[3]}" : "v" + shvVersion;
        if (made is null || build is null)
            return new(ShvKey, "ScriptHookV", shown, StatusLevel.Ok, $"ScriptHookV.dll {shvVersion}");
        if (made < build)
            return new(ShvKey, "ScriptHookV", L.T($"{shown} — outdated"), StatusLevel.Warning,
                       L.T($"This ScriptHookV supports {e.DisplayName()} up to build {made}; the game is build {build}. " +
                       $"Scripts won't start until ScriptHookV is updated: dev-c.com/gtav/scripthookv"));
        return new(ShvKey, "ScriptHookV", shown, StatusLevel.Ok,
                   L.T($"Supports {e.DisplayName()} up to build {made} (the game is build {build})."));
    }

    private static int[] Parts(string version)
    {
        var list = new List<int>();
        foreach (var s in version.Split('.', ' ', ','))
        {
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) break;
            list.Add(n);
        }
        return [.. list];
    }

    private static string? Version(string file)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(file).FileVersion?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"3.6.0.0" → "3.6.0".</summary>
    private static string Short(string version) => version.EndsWith(".0", StringComparison.Ordinal) && version.Count(c => c == '.') == 3
        ? version[..^2]
        : version;

    private static string Describe(string file)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(file);
            var what = string.IsNullOrWhiteSpace(v.FileDescription) ? "" : $" — {v.FileDescription.Trim()}";
            var ver = string.IsNullOrWhiteSpace(v.FileVersion) ? "" : $" {v.FileVersion.Trim()}";
            return $"{Path.GetFileName(file)}{ver}{what}";
        }
        catch (Exception)
        {
            return Path.GetFileName(file);
        }
    }

    private static int SafeCount(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern).Count();
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
