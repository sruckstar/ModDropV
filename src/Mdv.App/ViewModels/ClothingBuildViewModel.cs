using System.Collections.ObjectModel;
using System.ComponentModel;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using Mdv.Core.Preview;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>A slot a model can go to, as the form's list shows it ("Tops · jbib", "Hats · p_head").</summary>
public sealed record ClothingSlotOption(bool Prop, int Slot, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A model of the folder in the form: which MP ped and slot it goes to, and where that puts it.</summary>
public sealed partial class ClothingItemRow : ObservableObject
{
    private readonly Action _changed;

    public ClothingItemRow(ClothingItem item, ClothingSlotOption? slot, int ped, Action changed)
    {
        _changed = changed;
        Item = item;
        Loading = true;
        Slot = slot;
        PedIndex = ped;
        Loading = false;
    }

    public ClothingItem Item { get; }
    public string Name => Item.Name;
    public string Detail => string.Join(" · ", new[]
    {
        Item.Key.Contains('/') ? Item.Key[..Item.Key.LastIndexOf('/')] : null,
        Item.Textures.Count == 0 ? L.T("no texture") : L.T($"{Item.Textures.Count} texture(s)"),
        Item.Alternatives.Count > 0 ? L.T($"{Item.Alternatives.Count} alternative(s)") : null,
        Item.Cloth is not null ? L.T("cloth") : null,
    }.OfType<string>());

    /// <summary>0 MP male, 1 MP female.</summary>
    [ObservableProperty] public partial int PedIndex { get; set; }
    [ObservableProperty] public partial ClothingSlotOption? Slot { get; set; }
    /// <summary>Where it goes in the pack ("→ jbib_003_u.ydd · Tops 3"), or what's missing.</summary>
    [ObservableProperty] public partial string Result { get; set; } = "";
    /// <summary>Set by hand in the form (the default ped no longer applies to it).</summary>
    public bool PedTouched { get; set; }
    public bool Loading { get; set; }

    public bool NeedsSlot => Slot is null;

    partial void OnPedIndexChanged(int value)
    {
        if (Loading) return;
        PedTouched = true;
        _changed();
    }

    partial void OnSlotChanged(ClothingSlotOption? value)
    {
        OnPropertyChanged(nameof(NeedsSlot));
        if (!Loading) _changed();
    }
}

/// <summary>
/// The modder's MP clothes: a folder of clothing models — named as the game's (<c>jbib_000_u.ydd</c>, FiveM's
/// <c>mp_m_freemode_01_x^…</c>) or anything, the slot then picked here — becomes an add-on collection of each MP ped,
/// numbered from 0 per slot, its ymt and shop meta written. The side column shows the models by slot and ped, the
/// names and what gets written; <see cref="ClothingBuilder"/> builds it.
/// </summary>
public sealed partial class ClothingBuildViewModel : ModPanelViewModel
{
    private readonly GameModels _game;
    private ClothingSource? _source;
    private int _generation;
    private bool _loading;
    private string? _suggestedName;

    public ClothingBuildViewModel(MainViewModel shell) : base(shell)
    {
        _game = GameModels.Load(AppPaths.Data);
        shell.PropertyChanged += OnShellChanged;
        SlotOptions = [.. Enumerable.Range(0, ClothingNames.Components.Length).Select(i =>
                               new ClothingSlotOption(false, i, $"{L.T(ClothingNames.TrainerComponents[i])} · {ClothingNames.Components[i]}")),
                           .. Enumerable.Range(0, ClothingNames.Anchors.Length).Select(i =>
                               new ClothingSlotOption(true, i, $"{L.T(ClothingNames.TrainerAnchors[i])} · p_{ClothingNames.Anchors[i]}"))];
        Peds = [L.T("MP male"), L.T("MP female")];
    }

    public override ModCategory Category => ModCategory.Clothing;

    public ModelPreview Preview { get; } = new();

    public IReadOnlyList<ClothingSlotOption> SlotOptions { get; }
    public IReadOnlyList<string> Peds { get; }

    // ================================================================ source

    [ObservableProperty] public partial string InputFolder { get; set; } = "";
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string RouteTitle { get; set; } = L.T("No source selected");
    [ObservableProperty] public partial string RouteDetail { get; set; } = EmptyDetail;
    [ObservableProperty] public partial string RouteBadge { get; set; } = "—";
    [ObservableProperty] public partial string SourceNotice { get; set; } = "";
    [ObservableProperty] public partial bool HasSourceNotice { get; set; }

    private static string EmptyDetail => L.T(
        "Pick the folder with the clothes’ models — .ydd with their .ytd (and .yld cloth): named as the game’s " +
        "(jbib_000_u.ydd), FiveM’s, or any name — you pick the slot then.");

    public ObservableCollection<AnalysisRow> Analysis { get; } = [];
    /// <summary>Every model of the folder with its ped and slot.</summary>
    public ObservableCollection<ClothingItemRow> Items { get; } = [];
    /// <summary>The models by slot and ped: "Tops · jbib" → "male 3 · female 1".</summary>
    public ObservableCollection<AnalysisRow> Grid { get; } = [];
    public ObservableCollection<AnalysisRow> Names { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty] public partial bool HasItems { get; set; }
    [ObservableProperty] public partial bool HasGrid { get; set; }
    [ObservableProperty] public partial bool HasNames { get; set; }

    [RelayCommand]
    private async Task BrowseInput()
    {
        var path = await Shell.Pick(L.T("Folder with the clothes’ models"));
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
        ClothingSource? src = null;
        try
        {
            src = await Task.Run(() => ClothingBuilder.Read(folder));
        }
        catch (Exception ex)
        {
            AppLog.Error("clothing source analysis failed", ex);
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
        Items.Clear();
        HasItems = false;
        RouteBadge = "—";
        RouteTitle = error is null ? L.T("No source selected") : L.T("Source unavailable");
        RouteDetail = error ?? EmptyDetail;
        SourceNotice = "";
        HasSourceNotice = false;
        _ = Preview.LoadAsync(null);
        UpdatePlan();
    }

    /// <summary>What the folder holds, and the form started from it: every model with the ped and slot its names give.</summary>
    private void Show(ClothingSource src, string folder)
    {
        Analysis.Clear();
        Warnings.Clear();
        int count = src.Items.Count;
        if (src.PrebuiltRpf is not null)
        {
            RouteBadge = "dlc.rpf";
            RouteTitle = L.T("A finished pack");
            RouteDetail = L.T($"{Path.GetFileName(src.PrebuiltRpf)} is already an add-on — there is nothing to build. Players install it as it is.");
        }
        else if (count == 0)
        {
            RouteBadge = "—";
            RouteTitle = L.T("No clothing found");
            RouteDetail = L.T("There is no clothing model (.ydd) in the folder.");
        }
        else
        {
            RouteBadge = L.T($"{count} model(s)");
            RouteTitle = count == 1 ? L.T($"Clothing {src.Items[0].Name}") : L.T($"{count} clothing models");
            RouteDetail = L.T("An add-on collection of each MP ped: numbered from 0 per slot, its .ymt and shop meta written.");
        }
        int named = src.Items.Count(i => i.Part is not null);
        if (count > 0)
            Analysis.Add(new AnalysisRow(L.T("Models"), string.Join(", ", new[]
            {
                named > 0 ? L.T($"{named} named as the game’s") : null,
                count - named > 0 ? L.T($"{count - named} of other names") : null,
            }.OfType<string>())));
        int tex = src.Items.Sum(i => i.Textures.Count);
        Analysis.Add(new AnalysisRow(L.T("Textures"), tex == 0 ? L.T("none") : L.T($"{tex} .ytd")));
        int alt = src.Items.Sum(i => i.Alternatives.Count), cloth = src.Items.Count(i => i.Cloth is not null);
        if (alt + cloth > 0)
            Analysis.Add(new AnalysisRow(L.T("Also"), string.Join(", ", new[]
            {
                alt > 0 ? L.T($"{alt} alternative(s)") : null,
                cloth > 0 ? L.T($"{cloth} cloth .yld") : null,
            }.OfType<string>())));
        if (src.IsFiveM) Analysis.Add(new AnalysisRow(L.T("Format"), L.T("FiveM resource")));
        if (src.Editions.Count > 0)
            Analysis.Add(new AnalysisRow(L.T("Models for"), string.Join(" + ", src.Editions.Order().Select(e => e.DisplayName()))));
        foreach (var w in src.Warnings) Warnings.Add(w);

        SourceNotice = src.Ignored.Count > 0
            ? L.T($"Not used: {string.Join(", ", src.Ignored.Select(Path.GetFileName))} — the .ymt and shop meta are written anew for the new numbers.")
            : "";
        HasSourceNotice = SourceNotice.Length > 0;

        _loading = true;
        var name = ClothingBuilder.CleanName(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))) ?? "clothes";
        if (string.IsNullOrWhiteSpace(CollectionName) || CollectionName == _suggestedName) CollectionName = name;
        _suggestedName = name;
        Items.Clear();
        foreach (var i in src.Items)
        {
            var slot = i.Slot is { } s ? SlotOptions.First(o => o.Prop == s.Prop && o.Slot == s.Slot) : null;
            var row = new ClothingItemRow(i, slot, (i.Female ?? DefaultFemale) ? 1 : 0, UpdatePlan);
            Items.Add(row);
        }
        HasItems = Items.Count > 0;
        _loading = false;
        UpdatePlan();
    }

    private Task LoadPreviewAsync(ClothingSource src)
    {
        var items = src.Items.Select(i => (i.File, (IReadOnlyList<string>)[.. i.Textures.Select(t => t.File)], i.Name,
                                           i.Key.Contains('/') ? i.Key[..i.Key.LastIndexOf('/')] : L.T("clothing"))).ToList();
        if (items.Count == 0) return Preview.LoadAsync(null);
        return Preview.LoadAsync(ct => AddonModelLoader.LoadClothing(items, ct));
    }

    // ================================================================ form

    [ObservableProperty] public partial string CollectionName { get; set; } = "";
    /// <summary>The ped of models whose folder names neither (0 male, 1 female).</summary>
    [ObservableProperty] public partial int DefaultPedIndex { get; set; }

    private bool DefaultFemale => DefaultPedIndex == 1;

    /// <summary>RAGE asset names are lowercase [a-z0-9_].</summary>
    partial void OnCollectionNameChanged(string value)
    {
        var clean = VehicleBuilder.CleanModel(value) ?? "";
        if (value.EndsWith(' ') || value.EndsWith('_')) clean += "_";
        if (clean != value)
        {
            CollectionName = clean;
            return;
        }
        UpdatePlan();
    }

    /// <summary>Models their folder gives no ped follow the default, unless set by hand.</summary>
    partial void OnDefaultPedIndexChanged(int value)
    {
        foreach (var r in Items.Where(r => r.Item.Female is null && !r.PedTouched))
        {
            r.Loading = true;
            r.PedIndex = value;
            r.Loading = false;
        }
        UpdatePlan();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsEnhanced)) UpdatePlan();
    }

    private ClothingBuildOptions Options(string input, string outDir) => new()
    {
        InputFolder = input,
        OutDir = outDir,
        Name = CollectionName.Trim('_'),
        DefaultFemale = DefaultFemale,
        Assign = Items.Where(r => r.Slot is not null)
                      .ToDictionary(r => r.Item.Key, r => new ClothingSlot(r.PedIndex == 1, r.Slot!.Prop, r.Slot.Slot), StringComparer.OrdinalIgnoreCase),
        Pack = Shell.PackRpf,
        Edition = Shell.Edition,
        DataDir = AppPaths.Data,
    };

    /// <summary>Where every model goes, the grid of slots, the names — or why it can't be built yet.</summary>
    private void UpdatePlan()
    {
        if (_loading) return;
        Names.Clear();
        Grid.Clear();
        if (_source is not { } src)
        {
            HasNames = HasGrid = false;
            return;
        }
        var (plan, error) = ClothingBuilder.Plan(src, Options(src.Folder, ""), _game);
        var entries = plan?.Entries.ToDictionary(e => e.Item) ?? [];
        foreach (var r in Items)
            r.Result = entries.TryGetValue(r.Item, out var e)
                ? $"→ {e.NewName} · {e.Where}"
                : r.Slot is null ? L.T("pick its slot — the name doesn’t say") : "";

        // the grid: every slot with models, how many for each ped
        foreach (var o in SlotOptions)
        {
            int m = Items.Count(r => r.Slot == o && r.PedIndex == 0), f = Items.Count(r => r.Slot == o && r.PedIndex == 1);
            if (m + f == 0) continue;
            Grid.Add(new AnalysisRow(o.Label, string.Join(" · ", new[]
            {
                m > 0 ? L.T($"male {m}") : null,
                f > 0 ? L.T($"female {f}") : null,
            }.OfType<string>())));
        }
        HasGrid = Grid.Count > 0;

        if (plan is null)
        {
            PlanError = error;
            HasNames = false;
            return;
        }
        PlanError = null;
        Names.Add(new AnalysisRow(L.T("Pack"), L.T($"dlcpacks\\{plan.Name}  ·  dlc_{plan.Name}")));
        Names.Add(new AnalysisRow(L.T("Collections"), string.Join(", ", plan.Collections.Select(c => c.Collection))));
        Names.Add(new AnalysisRow(L.T("Written"), string.Join(", ", plan.Collections.Select(c => $"{(c.Female ? ClothingNames.MpFemale : ClothingNames.MpMale)}_{c.Collection}.ymt")
                                                                   .Append(L.T("shop meta")).Append("content.xml"))));
        Names.Add(new AnalysisRow(L.T("Players"), L.T("ModDrop V installs it as new slots of the game’s last collection — the game can’t take a collection more.")));
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
        if (input.Length == 0) return (null, L.T("No folder with the clothes’ models selected."));
        if (!Directory.Exists(input)) return (null, L.T("The folder with the clothes’ models is not found."));
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

    private static PanelOutcome Run(ClothingBuildOptions o, Action<string> log)
    {
        try
        {
            var r = ClothingBuilder.Build(o, log);
            var colls = string.Join(", ", r.Plan.Collections.Select(c => c.Collection));
            return r.DlcRpf is not null
                ? new PanelOutcome(true, L.T("Done"), L.T($"MP clothes built — collections {colls}:\n{r.Root}"), r.Root)
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
