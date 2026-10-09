using Mdv.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mdv.App.Services;

/// <summary>Startup checks, crash reporting and the <c>--diagnose</c> report.</summary>
public static class Diagnostics
{
    private const uint MbOk = 0x0, MbIconError = 0x10, MbSetForeground = 0x10000, MbTopmost = 0x40000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>A native message box: it has to work exactly when the UI stack is what failed.</summary>
    public static void MessageBox(string text, string caption = AppPaths.AppName)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                MessageBoxW(IntPtr.Zero, text, caption, MbOk | MbIconError | MbSetForeground | MbTopmost);
                return;
            }
        }
        catch
        {
            // fall through to stderr
        }
        Console.Error.WriteLine($"{caption}: {text}");
    }

    /// <summary>Log a fatal failure and put it on screen. Never throws.</summary>
    public static void Fatal(string summary, string detail = "", Exception? ex = null)
    {
        AppLog.Error(string.IsNullOrEmpty(detail) ? summary : $"{summary} | {detail}", ex);
        var body = new List<string> { summary };
        if (!string.IsNullOrEmpty(detail)) body.Add(detail);
        if (ex is not null) body.Add($"{ex.GetType().Name}: {ex.Message}");
        body.Add(L.T($"Details were written to:\n{AppLog.FilePath}"));
        MessageBox(string.Join("\n\n", body));
    }

    /// <param name="withGame">add how the last chosen game stands for mods (the --diagnose report; not at startup)</param>
    public static string Report(bool withGame = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppPaths.AppName} diagnostics");
        sb.AppendLine($"Version      : {typeof(Diagnostics).Assembly.GetName().Version}");
        sb.AppendLine($"OS           : {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Machine      : {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"Runtime      : {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Launched     : {Environment.ProcessPath}");
        sb.AppendLine(L.T($"App folder   : {AppPaths.Root}"));
        sb.AppendLine(L.T($"Data folder  : {AppPaths.AppData}"));
        foreach (var rel in new[] { "data/templates/_index.json", "data/weapons.meta", "data/weaponcomponents.meta",
                                    "data/weaponarchetypes.meta", "data/weaponanimations.meta",
                                    "data/plugins/dinput8.dll", "data/plugins/xinput1_4.dll",
                                    "data/plugins/limits/HeapAdjuster.asi",
                                    "data/plugins/limits/PackfileLimitAdjusterEnhanced.asi",
                                    "data/plugins/limits/WeaponLimitsAdjusterEnhanced.asi",
                                    "data/plugins/limits/WeaponLimitsAdjuster.asi",
                                    "data/plugins/limits/PoolHeapAdjusterEnhanced.asi",
                                    "ShadersGen9Conversion.xml" })
        {
            var ok = File.Exists(Path.Combine(AppPaths.Root, rel));
            sb.AppendLine($"{rel,-28}: {(ok ? "ok" : "MISSING")}");
        }
        if (withGame) AppendGame(sb);
        return sb.ToString();
    }

    private static void AppendGame(StringBuilder sb)
    {
        sb.AppendLine();
        try
        {
            var settings = Settings.Load();
            sb.AppendLine($"Language     : {(settings.Language is { Length: > 0 } lang ? lang : $"{L.SystemLanguage()} (Windows)")}");
            if (settings.LastGame is not { Length: > 0 } game)
            {
                sb.AppendLine("Game         : (not chosen yet)");
                return;
            }
            sb.AppendLine($"Game         : {game}");
            var status = GameStatus.Read(game, GameEditions.Detect(game), null);
            foreach (var i in status.Items)
            {
                var mark = i.Level switch { StatusLevel.Ok => "ok ", StatusLevel.Warning => "[!]", _ => " - " };
                sb.AppendLine($"  {mark} {i.Label,-18} {i.Value}");
                if (i.Level == StatusLevel.Warning && i.Detail is { } d)
                    foreach (var line in d.Split('\n')) sb.AppendLine($"      {line}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Game         : could not read ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary><c>ModDropV.exe --diagnose</c>: write the host report and open it.</summary>
    public static void WriteAndOpenReport()
    {
        var report = Report(withGame: true);
        var path = Path.Combine(AppPaths.LogDir, "diagnose.txt");
        try
        {
            File.WriteAllText(path, report);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            MessageBox(report);
        }
    }

    /// <summary>
    /// The bundle must be intact: antivirus quarantine likes to eat single files out of
    /// the folder, and users unzip only the .exe surprisingly often. False = the user
    /// was already told why and the caller must exit.
    /// </summary>
    public static bool Preflight()
    {
        AppLog.Info("diagnostics:\n" + Report());
        var templates = Path.Combine(AppPaths.Templates, "_index.json");
        var metas = Path.Combine(AppPaths.Data, "weapons.meta");
        if (File.Exists(templates) || File.Exists(metas)) return true;
        Fatal(L.T("Some of the program's files are missing, so it cannot start."),
              L.T($"This usually means the archive was only partly unpacked, or an antivirus removed " +
              $"files from the folder.\n\nUnpack the whole archive to a normal folder (for example " +
              $"C:\\ModDropV) and add that folder to your antivirus exclusions.\n\n" +
              $"Not found:\n{AppPaths.Templates}"));
        return false;
    }

    /// <summary>Reveal a path in the file manager.</summary>
    public static void OpenInFileManager(string path)
    {
        var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", target);
        else
            Process.Start("xdg-open", target);
    }
}
