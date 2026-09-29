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
    /// <summary>"Changes 192 files inside 17 game archives" — for plans that touch the game's archives.</summary>
    [ObservableProperty] public partial string PlanFootprint { get; set; } = "";
    [ObservableProperty] public partial bool HasPlanFootprint { get; set; }
    /// <summary>"Needs about 21 GB on D:\ — 260 GB free."</summary>
    [ObservableProperty] public partial string PlanSpace { get; set; } = "";
    [ObservableProperty] public partial bool PlanTooBig { get; set; }

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
        if (OnlineMode.IsOn(GameFolder.Trim()))
        {
            ShowResult(false, L.T("The mods are put away for GTA Online"),
                       L.T("Bring them back first (the button at the top), then install."), null);
            return;
        }
        var (job, error) = PrepareJob();
        if (job is null)
        {
            ShowResult(false, "Couldn't start", error ?? L.T("Unknown error."), null);
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
            plan = ModLibrary.PlanInstall(pkg, TargetFor(game, Edition));
        }
        catch (Exception ex)
        {
            AppLog.Error("planning the install failed", ex);
            ShowResult(false, L.T("Couldn't plan the install"), ex.Message, null);
            return;
        }
        plan.Warnings.InsertRange(0, pkg.Warnings);
        // files for the game folder itself (plugins, scripts, settings) can only be taken back by removing the mod
        bool gameFolder = plan.Ops.Any(o => o is FileEditOp or DeleteFileOp or CopyFilesOp ||
                                            o is CopyFileOp c && !c.GameRel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
                                                              && !c.GameRel.StartsWith("onigiri/", StringComparison.OrdinalIgnoreCase));
        bool onigiri = ModsLayout.UsesOnigiri(game);
        var note = pkg.Category == ModCategory.Script
            ? L.T("Scripts live in the game folder: files it replaces are kept and come back when the mod is removed. " +
              "Switch it off or remove it any time in the Library.")
            : gameFolder
                ? onigiri
                    ? L.T("The game's own archives stay untouched — changes to them go into the onigiri folder. Files for the " +
                      "game folder are copied there; removing the mod in the Library takes them back.")
                    : L.T("The game's own archives stay untouched — changes to them go into copies under mods. Files for the " +
                      "game folder are copied there; removing the mod in the Library takes them back.")
                : onigiri
                    ? L.T("The game's own files stay untouched — everything goes into the onigiri folder. " +
                      "Switch it off or remove it any time in the Library.")
                    : L.T("The game's own files stay untouched — everything goes into the mods folder. " +
                      "Switch it off or remove it any time in the Library.");
        OpenPlan(plan, $"{Edition.DisplayName()} · {game}", L.T("Install"), () => RunJobAsync(job), note);
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
        ShowFootprint(plan);
        _planConfirm = run;
        IsPlanOpen = true;
        AppLog.Info($"plan shown: {plan.Title} — {string.Join(" | ", plan.Describe())}");
    }

    /// <summary>What the plan takes: files in archives, the copies it makes in mods, the room on the drive.</summary>
    private void ShowFootprint(InstallPlan plan)
    {
        PlanFootprint = "";
        PlanSpace = "";
        PlanTooBig = false;
        HasPlanFootprint = false;
        var game = GameFolder.Trim();
        if (game.Length == 0 || !Directory.Exists(game)) return;
        PlanFootprint f;
        try
        {
            f = plan.Footprint(TargetFor(game, Edition));
        }
        catch (Exception ex)
        {
            AppLog.Error("plan footprint failed", ex);
            return;
        }
        var parts = new List<string>();
        if (f.InArchives > 0)
            parts.Add(L.T($"Changes {f.InArchives} file(s) inside {f.Archives.Count} game archive(s)"));
        if (f.NewCopies.Count > 0)
            parts.Add((f.NewCopies.Count == 1 ? L.T($"copies {f.NewCopies[0]} into {ModsLayout.RootRel(game)} first (the game's own stay untouched)") : L.T($"copies {f.NewCopies.Count} archives into {ModsLayout.RootRel(game)} first (the game's own stay untouched)")));
        PlanFootprint = parts.Count == 0 ? "" : string.Join("; ", parts) + ".";
        if (f.Bytes >= 64L << 20)
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(game));
            PlanSpace = L.T($"Needs about {Size(f.Bytes)} on {drive}") +
                        (f.Free is { } free ? L.T($" — {Size(free)} free.") : ".") +
                        (f.TooBig ? L.T(" Not enough room: free some space first, or the install stops and takes itself back.") : "");
            PlanTooBig = f.TooBig;
        }
        HasPlanFootprint = PlanFootprint.Length > 0 || (PlanSpace.Length > 0 && !PlanTooBig);
    }

    private static string Size(long n) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        n >= 1L << 30 ? "{0:0.0} GB" : "{1:0} MB", n / 1073741824.0, n / 1048576.0);

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
        StageHint = L.T("Getting ready");
        var run = new PlanRun();
        Follow(run);
        try
        {
            await Task.Run(() => InstallExecutor.Run(plan, TargetFor(game, Edition), OnLog, run));
            ShowResult(true, done, game, game);
        }
        catch (OperationCanceledException)
        {
            ShowResult(false, L.T("Cancelled — nothing changed"), L.T("Every step done so far was taken back; the game is as it was."), null);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{plan.Title} failed", ex);
            FailLog(ex);
            ShowResult(false, L.T($"{plan.Title} — failed"), L.T($"{ex.Message}\n\nSee the log for details."), null);
        }
        finally
        {
            IsBuilding = false;
            Follow(null);
        }
        await AfterGameChangedAsync();
    }
}
