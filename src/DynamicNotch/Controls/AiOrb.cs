using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DynamicNotch.Controls;

/// <summary>
/// The AI's mark: a four-point spark (and a small one beside it) in a soft glow, with a ring that orbits it. Alive
/// only while it's on screen and asked to be (<see cref="Start"/> / <see cref="Stop"/>); faster while the AI works
/// (<see cref="Busy"/>). The glowing parts are drawn once and kept: only the finished picture turns and breathes.
/// </summary>
public sealed class AiOrb : Grid
{
    readonly RotateTransform _spin = new();
    readonly ScaleTransform _breath = new(1, 1);
    readonly FrameworkElement _ring, _spark;
    bool _running, _busy;

    public AiOrb(double size, Color a, Color b, bool neon)
    {
        Width = Height = size;
        IsHitTestVisible = false;

        // a soft light behind
        var halo = new Ellipse
        {
            Fill = new RadialGradientBrush(Color.FromArgb(0x55, a.R, a.G, a.B), Color.FromArgb(0, a.R, a.G, a.B)),
            Margin = new Thickness(-size * 0.15),
        };
        Children.Add(halo);

        // the orbit: a stroke that fades along its length, so turning it reads as a comet
        var ringBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        ringBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, a.R, a.G, a.B), 0));
        ringBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, b.R, b.G, b.B), 0.5));
        ringBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, b.R, b.G, b.B), 1));
        var ring = new Ellipse { Stroke = ringBrush, StrokeThickness = Math.Max(1, size / 18), Margin = new Thickness(size * 0.04) };
        if (neon) ring.StrokeDashArray = new DoubleCollection { 3, 2 };
        _ring = ring;
        _ring.RenderTransformOrigin = new Point(0.5, 0.5);
        _ring.RenderTransform = _spin;
        Children.Add(_ring);

        // the sparks: a big four-point star and a small one, in a gradient, glowing
        var sparks = new Grid { Width = size, Height = size };
        var fill = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), a, new Point(0.2, 0), new Point(0.8, 1));
        fill.GradientStops.Insert(1, new GradientStop(Color.FromArgb(0xFF, (byte)((a.R + 255) / 2), (byte)((a.G + 255) / 2), (byte)((a.B + 255) / 2)), 0.35));
        sparks.Children.Add(new Path { Data = Star(size * 0.5, size * 0.53, size * 0.29), Fill = fill });
        sparks.Children.Add(new Path { Data = Star(size * 0.75, size * 0.27, size * 0.11), Fill = new SolidColorBrush(b) });
        sparks.Effect = new DropShadowEffect { Color = a, BlurRadius = size / 4, ShadowDepth = 0, Opacity = 0.9, RenderingBias = RenderingBias.Performance };
        sparks.CacheMode = new BitmapCache(2) { SnapsToDevicePixels = false };
        sparks.RenderTransformOrigin = new Point(0.5, 0.5);
        sparks.RenderTransform = _breath;
        _spark = sparks;
        Children.Add(_spark);
    }

    /// <summary>A four-point star with curved sides, centred on (cx, cy).</summary>
    static Geometry Star(double cx, double cy, double r)
    {
        double k = r * 0.16;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx, cy - r), true, true);
            c.QuadraticBezierTo(new Point(cx + k, cy - k), new Point(cx + r, cy), true, true);
            c.QuadraticBezierTo(new Point(cx + k, cy + k), new Point(cx, cy + r), true, true);
            c.QuadraticBezierTo(new Point(cx - k, cy + k), new Point(cx - r, cy), true, true);
            c.QuadraticBezierTo(new Point(cx - k, cy - k), new Point(cx, cy - r), true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>It comes alive: the ring orbits, the spark breathes (30 times a second is plenty for something this slow).</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        Animate();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        _breath.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _breath.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    /// <summary>Working: the ring spins faster and the spark pulses quicker.</summary>
    public void Busy(bool on)
    {
        if (_busy == on) return;
        _busy = on;
        if (on && !_running) { _running = true; }
        if (_running) Animate();
    }

    void Animate()
    {
        var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(_busy ? 1.4 : 7)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(turn, _busy ? 60 : 30);
        _spin.BeginAnimation(RotateTransform.AngleProperty, turn);
        var pulse = new DoubleAnimation(1, _busy ? 1.14 : 1.07, TimeSpan.FromMilliseconds(_busy ? 520 : 1700))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Timeline.SetDesiredFrameRate(pulse, 30);
        _breath.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        _breath.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }
}
