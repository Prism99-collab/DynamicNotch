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
    static readonly string[] PageNames = { "", "System", "Tools", "Clipboard & Files", "Colors", "Settings", "Calls", "Work" };
    /// <summary>The order pages come in, left to right (each keeps its own number: Calls is 6, shown second).</summary>
    static readonly int[] PageOrder = { 0, 7, 6, 1, 2, 3, 4, 5 }; // Work second, then Calls
    readonly UIElement[] _pageEls = new UIElement[8];

    /// <summary>A page's own panel in the strip.</summary>
    UIElement PageElement(int page) => _pageEls[page];
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
    DateTime _ignoreClipboardUntil;
    bool _dragOut;
    Point _fileDown;

    readonly List<(ToggleButton Switch, Func<bool> Get)> _settingSwitches = new();

    void WirePages()
    {
        // the Calls page goes second; each page remembers its own panel
        var made = PagesStrip.Children.Cast<UIElement>().ToList();
        for (int i = 0; i < made.Count && i < 6; i++) _pageEls[i] = made[i];
        // the System page is built in code now: it takes the old one's place
        int sys = PagesStrip.Children.IndexOf(made[1]);
        PagesStrip.Children.RemoveAt(sys);
        _pageEls[1] = BuildVitalsPage();
        PagesStrip.Children.Insert(sys, _pageEls[1]);
        _pageEls[CallsPage] = BuildCallsPage();
        PagesStrip.Children.Insert(1, _pageEls[CallsPage]);
        _pageEls[WorkPage] = BuildWorkPage(); // NotchWindow.WorkPage
        PagesStrip.Children.Insert(1, _pageEls[WorkPage]);

        // page dots (one per page, in the order they come)
        var hits = new Border[PageNames.Length];
        for (int i = 0; i < PageNames.Length; i++)
        {
            int idx = i;
            var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = DimBrush };
            var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(3, 5, 3, 5), Child = dot, Cursor = Cursors.Hand };
            hit.MouseLeftButtonUp += (_, e) => { GoToPage(idx); e.Handled = true; };
            _dots.Add(dot);
            hits[i] = hit;
        }
        foreach (int i in PageOrder) Dots.Children.Add(hits[i]);
        // each page draws only inside itself: a glow, a side rail or a row arriving can never bleed onto the
        // page next to it while the strip slides (or after it settles)
        foreach (var el in PagesStrip.Children.OfType<FrameworkElement>()) el.ClipToBounds = true;
        // Home: a small round button on the left of the dots, on every page but Home
        if (Dots.Parent is Grid bottom)
        {
            var catcher = new Border { Background = Brushes.Transparent };
            Grid.SetRow(catcher, Grid.GetRow(Dots));
            bottom.Children.Insert(0, catcher); // under the dots: the wheel between two dots still turns the page
            Dots.Background = Brushes.Transparent;
            _homeBtn = new Button
            {
                Style = (Style)FindResource("CircleButton"), Width = 18, Height = 18, Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(30, -6, 0, 0), // clear of the rounded corner
                Content = Glyph("", 9.5, Brushes.White), ToolTip = Loc.T("Home"), Opacity = 0, IsHitTestVisible = false,
                RenderTransform = new TranslateTransform(-6, 0),
            };
            Grid.SetRow(_homeBtn, Grid.GetRow(Dots));
            _homeBtn.Click += (_, _) => GoToPage(0);
            bottom.Children.Add(_homeBtn);
        }
        UpdateDots(animate: false);
        ApplyPageVisibility(); // pages switched off in Preferences

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
        ClipClear.Click += (_, _) => ClearClips(); // pinned items stay
        OpenDownloads.Click += (_, _) => { if (_files.DownloadsDir != null) Launch(_files.DownloadsDir); };

        BuildSettings();
    }

    // ───────────────────────────── paging ─────────────────────────────

    /// <param name="velocity">Finger speed in px/s; the page keeps that momentum as it settles.</param>
    void GoToPage(int page, double velocity = 0, bool instant = false)
    {
        page = Math.Clamp(page, 0, PageNames.Length - 1);
        if (PageHidden(page)) page = PageHidden(_page) ? 0 : _page; // switched off in Preferences
        bool changed = page != _page;
        if (changed) HideTip();
        if (changed && (_page == WorkPage || _page == 5)) EndWorkTyping(giveBack: true); // the Work page's or Settings' box had the keyboard
        if (changed && _page == CallsPage && _call != null) _leftCallsPage = true; // you moved on: the next opening stays where you are
        _page = page;
        HeaderPage(); // the quick buttons fold or open, the page's name refits
        _pageX.Configure(SpringStyle.Page);
        _pageX.Active = true;
        if (instant) { _pageX.Snap(-PagePos(page) * PageWidth); PagesShift.X = _pageX.Value; }
        else
        {
            _pageX.Value = PagesShift.X;
            _pageX.Velocity = Math.Clamp(velocity, -5000, 5000);
            _pageX.Target = -PagePos(page) * PageWidth;
            StartAnimating();
        }
        UpdateDots(animate: true);
        if (changed) OnPageShown(page);
        if (page != 0) ShowDevices(false);
    }

    Button? _homeBtn;

    void UpdateDots(bool animate)
    {
        PageTitle.Text = Loc.T(PageNames[_page]);
        if (_homeBtn != null)
        {
            // the Home button slides in away from Home, and out on it
            bool show = _page != 0;
            _homeBtn.IsHitTestVisible = show;
            var t = TimeSpan.FromMilliseconds(animate && !Eco ? 220 : 1);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _homeBtn.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, t));
            ((TranslateTransform)_homeBtn.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(show ? 0 : -6, t) { EasingFunction = ease });
        }
        RenderSwitch();
        RenderDeviceRings();
        for (int i = 0; i < _dots.Count; i++)
        {
            double w = i == _page ? 16 : 5;
            if (animate) _dots[i].BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else _dots[i].Width = w;
            _dots[i].Background = i == _page ? (_cyber && CustomAccent == null ? CyYellowBrush : _accent == Colors.White ? Brushes.White : Frozen(_accent)) : DimBrush;
        }
    }

    void OnPageShown(int page)
    {
        if (_mode != Mode.Expanded) return;
        CyberPage();
        if (page == 1) OnVitalsShown(); else VitalsHidden();
        UpdateCallUi(); // the call card's voice bars run only on Home
        if (page == 0) RefreshClockAndProgress();
        MicLive(page == 0);
        if (page == 2) UpdateTimerUi();
        if (page == 3) { RenderClipboard(); _shelfTab = _shelf.Count > 0; RenderShelfOrRecent(); } // the shelf first when something is kept
        if (page == 4) OnColorsShown();
        if (page == 5) { SyncSettingsPage(); SyncSmartPage(); }
        if (page == CallsPage) OnCallsShown();
        if (page == WorkPage) OnWorkShown();
    }

    void OnPagerDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<PillSlider>(e.OriginalSource as DependencyObject) != null || FindParent<ColorWheel>(e.OriginalSource as DependencyObject) != null) return;
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
            if (Mouse.Captured is PillSlider or ColorWheel) { _maybeSwipe = false; return; }
            if (TouchSwipeUpToClose(dx, dy)) { e.Handled = true; return; }
            if (Math.Abs(dx) < 8 || Math.Abs(dx) < Math.Abs(dy) * 1.2) return;
            _swiping = true;
            Pager.CaptureMouse(); // steals capture from any button, so it won't "click"
            _pageX.Active = false; // the finger drives the strip now
            _swipeBase = PagesShift.X + PagePos(_page) * PageWidth; // grab mid-flight without a jump
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
        double x = -PagePos(_page) * PageWidth + dx;
        double min = -(VisiblePageCount - 1) * PageWidth;
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
        int pos = PagePos(_page);
        double projected = -pos * PageWidth + dx + velocity * 0.18;
        int target = Math.Clamp((int)Math.Round(-projected / PageWidth), Math.Max(0, pos - 1), Math.Min(VisiblePageCount - 1, pos + 1));
        GoToPage(PageAtPos(target), velocity);
    }

    void OnHorizontalWheel(short delta)
    {
        _hwheel += delta;
        if ((DateTime.UtcNow - _lastWheelPage).TotalMilliseconds < 420) { _hwheel = 0; return; }
        if (Math.Abs(_hwheel) < 90) return;
        GoToPage(StepPage(_hwheel > 0 ? 1 : -1));
        _hwheel = 0;
        _lastWheelPage = DateTime.UtcNow;
    }

    int _vwheel;
    DateTime _vwheelAt;

    void OnExpandedWheel(object sender, MouseWheelEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (FindParent<PillSlider>(src) != null || FindParent<ScrollViewer>(src) != null) return;
        bool overChrome = e.GetPosition(Expanded).Y < 34 || e.GetPosition(Expanded).Y > 34 + 216;
        if (Keyboard.Modifiers != ModifierKeys.Shift && !overChrome) return;
        e.Handled = true;
        var now = DateTime.UtcNow;
        // one notch, one page: smooth-scrolling mice and touchpads send many small turns for one notch, so the turns
        // add up until they make a notch; right after a page change the wheel rests a moment (a spin doesn't race on)
        if ((now - _lastWheelPage).TotalMilliseconds < 280) { _vwheel = 0; return; }
        if ((now - _vwheelAt).TotalMilliseconds > 400) _vwheel = 0; // a new gesture starts from zero
        _vwheelAt = now;
        _vwheel += e.Delta;
        if (Math.Abs(_vwheel) < 100) return;
        GoToPage(StepPage(_vwheel < 0 ? 1 : -1));
        _vwheel = 0;
        _lastWheelPage = now;
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
        CyberRamCells(s.Memory); // Neon: the memory as RAM cells

        DiskRing.AnimateTo(s.Disk);
        DiskText.Text = $"{s.Disk * 100:0}%";
        DiskLabel.Text = $"{SystemMonitor.Bytes(s.DiskFree)} free";

        NetDown.Text = SystemMonitor.Bytes(s.DownBps) + "/s";
        NetUp.Text = SystemMonitor.Bytes(s.UpBps) + "/s";
        var up = s.Uptime;
        UptimeText.Text = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : $"{up.Hours}h {up.Minutes}m";

        RenderBattery(bt);
        UpdateChargeBolt(bt);
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

        // the signal in words, then what the link is: "Good signal · Wi-Fi 6 · 866 Mbps"
        string signal = !c.IsWifi ? Loc.T("Wired connection") :
            bars >= 4 ? Loc.T("Excellent signal") : bars == 3 ? Loc.T("Good signal") : bars == 2 ? Loc.T("Fair signal") : Loc.T("Weak signal");
        string link = string.Join(" · ", new[] { c.WifiGeneration, c.LinkMbps is { } r ? $"{r} Mbps" : null }.Where(x => x != null));
        NetQuality.Text = c.Captive ? Loc.T("Sign in to this network") : !c.Online ? Loc.T("No internet") : link.Length > 0 ? $"{signal} · {link}" : signal;
        NetQuality.ToolTip = c.SignalPercent is { } q ? $"{Loc.T("Signal")} {q}%" : null;
        if (c.Captive) for (int i = 0; i < rects.Count; i++) rects[i].Fill = i < lit ? Orange : Faint;

        if (c.PingMs is { } ms)
        {
            // coloured by the typical round trip (one slow reply doesn't turn it red), wobbly connections said so
            long typical = c.TypicalMs ?? ms;
            bool shaky = c.JitterMs is >= 30;
            PingText.Text = shaky ? $"{ms} ms · ±{c.JitterMs}" : $"{ms} ms";
            var quality = typical < 60 && !shaky ? Green : typical < 150 ? Orange : Red;
            PingDot.Fill = quality;
            PingText.Foreground = quality;
            PingText.ToolTip = c.JitterMs is { } j ? $"{Loc.T("Typical")} {typical} ms · {Loc.T("jitter")} {j} ms" : null;
        }
        else
        {
            PingText.Text = c.Online ? Loc.T("timed out") : "—";
            PingDot.Fill = c.Online ? Red : DimBrush;
            PingText.Foreground = c.Online ? Red : DimBrush;
            PingText.ToolTip = null;
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
                PalReact(Controls.Companion.Act.Pump, 2.6); // a focus round done: the companion cheers you on
                ShowOsd("timer", "", null, longBreak ? "4 rounds done · well earned" : $"Focus round {_focusRounds} done",
                    $"{_timerTotal.TotalMinutes:0} min of focus · click for a {breakMin} min break", null,
                    glyphBrush: Orange, seconds: 8, force: true, prio: Prio.High,
                    onClick: () => { SetTimerMode(breakTime: true); StartTimer(); });
            }
            else
            {
                if (_focusRounds >= 4) _focusRounds = 0; // the long break closes the set
                ShowOsd("timer", "", null, "Break's over", "Click to start another 25-minute focus", null,
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
        SetDigits(TimerText, FormatTimer(left));
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

    static bool LooksLikeLink(string s) => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    static void FlashRow(FrameworkElement el)
    {
        var a = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(350));
        el.BeginAnimation(OpacityProperty, a);
    }

    async Task RenderFilesAsync()
    {
        var files = await _files.RecentAsync(15);
        var items = await Task.Run(() => files.Select(f => (File: f, Icon: IconHelper.ForFile(f.FullName))).ToList());
        FilesList.Children.Clear();
        if (items.Count == 0)
        {
            FilesList.Children.Add(ClipEmptyState(Loc.T("No new files"), Loc.T("New downloads, screenshots and desktop files appear here.")));
            return;
        }
        foreach (var (file, icon) in items)
        {
            string path = file.FullName;
            var (nice, tag) = NiceFileName(file.Name, file.LastWriteTime);
            var row = ClipLine(new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = IconBrush(icon) }, nice, tag, ShortAgo(file.LastWriteTime),
                ClipAction("\uE838", Loc.T("Show in folder"), () => Launch("explorer.exe", $"/select,\"{path}\"")));
            var btn = new Button { Style = (Style)FindResource("RowButton"), Content = row, Height = ClipRowHeight + 2,
                ToolTip = $"{file.Name}\n{SystemMonitor.Bytes(file.Length)}\n\n{Loc.T("Click to open, or drag it into any app")}" };
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
        ClipCascade(FilesList);
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago" : t.ToString("d MMM");
    }

    // ───────────────────────────── page 5 · settings ─────────────────────────────

    void BuildSettings()
    {
        // Display: how it looks
        var grid = SettingsGrid;
        Add("Minimal when idle", () => _settings.MinimalWhenIdle, v => _settings.MinimalWhenIdle = v);
        Add("Slim over windows", () => _settings.SlimOverWindows, v => _settings.SlimOverWindows = v);
        Add("Music activity", () => _settings.ShowMediaActivity, v => _settings.ShowMediaActivity = v);
        Add("Music visualizer", () => _settings.AnimatedVisualizer, v => _settings.AnimatedVisualizer = v);
        Add("Hide in full screen", () => _settings.HideInFullscreen, v => _settings.HideInFullscreen = v);
        Add("FPS in games", () => _settings.GameStats, SetFpsInGames);
        AddThemeCell(grid); // which theme: a chip that moves to the next one
        Add("Battery mode", () => _settings.EcoOnBattery, v => _settings.EcoOnBattery = v);
        // Behaviour: how it reacts, what it tells you
        grid = BehaviourGrid;
        Add("Open at top edge", () => _settings.HoverToOpen, v => _settings.HoverToOpen = v);
        Add("Drop files on the notch", () => _settings.ShelfDrop, v => _settings.ShelfDrop = v);
        Add("Notification alerts", () => _settings.NotificationAlerts, v => _settings.NotificationAlerts = v);
        Add("System alerts", () => _settings.SystemAlerts, v => _settings.SystemAlerts = v);
        Add("App launch alerts", () => _settings.AppLaunchAlerts, v => _settings.AppLaunchAlerts = v);
        Add("“Copied” alerts", () => _settings.ClipboardAlerts, v => _settings.ClipboardAlerts = v);
        Add("Lock the notch", () => _settings.Locked, v => { _settings.Locked = !v; SetLocked(v); });
        Add("Always keep running", () => _settings.KeepRunning, v => { _settings.KeepRunning = v; KeepAlive.Sync(_settings); });
        Add("Start with Windows", () => _settings.StartWithWindows, v => { _settings.StartWithWindows = v; KeepAlive.Sync(_settings); });
        Add("Companion", () => _settings.Companion, v => _settings.Companion = v);
        Add("Face in the notch", () => _settings.NotchFace, v => { _settings.NotchFace = v; RefreshBase(); });
        Add("Compact when resting", () => _settings.CompactWhenResting, v => { _settings.CompactWhenResting = v; FaceWake(); });
        Add("Face sounds", () => _settings.FaceSounds, v => _settings.FaceSounds = v);
        Add("Tuck pills into the notch", () => _settings.TuckPills, v => { _settings.TuckPills = v; RefreshIslands(); });
        BuildSettingsTabs(); // Smart is the third tab (its switches are built with the Smart page code)
        CompactSettings(); // short rows, the ask box on top (NotchWindow.SettingsAsk)

        void Add(string label, Func<bool> get, Action<bool> set)
        {
            var cell = new Grid { Margin = new Thickness(4, 0, 10, 0), Height = 25 };
            cell.Children.Add(Localized(new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 36, 0) }, label));
            var sw = new ToggleButton { Style = (Style)FindResource("Switch"), IsChecked = get(), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, LayoutTransform = new ScaleTransform(0.8, 0.8) };
            RegisterSetting(label, "", grid == SettingsGrid ? 0 : 1, get, set); // findable from the ask box
            sw.Click += (_, _) =>
            {
                set(sw.IsChecked == true);
                _settings.Save();
                OnSettingsChanged();
            };
            cell.Children.Add(sw);
            _settingSwitches.Add((sw, get));
            grid.Children.Add(cell);
        }
    }

    void SyncSettingsPage()
    {
        foreach (var (sw, get) in _settingSwitches)
            if (sw.IsChecked != get()) sw.IsChecked = get();
    }
}
