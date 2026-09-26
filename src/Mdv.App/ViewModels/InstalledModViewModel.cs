using System.Globalization;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// One mod installed into the game, as the library lists it. The checkbox and the trash
/// button only mark a change; nothing touches the game until the changes are applied.
/// </summary>
public sealed partial class InstalledModViewModel : ObservableObject
{
    public InstalledModViewModel(InstalledMod mod, IReadOnlyList<string>? conflicts = null)
    {
        Mod = mod;
        Enabled = mod.Enabled;
        Conflicts = conflicts ?? [];
    }

    public InstalledMod Mod { get; }
    public string Name => Mod.Name;
    public ModCategory Category => Mod.Category;
    public string CategoryName => Mod.Category.ShortName();

    /// <summary>Where it lives: the shared pack or its own dlcpack.</summary>
    public string Where => Mod.Category == ModCategory.Weapon
        ? Mod.Kind == ModKind.Merged ? $"in the shared {Mod.Pack} pack" : $"own dlcpack · {Mod.Pack}"
        : Mod.Pack;

    /// <summary>"from Glock17.zip · 12 Sep 2026" — where it came from and when.</summary>
    public string Origin
    {
        get
        {
            var parts = new List<string>();
            if (Mod.Source is { Length: > 0 } src) parts.Add($"from {src}");
            if (Mod.Installed is { } when) parts.Add(when.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture));
            if (Mod.ImportedFrom is { } other) parts.Add($"taken over from {other}");
            return string.Join(" · ", parts);
        }
    }

    public bool HasOrigin => Origin.Length > 0;
    /// <summary>"  ·  from Glock17.zip · 12 Sep 2026" — follows <see cref="Where"/> on the row's second line.</summary>
    public string OriginSuffix => HasOrigin ? "  ·  " + Origin : "";
    public string Subline => Where + OriginSuffix;

    public string RemoveTip => Mod.Category == ModCategory.Weapon
        ? Mod.Kind == ModKind.Merged
            ? $"Remove from the game: its models, metas and texts are taken out of {Mod.Pack}"
            : $"Remove from the game: the whole «{Mod.Pack}» dlcpack is deleted"
        : "Remove from the game: the game's own files (or the mod installed before it) come back";

    /// <summary>Other installed mods that change the same game files.</summary>
    public IReadOnlyList<string> Conflicts { get; }
    public bool HasConflicts => Conflicts.Count > 0;
    public string ConflictsTip => HasConflicts
        ? $"Changes the same game files as {string.Join(", ", Conflicts)}. The mod installed last wins."
        : "";

    public bool CanOpenFolder => Mod.Folder is { } f && Directory.Exists(f);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(Status), nameof(HasStatus))]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(Status), nameof(HasStatus), nameof(CanToggle))]
    public partial bool PendingRemove { get; set; }

    public bool CanToggle => !PendingRemove && Mod.CanSwitch;

    public string ToggleTip => Mod.CanSwitch
        ? "Load this mod in the game"
        : "It copied files into the game folder — it can be removed, but not switched off";
    public bool IsChanged => PendingRemove || Enabled != Mod.Enabled;

    /// <summary>What applying will do to this mod, or its current state if nothing.</summary>
    public string Status =>
        PendingRemove ? "will be removed"
        : Enabled != Mod.Enabled ? (Enabled ? "will be switched on" : "will be switched off")
        : Enabled ? "" : "off";

    public bool HasStatus => Status.Length > 0;

    /// <summary>Does it match the library's search text (name, where, origin)?</summary>
    public bool Matches(string query) =>
        query.Length == 0 ||
        Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        Where.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        Origin.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    [RelayCommand]
    private void ToggleRemove() => PendingRemove = !PendingRemove;

    [RelayCommand]
    private void OpenFolder()
    {
        if (Mod.Folder is null) return;
        try
        {
            Diagnostics.OpenInFileManager(Mod.Folder);
        }
        catch (Exception ex)
        {
            AppLog.Error("open mod folder failed", ex);
        }
    }

    public void Revert()
    {
        PendingRemove = false;
        Enabled = Mod.Enabled;
    }

    public ModChange? Change => IsChanged ? new ModChange(Mod.Id, Enabled, PendingRemove) : null;
}

/// <summary>A category chip over the library: "Weapons 4".</summary>
public sealed partial class LibraryFilterViewModel(ModCategory? category, string label, int count) : ObservableObject
{
    /// <summary>Null: every category.</summary>
    public ModCategory? Category { get; } = category;
    public string Label { get; } = label;
    public int Count { get; } = count;

    [ObservableProperty] public partial bool IsOn { get; set; }
}
