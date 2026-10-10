using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// One mod in the load order list: its place (1 = top, wins), and the mods it shares game files with — which of them it
/// wins over at its place now. Moving it only marks a change; nothing touches the game until the order is applied.
/// </summary>
public sealed partial class LoadOrderRow : ObservableObject
{
    private readonly Action<LoadOrderRow, int> _move;
    private readonly Action<LoadOrderRow> _top;

    /// <param name="others">the mods it shares files with (ids)</param>
    /// <param name="move">moves it by a number of places in the list as shown (−1 up)</param>
    /// <param name="top">puts it at the top</param>
    public LoadOrderRow(RegisteredMod mod, IReadOnlyList<string> others, Action<LoadOrderRow, int> move, Action<LoadOrderRow> top)
    {
        Id = mod.Id;
        Name = mod.Name.Length > 0 ? mod.Name : mod.Id;
        CategoryName = mod.Category.ShortLabel();
        Enabled = mod.Enabled;
        Others = others;
        _move = move;
        _top = top;
    }

    public string Id { get; }
    public string Name { get; }
    public string CategoryName { get; }
    /// <summary>Switched on (a switched-off mod keeps its place for when it comes back).</summary>
    public bool Enabled { get; }
    public IReadOnlyList<string> Others { get; }
    public bool HasConflicts => Others.Count > 0;

    /// <summary>1 = top.</summary>
    [ObservableProperty] public partial int Position { get; set; }
    /// <summary>Its place differs from the one the game has now.</summary>
    [ObservableProperty] public partial bool Moved { get; set; }
    /// <summary>It is being dragged.</summary>
    [ObservableProperty] public partial bool Dragging { get; set; }
    [ObservableProperty] public partial string ConflictsTip { get; set; } = "";
    /// <summary>Of the mods it shares files with, how many are under it: "wins 2 / 3".</summary>
    [ObservableProperty] public partial string Wins { get; set; } = "";
    /// <summary>It wins over every mod it shares files with.</summary>
    [ObservableProperty] public partial bool WinsAll { get; set; }

    public string Off => Enabled ? "" : L.T("off");

    /// <summary>The tip and the count, from the places of the others now (names by id).</summary>
    internal void Describe(Func<string, int> position, Func<string, string> name)
    {
        if (!HasConflicts) return;
        var under = Others.Where(o => position(o) > Position).Select(name).ToList();
        var over = Others.Where(o => position(o) < Position).Select(name).ToList();
        WinsAll = over.Count == 0;
        Wins = L.T($"wins {under.Count} / {Others.Count}");
        ConflictsTip = over.Count == 0
            ? L.T($"Changes the same game files as {string.Join(", ", under)} — higher in the order, its versions win.")
            : under.Count == 0
                ? L.T($"Changes the same game files as {string.Join(", ", over)} — they are higher, theirs win.")
                : L.T($"Changes the same game files as {string.Join(", ", Others.Select(name))}: wins over {string.Join(", ", under)}; {string.Join(", ", over)} win over it.");
    }

    [RelayCommand]
    private void Up() => _move(this, -1);

    [RelayCommand]
    private void Down() => _move(this, 1);

    [RelayCommand]
    private void Top() => _top(this);
}
