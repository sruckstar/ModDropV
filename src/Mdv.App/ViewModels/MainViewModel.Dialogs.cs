using System.Collections.ObjectModel;
using Mdv.App.Services;
using Mdv.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>A GTA V installation offered in the game dialog.</summary>
public sealed record GameCandidate(string Path, GameEdition Edition)
{
    public string EditionName => Edition.DisplayName();
}

/// <summary>The pop-up dialogs: choosing the game (player flow) and the build log.</summary>
public sealed partial class MainViewModel
{
    private bool _gamesSearched;

    // ================================================================ game dialog

    [ObservableProperty] public partial bool IsGameDialogOpen { get; set; }

    public ObservableCollection<GameCandidate> GameCandidates { get; } = [];

    [ObservableProperty] public partial bool HasGameCandidates { get; set; }
    [ObservableProperty] public partial bool IsSearchingGames { get; set; }

    /// <summary>The game folder points at something we can install into.</summary>
    public bool HasGame
    {
        get
        {
            var game = GameFolder.Trim();
            if (game.Length == 0 || !Directory.Exists(game)) return false;
            return GameEditions.Detect(game) is not null || GameEditions.IsAmbiguous(game)
                   || Directory.Exists(Path.Combine(game, "mods"));
        }
    }

    /// <summary>Header chip: which game weapons go into.</summary>
    public string GameChipTitle => HasGame ? Edition.DisplayName() : L.T("Choose GTA V");

    public string GameChipDetail => HasGame ? GameFolder.Trim() : L.T("game folder not set");

    private void NotifyGameChanged()
    {
        OnPropertyChanged(nameof(HasGame));
        OnPropertyChanged(nameof(GameChipTitle));
        OnPropertyChanged(nameof(GameChipDetail));
    }

    partial void OnIsGameDialogOpenChanged(bool value)
    {
        if (value && !_gamesSearched) _ = SearchGamesAsync();
    }

    private async Task SearchGamesAsync()
    {
        _gamesSearched = true;
        IsSearchingGames = true;
        IReadOnlyList<GameInstall> found = [];
        try
        {
            found = await Task.Run(GameLocator.Find);
        }
        catch (Exception ex)
        {
            AppLog.Error("game search failed", ex);
        }
        GameCandidates.Clear();
        foreach (var g in found) GameCandidates.Add(new GameCandidate(g.Path, g.Edition));
        HasGameCandidates = GameCandidates.Count > 0;
        IsSearchingGames = false;
        AppLog.Info($"games found: {string.Join(" | ", found.Select(g => $"{g.Edition} {g.Path}"))}");
    }

    [RelayCommand]
    private void OpenGameDialog() => IsGameDialogOpen = true;

    [RelayCommand]
    private void CloseGameDialog() => IsGameDialogOpen = false;

    [RelayCommand]
    private void UseGame(GameCandidate game)
    {
        GameFolder = game.Path;
        IsGameDialogOpen = false;
    }

    // ================================================================ log dialog

    [ObservableProperty] public partial bool IsLogOpen { get; set; }

    [RelayCommand]
    private void OpenLog() => IsLogOpen = true;

    [RelayCommand]
    private void CloseLog() => IsLogOpen = false;
}
