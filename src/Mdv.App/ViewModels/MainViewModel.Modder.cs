using Mdv.Core;
using System.Collections.ObjectModel;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mdv.App.ViewModels;

/// <summary>An add-on type in the modder's picker: ready, or coming later.</summary>
public sealed partial class AddonTypeViewModel(ModCategory category, string label, bool ready, Action<AddonTypeViewModel> picked)
    : ObservableObject
{
    public ModCategory Category { get; } = category;
    public string Label { get; } = label;
    public bool Ready { get; } = ready;
    public bool Soon => !Ready;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) picked(this);
    }
}

/// <summary>A line of the "what ModDrop V handles" card.</summary>
public sealed record SupportedKind(string Title, string Detail, bool Ready)
{
    public bool Soon => !Ready;
}

/// <summary>Modder flow: which kind of add-on is being built — weapons, vehicles, peds, props and MP clothes.</summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<ModCategory, ModPanelViewModel> _modderPanels = [];
    private bool _pickingType;

    public ObservableCollection<AddonTypeViewModel> ModderTypes { get; } = [];

    /// <summary>What a player can install, today and later (the side card before anything is dropped).</summary>
    public IReadOnlyList<SupportedKind> PlayerKinds { get; } =
    [
        new(L.T("Weapons"), L.T("Replace or FiveM weapon mods and finished packs, as add-ons"), true),
        new(L.T("OIV packages & replacements"), L.T("Any file swap, safely through the mods folder"), true),
        new(L.T("Scripts & plugins"), L.T("ASI, ScriptHookVDotNet, RAGE Plugin Hook — with their dependencies"), true),
        new(L.T("Vehicles & peds"), L.T("Add-on packs, FiveM resources, replacements — checked against the game"), true),
        new(L.T("Liveries"), L.T("Pictures, texture dictionaries, modkit liveries — for the game's cars and add-ons"), true),
        new(L.T("Clothing"), L.T("MP clothes packs and FiveM clothing, story characters' clothes — new slots added when needed"), true),
        new(L.T("Maps, props & big packs"), L.T("Add-on maps and props, Menyoo and Map Editor maps, total conversions — with a size check and cancel"), true),
    ];

    /// <summary>What a modder can build, today and later.</summary>
    public IReadOnlyList<SupportedKind> ModderKinds { get; } =
    [
        new(L.T("Weapon"), L.T("Replace files → a complete add-on weapon DLC"), true),
        new(L.T("Vehicle"), L.T("Models → an add-on vehicle DLC, on the base of a game vehicle"), true),
        new(L.T("Ped"), L.T("Models → an add-on ped DLC, on the base of a game ped; .ymt written when missing"), true),
        new(L.T("Prop"), L.T("Models → spawnable add-on props; the .ytyp written with bounds from each model"), true),
        new(L.T("MP clothing"), L.T("Models of any name → an add-on collection of each MP ped; .ymt and shop meta written"), true),
    ];

    private static (ModCategory Category, string Label, string What, string Blurb)[] AddonTypes =>
    [
        (ModCategory.Weapon, L.T("Weapon"), L.T("Weapon add-ons"), ""),
        (ModCategory.Vehicle, L.T("Vehicle"), L.T("Vehicle add-ons"), ""),
        (ModCategory.Ped, L.T("Ped"), L.T("Ped add-ons"),
         L.T("Models, textures and variations (.ydd / .yft / .ytd / .ymt) into an add-on ped: peds.meta from a template " +
         "for its kind, the .ymt generated from the components when the mod has none.")),
        (ModCategory.Prop, L.T("Prop"), L.T("Prop add-ons"),
         L.T("Models (.ydr / .ytd / .ybn) into spawnable add-on props: the .ytyp is generated with the bounds taken " +
         "from each model, and the names to spawn them by are listed for Menyoo.")),
        (ModCategory.Clothing, L.T("MP clothing"), L.T("MP clothing add-ons"),
         L.T("Freemode clothes and props into an add-on pack: slots and genders sorted from the file names (or picked by " +
         "hand), the .ymt and shop meta generated, the collection name checked against the game’s.")),
    ];

    private void InitModderTypes()
    {
        foreach (var t in AddonTypes)
            ModderTypes.Add(new AddonTypeViewModel(t.Category, t.Label, t.Category is ModCategory.Weapon or ModCategory.Vehicle or ModCategory.Ped or ModCategory.Prop or ModCategory.Clothing, OnAddonTypePicked));
        var stored = Enum.TryParse<ModCategory>(Settings.AddonType, ignoreCase: true, out var c) ? c : ModCategory.Weapon;
        (ModderTypes.FirstOrDefault(t => t.Category == stored) ?? ModderTypes[0]).IsSelected = true;
    }

    private void OnAddonTypePicked(AddonTypeViewModel type)
    {
        if (_pickingType) return;
        _pickingType = true;
        try
        {
            foreach (var other in ModderTypes)
                if (other != type) other.IsSelected = false;
        }
        finally
        {
            _pickingType = false;
        }
        ModderPanel = PanelFor(type.Category);
        var key = type.Category.ToString().ToLowerInvariant();
        if (Settings.AddonType == key) return;
        Settings.AddonType = key;
        Settings.Save();
    }

    private ModPanelViewModel PanelFor(ModCategory category)
    {
        if (category == ModCategory.Weapon) return Weapon;
        if (category == ModCategory.Vehicle) return VehicleBuild;
        if (category == ModCategory.Ped) return PedBuild;
        if (category == ModCategory.Prop) return PropBuild;
        if (category == ModCategory.Clothing) return ClothingBuild;
        if (!_modderPanels.TryGetValue(category, out var panel))
        {
            var t = AddonTypes.First(a => a.Category == category);
            _modderPanels[category] = panel = new ComingSoonViewModel(this, category, t.What, t.Blurb);
        }
        return panel;
    }
}
