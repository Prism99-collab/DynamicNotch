using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicNotch.Controls;

/// <summary>
/// A small scrolling area graph: each new value slides in from the right on a smooth curve, older ones drift left.
/// Drawn in one pass (no child elements), clipped to rounded corners so it can sit behind a card's content.
/// </summary>
public sealed class LiveGraph : FrameworkElement
{
    readonly List<double> _values = new();
    readonly TranslateTransform _slide = new();
    Pen? _pen;
    Brush? _fill;
    Color _color = Colors.White;

    public int Capacity { get; set; } = 40;
    /// <summary>The top of the scale; when unset, the largest value shown (with a little headroom).</summary>
    public double? Max { get; set; }
    /// <summary>The scale never shrinks below this, so a quiet line stays near the bottom.</summary>
    public double Floor { get; set; } = 1;
    public double FillOpacity { get; set; } = 0.32;
    public double LineOpacity { get; set; } = 0.9;

    public LiveGraph()
    {
        IsHitTestVisible = false;
        RenderTransform = _slide;
    }

    public Color Color
    {
        get => _color;
        set
        {
            if (value == _color && _pen != null) return;
            _color = value;
            var line = new SolidColorBrush(Color.FromArgb((byte)(255 * LineOpacity), value.R, value.G, value.B));
            line.Freeze();
            _pen = new Pen(line, 1.4) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _pen.Freeze();
            var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fill.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * FillOpacity), value.R, value.G, value.B), 0));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, value.R, value.G, value.B), 1));
            fill.Freeze();
            _fill = fill;
            InvalidateVisual();
        }
    }

    public double Peak => _values.Count == 0 ? 0 : _values.Max();

    public void Clear()
    {
        _values.Clear();
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _slide.X = 0;
        InvalidateVisual();
    }

    /// <summary>Adds a value; it glides in over <paramref name="over"/> (the time until the next one).</summary>
    public void Push(double value, TimeSpan over)
    {
        _values.Add(Math.Max(0, value));
        while (_values.Count > Capacity + 1) _values.RemoveAt(0);
        InvalidateVisual();
        if (_values.Count < 2 || ActualWidth <= 0) return;
        double step = ActualWidth / (Capacity - 1);
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(step, 0, over));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || _values.Count < 2 || _pen == null) return;

        double step = w / (Capacity - 1);

        double max = Max ?? _values.Max() * 1.18;
        max = Math.Max(max, Floor);
        double top = 3, usable = h - top - 1;
        int n = _values.Count;
        var pts = new Point[n];
        for (int i = 0; i < n; i++)
        {
            double x = w - (n - 1 - i) * step; // newest on the right edge (the slide brings it in from beyond)
            double y = top + usable * (1 - Math.Min(1, _values[i] / max));
            pts[i] = new Point(x, y);
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            lc.BeginFigure(pts[0], false, false);
            ac.BeginFigure(new Point(pts[0].X, h), true, true);
            ac.LineTo(pts[0], false, false);
            // a soft curve through the midpoints: no sharp corners
            for (int i = 1; i < n; i++)
            {
                var mid = new Point((pts[i - 1].X + pts[i].X) / 2, (pts[i - 1].Y + pts[i].Y) / 2);
                lc.QuadraticBezierTo(pts[i - 1], mid, true, true);
                ac.QuadraticBezierTo(pts[i - 1], mid, false, true);
            }
            lc.LineTo(pts[n - 1], true, true);
            ac.LineTo(pts[n - 1], false, true);
            ac.LineTo(new Point(pts[n - 1].X, h), false, false);
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(_fill, null, area);
        dc.DrawGeometry(null, _pen, line);
    }

    /// <summary>
    /// The graph slides, so it's clipped by its container: this clips <paramref name="host"/> to rounded corners
    /// matching the card it sits in.
    /// </summary>
    public static void ClipRounded(FrameworkElement host, double radius)
    {
        void Apply() => host.Clip = new RectangleGeometry(new Rect(0, 0, host.ActualWidth, host.ActualHeight), radius, radius);
        host.SizeChanged += (_, _) => Apply();
        Apply();
    }
}
