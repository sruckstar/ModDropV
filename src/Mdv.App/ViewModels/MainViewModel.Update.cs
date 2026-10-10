using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using Mdv.App.Services;
using Mdv.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>What the update banner above the page says.</summary>
public enum UpdateBanner
{
    None,
    /// <summary>A new version is downloaded: restart to update.</summary>
    Ready,
    /// <summary>This start is the first of a new version: what's new.</summary>
    Updated,
    /// <summary>The downloaded version couldn't be put in.</summary>
    Failed,
}

/// <summary>
/// ModDrop V updates itself (<see cref="AppUpdate"/>): at start the latest GitHub release is checked, a newer one is
/// downloaded in the background and a banner offers to restart into it. No telemetry — only the request to GitHub.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private StagedUpdate? _update;
    private bool _applyingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateBanner), nameof(IsUpdateReady), nameof(IsUpdateFailed))]
    public partial UpdateBanner UpdateBannerKind { get; set; }

    public bool ShowUpdateBanner => UpdateBannerKind != UpdateBanner.None;
    public bool IsUpdateReady => UpdateBannerKind == UpdateBanner.Ready;
    public bool IsUpdateFailed => UpdateBannerKind == UpdateBanner.Failed;

    [ObservableProperty] public partial string UpdateTitle { get; set; } = "";
    [ObservableProperty] public partial string UpdateDetail { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateNotes))]
    public partial string UpdateNotes { get; set; } = "";

    public bool HasUpdateNotes => UpdateNotes.Length > 0;

    private string _updatePage = AppUpdate.ReleasesPage;

    /// <summary>A downloaded version waits for a restart (a dot on the update button).</summary>
    [ObservableProperty] public partial bool HasUpdateWaiting { get; set; }

    /// <summary>The update menu's line: the version, checking, downloading, the latest, an error.</summary>
    [ObservableProperty] public partial string UpdateStatus { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesNowCommand))]
    public partial bool IsCheckingUpdate { get; set; }

    public string VersionText => L.T($"ModDrop V {AppUpdate.Current.ToString(3)}");

    public bool CheckUpdatesAtStart
    {
        get => Settings.CheckUpdates;
        set
        {
            if (Settings.CheckUpdates == value) return;
            Settings.CheckUpdates = value;
            Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>The new build is in place: the app starts it and exits.</summary>
    public event Action? RestartRequested;

    /// <summary>
    /// At start: after an update — what's new and the old build's files cleaned up; a version downloaded earlier — its
    /// banner; otherwise ask GitHub (when allowed, at most every <see cref="CheckEvery"/>) and download a newer version.
    /// </summary>
    public async Task StartUpdatesAsync(Version? updatedFrom)
    {
        try
        {
            if (updatedFrom is not null)
            {
                var notes = AppUpdate.NotesOf(AppUpdate.Current);
                UpdateTitle = L.T($"Updated to ModDrop V {AppUpdate.Current.ToString(3)}");
                UpdateDetail = L.T("Your mods and settings stay as they were.");
                UpdateNotes = notes?.Notes ?? "";
                _updatePage = notes?.Page ?? AppUpdate.ReleasesPage;
                UpdateBannerKind = UpdateBanner.Updated;
            }
            await Task.Run(() =>
            {
                AppUpdate.Prune();
                // the old build has to exit before its renamed files can go
                for (int i = 0; i < (updatedFrom is null ? 1 : 30) && AppUpdate.CleanupOld(AppPaths.Root) > 0; i++)
                    Thread.Sleep(1000);
            });
            if (AppUpdate.Ready() is { } ready)
            {
                ShowReady(ready, manual: false);
                return;
            }
            UpdateStatus = L.T("Checks for a new version at start.");
            if (!Settings.CheckUpdates) return;
            if (Settings.LastUpdateCheck is { } last && DateTime.UtcNow - last < CheckEvery) return;
            await CheckAsync(manual: false);
        }
        catch (Exception ex)
        {
            AppLog.Error("update check at start failed", ex);
        }
    }

    private bool CanCheckUpdates() => !IsCheckingUpdate;

    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private async Task CheckUpdatesNow()
    {
        if (AppUpdate.Ready() is { } ready)
            ShowReady(ready, manual: true);
        else
            await CheckAsync(manual: true);
    }

    private async Task CheckAsync(bool manual)
    {
        IsCheckingUpdate = true;
        UpdateStatus = L.T("Checking for a new version…");
        try
        {
            var release = await AppUpdate.LatestAsync();
            Settings.LastUpdateCheck = DateTime.UtcNow;
            Settings.Save();
            if (release is null || release.Version <= AppUpdate.Current)
            {
                UpdateStatus = L.T("You have the latest version.");
                return;
            }
            var ver = release.Version.ToString(3);
            if (!manual && Settings.SkipVersion == ver)
            {
                UpdateStatus = L.T($"Version {ver} is skipped — “Check now” gets it.");
                return;
            }
            AppLog.Info($"downloading update {ver}");
            UpdateStatus = L.T($"Downloading ModDrop V {ver}…");
            int shown = -1;
            var staged = await Task.Run(() => AppUpdate.DownloadAsync(release, (have, total) =>
            {
                int pct = total > 0 ? (int)(have * 100 / total) : 0;
                if (pct == shown) return;
                shown = pct;
                Dispatcher.UIThread.Post(() => UpdateStatus = L.T($"Downloading ModDrop V {ver}… {pct}%"));
            }));
            ShowReady(staged, manual);
        }
        catch (Exception ex)
        {
            AppLog.Error("update check failed", ex);
            UpdateStatus = L.T($"Couldn’t check for a new version: {ex.Message}");
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private void ShowReady(StagedUpdate staged, bool manual)
    {
        _update = staged;
        _updatePage = staged.Page;
        var ver = staged.Version.ToString(3);
        HasUpdateWaiting = true;
        UpdateStatus = L.T($"ModDrop V {ver} is downloaded — restart to update.");
        if (!manual && Settings.SkipVersion == ver) return;
        UpdateTitle = L.T($"ModDrop V {ver} is ready");
        UpdateDetail = staged.HotLoadProtocol != HotLoad.Protocol && HotLoad.IsUnlocked(AppPaths.Root)
            ? L.T("This version needs a new early-access build to install mods into the running game — get it on Patreon. " +
                  "Everything else works after the update.")
            : L.T("Restart to update — your mods and settings stay. A running install finishes first.");
        UpdateNotes = staged.Notes;
        UpdateBannerKind = UpdateBanner.Ready;
    }

    private bool CanRestartToUpdate() => !IsBuilding && !_finishing && !_applyingUpdate;

    [RelayCommand(CanExecute = nameof(CanRestartToUpdate))]
    private async Task RestartToUpdate()
    {
        if (_update is not { } staged) return;
        _applyingUpdate = true;
        RestartToUpdateCommand.NotifyCanExecuteChanged();
        try
        {
            AppLog.Info($"applying update {staged.Version.ToString(3)} from {staged.Dir}");
            var result = await Task.Run(() => AppUpdate.Apply(staged.Dir, AppPaths.Root, AppLog.Info));
            if (result.Kind == ApplyKind.NoAccess) result = await Task.Run(() => ApplyElevated(staged.Dir));
            if (result.Kind == ApplyKind.Done)
            {
                RestartRequested?.Invoke();
                return;
            }
            UpdateTitle = L.T("The update didn’t go in");
            UpdateDetail = L.T($"{result.Message} ModDrop V stays as it was; you can also download the new version from its release page.");
            UpdateBannerKind = UpdateBanner.Failed;
        }
        catch (Exception ex)
        {
            AppLog.Error("applying the update failed", ex);
            UpdateTitle = L.T("The update didn’t go in");
            UpdateDetail = ex.Message;
            UpdateBannerKind = UpdateBanner.Failed;
        }
        finally
        {
            _applyingUpdate = false;
            RestartToUpdateCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The program folder needs admin rights (Program Files): the same exe puts the update in, elevated.</summary>
    private static ApplyResult ApplyElevated(string staged)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppPaths.Root, AppUpdate.ExeName))
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--apply-update \"{staged}\"",
            });
            if (p is null) return new(ApplyKind.Failed, L.T("The update couldn’t start."));
            p.WaitForExit();
            return p.ExitCode == 0
                ? new(ApplyKind.Done, "")
                : new(ApplyKind.Failed, L.T("The update couldn’t be put in (see the log)."));
        }
        catch (Win32Exception)                          // the user said no to the admin prompt
        {
            return new(ApplyKind.NoAccess, L.T("Updating ModDrop V in this folder needs administrator rights."));
        }
    }

    /// <summary>Not now: the banner goes away until the next start.</summary>
    [RelayCommand]
    private void DismissUpdate() => UpdateBannerKind = UpdateBanner.None;

    [RelayCommand]
    private void SkipUpdate()
    {
        if (_update is { } u)
        {
            Settings.SkipVersion = u.Version.ToString(3);
            Settings.Save();
        }
        UpdateBannerKind = UpdateBanner.None;
    }

    [RelayCommand]
    private void OpenUpdatePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_updatePage) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("open release page failed", ex);
        }
    }
}
