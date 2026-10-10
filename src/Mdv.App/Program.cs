using Mdv.Core;
using Avalonia;
using Mdv.App.Services;

namespace Mdv.App;

internal static class Program
{
    /// <summary>
    /// Every failure path ends in a log file plus a message box: a GUI exe that dies before
    /// its window exists otherwise just "does nothing" on the user's machine.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        AppLog.Init();

        if (args.Contains("--diagnose"))
        {
            Diagnostics.WriteAndOpenReport();
            return 0;
        }

        // the self-updater asked for admin rights to put a downloaded build into a folder the user can't write to
        int apply = Array.IndexOf(args, "--apply-update");
        if (apply >= 0 && apply + 1 < args.Length)
        {
            var result = AppUpdate.Apply(args[apply + 1], AppPaths.Root, AppLog.Info);
            if (result.Kind != ApplyKind.Done) AppLog.Info($"update not applied: {result.Message}");
            return result.Kind == ApplyKind.Done ? 0 : 1;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Diagnostics.Fatal(L.T("ModDrop V stopped because of an unexpected error."),
                                  L.T("Please send the log file to the developer."), ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };

        if (!Diagnostics.Preflight()) return 1;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Diagnostics.Fatal(L.T("ModDrop V could not start."),
                              L.T("Please send the log file to the developer."), ex);
            return 1;
        }
    }

    /// <summary>Avalonia configuration; also used by the visual designer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .LogToTrace();
}
