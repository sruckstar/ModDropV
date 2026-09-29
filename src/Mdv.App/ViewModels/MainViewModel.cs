using System.Text;
using Avalonia.Threading;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// The window. Players drop a mod, see what it is, set it up in its panel, review the
/// install plan and install it; the library lists what is installed and the game status
/// what the game has. Modders pick an add-on type and build it into a folder. Each mod
/// type's settings live in its own panel (<see cref="ModPanelViewModel"/>); this shell owns
/// the drop, the game, the output folder, the log, the result and the build stage.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly StringBuilder _log = new();

    internal Settings Settings { get; }

    /// <summary>Folder picker supplied by the view: (title) → chosen path or null.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }
    /// <summary>Archive picker supplied by the view: (title) → chosen files (empty = cancelled).</summary>
    public Func<string, Task<IReadOnlyList<string>>>? PickArchives { get; set; }
    /// <summary>Clipboard writer supplied by the view.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Raised after something was installed / changed in the game (its file index is out of date).</summary>
    public event Action<string>? GameFilesChanged;

    public MainViewModel() : this(new Settings()) { }

    public MainViewModel(Settings settings)
    {
        Settings = settings;
        Weapon = new WeaponViewModel(this);
        Oiv = new OivViewModel(this);
        Replace = new ReplacementViewModel(this);
        Script = new ScriptViewModel(this);
        Addon = new AddonViewModel(this);
        Livery = new LiveryViewModel(this);
        Clothing = new ClothingViewModel(this);
        Map = new MapViewModel(this);
        VehicleBuild = new VehicleBuildViewModel(this);
        PedBuild = new PedBuildViewModel(this);
        PropBuild = new PropBuildViewModel(this);
        foreach (var preview in new[] { Addon.Preview, Livery.Preview, VehicleBuild.Preview, PedBuild.Preview, PropBuild.Preview })
            preview.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModelPreview.IsOpen)) OnPropertyChanged(nameof(OpenModelPreview));
            };
        InitModderTypes();
        IsPlayer = settings.Mode == "player";
        OutputFolder = settings.LastOutput ?? "";
        IsEnhanced = settings.Edition == "enhanced";
        GameFolder = settings.LastGame ?? "";
        IsDark = settings.Theme != "light";
        IsGameDialogOpen = IsPlayer && !HasGame;          // a player starts by choosing the game
    }

    // ================================================================ mode / pages / theme

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModder), nameof(BuildButtonText), nameof(PrimaryCommand), nameof(ShowInstallPage),
                              nameof(ShowLibraryPage), nameof(ShowWorkspace), nameof(ShowOutputCard), nameof(ShowTopCards),
                              nameof(ShowFooter))]
    public partial bool IsPlayer { get; set; }

    public bool IsModder
    {
        get => !IsPlayer;
        set => IsPlayer = !value;
    }

    partial void OnIsPlayerChanged(bool value)
    {
        Settings.Mode = value ? "player" : "modder";
        Settings.Save();
        if (value) DetectGameEdition(GameFolder);
        else IsEnhanced = Settings.Edition == "enhanced";
        Weapon.OnModeChanged();
        NotifyPanel();
        _ = RefreshInstalledAsync();
        _ = RefreshStatusAsync();
        IsGameDialogOpen = value && !HasGame;
    }

    /// <summary>Player: the library page instead of the install page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstallPage), nameof(ShowInstallPage), nameof(ShowLibraryPage), nameof(ShowWorkspace),
                              nameof(ShowFooter))]
    public partial bool IsLibraryPage { get; set; }

    public bool IsInstallPage
    {
        get => !IsLibraryPage;
        set => IsLibraryPage = !value;
    }

    public bool ShowInstallPage => IsPlayer && !IsLibraryPage;
    public bool ShowLibraryPage => IsPlayer && IsLibraryPage;
    /// <summary>The source / settings / analysis columns: the modder's page or the player's install page.</summary>
    public bool ShowWorkspace => !ShowLibraryPage;
    /// <summary>The footer: the build button, or the result banner of what the library did.</summary>
    public bool ShowFooter => ShowWorkspace || ResultVisible;

    [RelayCommand]
    private void ShowLibrary() => IsLibraryPage = true;

    [ObservableProperty] public partial bool IsDark { get; set; }

    partial void OnIsDarkChanged(bool value)
    {
        Settings.Theme = value ? "dark" : "light";
        Settings.Save();
        ThemeChanged?.Invoke(value);
    }

    public event Action<bool>? ThemeChanged;

    [RelayCommand]
    private void ToggleTheme() => IsDark = !IsDark;

    public string BuildButtonText => IsPlayer ? L.T("Install into GTA V") : L.T("Build Add-On");

    /// <summary>The footer's button: the player sees the install plan first, the modder builds right away.</summary>
    public IAsyncRelayCommand PrimaryCommand => IsPlayer ? ReviewInstallCommand : BuildCommand;

    // ================================================================ panels

    /// <summary>The add-on weapon panel (both flows).</summary>
    public WeaponViewModel Weapon { get; }

    /// <summary>Player: an OIV package from the drop.</summary>
    public OivViewModel Oiv { get; }

    /// <summary>Player: loose files replacing game files.</summary>
    public ReplacementViewModel Replace { get; }

    /// <summary>Player: ASI plugins, ScriptHookVDotNet scripts, RAGE plugins.</summary>
    public ScriptViewModel Script { get; }

    /// <summary>Player: add-on vehicles and peds (finished packs, FiveM resources, loose models with metas).</summary>
    public AddonViewModel Addon { get; }

    /// <summary>Player: vehicle liveries (pictures into a vehicle's textures, modkit livery models).</summary>
    public LiveryViewModel Livery { get; }

    /// <summary>Player: clothes (MP clothes packs, FiveM clothing, story characters' and freemode replacements, new slots).</summary>
    public ClothingViewModel Clothing { get; }

    /// <summary>Player: maps and props (add-on packs, FiveM maps, loose placements) and Menyoo / Map Editor maps.</summary>
    public MapViewModel Map { get; }

    /// <summary>Modder: an add-on vehicle built from its models on the base of a game vehicle.</summary>
    public VehicleBuildViewModel VehicleBuild { get; }

    /// <summary>Modder: an add-on ped built from its models on the base of a game ped.</summary>
    public PedBuildViewModel PedBuild { get; }

    /// <summary>Modder: add-on props built from their models, their .ytyp written.</summary>
    public PropBuildViewModel PropBuild { get; }

    /// <summary>The 3D preview opened large (an add-on's, a livery's, the modder's vehicle, ped or props), or null.</summary>
    public ModelPreview? OpenModelPreview =>
        new[] { Addon.Preview, Livery.Preview, VehicleBuild.Preview, PedBuild.Preview, PropBuild.Preview }.FirstOrDefault(p => p.IsOpen);

    /// <summary>Player: the panel of the mod picked from the drop (null: nothing installable dropped yet).</summary>
    [ObservableProperty] public partial ModPanelViewModel? SelectedPanel { get; set; }

    /// <summary>Modder: the panel of the add-on type picked.</summary>
    [ObservableProperty] public partial ModPanelViewModel? ModderPanel { get; set; }

    partial void OnSelectedPanelChanged(ModPanelViewModel? value) => NotifyPanel();
    partial void OnModderPanelChanged(ModPanelViewModel? value) => NotifyPanel();

    /// <summary>The panel the form, the analysis column and the build button work with.</summary>
    public ModPanelViewModel? ActivePanel => IsPlayer ? SelectedPanel : ModderPanel;

    /// <summary>Player without anything installable dropped: the analysis column says what can be installed.</summary>
    public bool ShowSupportedCard => IsPlayer && SelectedPanel is null;

    /// <summary>The build button: off only for a kind that can't be built yet (anything else explains itself).</summary>
    public bool CanStart => ActivePanel?.CanRun ?? true;

    private void NotifyPanel()
    {
        OnPropertyChanged(nameof(ActivePanel));
        OnPropertyChanged(nameof(ShowSupportedCard));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowOutputCard));
        OnPropertyChanged(nameof(ShowTopCards));
    }

    // ================================================================ destination

    [ObservableProperty] public partial string OutputFolder { get; set; } = "";
    [ObservableProperty] public partial string GameFolder { get; set; } = "";
    [ObservableProperty] public partial bool PackRpf { get; set; } = true;

    /// <summary>Modder: the output card (players install into the game from the header chip) — for a type that builds.</summary>
    public bool ShowOutputCard => !IsPlayer && CanStart;

    /// <summary>The cards over the settings: the player's source, the modder's source and output.</summary>
    public bool ShowTopCards => IsPlayer || CanStart;

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
            _ = RefreshStatusAsync();
            return;                                      // the player's choice follows the game folder
        }
        var stored = value ? "enhanced" : "legacy";
        if (Settings.Edition == stored) return;
        Settings.Edition = stored;
        Settings.Save();
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
                hint = L.T($"Detected {e.DisplayName()} ({e.ExeName()}).");
            }
            else if (GameEditions.IsAmbiguous(folder))
                hint = L.T($"Both {GameEditions.LegacyExe} and {GameEditions.EnhancedExe} are here — choose the game version.");
            else
                hint = L.T($"No {GameEditions.LegacyExe} / {GameEditions.EnhancedExe} in this folder — is it the GTA V folder?");
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
        Remember(value.Trim(), Settings.LastGame, v => Settings.LastGame = v);
        Mdv.Core.Rpf.GameCrypto.UseGame(value);
        NotifyGameChanged();
        if (IsPlayer)
        {
            DetectGameEdition(value);
            _ = RefreshInstalledAsync();
            _ = RefreshStatusAsync();
        }
    }

    partial void OnOutputFolderChanged(string value) => Remember(value.Trim(), Settings.LastOutput, v => Settings.LastOutput = v);

    private void Remember(string value, string? stored, Action<string?> store)
    {
        if (value == (stored ?? "")) return;
        store(value.Length > 0 ? value : null);
        Settings.Save();
    }

    /// <summary>The game folder to install into, or why it can't take mods.</summary>
    internal (string? Game, string? Error) CheckGame()
    {
        var game = GameFolder.Trim();
        if (game.Length == 0) return (null, L.T("No GTA V game folder selected."));
        if (!Directory.Exists(game)) return (null, L.T("The selected game folder doesn't exist."));
        bool isGame = GameEditions.Detect(game) is not null || GameEditions.IsAmbiguous(game);
        if (!isGame && !Directory.Exists(Path.Combine(game, ModsLayout.ModsRoot)) && !Directory.Exists(Path.Combine(game, ModsLayout.OnigiriRoot)))
            return (null, L.T($"The selected folder doesn't look like GTA V: no {GameEditions.LegacyExe} / " +
                          $"{GameEditions.EnhancedExe} and no «mods» directory."));
        Settings.LastGame = game;
        Settings.Save();
        return (game, null);
    }

    /// <summary>The modder's output folder (created if needed), or why it can't be used.</summary>
    internal (string? Output, string? Error) CheckOutput()
    {
        var output = OutputFolder.Trim();
        if (output.Length == 0) return (null, L.T("No output folder selected."));
        try
        {
            Directory.CreateDirectory(output);
        }
        catch (Exception ex)
        {
            return (null, L.T($"Cannot create the output folder: {ex.Message}"));
        }
        Settings.LastOutput = output;
        Settings.Save();
        return (output, null);
    }

    [RelayCommand]
    private async Task BrowseOutput()
    {
        var path = await Pick(L.T("Output folder"));
        if (path is not null) OutputFolder = path;
    }

    [RelayCommand]
    private async Task BrowseGame()
    {
        var path = await Pick(L.T("GTA V game folder"));
        if (path is not null) GameFolder = path;
    }

    internal async Task<string?> Pick(string title) => PickFolder is null ? null : await PickFolder(title);

    // ================================================================ build / install

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(ReviewInstallCommand), nameof(ApplyInstalledCommand),
                                nameof(ReviewChangesCommand), nameof(ConfirmPlanCommand), nameof(UpdateCopiesCommand),
                                nameof(SetLanguageCommand), nameof(ToggleOnlineCommand))]
    public partial bool IsBuilding { get; set; }

    [ObservableProperty] public partial string StageStatus { get; set; } = "";
    [ObservableProperty] public partial string StageHint { get; set; } = "";

    // ---- a running plan: its steps, and stopping it
    private PlanRun? _run;
    [ObservableProperty] public partial bool HasStageSteps { get; set; }
    [ObservableProperty] public partial int StageStep { get; set; }
    [ObservableProperty] public partial int StageSteps { get; set; }
    [ObservableProperty] public partial string StageStepText { get; set; } = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    public partial bool CanCancelRun { get; set; }
    [ObservableProperty] public partial string CancelRunText { get; set; } = L.T("Cancel");

    /// <summary>Follow a plan while it runs (null: a job with no plan of its own).</summary>
    internal void Follow(PlanRun? run)
    {
        _run = run;
        HasStageSteps = false;
        StageStep = 0;
        StageSteps = 0;
        StageStepText = "";
        CanCancelRun = run is not null;
        CancelRunText = L.T("Cancel");
        if (run is null) return;
        run.Progress += p => Dispatcher.UIThread.Post(() =>
        {
            if (_run != run) return;
            StageSteps = p.Steps;
            StageStep = p.Step;
            HasStageSteps = p.Steps > 1;
            StageStepText = L.T($"Step {p.Step} of {p.Steps}: {p.What}");
        });
    }

    /// <summary>Stop the running plan after its current step; what it did is taken back.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    private void CancelRun()
    {
        if (_run is not { } run || run.Cancelled) return;
        run.Cancel();
        CanCancelRun = false;
        CancelRunText = L.T("Stopping…");
        StageHint = L.T("Stopping — taking back what was done");
        AppLog.Info("run: cancel asked");
    }
    /// <summary>Incremented every time the build reaches a new phase (the stage animation reacts to it).</summary>
    [ObservableProperty] public partial int FireCount { get; set; }

    [ObservableProperty] public partial string LogText { get; set; } = "";
    [ObservableProperty] public partial bool HasLog { get; set; }
    [ObservableProperty] public partial bool LogIsError { get; set; }
    [ObservableProperty] public partial string CopyLogLabel { get; set; } = L.T("Copy");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFooter))]
    public partial bool ResultVisible { get; set; }
    [ObservableProperty] public partial bool ResultOk { get; set; }
    [ObservableProperty] public partial string ResultTitle { get; set; } = "";
    [ObservableProperty] public partial string ResultDetail { get; set; } = "";
    [ObservableProperty] public partial string? ResultPath { get; set; }
    [ObservableProperty] public partial bool HasResultPath { get; set; }

    private bool CanBuild() => !IsBuilding;

    /// <summary>Build (modder) / install (player) right away — the player's button reviews the plan first.</summary>
    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task Build()
    {
        ResultVisible = false;
        if (IsPlayer && !HasGame)
        {
            IsGameDialogOpen = true;                   // nowhere to install yet — ask for the game
            return;
        }
        var (job, error) = PrepareJob();
        if (job is null)
        {
            ShowResult(false, "Couldn't start", error ?? L.T("Unknown error."), null);
            return;
        }
        await RunJobAsync(job);
    }

    /// <summary>The active panel's job, or why there is nothing to run.</summary>
    private (PanelJob? Job, string? Error) PrepareJob()
    {
        if (ActivePanel is { } panel) return panel.Prepare();
        if (IsPreparing) return (null, L.T("The drop is still being unpacked — wait a moment."));
        return (null, DropError ?? L.T("Drop a mod — its folder or a .zip / .rar / .7z archive — into the Source area first."));
    }

    /// <summary>Run a panel's job behind the build stage; the result goes to the banner, the details to the log.</summary>
    internal async Task RunJobAsync(PanelJob job)
    {
        StartLog(job.LogHeader);
        IsBuilding = true;
        StageStatus = job.Stage;
        StageHint = L.T("Getting ready");
        Follow(job.Control);
        AppLog.Info($"build started: mode={(IsPlayer ? "player" : "modder")} panel={ActivePanel?.Category}");
        try
        {
            var outcome = await Task.Run(() => job.Run(OnLog));
            if (!outcome.Ok) FailLog(null);
            ShowResult(outcome.Ok, outcome.Title, outcome.Detail, outcome.Path);
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("build cancelled");
            ShowResult(false, L.T("Cancelled — nothing changed"), L.T("Every step done so far was taken back; the game is as it was."), null);
        }
        catch (Exception ex)
        {
            AppLog.Error("build failed", ex);
            FailLog(ex);
            ShowResult(false, L.T("Build failed"), L.T($"{ex.Message}\n\nSee the log for details."), null);
        }
        finally
        {
            IsBuilding = false;
            Follow(null);
        }
        if (IsPlayer) await AfterGameChangedAsync();
    }

    /// <summary>The game was changed: re-read the library and the status, let the file index catch up.</summary>
    private async Task AfterGameChangedAsync()
    {
        if (GameFolder.Trim() is { Length: > 0 } game) GameFilesChanged?.Invoke(game);
        await RefreshInstalledAsync();
        await RefreshStatusAsync();
    }

    private void StartLog(IEnumerable<string> header)
    {
        _log.Clear();
        foreach (var line in header) _log.AppendLine(line);
        LogText = _log.ToString();
        HasLog = _log.Length > 0;
        LogIsError = false;
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

    /// <summary>Map pipeline / installer log lines to the caption shown on the build stage.</summary>
    internal static string? PhaseOf(string msg)
    {
        if (msg.StartsWith("Building template library", StringComparison.Ordinal)) return L.T("Building the template library");
        if (msg.StartsWith("Scan:", StringComparison.Ordinal)) return L.T("Scanning models");
        if (msg.StartsWith("Components:", StringComparison.Ordinal)) return L.T("Writing meta files");
        if (msg.StartsWith("Unpacking", StringComparison.Ordinal)) return L.T("Unpacking the archive");
        if (msg.StartsWith("Merged into", StringComparison.Ordinal)) return L.T("Loading the shared pack");
        if (msg.StartsWith("WEAPON hash", StringComparison.Ordinal)) return L.T("Assembling the dlcpack");
        if (msg.StartsWith("Self-checking", StringComparison.Ordinal)) return L.T("Verifying resources");
        if (msg.StartsWith("Converting", StringComparison.Ordinal)) return L.T("Converting models to gen9");
        if (msg.StartsWith("    Copying update", StringComparison.Ordinal)) return L.T("Setting up the mods folder");
        if (msg.StartsWith("Installing", StringComparison.Ordinal)) return L.T("Installing into the game");
        return null;
    }

    private void FailLog(Exception? ex)
    {
        if (ex is not null)
        {
            _log.AppendLine();
            _log.AppendLine(ex.ToString());
        }
        if (_log.Length == 0) _log.AppendLine(L.T("Unknown error."));
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
        CopyLogLabel = ok ? L.T("Copied ✓") : L.T("Error");
        await Task.Delay(1500);
        CopyLogLabel = L.T("Copy");
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
