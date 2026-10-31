using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DynamicNotch.Controls;

/// <summary>
/// A hue / saturation disc: hue goes round, colour deepens from the white centre to the rim. The disc is one
/// bitmap drawn once for its size (a few thousand pixels, a millisecond), so dragging the marker over it costs
/// nothing but moving one small circle. Drag or click; works with a finger too.
/// </summary>
public sealed class ColorWheel : Grid
{
    readonly Image _disc = new() { IsHitTestVisible = false };
    readonly Ellipse _marker = new()
    {
        Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2.5, IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.7, Color = Colors.Black },
    };
    readonly TranslateTransform _markerAt = new();
    int _drawnPx;

    public double Hue { get; private set; }
    public double Saturation { get; private set; }

    /// <summary>While dragging: hue (0–360), saturation (0–1).</summary>
    public event Action<double, double>? Picked;
    /// <summary>The finger / button was lifted.</summary>
    public event Action? Committed;

    public ColorWheel()
    {
        Background = Brushes.Transparent;
        Cursor = Cursors.Cross;
        _marker.RenderTransform = _markerAt;
        Children.Add(_disc);
        Children.Add(_marker);
    }

    public void SetMarker(double hue, double sat, Color fill)
    {
        Hue = hue;
        Saturation = sat;
        _marker.Fill = new SolidColorBrush(fill);
        PlaceMarker();
    }

    void PlaceMarker()
    {
        double d = Math.Min(ActualWidth, ActualHeight);
        if (d <= 0) return;
        double r = d / 2, a = Hue * Math.PI / 180;
        _markerAt.X = r + Math.Cos(a) * Saturation * (r - 2) - _marker.Width / 2;
        _markerAt.Y = r + Math.Sin(a) * Saturation * (r - 2) - _marker.Height / 2;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Draw();
        PlaceMarker();
    }

    void Draw()
    {
        double d = Math.Min(ActualWidth, ActualHeight);
        if (d <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int px = (int)Math.Round(d * dpi.DpiScaleX);
        if (px == _drawnPx || px <= 0) return;
        _drawnPx = px;
        var pixels = new int[px * px];
        double c = (px - 1) / 2.0, rad = px / 2.0;
        for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
                double dx = x - c, dy = y - c, dist = Math.Sqrt(dx * dx + dy * dy);
                double alpha = Math.Clamp(rad - dist, 0, 1); // a smooth, anti-aliased rim
                if (alpha <= 0) continue;
                double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var k = Hsv(hue, Math.Min(1, dist / rad), 1);
                pixels[y * px + x] = (int)(alpha * 255) << 24 | (int)(k.R * alpha) << 16 | (int)(k.G * alpha) << 8 | (int)(k.B * alpha);
            }
        var bmp = new WriteableBitmap(px, px, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, px, px), pixels, px * 4, 0);
        bmp.Freeze();
        _disc.Source = bmp;
        _disc.Width = _disc.Height = d;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        Committed?.Invoke();
        e.Handled = true;
    }

    void Pick(Point p)
    {
        double d = Math.Min(ActualWidth, ActualHeight), r = d / 2;
        double dx = p.X - r, dy = p.Y - r;
        Hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        Saturation = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / (r - 2));
        PlaceMarker();
        Picked?.Invoke(Hue, Saturation);
    }

    public static Color Hsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double H, double S, double V) ToHsv(Color k)
    {
        double r = k.R / 255.0, g = k.G / 255.0, b = k.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }
}
