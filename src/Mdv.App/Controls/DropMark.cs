using Avalonia;
using Avalonia.Input;
using SkiaSharp;

namespace Mdv.App.Controls;

/// <summary>
/// The brand mark: a drop with a "V" cut into it — GTA V, and the arrow of a mod dropping
/// into the game. On hover the V dips once, like something landing.
/// </summary>
public sealed class DropMark : SkiaControl
{
    private const double DipSeconds = 0.45;
    private double _phase = 1;          // 0..1 through a dip; 1 = at rest
    private DateTime _last;

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (_phase < 1) return;
        _phase = 0;
        _last = DateTime.UtcNow;
        Animate();
    }

    private void Animate()
    {
        var top = Avalonia.Controls.TopLevel.GetTopLevel(this);
        top?.RequestAnimationFrame(_ =>
        {
            var now = DateTime.UtcNow;
            _phase = Math.Min(1, _phase + (now - _last).TotalSeconds / DipSeconds);
            _last = now;
            InvalidateVisual();
            if (_phase < 1) Animate();
        });
    }

    protected override Frame CreateFrame(Rect bounds, bool dark) =>
        new MarkFrame { Bounds = bounds, Dark = dark, Dip = (float)Math.Sin(Math.PI * _phase) };

    /// <summary>
    /// Paint the mark into a <paramref name="size"/>-square at the canvas origin. Also used
    /// to render the app icon (tools/Mdv.Brand), so the icon and the header never drift apart.
    /// </summary>
    /// <param name="dip">0..1: how far the V has dipped (hover animation)</param>
    public static void Paint(SKCanvas c, float size, bool dark, float dip = 0)
    {
        int save = c.Save();
        c.Translate(size / 2f, size / 2f);
        c.Scale(size / 32f);

        // a drop: tip at the top, a round belly; the sides are tangent to the belly
        const float tipY = -14.6f, cy = 4.4f, r = 10.9f;
        double tangent = Math.Acos(r / (cy - tipY)) * 180 / Math.PI;     // at the belly centre, from "up"
        float start = (float)(-90 + tangent);
        using var drop = new SKPath();
        drop.MoveTo(0, tipY);
        drop.ArcTo(new SKRect(-r, cy - r, r, cy + r), start, 360 - 2 * (float)tangent, false);
        drop.Close();

        using var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(-8, -12), new SKPoint(8, 15),
                [Palette.AccentBright(dark), Palette.Accent(dark), Palette.AccentDeep(dark)], [0, 0.5f, 1],
                SKShaderTileMode.Clamp),
        };
        c.DrawPath(drop, fill);

        // a soft sheen on the upper left, so the drop reads as a drop and not a pin
        using var sheen = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round,
            Color = SKColors.White.WithAlpha(dark ? (byte)80 : (byte)95),
        };
        using (var arc = new SKPath())
        {
            arc.AddArc(new SKRect(-r + 2.6f, cy - r + 2.6f, r - 2.6f, cy + r - 2.6f), 196, 38);
            c.DrawPath(arc, sheen);
        }

        // the V
        float dy = 2.2f * dip;
        using var v = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.9f,
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = Palette.OnAccent(dark),
        };
        using var chevron = new SKPath();
        chevron.MoveTo(-5.3f, 1.4f + dy);
        chevron.LineTo(0, 7.4f + dy);
        chevron.LineTo(5.3f, 1.4f + dy);
        c.DrawPath(chevron, v);

        c.RestoreToCount(save);
    }

    private sealed class MarkFrame : Frame
    {
        public float Dip { get; init; }

        public override void Draw(SKCanvas c)
        {
            float size = (float)Math.Min(Bounds.Width, Bounds.Height);
            c.Translate(((float)Bounds.Width - size) / 2f, ((float)Bounds.Height - size) / 2f);
            Paint(c, size, Dark, Dip);
        }
    }
}
