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
            desktop.MainWindow = CreateWindow(desktop, settings, null);
        }
        base.OnFrameworkInitializationCompleted();
    }

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
