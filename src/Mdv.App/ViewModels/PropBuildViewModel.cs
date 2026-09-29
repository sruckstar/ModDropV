using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using Mdv.Core.Preview;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// The modder's add-on props: a folder of prop models (.ydr / .yft with their .ytd, collisions and animations; a
/// .ytyp of its own if it has one) becomes a pack whose .ytyp is written from the models — bounds, texture
/// dictionary, collision, draw distance — and loaded for good, so trainers spawn the props by name. Files named like
/// the game's get a prefix. The side column shows every prop, the names and what gets written; <see cref="PropBuilder"/>
/// builds it.
/// </summary>
public sealed partial class PropBuildViewModel : ModPanelViewModel
{
    private readonly GameModels _game;
    private PropSource? _source;
    private int _generation;
    private bool _loading;
    private string? _suggestedPack, _suggestedPrefix;

    public PropBuildViewModel(MainViewModel shell) : base(shell)
    {
        _game = GameModels.Load(AppPaths.Data);
        shell.PropertyChanged += OnShellChanged;
    }

    public override ModCategory Category => ModCategory.Prop;

    public ModelPreview Preview { get; } = new();

    // ================================================================ source

    [ObservableProperty] public partial string InputFolder { get; set; } = "";
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string RouteTitle { get; set; } = L.T("No source selected");
    [ObservableProperty] public partial string RouteDetail { get; set; } = EmptyDetail;
    [ObservableProperty] public partial string RouteBadge { get; set; } = "—";
    [ObservableProperty] public partial string SourceNotice { get; set; } = "";
    [ObservableProperty] public partial bool HasSourceNotice { get; set; }

    private static string EmptyDetail => L.T(
        "Pick the folder with the props’ models — .ydr (or .yft) with their .ytd, collisions and animations; " +
        "a .ytyp of your own, if you have one.");

    public ObservableCollection<AnalysisRow> Analysis { get; } = [];
    /// <summary>Every prop: its name and what its archetype says (size, textures, collision, draw distance).</summary>
    public ObservableCollection<AnalysisRow> Props { get; } = [];
    public ObservableCollection<AnalysisRow> Names { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty] public partial bool HasNames { get; set; }
    [ObservableProperty] public partial bool HasProps { get; set; }

    [RelayCommand]
    private async Task BrowseInput()
    {
        var path = await Shell.Pick(L.T("Folder with the props’ models"));
        if (path is not null) InputFolder = path;
    }

    partial void OnInputFolderChanged(string value) => _ = AnalyzeAsync(value);

    private async Task AnalyzeAsync(string folder)
    {
        int gen = ++_generation;
        folder = folder.Trim();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            Reset(folder.Length == 0 ? null : L.T("Folder not found."));
            return;
        }
        IsScanning = true;
        PropSource? src = null;
        try
        {
            src = await Task.Run(() => PropBuilder.Read(folder));
        }
        catch (Exception ex)
        {
            AppLog.Error("prop source analysis failed", ex);
        }
        if (gen != _generation) return;
        IsScanning = false;
        if (src is null)
        {
            Reset(L.T("Could not read the folder."));
            return;
        }
        _source = src;
        Show(src, folder);
        _ = LoadPreviewAsync(src);
    }

    private void Reset(string? error)
    {
        ++_generation;
        _source = null;
        IsScanning = false;
        Analysis.Clear();
        Warnings.Clear();
        RouteBadge = "—";
        RouteTitle = error is null ? L.T("No source selected") : L.T("Source unavailable");
        RouteDetail = error ?? EmptyDetail;
        SourceNotice = "";
        HasSourceNotice = false;
        _ = Preview.LoadAsync(null);
        UpdatePlan();
    }

    /// <summary>What the folder holds, and the form started from it (a prefix when its files carry the game's names).</summary>
    private void Show(PropSource src, string folder)
    {
        Analysis.Clear();
        Warnings.Clear();
        int count = src.Models.Count + src.OwnTypes.SelectMany(t => t.Archetypes).Count(a => !src.Models.Any(m => m.Name == a));
        if (src.PrebuiltRpf is not null)
        {
            RouteBadge = "dlc.rpf";
            RouteTitle = L.T("A finished pack");
            RouteDetail = L.T($"{Path.GetFileName(src.PrebuiltRpf)} is already an add-on — there is nothing to build. Players install it as it is.");
        }
        else if (count == 0)
        {
            RouteBadge = "—";
            RouteTitle = L.T("No prop found");
            RouteDetail = L.T("There is no prop model (.ydr / .yft) in the folder.");
        }
        else
        {
            RouteBadge = src.OwnTypes.Count > 0 ? L.T("own .ytyp") : L.T($"{count} prop(s)");
            var first = src.Models.Select(m => m.Name).Concat(src.OwnTypes.SelectMany(t => t.Archetypes)).First();
            RouteTitle = count == 1 ? L.T($"Prop {first}") : L.T($"{count} props");
            RouteDetail = src.NewModels.Any()
                ? L.T("A .ytyp is written for them: each prop’s size from its model, its textures and collision — spawnable by name.")
                : L.T("Its own .ytyp defines every prop — it’s packed as it is and loaded for good, so they spawn by name.");
        }
        int ydr = src.Models.Count(m => !m.Fragment), yft = src.Models.Count(m => m.Fragment);
        if (src.Models.Count > 0)
            Analysis.Add(new AnalysisRow(L.T("Models"), string.Join(", ", new[]
            {
                ydr > 0 ? L.T($"{ydr} .ydr") : null,
                yft > 0 ? L.T($"{yft} .yft (fragments)") : null,
            }.OfType<string>())));
        Analysis.Add(new AnalysisRow(L.T("Textures"), src.Dictionaries.Count == 0
            ? L.T("no .ytd — the models’ own")
            : string.Join(", ", src.Dictionaries.Keys.Order(StringComparer.Ordinal).Select(k => k + ".ytd"))));
        int ybn = src.Files.Keys.Count(k => k.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase));
        int ycd = src.Files.Keys.Count(k => k.EndsWith(".ycd", StringComparison.OrdinalIgnoreCase));
        if (ybn + ycd > 0)
            Analysis.Add(new AnalysisRow(L.T("Also"), string.Join(", ", new[]
            {
                ybn > 0 ? L.T($"{ybn} collision(s) .ybn") : null,
                ycd > 0 ? L.T($"{ycd} animation(s) .ycd") : null,
            }.OfType<string>())));
        if (src.OwnTypes.Count > 0)
            Analysis.Add(new AnalysisRow(L.T("Own .ytyp"), string.Join(", ", src.OwnTypes.Select(t => L.T($"{t.Name}.ytyp ({t.Archetypes.Count})")))));
        Analysis.Add(new AnalysisRow(L.T("Files"), L.T($"{src.Files.Count} model / texture file(s)")));
        if (src.Editions.Count > 0)
            Analysis.Add(new AnalysisRow(L.T("Models for"), string.Join(" + ", src.Editions.Order().Select(e => e.DisplayName()))));
        foreach (var w in src.Warnings) Warnings.Add(w);

        SourceNotice = src.OwnTypes.Count > 0
            ? L.T($"Found your own {string.Join(", ", src.OwnTypes.Select(t => t.Name + ".ytyp"))}. What it defines is packed as it is; " +
                  $"only the props it doesn’t define get archetypes written.")
            : "";
        HasSourceNotice = SourceNotice.Length > 0;

        _loading = true;
        var pack = VehicleBuilder.CleanModel(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))) ?? "props";
        if (string.IsNullOrWhiteSpace(PackName) || PackName == _suggestedPack) PackName = pack;
        _suggestedPack = pack;
        // files named like the game's: a prefix of the pack's name, unless one is typed already
        bool clash = src.Files.Keys.Select(k => k[..k.IndexOf('.')]).Concat(src.OwnTypes.SelectMany(t => t.Archetypes)).Any(_game.Has);
        var prefix = clash ? PackName.Trim('_') + "_" : "";
        if (string.IsNullOrWhiteSpace(Prefix) || Prefix == _suggestedPrefix) Prefix = prefix;
        _suggestedPrefix = prefix;
        _loading = false;
        UpdatePlan();
    }

    private Task LoadPreviewAsync(PropSource src)
    {
        var names = src.Models.Select(m => m.Name).ToList();
        if (names.Count == 0) return Preview.LoadAsync(null);
        var files = src.Files.Values.ToList();
        return Preview.LoadAsync(ct => AddonModelLoader.LoadProps(files, names, ct));
    }

    // ================================================================ form

    [ObservableProperty] public partial string PackName { get; set; } = "";
    [ObservableProperty] public partial string Prefix { get; set; } = "";
    [ObservableProperty] public partial bool Dynamic { get; set; } = true;
    /// <summary>Draw distance in metres; empty: by each prop's size.</summary>
    [ObservableProperty] public partial string LodDistance { get; set; } = "";

    partial void OnDynamicChanged(bool value) => UpdatePlan();
    partial void OnLodDistanceChanged(string value) => UpdatePlan();

    /// <summary>RAGE asset names are lowercase [a-z0-9_].</summary>
    partial void OnPackNameChanged(string value)
    {
        var clean = VehicleBuilder.CleanModel(value) ?? "";
        if (value.EndsWith(' ') || value.EndsWith('_')) clean += "_";
        if (clean != value)
        {
            PackName = clean;
            return;
        }
        UpdatePlan();
    }

    partial void OnPrefixChanged(string value)
    {
        var clean = PropBuilder.CleanPrefix(value);
        if (clean != value)
        {
            Prefix = clean;
            return;
        }
        UpdatePlan();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsEnhanced)) UpdatePlan();
    }

    private float? Lod => float.TryParse(LodDistance.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    public string LodNote => Lod is { } l
        ? L.T($"Every prop is drawn up to {l:0} m away.")
        : L.T("Empty: by each prop’s size, as the game’s own props — 40 m for a cup, 100 m for a car-sized one.");

    private PropBuildOptions Options(string input, string outDir) => new()
    {
        InputFolder = input,
        OutDir = outDir,
        PackName = PackName.Trim('_'),
        Prefix = Prefix,
        Dynamic = Dynamic,
        LodDist = Lod,
        Pack = Shell.PackRpf,
        Edition = Shell.Edition,
        DataDir = AppPaths.Data,
    };

    /// <summary>The names the props will get and what's written — or why they can't be built yet.</summary>
    private void UpdatePlan()
    {
        if (_loading) return;
        Names.Clear();
        Props.Clear();
        OnPropertyChanged(nameof(LodNote));
        if (_source is not { } src)
        {
            HasNames = HasProps = false;
            return;
        }
        var (n, error) = PropBuilder.Plan(src, Options(src.Folder, ""), _game);
        if (n is null)
        {
            PlanError = error;
            HasNames = HasProps = false;
            return;
        }
        PlanError = null;
        foreach (var p in n.Props)
        {
            var m = p.Model!;
            var what = new List<string> { L.T($"{m.Size.X:0.##} × {m.Size.Y:0.##} × {m.Size.Z:0.##} m"), L.T($"drawn to {p.LodDist:0} m") };
            what.Add(p.Txd is not null ? p.Txd + ".ytd" : m.OwnTextures > 0 ? L.T("own textures") : L.T("no textures"));
            what.Add(m.Collision ? m.Fragment ? L.T("fragment") : L.T("collision") : L.T("no collision"));
            Props.Add(new AnalysisRow(p.Name, string.Join(" · ", what)));
        }
        foreach (var o in n.Own) Props.Add(new AnalysisRow(o, L.T("own .ytyp")));
        HasProps = Props.Count > 0;
        Names.Add(new AnalysisRow(L.T("Pack"), L.T($"dlcpacks\\{n.Pack}  ·  dlc_{n.Pack}")));
        var written = new List<string>();
        if (n.Types.Length > 0) written.Add(n.Types + ".ytyp");
        written.Add("content.xml (DLC_ITYP_REQUEST)");
        Names.Add(new AnalysisRow(L.T("Written"), string.Join(", ", written)));
        Names.Add(new AnalysisRow(L.T("Physics"), n.Dynamic ? L.T("dynamic — they move when hit") : L.T("static — fixed in place")));
        if (n.Prefix.Length > 0) Names.Add(new AnalysisRow(L.T("Renamed"), L.T($"{n.Prefix}… — every file")));
        HasNames = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanError))]
    public partial string? PlanError { get; set; }

    public bool HasPlanError => PlanError is not null && _source is not null;

    // ================================================================ build

    public override (PanelJob? Job, string? Error) Prepare()
    {
        var input = InputFolder.Trim();
        if (input.Length == 0) return (null, L.T("No folder with the props’ models selected."));
        if (!Directory.Exists(input)) return (null, L.T("The folder with the props’ models is not found."));
        if (IsScanning || _source is null) return (null, L.T("The folder is still being read — wait a moment."));
        if (PlanError is { } error) return (null, error);
        var (output, outError) = Shell.CheckOutput();
        if (output is null) return (null, outError);
        var o = Options(input, output);
        return (new PanelJob(L.T("Building Add-On…"), log => Run(o, log))
        {
            LogHeader = [L.T($"Source: {input}")],
        }, null);
    }

    private static PanelOutcome Run(PropBuildOptions o, Action<string> log)
    {
        try
        {
            var r = PropBuilder.Build(o, log);
            var names = string.Join(", ", r.Names.SpawnNames.Take(4)) + (r.Names.SpawnNames.Count() > 4 ? ", …" : "");
            return r.DlcRpf is not null
                ? new PanelOutcome(true, L.T("Done"), L.T($"Add-on props built — spawn names {names}:\n{r.Root}"), r.Root)
                : new PanelOutcome(true, L.T("Done — loose folders"),
                                   L.T("Add-On built unpacked. Pack the *.rpf folders with CodeWalker (see manifest.json)."), r.Root);
        }
        catch (IntakeException ex)
        {
            log($"[!] {ex.Message}");
            return new PanelOutcome(false, L.T("Build not finished"), ex.Message);
        }
    }
}
