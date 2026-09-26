using System.Collections.ObjectModel;
using System.ComponentModel;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>Player flow: the weapons already installed into the selected game — switch on/off, remove.</summary>
public sealed partial class MainViewModel
{
    private int _installedGeneration;

    public ObservableCollection<InstalledWeaponViewModel> Installed { get; } = [];

    [ObservableProperty] public partial bool HasInstalled { get; set; }
    [ObservableProperty] public partial string InstalledEmptyText { get; set; } = "";
    [ObservableProperty] public partial string InstalledCount { get; set; } = "0";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyInstalledCommand))]
    public partial bool HasPendingChanges { get; set; }

    [ObservableProperty] public partial string PendingSummary { get; set; } = "";

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
        string empty;
        if (game is null)
            empty = GameFolder.Trim().Length == 0
                ? "Choose the GTA V folder to see the weapons installed into it."
                : "Nothing installed into this folder yet.";
        else
        {
            empty = "Nothing installed into this game by ModDrop V yet.";
            var target = TargetFor(game, Edition);
            try
            {
                mods = await Task.Run(() => ModLibrary.List(target));
            }
            catch (Exception ex)
            {
                AppLog.Error("listing installed weapons failed", ex);
                empty = $"Could not read the installed weapons: {ex.Message}";
            }
        }
        if (gen != _installedGeneration) return;         // a newer refresh owns the list

        foreach (var row in Installed) row.PropertyChanged -= OnInstalledRowChanged;
        Installed.Clear();
        foreach (var m in mods)
        {
            var row = new InstalledWeaponViewModel(m);
            row.PropertyChanged += OnInstalledRowChanged;
            Installed.Add(row);
        }
        HasInstalled = Installed.Count > 0;
        InstalledCount = Installed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        InstalledEmptyText = empty;
        UpdatePending();
    }

    private void OnInstalledRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledWeaponViewModel.IsChanged)) UpdatePending();
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

    private bool CanApplyInstalled() => HasPendingChanges && !IsBuilding;

    /// <summary>Apply the marked switches / removals; shared packs are rebuilt and reinstalled.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyInstalled))]
    private async Task ApplyInstalled()
    {
        var game = InstalledGameDir();
        var changes = Installed.Select(r => r.Change).OfType<ModChange>().ToList();
        if (game is null || changes.Count == 0) return;

        ResultVisible = false;
        _log.Clear();
        LogText = "";
        LogIsError = false;
        HasLog = false;
        IsBuilding = true;
        StageStatus = "Updating installed weapons…";
        StageHint = "Loading the cylinder";
        var target = TargetFor(game, Edition);
        AppLog.Info($"installed changes: {string.Join(", ", changes)} in {game}");
        try
        {
            await Task.Run(() => ModLibrary.Apply(target, changes, OnLog));
            var summary = PendingSummary;
            ShowResult(true, "Installed weapons updated", $"{summary} — done:\n{game}", game);
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
        await RefreshInstalledAsync();
    }
}
