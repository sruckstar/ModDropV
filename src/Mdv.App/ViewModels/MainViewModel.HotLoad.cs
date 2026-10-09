using System.Diagnostics;
using Avalonia.Threading;
using Mdv.App.Services;
using Mdv.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// Installing into the running game (early access: works when ModDropV.HotLoad.dll is next to ModDrop V; without it the
/// install page offers to support the author on Patreon).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The author's Patreon page: supporters get the early-access download.</summary>
    public static readonly string PatreonUrl = "https://www.patreon.com/andre500mods/posts/loading-mods-is-171347331";

    // ================================================================ installing into the running game

    /// <summary>GTA V itself runs (checked every few seconds while the window is open).</summary>
    [ObservableProperty] public partial bool GameRunning { get; set; }
    /// <summary>The early-access loader (ModDropV.HotLoad.dll) is next to ModDrop V.</summary>
    [ObservableProperty] public partial bool HotLoadUnlocked { get; set; }

    private bool _finishing;

    /// <summary>The add-on on the install page goes into the running game: the button says so and the install goes through the loader.</summary>
    public bool LiveInstallReady => IsPlayer && GameRunning && HotLoadUnlocked && Edition == GameEdition.Legacy && SelectedPanel is AddonViewModel;

    /// <summary>
    /// The loader is missing: the install page offers to unlock installs into the running game — also while the game is
    /// closed, since closing it first is what everyone does by habit (GTA V Legacy, as the loader).
    /// </summary>
    public bool ShowLivePitch => ShowInstallPage && !HotLoadUnlocked && !ResultVisible && Edition == GameEdition.Legacy;

    public string LivePitchTitle => GameRunning ? L.T("GTA V is running") : L.T("No need to close GTA V");

    public string LivePitchText => GameRunning
        ? L.T("Support ModDrop V on Patreon and install mods while the game runs — they go straight into it, no restart.")
        : L.T("Support ModDrop V on Patreon and mods go straight into the running game — leave it open, no restart.");

    partial void OnGameRunningChanged(bool value) => NotifyLive();
    partial void OnHotLoadUnlockedChanged(bool value) => NotifyLive();

    private void NotifyLive()
    {
        OnPropertyChanged(nameof(LiveInstallReady));
        OnPropertyChanged(nameof(ShowLivePitch));
        OnPropertyChanged(nameof(LivePitchTitle));
        OnPropertyChanged(nameof(LivePitchText));
        OnPropertyChanged(nameof(BuildButtonText));
    }

    /// <summary>
    /// Watch the game while the window is open: whether it runs (the install button and the Patreon note follow it), and
    /// once it closes, write what installs into it left waiting (dlclist.xml lines, limits).
    /// </summary>
    public IDisposable StartGameWatch()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) => await WatchGameAsync();
        timer.Start();
        _ = WatchGameAsync();
        return new Stopper(timer);
    }

    private sealed class Stopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }

    private async Task WatchGameAsync()
    {
        bool runs;
        try
        {
            runs = await Task.Run(OnlineMode.GameRuns);
        }
        catch (Exception ex)
        {
            AppLog.Error("game watch failed", ex);
            return;
        }
        GameRunning = runs;
        HotLoadUnlocked = HotLoad.IsUnlocked(AppPaths.Root);
        if (runs) _roomMadeFor = null;                   // once it closes, check the room again
        var game = GameFolder.Trim();
        if (runs || _finishing || IsBuilding || game.Length == 0 || !Directory.Exists(game)) return;
        bool finish = LiveInstall.HasPending(game);
        bool room = HotLoadUnlocked && Edition == GameEdition.Legacy && _roomMadeFor != game;
        if (!finish && !room) return;
        _finishing = true;
        try
        {
            var target = TargetFor(game, Edition);
            if (finish)
            {
                bool wrote = await Task.Run(() => LiveInstall.Finish(target, OnLog));
                AppLog.Info($"finished installs made while the game ran: {wrote}");
                if (wrote) await RefreshInstalledAsync();
            }
            if (room)
            {
                _roomMadeFor = game;
                bool made = await Task.Run(() => LiveInstall.Prepare(target, OnLog));
                AppLog.Info($"room for installs into the running game: {(made ? "made" : "there")}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("finishing installs made while the game ran failed", ex);
        }
        finally
        {
            _finishing = false;
        }
    }

    /// <summary>The game folder the room for installs into the running game was checked in since the game last closed.</summary>
    private string? _roomMadeFor;

    [RelayCommand]
    private void OpenPatreon()
    {
        try
        {
            Process.Start(new ProcessStartInfo(PatreonUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("open patreon failed", ex);
        }
    }
}
