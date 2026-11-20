using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch.Controls;

/// <summary>
/// The companion's own small window, under the notch. Only its few pixels are redrawn as it moves (not the whole
/// notch window), it never takes the focus or shows in Alt-Tab, and clicks pass through it except right on the face
/// (so it can be poked without ever getting in the way). Hidden, it costs nothing.
/// </summary>
public sealed class FaceHost : Window
{
    public const double HostWidth = 240, HostHeight = 150;

    IntPtr _hwnd;
    bool _clickable;
    (int X, int Y, int W, int H) _placed;

    public Canvas Stage { get; } = new() { Width = HostWidth, Height = HostHeight, ClipToBounds = true, Background = Brushes.Transparent };

    public FaceHost()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Title = "Pixo companion";
        Width = HostWidth;
        Height = HostHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        Content = Stage;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT; // never the focus, never in Alt-Tab, clicks pass
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    /// <summary>Clicks reach the window only while the pointer is on the face.</summary>
    public void Clickable(bool on)
    {
        if (on == _clickable || _hwnd == IntPtr.Zero) return;
        _clickable = on;
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex = on ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Its top centre at a point on the screen (physical pixels), at the notch's scale.</summary>
    public void Place(int centerX, int top, double scale)
    {
        if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        int w = (int)Math.Round(HostWidth * scale), h = (int)Math.Round(HostHeight * scale);
        var at = (centerX - w / 2, top, w, h);
        if (at == _placed) return;
        _placed = at;
        SetWindowPos(_hwnd, HWND_TOPMOST, at.Item1, at.top, w, h, SWP_NOACTIVATE);
    }

    /// <summary>Where it is on screen (physical pixels).</summary>
    public (int X, int Y, int W, int H) Placed => _placed;

    public void ShowQuiet() { if (!IsVisible) Show(); }
    public void HideQuiet() { Clickable(false); if (IsVisible) Hide(); }
}
