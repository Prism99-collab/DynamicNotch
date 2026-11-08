using System.Windows;
using System.Windows.Media;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Each theme's living background behind the open notch's cards, on every page (Cyberpunk has its data rain):
///  • Abyss: the deep sea at night: plankton drifting up in two depths, a sky of stars twinkling in waves, and now
///    and then one star flaring.
///  • Liquid glass: glass bubbles rising and swaying, the aurora inside drifting and breathing, soft white glints.
///  • Crystal: frost glitter twinkling, a band of light sweeping across the glass every few seconds, rainbow glints.
///  • Original: barely there: a few motes of light floating up, so the classic look stays clean.
/// Built from a few cached sheets the graphics chip slides and fades (Controls/Ambient): smooth, and nearly free.
/// Only while the notch is open, never in power-saving mode, hidden or off screen.
/// </summary>
public partial class NotchWindow
{
    readonly Dictionary<string, Ambient> _ambients = new();
    bool _ambientWired;

    Ambient? AmbientFor(string key)
    {
        if (_ambients.TryGetValue(key, out var a)) return a;
        a = key switch
        {
            "abyss" => AbyssAmbient(), "liquid" => LiquidAmbient(), "crystal" => CrystalAmbient(), "original" => OriginalAmbient(), _ => null,
        };
        if (a == null) return null;
        a.Visibility = Visibility.Collapsed;
        a.HorizontalAlignment = HorizontalAlignment.Center;
        a.VerticalAlignment = VerticalAlignment.Top;
        System.Windows.Controls.Grid.SetRowSpan(a, 3);
        Expanded.Children.Insert(Math.Min(1, Expanded.Children.Count), a); // behind every card, above the mood glow
        _ambients[key] = a;
        return a;
    }

    /// <summary>The current theme's background runs while the notch is open; every other one rests.</summary>
    void UpdateAmbient()
    {
        if (!_ambientWired) { _ambientWired = true; _power.Changed += UpdateAmbient; }
        string key = ThemeKey;
        bool on = key != "cyberpunk" && _mode == Mode.Expanded && !Eco && !_offScreen && !_hidden && RainDev != "off"; // (=off: for measuring)
        foreach (var (k, a) in _ambients)
            if (k != key || !on) { a.Stop(); a.Visibility = Visibility.Collapsed; }
        if (!on || AmbientFor(key) is not { } mine) return;
        mine.Visibility = Visibility.Visible;
        mine.Start();
    }

    static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    Ambient AbyssAmbient()
    {
        var a = new Ambient(ExpandedSize.Width, ExpandedSize.Height);
        Color teal = C(0x2E, 0xE6, 0xD6), cyan = C(0x7C, 0xF5, 0xFF), ice = C(0xD8, 0xFB, 0xFF);
        // the stars: two skies twinkling out of step, so it shimmers in waves
        a.AddSheet(r => r.Next(5) == 0 ? Ambient.Twinkle(4 + r.NextDouble() * 2, ice) : Ambient.Speck(0.9 + r.NextDouble() * 1.1, r.Next(3) == 0 ? cyan : ice),
            count: 34, speed: 0, opacity: 0.85, pulseLow: 0.15, pulseSeconds: 2.6);
        a.AddSheet(r => r.Next(6) == 0 ? Ambient.Twinkle(3.5 + r.NextDouble() * 2, cyan) : Ambient.Speck(0.8 + r.NextDouble(), ice),
            count: 28, speed: 0, opacity: 0.75, pulseLow: 0.1, pulseSeconds: 3.4, pulseDelay: 1.3);
        // plankton: far (small, slow, dim) and near (bigger, faster, brighter), always rising
        a.AddSheet(r => Ambient.Glow(3 + r.NextDouble() * 2.5, r.Next(3) == 0 ? cyan : teal),
            count: 16, speed: -7, sway: 6, swaySeconds: 7, opacity: 0.6, pulseLow: 0.35, pulseSeconds: 4);
        a.AddSheet(r => Ambient.Glow(5 + r.NextDouble() * 4, r.Next(2) == 0 ? cyan : teal),
            count: 9, speed: -13, sway: 10, swaySeconds: 9, opacity: 0.85, pulseLow: 0.5, pulseSeconds: 3);
        a.AddFlares(r => (Frozen(new RadialGradientBrush(Colors.White, Color.FromArgb(0x90, cyan.R, cyan.G, cyan.B))), 9 + r.NextDouble() * 6), everySeconds: 2.8);
        return a;
    }

    Ambient LiquidAmbient()
    {
        var a = new Ambient(ExpandedSize.Width, ExpandedSize.Height);
        Color mint = C(0x5F, 0xE3, 0xCF), violet = C(0x9B, 0x7B, 0xFF), white = Colors.White;
        // the aurora, alive: big soft lights drifting sideways and breathing
        a.AddSheet(r => Ambient.Glow(110 + r.NextDouble() * 70, r.Next(2) == 0 ? Color.FromArgb(0xFF, mint.R, mint.G, mint.B) : violet),
            count: 3, speed: 0, sway: 34, swaySeconds: 13, opacity: 0.16, pulseLow: 0.06, pulseSeconds: 7);
        // glass bubbles rising, two depths
        a.AddSheet(r => Ambient.Bubble(3 + r.NextDouble() * 3, white), count: 12, speed: -11, sway: 7, swaySeconds: 6, opacity: 0.55);
        a.AddSheet(r => Ambient.Bubble(6 + r.NextDouble() * 5, mint), count: 6, speed: -20, sway: 12, swaySeconds: 8, opacity: 0.7);
        // a few motes of light caught in the glass
        a.AddSheet(r => Ambient.Speck(1 + r.NextDouble(), white), count: 18, speed: 0, opacity: 0.6, pulseLow: 0.15, pulseSeconds: 3);
        a.AddFlares(r => (Frozen(new RadialGradientBrush(Colors.White, Color.FromArgb(0x70, mint.R, mint.G, mint.B))), 8 + r.NextDouble() * 5), everySeconds: 4);
        return a;
    }

    Ambient CrystalAmbient()
    {
        var a = new Ambient(ExpandedSize.Width, ExpandedSize.Height);
        Color ice = C(0xE6, 0xEE, 0xFF), silver = C(0xC8, 0xD4, 0xEA);
        // frost glitter, two layers twinkling out of step, drifting down very slowly
        a.AddSheet(r => r.Next(4) == 0 ? Ambient.Twinkle(4.5 + r.NextDouble() * 2.5, ice) : Ambient.Speck(1.1 + r.NextDouble() * 1.1, ice),
            count: 40, speed: 3, opacity: 1, pulseLow: 0.25, pulseSeconds: 2.2);
        a.AddSheet(r => Ambient.Speck(1 + r.NextDouble() * 0.9, silver), count: 34, speed: 2, opacity: 0.85, pulseLow: 0.15, pulseSeconds: 2.9, pulseDelay: 1);
        // a band of light sweeping across the glass every few seconds, at an angle
        var band = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 0.45));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.5));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 0.55));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        band.Freeze();
        a.AddSweep(band, bandWidth: 90, angle: 22, crossSeconds: 1.6, everySeconds: 7);
        // prism glints: each one a different colour of the rainbow
        Color[] prism = { C(0xFF, 0x7A, 0xA8), C(0xFF, 0xD3, 0x6B), C(0x6B, 0xFF, 0xC2), C(0x6B, 0xC8, 0xFF), C(0xC2, 0x7A, 0xFF) };
        a.AddFlares(r =>
        {
            var c = prism[r.Next(prism.Length)];
            return (Frozen(new RadialGradientBrush(Colors.White, Color.FromArgb(0xB0, c.R, c.G, c.B))), 8 + r.NextDouble() * 5);
        }, everySeconds: 3.5);
        return a;
    }

    Ambient OriginalAmbient()
    {
        var a = new Ambient(ExpandedSize.Width, ExpandedSize.Height);
        // the classic stays clean: a few faint motes of light floating up, and that's all
        a.AddSheet(r => Ambient.Glow(2 + r.NextDouble() * 2, Colors.White), count: 12, speed: -6, sway: 6, swaySeconds: 10, opacity: 0.28, pulseLow: 0.14, pulseSeconds: 5);
        return a;
    }
}
