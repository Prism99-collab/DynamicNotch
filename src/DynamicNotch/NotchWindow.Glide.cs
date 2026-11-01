using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Scrolling in the notch's lists (sound devices, the mixer, clipboard history, recent files, the shelf, "everything
/// live") glides instead of jumping: each wheel step eases the list to where it's going, and steps that come
/// quickly add up. A list that has nothing to scroll leaves the wheel alone (it can still change pages). Sliders
/// and the colour wheel keep their own wheel. Only this window's lists; nothing runs between scrolls.
/// </summary>
public partial class NotchWindow
{
    static bool _glideHandler;

    /// <summary>Where a list is gliding to (while it glides).</summary>
    static readonly DependencyProperty GlideTargetProperty = DependencyProperty.RegisterAttached("GlideTarget", typeof(double), typeof(NotchWindow), new PropertyMetadata(double.NaN));

    /// <summary>The animated offset; every change scrolls the list there.</summary>
    static readonly DependencyProperty GlideOffsetProperty = DependencyProperty.RegisterAttached("GlideOffset", typeof(double), typeof(NotchWindow),
        new PropertyMetadata(0.0, (d, e) => { if (d is ScrollViewer sv) sv.ScrollToVerticalOffset((double)e.NewValue); }));

    void WireGlide()
    {
        if (_glideHandler) return;
        _glideHandler = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), PreviewMouseWheelEvent, new MouseWheelEventHandler((s, e) =>
        {
            if (s is ScrollViewer sv && Window.GetWindow(sv) == this) Glide(sv, e);
        }));
    }

    void Glide(ScrollViewer sv, MouseWheelEventArgs e)
    {
        if (e.Handled || sv.ScrollableHeight <= 0) return;
        if (e.OriginalSource is DependencyObject src && (FindParent<PillSlider>(src) != null || FindParent<ColorWheel>(src) != null)) return;
        // the innermost list scrolls (a list inside a list keeps its own wheel)
        if (e.OriginalSource is DependencyObject o && FindParent<ScrollViewer>(o) is { } inner && !ReferenceEquals(inner, sv) && inner.ScrollableHeight > 0) return;
        e.Handled = true;
        HideTip();
        double target = (double)sv.GetValue(GlideTargetProperty);
        if (double.IsNaN(target)) target = sv.VerticalOffset;
        target = Math.Clamp(target - e.Delta * 0.55, 0, sv.ScrollableHeight);
        sv.SetValue(GlideTargetProperty, target);
        if (Eco) { sv.ScrollToVerticalOffset(target); sv.SetValue(GlideTargetProperty, double.NaN); return; }
        var glide = new DoubleAnimation(sv.VerticalOffset, target, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        glide.Completed += (_, _) =>
        {
            if ((double)sv.GetValue(GlideTargetProperty) == target) sv.SetValue(GlideTargetProperty, double.NaN);
        };
        sv.BeginAnimation(GlideOffsetProperty, glide);
    }
}
