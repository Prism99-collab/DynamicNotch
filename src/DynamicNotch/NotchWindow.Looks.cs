using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The themes beyond Original and Cyberpunk, all made of glass and light:
///  • Liquid glass: tinted glass in the spirit of Apple's Liquid Glass: dark and clear at the top where it meets
///    the screen's edge, more see-through toward the bottom, a slow aurora (teal and violet) inside, a rim that
///    catches light, a soft caustic along its bottom edge, and a glint of light that follows your pointer across
///    it. It moves like a drop of liquid: a little jelly wobble when it changes shape. Its accent and glow adapt
///    to the app you're on.
///  • Crystal: Apple's "clear" glass: more see-through still, silver light on top and a prism edge: the rim
///    splits light into a thin rainbow.
///  • Abyss: the deep sea: midnight blue, a bioluminescent glow rising from the bottom, and now and then a few
///    glowing particles drift up and fade, like plankton.
/// The controls inside become glass too (Glass.xaml), colours follow the theme, and everything from the Colors
/// page still applies on top. Nothing runs continuously: the glint moves only with the pointer near the notch,
/// the wobble and the blooms are short and occasional, and none of it happens in a game or on battery mode.
/// </summary>
public partial class NotchWindow
{
    sealed class Look
    {
        public string Key = "", Name = "";
        public Color Top, Mid, Bottom;          // the body, from the screen's edge down
        public Color RimColor = Colors.White;
        public double RimIdle = 0.3;
        public Color? Accent, Tint, Clock, Fps;
        public bool TintAlways;                  // false: the glow follows the app you're on, when it has colour
        public Color TileC, DimC;
        public Brush LookTile = null!, LookEdge = null!, LookSheen = null!, LookLabel = null!, LookSwitchOff = null!, LookSwitchOn = null!;
        public Func<Color, Color> Map = c => c;
        public bool Glint, Prism, Aurora, Particles, Jelly;
        public Color Signature;                  // the theme button's colour
    }

    static readonly string[] ThemeOrder = { "original", "cyberpunk", "liquid", "crystal", "abyss" };
    static readonly Dictionary<string, string> ThemeNames = new()
    {
        ["original"] = "Original", ["cyberpunk"] = "Cyberpunk", ["liquid"] = "Liquid glass", ["crystal"] = "Crystal", ["abyss"] = "Abyss",
    };

    Look? _look;
    Grid? _lookUnder, _lookOver;
    Border? _aurora, _floor, _sheen, _glint, _prism, _caustic;
    Canvas? _plankton;
    readonly TranslateTransform _glintShift = new();
    readonly ScaleTransform _jelly = new();
    readonly DispatcherTimer _bloomTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(11) };
    readonly List<(DependencyObject Target, DependencyProperty Prop, object? Before)> _lookUndo = new();
    double _glintTarget = double.NaN;

    static Look? LookFor(string key) => key switch
    {
        "liquid" => Liquid(), "crystal" => Crystal(), "abyss" => AbyssLook(), _ => null,
    };

    static Brush F(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static Brush VGrad(params (Color C, double At)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        foreach (var (c, at) in stops) g.GradientStops.Add(new GradientStop(c, at));
        g.Freeze();
        return g;
    }

    /// <summary>Greys (cards, tiles) become panes of frosted glass; overlays (menus, hints) stay solid enough to read.</summary>
    static Color GlassMap(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max - min > 6 || max < 0x10 || max > 0x44) return c;
        if (c.A is >= 0xE0 and < 0xFF) return Color.FromArgb(0xF2, 0x14, 0x19, 0x24);
        return Color.FromArgb((byte)(c.A / 255.0 * (0x12 + (max - 0x10) * 0.75)), 0xFF, 0xFF, 0xFF);
    }

    static Look Liquid() => new()
    {
        Key = "liquid", Name = "Liquid glass",
        // dense enough that what's behind can't be read through it (Windows gives no blur to a window like this)
        // nearly solid: without a blur behind it, anything more see-through lets text show through. The glass is in
        // the light instead: the sheen, the rim, the caustic, the glint, the aurora
        Top = Color.FromArgb(0xFA, 0x08, 0x0B, 0x13), Mid = Color.FromArgb(0xF6, 0x10, 0x17, 0x25), Bottom = Color.FromArgb(0xF2, 0x1A, 0x25, 0x3A),
        RimColor = Colors.White, RimIdle = 0.34,
        Tint = Color.FromRgb(0x5F, 0xE3, 0xCF), Clock = Colors.White, Fps = Colors.White,
        TileC = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), DimC = Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF),
        LookTile = F(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
        LookEdge = VGrad((Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 0.55), (Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 1)),
        LookSheen = VGrad((Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)),
        LookLabel = F(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
        LookSwitchOff = F(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), LookSwitchOn = F(Color.FromRgb(0x34, 0xC7, 0x9A)),
        Map = GlassMap, Glint = true, Aurora = true, Jelly = true,
        Signature = Color.FromRgb(0x7F, 0xE7, 0xD9),
    };

    static Look Crystal() => new()
    {
        Key = "crystal", Name = "Crystal",
        // frosted and smoky: lighter than the others, so it reads as clear glass
        Top = Color.FromArgb(0xF8, 0x05, 0x06, 0x09), Mid = Color.FromArgb(0xF4, 0x17, 0x1B, 0x23), Bottom = Color.FromArgb(0xF0, 0x28, 0x2D, 0x38),
        RimColor = Color.FromRgb(0xE8, 0xF0, 0xFF), RimIdle = 0.22,
        Accent = Color.FromRgb(0xE6, 0xEE, 0xFF), Tint = Color.FromRgb(0xB8, 0xC8, 0xE6), Clock = Colors.White, Fps = Colors.White,
        TileC = Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF), DimC = Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF),
        LookTile = F(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
        LookEdge = VGrad((Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 0.6), (Color.FromArgb(0x50, 0xDD, 0xE8, 0xFF), 1)),
        LookSheen = VGrad((Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)),
        LookLabel = F(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
        LookSwitchOff = F(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)), LookSwitchOn = F(Color.FromRgb(0x9F, 0xB8, 0xE8)),
        Map = GlassMap, Glint = true, Prism = true, Jelly = true,
        Signature = Color.FromRgb(0xE2, 0xE9, 0xF5),
    };

    static readonly Dictionary<int, Color> AbyssPalette = new()
    {
        [0x30D158] = Color.FromRgb(0x2E, 0xE6, 0xD6), [0x34C759] = Color.FromRgb(0x2E, 0xE6, 0xD6),
        [0x0A84FF] = Color.FromRgb(0x46, 0xA8, 0xFF), [0x64D2FF] = Color.FromRgb(0x7C, 0xF5, 0xFF),
        [0xFF9F0A] = Color.FromRgb(0xFF, 0xB8, 0x6B), [0xFFD60A] = Color.FromRgb(0xFF, 0xD8, 0x7A),
        [0xBF5AF2] = Color.FromRgb(0xB9, 0x8C, 0xFF), [0x5E5CE6] = Color.FromRgb(0x7A, 0x8C, 0xFF),
        [0xFF453A] = Color.FromRgb(0xFF, 0x5A, 0x7A),
    };

    static Color AbyssMap(Color c)
    {
        if (AbyssPalette.TryGetValue(c.R << 16 | c.G << 8 | c.B, out var m)) return Color.FromArgb(c.A, m.R, m.G, m.B);
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max - min <= 6 && max >= 0x10 && max <= 0x44)
            return Color.FromArgb(c.A, (byte)(max * 0.14), (byte)(max * 0.46), (byte)(max * 0.9));
        return c;
    }

    static Look AbyssLook() => new()
    {
        Key = "abyss", Name = "Abyss",
        Top = Colors.Black, Mid = Color.FromRgb(0x02, 0x0B, 0x1A), Bottom = Color.FromRgb(0x04, 0x1E, 0x38),
        RimColor = Color.FromRgb(0x2E, 0xE6, 0xD6), RimIdle = 0.26,
        Accent = Color.FromRgb(0x2E, 0xE6, 0xD6), Tint = Color.FromRgb(0x1A, 0xA6, 0xB8), TintAlways = true,
        Clock = Color.FromRgb(0x9C, 0xF8, 0xFF), Fps = Color.FromRgb(0x7C, 0xF5, 0xFF),
        TileC = Color.FromRgb(0x08, 0x1E, 0x33), DimC = Color.FromArgb(0xB0, 0x9C, 0xD8, 0xF0),
        LookTile = F(Color.FromRgb(0x0A, 0x22, 0x3A)),
        LookEdge = VGrad((Color.FromArgb(0x70, 0x7C, 0xF5, 0xFF), 0), (Color.FromArgb(0x14, 0x2E, 0xE6, 0xD6), 0.6), (Color.FromArgb(0x48, 0x2E, 0xE6, 0xD6), 1)),
        LookSheen = VGrad((Color.FromArgb(0x1C, 0x7C, 0xF5, 0xFF), 0), (Color.FromArgb(0x00, 0x7C, 0xF5, 0xFF), 1)),
        LookLabel = F(Color.FromArgb(0xB8, 0x9C, 0xE4, 0xF2)),
        LookSwitchOff = F(Color.FromRgb(0x0C, 0x26, 0x40)), LookSwitchOn = F(Color.FromRgb(0x1F, 0xC8, 0xBA)),
        Map = AbyssMap, Particles = true,
        Signature = Color.FromRgb(0x2E, 0xE6, 0xD6),
    };

    Brush LookBody(Look l, Color? custom)
    {
        if (custom is not { } c) return VGrad((l.Top, 0), (l.Mid, 0.5), (l.Bottom, 1));
        // your panel colour, as the same glass: same see-through profile, your hue
        Color Tinted(Color a, double k) => Color.FromArgb(a.A, (byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        return VGrad((l.Top, 0), (Tinted(l.Mid, 0.55), 0.5), (Tinted(l.Bottom, 1), 1));
    }

    // ───────────── the theme switch (all five) ─────────────

    string ThemeKey => ThemeOrder.Contains(_settings.Theme) ? _settings.Theme : "original";

    /// <summary>Switches the whole notch to a theme: the old one leaves cleanly, the new one arrives.</summary>
    public void SetTheme(string key)
    {
        if (!ThemeOrder.Contains(key)) key = "original";
        if (key == ThemeKey) return;
        string old = ThemeKey;
        if (old == "cyberpunk") ApplyCyber(false, animate: true);
        else if (_look != null) ApplyLook(null, animate: false);
        _settings.Theme = key;
        _settings.Cyberpunk = key == "cyberpunk";
        _settings.Save();
        AppLog.Write("theme: " + key);
        if (key == "cyberpunk") ApplyCyber(true, animate: true);
        else if (LookFor(key) is { } look) ApplyLook(look, animate: true);
        ApplyCustomColors();
        StyleCyberBtn();
        SyncSettingsPage();
        RefreshThemeChip();
        CyberChanged?.Invoke(key == "cyberpunk");
    }

    /// <summary>The theme button and the Settings chip: the next theme in the row.</summary>
    void NextTheme() => SetTheme(ThemeOrder[(Array.IndexOf(ThemeOrder, ThemeKey) + 1) % ThemeOrder.Length]);

    public void SetCyber(bool on) => SetTheme(on ? "cyberpunk" : "original");

    void ApplyLook(Look? look, bool animate)
    {
        BuildLookLayers();
        if (look == null)
        {
            // leave: everything back exactly as it was
            _look = null;
            _bloomTimer.Stop();
            foreach (var (t, p, before) in _lookUndo) { if (before == null) t.ClearValue(p); else t.SetValue(p, before); }
            _lookUndo.Clear();
            _lookUnder!.Visibility = _lookOver!.Visibility = Visibility.Collapsed;
            foreach (var e in _plankton!.Children.OfType<UIElement>()) e.BeginAnimation(OpacityProperty, null);
            _glintShift.BeginAnimation(TranslateTransform.XProperty, null);
            Recolor("Dim", ref _dimBefore, null);
            Recolor("Tile", ref _tileBefore, null);
            SkinAll(false);
            GameFps.Foreground = _gameFpsBefore;
            UpdateRim();
            return;
        }
        _look = look;
        Resources["LookTile"] = look.LookTile;
        Resources["LookEdge"] = look.LookEdge;
        Resources["LookSheen"] = look.LookSheen;
        Resources["LookLabel"] = look.LookLabel;
        Resources["LookSwitchOff"] = look.LookSwitchOff;
        Resources["LookSwitchOn"] = look.LookSwitchOn;

        // the shell: ears, the side pills' bodies and necks share the glass's top colour
        var top = F(look.Top);
        Swap(EarL, Shape.FillProperty, top);
        Swap(EarR, Shape.FillProperty, top);
        foreach (var bubble in new[] { SecondBubble, PrivacyBubble })
        {
            Swap(bubble, Border.BackgroundProperty, LookBody(look, null));
            if (bubble.Child is Grid g)
                foreach (var ear in g.Children.OfType<Path>().Where(p => p.Width <= 12)) Swap(ear, Shape.FillProperty, top);
        }
        Swap(SecondNeck, Border.BackgroundProperty, top);
        Swap(PrivacyNeck, Border.BackgroundProperty, top);
        if (look.Fps is { } fps) Swap(GameFps, TextBlock.ForegroundProperty, F(fps));

        // the light inside
        _lookUnder!.Visibility = _lookOver!.Visibility = Visibility.Visible;
        _aurora!.Visibility = look.Aurora ? Visibility.Visible : Visibility.Collapsed;
        _floor!.Visibility = look.Particles ? Visibility.Visible : Visibility.Collapsed;
        _plankton!.Visibility = look.Particles ? Visibility.Visible : Visibility.Collapsed;
        _prism!.Visibility = look.Prism ? Visibility.Visible : Visibility.Collapsed;
        _glint!.Visibility = look.Glint ? Visibility.Visible : Visibility.Collapsed;
        _sheen!.Visibility = look.Glint ? Visibility.Visible : Visibility.Collapsed;
        _caustic!.Visibility = look.Glint ? Visibility.Visible : Visibility.Collapsed;
        _floor.Background = new RadialGradientBrush
        {
            Center = new Point(0.5, 1.05), GradientOrigin = new Point(0.5, 1.05), RadiusX = 0.7, RadiusY = 0.9,
            GradientStops = { new GradientStop(Color.FromArgb(0x48, 0x2E, 0xE6, 0xD6), 0), new GradientStop(Color.FromArgb(0x10, 0x46, 0xA8, 0xFF), 0.55), new GradientStop(Colors.Transparent, 1) },
        };

        Recolor("Dim", ref _dimBefore, look.DimC);
        Recolor("Tile", ref _tileBefore, look.TileC);
        _skinMap = look.Map;
        _skinPrefix = "Gl";
        _skinCuts = false;
        SkinAll(true);
        ApplyRadius();
        UpdateRim();
        if (look.Particles) _bloomTimer.Start();
        if (animate) { LookArrive(); if (look.Particles) Bloom(); }
    }

    /// <summary>Sets a value for the theme, remembering what was there (undone when the theme leaves).</summary>
    void Swap(DependencyObject target, DependencyProperty prop, object value)
    {
        if (!_lookUndo.Any(u => ReferenceEquals(u.Target, target) && u.Prop == prop))
            _lookUndo.Add((target, prop, target.ReadLocalValue(prop) is var v && v != DependencyProperty.UnsetValue ? v : null));
        target.SetValue(prop, value);
    }

    void BuildLookLayers()
    {
        if (_lookUnder != null) return;
        _lookUnder = new Grid { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _lookOver = new Grid { IsHitTestVisible = false, Visibility = Visibility.Collapsed };

        // aurora: two soft lights inside the glass, teal from the lower left, violet from the right
        var aurora = new Grid();
        aurora.Children.Add(new Border
        {
            Background = new RadialGradientBrush
            {
                Center = new Point(0.12, 1.0), GradientOrigin = new Point(0.12, 1.0), RadiusX = 0.55, RadiusY = 1.1,
                GradientStops = { new GradientStop(Color.FromArgb(0x46, 0x3F, 0xE0, 0xC5), 0), new GradientStop(Colors.Transparent, 1) },
            },
        });
        aurora.Children.Add(new Border
        {
            Background = new RadialGradientBrush
            {
                Center = new Point(0.9, 0.95), GradientOrigin = new Point(0.9, 0.95), RadiusX = 0.5, RadiusY = 1.0,
                GradientStops = { new GradientStop(Color.FromArgb(0x3A, 0x9B, 0x7B, 0xFF), 0), new GradientStop(Colors.Transparent, 1) },
            },
        });
        _aurora = new Border { Child = aurora };
        _floor = new Border();
        _plankton = new Canvas { ClipToBounds = false };
        var rnd = new Random(7);
        for (int i = 0; i < 9; i++)
        {
            double s = 3 + rnd.NextDouble() * 3.5;
            var dot = new Ellipse
            {
                Width = s, Height = s, Opacity = 0, RenderTransform = new TranslateTransform(),
                Fill = new RadialGradientBrush(i % 3 == 0 ? Color.FromRgb(0x7C, 0xF5, 0xFF) : Color.FromRgb(0x2E, 0xE6, 0xD6), Colors.Transparent),
            };
            _plankton.Children.Add(dot);
        }
        // the still light is drawn once and reused (a call's voice bars or the visualizer redraw the notch every
        // frame: the glass must not be redrawn with them); only what moves stays live
        var still = new Grid { CacheMode = new BitmapCache { SnapsToDevicePixels = true } };
        still.Children.Add(_aurora);
        still.Children.Add(_floor);
        _lookUnder.Children.Add(still);
        _lookUnder.Children.Add(_plankton);

        // on top: the sheen on the upper half, a glint following the pointer, the prism rim, the bottom caustic
        _sheen = new Border
        {
            VerticalAlignment = VerticalAlignment.Top, Height = 22,
            Background = VGrad((Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)),
        };
        _glint = new Border
        {
            Width = 150, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _glintShift,
            Background = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.15), GradientOrigin = new Point(0.5, 0.0), RadiusX = 0.5, RadiusY = 0.9,
                GradientStops = { new GradientStop(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF), 0), new GradientStop(Colors.Transparent, 1) },
            },
        };
        var prism = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        foreach (var (c, at) in new[] { ("#B0FF7AA8", 0.0), ("#B0FFD36B", 0.25), ("#B06BFFC2", 0.5), ("#B06BC8FF", 0.75), ("#B0C27AFF", 1.0) })
            prism.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c), at));
        prism.Freeze();
        _prism = new Border { BorderBrush = prism, BorderThickness = new Thickness(1.3, 0, 1.3, 1.3) };
        _caustic = new Border
        {
            Height = 1.4, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(18, 0, 18, 1.5),
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5),
                GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0.5), new GradientStop(Colors.Transparent, 1) },
            },
        };
        var stillOver = new Grid { CacheMode = new BitmapCache { SnapsToDevicePixels = true } };
        stillOver.Children.Add(_sheen);
        stillOver.Children.Add(_caustic);
        stillOver.Children.Add(_prism);
        _lookOver.Children.Add(stillOver);
        _lookOver.Children.Add(_glint);

        Stage.Children.Insert(0, _lookUnder);
        Stage.Children.Add(_lookOver);
        Pill.RenderTransformOrigin = new Point(0.5, 0);
        Pill.RenderTransform = _jelly;
        _bloomTimer.Tick += (_, _) => { _bloomTimer.Interval = TimeSpan.FromSeconds(10 + _cyRand.NextDouble() * 6); Bloom(); };
    }

    /// <summary>The prism rim follows the notch's rounded corners.</summary>
    void LookRadius(double r)
    {
        if (_prism != null) _prism.CornerRadius = new CornerRadius(0, 0, r, r);
    }

    /// <summary>The theme just arrived: the glass fills with light.</summary>
    void LookArrive()
    {
        if (Eco) return;
        Stage.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Jelly(1.2);
    }

    /// <summary>A drop of liquid settling: a short squash and stretch from the top edge.</summary>
    void Jelly(double strength = 1)
    {
        if (_look?.Jelly != true || Eco || _mode == Mode.Game) return;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1 + 0.035 * strength, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new SineEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1 - 0.015 * strength, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        a.FillBehavior = FillBehavior.Stop;
        _jelly.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        var b = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        b.KeyFrames.Add(new EasingDoubleKeyFrame(1 - 0.02 * strength, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new SineEase { EasingMode = EasingMode.EaseOut }));
        b.KeyFrames.Add(new EasingDoubleKeyFrame(1 + 0.008 * strength, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        b.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        b.FillBehavior = FillBehavior.Stop;
        _jelly.BeginAnimation(ScaleTransform.ScaleXProperty, b);
    }

    /// <summary>A mode change in a glass theme: the drop wobbles, the deep sea blooms when it opens.</summary>
    void LookMoment(Mode from, Mode to)
    {
        if (_look == null) return;
        CyberSkinSoon();
        if (to == Mode.Game || from == Mode.Game) return;
        if (to is Mode.Expanded or Mode.Osd || from == Mode.Expanded) Jelly(to == Mode.Expanded ? 1 : 0.6);
        if (_look.Particles && to is Mode.Expanded or Mode.Osd) Bloom();
        if (_look.Aurora && to == Mode.Expanded && !Eco)
            _aurora!.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(700)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>
    /// The glint: a soft spot of light inside the glass that slides toward the pointer while it's near the top of
    /// the screen, like light on a real lens. Only moves when the pointer does; nothing at all otherwise.
    /// </summary>
    void LookGlint(int cursorX, int cursorY)
    {
        if (_look?.Glint != true || _glint == null || Eco || _mode == Mode.Game || _hidden) return;
        if (cursorY - _mon.Top > 360 * _scale) return;
        double w = Pill.ActualWidth;
        if (w <= 0) return;
        double center = _winLeft + _winW / 2.0;
        double off = Math.Clamp((cursorX - center) / _scale, -w / 2, w / 2);
        double target = Math.Round(w / 2 + off * 0.8 - _glint.Width / 2);
        if (!double.IsNaN(_glintTarget) && Math.Abs(target - _glintTarget) < 3) return;
        _glintTarget = target;
        _glintShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>A few glowing particles drift up and fade: the deep sea breathing. Short, and only now and then.</summary>
    void Bloom()
    {
        if (_look?.Particles != true || _plankton == null || Eco || _hidden || _mode is Mode.Game or Mode.Slim) return;
        double w = Pill.ActualWidth, h = Pill.ActualHeight;
        if (w < 40 || h < 12) return;
        foreach (var dot in _plankton.Children.OfType<Ellipse>())
        {
            Canvas.SetLeft(dot, 10 + _cyRand.NextDouble() * (w - 20));
            Canvas.SetTop(dot, h * (0.45 + _cyRand.NextDouble() * 0.5));
            var delay = TimeSpan.FromMilliseconds(_cyRand.Next(0, 700));
            var life = TimeSpan.FromMilliseconds(1700 + _cyRand.Next(0, 900));
            var glow = new DoubleAnimationUsingKeyFrames { BeginTime = delay, Duration = life, FillBehavior = FillBehavior.Stop };
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromPercent(0.3), new SineEase()));
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new SineEase()));
            Timeline.SetDesiredFrameRate(glow, 30);
            dot.BeginAnimation(OpacityProperty, glow);
            var rise = new DoubleAnimation(0, -(6 + _cyRand.NextDouble() * 8), life) { BeginTime = delay, FillBehavior = FillBehavior.Stop };
            Timeline.SetDesiredFrameRate(rise, 30);
            ((TranslateTransform)dot.RenderTransform).BeginAnimation(TranslateTransform.YProperty, rise);
        }
    }

    // ───────────── the theme chip on the Settings page ─────────────

    Button? _themeChip;

    /// <summary>"Theme" on the Settings page: shows the current one; a click moves to the next.</summary>
    void AddThemeCell(UniformGrid grid)
    {
        var cell = new Grid { Margin = new Thickness(4, 0, 10, 0) };
        cell.Children.Add(Localized(new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 90, 0) }, "Theme"));
        _themeChip = new Button { Height = 22, FontSize = 11, Padding = new Thickness(10, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _themeChip.Style = (Style)FindResource("Capsule");
        _themeChip.Click += (_, _) => NextTheme();
        cell.Children.Add(_themeChip);
        grid.Children.Add(cell);
        RefreshThemeChip();
    }

    void RefreshThemeChip()
    {
        if (_themeChip != null) _themeChip.Content = Loc.T(ThemeNames[ThemeKey]) + "  ›";
    }
}
