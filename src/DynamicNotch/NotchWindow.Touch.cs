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
    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201;

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
        // tap the notch: open it; tap the side island: it takes the front
        Pill.MouseLeftButtonUp += (_, e) =>
        {
            if (!_touchMode || !IsBase(_mode)) return;
            e.Handled = true;
            Expand(byHotkey: false);
        };
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
    /// A finger came down. While the notch is still click-through (the first touch), or slim and hard to hit,
    /// a tap on or just around it opens it.
    /// </summary>
    void OnTouchDown(int x, int y)
    {
        if (_hidden || !IsBase(_mode)) return;
        double cx = _winLeft + _winW / 2.0;
        double halfW = (_sw.Value / 2 + 24) * _scale, h = (Math.Max(_sh.Value, 10) + 18) * _scale;
        if (Math.Abs(x - cx) <= halfW && y >= _mon.Top && y <= _mon.Top + h) Expand(byHotkey: false);
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
