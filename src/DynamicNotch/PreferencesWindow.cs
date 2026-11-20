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

    enum Sec { General, Appearance, Behaviour, Alerts, Pages, Shortcuts, About, Search, Calls, Ai }

    /// <summary>The AI chat's section (for the chat's header to open it directly).</summary>
    public const int AiSection = (int)Sec.Ai;
    static readonly (Sec Sec, string Glyph, string Name, string Sub)[] Sections =
    {
        (Sec.General, "", "General", "Language, start-up and power"),
        (Sec.Appearance, "", "Appearance", "Theme, colours and how the notch looks"),
        (Sec.Behaviour, "", "Behaviour", "How the notch reacts to you"),
        (Sec.Search, "\uE721", "Search", "What it finds, how it opens, what it remembers"),
        (Sec.Ai, "\uE8BD", "AI chat", "The assistant in the search: key, model, how it answers, what it may do"),
        (Sec.Calls, "\uE717", "Calls", "The Calls page, and what happens when a call starts"),
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
        "neon" => Color.FromRgb(0xFE, 0xE8, 0x01), "liquid" => Color.FromRgb(0x7F, 0xE7, 0xD9),
        "crystal" => Color.FromRgb(0xC8, 0xD6, 0xF0), "abyss" => Color.FromRgb(0x2E, 0xE6, 0xD6), _ => Color.FromRgb(0x0A, 0x84, 0xFF),
    };
    Brush Accent => Frozen(AccentColor);

    PreferencesWindow(NotchWindow notch)
    {
        _notch = notch;
        _s = notch.PrefSettings;
        Title = "Pixo · " + Loc.T("Preferences");
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
        Title = "Pixo · " + Loc.T("Preferences");
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

    /// <summary>Pixo's logo, at a size.</summary>
    static FrameworkElement PixoLogo(double size)
    {
        var img = new System.Windows.Controls.Image { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        try { img.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")); } catch { }
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    FrameworkElement Brand()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 24, 16, 14) };
        panel.Children.Add(PixoLogo(40));
        var names = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "Pixo", FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") });
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
            case Sec.Behaviour: Behaviour(); break;
            case Sec.Alerts: Alerts(); break;
            case Sec.Pages: Pages(); break;
            case Sec.Shortcuts: Shortcuts(); break;
            case Sec.Search: SearchSection(); break;
            case Sec.Ai: AiSectionView(); break;
            case Sec.Calls: CallsSection(); break;
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
        new(Sec.General, "", "Battery mode", "Lighter animations while Windows battery saver is on, fewer checks when unplugged", () => _s.EcoOnBattery, v => _s.EcoOnBattery = v),

        new(Sec.Appearance, "", "Companion", "A little hacker drops by on her swing, and leaves when the pointer comes near", () => _s.Companion, v => _s.Companion = v),
        new(Sec.Appearance, "", "Companion during calls", "She also drops by while you are on a call", () => _s.CompanionInCalls, v => _s.CompanionInCalls = v),
        new(Sec.Appearance, "\uE76E", "Face in the notch", "A little face lives in the idle notch: it blinks, looks at the pointer and reacts to what happens. Click it to poke it, right-click for another hat", () => _s.NotchFace, v => _s.NotchFace = v),
        new(Sec.Appearance, "\uE73F", "Compact when resting", "Unused for 5 seconds, the notch tucks in small; it grows back when the pointer comes near", () => _s.CompactWhenResting, v => _s.CompactWhenResting = v),
        new(Sec.Appearance, "\uE767", "Face sounds", "Tiny sounds of its own: a hello, a boing when you poke it, a chime when an answer is done", () => _s.FaceSounds, v => _s.FaceSounds = v),
        new(Sec.Appearance, "\uE8A0", "Tuck pills into the notch", "The side pills show as a small light in the notch, and slide out when they start or when the pointer comes near", () => _s.TuckPills, v => _s.TuckPills = v),
        new(Sec.Appearance, "", "Minimal when idle", "A thin bar when nothing is happening", () => _s.MinimalWhenIdle, v => _s.MinimalWhenIdle = v),
        new(Sec.Appearance, "", "Slim over windows", "Stays slim over browser tabs and windows", () => _s.SlimOverWindows, v => _s.SlimOverWindows = v),
        new(Sec.Appearance, "", "Music activity", "Artwork and a visualizer while music plays", () => _s.ShowMediaActivity, v => _s.ShowMediaActivity = v),
        new(Sec.Appearance, "", "Music visualizer", "Bars that move with the music", () => _s.AnimatedVisualizer, v => _s.AnimatedVisualizer = v),

        new(Sec.Behaviour, "", "Open at top edge", "Push the pointer to the top of the screen to open it", () => _s.HoverToOpen, v => _s.HoverToOpen = v),
        new(Sec.Behaviour, "", "Drop files on the notch", "Keep files on the shelf, drag them out anywhere", () => _s.ShelfDrop, v => _s.ShelfDrop = v),
        new(Sec.Behaviour, "", "Hide in full screen", "Out of the way in videos and full-screen apps", () => _s.HideInFullscreen, v => _s.HideInFullscreen = v),
        new(Sec.Behaviour, "", "FPS in games", "A tiny frame-rate pill while you play", () => _s.GameStats, v => _notch.PrefSetFps(v)),
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
        new(Sec.Alerts, "\uE7BA", "Overload warnings", "CPU, GPU, memory and heat watched; warns when one stays high", () => _notch.PrefStrainOn("all"), v => _notch.PrefSetStrain("all", v), "Strain"),

        new(Sec.Ai, "\uE8BD", "AI chat in the search", "“Ask AI” in the results, Shift+Enter on what you typed, Tab to go back to a chat", () => _s.AiChat, v => _s.AiChat = v, "ai"),
        new(Sec.Ai, "\uE945", "Let it act on your PC", "The notch's commands, files, the clipboard, opening apps and pages", () => _s.AiTools, v => _s.AiTools = v, "ai"),
        new(Sec.Ai, "\uE774", "Let it use the web", "Searches and reads pages for current answers", () => _s.AiWeb, v => _s.AiWeb = v, "ai"),
        new(Sec.Ai, "\uE81C", "Keep your chats", "Saved on this PC so you can go back to them (Ctrl+H in the chat)", () => _s.AiHistory, v => _s.AiHistory = v, "ai"),
        new(Sec.Ai, "\uE8EE", "Go on with the last chat", "Asking from the search continues a chat used in the last 30 minutes", () => _s.AiContinue, v => _s.AiContinue = v, "ai"),
        new(Sec.Ai, "\uE8F1", "Let it remember things about you", "Short facts you tell it (your work, your preferences), kept on this PC and used in every chat", () => _s.AiMemory, v => _s.AiMemory = v, "ai"),
        new(Sec.Ai, "\uE7E7", "Shorten long alerts", "Wordy alerts from apps are put in a few words (messages from people are never sent)", () => _s.AiAlerts, v => _s.AiAlerts = v, "ai"),
        new(Sec.Search, "\uE765", "Type to search", "With the notch open and the pointer on it, just start typing", () => _s.SearchTypeToOpen, v => { _s.SearchTypeToOpen = v; _notch.PrefSearchChanged(); }, "open"),
        new(Sec.Search, "\uE721", "Search button", "The magnifier in the control center's header", () => _s.SearchButton, v => { _s.SearchButton = v; _notch.PrefSearchChanged(); }, "open"),
        new(Sec.Search, "\uE8EF", "Commands and answers", "“volume 30”, “remind me…”, sums, time zones and dates", () => _s.SearchCommands, v => _s.SearchCommands = v, "find"),
        new(Sec.Search, "\uE8B7", "Files across the PC", "From the index Windows keeps for File Explorer's search", () => _s.SearchWholePc, v => _s.SearchWholePc = v, "find"),
        new(Sec.Search, "\uE8A5", "Words inside documents", "Finds a PDF or a Word file by what's written in it", () => _s.SearchInsideFiles, v => _s.SearchInsideFiles = v, "find"),
        new(Sec.Search, "\uE7C4", "Open windows", "Goes to an app or a window that's already open", () => _s.SearchWindows, v => _s.SearchWindows = v, "find"),
        new(Sec.Search, "\uE77F", "Clipboard and shelf", "Finds what you copied or kept on the shelf", () => _s.SearchClipboard, v => _s.SearchClipboard = v, "find"),
        new(Sec.Search, "\uE945", "Learn from my picks", "What you choose for what you type moves to the top", () => _s.SearchLearn, v => _s.SearchLearn = v, "find"),
        new(Sec.Search, "\uE734", "Frequent apps", "Your most-used apps when the search is empty", () => _s.SearchFrequent, v => _s.SearchFrequent = v, "empty"),
        new(Sec.Search, "\uE81C", "Recent searches", "Shown when the search is empty; ↑ brings them back", () => _s.SearchRecent, v => _s.SearchRecent = v, "empty"),
        new(Sec.Search, "\uE82F", "Suggestions", "A few things to try", () => _s.SearchTry, v => _s.SearchTry = v, "empty"),

        new(Sec.Calls, "\uE717", "Calls page", "Second page of the control center: your call, from the notch", () => !_s.HiddenPages.Contains(6), v => _notch.PrefSetPage(6, v), "page"),
        new(Sec.Calls, "\uE8A7", "Open on the Calls page", "During a call, the notch opens straight on it", () => _s.CallsOpenOnCall, v => _s.CallsOpenOnCall = v, "page"),
        new(Sec.Calls, "\uE765", "Call app shortcuts", "Camera, share, raise hand and end, through Teams, Zoom and Meet's own shortcuts", () => _s.CallShortcuts, v => { _s.CallShortcuts = v; _notch.PrefCallsChanged(); }, "page"),
        new(Sec.Calls, "\uE768", "Pause music when a call starts", "And play it again when the call ends", () => _s.CallPauseMusic, v => _s.CallPauseMusic = v, "start"),
        new(Sec.Calls, "\uE74F", "Quiet other apps during calls", "Everything else down to a whisper, then back", () => _s.CallQuietOthers, v => _s.CallQuietOthers = v, "start"),
        new(Sec.Calls, "\uE708", "Focus during calls", "Notifications wait until the call ends", () => _s.CallFocus, v => _s.CallFocus = v, "start"),
        new(Sec.Calls, "\uE7F4", "Presenter mode hides desktop icons", "A clean desktop while you share your screen", () => _s.PresenterHidesIcons, v => _s.PresenterHidesIcons = v, "start"),
        new(Sec.Calls, "\uE81C", "Call history", "Your last calls on the Calls page (only on this PC)", () => _s.CallHistory, v => _s.CallHistory = v, "start"),
        new(Sec.Pages, "\uE821", "Work", "What you're working on, your projects, and two weeks of notifications", () => !_s.HiddenPages.Contains(7), v => _notch.PrefSetPage(7, v)),
        new(Sec.Pages, "\uE717", "Calls", "Your call, from the notch", () => !_s.HiddenPages.Contains(6), v => _notch.PrefSetPage(6, v)),
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

        Label(Loc.T("Companion size"));
        var sizes = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (name, k) in new[] { ("Small", 0.85), ("Medium", 1.0), ("Large", 1.2) })
        {
            var b = PillButton(null, Loc.T(name), () => { }, primary: false);
            b.Margin = new Thickness(0, 0, 8, 8);
            SetPill(b, Math.Abs(_s.CompanionSize - k) < 0.01);
            double size = k;
            b.Click += (_, _) =>
            {
                _s.CompanionSize = size; // she takes it on her next breath (a quarter of a second)
                _s.Save();
                foreach (var other in sizes.Children.OfType<Button>()) SetPill(other, ReferenceEquals(other, b));
                Saved();
            };
            sizes.Children.Add(b);
        }
        _content.Children.Add(sizes);

        Label(Loc.T("Face's hat"));
        var hats = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (name, key) in new[] { ("Auto (season)", "auto"), ("None", "none"), ("Beanie", "beanie"), ("Cap", "cap"), ("Party", "party"), ("Crown", "crown"), ("Santa", "santa"), ("Witch", "witch") })
        {
            var b = PillButton(null, Loc.T(name), () => { }, primary: false);
            b.Margin = new Thickness(0, 0, 8, 8);
            SetPill(b, (_s.FaceOutfit ?? "auto") == key);
            b.Click += (_, _) =>
            {
                _s.FaceOutfit = key; // it puts it on at its next look (a tenth of a second)
                _s.Save();
                foreach (var other in hats.Children.OfType<Button>()) SetPill(other, ReferenceEquals(other, b));
                Saved();
            };
            hats.Children.Add(b);
        }
        _content.Children.Add(hats);
    }

    static readonly string[] ThemeKeys = { "original", "neon", "liquid", "crystal", "abyss" };
    static readonly Dictionary<string, string> ThemeTitles = new()
    {
        ["original"] = "Original", ["neon"] = "Neon", ["liquid"] = "Prism", ["crystal"] = "Crystal", ["abyss"] = "Abyss",
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
            case "neon":
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

    /// <summary>Behaviour, and the FPS pill's extras: what shows beside the frame rate in a game, and in which colour.</summary>
    void Behaviour()
    {
        Card(Rows().Where(r => r.Sec == Sec.Behaviour));

        Label(Loc.T("Beside the FPS in games"));
        var stats = new List<Row>
        {
            new(Sec.Behaviour, "", "CPU usage", "The processor's load, in %", () => _notch.PrefFpsStat("cpu"), v => _notch.PrefSetFpsStat("cpu", v)),
            new(Sec.Behaviour, "", "GPU usage", "The graphics card's load, in %", () => _notch.PrefFpsStat("gpu"), v => _notch.PrefSetFpsStat("gpu", v)),
            new(Sec.Behaviour, "", "CPU temperature", StrainService.CpuTempAvailable ? "In °C" : Loc.T("This PC doesn't report it"),
                () => _notch.PrefFpsStat("cputemp") && StrainService.CpuTempAvailable, v => _notch.PrefSetFpsStat("cputemp", v)),
            new(Sec.Behaviour, "", "GPU temperature", "In °C (when the graphics driver reports it)", () => _notch.PrefFpsStat("gputemp"), v => _notch.PrefSetFpsStat("gputemp", v)),
        };
        Card(stats);
        _content.Children.Add(new TextBlock
        {
            Text = Loc.T("Small and quiet: they change once a second, in fixed widths, so nothing moves in the corner of your eye."),
            FontSize = 11.5, Foreground = Dim, Margin = new Thickness(4, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
        });

        Label(Loc.T("Size in games"));
        var sizes = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (name, k) in new[] { ("Small", 0.85), ("Normal", 1.0), ("Large", 1.2), ("Extra large", 1.4) })
        {
            var b = PillButton(null, Loc.T(name), () => { }, primary: false);
            b.Margin = new Thickness(0, 0, 8, 8);
            b.Tag = k;
            SetPill(b, Math.Abs(_notch.PrefFpsScale - k) < 0.01);
            double size = k;
            b.Click += (_, _) =>
            {
                _notch.PrefSetFpsScale(size);
                foreach (var other in sizes.Children.OfType<Button>()) SetPill(other, ReferenceEquals(other, b));
                Saved();
            };
            sizes.Children.Add(b);
        }
        _content.Children.Add(sizes);

        Label(Loc.T("Their colours"));
        var colours = new StackPanel { Margin = new Thickness(18, 12, 18, 12) };
        foreach (bool cpu in new[] { true, false })
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.Children.Add(new TextBlock { Text = cpu ? "CPU" : "GPU", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var swatches = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, FlowDirection = FlowDirection.LeftToRight };
            bool isCpu = cpu;
            void Paint()
            {
                string current = _notch.PrefFpsColor(isCpu) ?? NotchWindow.StatColors[0].Hex;
                foreach (Border b in swatches.Children) b.BorderBrush = string.Equals((string)b.Tag, current, StringComparison.OrdinalIgnoreCase) ? Text : Brushes.Transparent;
            }
            foreach (var (name, hex) in NotchWindow.StatColors)
            {
                var sw = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand, Tag = hex,
                    Background = Frozen((Color)ColorConverter.ConvertFromString(hex)), BorderThickness = new Thickness(2), ToolTip = Loc.T(name),
                };
                string chosen = hex;
                sw.MouseLeftButtonUp += (_, _) => { _notch.PrefSetFpsColor(isCpu, chosen == NotchWindow.StatColors[0].Hex ? null : chosen); Paint(); Saved(); };
                swatches.Children.Add(sw);
            }
            Paint();
            g.Children.Add(swatches);
            colours.Children.Add(g);
        }
        _content.Children.Add(CardOf(colours));

        BrowserTabs();
    }

    /// <summary>
    /// The optional browser extension: a browser tells Windows about one playing tab only; with the extension every
    /// tab that plays shows in the notch, with its own buttons. Its state, the folder it's in, and how to add it.
    /// </summary>
    void BrowserTabs()
    {
        Label(Loc.T("Every browser tab that plays"));
        var box = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        var connected = _notch.PrefBrowsersConnected;
        string Name(string b) => b switch { "MSEdge" => "Edge", _ => b };
        box.Children.Add(new TextBlock
        {
            Text = connected.Count > 0
                ? string.Format(Loc.T("Connected: {0}"), string.Join(", ", connected.Select(Name)))
                : Loc.T("Optional: without it, the notch shows the one tab your browser tells Windows about"),
            FontSize = 13.5, FontWeight = connected.Count > 0 ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
            Foreground = connected.Count > 0 ? Frozen(Color.FromRgb(0x30, 0xD1, 0x58)) : Text,
        });
        box.Children.Add(new TextBlock
        {
            Text = Loc.T("The \"Pixo Media\" extension shows every tab playing a video, music or a stream, with play, pause, next and previous. It only talks to the notch on this PC."),
            FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10),
        });
        string[] steps =
        {
            Loc.T("1. Open your browser's extensions page: chrome://extensions, edge://extensions, brave://extensions or opera://extensions"),
            Loc.T("2. Turn on \"Developer mode\""),
            Loc.T("3. Click \"Load unpacked\" and choose the \"chromium\" folder"),
            Loc.T("Firefox: about:debugging → This Firefox → Load Temporary Add-on → firefox\\manifest.json"),
        };
        foreach (var s in steps)
            box.Children.Add(new TextBlock { Text = s, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "extension");
        var open = PillButton("", Loc.T("Open the extension folder"), () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Write("extension folder: " + ex.Message); }
        }, primary: true);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 8, 0, 0);
        box.Children.Add(open);
        _content.Children.Add(CardOf(box));
    }

    void Alerts()
    {
        Label(Loc.T("Alerts"));
        Card(Rows().Where(r => r.Sec == Sec.Alerts && r.Group == null));
        Label(Loc.T("Smart helpers"));
        Card(Rows().Where(r => r.Sec == Sec.Alerts && r.Group == "Smart"));

        // strain warnings ("Overload risk" in Neon): the master switch and what it watches
        Label(Loc.T("Overload warnings"));
        var strain = new List<Row>
        {
            new(Sec.Alerts, "\uE7BA", "Overload warnings", "A warning when something stays over its line for 20 s (never in games)",
                () => _notch.PrefStrainOn("all"), v => _notch.PrefSetStrain("all", v)),
        };
        foreach (var (key, name, line) in _notch.PrefStrainTargets)
        {
            bool available = key != "cputemp" || StrainService.CpuTempAvailable;
            string glyph = key switch { "cpu" => "\uE950", "gpu" => "\uE7F4", "ram" => "\uE964", _ => "\uE9CA" };
            strain.Add(new(Sec.Alerts, glyph, name,
                available ? string.Format(Loc.T("Warn at {0} and over"), line) : Loc.T("This PC doesn't report it"),
                () => _notch.PrefStrainOn(key), v => { if (available) _notch.PrefSetStrain(key, v); }));
        }
        Card(strain);

        // where each line is: a stepper per target (5 % / 5 °C a step)
        Label(Loc.T("Warning lines"));
        var lines = new StackPanel();
        bool first = true;
        foreach (var (key, name, _) in _notch.PrefStrainTargets)
        {
            if (key == "cputemp" && !StrainService.CpuTempAvailable) continue;
            if (!first) lines.Children.Add(new Border { Height = 1, Background = Edge, Margin = new Thickness(18, 0, 18, 0) });
            first = false;
            var g = new Grid { Margin = new Thickness(18, 10, 14, 10) };
            g.Children.Add(new TextBlock { Text = Loc.T(name), VerticalAlignment = VerticalAlignment.Center });
            var stepper = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, FlowDirection = FlowDirection.LeftToRight };
            var value = new TextBlock { MinWidth = 54, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            string k = key;
            void ShowValue() { var (v, unit, _, _) = _notch.PrefStrainLine(k); value.Text = $"{v:0}{unit}"; }
            void Step(int dir) { var (v, _, _, _) = _notch.PrefStrainLine(k); _notch.PrefSetStrainLine(k, v + dir * 5); ShowValue(); Saved(); }
            var minus = PillButton("", "", () => Step(-1), primary: false);
            var plus = PillButton("", "", () => Step(1), primary: false);
            minus.Width = plus.Width = 34;
            minus.Padding = plus.Padding = new Thickness(0);
            stepper.Children.Add(minus);
            stepper.Children.Add(value);
            stepper.Children.Add(plus);
            ShowValue();
            g.Children.Add(stepper);
            lines.Children.Add(g);
        }
        _content.Children.Add(CardOf(lines));

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

    /// <summary>The search: its key, how else it opens, what it looks through, what it shows when empty, the web, memory.</summary>
    void SearchSection()
    {
        Label(Loc.T("Shortcut"));
        var keys = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var taken = new TextBlock { Foreground = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20)), FontSize = 12, Margin = new Thickness(4, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        void ShowTaken()
        {
            string name = NotchWindow.LauncherKeyOf(_s.SearchHotkey).Name;
            taken.Text = _notch.PrefSearchKeyTaken ? string.Format(Loc.T("{0} is taken by another app: pick another one"), name) : "";
            taken.Visibility = taken.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var (key, name) in new[] { ("alt-space", "Alt+Space"), ("ctrl-space", "Ctrl+Space"), ("ctrl-alt-space", "Ctrl+Alt+Space"), ("win-alt-space", "Win+Alt+Space"), ("off", "Off") })
        {
            var b = PillButton(null, key == "off" ? Loc.T(name) : name, () => { }, primary: false);
            b.Margin = new Thickness(0, 0, 8, 8);
            SetPill(b, _s.SearchHotkey == key || key == "alt-space" && NotchWindow.LauncherKeyOf(_s.SearchHotkey).Name == "Alt+Space");
            string k = key;
            b.Click += (_, _) =>
            {
                _s.SearchHotkey = k;
                _notch.PrefSearchChanged();
                foreach (var other in keys.Children.OfType<Button>()) SetPill(other, ReferenceEquals(other, b));
                ShowTaken();
                Saved();
            };
            keys.Children.Add(b);
        }
        _content.Children.Add(keys);
        ShowTaken();
        _content.Children.Add(taken);
        Card(Rows().Where(r => r.Sec == Sec.Search && r.Group == "open"));

        Label(Loc.T("What it finds"));
        Card(Rows().Where(r => r.Sec == Sec.Search && r.Group == "find"));

        Label(Loc.T("When it's empty"));
        Card(Rows().Where(r => r.Sec == Sec.Search && r.Group == "empty"));
        if (_notch.PrefHiddenApps > 0)
        {
            Button? back = null;
            back = PillButton("\uE7B3", string.Format(Loc.T("Show removed apps again ({0})"), _notch.PrefHiddenApps), () =>
            {
                _notch.PrefShowHiddenApps();
                if (back != null) back.Visibility = Visibility.Collapsed;
                Saved();
            }, primary: false);
            back.HorizontalAlignment = HorizontalAlignment.Left;
            back.Margin = new Thickness(0, 0, 0, 6);
            _content.Children.Add(back);
        }

        Choice(Loc.T("Web search"), new[] { ("google", "Google"), ("bing", "Bing"), ("duckduckgo", "DuckDuckGo"), ("brave", "Brave Search") },
            () => _s.SearchEngine, v => _s.SearchEngine = v);
        Choice(Loc.T("AI assistant for questions"), new[] { ("chatgpt", "ChatGPT"), ("claude", "Claude"), ("perplexity", "Perplexity"), ("copilot", "Copilot"), ("off", Loc.T("Off")) },
            () => _s.SearchAssistant, v => _s.SearchAssistant = v);
        Label(Loc.T("Memory"));
        var memory = new WrapPanel();
        Button? recent = null;
        recent = PillButton("\uE81C", Loc.T("Forget recent searches"), () =>
        {
            _notch.PrefForgetSearches();
            if (recent != null) recent.IsEnabled = false;
            Saved();
        }, primary: false);
        recent.Margin = new Thickness(0, 0, 8, 8);
        recent.IsEnabled = _notch.PrefRecentSearches > 0;
        memory.Children.Add(recent);
        var learned = PillButton("\uE945", Loc.T("Forget what it learned"), () =>
        {
            if (MessageBox.Show(this, Loc.T("Frequent apps and the order of results start over."), Loc.T("Forget what it learned"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _notch.PrefForgetLearned();
            Saved();
        }, primary: false);
        learned.Margin = new Thickness(0, 0, 8, 8);
        memory.Children.Add(learned);
        _content.Children.Add(memory);
    }

    /// <summary>The Calls page: the page, its buttons, what happens when a call starts, your quick replies.</summary>
    void CallsSection()
    {
        Label(Loc.T("The page"));
        Card(Rows().Where(r => r.Sec == Sec.Calls && r.Group == "page"));
        Label(Loc.T("When a call starts"));
        Card(Rows().Where(r => r.Sec == Sec.Calls && r.Group == "start"));

        Label(Loc.T("Quick replies"));
        _content.Children.Add(new TextBlock { Text = Loc.T("One per line. A click copies it and opens the call's chat."), Foreground = Dim, FontSize = 12, Margin = new Thickness(4, -2, 0, 8), TextWrapping = TextWrapping.Wrap });
        var box = new TextBox
        {
            Text = string.Join(Environment.NewLine, _s.CallReplies), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10, 8, 10, 8), Background = Faint, Foreground = Text,
            BorderBrush = Edge, BorderThickness = new Thickness(1), CaretBrush = Text, FontSize = 12.5,
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            var list = box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(12).ToList();
            if (list.SequenceEqual(_s.CallReplies)) return;
            _s.CallReplies = list;
            _s.Save();
            Saved();
        };
        _content.Children.Add(CardOf(box, new Thickness(0)));

        var clear = PillButton("\uE74D", Loc.T("Clear call history"), () => { _notch.PrefClearCallHistory(); Saved(); }, primary: false);
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        clear.Margin = new Thickness(0, 6, 0, 0);
        _content.Children.Add(clear);
    }

    /// <summary>A row of pills, one of them picked (saved at once).</summary>
    /// <summary>
    /// The AI chat: on or off and what it may do, the key (pasted here or in the chat, checked with OpenRouter),
    /// the model, how it answers, the user's own instructions, and the kept chats.
    /// </summary>
    void AiSectionView()
    {
        Card(Rows().Where(r => r.Sec == Sec.Ai && r.Group == "ai"));

        // ── who answers
        Services.OpenRouter.Choose(_s.AiProvider);
        Choice(Loc.T("The AI runs on"), new[] { ("gemini", Loc.T("Google Gemini + Groq (free, thousands of requests a day)")), ("openrouter", Loc.T("OpenRouter (many models; 50 free a day)")) },
            () => Services.OpenRouter.IsGemini ? "gemini" : "openrouter", v => { _s.AiProvider = v; Services.OpenRouter.Choose(v); RenderContent(); });

        // ── the key
        Label(Loc.T("Connection"));
        var box = new StackPanel();
        var state = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
        var help = new TextBlock { FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        void ShowState()
        {
            bool has = Services.OpenRouter.HasKey;
            state.Text = has ? "✓  " + Loc.T("Connected to") + " " + Services.OpenRouter.ProviderName : Loc.T("Not connected yet");
            state.Foreground = has ? Frozen(Color.FromRgb(0x30, 0xD1, 0x58)) : Text;
            help.Text = has ? Loc.T("Your key is saved on this PC, encrypted for your Windows account. Paste another one below to replace it.")
                : Services.OpenRouter.IsGemini
                    ? Loc.T("1. Open Google AI Studio (Get a key) and sign in with Google.  2. Click “Create API key” and copy it.  3. Paste it below and press Check & save.")
                    : Loc.T("1. Sign in at openrouter.ai/keys (free).  2. Create a key and copy it right away (it's shown once).  3. Paste it below and press Check & save.");
        }
        ShowState();
        box.Children.Add(state);
        box.Children.Add(help);
        var field = new Grid();
        field.ColumnDefinitions.Add(new ColumnDefinition());
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pw = new PasswordBox { Background = Brushes.Transparent, Foreground = Text, CaretBrush = Text, BorderThickness = new Thickness(0), FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 7, 10, 7) };
        var pwHint = new TextBlock { Text = Services.OpenRouter.IsGemini ? "AQ.… or AIza…" : "sk-or-v1-…", Foreground = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        pw.PasswordChanged += (_, _) => pwHint.Visibility = pw.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var pwHost = new Grid();
        pwHost.Children.Add(pw);
        pwHost.Children.Add(pwHint);
        field.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = Faint, BorderBrush = Edge, BorderThickness = new Thickness(1), Child = pwHost, Margin = new Thickness(0, 0, 8, 0) });
        var result = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0), Visibility = Visibility.Collapsed };
        Button check = null!;
        async void Check()
        {
            string raw = pw.Password.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"AIza[0-9A-Za-z_\-]{30,}|AQ\.[0-9A-Za-z_\-]{30,}|gsk_[A-Za-z0-9]{20,}|sk-or-[A-Za-z0-9_\-]{20,}");
            string key = m.Success ? m.Value : raw;
            result.Visibility = Visibility.Visible;
            if (key.Length < 32 || key.Contains(' '))
            {
                result.Foreground = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20));
                result.Text = raw.Contains("...") || raw.Contains('…') ? Loc.T("That's a shortened key from a list: copy the whole key.") : Loc.T("That isn't a key: a Google key starts with AQ. or AIza, an OpenRouter key with sk-or-v1-.");
                return;
            }
            check.IsEnabled = false;
            result.Foreground = Dim;
            result.Text = Loc.T("Checking the key…");
            bool? ok = await Services.OpenRouter.CheckKey(key);
            check.IsEnabled = true;
            if (ok == false) { result.Foreground = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20)); result.Text = Loc.T("The provider says this key isn't valid. Create a new one and paste it here."); return; }
            if (!Services.OpenRouter.SaveKey(key)) { result.Foreground = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20)); result.Text = Loc.T("Couldn't save the key on this PC."); return; }
            pw.Password = "";
            result.Foreground = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
            result.Text = ok == true ? Loc.T("Key checked and saved.") : Loc.T("Saved. It couldn't be checked right now: it's checked on your first message.");
            if (Services.OpenRouter.KindOf(key) is { } kind) { _s.AiProvider = kind; Services.OpenRouter.Choose(_s.AiProvider); }
            ShowState();
            Saved();
        }
        check = PillButton("\uE73E", Loc.T("Check & save"), Check, primary: true);
        pw.KeyDown += (_, e) => { if (e.Key == Key.Enter) Check(); };
        Grid.SetColumn(check, 1);
        field.Children.Add(check);
        box.Children.Add(field);
        box.Children.Add(result);
        var links = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var get = PillButton("\uE8A7", Loc.T("Get a key"), () => Open(Services.OpenRouter.IsGemini ? "https://aistudio.google.com/apikey" : "https://openrouter.ai/keys"), primary: false);
        get.Margin = new Thickness(0, 0, 8, 0);
        links.Children.Add(get);
        var credits = PillButton("\uE8C7", Services.OpenRouter.IsGemini ? Loc.T("Your limits") : Loc.T("Credits"), () => Open(Services.OpenRouter.IsGemini ? "https://aistudio.google.com/usage" : "https://openrouter.ai/settings/credits"), primary: false);
        credits.Margin = new Thickness(0, 0, 8, 0);
        links.Children.Add(credits);
        Button forget = null!;
        forget = PillButton("\uE74D", Loc.T("Forget the key"), () => { Services.OpenRouter.ForgetKey(); ShowState(); forget.IsEnabled = false; Saved(); }, primary: false);
        forget.IsEnabled = Services.OpenRouter.HasKey;
        links.Children.Add(forget);
        box.Children.Add(links);
        _content.Children.Add(CardOf(box, new Thickness(18, 16, 18, 16)));

        // ── Groq: the second engine (free, very fast); it shares the work with Google and covers for it
        if (Services.OpenRouter.IsGemini)
        {
            Label(Loc.T("Second engine: Groq (optional, free)"));
            var gbox = new StackPanel();
            var gstate = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
            var ghelp = new TextBlock { FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            void GroqState()
            {
                bool has = Services.OpenRouter.HasGroqKey, refused = Services.OpenRouter.GroqRefused;
                gstate.Text = refused ? Loc.T("Groq didn't accept its key") : has ? "✓  " + Loc.T("Groq connected: it shares the work with Google") : Loc.T("Not added");
                gstate.Foreground = refused ? Frozen(Color.FromRgb(0xFF, 0xB0, 0x20)) : has ? Frozen(Color.FromRgb(0x30, 0xD1, 0x58)) : Text;
                ghelp.Text = has && !refused
                    ? Loc.T("Quick questions and commands go to Groq (it answers in under a second), big or visual ones to Google, and each takes over when the other is busy, used up or down.")
                    : Loc.T("1. Open console.groq.com/keys and sign in (free).  2. Click “Create API Key” and copy it.  3. Paste it below and press Check & save. It makes the AI faster and gives it a second daily allowance.");
            }
            GroqState();
            gbox.Children.Add(gstate);
            gbox.Children.Add(ghelp);
            var gfield = new Grid();
            gfield.ColumnDefinitions.Add(new ColumnDefinition());
            gfield.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var gpw = new PasswordBox { Background = Brushes.Transparent, Foreground = Text, CaretBrush = Text, BorderThickness = new Thickness(0), FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 7, 10, 7) };
            var gHint = new TextBlock { Text = "gsk_…", Foreground = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            gpw.PasswordChanged += (_, _) => gHint.Visibility = gpw.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var gHost = new Grid();
            gHost.Children.Add(gpw);
            gHost.Children.Add(gHint);
            gfield.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = Faint, BorderBrush = Edge, BorderThickness = new Thickness(1), Child = gHost, Margin = new Thickness(0, 0, 8, 0) });
            var gresult = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0), Visibility = Visibility.Collapsed };
            Button gcheck = null!;
            async void GroqCheck()
            {
                var gm = System.Text.RegularExpressions.Regex.Match(gpw.Password.Trim(), @"gsk_[A-Za-z0-9]{20,}");
                gresult.Visibility = Visibility.Visible;
                if (!gm.Success) { gresult.Foreground = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20)); gresult.Text = Loc.T("That isn't a Groq key: it starts with gsk_."); return; }
                gcheck.IsEnabled = false;
                gresult.Foreground = Dim;
                gresult.Text = Loc.T("Checking with Groq…");
                bool? ok = await Services.OpenRouter.CheckKey(gm.Value);
                gcheck.IsEnabled = true;
                if (ok == false) { gresult.Foreground = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20)); gresult.Text = Loc.T("Groq says this key isn't valid. Create a new one and paste it here."); return; }
                if (!Services.OpenRouter.SaveKey(gm.Value)) { gresult.Foreground = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20)); gresult.Text = Loc.T("Couldn't save the key on this PC."); return; }
                gpw.Password = "";
                _s.AiProvider = "gemini";
                _s.AiGroqAsked = true;
                Services.OpenRouter.Choose(_s.AiProvider);
                gresult.Foreground = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
                gresult.Text = ok == true ? Loc.T("Groq added: the two now share the work.") : Loc.T("Saved. It couldn't be checked right now: it's checked on first use.");
                GroqState();
                Saved();
            }
            gcheck = PillButton("\uE73E", Loc.T("Check & save"), GroqCheck, primary: true);
            gpw.KeyDown += (_, e) => { if (e.Key == Key.Enter) GroqCheck(); };
            Grid.SetColumn(gcheck, 1);
            gfield.Children.Add(gcheck);
            gbox.Children.Add(gfield);
            gbox.Children.Add(gresult);
            var glinks = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            var gget = PillButton("\uE8A7", Loc.T("Get a Groq key"), () => Open("https://console.groq.com/keys"), primary: false);
            gget.Margin = new Thickness(0, 0, 8, 0);
            glinks.Children.Add(gget);
            Button gforget = null!;
            gforget = PillButton("\uE74D", Loc.T("Remove Groq"), () => { try { System.IO.File.Delete(System.IO.Path.Combine(Settings.Folder, "groq.key")); } catch { } GroqState(); gforget.IsEnabled = false; Saved(); }, primary: false);
            gforget.IsEnabled = Services.OpenRouter.HasGroqKey;
            glinks.Children.Add(gforget);
            gbox.Children.Add(glinks);
            _content.Children.Add(CardOf(gbox, new Thickness(18, 16, 18, 16)));
        }

        // ── the model
        if (Services.OpenRouter.IsGemini)
        {
            Choice(Loc.T("Model"), new[] { ("openrouter/free", Loc.T("Auto (best with free requests left)")), ("gemini-flash-lite-latest", "Gemini Flash-Lite"), ("gemma-4-31b-it", "Gemma 4 31B"), ("gemma-4-26b-a4b-it", "Gemma 4 26B"), ("gemini-flash-latest", "Gemini Flash") },
                () => _s.AiGeminiModel, v => _s.AiGeminiModel = v);
            _content.Children.Add(new TextBlock { Text = Loc.T("Free with Google, per day: Gemma 4 about 1,500 each, Flash-Lite about 500, Flash about 20. Auto moves to the next model when one runs out; they all come back at midnight, California time."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 0, 6) });
        }
        else
        Choice(Loc.T("Model"), new[]
        {
            ("openrouter/free", Loc.T("Free (auto)")), ("deepseek/deepseek-v4-flash", "DeepSeek Flash"), ("google/gemini-3-flash-preview", "Gemini Flash"),
            ("anthropic/claude-haiku-5.5", "Claude Haiku"), ("anthropic/claude-sonnet-5.5", "Claude Sonnet"),
        }, () => _s.AiModel, v => _s.AiModel = v);
        var custom = new Grid { Margin = new Thickness(0, 0, 0, 4), Visibility = Services.OpenRouter.IsGemini ? Visibility.Collapsed : Visibility.Visible };
        custom.ColumnDefinitions.Add(new ColumnDefinition());
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var idBox = new TextBox { Text = _s.AiModel, Background = Brushes.Transparent, Foreground = Text, CaretBrush = Text, BorderThickness = new Thickness(0), FontSize = 12.5, Padding = new Thickness(10, 7, 10, 7), ToolTip = Loc.T("Any model id from openrouter.ai/models") };
        custom.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = Faint, BorderBrush = Edge, BorderThickness = new Thickness(1), Child = idBox, Margin = new Thickness(0, 0, 8, 0) });
        var use = PillButton(null, Loc.T("Use this model"), () =>
        {
            string id = idBox.Text.Trim();
            if (id.Length == 0) return;
            _s.AiModel = id;
            _notch.PrefChanged();
            RenderContent();
            Saved();
        }, primary: false);
        Grid.SetColumn(use, 1);
        custom.Children.Add(use);
        var browse = PillButton("\uE8A7", Loc.T("Browse"), () => Open("https://openrouter.ai/models"), primary: false);
        browse.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(browse, 2);
        custom.Children.Add(browse);
        _content.Children.Add(custom);
        if (!Services.OpenRouter.IsGemini)
            _content.Children.Add(new TextBlock { Text = Loc.T("Free models cost nothing but can be slow or busy. Paid ones need credits on OpenRouter, and are better at doing things on the PC."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 0, 6) });

        // ── where it searches
        Choice(Loc.T("It searches the web with"), new[] { ("google", "Google"), ("brave", "Brave Search"), ("duckduckgo", "DuckDuckGo") },
            () => _s.AiSearchEngine, v => { _s.AiSearchEngine = v; Services.WebTools.Engine = v; });
        _content.Children.Add(new TextBlock { Text = Loc.T("If it doesn't answer, the others are tried, then the news and Wikipedia. Google runs in Windows' own Edge engine, out of sight."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 8) });

        // ── what waits for your OK
        Choice(Loc.T("Ask before running commands"), new[] { ("risky", Loc.T("Only risky ones")), ("changes", Loc.T("Anything that changes something")), ("never", Loc.T("Never ask")) },
            () => _s.AiConfirm ? _s.AiAsk : "never", v => { _s.AiAsk = v; _s.AiConfirm = true; });
        _content.Children.Add(new TextBlock { Text = Loc.T("Risky: deleting, installing, closing programs, downloads, the registry, security, disks and power. Looking things up never asks."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 8) });

        // ── how it answers
        Choice(Loc.T("Answers"), new[] { ("brief", Loc.T("Brief")), ("balanced", Loc.T("Balanced")), ("detailed", Loc.T("Detailed")) }, () => _s.AiStyle, v => _s.AiStyle = v);
        Choice(Loc.T("Creativity"), new[] { ("precise", Loc.T("Precise")), ("balanced", Loc.T("Balanced")), ("creative", Loc.T("Creative")) }, () => _s.AiCreativity, v => _s.AiCreativity = v);

        Label(Loc.T("Your instructions"));
        var ins = new TextBox
        {
            Text = _s.AiInstructions, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 74, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent, Foreground = Text, CaretBrush = Text, BorderThickness = new Thickness(0), FontSize = 12.5, Padding = new Thickness(10, 8, 10, 8),
        };
        var insHint = new TextBlock { Text = Loc.T("What it should always know or do. E.g. “I'm a student in Casablanca. Answer in French. Keep code in C#.”"), Foreground = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false, Margin = new Thickness(12, 9, 12, 0) };
        insHint.Visibility = ins.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ins.TextChanged += (_, _) => insHint.Visibility = ins.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ins.LostFocus += (_, _) =>
        {
            if (ins.Text == _s.AiInstructions) return;
            _s.AiInstructions = ins.Text.Length > 1500 ? ins.Text[..1500] : ins.Text;
            _notch.PrefChanged();
            Saved();
        };
        var insHost = new Grid();
        insHost.Children.Add(ins);
        insHost.Children.Add(insHint);
        _content.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = Faint, BorderBrush = Edge, BorderThickness = new Thickness(1), Child = insHost, Margin = new Thickness(0, 0, 0, 6) });

        // ── voice
        Label(Loc.T("Voice"));
        Card(new[]
        {
            new Row(Sec.Ai, "\uE720", "Talk to it", "The mic beside the message box, or Ctrl+M: your words appear as you speak", () => _s.AiVoice, v => _s.AiVoice = v, "voice"),
            new Row(Sec.Ai, "\uE724", "Send what I say right away", "Off: what you said waits in the box so you can check it", () => _s.AiVoiceSend, v => _s.AiVoiceSend = v, "voice"),
        });
        Choice(Loc.T("Listening with"), new[] { ("windows", Loc.T("Windows (free, words appear as you speak)")), ("ai", Services.OpenRouter.IsGemini ? Loc.T("Gemini (free with your key, any language)") : Loc.T("OpenRouter (needs $0.50 credit, any language)")) },
            () => _s.AiVoiceEngine == "openrouter" ? "ai" : _s.AiVoiceEngine, v => _s.AiVoiceEngine = v);
        var langs = new List<(string, string)> { ("auto", Loc.T("Windows' language")) };
        try { foreach (var l in Windows.Media.SpeechRecognition.SpeechRecognizer.SupportedTopicLanguages) langs.Add((l.LanguageTag, l.DisplayName)); } catch { }
        Choice(Loc.T("The language you speak"), langs.ToArray(), () => _s.AiVoiceLanguage, v => _s.AiVoiceLanguage = v);
        Choice(Loc.T("Read answers aloud"), new[] { ("voice", Loc.T("When I talk")), ("always", Loc.T("Always")), ("never", Loc.T("Never")) }, () => _s.AiSpeak, v => _s.AiSpeak = v);
        var voices = Services.VoiceOut.Voices();
        if (voices.Count > 0)
            Choice(Loc.T("Its voice"), new[] { ("", Loc.T("Default")) }.Concat(voices.Select(v => (v.Id, $"{v.Name.Replace("Microsoft ", "")} · {v.Language}"))).ToArray(), () => _s.AiVoiceId, v => _s.AiVoiceId = v);
        Choice(Loc.T("Speed"), new[] { ("0.85", Loc.T("Slower")), ("1", Loc.T("Normal")), ("1.25", Loc.T("Faster")), ("1.5", Loc.T("Fast")) },
            () => _s.AiVoiceRate.ToString(System.Globalization.CultureInfo.InvariantCulture), v => _s.AiVoiceRate = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
        _content.Children.Add(new TextBlock { Text = Loc.T("More voices (Arabic, French...): Windows Settings → Time & language → Speech → Add voices."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 8) });

        // ── pictures
        Choice(Loc.T("Making pictures"), new[] { ("", Loc.T("Automatic (free if any, else cheapest)")), ("google/gemini-3.1-flash-lite-image", "Gemini Flash Lite Image"), ("google/gemini-2.5-flash-image", "Gemini 2.5 Flash Image"), ("openai/gpt-5-image-mini", "GPT Image mini") },
            () => _s.AiImageModel, v => _s.AiImageModel = v);
        _content.Children.Add(new TextBlock { Text = Loc.T("Pictures are made through OpenRouter (Google's free tier makes none, and OpenRouter has no free picture model now): add an OpenRouter key with a little credit, about a cent a picture."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 8) });

        // ── what it remembers
        Label(Loc.T("What it remembers about you"));
        var facts = Services.AiMemory.All();
        var memo = new StackPanel();
        void FillMemory()
        {
            memo.Children.Clear();
            var now = Services.AiMemory.All();
            if (now.Count == 0)
            {
                memo.Children.Add(new TextBlock { Text = Loc.T("Nothing yet. Tell it in the chat, e.g. “remember that I work night shifts”."), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 14, 18, 14) });
                return;
            }
            for (int i = 0; i < now.Count; i++)
            {
                string fact = now[i];
                if (i > 0) memo.Children.Add(new Border { Height = 1, Background = Edge, Margin = new Thickness(18, 0, 0, 0) });
                var row = new Grid { MinHeight = 38 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = fact, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 8, 10, 8) });
                var del = new Button { Content = Glyph("\uE74D", 12, Dim), Width = 32, Height = 28, Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = Loc.T("Forget this"), Template = PillTemplate() };
                del.Click += (_, _) => { Services.AiMemory.Remove(fact); FillMemory(); Saved(); };
                Grid.SetColumn(del, 1);
                row.Children.Add(del);
                memo.Children.Add(row);
            }
        }
        FillMemory();
        _content.Children.Add(CardOf(memo));
        if (facts.Count > 0)
        {
            Button clearMemo = null!;
            clearMemo = PillButton("\uE74D", Loc.T("Forget everything"), () => { Services.AiMemory.Clear(); FillMemory(); clearMemo.IsEnabled = false; Saved(); }, primary: false);
            clearMemo.HorizontalAlignment = HorizontalAlignment.Left;
            clearMemo.Margin = new Thickness(0, 0, 0, 6);
            _content.Children.Add(clearMemo);
        }

        // ── what it learned while working (how things get done here, this person's habits)
        int learned = Services.AiLearn.Count;
        Label(Loc.T("What it learned on this PC"));
        _content.Children.Add(new TextBlock
        {
            Text = learned == 0 ? Loc.T("Nothing yet: it learns how things get done here as it works (which steps worked, your corrections, your habits).")
                : string.Format(Loc.T("{0} ways of doing things and lessons, used to get it right faster next time. Kept on this PC."), learned),
            FontSize = 11.5, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 6),
        });
        if (learned > 0)
        {
            Button forgetLearned = null!;
            forgetLearned = PillButton("\uE74D", Loc.T("Forget what it learned"), () => { Services.AiLearn.Clear(); forgetLearned.IsEnabled = false; Saved(); }, primary: false);
            forgetLearned.HorizontalAlignment = HorizontalAlignment.Left;
            forgetLearned.Margin = new Thickness(0, 0, 0, 6);
            _content.Children.Add(forgetLearned);
        }

        // ── kept chats
        Choice(Loc.T("Keep chats for"), new[] { ("0", Loc.T("Newest 60")), ("90", Loc.T("3 months")), ("30", Loc.T("1 month")), ("7", Loc.T("1 week")) },
            () => _s.AiKeepDays.ToString(), v => { _s.AiKeepDays = int.Parse(v); Services.ChatStore.KeepDays = _s.AiKeepDays; Services.ChatStore.Trim(); Saved(); });
        Label(Loc.T("Your chats"));
        var (n, bytes) = Services.ChatStore.Usage();
        var where = new TextBlock
        {
            Text = string.Format(Loc.T("{0} chats · {1} · pictures are never saved"), n, Services.SystemMonitor.Bytes(bytes)) + "\n" + Services.ChatStore.Folder,
            FontSize = 11.5, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 6),
        };
        _content.Children.Add(where);
        var openFolder = PillButton("\uE838", Loc.T("Open the folder"), () =>
        {
            System.IO.Directory.CreateDirectory(Services.ChatStore.Folder);
            Open(Services.ChatStore.Folder);
        }, primary: false);
        openFolder.HorizontalAlignment = HorizontalAlignment.Left;
        openFolder.Margin = new Thickness(0, 0, 0, 6);
        _content.Children.Add(openFolder);
        Button wipe = null!;
        bool armed = false;
        wipe = PillButton("\uE74D", n == 0 ? Loc.T("No chats kept") : string.Format(Loc.T("Delete all chats ({0})"), n), () =>
        {
            if (!armed) { armed = true; ((TextBlock)((StackPanel)wipe.Content).Children[^1]).Text = Loc.T("Click again to delete them all"); return; }
            Services.ChatStore.DeleteAll();
            ((TextBlock)((StackPanel)wipe.Content).Children[^1]).Text = Loc.T("Deleted");
            wipe.IsEnabled = false;
            Saved();
        }, primary: false);
        wipe.IsEnabled = n > 0;
        wipe.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Children.Add(wipe);
    }

    static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void Choice(string title, (string Key, string Name)[] options, Func<string> get, Action<string> set)
    {
        Label(title);
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        string now = get();
        bool any = options.Any(o => o.Key == now);
        foreach (var (key, name) in options)
        {
            var b = PillButton(null, name, () => { }, primary: false);
            b.Margin = new Thickness(0, 0, 8, 8);
            SetPill(b, key == now || !any && key == options[0].Key);
            string k = key;
            b.Click += (_, _) =>
            {
                set(k);
                _notch.PrefChanged();
                foreach (var other in row.Children.OfType<Button>()) SetPill(other, ReferenceEquals(other, b));
                Saved();
            };
            row.Children.Add(b);
        }
        _content.Children.Add(row);
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
        var mark = PixoLogo(76);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        hero.Children.Add(mark);
        hero.Children.Add(new TextBlock { Text = "Pixo", FontSize = 24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") });
        hero.Children.Add(new TextBlock { Text = Loc.T("Version") + " " + Version, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        hero.Children.Add(new TextBlock { Text = Loc.T("A living island at the top of your screen."), Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        _content.Children.Add(CardOf(hero));

        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(PillButton("", Loc.T("Open the settings folder"), () => Launch(Settings.Folder), primary: false));
        actions.Children.Add(PillButton("", Loc.T("Releases on GitHub"), () => Launch("https://github.com/Prism99-collab/Pixo/releases"), primary: false));
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
