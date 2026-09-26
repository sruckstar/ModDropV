using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Rendering;
using Avalonia.Threading;
using Mdv.App.Rendering;
using Mdv.App.Services;
using Mdv.Core.Preview;
using SkiaSharp;

namespace Mdv.App.Controls;

/// <summary>
/// An interactive 3D view of a <see cref="WeaponModel"/>: drag to turn it, the wheel zooms,
/// right / middle / Shift-drag pans, a double-click frames it again. Frames are rendered by
/// <see cref="SoftRenderer"/> on a worker thread — a quick one while it moves, a supersampled
/// one once it rests.
/// </summary>
public sealed class WeaponModelView : SkiaControl, ICustomHitTest
{
    // the Skia draw operation isn't hit-testable and there's no background: without this the
    // pointer and the wheel fall straight through to whatever lies behind the view
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public static readonly StyledProperty<WeaponModel?> ModelProperty =
        AvaloniaProperty.Register<WeaponModelView, WeaponModel?>(nameof(Model));

    /// <summary>Ids of the pieces to draw; null draws the ones visible by default.</summary>
    public static readonly StyledProperty<IReadOnlyCollection<string>?> VisiblePiecesProperty =
        AvaloniaProperty.Register<WeaponModelView, IReadOnlyCollection<string>?>(nameof(VisiblePieces));

    public static readonly StyledProperty<bool> TexturedProperty =
        AvaloniaProperty.Register<WeaponModelView, bool>(nameof(Textured), true);

    /// <summary>The weapon tint palette-coloured surfaces are drawn in (0 = the default).</summary>
    public static readonly StyledProperty<int> TintProperty =
        AvaloniaProperty.Register<WeaponModelView, int>(nameof(Tint));

    public static readonly StyledProperty<bool> AutoRotateProperty =
        AvaloniaProperty.Register<WeaponModelView, bool>(nameof(AutoRotate));

    /// <summary>Bumped to frame the model again (the "reset view" button).</summary>
    public static readonly StyledProperty<int> ResetCountProperty =
        AvaloniaProperty.Register<WeaponModelView, int>(nameof(ResetCount));

    public WeaponModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public IReadOnlyCollection<string>? VisiblePieces
    {
        get => GetValue(VisiblePiecesProperty);
        set => SetValue(VisiblePiecesProperty, value);
    }

    public bool Textured
    {
        get => GetValue(TexturedProperty);
        set => SetValue(TexturedProperty, value);
    }

    public int Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public bool AutoRotate
    {
        get => GetValue(AutoRotateProperty);
        set => SetValue(AutoRotateProperty, value);
    }

    public int ResetCount
    {
        get => GetValue(ResetCountProperty);
        set => SetValue(ResetCountProperty, value);
    }

    private const float Fov = 30 * MathF.PI / 180;
    private const float DefaultYaw = -32 * MathF.PI / 180;
    private const float DefaultPitch = 14 * MathF.PI / 180;
    private const float MaxPitch = 88 * MathF.PI / 180;
    /// <summary>Supersampled frames are capped at this many pixels.</summary>
    private const int MaxFinePixels = 7_000_000;

    // camera
    private float _yaw = DefaultYaw, _pitch = DefaultPitch, _distance = 1, _fitDistance = 1;
    private Vector3 _target;
    private Vector3 _fitMin, _fitMax;                  // the bounds the view was last framed on
    private bool _framed;
    private bool _moved;                               // the user turned / zoomed / panned since the last framing

    // interaction
    private enum Drag { None, Rotate, Pan }
    private Drag _drag;
    private Point _last;
    private TimeSpan? _lastFrame;
    private bool _spinning;

    // rendering: one worker, the latest request wins, results come back to the UI thread.
    // Quick frames follow the pointer; once nothing new arrives for a moment the worker
    // renders the same view again, supersampled.
    private sealed record Job(IReadOnlyList<PreviewMesh> Meshes, OrbitCamera Camera, bool Textured, int Tint, double Width, double Height);
    private const int SettleMs = 160;
    private readonly SoftRenderer _renderer = new();
    private readonly object _gate = new();
    private Job? _pending;
    private bool _working;
    private float _draftScale = 1;
    private FrameImage? _image;
    /// <summary>Replaced images and when they were replaced; freed once no frame can still draw them.</summary>
    private readonly Queue<(FrameImage Image, long Ticks)> _retired = new();
    private const int RetireMs = 300;

    public WeaponModelView()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            _framed = false;
            FitView();
            Request();
        }
        else if (change.Property == VisiblePiecesProperty)
        {
            // a part that sticks out (a suppressor, a scope) or goes away re-frames the view;
            // swapping like for like (one magazine for another) leaves the camera alone
            if (Model is { } m)
            {
                var (min, max) = m.Bounds(VisibleList().ToList() is { Count: > 0 } v ? v : null);
                float tol = (_fitMax - _fitMin).Length() * 0.03f;
                if (Vector3.Distance(min, _fitMin) > tol || Vector3.Distance(max, _fitMax) > tol) _framed = false;
            }
            Request();
        }
        else if (change.Property == TexturedProperty || change.Property == TintProperty)
            Request();
        else if (change.Property == ResetCountProperty)
            ResetView();
        else if (change.Property == AutoRotateProperty)
            Spin();
        else if (change.Property == BoundsProperty)
        {
            if (!_moved) _framed = false;              // an untouched view keeps the model framed as it resizes
            if (!_framed) FitView();
            Request();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Request();
        Spin();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // a frame still queued on the render thread finds its image disposed and skips it
        _image?.Dispose();
        _image = null;
        while (_retired.Count > 0) _retired.Dequeue().Image.Dispose();
    }

    // ------------------------------------------------------------------ camera

    private IEnumerable<PreviewPiece> VisibleList()
    {
        if (Model is not { } m) return [];
        var ids = VisiblePieces;
        return m.Pieces.Where(p => ids?.Contains(p.Id) ?? p.DefaultVisible);
    }

    /// <summary>Aim at the visible pieces and back off until they fill the view.</summary>
    private void FitView()
    {
        if (Model is not { } m || Bounds.Width < 2 || Bounds.Height < 2) return;
        var visible = VisibleList().ToList();
        var (min, max) = m.Bounds(visible.Count > 0 ? visible : null);
        _fitMin = min;
        _fitMax = max;
        _target = (min + max) * 0.5f;
        var cam = new OrbitCamera(_yaw, _pitch, 1, _target, Fov);
        _fitDistance = Math.Max(1e-3f, cam.FitDistance(min, max, (float)(Bounds.Width / Bounds.Height), 0.1f));
        _distance = _fitDistance;
        _framed = true;
        _moved = false;
    }

    private OrbitCamera Camera => new(_yaw, _pitch, _distance, _target, Fov);

    /// <summary>Back to the three-quarter view, framed.</summary>
    private void ResetView()
    {
        _yaw = DefaultYaw;
        _pitch = DefaultPitch;
        _framed = false;
        FitView();
        Request();
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsLeftButtonPressed && e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }
        bool pan = pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed
                   || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _drag = pan ? Drag.Pan : Drag.Rotate;
        _moved = true;
        _last = pt.Position;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == Drag.None) return;
        var p = e.GetPosition(this);
        float dx = (float)(p.X - _last.X), dy = (float)(p.Y - _last.Y);
        _last = p;
        if (_drag == Drag.Rotate)
        {
            _yaw -= dx * 0.0105f;
            _pitch = Math.Clamp(_pitch + dy * 0.0105f, -MaxPitch, MaxPitch);
        }
        else
        {
            // move the target so the model follows the pointer at the target's depth
            var (right, up, _) = Camera.Basis();
            float perPixel = 2 * _distance * MathF.Tan(Fov / 2) / (float)Math.Max(1, Bounds.Height);
            _target += (-dx * right + dy * up) * perPixel;
        }
        Request();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        e.Pointer.Capture(null);
        Request();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = Drag.None;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Model is null) return;
        _distance = Math.Clamp(_distance * MathF.Pow(0.87f, (float)e.Delta.Y), _fitDistance * 0.12f, _fitDistance * 6f);
        _moved = true;
        Request();
        e.Handled = true;
    }

    /// <summary>Turntable: turn a little every frame while auto-rotate is on and nobody drags.</summary>
    private void Spin()
    {
        if (_spinning || !AutoRotate || TopLevel.GetTopLevel(this) is not { } top) return;
        _spinning = true;
        _lastFrame = null;
        top.RequestAnimationFrame(Tick);

        void Tick(TimeSpan now)
        {
            if (!AutoRotate || TopLevel.GetTopLevel(this) is not { } t)
            {
                _spinning = false;
                Request();                     // settles into a fine frame
                return;
            }
            if (_lastFrame is { } prev && _drag == Drag.None && Model is not null)
            {
                _yaw -= (float)(now - prev).TotalSeconds * 0.6f;
                Request();
            }
            _lastFrame = now;
            t.RequestAnimationFrame(Tick);
        }
    }

    // ------------------------------------------------------------------ rendering

    private void Request()
    {
        if (Model is null)
        {
            lock (_gate) _pending = null;              // nothing of the old model is drawn any more
            Show(null);
            return;
        }
        if (!_framed) FitView();
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double w = Bounds.Width * scaling, h = Bounds.Height * scaling;
        if (w < 2 || h < 2) return;
        var meshes = VisibleList().SelectMany(p => p.Meshes).ToList();
        var job = new Job(meshes, Camera, Textured, Tint, w, h);
        lock (_gate)
        {
            _pending = job;
            Monitor.Pulse(_gate);
            if (_working) return;
            _working = true;
        }
        Task.Run(Work);
    }

    private void Work()
    {
        Job? settled = null;                           // the last quick frame, to refine
        while (true)
        {
            Job job;
            bool fine = false;
            lock (_gate)
            {
                if (_pending is null && settled is not null)
                    Monitor.Wait(_gate, SettleMs);
                if (_pending is not null)
                {
                    job = _pending;
                    _pending = null;
                }
                else if (settled is not null)
                {
                    job = settled;
                    fine = true;
                }
                else
                {
                    _working = false;
                    return;
                }
            }
            settled = fine ? null : job;

            double factor = fine ? Math.Min(2, Math.Sqrt(MaxFinePixels / (job.Width * job.Height))) : _draftScale;
            factor = Math.Max(factor, 0.5);
            int w = Math.Max(1, (int)Math.Round(job.Width * factor)), h = Math.Max(1, (int)Math.Round(job.Height * factor));
            SKImage? image = null;
            var sw = Stopwatch.StartNew();
            try
            {
                var px = new uint[w * h];
                _renderer.Render(job.Meshes, job.Camera, job.Textured, px, w, h, job.Tint);
                var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                image = SKImage.FromPixelCopy(info, MemoryMarshal.AsBytes(px.AsSpan()), w * 4);
            }
            catch (Exception ex)
            {
                AppLog.Error("3D preview render failed", ex);
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            if (!fine)
            {
                // keep dragging fluid: shrink the quick frames when they get slow, grow them back after
                if (ms > 30) _draftScale = Math.Max(0.5f, _draftScale * 0.85f);
                else if (ms < 12) _draftScale = Math.Min(1f, _draftScale * 1.15f);
            }
            if (image is not null)
            {
                var frame = new FrameImage(image);
                Dispatcher.UIThread.Post(() =>
                {
                    if (Model is null) frame.Dispose();         // the model went while this frame was drawn
                    else Show(frame);
                });
            }
        }
    }

    private void Show(FrameImage? image)
    {
        // The compositor keeps drawing the last committed frame until it receives a newer one,
        // and quick frames can arrive several times between commits — so a committed image
        // can't be freed after "a few newer ones", only after a while (and FrameImage makes
        // even a late draw of a freed one harmless instead of a crash).
        long now = Stopwatch.GetTimestamp();
        if (_image is { Committed: true }) _retired.Enqueue((_image, now));
        else _image?.Dispose();                        // replaced before any frame took it
        while (_retired.Count > 0 && Stopwatch.GetElapsedTime(_retired.Peek().Ticks, now).TotalMilliseconds > RetireMs)
            _retired.Dequeue().Image.Dispose();
        _image = image;
        InvalidateVisual();
    }

    protected override Frame CreateFrame(Rect bounds, bool dark)
    {
        if (_image is not null) _image.Committed = true;
        return new ViewFrame { Bounds = bounds, Dark = dark, Image = _image };
    }

    /// <summary>A rendered frame shared between the UI thread and the render thread: disposing
    /// waits for a draw in progress, and a draw after disposal does nothing.</summary>
    private sealed class FrameImage(SKImage image) : IDisposable
    {
        private readonly Lock _gate = new();
        private SKImage? _image = image;

        /// <summary>A frame for the render thread took it (UI thread only).</summary>
        public bool Committed { get; set; }

        public void Draw(SKCanvas c, SKRect dest)
        {
            lock (_gate)
            {
                if (_image is null) return;
                using var paint = new SKPaint();
                c.DrawImage(_image, new SKRect(0, 0, _image.Width, _image.Height), dest,
                            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _image?.Dispose();
                _image = null;
            }
        }
    }

    private sealed class ViewFrame : Frame
    {
        public FrameImage? Image { get; init; }

        public override void Draw(SKCanvas c)
        {
            var rect = new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height);
            // a soft studio backdrop: lighter behind the model, falling off to the edges
            var (inner, outer) = Dark
                ? (new SKColor(0x28, 0x30, 0x3C), new SKColor(0x10, 0x14, 0x1A))
                : (new SKColor(0xFF, 0xFF, 0xFF), new SKColor(0xDA, 0xE1, 0xE9));
            using (var bg = new SKPaint())
            {
                bg.Shader = SKShader.CreateRadialGradient(new SKPoint(rect.MidX, rect.MidY * 0.9f),
                    Math.Max(rect.Width, rect.Height) * 0.7f, [inner, outer], SKShaderTileMode.Clamp);
                c.DrawRect(rect, bg);
            }
            Image?.Draw(c, rect);
        }
    }
}
