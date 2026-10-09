using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// Player flow: "Play GTA Online" — every mod of the game folder goes into ModDropV-Stash, the
/// game starts clean; "Bring mods back" puts it all where it was (<see cref="OnlineMode"/>).
/// Both show their plan first, like every other change to the game.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The selected game's mods are put away.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlineButtonText), nameof(OnlineTip))]
    public partial bool IsOnlineMode { get; set; }

    public string OnlineButtonText => IsOnlineMode ? L.T("Bring mods back") : L.T("Play GTA Online");

    public string OnlineTip => IsOnlineMode
        ? L.T("Put every mod back where it was and switch BattlEye off — the game starts with mods again")
        : L.T("Move every mod, loader and script hook out of the game folder and switch BattlEye back on for a clean GTA Online — one click brings them back");

    private bool CanToggleOnline() => !IsBuilding;

    [RelayCommand(CanExecute = nameof(CanToggleOnline))]
    private async Task ToggleOnline()
    {
        ResultVisible = false;
        if (!HasGame)
        {
            IsGameDialogOpen = true;
            return;
        }
        var game = GameFolder.Trim();
        if (OnlineMode.IsOn(game)) ReviewBringBack(game);
        else await ReviewPutAway(game);
    }

    private async Task ReviewPutAway(string game)
    {
        OnlineScan scan;
        try
        {
            scan = await Task.Run(() => OnlineMode.Scan(game));
        }
        catch (Exception ex)
        {
            AppLog.Error("scanning the game for mods failed", ex);
            ShowResult(false, L.T("Couldn't read the game folder"), ex.Message, null);
            return;
        }
        if (scan.Items.Count == 0)
        {
            bool switched = false;
            try
            {
                switched = BattlEye.TurnOn(game, OnLog);
            }
            catch (Exception ex)
            {
                AppLog.Error("switching BattlEye on failed", ex);
                scan.Warnings.Add(L.T($"Couldn’t switch BattlEye back on: {ex.Message} — take {BattlEye.Switch} out of {BattlEye.FileName} in the game folder."));
            }
            var text = scan.Warnings.Count > 0 ? string.Join("\n", scan.Warnings)
                : switched ? L.T($"BattlEye is back on ({BattlEye.Switch} taken out of {BattlEye.FileName}) — the game is ready for GTA Online.")
                : L.T("The game is clean for GTA Online as it is.");
            ShowResult(scan.Warnings.Count == 0, L.T("No mods in the game folder"), text, null);
            return;
        }
        var plan = new InstallPlan { Title = L.T("Getting the game ready for GTA Online") };
        foreach (var group in scan.Items.GroupBy(i => i.Kind))
            plan.Add(new ActionOp(PutAwayStep(group.Key, group.Select(i => Shown(i.Name, i.IsFolder)).ToList()), _ => { }));
        if (BattlEye.IsOff(game))
            plan.Add(new ActionOp(L.T($"Switch BattlEye back on — GTA Online needs it ({BattlEye.Switch} comes out of {BattlEye.FileName})"), _ => { }));
        plan.Warnings.AddRange(scan.Warnings);
        if (OnlineMode.RunningGame() is { Count: > 0 } running)
            plan.Warnings.Insert(0, L.T($"GTA V is running ({string.Join(", ", running)}) — close the game first."));
        OpenPlan(plan, $"{Edition.DisplayName()} · {game}", L.T("Put mods away"),
                 () => RunOnlineAsync(game, log => OnlineMode.PutAway(game, Edition, log), L.T("Putting the mods away…"),
                                      L.T("Ready for GTA Online"),
                                      L.T($"The game starts without mods and with BattlEye on. They wait in {OnlineMode.StashName} — “Bring mods back” when you’re done."),
                                      L.T("Couldn’t put the mods away"), ""),
                 L.T($"Nothing is deleted: it all goes into {OnlineMode.StashName} in the game folder — on the same drive, so even " +
                     $"gigabytes move in a moment — and “Bring mods back” puts it where it was. The game’s own files aren’t touched. " +
                     $"If you switched BattlEye off in the Rockstar Games Launcher or in Steam’s launch options, switch it back on there too."));
    }

    private void ReviewBringBack(string game)
    {
        var manifest = OnlineMode.Manifest(game) ?? new StashManifest();
        var plan = new InstallPlan { Title = L.T("Bringing the mods back") };
        if (manifest.Items.Count == 0)
            plan.Add(new ActionOp(L.T($"Move everything in {OnlineMode.StashName} back into the game folder"), _ => { }));
        foreach (var group in manifest.Items.GroupBy(i => i.Kind).OrderBy(g => g.Key))
            plan.Add(new ActionOp(L.T($"Move back: {List(group.Select(i => Shown(i.Name, i.Folder)).ToList())}"), _ => { }));
        if (BattlEye.IsGame(game) && !BattlEye.IsOff(game))
            plan.Add(new ActionOp(L.T($"Switch BattlEye off so the game starts with mods ({BattlEye.Switch} in {BattlEye.FileName})"), _ => { }));
        if (OnlineMode.RunningGame() is { Count: > 0 } running)
            plan.Warnings.Add(L.T($"GTA V is running ({string.Join(", ", running)}) — close the game first."));
        OpenPlan(plan, $"{Edition.DisplayName()} · {game}", L.T("Bring mods back"),
                 () => RunOnlineAsync(game, log => OnlineMode.Restore(game, log), L.T("Bringing the mods back…"), L.T("Mods are back"),
                                      L.T("Everything is where it was and BattlEye is off; the game starts with mods again."),
                                      L.T("Couldn’t bring every mod back"), "\n\n" + L.T($"What didn’t move back is still in {OnlineMode.StashName} — close whatever uses it and try again.")),
                 L.T("Everything goes back where it was. If the game was updated in the meantime, the game status shows what " +
                     "needs a look (copies in mods, ScriptHookV)."));
    }

    private static string PutAwayStep(StashKind kind, List<string> names) => kind switch
    {
        StashKind.ModsFolder => L.T("Move the mods folder (add-on packs, archive copies) — moved, not copied"),
        StashKind.Loader => L.T($"Move the mod loaders: {List(names)}"),
        StashKind.ScriptHook => L.T($"Move ScriptHookV, ScriptHookVDotNet and the scripts: {List(names)}"),
        StashKind.Plugin => L.T($"Move the plugins: {List(names)}"),
        _ => L.T($"Move the other mod files: {List(names)}"),
    };

    private static string Shown(string name, bool folder) => name.Replace('/', '\\') + (folder ? "\\" : "");

    private static string List(List<string> names) =>
        names.Count <= 8 ? string.Join(", ", names) : string.Join(", ", names.Take(7)) + L.T($" and {names.Count - 7} more");

    private async Task RunOnlineAsync(string game, Action<Action<string>> work, string stage, string done, string doneText,
                                      string failTitle, string failHint)
    {
        ResultVisible = false;
        StartLog([]);
        IsBuilding = true;
        StageStatus = stage;
        StageHint = L.T("Moving files");
        try
        {
            await Task.Run(() => work(OnLog));
            var stash = OnlineMode.StashDir(game);
            bool left = Directory.Exists(stash) && !OnlineMode.IsOn(game);            // leftovers of a bring-back
            ShowResult(true, done, left ? L.T($"{doneText} Some files stayed in {OnlineMode.StashName} — see the log.") : doneText,
                       Directory.Exists(stash) ? stash : game);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{stage} failed", ex);
            FailLog(ex);
            ShowResult(false, failTitle, ex.Message + failHint, null);
        }
        finally
        {
            IsBuilding = false;
        }
        await AfterGameChangedAsync();
    }
}
