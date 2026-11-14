using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Controls;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Leaving the call, and your voice at a glance.
///  • The leave button (red, in the header) works in any call app: holding it fills a ring around it while the page
///    blushes red from its edges; let go early and nothing happens. Once full, the app's own leave button is pressed
///    for you (through Windows' accessibility, so nothing is typed and no window jumps forward), with the app's
///    shortcut as a fallback, and the call's window brought up if neither could be found.
///  • A live waveform beside the timer: it lights up as you speak (red when the call can't hear you).
///  • Two quiet watchers: a mic that sends nothing for a minute, or one that's been picking up steady noise for a
///    while (a fan, music, typing): a hint, once per call, with the fix one click away.
/// </summary>
public partial class NotchWindow
{
    Button? _leaveBtn;
    Grid? _leaveHost;
    Shape? _leaveRing;
    double _leaveUnits;
    bool _leaveHolding, _leaving;
    DateTime _leaveDown;
    DispatcherTimer? _leaveTimer;
    Rectangle? _blush;
    VoiceWave? _cWave;
    const int LeaveHoldMs = 900;

    /// <summary>The red leave button, with the ring that fills while you hold it.</summary>
    Button BuildLeaveButton(CallLook look)
    {
        bool neon = look.Neon;
        var red = Tint(CallBtn.HangUp);
        _leaveHost = new Grid { Width = 40, Height = 40, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform() };
        Shape Make() => neon ? new Rectangle { Width = 36, Height = 36 } : new Ellipse { Width = 36, Height = 36 };
        var track = Own(Make());
        track.Stroke = Frozen(A(red, 0x2E));
        track.StrokeThickness = 2;
        _leaveRing = Own(Make());
        _leaveRing.Stroke = Frozen(Lighten(red, 0.25));
        _leaveRing.StrokeThickness = 2;
        _leaveUnits = (neon ? 4 * 34 : Math.PI * 34) / 2; // the outline's length, in stroke widths
        _leaveRing.StrokeDashArray = new DoubleCollection { _leaveUnits, _leaveUnits + 20 };
        _leaveRing.StrokeDashOffset = _leaveUnits; // empty
        _leaveRing.RenderTransformOrigin = new Point(0.5, 0.5);
        _leaveRing.RenderTransform = new RotateTransform(neon ? 0 : -90); // it fills from the top
        var disc = new RadialGradientBrush { GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.45, 0.4), RadiusX = 0.75, RadiusY = 0.75 };
        disc.GradientStops.Add(new GradientStop(Lighten(red, 0.3), 0));
        disc.GradientStops.Add(new GradientStop(Darken(red, 0.35), 1));
        disc.Freeze();
        Shape body = neon ? new Rectangle { Width = 28, Height = 28 } : new Ellipse { Width = 28, Height = 28 };
        body.Fill = neon ? Frozen(A(red, 0x40)) : disc;
        if (neon) { body.Stroke = Frozen(red); body.StrokeThickness = 1; }
        var glyph = Own(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Foreground = neon ? Frozen(red) : Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        foreach (var el in new UIElement[] { track, Own(body), _leaveRing, glyph }) _leaveHost.Children.Add(el);
        _leaveBtn = new Button { Template = Bare, Content = _leaveHost, Cursor = Cursors.Hand, Focusable = false, ToolTip = "Hold to leave the call", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        _leaveBtn.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; if (_leaving || _call == null) return; StartLeaveHold(); _leaveBtn.CaptureMouse(); };
        _leaveBtn.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; StopLeaveHold(); };
        _leaveBtn.LostMouseCapture += (_, _) => StopLeaveHold();
        return _leaveBtn;
    }

    void StartLeaveHold()
    {
        _leaveHolding = true;
        _leaveDown = DateTime.UtcNow;
        var span = TimeSpan.FromMilliseconds(LeaveHoldMs);
        _leaveRing?.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(_leaveUnits, 0, span) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        if (_leaveHost?.RenderTransform is ScaleTransform st)
        {
            var squeeze = new DoubleAnimation(0.88, span);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, squeeze);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, squeeze);
        }
        Blush(true);
        OrbHint(Loc.T("Keep holding to leave…"), MutedColor, LeaveHoldMs + 400);
        _leaveTimer ??= new DispatcherTimer { Interval = span };
        _leaveTimer.Tick -= OnLeaveHeld;
        _leaveTimer.Tick += OnLeaveHeld;
        _leaveTimer.Stop();
        _leaveTimer.Start();
    }

    /// <summary>Let go: before the ring is full, everything eases back (a quick click just says how it works).</summary>
    void StopLeaveHold()
    {
        _leaveTimer?.Stop();
        if (_leaveBtn?.IsMouseCaptured == true) _leaveBtn.ReleaseMouseCapture();
        if (!_leaveHolding) return;
        _leaveHolding = false;
        if (_leaving) return;
        _leaveRing?.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(_leaveUnits, TimeSpan.FromMilliseconds(260)) { EasingFunction = Out });
        if (_leaveHost != null) Squeeze(_leaveHost, 1);
        Blush(false);
        if ((DateTime.UtcNow - _leaveDown).TotalMilliseconds < 250)
        {
            OrbHint(Loc.T("Hold the red button to leave"), MutedColor, 1800);
            if (_leaveHost != null) Shake(_leaveHost);
        }
        else OrbHint(null, default, 0);
    }

    async void OnLeaveHeld(object? sender, EventArgs e)
    {
        _leaveTimer?.Stop();
        if (!_leaveHolding || Mouse.LeftButton != MouseButtonState.Pressed) { StopLeaveHold(); return; }
        _leaveHolding = false;
        _leaving = true;
        if (_leaveBtn?.IsMouseCaptured == true) _leaveBtn.ReleaseMouseCapture();
        if (_leaveHost != null) Squeeze(_leaveHost, 1);
        LeaveBurst();
        FlashRim(MutedColor);
        await LeaveCallAsync();
    }

    /// <summary>The page blushing red from its edges while you hold (and fading back if you let go).</summary>
    void Blush(bool on)
    {
        if (_callFx == null || _callsPage == null) return;
        if (on)
        {
            if (_blush != null) FxRemove(_blush);
            var red = MutedColor;
            var edge = new RadialGradientBrush { RadiusX = 0.75, RadiusY = 0.9 };
            edge.GradientStops.Add(new GradientStop(A(red, 0x00), 0.45));
            edge.GradientStops.Add(new GradientStop(A(red, _cyber ? (byte)0x70 : (byte)0x5A), 1));
            edge.Freeze();
            _blush = new Rectangle { Width = Math.Max(1, _callsPage.ActualWidth), Height = Math.Max(1, _callsPage.ActualHeight), Fill = edge, Opacity = 0 };
            FxAdd(_blush);
            _blush.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(LeaveHoldMs)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        }
        else if (_blush is { } b)
        {
            _blush = null;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (_, _) => FxRemove(b);
            b.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>The moment you leave: Neon cuts out in a red glitch; the other themes fold the page's light into the button.</summary>
    void LeaveBurst()
    {
        if (_callFx == null || _callsPage == null || _leaveHost == null) return;
        Point o;
        try { o = _leaveHost.TranslatePoint(new Point(20, 20), _callsPage); } catch { o = new Point(PageWidth - 40, 24); }
        double w = _callsPage.ActualWidth, h = _callsPage.ActualHeight;
        double reach = Math.Sqrt(Math.Pow(Math.Max(o.X, w - o.X), 2) + Math.Pow(Math.Max(o.Y, h - o.Y), 2)) + 20;
        if (_cyber) NeonWave(o, reach, w, h, muted: true);
        else
        {
            // rings closing in on the button, the opposite of the mute wave: the call drawing to a point
            var red = MutedColor;
            for (int i = 0; i < 3; i++)
            {
                var ring = new Ellipse { Width = reach * 2, Height = reach * 2, Stroke = Frozen(A(red, (byte)(0xD0 - i * 0x35))), StrokeThickness = 2.2, RenderTransformOrigin = new Point(0.5, 0.5) };
                Canvas.SetLeft(ring, o.X - reach);
                Canvas.SetTop(ring, o.Y - reach);
                var st = new ScaleTransform(1, 1);
                ring.RenderTransform = st;
                ring.Opacity = 0;
                FxAdd(ring);
                var begin = TimeSpan.FromMilliseconds(i * 90);
                var shrink = new DoubleAnimation(1, 0.04, TimeSpan.FromMilliseconds(560)) { BeginTime = begin, EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseIn } };
                var fade = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(560))));
                fade.Completed += (_, _) => FxRemove(ring);
                st.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
                ring.BeginAnimation(OpacityProperty, fade);
            }
        }
        After(700, () => Blush(false));
    }

    /// <summary>Leaves the call: the app's own leave button, else its shortcut, else its window so you can.</summary>
    async Task LeaveCallAsync()
    {
        if (_call is not { } call) { _leaving = false; return; }
        string? app = CallKeys.AppOf(call.Key, call.Name);
        OrbHint(Loc.T("Leaving…"), MutedColor, 4000);
        var windows = await Task.Run(() => CallWindows(call, browser: app == "meet"));
        string? pressed = windows.Count > 0 ? await Task.Run(() => CallLeave.Press(windows)) : null;
        AppLog.Write($"leave call ({call.Name}): {(pressed != null ? "pressed \"" + pressed + "\"" : "no leave button found")}");
        if (pressed == null && !await CallShortcutAsync(CallKeys.Act.HangUp, keepFocus: true))
        {
            OrbHint(Loc.T("Couldn't find the leave button"), MutedColor, 2500);
            After(900, async () => { await BackToCallAsync(); Collapse(); });
        }
        // the call ending is noticed by the usual check (a few seconds); if it doesn't end, the button is usable again
        After(6000, () => { _leaving = false; if (_leaveRing != null) _leaveRing.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(_leaveUnits, TimeSpan.FromMilliseconds(300))); });
    }

    /// <summary>Every window of the call's app (for a browser, only windows whose tab in front is a call).</summary>
    static List<IntPtr> CallWindows(CallState call, bool browser)
    {
        string[] parts = call.Key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        string[] callWords = { "Meet", "Teams", "Zoom", "Discord", "WhatsApp", "Slack", "Messenger", "Telegram", "Skype", "Jitsi", "Webex" };
        var list = new List<IntPtr>();
        var pathOf = new Dictionary<uint, string?>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            string title = WindowTitle(h);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!pathOf.TryGetValue(pid, out var path)) pathOf[pid] = path = ProcessPathById(pid);
            if (path == null) return true;
            bool mine = call.Path != null ? string.Equals(path, call.Path, StringComparison.OrdinalIgnoreCase) : parts.All(p => path.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (!mine) return true;
            if (browser && !callWords.Any(wd => title.Contains(wd, StringComparison.OrdinalIgnoreCase))) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ───────────────────────────── a word under the orb ─────────────────────────────

    string? _orbHint;
    Color _orbHintColor;
    int _orbHintId;

    /// <summary>A short message under the mute orb for a moment (null clears it).</summary>
    void OrbHint(string? text, Color color, int ms)
    {
        _orbHint = text;
        _orbHintColor = color;
        int id = ++_orbHintId;
        if (_cOrbLabel != null && text != null)
        {
            _cOrbLabel.Text = text;
            _cOrbLabel.Foreground = Frozen(color);
            _cOrbLabel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(160)));
        }
        if (text == null) { if (_call != null) UpdateCallsPage(); return; }
        After(ms, () => { if (id != _orbHintId) return; _orbHint = null; if (_call != null && _page == CallsPage) UpdateCallsPage(); });
    }

    // ───────────────────────────── the mic watchers ─────────────────────────────

    double _rawTarget, _rawNow, _steadyNoise, _silence;
    readonly VoiceGate _micGate = new();
    bool _hintedNoise, _hintedSilence;

    /// <summary>Your voice for the waveform (always, even muted: that's when it matters), and the two watchers.</summary>
    void WatchMic(float peak, double seconds)
    {
        _micGate.Feed(peak, seconds);
        _rawTarget = _micGate.Level;
        if (CallMuted || _audio.MicMuted) { _steadyNoise = _silence = 0; return; }
        // steady noise: sound without the pauses speech always has
        if (peak > 0.035f) _steadyNoise += seconds; else if (peak < 0.015f) _steadyNoise = 0;
        // a dead mic: not a breath of sound for a long while
        if (peak < 0.0008f) _silence += seconds; else _silence = 0;
        if (!_hintedNoise && _steadyNoise >= 25)
        {
            _hintedNoise = true;
            if (_mode == Mode.Expanded && _page == CallsPage) OrbHint(Loc.T("Steady noise on your mic"), Tint(CallBtn.Quiet), 4000);
            else ShowOsd("open-mic", "", null, Loc.T("Your mic hears steady noise"), Loc.T("Click to mute the call"), null, glyphBrush: Orange, seconds: 3.5, prio: Prio.High,
                motion: Motion.Pulse, onClick: () => _ = SetCallMutedAsync(true));
        }
        if (!_hintedSilence && _silence >= 60)
        {
            _hintedSilence = true;
            if (_mode == Mode.Expanded && _page == CallsPage) { OrbHint(Loc.T("No sound from your mic"), MutedColor, 4000); ToggleCallSheet(Sheet.Audio); }
            else ShowOsd("dead-mic", "", null, Loc.T("No sound from your mic"), Loc.T("Click to pick another one"), null, glyphBrush: Red, seconds: 3.5, prio: Prio.High,
                motion: Motion.Shake, onClick: () => { ExpandTo(CallsPage); After(380, () => { if (_sheet != Sheet.Audio) ToggleCallSheet(Sheet.Audio); }); });
        }
    }

    void ResetMicWatch()
    {
        _steadyNoise = _silence = _rawNow = _rawTarget = 0;
        _micGate.Reset();
        _hintedNoise = _hintedSilence = _leaving = _leaveHolding = false;
        _orbHint = null;
    }

    /// <summary>The waveform, each frame the page is on screen.</summary>
    void StepWave(double dt)
    {
        _rawNow += (_rawTarget - _rawNow) * (1 - Math.Exp(-dt * (_rawTarget > _rawNow ? 22 : 8)));
        if (_cWave == null) return;
        _cWave.Tint = CallMuted && !_pttTalking ? MutedColor : LiveColor;
        _cWave.Push(_rawNow, _micGate.Voiced, _micGate.Noise, dt);
    }
}
