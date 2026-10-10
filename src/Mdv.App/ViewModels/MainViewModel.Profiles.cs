using System.Collections.ObjectModel;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// Player flow: profiles and undo — the mods as they stand are kept under a name (which ones are on, the load order, the pins)
/// and switched back to in one plan; "No mods" switches every mod off. The last changes to the mods (installs, removals,
/// switches, order, pins) are listed and can be undone; the result banner offers it right after a change.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The profiles and the recent changes as read (off the UI thread).</summary>
    internal sealed record ProfileData(List<ModSetup> Profiles, string? Last, ModSetup Now, List<ModSnapshot> Snapshots,
                                       Dictionary<string, string> Names);

    /// <summary>The library shows the profiles and the recent changes.</summary>
    [ObservableProperty] public partial bool IsProfilesView { get; set; }

    public ObservableCollection<ProfileRow> Profiles { get; } = [];
    public ObservableCollection<SnapshotRow> Snapshots { get; } = [];

    [ObservableProperty] public partial bool HasSnapshots { get; set; }
    /// <summary>"3 of 7 mods on — profile «Cars»" / "…not saved as a profile".</summary>
    [ObservableProperty] public partial string ProfileNow { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    public partial string NewProfileName { get; set; } = "";

    /// <summary>The change the result banner offers to undo (the run that just ended made it).</summary>
    private ModSnapshot? _undoable;
    [ObservableProperty] public partial bool ResultCanUndo { get; set; }

    /// <summary>Profile file pickers supplied by the view: (title, suggested name) → path to write, (title) → file to read.</summary>
    public Func<string, string, Task<string?>>? PickProfileSave { get; set; }
    public Func<string, Task<string?>>? PickProfileOpen { get; set; }

    partial void OnIsProfilesViewChanged(bool value)
    {
        if (value)
        {
            IsOrderView = false;
            IsConflictsView = false;
        }
        OnPropertyChanged(nameof(IsModsView));
    }

    private static ProfileData ReadProfiles(InstallTarget target, IReadOnlyList<InstalledMod> listed)
    {
        var reg = ModRegistry.Load(target.GameDir);
        var names = reg.Mods.ToDictionary(m => m.Id, m => m.Name.Length > 0 ? m.Name : m.Id, StringComparer.Ordinal);
        return new ProfileData(reg.Profiles, reg.Profile, ModProfiles.Current(target, listed: listed), reg.Snapshots, names);
    }

    internal void ShowProfiles(ProfileData? data)
    {
        Profiles.Clear();
        Snapshots.Clear();
        if (data is null)
        {
            HasSnapshots = false;
            ProfileNow = "";
            return;
        }
        var installed = data.Now.Mods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var vanilla = ModProfiles.Vanilla;
        Profiles.Add(new ProfileRow(vanilla, true, installed, ModProfiles.Matches(vanilla, data.Now) && data.Now.Mods.Count > 0, false,
                                    SwitchProfile, ExportProfile, DeleteProfile, InstallMissing));
        ProfileRow? active = null;
        foreach (var p in data.Profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new ProfileRow(p, false, installed, ModProfiles.Matches(p, data.Now), p.Name == data.Last,
                                     SwitchProfile, ExportProfile, DeleteProfile, InstallMissing);
            if (row.IsActive) active ??= row;
            Profiles.Add(row);
        }
        var on = L.T($"{data.Now.EnabledCount} of {data.Now.Mods.Count} mod(s) on");
        ProfileNow = active is not null ? L.T($"{on} — as profile «{active.Name}» has them")
            : data.Last is { } last && data.Profiles.Exists(p => p.Name == last) ? L.T($"{on} — changed since profile «{last}»")
            : L.T($"{on} — not saved as a profile");

        for (int i = 0; i < data.Snapshots.Count; i++)
        {
            var s = data.Snapshots[i];
            string Name(string id) => data.Names.GetValueOrDefault(id) ?? s.Before.Mods.FirstOrDefault(m => m.Id == id)?.Name ?? id;
            var touched = s.Added.Concat(s.Updated).Concat(s.Removed).Select(Name).Where(n => !s.Title.Contains(n, StringComparison.Ordinal)).ToList();
            Snapshots.Add(new SnapshotRow(s, string.Join(", ", touched), i == 0, UndoChange));
        }
        HasSnapshots = Snapshots.Count > 0;
    }

    private bool CanSaveProfile() => NewProfileName.Trim().Length > 0;

    /// <summary>Keep the mods as they stand under the typed name (one of the same name is replaced).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfile()
    {
        var game = InstalledGameDir();
        if (game is null || IsBuilding) return;
        var name = NewProfileName.Trim();
        bool replaced = Profiles.Any(p => !p.IsBuiltIn && p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        try
        {
            var saved = await Task.Run(() => ModProfiles.Save(TargetFor(game, Edition), name));
            NewProfileName = "";
            ShowResult(true, replaced ? L.T($"Profile «{saved.Name}» updated") : L.T($"Profile «{saved.Name}» saved"),
                       L.T($"{saved.EnabledCount} of {saved.Mods.Count} mod(s) on, their load order and pins — switch back to it any time."), null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowResult(false, L.T("Couldn't save the profile"), ex.Message, null);
        }
        await RefreshInstalledAsync();
    }

    /// <summary>Switch to a profile: the plan first (its mods on, the rest off, its order and pins).</summary>
    private void SwitchProfile(ProfileRow row)
    {
        var game = InstalledGameDir();
        if (game is null || IsBuilding) return;
        SetupPlan sp;
        try
        {
            sp = ModProfiles.PlanSwitch(TargetFor(game, Edition), row.Setup);
        }
        catch (Exception ex)
        {
            AppLog.Error("planning a profile switch failed", ex);
            ShowResult(false, L.T($"Couldn't switch to «{row.Name}»"), ex.Message, null);
            return;
        }
        var missing = MissingNote(sp);
        if (sp.Empty)
        {
            ShowResult(true, L.T($"The mods are as «{row.Name}» has them already"), missing ?? L.T("Nothing to change."), null);
            return;
        }
        OpenPlan(sp.Plan, $"{Edition.DisplayName()} · {game}", L.T("Switch"), async () =>
            {
                await RunPlanAsync(sp.Plan, L.T("Switching mods…"), L.T($"Profile «{row.Name}» is on"));
                if (missing is not null && ResultOk) ResultDetail = missing;
            },
            L.T("Mods switched off keep their files aside — switching back takes no download and no extra room."));
    }

    /// <summary>Undo a change (and every later one): the plan first.</summary>
    private void UndoChange(SnapshotRow row) => ReviewUndo(row.Snapshot);

    [RelayCommand]
    private void UndoResult()
    {
        ResultCanUndo = false;
        if (_undoable is { } s) ReviewUndo(s);
    }

    private void ReviewUndo(ModSnapshot snapshot)
    {
        var game = InstalledGameDir();
        if (game is null || IsBuilding) return;
        SetupPlan sp;
        try
        {
            sp = ModProfiles.PlanUndo(TargetFor(game, Edition), snapshot);
        }
        catch (Exception ex)
        {
            AppLog.Error("planning an undo failed", ex);
            ShowResult(false, L.T($"Couldn't undo «{snapshot.Title}»"), ex.Message, null);
            return;
        }
        var missing = MissingNote(sp);
        if (sp.Empty)
        {
            ShowResult(true, L.T("Nothing to undo"), missing ?? L.T("The mods are the way they were before it already."), null);
            return;
        }
        OpenPlan(sp.Plan, $"{Edition.DisplayName()} · {game}", L.T("Undo"), async () =>
            {
                await RunPlanAsync(sp.Plan, L.T("Undoing…"), L.T($"«{snapshot.Title}» undone"));
                if (missing is not null && ResultOk) ResultDetail = missing;
            },
            L.T("Mods installed since go, switches, the load order and pins come back. A removed mod only comes back installed again from its file."));
    }

    /// <summary>What a switch can't bring in, for the result banner (null: nothing missing).</summary>
    private static string? MissingNote(SetupPlan sp) => sp.Missing.Count == 0 ? null
        : L.T($"Not installed: {string.Join(", ", sp.Missing.Select(m => m.Name))} — the profile’s “not installed” mark installs them again from their files.");

    private async void ExportProfile(ProfileRow row)
    {
        if (row.IsBuiltIn || PickProfileSave is null) return;
        var path = await PickProfileSave(L.T("Export profile"), row.Name + ProfileFile.Extension);
        if (path is null) return;
        try
        {
            ModProfiles.Export(row.Setup, Edition, path);
            ShowResult(true, L.T($"Profile «{row.Name}» exported"),
                       L.T("The file lists its mods and the files they were installed from — import it into another game or share it."), Path.GetDirectoryName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowResult(false, L.T("Couldn't export the profile"), ex.Message, null);
        }
    }

    [RelayCommand]
    private async Task ImportProfile()
    {
        var game = InstalledGameDir();
        if (game is null || PickProfileOpen is null || IsBuilding) return;
        var path = await PickProfileOpen(L.T("Import profile"));
        if (path is null) return;
        try
        {
            var target = TargetFor(game, Edition);
            var (profile, missing) = await Task.Run(() =>
            {
                var p = ModProfiles.Import(path);
                ModProfiles.Add(game, p);
                return (p, ModProfiles.Plan(target, p, false, "").Missing);
            });
            ShowResult(true, L.T($"Profile «{profile.Name}» imported"), missing.Count == 0
                ? L.T($"All {profile.Mods.Count} of its mods are installed — switch to it in the list.")
                : L.T($"Not installed here: {string.Join(", ", missing.Select(m => m.Name))} — its “not installed” mark installs them from their files."), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowResult(false, L.T("Couldn't import the profile"), ex.Message, null);
        }
        await RefreshInstalledAsync();
    }

    private async void DeleteProfile(ProfileRow row)
    {
        var game = InstalledGameDir();
        if (row.IsBuiltIn || game is null || IsBuilding) return;
        try
        {
            ModProfiles.Delete(game, row.Name);
            ShowResult(true, L.T($"Profile «{row.Name}» deleted"), L.T("The mods stay as they are."), null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowResult(false, L.T("Couldn't delete the profile"), ex.Message, null);
        }
        await RefreshInstalledAsync();
    }

    /// <summary>Install the first of a profile's missing mods again from its file — through the Install page, like a drop.</summary>
    private async void InstallMissing(ProfileRow row)
    {
        if (row.Missing.Count == 0 || IsBuilding) return;
        var mod = row.Missing.FirstOrDefault(m => m.Source?.Path is { } p && (File.Exists(p) || Directory.Exists(p))) ?? row.Missing[0];
        var source = mod.Source?.Path is { } there && (File.Exists(there) || Directory.Exists(there)) ? there : null;
        if (source is null)
        {
            if (PickArchives is null) return;
            var picked = await PickArchives(L.T($"The file «{mod.Name}» was installed from"));
            if (picked.Count == 0) return;
            source = picked[0];
        }
        AppLog.Info($"profile «{row.Name}»: installing {mod.Id} from {source}");
        IsLibraryPage = false;
        await LoadPlayerSourceAsync([source]);
    }

    /// <summary>The change the run that just ended made, if it can be undone (for the result banner).</summary>
    private void OfferUndo(bool ok, DateTime? since)
    {
        _undoable = null;
        ResultCanUndo = false;
        if (!ok || since is null || !IsPlayer || InstalledGameDir() is not { } game) return;
        try
        {
            if (ModRegistry.Load(game).Snapshots.FirstOrDefault() is { } s && s.When >= since)
            {
                _undoable = s;
                ResultCanUndo = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no undo offered
        }
    }
}
