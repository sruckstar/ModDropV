using System.Collections.ObjectModel;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>A line of the game status card.</summary>
public sealed record StatusRow(string Label, string Value, StatusLevel Level, string? Detail)
{
    public bool IsOk => Level == StatusLevel.Ok;
    public bool IsWarning => Level == StatusLevel.Warning;
    public bool IsInfo => Level == StatusLevel.Info;
}

/// <summary>
/// Player flow: the state of the selected game — mods folder and its loader, ASI loader,
/// ScriptHookV / ScriptHookVDotNet, DLC packs, archive copies a game update left behind
/// (with the button that renews them).
/// </summary>
public sealed partial class MainViewModel
{
    private int _statusGeneration;
    private (string Game, MountedDlcs Dlcs)? _mounted;
    private List<string> _staleCopies = [];

    public ObservableCollection<StatusRow> StatusRows { get; } = [];

    [ObservableProperty] public partial bool HasStatus { get; set; }
    /// <summary>"Ready for mods" / "2 things to check" — the pill in the page bar.</summary>
    [ObservableProperty] public partial string StatusSummary { get; set; } = "";
    [ObservableProperty] public partial bool StatusHasWarnings { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCopiesCommand))]
    public partial bool HasStaleCopies { get; set; }

    [ObservableProperty] public partial string StaleCopiesText { get; set; } = "";

    /// <summary>The game's file index is built (called by the background warm-up): the status gets the DLC count.</summary>
    public void OnGameIndexReady(string game, MountedDlcs dlcs)
    {
        _mounted = (game, dlcs);
        if (string.Equals(game, GameFolder.Trim(), StringComparison.OrdinalIgnoreCase)) _ = RefreshStatusAsync();
    }

    /// <summary>Read the selected game's status (player mode only).</summary>
    internal async Task RefreshStatusAsync()
    {
        int gen = ++_statusGeneration;
        var game = IsPlayer && HasGame ? GameFolder.Trim() : null;
        IsOnlineMode = game is not null && OnlineMode.IsOn(game);
        if (game is null)
        {
            StatusRows.Clear();
            HasStatus = false;
            StatusHasWarnings = false;
            StatusSummary = L.T("No game chosen");
            HasStaleCopies = false;
            return;
        }
        var dlcs = _mounted is { } m && string.Equals(m.Game, game, StringComparison.OrdinalIgnoreCase) ? m.Dlcs : null;
        var edition = Edition;
        GameStatusReport? report = null;
        try
        {
            report = await Task.Run(() => GameStatus.Read(game, edition, dlcs));
        }
        catch (Exception ex)
        {
            AppLog.Error("reading the game status failed", ex);
        }
        if (gen != _statusGeneration) return;
        StatusRows.Clear();
        if (report is null)
        {
            HasStatus = false;
            StatusSummary = L.T("Status unknown");
            return;
        }
        foreach (var i in report.Items) StatusRows.Add(new StatusRow(i.Label, i.Value, i.Level, i.Detail));
        HasStatus = true;
        int warnings = report.Items.Count(i => i.Level == StatusLevel.Warning);
        StatusHasWarnings = warnings > 0;
        StatusSummary = IsOnlineMode ? L.T("Clean for GTA Online") : warnings == 0 ? L.T("Game ready for mods") : warnings == 1 ? L.T("1 thing to check") : L.T($"{warnings} things to check");
        _staleCopies = report.StaleCopies;
        HasStaleCopies = _staleCopies.Count > 0;
        StaleCopiesText = HasStaleCopies
            ? L.T($"The game was updated after these were copied: {string.Join(", ", _staleCopies.Select(a => "mods/" + a))}. Old copies are a common cause of crashes.")
            : "";
    }

    [RelayCommand]
    private Task RefreshStatus() => RefreshStatusAsync();

    private bool CanUpdateCopies() => HasStaleCopies && !IsBuilding;

    /// <summary>Fresh copies of the stale archives, with every mod's changes put back — after a look at the plan.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateCopies))]
    private void UpdateCopies()
    {
        var game = GameFolder.Trim();
        var plan = new InstallPlan { Title = L.T("Updating the copies in mods") }.Add(new RefreshCopiesOp(_staleCopies.ToList()));
        OpenPlan(plan, $"{Edition.DisplayName()} · {game}", L.T("Update copies"),
                 () => RunPlanAsync(plan, L.T("Updating the copies in mods…"), L.T("Copies in mods updated")),
                 L.T("Each copy is taken fresh from the updated game; the files your mods changed in it are put back."));
    }
}
