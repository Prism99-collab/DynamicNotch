using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// The mouse wheel over the notch, on every PC. The notch never takes focus (so it never steals it from what you're
/// doing); Windows then sends the wheel to the window under the pointer only when "Scroll inactive windows when I
/// hover over them" is on (the default, but many PCs have it off): there, scrolling the notch's lists did nothing.
/// So while the notch is on screen a low-level mouse hook on its own thread watches the wheel: a turn with the
/// pointer over the notch is taken (Windows doesn't deliver it a second time) and handed to whatever is under the
/// pointer, exactly as a normal wheel event would be; every other wheel turn goes on untouched in microseconds.
/// Not in a game, not while hidden or locked: then there's no hook at all.
/// </summary>
public partial class NotchWindow
{
    const int WM_MOUSEWHEEL_LL = 0x020A, WM_MOUSEHWHEEL_LL = 0x020E, WM_QUIT = 0x0012;

    Thread? _wheelThread;
    volatile uint _wheelThreadId;
    volatile bool _wheelStop;
    static bool _wheelLogged;
    HookProc? _wheelProc; // kept alive: the hook calls it from native code
    // the notch on screen, in physical pixels, kept current by the poll (read by the hook thread)
    int _hitL, _hitT, _hitR, _hitB;

    /// <summary>Called on every poll: the hook runs only while the notch is there to scroll, and knows where it is.</summary>
    void WheelPoll()
    {
        bool want = !_hidden && _mode != Mode.Game && !InGame && !_settings.Locked && IsVisible;
        if (want)
        {
            try
            {
                var tl = Pill.PointToScreen(new Point(0, 0));
                var br = Pill.PointToScreen(new Point(Pill.ActualWidth, Pill.ActualHeight));
                Volatile.Write(ref _hitL, (int)tl.X); Volatile.Write(ref _hitT, (int)tl.Y);
                Volatile.Write(ref _hitR, (int)br.X); Volatile.Write(ref _hitB, (int)br.Y);
            }
            catch { want = false; } // not on screen right now
        }
        ArmWheel(want);
    }

    void ArmWheel(bool on)
    {
        if (on)
        {
            if (_wheelThread != null) return;
            _wheelStop = false;
            _wheelThread = new Thread(WheelLoop) { IsBackground = true, Name = "wheel", Priority = ThreadPriority.AboveNormal };
            _wheelThread.Start();
        }
        else
        {
            if (_wheelThread == null) return;
            _wheelStop = true;
            uint id = _wheelThreadId;
            if (id != 0) PostThreadMessage(id, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _wheelThread = null;
        }
    }

    void WheelLoop()
    {
        IntPtr hook = IntPtr.Zero;
        try
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // a message queue first, so a stop can always reach this thread
            _wheelThreadId = GetCurrentThreadId();
            if (_wheelStop) return;
            _wheelProc = OnWheelHook;
            hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _wheelProc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) { Services.AppLog.Write("wheel: the hook was refused"); return; }
            if (!_wheelLogged) { _wheelLogged = true; Services.AppLog.Write("wheel: delivered to the notch directly (works whatever Windows' scroll setting)"); }
            while (!_wheelStop && GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        }
        catch { }
        finally
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            _wheelThreadId = 0;
        }
    }

    IntPtr OnWheelHook(int code, IntPtr wParam, IntPtr lParam)
    {
        int msg = wParam.ToInt32();
        if (code >= 0 && (msg == WM_MOUSEWHEEL_LL || msg == WM_MOUSEHWHEEL_LL) && !_wheelStop)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (m.X >= Volatile.Read(ref _hitL) && m.X < Volatile.Read(ref _hitR) && m.Y >= Volatile.Read(ref _hitT) && m.Y < Volatile.Read(ref _hitB))
            {
                short delta = (short)((m.MouseData >> 16) & 0xFFFF);
                int x = m.X, y = m.Y;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => RouteWheel(msg == WM_MOUSEHWHEEL_LL, delta, x, y));
                return (IntPtr)1; // ours: Windows doesn't deliver it again
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>The wheel turn, delivered to what's under the pointer the way WPF would (preview first, then the event).</summary>
    void RouteWheel(bool horizontal, short delta, int x, int y)
    {
        try
        {
            if (horizontal)
            {
                if (_mode == Mode.Expanded) OnHorizontalWheel(delta);
                return;
            }
            var p = Root.PointFromScreen(new Point(x, y));
            var target = Root.InputHitTest(p) as UIElement ?? Pill;
            var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            target.RaiseEvent(preview);
            if (preview.Handled) return;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent };
            target.RaiseEvent(wheel);
        }
        catch (Exception ex) { Services.AppLog.Write("wheel: " + ex.Message); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PEEKMSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll")] static extern bool PeekMessage(out PEEKMSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
