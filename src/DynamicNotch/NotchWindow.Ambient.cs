using System.Windows;
using System.Windows.Media;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Each theme's living background behind the open notch's cards, on every page (Neon has its data rain):
///  • Abyss: the deep sea at night: plankton drifting up in two depths, a sky of stars twinkling in waves, and now
///    and then one star flaring.
///  • Prism: glass bubbles rising and swaying, the aurora inside drifting and breathing, soft white glints.
///  • Crystal: frost glitter twinkling, a band of light sweeping across the glass every few seconds, rainbow glints.
///  • Original: barely there: a few motes of light floating up, so the classic look stays clean.
/// Built from a few cached sheets the graphics chip slides and fades (Controls/Ambient): smooth, and nearly free.
///  • Neon: a different one on each page, over its data rain (no pictures): city lights drifting on Home, data
///    packets rising on Work, signal rings on Calls, sparks on System, a crosshair grid with a scan on Tools,
///    fragments on Clipboard &amp; Files, colour motes on Colors, slow cells on Settings. Changing page cross-fades them.
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
            "abyss" => AbyssAmbient(), "liquid" => LiquidAmbient(), "crystal" => CrystalAmbient(), "original" => OriginalAmbient(),
            _ when key.StartsWith("neon") && int.TryParse(key[4..], out int page) => NeonAmbient(page),
            _ => null,
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
    string? _ambientWant;

    void UpdateAmbient()
    {
        if (!_ambientWired) { _ambientWired = true; _power.Changed += UpdateAmbient; }
        string key = ThemeKey;
        if (key == "neon") key = "neon" + _page; // Neon: each page its own
        bool on = _mode == Mode.Expanded && !_launcherOpen && !Eco && !_offScreen && !_hidden && RainDev != "off"; // (=off: for measuring)
        _ambientWant = on ? key : null;
        foreach (var (k, a) in _ambients)
        {
            if (k == _ambientWant || a.Visibility != Visibility.Visible) continue;
            if (!on) { a.BeginAnimation(OpacityProperty, null); a.Stop(); a.Visibility = Visibility.Collapsed; continue; }
            // another page: the old one fades out as the new one fades in
            var gone = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
            string mineKey = k;
            gone.Completed += (_, _) => { if (_ambientWant != mineKey) { a.Stop(); a.Visibility = Visibility.Collapsed; } };
            a.BeginAnimation(OpacityProperty, gone);
        }
        if (!on || AmbientFor(key) is not { } mine) return;
        if (mine.Visibility != Visibility.Visible) { mine.Opacity = 0; mine.Visibility = Visibility.Visible; }
        mine.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(340)));
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

    /// <summary>Neon, one look per page (over the data rain, so a touch quieter than the glass themes' backgrounds).</summary>
    Ambient NeonAmbient(int page)
    {
        var a = new Ambient(ExpandedSize.Width, ExpandedSize.Height);
        Color cyan = CyCyan, yellow = CyYellow, magenta = C(0xFF, 0x2B, 0xD6), teal = C(0x2F, 0xE0, 0xC6), red = C(0xFF, 0x2A, 0x4F), violet = C(0x9D, 0x5C, 0xFF);
        Color[] neon = { cyan, yellow, magenta, teal, violet };
        Brush FlareOf(Color c) => Frozen(new RadialGradientBrush(Colors.White, Color.FromArgb(0xA0, c.R, c.G, c.B)));
        switch (page)
        {
            case 0: // Home: city lights out of focus, drifting by, and tiny lights twinkling far away
                a.AddSheet(r => Ambient.Glow(26 + r.NextDouble() * 34, r.Next(3) switch { 0 => cyan, 1 => magenta, _ => yellow }),
                    count: 7, speed: 0, sway: 30, swaySeconds: 12, opacity: 0.2, pulseLow: 0.08, pulseSeconds: 5);
                a.AddSheet(r => Ambient.Speck(0.9 + r.NextDouble(), r.Next(4) == 0 ? yellow : cyan), count: 26, speed: 0, opacity: 0.7, pulseLow: 0.15, pulseSeconds: 2.4);
                a.AddFlares(r => (FlareOf(magenta), 8 + r.NextDouble() * 5), everySeconds: 3.4);
                break;
            case 7: // Work: data packets rising, two depths
                a.AddSheet(r => Ambient.Bit(1.6 + r.NextDouble() * 1.4, r.Next(3) == 0 ? yellow : cyan), count: 24, speed: -9, sway: 3, swaySeconds: 6, opacity: 0.5, pulseLow: 0.25, pulseSeconds: 2);
                a.AddSheet(r => Ambient.Bit(3.2 + r.NextDouble() * 1.6, r.Next(2) == 0 ? yellow : cyan), count: 8, speed: -17, opacity: 0.6);
                a.AddFlares(r => (FlareOf(yellow), 7 + r.NextDouble() * 4), everySeconds: 4);
                break;
            case 6: // Calls: signal rings rising like a voice going out
                a.AddSheet(r => Ambient.Ring(9 + r.NextDouble() * 18, r.Next(2) == 0 ? magenta : cyan), count: 8, speed: -7, sway: 9, swaySeconds: 7, opacity: 0.32, pulseLow: 0.1, pulseSeconds: 2.2);
                a.AddSheet(r => Ambient.Glow(2.5 + r.NextDouble() * 2, magenta), count: 12, speed: -12, sway: 5, swaySeconds: 5, opacity: 0.55);
                break;
            case 1: // System: sparks streaking up, hotter red and yellow, over a faint red haze
                a.AddSheet(r => Ambient.Glow(60 + r.NextDouble() * 40, red), count: 2, speed: 0, sway: 20, swaySeconds: 10, opacity: 0.12, pulseLow: 0.04, pulseSeconds: 3);
                a.AddSheet(r => Ambient.Streak(1.1, 7 + r.NextDouble() * 9, r.Next(3) == 0 ? red : yellow), count: 18, speed: -42, opacity: 0.5);
                a.AddSheet(r => Ambient.Speck(1 + r.NextDouble(), yellow), count: 14, speed: -20, sway: 4, swaySeconds: 3, opacity: 0.6);
                break;
            case 2: // Tools: a crosshair grid breathing in two waves, a scan crossing it now and then
                a.AddSheet(r => Ambient.Cross(4 + r.NextDouble() * 2, teal), count: 22, speed: 0, opacity: 0.5, pulseLow: 0.1, pulseSeconds: 2.6);
                a.AddSheet(r => Ambient.Cross(3 + r.NextDouble() * 2, cyan), count: 18, speed: 0, opacity: 0.45, pulseLow: 0.08, pulseSeconds: 3.3, pulseDelay: 1.2);
                var band = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                band.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, teal.R, teal.G, teal.B), 0));
                band.GradientStops.Add(new GradientStop(Color.FromArgb(0x2C, teal.R, teal.G, teal.B), 0.5));
                band.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, teal.R, teal.G, teal.B), 1));
                band.Freeze();
                a.AddSweep(band, bandWidth: 46, angle: 0, crossSeconds: 2.2, everySeconds: 6);
                break;
            case 3: // Clipboard & Files: fragments drifting up, turning to cells and bits
                a.AddSheet(r => r.Next(3) == 0 ? Ambient.Hex(6 + r.NextDouble() * 5, cyan) : Ambient.Bit(1.8 + r.NextDouble() * 1.5, teal), count: 18, speed: -8, sway: 10, swaySeconds: 8, opacity: 0.5, pulseLow: 0.25, pulseSeconds: 3);
                a.AddSheet(r => Ambient.Speck(1 + r.NextDouble(), cyan), count: 20, speed: 0, opacity: 0.6, pulseLow: 0.1, pulseSeconds: 2.8);
                a.AddFlares(r => (FlareOf(cyan), 7 + r.NextDouble() * 4), everySeconds: 3.6);
                break;
            case 4: // Colors: motes of every neon colour rising
                a.AddSheet(r => Ambient.Glow(3 + r.NextDouble() * 4, neon[r.Next(neon.Length)]), count: 20, speed: -10, sway: 8, swaySeconds: 7, opacity: 0.75, pulseLow: 0.4, pulseSeconds: 3);
                a.AddSheet(r => Ambient.Glow(30 + r.NextDouble() * 30, neon[r.Next(neon.Length)]), count: 4, speed: 0, sway: 24, swaySeconds: 11, opacity: 0.14, pulseLow: 0.05, pulseSeconds: 6);
                a.AddFlares(r => (FlareOf(neon[r.Next(neon.Length)]), 8 + r.NextDouble() * 5), everySeconds: 2.8);
                break;
            default: // Settings: slow cells hanging in the dark, breathing
                a.AddSheet(r => Ambient.Hex(11 + r.NextDouble() * 12, yellow, 0.9), count: 7, speed: -3, sway: 6, swaySeconds: 12, opacity: 0.26, pulseLow: 0.08, pulseSeconds: 4);
                a.AddSheet(r => Ambient.Speck(1 + r.NextDouble(), yellow), count: 22, speed: 0, opacity: 0.55, pulseLow: 0.1, pulseSeconds: 2.6);
                break;
        }
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
