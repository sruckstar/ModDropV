using System.Collections.ObjectModel;
using System.Globalization;
using Mdv.App.Services;
using Avalonia.Media;
using Mdv.Core.Preview;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>A model in the 3D preview's parts list: the weapon or one of its components.</summary>
public sealed partial class PreviewPieceViewModel : ObservableObject
{
    private readonly Action<PreviewPieceViewModel> _changed;

    public PreviewPieceViewModel(PreviewPiece piece, Action<PreviewPieceViewModel> changed)
    {
        Piece = piece;
        _changed = changed;
        IsOn = piece.DefaultVisible;
    }

    public PreviewPiece Piece { get; }
    public string Id => Piece.Id;
    public string Label => Piece.Label;
    public string Kind => Piece.Kind;
    public string? Bone => Piece.Attached ? Piece.Bone : null;

    /// <summary>Where it hangs, or why it floats at the weapon's origin.</summary>
    public string Where => Piece.Note ?? (Piece.Kind == "weapon" ? $"{Piece.Triangles:#,0} triangles"
        : Piece.Attached ? $"on {Piece.Bone}"
        : Piece.Bone is null ? "separate model — shown at the origin"
        : $"no {Piece.Bone} on the weapon — shown at the origin");

    [ObservableProperty] public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value) => _changed(this);
}

/// <summary>A weapon tint in the 3D preview: a swatch to pick it by.</summary>
public sealed partial class PreviewTintViewModel : ObservableObject
{
    private readonly Action<PreviewTintViewModel> _picked;

    public PreviewTintViewModel(WeaponTint tint, Action<PreviewTintViewModel> picked)
    {
        Tint = tint;
        _picked = picked;
        Swatch = new SolidColorBrush(Color.FromUInt32(tint.Swatch));
    }

    public WeaponTint Tint { get; }
    public int Index => Tint.Index;
    public string Name => Tint.Name;
    public IBrush Swatch { get; }
    /// <summary>The name with the index scripts and trainers set it by.</summary>
    public string Label => $"{Tint.Name} · tint {Tint.Index}";

    [ObservableProperty] public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (value) _picked(this);
    }
}

/// <summary>The 3D preview of the weapon found in the source.</summary>
public sealed partial class WeaponViewModel
{
    private int _previewGeneration;
    private CancellationTokenSource? _previewCts;
    private bool _syncingPieces;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(ShowPreviewPanel), nameof(PreviewTitle), nameof(PreviewStats))]
    [NotifyCanExecuteChangedFor(nameof(OpenPreviewCommand))]
    public partial WeaponModel? PreviewModel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPreviewPanel))]
    public partial bool IsLoadingPreview { get; set; }

    public bool HasPreview => PreviewModel is not null;
    public bool ShowPreviewPanel => HasPreview || IsLoadingPreview;

    /// <summary>Ids of the pieces switched on — a new set on every change, so views notice.</summary>
    [ObservableProperty] public partial IReadOnlyCollection<string>? PreviewVisible { get; set; }

    [ObservableProperty] public partial bool PreviewTextured { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewSpinning))]
    public partial bool PreviewAutoRotate { get; set; }

    [ObservableProperty] public partial int PreviewResetCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewSpinning))]
    public partial bool IsPreviewOpen { get; set; }

    /// <summary>The turntable runs only while the dialog is open — a hidden view doesn't spin.</summary>
    public bool PreviewSpinning => IsPreviewOpen && PreviewAutoRotate;

    public ObservableCollection<PreviewPieceViewModel> PreviewPieces { get; } = [];
    public ObservableCollection<PreviewTintViewModel> PreviewTints { get; } = [];

    /// <summary>The weapon tint the preview draws the palette-coloured surfaces in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewTintLabel))]
    public partial int PreviewTint { get; set; }

    [ObservableProperty] public partial bool HasPreviewTints { get; set; }

    public string PreviewTintLabel => PreviewTints.FirstOrDefault(t => t.Index == PreviewTint)?.Label ?? "";
    public ObservableCollection<string> PreviewWarnings { get; } = [];

    public string PreviewTitle => PreviewModel?.Name ?? "";

    public string PreviewStats
    {
        get
        {
            if (PreviewModel is not { } m) return "";
            var tris = m.Pieces.Where(p => PreviewVisible?.Contains(p.Id) ?? p.DefaultVisible).Sum(p => p.Triangles);
            return string.Format(CultureInfo.InvariantCulture, "{0:#,0} triangles shown · {1} texture(s) · {2} part(s)",
                                 tris, m.TextureCount, m.Pieces.Count);
        }
    }

    /// <summary>Read the models of <paramref name="folder"/> in the background; a newer call wins.</summary>
    private async Task LoadPreviewAsync(string folder)
    {
        ClearPreview();
        int gen = _previewGeneration;
        var cts = _previewCts = new CancellationTokenSource();
        IsLoadingPreview = true;
        WeaponModel? model = null;
        try
        {
            model = await Task.Run(() => WeaponModelLoader.Load(folder, cts.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error("3D preview: loading the model failed", ex);
        }
        if (gen != _previewGeneration) return;         // a newer source took over
        IsLoadingPreview = false;
        if (model is null) return;

        foreach (var p in model.Pieces.Where(p => !p.IsEmpty))
            PreviewPieces.Add(new PreviewPieceViewModel(p, OnPieceChanged));
        foreach (var w in model.Warnings) PreviewWarnings.Add(w);
        foreach (var t in model.Tints) PreviewTints.Add(new PreviewTintViewModel(t, OnTintPicked) { IsOn = t.Index == 0 });
        HasPreviewTints = PreviewTints.Count > 1;
        OnPropertyChanged(nameof(PreviewTintLabel));
        PreviewVisible = VisibleIds();
        PreviewModel = model;
        AppLog.Info($"3D preview: {model.Name}, {model.Pieces.Count} part(s), {model.Triangles} triangles, {model.TextureCount} texture(s)");
    }

    private void ClearPreview()
    {
        ++_previewGeneration;
        _previewCts?.Cancel();
        _previewCts = null;
        IsLoadingPreview = false;
        PreviewModel = null;
        PreviewPieces.Clear();
        PreviewWarnings.Clear();
        PreviewTints.Clear();
        HasPreviewTints = false;
        PreviewTint = 0;
        PreviewVisible = null;
        IsPreviewOpen = false;
    }

    /// <summary>A bone holds one component at a time, as in the game: switching one on swaps the other off.</summary>
    private void OnPieceChanged(PreviewPieceViewModel piece)
    {
        if (_syncingPieces) return;
        _syncingPieces = true;
        try
        {
            if (piece.IsOn && piece.Bone is { } bone)
                foreach (var other in PreviewPieces)
                    if (other != piece && other.Bone == bone) other.IsOn = false;
        }
        finally
        {
            _syncingPieces = false;
        }
        PreviewVisible = VisibleIds();
        OnPropertyChanged(nameof(PreviewStats));
    }

    private void OnTintPicked(PreviewTintViewModel tint)
    {
        foreach (var other in PreviewTints)
            if (other != tint) other.IsOn = false;
        PreviewTint = tint.Index;
    }

    private HashSet<string> VisibleIds() => PreviewPieces.Where(p => p.IsOn).Select(p => p.Id).ToHashSet();

    [RelayCommand(CanExecute = nameof(HasPreview))]
    private void OpenPreview() => IsPreviewOpen = true;

    [RelayCommand]
    private void ClosePreview() => IsPreviewOpen = false;

    [RelayCommand]
    private void ResetPreviewView() => PreviewResetCount++;
}
