using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Mdv.Core.Mods;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core;

/// <summary>What a pack takes: its archives and model / texture files, and how many usual add-ons that makes.</summary>
public sealed record PackCost(int Archives, int Fragments, int Drawables, int Txds, int Dwds)
{
    /// <summary>
    /// Usual add-ons it counts as: by whatever it has most of against one (<see cref="GamePools.PerModPools"/>), at least one.
    /// </summary>
    public int Units => Math.Max(1, new[]
    {
        Ceil(Archives, GamePools.PerModSettings["ArchiveCount"]), Ceil(Fragments, GamePools.PerModPools["FragmentStore"]),
        Ceil(Drawables, GamePools.PerModPools["DrawableStore"]), Ceil(Txds, GamePools.PerModPools["TxdStore"]),
        Ceil(Dwds, GamePools.PerModPools["DwdStore"]),
    }.Max());

    private static int Ceil(int n, int per) => (n + per - 1) / per;
}

/// <summary>
/// Room in the running game for packs loaded into it (<see cref="HotLoad"/>). The game sizes its pools and heap when it
/// starts, from gameconfig.xml and Heap Adjuster: a pack loaded later takes from what is free then, and nothing grows
/// until the next start. With the live reserve in place (<see cref="GamePools.LiveReserveMods"/> usual add-ons over the
/// limits for mods — ModDrop V puts it in whenever the early-access loader is next to it) there is room for that many,
/// counted by <see cref="PackCost.Units"/>; without it, for <see cref="BaseSlack"/>. What went in since the game started
/// is kept in <see cref="SessionFile"/>; a pack that might not fit any more isn't loaded — it waits for the next start.
/// </summary>
public static class LiveBudget
{
    /// <summary>In the game folder: the game's start, its room and the packs loaded into it since.</summary>
    public const string SessionFile = "ModDropV.HotLoad.session";

    /// <summary>The usual add-ons the game's limits leave room for without the reserve.</summary>
    public const int BaseSlack = 2;

    /// <summary>When the game process started (tests stand in for it); null when it can't be told.</summary>
    internal static Func<DateTime?> GameStarted = StartedNow;

    /// <summary>The room of this game run and what is used of it.</summary>
    public sealed record State(int Budget, int Used, bool Reserve, int HeapMb)
    {
        public int Left => Math.Max(0, Budget - Used);
    }

    /// <summary>The room of the running game, worked out once per game run.</summary>
    public static State Current(string gameDir)
    {
        var started = GameStarted();
        var path = Path.Combine(gameDir, SessionFile);
        var key = started?.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        if (lines.Any(l => l == "session " + key))
        {
            int budget = Int(lines, "budget"), heap = Int(lines, "heap");
            bool reserve = lines.Contains("reserve");
            int used = lines.Where(l => l.StartsWith("pack ", StringComparison.Ordinal))
                            .Sum(l => int.TryParse(l.Split(' ')[^1], out var u) ? u : 0);
            return new State(budget, used, reserve, heap);
        }
        var fresh = Measure(gameDir, started);
        var text = new List<string>
        {
            "# ModDrop V: the room of the running game for mods loaded into it, and what they took",
            "session " + key, "budget " + fresh.Budget.ToString(CultureInfo.InvariantCulture),
            "heap " + fresh.HeapMb.ToString(CultureInfo.InvariantCulture),
        };
        if (fresh.Reserve) text.Add("reserve");
        File.WriteAllLines(path, text);
        return fresh;
    }

    /// <summary>A pack went into the running game.</summary>
    public static void Record(string gameDir, string pack, int units) =>
        File.AppendAllLines(Path.Combine(gameDir, SessionFile), [$"pack {pack} {units.ToString(CultureInfo.InvariantCulture)}"]);

    /// <summary>
    /// What the game had at start: the live reserve in the gameconfig.xml it read (the mods folder's copy can't change
    /// while it runs), and the heap Heap Adjuster gave it — its log is written at start, with the size it set.
    /// </summary>
    private static State Measure(string gameDir, DateTime? started)
    {
        bool reserve = false;
        try
        {
            if (ModsOverlay.Load(gameDir).Read(GamePools.GameConfig) is { } data)
                reserve = GamePools.HasLiveReserve(TextIo.DecodeUtf8Sig(data, strict: false));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or RpfFormatException or ArgumentException)
        {
        }
        int heap = HeapThisRun(gameDir, started);
        int heapUnits = heap == 0 ? BaseSlack : Math.Max(BaseSlack, (heap - LimitAdjusters.HeapMb) / GamePools.PerModHeapMb);
        int pools = reserve ? GamePools.LiveReserveMods : BaseSlack;
        return new State(Math.Min(pools, heapUnits), 0, reserve, heap);
    }

    /// <summary>The heap Heap Adjuster set when this game run started (MB); 0 when it didn't run.</summary>
    internal static int HeapThisRun(string gameDir, DateTime? started)
    {
        var log = Path.Combine(gameDir, "HeapAdjuster.log");
        if (!File.Exists(log) || started is not { } at || File.GetLastWriteTime(log) < at.AddSeconds(-30)) return 0;
        try
        {
            var m = Regex.Matches(File.ReadAllText(log), @"Adjusted to:\s*(\d+)");
            return m.Count > 0 && long.TryParse(m[^1].Groups[1].Value, out var mb) ? LimitAdjusters.EffectiveHeapMb(mb) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>What a pack in mods\update\x64\dlcpacks takes (its dlc.rpf and the sub-packs next to it).</summary>
    public static PackCost CostOf(string gameDir, string pack)
    {
        var dir = GameInstaller.PackDir(gameDir, pack);
        int archives = 0, yft = 0, ydr = 0, ytd = 0, ydd = 0;
        if (Directory.Exists(dir))
            foreach (var rpf in Directory.EnumerateFiles(dir, "dlc*.rpf"))
            {
                try
                {
                    using var arc = RpfArchive.Open(rpf);
                    Walk(arc, 0);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or RpfFormatException or UnauthorizedAccessException)
                {
                    archives++;
                }
            }
        return new PackCost(archives, yft, ydr, ytd, ydd);

        void Walk(RpfArchive a, int depth)
        {
            archives++;
            foreach (var e in a.Files())
            {
                switch (PathUtil.SuffixLower(e.Name))
                {
                    case ".rpf" when e.StoredRaw && depth < 3:
                        using (var nested = a.OpenNested(e)) Walk(nested, depth + 1);
                        break;
                    case ".yft" when !e.Name.EndsWith("_hi.yft", StringComparison.OrdinalIgnoreCase): yft++; break;
                    case ".ydr": ydr++; break;
                    case ".ytd": ytd++; break;
                    case ".ydd": ydd++; break;
                }
            }
        }
    }

    private static int Int(IEnumerable<string> lines, string key) =>
        lines.Select(l => l.Split(' ')).FirstOrDefault(p => p.Length == 2 && p[0] == key) is { } p && int.TryParse(p[1], out var n) ? n : 0;

    private static DateTime? StartedNow()
    {
        DateTime? newest = null;
        try
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameEditions.LegacyExe)))
                using (p)
                {
                    try
                    {
                        if (!p.HasExited && (newest is null || p.StartTime > newest)) newest = p.StartTime;
                    }
                    catch (Exception)
                    {
                        // a process that isn't ours to look at
                    }
                }
        }
        catch (Exception)
        {
        }
        return newest;
    }
}
