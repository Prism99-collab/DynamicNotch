using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicNotch.Controls;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>Swipeable pages: Home, System, Tools, Clipboard &amp; Files, Smart and Settings.</summary>
public partial class NotchWindow
{
    static readonly string[] PageNames = { "", "System", "Tools", "Clipboard & Files", "Smart", "Settings" };
    const double PageWidth = 440;

    int _page;
    readonly List<Border> _dots = new();

    // swipe state
    bool _maybeSwipe, _swiping;
    double _swipeBase, _swipeVelocity, _lastMoveX;
    DateTime _lastMoveAt;
    Point _downPos;
    DateTime _downAt;
    double _hwheel;
    DateTime _lastWheelPage;

    // system page
    readonly DispatcherTimer _sysTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };

    // timer
    TimeSpan _timerTotal = TimeSpan.FromMinutes(25), _timerLeft = TimeSpan.FromMinutes(25);
    DateTime _timerEnd;
    bool _timerRunning;
    readonly DispatcherTimer _timerTick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // tools
    DispatcherTimer? _sleepConfirm;
    QuickToggle? _sleepTile;

    // clipboard & files
    readonly List<string> _clipHistory = new();
    DateTime _ignoreClipboardUntil;
    bool _dragOut;
    Point _fileDown;

    readonly List<(ToggleButton Switch, Func<bool> Get)> _settingSwitches = new();

    void WirePages()
    {
        // page dots
        for (int i = 0; i < PageNames.Length; i++)
        {
            int idx = i;
            var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = DimBrush };
            var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(3, 5, 3, 5), Child = dot, Cursor = Cursors.Hand };
            hit.MouseLeftButtonUp += (_, e) => { GoToPage(idx); e.Handled = true; };
            _dots.Add(dot);
            Dots.Children.Add(hit);
        }
        UpdateDots(animate: false);

        // dragging with the mouse
        Pager.PreviewMouseLeftButtonDown += OnPagerDown;
        Pager.PreviewMouseMove += OnPagerMove;
        Pager.PreviewMouseLeftButtonUp += OnPagerUp;
        Pager.LostMouseCapture += (_, _) => { if (_swiping) EndSwipe(0, 0); };
        // Shift+wheel anywhere, or plain wheel over the status line / dots
        Expanded.PreviewMouseWheel += OnExpandedWheel;

        _sysTimer.Tick += async (_, _) => await SampleSystemAsync();

        // tools
        _timerTick.Tick += (_, _) => TimerTick();
        foreach (var chip in TimerChips.Children.OfType<Button>())
            chip.Click += (_, _) => SetTimer(TimeSpan.FromMinutes(int.Parse(chip.Tag.ToString()!)));
        TimerStart.Click += (_, _) => ToggleTimer();
        TimerReset.Click += (_, _) => ResetTimer();
        FocusTab.Click += (_, _) => SetTimerMode(breakTime: false);
        BreakTab.Click += (_, _) => SetTimerMode(breakTime: true);
        TimerFace.MouseLeftButtonUp += (_, e) => { ToggleTimer(); e.Handled = true; };
        TimerFace.MouseWheel += (_, e) => { NudgeTimer(e.Delta > 0 ? 1 : -1); e.Handled = true; };
        for (int i = 0; i < 4; i++)
            RoundDots.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 0, 0, 0) });
        foreach (var tile in ActionsGrid.Children.OfType<QuickToggle>())
        {
            tile.Click += (_, _) => RunAction((string)tile.Tag, tile);
            tile.MouseRightButtonUp += (_, e) =>
            {
                // a second, related action on right-click
                switch ((string)tile.Tag)
                {
                    case "clipboard": e.Handled = true; GoToPage(3); break;
                    case "explorer": e.Handled = true; if (_files.DownloadsDir != null) Launch(_files.DownloadsDir); break;
                }
            };
        }
        UpdateTimerUi();

        // clipboard & files
        ClipClear.Click += (_, _) => { _clipHistory.Clear(); RenderClipboard(); };
        OpenDownloads.Click += (_, _) => { if (_files.DownloadsDir != null) Launch(_files.DownloadsDir); };

        BuildSettings();
    }

    // ───────────────────────────── paging ─────────────────────────────

    /// <param name="velocity">Finger speed in px/s; the page keeps that momentum as it settles.</param>
    void GoToPage(int page, double velocity = 0, bool instant = false)
    {
        page = Math.Clamp(page, 0, PageNames.Length - 1);
        bool changed = page != _page;
        _page = page;
        _pageX.Configure(SpringStyle.Page);
        _pageX.Active = true;
        if (instant) { _pageX.Snap(-page * PageWidth); PagesShift.X = _pageX.Value; }
        else
        {
            _pageX.Value = PagesShift.X;
            _pageX.Velocity = Math.Clamp(velocity, -5000, 5000);
            _pageX.Target = -page * PageWidth;
            StartAnimating();
        }
        UpdateDots(animate: true);
        if (changed) OnPageShown(page);
        if (page != 0) ShowDevices(false);
    }

    void UpdateDots(bool animate)
    {
        PageTitle.Text = PageNames[_page];
        for (int i = 0; i < _dots.Count; i++)
        {
            double w = i == _page ? 16 : 5;
            if (animate) _dots[i].BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else _dots[i].Width = w;
            _dots[i].Background = i == _page ? Brushes.White : DimBrush;
        }
    }

    void OnPageShown(int page)
    {
        if (_mode != Mode.Expanded) return;
        if (page == 1) { _ = SampleSystemAsync(); _sysTimer.Start(); } else _sysTimer.Stop();
        if (page == 0) RefreshClockAndProgress();
        if (page == 2) UpdateTimerUi();
        if (page == 3) { RenderClipboard(); _ = RenderFilesAsync(); }
        if (page == 4) SyncSmartPage();
        if (page == 5) SyncSettingsPage();
    }

    void OnPagerDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<PillSlider>(e.OriginalSource as DependencyObject) != null) return;
        _maybeSwipe = true;
        _downPos = e.GetPosition(Pager);
        _downAt = DateTime.UtcNow;
    }

    void OnPagerMove(object sender, MouseEventArgs e)
    {
        if (!_maybeSwipe || _dragOut) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _maybeSwipe = false; return; }
        var p = e.GetPosition(Pager);
        double dx = p.X - _downPos.X, dy = p.Y - _downPos.Y;
        if (!_swiping)
        {
            if (Mouse.Captured is PillSlider) { _maybeSwipe = false; return; }
            if (Math.Abs(dx) < 8 || Math.Abs(dx) < Math.Abs(dy) * 1.2) return;
            _swiping = true;
            Pager.CaptureMouse(); // steals capture from any button, so it won't "click"
            _pageX.Active = false; // the finger drives the strip now
            _swipeBase = PagesShift.X + _page * PageWidth; // grab mid-flight without a jump
            _lastMoveX = p.X;
            _lastMoveAt = DateTime.UtcNow;
            _swipeVelocity = 0;
        }
        var now = DateTime.UtcNow;
        double moveMs = (now - _lastMoveAt).TotalMilliseconds;
        if (moveMs > 0)
        {
            double instant = (p.X - _lastMoveX) / moveMs * 1000; // px/s
            _swipeVelocity = _swipeVelocity * 0.35 + instant * 0.65;
            _lastMoveX = p.X;
            _lastMoveAt = now;
        }
        dx += _swipeBase;
        double x = -_page * PageWidth + dx;
        double min = -(PageNames.Length - 1) * PageWidth;
        if (x > 0) x *= 0.3;                       // rubber band at the ends
        else if (x < min) x = min + (x - min) * 0.3;
        PagesShift.X = x;
        e.Handled = true;
    }

    void OnPagerUp(object sender, MouseButtonEventArgs e)
    {
        _maybeSwipe = false;
        if (!_swiping) return;
        var p = e.GetPosition(Pager);
        double dx = p.X - _downPos.X + _swipeBase;
        // a pause before releasing means "no fling"
        double velocity = (DateTime.UtcNow - _lastMoveAt).TotalMilliseconds > 80 ? 0 : _swipeVelocity;
        e.Handled = true;
        EndSwipe(dx, velocity);
    }

    void EndSwipe(double dx, double velocity)
    {
        _swiping = false;
        if (Pager.IsMouseCaptured) Pager.ReleaseMouseCapture();
        // project where the flick would coast to, then pick the nearest page
        double projected = -_page * PageWidth + dx + velocity * 0.18;
        int target = (int)Math.Round(-projected / PageWidth);
        target = Math.Clamp(target, _page - 1, _page + 1);
        GoToPage(target, velocity);
    }

    void OnHorizontalWheel(short delta)
    {
        _hwheel += delta;
        if ((DateTime.UtcNow - _lastWheelPage).TotalMilliseconds < 420) { _hwheel = 0; return; }
        if (Math.Abs(_hwheel) < 90) return;
        GoToPage(_page + (_hwheel > 0 ? 1 : -1));
        _hwheel = 0;
        _lastWheelPage = DateTime.UtcNow;
    }

    void OnExpandedWheel(object sender, MouseWheelEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (FindParent<PillSlider>(src) != null || FindParent<ScrollViewer>(src) != null) return;
        bool overChrome = e.GetPosition(Expanded).Y < 34 || e.GetPosition(Expanded).Y > 34 + 216;
        if (Keyboard.Modifiers != ModifierKeys.Shift && !overChrome) return;
        if ((DateTime.UtcNow - _lastWheelPage).TotalMilliseconds < 300) { e.Handled = true; return; }
        GoToPage(_page + (e.Delta < 0 ? 1 : -1));
        _lastWheelPage = DateTime.UtcNow;
        e.Handled = true;
    }

    static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ───────────────────────────── page 2 · system ─────────────────────────────

    async Task SampleSystemAsync()
    {
        var pingTask = _monitor.ConnectionAsync(); // these run alongside each other
        var batteryTask = _monitor.BatteryAsync();
        var s = await _monitor.SampleAsync();
        var bt = await batteryTask;
        if (s == null || _mode != Mode.Expanded) return;

        CpuRing.AnimateTo(s.Cpu);
        CpuText.Text = $"{s.Cpu * 100:0}%";
        CpuRing.RingBrush = s.Cpu > 0.85 ? Red : Blue;

        RamRing.AnimateTo(s.Memory);
        RamText.Text = $"{s.Memory * 100:0}%";
        RamLabel.Text = $"{s.MemUsed / 1073741824.0:0.0} / {s.MemTotal / 1073741824.0:0} GB";

        DiskRing.AnimateTo(s.Disk);
        DiskText.Text = $"{s.Disk * 100:0}%";
        DiskLabel.Text = $"{SystemMonitor.Bytes(s.DiskFree)} free";

        NetDown.Text = SystemMonitor.Bytes(s.DownBps) + "/s";
        NetUp.Text = SystemMonitor.Bytes(s.UpBps) + "/s";
        var up = s.Uptime;
        UptimeText.Text = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : $"{up.Hours}h {up.Minutes}m";

        RenderBattery(bt);
        RenderConnection(await pingTask);
    }

    void RenderBattery(BatteryDetails b)
    {
        BatRing.AnimateTo(_power.Percent / 100.0);
        BatText.Text = $"{_power.Percent}%";
        BatRing.RingBrush = _power.Percent <= 20 && !_power.PluggedIn ? Red : Green;

        if (!_power.HasBattery || !b.Present)
        {
            BatLabel.Text = "AC power";
            HealthText.Text = "";
            DrawText.Text = "No battery";
            CapacityText.Text = "";
            return;
        }

        // the line under the ring: how long you have, or how long until full
        BatLabel.Text =
            b.Full || (_power.PluggedIn && _power.Percent >= 99) ? "Fully charged" :
            b.Charging && b.TimeToFull is { } tf ? $"Full in {SystemMonitor.Duration(tf)}" :
            _power.PluggedIn ? "Plugged in" :
            b.TimeLeft is { } tl ? $"{SystemMonitor.Duration(tl)} left" :
            "Estimating…";

        if (b.HealthPercent is { } h)
        {
            HealthText.Text = $"{h:0}%";
            HealthText.Foreground = h >= 80 ? Green : h >= 60 ? Orange : Red;
        }
        else HealthText.Text = "—";

        DrawText.Text = b.WattsNow is { } w
            ? b.Charging ? $"Charging at {w:0.#} W" : _power.PluggedIn ? "On charger" : $"Using {w:0.#} W right now"
            : _power.PluggedIn ? "On charger" : "Measuring power draw…";
        CapacityText.Text = b.RemainingWh is { } r && b.FullWh is { } f ? $"{r:0.#} of {f:0.#} Wh" : "";
    }

    void RenderConnection(ConnectionDetails c)
    {
        NetName.Text = c.Name;
        int bars = c.Online ? Math.Clamp(c.SignalBars ?? (c.IsWifi ? 0 : 5), 0, 5) : 0;
        int lit = c.IsWifi ? (int)Math.Ceiling(bars * 4 / 5.0) : c.Online ? 4 : 0;
        var rects = SignalBars.Children.OfType<System.Windows.Shapes.Rectangle>().ToList();
        Brush on = !c.Online ? DimBrush : lit <= 1 ? Orange : Brushes.White;
        for (int i = 0; i < rects.Count; i++) rects[i].Fill = i < lit ? on : Faint;

        NetQuality.Text = !c.Online ? "No internet" :
            !c.IsWifi ? "Wired connection" :
            bars >= 4 ? "Excellent signal" : bars == 3 ? "Good signal" : bars == 2 ? "Fair signal" : "Weak signal";

        if (c.PingMs is { } ms)
        {
            PingText.Text = $"{ms} ms";
            var quality = ms < 60 ? Green : ms < 150 ? Orange : Red;
            PingDot.Fill = quality;
            PingText.Foreground = quality;
        }
        else
        {
            PingText.Text = c.Online ? "timed out" : "—";
            PingDot.Fill = c.Online ? Red : DimBrush;
            PingText.Foreground = c.Online ? Red : DimBrush;
        }
    }

    static readonly Brush Faint = Frozen(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF));

    // ───────────────────────────── page 3 · tools ─────────────────────────────

    void SetTimer(TimeSpan total)
    {
        _timerTotal = total;
        _timerRunning = false;
        _timerTick.Stop();
        _timerLeft = total;
        UpdateTimerUi();
        RefreshBase();
    }

    void StartTimer()
    {
        if (_timerLeft <= TimeSpan.Zero) _timerLeft = _timerTotal;
        _timerEnd = DateTime.UtcNow + _timerLeft;
        _timerRunning = true;
        _timerTick.Start();
        UpdateTimerUi();
        RefreshBase();
    }

    void PauseTimer()
    {
        _timerLeft = _timerEnd - DateTime.UtcNow;
        _timerRunning = false;
        _timerTick.Stop();
        UpdateTimerUi();
        RefreshBase();
    }

    void ResetTimer() => SetTimer(_timerTotal);

    // Pomodoro: focus rounds and breaks; after 4 rounds the next break is a long one
    bool _timerBreak;
    int _focusRounds;
    static readonly int[] FocusPresets = { 15, 25, 45, 60 }, BreakPresets = { 5, 10, 15, 20 };

    /// <summary>Orange while focusing, green on a break: the ring, the button, the live activity and the slim glow.</summary>
    Brush TimerBrush => _timerBreak ? Green : Orange;

    void ToggleTimer() { if (_timerRunning) PauseTimer(); else StartTimer(); }

    void SetTimerMode(bool breakTime)
    {
        if (breakTime == _timerBreak && !_timerRunning) return;
        _timerBreak = breakTime;
        var presets = breakTime ? BreakPresets : FocusPresets;
        int i = 0;
        foreach (var chip in TimerChips.Children.OfType<Button>())
        {
            chip.Tag = presets[i];
            chip.Content = presets[i++].ToString();
        }
        SetTimer(TimeSpan.FromMinutes(breakTime ? (_focusRounds >= 4 ? 15 : 5) : 25));
    }

    /// <summary>Scroll on the ring: a minute more or less (while running it moves the end time).</summary>
    void NudgeTimer(int minutes)
    {
        var step = TimeSpan.FromMinutes(minutes);
        if (_timerRunning)
        {
            var left = _timerEnd - DateTime.UtcNow + step;
            if (left < TimeSpan.FromMinutes(1)) { Bump(minutes); return; }
            _timerEnd += step;
            _timerTotal = TimeSpan.FromMinutes(Math.Max(_timerTotal.TotalMinutes, Math.Ceiling(left.TotalMinutes)));
            UpdateTimerUi();
            return;
        }
        double total = _timerTotal.TotalMinutes + minutes;
        if (total < 1 || total > 180) { Bump(minutes); return; }
        SetTimer(TimeSpan.FromMinutes(total));
    }

    void TimerTick()
    {
        _timerLeft = _timerEnd - DateTime.UtcNow;
        if (_timerLeft <= TimeSpan.Zero)
        {
            _timerRunning = false;
            _timerTick.Stop();
            _timerLeft = _timerTotal;
            try { SystemSounds.Asterisk.Play(); } catch { }
            if (!_timerBreak)
            {
                // a focus round done: offer the matching break
                _focusRounds++;
                bool longBreak = _focusRounds >= 4;
                int breakMin = longBreak ? 15 : 5;
                ShowOsd("timer", "", null, longBreak ? "4 rounds done · well earned" : $"Focus round {_focusRounds} done",
                    $"{_timerTotal.TotalMinutes:0} min of focus · click for a {breakMin} min break", null,
                    glyphBrush: Orange, seconds: 8, force: true, prio: Prio.High,
                    onClick: () => { SetTimerMode(breakTime: true); StartTimer(); });
            }
            else
            {
                if (_focusRounds >= 4) _focusRounds = 0; // the long break closes the set
                ShowOsd("timer", "", null, "Break's over", "Click to start a 25 min focus round", null,
                    glyphBrush: Green, seconds: 8, force: true, prio: Prio.High,
                    onClick: () => { SetTimerMode(breakTime: false); StartTimer(); });
            }
            UpdateTimerUi();
            RefreshBase();
            return;
        }
        if (_mode == Mode.Expanded && _page == 2) UpdateTimerUi();
        if (_mode == Mode.Activity) UpdateActivityContent();
    }

    void UpdateTimerUi()
    {
        var left = _timerRunning ? _timerEnd - DateTime.UtcNow : _timerLeft;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        var accent = TimerBrush;
        TimerText.Text = FormatTimer(left);
        TimerText.Foreground = _timerRunning ? accent : Brushes.White;
        // the ring drains as time runs out
        TimerRing.RingBrush = accent;
        TimerRing.AnimateTo(left.TotalSeconds / Math.Max(1, _timerTotal.TotalSeconds));
        bool started = left < _timerTotal;
        TimerStartText.Text = _timerRunning ? "Pause" : started ? "Resume" : "Start";
        TimerStartGlyph.Text = _timerRunning ? "" : "";
        TimerStart.Background = accent;
        TimerHint.Text = _timerRunning ? $"ends {DateTime.Now + left:t}" : _timerBreak ? (_focusRounds >= 4 ? "Long break" : "Break") : "Focus";

        StyleTab(FocusTab, !_timerBreak);
        StyleTab(BreakTab, _timerBreak);
        for (int i = 0; i < RoundDots.Children.Count; i++)
            ((Border)RoundDots.Children[i]).Background = i < _focusRounds ? Orange : Faint;
        foreach (var chip in TimerChips.Children.OfType<Button>())
        {
            bool on = !started && Math.Abs(Convert.ToDouble(chip.Tag) - _timerTotal.TotalMinutes) < 0.01;
            chip.Background = on ? accent : (Brush)FindResource("Tile");
            chip.Foreground = on ? Brushes.Black : Brushes.White;
            chip.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ───────────────────────────── keep awake ─────────────────────────────

    bool _keepAwake;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Screen stays on and the PC doesn't sleep (like PowerToys Awake) until switched off or the notch exits.</summary>
    void SetKeepAwake(bool on)
    {
        _keepAwake = on;
        // ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED, or just ES_CONTINUOUS to let go
        SetThreadExecutionState(on ? 0x80000003u : 0x80000000u);
        AwakeTile.IsOn = on;
        AwakeTile.Label = on ? "Awake" : "Keep awake";
    }

    static string FormatTimer(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    void RunAction(string action, QuickToggle tile)
    {
        switch (action)
        {
            case "awake": SetKeepAwake(!_keepAwake); break;
            case "snip": Collapse(); Dispatcher.InvokeAsync(async () => { await Task.Delay(350); Launch("ms-screenclip:"); }); break;
            case "taskmgr": Launch("taskmgr.exe"); break;
            case "explorer": Launch("explorer.exe"); break;
            case "calc": Launch("calc.exe"); break;
            case "settings": Launch("ms-settings:"); break;
            case "terminal":
                try { Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true }); Collapse(); }
                catch { Launch("powershell.exe"); }
                break;
            case "clipboard": Collapse(); SendWinV(); break;
            case "lock": Collapse(); LockWorkStation(); break;
            case "sleep":
                if (_sleepConfirm?.IsEnabled == true)
                {
                    CancelSleepConfirm();
                    Collapse();
                    Dispatcher.InvokeAsync(async () => { await Task.Delay(500); SetSuspendState(false, false, false); });
                }
                else
                {
                    _sleepTile = tile;
                    tile.IsOn = true;
                    tile.Label = "Click again";
                    _sleepConfirm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    _sleepConfirm.Tick += (_, _) => CancelSleepConfirm();
                    _sleepConfirm.Start();
                }
                break;
        }
    }

    void CancelSleepConfirm()
    {
        _sleepConfirm?.Stop();
        _sleepConfirm = null;
        if (_sleepTile != null) { _sleepTile.IsOn = false; _sleepTile.Label = "Sleep"; }
    }

    static void SendWinV()
    {
        // Win+V opens Windows' own clipboard history
        Task.Run(async () =>
        {
            await Task.Delay(250);
            Keybd(0x5B, false); Keybd(0x56, false); Keybd(0x56, true); Keybd(0x5B, true);
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    static void Keybd(byte vk, bool up) => keybd_event(vk, 0, up ? 2u : 0u, UIntPtr.Zero);

    // ───────────────────────────── page 4 · clipboard & files ─────────────────────────────

    void AddClipboardText(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 4000) text = text[..4000];
        _clipHistory.Remove(text);
        _clipHistory.Insert(0, text);
        if (_clipHistory.Count > 6) _clipHistory.RemoveAt(_clipHistory.Count - 1);
        if (_mode == Mode.Expanded && _page == 3) RenderClipboard();
    }

    void RenderClipboard()
    {
        ClipList.Children.Clear();
        ClipEmpty.Visibility = _clipHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var text in _clipHistory.Take(5))
        {
            string oneLine = string.Join(" ", text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(Glyph(LooksLikeLink(text) ? "" : "", 11, DimBrush));
            var t = new TextBlock { Text = oneLine, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(t, 1);
            row.Children.Add(t);
            var btn = new Button { Style = (Style)FindResource("RowButton"), Content = row, Height = 34, ToolTip = text.Length > 300 ? text[..300] + "…" : text };
            string value = text;
            btn.Click += (_, _) =>
            {
                _ignoreClipboardUntil = DateTime.UtcNow.AddMilliseconds(600);
                try { Clipboard.SetText(value); } catch { }
                AddClipboardText(value);
                FlashRow(btn);
            };
            ClipList.Children.Add(btn);
        }
    }

    static bool LooksLikeLink(string s) => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    static void FlashRow(FrameworkElement el)
    {
        var a = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(350));
        el.BeginAnimation(OpacityProperty, a);
    }

    async Task RenderFilesAsync()
    {
        var files = await _files.RecentAsync(5);
        var items = await Task.Run(() => files.Select(f => (File: f, Icon: IconHelper.ForFile(f.FullName))).ToList());
        FilesList.Children.Clear();
        if (items.Count == 0)
        {
            FilesList.Children.Add(new TextBlock { Text = "Nothing new in Downloads, Screenshots or Desktop.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)), Margin = new Thickness(4, 8, 4, 8) });
            return;
        }
        foreach (var (file, icon) in items)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = IconBrush(icon), HorizontalAlignment = HorizontalAlignment.Left });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = file.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = $"{Ago(file.LastWriteTime)} · {SystemMonitor.Bytes(file.Length)}", FontSize = 10, Foreground = DimBrush });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var btn = new Button { Style = (Style)FindResource("RowButton"), Content = row, Height = 36, ToolTip = "Click to open · drag down into any app · right-click to show in folder" };
            string path = file.FullName;
            btn.Click += (_, _) => { if (!_dragOut) Launch(path); };
            btn.MouseRightButtonUp += (_, e) => { e.Handled = true; Launch("explorer.exe", $"/select,\"{path}\""); };
            btn.PreviewMouseLeftButtonDown += (_, e) => _fileDown = e.GetPosition(this);
            btn.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _swiping || _dragOut) return;
                var p = e.GetPosition(this);
                double dx = p.X - _fileDown.X, dy = p.Y - _fileDown.Y;
                if (Math.Abs(dy) < 8 || Math.Abs(dy) < Math.Abs(dx)) return;
                _dragOut = true;
                _maybeSwipe = false;
                try { DragDrop.DoDragDrop(btn, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Move); }
                catch { }
                finally { _dragOut = false; }
                e.Handled = true;
            };
            FilesList.Children.Add(btn);
        }
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago" : t.ToString("d MMM");
    }

    // ───────────────────────────── page 5 · settings ─────────────────────────────

    void BuildSettings()
    {
        Add("Minimal when idle", () => _settings.MinimalWhenIdle, v => _settings.MinimalWhenIdle = v);
        Add("Music activity", () => _settings.ShowMediaActivity, v => _settings.ShowMediaActivity = v);
        Add("Notification alerts", () => _settings.NotificationAlerts, v => _settings.NotificationAlerts = v);
        Add("System alerts", () => _settings.SystemAlerts, v => _settings.SystemAlerts = v);
        Add("App launch alerts", () => _settings.AppLaunchAlerts, v => _settings.AppLaunchAlerts = v);
        Add("“Copied” alerts", () => _settings.ClipboardAlerts, v => _settings.ClipboardAlerts = v);
        Add("Music visualizer", () => _settings.AnimatedVisualizer, v => _settings.AnimatedVisualizer = v);
        Add("Open at top edge", () => _settings.HoverToOpen, v => _settings.HoverToOpen = v);
        Add("Hide in full screen", () => _settings.HideInFullscreen, v => _settings.HideInFullscreen = v);
        Add("Slim over windows", () => _settings.SlimOverWindows, v => _settings.SlimOverWindows = v);
        Add("Battery mode", () => _settings.EcoOnBattery, v => _settings.EcoOnBattery = v);
        Add("Always keep running", () => _settings.KeepRunning, v => { _settings.KeepRunning = v; KeepAlive.Sync(_settings); });
        Add("Start with Windows", () => _settings.StartWithWindows, v => { _settings.StartWithWindows = v; KeepAlive.Sync(_settings); });

        void Add(string label, Func<bool> get, Action<bool> set)
        {
            var cell = new Grid { Margin = new Thickness(4, 0, 10, 0) };
            cell.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 40, 0) });
            var sw = new ToggleButton { Style = (Style)FindResource("Switch"), IsChecked = get(), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            sw.Click += (_, _) =>
            {
                set(sw.IsChecked == true);
                _settings.Save();
                OnSettingsChanged();
            };
            cell.Children.Add(sw);
            _settingSwitches.Add((sw, get));
            SettingsGrid.Children.Add(cell);
        }
    }

    void SyncSettingsPage()
    {
        foreach (var (sw, get) in _settingSwitches)
            if (sw.IsChecked != get()) sw.IsChecked = get();
    }
}
