using Mdv.Core;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mdv.App.ViewModels;

namespace Mdv.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var v = typeof(MainWindow).Assembly.GetName().Version;
        if (v != null) Title = $"ModDrop V {v.ToString(3)}";
        // Player mode takes a drop anywhere in the window; the source card shows it (and the install page opens).
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZone.Classes.Set("dragover", false));
        AddHandler(DragDrop.DropEvent, OnDrop);
        // dialogs close on Esc or a click on the dimmed backdrop
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        GameScrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, GameScrim)) Vm?.CloseGameDialogCommand.Execute(null);
        };
        LogScrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, LogScrim)) Vm?.CloseLogCommand.Execute(null);
        };
        PreviewScrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, PreviewScrim)) Vm?.Weapon.ClosePreviewCommand.Execute(null);
        };
        PlanScrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, PlanScrim)) Vm?.ClosePlanCommand.Execute(null);
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Vm is not { } vm) return;
        if (vm.IsLogOpen) vm.IsLogOpen = false;
        else if (vm.Weapon.IsPreviewOpen) vm.Weapon.IsPreviewOpen = false;
        else if (vm.IsPlanOpen) vm.ClosePlanCommand.Execute(null);
        else if (vm.IsGameDialogOpen) vm.IsGameDialogOpen = false;
        else return;
        e.Handled = true;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private bool AcceptsDrop(DragEventArgs e) =>
        Vm is { IsPlayer: true, IsBuilding: false } && e.DataTransfer.Contains(DataFormat.File);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool ok = AcceptsDrop(e);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Classes.Set("dragover", ok);
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Set("dragover", false);
        if (!AcceptsDrop(e) || Vm is not { } vm) return;
        e.Handled = true;
        var paths = (e.DataTransfer.TryGetFiles() ?? [])
                    .Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0) return;
        vm.IsLibraryPage = false;
        await vm.DropSourceAsync(paths);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainViewModel vm) return;
        vm.PickFolder = PickFolderAsync;
        vm.PickArchives = PickArchivesAsync;
        vm.CopyToClipboard = async text =>
        {
            if (Clipboard is { } cb) await cb.SetTextAsync(text);
        };
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.LogText) or nameof(MainViewModel.IsLogOpen))
            Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    private async Task<IReadOnlyList<string>> PickArchivesAsync(string title)
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(L.T("Mod archives")) { Patterns = ["*.zip", "*.rar", "*.7z", "*.oiv", "*.rpf"] },
                new FilePickerFileType(L.T("All files")) { Patterns = ["*"] },
            ],
        });
        return result.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }
}
