using Mdv.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Mdv.App.Services;
using Mdv.Core.Index;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mdv.App.ViewModels;

/// <summary>A placement file / trainer map / prop of the mod: what it is and where it is in the world.</summary>
public sealed record MapItemRow(string Title, string Detail, string Tag)
{
    public bool HasDetail => Detail.Length > 0;
    public bool HasTag => Tag.Length > 0;
}

/// <summary>A part the mod comes with (game files it replaces, its scripts) — installed with it unless left out.</summary>
public sealed partial class MapPartRow : ObservableObject
{
    private readonly AddonPackage _pkg;
    private readonly ModPackage _part;

    public MapPartRow(AddonPackage pkg, ModPackage part)
    {
        _pkg = pkg;
        _part = part;
        IsOn = !pkg.SkippedExtras.Contains(part);
    }

    public string Title => _part is ScriptPackage ? L.T("Its scripts") : L.T("Game files it changes");
    public string Detail => _part switch
    {
        ScriptPackage s => L.T($"{string.Join(", ", s.Files.Where(f => f.IsEntry).Select(f => f.Name))} → scripts\\ (a script mod of its own in the Library)"),
        ReplacementPackage r => L.T($"{string.Join(", ", r.Files.Select(f => f.Name))} — in copies of the game's archives under mods"),
        _ => string.Join(" · ", _part.Parts),
    };

    [ObservableProperty] public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (value) _pkg.SkippedExtras.Remove(_part);
        else _pkg.SkippedExtras.Add(_part);
    }
}

/// <summary>
/// A map or props from the drop: an add-on pack (finished, a FiveM map, loose placement / archetype files), a map
/// saved by Menyoo / Map Editor, or both ways of the same map to pick from; what it places and where, the parts the
/// mod comes with, and the checks against the selected game (models it lacks, the game's own map sections, the tool
/// a trainer map needs) — run again whenever the game, its edition or the pack name changes.
/// </summary>
public sealed partial class MapViewModel : FileModViewModel
{
    private AddonPackage? _pkg;
    private PlacementPackage? _placements;
    private int _generation;
    private bool _loading;

    public MapViewModel(MainViewModel shell) : base(shell)
    {
        shell.PropertyChanged += OnShellChanged;
    }

    public override ModCategory Category => _pkg?.Kind ?? ModCategory.Map;
    public override ModPackage? Package => (ModPackage?)_pkg ?? _placements;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string KindBadge { get; set; } = "map";
    [ObservableProperty] public partial string Heading { get; set; } = "MAP";
    [ObservableProperty] public partial string SourceText { get; set; } = "";

    public ObservableCollection<MapItemRow> Items { get; } = [];
    public ObservableCollection<MapPartRow> Parts { get; } = [];
    public ObservableCollection<AddonCheckRow> Checks { get; } = [];
    public ObservableCollection<DependencyRow> Tools { get; } = [];

    public bool HasParts => Parts.Count > 0;
    public bool HasTools => Tools.Count > 0;

    [ObservableProperty] public partial string PackName { get; set; } = "";
    [ObservableProperty] public partial string PackDetail { get; set; } = "";

    /// <summary>The mod has the same map as a Menyoo / Map Editor file too — the player picks the way in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPack))]
    public partial bool HasPlacementChoice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallAsPack), nameof(ShowPack), nameof(Note))]
    public partial bool InstallAsPlacement { get; set; }

    public bool InstallAsPack
    {
        get => !InstallAsPlacement;
        set => InstallAsPlacement = !value;
    }

    [ObservableProperty] public partial string PackText { get; set; } = "";
    [ObservableProperty] public partial string PlacementText { get; set; } = "";

    public bool ShowPack => _pkg is not null && !InstallAsPlacement;

    [ObservableProperty] public partial bool IsChecking { get; set; }
    [ObservableProperty] public partial string CheckStatus { get; set; } = "";
    [ObservableProperty] public partial bool ChecksNeedAttention { get; set; }

    private bool UsesPlacement => _pkg is null || (_pkg.UsePlacement && _pkg.Placement is not null);
    private PlacementPackage? ActivePlacement => _pkg is null ? _placements : _pkg.UsePlacement ? _pkg.Placement : null;

    /// <summary>How it goes into the game, for the note under the checks.</summary>
    public string Note => UsesPlacement
        ? (ActivePlacement?.Files.Any(f => f.Tool == PlacementTool.MapEditor) ?? false) &&
          !(ActivePlacement?.Files.Any(f => f.Tool == PlacementTool.Menyoo) ?? false)
            ? L.T(@"It goes into scripts\AutoloadMaps — Map Editor loads it with the game. Switching it off in the Library renames it to *.disabled.")
            : L.T(@"It goes into menyooStuff\Spooner — in the game open Menyoo (F8): Object Spooner → Manage Saved Files → the map → Load Placement. " +
              "Switching it off in the Library renames it to *.disabled.")
        : _pkg?.Kind == ModCategory.Prop
            ? L.T(@"It goes into mods\update\x64\dlcpacks as a pack of its own and into dlclist.xml; its props load with the game — spawn them by name " +
              "with Menyoo's Object Spooner or Map Editor. Switch it off or remove it any time in the Library.")
            : L.T(@"It goes into mods\update\x64\dlcpacks as a pack of its own and into dlclist.xml — the map loads with the game in story mode, " +
              "no trainer needed. Switch it off or remove it any time in the Library.");

    partial void OnInstallAsPlacementChanged(bool value)
    {
        if (_loading || _pkg is null) return;
        _pkg.UsePlacement = value && _pkg.Placement is not null;
        ShowItems();
        _ = CheckAsync();
    }

    partial void OnPackNameChanged(string value)
    {
        if (_loading || _pkg is null) return;
        if (AddonPackHandler.Clean(value) is { } clean && clean != _pkg.PackName)
        {
            _pkg.PackName = clean;
            _pkg.Checks = null;
            _ = CheckAsync();
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((_pkg is not null || _placements is not null) && e.PropertyName is nameof(MainViewModel.GameFolder) or nameof(MainViewModel.IsEnhanced))
        {
            if (_pkg is not null) _pkg.Checks = null;
            _ = CheckAsync();
        }
    }

    protected override string? NotReady()
    {
        if (IsChecking) return L.T("The map is still being checked against the game — wait a moment.");
        if (_pkg is null || UsesPlacement) return null;
        if (AddonPackHandler.Clean(PackName) is null) return L.T("Give the pack a folder name (latin letters, digits, _).");
        return _pkg.Checks?.Blocking;
    }

    /// <summary>Show a map / props add-on, or a trainer map on its own (both null: none).</summary>
    public Task UseAsync(AddonPackage? pkg, PlacementPackage? placements = null)
    {
        _pkg = pkg;
        _placements = pkg is null ? placements : null;
        ++_generation;
        Items.Clear();
        Checks.Clear();
        Tools.Clear();
        Parts.Clear();
        if (pkg is null && placements is null)
        {
            SetWarnings([]);
            HasPlacementChoice = false;
            NotifyLists();
            return Task.CompletedTask;
        }
        _loading = true;
        var main = (ModPackage?)pkg ?? placements!;
        Name = main.Name;
        bool props = pkg?.Kind == ModCategory.Prop;
        KindBadge = props ? L.T("props") : L.T("map");
        Heading = props ? L.T("PROPS") : L.T("MAP");
        SourceText = pkg is null
            ? L.T($"A map saved by {string.Join(" / ", placements!.Files.Select(f => f.Tool == PlacementTool.Menyoo ? "Menyoo" : "Map Editor").Distinct())} — it needs that tool in the game")
            : pkg.Finished is { } f ? (props ? L.T($"A finished props pack ({f.Device ?? "dlc.rpf"})") : L.T($"A finished map pack ({f.Device ?? "dlc.rpf"})"))
            : pkg.Compose is { Resources.Count: > 0 } s ? L.T($"A FiveM map ({string.Join(", ", s.Resources)}) — packed into a dlc.rpf on install")
            : props ? L.T("Loose props with their archetypes (.ytyp) — packed into a dlc.rpf on install")
            : L.T("Loose map files (.ymap / .ytyp and their models) — packed into a dlc.rpf on install");
        HasPlacementChoice = pkg?.Placement is not null;
        InstallAsPlacement = pkg?.UsePlacement ?? true;
        PackText = L.T("Add-on map — loads with the game by itself, no trainer needed");
        PlacementText = pkg?.Placement is { } pl
            ? L.T($"{string.Join(" / ", pl.Files.Select(x => x.Tool == PlacementTool.Menyoo ? L.T("Menyoo map") : L.T("Map Editor map")).Distinct())} — " +
              $"{string.Join(", ", pl.Files.Select(x => x.Name))}, loaded in the trainer")
            : "";
        PackName = pkg?.PackName ?? "";
        if (pkg is not null)
            foreach (var part in pkg.Extras) Parts.Add(new MapPartRow(pkg, part));
        _loading = false;
        ShowItems();
        NotifyLists();
        SetWarnings(main.Warnings);
        return CheckAsync();
    }

    private void NotifyLists()
    {
        OnPropertyChanged(nameof(HasParts));
        OnPropertyChanged(nameof(HasTools));
        OnPropertyChanged(nameof(ShowPack));
        OnPropertyChanged(nameof(Note));
    }

    private static string At((float X, float Y, float Z)? at) =>
        at is { } c ? L.T($"around {c.X:0}, {c.Y:0} (z {c.Z:0})") : "";

    /// <summary>What it places — the add-on's placements and props, or the trainer maps.</summary>
    private void ShowItems()
    {
        Items.Clear();
        if (ActivePlacement is { } pp)
        {
            foreach (var f in pp.Files)
                Items.Add(new MapItemRow(Path.GetFileNameWithoutExtension(f.Name),
                    string.Join(" · ", new[] { f.Counts(), At(f.At) }.Where(s => s.Length > 0)),
                    f.Tool == PlacementTool.Menyoo ? "menyoo" : L.T("map editor")));
            return;
        }
        if (_pkg is not { } pkg) return;
        var c = pkg.Content;
        foreach (var m in c.Maps)
            Items.Add(new MapItemRow(m.Name,
                string.Join(" · ", new[] { L.T($"{m.Entities} object(s) of {m.Archetypes.Count} kind(s)"), At(m.Center) }
                                   .Where(s => s.Length > 0)),
                L.T("placement")));
        if (c.Maps.Count == 0)
            foreach (var y in c.Ymaps) Items.Add(new MapItemRow(Path.GetFileNameWithoutExtension(y.Split('/')[^1]), "", L.T("placement")));
        if (c.Archetypes.Count > 0)
            Items.Add(new MapItemRow(pkg.Kind == ModCategory.Prop ? L.T($"{c.Archetypes.Count} prop(s)") : L.T($"Its own models ({c.Archetypes.Count})"),
                string.Join(", ", c.Archetypes.Take(12)) + (c.Archetypes.Count > 12 ? ", …" : ""),
                pkg.Kind == ModCategory.Prop ? L.T("spawn names") : L.T("archetypes")));
    }

    /// <summary>Check against the selected game in the background; a newer check wins.</summary>
    private async Task CheckAsync()
    {
        int gen = ++_generation;
        var game = Shell.GameFolder.Trim();
        UpdatePackDetail();
        Tools.Clear();
        if (game.Length == 0 || !Directory.Exists(game))
        {
            Checks.Clear();
            IsChecking = false;
            ChecksNeedAttention = false;
            CheckStatus = L.T("Choose the GTA V folder — the map is checked against what the game already has.");
            NotifyLists();
            return;
        }
        IsChecking = true;
        CheckStatus = L.T("Checking against the game…");
        var target = MainViewModel.TargetFor(game, Shell.Edition);
        var pkg = _pkg;
        var placement = ActivePlacement;
        AddonCheckReport? report = null;
        string? error = null;
        try
        {
            report = await Task.Run(() =>
            {
                GameIndex? index = null;
                try
                {
                    index = GameIndex.Open(game, GameIndexCache.DefaultRoot);
                }
                catch (Exception ex)
                {
                    AppLog.Error("map checks: the game index could not be read", ex);
                }
                if (placement is not null) PlacementHandler.Check(placement, game, target.Edition, null, index);
                return pkg is not null && placement is null ? AddonPackHandler.Check(pkg, target, index) : null;
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("checking the map failed", ex);
            error = L.T($"The game could not be checked: {ex.Message}");
        }
        if (gen != _generation) return;
        IsChecking = false;
        Checks.Clear();
        int problems;
        if (placement is not null)
        {
            foreach (var d in placement.Dependencies) Tools.Add(new DependencyRow(d));
            if (placement.MissingModels.Count > 0)
                Checks.Add(new AddonCheckRow(new AddonCheck(CheckLevel.Warn, L.T("Models not in the game"), PlacementHandler.MissingText(placement.MissingModels))));
            problems = placement.Dependencies.Count(d => d.IsProblem) + (placement.MissingModels.Count > 0 ? 1 : 0);
            CheckStatus = error ?? (problems > 0 ? (problems == 1 ? L.T("1 thing to look at — it can still be installed.") : L.T($"{problems} things to look at — it can still be installed."))
                                                 : L.T("The tool it needs is there, and every model it places."));
        }
        else if (report is not null)
        {
            foreach (var c in report.Items.OrderByDescending(c => c.Level)) Checks.Add(new AddonCheckRow(c));
            problems = report.Items.Count(i => i.Level is CheckLevel.Warn or CheckLevel.Block);
            CheckStatus = report.Blocking is not null ? L.T("It can't go into this game.")
                : problems > 0 ? (problems == 1 ? L.T("1 thing to look at — it can still be installed.") : L.T($"{problems} things to look at — it can still be installed."))
                : L.T("Nothing clashes with what the game already has.");
            _loading = true;
            PackName = pkg!.PackName;                  // a taken name was changed to a free one
            _loading = false;
        }
        else
        {
            problems = 0;
            CheckStatus = error ?? "";
        }
        ChecksNeedAttention = problems > 0;
        UpdatePackDetail();
        NotifyLists();
    }

    private void UpdatePackDetail()
    {
        if (_pkg is not { } pkg) return;
        PackDetail = L.T($"mods\\update\\x64\\dlcpacks\\{pkg.PackName}  ·  mounted as {pkg.Device}") +
                     (pkg.PackNameFrom is { } from ? L.T($"  ·  name from {from}") : "");
    }
}
