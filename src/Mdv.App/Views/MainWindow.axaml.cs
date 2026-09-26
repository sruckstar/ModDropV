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
        // Player mode takes a drop anywhere in the window; the source card shows it.
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZone.Classes.Set("dragover", false));
        AddHandler(DragDrop.DropEvent, OnDrop);
        SidePanel.SizeChanged += (_, _) => LayoutSidePanel();
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
            if (ReferenceEquals(e.Source, PreviewScrim)) Vm?.ClosePreviewCommand.Execute(null);
        };
    }

    /// <summary>
    /// Modders: the analysis fills the side panel. Players: it takes what it needs, up to a bit
    /// over half the panel so the installed list keeps room; past that it scrolls inside.
    /// </summary>
    private void LayoutSidePanel()
    {
        bool player = Vm?.IsPlayer == true;
        double h = SidePanel.Bounds.Height;
        SidePanel.RowDefinitions[0].Height = player ? GridLength.Auto : GridLength.Star;
        // the hidden installed list would still claim its star share
        SidePanel.RowDefinitions[1].Height = player ? GridLength.Star : new GridLength(0);
        AnalysisCard.MaxHeight = player
            ? Math.Max(0, Math.Min(h * 0.56, h - SidePanel.RowSpacing - InstalledCard.MinHeight))
            : double.PositiveInfinity;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Vm is not { } vm) return;
        if (vm.IsLogOpen) vm.IsLogOpen = false;
        else if (vm.IsPreviewOpen) vm.IsPreviewOpen = false;
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
        if (paths.Count > 0) await vm.DropSourceAsync(paths);
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
        LayoutSidePanel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.LogText) or nameof(MainViewModel.IsLogOpen))
            Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
        else if (e.PropertyName == nameof(MainViewModel.IsPlayer))
            LayoutSidePanel();
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
                new FilePickerFileType("Weapon archives") { Patterns = ["*.zip", "*.rar", "*.7z", "*.oiv", "*.rpf"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        return result.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }
}
