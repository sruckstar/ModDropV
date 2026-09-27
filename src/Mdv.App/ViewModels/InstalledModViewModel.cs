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
    private readonly Action<InstalledModViewModel>? _raise;

    /// <param name="onTop">its versions of the files it shares with other mods are the ones the game gets</param>
    /// <param name="raise">puts it on top of the others (shows the plan first)</param>
    public InstalledModViewModel(InstalledMod mod, IReadOnlyList<string>? conflicts = null, bool onTop = true,
                                 Action<InstalledModViewModel>? raise = null)
    {
        Mod = mod;
        Enabled = mod.Enabled;
        Conflicts = conflicts ?? [];
        OnTop = onTop;
        _raise = raise;
    }

    public InstalledMod Mod { get; }
    public string Name => Mod.Name;
    public ModCategory Category => Mod.Category;
    public string CategoryName => Mod.Category.ShortLabel();

    /// <summary>Where it lives: the shared pack or its own dlcpack.</summary>
    public string Where => Mod.Category == ModCategory.Weapon
        ? Mod.Kind == ModKind.Merged ? L.T($"in the shared {Mod.Pack} pack") : L.T($"own dlcpack · {Mod.Pack}")
        : Mod.Pack;

    /// <summary>"from Glock17.zip · 12 Sep 2026" — where it came from and when.</summary>
    public string Origin
    {
        get
        {
            var parts = new List<string>();
            if (Mod.Source is { Length: > 0 } src) parts.Add(L.T($"from {src}"));
            if (Mod.Installed is { } when) parts.Add(L.Date(when.ToLocalTime()));
            if (Mod.ImportedFrom is { } other) parts.Add(L.T($"taken over from {other}"));
            return string.Join(" · ", parts);
        }
    }

    public bool HasOrigin => Origin.Length > 0;
    /// <summary>"  ·  from Glock17.zip · 12 Sep 2026" — follows <see cref="Where"/> on the row's second line.</summary>
    public string OriginSuffix => HasOrigin ? "  ·  " + Origin : "";
    public string Subline => Where + OriginSuffix;

    public string RemoveTip => Mod.Category == ModCategory.Weapon
        ? Mod.Kind == ModKind.Merged
            ? L.T($"Remove from the game: its models, metas and texts are taken out of {Mod.Pack}")
            : L.T($"Remove from the game: the whole «{Mod.Pack}» dlcpack is deleted")
        : L.T("Remove from the game: the game's own files (or the mod installed before it) come back");

    /// <summary>Other installed mods that change the same game files.</summary>
    public IReadOnlyList<string> Conflicts { get; }
    public bool HasConflicts => Conflicts.Count > 0;
    /// <summary>Where it shares files with other mods, the game gets its versions.</summary>
    public bool OnTop { get; }
    /// <summary>Another mod's versions win somewhere — it can be put on top.</summary>
    public bool CanRaise => HasConflicts && !OnTop && _raise is not null;
    public string ConflictsTip => !HasConflicts ? ""
        : OnTop ? L.T($"Changes the same game files as {string.Join(", ", Conflicts)} — its versions are on top, the game gets them.")
        : L.T($"Changes the same game files as {string.Join(", ", Conflicts)} — theirs are on top where they overlap. " +
          $"«On top» puts this one's first.");

    [RelayCommand]
    private void Raise() => _raise?.Invoke(this);

    public bool CanOpenFolder => Mod.Folder is { } f && Directory.Exists(f);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(Status), nameof(HasStatus))]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(Status), nameof(HasStatus), nameof(CanToggle))]
    public partial bool PendingRemove { get; set; }

    public bool CanToggle => !PendingRemove && Mod.CanSwitch;

    public string ToggleTip => Mod.CanSwitch
        ? L.T("Load this mod in the game")
        : L.T("It copied files into the game folder — it can be removed, but not switched off");
    public bool IsChanged => PendingRemove || Enabled != Mod.Enabled;

    /// <summary>What applying will do to this mod, or its current state if nothing.</summary>
    public string Status =>
        PendingRemove ? L.T("will be removed")
        : Enabled != Mod.Enabled ? (Enabled ? L.T("will be switched on") : L.T("will be switched off"))
        : Enabled ? "" : L.T("off");

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
