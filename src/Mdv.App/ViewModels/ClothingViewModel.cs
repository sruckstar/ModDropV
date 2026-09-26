using System.Collections.ObjectModel;
using System.ComponentModel;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Index;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mdv.App.ViewModels;

/// <summary>
/// Clothes from the drop. As an add-on — an MP clothes pack, a FiveM clothing resource, or loose models packed as a
/// collection of their own for the MP male / female — it shows the collections, the dlcpacks folder and the checks
/// against the game. As a replacement it shows whose clothes they are (story characters, the MP peds, any MP collection
/// of the game), where each file goes, and which models get new slots in the wearer's ymt ("as new clothes").
/// </summary>
public sealed partial class ClothingViewModel : FileModViewModel
{
    private AddonPackage? _pkg;
    private int _checkGen, _resolveGen;
    private bool _loading;
    private string? _wearersFor;

    public ClothingViewModel(MainViewModel shell) : base(shell)
    {
        shell.PropertyChanged += OnShellChanged;
    }

    public override ModCategory Category => ModCategory.Clothing;
    public override ModPackage? Package => _pkg;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string SourceText { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";

    // ---- add-on or replace
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallAs))]
    public partial bool HasReplace { get; set; }

    /// <summary>A story character's clothes: only the replacement goes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallAs), nameof(CanAddon))]
    public partial bool ReplaceOnly { get; set; }

    public bool ShowInstallAs => HasReplace;
    public bool CanAddon => !ReplaceOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallAsAddon), nameof(ShowAddon), nameof(Note), nameof(SideHeading))]
    public partial bool InstallAsReplace { get; set; }

    public bool InstallAsAddon
    {
        get => !InstallAsReplace;
        set => InstallAsReplace = !value;
    }

    public bool ShowAddon => !InstallAsReplace;
    public string SideHeading => InstallAsReplace ? "HOW IT GOES IN" : "CHECKS";
    [ObservableProperty] public partial string AddonText { get; set; } = "";
    [ObservableProperty] public partial string ReplaceText { get; set; } = "";

    partial void OnInstallAsReplaceChanged(bool value)
    {
        if (_loading || _pkg is null) return;
        _pkg.UseReplace = value && _pkg.Replace is not null;
        if (value) _ = ResolveAsync();
        else _ = CheckAsync();
    }

    // ---- add-on
    public ObservableCollection<AddonItemRow> Items { get; } = [];

    /// <summary>Loose models packed as a collection of their own: for the MP male or female.</summary>
    [ObservableProperty] public partial bool HasLoose { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LooseIsMale))]
    public partial bool LooseIsFemale { get; set; }

    public bool LooseIsMale
    {
        get => !LooseIsFemale;
        set => LooseIsFemale = !value;
    }

    partial void OnLooseIsFemaleChanged(bool value)
    {
        if (_loading || _pkg?.LooseClothing is not { } c) return;
        c.Ped = value ? ClothingNames.MpFemale : ClothingNames.MpMale;
        _pkg.Checks = null;
        ShowItems();
        _ = CheckAsync();
    }

    [ObservableProperty] public partial string PackName { get; set; } = "";
    [ObservableProperty] public partial string PackDetail { get; set; } = "";

    partial void OnPackNameChanged(string value)
    {
        if (_loading || _pkg is null) return;
        if (AddonPackHandler.Clean(value) is { } clean && clean != _pkg.PackName)
        {
            _pkg.PackName = clean;
            _pkg.Checks = null;
            ShowItems();
            _ = CheckAsync();
        }
    }

    public ObservableCollection<AddonCheckRow> Checks { get; } = [];
    [ObservableProperty] public partial bool IsChecking { get; set; }
    [ObservableProperty] public partial string CheckStatus { get; set; } = "";

    // ---- replace
    public ObservableCollection<WearerChoice> Wearers { get; } = [];
    [ObservableProperty] public partial WearerChoice? SelectedWearer { get; set; }
    public ObservableCollection<ReplaceFileRow> Files { get; } = [];
    [ObservableProperty] public partial bool IsResolving { get; set; }
    [ObservableProperty] public partial string ReplaceStatus { get; set; } = "";

    /// <summary>Every model goes in as new clothes (new slots) instead of in place of the wearer's same-numbered ones.</summary>
    [ObservableProperty] public partial bool AsNew { get; set; }

    partial void OnSelectedWearerChanged(WearerChoice? value)
    {
        if (_loading || _pkg is null || value is null || _pkg.Replace?.Wearer == value.Wearer) return;
        AddonPackHandler.SetWearer(_pkg, value.Wearer);
        _loading = true;
        ReplaceOnly = _pkg.ReplaceOnly;
        if (_pkg.LooseClothing is { } c) LooseIsFemale = c.IsFemale;
        _loading = false;
        ShowItems();
        _ = ResolveAsync();
    }

    partial void OnAsNewChanged(bool value)
    {
        if (_loading || _pkg?.Replace is not { } r) return;
        r.AsNew = value;
        r.ResolvedFor = null;
        _ = ResolveAsync();
    }

    public string Note => InstallAsReplace
        ? "The files go into copies of the game's archives under mods, where the game loads them from — the game's own files stay " +
          "untouched. New slots are added to a copy of the wearer's ymt; switch it off or remove it any time in the Library."
        : @"It goes into mods\update\x64\dlcpacks as a pack of its own and into dlclist.xml; its clothes come after the game's own " +
          "in a trainer's wardrobe. Switch it off or remove it any time in the Library.";

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_pkg is null || e.PropertyName is not (nameof(MainViewModel.GameFolder) or nameof(MainViewModel.IsEnhanced))) return;
        _pkg.Checks = null;
        _wearersFor = null;
        if (_pkg.Replace is { } r) r.ResolvedFor = null;
        _ = InstallAsReplace ? ResolveAsync() : CheckAsync();
    }

    protected override string? NotReady()
    {
        if (_pkg is null) return null;
        if (InstallAsReplace)
            return IsResolving ? "The files are still being looked up in the game — wait a moment."
                : _pkg.Replace is { } r && r.Files.All(f => f.Target is null)
                    ? $"None of the mod's files fit {r.Wearer?.Label ?? "the ped"}{(r.SlotsProblem is { } why ? $" — {why}" : "")}."
                    : null;
        if (IsChecking) return "The clothes are still being checked against the game — wait a moment.";
        if (AddonPackHandler.Clean(PackName) is null) return "Give the pack a folder name (latin letters, digits, _).";
        return _pkg.Checks?.Blocking;
    }

    /// <summary>Show clothes (null: none): the add-on's collections and checks, or where the replacement's files go.</summary>
    public Task UseAsync(AddonPackage? pkg)
    {
        _pkg = pkg;
        ++_checkGen;
        ++_resolveGen;
        Items.Clear();
        Checks.Clear();
        Files.Clear();
        Wearers.Clear();
        _wearersFor = null;
        if (pkg is null)
        {
            SetWarnings([]);
            HasReplace = false;
            HasLoose = false;
            return Task.CompletedTask;
        }
        _loading = true;
        Name = pkg.Name;
        SourceText = pkg.Finished is { } f ? $"A finished clothes pack ({f.Device ?? "dlc.rpf"}{(f.SubPacks.Count > 0 ? $" + {f.SubPacks.Count} sub-pack(s)" : "")})"
            : pkg.LooseClothing is not null ? "Loose clothing models — as an add-on they become a collection of their own (ymt and shop meta written)"
            : pkg.Compose is { Resources.Count: > 0 } s ? $"A FiveM clothing resource ({string.Join(", ", s.Resources)}) — packed into a dlc.rpf on install"
            : "Clothing files and metas — packed into a dlc.rpf on install";
        Summary = string.Join("  ·  ", pkg.Parts);
        HasReplace = pkg.Replace is not null;
        ReplaceOnly = pkg.ReplaceOnly;
        HasLoose = pkg.LooseClothing is not null;
        LooseIsFemale = pkg.LooseClothing?.IsFemale ?? false;
        AsNew = pkg.Replace?.AsNew ?? false;
        AddonText = HasLoose ? "Add-On — new clothes: a collection of their own, after the game's; nothing is replaced"
                             : "Add-On — the pack as it is: new clothes after the game's; nothing is replaced";
        ReplaceText = "Replace — in place of a ped's own clothes (or as new slots of the story characters)";
        InstallAsReplace = pkg.UseReplace;
        PackName = pkg.PackName;
        _loading = false;
        ShowItems();
        SetWarnings(pkg.Warnings);
        return InstallAsReplace ? ResolveAsync() : CheckAsync();
    }

    private void ShowItems()
    {
        Items.Clear();
        if (_pkg is not { } pkg) return;
        foreach (var c in AddonPackHandler.CollectionsOf(pkg))
        {
            var (models, props) = pkg.Content.CountsOf(c);
            if (pkg.LooseClothing is { } loose && c.Ped == loose.Ped && c.DlcName == loose.NameFor(pkg.Device))
            {
                models = loose.Parts.Count(p => p.Part.Kind == ClothingPartKind.Drawable && !p.Part.Prop);
                props = loose.Parts.Count(p => p.Part.Kind == ClothingPartKind.Drawable && p.Part.Prop);
            }
            var detail = string.Join(" · ", new[] { models > 0 ? $"{models} model(s)" : null, props > 0 ? $"{props} prop(s)" : null }.OfType<string>());
            Items.Add(new AddonItemRow(ClothingNames.PedLabel(c.Ped), c.FullName, detail));
        }
    }

    /// <summary>Check the add-on against the selected game in the background; a newer check wins.</summary>
    private async Task CheckAsync()
    {
        if (_pkg is not { } pkg) return;
        int gen = ++_checkGen;
        var game = Shell.GameFolder.Trim();
        UpdatePackDetail();
        if (game.Length == 0 || !Directory.Exists(game))
        {
            Checks.Clear();
            IsChecking = false;
            CheckStatus = "Choose the GTA V folder — the clothes are checked against what the game already has.";
            return;
        }
        IsChecking = true;
        CheckStatus = "Checking against the game…";
        var target = MainViewModel.TargetFor(game, Shell.Edition);
        AddonCheckReport? report = null;
        string? error = null;
        try
        {
            report = await Task.Run(() => AddonPackHandler.Check(pkg, target, OpenIndex(game)));
        }
        catch (Exception ex)
        {
            AppLog.Error("checking the clothes failed", ex);
            error = $"The game could not be checked: {ex.Message}";
        }
        if (gen != _checkGen) return;
        IsChecking = false;
        Checks.Clear();
        if (report is null)
        {
            CheckStatus = error ?? "";
            return;
        }
        foreach (var c in report.Items.OrderByDescending(c => c.Level)) Checks.Add(new AddonCheckRow(c));
        Summary = string.Join("  ·  ", pkg.Parts);
        int problems = report.Items.Count(i => i.Level is CheckLevel.Warn or CheckLevel.Block);
        CheckStatus = report.Blocking is not null ? "It can't go into this game."
            : problems > 0 ? $"{problems} thing{(problems == 1 ? "" : "s")} to look at — it can still be installed."
            : "Nothing clashes with what the game already has.";
        _loading = true;
        PackName = pkg.PackName;
        _loading = false;
        ShowItems();
        UpdatePackDetail();
    }

    private static GameIndex? OpenIndex(string game)
    {
        try
        {
            return GameIndex.Open(game, GameIndexCache.DefaultRoot);
        }
        catch (Exception ex)
        {
            AppLog.Error("clothing: the game index could not be read", ex);
            return null;
        }
    }

    private void UpdatePackDetail()
    {
        if (_pkg is not { } pkg) return;
        PackDetail = $"mods\\update\\x64\\dlcpacks\\{pkg.PackName}  ·  mounted as {pkg.Device}" +
                     (pkg.PackNameFrom is { } from ? $"  ·  name from {from}" : "");
    }

    /// <summary>Look the replacement's files up in the wearer's folders (and list the wearers once per game).</summary>
    private async Task ResolveAsync()
    {
        if (_pkg?.Replace is not { } r) return;
        int gen = ++_resolveGen;
        var game = Shell.GameFolder.Trim();
        Files.Clear();
        if (game.Length == 0 || !Directory.Exists(game))
        {
            ReplaceStatus = "Choose the GTA V folder — each file's place is looked up in the game.";
            return;
        }
        IsResolving = true;
        ReplaceStatus = "Looking the files up in the game…";
        List<WearerChoice>? wearers = null;
        bool listWearers = _wearersFor != game;
        string? error = null;
        try
        {
            wearers = await Task.Run(() =>
            {
                var index = GameIndex.Open(game, GameIndexCache.DefaultRoot);
                var list = listWearers ? ReplacementHandler.WearerChoices(r, index) : null;
                ReplacementHandler.Resolve(r, index);
                return list;
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("resolving clothing targets failed", ex);
            error = $"The game's files could not be read: {ex.Message}";
        }
        if (gen != _resolveGen) return;
        IsResolving = false;
        _loading = true;
        if (wearers is not null)
        {
            _wearersFor = game;
            Wearers.Clear();
            foreach (var w in wearers) Wearers.Add(w);
        }
        SelectedWearer = Wearers.FirstOrDefault(w => w.Wearer == r.Wearer);
        if (SelectedWearer is null && r.Wearer is { } cur)
        {
            Wearers.Insert(0, new WearerChoice(cur, 0, !cur.IsMp));
            SelectedWearer = Wearers[0];
        }
        _loading = false;
        foreach (var f in r.Files) Files.Add(new ReplaceFileRow(f));
        Summary = string.Join("  ·  ", r.Parts);
        int placed = r.Files.Count(f => f.Target is not null);
        int slots = r.NewSlotFiles.Count(f => f.Part is { Kind: ClothingPartKind.Drawable });
        ReplaceStatus = error ?? (placed == 0
            ? $"None of the files fit {r.Wearer?.Label}{(r.SlotsProblem is { } why ? $" — {why}" : "")}."
            : $"{placed} of {r.Files.Count} file(s) go in{(slots > 0 ? $" — {slots} model(s) as new slots of {r.Wearer?.Ymt}" : "")}" +
              (r.SlotsProblem is { } p && placed < r.Files.Count ? $"; the rest can't: {p}" : "") + ".");
        SetWarnings(_pkg.Warnings.Concat(r.Warnings));
    }
}
