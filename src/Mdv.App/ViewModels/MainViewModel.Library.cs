using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Mdv.App.Services;
using Mdv.Core;
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
    private static InstallTarget TargetFor(string game, GameEdition edition) =>
        new(game, edition, AppPaths.StagingFor(edition), Path.Combine(AppPaths.Data, "plugins"),
            WeaponStaging.AwbStagingDirs(edition));

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
        Dictionary<string, List<string>> conflicts = [];
        string empty;
        if (game is null)
            empty = GameFolder.Trim().Length == 0
                ? "Choose the GTA V folder to see the mods installed into it."
                : "Nothing installed into this folder yet.";
        else
        {
            empty = "Nothing installed into this game by ModDrop V yet — drop a mod on the Install page.";
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
                empty = $"Could not read the installed mods: {ex.Message}";
            }
        }
        if (gen != _installedGeneration) return;         // a newer refresh owns the list
        IsReadingLibrary = false;

        foreach (var row in Installed) row.PropertyChanged -= OnInstalledRowChanged;
        Installed.Clear();
        foreach (var m in mods)
        {
            var row = new InstalledModViewModel(m, conflicts.GetValueOrDefault(m.Id));
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
    private static Dictionary<string, List<string>> Conflicts(string game, List<InstalledMod> mods)
    {
        var result = new Dictionary<string, List<string>>();
        if (!File.Exists(ModsOverlay.StatePath(game))) return result;
        var overlay = ModsOverlay.Load(game);
        var names = mods.ToDictionary(m => m.Id, m => m.Name);
        foreach (var m in mods)
        {
            var others = overlay.PathsOf(m.Id).SelectMany(overlay.OwnersOf).Where(o => o != m.Id).Distinct()
                                .Select(o => names.GetValueOrDefault(o, o)).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                                .ToList();
            if (others.Count > 0) result[m.Id] = others;
        }
        return result;
    }

    /// <summary>"All" plus a chip per category present; the one picked stays picked if it's still there.</summary>
    private void BuildFilters()
    {
        var picked = LibraryFilters.FirstOrDefault(f => f.IsOn)?.Category;
        foreach (var f in LibraryFilters) f.PropertyChanged -= OnFilterChanged;
        LibraryFilters.Clear();
        LibraryFilters.Add(new LibraryFilterViewModel(null, "All", Installed.Count));
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
        if (off > 0) parts.Add($"{off} to switch off");
        if (on > 0) parts.Add($"{on} to switch on");
        if (remove > 0) parts.Add($"{remove} to remove");
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
            ShowResult(false, "Couldn't plan the changes", ex.Message, null);
            return;
        }
        OpenPlan(plan, $"{Edition.DisplayName()} · {game}", "Apply changes", ApplyInstalledAsync,
                 "Everything is done as one step: if something fails, the game is left exactly as it was.");
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
        StageStatus = "Updating installed mods…";
        StageHint = "Getting ready";
        var target = TargetFor(game, Edition);
        AppLog.Info($"installed changes: {string.Join(", ", changes)} in {game}");
        try
        {
            await Task.Run(() => ModLibrary.Apply(target, changes, OnLog));
            var summary = PendingSummary;
            ShowResult(true, "Installed mods updated", $"{summary} — done:\n{game}", game);
        }
        catch (Exception ex)
        {
            AppLog.Error("installed changes failed", ex);
            FailLog(ex);
            ShowResult(false, "Update failed", $"{ex.Message}\n\nSee the log for details.", null);
        }
        finally
        {
            IsBuilding = false;
        }
        await AfterGameChangedAsync();
    }
}
