using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Preferences (right-click the tray icon): every setting of the notch in one calm window.
///  • a sidebar with the sections (General, Appearance, Behaviour, Alerts, Pages, Shortcuts, About) and a search
///    box that finds any switch by name or description, whatever section it lives in;
///  • Appearance shows both themes as live miniature previews, and a colour mixer (wheel, brightness, the four
///    things to paint, presets, shuffle) that paints the real notch while you drag;
///  • Pages lets you hide the notch pages you don't use; Language switches the notch and this window between
///    English, French and Arabic (right to left);
///  • every change applies and saves at once ("Saved" flashes), the notch's own switches follow.
/// Built only when opened; on Windows 11 it sits on Mica, the system's own translucent material. Nothing runs
/// while it's closed.
/// </summary>
public sealed class PreferencesWindow : Window
{
    static PreferencesWindow? _open;

    public static void ShowFor(NotchWindow notch, int section = -1)
    {
        if (section >= 0 && _open == null) { _open = new PreferencesWindow(notch) { _sec = (Sec)section }; _open.Closed += (_, _) => _open = null; _open.RenderContent(); _open.RenderNav(); _open.Show(); return; }
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }
        _open = new PreferencesWindow(notch);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    enum Sec { General, Appearance, Behaviour, Alerts, Pages, Shortcuts, About }
    static readonly (Sec Sec, string Glyph, string Name, string Sub)[] Sections =
    {
        (Sec.General, "", "General", "Language, start-up and power"),
        (Sec.Appearance, "", "Appearance", "Theme, colours and how the notch looks"),
        (Sec.Behaviour, "", "Behaviour", "How the notch reacts to you"),
        (Sec.Alerts, "", "Alerts", "What the notch tells you, and the smart helpers"),
        (Sec.Pages, "", "Pages", "Choose the pages of the control center"),
        (Sec.Shortcuts, "", "Shortcuts", "Keys and gestures"),
        (Sec.About, "", "About", "Version, files and reset"),
    };

    sealed record Row(Sec Sec, string Glyph, string Title, string Desc, Func<bool> Get, Action<bool> Set, string? Group = null);

    readonly NotchWindow _notch;
    readonly Settings _s;
    Sec _sec = Sec.General;
    string _query = "";
    bool _mica;
    readonly StackPanel _nav = new();
    readonly StackPanel _content = new() { Margin = new Thickness(34, 26, 34, 34) };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, PanningMode = PanningMode.VerticalOnly };
    TextBlock _saved = null!;
    TextBox _search = null!;
    int _paint;
    double _h, _sat, _v = 1;

    static readonly Brush Text = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF5));
    static readonly Brush Dim = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    static readonly Brush Faint = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    static readonly Brush Edge = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));

    Brush CardBrush => Frozen(_mica ? Color.FromArgb(0x66, 0x1E, 0x1E, 0x24) : Color.FromRgb(0x1B, 0x1B, 0x20));
    Color AccentColor => Hex(_s.ColorAccent) ?? _s.Theme switch
    {
        "cyberpunk" => Color.FromRgb(0xFE, 0xE8, 0x01), "liquid" => Color.FromRgb(0x7F, 0xE7, 0xD9),
        "crystal" => Color.FromRgb(0xC8, 0xD6, 0xF0), "abyss" => Color.FromRgb(0x2E, 0xE6, 0xD6), _ => Color.FromRgb(0x0A, 0x84, 0xFF),
    };
    Brush Accent => Frozen(AccentColor);

    PreferencesWindow(NotchWindow notch)
    {
        _notch = notch;
        _s = notch.PrefSettings;
        Title = "Dynamic Notch · " + Loc.T("Preferences");
        Width = 880;
        Height = 600;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Frozen(Color.FromRgb(0x10, 0x10, 0x13));
        Foreground = Text;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SourceInitialized += (_, _) => Chrome();
        Resources[typeof(ScrollBar)] = ScrollStyle();
        _scroll.PreviewMouseWheel += SmoothWheel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { if (_query.Length > 0) _search.Text = ""; else Close(); }
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); e.Handled = true; }
        };
        Build();
    }

    // ───────────── window chrome: dark title bar, rounded corners, Mica on Windows 11 ─────────────

    void Chrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1, round = 2;
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);      // dark title bar
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);   // rounded corners
        if (Environment.OSVersion.Version.Build < 22621) return;
        int mica = 2;
        if (DwmSetWindowAttribute(hwnd, 38, ref mica, 4) != 0) return;
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
        if (HwndSource.FromHwnd(hwnd) is { } src) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        Background = Brushes.Transparent;
        _mica = true;
        Build();
    }

    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

    // ───────────── layout ─────────────

    void Build()
    {
        // rebuilt on a language / theme change: the long-lived parts leave their old places first
        if (_scroll.Parent is Panel oldRight) oldRight.Children.Remove(_scroll);
        if (_nav.Parent is ScrollViewer oldNav) oldNav.Content = null;
        FlowDirection = Loc.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Title = "Dynamic Notch · " + Loc.T("Preferences");
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // sidebar
        var side = new Grid { Background = Frozen(_mica ? Color.FromArgb(0x40, 0x0A, 0x0A, 0x0D) : Color.FromRgb(0x15, 0x15, 0x19)) };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition());
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.Children.Add(Brand());
        _search = new TextBox
        {
            Margin = new Thickness(16, 6, 16, 10), Padding = new Thickness(30, 7, 10, 7), Background = Faint, Foreground = Text,
            BorderBrush = Edge, BorderThickness = new Thickness(1), CaretBrush = Text, FontSize = 12.5, Text = _query,
        };
        _search.Template = SearchTemplate();
        _search.TextChanged += (_, _) => { _query = _search.Text.Trim(); RenderContent(); RenderNav(); };
        Grid.SetRow(_search, 1);
        side.Children.Add(_search);
        var navScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _nav };
        Grid.SetRow(navScroll, 2);
        side.Children.Add(navScroll);
        var open = PillButton("", Loc.T("Open the control center"), () => { if (_notch.IsLocked) _notch.SetLocked(false); _notch.Expand(byHotkey: true); }, primary: true);
        open.Margin = new Thickness(16, 8, 16, 18);
        open.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(open, 3);
        side.Children.Add(open);
        root.Children.Add(side);

        // content
        _scroll.Content = _content;
        var right = new Grid();
        right.Children.Add(_scroll);
        _saved = new TextBlock
        {
            Text = "  " + Loc.T("Saved"), FontSize = 12, Foreground = Frozen(Color.FromRgb(0x30, 0xD1, 0x58)), Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 30, 36, 0),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Variable Text"), IsHitTestVisible = false,
        };
        right.Children.Add(_saved);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        Content = root;
        RenderNav();
        RenderContent();
    }

    FrameworkElement Brand()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 24, 16, 14) };
        panel.Children.Add(MiniNotch(46, 14, _s.Cyberpunk));
        var names = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "Dynamic Notch", FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") });
        names.Children.Add(new TextBlock { Text = Loc.T("Preferences") + " · " + Version, FontSize = 11, Foreground = Dim });
        panel.Children.Add(names);
        return panel;
    }

    static string Version => typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>The notch in miniature: its shape in the current theme, a live bar and its camera dot, the rim breathing in the accent colour.</summary>
    FrameworkElement MiniNotch(double w, double h, bool cyber)
    {
        double r = h / 2, pad = 10;
        var host = new Canvas { Width = w + pad * 2, Height = h + pad * 2, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        var shape = new Path
        {
            Data = Geometry.Parse(cyber
                ? $"M0,0 L{w},0 L{w},{h - r * 0.9} L{w - r * 0.9},{h} L{w * 0.62 + 2},{h} L{w * 0.62},{h + 2} L{r * 0.5},{h + 2} L0,{h - r * 0.5 + 2} Z"
                : $"M0,0 L{w},0 L{w},{h - r} A{r},{r} 0 0 1 {w - r},{h} L{r},{h} A{r},{r} 0 0 1 0,{h - r} Z"),
            Fill = Brushes.Black, Stroke = Accent, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
            Effect = new DropShadowEffect { Color = AccentColor, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.7 },
        };
        Canvas.SetLeft(shape, pad);
        Canvas.SetTop(shape, 2);
        host.Children.Add(shape);
        // a live activity's bar on the left, the camera dot on the right
        var bar = new Rectangle { Width = w * 0.3, Height = Math.Max(1.5, h * 0.12), RadiusX = cyber ? 0 : 1, RadiusY = cyber ? 0 : 1, Fill = Accent };
        Canvas.SetLeft(bar, pad + w * 0.16);
        Canvas.SetTop(bar, 2 + h * 0.46);
        host.Children.Add(bar);
        var dot = new Ellipse { Width = h * 0.34, Height = h * 0.34, Fill = Frozen(Color.FromRgb(0x22, 0x22, 0x2A)), Stroke = Frozen(Color.FromRgb(0x3A, 0x3A, 0x48)), StrokeThickness = 0.8 };
        Canvas.SetLeft(dot, pad + w * 0.74);
        Canvas.SetTop(dot, 2 + h * 0.3);
        host.Children.Add(dot);
        // a still glow: animating an effect would redraw the whole (translucent) window every frame
        return host;
    }

    void RenderNav()
    {
        _nav.Children.Clear();
        foreach (var (sec, glyph, name, _) in Sections)
        {
            bool on = _query.Length == 0 && sec == _sec;
            var item = new Grid { Height = 38, Margin = new Thickness(10, 1, 10, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var bg = new Border { CornerRadius = new CornerRadius(8), Background = on ? Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent };
            item.Children.Add(bg);
            item.Children.Add(new Border
            {
                Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Background = Accent, HorizontalAlignment = HorizontalAlignment.Left,
                Opacity = on ? 1 : 0, Margin = new Thickness(0, 0, 0, 0),
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            row.Children.Add(Glyph(glyph, 15, on ? Accent : Text));
            row.Children.Add(new TextBlock { Text = Loc.T(name), Margin = new Thickness(12, 0, 0, 0), FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(row);
            item.MouseEnter += (_, _) => { if (!on) bg.Background = Frozen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)); };
            item.MouseLeave += (_, _) => { if (!on) bg.Background = Brushes.Transparent; };
            item.MouseLeftButtonUp += (_, _) =>
            {
                _sec = sec;
                if (_query.Length > 0) _search.Text = ""; else { RenderNav(); RenderContent(); }
            };
            _nav.Children.Add(item);
        }
    }

    void RenderContent()
    {
        _content.Children.Clear();
        if (_query.Length > 0) { RenderSearch(); Enter(); return; }
        var (_, _, name, sub) = Sections.First(x => x.Sec == _sec);
        Header(Loc.T(name), Loc.T(sub));
        switch (_sec)
        {
            case Sec.General: General(); break;
            case Sec.Appearance: Appearance(); break;
            case Sec.Behaviour: Card(Rows().Where(r => r.Sec == Sec.Behaviour)); break;
            case Sec.Alerts: Alerts(); break;
            case Sec.Pages: Pages(); break;
            case Sec.Shortcuts: Shortcuts(); break;
            case Sec.About: About(); break;
        }
        BeginAnimation(SmoothOffsetProperty, null);
        _scrollAnimating = false;
        _scroll.ScrollToTop();
        Enter();
    }

    /// <summary>A section slides in.</summary>
    void Enter()
    {
        var shift = new TranslateTransform();
        _content.RenderTransform = shift;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        _content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    void Header(string title, string sub)
    {
        _content.Children.Add(new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") });
        _content.Children.Add(new TextBlock { Text = sub, Foreground = Dim, Margin = new Thickness(0, 4, 0, 22) });
    }

    void Label(string text) => _content.Children.Add(new TextBlock
    {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Dim, Margin = new Thickness(4, 18, 0, 8),
    });

    // ───────────── the switches ─────────────

    List<Row> Rows() => new()
    {
        new(Sec.General, "", "Start with Windows", "Opens by itself when you sign in", () => _s.StartWithWindows, v => { _s.StartWithWindows = v; KeepAlive.Sync(_s); }),
        new(Sec.General, "", "Always keep running", "Comes back after a crash, a sleep or a lock", () => _s.KeepRunning, v => { _s.KeepRunning = v; KeepAlive.Sync(_s); }),
        new(Sec.General, "", "Battery mode", "Fewer animations and checks when unplugged", () => _s.EcoOnBattery, v => _s.EcoOnBattery = v),

        new(Sec.Appearance, "", "Minimal when idle", "A thin bar when nothing is happening", () => _s.MinimalWhenIdle, v => _s.MinimalWhenIdle = v),
        new(Sec.Appearance, "", "Slim over windows", "Stays slim over browser tabs and windows", () => _s.SlimOverWindows, v => _s.SlimOverWindows = v),
        new(Sec.Appearance, "", "Music activity", "Artwork and a visualizer while music plays", () => _s.ShowMediaActivity, v => _s.ShowMediaActivity = v),
        new(Sec.Appearance, "", "Music visualizer", "Bars that move with the music", () => _s.AnimatedVisualizer, v => _s.AnimatedVisualizer = v),

        new(Sec.Behaviour, "", "Open at top edge", "Push the pointer to the top of the screen to open it", () => _s.HoverToOpen, v => _s.HoverToOpen = v),
        new(Sec.Behaviour, "", "Drop files on the notch", "Keep files on the shelf, drag them out anywhere", () => _s.ShelfDrop, v => _s.ShelfDrop = v),
        new(Sec.Behaviour, "", "Hide in full screen", "Out of the way in videos and full-screen apps", () => _s.HideInFullscreen, v => _s.HideInFullscreen = v),
        new(Sec.Behaviour, "", "FPS in games", "A tiny frame-rate pill while you play", () => _s.GameStats && FpsService.AccountCanTrace(), v => _notch.PrefSetFps(v)),
        new(Sec.Behaviour, "", "Lock the notch", "It shows things but reacts to nothing", () => _s.Locked, v => _notch.SetLocked(v)),

        new(Sec.Alerts, "", "Notification alerts", "Messages and app notifications", () => _s.NotificationAlerts, v => _s.NotificationAlerts = v),
        new(Sec.Alerts, "", "System alerts", "Volume, brightness, battery, devices", () => _s.SystemAlerts, v => _s.SystemAlerts = v),
        new(Sec.Alerts, "", "App launch alerts", "\"Opening Spotify\" when an app starts", () => _s.AppLaunchAlerts, v => _s.AppLaunchAlerts = v),
        new(Sec.Alerts, "", "“Copied” alerts", "A nod when you copy something", () => _s.ClipboardAlerts, v => _s.ClipboardAlerts = v),
        new(Sec.Alerts, "", "Battery drain", "Spots apps eating power", () => _s.SmartBatteryDrain, v => _s.SmartBatteryDrain = v, "Smart"),
        new(Sec.Alerts, "", "Bad connection", "Shaky Wi-Fi before your calls", () => _s.SmartConnection, v => _s.SmartConnection = v, "Smart"),
        new(Sec.Alerts, "", "Charge reminder", "When ~45 min are left", () => _s.SmartChargeReminder, v => _s.SmartChargeReminder = v, "Smart"),
        new(Sec.Alerts, "", "Downloads tidy-up", "Weekly, if it's over 5 GB", () => _s.SmartDownloads, v => _s.SmartDownloads = v, "Smart"),
        new(Sec.Alerts, "", "Break reminder", "After long stretches of work", () => _s.SmartBreaks, v => _notch.PrefSetBreaks(v), "Smart"),
        new(Sec.Alerts, "", "Meeting mode", "Camera on: pause music, quiet", () => _s.MeetingMode, v => _notch.PrefSetMeeting(v), "Smart"),

        new(Sec.Pages, "", "System", "CPU, memory, storage, network and battery", () => !_s.HiddenPages.Contains(1), v => _notch.PrefSetPage(1, v)),
        new(Sec.Pages, "", "Tools", "Timer, snip, terminal, calculator and more", () => !_s.HiddenPages.Contains(2), v => _notch.PrefSetPage(2, v)),
        new(Sec.Pages, "", "Clipboard & Files", "Copy history, the shelf and recent files", () => !_s.HiddenPages.Contains(3), v => _notch.PrefSetPage(3, v)),
        new(Sec.Pages, "", "Colors", "The colour studio of the notch", () => !_s.HiddenPages.Contains(4), v => _notch.PrefSetPage(4, v)),
    };

    void Card(IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        var stack = new StackPanel();
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = Edge, Margin = new Thickness(54, 0, 0, 0) });
            stack.Children.Add(RowView(list[i]));
        }
        _content.Children.Add(CardOf(stack));
    }

    Border CardOf(UIElement child, Thickness? padding = null) => new()
    {
        CornerRadius = new CornerRadius(12), Background = CardBrush, BorderBrush = Edge, BorderThickness = new Thickness(1),
        Padding = padding ?? new Thickness(0), Margin = new Thickness(0, 0, 0, 10), Child = child,
    };

    FrameworkElement RowView(Row r)
    {
        var g = new Grid { MinHeight = 62, Background = Brushes.Transparent };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Faint, Child = Glyph(r.Glyph, 14, Text) };
        g.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 16, 10) };
        text.Children.Add(new TextBlock { Text = Loc.T(r.Title), FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = Loc.T(r.Desc), FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var sw = new ToggleButton { Style = (Style)Application.Current.FindResource("Switch"), IsChecked = r.Get(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        sw.Click += (_, _) =>
        {
            r.Set(sw.IsChecked == true);
            _notch.PrefChanged();
            sw.IsChecked = r.Get(); // some switches ask Windows first (FPS): show what really happened
            Saved();
        };
        Grid.SetColumn(sw, 2);
        g.Children.Add(sw);
        // the whole row is the switch's label
        text.Cursor = Cursors.Hand;
        text.Background = Brushes.Transparent;
        text.MouseLeftButtonUp += (_, _) => { sw.IsChecked = !(sw.IsChecked == true); sw.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); };
        return g;
    }

    void Saved()
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1600) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600))));
        _saved.BeginAnimation(OpacityProperty, a);
    }

    void RenderSearch()
    {
        Header(Loc.T("Search"), string.Format(Loc.T("Settings matching “{0}”"), _query));
        var hits = Rows().Where(r => Matches(r.Title) || Matches(r.Desc) || Matches(Sections.First(s => s.Sec == r.Sec).Name)).ToList();
        if (hits.Count == 0)
        {
            _content.Children.Add(new TextBlock { Text = Loc.T("Nothing found. Try another word."), Foreground = Dim, Margin = new Thickness(4, 10, 0, 0) });
            return;
        }
        foreach (var group in hits.GroupBy(r => r.Sec))
        {
            Label(Loc.T(Sections.First(s => s.Sec == group.Key).Name));
            Card(group);
        }
    }

    bool Matches(string en) =>
        en.Contains(_query, StringComparison.OrdinalIgnoreCase) || Loc.T(en).Contains(_query, StringComparison.OrdinalIgnoreCase);

    // ───────────── sections ─────────────

    void General()
    {
        Label(Loc.T("Language"));
        var langs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (code, name, sample) in new[] { ("en", "English", "Hello"), ("fr", "Français", "Bonjour"), ("ar", "العربية", "مرحبا") })
        {
            bool on = Loc.Lang == code;
            var card = new Border
            {
                Width = 150, Height = 66, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand,
                Background = on ? Frozen(Color.FromArgb(0x2A, AccentColor.R, AccentColor.G, AccentColor.B)) : CardBrush,
                BorderBrush = on ? Accent : Edge, BorderThickness = new Thickness(on ? 1.5 : 1),
            };
            var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
            inner.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold });
            inner.Children.Add(new TextBlock { Text = sample, FontSize = 12, Foreground = Dim });
            card.Child = inner;
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (Loc.Lang == code) return;
                _s.Language = code;
                _s.Save();
                Loc.Set(code);
                _notch.PrefRelabel();
                Build();
                Saved();
            };
            langs.Children.Add(card);
        }
        _content.Children.Add(langs);
        _content.Children.Add(new TextBlock
        {
            Text = Loc.T("The notch's pages and this window follow the language; a few alerts stay in English."),
            FontSize = 11.5, Foreground = Dim, Margin = new Thickness(4, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        Label(Loc.T("Start-up and power"));
        Card(Rows().Where(r => r.Sec == Sec.General));
    }

    void Appearance()
    {
        Label(Loc.T("Theme"));
        var themes = new WrapPanel();
        foreach (var key in ThemeKeys) themes.Children.Add(ThemeCard(key));
        _content.Children.Add(themes);

        Label(Loc.T("Colours"));
        _content.Children.Add(Mixer());

        Label(Loc.T("Look"));
        Card(Rows().Where(r => r.Sec == Sec.Appearance));
    }

    static readonly string[] ThemeKeys = { "original", "cyberpunk", "liquid", "crystal", "abyss" };
    static readonly Dictionary<string, string> ThemeTitles = new()
    {
        ["original"] = "Original", ["cyberpunk"] = "Cyberpunk", ["liquid"] = "Liquid glass", ["crystal"] = "Crystal", ["abyss"] = "Abyss",
    };

    /// <summary>A theme, as a little picture of the notch in it.</summary>
    FrameworkElement ThemeCard(string key)
    {
        bool on = _s.Theme == key;
        var stage = new Grid { Height = 92, ClipToBounds = true };
        const double w = 124, h = 32;
        TextBlock Clock() => new() { Text = "12:07", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Border Glass(Brush body, Brush rim, params Brush[] lights)
        {
            var g = new Grid();
            var corner = new CornerRadius(0, 0, h / 2, h / 2);
            g.Children.Add(new Border { CornerRadius = corner, Background = body });
            foreach (var l in lights) g.Children.Add(new Border { CornerRadius = corner, Background = l });
            g.Children.Add(new Border { CornerRadius = corner, BorderBrush = rim, BorderThickness = new Thickness(1.2, 0, 1.2, 1.2) });
            g.Children.Add(Clock());
            return new Border { Width = w, Height = h, VerticalAlignment = VerticalAlignment.Top, Child = g };
        }
        RadialGradientBrush Light(Color c, double x, double y) => new()
        {
            Center = new Point(x, y), GradientOrigin = new Point(x, y), RadiusX = 0.6, RadiusY = 1.1,
            GradientStops = { new GradientStop(c, 0), new GradientStop(Colors.Transparent, 1) },
        };
        LinearGradientBrush V(params (string C, double At)[] stops)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            foreach (var (c, at) in stops) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c), at));
            return g;
        }
        switch (key)
        {
            case "cyberpunk":
                stage.Background = new LinearGradientBrush(Color.FromRgb(0x00, 0x06, 0x0E), Color.FromRgb(0x06, 0x1E, 0x2A), 90);
                stage.Children.Add(new Path
                {
                    Data = Geometry.Parse($"M0,0 L{w},0 L{w},{h - 10} L{w - 10},{h} L{w * 0.62 + 4},{h} L{w * 0.62},{h + 4} L6,{h + 4} L0,{h - 2} Z"),
                    Fill = Frozen(Color.FromRgb(0x00, 0x06, 0x0E)), Stroke = Frozen(Color.FromRgb(0xFE, 0xE8, 0x01)), StrokeThickness = 1.5,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                });
                stage.Children.Add(new TextBlock { Text = "12:07", FontFamily = new FontFamily("Bahnschrift"), FontStretch = FontStretches.SemiCondensed, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(Color.FromRgb(0xFE, 0xE8, 0x01)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 9, 0, 0) });
                break;
            case "liquid":
                stage.Background = new LinearGradientBrush(Color.FromRgb(0x1C, 0x4A, 0x6E), Color.FromRgb(0x4A, 0x2A, 0x6A), 35);
                stage.Children.Add(Glass(V(("#F2080B13", 0), ("#DC101725", 0.5), ("#C41A253A", 1)), V(("#00FFFFFF", 0), ("#90FFFFFF", 1)),
                    Light(Color.FromArgb(0x60, 0x3F, 0xE0, 0xC5), 0.15, 1), Light(Color.FromArgb(0x55, 0x9B, 0x7B, 0xFF), 0.88, 0.95)));
                break;
            case "crystal":
                stage.Background = new LinearGradientBrush(Color.FromRgb(0x5A, 0x7C, 0xB8), Color.FromRgb(0xC0, 0x8C, 0xB8), 20);
                var prism = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                foreach (var (c, at) in new[] { ("#E0FF7AA8", 0.0), ("#E0FFD36B", 0.25), ("#E06BFFC2", 0.5), ("#E06BC8FF", 0.75), ("#E0C27AFF", 1.0) })
                    prism.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c), at));
                stage.Children.Add(Glass(V(("#EC050609", 0), ("#BE0B0E14", 0.5), ("#A612161F", 1)), prism, V(("#40FFFFFF", 0), ("#00FFFFFF", 0.5))));
                break;
            case "abyss":
                stage.Background = new LinearGradientBrush(Color.FromRgb(0x01, 0x06, 0x10), Color.FromRgb(0x03, 0x16, 0x2E), 90);
                stage.Children.Add(Glass(V(("#000000", 0), ("#020B1A", 0.5), ("#041E38", 1)), V(("#002EE6D6", 0), ("#702EE6D6", 1)),
                    Light(Color.FromArgb(0x60, 0x2E, 0xE6, 0xD6), 0.5, 1.1)));
                var rnd = new Random(3);
                for (int i = 0; i < 9; i++)
                    stage.Children.Add(new Ellipse
                    {
                        Width = 3, Height = 3, Fill = Frozen(i % 3 == 0 ? Color.FromRgb(0x7C, 0xF5, 0xFF) : Color.FromRgb(0x2E, 0xE6, 0xD6)), Opacity = 0.4 + rnd.NextDouble() * 0.5,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10 + rnd.NextDouble() * 150, 42 + rnd.NextDouble() * 44, 0, 0),
                    });
                break;
            default:
                stage.Background = new LinearGradientBrush(Color.FromRgb(0x1A, 0x22, 0x3A), Color.FromRgb(0x2E, 0x1F, 0x3E), 30);
                stage.Children.Add(new Border
                {
                    Width = w, Height = h, CornerRadius = new CornerRadius(0, 0, h / 2, h / 2), Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Top,
                    BorderBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1, 0, 1, 1), Child = Clock(),
                });
                break;
        }
        var caption = new Grid { Margin = new Thickness(12, 9, 12, 10) };
        caption.Children.Add(new TextBlock { Text = Loc.T(ThemeTitles[key]), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (on) caption.Children.Add(new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Accent, HorizontalAlignment = HorizontalAlignment.Right,
            Child = Glyph("", 10, Brushes.Black),
        });
        var body = new StackPanel();
        body.Children.Add(new Border { CornerRadius = new CornerRadius(11, 11, 0, 0), Child = stage, ClipToBounds = true });
        body.Children.Add(caption);
        var card = new Border
        {
            Width = 180, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(12), Background = CardBrush, Cursor = Cursors.Hand,
            BorderBrush = on ? Accent : Edge, BorderThickness = new Thickness(on ? 2 : 1), Child = body, ClipToBounds = true,
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (_s.Theme == key) return;
            _notch.SetTheme(key);
            Build();
            Saved();
        };
        return card;
    }

    /// <summary>The colour mixer: the same one as the notch's Colors page, bigger, painting the notch live.</summary>
    FrameworkElement Mixer()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new StackPanel { Margin = new Thickness(18, 18, 0, 18) };
        var wheel = new ColorWheel { Width = 150, Height = 150, FlowDirection = FlowDirection.LeftToRight };
        var bright = new PillSlider { Height = 20, Margin = new Thickness(4, 12, 4, 0), TrackHeight = 7, FlowDirection = FlowDirection.LeftToRight };
        left.Children.Add(wheel);
        left.Children.Add(bright);
        grid.Children.Add(left);

        var right = new StackPanel { Margin = new Thickness(22, 18, 18, 18) };
        Grid.SetColumn(right, 1);
        int count = NotchWindow.PrefPaintCount;
        var targets = new UniformGrid { Columns = count };
        var swatches = new Border[count];
        TextBlock hex = null!, hint = null!;
        Button auto = null!;
        var labels = new TextBlock[count];
        var names = Enumerable.Range(0, count).Select(NotchWindow.PrefPaintName).ToArray();
        var hints = Enumerable.Range(0, count).Select(NotchWindow.PrefPaintHint).ToArray();
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            var cell = new StackPanel { Cursor = Cursors.Hand, Background = Brushes.Transparent };
            swatches[i] = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(2.5) };
            labels[i] = new TextBlock { Text = Loc.T(names[i]), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
            cell.Children.Add(swatches[i]);
            cell.Children.Add(labels[i]);
            cell.MouseLeftButtonUp += (_, _) => { _paint = idx; Load(); };
            targets.Children.Add(cell);
        }
        right.Children.Add(targets);

        var readout = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        readout.ColumnDefinitions.Add(new ColumnDefinition());
        readout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel();
        hex = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = Loc.Rtl ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        hint = new TextBlock { FontSize = 12, Foreground = Dim };
        info.Children.Add(hex);
        info.Children.Add(hint);
        readout.Children.Add(info);
        auto = PillButton(null, Loc.T("Auto"), () => { }, primary: false);
        Grid.SetColumn(auto, 1);
        readout.Children.Add(auto);
        right.Children.Add(readout);

        var presets = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        for (int i = 0; i < NotchWindow.PresetCount; i++)
        {
            int idx = i;
            var cols = NotchWindow.PresetColors(i);
            Brush fill;
            if (cols == null) fill = new LinearGradientBrush(Colors.White, Color.FromRgb(0x3A, 0x3A, 0x3E), 45);
            else
            {
                var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                for (int k = 0; k < 3; k++) { g.GradientStops.Add(new GradientStop(cols[k], k / 3.0)); g.GradientStops.Add(new GradientStop(cols[k], (k + 1) / 3.0)); }
                fill = g;
            }
            var tile = new Border
            {
                Width = 54, Height = 26, CornerRadius = new CornerRadius(8), Background = fill, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                BorderBrush = Edge, BorderThickness = new Thickness(1), ToolTip = Loc.T(NotchWindow.PresetName(i).Split(" · ")[0]),
            };
            if (cols == null) tile.Child = new TextBlock { Text = "AUTO", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tile.MouseLeftButtonUp += (_, _) => { _notch.PrefPreset(idx); Load(); Saved(); };
            presets.Children.Add(tile);
        }
        presets.Children.Add(PillButton("", Loc.T("Shuffle"), () => { _notch.PrefShuffle(); Load(); Saved(); }, primary: false));
        right.Children.Add(presets);
        grid.Children.Add(right);

        void Load()
        {
            var c = _notch.PrefShownColor(_paint);
            var (h, s, v) = ColorWheel.ToHsv(c);
            if (s > 0.01) _h = h;
            _sat = s;
            _v = v;
            bright.Value = v;
            Show();
        }

        void Show()
        {
            var c = _notch.PrefShownColor(_paint);
            bool isAuto = _notch.PrefColor(_paint) == null;
            wheel.SetMarker(_h, _sat, c);
            var mix = new LinearGradientBrush(Colors.Black, ColorWheel.Hsv(_h, _sat, 1), 0);
            mix.Freeze();
            bright.FillBrush = mix;
            hex.Text = (isAuto ? Loc.T("Auto") + " · " : "") + $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            hint.Text = Loc.T(hints[_paint]);
            for (int i = 0; i < count; i++)
            {
                var k = _notch.PrefColor(i) == null ? (Brush?)null : Frozen(_notch.PrefShownColor(i));
                swatches[i].Background = k ?? new LinearGradientBrush(Color.FromRgb(0xFF, 0x5E, 0x5E), Color.FromRgb(0x0A, 0x84, 0xFF), 45);
                swatches[i].BorderBrush = i == _paint ? Text : Edge;
                labels[i].Foreground = i == _paint ? Text : Dim;
                labels[i].FontWeight = i == _paint ? FontWeights.SemiBold : FontWeights.Normal;
            }
            SetPill(auto, isAuto);
        }

        void Paint(bool save)
        {
            var c = ColorWheel.Hsv(_h, _sat, _v);
            _notch.PrefSetColor(_paint, $"#{c.R:X2}{c.G:X2}{c.B:X2}", save);
            Show();
        }

        wheel.Picked += (h, s) => { _h = h; _sat = s; if (_v < 0.2) { _v = 1; bright.Value = 1; } Paint(false); };
        wheel.Committed += () => { Paint(true); Saved(); };
        bright.ValueChanged += v => { _v = Math.Max(0.04, v); Paint(false); };
        bright.ValueCommitted += _ => { Paint(true); Saved(); };
        auto.Click += (_, _) =>
        {
            if (_notch.PrefColor(_paint) == null) Paint(true);
            else _notch.PrefSetColor(_paint, null, true);
            Load();
            Saved();
        };
        grid.Loaded += (_, _) => Load();
        return CardOf(grid);
    }

    void Alerts()
    {
        Label(Loc.T("Alerts"));
        Card(Rows().Where(r => r.Sec == Sec.Alerts && r.Group == null));
        Label(Loc.T("Smart helpers"));
        Card(Rows().Where(r => r.Sec == Sec.Alerts && r.Group == "Smart"));

        var row = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        row.Children.Add(new TextBlock { Text = Loc.T("Break reminder every"), VerticalAlignment = VerticalAlignment.Center });
        var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (int m in new[] { 45, 60, 90, 120 })
        {
            var b = PillButton(null, $"{m} min", () => { }, primary: false);
            b.Margin = new Thickness(6, 0, 0, 0);
            SetPill(b, _s.BreakMinutes == m);
            b.Click += (_, _) =>
            {
                _s.BreakMinutes = m;
                _notch.PrefChanged();
                foreach (var other in chips.Children.OfType<Button>()) SetPill(other, ReferenceEquals(other, b));
                Saved();
            };
            chips.Children.Add(b);
        }
        row.Children.Add(chips);
        _content.Children.Add(CardOf(row));
    }

    void Pages()
    {
        Label(Loc.T("Always there"));
        var fixedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 14, 18, 14) };
        fixedRow.Children.Add(Glyph("", 14, Text));
        fixedRow.Children.Add(new TextBlock { Text = Loc.T("Home and Settings stay, so the notch is always usable"), Margin = new Thickness(12, 0, 0, 0), Foreground = Dim, VerticalAlignment = VerticalAlignment.Center });
        _content.Children.Add(CardOf(fixedRow));
        Label(Loc.T("Shown in the control center"));
        Card(Rows().Where(r => r.Sec == Sec.Pages));
    }

    void Shortcuts()
    {
        var list = new StackPanel();
        var items = new (string[] Keys, string What)[]
        {
            (new[] { "Win", "Alt", "N" }, "Open or close the control center"),
            (new[] { "Shift", "Scroll" }, "Change page"),
            (new[] { "Two-finger swipe" }, "Change page on a touchpad"),
            (new[] { "Esc" }, "Close the control center"),
            (new[] { "Drag a file", "Notch" }, "Keep it on the shelf"),
            (new[] { "Swipe down" }, "Open it with a finger (touch screens)"),
            (new[] { "Click", "Tray icon" }, "Unlock a locked notch"),
            (new[] { "Right-click", "Tray icon" }, "This window"),
        };
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) list.Children.Add(new Border { Height = 1, Background = Edge, Margin = new Thickness(18, 0, 18, 0) });
            var g = new Grid { Margin = new Thickness(18, 13, 18, 13) };
            g.Children.Add(new TextBlock { Text = Loc.T(items[i].What), VerticalAlignment = VerticalAlignment.Center });
            var keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, FlowDirection = FlowDirection.LeftToRight };
            for (int k = 0; k < items[i].Keys.Length; k++)
            {
                if (k > 0) keys.Children.Add(new TextBlock { Text = "+", Foreground = Dim, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                keys.Children.Add(Keycap(Loc.T(items[i].Keys[k])));
            }
            g.Children.Add(keys);
            list.Children.Add(g);
        }
        _content.Children.Add(CardOf(list));
    }

    static FrameworkElement Keycap(string text) => new Border
    {
        MinWidth = 28, Height = 26, Padding = new Thickness(9, 0, 9, 0), CornerRadius = new CornerRadius(6),
        Background = Frozen(Color.FromRgb(0x2B, 0x2B, 0x31)), BorderBrush = Frozen(Color.FromRgb(0x18, 0x18, 0x1C)), BorderThickness = new Thickness(1, 1, 1, 3),
        Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
    };

    void About()
    {
        var hero = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 22) };
        var mark = MiniNotch(120, 30, _s.Cyberpunk);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        hero.Children.Add(mark);
        hero.Children.Add(new TextBlock { Text = "Dynamic Notch", FontSize = 24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") });
        hero.Children.Add(new TextBlock { Text = Loc.T("Version") + " " + Version, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        hero.Children.Add(new TextBlock { Text = Loc.T("A living island at the top of your screen."), Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        _content.Children.Add(CardOf(hero));

        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(PillButton("", Loc.T("Open the settings folder"), () => Launch(Settings.Folder), primary: false));
        actions.Children.Add(PillButton("", Loc.T("Releases on GitHub"), () => Launch("https://github.com/Prism99-collab/DynamicNotch/releases"), primary: false));
        var reset = PillButton("", Loc.T("Reset all preferences"), () =>
        {
            if (MessageBox.Show(this, Loc.T("Every preference goes back to how it was at install, and the notch restarts."), Loc.T("Reset all preferences"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            var fresh = new Settings { Welcomed = true };
            fresh.Save();
            AppLog.Write("preferences reset by user");
            ((App)Application.Current).Restart();
        }, primary: false);
        reset.Foreground = Frozen(Color.FromRgb(0xFF, 0x69, 0x61));
        actions.Children.Add(reset);
        foreach (FrameworkElement a in actions.Children) a.Margin = new Thickness(0, 0, 10, 10);
        _content.Children.Add(actions);
    }

    static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    // ───────────── scrolling: a slim pill of a scrollbar, and the wheel glides ─────────────

    /// <summary>
    /// A thin rounded thumb floating on nothing: barely there at rest, it widens and brightens under the pointer and
    /// takes the accent colour while you drag it.
    /// </summary>
    Style ScrollStyle()
    {
        var a = AccentColor;
        string accent = $"#{a.R:X2}{a.G:X2}{a.B:X2}";
        string xaml = $@"
<Style TargetType='ScrollBar' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Setter Property='Width' Value='12' />
  <Setter Property='MinWidth' Value='12' />
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Grid Background='Transparent'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Border x:Name='T' Width='4' CornerRadius='2' Margin='0,6,3,6' HorizontalAlignment='Right' Background='#38FFFFFF' />
                    <ControlTemplate.Triggers>
                      <Trigger Property='IsMouseOver' Value='True'>
                        <Setter TargetName='T' Property='Width' Value='7' />
                        <Setter TargetName='T' Property='CornerRadius' Value='3.5' />
                        <Setter TargetName='T' Property='Background' Value='#80FFFFFF' />
                      </Trigger>
                      <Trigger Property='IsDragging' Value='True'>
                        <Setter TargetName='T' Property='Width' Value='7' />
                        <Setter TargetName='T' Property='CornerRadius' Value='3.5' />
                        <Setter TargetName='T' Property='Background' Value='{accent}' />
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    static readonly DependencyProperty SmoothOffsetProperty = DependencyProperty.Register("SmoothOffset", typeof(double), typeof(PreferencesWindow),
        new PropertyMetadata(0.0, (d, e) => ((PreferencesWindow)d)._scroll.ScrollToVerticalOffset((double)e.NewValue)));
    double _scrollTarget;
    bool _scrollAnimating;

    /// <summary>The wheel glides to where it's going (sliders and the colour wheel keep their own wheel).</summary>
    void SmoothWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && (FindUp<PillSlider>(src) || FindUp<ColorWheel>(src))) return;
        e.Handled = true;
        if (!_scrollAnimating) _scrollTarget = _scroll.VerticalOffset;
        _scrollTarget = Math.Clamp(_scrollTarget - e.Delta * 0.9, 0, _scroll.ScrollableHeight);
        var glide = new DoubleAnimation(_scroll.VerticalOffset, _scrollTarget, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        glide.Completed += (_, _) => _scrollAnimating = false;
        _scrollAnimating = true;
        BeginAnimation(SmoothOffsetProperty, glide);
    }

    static bool FindUp<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null) { if (d is T) return true; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d); }
        return false;
    }

    // ───────────── small pieces ─────────────

    static TextBlock Glyph(string glyph, double size, Brush brush) => new()
    {
        Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight,
    };

    Button PillButton(string? glyph, string text, Action act, bool primary)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (glyph != null) row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = row, Height = 34, Padding = new Thickness(16, 0, 16, 0), FontSize = 12.5, Cursor = Cursors.Hand,
            Foreground = primary ? Brushes.Black : Text, Background = primary ? Accent : Faint, Template = PillTemplate(),
        };
        b.Click += (_, _) => act();
        return b;
    }

    void SetPill(Button b, bool on)
    {
        b.Background = on ? Accent : Faint;
        b.Foreground = on ? Brushes.Black : Text;
    }

    static ControlTemplate PillTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, Edge);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.85, "Bd"));
        t.Triggers.Add(hover);
        var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(OpacityProperty, 0.65, "Bd"));
        t.Triggers.Add(press);
        return t;
    }

    ControlTemplate SearchTemplate()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.TextProperty, "");
        icon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        icon.SetValue(TextBlock.FontSizeProperty, 12.0);
        icon.SetValue(TextBlock.ForegroundProperty, Dim);
        icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(MarginProperty, new Thickness(11, 0, 0, 0));
        icon.SetValue(IsHitTestVisibleProperty, false);
        grid.AppendChild(icon);
        var hint = new FrameworkElementFactory(typeof(TextBlock));
        hint.SetValue(TextBlock.TextProperty, Loc.T("Search settings") + "   Ctrl+F");
        hint.SetValue(TextBlock.ForegroundProperty, Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)));
        hint.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        hint.SetValue(MarginProperty, new Thickness(31, 0, 0, 0));
        hint.SetValue(IsHitTestVisibleProperty, false);
        hint.Name = "Hint";
        grid.AppendChild(hint);
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(MarginProperty, new Thickness(26, 0, 6, 0));
        host.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(host);
        bd.AppendChild(grid);
        t.VisualTree = bd;
        var typed = new Trigger { Property = TextBox.TextProperty, Value = "" };
        typed.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible, "Hint"));
        t.Triggers.Add(typed);
        var notEmpty = new Trigger { Property = TextBox.IsKeyboardFocusedProperty, Value = true };
        notEmpty.Setters.Add(new Setter(Border.BorderBrushProperty, Accent));
        t.Triggers.Add(notEmpty);
        // the hint shows only while the box is empty
        hint.SetValue(VisibilityProperty, Visibility.Collapsed);
        return t;
    }

    static Color? Hex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return (Color)ColorConverter.ConvertFromString(s); } catch { return null; }
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
