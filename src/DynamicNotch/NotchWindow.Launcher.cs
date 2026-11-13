using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The notch's own search, a launcher at the top of the screen (not Windows Search).
/// Ways in: Alt+Space from anywhere; or, with the notch open and the pointer on it, just start typing; or the
/// magnifier in the header. It finds apps (desktop and Store), recent files and what's in Downloads, Desktop and
/// Documents, Windows settings pages, and the notch's own commands ("timer 25", "focus", "mute", a theme's name...),
/// and answers by itself: sums, conversions, colours, web addresses. What you open often comes first.
/// Enter opens, Ctrl+Enter shows a file in its folder, Tab completes, Esc closes and gives the keyboard back to
/// the app you were in.
/// </summary>
public partial class NotchWindow
{
    const int LauncherHotkeyId = 0x4E02;
    readonly LauncherIndex _index = new();
    Border? _launch;
    TextBox? _launchBox;
    TextBlock? _launchHint, _launchPrompt, _launchFoot;
    Grid? _launchBody;
    StackPanel? _launchList, _launchStarters;
    Button? _searchBtn;
    bool _launcherOpen;
    IntPtr _launchPrevFg;
    int _launchSel;
    readonly List<Hit> _hits = new();

    /// <summary>One line in the results: what it says, how it looks, what Enter does.</summary>
    /// <c>Section</c>: the label it sits under; <c>Highlight</c>: the text whose letters light up in the title;
    /// <c>Actions</c>: what → offers; <c>Preview</c>: a picture to show as its icon; <c>Key</c>: what makes it the same line.
    sealed record Hit(string Title, string Detail, string? Glyph, string? IconPath, Action Run,
        LauncherIndex.Entry? Entry = null, string? Swatch = null, bool Big = false, Action? Reveal = null, string? Complete = null,
        string? Section = null, string? Highlight = null, Func<List<HitAction>>? Actions = null, string? Preview = null, string? Key = null);

    // ───────────────────────────── wiring ─────────────────────────────

    void WireLauncher()
    {
        System.Text.RegularExpressions.Regex.CacheSize = 512;
        // the first letter typed shouldn't pay for getting the patterns ready: done quietly, a little after start
        After(15000, () => Task.Run(() =>
        {
            try
            {
                LauncherIntent.Parse("remind me to stretch in 20 min and volume 30", DateTime.Now);
                LauncherIntent.Fact("days until new year", DateTime.Now);
                LauncherAnswers.For("5 km in miles");
            }
            catch { }
        }));
        _index.Refresh();
        LoadReminders();
        WireWindowsSearch();
        WireFileIndex();
        Deactivated += (_, _) => { if (_launcherOpen) CloseLauncher(giveBack: false); };
        AddSearchButton();
    }

    /// <summary>Alt+Space: open it (or close it again). Not over a game: the notch never takes the keyboard there.</summary>
    void OnLauncherHotkey()
    {
        if (_launcherOpen) { CloseLauncher(giveBack: true); return; }
        if (_game != null || _settings.Locked) return;
        OpenLauncher(null);
    }

    /// <summary>A magnifier beside the desktop-icons button, dressed the same way in every theme.</summary>
    void AddSearchButton()
    {
        if (DesktopBtn.Parent is not Panel row) return;
        _searchBtn = new Button
        {
            Width = 18, Height = 18, Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand, Template = DesktopBtn.Template,
            FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Content = "", ToolTip = "Search  ·  Alt+Space",
        };
        _searchBtn.Click += (_, _) => OpenLauncher(null);
        _searchBtn.PreviewMouseLeftButtonDown += (_, _) => ClusterGlitch(strong: true);
        row.Children.Insert(0, _searchBtn);
        PaintSearchButton();
    }

    void PaintSearchButton()
    {
        if (_searchBtn == null) return;
        _searchBtn.Visibility = _settings.SearchButton ? Visibility.Visible : Visibility.Collapsed;
        _searchBtn.ToolTip = LauncherKeyName.Length > 0 ? "Search  ·  " + LauncherKeyName : "Search";
        _searchBtn.Tag = _cyber ? "cy" : null;
        _searchBtn.Background = Frozen(_cyber ? Color.FromArgb(0x30, CyCyan.R, CyCyan.G, CyCyan.B) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        _searchBtn.BorderBrush = Frozen(_cyber ? Color.FromArgb(0x90, CyCyan.R, CyCyan.G, CyCyan.B) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        _searchBtn.Foreground = Frozen(_cyber ? CyCyan : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    }

    // ───────────────────────────── open / close ─────────────────────────────

    void OpenLauncher(string? seed)
    {
        if (_launcherOpen) { if (seed != null && _launchBox != null) { _launchBox.Text += seed; _launchBox.CaretIndex = _launchBox.Text.Length; } return; }
        var fg = GetForegroundWindow();
        _launchPrevFg = fg != _hwnd ? fg : IntPtr.Zero;
        BuildLauncher();
        if (_launch == null || _launchBox == null) return;
        if (_mode != Mode.Expanded) Expand(byHotkey: true);
        CloseStrainLog();
        _launcherOpen = true;
        UpdateRain(); UpdateAmbient(); // the panel hides what's behind it: those rest while it's open
        _procInfo.Clear(); // process ids get reused: read the open apps afresh each time
        _openWins = new();
        if (_settings.SearchWindows) LoadOpenWindows();
        _wsItems = new();
        _wsAsked = _wsAnswered = null;
        _fiItems = new();
        _fiAsked = null;
        _desktopHidden = DesktopIcons.Shown == false || Environment.GetEnvironmentVariable("DYNAMICNOTCH_DESKTOP") == "hidden"; // (=hidden: development, to see it without touching the desktop)
        _desktopItems = null;
        _recallPos = -1;
        _actionsFor = null;
        _mainHits = null;
        _rows.Clear();
        _labels.Clear();
        _fileFacts.Clear();
        _index.Refresh();
        StyleLauncher();

        // the notch takes the keyboard while you type, then gives it back
        SetActivatable(true);
        TakeForeground();
        Activate();

        _launch.Visibility = Visibility.Visible;
        _launch.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        _launchScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _launchBox.Text = seed ?? "";
        _launchBox.CaretIndex = _launchBox.Text.Length;
        _launchBox.Focus();
        Keyboard.Focus(_launchBox);
        Search();
        if (_cyber) CyberGlitch(strong: false, tint: CyCyan);
        // the results area keeps one size while the search is open: what changes inside it as you type is laid out
        // inside it, and never makes the rest of the notch measure itself again
        if (_launchBody != null) { _launchBody.Width = double.NaN; _launchBody.Height = double.NaN; }
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (_launchBody is { ActualHeight: > 20, ActualWidth: > 20 } b && _launcherOpen) { b.Width = b.ActualWidth; b.Height = b.ActualHeight; }
            ScanIn();
        });
    }

    void CloseLauncher(bool giveBack)
    {
        if (!_launcherOpen) return;
        EndLauncher(giveBack);
        Collapse();
    }

    /// <summary>The search goes away (the notch itself closes in Collapse, which calls this too).</summary>
    void EndLauncher(bool giveBack)
    {
        if (!_launcherOpen) return;
        _launcherOpen = false;
        UpdateRain(); UpdateAmbient();
        if (_launch != null)
        {
            _launch.BeginAnimation(OpacityProperty, null);
            _launch.Opacity = 0;
            _launch.Visibility = Visibility.Collapsed;
        }
        SetActivatable(false);
        // the keyboard goes back to the app you were typing in
        if (giveBack && _launchPrevFg != IntPtr.Zero && IsWindow(_launchPrevFg)) SetForegroundWindow(_launchPrevFg);
        _launchPrevFg = IntPtr.Zero;
    }

    /// <summary>Alt+Space for the search, registered with the window (another app may own it already).</summary>
    void RegisterLauncherKey()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, LauncherHotkeyId);
        LauncherKeyTaken = false;
        var (mods, vk, name) = LauncherKeyOf(_settings.SearchHotkey);
        if (vk == 0) return; // off
        if (!RegisterHotKey(_hwnd, LauncherHotkeyId, mods | MOD_NOREPEAT, vk))
        {
            LauncherKeyTaken = true;
            AppLog.Write($"{name} is taken by another app: pick another key in Preferences → Search, or use the magnifier");
        }
    }

    /// <summary>The shortcut's keys and its name ("" when it's off).</summary>
    internal static (uint Mods, uint Vk, string Name) LauncherKeyOf(string key) => key switch
    {
        "ctrl-space" => (MOD_CONTROL, 0x20, "Ctrl+Space"),
        "ctrl-alt-space" => (MOD_CONTROL | MOD_ALT, 0x20, "Ctrl+Alt+Space"),
        "win-alt-space" => (MOD_WIN | MOD_ALT, 0x20, "Win+Alt+Space"),
        "off" => (0, 0, ""),
        _ => (MOD_ALT, 0x20, "Alt+Space"),
    };

    internal bool LauncherKeyTaken { get; private set; }
    string LauncherKeyName => LauncherKeyOf(_settings.SearchHotkey).Name;

    void SetActivatable(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex = on ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Windows only lets the app you're using hand the keyboard over: join its input for a moment to take it.</summary>
    void TakeForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == _hwnd) return;
        uint fgThread = GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
        bool joined = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try { BringWindowToTop(_hwnd); SetForegroundWindow(_hwnd); }
        finally { if (joined) AttachThreadInput(me, fgThread, false); }
    }

    /// <summary>For the message loop: while the search is open the notch takes the clicks it gets.</summary>
    bool LauncherTakesClicks => _launcherOpen;

    // ───────────────────────────── the panel ─────────────────────────────

    readonly ScaleTransform _launchScale = new(1, 1, 220, 0);

    void BuildLauncher()
    {
        if (_launch != null) return;
        var root = new Grid { Margin = new Thickness(18, 10, 18, 8) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });

        // the search line: a prompt, what you type, a hint on the right
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _launchPrompt = new TextBlock { FontSize = 15, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        line.Children.Add(_launchPrompt);
        _launchBox = new TextBox
        {
            FontSize = 17, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White,
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), FocusVisualStyle = null,
        };
        _launchBox.TextChanged += (_, _) => QueueSearch();
        _launchBox.SelectionChanged += (_, _) => { if (_launcherOpen && _ghost != null && _launchBox.CaretIndex != _launchBox.Text.Length) SetGhost(null); };
        _launchBox.PreviewKeyDown += OnLauncherKey;
        Grid.SetColumn(_launchBox, 1);
        line.Children.Add(_launchBox);
        _launchHint = new TextBlock { FontSize = 13, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 1) };
        Grid.SetColumn(_launchHint, 1);
        line.Children.Add(_launchHint);
        // the grey rest of the name Enter will open ("spo" + "tify"), right after what you typed
        _launchGhost = new TextBlock { Foreground = Frozen(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Grid.SetColumn(_launchGhost, 1);
        line.Children.Add(_launchGhost);
        var esc = new TextBlock { Text = "esc", FontSize = 10, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        esc.MouseLeftButtonUp += (_, _) => CloseLauncher(giveBack: true);
        Grid.SetColumn(esc, 2);
        line.Children.Add(esc);
        var underline = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), Tag = "line" };
        _launchLine = underline;
        Grid.SetColumnSpan(underline, 3);
        line.Children.Add(underline);
        root.Children.Add(line);

        // the results (or, while empty, what you use most and a few things to try)
        var body = _launchBody = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        _launchList = new StackPanel();
        _launchStarters = new StackPanel();
        // as many results as there are, scrolled by the keys (or the wheel); the picked one is kept in view
        _launchScroll = new ScrollViewer { Content = BuildListLayer(), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, CanContentScroll = false };
        body.Children.Add(_launchScroll);
        _launchScroll.ScrollChanged += (_, e) => { if (e.VerticalChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 40) RenderRest(); };
        AttachScrollHints(body);
        body.Children.Add(new ScrollViewer { Content = _launchStarters, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        _launchFoot = new TextBlock { FontSize = 9.5, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(_launchFoot, 2);
        root.Children.Add(_launchFoot);

        _launchScanLine = new Border
        {
            Height = 2, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Opacity = 0, Visibility = Visibility.Collapsed,
            Background = new LinearGradientBrush(Color.FromArgb(0, CyCyan.R, CyCyan.G, CyCyan.B), CyCyan, 0) { GradientStops = { new GradientStop(Color.FromArgb(0, CyCyan.R, CyCyan.G, CyCyan.B), 0), new GradientStop(CyCyan, 0.5), new GradientStop(Color.FromArgb(0, CyCyan.R, CyCyan.G, CyCyan.B), 1) } },
            RenderTransform = new TranslateTransform(),
        };
        Grid.SetRowSpan(_launchScanLine, 3);
        root.Children.Add(_launchScanLine);
        _launch = new Border { Child = root, Visibility = Visibility.Collapsed, Opacity = 0, RenderTransform = _launchScale };
        Grid.SetRowSpan(_launch, 3);
        Panel.SetZIndex(_launch, 40);
        Expanded.Children.Add(_launch);
        _index.Changed += () => Dispatcher.InvokeAsync(() => { if (_launcherOpen && _actionsFor == null) Search(merge: true); }); // never pulls you out of a second level
    }

    Color LaunchAccent => _cyber ? CyCyan : _accent == Colors.White ? Color.FromRgb(0x0A, 0x84, 0xFF) : _accent;

    // ───────────────────────────── searching ─────────────────────────────

    void Search() => Search(merge: false);

    bool _measureSearch; // development: each search's time goes to the log

    void Search(bool merge)
    {
        if (!_measureSearch) { SearchCore(merge); return; }
        var sw = Stopwatch.StartNew();
        SearchCore(merge);
        double build = sw.Elapsed.TotalMilliseconds;
        _launchList?.UpdateLayout();
        AppLog.Write($"search '{_launchBox?.Text}' {(merge ? "merge " : "")}{build:0.0} ms, with layout {sw.Elapsed.TotalMilliseconds:0.0} ms, {_hits.Count} lines");
    }

    /// <summary>
    /// Builds the results for what's typed. Things it understood (a task, an answer) come first; then sections (apps,
    /// files, open windows, settings, what you copied…), the best section first, each sorted by how well it fits, with
    /// what you usually pick lifted to the top; the web last. <paramref name="merge"/>: files from Windows' index just
    /// arrived, so the list is redrawn in place, keeping the line you're on.
    /// </summary>
    void SearchCore(bool merge)
    {
        if (_launchBox == null || _launchList == null || _launchStarters == null || _launchHint == null || _launchScroll == null) return;
        _searchDone = _searchAsked;
        if (!merge) _renderAll = false;
        var lap = Stopwatch.StartNew(); var laps = new List<string>();
        void Lap(string n) { if (_measureSearch) { laps.Add($"{n} {lap.Elapsed.TotalMilliseconds:0.0}"); lap.Restart(); } }
        string q = _launchBox.Text;
        if (!_recalling) _recallPos = -1;
        // files arriving while you type never take the line you're on from under Enter
        string? keepKey = merge && _pickedByKeys && _launchSel < _hits.Count ? _hits[_launchSel].Key : null;
        if (!merge) _pickedByKeys = false;
        _actionsFor = null;
        _launchHint.Text = q.Length == 0 ? (_cyber ? "SEARCH // ASK // COMMAND" : "Search, ask, or tell it what to do…") : "";
        _hits.Clear();
        if (q.Trim().Length == 0)
        {
            _launchScroll.Visibility = Visibility.Collapsed;
            ((UIElement?)_launchStarters.Parent ?? _launchStarters).Visibility = Visibility.Visible; _launchStarters.Visibility = Visibility.Visible;
            RenderStarters();
            SetGhost(null);
            var tips = new List<string>();
            if (_settings.SearchRecent && _index.Recent.Count > 0) tips.Add("↑ brings back what you searched");
            if (LauncherKeyName.Length > 0) tips.Add(LauncherKeyName + " opens this from anywhere");
            SetFoot(string.Join("  ·  ", tips));
            return;
        }
        ((UIElement?)_launchStarters.Parent ?? _launchStarters).Visibility = Visibility.Collapsed;
        _launchScroll.Visibility = Visibility.Visible;

        // "desktop:" (or the chip in the empty search): only what's on the desktop
        if (q.TrimStart().StartsWith("desktop:", StringComparison.OrdinalIgnoreCase))
        {
            DesktopResults(q.TrimStart()[8..].Trim());
            return;
        }

        // 1. something to do, said in your own words ("remind me…", "volume 30 and dark mode", "close spotify")
        string find = q;
        bool understood = false;
        Hit? soft = null;
        if (_settings.SearchCommands) find = AddTaskHits(q, out understood, out soft);
        // 2. an answer the notch works out itself: a sum, a conversion, the time somewhere, a date, the battery...
        if (_settings.SearchCommands && (NotchFact(q) ?? LauncherIntent.Fact(q, DateTime.Now) ?? LauncherAnswers.For(q)) is { } a)
        {
            understood = true;
            if (a.Url != null)
                _hits.Add(new Hit(a.Title, a.Detail, "", null, () => OpenTarget(a.Url!), Key: "answer"));
            else
                _hits.Add(new Hit(a.Title, a.Detail, a.Color != null ? null : "", null,
                    a.Copy.Length == 0 ? () => Nudge() : a.Secret ? () => CopySecret(a.Copy) : () => CopyAnswer(a.Copy), Swatch: a.Color, Big: true, Key: "answer"));
        }
        int understoodCount = _hits.Count;
        Lap("tasks");

        // 3. everything else that fits, scored, one section per kind
        var pool = new Dictionary<string, (Hit Hit, double Score)>(StringComparer.OrdinalIgnoreCase);
        void Offer(Hit h, double s)
        {
            string key = h.Key ?? h.Title;
            if (!pool.TryGetValue(key, out var had) || had.Score < s) pool[key] = (h, s);
        }
        string lower = q.Trim().ToLowerInvariant();
        if (!understood)
            foreach (var c in Commands(q))
                Offer(c with { Section = "Notch", Key = "cmd|" + c.Title }, c.Title.Equals(q.Trim(), StringComparison.OrdinalIgnoreCase) ? 1000 : c.Title.ToLowerInvariant().StartsWith(lower) ? 700 : 300);

        Lap("commands");
        // apps, settings, folders and files the notch knows (scored first, lines made only for the best)
        var scored = new List<(LauncherIndex.Entry e, double s)>();
        foreach (var e in _index.Entries)
        {
            double s = LauncherIndex.Score(find, e);
            if (s <= 0) continue;
            scored.Add((e, s + DesktopLift(e) + _index.Habit(e) + (_settings.SearchLearn ? _index.PickBoost(q, e) : 0)));
        }
        Lap("score " + _index.Entries.Count);
        foreach (var (e, s) in scored.OrderByDescending(x => x.s).Take(48)) Offer(EntryHit(e, find), s);
        Lap("lines " + Math.Min(48, scored.Count));

        // files anywhere on the PC, from Windows' index (asked in the background; what's known so far is shown)
        if (!understood && find.Trim().Length >= 2 && _ws.Available && _settings.SearchWholePc)
        {
            _ws.Inside = _settings.SearchInsideFiles;
            if (!string.Equals(_wsAsked, find, StringComparison.Ordinal)) { _wsAsked = find; _ws.Query(find); }
            foreach (var item in _wsItems)
            {
                if (IsShortcut(item.Path) && !OnDesktop(item.Path)) continue; // shortcuts are the apps' own lines (on the desktop they're what you see)
                var e = new LauncherIndex.Entry(item.Name, item.Path, item.Folder ? LauncherIndex.Kind.Folder : LauncherIndex.Kind.File, ParentName(item.Path));
                double s = LauncherIndex.Score(find, e);
                bool inside = s <= 0 && item.ByContent && string.Equals(_wsAnswered, find, StringComparison.Ordinal);
                if (s <= 0 && !inside) continue; // an older answer that no longer fits
                if (inside) s = 170;
                if (!item.Folder && DateTime.Now - item.Modified < TimeSpan.FromDays(14)) s += 30; // recent work first
                Offer(EntryHit(e, find, item, inside ? lower : null), s + DesktopLift(e) + _index.Habit(e) + (_settings.SearchLearn ? _index.PickBoost(q, e) : 0));
            }
        }

        // every drive, from the notch's own list of names (works on any PC, indexing on or off)
        if (!understood && find.Trim().Length >= 2 && _settings.SearchWholePc && Started(_fileIndex))
        {
            if (!string.Equals(_fiAsked, find, StringComparison.Ordinal)) { _fiAsked = find; _fileIndex.Query(find); }
            foreach (var f in _fiItems)
            {
                if (IsShortcut(f.Path) && !OnDesktop(f.Path)) continue;
                var e = new LauncherIndex.Entry(f.Name, f.Path, f.Folder ? LauncherIndex.Kind.Folder : LauncherIndex.Kind.File, ParentName(f.Path));
                double s = LauncherIndex.Score(find, e);
                if (s <= 0) continue; // an older answer that no longer fits
                if (!f.Folder && DateTime.Now - f.Modified < TimeSpan.FromDays(14)) s += 30;
                Offer(EntryHit(e, find, new WindowsSearch.Item(f.Path, f.Name, f.Folder, f.Modified, -1, false)), s + DesktopLift(e) + _index.Habit(e) + (_settings.SearchLearn ? _index.PickBoost(q, e) : 0));
            }
        }

        // what's already open, and what you copied or kept
        if (!understood && find.Trim().Length >= 2)
        {
            if (_settings.SearchWindows) OfferWindows(find, pool.Values.Select(x => x.Hit.Entry).Where(e => e?.Kind == LauncherIndex.Kind.App).ToList()!, Offer);
            if (_settings.SearchClipboard) OfferKept(find, Offer);
        }

        // with a clear match, letters-in-order guesses ("tcno account switcher" for "notch") are only noise
        if (pool.Count > 0 && pool.Values.Max(x => x.Score) >= 600)
            foreach (var weak in pool.Where(x => x.Value.Score < 260).Select(x => x.Key).ToList()) pool.Remove(weak);

        Lap("files+windows+kept");
        // sections in a fixed order of importance: apps first, then what's open, the desktop, settings, the notch's
        // own commands, files, what you copied; inside each, the best first; a few lines per kind
        var sections = pool.Values
            .GroupBy(x => x.Hit.Section ?? "")
            .Select(g => (Name: g.Key, Lines: g.OrderByDescending(x => x.Score).Take(SectionCap(g.Key)).ToList()))
            .Select(g => (g.Name, g.Lines, Best: g.Lines[0].Score))
            .OrderBy(g => SectionRank(g.Name))
            .ThenByDescending(g => g.Best)
            .ToList();
        double best = sections.Count > 0 ? sections.Max(g => g.Best) : 0;
        // a web search that only might be meant ("youtube music" is also an app) goes after a strong match, else first
        if (soft != null && best < 790) { _hits.Insert(understoodCount, soft); understoodCount++; soft = null; }

        // a real question ("how do I…", "why is the sky blue?") with nothing on the PC to match: ChatGPT, already asked, first
        string query = q.Trim();
        bool question = query.EndsWith('?') || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4
            || System.Text.RegularExpressions.Regex.IsMatch(query, @"^(?:how|what|why|who|when|where|which|can|could|should|is|are|do|does|explain|write|summari[sz]e|help|tell me|give me|make me)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var (aiName, aiUrl) = Assistant(_settings.SearchAssistant);
        Hit? ask = !understood && query.Length >= 3 && aiUrl != null
            ? new Hit($"Ask {aiName} “{query}”", "Opens in your browser, already asked", "", null,
                () => OpenTarget(aiUrl + Uri.EscapeDataString(query)), Section: "Web", Key: "web|ask")
            : null;
        if (ask != null && question && best < 650) { _hits.Add(ask with { Section = null }); ask = null; }

        foreach (var s in sections)
            foreach (var line in s.Lines) _hits.Add(line.Hit);

        // 4. always a way out: the web
        if (soft != null) _hits.Add(soft with { Section = "Web" });
        var (engine, engineUrl) = Engine(_settings.SearchEngine);
        if (!understood)
            _hits.Add(new Hit($"Search the web for “{query}”", "With " + engine, "", null,
                () => OpenTarget(engineUrl + Uri.EscapeDataString(query)), Section: "Web", Key: "web|search"));
        if (ask != null) _hits.Add(ask);

        _launchSel = 0;
        if (keepKey != null && _hits.FindIndex(h => h.Key == keepKey) is >= 0 and var at) _launchSel = at;
        Lap("sort");
        RenderHits(animate: !merge);
        Lap("draw");
        UpdateGhost();
        if (_measureSearch) AppLog.Write("  laps: " + string.Join(", ", laps));
    }

    internal static (string Name, string? Url) Assistant(string key) => key switch
    {
        "claude" => ("Claude", "https://claude.ai/new?q="),
        "perplexity" => ("Perplexity", "https://www.perplexity.ai/search?q="),
        "copilot" => ("Copilot", "https://copilot.microsoft.com/?q="),
        "off" => ("", null),
        _ => ("ChatGPT", "https://chatgpt.com/?q="),
    };

    internal static (string Name, string Url) Engine(string key) => key switch
    {
        "bing" => ("Bing", "https://www.bing.com/search?q="),
        "duckduckgo" => ("DuckDuckGo", "https://duckduckgo.com/?q="),
        "brave" => ("Brave Search", "https://search.brave.com/search?q="),
        _ => ("Google", "https://www.google.com/search?q="),
    };

    /// <summary>The order sections always come in, most important first.</summary>
    static int SectionRank(string section) => section switch
    {
        "Apps" => 0, "Open windows" => 1, "Desktop" => 2, "Settings" => 3, "Notch" => 4, "Files" => 5, "Clipboard & shelf" => 6, _ => 7,
    };

    static int SectionCap(string section) => section switch { "Apps" => 6, "Files" => 8, "Desktop" => 6, "Open windows" => 4, "Settings" => 4, "Clipboard & shelf" => 4, _ => 4 };

    static string ParentName(string path)
    {
        try { return Path.GetFileName(Path.GetDirectoryName(path.TrimEnd('\\')) ?? "") is { Length: > 0 } n ? n : path; } catch { return ""; }
    }

    Hit EntryHit(LauncherIndex.Entry e, string? find = null, WindowsSearch.Item? item = null, string? inside = null)
    {
        bool shellPlace = e.Target.StartsWith("shell:") && e.Kind == LauncherIndex.Kind.Folder;
        string? glyph = shellPlace ? "" : e.Kind == LauncherIndex.Kind.Setting ? "" : null;
        string section = e.Kind switch { LauncherIndex.Kind.App => "Apps", LauncherIndex.Kind.Setting => "Settings", _ => OnDesktop(e.Target) ? "Desktop" : "Files" };
        bool onDisk = e.Kind is LauncherIndex.Kind.File or LauncherIndex.Kind.Folder && !shellPlace;
        bool realFile = onDisk && !e.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        Action? reveal = realFile ? () => RevealInFolder(e.Target) : null;
        string key = onDisk ? e.Target : e.Kind + "|" + e.Target;

        // an app that's already open: Enter goes to it instead of opening a second one
        if (e.Kind == LauncherIndex.Kind.App && WindowFor(e) is { } win)
            return new Hit(e.Name, "Open  ·  Enter switches to it", null, e.Target, () => { _index.Used(e); CloseLauncher(giveBack: false); FocusWindow(win.Hwnd); }, e,
                Complete: e.Name, Section: section, Highlight: find, Actions: () => EntryActions(e, win), Key: key);

        string detail = e.Kind switch
        {
            LauncherIndex.Kind.App => "App",
            LauncherIndex.Kind.Setting => "Settings",
            LauncherIndex.Kind.Folder => e.Detail == "Place" ? "Folder" : "Folder · " + e.Detail,
            _ => "File · " + e.Detail,
        };
        if (onDisk) detail = FileDetail(e, item, inside);
        string? preview = realFile && e.Kind == LauncherIndex.Kind.File && IsPicture(e.Target) ? e.Target : null;
        string title = IsShortcut(e.Target) ? Path.GetFileNameWithoutExtension(e.Name) : e.Name;
        return new Hit(title, detail, glyph, glyph == null ? e.Target : null, () => { _index.Used(e); OpenTarget(e.Target); }, e,
            Reveal: reveal, Complete: title, Section: section, Highlight: inside == null ? find : null,
            Actions: e.Kind == LauncherIndex.Kind.Setting || shellPlace ? null : () => EntryActions(e, null), Preview: preview, Key: key);
    }

    /// <summary>"Downloads · 2.4 MB · 3 Nov" (the folder it's in, its size, when it last changed).</summary>
    string FileDetail(LauncherIndex.Entry e, WindowsSearch.Item? item, string? inside)
    {
        string where = e.Detail is { Length: > 0 } d && d != "Recent" ? d : ParentName(e.Target);
        if (OnDesktop(e.Target)) where = "Desktop";
        if (IsShortcut(e.Target)) return OnDesktop(e.Target) ? "Shortcut · " + where : "Recent";
        DateTime when = item?.Modified ?? DateTime.MinValue;
        long size = item?.Size ?? -1;
        if (item == null) (when, size) = FileFacts(e.Target, e.Kind == LauncherIndex.Kind.Folder);
        else if (size < 0 && e.Kind == LauncherIndex.Kind.File) size = FileFacts(e.Target, false).Size;
        var parts = new List<string>();
        if (inside != null) parts.Add($"“{inside}” inside");
        parts.Add(where);
        if (e.Kind == LauncherIndex.Kind.File && size >= 0) parts.Add(SystemMonitor.Bytes(size));
        if (when > DateTime.MinValue.AddYears(1)) parts.Add(Stamp(when));
        return string.Join(" · ", parts);
    }

    static string Stamp(DateTime t)
    {
        var c = System.Globalization.CultureInfo.CurrentCulture;
        if (t.Date == DateTime.Today) return t.ToString("t", c);
        if (t.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        return t.Year == DateTime.Today.Year ? t.ToString("d MMM", c) : t.ToString("d MMM yyyy", c);
    }

    static bool IsPicture(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".ico";

    static void RevealInFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    /// <summary>What the notch can do when asked: a timer, focus, mute, a theme, the desktop's icons, sleep...</summary>
    IEnumerable<Hit> Commands(string q)
    {
        string s = q.Trim().ToLowerInvariant();
        var all = new (string Name, string Words, string Glyph, string Detail, Action Run)[]
        {
            ("Focus", "focus concentrate do not disturb dnd", "", _focus.IsOn ? "Turn focus off" : "Hold notifications while you work", () => { _focus.Toggle(); CloseLauncher(true); }),
            ("Mute", "mute unmute sound volume silence", "", _audio.Muted ? "Turn the sound back on" : "Silence the PC", () => { _audio.ToggleMute(); RefreshVolume(); CloseLauncher(true); }),
            ("Desktop icons", "desktop icons hide show", "", "Show or hide the icons on your desktop", () => { DesktopIcons.Toggle(); After(400, RefreshDesktopBtn); CloseLauncher(true); }),
            ("Clipboard history", "clipboard copied paste history", "", "What you copied lately", () => { CloseLauncherTo(3); }),
            ("Screenshot", "screenshot snip capture screen", "", "Snip part of the screen", () => { CloseLauncher(false); After(350, () => Launch("ms-screenclip:")); }),
            ("Lock the PC", "lock pc computer away", "", "Back to the sign-in screen", () => { CloseLauncher(false); LockWorkStation(); }),
            ("Sleep", "sleep suspend pc computer", "", "Put the PC to sleep now", () => { CloseLauncher(false); After(500, () => SetSuspendState(false, false, false)); }),
            ("Empty the Recycle Bin", "empty recycle bin trash delete", "", "Frees the space for good", () => { SHEmptyRecycleBin(IntPtr.Zero, null, 0x7); CloseLauncher(true); }),
            ("Notch preferences", "preferences settings notch options", "", "Everything about the notch", () => { CloseLauncher(false); PreferencesWindow.ShowFor(this); }),
        };
        foreach (var c in all)
            if (c.Name.ToLowerInvariant().StartsWith(s) || s.Length >= 3 && c.Words.Split(' ').Any(w => w.StartsWith(s)))
                yield return new Hit(c.Name, c.Detail, c.Glyph, null, c.Run, Complete: c.Name);
        foreach (var key in ThemeOrder)
        {
            string name = ThemeNames.TryGetValue(key, out var n) ? n : key;
            if (s.Length >= 3 && (name.ToLowerInvariant().StartsWith(s) || ("theme " + name.ToLowerInvariant()).StartsWith(s)))
                yield return new Hit($"{name} theme", ThemeKey == key ? "The theme you're on" : "Switch the notch's look", "", null,
                    () => { SetTheme(key); After(250, () => { StyleLauncher(); Search(); }); }, Complete: name);
        }
    }

    // ───────────────────────────── drawing the results ─────────────────────────────

    /// <summary>The title, with the letters you typed lit up so it's clear why it's there.</summary>
    void FillTitle(TextBlock tb, Hit h)
    {
        bool[]? marks = h.Highlight is { Length: > 0 } q && !h.Big ? Marks(h.Title, q) : null;
        if (marks == null || !marks.Contains(true)) { tb.Text = h.Title; return; }
        var lit = Frozen(Skin.Lit);
        var dim = Frozen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
        int i = 0;
        while (i < h.Title.Length)
        {
            int j = i;
            while (j < h.Title.Length && marks[j] == marks[i]) j++;
            var run = new System.Windows.Documents.Run(h.Title[i..j]) { Foreground = marks[i] ? lit : dim };
            if (marks[i]) run.FontWeight = FontWeights.SemiBold;
            tb.Inlines.Add(run);
            i = j;
        }
    }

    /// <summary>Which letters of a name the query matched: the whole query, else each word's start, else the initials, else the letters in order.</summary>
    static bool[] Marks(string title, string query)
    {
        string t = title.ToLowerInvariant(), q = query.Trim().ToLowerInvariant();
        var m = new bool[t.Length];
        if (q.Length == 0 || t.Length == 0) return m;
        int at = t.IndexOf(q, StringComparison.Ordinal);
        if (at >= 0) { for (int k = 0; k < q.Length; k++) m[at + k] = true; return m; }
        bool all = true;
        foreach (var w in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int p = -1;
            for (int k = 0; k <= t.Length - w.Length; k++)
                if ((k == 0 || !char.IsLetterOrDigit(t[k - 1])) && string.CompareOrdinal(t, k, w, 0, w.Length) == 0) { p = k; break; }
            if (p < 0) p = t.IndexOf(w, StringComparison.Ordinal);
            if (p < 0) { all = false; break; }
            for (int k = 0; k < w.Length; k++) m[p + k] = true;
        }
        if (all) return m;
        Array.Clear(m);
        // initials ("vsc" → Visual Studio Code)
        int qi = 0;
        for (int k = 0; k < t.Length && qi < q.Length; k++)
            if (char.IsLetterOrDigit(t[k]) && (k == 0 || !char.IsLetterOrDigit(t[k - 1])) && t[k] == q[qi]) { m[k] = true; qi++; }
        if (qi == q.Length) return m;
        Array.Clear(m);
        // the letters in order
        qi = 0;
        for (int k = 0; k < t.Length && qi < q.Length; k++)
            if (t[k] == q[qi]) { m[k] = true; qi++; }
        if (qi < q.Length) Array.Clear(m);
        return m;
    }

    void SetFoot(string text) { if (_launchFoot != null) _launchFoot.Text = text; }

    // ───────────────────────────── the grey completion ─────────────────────────────

    string? _ghost;

    /// <summary>What Enter will open, finished in grey after what you typed ("spo" + "tify"); Tab takes it.</summary>
    void UpdateGhost()
    {
        string? ghost = null;
        if (_launchBox != null && _actionsFor == null && _launchBox.CaretIndex == _launchBox.Text.Length && _launchBox.SelectionLength == 0
            && _hits.Count > 0 && _launchSel == 0 && _hits[0].Complete is { } c && _hits[0].Entry != null)
        {
            string typed = _launchBox.Text;
            if (typed.Length > 0 && typed.Length < c.Length && c.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && !typed.EndsWith(' ')) ghost = c[typed.Length..];
        }
        SetGhost(ghost);
    }

    void SetGhost(string? ghost)
    {
        _ghost = ghost;
        if (_launchGhost == null || _launchBox == null) return;
        if (ghost == null) { _launchGhost.Visibility = Visibility.Collapsed; return; }
        var tf = new Typeface(_launchBox.FontFamily, _launchBox.FontStyle, _launchBox.FontWeight, _launchBox.FontStretch);
        var ft = new FormattedText(_launchBox.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, _launchBox.FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _launchGhost.FontFamily = _launchBox.FontFamily;
        _launchGhost.FontSize = _launchBox.FontSize;
        _launchGhost.Margin = new Thickness(2 + ft.WidthIncludingTrailingWhitespace, 0, 0, 0);
        _launchGhost.Text = ghost;
        _launchGhost.Visibility = Visibility.Visible;
    }

    // ───────────────────────────── the empty search ─────────────────────────────

    /// <summary>The empty search: your usual apps as a row of icons, what's coming up, what you searched lately, a few things to try.</summary>
    void RenderStarters()
    {
        if (_launchStarters == null) return;
        _launchStarters.Children.Clear();
        var usual = _settings.SearchFrequent ? _index.Favourites(7, _settings.SearchHiddenApps).ToList() : new();
        bool first = true;
        double Gap() { double g = first ? 2 : 8; first = false; return g; }
        AddDesktopTiles(Gap);
        if (_desktopHidden && DesktopItems().Count > 0 && usual.Count > 6) usual = usual.Take(6).ToList(); // room for both rows
        if (usual.Count > 0)
        {
            _launchStarters.Children.Add(new TextBlock { Text = _cyber ? "FREQUENT" : "Your usual", FontSize = 10, Foreground = DimBrush, Margin = new Thickness(4, Gap(), 0, 6) });
            var icons = new WrapPanel();
            foreach (var e in usual.Take(TilesPerRow)) icons.Children.Add(FrequentTile(e));
            _launchStarters.Children.Add(icons);
        }
        // what's coming up: your reminders, a click lists them
        if (_reminders.Count > 0)
        {
            _launchStarters.Children.Add(new TextBlock { Text = _cyber ? "QUEUED" : "Coming up", FontSize = 10, Foreground = DimBrush, Margin = new Thickness(4, Gap(), 0, 6) });
            var soon = new WrapPanel();
            foreach (var r in _reminders.Take(3))
            {
                var c = System.Globalization.CultureInfo.CurrentCulture;
                string when = r.At.Date == DateTime.Today ? r.At.ToString("t", c) : r.At.ToString("ddd", c) + " " + r.At.ToString("t", c);
                soon.Children.Add(Chip($"{when}  ·  {Trim(r.Text, 26)}", "reminders", Frozen(_cyber ? CyYellow : Color.FromRgb(0xFF, 0xC8, 0x60))));
            }
            _launchStarters.Children.Add(soon);
        }
        // what you searched lately
        var recent = _settings.SearchRecent ? _index.Recent.Take(5).ToList() : new();
        if (recent.Count > 0)
        {
            _launchStarters.Children.Add(new TextBlock { Text = _cyber ? "RECENT" : "Recent", FontSize = 10, Foreground = DimBrush, Margin = new Thickness(4, Gap(), 0, 6) });
            var row = new WrapPanel();
            foreach (var r in recent) row.Children.Add(Chip(Trim(r, 30), r, null));
            _launchStarters.Children.Add(row);
        }
        // a few things to try, while there's room
        if (_settings.SearchTry && recent.Count < 3 && (_reminders.Count == 0 || usual.Count == 0))
        {
            _launchStarters.Children.Add(new TextBlock { Text = _cyber ? "TRY" : "Try", FontSize = 10, Foreground = DimBrush, Margin = new Thickness(4, Gap(), 0, 6) });
            var tries = new WrapPanel();
            foreach (var t in new[] { "remind me in 20 min to stretch", "volume 30 and dark mode", "time in tokyo", "days until new year", "15% of 80", "flip a coin" })
                tries.Children.Add(Chip(t, t, null));
            _launchStarters.Children.Add(tries);
        }
    }

    /// <summary>
    /// One of your usual apps: a click opens it; a small × in its corner (shown on hover), or a right-click, takes it out
    /// of the row for good (Ctrl+Z puts it back; Preferences → Search too).
    /// </summary>
    FrameworkElement FrequentTile(LauncherIndex.Entry e)
    {
        var tile = new StackPanel { Width = 54 };
        var icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(_cyber ? 0 : 8), HorizontalAlignment = HorizontalAlignment.Center };
        IconInto(icon, e.Target);
        tile.Children.Add(icon);
        tile.Children.Add(new TextBlock { Text = e.Name, FontSize = 9.5, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 4, 2, 0), Foreground = DimBrush });
        var open = new Button { Style = (Style)FindResource("RowButton"), Content = tile, Height = 54, ToolTip = e.Name + "  ·  right-click removes it from here" };
        open.Click += (_, _) => { _index.Used(e); OpenTarget(e.Target); };

        var x = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(_cyber ? 0 : 7.5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 7, 0), Cursor = Cursors.Hand, Opacity = 0, ToolTip = "Remove from Frequent",
            Background = Frozen(_cyber ? Color.FromRgb(0x2A, 0x08, 0x10) : Color.FromRgb(0x3A, 0x3A, 0x42)),
            BorderBrush = Frozen(_cyber ? CyRed : Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(_cyber ? 1 : 0.5),
            Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = Frozen(_cyber ? CyRed : Colors.White) },
        };
        var host = new Grid { Margin = new Thickness(0, 0, 2, 0) };
        host.Children.Add(open);
        host.Children.Add(x);
        host.MouseEnter += (_, _) => x.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        host.MouseLeave += (_, _) => x.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
        x.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; HideFrequent(e, host); };
        open.MouseRightButtonUp += (_, ev) => { ev.Handled = true; HideFrequent(e, host); };
        return host;
    }

    LauncherIndex.Entry? _lastHidden;

    void HideFrequent(LauncherIndex.Entry e, FrameworkElement tile)
    {
        if (!_settings.SearchHiddenApps.Contains(e.Target)) _settings.SearchHiddenApps.Add(e.Target);
        _settings.Save();
        _lastHidden = e;
        // it folds away, then the row closes up
        tile.IsHitTestVisible = false;
        var scale = new ScaleTransform(1, 1, 27, 27);
        tile.RenderTransform = scale;
        var shrink = new DoubleAnimation(1, 0.6, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170));
        fade.Completed += (_, _) =>
        {
            if (_launchBox is not { Text.Length: 0 }) return;
            RenderStarters();
            SetFoot($"{Trim(e.Name, 30)} removed from Frequent  ·  Ctrl+Z puts it back");
        };
        tile.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Ctrl+Z in the empty box: the app you just took out of Frequent comes back.</summary>
    bool UndoHideFrequent()
    {
        if (_lastHidden == null) return false;
        _settings.SearchHiddenApps.Remove(_lastHidden.Target);
        _settings.Save();
        _lastHidden = null;
        Search();
        return true;
    }

    Button Chip(string label, string text, Brush? foreground)
    {
        var chip = new Button { Style = (Style)FindResource("Capsule"), Content = label, Height = 24, Padding = new Thickness(10, 0, 10, 0), FontSize = 11, Margin = new Thickness(0, 0, 6, 6) };
        if (foreground != null) chip.Foreground = foreground;
        chip.Click += (_, _) => { if (_launchBox == null) return; _launchBox.Text = text; _launchBox.CaretIndex = text.Length; _launchBox.Focus(); };
        return chip;
    }

    // ───────────────────────────── keys and running ─────────────────────────────

    int _recallPos = -1;
    bool _pickedByKeys; // you moved to a line yourself: results arriving later keep it picked
    bool _recalling;

    void OnLauncherKey(object sender, KeyEventArgs e)
    {
        if (_launchBox == null) return;
        if (e.Key is Key.Enter or Key.Tab or Key.Up or Key.Down or Key.Right or Key.PageUp or Key.PageDown) SearchNowIfBehind();
        int count = _hits.Count;
        bool atEnd = _launchBox.CaretIndex == _launchBox.Text.Length && _launchBox.SelectionLength == 0;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_actionsFor != null) LeaveActions();
                else if (_launchBox.Text.Length > 0) _launchBox.Text = "";
                else CloseLauncher(giveBack: true);
                break;
            case Key.Down:
                e.Handled = true;
                _pickedByKeys = true;
                if (_launchSel + 2 >= RenderFirst) RenderRest();
                if (count > 0) { _launchSel = (_launchSel + 1) % count; PaintSelection(); SetGhost(null); }
                break;
            case Key.Up:
                e.Handled = true;
                _pickedByKeys = true;
                // in the empty box (or while going back through them): what you searched before, newest first
                if (_actionsFor == null && _settings.SearchRecent && _index.Recent.Count > 0 && (_launchBox.Text.Length == 0 || _recallPos >= 0 && _launchSel == 0 && _recallPos < _index.Recent.Count && _launchBox.Text == _index.Recent[_recallPos]))
                {
                    if (_recallPos + 1 >= _index.Recent.Count) { Bump(1); break; }
                    _recallPos++;
                    _recalling = true;
                    _launchBox.Text = _index.Recent[_recallPos];
                    _launchBox.CaretIndex = _launchBox.Text.Length;
                    _recalling = false;
                    break;
                }
                if (count > 0)
                {
                    if (_launchSel == 0) RenderRest(); // round to the last line: draw it first
                    _launchSel = (_launchSel - 1 + count) % count;
                    PaintSelection();
                    if (_launchSel == 0) UpdateGhost(); else SetGhost(null);
                }
                break;
            case Key.PageDown:
                e.Handled = true;
                _pickedByKeys = true;
                RenderRest();
                if (count > 0) { _launchSel = Math.Min(count - 1, _launchSel + 6); PaintSelection(); SetGhost(null); }
                break;
            case Key.PageUp:
                e.Handled = true;
                _pickedByKeys = true;
                if (count > 0) { _launchSel = Math.Max(0, _launchSel - 6); PaintSelection(); if (_launchSel == 0) UpdateGhost(); }
                break;
            case Key.Tab:
                e.Handled = true;
                if (_actionsFor != null) break;
                if (_ghost != null) { string full = _launchBox.Text + _ghost; _launchBox.Text = full; _launchBox.CaretIndex = full.Length; }
                else if (_launchSel < _hits.Count && _hits[_launchSel].Complete is { } c) { _launchBox.Text = c; _launchBox.CaretIndex = c.Length; }
                break;
            case Key.Right when atEnd && _actionsFor == null && _launchSel < count && _hits[_launchSel].Actions != null:
                e.Handled = true;
                EnterActions();
                break;
            case Key.Left when _actionsFor != null:
            case Key.Back when _actionsFor != null:
                e.Handled = true;
                LeaveActions();
                break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control && _launchBox.Text.Length == 0:
                e.Handled = UndoHideFrequent();
                break;
            case Key.Enter:
                e.Handled = true;
                Run(_launchSel, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                break;
        }
    }

    void Run(int index, bool reveal)
    {
        if (index < 0 || index >= _hits.Count || _launchBox == null) return;
        var h = _hits[index];
        string typed = _launchBox.Text;
        var picked = _actionsFor?.Entry ?? h.Entry;
        if (reveal && h.Reveal != null) { CloseLauncher(giveBack: false); h.Reveal(); }
        else h.Run();
        // it did something (the search closed): remember what was typed, and what it meant
        if (!_launcherOpen)
        {
            if (picked != null && _settings.SearchLearn) _index.Picked(typed, picked);
            if (_settings.SearchRecent) _index.Searched(typed);
        }
    }

    /// <summary>Opens an app, a file, a page or a settings page; the app that opens takes the keyboard.</summary>
    void OpenTarget(string target)
    {
        CloseLauncher(giveBack: false);
        try
        {
            if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Write("launcher: " + ex.Message); }
    }

    /// <summary>A sum or a conversion: the result goes to the clipboard (and into its history), with a nod.</summary>
    void CopyAnswer(string text)
    {
        try { Clipboard.SetText(text); } catch { }
        CloseLauncher(giveBack: true);
        After(200, () => ShowOsd("copied", "", null, "Copied", text, null, glyphBrush: Blue, seconds: 1.4, prio: Prio.Low));
    }

    void CloseLauncherTo(int page)
    {
        CloseLauncher(giveBack: false);
        After(250, () => ExpandTo(page));
    }

    // ───────────────────────────── type to search ─────────────────────────────
    // With the notch open and the pointer on it, the first letter you type opens the search with that letter in it.
    // A keyboard hook runs only in that moment (notch open, pointer on it, search closed); it looks at one key, and
    // only lets it through to the notch: nothing else is read, nothing is kept.

    IntPtr _typeHook;
    HookProc? _typeProc;

    void UpdateTypeToSearch(bool pointerInside)
    {
        bool want = pointerInside && _settings.SearchTypeToOpen && _mode == Mode.Expanded && !_launcherOpen && !_settings.Locked && _game == null;
        if (want == (_typeHook != IntPtr.Zero)) return;
        if (want)
        {
            _typeProc ??= OnTypeHook;
            _typeHook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _typeProc, GetModuleHandle(null), 0);
        }
        else
        {
            UnhookWindowsHookEx(_typeHook);
            _typeHook = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vk, scan, flags, time; public IntPtr extra; }

    IntPtr OnTypeHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() == 0x0100 /* WM_KEYDOWN */)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & 0x10) != 0;
            bool mods = (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
            if (!injected && !mods && TypedChar(k.vk, k.scan) is { } ch)
            {
                Dispatcher.BeginInvoke(() => OpenLauncher(ch));
                return new IntPtr(1); // that key is the notch's: the app underneath doesn't get it
            }
        }
        return CallNextHookEx(_typeHook, code, wParam, lParam);
    }

    /// <summary>The character a key types on the keyboard layout in use (letters, digits, signs; not space or controls).</summary>
    static string? TypedChar(uint vk, uint scan)
    {
        if (vk is < 0x30 or > 0xFE || vk is >= 0x5B and <= 0x5F || vk is >= 0x70 and <= 0x87 || vk is >= 0xA0 and <= 0xA5) return null;
        var state = new byte[256];
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) state[0x10] = 0x80;
        if ((GetKeyState(0x14) & 1) != 0) state[0x14] = 0x01;
        var fg = GetForegroundWindow();
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(fg, out _));
        var sb = new System.Text.StringBuilder(8);
        int n = ToUnicodeEx(vk, scan, state, sb, sb.Capacity, 0x4 /* don't change the keyboard's state */, layout);
        if (n != 1) return null;
        char c = sb[0];
        return char.IsControl(c) || char.IsWhiteSpace(c) ? null : c.ToString();
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern short GetKeyState(int key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, System.Text.StringBuilder buf, int size, uint flags, IntPtr layout);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
}
