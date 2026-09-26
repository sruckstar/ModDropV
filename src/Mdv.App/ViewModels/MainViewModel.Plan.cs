using System.Collections.ObjectModel;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>One numbered step of the plan dialog.</summary>
public sealed record PlanStepRow(int Number, string Text);

/// <summary>
/// "What will be done": every change to the game — an install, the library's switches and
/// removals, refreshing the copies in mods — is shown as its install plan first and runs
/// only from the dialog's confirm button.
/// </summary>
public sealed partial class MainViewModel
{
    private Func<Task>? _planConfirm;

    [ObservableProperty] public partial bool IsPlanOpen { get; set; }
    [ObservableProperty] public partial string PlanTitle { get; set; } = "";
    /// <summary>The game it goes into.</summary>
    [ObservableProperty] public partial string PlanTarget { get; set; } = "";
    [ObservableProperty] public partial string PlanConfirmText { get; set; } = "";
    [ObservableProperty] public partial string PlanNote { get; set; } = "";
    [ObservableProperty] public partial bool HasPlanWarnings { get; set; }

    public ObservableCollection<PlanStepRow> PlanSteps { get; } = [];
    public ObservableCollection<string> PlanWarnings { get; } = [];

    /// <summary>Player: show the plan of installing the picked mod; the dialog's button installs it.</summary>
    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task ReviewInstall()
    {
        ResultVisible = false;
        if (!HasGame)
        {
            IsGameDialogOpen = true;                   // nowhere to install yet — ask for the game
            return;
        }
        var (job, error) = PrepareJob();
        if (job is null)
        {
            ShowResult(false, "Couldn't start", error ?? "Unknown error.", null);
            return;
        }
        if (job.Package is not { } pkg)
        {
            await RunJobAsync(job);
            return;
        }
        var game = GameFolder.Trim();
        InstallPlan plan;
        try
        {
            plan = ModLibrary.HandlerFor(pkg.Category).PlanInstall(pkg, TargetFor(game, Edition));
        }
        catch (Exception ex)
        {
            AppLog.Error("planning the install failed", ex);
            ShowResult(false, "Couldn't plan the install", ex.Message, null);
            return;
        }
        plan.Warnings.InsertRange(0, pkg.Warnings);
        // files for the game folder itself (plugins, scripts, settings) can only be taken back by removing the mod
        bool gameFolder = plan.Ops.Any(o => o is FileEditOp or DeleteFileOp ||
                                            o is CopyFileOp c && !c.GameRel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase));
        OpenPlan(plan, $"{Edition.DisplayName()} · {game}", "Install", () => RunJobAsync(job), gameFolder
                     ? "The game's own archives stay untouched — changes to them go into copies under mods. Files for the " +
                       "game folder are copied there; removing the mod in the Library takes them back."
                     : "The game's own files stay untouched — everything goes into the mods folder. " +
                       "Switch it off or remove it any time in the Library.");
    }

    internal void OpenPlan(InstallPlan plan, string target, string confirm, Func<Task> run, string note)
    {
        PlanTitle = plan.Title;
        PlanTarget = target;
        PlanConfirmText = confirm;
        PlanNote = note;
        PlanSteps.Clear();
        int i = 0;
        foreach (var step in plan.Describe()) PlanSteps.Add(new PlanStepRow(++i, step));
        PlanWarnings.Clear();
        foreach (var w in plan.Warnings.Distinct()) PlanWarnings.Add(w);
        HasPlanWarnings = PlanWarnings.Count > 0;
        _planConfirm = run;
        IsPlanOpen = true;
        AppLog.Info($"plan shown: {plan.Title} — {string.Join(" | ", plan.Describe())}");
    }

    [RelayCommand]
    private void ClosePlan()
    {
        IsPlanOpen = false;
        _planConfirm = null;
    }

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task ConfirmPlan()
    {
        var run = _planConfirm;
        ClosePlan();
        if (run is not null) await run();
    }

    /// <summary>Run a plan straight through the executor (steps that belong to no panel: refreshing copies…).</summary>
    private async Task RunPlanAsync(InstallPlan plan, string stage, string done)
    {
        var game = GameFolder.Trim();
        ResultVisible = false;
        StartLog([]);
        IsBuilding = true;
        StageStatus = stage;
        StageHint = "Getting ready";
        try
        {
            await Task.Run(() => InstallExecutor.Run(plan, TargetFor(game, Edition), OnLog));
            ShowResult(true, done, game, game);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{plan.Title} failed", ex);
            FailLog(ex);
            ShowResult(false, $"{plan.Title} — failed", $"{ex.Message}\n\nSee the log for details.", null);
        }
        finally
        {
            IsBuilding = false;
        }
        await AfterGameChangedAsync();
    }
}
