using System.Runtime.InteropServices;

namespace Mdv.App.Services;

/// <summary>
/// File log at <c>%LOCALAPPDATA%\ModDropV\logs\moddropv.log</c> — the primary
/// support artefact. If a user reports "nothing happens" and this file doesn't exist,
/// the exe never got to run its own code (AV, SmartScreen, partial unzip).
/// </summary>
public static class AppLog
{
    private static readonly Lock Gate = new();
    private static string? _file;

    public static string FilePath => _file ?? Path.Combine(AppPaths.LogDir, "moddropv.log");

    public static void Init()
    {
        try
        {
            var file = Path.Combine(AppPaths.LogDir, "moddropv.log");
            var fi = new FileInfo(file);
            if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
            _file = file;
        }
        catch
        {
            _file = null;
        }
        Info($"--- {AppPaths.AppName} starting ---");
        Info($"launched={Environment.ProcessPath}");
        Info($"os={RuntimeInformation.OSDescription} runtime={RuntimeInformation.FrameworkDescription}");
    }

    public static void Info(string msg) => Write("INFO   ", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR  ", ex is null ? msg : $"{msg}\n{ex}");

    private static void Write(string level, string msg)
    {
        if (_file is null) return;
        try
        {
            lock (Gate)
                File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {level} mdv: {msg}{Environment.NewLine}");
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
