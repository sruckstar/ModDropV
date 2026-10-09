using System.IO.Pipes;
using System.Text;
using Mdv.Core.Mods;

namespace Mdv.Core;

/// <summary>What became of a request to load packs into the running game.</summary>
public enum HotLoadKind
{
    /// <summary>The early-access library isn't next to ModDrop V: the player is offered to unlock it.</summary>
    Locked,
    /// <summary>Something stands in the way (the message says what).</summary>
    Blocked,
    /// <summary>The packs were sent; <see cref="HotLoadOutcome.Packs"/> says how each went.</summary>
    Done,
}

/// <summary>
/// How one pack went: loaded (in <see cref="Ms"/>), already in the game, queued — the game stands still (paused while
/// ModDrop V has the focus) and loads it once it moves again — or failed (<see cref="Error"/>).
/// </summary>
public sealed record HotLoadPack(string Pack, bool Loaded, bool Already, int Ms, string? Error, bool Queued = false);

public sealed record HotLoadOutcome(HotLoadKind Kind, string Message, IReadOnlyList<HotLoadPack> Packs)
{
    public bool AllLoaded => Kind == HotLoadKind.Done && Packs.All(p => p.Loaded || p.Already);

    /// <summary>Every pack is in the game or waits in its queue for the game to move again.</summary>
    public bool AllTaken => Kind == HotLoadKind.Done && Packs.All(p => p.Loaded || p.Already || p.Queued);
}

/// <summary>
/// Early access: an installed add-on pack goes into the game that runs right now, without a restart. The work is done
/// inside the game by <see cref="LibraryName"/> — a separate download next to ModDrop V for the author's supporters —
/// copied into the game folder as <see cref="PluginName"/> (an ASI on ScriptHookV, GTA V Legacy build 3889). It mounts
/// the pack the way the game mounts the packs of dlclist.xml at start; ModDrop V asks it over a named pipe.
/// Unloading isn't possible: switching a mod off or removing it still takes a restart.
/// </summary>
public static class HotLoad
{
    public const string LibraryName = "ModDropV.HotLoad.dll";
    public const string PluginName = "ModDropV.HotLoad.asi";
    public const string PipeName = "ModDropV.HotLoad";
    public static readonly string ShvLink = "http://www.dev-c.com/gtav/scripthookv/";

    /// <summary>The early-access library, when it is next to ModDrop V.</summary>
    public static string? Library(string appDir)
    {
        var path = Path.Combine(appDir, LibraryName);
        return File.Exists(path) ? path : null;
    }

    public static bool IsUnlocked(string appDir) => Library(appDir) is not null;

    /// <summary>The dlcpacks a mod's install listed in dlclist.xml — what loading it into the game means.</summary>
    public static List<string> PacksOf(RegisteredMod mod) =>
        [.. mod.Journal.OfType<DlclistAdded>().Select(s => s.Pack).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Load <paramref name="packs"/> (folders under mods\update\x64\dlcpacks) into the running game. Puts the plugin
    /// into the game folder when it isn't there (it starts with the game: the first time takes one restart).
    /// </summary>
    public static HotLoadOutcome Load(string appDir, string gameDir, GameEdition edition, IReadOnlyList<string> packs,
                                      TimeSpan? connectTimeout = null)
    {
        if (Library(appDir) is not { } library)
            return new(HotLoadKind.Locked, L.T("Loading mods into the running game is in early access for the author’s supporters."), []);
        if (edition != GameEdition.Legacy)
            return Blocked(L.T("For now mods load into a running GTA V Legacy only — GTA V Enhanced comes in a later update."));
        if (!File.Exists(Path.Combine(gameDir, "ScriptHookV.dll")))
            return Blocked(L.T($"Loading into the running game needs ScriptHookV in the game folder. Download it from {ShvLink} " +
                               $"and put ScriptHookV.dll next to GTA5.exe."));

        bool running = OnlineMode.RunningGame().Contains(GameEditions.LegacyExe, StringComparer.OrdinalIgnoreCase);
        switch (EnsurePlugin(library, gameDir))
        {
            case PluginState.Installed:
                return Blocked(running
                    ? L.T("Put the loader into the game folder. It starts with the game: restart GTA V once — from then on " +
                          "installed mods go into the running game straight away.")
                    : L.T("Put the loader into the game folder. Start GTA V, and once you are in the game, load the mod again."));
            case PluginState.Locked:
                return Blocked(L.T("The game runs an older loader, which can’t be replaced while it runs. Close GTA V, then load " +
                                   "the mod again."));
        }
        if (!running)
            return Blocked(L.T("GTA V isn’t running. Mods load into the running game — or start with it: the game loads " +
                               "everything installed when it starts anyway."));

        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect((int)(connectTimeout ?? TimeSpan.FromSeconds(2)).TotalMilliseconds);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            var hello = Ask(writer, reader, "hello");
            if (hello != "ok" && !hello.StartsWith("ok ", StringComparison.Ordinal))
                return Blocked(hello switch
                {
                    "fail starting" => L.T("The game is still starting. Load the mod again once you are in the game."),
                    "fail build" => L.T("This GTA V Legacy build isn’t supported yet — the loader knows build 3889. A new version " +
                                        "for the current build comes for the author’s supporters."),
                    _ => L.T($"The loader in the game answered: {hello}"),
                });
            var results = new List<HotLoadPack>();
            var room = LiveBudget.Current(gameDir);
            int used = room.Used;
            foreach (var pack in packs)
            {
                // what the game sized at start must hold it: a pack that might not fit waits for the next start
                int units = LiveBudget.CostOf(gameDir, pack).Units;
                if (used + units > room.Budget)
                {
                    results.Add(new(pack, false, false, 0, NoRoom(room, used, units)));
                    continue;
                }
                var result = Parse(pack, Ask(writer, reader, "load " + pack));
                if (result.Loaded || result.Queued)
                {
                    used += units;
                    LiveBudget.Record(gameDir, pack, units);
                }
                results.Add(result);
            }
            return new(HotLoadKind.Done, Summary(results), results);
        }
        catch (TimeoutException)
        {
            return Blocked(L.T("The loader doesn’t answer in the game. The game was started before the loader was put in, or " +
                               "ScriptHookV doesn’t run (after a game update it waits for its own update). Restart GTA V."));
        }
        catch (IOException ex)
        {
            return Blocked(L.T($"Lost the connection to the game: {ex.Message}"));
        }
    }

    private static HotLoadOutcome Blocked(string message) => new(HotLoadKind.Blocked, message, []);

    /// <summary>Why a pack isn't loaded into the running game: it might not fit what the game sized at start.</summary>
    private static string NoRoom(LiveBudget.State room, int used, int units)
    {
        int left = Math.Max(0, room.Budget - used);
        var why = room.Reserve
            ? L.T($"it counts as {units} usual mod(s), and the game has room for {left} more since it started")
            : L.T($"it counts as {units} usual mod(s), and the game has room for {left} more — it was started before ModDrop V made room for mods installed while it runs");
        return L.T($"{why}. The game could run out of memory, so it waits: restart GTA V and the game loads it at start");
    }

    private static string Ask(StreamWriter writer, StreamReader reader, string line)
    {
        writer.WriteLine(line);
        var read = reader.ReadLineAsync();
        if (!read.Wait(TimeSpan.FromSeconds(90)))
            throw new IOException(L.T("no answer in 90 seconds"));
        return read.Result ?? throw new IOException(L.T("the game closed the connection"));
    }

    /// <summary>A reply of the loader to <c>load &lt;pack&gt;</c> (see the protocol in its source).</summary>
    internal static HotLoadPack Parse(string pack, string reply)
    {
        if (reply == "already") return new(pack, false, true, 0, null);
        if (reply == "queued") return new(pack, false, false, 0, null, Queued: true);
        if (reply.StartsWith("ok", StringComparison.Ordinal))
        {
            int.TryParse(reply.AsSpan(2).Trim(), out int ms);
            return new(pack, true, false, ms, null);
        }
        string error = reply switch
        {
            "fail missing" => L.T("its dlc.rpf isn’t in mods\\update\\x64\\dlcpacks"),
            "fail setup" => L.T("the game couldn’t read its setup2.xml"),
            "fail duplicate" => L.T("a pack with the same name is already in the game"),
            "fail mount" => L.T("the game couldn’t open its dlc.rpf"),
            "fail crash" => L.T("the game failed while loading it (see ModDropV.HotLoad.log in the game folder)"),
            "fail busy" => L.T("the game didn’t get to it within a minute"),     // loader 1
            _ => reply,
        };
        return new(pack, false, false, 0, error);
    }

    private static string Summary(List<HotLoadPack> results)
    {
        var lines = new List<string>();
        foreach (var r in results)
            lines.Add(r.Loaded ? L.T($"{r.Pack}: loaded into the game ({r.Ms} ms).")
                : r.Already ? L.T($"{r.Pack}: already in the game.")
                : r.Queued ? L.T($"{r.Pack}: waits for the game — it stands still (paused while you are out of it) and loads the pack once you are back in it.")
                : L.T($"{r.Pack}: not loaded — {r.Error}."));
        if (results.Any(r => r.Loaded || r.Queued))
            lines.Add(L.T("It is in the game now: spawn it with your trainer or menu. Some things (shops, missions) only " +
                          "pick new items up after a restart."));
        return string.Join("\n", lines);
    }

    private enum PluginState { Current, Installed, Locked }

    /// <summary>The loader in the game folder, the same as the library (copied when missing or older).</summary>
    private static PluginState EnsurePlugin(string library, string gameDir)
    {
        var dst = Path.Combine(gameDir, PluginName);
        if (File.Exists(dst) && SameBytes(library, dst)) return PluginState.Current;
        try
        {
            File.Copy(library, dst, overwrite: true);
            return PluginState.Installed;
        }
        catch (IOException)
        {
            return PluginState.Locked;          // loaded by the running game
        }
    }

    private static bool SameBytes(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
