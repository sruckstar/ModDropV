using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using Mdv.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>A price field for one component found in the source folder.</summary>
public sealed partial class ComponentPriceViewModel : ObservableObject
{
    public required string Stem { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }

    [ObservableProperty] public partial string Price { get; set; } = "";

    partial void OnPriceChanged(string value)
    {
        var digits = MainViewModel.DigitsOnly(value);
        if (digits != value) Price = digits;
    }
}

/// <summary>One "label: value" line of the source analysis panel.</summary>
public sealed record AnalysisRow(string Label, string Value);

/// <summary>
/// The whole window. Two flows share one engine:
/// <list type="bullet">
///   <item><b>Modder</b> — Replace → Add-On written to a chosen folder, as a packed dlc.rpf
///   or as loose folders for CodeWalker; the model can be renamed.</item>
///   <item><b>Player</b> — Replace → Add-On installed straight into GTA V
///   (mods/update/x64/dlcpacks + dlclist.xml), optionally into one shared AddonWeapons pack.</item>
/// </list>
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string DefaultName = "Custom Weapon";
    public const string DefaultDesc = "An add-on weapon.";
    public const string DefaultPrice = "5000";
    public const string DefaultAmmoPrice = "100";

    private readonly Settings _settings;
    private readonly StringBuilder _log = new();
    private int _scanGeneration;
    private int _prepGeneration;
    private CancellationTokenSource? _prepCts;
    /// <summary>Field → the value last filled in from a source; replaced by the next source unless edited.</summary>
    private readonly Dictionary<string, string> _suggested = [];

    /// <summary>Folder picker supplied by the view: (title) → chosen path or null.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }
    /// <summary>Archive picker supplied by the view: (title) → chosen files (empty = cancelled).</summary>
    public Func<string, Task<IReadOnlyList<string>>>? PickArchives { get; set; }
    /// <summary>Clipboard writer supplied by the view.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    public MainViewModel() : this(new Settings()) { }

    public MainViewModel(Settings settings)
    {
        _settings = settings;
        IsPlayer = settings.Mode == "player";
        OutputFolder = settings.LastOutput ?? "";
        IsEnhanced = settings.Edition == "enhanced";
        GameFolder = settings.LastGame ?? "";
        IsDark = settings.Theme != "light";
        IsGameDialogOpen = IsPlayer && !HasGame;          // a player starts by choosing the game
    }

    // ================================================================ mode / theme

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModder), nameof(BuildButtonText), nameof(ModelNameVisible))]
    public partial bool IsPlayer { get; set; }

    public bool IsModder
    {
        get => !IsPlayer;
        set => IsPlayer = !value;
    }

    partial void OnIsPlayerChanged(bool value)
    {
        _settings.Mode = value ? "player" : "modder";
        _settings.Save();
        if (value) DetectGameEdition(GameFolder);
        else IsEnhanced = _settings.Edition == "enhanced";
        // each mode has its own source: the modder's folder path / the player's drop
        _ = AnalyzeSourceAsync(ActiveInput);
        _ = RefreshInstalledAsync();
        IsGameDialogOpen = value && !HasGame;
    }

    [ObservableProperty] public partial bool IsDark { get; set; }

    partial void OnIsDarkChanged(bool value)
    {
        _settings.Theme = value ? "dark" : "light";
        _settings.Save();
        ThemeChanged?.Invoke(value);
    }

    public event Action<bool>? ThemeChanged;

    [RelayCommand]
    private void ToggleTheme() => IsDark = !IsDark;

    public string BuildButtonText => IsPlayer ? "Install into GTA V" : "Build Add-On";

    // ================================================================ source

    [ObservableProperty] public partial string InputFolder { get; set; } = "";
    [ObservableProperty] public partial string SourceNotice { get; set; } = "";
    [ObservableProperty] public partial bool HasSourceNotice { get; set; }
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string RouteTitle { get; set; } = "No source selected";
    [ObservableProperty] public partial string RouteDetail { get; set; } = ModderEmptyDetail;

    private const string ModderEmptyDetail =
        "Pick the folder with the weapon's Replace files (.ydr / .ytd), a folder with your own " +
        "metas, or a folder holding a finished dlc.rpf.";
    private const string PlayerEmptyDetail =
        "Drop the weapon's folder or archive (.zip / .rar / .7z) — models, textures and the mod's " +
        "own configs are found inside automatically.";
    [ObservableProperty] public partial string RouteBadge { get; set; } = "—";

    /// <summary>File name of a finished dlc.rpf found in the source folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelNameEnabled))]
    public partial string? PrebuiltRpf { get; set; }

    /// <summary>Names of the .meta/.xml files the source folder ships.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelNameEnabled))]
    public partial IReadOnlyList<string>? SuppliedMetas { get; set; }

    public ObservableCollection<AnalysisRow> Analysis { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<ComponentPriceViewModel> Components { get; } = [];

    [ObservableProperty] public partial bool HasComponents { get; set; }

    [RelayCommand]
    private async Task BrowseInput()
    {
        var path = await Pick("Folder with the Replace files");
        if (path is not null) InputFolder = path;
    }

    partial void OnInputFolderChanged(string value)
    {
        if (!IsPlayer) _ = AnalyzeSourceAsync(value);
    }

    /// <summary>The folder the build reads: the modder's pick, or the player's prepared drop.</summary>
    private string ActiveInput => IsPlayer ? Intake?.InputFolder ?? "" : InputFolder;

    // ---------------------------------------------------------------- player source (drop)

    /// <summary>The player's dropped folder / archive, unpacked and sorted into a build input.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIntake), nameof(DropZoneEmpty), nameof(IntakeTitle), nameof(IntakeSummary))]
    public partial IntakeResult? Intake { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DropZoneEmpty))]
    public partial bool IsPreparing { get; set; }

    [ObservableProperty] public partial string PrepareStatus { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDropError))]
    public partial string? DropError { get; set; }

    public bool HasDropError => DropError is not null;
    public bool HasIntake => Intake is not null;
    public bool DropZoneEmpty => Intake is null && !IsPreparing;

    public string IntakeTitle => Intake is null ? "" : string.Join(", ", Intake.Sources.Select(Path.GetFileName));

    public string IntakeSummary
    {
        get
        {
            if (Intake is not { } r) return "";
            var parts = new List<string>();
            if (r.PrebuiltRpf is not null) parts.Add("finished dlc.rpf");
            else
            {
                parts.Add(r.Models.Count == 1 ? "1 model / texture" : $"{r.Models.Count} models / textures");
                if (r.Configs.Count > 0) parts.Add(r.Configs.Count == 1 ? "1 config" : $"{r.Configs.Count} configs");
            }
            if (r.Ignored.Count > 0) parts.Add($"{r.Ignored.Count} skipped");
            if (r.Archives.Count > 0) parts.Add(r.Archives.Count == 1 ? "unpacked" : $"{r.Archives.Count} archives unpacked");
            return string.Join("  ·  ", parts);
        }
    }

    [RelayCommand]
    private async Task BrowseSourceFolder()
    {
        var path = await Pick("Folder with the weapon");
        if (path is not null) await LoadPlayerSourceAsync([path]);
    }

    [RelayCommand]
    private async Task BrowseSourceArchive()
    {
        if (PickArchives is null) return;
        var files = await PickArchives("Weapon archive");
        if (files.Count > 0) await LoadPlayerSourceAsync(files);
    }

    [RelayCommand]
    private void ClearPlayerSource()
    {
        _prepCts?.Cancel();
        ++_prepGeneration;
        IsPreparing = false;
        DropError = null;
        Intake = null;                                // its workspace goes with the next drop
        _ = AnalyzeSourceAsync(ActiveInput);
    }

    /// <summary>Files / folders dropped on the source area (called by the view).</summary>
    public Task DropSourceAsync(IReadOnlyList<string> paths) => LoadPlayerSourceAsync(paths);

    /// <summary>
    /// Unpack and sort a dropped source in the background. A newer drop cancels an older
    /// one still unpacking; only the latest result (and its workspace) survives.
    /// </summary>
    internal async Task LoadPlayerSourceAsync(IReadOnlyList<string> paths)
    {
        if (IsBuilding || paths.Count == 0) return;
        _prepCts?.Cancel();
        var cts = _prepCts = new CancellationTokenSource();
        int gen = ++_prepGeneration;

        IsPreparing = true;
        DropError = null;
        Intake = null;                                // the new drop replaces the old one
        ++_scanGeneration;                            // …and any analysis of it still running
        ResetAnalysis(null);
        RouteTitle = "Reading the drop…";
        RouteDetail = "Unpacking and looking for models, textures and configs.";
        PrepareStatus = $"Reading {Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]))}…";
        AppLog.Info($"player source: {string.Join(" | ", paths)}");

        IntakeResult? result = null;
        string? error = null;
        try
        {
            result = await Task.Run(() =>
            {
                // only one drop is kept: clear earlier workspaces before this one is created
                SourceIntake.CleanWorkRoot(AppPaths.SourcesDir);
                void Progress(string msg) => Dispatcher.UIThread.Post(() =>
                {
                    if (gen == _prepGeneration) PrepareStatus = msg;
                });
                var dropped = SourceIntake.Gather(paths, AppPaths.SourcesDir, Progress, cts.Token);
                try
                {
                    return SourceIntake.PrepareWeapon(dropped, AppPaths.Templates, Progress);
                }
                catch (IntakeException ex)
                {
                    PathUtil.TryDeleteDir(dropped.WorkDir);
                    throw NotAWeapon(dropped, ex);
                }
            });
        }
        catch (OperationCanceledException)
        {
            return;                                   // superseded — the newer drop owns the state
        }
        catch (IntakeException ex)
        {
            error = ex.Message;
            AppLog.Info("player source rejected: " + ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error("player source failed", ex);
            error = $"Could not read what was dropped: {ex.Message}";
        }

        if (gen != _prepGeneration)
        {
            if (result is not null) PathUtil.TryDeleteDir(result.WorkDir);
            return;
        }
        IsPreparing = false;
        Intake = result;
        DropError = error;
        if (result is not null)
        {
            AppLog.Info($"player source ready: {result.Models.Count} model(s), {result.Configs.Count} config(s), " +
                        $"prebuilt={result.PrebuiltRpf?.Origin ?? "-"}, ignored={result.Ignored.Count} -> {result.InputFolder}");
        }
        await AnalyzeSourceAsync(ActiveInput);
    }

    /// <summary>No weapon in the drop: say what it looks like instead, when the detector can tell.</summary>
    private static IntakeException NotAWeapon(DroppedSource dropped, IntakeException ex)
    {
        var report = ModDetector.Detect(dropped);
        if (report.Primary is not { } p || report.Has(ModCategory.Weapon)) return ex;
        AppLog.Info($"player source is not a weapon: {report.Summary()}");
        return new IntakeException(
            $"This looks like a {p.Category.DisplayName().ToLowerInvariant()} mod ({string.Join(", ", p.Evidence.Take(2))}), " +
            "not a weapon. ModDrop V installs add-on weapons for now — other mod types are on the way.", ex);
    }

    // ---------------------------------------------------------------- analysis

    /// <summary>
    /// Describe a source folder: priceable components, a friendly name guess, and which of
    /// the three build routes it takes (raw models / models + metas / a finished dlc.rpf).
    /// </summary>
    private async Task AnalyzeSourceAsync(string folder)
    {
        int gen = ++_scanGeneration;
        folder = folder.Trim();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            ResetAnalysis(folder.Length == 0 ? null : "Folder not found.");
            return;
        }

        IsScanning = true;
        ClearPreview();
        SourceAnalysis? a = null;
        var intake = IsPlayer ? Intake : null;
        try
        {
            a = await Task.Run(() => SourceAnalysis.Run(folder, intake));
        }
        catch (Exception ex)
        {
            AppLog.Error("source analysis failed", ex);
        }
        if (gen != _scanGeneration) return;          // a newer pick superseded this one
        IsScanning = false;
        if (a is null)
        {
            ResetAnalysis("Could not read the folder.");
            return;
        }

        ApplyStoreInfo(a);

        PrebuiltRpf = a.PrebuiltRpf;
        SuppliedMetas = a.Metas;
        RouteTitle = a.RouteTitle;
        RouteDetail = a.RouteDetail;
        RouteBadge = a.RouteBadge;

        Analysis.Clear();
        foreach (var row in a.Rows) Analysis.Add(row);
        Warnings.Clear();
        foreach (var w in a.Warnings) Warnings.Add(w);

        Components.Clear();
        foreach (var c in a.Components)
            Components.Add(new ComponentPriceViewModel
            {
                Stem = c.Stem, Label = c.Label, Kind = c.Kind,
                Price = a.Store.ComponentPrices.TryGetValue(c.Stem, out var cost) ? cost.ToString(CultureInfo.InvariantCulture) : "",
            });
        HasComponents = Components.Count > 0;
        UpdateSourceNotice();
        await LoadPreviewAsync(folder);
    }

    /// <summary>
    /// Start the form from what the source says about its weapon (the mod's own name,
    /// description and prices, else the folder name and the defaults). A field the user
    /// edited keeps the edit; one still holding the previous source's value follows the new one.
    /// </summary>
    private void ApplyStoreInfo(SourceAnalysis a)
    {
        var s = a.Store;
        Name = Suggest(nameof(Name), Name, DefaultName, a.SuggestedName);
        Description = Suggest(nameof(Description), Description, DefaultDesc, s.Description);
        Price = Suggest(nameof(Price), Price, DefaultPrice, s.Price?.ToString(CultureInfo.InvariantCulture));
        AmmoPrice = Suggest(nameof(AmmoPrice), AmmoPrice, DefaultAmmoPrice, s.AmmoPrice?.ToString(CultureInfo.InvariantCulture));
    }

    private string Suggest(string field, string current, string fallback, string? found)
    {
        var next = string.IsNullOrWhiteSpace(found) ? fallback : found;
        bool untouched = string.IsNullOrWhiteSpace(current) || current == fallback
                         || (_suggested.TryGetValue(field, out var prev) && current == prev);
        _suggested[field] = next;
        return untouched ? next : current;
    }

    private void ResetAnalysis(string? error)
    {
        IsScanning = false;
        ClearPreview();
        PrebuiltRpf = null;
        SuppliedMetas = null;
        Analysis.Clear();
        Warnings.Clear();
        Components.Clear();
        HasComponents = false;
        RouteBadge = "—";
        if (IsPlayer && DropError is not null) error = DropError;
        RouteTitle = error is null ? "No source selected" : "Source unavailable";
        RouteDetail = error ?? (IsPlayer ? PlayerEmptyDetail : ModderEmptyDetail);
        UpdateSourceNotice();
    }

    /// <summary>Tell the user what we found, because it changes what the build does.</summary>
    private void UpdateSourceNotice()
    {
        string text = "";
        if (PrebuiltRpf is not null)
        {
            var shown = IsPlayer && Intake?.PrebuiltRpf is { } p ? p.Origin : PrebuiltRpf;
            text = IsPlayer
                ? $"Found a finished pack «{shown}». It is installed as-is — no models are converted. " +
                  "Tick «Single AddonWeapons pack» to unpack it and fold its models and metas into the shared DLC instead."
                : $"Found a finished pack «{PrebuiltRpf}». It is copied to the output as-is — no models are converted.";
        }
        else if (SuppliedMetas is { Count: > 0 })
        {
            text = $"Found your own config: {string.Join(", ", SuppliedMetas)}. These are shipped as-is instead of " +
                   "being generated from templates, and the models keep their original names.";
        }
        SourceNotice = text;
        HasSourceNotice = text.Length > 0;
    }

    // ================================================================ destination

    [ObservableProperty] public partial string OutputFolder { get; set; } = "";
    [ObservableProperty] public partial string GameFolder { get; set; } = "";
    [ObservableProperty] public partial bool PackRpf { get; set; } = true;
    [ObservableProperty] public partial bool MergePack { get; set; } = true;

    /// <summary>Pack for GTA V Enhanced (gen9 models) instead of Legacy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLegacy), nameof(GameChipTitle))]
    public partial bool IsEnhanced { get; set; }

    public bool IsLegacy
    {
        get => !IsEnhanced;
        set => IsEnhanced = !value;
    }

    public GameEdition Edition => IsEnhanced ? GameEdition.Enhanced : GameEdition.Legacy;

    /// <summary>What the selected game folder turned out to be (player flow).</summary>
    [ObservableProperty] public partial string GameEditionHint { get; set; } = "";
    [ObservableProperty] public partial bool HasGameEditionHint { get; set; }

    partial void OnIsEnhancedChanged(bool value)
    {
        if (IsPlayer)
        {
            _ = RefreshInstalledAsync();                 // each edition stages its own shared pack
            return;                                      // the player's choice follows the game folder
        }
        var stored = value ? "enhanced" : "legacy";
        if (_settings.Edition == stored) return;
        _settings.Edition = stored;
        _settings.Save();
    }

    /// <summary>Pick the edition from the game folder's executable and say what was found.</summary>
    private void DetectGameEdition(string folder)
    {
        folder = folder.Trim();
        string hint = "";
        if (folder.Length > 0 && Directory.Exists(folder))
        {
            if (GameEditions.Detect(folder) is { } e)
            {
                IsEnhanced = e == GameEdition.Enhanced;
                hint = $"Detected {e.DisplayName()} ({e.ExeName()}).";
            }
            else if (GameEditions.IsAmbiguous(folder))
                hint = $"Both {GameEditions.LegacyExe} and {GameEditions.EnhancedExe} are here — choose the game version.";
            else
                hint = $"No {GameEditions.LegacyExe} / {GameEditions.EnhancedExe} in this folder — is it the GTA V folder?";
        }
        GameEditionHint = hint;
        HasGameEditionHint = hint.Length > 0;
    }

    public bool LooseFolders
    {
        get => !PackRpf;
        set => PackRpf = !value;
    }

    partial void OnPackRpfChanged(bool value) => OnPropertyChanged(nameof(LooseFolders));

    // Folders are remembered as soon as they are picked or typed, not only after a build.
    partial void OnGameFolderChanged(string value)
    {
        Remember(value.Trim(), _settings.LastGame, v => _settings.LastGame = v);
        NotifyGameChanged();
        if (IsPlayer)
        {
            DetectGameEdition(value);
            _ = RefreshInstalledAsync();
        }
    }
    partial void OnOutputFolderChanged(string value) => Remember(value.Trim(), _settings.LastOutput, v => _settings.LastOutput = v);

    private void Remember(string value, string? stored, Action<string?> store)
    {
        if (value == (stored ?? "")) return;
        store(value.Length > 0 ? value : null);
        _settings.Save();
    }

    [RelayCommand]
    private async Task BrowseOutput()
    {
        var path = await Pick("Output folder");
        if (path is not null) OutputFolder = path;
    }

    [RelayCommand]
    private async Task BrowseGame()
    {
        var path = await Pick("GTA V game folder");
        if (path is not null) GameFolder = path;
    }

    private async Task<string?> Pick(string title) => PickFolder is null ? null : await PickFolder(title);

    // ================================================================ weapon

    [ObservableProperty] public partial string Name { get; set; } = DefaultName;
    [ObservableProperty] public partial string Description { get; set; } = DefaultDesc;
    [ObservableProperty] public partial string Price { get; set; } = DefaultPrice;
    [ObservableProperty] public partial string AmmoPrice { get; set; } = DefaultAmmoPrice;
    [ObservableProperty] public partial string ModelName { get; set; } = "";

    /// <summary>Renaming the model is a modder concern; players never see it.</summary>
    public bool ModelNameVisible => !IsPlayer;
    /// <summary>Supplied metas / a finished pack already pin the model names.</summary>
    public bool ModelNameEnabled => PrebuiltRpf is null && SuppliedMetas is not { Count: > 0 };

    internal static string DigitsOnly(string s) => new(s.Where(char.IsAsciiDigit).ToArray());

    partial void OnPriceChanged(string value)
    {
        var d = DigitsOnly(value);
        if (d != value) Price = d;
    }

    partial void OnAmmoPriceChanged(string value)
    {
        var d = DigitsOnly(value);
        if (d != value) AmmoPrice = d;
    }

    /// <summary>RAGE asset names are lowercase [a-z0-9_].</summary>
    partial void OnModelNameChanged(string value)
    {
        var sb = new StringBuilder();
        bool pendingSep = false;
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch) || ch == '-') { pendingSep = true; continue; }
            if (pendingSep) { sb.Append('_'); pendingSep = false; }
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') sb.Append(ch);
        }
        if (pendingSep) sb.Append('_');
        var clean = sb.ToString();
        if (clean != value) ModelName = clean;
    }

    // ================================================================ build

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(ApplyInstalledCommand))]
    public partial bool IsBuilding { get; set; }

    [ObservableProperty] public partial string StageStatus { get; set; } = "";
    [ObservableProperty] public partial string StageHint { get; set; } = "";
    /// <summary>Incremented every time the revolver should fire (a build phase advanced).</summary>
    [ObservableProperty] public partial int FireCount { get; set; }

    [ObservableProperty] public partial string LogText { get; set; } = "";
    [ObservableProperty] public partial bool HasLog { get; set; }
    [ObservableProperty] public partial bool LogIsError { get; set; }
    [ObservableProperty] public partial string CopyLogLabel { get; set; } = "Copy";

    [ObservableProperty] public partial bool ResultVisible { get; set; }
    [ObservableProperty] public partial bool ResultOk { get; set; }
    [ObservableProperty] public partial string ResultTitle { get; set; } = "";
    [ObservableProperty] public partial string ResultDetail { get; set; } = "";
    [ObservableProperty] public partial string? ResultPath { get; set; }
    [ObservableProperty] public partial bool HasResultPath { get; set; }

    private bool CanBuild() => !IsBuilding;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task Build()
    {
        ResultVisible = false;
        if (IsPlayer && !HasGame)
        {
            IsGameDialogOpen = true;                   // nowhere to install yet — ask for the game
            return;
        }
        var (opts, error) = GatherOptions();
        if (opts is null)
        {
            ShowResult(false, "Couldn't start", error ?? "Unknown error.", null);
            return;
        }

        _log.Clear();
        LogText = "";
        HasLog = false;
        LogIsError = false;
        if (IsPlayer && Intake is { } src)
        {
            _log.AppendLine($"Source: {IntakeTitle} — {IntakeSummary}");
            foreach (var c in src.Configs) _log.AppendLine($"    config shipped as-is: {c.Name}  <- {c.Origin}");
            LogText = _log.ToString();
            HasLog = true;
        }
        IsBuilding = true;
        StageStatus = IsPlayer ? "Installing into GTA V…" : "Building Add-On…";
        StageHint = "Loading the cylinder";
        AppLog.Info($"build started: mode={(IsPlayer ? "player" : "modder")} input={opts.InputFolder}");

        try
        {
            var result = await Task.Run(() => Pipeline.BuildAddon(opts, OnLog));
            if (result is null)
            {
                FailLog(null);
                ShowResult(false, "Build not finished", "Base weapon could not be determined — see the log.", null);
            }
            else if (result.InstalledTo is not null)
                ShowResult(true, $"Installed into {opts.Edition!.Value.DisplayName()}",
                           $"Mod built and installed into the game:\n{result.InstalledTo}", result.InstalledTo);
            else if (!result.Packed)
                ShowResult(true, "Done — loose folders",
                           "Add-On built unpacked. Pack the *.rpf folders with CodeWalker (see manifest.json).",
                           result.Root);
            else
                ShowResult(true, "Done", $"Add-On built:\n{result.Root}", result.Root);
        }
        catch (Exception ex)
        {
            AppLog.Error("build failed", ex);
            FailLog(ex);
            ShowResult(false, "Build failed", $"{ex.Message}\n\nSee the log for details.", null);
        }
        finally
        {
            IsBuilding = false;
        }
        if (IsPlayer) await RefreshInstalledAsync();
    }

    /// <summary>Validate the form into build options (mirrors the original backend checks).</summary>
    internal (BuildOptions? Options, string? Error) GatherOptions()
    {
        var input = ActiveInput.Trim();
        if (IsPlayer)
        {
            if (IsPreparing) return (null, "The dropped weapon is still being unpacked — wait a moment.");
            if (Intake is null)
                return (null, DropError ?? "Drop the weapon — its folder or a .zip / .rar / .7z archive — into the Source area first.");
            if (!Directory.Exists(input)) return (null, "The unpacked copy of the weapon is gone — drop it again.");
        }
        else
        {
            if (input.Length == 0) return (null, "No Replace-files folder selected.");
            if (!Directory.Exists(input)) return (null, "Replace-files folder not found.");
        }

        if (!TryInt(Price, out var price) || !TryInt(AmmoPrice, out var ammo))
            return (null, "Price and ammo price must be numbers.");

        var compPrices = new Dictionary<string, int>();
        foreach (var c in Components)
        {
            var raw = c.Price.Trim();
            if (raw.Length == 0) continue;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return (null, $"Price of component «{c.Stem}» must be an integer.");
            compPrices[c.Stem] = v;
        }

        var name = Name.Trim();
        if (name.Length == 0) name = DefaultName;
        var desc = Description.Trim();
        if (desc.Length == 0) desc = DefaultDesc;

        string? modelName = IsPlayer ? null : ModelName.Trim();
        if (string.IsNullOrEmpty(modelName)) modelName = null;
        if (modelName is not null && Namer.SanitizeModelName(modelName).Length == 0)
            return (null, "The model name may only contain latin letters, digits and underscores (e.g. w_pi_mygun).");

        string outDir;
        bool packRpf;
        string? installDir = null;
        if (IsPlayer)
        {
            var game = GameFolder.Trim();
            if (game.Length == 0) return (null, "No GTA V game folder selected.");
            if (!Directory.Exists(game)) return (null, "The selected game folder doesn't exist.");
            bool isGame = GameEditions.Detect(game) is not null || GameEditions.IsAmbiguous(game);
            if (!isGame && !Directory.Exists(Path.Combine(game, "mods")))
                return (null, $"The selected folder doesn't look like GTA V: no {GameEditions.LegacyExe} / " +
                              $"{GameEditions.EnhancedExe} and no «mods» directory.");
            installDir = game;
            outDir = AppPaths.StagingFor(Edition);
            packRpf = true;
            _settings.LastGame = game;
        }
        else
        {
            var output = OutputFolder.Trim();
            if (output.Length == 0) return (null, "No output folder selected.");
            try
            {
                Directory.CreateDirectory(output);
            }
            catch (Exception ex)
            {
                return (null, $"Cannot create the output folder: {ex.Message}");
            }
            outDir = output;
            packRpf = PackRpf;
            _settings.LastOutput = output;
        }
        _settings.Save();

        return (new BuildOptions
        {
            InputFolder = input,
            OutDir = outDir,
            TemplatesDir = AppPaths.Templates,
            DataDir = AppPaths.Data,
            Name = name,
            Desc = desc,
            Price = price,
            AmmoCost = ammo,
            ComponentPrices = compPrices,
            ModelName = modelName,
            PackRpf = packRpf,
            // the single-pack merge belongs to the player flow only
            MergePack = IsPlayer && MergePack,
            InstallGameDir = installDir,
            SourcePath = IsPlayer && Intake is { Sources.Count: 1 } drop ? drop.Sources[0] : null,
            Edition = Edition,
            PluginsDir = Path.Combine(AppPaths.Data, "plugins"),
        }, null);
    }

    /// <summary><c>int(value or 0)</c>: an empty field counts as 0.</summary>
    private static bool TryInt(string s, out int value)
    {
        s = s.Trim();
        if (s.Length == 0)
        {
            value = 0;
            return true;
        }
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Pipeline log hook — called on the worker thread.</summary>
    private void OnLog(string msg)
    {
        AppLog.Info("build: " + msg);
        var phase = PhaseOf(msg);
        Dispatcher.UIThread.Post(() =>
        {
            _log.AppendLine(msg);
            LogText = _log.ToString();
            HasLog = true;
            if (phase is not null && phase != StageHint)
            {
                StageHint = phase;
                FireCount++;
            }
        });
    }

    /// <summary>Map pipeline log lines to the stage caption shown under the revolver.</summary>
    internal static string? PhaseOf(string msg)
    {
        if (msg.StartsWith("Building template library", StringComparison.Ordinal)) return "Building the template library";
        if (msg.StartsWith("Scan:", StringComparison.Ordinal)) return "Scanning models";
        if (msg.StartsWith("Components:", StringComparison.Ordinal)) return "Writing meta files";
        if (msg.StartsWith("Unpacking", StringComparison.Ordinal)) return "Unpacking the archive";
        if (msg.StartsWith("Merged into", StringComparison.Ordinal)) return "Loading the shared pack";
        if (msg.StartsWith("WEAPON hash", StringComparison.Ordinal)) return "Assembling the dlcpack";
        if (msg.StartsWith("Self-checking", StringComparison.Ordinal)) return "Verifying resources";
        if (msg.StartsWith("Converting", StringComparison.Ordinal)) return "Converting models to gen9";
        if (msg.StartsWith("    Copying update", StringComparison.Ordinal)) return "Setting up the mods folder";
        if (msg.StartsWith("Installing", StringComparison.Ordinal)) return "Installing into the game";
        return null;
    }

    private void FailLog(Exception? ex)
    {
        if (ex is not null)
        {
            _log.AppendLine();
            _log.AppendLine(ex.ToString());
        }
        if (_log.Length == 0) _log.AppendLine("Unknown error.");
        LogText = _log.ToString();
        HasLog = true;
        LogIsError = true;
    }

    private void ShowResult(bool ok, string title, string detail, string? path)
    {
        ResultOk = ok;
        ResultTitle = title;
        ResultDetail = detail;
        ResultPath = path;
        HasResultPath = path is not null;
        ResultVisible = true;
        AppLog.Info($"result: {title} | {detail.Replace('\n', ' ')}");
    }

    [RelayCommand]
    private void DismissResult() => ResultVisible = false;

    [RelayCommand]
    private void OpenResult()
    {
        if (ResultPath is null) return;
        try
        {
            Diagnostics.OpenInFileManager(ResultPath);
        }
        catch (Exception ex)
        {
            AppLog.Error("open folder failed", ex);
        }
    }

    [RelayCommand]
    private async Task CopyLog()
    {
        bool ok = false;
        try
        {
            if (CopyToClipboard is not null)
            {
                await CopyToClipboard(LogText);
                ok = true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("clipboard failed", ex);
        }
        CopyLogLabel = ok ? "Copied ✓" : "Error";
        await Task.Delay(1500);
        CopyLogLabel = "Copy";
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Diagnostics.OpenInFileManager(AppLog.FilePath);
        }
        catch (Exception ex)
        {
            AppLog.Error("open log folder failed", ex);
        }
    }
}
