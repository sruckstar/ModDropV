using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Index;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// Player flow: the library — everything ModDrop V installed into the selected game, by
/// category and search; switch mods on / off or remove them (marked first, applied together
/// after a look at the plan), open their folder, see which ones change the same files.
/// </summary>
public sealed partial class MainViewModel
{
    private int _installedGeneration;

    /// <summary>Every installed mod.</summary>
    public ObservableCollection<InstalledModViewModel> Installed { get; } = [];
    /// <summary>The ones the category chip and the search let through.</summary>
    public ObservableCollection<InstalledModViewModel> InstalledView { get; } = [];
    public ObservableCollection<LibraryFilterViewModel> LibraryFilters { get; } = [];

    [ObservableProperty] public partial bool HasInstalled { get; set; }
    [ObservableProperty] public partial string InstalledEmptyText { get; set; } = "";
    [ObservableProperty] public partial string InstalledCount { get; set; } = "0";
    [ObservableProperty] public partial string LibrarySearch { get; set; } = "";
    /// <summary>Mods are installed, but the filter hides all of them.</summary>
    [ObservableProperty] public partial bool LibraryNoMatch { get; set; }
    [ObservableProperty] public partial bool IsReadingLibrary { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyInstalledCommand), nameof(ReviewChangesCommand))]
    public partial bool HasPendingChanges { get; set; }

    [ObservableProperty] public partial string PendingSummary { get; set; } = "";

    partial void OnLibrarySearchChanged(string value) => FilterLibrary();

    /// <summary>The game an installed-list read / change goes to; AddonWeapons Builder's staged packs are picked up from it.</summary>
    internal static InstallTarget TargetFor(string game, GameEdition edition) =>
        new(game, edition, AppPaths.StagingFor(edition), Path.Combine(AppPaths.Data, "plugins"),
            WeaponStaging.AwbStagingDirs(edition)) { IndexCacheRoot = GameIndexCache.DefaultRoot };

    /// <summary>The game folder as typed, if it can hold our mods.</summary>
    private string? InstalledGameDir()
    {
        var game = GameFolder.Trim();
        return game.Length > 0 && Directory.Exists(Path.Combine(game, "mods")) ? game : null;
    }

    /// <summary>Re-read what is installed in the selected game (player mode only).</summary>
    internal async Task RefreshInstalledAsync()
    {
        int gen = ++_installedGeneration;
        var game = IsPlayer ? InstalledGameDir() : null;
        List<InstalledMod> mods = [];
        Dictionary<string, (List<string> Others, bool OnTop)> conflicts = [];
        string empty;
        if (IsPlayer && GameFolder.Trim() is { Length: > 0 } chosen && OnlineMode.IsOn(chosen))
        {
            game = null;                                 // the registry is in the stash with the mods
            empty = L.T("The mods are put away for GTA Online — “Bring mods back” (at the top) shows them here again.");
        }
        else if (game is null)
            empty = GameFolder.Trim().Length == 0
                ? L.T("Choose the GTA V folder to see the mods installed into it.")
                : L.T("Nothing installed into this folder yet.");
        else
        {
            empty = L.T("Nothing installed into this game by ModDrop V yet — drop a mod on the Install page.");
            var target = TargetFor(game, Edition);
            IsReadingLibrary = true;
            try
            {
                (mods, conflicts) = await Task.Run(() =>
                {
                    var list = ModLibrary.List(target);
                    return (list, Conflicts(game, list));
                });
            }
            catch (Exception ex)
            {
                AppLog.Error("listing installed mods failed", ex);
                empty = L.T($"Could not read the installed mods: {ex.Message}");
            }
        }
        if (gen != _installedGeneration) return;         // a newer refresh owns the list
        IsReadingLibrary = false;

        foreach (var row in Installed) row.PropertyChanged -= OnInstalledRowChanged;
        Installed.Clear();
        foreach (var m in mods)
        {
            var c = conflicts.GetValueOrDefault(m.Id);
            var row = new InstalledModViewModel(m, c.Others, c.Others is null || c.OnTop, RaiseInstalled);
            row.PropertyChanged += OnInstalledRowChanged;
            Installed.Add(row);
        }
        HasInstalled = Installed.Count > 0;
        InstalledCount = Installed.Count.ToString(CultureInfo.InvariantCulture);
        InstalledEmptyText = empty;
        BuildFilters();
        UpdatePending();
    }

    /// <summary>
    /// Installed mod → the other installed mods that change some of the same game files
    /// (through the mods layer). Reads only.
    /// </summary>
    private static Dictionary<string, (List<string> Others, bool OnTop)> Conflicts(string game, List<InstalledMod> mods)
    {
        var result = new Dictionary<string, (List<string>, bool)>();
        if (!File.Exists(ModsOverlay.StatePath(game))) return result;
        var overlay = ModsOverlay.Load(game);
        var names = mods.ToDictionary(m => m.Id, m => m.Name);
        // the shared limits are no mod; add-on packs installed before them keep a raised pool of their own and share
        // gameconfig.xml without clashing: each builds on the one below
        var raisers = ModRegistry.Load(game).Mods.Where(r => r.Get("pools") == "1").Select(r => r.Id).ToHashSet();
        foreach (var m in mods)
        {
            var shared = overlay.PathsOf(m.Id)
                                .Select(p => p.Equals(GamePools.GameConfig, StringComparison.OrdinalIgnoreCase) && raisers.Contains(m.Id)
                                    ? [.. overlay.OwnersOf(p).Where(o => o == m.Id || !raisers.Contains(o))]
                                    : overlay.OwnersOf(p))
                                .Select(o => o.Where(x => x != GamePools.LimitsOwner).ToList())
                                .Where(o => o.Count > 1).ToList();
            var others = shared.SelectMany(o => o).Where(o => o != m.Id).Distinct()
                               .Select(o => names.GetValueOrDefault(o, o)).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                               .ToList();
            if (others.Count > 0) result[m.Id] = (others, shared.All(o => o[0] == m.Id));
        }
        return result;
    }

    /// <summary>Put a mod's versions of the files it shares with other mods on top — through the plan.</summary>
    private void RaiseInstalled(InstalledModViewModel row)
    {
        var game = InstalledGameDir();
        if (game is null || IsBuilding) return;
        var plan = new InstallPlan { Title = L.T($"Putting «{row.Name}» on top") };
        plan.Add(new OverlayRaiseOp(row.Mod.Id, row.Name));
        plan.Warnings.Add(L.T($"Where it and {string.Join(", ", row.Conflicts)} change the same files, the game gets «{row.Name}»'s."));
        OpenPlan(plan, $"{Edition.DisplayName()} · {game}", L.T("Put on top"),
                 () => RunPlanAsync(plan, L.T("Reordering mods…"), L.T($"«{row.Name}» is on top")),
                 L.T("Only the order changes — every mod keeps its files, and removing one brings the next one's back."));
    }

    /// <summary>"All" plus a chip per category present; the one picked stays picked if it's still there.</summary>
    private void BuildFilters()
    {
        var picked = LibraryFilters.FirstOrDefault(f => f.IsOn)?.Category;
        foreach (var f in LibraryFilters) f.PropertyChanged -= OnFilterChanged;
        LibraryFilters.Clear();
        LibraryFilters.Add(new LibraryFilterViewModel(null, L.T("All"), Installed.Count));
        foreach (var g in Installed.GroupBy(r => r.Category).OrderBy(g => g.Key))
            LibraryFilters.Add(new LibraryFilterViewModel(g.Key, g.Key.PluralName(), g.Count()));
        (LibraryFilters.FirstOrDefault(f => f.Category == picked) ?? LibraryFilters[0]).IsOn = true;
        foreach (var f in LibraryFilters) f.PropertyChanged += OnFilterChanged;
        FilterLibrary();
    }

    private void OnFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryFilterViewModel.IsOn) || sender is not LibraryFilterViewModel { IsOn: true } on) return;
        foreach (var f in LibraryFilters)
            if (f != on) f.IsOn = false;
        FilterLibrary();
    }

    private void FilterLibrary()
    {
        var category = LibraryFilters.FirstOrDefault(f => f.IsOn)?.Category;
        var query = LibrarySearch.Trim();
        InstalledView.Clear();
        foreach (var row in Installed)
            if ((category is null || row.Category == category) && row.Matches(query))
                InstalledView.Add(row);
        LibraryNoMatch = HasInstalled && InstalledView.Count == 0;
    }

    private void OnInstalledRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledModViewModel.IsChanged)) UpdatePending();
    }

    private void UpdatePending()
    {
        var changed = Installed.Where(r => r.IsChanged).ToList();
        int remove = changed.Count(r => r.PendingRemove);
        int on = changed.Count(r => !r.PendingRemove && r.Enabled);
        int off = changed.Count - remove - on;
        var parts = new List<string>();
        if (off > 0) parts.Add(L.T($"{off} to switch off"));
        if (on > 0) parts.Add(L.T($"{on} to switch on"));
        if (remove > 0) parts.Add(L.T($"{remove} to remove"));
        PendingSummary = string.Join(" · ", parts);
        HasPendingChanges = changed.Count > 0;
    }

    [RelayCommand]
    private void RevertInstalled()
    {
        foreach (var row in Installed) row.Revert();
    }

    [RelayCommand]
    private async Task RefreshLibrary()
    {
        await RefreshInstalledAsync();
        await RefreshStatusAsync();
    }

    private bool CanApplyInstalled() => HasPendingChanges && !IsBuilding;

    private List<ModChange> PendingChanges() => Installed.Select(r => r.Change).OfType<ModChange>().ToList();

    /// <summary>Show what applying the marked changes will do; applying is the dialog's confirm button.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyInstalled))]
    private void ReviewChanges()
    {
        var game = InstalledGameDir();
        var changes = PendingChanges();
        if (game is null || changes.Count == 0) return;
        InstallPlan plan;
        try
        {
            plan = ModLibrary.PlanChanges(TargetFor(game, Edition), changes);
        }
        catch (Exception ex)
        {
            AppLog.Error("planning installed changes failed", ex);
            ShowResult(false, L.T("Couldn't plan the changes"), ex.Message, null);
            return;
        }
        OpenPlan(plan, $"{Edition.DisplayName()} · {game}", L.T("Apply changes"), ApplyInstalledAsync,
                 L.T("Everything is done as one step: if something fails, the game is left exactly as it was."));
    }

    /// <summary>Apply the marked switches / removals; shared packs are rebuilt and reinstalled.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyInstalled))]
    private Task ApplyInstalled() => ApplyInstalledAsync();

    private async Task ApplyInstalledAsync()
    {
        var game = InstalledGameDir();
        var changes = PendingChanges();
        if (game is null || changes.Count == 0) return;

        ResultVisible = false;
        StartLog([]);
        IsBuilding = true;
        StageStatus = L.T("Updating installed mods…");
        StageHint = L.T("Getting ready");
        var target = TargetFor(game, Edition);
        AppLog.Info($"installed changes: {string.Join(", ", changes)} in {game}");
        var run = new PlanRun();
        Follow(run);
        try
        {
            await Task.Run(() => ModLibrary.Apply(target, changes, OnLog, run));
            var summary = PendingSummary;
            ShowResult(true, L.T("Installed mods updated"), L.T($"{summary} — done:\n{game}"), game);
        }
        catch (OperationCanceledException)
        {
            ShowResult(false, L.T("Cancelled — nothing changed"), L.T("Every step done so far was taken back."), null);
        }
        catch (Exception ex)
        {
            AppLog.Error("installed changes failed", ex);
            FailLog(ex);
            ShowResult(false, L.T("Update failed"), L.T($"{ex.Message}\n\nSee the log for details."), null);
        }
        finally
        {
            IsBuilding = false;
            Follow(null);
        }
        await AfterGameChangedAsync();
    }
}
