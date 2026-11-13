using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicNotch;

/// <summary>
/// The search dressed for each theme, so it always belongs to the notch around it:
///  • Original: dark glass, your accent colour, lines that ease up into place;
///  • Neon: a night-black HUD with a cyan rim, yellow type, lines that flicker on, a scan and corner brackets;
///  • Prism: tinted glass going from teal to violet, a two-colour rim, a gradient highlight, lines that land like drops;
///  • Crystal: cool frost with a faint rainbow rim, a frosted-pane highlight with a glint, lines that catch the light;
///  • Abyss: the deep sea, a glow rising from below, a bioluminescent edge, lines that rise slowly like bubbles.
/// Everything here is brushes and short animations (no effects, no blur), so every theme stays light while you type.
/// </summary>
public partial class NotchWindow
{
    enum Arrival { Flicker, Ease, Drop, Glint, Rise }

    sealed record LaunchSkin(
        string Key, Brush Panel, Brush Rim, double RimWidth, Color Accent,
        Brush SelFill, Brush? SelEdge, Thickness SelEdgeWidth, double SelRadius,
        Color Lit, Color Label, Color Line, Color Prompt, Color Glyph, Color? Scan, Arrival Arrive, double RowRadius);

    LaunchSkin? _skin;
    string? _skinFor;

    /// <summary>The look for the theme in use (made once per theme and accent).</summary>
    LaunchSkin Skin
    {
        get
        {
            string key = _cyber ? "neon" : ThemeKey;
            string id = key + "|" + _accent;
            if (_skin != null && _skinFor == id) return _skin;
            _skinFor = id;
            return _skin = key switch
            {
                "neon" => new LaunchSkin(key,
                    Frozen(Color.FromArgb(0xFA, 0x03, 0x08, 0x0C)), Frozen(Color.FromArgb(0x70, CyCyan.R, CyCyan.G, CyCyan.B)), 1, CyCyan,
                    Frozen(Color.FromArgb(0x2A, CyCyan.R, CyCyan.G, CyCyan.B)), Frozen(CyYellow), new Thickness(2, 0, 0, 0), 0,
                    CyYellow, Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x90, CyCyan.R, CyCyan.G, CyCyan.B), CyYellow, CyCyan, CyCyan, Arrival.Flicker, 0),

                "liquid" => PrismSkin(),
                "crystal" => CrystalSkin(),
                "abyss" => AbyssSkin(),
                _ => OriginalSkin(),
            };
        }
    }

    LaunchSkin OriginalSkin()
    {
        var a = LaunchAccent;
        return new LaunchSkin("original",
            Frozen(Color.FromArgb(0xFA, 0x10, 0x11, 0x15)), Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), 0, a,
            Frozen(Color.FromArgb(0x26, a.R, a.G, a.B)), null, new Thickness(0), 8,
            Colors.White, Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF), null, Arrival.Ease, 8);
    }

    static LaunchSkin PrismSkin()
    {
        Color teal = Color.FromRgb(0x7F, 0xE7, 0xD9), violet = Color.FromRgb(0xA7, 0x8B, 0xFA);
        var panel = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, 0x0C, 0x1A, 0x1F), 0));
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, 0x10, 0x14, 0x20), 0.55));
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, 0x17, 0x11, 0x26), 1));
        var rim = new LinearGradientBrush(Color.FromArgb(0x90, teal.R, teal.G, teal.B), Color.FromArgb(0x90, violet.R, violet.G, violet.B), 0);
        var sel = new LinearGradientBrush(Color.FromArgb(0x34, teal.R, teal.G, teal.B), Color.FromArgb(0x26, violet.R, violet.G, violet.B), 0);
        return new LaunchSkin("liquid", Frozen(panel), Frozen(rim), 1, teal, Frozen(sel), null, new Thickness(0), 11,
            Color.FromRgb(0xB2, 0xF7, 0xEE), Color.FromArgb(0x99, teal.R, teal.G, teal.B), Color.FromArgb(0x60, teal.R, teal.G, teal.B), teal,
            Color.FromArgb(0xE0, 0xC9, 0xF3, 0xEE), teal, Arrival.Drop, 11);
    }

    static LaunchSkin CrystalSkin()
    {
        Color ice = Color.FromRgb(0xC8, 0xD6, 0xF0);
        var panel = new LinearGradientBrush(Color.FromArgb(0xFC, 0x1C, 0x21, 0x2B), Color.FromArgb(0xFC, 0x11, 0x14, 0x1C), 90);
        // a prism's thin rainbow along the rim
        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        foreach (var (c, o) in new[] { (Color.FromArgb(0x70, 0xFF, 0x8F, 0x9C), 0.0), (Color.FromArgb(0x70, 0xFF, 0xD7, 0x8A), 0.25), (Color.FromArgb(0x70, 0x9C, 0xF5, 0xB8), 0.5),
                                       (Color.FromArgb(0x70, 0x8A, 0xC8, 0xFF), 0.75), (Color.FromArgb(0x70, 0xC4, 0x9C, 0xFF), 1.0) })
            rim.GradientStops.Add(new GradientStop(c, o));
        var sel = new LinearGradientBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 90);
        return new LaunchSkin("crystal", Frozen(panel), Frozen(rim), 1, ice, Frozen(sel), Frozen(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF)), new Thickness(1), 8,
            Color.FromRgb(0xEE, 0xF4, 0xFF), Color.FromArgb(0xB0, 0x8F, 0xA3, 0xC8), Color.FromArgb(0x48, ice.R, ice.G, ice.B), ice,
            Color.FromArgb(0xE6, 0xDC, 0xE6, 0xF8), Colors.White, Arrival.Glint, 8);
    }

    static LaunchSkin AbyssSkin()
    {
        Color teal = Color.FromRgb(0x2E, 0xE6, 0xD6);
        // the deep, lit from below
        var panel = new RadialGradientBrush { Center = new Point(0.5, 1.15), GradientOrigin = new Point(0.5, 1.15), RadiusX = 0.9, RadiusY = 0.9 };
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, 0x06, 0x2A, 0x2E), 0));
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, 0x04, 0x12, 0x18), 0.55));
        panel.GradientStops.Add(new GradientStop(Color.FromArgb(0xFC, 0x02, 0x07, 0x0C), 1));
        var sel = new LinearGradientBrush(Color.FromArgb(0x38, teal.R, teal.G, teal.B), Color.FromArgb(0x06, teal.R, teal.G, teal.B), 0);
        return new LaunchSkin("abyss", Frozen(panel), Frozen(Color.FromArgb(0x48, teal.R, teal.G, teal.B)), 1, teal,
            Frozen(sel), Frozen(Color.FromArgb(0xC0, teal.R, teal.G, teal.B)), new Thickness(2, 0, 0, 0), 6,
            Color.FromRgb(0x6C, 0xF6, 0xEA), Color.FromArgb(0x88, teal.R, teal.G, teal.B), Color.FromArgb(0x58, teal.R, teal.G, teal.B), teal,
            Color.FromArgb(0xD8, 0x9C, 0xF3, 0xEA), null, Arrival.Rise, 6);
    }

    /// <summary>How a new line comes in, in this theme's manner.</summary>
    void ArriveRow(UIElement el, TranslateTransform tt, int order)
    {
        var arrive = Skin.Arrive;
        int step = arrive == Arrival.Rise ? 20 : 14;
        var delay = TimeSpan.FromMilliseconds(Math.Min(order, 8) * step);
        tt.BeginAnimation(TranslateTransform.YProperty, null);
        tt.BeginAnimation(TranslateTransform.XProperty, null);
        tt.X = tt.Y = 0;
        el.Opacity = 0;
        switch (arrive)
        {
            case Arrival.Flicker: // a HUD switching on
                var flicker = new DoubleAnimationUsingKeyFrames { BeginTime = delay, Duration = TimeSpan.FromMilliseconds(150) };
                flicker.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                flicker.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(30))));
                flicker.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(65))));
                flicker.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
                el.BeginAnimation(OpacityProperty, flicker);
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(170)) { BeginTime = delay, EasingFunction = Out });
                break;
            case Arrival.Drop: // a drop of glass settling, with the slightest give
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { BeginTime = delay });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-9, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = delay, EasingFunction = Settle });
                break;
            case Arrival.Glint: // light catching an edge
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { BeginTime = delay });
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(9, 0, TimeSpan.FromMilliseconds(200)) { BeginTime = delay, EasingFunction = Out });
                break;
            case Arrival.Rise: // slow, like something floating up from the deep
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = delay });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(320)) { BeginTime = delay, EasingFunction = Float });
                break;
            default:
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)) { BeginTime = delay });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(170)) { BeginTime = delay, EasingFunction = Out });
                break;
        }
    }

    static readonly IEasingFunction Settle = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
    static readonly IEasingFunction Float = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    /// <summary>The panel, the prompt, the box and the underline in this theme's colours.</summary>
    void StyleLauncher()
    {
        if (_launch == null || _launchBox == null || _launchPrompt == null) return;
        var s = Skin;
        _launch.Background = s.Panel;
        _launch.BorderBrush = s.Rim;
        _launch.BorderThickness = new Thickness(s.RimWidth);
        _launchPrompt.Text = _cyber ? "›" : "";
        _launchPrompt.FontFamily = _cyber ? CyFont : (FontFamily)FindResource("IconFont");
        _launchPrompt.FontSize = _cyber ? 22 : 14;
        _launchPrompt.Foreground = Frozen(s.Prompt);
        _launchBox.FontFamily = _cyber ? CyFont : new FontFamily("Segoe UI Variable Display, Segoe UI");
        _launchBox.Foreground = _cyber ? Frozen(CyYellow) : Brushes.White;
        _launchBox.CaretBrush = Frozen(s.Accent);
        _launchBox.SelectionBrush = Frozen(s.Accent);
        if (_launchLine != null) _launchLine.Background = s.Key == "liquid"
            ? Frozen(new LinearGradientBrush(Color.FromArgb(0x70, 0x7F, 0xE7, 0xD9), Color.FromArgb(0x70, 0xA7, 0x8B, 0xFA), 0))
            : Frozen(s.Line);
        if (_selScan != null && s.Scan is { } scan) _selScan.Fill = Frozen(scan);
        if (_scrollThumb != null) _scrollThumb.Background = Frozen(Color.FromArgb(0x70, s.Accent.R, s.Accent.G, s.Accent.B));
    }
}
