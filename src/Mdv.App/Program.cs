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
