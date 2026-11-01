using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// Cyberpunk theme (the yellow button beside the lock, or Settings): the notch becomes a piece of street-tech HUD.
///  • shape: hard 45° cuts instead of round corners, asymmetric, with a step in its bottom edge; the ears go angular;
///  • a yellow frame along its sides and bottom, cyan circuit traces, hazard stripes, a ruler, a part-number tag;
///  • the palette of the 2077 title card: night #00060E, yellow #FEE801, olive #9A9F17, cyan #54C1E6, teal #39C4B6;
///    the condensed Bahnschrift that ships with Windows for all text, the clock in yellow;
///  • motion: a glitch burst on every change (signal slices, a cyan ghost of the frame, a jolt), a scan line
///    sweeping down when it opens, titles that decode letter by letter.
/// Nothing runs continuously except a slow trace pulse while the notch is open; in a game it's static, and
/// battery mode keeps only the essential motion. Off, everything is exactly as before.
/// </summary>
public partial class NotchWindow
{
    static readonly Color CyYellow = Color.FromRgb(0xFE, 0xE8, 0x01), CyOlive = Color.FromRgb(0x9A, 0x9F, 0x17),
        CyCyan = Color.FromRgb(0x54, 0xC1, 0xE6), CyTeal = Color.FromRgb(0x39, 0xC4, 0xB6), CyNight = Color.FromRgb(0x00, 0x06, 0x0E);
    static readonly FontFamily CyFont = new("Bahnschrift, Segoe UI");
    static readonly Brush CyYellowBrush = Frozen(CyYellow), CyCyanBrush = Frozen(CyCyan);
    const string Scramble = "#$%&@<>/\\|=+*01?!ZX";

    // fonts: every text in the notch, including ones created later (list rows, chips, tips)
    static bool _cyberFonts;
    static bool _cyberHandler;
    sealed class FontBefore { public FontFamily Family = null!; public FontStretch Stretch; }
    static readonly ConditionalWeakTable<TextBlock, FontBefore> _fontBefore = new();

    bool _cyber;
    TranslateTransform? _stageTear;
    static bool _pressHandler;
    Canvas _hud = null!;
    Path _cyFrame = null!, _cyGhost = null!, _cyTrace = null!, _cyTicks = null!;
    Rectangle _cyScan = null!, _cyLines = null!, _cyStripes = null!;
    Rectangle[] _cyBars = null!;
    TextBlock _cyTag = null!;
    readonly TranslateTransform _cyGhostShift = new(), _cyScanShift = new();
    Brush? _pillBgBefore, _gameFpsBefore, _cyberBody;
    Geometry? _earLBefore, _earRBefore;
    FontFamily? _windowFontBefore;
    Color? _dimBefore, _tileBefore;
    readonly Random _cyRand = new();
    readonly Dictionary<TextBlock, DispatcherTimer> _decoding = new();

    void WireCyber()
    {
        _pillBgBefore = Pill.Background;
        _gameFpsBefore = GameFps.Foreground;
        _earLBefore = EarL.Data;
        _earRBefore = EarR.Data;
        _windowFontBefore = FontFamily;
        BuildHud();
        BuildCyberPills();
        WireCyberSkin();
        CyberBtn.Click += (_, _) => NextTheme();
        // a press in the cyber skin: a tiny glitch answers it (light, once per press)
        if (!_pressHandler)
        {
            _pressHandler = true;
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                new RoutedEventHandler((s, _) => { if (_cyber && !Eco && s is DependencyObject d && Window.GetWindow(d) == this) CyberGlitch(strong: false, light: true); }));
        }
        if (!_cyberHandler)
        {
            _cyberHandler = true;
            EventManager.RegisterClassHandler(typeof(TextBlock), LoadedEvent, new RoutedEventHandler((s, _) => { if (s is TextBlock tb) Retype(tb, _cyberFonts); }));
        }
        // the theme from last time (older settings only knew Cyberpunk on / off)
        if (_settings.Cyberpunk && _settings.Theme == "original") _settings.Theme = "cyberpunk";
        if (ThemeKey == "cyberpunk") ApplyCyber(true, animate: false);
        else if (LookFor(ThemeKey) is { } look) ApplyLook(look, animate: false);
        StyleCyberBtn();
        // development: open the control center on start (for screenshots), no input needed
        // DYNAMICNOTCH_OPEN=1 opens it; =p2 opens it on page 2
        if (Environment.GetEnvironmentVariable("DYNAMICNOTCH_OPEN") is { Length: > 0 } open)
            After(3000, () =>
            {
                if (open.StartsWith("prefs")) { PreferencesWindow.ShowFor(this, int.TryParse(open[5..], out int sec) ? sec : 0); return; }
                if (open == "osd")
                {
                    ShowOsd("message", "", null, "New message", "Night City · Discord", null, glyphBrush: Green, seconds: 6, force: true);
                    return;
                }
                Expand(byHotkey: true);
                if (open == "flip") After(1500, () => SetCyber(!_settings.Cyberpunk)); // the switch, live
                if (open == "cycle") for (int i = 1; i <= 10; i++) After(1400 * i, NextTheme); // every theme, twice round
                if (open.StartsWith('p') && int.TryParse(open[1..], out int pg)) GoToPage(pg, instant: true);
            });
    }

    /// <summary>The theme was switched (the tray icon follows it).</summary>
    public event Action<bool>? CyberChanged;

    void ApplyCyber(bool on, bool animate)
    {
        // switching back: one last burst in the old skin, then it's gone
        if (animate && !on) CyberGlitch(strong: true);
        _cyber = on;
        _cyberFonts = on;

        var body = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        body.GradientStops.Add(new GradientStop(Colors.Black, 0));
        body.GradientStops.Add(new GradientStop(CyNight, 0.55));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0x04, 0x13, 0x1D), 1));
        body.Freeze();
        _cyberBody = body;
        Pill.Background = on ? body : _pillBgBefore;
        // angular ears: a 45° flare into the screen's edge
        EarL.Data = on ? Geometry.Parse("M0,0 L10,0 L10,10 Z") : _earLBefore;
        EarR.Data = on ? Geometry.Parse("M10,0 L0,0 L0,10 Z") : _earRBefore;
        Rim.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _hud.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on)
        {
            Pill.Clip = null;
            Stage.Clip = _clip;
            // nothing of the skin keeps ticking once it's off (a forever animation would keep the renderer awake)
            _cyTrace.BeginAnimation(OpacityProperty, null);
            _cyFrame.Effect = null;
            foreach (var t in _decoding.Values) t.Stop();
            _decoding.Clear();
        }

        FontFamily = on ? CyFont : _windowFontBefore;
        FontStretch = on ? FontStretches.SemiCondensed : FontStretches.Normal;
        if (on) { _skinMap = CyMap; _skinPrefix = "Cy"; _skinCuts = true; }
        SkinAll(on); // the content: controls, cards, colours, gauges (NotchWindow.CyberSkin)
        CyberPills(on); // the side pills' plates, the data blip (NotchWindow.CyberPills)
        CyberArt(on); // your faded pictures behind the pages (NotchWindow.CyberArt)
        Recolor("Dim", ref _dimBefore, on ? Color.FromArgb(0xB4, CyCyan.R, CyCyan.G, CyCyan.B) : null);
        Recolor("Tile", ref _tileBefore, on ? Color.FromRgb(0x0A, 0x1A, 0x24) : null);
        ApplyClockColor();
        ApplyPanelColor(); // your panel colour wins over the theme's body
        GameFps.Foreground = on ? Frozen(Color.FromArgb(0xE6, CyYellow.R, CyYellow.G, CyYellow.B)) : _gameFpsBefore;
        GameFpsUnit.Foreground = on ? Frozen(Color.FromArgb(0x8C, CyCyan.R, CyCyan.G, CyCyan.B)) : Frozen(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));

        ApplyRadius();
        ApplyAccent();
        _panelTint = null;
        UpdatePanelTint();
        UpdateRim();
        StyleCyberBtn();
        CyberHudForMode();
        if (animate && on)
        {
            // boot: a hard burst, the scan line, the header decoding itself
            CyberGlitch(strong: true);
            if (_mode == Mode.Expanded) CyberScan();
            CyberDecode(ClockText);
            CyberDecode(PageTitle);
        }
    }

    /// <summary>Changes a shared brush's colour in place (every text using it follows), remembering the original.</summary>
    static void Recolor(string key, ref Color? before, Color? to)
    {
        if (Application.Current.TryFindResource(key) is not SolidColorBrush b || b.IsFrozen) return;
        if (to is { } c) { before ??= b.Color; b.Color = c; }
        else if (before is { } old) { b.Color = old; before = null; }
    }

    // ───────────── fonts ─────────────

    static bool IsIconFont(FontFamily f) =>
        f.Source.Contains("Icons", StringComparison.OrdinalIgnoreCase) || f.Source.Contains("MDL2", StringComparison.OrdinalIgnoreCase)
        || f.Source.Contains("Emoji", StringComparison.OrdinalIgnoreCase) || f.Source.Contains("Symbol", StringComparison.OrdinalIgnoreCase);

    static void Retype(TextBlock tb, bool on)
    {
        if (on)
        {
            if (_fontBefore.TryGetValue(tb, out _)) return;
            // texts that just inherit the window's font follow it; only an explicit font is swapped (icons never)
            if (tb.ReadLocalValue(TextBlock.FontFamilyProperty) == DependencyProperty.UnsetValue || IsIconFont(tb.FontFamily)) return;
            _fontBefore.Add(tb, new FontBefore { Family = tb.FontFamily, Stretch = tb.FontStretch });
            tb.FontFamily = CyFont;
            tb.FontStretch = FontStretches.SemiCondensed;
        }
        else if (_fontBefore.TryGetValue(tb, out var f))
        {
            tb.FontFamily = f.Family;
            tb.FontStretch = f.Stretch;
            _fontBefore.Remove(tb);
        }
    }

    static void RetypeAll(DependencyObject node)
    {
        if (node is TextBlock tb) Retype(tb, _cyberFonts);
        int n = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (int i = 0; i < n; i++) RetypeAll(VisualTreeHelper.GetChild(node, i));
    }

    // ───────────── the HUD layer ─────────────

    void BuildHud()
    {
        _hud = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed, ClipToBounds = false };

        // scanlines: one faint cyan line every 3 px (open panel only)
        var lines = new DrawingGroup();
        lines.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 1, 3))));
        lines.Children.Add(new GeometryDrawing(Frozen(Color.FromArgb(0x10, CyCyan.R, CyCyan.G, CyCyan.B)), null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        var lineBrush = new DrawingBrush(lines) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 1, 3), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        lineBrush.Freeze();
        _cyLines = new Rectangle { Fill = lineBrush };

        // hazard stripes
        var stripes = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(6, 6), MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat };
        stripes.GradientStops.Add(new GradientStop(CyYellow, 0));
        stripes.GradientStops.Add(new GradientStop(CyYellow, 0.5));
        stripes.GradientStops.Add(new GradientStop(Colors.Transparent, 0.5));
        stripes.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        stripes.Freeze();
        _cyStripes = new Rectangle { Width = 34, Height = 4, Fill = stripes, Opacity = 0.85 };

        _cyTrace = new Path { Stroke = CyCyanBrush, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Miter };
        _cyGhost = new Path { Stroke = CyCyanBrush, StrokeThickness = 2, Opacity = 0, RenderTransform = _cyGhostShift };
        _cyFrame = new Path { Stroke = Frozen(Color.FromArgb(0xF0, CyYellow.R, CyYellow.G, CyYellow.B)), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Miter };
        _cyTicks = new Path { Stroke = Frozen(Color.FromArgb(0x90, CyYellow.R, CyYellow.G, CyYellow.B)), StrokeThickness = 1 };
        var ticks = new StreamGeometry();
        using (var ctx = ticks.Open())
            for (int i = 0; i < 12; i++)
            {
                double y = i * 5 + 0.5;
                ctx.BeginFigure(new Point(0, y), false, false);
                ctx.LineTo(new Point(i % 4 == 0 ? 6 : 3, y), true, false);
            }
        ticks.Freeze();
        _cyTicks.Data = ticks;
        _cyTag = new TextBlock
        {
            Text = "DN-2077 // NOTCH.OS", FontFamily = CyFont, FontStretch = FontStretches.Condensed, FontSize = 7.5,
            Foreground = Frozen(Color.FromArgb(0xB0, CyYellow.R, CyYellow.G, CyYellow.B)),
        };

        // the scan line: a bright cyan edge with a fading trail above it
        var scan = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        scan.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        scan.GradientStops.Add(new GradientStop(Color.FromArgb(0x26, CyCyan.R, CyCyan.G, CyCyan.B), 0.8));
        scan.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, CyCyan.R, CyCyan.G, CyCyan.B), 0.95));
        scan.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        scan.Freeze();
        _cyScan = new Rectangle { Height = 24, Fill = scan, Opacity = 0, RenderTransform = _cyScanShift };

        // signal slices for the glitch
        _cyBars = Enumerable.Range(0, 5).Select(i => new Rectangle { Opacity = 0, Fill = i % 2 == 0 ? CyYellowBrush : CyCyanBrush }).ToArray();

        foreach (UIElement e in new UIElement[] { _cyLines, _cyScan, _cyTrace, _cyTicks, _cyStripes, _cyTag, _cyGhost, _cyFrame })
            _hud.Children.Add(e);
        foreach (var b in _cyBars) _hud.Children.Add(b);
        Stage.Children.Add(_hud);
    }

    /// <summary>The cut shape, its frame and its traces for the notch's current size (every frame while it moves).</summary>
    void CyberShape(double w, double h, double r)
    {
        bool tag = _mode == Mode.Game;                    // the FPS tag: small even cuts, nothing more
        double c1 = tag ? 3.5 : Math.Clamp(r * 0.8, 1.5, h * 0.45);   // bottom-left cut
        double c2 = tag ? 3.5 : Math.Clamp(r * 1.35, 2, h * 0.6);     // bottom-right cut, deeper: asymmetric like a HUD panel
        double s = h > 26 ? 3.5 : 0;                      // a step in the bottom edge
        double xs = Math.Round(w * 0.62);

        Pill.Clip = Poly(true, new(0, -1), new(w, -1), new(w, h - s - c2), new(w - c2, h - s), new(xs + s, h - s), new(xs, h), new(c1, h), new(0, h - c1));
        if (Stage.Clip != null) Stage.Clip = null;
        var frame = Poly(false, new(0, 0), new(0, h - c1), new(c1, h), new(xs, h), new(xs + s, h - s), new(w - c2, h - s), new(w, h - s - c2), new(w, 0));
        _cyFrame.Data = frame;
        _cyGhost.Data = frame;

        if (h > 20)
        {
            // circuit traces: one along the bottom left that climbs a step, one up the right side
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                double y = h - 4.5, len = Math.Min(70, w * 0.22), x0 = c1 + 6;
                ctx.BeginFigure(new Point(x0, y), false, false);
                ctx.LineTo(new Point(x0 + len, y), true, false);
                ctx.LineTo(new Point(x0 + len + 5, y - 5), true, false);
                ctx.LineTo(new Point(x0 + len + 5 + len * 0.5, y - 5), true, false);
                double rx = w - 4.5, ry = h - s - c2 - 5;
                ctx.BeginFigure(new Point(rx, ry), false, false);
                ctx.LineTo(new Point(rx, Math.Max(4, ry - Math.Min(40, h * 0.35))), true, false);
            }
            g.Freeze();
            _cyTrace.Data = g;
            _cyTrace.Visibility = Visibility.Visible;
        }
        else _cyTrace.Visibility = Visibility.Collapsed;

        _cyLines.Width = w;
        _cyLines.Height = h;
        _cyScan.Width = w;
        Canvas.SetLeft(_cyTicks, 5);
        Canvas.SetTop(_cyTicks, Math.Max(6, h * 0.22));
        Canvas.SetLeft(_cyStripes, w - c2 - 44);
        Canvas.SetTop(_cyStripes, h - s - 8);
        Canvas.SetLeft(_cyTag, xs + s + 6);
        Canvas.SetTop(_cyTag, h - s - 13);
        foreach (var b in _cyBars) b.MaxWidth = w;
    }

    static Geometry Poly(bool closed, params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], closed, closed);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i], true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>What the HUD shows in each mode: the full dress open, a lean frame otherwise, nothing moving in a game.</summary>
    void CyberHudForMode()
    {
        if (!_cyber) return;
        bool open = _mode == Mode.Expanded;
        var extras = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) CyberTag();
        _cyLines.Visibility = _cyTicks.Visibility = _cyStripes.Visibility = _cyTag.Visibility = extras;
        // the frame in your edge colour (yellow by default); a soft glow of it when open or alerting, never in a game
        var edge = CustomEdge ?? CyYellow;
        if (_cyFrame.Stroke is not SolidColorBrush fs || fs.Color.R != edge.R || fs.Color.G != edge.G || fs.Color.B != edge.B)
            _cyFrame.Stroke = Frozen(Color.FromArgb(0xF0, edge.R, edge.G, edge.B));
        _cyFrame.Effect = _mode is Mode.Expanded or Mode.Osd && !Eco
            ? new System.Windows.Media.Effects.DropShadowEffect { Color = edge, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.55 }
            : null;
        _cyTrace.Opacity = _mode == Mode.Game ? 0 : 1;
        // in a game the pill is just the number: no frame line around it, nothing bright at the edge of your view
        _cyFrame.Opacity = _mode == Mode.Game ? 0 : 1;
        if (_mode == Mode.Game) { Fade(EarL, 0, 120); Fade(EarR, 0, 120); } // no flares beside the tag
        if (open && !Eco)
        {
            // a slow breath: 20 frames a second look the same and cost a fraction of the screen's full refresh rate
            var breath = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(1400))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Timeline.SetDesiredFrameRate(breath, 20);
            _cyTrace.BeginAnimation(OpacityProperty, breath);
        }
        else _cyTrace.BeginAnimation(OpacityProperty, null);
        CyberTint();
    }

    /// <summary>The traces wear the pop-up's colour while it's up, cyan otherwise; the frame stays yellow.</summary>
    void CyberTint()
    {
        if (!_cyber) return;
        var c = _mode == Mode.Osd && _osdAccent is { } a ? CyMap(a) : _mode == Mode.Expanded && CurrentActivity == LiveActivity.Call ? ((SolidColorBrush)CallBrush).Color : CyCyan;
        if (_cyTrace.Stroke is SolidColorBrush b && b.Color == c) return;
        _cyTrace.Stroke = Frozen(c);
    }

    /// <summary>A mode change in the cyber skin: a glitch, plus the scan line when it opens. Never in a game.</summary>
    void CyberMoment(Mode from, Mode to)
    {
        if (!_cyber) return;
        CyberHudForMode();
        CyberSkinSoon();
        if (to == Mode.Game || from == Mode.Game || to == Mode.Slim && from == Mode.Idle) return;
        if (to == Mode.Expanded) { CyberArtShow(); CyberGlitch(strong: false); CyberScan(); CyberDecode(PageTitle); }
        else if (to == Mode.Osd) CyberGlitch(strong: false);
        else if (from == Mode.Expanded || from == Mode.Osd) CyberGlitch(strong: false, light: true);
    }

    // ───────────── motion ─────────────

    /// <summary>
    /// Signal glitch: slices of yellow and cyan flash across it, a cyan ghost of the frame splits off and snaps back,
    /// the whole shape jolts sideways in hard steps and the content flickers. ~200 ms.
    /// </summary>
    void CyberGlitch(bool strong, bool light = false)
    {
        if (Eco && !strong) return;
        double w = Math.Max(1, Pill.ActualWidth), h = Math.Max(1, Pill.ActualHeight);
        int bars = light ? 2 : strong ? _cyBars.Length : 3;
        for (int i = 0; i < _cyBars.Length; i++)
        {
            var b = _cyBars[i];
            if (i >= bars) { b.BeginAnimation(OpacityProperty, null); b.Opacity = 0; continue; }
            b.Width = w * (0.12 + _cyRand.NextDouble() * (strong ? 0.5 : 0.3));
            b.Height = 1 + _cyRand.Next(strong ? 4 : 2);
            Canvas.SetLeft(b, _cyRand.NextDouble() * (w - b.Width));
            Canvas.SetTop(b, _cyRand.NextDouble() * Math.Max(1, h - b.Height));
            int t0 = _cyRand.Next(0, strong ? 140 : 80), on = 35 + _cyRand.Next(50);
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(t0 + on + 90) };
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t0))));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t0 + on))));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(strong ? 0.6 : 0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t0 + on + 40))));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t0 + on + 80))));
            b.BeginAnimation(OpacityProperty, a);
        }
        // the content tears sideways for a few frames, like a bad signal (a pixel or two: felt more than seen)
        _stageTear ??= new TranslateTransform();
        if (Stage.RenderTransform != _stageTear) Stage.RenderTransform = _stageTear;
        if (strong) Steps(_stageTear, TranslateTransform.XProperty, 28, 0, -3, 2.5, -1, 0);
        else if (light) Steps(_stageTear, TranslateTransform.XProperty, 26, 0, 1.2, 0);
        else Steps(_stageTear, TranslateTransform.XProperty, 28, 0, -1.8, 1.2, 0);
        if (light) return;

        // the cyan ghost: chromatic split of the frame
        Steps(_cyGhost, OpacityProperty, 32, 0, 0.9, 0.5, 0.9, 0.3, 0);
        Steps(_cyGhostShift, TranslateTransform.XProperty, 32, 0, 3, -2.5, 2, -1, 0);
        Steps(_cyGhostShift, TranslateTransform.YProperty, 32, 0, 1, 0, -1, 0, 0);
        // the jolt and the flicker
        _shellShake ??= new TranslateTransform();
        if (Shell.RenderTransform != _shellShake) Shell.RenderTransform = _shellShake;
        if (strong) Steps(_shellShake, TranslateTransform.XProperty, 30, -3, 2.5, -1.5, 1, 0);
        else Steps(_shellShake, TranslateTransform.XProperty, 30, -1.5, 1, 0);
        if (strong) Steps(Stage, OpacityProperty, 30, 1, 0.55, 1, 0.8, 1);
    }

    /// <summary>Hard steps (no easing), the way a bad signal moves.</summary>
    static void Steps(IAnimatable target, DependencyProperty p, int stepMs, params double[] values)
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(stepMs * values.Length) };
        for (int i = 0; i < values.Length; i++) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(values[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(stepMs * i))));
        a.FillBehavior = FillBehavior.Stop;
        target.BeginAnimation(p, a);
    }

    /// <summary>A scan line sweeps down the opening panel.</summary>
    void CyberScan()
    {
        if (Eco) return;
        _cyScan.Opacity = 1;
        var move = new DoubleAnimation(-24, ExpandedSize.Height + 4, TimeSpan.FromMilliseconds(620))
            { BeginTime = TimeSpan.FromMilliseconds(90), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        move.Completed += (_, _) => _cyScan.Opacity = 0;
        _cyScanShift.BeginAnimation(TranslateTransform.YProperty, move);
    }

    /// <summary>A text decodes itself: scrambled symbols resolve into the real letters, left to right, in ~270 ms.</summary>
    /// <param name="cursor">a block cursor rides the edge of the decoded text while it resolves (pop-ups)</param>
    /// <param name="holdMs">fully scrambled for a moment before it starts resolving (the line under a title)</param>
    void CyberDecode(TextBlock tb, bool cursor = false, int holdMs = 0)
    {
        if (!_cyber || Eco) return;
        string final = tb.Text;
        if (string.IsNullOrWhiteSpace(final) || final.Length > 48) return;
        if (_decoding.Remove(tb, out var old)) old.Stop();
        const int steps = 9;
        int step = -(holdMs / 30); // negative: still holding
        string written = "";
        var t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        t.Tick += (_, _) =>
        {
            // something else changed the text meanwhile (the clock ticked, a new title): it wins
            if (written.Length > 0 && tb.Text != written) { t.Stop(); _decoding.Remove(tb); return; }
            step++;
            if (step >= steps) { tb.Text = final; t.Stop(); _decoding.Remove(tb); return; }
            int shown = final.Length * Math.Max(0, step) / steps;
            var chars = final.ToCharArray();
            for (int i = shown; i < chars.Length; i++)
                if (!char.IsWhiteSpace(chars[i])) chars[i] = Scramble[_cyRand.Next(Scramble.Length)];
            written = new string(chars);
            if (cursor) written = written[..shown] + "\u2588" + written[Math.Min(written.Length, shown + 1)..];
            tb.Text = written;
        };
        _decoding[tb] = t;
        t.Start();
    }

    /// <summary>The button: solid yellow to switch it on; dark with a yellow edge while it's on.</summary>
    void StyleCyberBtn()
    {
        // the theme button wears the theme it's on; a click moves to the next one
        string key = ThemeKey;
        var sig = key switch { "cyberpunk" => CyYellow, "original" => Colors.White, _ => LookFor(key)?.Signature ?? Colors.White };
        bool dark = key == "cyberpunk";
        CyberBtn.Background = dark ? Frozen(CyNight) : Frozen(sig);
        CyberBtn.BorderBrush = dark ? CyYellowBrush : Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        CyberBtn.Foreground = dark ? CyYellowBrush : Frozen(CyNight);
        CyberBtn.ToolTip = string.Format(Services.Loc.T("Theme: {0} · click for the next"), Services.Loc.T(ThemeNames[key]));
    }

    /// <summary>Slider fills in the cyber skin: olive into yellow.</summary>
    Brush CyberFill()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        g.GradientStops.Add(new GradientStop(CyOlive, 0));
        g.GradientStops.Add(new GradientStop(CyYellow, 1));
        g.Freeze();
        return g;
    }
}
