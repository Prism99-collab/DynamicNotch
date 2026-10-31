using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Colour that belongs to the moment:
///  • opening the notch, it takes the colour of the app you're on (a tiny sample of its window, the notch's own
///    pixels left out): a deep glow of it fades in behind the panel, its rim lights up in it, the sliders wear it;
///  • something live overrides it with a colour that means something: call green (red when muted), download blue,
///    timer orange, Focus purple, or the song's cover when the cover has colour;
///  • the quick toggles each have their own colour, dimmer when they're on but not connected.
/// </summary>
public partial class NotchWindow
{
    Color _accent = Colors.White;          // the colour of the app you were on when the notch opened
    bool _hasAmbient;
    Color? _panelTint;                     // what the open panel is tinted with right now
    RadialGradientBrush? _tintBrush;
    int _ambientVersion;

    static readonly Color WifiBlue = Color.FromRgb(0x0A, 0x84, 0xFF);
    static readonly Color BtIndigo = Color.FromRgb(0x5E, 0x5C, 0xE6);
    static readonly Color FocusViolet = Color.FromRgb(0xBF, 0x5A, 0xF2);

    void WireColors()
    {
        // the tint lives in a layer behind the whole panel: one brush whose colours glide
        _tintBrush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0), GradientOrigin = new Point(0.5, 0), RadiusX = 0.9, RadiusY = 1.25,
            GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Transparent, 0.5), new GradientStop(Colors.Transparent, 1) },
        };
        MediaGlow.Background = _tintBrush;
        MediaGlow.Opacity = 1;

        FocusTile.OnBrush = Frozen(FocusViolet);
        BtTile.OnBrush = Frozen(BtIndigo);
    }

    /// <summary>
    /// The notch is opening: read the colour of the app in front (off the UI thread, a few milliseconds),
    /// then let the panel glow in it.
    /// </summary>
    async void SampleAmbient()
    {
        int version = ++_ambientVersion;
        var fg = GetForegroundWindow();
        ScreenColor.Area area;
        if (fg != IntPtr.Zero && fg != _hwnd && GetWindowRect(fg, out var r) && r.Right - r.Left > 200 && r.Bottom - r.Top > 150)
            area = new(Math.Max(r.Left, _mon.Left), Math.Max(r.Top, _mon.Top), Math.Min(r.Right, _mon.Right), Math.Min(r.Bottom, _mon.Bottom));
        else
            area = new(_mon.Left, _mon.Top, _mon.Right, _mon.Bottom); // the desktop itself
        int notchH = (int)Math.Round((ExpandedSize.Height + 24) * _scale), notchHalf = (int)Math.Round((ExpandedSize.Width / 2 + 30) * _scale);
        int cx = _winLeft + _winW / 2;
        var skip = new ScreenColor.Area(cx - notchHalf, _mon.Top, cx + notchHalf, _mon.Top + notchH);

        var c = await Task.Run(() => ScreenColor.Sample(area, skip));
        if (version != _ambientVersion) return;
        _hasAmbient = c != null;
        _accent = c is { } k ? ScreenColor.Glow(k) : Colors.White;
        ApplyAccent();
        _panelTint = null; // repaint even if the colour is the same as last time: it fades in again with the panel
        UpdatePanelTint();
        UpdateRim();
    }

    /// <summary>A gentle gradient for slider fills: a little deeper on the left, the colour itself on the right.</summary>
    Brush AccentFill()
    {
        var c = _accent;
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        g.GradientStops.Add(new GradientStop(Color.FromRgb((byte)(c.R * 0.72), (byte)(c.G * 0.72), (byte)(c.B * 0.72)), 0));
        g.GradientStops.Add(new GradientStop(c, 1));
        g.Freeze();
        return g;
    }

    void ApplyAccent()
    {
        var fill = AccentFill();
        foreach (var s in new[] { VolSlider, BriSlider, ExtBriSlider, AppVolSlider }) s.FillBrush = fill;
        foreach (var row in _mixerRows.Values) row.Slider.FillBrush = fill;
        UpdateDots(animate: false);
    }

    bool ColourfulCover => _media.HasSession && _media.Art != null && _media.Accent != Colors.White;

    /// <summary>What the open panel glows with: a live thing's own colour, else the app you're on.</summary>
    Color? PanelTintColor() => CurrentActivity switch
    {
        LiveActivity.Call => ((SolidColorBrush)CallBrush).Color,
        LiveActivity.Media when ColourfulCover => _media.Accent,
        LiveActivity.Download => WifiBlue,
        LiveActivity.Timer => ((SolidColorBrush)TimerBrush).Color,
        LiveActivity.Focus => FocusViolet,
        _ => _hasAmbient ? _accent : ColourfulCover ? _media.Accent : null,
    };

    /// <summary>The panel's rim while open: the same colour, so the glow has an edge.</summary>
    Color? ExpandedRimColor() => PanelTintColor();

    /// <summary>The deep glow behind the open panel, gliding to the new colour.</summary>
    void UpdatePanelTint()
    {
        if (_tintBrush == null) return;
        var c = PanelTintColor();
        if (c == _panelTint) return;
        bool fresh = _panelTint == null;
        _panelTint = c;
        // music and calls glow from their artwork on the left; the app's colour pours in from the top
        bool fromArt = CurrentActivity is LiveActivity.Call || (CurrentActivity == LiveActivity.Media && ColourfulCover);
        var center = fromArt ? new Point(0.14, 0.32) : new Point(0.5, 0);
        _tintBrush.Center = _tintBrush.GradientOrigin = center;
        var k = c ?? Colors.Black;
        // a colourless app gets a quieter, silvery glow; a colourful one the full colour
        byte[] alpha = c == null ? new byte[] { 0, 0, 0 } : c == ScreenColor.Neutral ? new byte[] { 0x3C, 0x18, 0x08 } : new byte[] { 0x78, 0x34, 0x12 };
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var dur = TimeSpan.FromMilliseconds(fresh ? 800 : 650);
        for (int i = 0; i < 3; i++)
        {
            var stop = _tintBrush.GradientStops[i];
            var to = Color.FromArgb(alpha[i], k.R, k.G, k.B);
            // a fresh glow fades in from nothing in its own colour (not from the last one's)
            var anim = fresh ? new ColorAnimation(Color.FromArgb(0, k.R, k.G, k.B), to, dur) : new ColorAnimation(to, dur);
            anim.EasingFunction = ease;
            stop.BeginAnimation(GradientStop.ColorProperty, anim);
        }
        ProgressSlider.FillBrush = ColourfulCover ? Frozen(_media.Accent) : AccentFill(); // the song's bar keeps the song's colour
        if (_mode == Mode.Expanded) UpdateRim();
    }

    /// <summary>Tiles say more than on / off: Wi-Fi on but not connected, Bluetooth on with nothing connected.</summary>
    void ColorTiles()
    {
        bool wifiConnected = _radio.WifiOn && !string.IsNullOrEmpty(_radio.NetworkName);
        WifiTile.OnBrush = Frozen(wifiConnected ? WifiBlue : Dim(WifiBlue));
        bool btDevice = _radio.BluetoothOn && _bt.Primary != null;
        BtTile.OnBrush = Frozen(btDevice ? BtIndigo : Dim(BtIndigo));
    }

    static Color Dim(Color c) => Color.FromRgb((byte)(c.R * 0.45 + 0x26 * 0.55), (byte)(c.G * 0.45 + 0x26 * 0.55), (byte)(c.B * 0.45 + 0x2A * 0.55));

    /// <summary>The brightness icons glow with the level: dim when the screen is dim.</summary>
    void BrightnessGlyphs()
    {
        BriGlyph.Opacity = 0.5 + 0.5 * Math.Clamp(BriSlider.Value, 0, 1);
        ExtBriGlyph.Opacity = 0.5 + 0.5 * Math.Clamp(ExtBriSlider.Value, 0, 1);
    }
}
