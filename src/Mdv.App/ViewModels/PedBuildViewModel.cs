using System.Collections.ObjectModel;
using System.ComponentModel;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using Mdv.Core.Preview;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>A kind of ped in the base picker's filter: every ped, or only the male / female / animal ones.</summary>
public sealed record PedKindItem(string? Key, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The modder's add-on ped: a folder with the ped's models (a dictionary ped — name.ydd / .ytd — or a streamed one
/// with a folder of components; Replace files of a game ped too — they're renamed), a game ped picked as the base
/// (movement, voice, personality and behaviour come from its peds.meta entry) and the variations file, written from
/// the components when the folder has none. The side column shows what the folder holds, what gets written and the
/// names the ped gets; <see cref="PedBuilder"/> builds it.
/// </summary>
public sealed partial class PedBuildViewModel : ModPanelViewModel
{
    private readonly PedTemplates _lib;
    private readonly VanillaModels _vanilla;
    private PedSource? _source;
    private int _generation;
    private bool _loading;
    private string? _suggestedName;

    public PedBuildViewModel(MainViewModel shell) : base(shell)
    {
        _lib = PedTemplates.Load(AppPaths.Data);
        _vanilla = VanillaModels.Load(AppPaths.Data);
        Kinds = [new PedKindItem(null, L.T("every kind")), .. PedKinds.All.Select(k => new PedKindItem(k, PedKinds.Name(k)))];
        Kind = Kinds[0];
        Bases = _lib.All;
        shell.PropertyChanged += OnShellChanged;
    }

    public override ModCategory Category => ModCategory.Ped;

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
        "Pick the folder with the ped’s models — name.yft with name.ydd / name.ytd, or a folder of components " +
        "(head_000_r.ydd…); name.ymt and your own peds.meta, if you have them.");

    public ObservableCollection<AnalysisRow> Analysis { get; } = [];
    public ObservableCollection<AnalysisRow> Names { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty] public partial bool HasNames { get; set; }

    [RelayCommand]
    private async Task BrowseInput()
    {
        var path = await Shell.Pick(L.T("Folder with the ped’s models"));
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
        PedSource? src = null;
        try
        {
            src = await Task.Run(() => PedBuilder.Read(folder));
        }
        catch (Exception ex)
        {
            AppLog.Error("ped source analysis failed", ex);
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

    /// <summary>What the folder holds, and the form started from it (a Replace file's game ped is the base).</summary>
    private void Show(PedSource src, string folder)
    {
        Analysis.Clear();
        Warnings.Clear();
        var ped = src.Ped;
        if (src.PrebuiltRpf is not null)
        {
            RouteBadge = "dlc.rpf";
            RouteTitle = L.T("A finished pack");
            RouteDetail = L.T($"{Path.GetFileName(src.PrebuiltRpf)} is already an add-on — there is nothing to build. Players install it as it is.");
        }
        else if (src.Peds.Count == 0)
        {
            RouteBadge = "—";
            RouteTitle = L.T("No ped found");
            RouteDetail = L.T("There is no ped model (.yft) in the folder.");
        }
        else if (ped is null)
        {
            RouteBadge = L.T($"{src.Peds.Count} peds");
            RouteTitle = L.T("Several peds");
            RouteDetail = L.T($"The folder holds {string.Join(", ", src.Peds)} — build them one at a time, each from its own folder.");
        }
        else
        {
            bool replace = _vanilla.IsPed(ped);
            RouteBadge = src.OwnInit ? L.T("own peds.meta") : replace ? L.T("replace → add-on") : src.Streamed ? L.T("streamed") : L.T("models");
            RouteTitle = replace ? L.T($"Replace files of the game’s ped {ped}") : src.OwnInit ? L.T($"{ped} with its own peds.meta") : L.T($"Ped {ped}");
            RouteDetail = replace
                ? L.T("They become an add-on of their own: the files get the new name, the peds.meta entry is written from the base ped.")
                : src.OwnInit
                    ? L.T("Its peds.meta is used — with the name from the form.")
                    : L.T("The peds.meta entry is written from the base ped: how it moves, talks and behaves.");
        }
        if (ped is not null)
        {
            var own = new[] { ".yft", ".ydd", ".ytd", ".ymt", "_p.ydd", "_p.ytd" }.Where(s => src.Has(ped + s)).Select(s => ped + s);
            Analysis.Add(new AnalysisRow(L.T("Model"), string.Join("  ", own)));
            Analysis.Add(new AnalysisRow(L.T("Layout"), src.Streamed
                ? L.T($"streamed — its components in the folder {ped}")
                : L.T($"its components in {ped}.ydd")));
            var slots = src.Parts.Where(p => !p.Prop && p.Kind == ClothingPartKind.Drawable).Select(p => p.Slot).Distinct().Count();
            Analysis.Add(new AnalysisRow(L.T("Components"), src.Drawables == 0
                ? L.T("none ModDrop V can name")
                : L.T($"{src.Drawables} model(s) in {slots} slot(s)") + (src.Props > 0 ? L.T($", {src.Props} prop(s)") : "")));
            Analysis.Add(new AnalysisRow(L.T("Variations"), src.HasYmt ? L.T($"its own {ped}.ymt") : L.T("none — written from the components")));
        }
        Analysis.Add(new AnalysisRow(L.T("Files"), L.T($"{src.Files.Count} model / texture file(s)")));
        if (src.Metas.Count > 0)
            Analysis.Add(new AnalysisRow(L.T("Own metas"), string.Join(", ", src.Metas.Values.Select(Path.GetFileName))));
        if (src.Editions.Count > 0)
            Analysis.Add(new AnalysisRow(L.T("Models for"), string.Join(" + ", src.Editions.Order().Select(e => e.DisplayName()))));
        foreach (var w in src.Warnings) Warnings.Add(w);

        SourceNotice = src.OwnInit
            ? L.T($"Found your own {Path.GetFileName(src.Metas[PedBuilder.InitType])}. It is packed instead of being written from a base ped; " +
                  $"the name from the form goes into it.")
            : "";
        HasSourceNotice = SourceNotice.Length > 0;

        _loading = true;
        if (ped is not null)
        {
            // Replace files: the game's ped they replace is the base; else one of the kind the name says
            if (_lib.Find(ped) is { } same) Base = same;
            else if (Base is null || Base.Name == _suggestedBase)
            {
                var guess = src.OwnKind ?? (PedMeta.Guess(ped) == PedGender.Female ? "female" : "male");
                Base = _lib.Default(guess);
            }
            _suggestedBase = Base?.Name;
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            var suggested = _vanilla.IsPed(ped) ? Unique(VehicleBuilder.CleanModel(folderName) ?? ped + "2") : ped;
            bool untouched = string.IsNullOrWhiteSpace(ModelName) || ModelName == _suggestedName;
            _suggestedName = suggested;
            if (untouched) ModelName = suggested;
        }
        _loading = false;
        OnPropertyChanged(nameof(YmtChoice));
        UpdatePlan();
    }

    private string? _suggestedBase;

    /// <summary>A name the game doesn't have: myped, myped2…</summary>
    private string Unique(string name)
    {
        if (name.Length > 24) name = name[..24];
        var n = name;
        for (int i = 2; _vanilla.IsPed(n) || n.Length == 0; i++) n = name + i;
        return n;
    }

    private Task LoadPreviewAsync(PedSource src)
    {
        if (src.Ped is not { } ped) return Preview.LoadAsync(null);
        var files = src.Files.Select(f => (f.Source, f.Image)).ToList();
        return Preview.LoadAsync(ct => AddonModelLoader.LoadPed(files, ped, ct));
    }

    // ================================================================ form

    public IReadOnlyList<PedKindItem> Kinds { get; }

    /// <summary>The game's peds an add-on can be based on, of the kind picked.</summary>
    [ObservableProperty] public partial IReadOnlyList<PedTemplate> Bases { get; set; } = [];

    [ObservableProperty] public partial PedKindItem Kind { get; set; } = null!;
    [ObservableProperty] public partial string ModelName { get; set; } = "";
    [ObservableProperty] public partial PedTemplate? Base { get; set; }
    [ObservableProperty] public partial bool RegenerateYmt { get; set; }

    /// <summary>The folder has its own .ymt: the choice to write one anyway is offered.</summary>
    public bool YmtChoice => _source is { HasYmt: true };

    /// <summary>With its own peds.meta the base isn't used.</summary>
    public bool BaseEnabled => _source is not { OwnInit: true };

    public string BaseNote => _source is { OwnInit: true }
        ? L.T("Not used — the folder’s own peds.meta says how the ped moves and behaves.")
        : Base is { } b
            ? L.T($"{b.Group} · {b.KindName} · {(b.Dlc == "base" ? L.T("original game") : b.Dlc)} — " +
                  $"movement, gestures, voice, personality and behaviour come from it.") +
              (b.IsAnimal ? " " + L.T("An animal’s model must have that animal’s skeleton.") : "")
            : L.T("The game’s ped the add-on moves, talks and behaves like — type its name (a_m_y_hipster_01, a_c_husky…).");

    partial void OnKindChanged(PedKindItem value)
    {
        Bases = value.Key is null ? _lib.All : [.. _lib.All.Where(t => t.Kind == value.Key)];
    }

    partial void OnBaseChanged(PedTemplate? value)
    {
        OnPropertyChanged(nameof(BaseNote));
        UpdatePlan();
    }

    partial void OnRegenerateYmtChanged(bool value) => UpdatePlan();

    /// <summary>RAGE asset names are lowercase [a-z0-9_].</summary>
    partial void OnModelNameChanged(string value)
    {
        var clean = VehicleBuilder.CleanModel(value) ?? "";
        if (value.EndsWith(' ') || value.EndsWith('_')) clean += "_";
        if (clean != value)
        {
            ModelName = clean;
            return;
        }
        UpdatePlan();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsEnhanced)) UpdatePlan();
    }

    private PedBuildOptions Options(string input, string outDir) => new()
    {
        InputFolder = input,
        OutDir = outDir,
        BasePed = Base?.Name,
        ModelName = ModelName.Trim('_'),
        RegenerateYmt = RegenerateYmt,
        Pack = Shell.PackRpf,
        Edition = Shell.Edition,
        DataDir = AppPaths.Data,
    };

    /// <summary>The names the ped will get and what's written — or why it can't be built yet.</summary>
    private void UpdatePlan()
    {
        if (_loading) return;
        Names.Clear();
        OnPropertyChanged(nameof(BaseEnabled));
        OnPropertyChanged(nameof(BaseNote));
        if (_source is not { } src)
        {
            HasNames = false;
            return;
        }
        var (n, error) = PedBuilder.Plan(src, Options(src.Folder, ""), _lib, _vanilla);
        if (n is null)
        {
            PlanError = error;
            HasNames = false;
            return;
        }
        PlanError = null;
        Names.Add(new AnalysisRow(L.T("Spawn name"), n.Name));
        Names.Add(new AnalysisRow(L.T("Pack"), L.T($"dlcpacks\\{n.Name}  ·  dlc_{n.Name}")));
        Names.Add(new AnalysisRow(L.T("Kind"), PedKinds.Name(n.Kind)));
        Names.Add(new AnalysisRow(L.T("Archive"), n.Streamed ? "streamedpeds.rpf" : "peds.rpf"));
        var written = new List<string> { n.Base is not null ? "peds.meta" : L.T("peds.meta (its own, with this name)") };
        if (n.WriteYmt) written.Add(n.Name + ".ymt");
        Names.Add(new AnalysisRow(L.T("Written"), string.Join(", ", written)));
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
        if (input.Length == 0) return (null, L.T("No folder with the ped’s models selected."));
        if (!Directory.Exists(input)) return (null, L.T("The folder with the ped’s models is not found."));
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

    private static PanelOutcome Run(PedBuildOptions o, Action<string> log)
    {
        try
        {
            var r = PedBuilder.Build(o, log);
            return r.DlcRpf is not null
                ? new PanelOutcome(true, L.T("Done"), L.T($"Add-on ped built — spawn name {r.Names.Name}:\n{r.Root}"), r.Root)
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
