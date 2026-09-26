using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Index;
using Mdv.Core.Mods;
using Mdv.Core.Preview;
using Mdv.Core.Textures;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>Texture pixels as a bitmap for the UI.</summary>
internal static class TextureBitmaps
{
    public static Bitmap? From(TexturePixels? px)
    {
        if (px is null || px.Width == 0 || px.Height == 0) return null;
        try
        {
            var wb = new WriteableBitmap(new PixelSize(px.Width, px.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using var fb = wb.Lock();
            var bytes = MemoryMarshal.AsBytes(px.Bgra.AsSpan());
            for (int y = 0; y < px.Height; y++)
                Marshal.Copy(bytes.Slice(y * px.Width * 4, px.Width * 4).ToArray(), 0, fb.Address + y * fb.RowBytes, px.Width * 4);
            return wb;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            return null;                                        // no renderer (headless tests): no thumbnail
        }
    }
}

/// <summary>A picture of the livery: what it looks like, the vehicle texture it replaces (picked from the vehicle's), and that texture now.</summary>
public sealed partial class LiveryTextureRow : ObservableObject
{
    private readonly LiveryTexture _tex;
    private readonly LiveryResolution? _r;
    private readonly Action _changed;
    private bool _init = true;

    public LiveryTextureRow(LiveryTexture tex, LiveryResolution? r, Bitmap? picture, Action changed)
    {
        _tex = tex;
        _r = r;
        _changed = changed;
        Picture = picture;
        Slots = r is null ? [] : [.. r.Slots.Select(s => s.Name)];
        SelectedSlot = tex.Slot;
        _init = false;
        LoadCurrent();
    }

    public string Name => _tex.Name;
    public string Origin => _tex.Origin;
    public Bitmap? Picture { get; }
    public bool HasPicture => Picture is not null;
    public IReadOnlyList<string> Slots { get; }
    public bool HasSlots => Slots.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaced), nameof(IsSkipped))]
    public partial string? SelectedSlot { get; set; }

    public bool IsPlaced => SelectedSlot is not null;
    public bool IsSkipped => SelectedSlot is null && HasSlots;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrent))]
    public partial Bitmap? Current { get; set; }

    public bool HasCurrent => Current is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; set; }

    public bool HasNote => Note is not null;

    /// <summary>"512×512 DXT1" of the texture it replaces.</summary>
    [ObservableProperty] public partial string SlotInfo { get; set; } = "";

    partial void OnSelectedSlotChanged(string? value)
    {
        if (_init) return;
        _tex.Slot = value;
        _tex.Note = null;
        LoadCurrent();
        _changed();
    }

    private void LoadCurrent()
    {
        Note = _tex.Note;
        var slot = SelectedSlot;
        var info = slot is null ? null : _r?.Slots.FirstOrDefault(s => s.Name.Equals(slot, StringComparison.OrdinalIgnoreCase));
        SlotInfo = info is null ? "" : $"{info.Width}×{info.Height} {info.Format}";
        Current = slot is null || _r is null ? null : TextureBitmaps.From(_r.GamePixels(slot, 128));
    }
}

/// <summary>A modkit livery model and what happens to it.</summary>
public sealed record LiveryModelRow(string Name, string Detail, bool Problem)
{
    public bool IsOk => !Problem;
}

/// <summary>
/// A vehicle livery from the drop: the vehicle it goes on (guessed from the textures' names, the mod's name — or picked,
/// the game's vehicles and installed add-ons), which texture of it each picture replaces (with the picture and the
/// texture the game has now side by side), its modkit livery models, and the vehicle wearing it in 3D. Looked up in the
/// selected game again whenever the game or the vehicle changes.
/// </summary>
public sealed partial class LiveryViewModel : FileModViewModel
{
    private LiveryPackage? _pkg;
    private int _generation;
    private bool _loading;
    private readonly Dictionary<string, Bitmap?> _pictures = new(StringComparer.OrdinalIgnoreCase);

    public LiveryViewModel(MainViewModel shell) : base(shell)
    {
        shell.PropertyChanged += OnShellChanged;
    }

    public override ModCategory Category => ModCategory.Livery;
    public override ModPackage? Package => _pkg;

    public ModelPreview Preview { get; } = new();

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";

    /// <summary>The vehicles it can go on: the game's own and the installed add-ons (model names).</summary>
    [ObservableProperty] public partial IReadOnlyList<string> Vehicles { get; set; } = [];
    private List<LiveryVehicle> _vehicles = [];

    /// <summary>The vehicle picked (a model name).</summary>
    [ObservableProperty] public partial string? Vehicle { get; set; }
    [ObservableProperty] public partial string VehicleNote { get; set; } = "";

    /// <summary>Other vehicles of the game whose textures the pictures fit.</summary>
    public ObservableCollection<string> Alternatives { get; } = [];
    [ObservableProperty] public partial bool HasAlternatives { get; set; }

    public ObservableCollection<LiveryTextureRow> Textures { get; } = [];
    public ObservableCollection<LiveryModelRow> Models { get; } = [];
    [ObservableProperty] public partial bool HasTextures { get; set; }
    [ObservableProperty] public partial bool HasModels { get; set; }

    [ObservableProperty] public partial bool IsResolving { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";

    public string Note =>
        "The pictures go into the vehicle's own texture dictionaries (compressed like the textures they replace), in copies " +
        "of the game's archives under mods — or into the add-on's pack. Switch the livery off or remove it in the Library " +
        "and the vehicle's previous textures come back.";

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_pkg is not null && e.PropertyName is nameof(MainViewModel.GameFolder) or nameof(MainViewModel.IsEnhanced))
        {
            LoadVehicles();
            _ = ResolveAsync();
        }
    }

    partial void OnVehicleChanged(string? value)
    {
        if (_loading || _pkg is null || value is null) return;
        var v = value.Trim().ToLowerInvariant();
        if (v == _pkg.Vehicle || !_vehicles.Any(x => x.Model == v)) return;
        _pkg.Vehicle = v;
        _pkg.VehicleFrom = "your choice";
        foreach (var t in _pkg.Textures) t.Slot = null;
        _ = ResolveAsync();
    }

    [RelayCommand]
    private void PickVehicle(string? model)
    {
        if (model is not null) Vehicle = model;
    }

    protected override string? NotReady()
    {
        if (_pkg is null) return null;
        if (IsResolving) return "The livery is still being looked up in the game — wait a moment.";
        if (_pkg.Vehicle is null) return "Pick the vehicle the livery is for.";
        return null;
    }

    /// <summary>Show a livery (null: none) and look it up in the selected game.</summary>
    public Task UseAsync(LiveryPackage? pkg)
    {
        _pkg = pkg;
        ++_generation;
        Textures.Clear();
        Models.Clear();
        foreach (var b in _pictures.Values) b?.Dispose();
        _pictures.Clear();
        if (pkg is null)
        {
            SetWarnings([]);
            _ = Preview.LoadAsync(null);
            return Task.CompletedTask;
        }
        Name = pkg.Name;
        Summary = string.Join("  ·  ", pkg.Parts);
        LoadVehicles();
        return ResolveAsync();
    }

    private void LoadVehicles()
    {
        if (_pkg is not { } pkg) return;
        var game = Shell.GameFolder.Trim();
        _vehicles = game.Length > 0 && Directory.Exists(game)
            ? LiveryHandler.Vehicles(MainViewModel.TargetFor(game, Shell.Edition), null)
            : [.. VanillaModels.Load(null).Vehicles.Order(StringComparer.Ordinal).Select(v => new LiveryVehicle(v))];
        Vehicles = [.. _vehicles.Select(v => v.Model).Distinct()];
        _loading = true;
        Vehicle = pkg.Vehicle;
        _loading = false;
    }

    private void ShowVehicle()
    {
        if (_pkg is not { } pkg) return;
        _loading = true;
        Vehicle = pkg.Vehicle;
        _loading = false;
        var addon = _vehicles.FirstOrDefault(v => v.Model == pkg.Vehicle && v.Pack is not null);
        VehicleNote = pkg.Vehicle is null
            ? pkg.Guesses.Count > 0 ? "Not clear which vehicle it is for — pick one." : "The mod doesn't say which vehicle it is for — pick one."
            : $"{(addon is null ? "The game's own" : $"An installed add-on ({addon.Label}, dlcpacks\\{addon.Pack})")} — by {pkg.VehicleFrom}.";
        Alternatives.Clear();
        foreach (var g in pkg.Guesses.Where(g => g != pkg.Vehicle).Take(8)) Alternatives.Add(g);
        HasAlternatives = Alternatives.Count > 0;
    }

    private async Task ResolveAsync()
    {
        var pkg = _pkg;
        if (pkg is null) return;
        int gen = ++_generation;
        ShowVehicle();
        Textures.Clear();
        Models.Clear();
        var game = Shell.GameFolder.Trim();
        if (game.Length == 0 || !Directory.Exists(game) || pkg.Vehicle is null)
        {
            Status = pkg.Vehicle is null ? "Pick the vehicle — its textures are read from the game."
                : "Choose the GTA V folder — the vehicle's textures are read from the game.";
            ShowRows(null);
            SetWarnings(pkg.Warnings);
            _ = Preview.LoadAsync(null);
            return;
        }
        IsResolving = true;
        Status = $"Reading the {pkg.Vehicle}'s textures from the game…";
        var target = MainViewModel.TargetFor(game, Shell.Edition);
        LiveryResolution? r = null;
        string? error = null;
        try
        {
            r = await Task.Run(() => LiveryHandler.Resolve(pkg, target, GameIndex.Open(game, GameIndexCache.DefaultRoot)));
        }
        catch (Exception ex)
        {
            AppLog.Error("resolving the livery failed", ex);
            error = $"The game's files could not be read: {ex.Message}";
        }
        if (gen != _generation) return;
        IsResolving = false;
        ShowRows(r);
        int placed = pkg.Textures.Count(t => t.Slot is not null);
        Status = error ?? (r is null ? "" : r.Dictionaries.Count == 0 && pkg.Textures.Count > 0
            ? $"The game has no texture dictionary of the {pkg.Vehicle}."
            : pkg.Textures.Count == 0 ? $"{pkg.Models.Count} livery model(s) for the {pkg.Vehicle}."
            : placed == pkg.Textures.Count ? $"All {placed} picture(s) have a place on the {pkg.Vehicle}."
            : $"{placed} of {pkg.Textures.Count} picture(s) placed — pick a texture for the rest, or they are skipped.");
        SetWarnings([.. pkg.Warnings, .. r?.Warnings ?? []]);
        _ = PreviewAsync();
    }

    private void ShowRows(LiveryResolution? r)
    {
        if (_pkg is not { } pkg) return;
        Textures.Clear();
        foreach (var t in pkg.Textures)
        {
            var key = t.Source + "|" + t.Inner;
            if (!_pictures.TryGetValue(key, out var bmp) || bmp is null)
                _pictures[key] = bmp = TextureBitmaps.From(t.Inner is null ? TextureImages.Preview(t.Source, 128) : Ytd.Pixels(File.ReadAllBytes(t.Source), t.Inner, 128));
            Textures.Add(new LiveryTextureRow(t, r, bmp, () => _ = PreviewAsync()));
        }
        Models.Clear();
        foreach (var m in pkg.Models)
            Models.Add(r is null ? new LiveryModelRow(m.Name, "", false)
                : m.Replaces ? new LiveryModelRow(m.Name, "replaces the game's livery of this name", false)
                : r.Carcols is not null ? new LiveryModelRow(m.Name, $"a new livery — added to the modkit {r.KitName}", false)
                : new LiveryModelRow(m.Name, r.KitProblem ?? "skipped", true));
        HasTextures = Textures.Count > 0;
        HasModels = Models.Count > 0;
    }

    private Task PreviewAsync()
    {
        if (_pkg is not { Resolved: not null } pkg) return Preview.LoadAsync(null);
        return Preview.LoadAsync(ct => AddonModelLoader.Load(pkg, ct));
    }
}
