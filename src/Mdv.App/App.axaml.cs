using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Mdv.App.Services;
using Mdv.App.ViewModels;
using Mdv.App.Views;

namespace Mdv.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel(Settings.Load());
            ApplyTheme(vm.IsDark);
            vm.ThemeChanged += ApplyTheme;
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(bool dark) =>
        RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
}
