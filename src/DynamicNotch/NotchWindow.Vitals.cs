using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The System page: how your PC is doing, in one look.
///  • a line that says it in words ("Running smoothly", "Chrome is using 64 % of the CPU", "Running hot · GPU 91 °C",
///    "Memory almost full") and how long the PC has been on; a download in progress shows there too;
///  • four live tiles, CPU, GPU, memory and network, each in its own colour: a big number, the minute's peak, a level bar;
///  • what's using it: the three apps doing the most for the tile you picked, ending one is two clicks away;
///  • your connection, battery and disk, in a few words.
/// Smooth by design: bars move by their mask on the compositor, numbers count between readings, rows change in place.
/// Everything is sampled only while the page is on screen, off the UI thread.
/// </summary>
public partial class NotchWindow
{
    enum VMetric { Cpu, Gpu, Ram, Net }

    sealed class VTile
    {
        public required VMetric Kind;
        public required Border Plate;
        public required TextBlock Value, Sub, Label;
        public required VPeak Graph;                 // the minute's readings, for the peak
        public GradientStop[] LevelStops = null!;    // the level bar's edge (two stops, moved together)
        public SolidColorBrush LevelFill = null!;
        public double Level = -1;
        public required Button Btn;
        public FrameworkElement Root = null!;       // what sits in the row (the button, or the button and its neon frame)
        public SolidColorBrush? Edge;                // Neon: the frame's colour
        public System.Windows.Media.Effects.DropShadowEffect? Glow; // Neon: the soft light around it
        public double Shown, Target;   // the number on screen, and where it's heading
        public string Unit = "%";
        public Color Hue;
        public TextBlock? Ghost, PeakText;           // Neon: the number's colour-split shadow; the peak's label
        public Rectangle? TopBar;
    }

    /// <summary>The last minute of readings, just for its highest.</summary>
    sealed class VPeak
    {
        readonly double[] _v = new double[60];
        int _n, _head;
        public double PeakRaw { get; private set; }
        public void Push(double x)
        {
            _v[_head] = Math.Max(0, x);
            _head = (_head + 1) % _v.Length;
            if (_n < _v.Length) _n++;
            double m = 0;
            for (int i = 0; i < _n; i++) m = Math.Max(m, _v[i]);
            PeakRaw = m;
        }
    }

    sealed class VRow
    {
        public required Grid Root;
        public required Border Icon, Fill;
        public required Rectangle Sliver;
        public required TextBlock Name, Value, X;
        public required Button End;
        public string App = "";
        public AppLoad.App? Current;
        public double Shown = double.NaN, Target;
        public bool Leaving;
    }

    Grid? _vitals;
    string? _vitalsBuiltFor;
    readonly DispatcherTimer _vitalsTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    readonly AppLoad _appLoad = new();
    StrainService? _vStrain;
    bool _vBusy, _vWired, _vTweening;
    int _vTick;
    VMetric _vMetric = VMetric.Cpu;
    readonly List<VTile> _vTiles = new();
    readonly Dictionary<string, VRow> _vRowMap = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, double> _vSmooth = new(StringComparer.OrdinalIgnoreCase);
    Canvas? _vList;
    Grid? _vListClip;
    const double VRowH = 19;
    UniformGrid? _vTileGrid;
    Shape? _vDot;
    Border? _vTag;                 // Neon: [ OK ] / [ WARN ] / [ CRIT ] in place of the dot
    TextBlock? _vTagText, _vCaret;
    Rectangle? _vScan, _vAppsRule;
    TextBlock? _vHeadline, _vSubline, _vUptime, _vAppsTitle, _vAppsEmpty;
    Border? _vDlTrack, _vDlFill;
    TextBlock? _vNetName, _vNetPing, _vBatText, _vBatSide, _vDiskText, _vNetGlyph, _vBatGlyph;
    StackPanel? _vBars;
    Border? _vDiskFill;
    string? _vEndArmed;
    int _vEndArmId;
    string? _vSay;
    DateTime _vSayUntil;
    Color _vDotColor;
    TimeSpan _vTweenLast;

    // the latest readings
    SystemSnapshot? _vSnap;
    StrainService.Reading? _vRead;
    List<AppLoad.App> _vAppList = new();
    ConnectionDetails? _vConn;
    BatteryDetails? _vBat;

    /// <summary>Makes the page (it takes the System page's place in the strip).</summary>
    Grid BuildVitalsPage()
    {
        _vitals = new Grid { Width = PageWidth, Height = 216 };
        if (!_vWired)
        {
            _vWired = true;
            _vitalsTimer.Tick += async (_, _) => await SampleVitalsAsync();
            _files.DownloadsChanged += () => { if (_page == 1 && _mode == Mode.Expanded) RenderVitalsHeader(); };
        }
        RebuildVitals();
        return _vitals;
    }

    void RebuildVitals()
    {
        if (_vitals == null) return;
        _vitalsBuiltFor = Skin.Key + "|" + _accent + "|" + Eco;
        _vitals.Children.Clear();
        _vTiles.Clear();
        _vRowMap.Clear();
        _vSmooth.Clear();
        _vitals.Children.Add(BuildVitalsFace());
        _vScan = null;
        if (_cyber)
        {
            var lines = new DrawingBrush(new GeometryDrawing(Frozen(Color.FromArgb(0x08, 0x5E, 0xF6, 0xFF)), null, new RectangleGeometry(new Rect(0, 0, 4, 1))))
            { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 4, 3), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 4, 3), ViewboxUnits = BrushMappingMode.Absolute };
            lines.Freeze();
            _vitals.Children.Add(Own(new Rectangle { Fill = lines, IsHitTestVisible = false }));
        }
    }

    /// <summary>The page came into view: fresh look if the theme changed, a first reading right away, then every second.</summary>
    void OnVitalsShown()
    {
        if (_vitalsBuiltFor != Skin.Key + "|" + _accent + "|" + Eco) RebuildVitals();
        var every = TimeSpan.FromSeconds(Eco ? 2 : 1);
        _vitalsTimer.Interval = every;
        _vitalsTimer.Start();
        if (_vScan != null && !Eco)
            ((TranslateTransform)_vScan.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever,
                KeyFrames =
                {
                    new LinearDoubleKeyFrame(-40, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(220, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.6)), new SineEase { EasingMode = EasingMode.EaseInOut }),
                    new DiscreteDoubleKeyFrame(220, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(7))),
                },
            });
        if (_vCaret != null) _vCaret.Opacity = 0.6; // it rests: nothing on the page moves unless it means something
        _ = SampleVitalsAsync();
        PlayVitalsArrival();
    }

    void VitalsHidden()
    {
        _vitalsTimer.Stop();
        _vEndArmed = null;
        VTween(false);
        _vDot?.BeginAnimation(OpacityProperty, null);
        _vTag?.BeginAnimation(OpacityProperty, null);
        _vCaret?.BeginAnimation(OpacityProperty, null);
        if (_vScan != null) ((TranslateTransform)_vScan.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
    }

    // ───────────────────────────── colours ─────────────────────────────

    Color Warm => _cyber ? CyYellow : Color.FromRgb(0xFF, 0x9F, 0x0A);
    Color Hot => _cyber ? CyRed : Color.FromRgb(0xFF, 0x45, 0x3A);
    Color Calm => _cyber ? Color.FromRgb(0x00, 0xFF, 0xA3) : Color.FromRgb(0x30, 0xD1, 0x58);
    /// <summary>A level that's fine: soft white, no colour asking for attention.</summary>
    static Color Quiet => Color.FromArgb(0x9C, 0xFF, 0xFF, 0xFF);

    /// <summary>Each tile's own colour (Neon: cyan, magenta, yellow, green).</summary>
    Color Hue(VMetric m) => _cyber
        ? m switch { VMetric.Cpu => CyCyan, VMetric.Gpu => CyMagenta, VMetric.Ram => CyYellow, _ => Color.FromRgb(0x00, 0xFF, 0xA3) }
        : Skin.Key switch
        {
            "liquid" => m switch { VMetric.Cpu => Color.FromRgb(0x7F, 0xE7, 0xD9), VMetric.Gpu => Color.FromRgb(0xA7, 0x8B, 0xFA), VMetric.Ram => Color.FromRgb(0xF4, 0xB8, 0xE4), _ => Color.FromRgb(0x8B, 0xD3, 0xFF) },
            "abyss" => m switch { VMetric.Cpu => Color.FromRgb(0x2E, 0xE6, 0xD6), VMetric.Gpu => Color.FromRgb(0x5B, 0x8C, 0xFF), VMetric.Ram => Color.FromRgb(0x9B, 0xF2, 0x8A), _ => Color.FromRgb(0x4F, 0xC3, 0xF7) },
            "crystal" => m switch { VMetric.Cpu => Color.FromRgb(0x9E, 0xC9, 0xFF), VMetric.Gpu => Color.FromRgb(0xD0, 0xB4, 0xFF), VMetric.Ram => Color.FromRgb(0xFF, 0xD6, 0xA5), _ => Color.FromRgb(0xA8, 0xF0, 0xD8) },
            _ => m switch { VMetric.Cpu => Color.FromRgb(0x0A, 0x84, 0xFF), VMetric.Gpu => Color.FromRgb(0xBF, 0x5A, 0xF2), VMetric.Ram => Color.FromRgb(0x30, 0xD5, 0xC8), _ => Color.FromRgb(0x30, 0xD1, 0x58) },
        };

    /// <summary>The tile's colour, red at the limit.</summary>
    Color LoadHue(VMetric m, double v) => v >= 0.9 ? Hot : Hue(m);

    /// <summary>For bars that aren't a tile's: calm, warm when busy, red at the limit.</summary>
    Color LoadColor(double v) => v >= 0.85 ? Hot : v >= 0.6 ? Warm : CallsLook().Accent;

    // ───────────────────────────── building ─────────────────────────────

    Grid BuildVitalsFace()
    {
        var look = CallsLook();
        var g = new Grid { Margin = new Thickness(20, 0, 20, 0) };
        foreach (var h in new[] { 36.0, 6, 72, 6, 82, double.NaN })
            g.RowDefinitions.Add(new RowDefinition { Height = double.IsNaN(h) ? new GridLength(1, GridUnitType.Star) : new GridLength(h) });

        // ── the verdict, the uptime, Task Manager
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _vDot = Own<Shape>(look.Neon ? new Rectangle { Width = 7, Height = 7 } : new Ellipse { Width = 8, Height = 8 });
        _vDot.Fill = new SolidColorBrush(look.Label);
        _vDot.VerticalAlignment = VerticalAlignment.Center;
        _vDot.HorizontalAlignment = HorizontalAlignment.Left;
        _vDot.Margin = new Thickness(0, 0, 0, 14);
        head.Children.Add(_vDot);
        _vTag = null;
        _vCaret = null;
        if (look.Neon)
        {
            _vDot.Visibility = Visibility.Collapsed;
            head.ColumnDefinitions[0].Width = GridLength.Auto;
            _vTagText = Own(new TextBlock { FontFamily = CyFont, FontSize = 10.5, FontWeight = FontWeights.Bold, Text = "SCAN", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            _vTag = Own(new Border { BorderThickness = new Thickness(1), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 46,
                Background = new SolidColorBrush(Color.FromArgb(0x22, CyCyan.R, CyCyan.G, CyCyan.B)), BorderBrush = new SolidColorBrush(CyCyan), Child = _vTagText });
            _vTagText.Foreground = new SolidColorBrush(CyNight);
            _vTag.Background = new SolidColorBrush(CyCyan);
            head.Children.Add(_vTag);
        }
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _vHeadline = Own(new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = look.Font, Text = Loc.T("Checking your PC…") });
        _vSubline = Own(new TextBlock { FontSize = 11, Foreground = Frozen(look.Label), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), FontFamily = look.Font });
        if (look.Neon)
        {
            var headRow = new StackPanel { Orientation = Orientation.Horizontal };
            headRow.Children.Add(_vHeadline);
            _vCaret = Own(new TextBlock { Text = "_", FontSize = 15, FontWeight = FontWeights.Bold, FontFamily = look.Font, Foreground = Frozen(CyCyan), Margin = new Thickness(2, 0, 0, 0) });
            headRow.Children.Add(_vCaret);
            titles.Children.Add(headRow);
        }
        else titles.Children.Add(_vHeadline);
        titles.Children.Add(_vSubline);
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);

        var upRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(9, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        upRow.Children.Add(Own(new TextBlock { Text = "\uE823", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Foreground = Frozen(look.Label), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) }));
        _vUptime = Own(new TextBlock { FontSize = 10.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontFamily = look.Font });
        System.Windows.Documents.Typography.SetNumeralAlignment(_vUptime, FontNumeralAlignment.Tabular);
        upRow.Children.Add(_vUptime);
        var up = CallPlate(upRow, look, null, out var upPlate, 26);
        upPlate.CornerRadius = new CornerRadius(look.Neon ? 0 : 13);
        up.Cursor = Cursors.Arrow;
        up.ToolTip = "How long the PC has been on since it last started";
        up.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(up, 2); // not shown: when calm, the line under the verdict says how long the PC has been on

        var tm = CallPlate(Own(new TextBlock { Text = "\uE9D9", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = Frozen(look.Accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }), look, null, out var tmPlate, 26);
        tmPlate.Width = 26;
        tmPlate.CornerRadius = new CornerRadius(look.Neon ? 0 : 13);
        tm.Margin = new Thickness(6, 0, 0, 0);
        tm.VerticalAlignment = VerticalAlignment.Center;
        tm.ToolTip = "Task Manager";
        tm.Click += (_, _) => Launch("taskmgr.exe");
        Grid.SetColumn(tm, 3);
        head.Children.Add(tm);

        // a download in progress: a thin bar under the line
        _vDlTrack = Own(new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Background = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), Visibility = Visibility.Collapsed, Margin = new Thickness(18, 0, 0, -3) });
        _vDlFill = Own(new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = Frozen(look.Accent) });
        _vDlTrack.Child = _vDlFill;
        Grid.SetColumnSpan(_vDlTrack, 2);
        head.Children.Add(_vDlTrack);
        g.Children.Add(head);

        // ── the tiles
        _vTileGrid = new UniformGrid { Rows = 1, Margin = new Thickness(-4, 0, -4, 0) };
        _vTileGrid.Children.Add(BuildVTile(VMetric.Cpu, "CPU", "01", look));
        _vTileGrid.Children.Add(BuildVTile(VMetric.Gpu, "GPU", "02", look));
        _vTileGrid.Children.Add(BuildVTile(VMetric.Ram, "RAM", "03", look));
        _vTileGrid.Children.Add(BuildVTile(VMetric.Net, look.Neon ? "NET" : "Net", "04", look));
        Grid.SetRow(_vTileGrid, 2);
        g.Children.Add(_vTileGrid);

        // ── what's using it | connection, battery, disk
        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        var apps = new Grid();
        apps.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        apps.RowDefinitions.Add(new RowDefinition());
        var appsHead = new Grid();
        appsHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        appsHead.ColumnDefinitions.Add(new ColumnDefinition());
        appsHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _vAppsRule = null;
        if (look.Neon)
        {
            _vAppsRule = Own(new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 6, 0), Fill = new SolidColorBrush(A(CyCyan, 0x55)) });
            Grid.SetColumn(_vAppsRule, 1);
            appsHead.Children.Add(_vAppsRule);
        }
        _vAppsTitle = Own(new TextBlock { FontSize = 10, Foreground = Frozen(look.Label), FontFamily = look.Font, Margin = new Thickness(2, 0, 0, 0) });
        appsHead.Children.Add(_vAppsTitle);
        apps.Children.Add(appsHead);
        _vListClip = new Grid { ClipToBounds = true, Background = Brushes.Transparent };
        _vList = new Canvas { HorizontalAlignment = HorizontalAlignment.Stretch };
        _vListClip.Children.Add(_vList);
        _vAppsEmpty = Own(new TextBlock { Text = Loc.T("Looking…"), FontSize = 10.5, Foreground = Frozen(look.Label), FontFamily = look.Font, Margin = new Thickness(4, 3, 0, 0), IsHitTestVisible = false });
        _vListClip.Children.Add(_vAppsEmpty);
        _vListClip.SizeChanged += (_, _) => { foreach (var r in _vRowMap.Values) r.Root.Width = RowWidth; };
        Grid.SetRow(_vListClip, 1);
        apps.Children.Add(_vListClip);
        var appsPanel = VPanel(apps, look, new Thickness(6, 4, 6, 4), look.Neon ? Frozen(A(CyCyan, 0x55)) : null, CyCyan);
        bottom.Children.Add(appsPanel);

        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        side.Children.Add(SideRow(look, out _vNetGlyph, out _vNetName, out _vNetPing, extra: _vBars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }));
        for (int i = 0; i < 4; i++) _vBars.Children.Add(Own(new Rectangle { Width = 2.5, Height = 3 + i * 2.2, Margin = new Thickness(0, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Bottom, Fill = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)) }));
        side.Children.Add(SideRow(look, out _vBatGlyph, out _vBatText, out _vBatSide));
        var diskRow = SideRow(look, out var diskGlyph, out _vDiskText, out _, bar: true);
        diskGlyph.Text = "\uEDA2";
        side.Children.Add(diskRow);
        var right = VPanel(side, look, new Thickness(10, 5, 10, 5), look.Neon ? Frozen(A(CyCyan, 0x55)) : null, CyCyan);
        Grid.SetColumn(right, 2);
        bottom.Children.Add(right);
        Grid.SetRow(bottom, 4);
        g.Children.Add(bottom);

        PaintVTiles(animate: false);
        RenderVitalsAppsTitle();
        return g;
    }

    /// <param name="edge">Neon: the frame's brush (a gradient across, say); its soft light is <paramref name="glow"/>.</param>
    FrameworkElement VPanel(FrameworkElement child, CallLook look, Thickness pad, Brush? edge = null, Color? glow = null)
    {
        var b = Own(new Border
        {
            CornerRadius = new CornerRadius(look.Neon ? 0 : look.Radius), Background = Frozen(look.Plate), BorderBrush = Frozen(look.Edge), BorderThickness = new Thickness(1),
            Padding = pad, Child = child,
        });
        if (!look.Neon) return b;
        // Neon: the frame draws the edge, cuts and all
        b.BorderThickness = new Thickness(0);
        b.Padding = new Thickness(pad.Left + 1, pad.Top + 1, pad.Right + 1, pad.Bottom + 1);
        NeonCorner(b, 9, 6);
        var frame = new NeonFrame { Stroke = edge ?? Frozen(look.Edge), CutTR = 9, CutBL = 6, Hatch = true, Effect = NeonGlow(glow ?? CyCyan, 0.14) };
        var holder = new Grid();
        holder.Children.Add(b);
        holder.Children.Add(Kept(frame));
        return holder;
    }

    /// <summary>
    /// A glowing frame drawn once and kept as a picture: what moves behind it (the rain) or inside the tile no longer
    /// makes its blur be worked out again every frame, which is what weak graphics chips struggle with. Kept at the
    /// screen's own scale, so it stays sharp.
    /// </summary>
    FrameworkElement Kept(FrameworkElement glowing)
    {
        if (glowing.Effect == null) return glowing;
        return new Grid { Children = { glowing }, IsHitTestVisible = false, CacheMode = new BitmapCache(Math.Max(1, _scale)) { SnapsToDevicePixels = true } };
    }

    /// <summary>A soft light around a neon edge: small and dim, so it lifts the colour without hurting the eyes.</summary>
    /// (None in battery saver: the edge alone, nothing extra for the graphics card to draw.)
    System.Windows.Media.Effects.DropShadowEffect? NeonGlow(Color c, double opacity) => Eco ? null :
        new() { Color = c, BlurRadius = 9, ShadowDepth = 0, Opacity = opacity, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };

    /// <summary>A neon colour sideways into another (for the panels' edges).</summary>
    static Brush NeonSweep(Color from, Color to)
    {
        var g = new LinearGradientBrush(A(from, 0xD0), A(to, 0xD0), 0);
        g.Freeze();
        return g;
    }

    /// <summary>A dark plate, tinted a little with a colour (the picked tile).</summary>
    static Color Tinted(Color dark, Color hue, double k) => Color.FromArgb(dark.A,
        (byte)(dark.R + (hue.R - dark.R) * k), (byte)(dark.G + (hue.G - dark.G) * k), (byte)(dark.B + (hue.B - dark.B) * k));

    /// <summary>Neon: the top-right and bottom-left corners cut off, like the rest of the neon panels.</summary>
    static void NeonCorner(FrameworkElement el, double cut, double cutBL = 0)
    {
        el.SizeChanged += (_, e) => el.Clip = NeonFrame.Outline(e.NewSize.Width, e.NewSize.Height, 0, cut, 0, cutBL, 0);
    }

    Grid SideRow(CallLook look, out TextBlock glyph, out TextBlock text, out TextBlock right, FrameworkElement? extra = null, bool bar = false)
    {
        var row = new Grid { Height = 18 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        glyph = Own(new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Frozen(look.Accent), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(glyph);
        if (extra != null) { Grid.SetColumn(extra, 1); row.Children.Add(extra); }
        text = Own(new TextBlock { FontSize = 10.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = look.Font });
        Grid.SetColumn(text, 2);
        row.Children.Add(text);
        right = Own(new TextBlock { FontSize = 10.5, Foreground = Frozen(look.Label), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), FontFamily = look.Font });
        System.Windows.Documents.Typography.SetNumeralAlignment(right, FontNumeralAlignment.Tabular);
        Grid.SetColumn(right, 3);
        row.Children.Add(right);
        if (bar)
        {
            var track = Own(new Border { Width = 46, Height = 4, CornerRadius = new CornerRadius(look.Neon ? 0 : 2), Background = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            _vDiskFill = Own(new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(look.Neon ? 0 : 2), Background = new SolidColorBrush(look.Accent) });
            track.Child = _vDiskFill;
            if (look.Neon)
            {
                var cells = new Grid { IsHitTestVisible = false };
                for (int i = 1; i < 10; i++) cells.Children.Add(Own(new Rectangle { Width = 1, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(i * 4.6 - 0.5, 0, 0, 0), Fill = Frozen(Color.FromArgb(0xFF, 0x04, 0x0B, 0x10)) }));
                var holder = new Grid { Width = 46, Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                track.Margin = new Thickness(0);
                holder.Children.Add(track);
                holder.Children.Add(cells);
                Grid.SetColumn(holder, 3);
                row.Children.Remove(right);
                row.Children.Add(holder);
                return row;
            }
            Grid.SetColumn(track, 3);
            row.Children.Remove(right);
            row.Children.Add(track);
        }
        return row;
    }

    FrameworkElement BuildVTile(VMetric kind, string label, string index, CallLook look)
    {
        var hue = Hue(kind);
        // the words on top, the level bar along the bottom
        var inner = new Grid { ClipToBounds = true };
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.RowDefinitions.Add(new RowDefinition());

        // ── words: name and detail on one line, the number (and the minute's peak) under them
        var text = new StackPanel { Margin = new Thickness(10, 7, 9, 0), IsHitTestVisible = false };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        var lab = Own(new TextBlock { Text = look.Neon ? label.ToUpperInvariant() : label, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(look.Label), FontFamily = look.Font, VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(lab);
        var sub = Own(new TextBlock { FontSize = 10, Foreground = Frozen(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)), FontFamily = look.Font, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        System.Windows.Documents.Typography.SetNumeralAlignment(sub, FontNumeralAlignment.Tabular);
        Grid.SetColumn(sub, 1);
        top.Children.Add(sub);
        text.Children.Add(top);

        var numberLine = new Grid { Margin = new Thickness(0, 1, 0, 0) };
        numberLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        numberLine.ColumnDefinitions.Add(new ColumnDefinition());
        var value = Own(new TextBlock { FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontFamily = look.Font, Text = "—" });
        System.Windows.Documents.Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        TextBlock? ghost = null;
        if (look.Neon)
        {
            // the number split into two colours, a hair apart
            var split = new Grid();
            var other = kind == VMetric.Gpu ? CyCyan : CyMagenta;
            ghost = Own(new TextBlock { FontSize = 24, FontWeight = FontWeights.SemiBold, FontFamily = look.Font, Text = "—", Foreground = Frozen(Color.FromArgb(0x80, other.R, other.G, other.B)), Margin = new Thickness(-1.4, 0, 0, 0) });
            System.Windows.Documents.Typography.SetNumeralAlignment(ghost, FontNumeralAlignment.Tabular);
            split.Children.Add(ghost);
            split.Children.Add(value);
            numberLine.Children.Add(split);
        }
        else numberLine.Children.Add(value);
        var peakText = Own(new TextBlock
        {
            FontFamily = look.Font, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 6), Opacity = 0,
            Foreground = Frozen(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)),
            ToolTip = "The highest in the last minute",
        });
        System.Windows.Documents.Typography.SetNumeralAlignment(peakText, FontNumeralAlignment.Tabular);
        Grid.SetColumn(peakText, 1);
        numberLine.Children.Add(peakText);
        text.Children.Add(numberLine);
        inner.Children.Add(text);

        // ── the level: a thin bar along the bottom (Neon: in cells). It moves by its mask only, no layout.
        var bar = new Grid { Height = look.Neon ? 5 : 3, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 10, 9), IsHitTestVisible = false };
        Grid.SetRow(bar, 1);
        bar.Children.Add(Own(new Rectangle { Fill = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), RadiusX = look.Neon ? 0 : 1.5, RadiusY = look.Neon ? 0 : 1.5 }));
        var levelStops = new[] { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Transparent, 0) };
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
        mask.GradientStops.Add(levelStops[0]);
        mask.GradientStops.Add(levelStops[1]);
        var levelFill = new SolidColorBrush(hue);
        bar.Children.Add(Own(new Rectangle { Fill = levelFill, OpacityMask = mask, RadiusX = look.Neon ? 0 : 1.5, RadiusY = look.Neon ? 0 : 1.5 }));
        if (look.Neon)
        {
            var cellsMask = new DrawingBrush(new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, 3, 1))))
            { TileMode = TileMode.Tile, Viewbox = new Rect(0, 0, 4, 1), ViewboxUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 4.5, 10), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            cellsMask.Freeze();
            bar.OpacityMask = cellsMask;
        }
        inner.Children.Add(bar);
        var graph = new VPeak();
        Rectangle? topBar = null;
        if (look.Neon)
        {
            topBar = Own(new Rectangle { Height = 2, VerticalAlignment = VerticalAlignment.Top, Fill = Frozen(hue), Opacity = 0, IsHitTestVisible = false });
            Grid.SetRowSpan(topBar, 2);
            inner.Children.Add(topBar);
        }
        var b = CallPlate(inner, look, hue, out var plate, 72);
        plate.ClipToBounds = true;
        b.Margin = new Thickness(4, 0, 4, 0);
        var tile = new VTile { Kind = kind, Plate = plate, Value = value, Sub = sub, Label = lab, Graph = graph, Btn = b, Hue = hue, Unit = kind == VMetric.Net ? "" : "%",
            Ghost = ghost, PeakText = peakText, TopBar = topBar, Root = b, LevelStops = levelStops, LevelFill = levelFill };
        if (look.Neon)
        {
            // a crisp edge in the tile's colour, its cuts included, with a soft light; it lifts and gives with the tile
            plate.BorderThickness = new Thickness(0);
            NeonCorner(plate, 9, 6);
            tile.Edge = new SolidColorBrush(A(CyCyan, 0x40));
            tile.Glow = NeonGlow(hue, 0);
            var frame = new NeonFrame { Stroke = tile.Edge, CutTR = 9, CutBL = 6, Hatch = true, Effect = tile.Glow };
            var kept = Kept(frame);
            kept.RenderTransform = plate.RenderTransform;
            kept.RenderTransformOrigin = new Point(0.5, 0.5);
            var holder = new Grid { Margin = b.Margin };
            b.Margin = new Thickness(0);
            holder.Children.Add(b);
            holder.Children.Add(kept);
            tile.Root = holder;
            var fx = tile.Glow;
            if (fx != null) b.MouseEnter += (_, _) => fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(_vMetric == kind ? 0.5 : 0.22, TimeSpan.FromMilliseconds(160)));
            if (fx != null) b.MouseLeave += (_, _) => fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(_vMetric == kind ? 0.42 : 0, TimeSpan.FromMilliseconds(260)));
        }
        if (kind == VMetric.Net) { b.Cursor = Cursors.Arrow; b.ToolTip = "Download speed · upload and ping below"; }
        else
        {
            b.ToolTip = "See which apps use it";
            b.Click += (_, _) =>
            {
                if (_vMetric == kind) return;
                _vMetric = kind;
                PaintVTiles(animate: true);
                RenderVitalsAppsTitle();
                RenderVitalsApps(swap: true);
            };
        }
        _vTiles.Add(tile);
        return tile.Root;
    }

    /// <summary>The tile whose apps are listed below is lit in its colour (the colours glide).</summary>
    void PaintVTiles(bool animate)
    {
        var look = CallsLook();
        var d = TimeSpan.FromMilliseconds(animate ? 240 : 0);
        foreach (var t in _vTiles)
        {
            bool on = t.Kind == _vMetric;
            t.Plate.Tag = on ? "on" : null;
            var edge = on ? A(t.Hue, look.Neon ? (byte)0xD0 : (byte)0x99) : look.Edge;
            var fill = on ? A(t.Hue, look.Neon ? (byte)0x1E : (byte)0x1A) : look.Plate;
            if (look.Neon) fill = on ? Tinted(look.Plate, t.Hue, 0.13) : look.Plate;
            ((SolidColorBrush)t.Plate.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(edge, d) { EasingFunction = Out });
            ((SolidColorBrush)t.Plate.Background).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(fill, d) { EasingFunction = Out });
            // only the picked tile wears its colour; the others stay quiet until they're busy
            t.Edge?.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? t.Hue : A(CyCyan, 0x40), d) { EasingFunction = Out });
            t.Glow?.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(on ? 0.42 : 0, d));
            t.TopBar?.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, d));
            ((SolidColorBrush)t.Label.Foreground).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? (look.Neon ? t.Hue : Colors.White) : look.Label, d));
        }
        if (animate) RenderVitalsTiles(false, false); // the level bars take the new colours
    }

    double RowWidth => Math.Max(0, (_vListClip?.ActualWidth ?? 0) - 5);

    /// <summary>One app's line: a soft fill behind it as wide as its share, a coloured edge, icon, name, number, and ✕ on hover.</summary>
    VRow MakeVRow(AppLoad.App a, CallLook look)
    {
        var root = new Grid { Height = VRowH - 1, Width = RowWidth, Background = Brushes.Transparent, Opacity = 0 };
        var hover = Own(new Border { CornerRadius = new CornerRadius(look.Neon ? 0 : 4), Background = Brushes.Transparent });
        root.Children.Add(hover);
        var fill = Own(new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(look.Neon ? 0 : 4), Background = new SolidColorBrush(Colors.Transparent) });
        root.Children.Add(fill);
        var sliver = Own(new Rectangle { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3), RadiusX = look.Neon ? 0 : 1, RadiusY = look.Neon ? 0 : 1, Fill = new SolidColorBrush(Colors.Transparent) });
        root.Children.Add(sliver);
        var line = new Grid { Margin = new Thickness(7, 0, 2, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        var icon = Own(new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(look.Neon ? 0 : 3), Background = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(icon);
        var name = Own(new TextBlock { FontSize = 10.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = look.Font });
        Grid.SetColumn(name, 1);
        line.Children.Add(name);
        var val = Own(new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0), FontFamily = look.Font });
        System.Windows.Documents.Typography.SetNumeralAlignment(val, FontNumeralAlignment.Tabular);
        Grid.SetColumn(val, 2);
        line.Children.Add(val);
        var x = Own(new TextBlock { Text = "\uE711", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Frozen(look.Label), Opacity = 0 });
        var end = new Button { Template = Bare, Content = new Border { Background = Brushes.Transparent, Child = x }, Cursor = Cursors.Hand, Focusable = false, Visibility = Visibility.Hidden };
        Grid.SetColumn(end, 3);
        line.Children.Add(end);
        root.Children.Add(line);
        var r = new VRow { Root = root, Icon = icon, Fill = fill, Sliver = sliver, Name = name, Value = val, X = x, End = end, App = a.Name, Current = a };
        var hoverColor = Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF);
        root.MouseEnter += (_, _) =>
        {
            hover.Background = Frozen(hoverColor);
            if (r.Current?.CanEnd == true) x.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        };
        root.MouseLeave += (_, _) =>
        {
            hover.Background = Brushes.Transparent;
            if (_vEndArmed != r.App) x.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
        };
        end.Click += (_, _) => { if (r.Current is { } cur) EndApp(cur); };
        name.Text = a.Display;
        name.ToolTip = a.Count > 1 ? $"{a.Path ?? a.Name} · {a.Count} processes" : a.Path ?? a.Name;
        SetRowIcon(r, a.Path);
        return r;
    }

    /// <summary>The tiles rise in one after another (not in Eco).</summary>
    void PlayVitalsArrival()
    {
        if (Eco || _vTileGrid == null) return;
        int i = 0;
        foreach (UIElement el in _vTileGrid.Children)
        {
            var shift = new TranslateTransform(0, 10);
            el.RenderTransform = shift;
            el.Opacity = 0;
            var begin = TimeSpan.FromMilliseconds(45 * i++);
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(460)) { BeginTime = begin, EasingFunction = new SpringEase { Damping = 0.75 } });
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = begin });
        }
    }

    // ───────────────────────────── sampling ─────────────────────────────

    async Task SampleVitalsAsync()
    {
        if (_page != 1 || _mode != Mode.Expanded) { VitalsHidden(); return; }
        if (_vBusy) return;
        _vBusy = true;
        try
        {
            int tick = _vTick++;
            _vStrain ??= new StrainService();
            var strain = _vStrain;
            bool appsTick = tick % 2 == 0 || tick == 1;
            var readTask = Task.Run(() => { try { return (StrainService.Reading?)strain.Sample(perApp: appsTick); } catch { return null; } });
            var snapTask = _monitor.SampleAsync();
            Task<List<AppLoad.App>>? appsTask = appsTick ? readTask.ContinueWith(_ => { try { return _appLoad.Sample(strain.GpuByProcess); } catch { return new List<AppLoad.App>(); } }, TaskScheduler.Default) : null;
            Task<ConnectionDetails>? connTask = tick % 3 == 0 ? _monitor.ConnectionAsync() : null;
            Task<BatteryDetails>? batTask = tick % 3 == 0 ? _monitor.BatteryAsync() : null;

            var snap = await snapTask;
            var read = await readTask;
            if (snap != null) _vSnap = snap;
            if (read != null) _vRead = read;
            if (appsTask != null) _vAppList = await appsTask;
            if (batTask != null) _vBat = await batTask;
            if (_page != 1 || _mode != Mode.Expanded) return;
            RenderVitalsTiles(snap != null, read != null);
            if (appsTask != null) RenderVitalsApps(swap: false);
            RenderVitalsSide();
            RenderVitalsHeader();
            if (connTask != null)
            {
                _vConn = await connTask;
                if (_page == 1 && _mode == Mode.Expanded) { RenderVitalsSide(); RenderVitalsHeader(); }
            }
        }
        catch (Exception ex) { AppLog.Write("system page: " + ex.Message); }
        finally { _vBusy = false; }
    }

    // ───────────────────────────── painting ─────────────────────────────

    void RenderVitalsTiles(bool fresh, bool freshRead)
    {
        var s = _vSnap;
        var r = _vRead;
        bool gpuKnown = r?.GpuKnown != false && _vStrain?.GpuAvailable != false;
        foreach (var t in _vTiles)
        {
            switch (t.Kind)
            {
                case VMetric.Cpu when s != null:
                    Feed(t, s.Cpu, fresh);
                    Temp(t, r?.CpuTemp, 80);
                    break;
                case VMetric.Gpu:
                    t.Root.Visibility = gpuKnown ? Visibility.Visible : Visibility.Collapsed;
                    if (r == null) break;
                    Feed(t, r.Gpu / 100.0, freshRead);
                    Temp(t, r.GpuTemp, 78);
                    break;
                case VMetric.Ram when s != null:
                    Feed(t, s.Memory, fresh);
                    t.Sub.Text = $"{s.MemUsed / 1073741824.0:0.0}/{s.MemTotal / 1073741824.0:0} GB";
                    break;
                case VMetric.Net when s != null:
                    t.Target = s.DownBps;
                    t.Sub.Text = s.UpBps >= 100 * 1024 ? "↑ " + Speed(s.UpBps) : ""; // the upload only when something is sending
                    if (fresh) { t.Graph.Push(s.DownBps); ShowPeak(t, Speed(t.Graph.PeakRaw), t.Graph.PeakRaw > Math.Max(100 * 1024, s.DownBps * 2)); }
                    SetLevel(t, t.Graph.PeakRaw > 0 ? s.DownBps / Math.Max(1, t.Graph.PeakRaw) : 0, Quiet);
                    break;
            }
        }
        if (_vTileGrid != null && _vTileGrid.Columns != (gpuKnown ? 4 : 3)) _vTileGrid.Columns = gpuKnown ? 4 : 3;
        if (!gpuKnown && _vMetric == VMetric.Gpu) { _vMetric = VMetric.Cpu; PaintVTiles(animate: true); RenderVitalsAppsTitle(); }
        VTween(true);

        void Feed(VTile t, double v, bool push)
        {
            t.Target = v * 100;
            // colour means something: red at the limit, amber when busy, the tile's own only when it's the picked one
            var c = v >= 0.9 ? Hot : v >= 0.7 ? Warm : t.Kind == _vMetric ? t.Hue : Quiet;
            t.Value.Foreground = v >= 0.9 ? Frozen(Lighten(c, 0.2)) : Brushes.White;
            // the minute's peak only when it says something the number doesn't (a spike well above now)
            if (push) t.Graph.Push(v);
            ShowPeak(t, $"{t.Graph.PeakRaw * 100:0}%", t.Graph.PeakRaw - v >= 0.15);
            SetLevel(t, v, c);
        }

        void Temp(VTile t, double? temp, double warm)
        {
            // a temperature only when it's getting warm
            bool show = temp is { } tc && tc >= warm;
            t.Sub.Text = show ? $"{temp:0} °C" : "";
            if (show) t.Sub.Foreground = Frozen(temp >= warm + 12 ? Hot : Warm);
        }

        void ShowPeak(VTile t, string label, bool show)
        {
            if (t.PeakText == null) return;
            string pk = (_cyber ? "PK " : "↑ ") + label;
            if (t.PeakText.Text != pk) t.PeakText.Text = pk;
            double to = show ? 1 : 0;
            if (Math.Abs(t.PeakText.Opacity - to) > 0.01) t.PeakText.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(300)));
        }
    }

    /// <summary>The level bar glides to its new length (its mask moves: nothing is laid out again), in its colour.</summary>
    void SetLevel(VTile t, double f, Color c)
    {
        f = Math.Clamp(f, 0, 1);
        if (t.LevelFill.Color != c) t.LevelFill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(300)));
        if (Math.Abs(f - t.Level) < 0.004) return;
        var a = new DoubleAnimation(f, TimeSpan.FromMilliseconds(t.Level < 0 ? 0 : 520)) { EasingFunction = Out };
        t.Level = f;
        t.LevelStops[0].BeginAnimation(GradientStop.OffsetProperty, a);
        t.LevelStops[1].BeginAnimation(GradientStop.OffsetProperty, a);
    }

    /// <summary>The numbers count to their new value at the display's rate, then the frame hook goes away.</summary>
    void VTween(bool on)
    {
        if (on == _vTweening) return;
        _vTweening = on;
        if (on) { _vTweenLast = TimeSpan.Zero; CompositionTarget.Rendering += OnVitalsFrame; }
        else CompositionTarget.Rendering -= OnVitalsFrame;
    }

    void OnVitalsFrame(object? sender, EventArgs e)
    {
        if (_page != 1 || _mode != Mode.Expanded) { VTween(false); return; }
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _vTweenLast) return;
        double dt = _vTweenLast == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - _vTweenLast).TotalSeconds, 0.1);
        _vTweenLast = now;
        bool moving = false;
        foreach (var t in _vTiles)
        {
            double k = 1 - Math.Exp(-dt * 9);
            double next = t.Shown + (t.Target - t.Shown) * k;
            bool done = t.Kind == VMetric.Net ? Math.Abs(t.Target - next) < Math.Max(64, t.Target * 0.01) : Math.Abs(t.Target - next) < 0.4;
            if (done) next = t.Target; else moving = true;
            if (next == t.Shown && t.Value.Text != "—") continue;
            t.Shown = next;
            string text = t.Kind == VMetric.Net ? Speed(next) : $"{next:0}%";
            if (t.Value.Text != text) { t.Value.Text = text; if (t.Ghost != null) t.Ghost.Text = text; }
            if (t.Kind == VMetric.Net)
            {
                double size = text.Length > 7 ? 15 : 18;
                if (t.Value.FontSize != size) { t.Value.FontSize = size; if (t.Ghost != null) t.Ghost.FontSize = size; }
            }
        }
        foreach (var r in _vRowMap.Values)
        {
            if (double.IsNaN(r.Shown)) continue;
            double k = 1 - Math.Exp(-dt * 8);
            double next = r.Shown + (r.Target - r.Shown) * k;
            if (Math.Abs(r.Target - next) < (_vMetric == VMetric.Ram ? 1 : 0.05)) next = r.Target; else moving = true;
            r.Shown = next;
            string text = VRowText(next);
            if (r.Value.Text != text) r.Value.Text = text;
        }
        if (!moving) VTween(false);
    }

    static string Speed(double bps)
    {
        if (bps < 1024) return $"{bps:0} B/s";
        if (bps < 1024 * 1024) return $"{bps / 1024:0} KB/s";
        return $"{bps / 1048576:0.#} MB/s";
    }

    void RenderVitalsAppsTitle()
    {
        if (_vAppsTitle == null) return;
        string what = _vMetric switch { VMetric.Gpu => "GPU", VMetric.Ram => Loc.T("memory"), _ => "CPU" };
        _vAppsTitle.Text = _cyber ? $"// TOP_{(_vMetric == VMetric.Ram ? "RAM" : what)}" : string.Format(Loc.T("Using the most {0}"), what);
        _vAppsTitle.Foreground = Frozen(_cyber ? Hue(_vMetric) : CallsLook().Label);
        if (_vAppsRule != null) ((SolidColorBrush)_vAppsRule.Fill).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(A(Hue(_vMetric), 0x60), TimeSpan.FromMilliseconds(240)));
    }

    /// <summary>
    /// The three apps doing the most for the picked tile. Each app keeps its own row,
    /// which slides to its new place when the ranking changes; values are smoothed (so apps don't hop places on every
    /// reading) and count to their new number; the fill behind each row glides to its share. ✕ ends one (twice).
    /// </summary>
    void RenderVitalsApps(bool swap)
    {
        if (_vList == null || _vAppsEmpty == null) return;
        var look = CallsLook();
        var hue = Hue(_vMetric);
        if (swap)
        {
            // another tile: the old list fades away, the new one comes in from the side
            foreach (var old in _vRowMap.Values.ToList()) LeaveRow(old, fast: true);
            _vRowMap.Clear();
            _vSmooth.Clear();
        }

        // smoothed values: half the way to the new reading each time
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _vAppList)
        {
            double raw = _vMetric switch { VMetric.Gpu => a.Gpu, VMetric.Ram => a.MemoryMb, _ => a.Cpu };
            _vSmooth[a.Name] = _vSmooth.TryGetValue(a.Name, out var prev) && _vMetric != VMetric.Ram ? prev + (raw - prev) * 0.5 : raw;
            seen.Add(a.Name);
        }
        foreach (var k in _vSmooth.Keys.Where(k => !seen.Contains(k)).ToList()) _vSmooth.Remove(k);

        double floor = 0;
        var top = _vAppList.Select(a => (App: a, V: _vSmooth[a.Name])).Where(x => x.V >= floor).OrderByDescending(x => x.V).Take(3).ToList();
        double ramTop = Math.Max(1, top.Count > 0 ? top.Max(x => x.V) : 1);
        double w = RowWidth;

        for (int i = 0; i < top.Count; i++)
        {
            var (a, v) = top[i];
            bool fresh = !_vRowMap.TryGetValue(a.Name, out var row);
            if (fresh)
            {
                row = MakeVRow(a, look);
                _vRowMap[a.Name] = row;
                Canvas.SetTop(row.Root, i * VRowH + (swap ? 0 : 6));
                if (swap) row.Root.RenderTransform = new TranslateTransform(10, 0);
                _vList.Children.Add(row.Root);
            }
            row!.Current = a;
            row.Leaving = false;
            row.Target = v;
            if (double.IsNaN(row.Shown)) row.Shown = v;
            var stagger = TimeSpan.FromMilliseconds(swap ? 35 * i : 0);
            row.Root.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(i * VRowH, TimeSpan.FromMilliseconds(420)) { EasingFunction = Out, BeginTime = stagger });
            if (row.Root.Opacity < 1 || fresh) row.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { BeginTime = stagger });
            if (swap && row.Root.RenderTransform is TranslateTransform tt) tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = Out, BeginTime = stagger });
            if (!fresh && a.Count > 1) row.Name.ToolTip = $"{a.Path ?? a.Name} · {a.Count} processes";

            double share = _vMetric == VMetric.Ram ? v / ramTop : v / 100;
            double load = _vMetric == VMetric.Ram && _vSnap is { } s ? v * 1048576.0 / Math.Max(1, s.MemTotal) * 2.5 : v / 100;
            var c = load >= 0.85 ? Hot : load >= 0.6 ? Warm : hue;
            row.Fill.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Max(3, w * Math.Clamp(share, 0, 1)), TimeSpan.FromMilliseconds(700)) { EasingFunction = Out });
            ((SolidColorBrush)row.Fill.Background).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(A(c, look.Neon ? (byte)0x30 : (byte)0x26), TimeSpan.FromMilliseconds(400)));
            ((SolidColorBrush)row.Sliver.Fill).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(400)));
            row.Value.Foreground = Frozen(Lighten(c, 0.2));
            bool armed = _vEndArmed == a.Name;
            row.End.Visibility = a.CanEnd ? Visibility.Visible : Visibility.Hidden;
            row.End.ToolTip = string.Format(Loc.T("End {0}"), a.Display);
            row.X.Foreground = Frozen(armed ? Hot : look.Label);
            if (armed) { row.X.BeginAnimation(OpacityProperty, null); row.X.Opacity = 1; }
            else if (!row.Root.IsMouseOver) { row.X.BeginAnimation(OpacityProperty, null); row.X.Opacity = 0; }
            if (fresh) row.Value.Text = VRowText(v);
        }
        var keep = new HashSet<string>(top.Select(x => x.App.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _vRowMap.Values.Where(r => !keep.Contains(r.App)).ToList())
        {
            _vRowMap.Remove(gone.App);
            LeaveRow(gone, fast: false);
        }

        _vAppsEmpty.Text = _vAppList.Count == 0 ? Loc.T("Looking…") : Loc.T("Nothing is busy right now");
        _vAppsEmpty.BeginAnimation(OpacityProperty, new DoubleAnimation(top.Count == 0 ? 1 : 0, TimeSpan.FromMilliseconds(200)));
        VTween(true);
    }

    void LeaveRow(VRow row, bool fast)
    {
        row.Leaving = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(fast ? 120 : 260));
        fade.Completed += (_, _) => { if (row.Leaving) _vList?.Children.Remove(row.Root); };
        row.Root.BeginAnimation(OpacityProperty, fade);
    }

    string VRowText(double v) => _vMetric == VMetric.Ram
        ? (v >= 1024 ? $"{v / 1024.0:0.0} GB" : $"{v:0} MB")
        : v < 1 ? "<1%" : $"{v:0}%";

    readonly Dictionary<string, Brush?> _vIcons = new(StringComparer.OrdinalIgnoreCase);

    void SetRowIcon(VRow row, string? path)
    {
        row.Icon.Background = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        if (path == null) return;
        if (_vIcons.TryGetValue(path, out var cached)) { if (cached != null) row.Icon.Background = cached; return; }
        _vIcons[path] = null;
        string want = row.App ?? "";
        _ = Task.Run(() => { try { return IconHelper.ForPath(path); } catch { return null; } }).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            if (t.Result is not { } img || IconBrush(img) is not { } brush) return;
            _vIcons[path] = brush;
            foreach (var r in _vRowMap.Values) if (r.Current?.Path == path) r.Icon.Background = brush;
        }));
    }

    /// <summary>First click arms it (the line says what will close), the second within 4 s ends every process of that app.</summary>
    void EndApp(AppLoad.App a)
    {
        if (_vEndArmed != a.Name)
        {
            _vEndArmed = a.Name;
            int id = ++_vEndArmId;
            VitalsSay(string.Format(Loc.T("Click ✕ again to close {0}"), a.Display), 4000);
            RenderVitalsApps(swap: false);
            After(4000, () => { if (id == _vEndArmId && _vEndArmed != null) { _vEndArmed = null; RenderVitalsApps(swap: false); } });
            return;
        }
        _vEndArmed = null;
        _vEndArmId++;
        string name = a.Name, display = a.Display;
        _ = Task.Run(() => AppLoad.End(name)).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            AppLog.Write($"system page: ended {name} ({(t.Result ? "ok" : "some processes refused")})");
            VitalsSay(t.Result ? string.Format(Loc.T("Closed {0}"), display) : string.Format(Loc.T("{0} didn't close completely"), display), 3500);
            FlashRim(t.Result ? Calm : Hot);
            _vAppList = _vAppList.Where(x => !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            RenderVitalsApps(swap: false);
        }));
    }

    /// <summary>A short message in the line's place for a few seconds.</summary>
    void VitalsSay(string text, int ms)
    {
        _vSay = text;
        _vSayUntil = DateTime.UtcNow.AddMilliseconds(ms);
        RenderVitalsHeader();
        After(ms + 50, RenderVitalsHeader);
    }

    void RenderVitalsSide()
    {
        if (_vNetName == null) return;
        var look = CallsLook();
        // the connection
        if (_vConn is { } c)
        {
            _vNetGlyph!.Text = c.IsWifi ? "\uE701" : "\uE839";
            _vNetName.Text = !c.Online ? Loc.T("No internet") : c.Captive ? Loc.T("Sign in to this network") : c.Name;
            int bars = c.Online ? Math.Clamp(c.SignalBars ?? (c.IsWifi ? 0 : 5), 0, 5) : 0;
            int lit = c.IsWifi ? (int)Math.Ceiling(bars * 4 / 5.0) : c.Online ? 4 : 0;
            _vBars!.Visibility = c.IsWifi ? Visibility.Visible : Visibility.Collapsed;
            var on = lit <= 1 ? Warm : Colors.White;
            for (int i = 0; i < _vBars.Children.Count; i++)
                ((SolidColorBrush)((Rectangle)_vBars.Children[i]).Fill).BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(i < lit ? on : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(40 * i) });
            if (c.PingMs is { } ms)
            {
                long typical = c.TypicalMs ?? ms;
                bool shaky = c.JitterMs is >= 30;
                _vNetPing!.Text = $"{ms} ms";
                _vNetPing.Foreground = Frozen(typical < 60 && !shaky ? look.Label : typical < 150 ? Warm : Hot);
                _vNetPing.ToolTip = c.JitterMs is { } j ? $"{Loc.T("Typical")} {typical} ms · {Loc.T("jitter")} {j} ms" : null;
            }
            else
            {
                _vNetPing!.Text = c.Online ? Loc.T("timed out") : "—";
                _vNetPing.Foreground = Frozen(c.Online ? Hot : look.Label);
            }
            string link = string.Join(" · ", new[] { c.WifiGeneration, c.LinkMbps is { } r ? $"{r} Mbps" : null, c.SignalPercent is { } q ? $"{Loc.T("signal")} {q}%" : null }.Where(x => x != null));
            _vNetName.ToolTip = link.Length > 0 ? link : null;
        }
        else if (_vNetName.Text.Length == 0) { _vNetGlyph!.Text = "\uE701"; _vNetName.Text = Loc.T("Checking…"); }

        // the battery
        var b = _vBat;
        if (!_power.HasBattery || b is { Present: false })
        {
            _vBatGlyph!.Text = "\uE945";
            _vBatText!.Text = Loc.T("On mains power");
            _vBatSide!.Text = "";
        }
        else
        {
            _vBatGlyph!.Text = _power.PluggedIn ? "\uE945" : "\uE83F";
            _vBatGlyph.Foreground = Frozen(_power.Percent <= 20 && !_power.PluggedIn ? Hot : look.Accent);
            string state =
                b == null ? "" :
                b.Full || (_power.PluggedIn && _power.Percent >= 99) ? Loc.T("full") :
                b.Charging && b.TimeToFull is { } tf ? string.Format(Loc.T("full in {0}"), SystemMonitor.Duration(tf)) :
                b.Charging ? Loc.T("charging") :
                _power.PluggedIn ? Loc.T("plugged in") :
                b.TimeLeft is { } tl ? string.Format(Loc.T("{0} left"), SystemMonitor.Duration(tl)) : "";
            _vBatText!.Text = state.Length > 0 ? $"{_power.Percent}% · {state}" : $"{_power.Percent}%";
            _vBatText.ToolTip = b?.WattsNow is { } w ? (b.Charging ? $"Charging at {w:0.#} W" : $"Using {w:0.#} W") : null;
            if (b?.HealthPercent is { } h && h < 80) // the battery's health only once it's worn
            {
                _vBatSide!.Text = string.Format(Loc.T("{0}% health"), h.ToString("0"));
                _vBatSide.Foreground = Frozen(h >= 80 ? look.Label : h >= 60 ? Warm : Hot);
                _vBatSide.ToolTip = b.RemainingWh is { } rw && b.FullWh is { } fw ? $"{rw:0.#} of {fw:0.#} Wh" : null;
            }
            else _vBatSide!.Text = "";
        }

        // the system disk
        if (_vSnap is { } s && _vDiskText != null && _vDiskFill != null)
        {
            string drive = System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
            _vDiskText.Text = string.Format(Loc.T("{0} {1} free"), drive, SystemMonitor.Bytes(s.DiskFree));
            double to = Math.Max(2, 46 * Math.Clamp(s.Disk, 0, 1));
            if (Math.Abs(_vDiskFill.Width - to) > 0.5 || double.IsNaN(_vDiskFill.Width))
                _vDiskFill.BeginAnimation(WidthProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(600)) { EasingFunction = Out });
            ((SolidColorBrush)_vDiskFill.Background).Color = s.Disk >= 0.95 ? Hot : s.Disk >= 0.85 ? Warm : look.Accent;
        }
    }

    /// <summary>The line: the most important thing about the PC right now, in words.</summary>
    void RenderVitalsHeader()
    {
        if (_vHeadline == null || _vSubline == null || _vDot == null || _vUptime == null) return;
        if (_vSnap is { } snap)
        {
            var u = snap.Uptime;
            _vUptime.Text = UpFor(u);
        }
        var (head, sub, c) = Verdict();
        if (_vHeadline.Text != head)
        {
            _vHeadline.Text = head;
            _vHeadline.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(260)));
        }
        if (c != _vDotColor && _vTag != null && _vTagText != null)
        {
            string tag = c == Calm ? "OK" : c == Hot ? "CRIT" : c == Warm ? "WARN" : "SCAN";
            _vTagText.Text = tag;
            var d0 = TimeSpan.FromMilliseconds(260);
            // calm: a quiet outline; only a warning fills in with its colour
            bool quiet = c == Calm || tag == "SCAN";
            var tone = quiet ? CyCyan : c;
            _vTagText.Foreground = Frozen(quiet ? A(CyCyan, 0xD0) : CyNight);
            ((SolidColorBrush)_vTag.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(quiet ? A(tone, 0x70) : tone, d0));
            ((SolidColorBrush)_vTag.Background).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(quiet ? A(tone, 0x14) : tone, d0));
            if (c == Hot && !Eco)
                _vTag.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, KeyFrames = { new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)), new DiscreteDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))), new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800))) } });
            else { _vTag.BeginAnimation(OpacityProperty, null); _vTag.Opacity = 1; }
            if (_vCaret != null) _vCaret.Foreground = Frozen(c == Calm ? CyCyan : c);
        }
        if (c != _vDotColor)
        {
            _vDotColor = c;
            ((SolidColorBrush)_vDot.Fill).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(300)));
            // a calm dot rests; a warning one breathes (Neon: blinks like a status LED)
            if (c == Calm || Eco) { _vDot.BeginAnimation(OpacityProperty, null); _vDot.Opacity = 1; }
            else _vDot.BeginAnimation(OpacityProperty, _cyber
                ? new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, KeyFrames = { new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)), new DiscreteDoubleKeyFrame(0.25, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))), new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))) } }
                : new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }

        // a download in progress takes the second line, with its bar
        var d = _files.Items.FirstOrDefault();
        bool saying = _vSay != null && DateTime.UtcNow < _vSayUntil;
        if (saying) sub = _vSay!;
        else if (d != null)
        {
            string speed = d.Speed > 1024 ? $" · {SystemMonitor.Bytes(d.Speed)}/s" : "";
            string left = d.Left is { } l ? $" · {ShortDuration(l)} {Loc.T("left")}" : "";
            sub = d.Progress is { } p ? $"↓ *{d.Name}* · *{p * 100:0}%*{speed}{left}" : $"↓ *{d.Name}* · *{SystemMonitor.Bytes(d.Bytes)}*{speed}";
        }
        else _vSay = null;
        if (_cyber && sub.Length > 0 && !sub.StartsWith("> ")) sub = "> " + sub;
        if (_vSubRaw != sub) { _vSubRaw = sub; Emphasize(_vSubline, sub); }
        if (_vDlTrack != null && _vDlFill != null)
        {
            bool dl = d != null && !saying;
            _vDlTrack.Visibility = dl ? Visibility.Visible : Visibility.Collapsed;
            if (dl)
            {
                double track = Math.Max(0, _vDlTrack.ActualWidth);
                double to = d!.Progress is { } p ? track * p : track * 0.15;
                _vDlFill.BeginAnimation(WidthProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(900)) { EasingFunction = Out });
            }
        }
    }

    string? _vSubRaw;

    static string UpFor(TimeSpan u) => u.TotalDays >= 1 ? $"{(int)u.TotalDays}d {u.Hours}h" : $"{u.Hours}h {u.Minutes:00}m";

    /// <summary>Marks the first place each of these appears (for <see cref="Emphasize"/>), whatever the language's word order.</summary>
    static string Mark(string text, params string[] what)
    {
        foreach (var w in what)
        {
            int i = w.Length > 0 ? text.IndexOf(w, StringComparison.Ordinal) : -1;
            if (i >= 0) text = text[..i] + "*" + w + "*" + text[(i + w.Length)..];
        }
        return text;
    }

    /// <summary>Text with *marked* parts: those are white and bold, the rest stays soft. The eye lands on what matters.</summary>
    static void Emphasize(TextBlock tb, string text)
    {
        tb.Inlines.Clear();
        var parts = text.Split('*');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            var run = new System.Windows.Documents.Run(parts[i]);
            if (i % 2 == 1) { run.Foreground = Brushes.White; run.FontWeight = FontWeights.SemiBold; }
            tb.Inlines.Add(run);
        }
    }

    (string Head, string Sub, Color Color) Verdict()
    {
        var s = _vSnap;
        var r = _vRead;
        var c = _vConn;
        if (s == null) return (Loc.T("Checking your PC…"), "", CallsLook().Label);
        var byCpu = _vAppList.OrderByDescending(a => a.Cpu).FirstOrDefault();
        var byGpu = _vAppList.OrderByDescending(a => a.Gpu).FirstOrDefault();
        var byRam = _vAppList.OrderByDescending(a => a.MemoryMb).FirstOrDefault();
        string Gb(long mb) => mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb} MB";

        if (c is { Online: false }) return (Loc.T("No internet"), c.IsWifi ? string.Format(Loc.T("Connected to {0}, but nothing gets through"), $"*{c.Name}*") : Loc.T("Check the cable or the router"), Hot);
        if (r?.CpuTemp is >= 95 || r?.GpuTemp is >= 88)
        {
            var parts = new List<string>();
            if (r.CpuTemp is { } ct) parts.Add($"CPU *{ct:0} °C*");
            if (r.GpuTemp is { } gt) parts.Add($"GPU *{gt:0} °C*");
            return (Loc.T("Running hot"), string.Join(" · ", parts) + " · " + Loc.T("give it some air"), Hot);
        }
        if (s.Cpu >= 0.85) return (Loc.T("The CPU is maxed out"), byCpu is { Cpu: >= 5 } ? Mark(string.Format(Loc.T("{0} is using {1}% of it"), byCpu.Display, byCpu.Cpu.ToString("0")), byCpu.Display, byCpu.Cpu.ToString("0") + "%") : Loc.T("Many things at once"), Hot);
        if (r is { GpuKnown: true, Gpu: >= 95 }) return (Loc.T("The GPU is maxed out"), byGpu is { Gpu: >= 5 } ? Mark(string.Format(Loc.T("{0} is using {1}% of it"), byGpu.Display, byGpu.Gpu.ToString("0")), byGpu.Display, byGpu.Gpu.ToString("0") + "%") : Loc.T("A game or a video at full tilt"), Warm);
        if (s.Memory >= 0.9) return (Loc.T("Memory is almost full"), byRam != null ? string.Format(Loc.T("{0} takes {1}"), $"*{byRam.Display}*", $"*{Gb(byRam.MemoryMb)}*") : "", Hot);
        if (s.Disk >= 0.95) return (Loc.T("Your disk is almost full"), string.Format(Loc.T("Only {0} left"), $"*{SystemMonitor.Bytes(s.DiskFree)}*"), Hot);
        if (_power.HasBattery && !_power.PluggedIn && _power.Percent <= 15) return (Loc.T("Battery low"), _vBat?.TimeLeft is { } tl ? string.Format(Loc.T("About {0} left · plug in soon"), $"*{SystemMonitor.Duration(tl)}*") : Loc.T("Plug in soon"), Hot);
        if (c is { Online: true } && (c.TypicalMs is >= 150 || c.JitterMs is >= 50)) return (Loc.T("Your connection is laggy"), Mark(string.Format(Loc.T("{0} ms ping · calls and games may stutter"), c.TypicalMs ?? c.PingMs), $"{c.TypicalMs ?? c.PingMs} ms"), Warm);
        if (s.Cpu >= 0.6) return (Loc.T("Working hard"), byCpu is { Cpu: >= 5 } ? Busiest(byCpu) : Loc.T("Lots going on"), Warm);
        if (s.Memory >= 0.8) return (Loc.T("Memory is filling up"), byRam != null ? string.Format(Loc.T("{0} takes {1}"), $"*{byRam.Display}*", $"*{Gb(byRam.MemoryMb)}*") : "", Warm);
        string calm = _cyber ? "SYSTEMS NOMINAL" : Loc.T("Running smoothly");
        // calm: an app only worth naming when it's really doing something; otherwise the PC's headroom at a glance
        if (byCpu is { Cpu: >= 10 }) return (calm, Busiest(byCpu), Calm);
        // the tiles already show the numbers: the line only says how long the PC has been on
        return (calm, string.Format(Loc.T("On for {0}"), $"*{UpFor(s.Uptime)}*"), Calm);

        static string Busiest(AppLoad.App a) => string.Format(Loc.T("{0} is the busiest · {1} CPU"), $"*{a.Display}*", $"*{a.Cpu:0}%*");
    }
}
