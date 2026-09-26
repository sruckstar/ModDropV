using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using SkiaSharp;

namespace Mdv.App.Controls;

/// <summary>
/// The build-in-progress animation, drawn with SkiaSharp: a side-view revolver whose
/// cylinder indexes after every shot, with recoil, a muzzle flash and drifting smoke.
/// It fires on its own every couple of seconds and whenever <see cref="FireCount"/>
/// changes (the view model bumps it when the build reaches a new phase).
/// </summary>
public sealed class RevolverView : SkiaControl
{
    public static readonly StyledProperty<bool> RunningProperty =
        AvaloniaProperty.Register<RevolverView, bool>(nameof(Running));

    public static readonly StyledProperty<int> FireCountProperty =
        AvaloniaProperty.Register<RevolverView, int>(nameof(FireCount));

    public bool Running
    {
        get => GetValue(RunningProperty);
        set => SetValue(RunningProperty, value);
    }

    public int FireCount
    {
        get => GetValue(FireCountProperty);
        set => SetValue(FireCountProperty, value);
    }

    private const double AutoFireEvery = 2.2;       // seconds between idle shots
    private const double MinGap = 0.45;             // never fire faster than this

    private readonly Stopwatch _clock = new();
    private double _lastFire = -10;
    private int _shots;
    private bool _frameRequested;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RunningProperty)
        {
            if (Running)
            {
                _clock.Restart();
                _lastFire = -10;
                _shots = 0;
                RequestFrame();
            }
            else
            {
                _clock.Stop();
            }
        }
        else if (change.Property == FireCountProperty && Running)
        {
            Fire();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Running) RequestFrame();
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    private void Fire()
    {
        if (Now - _lastFire < MinGap) return;
        _lastFire = Now;
        _shots++;
    }

    private void RequestFrame()
    {
        if (_frameRequested) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        _frameRequested = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            if (!Running || !IsEffectivelyVisible) return;
            // first shot shortly after the stage appears, then periodically
            if (_shots == 0 ? Now > 0.6 : Now - _lastFire > AutoFireEvery) Fire();
            InvalidateVisual();
            RequestFrame();
        });
    }

    protected override Frame CreateFrame(Rect bounds, bool dark) => new GunFrame
    {
        Bounds = bounds, Dark = dark, Time = Now, SinceFire = Now - _lastFire, Shots = _shots,
    };

    // =====================================================================

    private sealed class GunFrame : Frame
    {
        public double Time { get; init; }
        public double SinceFire { get; init; }
        public int Shots { get; init; }

        private const float ViewW = 340, ViewH = 220;

        private static double EaseOut(double x) => 1 - Math.Pow(1 - Math.Clamp(x, 0, 1), 3);

        public override void Draw(SKCanvas c)
        {
            float scale = (float)Math.Min(Bounds.Width / ViewW, Bounds.Height / ViewH);
            c.Translate((float)(Bounds.Width - ViewW * scale) / 2f, (float)(Bounds.Height - ViewH * scale) / 2f);
            c.Scale(scale);
            c.Translate(10, 10);

            double tau = SinceFire;
            // recoil: a sharp kick that settles back
            double kick = tau < 0 ? 0 : tau < 0.05 ? tau / 0.05 : Math.Exp(-(tau - 0.05) * 9);
            if (tau > 1.5) kick = 0;
            float bob = (float)(Math.Sin(Time * 2.1) * 1.4);

            DrawGlow(c, (float)kick);

            c.Save();
            c.Translate((float)(-7 * kick), bob);
            c.RotateDegrees((float)(-7 * kick), 104, 140);
            DrawGun(c, tau);
            c.Restore();

            // flash + smoke live in world space at the (recoiled) muzzle
            c.Save();
            c.Translate((float)(-7 * kick), bob);
            c.RotateDegrees((float)(-7 * kick), 104, 140);
            DrawSmoke(c, tau);
            DrawFlash(c, tau);
            c.Restore();
        }

        private void DrawGlow(SKCanvas c, float kick)
        {
            var glow = Palette.Accent(Dark);
            byte a = (byte)Math.Clamp(28 + 60 * kick, 0, 255);
            using var shader = SKShader.CreateRadialGradient(new SKPoint(150, 110), 150,
                [glow.WithAlpha(a), glow.WithAlpha(0)], [0, 1], SKShaderTileMode.Clamp);
            using var p = new SKPaint { IsAntialias = true, Shader = shader };
            c.DrawCircle(150, 110, 150, p);
        }

        private SKPaint Steel(float y0, float y1) => new()
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, y0), new SKPoint(0, y1),
                [Palette.SteelTop, Palette.SteelBottom], SKShaderTileMode.Clamp),
        };

        private SKPaint AccentFill(float y0, float y1) => new()
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, y0), new SKPoint(0, y1),
                [Palette.AccentBright(Dark), Palette.AccentDeep(Dark)], SKShaderTileMode.Clamp),
        };

        private SKPaint Outline(float w = 1) => new()
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w, Color = Palette.AccentLine(Dark),
        };

        private void RoundRect(SKCanvas c, float x, float y, float w, float h, float r, SKPaint fill, SKPaint? stroke)
        {
            var rr = new SKRoundRect(new SKRect(x, y, x + w, y + h), r);
            c.DrawRoundRect(rr, fill);
            if (stroke is not null) c.DrawRoundRect(rr, stroke);
        }

        private void DrawGun(SKCanvas c, double tau)
        {
            using var line = Outline();

            // grip
            using (var grip = new SKPath())
            using (var steel = Steel(128, 186))
            {
                grip.MoveTo(104, 128);
                grip.QuadTo(94, 162, 74, 180);
                grip.LineTo(96, 186);
                grip.QuadTo(118, 166, 126, 142);
                grip.Close();
                c.DrawPath(grip, steel);
                c.DrawPath(grip, line);
                // grip checkering
                using var check = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, Color = Palette.AccentLine(Dark).WithAlpha(60) };
                for (int i = 0; i < 4; i++)
                    c.DrawLine(98 - i * 5, 150 + i * 8, 116 - i * 5, 150 + i * 8, check);
            }

            // hammer housing (the hammer pivots in it)
            using (var hh = Steel(76, 92)) RoundRect(c, 80, 76, 20, 16, 4, hh, line);

            // frame body, top strap, barrel
            using (var s1 = Steel(86, 132)) RoundRect(c, 86, 86, 80, 46, 12, s1, line);
            using (var s2 = Steel(78, 94)) RoundRect(c, 120, 78, 70, 16, 6, s2, line);
            using (var s3 = Steel(94, 114)) RoundRect(c, 150, 94, 118, 20, 7, s3, line);
            using (var rib = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(18) })
                c.DrawRect(156, 97, 104, 3, rib);

            // trigger guard + trigger
            using (var guard = new SKPath())
            using (var gp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, StrokeCap = SKStrokeCap.Round })
            using (var gshader = SKShader.CreateLinearGradient(new SKPoint(0, 132), new SKPoint(0, 158),
                       [Palette.AccentBright(Dark), Palette.AccentDeep(Dark)], SKShaderTileMode.Clamp))
            {
                gp.Shader = gshader;
                guard.MoveTo(108, 132);
                guard.QuadTo(114, 158, 138, 156);
                c.DrawPath(guard, gp);
            }
            using (var trig = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, StrokeCap = SKStrokeCap.Round, Color = Palette.Accent(Dark) })
            {
                float pull = tau is >= 0 and < 0.12 ? 3 : 0;
                c.DrawLine(120 - pull * 0.3f, 130, 120 - pull, 142, trig);
            }

            // muzzle band + front sight
            using (var g1 = AccentFill(92, 116)) RoundRect(c, 256, 92, 9, 24, 3, g1, null);
            using (var g2 = AccentFill(70, 80)) RoundRect(c, 176, 70, 8, 10, 2, g2, null);

            // hammer: snaps forward on the shot, then re-cocks
            double cock = tau < 0 ? 1 : tau < 0.04 ? 1 - tau / 0.04 : EaseOut((tau - 0.35) / 0.5);
            c.Save();
            c.RotateDegrees((float)(18 * (1 - cock)), 90, 84);
            using (var hammer = new SKPath())
            using (var hs = Steel(64, 86))
            {
                hammer.MoveTo(86, 84);
                hammer.QuadTo(72, 80, 68, 68);
                hammer.LineTo(76, 64);
                hammer.QuadTo(82, 74, 92, 76);
                hammer.Close();
                c.DrawPath(hammer, hs);
                c.DrawPath(hammer, line);
            }
            c.Restore();

            DrawCylinder(c, tau);
        }

        private void DrawCylinder(SKCanvas c, double tau)
        {
            // indexes 60° right after each shot
            double settled = Math.Max(0, Shots - 1) * 60;
            double step = Shots == 0 ? 0 : 60 * EaseOut((tau - 0.08) / 0.3);
            float angle = (float)(settled + step);

            using var steel = Steel(80, 140);
            using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f };
            using var ringShader = SKShader.CreateLinearGradient(new SKPoint(0, 80), new SKPoint(0, 140),
                [Palette.AccentBright(Dark), Palette.AccentDeep(Dark)], SKShaderTileMode.Clamp);
            ring.Shader = ringShader;
            c.DrawCircle(118, 110, 30, steel);
            c.DrawCircle(118, 110, 30, ring);

            c.Save();
            c.RotateDegrees(angle, 118, 110);
            using var chamber = new SKPaint { IsAntialias = true, Color = Palette.Chamber };
            using var chamberLine = Outline();
            using var brass = new SKPaint { IsAntialias = true, Color = Palette.AccentDeep(Dark).WithAlpha(150) };
            for (int i = 0; i < 6; i++)
            {
                double a = i * Math.PI / 3;
                float x = 118 + (float)(19 * Math.Cos(a)), y = 110 + (float)(19 * Math.Sin(a));
                c.DrawCircle(x, y, 6.4f, chamber);
                c.DrawCircle(x, y, 6.4f, chamberLine);
                if (i != 0) c.DrawCircle(x, y, 3.2f, brass);           // one spent chamber
            }
            c.Restore();

            using var hub = AccentFill(105, 115);
            c.DrawCircle(118, 110, 4.6f, hub);
        }

        private void DrawFlash(SKCanvas c, double tau)
        {
            if (tau < 0 || tau > 0.16) return;
            double p = tau / 0.16;
            float s = (float)(0.7 + 0.6 * p);
            byte alpha = (byte)(255 * (1 - p));
            c.Save();
            c.Translate(268, 104);
            c.Scale(s);
            using var shader = SKShader.CreateRadialGradient(new SKPoint(4, 0), 46,
                [new SKColor(0xff, 0xf7, 0xe0, alpha), Palette.Flash.WithAlpha(alpha), new SKColor(255, 120, 20, 0)],
                [0, 0.35f, 1], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { IsAntialias = true, Shader = shader };
            using var star = new SKPath();
            star.MoveTo(-2, 0);
            star.LineTo(38, -16);
            star.LineTo(24, 0);
            star.LineTo(48, 0);
            star.LineTo(24, 16);
            star.LineTo(38, 32 - 16);
            star.Close();
            c.DrawPath(star, paint);
            c.DrawCircle(4, 0, 15, paint);
            c.Restore();
        }

        private void DrawSmoke(SKCanvas c, double tau)
        {
            if (tau < 0.03 || tau > 1.2) return;
            double p = (tau - 0.03) / 1.17;
            var puffs = new (float X, float Y, float R, float Drift)[]
            {
                (286, 104, 13, 1.0f), (298, 96, 9, 1.4f), (278, 100, 8, 0.7f),
            };
            foreach (var (x, y, r, drift) in puffs)
            {
                byte a = (byte)(90 * (1 - p) * (1 - p));
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = (Dark ? new SKColor(190, 190, 190) : new SKColor(110, 118, 130)).WithAlpha(a),
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3 + (float)p * 4),
                };
                c.DrawCircle(x + (float)(18 * p * drift), y - (float)(26 * p * drift), r * (float)(0.6 + 1.1 * p), paint);
            }
        }
    }
}
