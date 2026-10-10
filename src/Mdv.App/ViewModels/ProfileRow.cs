using System.Globalization;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// One profile in the library: how many of its mods are on, whether the game is set up as it has it now, which of its
/// mods aren't installed. "No mods" is the built-in one (every mod off) — it can't be exported or deleted.
/// </summary>
public sealed partial class ProfileRow
{
    private readonly Action<ProfileRow> _switch, _export, _delete, _missing;

    /// <param name="installed">ids of the mods installed now</param>
    /// <param name="active">the mods are as it has them</param>
    /// <param name="last">it was switched to (or saved) last</param>
    public ProfileRow(ModSetup setup, bool builtIn, ISet<string> installed, bool active, bool last,
                      Action<ProfileRow> switchTo, Action<ProfileRow> export, Action<ProfileRow> delete, Action<ProfileRow> missing)
    {
        Setup = setup;
        IsBuiltIn = builtIn;
        IsActive = active;
        Changed = last && !active;
        Missing = [.. setup.Mods.Where(m => !installed.Contains(m.Id))];
        _switch = switchTo;
        _export = export;
        _delete = delete;
        _missing = missing;
    }

    public ModSetup Setup { get; }
    public string Name => Setup.Name;
    public bool IsBuiltIn { get; }
    public bool CanEdit => !IsBuiltIn;
    /// <summary>The game's mods are the way it has them.</summary>
    public bool IsActive { get; }
    /// <summary>Switched to last, but the mods changed since.</summary>
    public bool Changed { get; }
    public IReadOnlyList<SetupMod> Missing { get; }
    public bool HasMissing => Missing.Count > 0;
    public string MissingText => L.T($"{Missing.Count} not installed");
    public string MissingTip => L.T($"Not installed in this game: {string.Join(", ", Missing.Select(m => m.Name))} — click to install the first one again from its file.");

    public string Summary => IsBuiltIn
        ? L.T("Every mod switched off — the game as it came, with the mods kept for later")
        : L.T($"{Setup.EnabledCount} of {Setup.Mods.Count} mod(s) on · saved {Setup.When.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}");

    public string ModsTip => IsBuiltIn ? "" : string.Join("\n", Setup.Mods.OrderBy(m => !m.Enabled).Select(m => (m.Enabled ? "● " : "○ ") + m.Name));

    [RelayCommand] private void Switch() => _switch(this);
    [RelayCommand] private void Export() => _export(this);
    [RelayCommand] private void Delete() => _delete(this);
    [RelayCommand] private void InstallMissing() => _missing(this);
}

/// <summary>One recent change to the mods (a plan that went through) that can be undone.</summary>
public sealed partial class SnapshotRow(ModSnapshot snapshot, string names, bool latest, Action<SnapshotRow> undo)
{
    public ModSnapshot Snapshot { get; } = snapshot;
    public string Title => Snapshot.Title;
    public string When => Snapshot.When.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    /// <summary>The mods it touched, when its title doesn't name them.</summary>
    public string Names { get; } = names;
    public bool HasNames => Names.Length > 0;
    public bool IsLatest { get; } = latest;

    public string UndoTip => IsLatest
        ? L.T("Put the mods back the way they were before it (shows the plan first)")
        : L.T("Put the mods back the way they were before it — the later changes are undone too (shows the plan first)");

    [RelayCommand] private void Undo() => undo(this);
}
