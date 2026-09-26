using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
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
        var digits = WeaponViewModel.DigitsOnly(value);
        if (digits != value) Price = digits;
    }
}

/// <summary>One "label: value" line of the source analysis panel.</summary>
public sealed record AnalysisRow(string Label, string Value);

/// <summary>
/// The add-on weapon panel. Two flows share one engine:
/// <list type="bullet">
///   <item><b>Modder</b> — Replace → Add-On written to a chosen folder, as a packed dlc.rpf
///   or as loose folders for CodeWalker; the model can be renamed.</item>
///   <item><b>Player</b> — the weapon found in a drop, installed straight into GTA V
///   (mods/update/x64/dlcpacks + dlclist.xml), optionally into one shared AddonWeapons pack.</item>
/// </list>
/// </summary>
public sealed partial class WeaponViewModel : ModPanelViewModel
{
    public const string DefaultName = "Custom Weapon";
    public const string DefaultDesc = "An add-on weapon.";
    public const string DefaultPrice = "5000";
    public const string DefaultAmmoPrice = "100";

    private int _scanGeneration;
    /// <summary>Field → the value last filled in from a source; replaced by the next source unless edited.</summary>
    private readonly Dictionary<string, string> _suggested = [];

    public WeaponViewModel(MainViewModel shell) : base(shell) { }

    public override ModCategory Category => ModCategory.Weapon;

    private bool IsPlayer => Shell.IsPlayer;

    public override void OnModeChanged()
    {
        OnPropertyChanged(nameof(ModelNameVisible));
        OnPropertyChanged(nameof(MergePackVisible));
        _ = AnalyzeSourceAsync(ActiveInput);       // each mode has its own source: the modder's folder / the player's drop
    }

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
        var path = await Shell.Pick("Folder with the Replace files");
        if (path is not null) InputFolder = path;
    }

    partial void OnInputFolderChanged(string value)
    {
        if (!IsPlayer) _ = AnalyzeSourceAsync(value);
    }

    /// <summary>The folder the build reads: the modder's pick, or the weapon picked out of the player's drop.</summary>
    private string ActiveInput => IsPlayer ? Intake?.InputFolder ?? "" : InputFolder;

    /// <summary>The weapon picked out of the player's drop (its unpacked files, sorted into a build input).</summary>
    [ObservableProperty] public partial IntakeResult? Intake { get; set; }

    /// <summary>Take the weapon found in a drop (null: the drop is gone) and analyse it.</summary>
    internal Task UseIntakeAsync(IntakeResult? intake)
    {
        Intake = intake;
        return AnalyzeSourceAsync(ActiveInput);
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
        ++_scanGeneration;
        IsScanning = false;
        ClearPreview();
        PrebuiltRpf = null;
        SuppliedMetas = null;
        Analysis.Clear();
        Warnings.Clear();
        Components.Clear();
        HasComponents = false;
        RouteBadge = "—";
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

    // ================================================================ weapon

    [ObservableProperty] public partial string Name { get; set; } = DefaultName;
    [ObservableProperty] public partial string Description { get; set; } = DefaultDesc;
    [ObservableProperty] public partial string Price { get; set; } = DefaultPrice;
    [ObservableProperty] public partial string AmmoPrice { get; set; } = DefaultAmmoPrice;
    [ObservableProperty] public partial string ModelName { get; set; } = "";

    /// <summary>Player: all guns go into one shared AddonWeapons DLC (else a pack per gun).</summary>
    [ObservableProperty] public partial bool MergePack { get; set; } = true;

    /// <summary>Renaming the model is a modder concern; players never see it.</summary>
    public bool ModelNameVisible => !IsPlayer;
    /// <summary>The shared-pack switch belongs to the player flow.</summary>
    public bool MergePackVisible => IsPlayer;
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

    public override (PanelJob? Job, string? Error) Prepare()
    {
        var (o, error) = GatherOptions();
        if (o is null) return (null, error);
        var header = new List<string>();
        if (IsPlayer && Intake is { } src)
        {
            header.Add($"Source: {string.Join(", ", src.Sources.Select(Path.GetFileName))} — {IntakeSummary(src)}");
            foreach (var c in src.Configs) header.Add($"    config shipped as-is: {c.Name}  <- {c.Origin}");
        }
        WeaponPackage? package = null;
        if (IsPlayer)
        {
            package = new WeaponPackage { Name = o.Name, Options = o, Intake = Intake };
            package.Warnings.AddRange(Warnings);                 // what the analysis flagged, shown with the plan
        }
        return (new PanelJob(IsPlayer ? "Installing into GTA V…" : "Building Add-On…", log => Run(o, log))
        {
            Package = package,
            LogHeader = header,
        }, null);
    }

    /// <summary>"22 models / textures · 5 configs · 1 skipped" — what the weapon's drop gave the build.</summary>
    internal static string IntakeSummary(IntakeResult r)
    {
        var parts = new List<string>();
        if (r.PrebuiltRpf is not null) parts.Add("finished dlc.rpf");
        else
        {
            parts.Add(r.Models.Count == 1 ? "1 model / texture" : $"{r.Models.Count} models / textures");
            if (r.Configs.Count > 0) parts.Add(r.Configs.Count == 1 ? "1 config" : $"{r.Configs.Count} configs");
        }
        if (r.Ignored.Count > 0) parts.Add($"{r.Ignored.Count} skipped");
        return string.Join("  ·  ", parts);
    }

    /// <summary>The build (and for the player the install) — on a worker thread.</summary>
    private static PanelOutcome Run(BuildOptions opts, Action<string> log)
    {
        var result = Pipeline.BuildAddon(opts, log);
        if (result is null)
            return new PanelOutcome(false, "Build not finished", "Base weapon could not be determined — see the log.");
        if (result.InstalledTo is not null)
            return new PanelOutcome(true, $"Installed into {opts.Edition!.Value.DisplayName()}",
                                    $"Mod built and installed into the game:\n{result.InstalledTo}", result.InstalledTo);
        if (!result.Packed)
            return new PanelOutcome(true, "Done — loose folders",
                                    "Add-On built unpacked. Pack the *.rpf folders with CodeWalker (see manifest.json).",
                                    result.Root);
        return new PanelOutcome(true, "Done", $"Add-On built:\n{result.Root}", result.Root);
    }

    /// <summary>Validate the form into build options (mirrors the original backend checks).</summary>
    internal (BuildOptions? Options, string? Error) GatherOptions()
    {
        var input = ActiveInput.Trim();
        if (IsPlayer)
        {
            if (Shell.IsPreparing) return (null, "The dropped weapon is still being unpacked — wait a moment.");
            if (Intake is null)
                return (null, Shell.DropError ?? "Drop the weapon — its folder or a .zip / .rar / .7z archive — into the Source area first.");
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
            var (game, gameError) = Shell.CheckGame();
            if (game is null) return (null, gameError);
            installDir = game;
            outDir = AppPaths.StagingFor(Shell.Edition);
            packRpf = true;
        }
        else
        {
            var (output, outError) = Shell.CheckOutput();
            if (output is null) return (null, outError);
            outDir = output;
            packRpf = Shell.PackRpf;
        }

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
            Edition = Shell.Edition,
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
}
