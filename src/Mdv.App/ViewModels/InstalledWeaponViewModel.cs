using Mdv.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// One weapon installed into the game. The checkbox and the trash button only mark a
/// change; nothing touches the game until the changes are applied.
/// </summary>
public sealed partial class InstalledWeaponViewModel : ObservableObject
{
    public InstalledWeaponViewModel(InstalledMod mod)
    {
        Mod = mod;
        Enabled = mod.Enabled;
    }

    public InstalledMod Mod { get; }
    public string Name => Mod.Name;

    /// <summary>Where it lives: the shared pack or its own dlcpack.</summary>
    public string Where => Mod.Kind == ModKind.Merged
        ? $"in the shared {Mod.Pack} pack"
        : $"own dlcpack · {Mod.Pack}";

    public string RemoveTip => Mod.Kind == ModKind.Merged
        ? $"Remove from the game: its models, metas and texts are taken out of {Mod.Pack}"
        : $"Remove from the game: the whole «{Mod.Pack}» dlcpack is deleted";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(Status), nameof(HasStatus))]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(Status), nameof(HasStatus), nameof(CanToggle))]
    public partial bool PendingRemove { get; set; }

    public bool CanToggle => !PendingRemove;
    public bool IsChanged => PendingRemove || Enabled != Mod.Enabled;

    /// <summary>What applying will do to this weapon, or its current state if nothing.</summary>
    public string Status =>
        PendingRemove ? "will be removed"
        : Enabled != Mod.Enabled ? (Enabled ? "will be switched on" : "will be switched off")
        : Enabled ? "" : "off";

    public bool HasStatus => Status.Length > 0;

    [RelayCommand]
    private void ToggleRemove() => PendingRemove = !PendingRemove;

    public void Revert()
    {
        PendingRemove = false;
        Enabled = Mod.Enabled;
    }

    public ModChange? Change => IsChanged ? new ModChange(Mod.Id, Enabled, PendingRemove) : null;
}
