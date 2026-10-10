using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// One file several mods have a version of: where it is, whose version the game gets, the others. Clicking another mod's
/// chip marks it as the file's winner (a pin, stronger than the load order); "back to the order" drops a pin. Nothing
/// touches the game until the marked pins are applied.
/// </summary>
public sealed partial class ConflictRow : ObservableObject
{
    private readonly Func<string, int> _rank;
    private readonly Action _changed;

    /// <param name="rank">a mod's place in the load order (0 = top)</param>
    /// <param name="changed">a pin was marked or unmarked</param>
    public ConflictRow(ContestedFile file, Func<string, string> name, Func<string, int> rank, Action changed)
    {
        File = file;
        _rank = rank;
        _changed = changed;
        Chips = [.. file.Mods.Select(m => new ConflictChip(this, m, name(m)))];
        Refresh();
    }

    public ContestedFile File { get; }
    public string Path => File.Path;
    public string AreaName => File.Area switch
    {
        ConflictArea.Archive => L.T("archive"),
        ConflictArea.GameFolder => L.T("game folder"),
        _ => L.T("dlclist"),
    };
    public string AreaTip => File.Area switch
    {
        ConflictArea.Archive => L.T("A file inside the game’s archives — the mods’ versions are kept in the copies in mods"),
        ConflictArea.GameFolder => L.T("A file of the game folder (a plugin, its settings, a script) — the covered versions are kept aside"),
        _ => L.T("The same add-on pack is listed by several mods — one pack, nothing to pick"),
    };
    public IReadOnlyList<ConflictChip> Chips { get; }
    /// <summary>The winner can't be picked: a mod edited the version under it (or a dlclist.xml line).</summary>
    public bool Fixed => File.Fixed;
    public string FixedTip => File.Area == ConflictArea.Dlclist
        ? L.T("Shown only: the pack is the same whoever listed it.")
        : L.T("A mod edited the version under it rather than bringing its own — the versions stay as they are.");

    /// <summary>What a fixed row says instead of offering a pick.</summary>
    public string FixedLabel => File.Area == ConflictArea.Dlclist ? L.T("info") : L.T("edited");

    /// <summary>The pin marked here: a mod id, <see cref="ToOrder"/> (drop the pin), or null (as it is).</summary>
    [ObservableProperty] public partial string? Pending { get; set; }
    public const string ToOrder = "\0order";

    /// <summary>The pin it will have: the marked one, else the one it has.</summary>
    public string? PinAfter => Pending switch
    {
        null => File.Pinned,
        ToOrder => null,
        var m => m,
    };
    /// <summary>The mod whose version the game will get.</summary>
    public string WinnerAfter => PinAfter ?? (Pending == ToOrder ? File.Mods.MinBy(_rank)! : File.Winner);
    public bool IsPinned => PinAfter is not null;
    public bool HasPending => Pending is not null;
    public bool CanUnpin => IsPinned && !Fixed;
    public string PinTip => L.T("Picked by hand: this mod’s version wins here whatever the load order says");

    partial void OnPendingChanged(string? value)
    {
        Refresh();
        _changed();
    }

    private void Refresh()
    {
        foreach (var c in Chips) c.Winner = c.Mod == WinnerAfter;
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(CanUnpin));
    }

    /// <summary>Give the file to <paramref name="mod"/>: a pin, unless that is how it stands already.</summary>
    internal void Pick(string mod)
    {
        if (Fixed) return;
        Pending = mod == File.Pinned ? null : mod;
    }

    [RelayCommand]
    private void Unpin() => Pending = File.Pinned is null ? null : ToOrder;
}

/// <summary>A mod's version of a shared file, as a chip in its row.</summary>
public sealed partial class ConflictChip(ConflictRow row, string mod, string name) : ObservableObject
{
    public string Mod { get; } = mod;
    public string Name { get; } = name;
    public bool CanPick => !row.Fixed;

    /// <summary>The game gets this one's version (after the marked pins).</summary>
    [ObservableProperty] public partial bool Winner { get; set; }

    public string Tip => row.Fixed ? row.FixedTip
        : Winner ? L.T($"The game gets «{Name}»’s version")
        : L.T($"Give the game «{Name}»’s version of this file");

    partial void OnWinnerChanged(bool value) => OnPropertyChanged(nameof(Tip));

    [RelayCommand]
    private void Pick() => row.Pick(Mod);
}
