using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using SkiaSharp;

namespace Mdv.App.Controls;

/// <summary>
/// A control that paints itself directly with SkiaSharp. Subclasses snapshot their state
/// into an immutable <see cref="Frame"/> on the UI thread; the frame is rendered on the
/// render thread through Avalonia's Skia API lease.
/// </summary>
public abstract class SkiaControl : Control
{
    /// <summary>Immutable drawing state handed to the render thread.</summary>
    protected abstract class Frame
    {
        public required Rect Bounds { get; init; }
        public required bool Dark { get; init; }
        public abstract void Draw(SKCanvas canvas);
    }

    protected abstract Frame CreateFrame(Rect bounds, bool dark);

    protected bool IsDarkTheme => ActualThemeVariant != ThemeVariant.Light;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        context.Custom(new DrawOperation(CreateFrame(bounds, IsDarkTheme)));
    }

    protected SkiaControl()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    private sealed class DrawOperation(Frame frame) : ICustomDrawOperation
    {
        public Rect Bounds => frame.Bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) return;
            using var api = lease.Lease();
            var canvas = api.SkCanvas;
            int save = canvas.Save();
            try
            {
                frame.Draw(canvas);
            }
            finally
            {
                canvas.RestoreToCount(save);
            }
        }
    }

    // ---- shared palette (graphite + electric azure) --------------------------

    protected static class Palette
    {
        public static SKColor Accent(bool dark) => dark ? new SKColor(0x4d, 0xa3, 0xff) : new SKColor(0x1f, 0x6f, 0xd6);
        public static SKColor AccentBright(bool dark) => dark ? new SKColor(0x7c, 0xc6, 0xff) : new SKColor(0x35, 0x85, 0xea);
        public static SKColor AccentDeep(bool dark) => dark ? new SKColor(0x2f, 0x78, 0xd8) : new SKColor(0x17, 0x52, 0x9e);
        public static SKColor AccentLine(bool dark) => dark ? new SKColor(0x4d, 0xa3, 0xff, 92) : new SKColor(0x1f, 0x6f, 0xd6, 102);
        public static SKColor OnAccent(bool dark) => dark ? new SKColor(0x06, 0x12, 0x1f) : SKColors.White;
    }
}
