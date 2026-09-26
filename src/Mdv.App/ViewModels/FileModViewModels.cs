using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Index;
using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>
/// The panel of a mod that changes game files (an OIV package, a replacement): the player
/// installs the package found in the drop as it is — the plan shows what it will do first.
/// </summary>
public abstract class FileModViewModel(MainViewModel shell) : ModPanelViewModel(shell)
{
    public abstract ModPackage? Package { get; }

    /// <summary>What the analysis flagged (shown here and with the plan).</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    protected void SetWarnings(IEnumerable<string> warnings)
    {
        Warnings.Clear();
        foreach (var w in warnings.Distinct()) Warnings.Add(w);
        OnPropertyChanged(nameof(HasWarnings));
    }

    /// <summary>Anything that must be settled before the install can be planned (null: ready).</summary>
    protected virtual string? NotReady() => null;

    public override (PanelJob? Job, string? Error) Prepare()
    {
        if (Shell.IsPreparing) return (null, "The drop is still being unpacked — wait a moment.");
        if (Package is not { } pkg) return (null, "Drop the mod — its folder or archive — first.");
        if (NotReady() is { } why) return (null, why);
        var game = Shell.GameFolder.Trim();
        var edition = Shell.Edition;
        var target = MainViewModel.TargetFor(game, edition);
        return (new PanelJob("Installing into GTA V…", log =>
        {
            log($"Installing «{pkg.Name}» ({pkg.Category.DisplayName()}) into {edition.DisplayName()}: {game}");
            ModLibrary.Install(pkg, target, log);
            return new PanelOutcome(true, $"Installed into {edition.DisplayName()}",
                                    $"«{pkg.Name}» is in the game — manage it in the Library:\n{game}", game);
        })
        {
            Package = pkg,
            LogHeader = pkg.Source is { } src ? [$"Source: {src.Name} — {pkg.Category.DisplayName()}"] : [],
        }, null);
    }
}

/// <summary>One step of an OIV package, as its panel lists it: "put in" + where.</summary>
public sealed record OivStepRow(string Kind, string Path, string Where);

/// <summary>An OIV package from the drop: its own card (name, version, author, colours, icon) and what it does.</summary>
public sealed partial class OivViewModel(MainViewModel shell) : FileModViewModel(shell)
{
    private OivPackage? _pkg;

    public override ModCategory Category => ModCategory.Package;
    public override ModPackage? Package => _pkg;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Byline { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial bool HasDescription { get; set; }
    [ObservableProperty] public partial string? Link { get; set; }
    [ObservableProperty] public partial bool HasLink { get; set; }
    [ObservableProperty] public partial Bitmap? Icon { get; set; }
    [ObservableProperty] public partial bool HasIcon { get; set; }
    /// <summary>The package's own header colour (the accent when it has none).</summary>
    [ObservableProperty] public partial IBrush? HeaderBrush { get; set; }
    [ObservableProperty] public partial IBrush? HeaderText { get; set; }
    [ObservableProperty] public partial bool HasHeaderColor { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    public ObservableCollection<OivStepRow> Steps { get; } = [];

    /// <summary>Show a package (null: none).</summary>
    public void Use(OivPackage? pkg)
    {
        _pkg = pkg;
        Icon?.Dispose();
        Icon = null;
        Steps.Clear();
        if (pkg is null)
        {
            SetWarnings([]);
            return;
        }
        Name = pkg.Name;
        var by = new List<string>();
        if (pkg.Version is { } v) by.Add("v" + v);
        if (pkg.Author is { } a) by.Add("by " + a);
        by.Add(pkg.FormatVersion.Length > 0 ? $"OIV {pkg.FormatVersion}" : "OIV");
        Byline = string.Join("  ·  ", by);
        Description = pkg.Description ?? "";
        HasDescription = Description.Length > 0;
        Link = pkg.Link is { } l && Uri.TryCreate(l, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? l : null;
        HasLink = Link is not null;
        try
        {
            Icon = pkg.IconPath is { } icon ? new Bitmap(icon) : null;
        }
        catch (Exception ex)
        {
            AppLog.Info($"OIV icon unreadable: {ex.Message}");
        }
        HasIcon = Icon is not null;
        if (pkg.HeaderColor is { } c && Color.TryParse(c, out var color))
        {
            HeaderBrush = new SolidColorBrush(color);
            HeaderText = pkg.BlackText ? Brushes.Black : Brushes.White;
            HasHeaderColor = true;
        }
        else
        {
            HeaderBrush = null;
            HeaderText = null;
            HasHeaderColor = false;
        }

        foreach (var s in pkg.Steps.Where(s => s is not OivArchive))
            Steps.Add(new OivStepRow(s switch
            {
                OivAdd => "put in",
                OivDelete => "delete",
                OivXml => "edit xml",
                OivText => "edit text",
                _ => "",
            }, s.Path, s.InArchive ? "in a copy of its archive under mods" : s.Path.EndsWith(".rpf") ? "into mods" : "game folder"));
        int archives = pkg.Steps.Count(s => s.InArchive);
        Summary = $"{Steps.Count} step(s) — {archives} inside game archives (done in copies under mods), " +
                  $"{Steps.Count - archives} in the game folder.";
        SetWarnings(pkg.Warnings);
    }

    [RelayCommand]
    private void OpenLink()
    {
        if (Link is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(Link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("open OIV link failed", ex);
        }
    }
}

/// <summary>A file of a replacement and the place in the game it goes (picked from the candidates).</summary>
public sealed partial class ReplaceFileRow : ObservableObject
{
    private readonly ReplacementFile _file;

    public ReplaceFileRow(ReplacementFile file)
    {
        _file = file;
        Candidates = [.. file.Candidates];
        SelectedTarget = Candidates.FirstOrDefault(c => c.GamePath == file.Target);
    }

    public string Name => _file.Name;
    public string Origin => _file.Origin;
    public IReadOnlyList<ReplaceTarget> Candidates { get; }
    public bool Found => Candidates.Count > 0;
    public bool Missing => !Found;
    /// <summary>More than one place to choose from.</summary>
    public bool HasChoice => Candidates.Count > 1;
    public string? Note => _file.Note;
    public bool HasNote => Note is not null;

    [ObservableProperty] public partial ReplaceTarget? SelectedTarget { get; set; }

    partial void OnSelectedTargetChanged(ReplaceTarget? value) => _file.Target = value?.GamePath;
}

/// <summary>
/// Loose replacement files from the drop: each file's place in the game, looked up in the game's
/// file index (in the background) whenever the drop or the game changes; the player can pick another.
/// </summary>
public sealed partial class ReplacementViewModel : FileModViewModel
{
    private ReplacementPackage? _pkg;
    private int _generation;

    public ReplacementViewModel(MainViewModel shell) : base(shell)
    {
        shell.PropertyChanged += OnShellChanged;
    }

    public override ModCategory Category => ModCategory.Replacement;
    public override ModPackage? Package => _pkg;

    public ObservableCollection<ReplaceFileRow> Files { get; } = [];

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial bool IsResolving { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.GameFolder) && _pkg is not null) _ = ResolveAsync();
    }

    protected override string? NotReady() =>
        IsResolving ? "The files are still being looked up in the game — wait a moment."
        : _pkg is not null && _pkg.Files.All(f => f.Target is null)
            ? "None of the mod's files are in this game — nothing to replace."
            : null;

    /// <summary>Show a replacement (null: none) and look its files up in the selected game.</summary>
    public Task UseAsync(ReplacementPackage? pkg)
    {
        _pkg = pkg;
        Files.Clear();
        if (pkg is null)
        {
            ++_generation;
            SetWarnings([]);
            return Task.CompletedTask;
        }
        Name = pkg.Name;
        return ResolveAsync();
    }

    private async Task ResolveAsync()
    {
        var pkg = _pkg;
        if (pkg is null) return;
        int gen = ++_generation;
        var game = Shell.GameFolder.Trim();
        Files.Clear();
        if (game.Length == 0 || !Directory.Exists(game))
        {
            Status = "Choose the GTA V folder — each file's place is looked up in the game.";
            Summary = string.Join("  ·  ", pkg.Parts);
            SetWarnings(pkg.Warnings);
            return;
        }
        IsResolving = true;
        Status = "Looking the files up in the game…";
        string? error = null;
        try
        {
            await Task.Run(() => ReplacementHandler.Resolve(pkg, GameIndex.Open(game, GameIndexCache.DefaultRoot)));
        }
        catch (Exception ex)
        {
            AppLog.Error("resolving replacement targets failed", ex);
            error = $"The game's files could not be read: {ex.Message}";
        }
        if (gen != _generation) return;
        IsResolving = false;
        foreach (var f in pkg.Files) Files.Add(new ReplaceFileRow(f));
        int found = pkg.Files.Count(f => f.Target is not null);
        Status = error ?? (found == pkg.Files.Count
            ? $"All {found} file(s) found in the game."
            : $"{found} of {pkg.Files.Count} file(s) found in the game — the rest are skipped.");
        Summary = string.Join("  ·  ", pkg.Parts);
        SetWarnings(pkg.Warnings);
    }
}
