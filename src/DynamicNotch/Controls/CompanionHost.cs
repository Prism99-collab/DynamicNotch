using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch.Controls;

/// <summary>
/// The companion's own little window, under the notch. A see-through window is copied back from the graphics chip
/// on every frame it changes: in her own small window only her few pixels are, at the screen's full rate, instead of
/// the whole notch window each time she moves. It never takes a click or the focus, never shows in Alt-Tab, and is
/// hidden (costing nothing) whenever she isn't out.
/// </summary>
public sealed class CompanionHost : Window
{
    /// <summary>Wide enough for her furthest spot along the widest resting notch, tall enough for the swing.</summary>
    public const double HostWidth = 460, HostHeight = 176;

    IntPtr _hwnd;
    (int X, int Y, int W, int H) _placed;

    public CompanionHost(Companion pal)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "Dynamic Notch companion";
        Width = HostWidth;
        Height = HostHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var stage = new Grid { ClipToBounds = true, IsHitTestVisible = false, UseLayoutRounding = true };
        stage.Children.Add(pal);
        Content = stage;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT; // never the focus, never a click, never in Alt-Tab
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    /// <summary>Its top centre at the notch's top centre (physical pixels), at the notch's scale.</summary>
    public void Place(int centerX, int top, double scale)
    {
        if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        int w = (int)Math.Round(HostWidth * scale), h = (int)Math.Round(HostHeight * scale);
        var at = (centerX - w / 2, top, w, h);
        if (at == _placed) return;
        _placed = at;
        SetWindowPos(_hwnd, HWND_TOPMOST, at.Item1, at.top, w, h, SWP_NOACTIVATE);
    }

    public void ShowQuiet()
    {
        if (!IsVisible) Show();
    }

    public void HideQuiet()
    {
        if (IsVisible) Hide();
    }
}
