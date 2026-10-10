using System.Collections.ObjectModel;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// Player flow: the library's load order — every mod in one list, the higher one wins where mods change the same game
/// files (and, when asked, their add-on packs follow it in dlclist.xml). Moves are marked first and applied together
/// as one plan.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The game's order as read: mods top first, the mods each shares files with, whether packs follow it.</summary>
    internal sealed record OrderData(List<RegisteredMod> Mods, Dictionary<string, List<string>> Others, bool Packs);

    private List<LoadOrderRow> _order = [];
    private List<string> _savedOrder = [];
    private bool _savedPacks;
    private Dictionary<string, string> _orderNames = [];

    /// <summary>The library shows the load order instead of the mods by category.</summary>
    [ObservableProperty] public partial bool IsOrderView { get; set; }
    public bool IsModsView
    {
        get => !IsOrderView && !IsConflictsView;
        set
        {
            if (!value) return;
            IsOrderView = false;
            IsConflictsView = false;
        }
    }

    /// <summary>The rows the filter lets through, top first.</summary>
    public ObservableCollection<LoadOrderRow> OrderView { get; } = [];

    /// <summary>Only the mods that share game files with another one.</summary>
    [ObservableProperty] public partial bool OrderOnlyShared { get; set; }
    /// <summary>The mods' add-on packs follow the order in dlclist.xml.</summary>
    [ObservableProperty] public partial bool OrderPacks { get; set; }
    [ObservableProperty] public partial bool HasOrder { get; set; }
    [ObservableProperty] public partial string OrderEmptyText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewOrderCommand))]
    public partial bool HasOrderChanges { get; set; }

    [ObservableProperty] public partial string OrderSummary { get; set; } = "";
    /// <summary>The mods that share files, as the header says it.</summary>
    [ObservableProperty] public partial string OrderSharedCount { get; set; } = "";

    partial void OnIsOrderViewChanged(bool value)
    {
        if (value) IsConflictsView = false;
        OnPropertyChanged(nameof(IsModsView));
    }
    partial void OnOrderOnlySharedChanged(bool value) => FilterOrder();
    partial void OnOrderPacksChanged(bool value) => UpdateOrderPending();

    /// <summary>Read the load order (off the UI thread); null when there is no game or it can't be read.</summary>
    private static OrderData? ReadOrder(string game)
    {
        var reg = ModRegistry.Load(game);
        var order = ModOrder.Of(reg, ModOrder.StateOf(game));
        var mods = order.Select(id => reg.Find(id)!).ToList();
        var others = ModOrder.Conflicts(game, order).ToDictionary(kv => kv.Key, kv => kv.Value.Others);
        return new OrderData(mods, others, reg.OrderPacks);
    }

    /// <summary>Show a freshly read order (marked moves are dropped: the game's order may have changed under them).</summary>
    internal void ShowOrder(OrderData? data, string empty)
    {
        _order = [.. (data?.Mods ?? []).Select(m => new LoadOrderRow(m, data!.Others.GetValueOrDefault(m.Id) ?? [], MoveOrderBy, MoveOrderToTop))];
        _orderNames = _order.ToDictionary(r => r.Id, r => r.Name);
        _savedOrder = [.. _order.Select(r => r.Id)];
        _savedPacks = data?.Packs ?? false;
        OrderPacks = _savedPacks;
        HasOrder = _order.Count > 0;
        OrderEmptyText = empty;
        int shared = _order.Count(r => r.HasConflicts);
        OrderSharedCount = L.T($"{shared} of {_order.Count} share game files with another mod");
        FilterOrder();
    }

    private void FilterOrder()
    {
        OrderView.Clear();
        foreach (var row in _order)
            if (!OrderOnlyShared || row.HasConflicts) OrderView.Add(row);
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < _order.Count; i++) _order[i].Position = i + 1;
        var at = _order.ToDictionary(r => r.Id, r => r.Position);
        foreach (var row in _order)
        {
            row.Moved = _savedOrder.IndexOf(row.Id) + 1 != row.Position;
            row.Describe(id => at.GetValueOrDefault(id, int.MaxValue), id => _orderNames.GetValueOrDefault(id, id));
        }
        UpdateOrderPending();
    }

    private void UpdateOrderPending()
    {
        bool moved = !_order.Select(r => r.Id).SequenceEqual(_savedOrder);
        bool packs = OrderPacks != _savedPacks;
        var parts = new List<string>();
        if (moved) parts.Add(L.T($"{_order.Count(r => r.Moved)} mod(s) moved"));
        if (packs) parts.Add(OrderPacks ? L.T("packs to follow the order") : L.T("packs to stop following it"));
        OrderSummary = string.Join(" · ", parts);
        HasOrderChanges = moved || packs;
    }

    /// <summary>
    /// Put a row at <paramref name="index"/> of the list as shown: dragged down past a row it goes right after it, up — right
    /// before it (rows the filter hides keep their places around it).
    /// </summary>
    internal void MoveOrderRow(LoadOrderRow row, int index)
    {
        int from = OrderView.IndexOf(row);
        index = Math.Clamp(index, 0, OrderView.Count - 1);
        if (from < 0 || index == from) return;
        var anchor = OrderView[index];
        _order.Remove(row);
        int at = _order.IndexOf(anchor);
        _order.Insert(index > from ? at + 1 : at, row);
        OrderView.Move(from, index);
        Renumber();
    }

    private void MoveOrderBy(LoadOrderRow row, int delta) => MoveOrderRow(row, OrderView.IndexOf(row) + delta);

    private void MoveOrderToTop(LoadOrderRow row)
    {
        int from = OrderView.IndexOf(row);
        if (from < 0) return;
        _order.Remove(row);
        _order.Insert(0, row);
        OrderView.Move(from, 0);
        Renumber();
    }

    [RelayCommand]
    private void RevertOrder()
    {
        var byId = _order.ToDictionary(r => r.Id);
        _order = [.. _savedOrder.Select(id => byId[id])];
        OrderPacks = _savedPacks;
        FilterOrder();
    }

    private bool CanReviewOrder() => HasOrderChanges && !IsBuilding;

    /// <summary>Show what the new order changes (which mod's version each shared file gets), then apply it as one plan.</summary>
    [RelayCommand(CanExecute = nameof(CanReviewOrder))]
    private void ReviewOrder()
    {
        var game = InstalledGameDir();
        if (game is null) return;
        var order = _order.Select(r => r.Id).ToList();
        bool? packs = OrderPacks != _savedPacks ? OrderPacks : null;
        var plan = new InstallPlan { Title = L.T("Changing the load order") };
        plan.Add(new ModOrderOp(order, packs switch
        {
            true => L.T("Put the mods in the new load order, their add-on packs in dlclist.xml too"),
            false => L.T("Put the mods in the new load order; dlclist.xml stops following it (left as it is)"),
            _ => null,
        }, packs));
        try
        {
            if (File.Exists(ModsOverlay.StatePath(game)))
            {
                string Name(string id) => _orderNames.GetValueOrDefault(id, id);
                foreach (var g in ModsOverlay.Load(game).RestackPreview(order).GroupBy(p => (p.To, p.From))
                                             .OrderByDescending(g => g.Count()))
                    plan.Warnings.Add(L.T($"«{Name(g.Key.To)}»'s version replaces «{Name(g.Key.From)}»'s in {g.Count()} file(s)."));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("previewing the load order failed", ex);
        }
        if (plan.Warnings.Count == 0 && packs is null)
            plan.Warnings.Add(L.T("No file changes hands — the new order is only recorded (it counts for the next conflicts)."));
        OpenPlan(plan, $"{Edition.DisplayName()} · {game}", L.T("Apply order"),
                 () => RunPlanAsync(plan, L.T("Reordering mods…"), L.T("Load order applied")),
                 L.T("Only the order changes — every mod keeps its files, and removing one brings the next one's back."));
    }
}
