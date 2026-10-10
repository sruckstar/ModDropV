using Mdv.Core;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        // the load order: a row is dragged by its handle, Alt+↑ / Alt+↓ move the row a button of it has the focus in
        OrderList.AddHandler(PointerPressedEvent, OnOrderPressed, RoutingStrategies.Tunnel);
        OrderList.PointerMoved += OnOrderMoved;
        OrderList.PointerReleased += (_, e) => EndOrderDrag(e.Pointer);
        OrderList.PointerCaptureLost += (_, _) => EndOrderDrag(null);
        OrderList.AddHandler(KeyDownEvent, OnOrderKey, RoutingStrategies.Tunnel);
    }

    private LoadOrderRow? _dragged;

    private void OnOrderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { IsBuilding: false } || !e.GetCurrentPoint(OrderList).Properties.IsLeftButtonPressed) return;
        var grip = (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("grip"));
        if (grip?.DataContext is not LoadOrderRow row) return;
        _dragged = row;
        row.Dragging = true;
        e.Pointer.Capture(OrderList);
        e.Handled = true;
    }

    private void OnOrderMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged is not { } row || Vm is not { } vm) return;
        // near the edges of the visible part the list scrolls along
        double y = e.GetPosition(OrderScroll).Y;
        if (y < 24) OrderScroll.Offset = OrderScroll.Offset.WithY(Math.Max(0, OrderScroll.Offset.Y - 12));
        else if (y > OrderScroll.Bounds.Height - 24) OrderScroll.Offset = OrderScroll.Offset.WithY(OrderScroll.Offset.Y + 12);

        var at = e.GetPosition(OrderList).Y;
        int target = -1;
        for (int i = 0; i < vm.OrderView.Count; i++)
        {
            if (OrderList.ContainerFromIndex(i) is not { } c || c.TranslatePoint(new Point(0, 0), OrderList) is not { } top) continue;
            target = i;
            if (at < top.Y + c.Bounds.Height) break;
        }
        if (target >= 0) vm.MoveOrderRow(row, target);
    }

    private void EndOrderDrag(IPointer? pointer)
    {
        if (_dragged is not { } row) return;
        _dragged = null;
        row.Dragging = false;
        pointer?.Capture(null);
    }

    private void OnOrderKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down) || Vm is not { IsBuilding: false } vm) return;
        if ((e.Source as Control)?.DataContext is not LoadOrderRow row) return;
        bool up = e.Key == Key.Up;
        vm.MoveOrderRow(row, vm.OrderView.IndexOf(row) + (up ? -1 : 1));
        e.Handled = true;
        // the row's container may be a new one now: the focus follows the row
        Dispatcher.UIThread.Post(() =>
        {
            if (OrderList.ContainerFromIndex(vm.OrderView.IndexOf(row)) is not { } c) return;
            var button = c.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Command == (up ? row.UpCommand : row.DownCommand));
            button?.Focus(NavigationMethod.Directional);
            c.BringIntoView();
        }, DispatcherPriority.Background);
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
