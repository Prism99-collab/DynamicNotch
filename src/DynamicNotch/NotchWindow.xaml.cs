using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Controls;
using DynamicNotch.Services;
using Microsoft.Win32;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Core of the notch: the shape state machine, spring animations, click-through, hover / hotkey and
/// window-level system messages. Pages, alerts and the home view live in the other partial files.
/// </summary>
public partial class NotchWindow : Window
{
    enum Mode { Idle, Slim, Media, Activity, Osd, Expanded, Drop, Game }

    // Shapes, in device-independent pixels (rendered crisply at the monitor's real DPI).
    static readonly Size IdleSize = new(150, 26), SlimSize = new(118, 6), MediaSize = new(252, 30),
        ActivitySize = new(230, 30), ExpandedSize = new(440, 268), DropSize = new(380, 92),
        GameSize = new(54, 18); // in a game: just the frame rate, as small as it can be
    const double IdleRadius = 13, SlimRadius = 3, MediaRadius = 15, ExpandedRadius = 30, DropRadius = 28;
    const int HotkeyId = 0x4E01;

    readonly Settings _settings;
    readonly MediaService _media;
    readonly AudioService _audio;
    readonly RadioService _radio;
    readonly BluetoothService _bt;
    readonly BrightnessService _bri;
    readonly MonitorBrightnessService _extBri;
    readonly PowerService _power;
    readonly FocusService _focus;
    readonly PrivacyService _privacy;
    readonly FilesService _files;
    readonly NotificationService _notifications;
    readonly NotificationSoundService _notificationSound = new();
    readonly SystemMonitor _monitor = new();

    IntPtr _hwnd;
    double _scale = 1;
    int _winLeft, _winTop, _winW;
    uint _shellMsg;
    RECT _mon;

    Mode _mode = Mode.Slim;
    bool _hidden, _clickThrough = true, _overWindow, _eqRunning;
    FrameworkElement? _activePanel;
    Size _osdSize = new(300, 36);

    readonly DispatcherTimer _poll, _osdTimer, _tick;
    readonly ScaleTransform[] _eqBars;
    DateTime _lastPoll = DateTime.UtcNow, _expandedAt;
    double _dwell, _leave;
    bool _byHotkey, _entered;
    int _pollCount;
    WinEventDelegate? _winEventProc, _alertEventProc;
    IntPtr _winEventHook, _alertEventHook;

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(NotchWindow), new PropertyMetadata(SlimRadius, (d, _) => ((NotchWindow)d).ApplyRadius()));

    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public NotchWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();

        _media = new MediaService(Dispatcher);
        _audio = new AudioService(Dispatcher);
        _radio = new RadioService(Dispatcher);
        _bt = new BluetoothService(Dispatcher);
        _bri = new BrightnessService(Dispatcher);
        _extBri = new MonitorBrightnessService(Dispatcher);
        _power = new PowerService(Dispatcher);
        _focus = new FocusService(Dispatcher);
        _privacy = new PrivacyService(Dispatcher);
        _files = new FilesService(Dispatcher);
        _devices = new DeviceBatteryService(Dispatcher);
        _notifications = new NotificationService(Dispatcher);

        foreach (var p in new FrameworkElement[] { CompactMedia, CompactActivity, Osd, Expanded, DropPanel, GamePanel })
        {
            p.RenderTransformOrigin = new Point(0.5, 0);
            p.RenderTransform = new ScaleTransform(1, 1);
        }
        _eqBars = Eq.Children.OfType<Rectangle>().Select(r =>
        {
            var s = new ScaleTransform(1, 0.35);
            r.RenderTransformOrigin = new Point(0.5, 1);
            r.RenderTransform = s;
            return s;
        }).ToArray();

        // ~16 checks a second: a few cheap Win32 calls, nothing is redrawn unless something changes
        // Normal priority: the edge check keeps its rhythm even while frames are rendering (Background could lag)
        _poll = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(60) };
        _poll.Tick += OnPoll;
        _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => RefreshClockAndProgress();

        Root.RenderTransform = _leanShift;
        _lean.Configure(SpringStyle.Peek);
        Pill.SizeChanged += (_, _) => { UpdateClip(); PlacePrivacyBubble(); PlaceSecond(); };
        SecondBubble.SizeChanged += (_, _) => PlaceSecond();
        PrivacyBubble.SizeChanged += (_, _) => PlacePrivacyBubble();
        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitServicesAsync();

        // each feature starts on its own: one that can't (no Bluetooth radio, no battery, no audio device on
        // this PC...) is logged and skipped, and the notch still comes up
        Safe(nameof(WireHome), WireHome);
        Safe(nameof(WirePages), WirePages);
        Safe(nameof(WireAlerts), WireAlerts);
        Safe(nameof(WireSmart), WireSmart);
        Safe(nameof(WireAwareness), WireAwareness);
        Safe(nameof(WireShelf), WireShelf);
        Safe(nameof(WireCall), WireCall);
        Safe(nameof(WireTips), WireTips);
        Safe(nameof(WirePriority), WirePriority);
        Safe(nameof(WireSystemLive), WireSystemLive);
        Safe(nameof(WireTouch), WireTouch);
        Safe(nameof(WireGame), WireGame);
        Safe(nameof(WireMixer), WireMixer);
        Safe(nameof(WireLock), WireLock);
        Safe(nameof(WireCyber), WireCyber);
        Safe(nameof(WireStrain), WireStrain);
        Safe(nameof(WireHolo), WireHolo);
        Safe(nameof(WireGlide), WireGlide);
        Safe(nameof(ApplyCustomColors), ApplyCustomColors); // your colours from the Colors page
        Safe(nameof(WireColors), WireColors);
        Safe(nameof(WireDevices), WireDevices);
        Safe(nameof(WireRinging), WireRinging);
    }

    static void Safe(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Services.AppLog.Write($"startup: {what} skipped: {ex.GetBaseException().Message}"); }
    }

    static async Task SafeAsync(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Services.AppLog.Write($"startup: {what} skipped: {ex.GetBaseException().Message}"); }
    }

    // ───────────────────────────── window plumbing ─────────────────────────────

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        // never steal focus, never show in Alt+Tab, start fully click-through
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));

        PositionWindow();
        // Win+Alt+N (another app may already own it: the notch still opens from the top edge and the tray icon)
        if (!RegisterHotKey(_hwnd, HotkeyId, MOD_WIN | MOD_ALT | MOD_NOREPEAT, 0x4E)) Services.AppLog.Write("Win+Alt+N is taken by another app: open the notch from the top edge or its tray icon");
        System.Windows.Media.RenderCapability.TierChanged += (_, _) =>
        {
            _lowGraphics = null;
            Services.AppLog.Write($"graphics changed: {(LowGraphics ? "light motion (no graphics hardware)" : "full motion")}");
            ApplyPowerProfile();
        };
        AddClipboardFormatListener(_hwnd);
        _shellMsg = RegisterWindowMessage("SHELLHOOK");
        RegisterShellHookWindow(_hwnd);

        // called straight from Windows: an exception escaping here would end the app, so contain it
        _winEventProc = (_, _, hwnd, _, _, _, _) =>
        {
            try { CheckForeground(); OnForegroundChanged(hwnd); }
            catch (Exception ex) { Services.AppLog.Write("foreground check failed: " + ex.Message); }
        };
        _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        // any app's error / warning dialog ("Wrong password", "Access denied"...): delivered on this thread
        _alertEventProc = (_, _, hwnd, _, child, _, _) =>
        {
            try { OnAppAlert(hwnd, child); }
            catch (Exception ex) { Services.AppLog.Write("alert check failed: " + ex.Message); }
        };
        _alertEventHook = SetWinEventHook(EVENT_SYSTEM_ALERT, EVENT_SYSTEM_ALERT, IntPtr.Zero, _alertEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        DpiChanged += (_, _) => PositionWindow();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerMode;
        _poll.Start();
        CheckForeground();
        SetMode(BaseMode);
    }

    void OnDisplayChanged(object? s, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        PositionWindow();
        OnScreensChanged();
        // a monitor may have been plugged in, unplugged or switched on: look again, and keep looking a little while
        _ = _extBri.RefreshAsync(MonitorInUse(), rescan: true);
        _extBri.RetryUntilFound(MonitorInUse);
    });

    /// <summary>The screen of the window you're working in.</summary>
    IntPtr MonitorInUse()
    {
        var fg = GetForegroundWindow();
        return MonitorFromWindow(fg != IntPtr.Zero && fg != _hwnd ? fg : _hwnd, MONITOR_DEFAULTTONEAREST);
    }

    void OnSessionSwitch(object? s, SessionSwitchEventArgs e)
    {
        Services.AppLog.Write($"session {e.Reason}");
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.SessionLogon)
            Dispatcher.InvokeAsync(Recover);
    }

    void OnPowerMode(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Services.AppLog.Write("going to sleep");
        if (e.Mode == PowerModes.Resume) { Services.AppLog.Write("woke up"); Dispatcher.InvokeAsync(Recover); }
    }

    /// <summary>After unlock / wake: screens and audio devices may have changed while we were away.</summary>
    async void Recover()
    {
        try
        {
            await Task.Delay(800); // let Windows finish re-attaching displays
            PositionWindow();
            KeepOnTop();
            CheckForeground();
            if (_mode == Mode.Expanded) Collapse();
            else SetMode(BaseMode);
            _audio.Init();
            _ = _extBri.RefreshAsync(MonitorInUse(), rescan: true); // the monitor may still be waking up
            _extBri.RetryUntilFound(MonitorInUse);
        }
        catch (Exception ex) { Services.AppLog.Write("recover failed: " + ex.Message); }
    }

    // In a game the notch is a 54 px pill: the window shrinks around it, so each refresh of the frame rate redraws
    // a few thousand pixels instead of the whole 560 x 300 transparent window.
    const double FullWidth = 560, FullHeight = 300, SmallWidth = 140, SmallHeight = 36;
    bool _smallWindow;

    void SetSmallWindow(bool small)
    {
        if (small == _smallWindow) return;
        _smallWindow = small;
        Width = small ? Math.Max(SmallWidth, GamePillWidth + 40) : FullWidth; // wide enough for the FPS pill's extras
        Height = small ? SmallHeight : FullHeight;
        PositionWindow();
    }

    void PositionWindow()
    {
        if (_hwnd == IntPtr.Zero) return;
        var primary = System.Windows.Forms.Screen.PrimaryScreen;
        // while the screen is off / locked Windows can report no display or an empty one: keep the old position
        if (primary == null || primary.Bounds.Width <= 0 || primary.Bounds.Height <= 0) return;
        var screen = primary.Bounds; // physical pixels (per-monitor DPI aware)
        _scale = GetDpiForWindow(_hwnd) / 96.0;
        _refreshHz = RefreshRate(); // the screen may have switched between 60 and 180 Hz
        _mon = new RECT { Left = screen.Left, Top = screen.Top, Right = screen.Right, Bottom = screen.Bottom };
        _winW = (int)Math.Round(Width * _scale);
        int h = (int)Math.Round(Height * _scale);
        _winLeft = screen.Left + (screen.Width - _winW) / 2;
        _winTop = screen.Top;
        SetWindowPos(_hwnd, HWND_TOPMOST, _winLeft, _winTop, _winW, h, SWP_NOACTIVATE);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                handled = true;
                if (_settings.Locked) { SetLocked(false); Expand(byHotkey: true); } // the shortcut is deliberate: it unlocks
                else if (_mode == Mode.Expanded) Collapse(); else Expand(byHotkey: true);
                break;
            case WM_CLIPBOARDUPDATE:
                Dispatcher.InvokeAsync(OnClipboardChanged, DispatcherPriority.Background);
                break;
            case WM_DEVICECHANGE:
                OnDeviceChange(wParam.ToInt32(), lParam);
                break;
            case WM_MOUSEHWHEEL when _mode == Mode.Expanded:
                OnHorizontalWheel((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                handled = true;
                break;
            default:
                if (msg == _shellMsg && _shellMsg != 0 && wParam.ToInt32() == HSHELL_FLASH) OnWindowFlash(lParam);
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The notch takes the mouse only when it has something to offer: open, a clickable pop-up, the drop zone,
    /// or while something is being dragged toward it. Otherwise everything under it keeps working.
    /// </summary>
    void RefreshClickThrough() =>
        SetClickThrough(_settings.Locked && _mode != Mode.Expanded || !(_mode is Mode.Expanded or Mode.Drop || (_mode == Mode.Osd && _osd?.OnClick != null) || _dropArmed
            || (_touchMode && IsBase(_mode) && !_hidden))); // with a finger the notch itself takes the tap (only its own pixels)

    void SetClickThrough(bool on)
    {
        if (on == _clickThrough || _hwnd == IntPtr.Zero) return;
        _clickThrough = on;
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    void KeepOnTop() => SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public void Shutdown()
    {
        _poll.Stop();
        StopGameTracing(); // a frame-count trace must not outlive the notch
        _devices.Dispose();
        _notificationSound.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            RemoveClipboardFormatListener(_hwnd);
            DeregisterShellHookWindow(_hwnd);
        }
        if (_winEventHook != IntPtr.Zero) UnhookWinEvent(_winEventHook);
        if (_alertEventHook != IntPtr.Zero) UnhookWinEvent(_alertEventHook);
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerMode;
        Close();
    }

    public void OnSettingsChanged()
    {
        _eqRunning = !_eqRunning; // force refresh
        CheckForeground();
        RefreshBase();
        ApplyPowerProfile();
        SyncSettingsPage();
    }

    /// <summary>Battery mode: on battery (or Windows battery saver) do the same job with fewer wake-ups.</summary>
    bool Eco => LowGraphics || (_settings.EcoOnBattery && _power.HasBattery && (!_power.PluggedIn || _power.Saver));

    /// <summary>
    /// No graphics hardware for WPF to draw with (an old or basic GPU, a virtual machine, Remote Desktop, or hardware
    /// drawing switched off): every frame is drawn by the CPU, so the notch keeps to its light motion (the same as
    /// battery mode). Checked again whenever Windows reports the graphics changed.
    /// </summary>
    static bool LowGraphics => _lowGraphics ??= DetectLowGraphics();
    static bool? _lowGraphics;

    static bool DetectLowGraphics()
    {
        try
        {
            if ((System.Windows.Media.RenderCapability.Tier >> 16) == 0) return true;
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Avalon.Graphics");
            return key?.GetValue("DisableHWAcceleration") is int v && v != 0;
        }
        catch { return false; }
    }

    void ApplyPowerProfile()
    {
        bool eco = Eco;
        _notifications.SetInterval(TimeSpan.FromSeconds(eco ? 5 : 2));
        _sysTimer.Interval = TimeSpan.FromSeconds(eco ? 3 : 1.5);
        UpdateEqualizer();
    }

    // ───────────────────────────── state machine ─────────────────────────────

    Mode BaseMode
    {
        get
        {
            // over a window's tabs / title bar: step aside into the slim bar; a glowing line shows what's live
            // (a game is the exception: the frame rate stays, in its own tiny pill)
            if (CurrentActivity == LiveActivity.Game) return Mode.Game;
            if (_overWindow && _settings.SlimOverWindows) return Mode.Slim;
            switch (CurrentActivity)
            {
                case LiveActivity.Media: return Mode.Media;
                case LiveActivity.Timer or LiveActivity.Download or LiveActivity.Focus or LiveActivity.Call: return Mode.Activity;
            }
            return _settings.MinimalWhenIdle || (_overWindow && _settings.SlimOverWindows) ? Mode.Slim : Mode.Idle;
        }
    }

    bool IsBase(Mode m) => m is Mode.Idle or Mode.Slim or Mode.Media or Mode.Activity or Mode.Game;

    void RefreshBase()
    {
        if (CurrentActivity != _pulseActivity) { _pulseActivity = CurrentActivity; CyberPulse(); } // a new live activity
        if (IsBase(_mode) && _mode != BaseMode) SetMode(BaseMode);
        else if (_mode == Mode.Activity) UpdateActivityContent();
        UpdateSecond();
        UpdateSlimGlow(); // the live activity may have changed while staying slim
        RenderSwitch();
        UpdatePanelTint();
    }

    void SetMode(Mode m)
    {
        bool modeChanged = m != _mode;
        var from = _mode;
        if (modeChanged) { HideTip(); HideLiveMenu(); }
        if (m != Mode.Game) SetSmallWindow(false); // anything else gets the full window back before it starts to grow
        else if (modeChanged) After(350, () => { if (_mode == Mode.Game) SetSmallWindow(true); }); // once the pill has settled
        _mode = m;
        if (m != Mode.Osd) _osd = null;
        if (m == Mode.Expanded) _osdQueue.Clear(); // you're looking at the notch already
        if (m == Mode.Expanded) _lean.Target = 0; // open: centred, no leaning
        if (modeChanged) { _peek = false; _osdHover = false; _pressed = false; }
        FrameworkElement? panel = m switch
        {
            Mode.Expanded => Expanded,
            Mode.Drop => DropPanel,
            Mode.Osd => Osd,
            Mode.Media => CompactMedia,
            Mode.Activity => CompactActivity,
            Mode.Game => GamePanel,
            _ => null,
        };
        if (m == Mode.Activity) UpdateActivityContent();

        ApplyShape();
        ShowPanel(panel);
        Fade(EarL, m == Mode.Slim ? 0 : 1, 160);
        Fade(EarR, m == Mode.Slim ? 0 : 1, 160);
        RefreshClickThrough();
        UpdateEqualizer();
        UpdatePrivacyBubble();
        UpdateOsdChrome();
        UpdateRim(); // and the glow under the notch: the pop-up's colour, or what's live
        UpdateSecond();
        UpdateSlimGlow();
        UpdateCallUi();
        if (!IsBase(m)) SetDodged(false); // pop-ups and the open notch never hide from the cursor
        if (modeChanged) { CyberMoment(from, m); LookMoment(from, m); }
    }

    // ───────────────────────────── spring-driven shape ─────────────────────────────

    readonly Spring _sw = new(SlimSize.Width), _sh = new(SlimSize.Height), _sr = new(SlimRadius), _pageX = new(0), _lean = new(0);
    readonly TranslateTransform _leanShift = new();
    bool _animating, _peek, _osdHover, _pressed, _atEdge;
    RectangleGeometry? _clip;
    TimeSpan _lastFrameRaw;
    static readonly List<double>? FrameLog = Environment.GetEnvironmentVariable("DYNAMICNOTCH_FRAMELOG") == "1" ? new() : null;
    TimeSpan _lastFrame;

    /// <summary>Target shape for the current mode, plus small "alive" offsets for hover / press.</summary>
    void ApplyShape()
    {
        double w, h, r;
        switch (_mode)
        {
            case Mode.Expanded: w = ExpandedSize.Width; h = ExpandedSize.Height; r = ExpandedRadius; break;
            case Mode.Drop:
                // the file is right over it: the receptacle swells to welcome it
                w = DropSize.Width + (_dropHot ? 16 : 0); h = DropSize.Height + (_dropHot ? 6 : 0); r = DropRadius; break;
            case Mode.Osd: w = _osdSize.Width; h = _osdSize.Height; r = _osdSize.Height / 2; break;
            case Mode.Media: w = MediaSize.Width; h = MediaSize.Height; r = MediaRadius; break;
            case Mode.Activity: w = ActivitySize.Width; h = ActivitySize.Height; r = MediaRadius; break;
            // the cyber skin's FPS tag is a touch tighter (just the number, small cuts, no flares)
            case Mode.Game: w = GamePillWidth; h = _cyber ? 16 : GameSize.Height; r = h / 2; break;
            case Mode.Slim: w = SlimSize.Width; h = SlimSize.Height; r = SlimRadius; break;
            default: w = IdleSize.Width; h = IdleSize.Height; r = IdleRadius; break;
        }

        var style = w * h > _sw.Value * _sh.Value + 1 ? (_mode == Mode.Expanded ? SpringStyle.Open : SpringStyle.Pop) : SpringStyle.Close;

        if (_peek && _mode is Mode.Slim or Mode.Idle)
        {
            // anticipation: the bar swells as soon as the cursor touches the edge
            w = IdleSize.Width + 16; h = IdleSize.Height + 3; r = (IdleSize.Height + 3) / 2;
            style = SpringStyle.Peek;
        }
        else if (_peek && _mode is Mode.Media or Mode.Activity)
        {
            w += 16; h += 4; r += 2; style = SpringStyle.Peek;
        }
        else if (_pulling && IsBase(_mode))
        {
            // a finger pulling it down: it stretches after the finger, less and less the further it goes
            if (_mode is Mode.Slim or Mode.Idle) { w = IdleSize.Width; h = IdleSize.Height; r = IdleRadius; }
            double stretch = 1 - Math.Exp(-_pull / 140);
            h += 110 * stretch;
            w += 90 * stretch;
            r = Math.Min(h / 2, r + 22 * stretch);
            style = SpringStyle.Peek;
        }
        else if (_pressed && _touchMode && IsBase(_mode) && _mode != Mode.Game)
        {
            if (_mode is Mode.Slim) { w = IdleSize.Width; h = IdleSize.Height; r = IdleRadius; }
            w -= 8; h -= 2; style = SpringStyle.Press;
        }
        else if (_mode == Mode.Osd && (_osdHover || _pressed))
        {
            if (_pressed) { w -= 8; h -= 2; style = SpringStyle.Press; }
            else { w += 14; h += 4; r += 2; style = SpringStyle.Peek; }
        }

        _sw.Configure(style);
        _sr.Configure(style);
        // jelly: height has its own timing, so the shape stretches like a drop instead of scaling like a box.
        // Opening, it trails the width and overshoots a touch; closing, it tucks in first.
        switch (style)
        {
            case SpringStyle.Open: _sh.Configure(0.54, 0.72); break;
            case SpringStyle.Pop: _sh.Configure(0.46, 0.66); break;
            case SpringStyle.Close: _sh.Configure(0.28, 1.0); break;
            default: _sh.Configure(style); break;
        }
        _sw.Target = w;
        _sh.Target = h;
        _sr.Target = r;
        StartAnimating();
    }

    void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
        StartPacer();
    }

    // WPF raises CompositionTarget.Rendering only as fast as the fastest running animation asks for
    // (60 fps by default). While the springs move, an invisible animation at the screen's refresh rate
    // pulls the whole render loop up to 144 / 180 / 240 Hz; it stops with them, so idle stays idle.
    readonly TranslateTransform _pacer = new();
    int _refreshHz = 60;

    void StartPacer()
    {
        // on battery saver keep 60 fps: same motion, a third of the frames
        int hz = Eco ? 60 : _refreshHz;
        var a = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(a, hz);
        _pacer.BeginAnimation(TranslateTransform.XProperty, a);
    }

    void StopPacer() => _pacer.BeginAnimation(TranslateTransform.XProperty, null);

    /// <summary>Runs once per display refresh only while something is moving; unhooks itself when settled.</summary>
    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return; // WPF can raise this more than once per frame
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        dt = Math.Clamp(dt, 1 / 400.0, 1 / 30.0); // real frame time, up to 360 Hz screens

        if (FrameLog != null && _lastFrameRaw != TimeSpan.Zero) FrameLog.Add((now - _lastFrameRaw).TotalMilliseconds);
        _lastFrameRaw = now;
        bool done = _sw.Step(dt) & _sh.Step(dt) & _sr.Step(dt) & _pageX.Step(dt) & _lean.Step(dt);
        Pill.Width = Math.Max(4, _sw.Value);
        Pill.Height = Math.Max(3, _sh.Value);
        Radius = _sr.Value;
        if (_pageX.Active) PagesShift.X = _pageX.Value;
        _leanShift.X = _lean.Value;

        if (!done) return;
        CompositionTarget.Rendering -= OnFrame;
        StopPacer();
        _animating = false;
        _lastFrameRaw = TimeSpan.Zero;
        if (FrameLog is { Count: > 0 })
        {
            // diagnostics only (DYNAMICNOTCH_FRAMELOG=1): frame pacing of the last animation
            var f = FrameLog;
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(Settings.Folder, "frames.log"),
                    $"{_mode}: {f.Count} frames, avg {f.Average():0.0} ms, worst {f.Max():0.0} ms, >20ms: {f.Count(x => x > 20)}\n");
            }
            catch { }
            f.Clear();
        }
    }

    void ShowPanel(FrameworkElement? panel)
    {
        if (_activePanel == panel) return;
        var old = _activePanel;
        _activePanel = panel;

        if (old != null)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
            fade.Completed += (_, _) => { if (_activePanel != old) old.Visibility = Visibility.Collapsed; };
            old.BeginAnimation(OpacityProperty, fade);
        }

        if (panel != null)
        {
            panel.Visibility = Visibility.Visible;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var begin = TimeSpan.FromMilliseconds(old == null ? 60 : 110);
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = begin, EasingFunction = ease });
            var st = (ScaleTransform)panel.RenderTransform;
            var scale = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(380)) { BeginTime = begin, EasingFunction = new SpringEase { Damping = 0.7 } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        }
    }

    static void Fade(UIElement el, double to, int ms) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));

    void ApplyRadius()
    {
        double r = Math.Max(0, Radius);
        double cr = _cyber ? 0 : r; // the cyber skin cuts its corners instead (see CyberShape)
        Pill.CornerRadius = new CornerRadius(0, 0, cr, cr);
        Rim.CornerRadius = new CornerRadius(0, 0, cr, cr);
        LookRadius(cr);
        UpdateClip();
    }

    void UpdateClip()
    {
        double w = Pill.ActualWidth, h = Pill.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = Math.Min(Math.Max(0, Radius), Math.Min(w, h) / 2);
        if (_cyber) { CyberShape(w, h, r); return; }
        // rect extended above the top edge so only the bottom corners are rounded; reused, not reallocated every frame
        _clip ??= new RectangleGeometry();
        _clip.Rect = new Rect(0, -r, w, h + r);
        _clip.RadiusX = _clip.RadiusY = r;
        if (Stage.Clip != _clip) Stage.Clip = _clip;
    }

    public void Expand(bool byHotkey)
    {
        if (_mode == Mode.Expanded) return;
        _osdTimer.Stop();
        _byHotkey = byHotkey;
        _entered = false;
        _leave = 0;
        _expandedAt = DateTime.UtcNow;
        ShowOnScreen(true);
        if (_hidden) Fade(Shell, 1, 150);
        SampleAmbient(); // the colour of the app you're on, before the panel covers any of it
        _devices.Refresh(); // a fresh headset reading for the rings
        RefreshExpanded();
        SetMode(Mode.Expanded);
        PlayEntrance();
        _tick.Start();
        OnPageShown(_page);
        KeepOnTop();
    }

    void Collapse()
    {
        if (_mode != Mode.Expanded) return;
        _tick.Stop();
        _sysTimer.Stop();
        ShowDevices(false);
        CancelSleepConfirm();
        SetMode(BaseMode);
        if (_hidden) Fade(Shell, 0, 200);
    }

    // ───────────────────────────── hover / foreground tracking ─────────────────────────────

    void OnPoll(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = (now - _lastPoll).TotalMilliseconds;
        _lastPoll = now;
        if (++_pollCount % 8 == 0) CheckForeground(); // catches maximize / F11 without a focus change
        WheelPoll(); // the wheel reaches the notch on every PC (NotchWindow.Wheel)
        CheckLockKeys();
        CheckKeyboardLayout();

        if (!GetCursorPos(out var p)) return;
        if (_look != null) LookGlint(p.X, p.Y); // the glass themes' light follows the pointer near the top
        bool button = IsButtonDown();
        UpdateDropArming(p);
        if (_gestureLive && !button && Environment.TickCount64 - _fingerMoveAt > 600) CancelTouchGesture(); // a lift Windows never reported

        // check often only when the cursor is near the notch; otherwise relax to save power
        // poll fast only when the cursor is close to the notch; relax elsewhere to save power
        double fromTop = p.Y - _mon.Top;
        var interval = TimeSpan.FromMilliseconds(
            _mode == Mode.Expanded ? 60 : fromTop < 90 * _scale ? 30 : fromTop < 160 * _scale ? 60 : Eco ? 300 : 180);
        if (_poll.Interval != interval) _poll.Interval = interval;
        TidyMemoryWhenIdle(now, fromTop);

        if (_mode != Mode.Expanded)
        {
            bool atEdge = false;
            // a finger has its own gestures; locked or in a game, the notch doesn't react at all
            if (!_hidden && _settings.HoverToOpen && _mode is not (Mode.Osd or Mode.Game) && !_touchMode && !_settings.Locked && !InGame)
            {
                double half = (Math.Max(_sw.Target / 2, 70) + 6) * _scale;
                double cx = _winLeft + _winW / 2.0;
                atEdge = p.Y <= _mon.Top + Math.Max(1, (int)Math.Round(_scale)) && p.X >= cx - half && p.X <= cx + half && !button;
            }
            bool wasAtEdge = _atEdge;
            _atEdge = atEdge;
            UpdateDodge(p, atEdge);
            UpdateLean(p, !_touchMode && (atEdge || _osdHover || _mode == Mode.Drop)); // the drop zone leans toward the dragged file
            if (atEdge != _peek && IsBase(_mode))
            {
                _peek = atEdge;
                ApplyShape();
                IslandsPeek(atEdge);
            }
            // count only the time actually spent at the edge (not the gap since the last check)
            if (atEdge && wasAtEdge)
            {
                _dwell += dt;
                if (_dwell >= 150) { _dwell = 0; Expand(byHotkey: false); }
            }
            else _dwell = 0;
            return;
        }

        double ex = (ExpandedSize.Width / 2 + 10 + 16) * _scale;
        double cxe = _winLeft + _winW / 2.0;
        bool inside = p.X >= cxe - ex && p.X <= cxe + ex && p.Y >= _winTop && p.Y <= _winTop + (ExpandedSize.Height + 16) * _scale;
        bool dragging = VolSlider.IsDragging || BriSlider.IsDragging || ExtBriSlider.IsDragging || AppVolSlider.IsDragging || ProgressSlider.IsDragging || _swiping || _dragOut;

        if (inside) { _entered = true; _leave = 0; return; }
        if (button || dragging) return;
        if (_byHotkey && !_entered)
        {
            if ((now - _expandedAt).TotalSeconds > 5) Collapse();
            return;
        }
        _leave += dt;
        if (_leave >= 300) Collapse();
    }

    DateTime _lastBusyAt = DateTime.UtcNow;
    bool _tidied;

    /// <summary>
    /// Gives memory back while nobody is looking: once the notch has been at rest for 10 s (closed, nothing
    /// moving, cursor away from it) it compacts the heap a single time, so a cleanup can never land in the
    /// middle of an animation. It runs again only after the notch has been used.
    /// </summary>
    void TidyMemoryWhenIdle(DateTime now, double fromTop)
    {
        bool busy = _animating || !IsBase(_mode) || _osdQueue.Count > 0 || fromTop < 160 * _scale;
        if (busy) { _lastBusyAt = now; _tidied = false; return; }
        if (_tidied || (now - _lastBusyAt).TotalSeconds < 10) return;
        _tidied = true;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    void CheckForeground()
    {
        if (_hwnd == IntPtr.Zero) return;
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != _hwnd && fg != _pulseWindow) { _pulseWindow = fg; CyberPulse(); } // another app in front
        bool fullscreen = false, over = false;

        if (fg != IntPtr.Zero && fg != _hwnd && MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) == MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST))
        {
            string cls = ClassName(fg);
            bool shell = cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
            if (!shell && GetWindowRect(fg, out var r))
            {
                fullscreen = r.Left <= _mon.Left && r.Top <= _mon.Top && r.Right >= _mon.Right && r.Bottom >= _mon.Bottom;
                double half = (IdleSize.Width / 2 + 10) * _scale;
                double cx = _winLeft + _winW / 2.0;
                over = r.Top <= _mon.Top + 12 * _scale && r.Left < cx + half && r.Right > cx - half;
            }
        }
        // a D3D game in exclusive full screen. ("Presentation mode" isn't trusted: some PCs leave it on for good,
        // which would hide the notch forever; a real full-screen window is caught by the size check above.)
        if (!fullscreen && SHQueryUserNotificationState(out int state) == 0 && state == 3) fullscreen = true;

        CheckGame(fg, fullscreen);
        SetHidden(fullscreen && _settings.HideInFullscreen && !InGame); // in a game the stats pill stays
        if (over != _overWindow)
        {
            _overWindow = over;
            RefreshBase();
        }
        if (!_hidden) KeepOnTop();
    }

    void SetHidden(bool hidden)
    {
        if (hidden == _hidden) return;
        _hidden = hidden;
        UpdatePrivacyBubble();
        UpdateSecond();
        if (_mode == Mode.Expanded) return; // fades when it closes
        if (hidden && _mode == Mode.Osd) { _osdTimer.Stop(); SetMode(BaseMode); }
        if (!hidden) ShowOnScreen(true);
        var fade = new System.Windows.Media.Animation.DoubleAnimation(hidden ? 0 : 1, TimeSpan.FromMilliseconds(hidden ? 160 : 260));
        // hidden over a full-screen app: once faded, the window leaves the screen entirely, so nothing is drawn on
        // top of a game (an overlay, even an invisible one, makes Windows composite every frame of it)
        if (hidden) fade.Completed += (_, _) => { if (_hidden && _mode != Mode.Expanded) ShowOnScreen(false); };
        Shell.BeginAnimation(OpacityProperty, fade);
    }

    bool _offScreen;

    void ShowOnScreen(bool on)
    {
        if (_hwnd == IntPtr.Zero || on != _offScreen) return;
        _offScreen = !on;
        ShowWindow(_hwnd, on ? 4 /* SW_SHOWNOACTIVATE */ : 0 /* SW_HIDE */);
        if (on) KeepOnTop();
    }

    // ───────────────────────────── startup ─────────────────────────────

    /// <summary>
    /// Builds every page's visuals once while nobody is looking (templates, text, icons), so the first
    /// real open doesn't stall for a frame or ten.
    /// </summary>
    void PrewarmPanels()
    {
        if (_mode == Mode.Expanded) return;
        _ = _monitor.SampleAsync(); // baseline, so the System page's first numbers aren't 0
        RefreshExpanded();
        foreach (var p in new FrameworkElement[] { Expanded, Osd, CompactActivity, CompactMedia })
        {
            if (p.Visibility == Visibility.Visible) continue;
            p.Visibility = Visibility.Visible;
            p.Measure(new Size(p.Width, p.Height));
            p.Arrange(new Rect(0, 0, p.Width, p.Height));
            p.UpdateLayout();
            p.Visibility = Visibility.Collapsed;
        }
    }

    async Task InitServicesAsync()
    {
        Safe("audio", _audio.Init);
        Safe("power", _power.Init);
        Safe("focus", _focus.Init);
        Safe("brightness", _bri.Init);
        Safe("monitor brightness", () =>
        {
            _ = _extBri.RefreshAsync(MonitorInUse(), rescan: true); // the monitor may still be waking up
            _extBri.RetryUntilFound(MonitorInUse);
        });
        Safe("bluetooth", _bt.Start);
        Safe("privacy", _privacy.Start);
        Safe("files", _files.Start);
        await SafeAsync("media", _media.InitAsync);
        await SafeAsync("network", _radio.InitAsync);
        _lastNetwork = _radio.NetworkName;
        Safe("media ui", OnMediaChanged);
        _alertsReady = true;
        Safe("power profile", ApplyPowerProfile);
        await Dispatcher.InvokeAsync(() => Safe("prewarm", PrewarmPanels), DispatcherPriority.ApplicationIdle);
        Safe("welcome", Welcome);
        await SafeAsync("notifications", _notifications.InitAsync);
        Services.AppLog.Write($"notifications: {_notifications.Status} | render tier: {System.Windows.Media.RenderCapability.Tier >> 16}{(LowGraphics ? " (light motion)" : "")}, mode: {(HwndSource.FromHwnd(_hwnd)?.CompositionTarget as HwndTarget)?.RenderMode}");
    }
}
