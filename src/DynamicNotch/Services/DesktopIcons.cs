using System.Runtime.InteropServices;

namespace DynamicNotch.Services;

/// <summary>
/// Windows' own "Show desktop icons" (right-click the desktop → View): the same command Explorer runs, so the choice
/// is Windows', it stays after a restart, and the desktop's right-click menu agrees with the notch. The desktop's
/// icon view lives under "Progman", or under a "WorkerW" window when a wallpaper slideshow or wallpaper app is on.
/// Without Explorer's desktop (another shell) there's nothing to switch, and the notch hides its button.
/// </summary>
public static class DesktopIcons
{
    const int WM_COMMAND = 0x0111, ToggleDesktopIcons = 0x7402;

    static IntPtr DefView()
    {
        var progman = FindWindow("Progman", null);
        var view = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero) return view;
        IntPtr w = IntPtr.Zero;
        while ((w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
        {
            view = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero) return view;
        }
        return IntPtr.Zero;
    }

    /// <summary>Are the icons shown? Null when this PC has no Explorer desktop to switch.</summary>
    public static bool? Shown
    {
        get
        {
            var view = DefView();
            if (view == IntPtr.Zero) return null;
            var list = FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            return list == IntPtr.Zero ? null : IsWindowVisible(list);
        }
    }

    /// <summary>Shows the icons if hidden, hides them if shown. False when there's no desktop to switch.</summary>
    public static bool Toggle()
    {
        var view = DefView();
        if (view == IntPtr.Zero) return false;
        return PostMessage(view, WM_COMMAND, (IntPtr)ToggleDesktopIcons, IntPtr.Zero); // never waits on Explorer
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
