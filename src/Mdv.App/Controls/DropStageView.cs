using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using SkiaSharp;

namespace Mdv.App.Controls;

/// <summary>
/// The work-in-progress animation, drawn with SkiaSharp: the brand drop falls into a still
/// pool, splashes and sends rings across it. A drop falls on its own every couple of
/// seconds and whenever <see cref="PulseCount"/> changes (the view model bumps it when the
/// work reaches a new phase).
/// </summary>
public sealed class DropStageView : SkiaControl
{
    public static readonly StyledProperty<bool> RunningProperty =
        AvaloniaProperty.Register<DropStageView, bool>(nameof(Running));

    public static readonly StyledProperty<int> PulseCountProperty =
        AvaloniaProperty.Register<DropStageView, int>(nameof(PulseCount));

    public bool Running
    {
        get => GetValue(RunningProperty);
        set => SetValue(RunningProperty, value);
    }

    public int PulseCount
    {
        get => GetValue(PulseCountProperty);
        set => SetValue(PulseCountProperty, value);
    }

    private const double AutoEvery = 1.9;          // seconds between idle drops
    private const double MinGap = 0.5;             // never release faster than this
    private const double FallTime = 0.62;          // release → impact
    private const double RingLife = 2.1;           // how long the rings of one impact run

    private readonly Stopwatch _clock = new();
    private readonly List<double> _drops = [];     // release times
    private bool _frameRequested;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RunningProperty)
        {
            if (Running)
            {
                _clock.Restart();
                _drops.Clear();
                RequestFrame();
            }
            else
            {
                _clock.Stop();
            }
        }
        else if (change.Property == PulseCountProperty && Running)
        {
            Release();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Running) RequestFrame();
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    private double LastRelease => _drops.Count > 0 ? _drops[^1] : -10;

    private void Release()
    {
        if (Now - LastRelease < MinGap) return;
        _drops.Add(Now);
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
            // the first drop right as the stage appears, then periodically
            if (_drops.Count == 0 ? Now > 0.15 : Now - LastRelease > AutoEvery) Release();
            _drops.RemoveAll(t => Now - t > FallTime + RingLife);
            InvalidateVisual();
            RequestFrame();
        });
    }

    protected override Frame CreateFrame(Rect bounds, bool dark) => new StageFrame
    {
        Bounds = bounds, Dark = dark, Time = Now, Drops = [.. _drops],
    };

    // =====================================================================

    private sealed class StageFrame : Frame
    {
        public double Time { get; init; }
        public double[] Drops { get; init; } = [];

        private const float ViewW = 360, ViewH = 250;
        private const float PoolX = 180, PoolY = 188;        // centre of the pool surface
        private const float TopY = 18;                       // where a drop is released

        private static double EaseIn(double x) => x * x;
        private static double EaseOut(double x) => 1 - Math.Pow(1 - Math.Clamp(x, 0, 1), 3);

        public override void Draw(SKCanvas c)
        {
            float scale = (float)Math.Min(Bounds.Width / ViewW, Bounds.Height / ViewH);
            c.Translate((float)(Bounds.Width - ViewW * scale) / 2f, (float)(Bounds.Height - ViewH * scale) / 2f);
            c.Scale(scale);

            // how hard the latest impact still glows
            double glow = 0;
            foreach (var t in Drops)
            {
                double since = Time - t - FallTime;
                if (since >= 0) glow = Math.Max(glow, Math.Exp(-since * 2.4));
            }
            DrawGlow(c, (float)glow);
            DrawPool(c);
            foreach (var t in Drops)
            {
                double since = Time - t;
                if (since < FallTime) DrawFalling(c, since / FallTime);
                else DrawImpact(c, since - FallTime);
            }
        }

        /// <summary>A soft oval light over the pool that flares with each impact; it fades out well inside the view.</summary>
        private void DrawGlow(SKCanvas c, float kick)
        {
            var accent = Palette.Accent(Dark);
            byte a = (byte)Math.Clamp(30 + 70 * kick, 0, 255);
            const float cx = PoolX, cy = PoolY - 40, r = 170;
            using var shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), r,
                [accent.WithAlpha(a), accent.WithAlpha((byte)(a / 3)), accent.WithAlpha(0)], [0, 0.45f, 1], SKShaderTileMode.Clamp);
            using var p = new SKPaint { IsAntialias = true, Shader = shader };
            c.Save();
            c.Scale(1, 0.62f, cx, cy);                       // 170 x 105: inside the 360 x 250 view
            c.DrawCircle(cx, cy, r, p);
            c.Restore();
        }

        /// <summary>The still surface: a flat ellipse with a soft rim.</summary>
        private void DrawPool(SKCanvas c)
        {
            var rect = new SKRect(PoolX - 150, PoolY - 22, PoolX + 150, PoolY + 22);
            using var fill = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(new SKPoint(PoolX, PoolY), 150,
                    [Palette.Accent(Dark).WithAlpha(Dark ? (byte)46 : (byte)38), Palette.Accent(Dark).WithAlpha(0)],
                    [0, 1], SKShaderTileMode.Clamp),
            };
            c.Save();
            c.Scale(1, 22f / 150f, PoolX, PoolY);
            c.DrawCircle(PoolX, PoolY, 150, fill);
            c.Restore();
            using var rim = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1,
                Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, 0), new SKPoint(rect.Right, 0),
                    [Palette.AccentLine(Dark).WithAlpha(0), Palette.AccentLine(Dark), Palette.AccentLine(Dark).WithAlpha(0)],
                    [0, 0.5f, 1], SKShaderTileMode.Clamp),
            };
            c.DrawOval(new SKRect(PoolX - 120, PoolY - 14, PoolX + 120, PoolY + 14), rim);
        }

        /// <summary>The drop on its way down, stretched a little by the speed.</summary>
        private void DrawFalling(SKCanvas c, double k)
        {
            float y = (float)(TopY + (PoolY - 16 - TopY) * EaseIn(k));
            float size = 40;
            float stretch = 1 + 0.18f * (float)k;
            c.Save();
            c.Translate(PoolX, y);
            c.Scale(1 / MathF.Sqrt(stretch), stretch);
            c.Translate(-size / 2, -size / 2);
            DropMark.Paint(c, size, Dark);
            c.Restore();

            // its reflection growing on the surface as it comes close
            byte a = (byte)(90 * EaseIn(k));
            using var shade = new SKPaint { IsAntialias = true, Color = Palette.AccentBright(Dark).WithAlpha(a) };
            c.DrawOval(new SKRect(PoolX - 10, PoolY - 2.5f, PoolX + 10, PoolY + 2.5f), shade);
        }

        /// <summary>After the impact: a splash crown, a bounced droplet and rings spreading out.</summary>
        private void DrawImpact(SKCanvas c, double since)
        {
            // rings
            for (int i = 0; i < 3; i++)
            {
                double t = (since - i * 0.2) / (RingLife - 0.4);
                if (t <= 0 || t >= 1) continue;
                float rx = (float)(14 + 136 * EaseOut(t));
                float ry = rx * 0.15f;
                byte a = (byte)(210 * (1 - t) * (1 - t) * (i == 0 ? 1 : 0.7));
                using var ring = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(2.2 - 1.2 * t),
                    Color = Palette.AccentBright(Dark).WithAlpha(a),
                };
                c.DrawOval(new SKRect(PoolX - rx, PoolY - ry, PoolX + rx, PoolY + ry), ring);
            }

            // splash crown: droplets thrown up and out, falling back
            if (since < 0.7)
            {
                double t = since / 0.7;
                using var dot = new SKPaint { IsAntialias = true, Color = Palette.AccentBright(Dark).WithAlpha((byte)(230 * (1 - t))) };
                for (int i = 0; i < 6; i++)
                {
                    double ang = Math.PI * (0.12 + 0.76 * i / 5.0);            // a fan over the surface
                    double v = 96 + 16 * (i % 2);
                    float x = PoolX + (float)(Math.Cos(ang) * v * 0.85 * t);
                    float y = PoolY - (float)(Math.Sin(ang) * v * t - 150 * t * t);
                    c.DrawCircle(x, Math.Min(y, PoolY), (float)(2.6 * (1 - 0.5 * t)), dot);
                }
            }

            // the bounced droplet: up, then back into the pool
            if (since < 0.9)
            {
                double t = since / 0.9;
                float y = PoolY - (float)(4 * 34 * t * (1 - t));
                using var bead = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateLinearGradient(new SKPoint(PoolX, y - 5), new SKPoint(PoolX, y + 5),
                        [Palette.AccentBright(Dark), Palette.AccentDeep(Dark)], SKShaderTileMode.Clamp),
                };
                c.DrawCircle(PoolX, y, (float)(4.5 * (1 - 0.35 * t)), bead);
            }
        }
    }
}
