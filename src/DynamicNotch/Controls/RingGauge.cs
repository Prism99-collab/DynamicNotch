using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicNotch.Controls;

/// <summary>Vector ring gauge (Apple-Watch style) whose value glides smoothly between samples.</summary>
public sealed class RingGauge : FrameworkElement
{
    static readonly Pen TrackPen = MakePen(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }

    public const double Thickness = 6;

    public void AnimateTo(double v)
    {
        v = Math.Clamp(v, 0, 1);
        double delta = Math.Abs(v - Value);
        if (delta < 0.004) return; // nothing visible changed: don't redraw at all
        if (delta < 0.03)
        {
            // small drift: jump straight there (one redraw) instead of a 36-frame animation
            BeginAnimation(ValueProperty, null);
            Value = v;
            return;
        }
        var a = new DoubleAnimation(v, TimeSpan.FromMilliseconds(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        // a ring filling slowly looks just as smooth at 30 fps, at half the redraw cost
        Timeline.SetDesiredFrameRate(a, 30);
        BeginAnimation(ValueProperty, a);
    }

    static Pen MakePen(Brush b)
    {
        var p = new Pen(b, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;
        double r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, TrackPen, c, r, r);

        double v = Math.Clamp(Value, 0, 1);
        if (v <= 0.002) return;
        var pen = new Pen(RingBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }

        double a = v * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
