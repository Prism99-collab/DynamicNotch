using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DynamicNotch;

/// <summary>
/// Hover hints drawn inside the notch. Windows' own tooltips are white and open in a separate window that the
/// always-on-top notch covers, so they are cancelled and shown here instead: dark glass, in front, gliding in.
/// </summary>
public partial class NotchWindow
{
    FrameworkElement? _tipOwner;

    void WireTips()
    {
        AddHandler(FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler(OnTipOpening), true);
        PreviewMouseDown += (_, _) => HideTip();
        PreviewMouseWheel += (_, _) => HideTip();
    }

    void OnTipOpening(object sender, ToolTipEventArgs e)
    {
        e.Handled = true; // never the white Windows tooltip
        if (e.Source is not FrameworkElement el || _mode != Mode.Expanded || !el.IsMouseOver) return;
        ShowTip(el, ToolTipService.GetToolTip(el)?.ToString());
    }

    /// <summary>Shows a hint for <paramref name="el"/> right away (it hides when the pointer leaves it).</summary>
    void ShowTip(FrameworkElement el, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _mode != Mode.Expanded) return;
        if (_tipOwner != el)
        {
            if (_tipOwner != null) _tipOwner.MouseLeave -= OnTipOwnerLeave;
            _tipOwner = el;
            el.MouseLeave += OnTipOwnerLeave;
        }

        TipText.Text = text;
        Tip.Visibility = Visibility.Visible;
        Tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size tip = Tip.DesiredSize;

        // below the element, or above it when there's no room; kept inside the open panel
        Point at = el.TranslatePoint(new Point(el.ActualWidth / 2, 0), Root);
        double panelLeft = (Root.ActualWidth - ExpandedSize.Width) / 2 + 6, panelRight = panelLeft + ExpandedSize.Width - 12;
        double x = Math.Clamp(at.X - tip.Width / 2, panelLeft, Math.Max(panelLeft, panelRight - tip.Width));
        double below = at.Y + el.ActualHeight + 6;
        bool under = below + tip.Height <= ExpandedSize.Height - 4;
        double y = under ? below : Math.Max(2, at.Y - tip.Height - 6);
        Tip.Margin = new Thickness(x, y, 0, 0);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(160);
        Tip.BeginAnimation(OpacityProperty, new DoubleAnimation(Tip.Opacity, 1, dur) { EasingFunction = ease });
        TipShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(under ? -5 : 5, 0, dur) { EasingFunction = ease });
        TipScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
        TipScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
    }

    void OnTipOwnerLeave(object sender, MouseEventArgs e) => HideTip();

    void HideTip()
    {
        if (_tipOwner != null) { _tipOwner.MouseLeave -= OnTipOwnerLeave; _tipOwner = null; }
        if (Tip == null || Tip.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => { if (_tipOwner == null) Tip.Visibility = Visibility.Collapsed; };
        Tip.BeginAnimation(OpacityProperty, fade);
    }
}
