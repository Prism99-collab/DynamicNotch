using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// Fingers and pens. The notch normally lets clicks fall through to the window under it and opens when the mouse
/// touches the top edge, which a finger can't do. So on a touch screen it watches (from its own thread, so the
/// system's pointer never waits on it) whether you're using touch / pen or the mouse:
///   touch: tap the notch to open it, tap outside or swipe up to close, tap the side island to swap them.
///   mouse: everything as before, click-through and hover to open.
/// On a PC without a touch screen or pen none of this runs.
/// </summary>
public partial class NotchWindow
{
    bool _touchMode;
    Thread? _touchThread;
    HookProc? _hookProc; // kept alive: the hook calls it from native code
    volatile bool _touchFlag;
    int _lastTouchX, _lastTouchY;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);

    const int SM_DIGITIZER = 94, NID_READY = 0x80, NID_ANY = 0x01 | 0x02 | 0x04 | 0x08; // integrated / external touch / pen
    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202;

    // ───── the finger on the notch: pull down to open, swipe sideways to switch, tap to open ─────
    volatile bool _gestureLive;         // a finger came down on the notch and hasn't lifted yet
    int _gestureX, _gestureY, _movePosted;
    bool _pulling;                      // the notch is following a finger down
    double _pull;                       // how far (DIPs) it's been pulled
    double _gestureStartX, _gestureStartY, _lastMoveY;
    long _fingerMoveAt;
    double _pullVelocity;               // DIPs / s, positive downward
    bool _swiped;                       // this gesture already switched the activity

    static bool HasTouchOrPen
    {
        get
        {
            int d = GetSystemMetrics(SM_DIGITIZER);
            return (d & NID_READY) != 0 && (d & NID_ANY) != 0;
        }
    }

    void WireTouch()
    {
        // the notch itself is handled by the finger gesture (tap, pull down, swipe); a tap on the side island
        // brings it to the front
        SecondBubble.MouseLeftButtonUp += (_, e) =>
        {
            if (!_touchMode || _secondShown == LiveActivity.None) return;
            e.Handled = true;
            if (_secondShown == LiveActivity.Media) _cardMedia = true;
            Pin(_secondShown);
        };

        if (!HasTouchOrPen) return;
        _touchThread = new Thread(TouchLoop) { IsBackground = true, Name = "touch", Priority = ThreadPriority.AboveNormal };
        _touchThread.Start();
    }

    /// <summary>A low-level mouse hook on its own thread: Windows marks the mouse messages it makes from touch and pen.</summary>
    void TouchLoop()
    {
        try
        {
            _hookProc = OnLowLevelMouse;
            if (SetWindowsHookEx(14 /* WH_MOUSE_LL */, _hookProc, GetModuleHandle(null), 0) == IntPtr.Zero) return;
            Services.AppLog.Write("touch: watching for touch and pen");
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        }
        catch { }
    }

    IntPtr OnLowLevelMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            // MI_WP_SIGNATURE: this mouse message was made from a finger or a pen
            bool fromTouch = ((long)m.ExtraInfo & 0xFFFFFF00) == 0xFF515700;
            if (fromTouch)
            {
                _lastTouchX = m.X; _lastTouchY = m.Y;
                if (!_touchFlag) { _touchFlag = true; Dispatcher.BeginInvoke(DispatcherPriority.Input, () => SetTouchMode(true)); }
                if (msg == WM_LBUTTONDOWN) { int x = m.X, y = m.Y; Dispatcher.BeginInvoke(DispatcherPriority.Input, () => OnTouchDown(x, y)); }
                else if (msg == WM_MOUSEMOVE && _gestureLive)
                {
                    // follow the finger, at most one update queued at a time (the UI always gets the latest point)
                    Volatile.Write(ref _gestureX, m.X);
                    Volatile.Write(ref _gestureY, m.Y);
                    if (Interlocked.Exchange(ref _movePosted, 1) == 0)
                        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                        {
                            Interlocked.Exchange(ref _movePosted, 0);
                            OnTouchMove(Volatile.Read(ref _gestureX), Volatile.Read(ref _gestureY));
                        });
                }
                else if (msg == WM_LBUTTONUP && _gestureLive)
                {
                    int x = m.X, y = m.Y;
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () => OnTouchUp(x, y));
                }
            }
            else if (_touchFlag && msg == WM_MOUSEMOVE && (m.Flags & 0x1) == 0 /* not injected */ &&
                     (Math.Abs(m.X - _lastTouchX) > 3 || Math.Abs(m.Y - _lastTouchY) > 3))
            {
                // the real mouse moved: back to hover and click-through
                _touchFlag = false;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => SetTouchMode(false));
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    void SetTouchMode(bool on)
    {
        if (on == _touchMode) return;
        _touchMode = on;
        if (on) { SetDodged(false); _lean.Target = 0; }
        RefreshClickThrough();
        UpdateAura(_mode == Mode.Osd ? _osdAccent : null);
    }

    /// <summary>
    /// A finger came down. On the notch (or just around it: the slim bar is thin) it starts a gesture:
    /// a tap opens it, pulling down stretches it after the finger and opens it, a sideways swipe switches what it shows.
    /// </summary>
    void OnTouchDown(int x, int y)
    {
        _gestureLive = false;
        if (_hidden || !IsBase(_mode) || _mode == Mode.Game || _settings.Locked) return; // never in a game or locked
        double cx = _winLeft + _winW / 2.0;
        double halfW = (Math.Max(_sw.Value, IdleSize.Width) / 2 + 16) * _scale, h = (Math.Max(_sh.Value, 12) + 22) * _scale;
        if (Math.Abs(x - cx) > halfW || y < _mon.Top || y > _mon.Top + h) return;
        _gestureLive = true;
        _gestureStartX = x / _scale;
        _gestureStartY = _lastMoveY = y / _scale;
        _fingerMoveAt = Environment.TickCount64;
        _pullVelocity = 0;
        _pull = 0;
        _pulling = false;
        _swiped = false;
        // the finger presses in: a small squeeze, like a button
        _pressed = true;
        PressShape();
    }

    void OnTouchMove(int x, int y)
    {
        if (!_gestureLive || !IsBase(_mode)) return;
        double px = x / _scale, py = y / _scale;
        double dx = px - _gestureStartX, dy = py - _gestureStartY;
        long now = Environment.TickCount64;
        double ms = Math.Max(1, now - _fingerMoveAt);
        _pullVelocity = _pullVelocity * 0.4 + (py - _lastMoveY) / ms * 1000 * 0.6;
        _lastMoveY = py;
        _fingerMoveAt = now;

        // sideways: switch which live activity leads (left / right), once per swipe
        if (!_pulling && !_swiped && Math.Abs(dx) > 34 && Math.Abs(dx) > Math.Abs(dy) * 1.5)
        {
            _swiped = true;
            _pressed = false;
            SwipeActivity(dx < 0 ? -1 : 1);
            return;
        }
        if (_swiped) return;

        // down: the notch follows the finger, with resistance (it stretches less the further you pull)
        if (dy > 6 && dy > Math.Abs(dx))
        {
            _pressed = false;
            _pulling = true;
            _pull = dy;
            ApplyShape();
        }
        else if (_pulling && dy <= 6)
        {
            _pulling = false;
            _pull = 0;
            ApplyShape();
        }
    }

    void OnTouchUp(int x, int y)
    {
        if (!_gestureLive) return;
        _gestureLive = false;
        bool wasPulling = _pulling;
        double pull = _pull, velocity = _pullVelocity;
        double dx = x / _scale - _gestureStartX, dy = y / _scale - _gestureStartY;
        _pulling = false;
        _pull = 0;
        _pressed = false;
        if (_swiped) { ApplyShape(); return; }
        if (!IsBase(_mode)) return;

        bool tap = Math.Abs(dx) < 10 && Math.Abs(dy) < 10;
        // far enough, or a quick flick: open from right where the finger left it (the springs carry on from there)
        bool open = tap || (wasPulling && (pull > 46 || (velocity > 450 && pull > 14)));
        if (open) Expand(byHotkey: false);
        else ApplyShape(); // not far enough: spring back
    }

    void CancelTouchGesture()
    {
        _gestureLive = false;
        bool reshape = _pulling || _pressed;
        _pulling = false;
        _pull = 0;
        _pressed = false;
        if (reshape && IsBase(_mode)) ApplyShape();
    }

    /// <summary>The squeeze under a finger that just touched the notch.</summary>
    void PressShape()
    {
        if (!IsBase(_mode)) return;
        ApplyShape();
    }

    /// <summary>A sideways swipe on the notch: the next (or previous) live activity takes the front.</summary>
    void SwipeActivity(int direction)
    {
        var items = SwitchItems();
        if (items.Count < 2) { Bump(direction); ApplyShape(); return; } // nothing to switch to: a little nudge says so
        var front = FrontItem(items);
        int i = front == null ? 0 : Math.Max(0, items.FindIndex(t => t.Key == front.Key));
        var next = items[(i + direction + items.Count) % items.Count];
        Pick(next);
        Bump(direction);
    }

    /// <summary>Swipe up on the open notch (with a finger) to close it, like a phone.</summary>
    bool TouchSwipeUpToClose(double dx, double dy)
    {
        if (!_touchMode || dy > -36 || Math.Abs(dy) < Math.Abs(dx) * 1.4) return false;
        _maybeSwipe = false;
        Collapse();
        return true;
    }
}
