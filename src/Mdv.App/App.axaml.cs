using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Mdv.App.Services;
using Mdv.App.ViewModels;
using Mdv.App.Views;
using Mdv.Core;

namespace Mdv.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Settings.Load();
            L.Folder = Path.Combine(AppPaths.Data, "lang");
            L.Use(settings.Language.Length > 0 ? settings.Language : L.SystemLanguage());
            int from = Array.IndexOf(desktop.Args ?? [], "--updated-from");
            _updatedFrom = from >= 0 && from + 1 < desktop.Args!.Length ? AppUpdate.ParseVersion(desktop.Args[from + 1]) : null;
            if (_updatedFrom is not null) AppLog.Info($"updated from {_updatedFrom} to {AppUpdate.Current}");
            desktop.MainWindow = CreateWindow(desktop, settings, null);
            desktop.Exit += (_, _) => MainViewModel.CleanWorkspaces();     // an unpacked drop can be gigabytes
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The version this start was updated from (shown once: a language switch's new window doesn't repeat it).</summary>
    private Version? _updatedFrom;

    /// <summary>
    /// The main window with a fresh view model. A language switch builds a new one in place of
    /// <paramref name="previous"/> (same size and position) — every text is made again in the new language.
    /// </summary>
    private MainWindow CreateWindow(IClassicDesktopStyleApplicationLifetime desktop, Settings settings, Window? previous)
    {
        var vm = new MainViewModel(settings);
        ApplyTheme(vm.IsDark);
        vm.ThemeChanged += ApplyTheme;
        GameIndexWarmup.Attach(vm);
        var window = new MainWindow { DataContext = vm };
        var watch = vm.StartGameWatch();                 // installs into the running game, and what they leave for later
        window.Closed += (_, _) => watch.Dispose();
        _ = vm.StartUpdatesAsync(_updatedFrom);
        _updatedFrom = null;
        vm.RestartRequested += () =>
        {
            // the new build is in place: start it and leave (it removes this build's renamed files once we're gone)
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(AppPaths.Root, AppUpdate.ExeName))
                {
                    ArgumentList = { "--updated-from", AppUpdate.Current.ToString(3) },
                    WorkingDirectory = AppPaths.Root,
                });
            }
            catch (Exception ex)
            {
                AppLog.Error("starting the updated ModDrop V failed", ex);
            }
            desktop.Shutdown();
        };
        if (previous is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = previous.Position;
            window.Width = previous.Width;
            window.Height = previous.Height;
            window.WindowState = previous.WindowState;
        }
        vm.LanguageChanged += code =>
        {
            L.Use(code);
            var next = CreateWindow(desktop, settings, window);
            desktop.MainWindow = next;
            next.Show();
            window.Close();
        };
        return window;
    }

    private void ApplyTheme(bool dark) =>
        RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
}
