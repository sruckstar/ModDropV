using System.Collections.ObjectModel;
using Avalonia.Threading;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Mods;
using Mdv.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mdv.App.ViewModels;

/// <summary>
/// One mod found in the player's drop: installable (a handler turned it into a package) or
/// not yet (a kind ModDrop V doesn't install, or its handler said why it can't).
/// </summary>
public sealed partial class DetectedModViewModel : ObservableObject
{
    private readonly Action<DetectedModViewModel>? _picked;

    public DetectedModViewModel(ModCategory category, string title, string detail, ModPackage? package, string state,
                                Action<DetectedModViewModel>? picked = null)
    {
        Category = category;
        Title = title;
        Detail = detail;
        Package = package;
        State = state;
        _picked = picked;
    }

    public ModCategory Category { get; }
    public string CategoryName => Category.ShortName();
    public string Title { get; }
    /// <summary>What was found ("22 model / texture file(s) · own config …") or the evidence.</summary>
    public string Detail { get; }
    public ModPackage? Package { get; }
    public bool Installable => Package is not null;
    /// <summary>"ready" / "coming soon" / "can't install".</summary>
    public string State { get; }

    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _picked?.Invoke(this);
    }
}

/// <summary>Player flow: the drop — unpacked, looked at by every mod handler, one of the mods found picked.</summary>
public sealed partial class MainViewModel
{
    private int _prepGeneration;
    private CancellationTokenSource? _prepCts;
    private DroppedSource? _drop;
    private bool _pickingDetected;
    /// <summary>The picked mod's panel reading its files (awaited by the drop, so a drop is done when its panel is).</summary>
    private Task _panelLoad = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DropZoneEmpty))]
    public partial bool IsPreparing { get; set; }

    [ObservableProperty] public partial string PrepareStatus { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDropError), nameof(ShowDropProblem), nameof(ShowDropNotice))]
    public partial string? DropError { get; set; }

    public bool HasDropError => DropError is not null;
    /// <summary>The drop couldn't be read (red).</summary>
    public bool ShowDropProblem => HasDropError && !HasDrop;
    /// <summary>The drop was read, but nothing in it can be installed (amber).</summary>
    public bool ShowDropNotice => HasDropError && HasDrop;

    /// <summary>A drop is loaded (unpacked and analysed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DropZoneEmpty), nameof(ShowDropProblem), nameof(ShowDropNotice))]
    public partial bool HasDrop { get; set; }

    public bool DropZoneEmpty => !HasDrop && !IsPreparing;

    [ObservableProperty] public partial string DropTitle { get; set; } = "";
    [ObservableProperty] public partial string DropSummary { get; set; } = "";

    /// <summary>What the drop holds, installable ones first.</summary>
    public ObservableCollection<DetectedModViewModel> Detected { get; } = [];

    [ObservableProperty] public partial bool HasDetected { get; set; }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task BrowseSourceFolder()
    {
        var path = await Pick("Folder with the mod");
        if (path is not null) await LoadPlayerSourceAsync([path]);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task BrowseSourceArchive()
    {
        if (PickArchives is null) return;
        var files = await PickArchives("Mod archive");
        if (files.Count > 0) await LoadPlayerSourceAsync(files);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ClearPlayerSource()
    {
        _prepCts?.Cancel();
        ++_prepGeneration;
        IsPreparing = false;
        DropError = null;
        ForgetDrop();                                 // its workspace goes with the next drop
    }

    /// <summary>Files / folders dropped on the window (called by the view).</summary>
    public Task DropSourceAsync(IReadOnlyList<string> paths) => LoadPlayerSourceAsync(paths);

    private void ForgetDrop()
    {
        _drop = null;
        HasDrop = false;
        DropTitle = "";
        DropSummary = "";
        Detected.Clear();
        HasDetected = false;
        SelectedPanel = null;
        _ = Weapon.UseIntakeAsync(null);
        Oiv.Use(null);
        _ = Replace.UseAsync(null);
        Script.Use(null);
        _ = Addon.UseAsync(null);
        _ = Livery.UseAsync(null);
        _ = Clothing.UseAsync(null);
        _ = Map.UseAsync(null);
    }

    /// <summary>
    /// Unpack a dropped source in the background and let every mod handler look at it. A
    /// newer drop cancels an older one still unpacking; only the latest result (and its
    /// workspace) survives. The first installable mod found is picked.
    /// </summary>
    internal async Task LoadPlayerSourceAsync(IReadOnlyList<string> paths)
    {
        if (IsBuilding || paths.Count == 0) return;
        _prepCts?.Cancel();
        var cts = _prepCts = new CancellationTokenSource();
        int gen = ++_prepGeneration;

        IsPreparing = true;
        DropError = null;
        ForgetDrop();                                 // the new drop replaces the old one
        PrepareStatus = $"Reading {Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]))}…";
        AppLog.Info($"player source: {string.Join(" | ", paths)}");

        DroppedSource? dropped = null;
        DropAnalysis? analysis = null;
        string? error = null;
        try
        {
            (dropped, analysis) = await Task.Run(() =>
            {
                // only one drop is kept: clear earlier workspaces before this one is created
                SourceIntake.CleanWorkRoot(AppPaths.SourcesDir);
                void Progress(string msg) => Dispatcher.UIThread.Post(() =>
                {
                    if (gen == _prepGeneration) PrepareStatus = msg;
                });
                var d = SourceIntake.Gather(paths, AppPaths.SourcesDir, Progress, cts.Token);
                Progress("Looking at what's inside…");
                return (d, ModLibrary.Analyze(d, new HandlerEnv(AppPaths.Data)));
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
            if (dropped is not null) PathUtil.TryDeleteDir(dropped.WorkDir);
            return;
        }
        IsPreparing = false;
        if (dropped is null || analysis is null)
        {
            DropError = error;
            return;
        }

        _drop = dropped;
        HasDrop = true;
        DropTitle = string.Join(", ", dropped.Sources.Select(s => Path.GetFileName(Path.TrimEndingDirectorySeparator(s))));
        DropSummary = Summarize(dropped);
        AppLog.Info($"player source analysed: {analysis.Report.Summary()}; packages: " +
                    $"{string.Join(", ", analysis.Packages.Select(p => $"{p.Category} «{p.Name}»"))}" +
                    string.Concat(analysis.Problems.Select(kv => $"; {kv.Key}: {kv.Value}")));

        foreach (var item in DetectedItems(analysis)) Detected.Add(item);
        HasDetected = Detected.Count > 0;
        if (Detected.FirstOrDefault(d => d.Installable) is { } first)
        {
            first.IsSelected = true;                   // picks its panel
            await _panelLoad;
            return;
        }
        DropError = NothingToInstall(analysis);
        AppLog.Info("player source has nothing installable: " + DropError);
        PathUtil.TryDeleteDir(dropped.WorkDir);
    }

    /// <summary>"12 files · 1 archive unpacked".</summary>
    private static string Summarize(DroppedSource d)
    {
        var parts = new List<string> { d.Files.Count == 1 ? "1 file" : $"{d.Files.Count} files" };
        if (d.Archives.Count > 0) parts.Add(d.Archives.Count == 1 ? "unpacked" : $"{d.Archives.Count} archives unpacked");
        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// The packages, then the kinds found that can't be installed (yet). Weak side findings
    /// (a quarter of the main one's score or less — a weapon's FiveM manifest naming a ped
    /// personality file) are left out.
    /// </summary>
    private List<DetectedModViewModel> DetectedItems(DropAnalysis a)
    {
        var items = new List<DetectedModViewModel>();
        foreach (var p in a.Packages)
            items.Add(new DetectedModViewModel(p.Category, p.Name, string.Join("  ·  ", p.Parts), p, "ready", OnDetectedPicked));
        int top = a.Report.Primary?.Score ?? 0;
        foreach (var d in a.Report.Found)
        {
            if (a.Packages.Any(p => p.Category == d.Category)) continue;
            // what goes in as part of another mod (a map's scripts) is no mod of its own here
            if (a.Packages.OfType<AddonPackage>().Any(p => p.Extras.Any(e => e.Category == d.Category))) continue;
            if (a.Packages.Any(p => p.Category == ModCategory.Package)) continue;          // it is all the OIV's content
            bool main = d == a.Report.Primary;
            if (a.Problems.TryGetValue(d.Category, out var why))
                items.Add(new DetectedModViewModel(d.Category, d.Category.DisplayName(), why, null, "can't install"));
            else if (main || (d.Score >= 3 && d.Score * 4 > top))
                items.Add(new DetectedModViewModel(d.Category, d.Category.DisplayName(),
                                                   string.Join(", ", d.Evidence.Take(3)), null, "coming soon"));
        }
        return items;
    }

    /// <summary>Why nothing in the drop can be installed.</summary>
    private static string NothingToInstall(DropAnalysis a)
    {
        if (a.Report.Primary is not { } p)
            return "Nothing ModDrop V recognises was found in what was dropped — no weapon models, OIV package, " +
                   "game files to replace (.ytd / .yft / .awc / .meta …), scripts or add-on packs.";
        if (a.Problems.TryGetValue(p.Category, out var why)) return why;
        return $"This looks like a {p.Category.DisplayName().ToLowerInvariant()} mod ({string.Join(", ", p.Evidence.Take(2))}). " +
               "ModDrop V installs weapons, vehicles, peds, liveries, clothes, OIV packages, file replacements and scripts for now — other mod types are on the way.";
    }

    /// <summary>A mod of the drop was picked: the others are unpicked and its panel takes over.</summary>
    private void OnDetectedPicked(DetectedModViewModel item)
    {
        if (_pickingDetected) return;
        _pickingDetected = true;
        try
        {
            foreach (var other in Detected)
                if (other != item) other.IsSelected = false;
        }
        finally
        {
            _pickingDetected = false;
        }
        switch (item.Package)
        {
            case WeaponPackage w:
                SelectedPanel = Weapon;
                _panelLoad = Weapon.UseIntakeAsync(w.Intake);
                break;
            case OivPackage o:
                Oiv.Use(o);
                SelectedPanel = Oiv;
                break;
            case ReplacementPackage r:
                SelectedPanel = Replace;
                _panelLoad = Replace.UseAsync(r);
                break;
            case ScriptPackage s:
                Script.Use(s);
                SelectedPanel = Script;
                break;
            case AddonPackage { Kind: ModCategory.Map or ModCategory.Prop } map:
                SelectedPanel = Map;
                _panelLoad = Map.UseAsync(map);
                break;
            case PlacementPackage pl:
                SelectedPanel = Map;
                _panelLoad = Map.UseAsync(null, pl);
                break;
            case AddonPackage { Kind: ModCategory.Clothing } cl:
                SelectedPanel = Clothing;
                _panelLoad = Clothing.UseAsync(cl);
                break;
            case AddonPackage ad:
                SelectedPanel = Addon;
                _panelLoad = Addon.UseAsync(ad);
                break;
            case LiveryPackage lv:
                SelectedPanel = Livery;
                _panelLoad = Livery.UseAsync(lv);
                break;
            default:
                SelectedPanel = null;
                break;
        }
    }
}
