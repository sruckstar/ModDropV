using System.Text;

namespace Mdv.Core;

/// <summary>
/// BattlEye, GTA V's anti-cheat (Legacy and Enhanced): it keeps ASI loaders and script hooks out of the game, so story mode
/// with mods needs it off. The switch is <c>-nobattleye</c> in <c>&lt;game&gt;\commandline.txt</c> — the game reads it
/// whichever store starts it (Steam, Epic, Rockstar Games Launcher). The first install of a mod puts it there, "Play GTA
/// Online" takes it out (GTA Online needs BattlEye), "Bring mods back" puts it there again. The rest of the file is kept.
/// <para>
/// BattlEye can also be switched off in the Rockstar Games Launcher's settings or in Steam's launch options — ModDrop V
/// doesn't see those.
/// </para>
/// </summary>
public static class BattlEye
{
    public static readonly string Switch = "-nobattleye";
    public static readonly string FileName = "commandline.txt";

    private static string PathOf(string gameDir) => Path.Combine(gameDir, FileName);

    /// <summary>A real game install (a folder with the game's executable) — a bare mods tree gets no commandline.txt.</summary>
    public static bool IsGame(string gameDir) => GameEditions.Detect(gameDir) is not null || GameEditions.IsAmbiguous(gameDir);

    /// <summary>commandline.txt has <c>-nobattleye</c>: the game starts without the anti-cheat.</summary>
    public static bool IsOff(string gameDir)
    {
        try
        {
            var path = PathOf(gameDir);
            return File.Exists(path) && Tokens(File.ReadAllText(path)).Any(IsSwitch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Put <c>-nobattleye</c> into commandline.txt (mods can load). Not journaled: like the mods folder, it stays.</summary>
    /// <returns>the file was changed</returns>
    public static bool TurnOff(string gameDir, Action<string> log)
    {
        if (!IsGame(gameDir) || IsOff(gameDir)) return false;
        var path = PathOf(gameDir);
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var sep = text.Length == 0 || char.IsWhiteSpace(text[^1]) ? "" : Environment.NewLine;
        File.WriteAllText(path, text + sep + Switch + Environment.NewLine, new UTF8Encoding(false));
        log(L.T($"    Switched BattlEye off for story mode with mods ({Switch} in {FileName}) — “Play GTA Online” switches it back on."));
        return true;
    }

    /// <summary>Take <c>-nobattleye</c> out of commandline.txt (GTA Online needs BattlEye); a file left empty goes.</summary>
    /// <returns>the file was changed</returns>
    public static bool TurnOn(string gameDir, Action<string> log)
    {
        if (!IsOff(gameDir)) return false;
        var path = PathOf(gameDir);
        var lines = File.ReadAllLines(path)
            .Select(l => Tokens(l).Any(IsSwitch) ? string.Join(' ', Tokens(l).Where(t => !IsSwitch(t))) : l)
            .Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) File.Delete(path);
        else File.WriteAllLines(path, lines, new UTF8Encoding(false));
        log(L.T($"    Switched BattlEye back on ({Switch} taken out of {FileName}) — GTA Online needs it."));
        return true;
    }

    private static IEnumerable<string> Tokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsSwitch(string token) => token.Equals(Switch, StringComparison.OrdinalIgnoreCase);
}
