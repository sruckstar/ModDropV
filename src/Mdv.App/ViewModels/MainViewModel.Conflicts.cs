using System.Collections.ObjectModel;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// Player flow: the library's conflicts — every file installed mods share (in the game's archives, the game folder,
/// dlclist.xml lines), whose version the game gets and who else has one. A winner can be picked per file (a pin, stronger
/// than the load order); pins are marked first and applied together as one plan.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>A shown list is cut here: thousands of rows (NaturalVision against another big mod) would only slow the window.</summary>
    private const int ConflictsShown = 300;

    /// <summary>The game's shared files as read, with the mods' names and places in the order.</summary>
    internal sealed record ConflictData(List<ContestedFile> Files, Dictionary<string, string> Names, List<string> Order);

    private List<ConflictRow> _conflicts = [];
    private Dictionary<string, string> _conflictNames = [];

    /// <summary>The library shows the shared files.</summary>
    [ObservableProperty] public partial bool IsConflictsView { get; set; }

    public ObservableCollection<ConflictRow> ConflictsView { get; } = [];
    /// <summary>"All mods", then each mod that shares a file.</summary>
    public ObservableCollection<ConflictModFilter> ConflictMods { get; } = [];

    [ObservableProperty] public partial ConflictModFilter? ConflictMod { get; set; }
    [ObservableProperty] public partial string ConflictSearch { get; set; } = "";
    [ObservableProperty] public partial bool HasSharedFiles { get; set; }
    [ObservableProperty] public partial string ConflictsCount { get; set; } = "";
    /// <summary>"…and 1 200 more — pick a mod or search to narrow it down".</summary>
    [ObservableProperty] public partial string ConflictsMore { get; set; } = "";
    [ObservableProperty] public partial bool ConflictsNoMatch { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewPinsCommand))]
    public partial bool HasPinChanges { get; set; }

    [ObservableProperty] public partial string PinSummary { get; set; } = "";

    /// <summary>A mod is picked in the filter: its files can all go to it at once.</summary>
    public bool CanGiveAll => ConflictMod?.Id is not null;

    partial void OnIsConflictsViewChanged(bool value)
    {
        if (value) IsOrderView = false;
        OnPropertyChanged(nameof(IsModsView));
    }
    partial void OnConflictModChanged(ConflictModFilter? value)
    {
        OnPropertyChanged(nameof(CanGiveAll));
        FilterConflicts();
    }
    partial void OnConflictSearchChanged(string value) => FilterConflicts();

    /// <summary>Read the shared files (off the UI thread).</summary>
    private static ConflictData ReadConflicts(string game)
    {
        var reg = ModRegistry.Load(game);
        var names = reg.Mods.ToDictionary(m => m.Id, m => m.Name.Length > 0 ? m.Name : m.Id, StringComparer.Ordinal);
        return new ConflictData(FileConflicts.Of(game), names, ModOrder.Of(reg, ModOrder.StateOf(game)));
    }

    /// <summary>Show freshly read shared files (marked pins are dropped: the game may have changed under them).</summary>
    internal void ShowConflicts(ConflictData? data)
    {
        _conflictNames = data?.Names ?? [];
        var order = data?.Order ?? [];
        string Name(string id) => _conflictNames.GetValueOrDefault(id, id);
        int Rank(string id) => order.IndexOf(id) is >= 0 and var i ? i : int.MaxValue;
        _conflicts = [.. (data?.Files ?? []).Select(f => new ConflictRow(f, Name, Rank, UpdatePinsPending))];
        HasSharedFiles = _conflicts.Count > 0;
        int pinned = _conflicts.Count(r => r.File.Pinned is not null);
        ConflictsCount = pinned > 0
            ? L.T($"{_conflicts.Count} shared file(s), {pinned} picked by hand")
            : L.T($"{_conflicts.Count} shared file(s)");

        var keep = ConflictMod?.Id;
        ConflictMods.Clear();
        ConflictMods.Add(new ConflictModFilter(null, L.T("All mods"), _conflicts.Count));
        foreach (var g in _conflicts.SelectMany(r => r.File.Mods).GroupBy(m => m).OrderBy(g => Rank(g.Key)))
            ConflictMods.Add(new ConflictModFilter(g.Key, Name(g.Key), g.Count()));
        ConflictMod = ConflictMods.FirstOrDefault(m => m.Id == keep) ?? ConflictMods[0];
        FilterConflicts();
        UpdatePinsPending();
    }

    private void FilterConflicts()
    {
        ConflictsView.Clear();
        var mod = ConflictMod?.Id;
        var text = ConflictSearch.Trim();
        var match = _conflicts.Where(r => (mod is null || r.File.Mods.Contains(mod)) &&
                                          (text.Length == 0 || r.Path.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var row in match.Take(ConflictsShown)) ConflictsView.Add(row);
        ConflictsMore = match.Count > ConflictsShown
            ? L.T($"…and {match.Count - ConflictsShown} more — pick a mod or search to narrow it down.")
            : "";
        ConflictsNoMatch = HasSharedFiles && match.Count == 0;
    }

    /// <summary>Open the shared files of one mod (the "conflict" badge of the mods list).</summary>
    internal void ShowConflictsOf(InstalledModViewModel row)
    {
        IsConflictsView = true;
        ConflictSearch = "";
        ConflictMod = ConflictMods.FirstOrDefault(m => m.Id == row.Mod.Id) ?? ConflictMods.FirstOrDefault();
    }

    private void UpdatePinsPending()
    {
        var marked = _conflicts.Where(r => r.HasPending).ToList();
        int pins = marked.Count(r => r.Pending != ConflictRow.ToOrder), back = marked.Count - pins;
        var parts = new List<string>();
        if (pins > 0) parts.Add(L.T($"{pins} file(s) to pin"));
        if (back > 0) parts.Add(L.T($"{back} back to the load order"));
        PinSummary = string.Join(" · ", parts);
        HasPinChanges = marked.Count > 0;
    }

    /// <summary>Every shown file of the mod picked in the filter goes to it.</summary>
    [RelayCommand]
    private void GiveAllToMod()
    {
        if (ConflictMod?.Id is not { } mod) return;
        foreach (var row in ConflictsView) row.Pick(mod);
    }

    [RelayCommand]
    private void RevertPins()
    {
        foreach (var row in _conflicts) row.Pending = null;
    }

    private bool CanReviewPins() => HasPinChanges && !IsBuilding;

    /// <summary>Show the files that change hands, then apply the marked pins as one plan.</summary>
    [RelayCommand(CanExecute = nameof(CanReviewPins))]
    private void ReviewPins()
    {
        var game = InstalledGameDir();
        if (game is null) return;
        string Name(string id) => _conflictNames.GetValueOrDefault(id, id);
        var plan = new InstallPlan { Title = L.T("Picking winners of shared files") };
        foreach (var row in _conflicts.Where(r => r.HasPending))
        {
            var to = row.PinAfter;
            plan.Add(new PinFileOp(row.File.Area, row.File.Key, to, to is null
                ? L.T($"{row.Path}: back to the load order («{Name(row.WinnerAfter)}»’s version)")
                : L.T($"{row.Path}: «{Name(to)}»’s version, whatever the load order says")));
        }
        foreach (var g in _conflicts.Where(r => r.HasPending && r.WinnerAfter != r.File.Winner)
                                    .GroupBy(r => (To: r.WinnerAfter, From: r.File.Winner)).OrderByDescending(g => g.Count()))
            plan.Warnings.Add(L.T($"«{Name(g.Key.To)}»'s version replaces «{Name(g.Key.From)}»'s in {g.Count()} file(s)."));
        if (plan.Warnings.Count == 0)
            plan.Warnings.Add(L.T("No file changes hands now — the pins keep these versions on top whatever the order becomes."));
        OpenPlan(plan, $"{Edition.DisplayName()} · {game}", L.T("Apply pins"),
                 () => RunPlanAsync(plan, L.T("Picking winners…"), L.T("Pins applied")),
                 L.T("Every mod keeps its files — a pin only picks whose version the game gets. Removing the pinned mod hands the file back to the load order."));
    }
}

/// <summary>One choice of the conflicts' mod filter.</summary>
/// <param name="Id">null: every mod</param>
public sealed record ConflictModFilter(string? Id, string Name, int Count)
{
    public string Label => $"{Name} ({Count})";
}
