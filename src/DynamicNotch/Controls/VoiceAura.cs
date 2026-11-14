using System.Windows;
using System.Windows.Media;

namespace DynamicNotch.Controls;

/// <summary>
/// Rings that ripple out from a circle as you speak: three of them, each born at the circle's edge and fading as it
/// grows, faster and brighter the louder you are; a soft halo that breathes with your voice. Drawn directly (no
/// layout), and only redrawn while there's something to show.
/// </summary>
public sealed class VoiceAura : FrameworkElement
{
    double _level, _phase;
    Color _tint = Colors.White;
    readonly Pen[] _pens = new Pen[3];
    readonly SolidColorBrush[] _ring = { new(), new(), new() };
    readonly RadialGradientBrush _halo = new();

    /// <summary>The circle the rings start from (diameter, centred).</summary>
    public double Core { get; set; } = 40;

    public VoiceAura()
    {
        IsHitTestVisible = false;
        for (int i = 0; i < 3; i++) _pens[i] = new Pen(_ring[i], 1.6);
        _halo.GradientStops.Add(new GradientStop(Colors.Transparent, 0.55));
        _halo.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
    }

    public Color Tint
    {
        get => _tint;
        set { if (_tint == value) return; _tint = value; InvalidateVisual(); }
    }

    /// <summary>Moves the rings on by one frame: <paramref name="level"/> 0–1, <paramref name="dt"/> seconds.</summary>
    public void Step(double level, double dt)
    {
        double before = _level;
        _level = level;
        _phase = (_phase + dt * (0.35 + level * 1.1)) % 1.0;
        if (level > 0.01 || before > 0.01) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_level <= 0.01) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r0 = Core / 2, room = Math.Max(4, Math.Min(ActualWidth, ActualHeight) / 2 - r0 - 1);
        // a halo that breathes with the voice
        _halo.GradientStops[0].Color = Color.FromArgb((byte)(_level * 90), _tint.R, _tint.G, _tint.B);
        double hr = r0 + room * (0.35 + 0.4 * _level);
        dc.DrawEllipse(_halo, null, c, hr, hr);
        // three rings, each a third of a cycle apart
        for (int i = 0; i < 3; i++)
        {
            double p = (_phase + i / 3.0) % 1.0;
            double r = r0 + room * p;
            byte a = (byte)Math.Clamp((1 - p) * _level * 230, 0, 255);
            _ring[i].Color = Color.FromArgb(a, _tint.R, _tint.G, _tint.B);
            _pens[i].Thickness = 1.2 + 1.4 * (1 - p) * _level;
            dc.DrawEllipse(null, _pens[i], c, r, r);
        }
    }
}
