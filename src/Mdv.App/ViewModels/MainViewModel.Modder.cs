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

/// <summary>Modder flow: which kind of add-on is being built. Weapons are ready; the rest say what they'll do.</summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<ModCategory, ModPanelViewModel> _modderPanels = [];
    private bool _pickingType;

    public ObservableCollection<AddonTypeViewModel> ModderTypes { get; } = [];

    /// <summary>What a player can install, today and later (the side card before anything is dropped).</summary>
    public IReadOnlyList<SupportedKind> PlayerKinds { get; } =
    [
        new("Weapons", "Replace or FiveM weapon mods and finished packs, as add-ons", true),
        new("OIV packages & replacements", "Any file swap, safely through the mods folder", true),
        new("Scripts & plugins", "ASI, ScriptHookVDotNet, RAGE Plugin Hook — with their dependencies", true),
        new("Vehicles & peds", "Add-on packs, FiveM resources, replacements — checked against the game", true),
        new("Liveries", "Pictures, texture dictionaries, modkit liveries — for the game's cars and add-ons", true),
        new("Clothing", "Story characters and freemode", false),
        new("Props, maps & big packs", "ymap, Menyoo and Map Editor maps, total conversions", false),
    ];

    /// <summary>What a modder can build, today and later.</summary>
    public IReadOnlyList<SupportedKind> ModderKinds { get; } =
    [
        new("Weapon", "Replace files → a complete add-on weapon DLC", true),
        new("Vehicle", "From a vanilla base car", false),
        new("Ped", "peds.meta from a template, .ymt generated", false),
        new("Prop", ".ytyp with bounds from the model", false),
        new("MP clothing", ".ymt and shop meta, slots sorted", false),
    ];

    private static readonly (ModCategory Category, string Label, string What, string Blurb)[] AddonTypes =
    [
        (ModCategory.Weapon, "Weapon", "Weapon add-ons", ""),
        (ModCategory.Vehicle, "Vehicle", "Vehicle add-ons",
         "Pick a vanilla car as the base — handling, layout, sounds and class come from it — drop in the .yft / .ytd " +
         "and get a ready dlc.rpf: vehicles.meta, handling, variations, modkits and the in-game name generated."),
        (ModCategory.Ped, "Ped", "Ped add-ons",
         "Models, textures and variations (.ydd / .yft / .ytd / .ymt) into an add-on ped: peds.meta from a template " +
         "for its kind, the .ymt generated from the components when the mod has none."),
        (ModCategory.Prop, "Prop", "Prop add-ons",
         "Models (.ydr / .ytd / .ybn) into spawnable add-on props: the .ytyp is generated with the bounds taken " +
         "from each model, and the names to spawn them by are listed for Menyoo."),
        (ModCategory.Clothing, "MP clothing", "MP clothing add-ons",
         "Freemode clothes and props into an add-on pack: slots and genders sorted from the file names, the .ymt " +
         "and shop meta generated, the collection named so it never collides with installed packs."),
    ];

    private void InitModderTypes()
    {
        foreach (var t in AddonTypes)
            ModderTypes.Add(new AddonTypeViewModel(t.Category, t.Label, t.Category == ModCategory.Weapon, OnAddonTypePicked));
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
        if (!_modderPanels.TryGetValue(category, out var panel))
        {
            var t = AddonTypes.First(a => a.Category == category);
            _modderPanels[category] = panel = new ComingSoonViewModel(this, category, t.What, t.Blurb);
        }
        return panel;
    }
}
