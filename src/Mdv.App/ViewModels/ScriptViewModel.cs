using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Mdv.App.Services;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>One file of a script mod and where it goes.</summary>
public sealed record ScriptFileRow(string Name, string Dest, string Kind, string? Note)
{
    public bool HasKind => Kind.Length > 0;
    public bool HasNote => Note is not null;
}

/// <summary>One thing a script mod needs, as it stands in the selected game.</summary>
public sealed partial class DependencyRow(ScriptDependency dep) : ObservableObject
{
    public ScriptDependency Dependency { get; } = dep;
    public string Name => Dependency.Name;
    public string State => Dependency.StateText;
    public string Detail => Dependency.Detail;
    public string NeededBy => Dependency.NeededBy.Count == 0 ? "" : "needed by " + string.Join(", ", Dependency.NeededBy.Take(3)) +
                                                                     (Dependency.NeededBy.Count > 3 ? ", …" : "");
    public bool IsProblem => Dependency.IsProblem;
    public bool IsOk => Dependency.State is DependencyState.Ok or DependencyState.InMod or DependencyState.Bundled;
    public bool IsPending => !IsProblem && !IsOk;
    public bool HasLink => Dependency.Link is not null && (IsProblem || IsPending);

    [RelayCommand]
    private void OpenLink()
    {
        if (Dependency.Link is not { } link) return;
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("open dependency link failed", ex);
        }
    }
}

/// <summary>
/// ASI plugins, ScriptHookVDotNet scripts and RAGE plugins from the drop: where each file goes, the
/// version to install when the mod ships several, and what it needs from the game — checked again
/// whenever the game or its edition changes.
/// </summary>
public sealed partial class ScriptViewModel : FileModViewModel
{
    private ScriptPackage? _pkg;
    private bool _loading;

    public ScriptViewModel(MainViewModel shell) : base(shell)
    {
        shell.PropertyChanged += OnShellChanged;
    }

    public override ModCategory Category => ModCategory.Script;
    public override ModPackage? Package => _pkg;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string LeftOut { get; set; } = "";
    [ObservableProperty] public partial bool HasLeftOut { get; set; }
    [ObservableProperty] public partial string NeedsSummary { get; set; } = "";
    [ObservableProperty] public partial bool NeedsAttention { get; set; }
    [ObservableProperty] public partial bool CanSwitch { get; set; }

    public ObservableCollection<ScriptFileRow> Files { get; } = [];
    public ObservableCollection<DependencyRow> Dependencies { get; } = [];
    public ObservableCollection<string> Variants { get; } = [];

    public bool HasVariants => Variants.Count > 1;

    [ObservableProperty] public partial string? SelectedVariant { get; set; }

    partial void OnSelectedVariantChanged(string? value)
    {
        if (_loading || _pkg is null || value is null) return;
        int i = Variants.IndexOf(value);
        if (i < 0 || i == _pkg.Selected) return;
        _pkg.Selected = i;
        _pkg.VariantPicked = true;
        Refresh();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_pkg is not null && e.PropertyName is nameof(MainViewModel.GameFolder) or nameof(MainViewModel.IsEnhanced)) Refresh();
    }

    /// <summary>Show a script mod (null: none).</summary>
    public void Use(ScriptPackage? pkg)
    {
        _pkg = pkg;
        _loading = true;
        Variants.Clear();
        foreach (var v in pkg?.Variants ?? []) Variants.Add(v.ToString());
        OnPropertyChanged(nameof(HasVariants));
        _loading = false;
        if (pkg is null)
        {
            Files.Clear();
            Dependencies.Clear();
            SetWarnings([]);
            return;
        }
        Name = pkg.Name;
        Refresh();
    }

    /// <summary>Check the mod against the selected game and show the result.</summary>
    private void Refresh()
    {
        if (_pkg is not { } pkg) return;
        var game = Shell.GameFolder.Trim();
        try
        {
            ScriptHandler.Check(pkg, game.Length > 0 && Directory.Exists(game) ? game : null, Shell.Edition);
        }
        catch (Exception ex)
        {
            AppLog.Error("checking script dependencies failed", ex);
        }
        _loading = true;
        SelectedVariant = Variants.Count > 0 ? Variants[pkg.Selected] : null;
        _loading = false;

        Files.Clear();
        foreach (var f in pkg.Files.OrderBy(f => f.Shared).ThenBy(f => f.Dest.Count(c => c == '/')).ThenBy(f => f.Dest, StringComparer.OrdinalIgnoreCase))
            Files.Add(new ScriptFileRow(f.Name, f.Dest.Replace('/', '\\'), f.KindText,
                                        f.Skip ?? (f.Shared ? "Shared with other mods — stays when this one is removed." : null)));
        Summary = string.Join("  ·  ", pkg.Parts.Where(p => p.Length > 0));
        var left = pkg.Variant.LeftOut.Select(o => o[(o.Replace('\\', '/').LastIndexOf('/') + 1)..]).ToList();
        LeftOut = left.Count == 0 ? "" : $"Not installed: {string.Join(", ", left.Take(6))}{(left.Count > 6 ? $" and {left.Count - 6} more" : "")}" +
                                         " — readmes, screenshots and files for other versions.";
        HasLeftOut = left.Count > 0;
        CanSwitch = pkg.Files.Any(f => f.IsEntry);

        Dependencies.Clear();
        foreach (var d in pkg.Dependencies) Dependencies.Add(new DependencyRow(d));
        int problems = pkg.Dependencies.Count(d => d.IsProblem);
        NeedsAttention = problems > 0;
        NeedsSummary = pkg.Dependencies.Count == 0 ? "Nothing — it runs on its own."
            : problems > 0 ? $"{problems} of {pkg.Dependencies.Count} need{(problems == 1 ? "s" : "")} your attention — the mod won't work without {(problems == 1 ? "it" : "them")}."
            : game.Length == 0 ? "Choose the GTA V folder to check what is installed."
            : "Everything it needs is in place.";
        SetWarnings(pkg.Warnings);
    }
}
