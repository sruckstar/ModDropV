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

/// <summary>A vehicle class in the picker: VC_SUPER shown as "Super".</summary>
public sealed record VehicleClassItem(string Key, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The modder's add-on vehicle: a folder with the vehicle's models (Replace files too — they're renamed), a game
/// vehicle picked as the base (handling, layout, cameras, class and sound come from it), the name and make shown in
/// the game, and a modkit for the tuning shops. The side column shows what the folder holds, what gets written and
/// the names the vehicle gets; <see cref="VehicleBuilder"/> builds it.
/// </summary>
public sealed partial class VehicleBuildViewModel : ModPanelViewModel
{
    private readonly VehicleTemplates _lib;
    private readonly VanillaModels _vanilla;
    private VehicleSource? _source;
    private int _generation;
    private bool _loading;
    /// <summary>Field → the value last filled in from a source; replaced by the next source unless edited.</summary>
    private readonly Dictionary<string, string> _suggested = [];

    public VehicleBuildViewModel(MainViewModel shell) : base(shell)
    {
        _lib = VehicleTemplates.Load(AppPaths.Data);
        _vanilla = VanillaModels.Load(AppPaths.Data);
        Bases = _lib.All;
        Classes = [.. VehicleClasses.All.Select(c => new VehicleClassItem(c, VehicleClasses.Name(c)))];
        shell.PropertyChanged += OnShellChanged;
    }

    public override ModCategory Category => ModCategory.Vehicle;

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
        "Pick the folder with the vehicle’s models — name.yft, name_hi.yft, name.ytd (Replace files of a game vehicle too) — " +
        "and, if you wrote them, its own metas.");

    public ObservableCollection<AnalysisRow> Analysis { get; } = [];
    public ObservableCollection<AnalysisRow> Names { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty] public partial bool HasNames { get; set; }

    [RelayCommand]
    private async Task BrowseInput()
    {
        var path = await Shell.Pick(L.T("Folder with the vehicle’s models"));
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
        VehicleSource? src = null;
        try
        {
            src = await Task.Run(() => VehicleBuilder.Read(folder));
        }
        catch (Exception ex)
        {
            AppLog.Error("vehicle source analysis failed", ex);
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

    /// <summary>What the folder holds, and the form started from it (a Replace file's game vehicle is the base).</summary>
    private void Show(VehicleSource src, string folder)
    {
        Analysis.Clear();
        Warnings.Clear();
        var model = src.Model;
        if (src.PrebuiltRpf is not null)
        {
            RouteBadge = "dlc.rpf";
            RouteTitle = L.T("A finished pack");
            RouteDetail = L.T($"{Path.GetFileName(src.PrebuiltRpf)} is already an add-on — there is nothing to build. Players install it as it is.");
        }
        else if (src.Models.Count == 0)
        {
            RouteBadge = "—";
            RouteTitle = L.T("No vehicle found");
            RouteDetail = src.IsFiveM
                ? L.T("This is a FiveM resource without models ModDrop V could read. Players install FiveM vehicles as they are — drop it in the For Players mode.")
                : L.T("There is no vehicle model (.yft) in the folder.");
        }
        else if (model is null)
        {
            RouteBadge = L.T($"{src.Models.Count} vehicles");
            RouteTitle = L.T("Several vehicles");
            RouteDetail = L.T($"The folder holds {string.Join(", ", src.Models)} — build them one at a time, each from its own folder.");
        }
        else
        {
            bool replace = _vanilla.IsVehicle(model);
            RouteBadge = src.OwnInit ? L.T("own metas") : replace ? L.T("replace → add-on") : L.T("models");
            RouteTitle = replace ? L.T($"Replace files of the game’s {model}") : src.OwnInit ? L.T($"{model} with its own metas") : L.T($"Vehicle {model}");
            RouteDetail = replace
                ? L.T("They become an add-on of their own: the files get the new model name, the metas are written from the base vehicle.")
                : src.OwnInit
                    ? L.T("Its vehicles.meta is used — with the name, make, class and sound from the form; what the folder lacks is written from the base vehicle.")
                    : L.T("The metas are written from the base vehicle; the name and make go into the game’s text.");
        }
        if (src.Model is { } m)
        {
            var own = new[] { "", "_hi" }.Where(s => src.Has(m + s + ".yft")).Select(s => m + s + ".yft")
                          .Concat(new[] { "", "+hi" }.Where(s => src.Has(m + s + ".ytd")).Select(s => m + s + ".ytd"));
            Analysis.Add(new AnalysisRow(L.T("Model"), string.Join("  ", own)));
        }
        Analysis.Add(new AnalysisRow(L.T("Files"), L.T($"{src.Files.Count} model / texture file(s)")));
        if (src.Parts.Count > 0)
            Analysis.Add(new AnalysisRow(L.T("Parts"), string.Join(", ", src.Parts.Take(8)) + (src.Parts.Count > 8 ? ", …" : "")));
        if (src.Metas.Count > 0)
            Analysis.Add(new AnalysisRow(L.T("Own metas"), string.Join(", ", src.Metas.Values.Select(Path.GetFileName))));
        if (src.Audio.Count > 0) Analysis.Add(new AnalysisRow(L.T("Sounds"), L.T($"{src.Audio.Count} file(s) — packed with it")));
        if (src.Editions.Count > 0)
            Analysis.Add(new AnalysisRow(L.T("Models for"), string.Join(" + ", src.Editions.Order().Select(e => e.DisplayName()))));
        foreach (var w in src.Warnings) Warnings.Add(w);

        SourceNotice = src.OwnInit
            ? L.T($"Found your own {string.Join(", ", src.Metas.Values.Select(Path.GetFileName))}. They are packed instead of " +
                  $"being written from the base vehicle; the names below start from its vehicles.meta and go into it.")
            : "";
        HasSourceNotice = SourceNotice.Length > 0;

        _loading = true;
        if (model is not null)
        {
            if (_lib.Find(model) is { } same) Base = same;            // Replace files: the game's vehicle they replace is the base
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            var suggested = _vanilla.IsVehicle(model) ? Unique(VehicleBuilder.CleanModel(folderName)?.Replace("_", "") ?? model + "2") : model;
            ModelName = Suggest(nameof(ModelName), ModelName, suggested);
            var ownName = src.OwnInit ? VehicleBuilder.OwnDisplayName(src, _lib) : null;
            DisplayName = Suggest(nameof(DisplayName), DisplayName,
                                  ownName ?? (_vanilla.IsVehicle(model) || model != suggested ? folderName : Pretty(src.OwnGameName ?? model)));
            if (src.OwnInit)
            {
                Base ??= _lib.Find(src.OwnSound) ?? _lib.Find(src.OwnHandlingId);   // the game vehicle it sounds or drives like
                // its own vehicles.meta says the make, class and sound; the base only fills what the folder lacks
                Make = Suggest(nameof(Make), Make, VehicleBuilder.OwnMakeText(src, _lib) ?? "");
                if (src.OwnClass is { } cls && Classes.FirstOrDefault(c => c.Key.Equals(cls, StringComparison.OrdinalIgnoreCase)) is { } item)
                    Class = item;
                Sound = _lib.Find(src.OwnSound);
            }
        }
        _loading = false;
        UpdatePlan();
    }

    /// <summary>A model name the game doesn't have: mycar, mycar2…</summary>
    private string Unique(string name)
    {
        if (name.Length > 16) name = name[..16];
        var n = name;
        for (int i = 2; _vanilla.IsVehicle(n) || n.Length == 0; i++) n = name + i;
        return n;
    }

    private static string Pretty(string s) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Replace('_', ' '));

    private string Suggest(string field, string current, string next)
    {
        bool untouched = string.IsNullOrWhiteSpace(current) || (_suggested.TryGetValue(field, out var prev) && current == prev);
        _suggested[field] = next;
        return untouched ? next : current;
    }

    private Task LoadPreviewAsync(VehicleSource src)
    {
        if (src.Model is not { } model || !src.Has(model + ".yft")) return Preview.LoadAsync(null);
        var files = src.Files.ToList();
        return Preview.LoadAsync(ct => AddonModelLoader.LoadVehicle(files, model, ct));
    }

    // ================================================================ form

    /// <summary>The game's vehicles an add-on can be based on.</summary>
    public IReadOnlyList<VehicleTemplate> Bases { get; }
    public IReadOnlyList<VehicleClassItem> Classes { get; }

    [ObservableProperty] public partial string DisplayName { get; set; } = "";
    [ObservableProperty] public partial string Make { get; set; } = "";
    [ObservableProperty] public partial string ModelName { get; set; } = "";
    [ObservableProperty] public partial VehicleTemplate? Base { get; set; }
    [ObservableProperty] public partial VehicleClassItem? Class { get; set; }
    /// <summary>A game vehicle whose engine sound it takes (null: the base's).</summary>
    [ObservableProperty] public partial VehicleTemplate? Sound { get; set; }
    [ObservableProperty] public partial bool Modkit { get; set; } = true;

    public bool KitEnabled => _source is not { OwnVariation: true } && _source is not { OwnCarcols: true };

    public string BaseNote => Base is { } b
        ? L.T($"{b.ClassName} · {(b.Dlc == "base" ? L.T("original game") : b.Dlc)} — " +
              $"handling, layout, cameras and sound come from it.")
        : L.T("The game’s vehicle the add-on drives, sits and sounds like — type its name or model.");

    partial void OnBaseChanged(VehicleTemplate? oldValue, VehicleTemplate? newValue)
    {
        // the class follows the base unless it was picked by hand or its own vehicles.meta says it; so does the make
        if (_source is not { OwnClass: not null } && (Class is null || Class.Key == oldValue?.Class))
            Class = Classes.FirstOrDefault(c => c.Key == newValue?.Class);
        if (_source is not { OwnInit: true } && (Make.Length == 0 || Make == oldValue?.Make)) Make = newValue?.Make ?? "";
        OnPropertyChanged(nameof(BaseNote));
        UpdatePlan();
    }

    partial void OnDisplayNameChanged(string value) => UpdatePlan();
    partial void OnMakeChanged(string value) => UpdatePlan();
    partial void OnClassChanged(VehicleClassItem? value) => UpdatePlan();
    partial void OnSoundChanged(VehicleTemplate? value) => UpdatePlan();
    partial void OnModkitChanged(bool value) => UpdatePlan();

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

    private VehicleBuildOptions Options(string input, string outDir) => new()
    {
        InputFolder = input,
        OutDir = outDir,
        BaseModel = Base?.Model ?? "",
        ModelName = ModelName.Trim('_'),
        DisplayName = DisplayName.Trim(),
        Make = Make.Trim(),
        VehicleClass = Class?.Key,
        Sound = Sound?.Model,
        Modkit = Modkit,
        Pack = Shell.PackRpf,
        Edition = Shell.Edition,
        DataDir = AppPaths.Data,
    };

    /// <summary>The names the vehicle will get (spawn name, handling, labels, modkit) — or why it can't be built yet.</summary>
    private void UpdatePlan()
    {
        if (_loading) return;
        Names.Clear();
        OnPropertyChanged(nameof(KitEnabled));
        if (_source is not { } src)
        {
            HasNames = false;
            return;
        }
        var (n, error) = VehicleBuilder.Plan(src, Options(src.Folder, ""), _lib, _vanilla);
        if (n is null)
        {
            PlanError = error;
            HasNames = false;
            return;
        }
        PlanError = null;
        Names.Add(new AnalysisRow(L.T("Spawn name"), n.Model));
        Names.Add(new AnalysisRow(L.T("In the game"), string.Join(" ", new[] { n.MakeText ?? (n.MakeKey is null ? null : Base?.Make), n.DisplayName }
                                                                  .Where(s => !string.IsNullOrEmpty(s)))));
        Names.Add(new AnalysisRow(L.T("Pack"), L.T($"dlcpacks\\{n.Model}  ·  dlc_{n.Model}")));
        Names.Add(new AnalysisRow(L.T("Handling"), n.Handling));
        Names.Add(new AnalysisRow(L.T("Name label"), n.GameName + (n.MakeText is not null ? $"  ·  {n.MakeKey}" : "")));
        Names.Add(new AnalysisRow(L.T("Modkit"), n.KitId is { } id ? L.T($"id {id} — engine, brakes, gearbox, armour, horns") : L.T("none")));
        Names.Add(new AnalysisRow(L.T("Sound"), n.Sound.ToLowerInvariant()));
        var written = new List<string>();
        written.Add(src.OwnInit ? L.T("vehicles.meta (its own, with these names)") : "vehicles.meta");
        if (!src.OwnHandling) written.Add("handling.meta");
        if (!src.OwnVariation) written.Add("carvariations.meta");
        if (n.KitName is not null) written.Add("carcols.meta");
        Names.Add(new AnalysisRow(L.T("Written"), written.Count > 0 ? string.Join(", ", written) : L.T("nothing — the folder has every meta")));
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
        if (input.Length == 0) return (null, L.T("No folder with the vehicle’s models selected."));
        if (!Directory.Exists(input)) return (null, L.T("The folder with the vehicle’s models is not found."));
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

    private static PanelOutcome Run(VehicleBuildOptions o, Action<string> log)
    {
        try
        {
            var r = VehicleBuilder.Build(o, log);
            return r.DlcRpf is not null
                ? new PanelOutcome(true, L.T("Done"), L.T($"Add-on vehicle built — spawn name {r.Names.Model}:\n{r.Root}"), r.Root)
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
