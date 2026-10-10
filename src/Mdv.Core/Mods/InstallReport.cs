using System.Globalization;
using System.Text;

namespace Mdv.Core.Mods;

/// <summary>
/// A text report of every plan run (an install, a removal, a switch, a new load order…):
/// <c>%LOCALAPPDATA%\ModDropV\logs\installs\&lt;time&gt;-&lt;mod&gt;.txt</c> — the plan's steps and warnings, what changed in the
/// game file by file (whose version a replaced file was), the dlclist.xml, .ini and archive changes, and the full log with
/// what was skipped and why. For the player who asks "why don't the cops show up" — and for the modder they send it to.
/// </summary>
public static class InstallReport
{
    /// <summary>Where the reports go (tests point it elsewhere).</summary>
    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModDropV", "logs", "installs");

    /// <summary>Older reports beyond this many go.</summary>
    public const int Keep = 300;

    /// <summary>A run's exception carries its report's path under this key (<see cref="Exception.Data"/>).</summary>
    public const string DataKey = "mdv.report";

    /// <summary>The report written last and when (the app's result banner offers the one its run wrote).</summary>
    public static (string Path, DateTime When)? Last { get; private set; }

    /// <summary>The report of a failed run, if one was written.</summary>
    public static string? Of(Exception ex) => ex.Data[DataKey] as string;

    /// <summary>
    /// Write the report of a run; null when it couldn't be written (a report never stops a plan).
    /// </summary>
    /// <param name="lines">the run's log</param>
    /// <param name="before">the registry as it was before the run (whose version a replaced file was)</param>
    /// <param name="error">why it failed (it was taken back), or null when it went through</param>
    internal static string? Write(InstallPlan plan, InstallContext ctx, IReadOnlyList<string> lines, DateTime started,
                                  ModRegistry? before, Exception? error)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var name = ctx.Registered.FirstOrDefault()?.Name
                       ?? before?.Find(ctx.Unregistered.FirstOrDefault() ?? "")?.Name
                       ?? plan.Title;
            var path = FreePath(Path.Combine(Root, started.ToLocalTime().ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) + "-" + Slug(name)));
            File.WriteAllText(path, Text(plan, ctx, lines, started, before, error), new UTF8Encoding(true));
            Prune();
            Last = (path, DateTime.UtcNow);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ctx.Log(L.T($"    [!] The install report could not be written: {ex.Message}"));
            return null;
        }
    }

    internal static string Text(InstallPlan plan, InstallContext ctx, IReadOnlyList<string> lines, DateTime started,
                                ModRegistry? before, Exception? error)
    {
        var sb = new StringBuilder();
        string Name(string id) => ctx.Registered.FirstOrDefault(m => m.Id == id)?.Name ?? before?.Find(id)?.Name ?? id;
        sb.AppendLine(L.T($"ModDrop V {AppUpdate.Current} — install report"));
        sb.AppendLine(new string('=', 60));
        sb.AppendLine(L.T($"Plan:     {plan.Title}"));
        sb.AppendLine(L.T($"Game:     {ctx.GameDir} ({ctx.Target.Edition.DisplayName()})"));
        var when = started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var took = (DateTime.UtcNow - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        sb.AppendLine(L.T($"Started:  {when}, took {took} s"));
        sb.AppendLine(error switch
        {
            null => L.T("Result:   done"),
            OperationCanceledException => L.T("Result:   cancelled — everything it did was taken back"),
            _ => L.T($"Result:   FAILED — everything it did was taken back: {error.Message}"),
        });
        foreach (var m in ctx.Registered)
            sb.AppendLine(L.T($"Installed: {m.Name} ({m.Id})") + (m.Source is { } src ? L.T($" from {src.Path ?? src.Name}") : ""));
        foreach (var id in ctx.Unregistered) sb.AppendLine(L.T($"Removed:   {Name(id)} ({id})"));
        foreach (var (id, on) in ctx.Switched) sb.AppendLine(on ? L.T($"Switched on:  {Name(id)}") : L.T($"Switched off: {Name(id)}"));

        sb.AppendLine();
        sb.AppendLine(L.T("Plan steps"));
        int n = 0;
        foreach (var step in plan.Describe()) sb.AppendLine($"  {++n,3}. {step}");
        if (plan.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(L.T("Warnings shown with the plan"));
            foreach (var w in plan.Warnings.Distinct()) sb.AppendLine("  - " + w);
        }

        sb.AppendLine();
        sb.AppendLine(error is null ? L.T("What changed in the game") : L.T("What it changed before it was taken back"));
        var changes = Changes(ctx, before);
        if (changes.Count == 0) sb.AppendLine(L.T("  nothing in the game folder"));
        foreach (var c in changes.Take(5000)) sb.AppendLine("  " + c);
        if (changes.Count > 5000) sb.AppendLine(L.T($"  …and {changes.Count - 5000} more"));

        sb.AppendLine();
        sb.AppendLine(L.T("Log"));
        foreach (var line in lines) sb.AppendLine("  " + line);
        if (error is not null and not OperationCanceledException)
        {
            sb.AppendLine();
            sb.AppendLine(error.ToString());
        }
        return sb.ToString();
    }

    /// <summary>The transaction's steps in words, one per line; ModDrop V's own housekeeping (stashes) left out.</summary>
    private static List<string> Changes(InstallContext ctx, ModRegistry? before)
    {
        var home = GameFiles.KeyOf(ctx.Journal.HomeDir) + "/";
        bool Ours(string path) => GameFiles.KeyOf(path).StartsWith(home, StringComparison.Ordinal);
        // whose version a file of the game folder was: the top of its chain, else the one mod that has it
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        if (before is not null)
        {
            foreach (var m in before.Mods)
                foreach (var s in m.Journal)
                    if (s is CreatedFile or MovedAside { Keep: true })
                        owners.TryAdd(GameFiles.KeyOf(s is CreatedFile f ? f.Path : ((MovedAside)s).Path), m.Name);
            foreach (var (key, chain) in GameFiles.Chains(before.Mods.Select(m => KeyValuePair.Create(m.Id, m.Journal))))
                owners[key] = before.Find(chain.Links[^1].Mod)?.Name ?? chain.Links[^1].Mod;
        }
        string Whose(string path) => owners.TryGetValue(GameFiles.KeyOf(path), out var who)
            ? L.T($"«{who}»'s version")
            : L.T("the file that was there");

        var list = new List<string>();
        foreach (var step in ctx.Journal.Steps)
            switch (step)
            {
                case CreatedFile f when !Ours(f.Path):
                    list.Add(L.T($"+ new file   {f.Path}"));
                    break;
                case CreatedDir d when !Ours(d.Path):
                    list.Add(L.T($"+ new folder {d.Path}/"));
                    break;
                case MovedAside { Edit: true } m when !Ours(m.Path):
                    list.Add(L.T($"~ edited     {m.Path}") + (m.Keep ? L.T(" (the file before kept for a removal)") : ""));
                    break;
                case MovedAside { Keep: true } m when !Ours(m.Path):
                    list.Add(L.T($"~ replaced   {m.Path} — {Whose(m.Path)} kept for a removal"));
                    break;
                case MovedAside m when !Ours(m.Path):
                    list.Add(L.T($"- took away  {m.Path}"));
                    break;
                case Moved mv when Ours(mv.From) && !Ours(mv.To):
                    list.Add(L.T($"↺ put back   {mv.To}"));
                    break;
                case Moved mv when !Ours(mv.From) && !Ours(mv.To):
                    list.Add(L.T($"→ moved      {mv.From} → {mv.To}"));
                    break;
                case DlclistAdded a:
                    list.Add(L.T($"+ dlclist    dlcpacks:/{a.Pack}/"));
                    break;
                case DlclistRemoved r:
                    list.Add(L.T($"- dlclist    dlcpacks:/{r.Pack}/"));
                    break;
                case IniKeySet k:
                    list.Add(L.T($"~ setting    {k.Path} [{k.Section}] {k.Key}: {k.Old?.ToString(CultureInfo.InvariantCulture) ?? L.T("not set")} → {k.New}"));
                    break;
                case RpfEntrySet e:
                    list.Add(L.T($"~ archive    {e.Archive}/{e.Inner} — was {PriorText(e.Prior)}"));
                    break;
            }
        return list;
    }

    private static string PriorText(string prior) => prior switch
    {
        ModsOverlay.Game => L.T("the game's own file"),
        ModsOverlay.Absent => L.T("not there"),
        _ => L.T("another version (kept)"),
    };

    private static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (sb.Length >= 48) break;
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }
        var s = sb.ToString().Trim('_', '.');
        return s.Length == 0 ? "plan" : s;
    }

    private static string FreePath(string stem)
    {
        var path = stem + ".txt";
        for (int i = 2; File.Exists(path); i++) path = $"{stem}-{i}.txt";
        return path;
    }

    private static void Prune()
    {
        var old = new DirectoryInfo(Root).GetFiles("*.txt").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Keep);
        foreach (var f in old)
            try { f.Delete(); } catch (IOException) { /* next time */ }
    }
}
