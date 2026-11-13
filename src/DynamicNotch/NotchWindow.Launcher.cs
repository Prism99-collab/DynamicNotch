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
    StackPanel? _launchList, _launchStarters;
    Button? _searchBtn;
    bool _launcherOpen;
    IntPtr _launchPrevFg;
    int _launchSel;
    readonly List<Hit> _hits = new();

    /// <summary>One line in the results: what it says, how it looks, what Enter does.</summary>
    sealed record Hit(string Title, string Detail, string? Glyph, string? IconPath, Action Run,
        LauncherIndex.Entry? Entry = null, string? Swatch = null, bool Big = false, Action? Reveal = null, string? Complete = null);

    // ───────────────────────────── wiring ─────────────────────────────

    void WireLauncher()
    {
        _index.Refresh();
        LoadReminders();
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
        _procInfo.Clear(); // process ids get reused: read the open apps afresh each time
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
        if (!RegisterHotKey(_hwnd, LauncherHotkeyId, MOD_ALT | MOD_NOREPEAT, 0x20 /* Space */))
            AppLog.Write("Alt+Space is taken by another app: open the search from the magnifier in the notch");
    }

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
        _launchBox.TextChanged += (_, _) => Search();
        _launchBox.PreviewKeyDown += OnLauncherKey;
        Grid.SetColumn(_launchBox, 1);
        line.Children.Add(_launchBox);
        _launchHint = new TextBlock { FontSize = 13, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 1) };
        Grid.SetColumn(_launchHint, 1);
        line.Children.Add(_launchHint);
        var esc = new TextBlock { Text = "esc", FontSize = 10, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        esc.MouseLeftButtonUp += (_, _) => CloseLauncher(giveBack: true);
        Grid.SetColumn(esc, 2);
        line.Children.Add(esc);
        var underline = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), Tag = "line" };
        Grid.SetColumnSpan(underline, 3);
        line.Children.Add(underline);
        root.Children.Add(line);

        // the results (or, while empty, what you use most and a few things to try)
        var body = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        _launchList = new StackPanel();
        _launchStarters = new StackPanel();
        body.Children.Add(_launchList);
        body.Children.Add(_launchStarters);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        _launchFoot = new TextBlock { FontSize = 9.5, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(_launchFoot, 2);
        root.Children.Add(_launchFoot);

        _launch = new Border { Child = root, Visibility = Visibility.Collapsed, Opacity = 0, RenderTransform = _launchScale };
        Grid.SetRowSpan(_launch, 3);
        Panel.SetZIndex(_launch, 40);
        Expanded.Children.Add(_launch);
        _index.Changed += () => Dispatcher.InvokeAsync(() => { if (_launcherOpen) Search(); });
    }

    /// <summary>The theme's own plate: Neon's night with a cyan rim and a yellow prompt, dark glass elsewhere.</summary>
    void StyleLauncher()
    {
        if (_launch == null || _launchBox == null || _launchPrompt == null) return;
        _launch.Background = Frozen(_cyber ? Color.FromArgb(0xFA, 0x03, 0x08, 0x0C) : Color.FromArgb(0xFA, 0x10, 0x11, 0x15));
        _launch.BorderBrush = Frozen(_cyber ? Color.FromArgb(0x70, CyCyan.R, CyCyan.G, CyCyan.B) : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
        _launch.BorderThickness = new Thickness(_cyber ? 1 : 0);
        var accent = LaunchAccent;
        _launchPrompt.Text = _cyber ? "›" : "";
        _launchPrompt.FontFamily = _cyber ? CyFont : (FontFamily)FindResource("IconFont");
        _launchPrompt.FontSize = _cyber ? 22 : 14;
        _launchPrompt.Foreground = Frozen(_cyber ? CyYellow : Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        _launchBox.FontFamily = _cyber ? CyFont : new FontFamily("Segoe UI Variable Display, Segoe UI");
        _launchBox.Foreground = _cyber ? Frozen(CyYellow) : Brushes.White;
        _launchBox.CaretBrush = Frozen(accent);
        _launchBox.SelectionBrush = Frozen(accent);
        if (((Grid)_launchBox.Parent).Children.OfType<Border>().FirstOrDefault(b => (string?)b.Tag == "line") is { } line)
            line.Background = Frozen(_cyber ? Color.FromArgb(0x90, CyCyan.R, CyCyan.G, CyCyan.B) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    }

    Color LaunchAccent => _cyber ? CyCyan : _accent == Colors.White ? Color.FromRgb(0x0A, 0x84, 0xFF) : _accent;

    // ───────────────────────────── searching ─────────────────────────────

    void Search()
    {
        if (_launchBox == null || _launchList == null || _launchStarters == null || _launchHint == null) return;
        string q = _launchBox.Text;
        _launchHint.Text = q.Length == 0 ? (_cyber ? "SEARCH // ASK // COMMAND" : "Search, ask, or tell it what to do…") : "";
        _hits.Clear();
        if (q.Trim().Length == 0)
        {
            _launchList.Visibility = Visibility.Collapsed;
            _launchStarters.Visibility = Visibility.Visible;
            RenderStarters();
            SetFoot("Alt+Space opens this from anywhere");
            return;
        }
        _launchStarters.Visibility = Visibility.Collapsed;
        _launchList.Visibility = Visibility.Visible;

        // 1. something to do, said in your own words ("remind me…", "volume 30 and dark mode", "close spotify")
        string find = AddTaskHits(q, out bool understood, out var soft);
        // 2. an answer the notch works out itself: a sum, a conversion, the time somewhere, a date, the battery...
        if ((NotchFact(q) ?? LauncherIntent.Fact(q, DateTime.Now) ?? LauncherAnswers.For(q)) is { } a)
        {
            understood = true;
            if (a.Url != null)
                _hits.Add(new Hit(a.Title, a.Detail, "", null, () => OpenTarget(a.Url!)));
            else
                _hits.Add(new Hit(a.Title, a.Detail, a.Color != null ? null : "", null,
                    a.Copy.Length == 0 ? () => Nudge() : a.Secret ? () => CopySecret(a.Copy) : () => CopyAnswer(a.Copy), Swatch: a.Color, Big: true));
        }
        // 3. the notch's own commands: by name first; ones that only match a hidden word ("dis" → do not disturb) after the apps
        var commands = understood ? new List<Hit>() : Commands(q).ToList();
        string lower = q.Trim().ToLowerInvariant();
        foreach (var c in commands.Where(c => c.Title.ToLowerInvariant().StartsWith(lower))) _hits.Add(c);
        // 4. apps, settings, files: best first, with your habits
        var ranked = _index.Entries
            .Select(e => (e, s: LauncherIndex.Score(find, e)))
            .Where(x => x.s > 0)
            .Select(x => (x.e, s: x.s + _index.Habit(x.e)))
            .OrderByDescending(x => x.s)
            .Take(8 - Math.Min(3, _hits.Count))
            .ToList();
        for (int i = 0; i < ranked.Count; i++)
        {
            _hits.Add(EntryHit(ranked[i].e));
            if (i == 0 && soft != null && ranked[0].s >= 790) { _hits.Add(soft); soft = null; } // "youtube music" the app first, the search right after
        }
        if (soft != null) _hits.Insert(0, soft);
        foreach (var c in commands.Where(c => !_hits.Contains(c))) _hits.Add(c);
        // 5. always a way out: the web
        string query = q.Trim();
        if (!understood)
            _hits.Add(new Hit($"Search the web for “{query}”", "Opens in your browser", "", null,
                () => OpenTarget("https://www.google.com/search?q=" + Uri.EscapeDataString(query))));
        // a real question ("how do I…", "write me…", "why is the sky blue?"): ChatGPT, already asked, high up the list
        if (!understood && query.Length >= 3)
        {
            var askHit = new Hit($"Ask ChatGPT “{query}”", "Opens in your browser, already asked", "", null,
                () => OpenTarget("https://chatgpt.com/?q=" + Uri.EscapeDataString(query)));
            bool question = query.EndsWith('?') || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4
                || System.Text.RegularExpressions.Regex.IsMatch(query, @"^(?:how|what|why|who|when|where|which|can|could|should|is|are|do|does|explain|write|summari[sz]e|help|tell me|give me|make me)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (question) _hits.Insert(Math.Max(0, Math.Min(2, _hits.Count - 1)), askHit); else _hits.Add(askHit);
        }

        _launchSel = 0;
        RenderHits();
    }

    Hit EntryHit(LauncherIndex.Entry e)
    {
        string detail = e.Kind switch
        {
            LauncherIndex.Kind.App => "App",
            LauncherIndex.Kind.Setting => "Settings",
            LauncherIndex.Kind.Folder => e.Detail == "Place" ? "Folder" : "Folder · " + e.Detail,
            _ => "File · " + e.Detail,
        };
        string? glyph = e.Target.StartsWith("shell:") && e.Kind == LauncherIndex.Kind.Folder ? "" : e.Kind == LauncherIndex.Kind.Setting ? "" : null;
        Action? reveal = e.Kind is LauncherIndex.Kind.File or LauncherIndex.Kind.Folder && !e.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? () => { try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{e.Target}\"") { UseShellExecute = true }); } catch { } }
            : null;
        return new Hit(e.Name, detail, glyph, glyph == null ? e.Target : null, () => { _index.Used(e); OpenTarget(e.Target); }, e, Reveal: reveal, Complete: e.Name);
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

    void RenderHits()
    {
        if (_launchList == null) return;
        _launchList.Children.Clear();
        int shown = 0;
        foreach (var h in _hits)
        {
            if (shown >= 6) break; // what fits; the rest is a few letters away
            var row = HitRow(h, shown);
            _launchList.Children.Add(row);
            // a quick cascade as they arrive
            var tt = new TranslateTransform(0, 5);
            row.RenderTransform = tt;
            var delay = TimeSpan.FromMilliseconds(16 * shown);
            row.Opacity = 0;
            row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { BeginTime = delay });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(160)) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            shown++;
        }
        PaintSelection();
    }

    FrameworkElement HitRow(Hit h, int index)
    {
        var grid = new Grid { Height = h.Big ? 36 : 28 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement lead;
        if (h.Swatch != null)
            lead = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(_cyber ? 0 : 6), Background = Frozen((Color)ColorConverter.ConvertFromString(h.Swatch)),
                BorderBrush = Frozen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1) };
        else if (h.IconPath != null)
            lead = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = IconBrush(LauncherIcon(h.IconPath)) };
        else
            lead = new TextBlock { Text = h.Glyph ?? "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, Foreground = Frozen(_cyber ? CyCyan : Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)) };
        lead.HorizontalAlignment = HorizontalAlignment.Left;
        lead.VerticalAlignment = VerticalAlignment.Center;
        lead.Margin = new Thickness(6, 0, 0, 0);
        grid.Children.Add(lead);

        var title = new TextBlock
        {
            Text = h.Title, FontSize = h.Big ? 16 : 12.5, FontWeight = h.Big ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 8, 0),
            FontFamily = _cyber && h.Big ? CyFont : new FontFamily("Segoe UI Variable Text, Segoe UI"),
            Foreground = _cyber && h.Big ? Frozen(CyYellow) : Brushes.White,
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var detail = new TextBlock { Text = h.Detail, FontSize = 10, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), MaxWidth = h.Big ? 330 : 170, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(detail, 2);
        grid.Children.Add(detail);

        var plate = new Border { Child = grid, CornerRadius = new CornerRadius(_cyber ? 0 : 8), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = index };
        plate.MouseEnter += (_, _) => { _launchSel = index; PaintSelection(); };
        plate.MouseLeftButtonUp += (_, e) => { e.Handled = true; Run(index, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); };
        return plate;
    }

    /// <summary>The picked line: a soft accent wash (Neon: a cyan bar with a yellow tick on its left).</summary>
    void PaintSelection()
    {
        if (_launchList == null) return;
        var accent = LaunchAccent;
        foreach (var el in _launchList.Children.OfType<Border>())
        {
            bool on = el.Tag is int i && i == _launchSel;
            el.Background = on ? Frozen(Color.FromArgb(_cyber ? (byte)0x2A : (byte)0x26, accent.R, accent.G, accent.B)) : Brushes.Transparent;
            el.BorderBrush = on && _cyber ? Frozen(CyYellow) : null;
            el.BorderThickness = on && _cyber ? new Thickness(2, 0, 0, 0) : new Thickness(0);
        }
        var h = _launchSel < _hits.Count ? _hits[_launchSel] : null;
        SetFoot(h == null ? "" : h.Reveal != null ? "Enter opens  ·  Ctrl+Enter shows it in its folder  ·  Tab completes" : h.Complete != null ? "Enter opens  ·  Tab completes" : "Enter");
    }

    void SetFoot(string text) { if (_launchFoot != null) _launchFoot.Text = text; }

    /// <summary>The empty search: your usual apps as a row of icons, and a few things to try.</summary>
    void RenderStarters()
    {
        if (_launchStarters == null) return;
        _launchStarters.Children.Clear();
        var usual = _index.Favourites(7).ToList();
        if (usual.Count > 0)
        {
            _launchStarters.Children.Add(new TextBlock { Text = _cyber ? "FREQUENT" : "Your usual", FontSize = 10, Foreground = DimBrush, Margin = new Thickness(4, 2, 0, 6) });
            var icons = new WrapPanel();
            foreach (var e in usual)
            {
                var tile = new StackPanel { Width = 54 };
                tile.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(_cyber ? 0 : 8), Background = IconBrush(LauncherIcon(e.Target)), HorizontalAlignment = HorizontalAlignment.Center });
                tile.Children.Add(new TextBlock { Text = e.Name, FontSize = 9.5, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 4, 2, 0), Foreground = DimBrush });
                var b = new Button { Style = (Style)FindResource("RowButton"), Content = tile, Height = 54, Margin = new Thickness(0, 0, 2, 0), ToolTip = e.Name };
                var entry = e;
                b.Click += (_, _) => { _index.Used(entry); OpenTarget(entry.Target); };
                icons.Children.Add(b);
            }
            _launchStarters.Children.Add(icons);
        }
        // what's coming up: your reminders, a click lists them
        if (_reminders.Count > 0)
        {
            _launchStarters.Children.Add(new TextBlock { Text = _cyber ? "QUEUED" : "Coming up", FontSize = 10, Foreground = DimBrush, Margin = new Thickness(4, usual.Count > 0 ? 8 : 2, 0, 6) });
            var soon = new WrapPanel();
            foreach (var r in _reminders.Take(3))
            {
                var c = System.Globalization.CultureInfo.CurrentCulture;
                string when = r.At.Date == DateTime.Today ? r.At.ToString("t", c) : r.At.ToString("ddd", c) + " " + r.At.ToString("t", c);
                string text = r.Text.Length > 26 ? r.Text[..25] + "…" : r.Text;
                var chip = new Button { Style = (Style)FindResource("Capsule"), Content = $"{when}  ·  {text}", Height = 24, Padding = new Thickness(10, 0, 10, 0), FontSize = 11, Margin = new Thickness(0, 0, 6, 6),
                    Foreground = Frozen(_cyber ? CyYellow : Color.FromRgb(0xFF, 0xC8, 0x60)) };
                chip.Click += (_, _) => { if (_launchBox == null) return; _launchBox.Text = "reminders"; _launchBox.CaretIndex = _launchBox.Text.Length; _launchBox.Focus(); };
                soon.Children.Add(chip);
            }
            _launchStarters.Children.Add(soon);
        }
        _launchStarters.Children.Add(new TextBlock { Text = _cyber ? "TRY" : "Try", FontSize = 10, Foreground = DimBrush, Margin = new Thickness(4, usual.Count > 0 || _reminders.Count > 0 ? 8 : 2, 0, 6) });
        var tries = new WrapPanel();
        foreach (var t in new[] { "remind me in 20 min to stretch", "volume 30 and dark mode", "time in tokyo", "days until new year", "15% of 80", "flip a coin" })
        {
            var chip = new Button { Style = (Style)FindResource("Capsule"), Content = t, Height = 24, Padding = new Thickness(10, 0, 10, 0), FontSize = 11, Margin = new Thickness(0, 0, 6, 6) };
            string text = t;
            chip.Click += (_, _) => { if (_launchBox == null) return; _launchBox.Text = text; _launchBox.CaretIndex = text.Length; _launchBox.Focus(); };
            tries.Children.Add(chip);
        }
        _launchStarters.Children.Add(tries);
    }

    readonly Dictionary<string, ImageSource?> _launchIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app's or file's own icon (asked once, kept for the session).</summary>
    ImageSource? LauncherIcon(string target)
    {
        if (_launchIcons.TryGetValue(target, out var img)) return img;
        try { img = IconHelper.ForPath(target); } catch { img = null; }
        _launchIcons[target] = img;
        return img;
    }

    // ───────────────────────────── keys and running ─────────────────────────────

    void OnLauncherKey(object sender, KeyEventArgs e)
    {
        int count = Math.Min(6, _hits.Count);
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_launchBox is { Text.Length: > 0 }) _launchBox.Text = "";
                else CloseLauncher(giveBack: true);
                break;
            case Key.Down:
                e.Handled = true;
                if (count > 0) { _launchSel = (_launchSel + 1) % count; PaintSelection(); }
                break;
            case Key.Up:
                e.Handled = true;
                if (count > 0) { _launchSel = (_launchSel - 1 + count) % count; PaintSelection(); }
                break;
            case Key.Tab:
                e.Handled = true;
                if (_launchSel < _hits.Count && _hits[_launchSel].Complete is { } c && _launchBox != null) { _launchBox.Text = c; _launchBox.CaretIndex = c.Length; }
                break;
            case Key.Enter:
                e.Handled = true;
                Run(_launchSel, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                break;
        }
    }

    void Run(int index, bool reveal)
    {
        if (index < 0 || index >= _hits.Count) return;
        var h = _hits[index];
        if (reveal && h.Reveal != null) { CloseLauncher(giveBack: false); h.Reveal(); return; }
        h.Run();
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
        bool want = pointerInside && _mode == Mode.Expanded && !_launcherOpen && !_settings.Locked && _game == null;
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
