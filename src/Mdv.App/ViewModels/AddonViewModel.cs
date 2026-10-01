using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Index;
using Mdv.Core.Mods;
using Mdv.Core.Preview;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// A model shown in 3D (an add-on vehicle / ped): the thumbnail and the large dialog share it. The parts
/// list works as the weapon's: parts on the same "bone" (a ped's component slot) replace each other.
/// </summary>
public sealed partial class ModelPreview : ObservableObject
{
    private int _generation;
    private CancellationTokenSource? _cts;
    private bool _syncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(ShowPanel), nameof(Title), nameof(Stats), nameof(PartsHint))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial WeaponModel? Model { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPanel))]
    public partial bool IsLoading { get; set; }

    public bool HasPreview => Model is not null;
    public bool ShowPanel => HasPreview || IsLoading;

    /// <summary>Ids of the parts switched on — a new set on every change, so views notice.</summary>
    [ObservableProperty] public partial IReadOnlyCollection<string>? Visible { get; set; }
    [ObservableProperty] public partial bool Textured { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Spinning))]
    public partial bool AutoRotate { get; set; }

    [ObservableProperty] public partial int ResetCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Spinning))]
    public partial bool IsOpen { get; set; }

    public bool Spinning => IsOpen && AutoRotate;

    public ObservableCollection<PreviewPieceViewModel> Pieces { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    public string Title => Model?.Name ?? "";

    /// <summary>What the parts list means: a ped's drawables take turns in their slot; a vehicle misses the game's shared textures.</summary>
    public string PartsHint => Pieces.Any(p => p.Bone is { } b && Pieces.Count(o => o.Bone == b) > 1)
        ? L.T("A ped wears one drawable per slot, as in the game — pick another to see it.")
        : L.T("Textures the game shares between vehicles aren’t in the mod: those surfaces are drawn plain.");

    public string Stats
    {
        get
        {
            if (Model is not { } m) return "";
            var tris = m.Pieces.Where(p => Visible?.Contains(p.Id) ?? p.DefaultVisible).Sum(p => p.Triangles);
            return string.Format(CultureInfo.InvariantCulture, L.T("{0:#,0} triangles shown · {1} texture(s) · {2} part(s)"),
                                 tris, m.TextureCount, m.Pieces.Count);
        }
    }

    /// <summary>Load a model in the background (null: clear); a newer call wins.</summary>
    public async Task LoadAsync(Func<CancellationToken, WeaponModel?>? load)
    {
        Clear();
        if (load is null) return;
        int gen = _generation;
        var cts = _cts = new CancellationTokenSource();
        IsLoading = true;
        WeaponModel? model = null;
        try
        {
            model = await Task.Run(() => load(cts.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error("3D preview: loading the model failed", ex);
        }
        if (gen != _generation) return;
        IsLoading = false;
        if (model is null || model.Pieces.All(p => p.IsEmpty)) return;
        foreach (var p in model.Pieces.Where(p => !p.IsEmpty)) Pieces.Add(new PreviewPieceViewModel(p, OnPieceChanged));
        foreach (var w in model.Warnings) Warnings.Add(w);
        Visible = VisibleIds();
        Model = model;
        AppLog.Info($"3D preview: {model.Name}, {model.Pieces.Count} part(s), {model.Triangles} triangles, {model.TextureCount} texture(s)");
    }

    public void Clear()
    {
        ++_generation;
        _cts?.Cancel();
        _cts = null;
        IsLoading = false;
        Model = null;
        Pieces.Clear();
        Warnings.Clear();
        Visible = null;
        IsOpen = false;
    }

    private void OnPieceChanged(PreviewPieceViewModel piece)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            if (piece.IsOn && piece.Bone is { } bone)
                foreach (var other in Pieces)
                    if (other != piece && other.Bone == bone) other.IsOn = false;
        }
        finally
        {
            _syncing = false;
        }
        Visible = VisibleIds();
        OnPropertyChanged(nameof(Stats));
    }

    private HashSet<string> VisibleIds() => Pieces.Where(p => p.IsOn).Select(p => p.Id).ToHashSet();

    [RelayCommand(CanExecute = nameof(HasPreview))]
    private void Open() => IsOpen = true;

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void ResetView() => ResetCount++;
}

/// <summary>A vehicle / ped the add-on brings: its in-game name, the spawn name, what else is known.</summary>
public sealed record AddonItemRow(string Title, string Spawn, string Detail)
{
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>One finding of the checks against the game.</summary>
public sealed partial class AddonCheckRow(AddonCheck check) : ObservableObject
{
    public AddonCheck Check { get; } = check;
    public string Title => Check.Title;
    public string Detail => Check.Detail;
    public bool IsOk => Check.Level == CheckLevel.Ok;
    public bool IsInfo => Check.Level == CheckLevel.Info;
    public bool IsProblem => Check.Level is CheckLevel.Warn or CheckLevel.Block;
    public bool HasLink => Check.Link is not null;

    [RelayCommand]
    private void OpenLink()
    {
        if (Check.Link is not { } link) return;
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("open check link failed", ex);
        }
    }
}

/// <summary>
/// An add-on vehicle or ped from the drop: what it brings (in-game names, spawn names), the dlcpacks folder
/// it goes into, the Replace version when the mod has one, a 3D preview, and the checks against the selected
/// game (taken spawn names, modkit ids, the same pack installed twice, the edition, add-on limits) — run
/// again whenever the game, its edition or the pack name changes.
/// </summary>
public sealed partial class AddonViewModel : FileModViewModel
{
    private AddonPackage? _pkg;
    private int _generation;
    private bool _loading;

    public AddonViewModel(MainViewModel shell) : base(shell)
    {
        shell.PropertyChanged += OnShellChanged;
    }

    public override ModCategory Category => _pkg?.Kind ?? ModCategory.Vehicle;
    public override ModPackage? Package => _pkg;

    public ModelPreview Preview { get; } = new();

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string KindBadge { get; set; } = "vehicle";
    /// <summary>"VEHICLE" / "PEDS" — the form card's heading.</summary>
    [ObservableProperty] public partial string Heading { get; set; } = "ADD-ON";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string SourceText { get; set; } = "";

    public ObservableCollection<AddonItemRow> Items { get; } = [];
    public ObservableCollection<AddonCheckRow> Checks { get; } = [];

    [ObservableProperty] public partial string PackName { get; set; } = "";
    [ObservableProperty] public partial string PackDetail { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPack))]
    public partial bool HasReplace { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallAsAddon), nameof(ShowPack), nameof(ShowChecks), nameof(Note), nameof(SideHeading), nameof(ShowGaps))]
    public partial bool InstallAsReplace { get; set; }

    public bool InstallAsAddon
    {
        get => !InstallAsReplace;
        set => InstallAsReplace = !value;
    }

    public bool ShowPack => !InstallAsReplace;
    public bool ShowChecks => !InstallAsReplace;
    public string SideHeading => InstallAsReplace ? "PREVIEW" : "CHECKS";

    [ObservableProperty] public partial string ReplaceText { get; set; } = "";

    /// <summary>Alternatives of the ped's components the mod ships: the ones ticked go in place of its own.</summary>
    public ObservableCollection<PedVariantRow> Variants { get; } = [];
    [ObservableProperty] public partial bool HasVariants { get; set; }

    /// <summary>The mod has peds without a peds.meta: one is written, from the template chosen here.</summary>
    [ObservableProperty] public partial bool HasNewPeds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PedIsMale))]
    public partial bool PedIsFemale { get; set; }

    public bool PedIsMale
    {
        get => !PedIsFemale;
        set => PedIsFemale = !value;
    }

    partial void OnPedIsFemaleChanged(bool value)
    {
        if (_loading || _pkg?.Compose is not { } spec) return;
        foreach (var p in spec.NewPeds) p.Gender = value ? PedGender.Female : PedGender.Male;
        ShowItems();
    }

    /// <summary>Its vehicles name a handling / layout neither the mod nor the game has: taken from the game vehicle picked here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGaps))]
    public partial bool HasGaps { get; set; }

    public bool ShowGaps => HasGaps && !InstallAsReplace;
    [ObservableProperty] public partial string GapText { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<VehicleTemplate> GapBases { get; set; } = [];
    [ObservableProperty] public partial VehicleTemplate? GapBase { get; set; }

    partial void OnGapBaseChanged(VehicleTemplate? value)
    {
        if (_loading || _pkg is null || _pkg.GapBase == value?.Model) return;
        _pkg.GapBase = value?.Model;
        _pkg.Checks = null;
        _ = CheckAsync();
    }

    [ObservableProperty] public partial bool FixKits { get; set; } = true;
    [ObservableProperty] public partial bool HasKitFixes { get; set; }

    [ObservableProperty] public partial bool IsChecking { get; set; }
    [ObservableProperty] public partial string CheckStatus { get; set; } = "";
    [ObservableProperty] public partial bool ChecksNeedAttention { get; set; }

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

    /// <summary>How it goes into the game, for the note under the checks.</summary>
    public string Note => InstallAsReplace
        ? L.T($"The Replace version's files go into copies of the game's archives under {ModsLayout.RootRel(Shell.GameFolder.Trim())}, where the game " +
          $"loads them from — the game's own files stay untouched. Switch it off or remove it any time in the Library.")
        : L.T($"It goes into {ModsLayout.DlcpacksShown(Shell.GameFolder.Trim())} as a pack of its own and into dlclist.xml — the game's files " +
          $"stay untouched. Switch it off or remove it any time in the Library.");

    partial void OnInstallAsReplaceChanged(bool value)
    {
        if (_loading || _pkg is null) return;
        _pkg.UseReplace = value && _pkg.Replace is not null;
        ShowItems();
        _ = PreviewAsync();
    }

    /// <summary>The vehicles / peds the add-on brings — or, installed as a replacement, the game's ones it replaces.</summary>
    private void ShowItems()
    {
        Items.Clear();
        if (_pkg is not { } pkg) return;
        bool ped = pkg.Kind == ModCategory.Ped;
        var c = pkg.Content;
        if (pkg.UseReplace && pkg.Replace is { } r)
        {
            foreach (var name in r.Replaces.DefaultIfEmpty(string.Join(", ", r.Files.Select(f => f.Name).Take(3))))
                Items.Add(new AddonItemRow(name, name, L.T($"the game's own {(ped ? "ped" : "vehicle")} — replaced by the mod's models")));
            return;
        }
        if (ped)
            foreach (var p in c.Peds)
            {
                var made = pkg.Compose?.NewPeds.FirstOrDefault(n => n.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                var detail = made is not null
                    ? L.T($"no peds.meta in the mod — a {made.Gender.ToString().ToLowerInvariant()} ped's entry is written for it") +
                      (made.Streamed ? " · streamed" : "")
                    : p.PedType is { } t ? Pretty(t) : "";
                Items.Add(new AddonItemRow(p.Name, p.Name, detail));
            }
        else
            foreach (var v in c.Vehicles)
            {
                var detail = string.Join(" · ", new[] { v.Class is { } cl ? Pretty(cl.Replace("VC_", "")) : null,
                                                        c.Label(v.Make) ?? v.Make }.OfType<string>());
                Items.Add(new AddonItemRow(c.DisplayName(v) ?? v.Model, v.Model, detail));
            }
    }

    partial void OnFixKitsChanged(bool value)
    {
        if (_loading || _pkg is null) return;
        _pkg.FixKits = value;
        _ = CheckAsync();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_pkg is not null && e.PropertyName is nameof(MainViewModel.GameFolder) or nameof(MainViewModel.IsEnhanced))
        {
            _pkg.Checks = null;
            _ = CheckAsync();
        }
    }

    protected override string? NotReady()
    {
        if (_pkg is null) return null;
        if (_pkg.UseReplace) return null;
        if (IsChecking) return L.T("The add-on is still being checked against the game — wait a moment.");
        if (AddonPackHandler.Clean(PackName) is null) return L.T("Give the pack a folder name (latin letters, digits, _).");
        return _pkg.Checks?.Blocking;
    }

    /// <summary>Show an add-on (null: none): what it brings, its preview, and the checks against the selected game.</summary>
    public Task UseAsync(AddonPackage? pkg)
    {
        _pkg = pkg;
        ++_generation;
        Items.Clear();
        Checks.Clear();
        Variants.Clear();
        if (pkg?.Compose is { Variants.Count: > 0 } withVariants)
        {
            var rows = new List<PedVariantRow>();
            foreach (var v in withVariants.Variants) rows.Add(new PedVariantRow(v, rows));
            foreach (var row in rows) Variants.Add(row);
        }
        HasVariants = Variants.Count > 0;
        if (pkg is null)
        {
            SetWarnings([]);
            HasReplace = false;
            HasKitFixes = false;
            HasNewPeds = false;
            HasGaps = false;
            _ = Preview.LoadAsync(null);
            return Task.CompletedTask;
        }
        _loading = true;
        bool ped = pkg.Kind == ModCategory.Ped;
        Name = pkg.Name;
        KindBadge = ped ? "ped" : "vehicle";
        var c = pkg.Content;
        int n = c.SpawnNames.Count();
        Heading = ped ? (n > 1 ? "PEDS" : "PED") : (n > 1 ? "VEHICLES" : "VEHICLE");
        ShowItems();
        HasNewPeds = pkg.Compose is { NewPeds.Count: > 0 };
        PedIsFemale = pkg.Compose is { NewPeds: [var first, ..] } && first.Gender == PedGender.Female;
        SourceText = pkg.Finished is { } f ? L.T($"A finished add-on pack ({f.Device ?? "dlc.rpf"})")
            : pkg.Compose is { NewPeds.Count: > 0 } ? L.T("Models only, no peds.meta — ModDrop V writes one; all packed into a dlc.rpf on install")
            : pkg.Compose is { Resources.Count: > 0 } s ? L.T($"A FiveM resource ({string.Join(", ", s.Resources)}) — packed into a dlc.rpf on install")
            : L.T("Loose models and metas — packed into a dlc.rpf on install");
        Summary = string.Join("  ·  ", pkg.Parts);
        HasReplace = pkg.Replace is not null;
        InstallAsReplace = pkg.UseReplace;
        ReplaceText = pkg.Replace is not { } r ? ""
            : r.Replaces.Count > 0 ? L.T($"Replace — in place of the game's {string.Join(", ", r.Replaces)}; no new spawn name")
            : L.T($"Replace — in place of the game's own ({string.Join(", ", r.Files.Select(x => x.Name).Take(4))}{(r.Files.Count > 4 ? ", …" : "")})");
        FixKits = pkg.FixKits;
        HasGaps = pkg.Gaps.Count > 0;
        GapBases = HasGaps ? VehicleTemplates.Load(pkg.DataDir).All : [];
        GapBase = AddonPackHandler.GapBaseOf(pkg);
        GapText = HasGaps
            ? L.T($"Its vehicles.meta names {string.Join(", ", pkg.Gaps.Select(g => g.Text).Distinct())}, which neither the mod nor the game has — " +
                  $"without it the game crashes spawning the vehicle. It’s taken from this game vehicle — pick one close to it.")
            : "";
        PackName = pkg.PackName;
        _loading = false;
        SetWarnings(pkg.Warnings);
        _ = PreviewAsync();
        return CheckAsync();
    }

    private static string Pretty(string s) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Replace('_', ' ').ToLowerInvariant());

    private Task PreviewAsync()
    {
        if (_pkg is not { } pkg) return Preview.LoadAsync(null);
        if (pkg.UseReplace && pkg.Replace is { } r) return Preview.LoadAsync(ct => AddonModelLoader.Load(r, ct));
        return Preview.LoadAsync(ct => AddonModelLoader.Load(pkg, ct));
    }

    /// <summary>Check the add-on against the selected game in the background; a newer check wins.</summary>
    private async Task CheckAsync()
    {
        if (_pkg is not { } pkg) return;
        int gen = ++_generation;
        var game = Shell.GameFolder.Trim();
        UpdatePackDetail();
        if (game.Length == 0 || !Directory.Exists(game))
        {
            Checks.Clear();
            IsChecking = false;
            ChecksNeedAttention = false;
            CheckStatus = L.T("Choose the GTA V folder — the add-on is checked against what the game already has.");
            return;
        }
        IsChecking = true;
        CheckStatus = L.T("Checking against the game…");
        var target = MainViewModel.TargetFor(game, Shell.Edition);
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
                    AppLog.Error("add-on checks: the game index could not be read", ex);
                }
                return AddonPackHandler.Check(pkg, target, index);
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("checking the add-on failed", ex);
            error = L.T($"The game could not be checked: {ex.Message}");
        }
        if (gen != _generation) return;
        IsChecking = false;
        Checks.Clear();
        if (report is null)
        {
            CheckStatus = error ?? "";
            return;
        }
        foreach (var c in report.Items.OrderByDescending(c => c.Level)) Checks.Add(new AddonCheckRow(c));
        HasKitFixes = report.KitFixes.Count > 0;
        int problems = report.Items.Count(i => i.Level is CheckLevel.Warn or CheckLevel.Block);
        ChecksNeedAttention = problems > 0;
        CheckStatus = report.Blocking is not null ? L.T("It can't go into this game.")
            : problems > 0 ? (problems == 1 ? L.T("1 thing to look at — it can still be installed.") : L.T($"{problems} things to look at — it can still be installed."))
            : L.T("Nothing clashes with what the game already has.");
        _loading = true;
        PackName = pkg.PackName;                      // a taken name was changed to a free one
        _loading = false;
        UpdatePackDetail();
    }

    private void UpdatePackDetail()
    {
        if (_pkg is not { } pkg) return;
        PackDetail = L.T($"mods\\update\\x64\\dlcpacks\\{pkg.PackName}  ·  mounted as {pkg.Device}") +
                     (pkg.PackNameFrom is { } from ? L.T($"  ·  name from {from}") : "");
    }
}
