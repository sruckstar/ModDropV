using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// Player flow: verify / repair — check every installed mod against what its install left (<see cref="ModCheck"/>), mark the
/// broken ones in the library, fix in place what a plan can (dlclist.xml lines, stale copies) and install the rest again
/// from their own file. A quick check (lengths only) runs by itself after a game update. Install reports open from here too.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The last check's findings by mod id (rows read them when the library is rebuilt).</summary>
    private Dictionary<string, ModHealth> _health = new(StringComparer.Ordinal);
    /// <summary>The game those findings are of.</summary>
    private string? _healthGame;
    /// <summary>The game build the quick check after a game update last ran for (game folder → its exe signature).</summary>
    private readonly Dictionary<string, string> _quickChecked = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckModsCommand))]
    public partial bool IsChecking { get; set; }

    /// <summary>The install report the result banner offers (the run that just ended wrote it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultReport))]
    public partial string? ResultReport { get; set; }
    public bool HasResultReport => ResultReport is not null;

    /// <summary>The banner shows a check that found something to repair.</summary>
    [ObservableProperty] public partial bool ResultCanRepair { get; set; }

    private bool CanCheck() => !IsChecking;

    /// <summary>Check every installed mod, file by file.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckMods() => CheckAsync(quick: false, quiet: false);

    /// <param name="ids">only these mods (the others keep what the last check found)</param>
    /// <param name="quiet">a check the player didn't ask for: only the library's marks change, the banner stays</param>
    private async Task CheckAsync(bool quick, bool quiet, IReadOnlyCollection<string>? ids = null)
    {
        var game = InstalledGameDir();
        if (game is null || IsChecking || IsBuilding || !HasInstalled) return;
        IsChecking = true;
        List<ModHealth> health;
        try
        {
            health = await Task.Run(() => ModCheck.Verify(game, ids, quick));
        }
        catch (Exception ex)
        {
            AppLog.Error("checking the mods failed", ex);
            if (!quiet) ShowResult(false, L.T("Couldn't check the mods"), ex.Message, null);
            return;
        }
        finally
        {
            IsChecking = false;
        }
        if (!string.Equals(game, InstalledGameDir(), StringComparison.OrdinalIgnoreCase)) return;
        if (ids is null || _healthGame != game) _health = new(StringComparer.Ordinal);
        _healthGame = game;
        foreach (var h in health) _health[h.Id] = h;
        foreach (var row in Installed) row.Health = _health.GetValueOrDefault(row.Mod.Id);
        var broken = _health.Values.Where(h => h.Broken).ToList();
        AppLog.Info($"mods checked ({(quick ? "quick" : "full")}): {health.Count}, broken: {string.Join(", ", broken.Select(h => h.Id))}");
        if (quiet) return;
        if (broken.Count == 0)
        {
            ShowResult(true, L.T($"All {_health.Count} mod(s) are in order"), L.T("Every file is there and unchanged, the packs are listed, the copies are up to date."), null);
            return;
        }
        var names = string.Join(", ", broken.Take(4).Select(h => h.Name)) + (broken.Count > 4 ? L.T($" and {broken.Count - 4} more") : "");
        ShowResult(false, broken.Count == 1 ? L.T($"«{broken[0].Name}» needs a repair") : L.T($"{broken.Count} mods need a repair"),
                   L.T($"{names} — hover the “broken” mark in the library to see what is wrong."), null);
        ResultCanRepair = true;
    }

    /// <summary>The library was read again: rows get the last check's findings (of this game); after a plan the broken ones are checked again.</summary>
    private void ShowHealth(string? game, bool recheck)
    {
        if (game is null || !string.Equals(game, _healthGame, StringComparison.OrdinalIgnoreCase))
        {
            _health = new(StringComparer.Ordinal);
            _healthGame = game;
        }
        foreach (var row in Installed) row.Health = _health.GetValueOrDefault(row.Mod.Id);
        var broken = _health.Values.Where(h => h.Broken).Select(h => h.Id).Where(id => Installed.Any(r => r.Mod.Id == id)).ToList();
        if (recheck && broken.Count > 0) _ = CheckAsync(quick: false, quiet: true, broken);
    }

    /// <summary>After a game update the copies are stale: a quick check (lengths only) shows what else the update broke, once per build.</summary>
    private void CheckAfterGameUpdate()
    {
        var game = InstalledGameDir();
        if (game is null || !HasStaleCopies || IsBuilding || !HasInstalled) return;
        var build = Mdv.Core.Index.GameIndex.ExeSignature(game);
        if (_quickChecked.TryGetValue(game, out var done) && done == build) return;
        _quickChecked[game] = build;
        _ = CheckAsync(quick: true, quiet: true);
    }

    /// <summary>Repair every broken mod: the in-place fixes in one plan, then the first one that needs installing again.</summary>
    [RelayCommand]
    private Task RepairAll() => RepairAsync([.. _health.Values.Where(h => h.Broken)]);

    /// <summary>Repair one mod (its row's button).</summary>
    private Task RepairMod(InstalledModViewModel row) =>
        row.Health is { Broken: true } h ? RepairAsync([h]) : Task.CompletedTask;

    private async Task RepairAsync(List<ModHealth> broken)
    {
        var game = InstalledGameDir();
        ResultCanRepair = false;
        if (game is null || broken.Count == 0 || IsBuilding) return;
        if (ModCheck.PlanRepair(broken) is { } plan)
        {
            var again = broken.Where(h => h.NeedsReinstall).Select(h => h.Name).ToList();
            if (again.Count > 0)
                plan.Warnings.Add(L.T($"{string.Join(", ", again)}: broken files only installing the mod again brings back — “repair” on its row does it after this."));
            OpenPlan(plan, $"{Edition.DisplayName()} · {game}", L.T("Repair"), async () =>
                {
                    await RunPlanAsync(plan, L.T("Repairing mods…"), L.T("Mods repaired"));
                    await CheckAsync(quick: false, quiet: true);
                },
                L.T("Only what is broken is put right: the copies are taken fresh from the game with every mod’s changes back, missing pack lines are listed again."));
            return;
        }
        if (broken.FirstOrDefault(h => h.NeedsReinstall) is { } mod) await ReinstallAsync(mod);
    }

    /// <summary>Install a mod again from its own file: it goes through the Install page like a drop (the plan is shown there).</summary>
    private async Task ReinstallAsync(ModHealth mod)
    {
        var source = mod.SourceThere ? mod.SourcePath : null;
        if (source is null)
        {
            // the file it came from moved or was deleted (or was never recorded): ask for it
            if (PickArchives is null) return;
            var picked = await PickArchives(L.T($"The file «{mod.Name}» was installed from"));
            if (picked.Count == 0) return;
            source = picked[0];
        }
        AppLog.Info($"repair: installing {mod.Id} again from {source}");
        IsLibraryPage = false;
        await LoadPlayerSourceAsync([source]);
        ShowResult(true, L.T($"Install «{mod.Name}» again to repair it"),
                   L.T("Its file is loaded — pick the same options as before and install: it replaces the broken copy and keeps its place in the load order."), null);
    }

    /// <summary>Open an install report in the system's text viewer.</summary>
    internal static void OpenReport(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            Diagnostics.OpenFile(path);
        }
        catch (Exception ex)
        {
            AppLog.Error("opening the install report failed", ex);
        }
    }

    [RelayCommand]
    private void OpenResultReport() => OpenReport(ResultReport);

    [RelayCommand]
    private void OpenReportsFolder()
    {
        try
        {
            Directory.CreateDirectory(InstallReport.Root);
            Diagnostics.OpenInFileManager(InstallReport.Root);
        }
        catch (Exception ex)
        {
            AppLog.Error("opening the reports folder failed", ex);
        }
    }
}
