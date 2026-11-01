using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The Colors page: paint the notch yourself.
///  • four things to paint: Accent (sliders, page dots, highlights), Glow (the light behind the open panel),
///    Edge (the rim around the notch, or the cyber frame), Clock (the time in the header);
///  • a hue / saturation wheel and a brightness mixer for the one you picked, previewed live on the notch itself
///    while you drag; "Auto" gives that one back to the notch (the app's colour, the theme's own);
///  • presets that set all four at once, and Shuffle: a random palette built from colour harmony.
/// The page is built the first time it's opened and runs nothing in the background: it costs only while you drag.
/// Live things keep their meaning (a call stays green, a download blue); your colours are the notch's own.
/// The settings page next to it groups everything else into three tabs: Display, Behaviour, Smart.
/// </summary>
public partial class NotchWindow
{
    enum Paint { Accent, Glow, Edge, Clock, Panel, Pills }
    internal static readonly string[] PaintNames = { "Accent", "Glow", "Edge", "Clock", "Panel", "Pills" };
    internal static readonly string[] PaintHints =
    {
        "Sliders, page dots, highlights", "The light behind the open panel", "The rim around the notch", "The time in the header",
        "The notch's own body, kept dark", "The side pills' outline and glow",
    };
    internal const int PaintCount = 6;

    sealed record Preset(string Name, string? A, string? G, string? E, string? C);
    static readonly Preset[] Presets =
    {
        new("Original · the notch decides", null, null, null, null),
        new("Neon", "#FF2E88", "#7B2CFF", "#FF2E88", "#FF8BC4"),
        new("Night City", "#FEE801", "#39C4B6", "#54C1E6", "#FEE801"),
        new("Ocean", "#3FA9F5", "#0B5ED7", "#64D2FF", "#BFE9FF"),
        new("Sunset", "#FF7A45", "#FF2D55", "#FFB86B", "#FFD6A5"),
        new("Matrix", "#00FF66", "#00B140", "#00FF66", "#7DFFAE"),
        new("Sakura", "#FF8FB1", "#C77DFF", "#FFC2D1", "#FFE5EC"),
        new("Aurora", "#4ADE80", "#22D3EE", "#A78BFA", "#CFFAFE"),
    };

    Color _ambientAccent = Colors.White; // the app's colour, when the accent is left to the notch
    bool _studioBuilt;
    Paint _paint;
    double _h, _s, _v = 1;
    ColorWheel _wheel = null!;
    PillSlider _bright = null!;
    Border _orb = null!;
    TextBlock _hex = null!, _paintHint = null!;
    Button _autoChip = null!;
    readonly Border[] _paintSwatch = new Border[PaintCount];
    readonly TextBlock[] _paintLabel = new TextBlock[PaintCount];
    readonly Random _shuffleRand = new();

    static Color? Hex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return (Color)ColorConverter.ConvertFromString(s); } catch { return null; }
    }

    static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    Color? CustomAccent => Hex(_settings.ColorAccent);
    Color? CustomGlow => Hex(_settings.ColorGlow);
    Color? CustomEdge => Hex(_settings.ColorEdge);
    Color? CustomClock => Hex(_settings.ColorClock);

    string? GetPaint(Paint p) => p switch
    {
        Paint.Accent => _settings.ColorAccent, Paint.Glow => _settings.ColorGlow, Paint.Edge => _settings.ColorEdge,
        Paint.Clock => _settings.ColorClock, Paint.Panel => _settings.ColorPanel, _ => _settings.ColorPills,
    };

    void SetPaint(Paint p, string? v)
    {
        switch (p)
        {
            case Paint.Accent: _settings.ColorAccent = v; break;
            case Paint.Glow: _settings.ColorGlow = v; break;
            case Paint.Edge: _settings.ColorEdge = v; break;
            case Paint.Panel: _settings.ColorPanel = v; break;
            case Paint.Pills: _settings.ColorPills = v; break;
            default: _settings.ColorClock = v; break;
        }
    }

    /// <summary>What each one looks like when it's left to the notch.</summary>
    Color DefaultFor(Paint p) => p switch
    {
        Paint.Accent => _cyber ? CyYellow : _look?.Accent ?? _ambientAccent,
        Paint.Glow => _cyber ? CyTeal : _look?.Tint ?? (_hasAmbient ? _ambientAccent : Color.FromRgb(0x8E, 0x8E, 0x93)),
        Paint.Edge => _cyber ? CyYellow : _look?.RimColor ?? Colors.White,
        Paint.Panel => _cyber ? Color.FromRgb(0x04, 0x13, 0x1D) : _look != null ? Color.FromRgb(_look.Bottom.R, _look.Bottom.G, _look.Bottom.B) : Color.FromRgb(0x0B, 0x0B, 0x0E),
        Paint.Pills => _cyber ? CyYellow : Colors.White,
        _ => _cyber ? CyYellow : Colors.White,
    };

    /// <summary>Your colours, everywhere they belong (also at start, and whenever the theme changes).</summary>
    void ApplyCustomColors()
    {
        _accent = CustomAccent ?? _look?.Accent ?? _ambientAccent;
        ApplyAccent();
        _panelTint = null;
        UpdatePanelTint();
        UpdateRim();
        ApplyClockColor();
        ApplyPanelColor();
        _pillColor = CustomPills;
        if (_cyber) CyberHudForMode();
        UpdateDots(animate: false);
        // the side pills repaint with it (at start-up the services behind them may not be ready yet)
        try { StylePrivacyIsland(); UpdateSecond(); } catch { }
    }

    Color? CustomPanel => Hex(_settings.ColorPanel);
    Color? CustomPills => Hex(_settings.ColorPills);
    static Color? _pillColor; // read by the islands' rim (a static helper)

    /// <summary>The notch's body: black where it meets the screen's edge, your colour deepening toward the bottom.</summary>
    void ApplyPanelColor()
    {
        var mine = CustomPanel;
        if (_look != null) { Pill.Background = LookBody(_look, mine); return; } // glass stays glass, in your colour
        if (mine is not { } c)
        {
            Pill.Background = _cyber && _cyberBody != null ? _cyberBody : _pillBgBefore ?? Pill.Background;
            return;
        }
        var g = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        g.GradientStops.Add(new GradientStop(Colors.Black, 0));
        g.GradientStops.Add(new GradientStop(Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45)), 0.55));
        g.GradientStops.Add(new GradientStop(c, 1));
        g.Freeze();
        Pill.Background = g;
    }

    void ApplyClockColor()
    {
        if (CustomClock is { } c) ClockText.Foreground = Frozen(c);
        else if (_cyber) ClockText.Foreground = CyYellowBrush;
        else if (_look?.Clock is { } lc) ClockText.Foreground = Frozen(lc);
        else ClockText.ClearValue(TextBlock.ForegroundProperty);
    }

    // ───────────── the page ─────────────

    void OnColorsShown()
    {
        if (!_studioBuilt) { _studioBuilt = true; BuildStudio(); }
        SelectPaint(_paint);
    }

    void BuildStudio()
    {
        var grid = new Grid { Margin = new Thickness(22, 4, 22, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        // ── the mixer: wheel + brightness ──
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _wheel = new ColorWheel { Width = 150, Height = 150 };
        _wheel.Picked += (h, s) => { _h = h; _s = s; if (_v < 0.2) { _v = 1; _bright.Value = 1; } PaintLive(); };
        _wheel.Committed += () => _settings.Save();
        left.Children.Add(_wheel);
        _bright = new PillSlider { Height = 20, Margin = new Thickness(6, 10, 6, 0), TrackHeight = 7 };
        _bright.ValueChanged += v => { _v = Math.Max(0.04, v); PaintLive(); };
        _bright.ValueCommitted += _ => _settings.Save();
        left.Children.Add(_bright);
        grid.Children.Add(left);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 2);

        // ── what to paint ──
        var targets = new UniformGrid { Columns = PaintCount, Height = 44 };
        for (int i = 0; i < PaintCount; i++)
        {
            var p = (Paint)i;
            var cell = new StackPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            _paintSwatch[i] = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center };
            _paintLabel[i] = new TextBlock { Text = Loc.T(PaintNames[i]), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            cell.Children.Add(_paintSwatch[i]);
            cell.Children.Add(_paintLabel[i]);
            cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; SelectPaint(p); };
            cell.MouseEnter += (_, _) => ShowTip(cell, Loc.T(PaintHints[(int)p]));
            targets.Children.Add(cell);
        }
        right.Children.Add(targets);

        // ── the colour itself: a glowing orb, its code, Auto ──
        var readout = new Grid { Margin = new Thickness(0, 8, 0, 0), Height = 36 };
        readout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        readout.ColumnDefinitions.Add(new ColumnDefinition());
        readout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _orb = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1),
            BorderBrush = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.85 },
        };
        readout.Children.Add(_orb);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        _hex = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
        _paintHint = new TextBlock { FontSize = 10, Foreground = DimBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Children.Add(_hex);
        text.Children.Add(_paintHint);
        text.MouseLeftButtonUp += (_, e) => { e.Handled = true; try { Clipboard.SetText(ToHex(CurrentStudioColor())); } catch { } };
        text.MouseEnter += (_, _) => ShowTip(text, Loc.T("Click to copy the colour code"));
        Grid.SetColumn(text, 1);
        readout.Children.Add(text);
        _autoChip = new Button { Content = Loc.T("Auto"), Height = 24, FontSize = 11, Padding = new Thickness(11, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center };
        _autoChip.Style = (Style)FindResource("Capsule");
        _autoChip.Click += (_, _) => ToggleAuto();
        _autoChip.MouseEnter += (_, _) => ShowTip(_autoChip, Loc.T("Let the notch choose this one (the app's colour, the theme's own)"));
        Grid.SetColumn(_autoChip, 2);
        readout.Children.Add(_autoChip);
        right.Children.Add(readout);

        // ── presets, shuffle, reset ──
        var head = new Grid { Margin = new Thickness(0, 10, 0, 4) };
        head.Children.Add(new TextBlock { Text = Loc.T("PRESETS"), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(SmallAction(Loc.T("Shuffle"), Loc.T("A random palette from colour harmony"), Shuffle, ""));
        actions.Children.Add(SmallAction(Loc.T("Reset"), Loc.T("Back to the notch's own colours"), () => ApplyPreset(Presets[0])));
        head.Children.Add(actions);
        right.Children.Add(head);

        var presets = new UniformGrid { Columns = 4, Rows = 2 };
        foreach (var pr in Presets) presets.Children.Add(PresetTile(pr));
        right.Children.Add(presets);
        grid.Children.Add(right);

        ColorsPage.Children.Add(grid);
    }

    Button SmallAction(string text, string tip, Action act, string? glyph = null)
    {
        object content = text;
        if (glyph != null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = row;
        }
        var b = new Button { Content = content, Height = 20, FontSize = 10, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0) };
        b.Style = (Style)FindResource("Capsule");
        b.Click += (_, _) => act();
        b.MouseEnter += (_, _) => ShowTip(b, tip);
        return b;
    }

    /// <summary>A preset: its colours as three bands; one gradient brush, nothing to lay out.</summary>
    FrameworkElement PresetTile(Preset pr)
    {
        Brush fill;
        if (pr.A == null)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            foreach (var (c, at) in new[] { ("#FFFFFF", 0.0), ("#8E8E93", 0.5), ("#2A2A2E", 1.0) })
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c), at));
            g.Freeze();
            fill = g;
        }
        else
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            var cols = new[] { Hex(pr.A)!.Value, Hex(pr.G)!.Value, Hex(pr.E)!.Value };
            for (int i = 0; i < 3; i++)
            {
                g.GradientStops.Add(new GradientStop(cols[i], i / 3.0));
                g.GradientStops.Add(new GradientStop(cols[i], (i + 1) / 3.0));
            }
            g.Freeze();
            fill = g;
        }
        var tile = new Border
        {
            Height = 22, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(7), Background = fill, Cursor = Cursors.Hand,
            BorderBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(),
        };
        if (pr.A == null)
            tile.Child = new TextBlock { Text = "AUTO", FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        tile.MouseEnter += (_, _) => ShowTip(tile, pr.Name);
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var pop = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(360)) { EasingFunction = new SpringEase { Damping = 0.5 } };
            ((ScaleTransform)tile.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            ((ScaleTransform)tile.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            ApplyPreset(pr);
        };
        return tile;
    }

    void ApplyPreset(Preset pr)
    {
        _settings.ColorAccent = pr.A;
        _settings.ColorGlow = pr.G;
        _settings.ColorEdge = pr.E;
        _settings.ColorClock = pr.C;
        _settings.ColorPills = pr.E;
        if (pr.A == null) _settings.ColorPanel = null;
        _settings.Save();
        ApplyCustomColors();
        if (_cyber) CyberGlitch(strong: false, light: true);
        SelectPaint(_paint);
    }

    /// <summary>A palette from one random hue: accent, a glow beside it on the wheel, an edge a third of the way round, a clock almost white.</summary>
    void Shuffle()
    {
        double h = _shuffleRand.NextDouble() * 360;
        ApplyPreset(new Preset("", ToHex(ColorWheel.Hsv(h, 0.78, 1)), ToHex(ColorWheel.Hsv(h + 28, 0.85, 0.92)),
            ToHex(ColorWheel.Hsv(h + 120, 0.6, 1)), ToHex(ColorWheel.Hsv(h, 0.18, 1))));
    }

    Color CurrentStudioColor() => Hex(GetPaint(_paint)) ?? DefaultFor(_paint);

    void SelectPaint(Paint p)
    {
        _paint = p;
        if (!_studioBuilt) return;
        var (h, s, v) = ColorWheel.ToHsv(CurrentStudioColor());
        if (s > 0.01) _h = h; // a grey keeps the last hue (its own is meaningless)
        _s = s;
        _v = v;
        _bright.Value = v;
        RefreshStudio();
    }

    /// <summary>Dragging on the wheel or the mixer: the notch takes the colour as you go.</summary>
    void PaintLive()
    {
        var c = ColorWheel.Hsv(_h, _s, _paint == Paint.Panel ? Math.Min(_v, 0.38) : _v);
        if (GetPaint(_paint) == ToHex(c)) return;
        SetPaint(_paint, ToHex(c));
        ApplyCustomColorsSoon();
        RefreshStudio();
    }

    bool _colorsQueued;

    /// <summary>
    /// While dragging, the mouse reports far more often than the screen redraws: the notch is repainted once,
    /// after the pending moves are handled, with the latest colour (not once per move).
    /// </summary>
    void ApplyCustomColorsSoon()
    {
        if (_colorsQueued) return;
        _colorsQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _colorsQueued = false;
            ApplyCustomColors();
        });
    }

    void ToggleAuto()
    {
        SetPaint(_paint, GetPaint(_paint) == null ? ToHex(ColorWheel.Hsv(_h, _s, _v)) : null);
        _settings.Save();
        ApplyCustomColors();
        SelectPaint(_paint);
    }

    void RefreshStudio()
    {
        if (!_studioBuilt) return;
        var c = CurrentStudioColor();
        bool auto = GetPaint(_paint) == null;
        _wheel.SetMarker(_h, _s, c);
        var full = ColorWheel.Hsv(_h, _s, 1);
        var mix = new LinearGradientBrush(Colors.Black, full, 0);
        mix.Freeze();
        _bright.FillBrush = mix;
        _orb.Background = Frozen(c);
        if (_orb.Effect is DropShadowEffect fx) fx.Color = c;
        _hex.Text = auto ? $"{Loc.T("Auto")} · {ToHex(c)}" : ToHex(c);
        _paintHint.Text = Loc.T(PaintHints[(int)_paint]);
        _autoChip.Background = auto ? Brushes.White : (Brush)FindResource("Tile");
        _autoChip.Foreground = auto ? Brushes.Black : Brushes.White;
        for (int i = 0; i < PaintCount; i++)
        {
            var p = (Paint)i;
            var k = Hex(GetPaint(p));
            _paintSwatch[i].Background = k is { } kk ? Frozen(kk) : AutoSwatch;
            bool on = p == _paint;
            _paintSwatch[i].BorderBrush = on ? (_cyber ? CyYellowBrush : Brushes.White) : Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            _paintLabel[i].Foreground = on ? Brushes.White : DimBrush;
            _paintLabel[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>"Left to the notch": a little spectrum.</summary>
    static readonly Brush AutoSwatch = MakeAutoSwatch();
    static Brush MakeAutoSwatch()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        string[] cols = { "#FF5E5E", "#FFD60A", "#30D158", "#0A84FF", "#BF5AF2" };
        for (int i = 0; i < cols.Length; i++) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(cols[i]), i / 4.0));
        g.Freeze();
        return g;
    }

    // ───────────── settings: three tabs on one page ─────────────

    int _settingsTab;
    static readonly string[] SettingsTabNames = { "Display", "Behaviour", "Smart" };

    void BuildSettingsTabs()
    {
        for (int i = 0; i < SettingsTabNames.Length; i++)
        {
            int tab = i;
            var b = new Button { Content = Loc.T(SettingsTabNames[i]), Height = 22, FontSize = 11, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(3, 0, 3, 0) };
            b.Style = (Style)FindResource("Capsule");
            b.Click += (_, _) => SelectSettingsTab(tab);
            SettingsTabs.Children.Add(b);
        }
        // everything else (language, colours, pages, shortcuts): the Preferences window
        var more = new Button { Width = 26, Height = 22, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(0),
            Content = new TextBlock { Text = "\uE713", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11 } };
        more.Style = (Style)FindResource("Capsule");
        more.Click += (_, _) => { Collapse(); PreferencesWindow.ShowFor(this); };
        more.MouseEnter += (_, _) => ShowTip(more, Loc.T("Preferences…"));
        SettingsTabs.Children.Add(more);
        SelectSettingsTab(0, animate: false);
    }

    void SelectSettingsTab(int tab, bool animate = true)
    {
        _settingsTab = tab;
        FrameworkElement[] panels = { SettingsGrid, BehaviourGrid, SmartPanel };
        for (int i = 0; i < panels.Length; i++)
        {
            bool on = i == tab;
            panels[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on && animate)
            {
                var shift = panels[i].RenderTransform as TranslateTransform ?? new TranslateTransform();
                panels[i].RenderTransform = shift;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
                panels[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            }
        }
        foreach (var (b, i) in SettingsTabs.Children.OfType<Button>().Take(SettingsTabNames.Length).Select((b, i) => (b, i)))
        {
            bool on = i == tab;
            b.Background = on ? Brushes.White : (Brush)FindResource("Tile");
            b.Foreground = on ? Brushes.Black : Brushes.White;
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (_cyber && animate) CyberGlitch(strong: false, light: true);
    }
}
