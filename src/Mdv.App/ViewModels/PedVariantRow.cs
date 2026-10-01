using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mdv.App.ViewModels;

/// <summary>
/// One variant of a ped's components (an alternative the mod ships), with its checkbox: switched on, the variants it
/// shares files with go off. The install panel and the Library's variants dialog both list them.
/// </summary>
public sealed partial class PedVariantRow : ObservableObject
{
    private readonly IReadOnlyList<PedVariantRow> _all;
    private readonly Action? _changed;
    private bool _syncing;

    public PedVariantRow(PedVariant variant, IReadOnlyList<PedVariantRow> all, Action? changed = null)
    {
        Variant = variant;
        _all = all;
        _changed = changed;
        _syncing = true;
        IsOn = variant.On;
        _syncing = false;
    }

    public PedVariant Variant { get; }
    public string Name => Variant.Name;

    /// <summary>"replaces berd_001_u.ydd + 3 textures"</summary>
    public string Detail
    {
        get
        {
            var models = Variant.Files.Where(f => !f.Name.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)).Select(f => f.Name).ToList();
            int textures = Variant.Files.Count - models.Count;
            return models.Count == 0 ? L.T($"textures only: {textures}")
                : textures == 0 ? L.T($"replaces {string.Join(", ", models)}")
                : L.T($"replaces {string.Join(", ", models)} + {textures} texture(s)");
        }
    }

    public string Tip => string.Join("\n", Variant.Files.Select(f => f.Name));

    [ObservableProperty] public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (_syncing) return;
        PedVariants.Set(_all.Select(r => r.Variant).ToList(), Variant, value);
        foreach (var r in _all)
        {
            r._syncing = true;
            r.IsOn = r.Variant.On;
            r._syncing = false;
        }
        _changed?.Invoke();
    }
}
