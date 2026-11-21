using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DynamicNotch;

/// <summary>
/// Hover hints drawn inside the notch. Windows' own tooltips are white and open in a separate window that the
/// always-on-top notch covers, so they are cancelled and shown here instead: dark glass (or the theme's), in front,
/// gliding in after a short pause, and gone again after a few seconds so they never sit in the way.
/// Windows raises "tooltip opening" on the element itself (it doesn't travel up to the window), so it's caught for
/// every element by a class handler, and only for this window's elements.
/// </summary>
public partial class NotchWindow
{
    FrameworkElement? _tipOwner;
    static bool _tipHandler;
    readonly System.Windows.Threading.DispatcherTimer _tipLife = new() { Interval = TimeSpan.FromSeconds(3.5) };

    void WireTips()
    {
        if (!_tipHandler)
        {
            _tipHandler = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent,
                new ToolTipEventHandler((s, e) => { if (s is FrameworkElement el && Window.GetWindow(el) == this) OnTipOpening(el, e); }), true);
        }
        PreviewMouseDown += (_, _) => HideTip();
        PreviewMouseWheel += (_, _) => HideTip();
        _tipLife.Tick += (_, _) => HideTip();
    }

    FrameworkElement? _tipPending;

    /// <summary>
    /// A hint only when it helps, where it belongs: never while the notch or its page is still moving (the element
    /// isn't where it will be), never for something faded out or hidden, never for a text that's already shown in
    /// full, and only once the pointer has rested on it a moment (passing over things doesn't pop hints everywhere).
    /// </summary>
    void OnTipOpening(FrameworkElement el, ToolTipEventArgs e)
    {
        e.Handled = true; // never the white Windows tooltip
        if (!TipWanted(el)) return;
        string? text = ToolTipService.GetToolTip(el)?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return;
        _tipPending = el;
        After(220, () =>
        {
            if (_tipPending != el) return;
            _tipPending = null;
            if (TipWanted(el)) ShowTip(el, ToolTipService.GetToolTip(el)?.ToString());
        });
    }

    bool TipWanted(FrameworkElement el)
    {
        if (_mode != Mode.Expanded || !el.IsMouseOver || !el.IsVisible || _animating) return false;
        if ((DateTime.UtcNow - _expandedAt).TotalMilliseconds < 600) return false; // just opened: things are still arriving
        // faded out, or inside something faded out (a card making way for another)
        for (DependencyObject? d = el; d != null && d != Root; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is UIElement u && (u.Opacity < 0.3 || u.Visibility != Visibility.Visible)) return false;
        // a title that fits needs no hint repeating it
        if (el is TextBlock tb && tb.TextWrapping == TextWrapping.NoWrap && tb.TextTrimming != TextTrimming.None && !Trimmed(tb)) return false;
        return true;
    }

    static bool Trimmed(TextBlock tb)
    {
        if (string.IsNullOrEmpty(tb.Text) || tb.ActualWidth <= 0) return false;
        var ft = new System.Windows.Media.FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentCulture, tb.FlowDirection,
            new System.Windows.Media.Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, System.Windows.Media.Brushes.White,
            System.Windows.Media.VisualTreeHelper.GetDpi(tb).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace > tb.ActualWidth - tb.Padding.Left - tb.Padding.Right + 0.5;
    }

    /// <summary>Shows a hint for <paramref name="el"/> right away (it hides when the pointer leaves it).</summary>
    void ShowTip(FrameworkElement el, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _mode != Mode.Expanded) return;
        if (_tipOwner != el)
        {
            if (_tipOwner != null) { _tipOwner.MouseLeave -= OnTipOwnerLeave; _tipOwner.IsVisibleChanged -= OnTipOwnerGone; }
            _tipOwner = el;
            el.MouseLeave += OnTipOwnerLeave;
            el.IsVisibleChanged += OnTipOwnerGone; // its element went (a re-drawn card): so does the hint
        }

        TipText.Text = text;
        Tip.Visibility = Visibility.Visible;
        _tipLife.Stop();
        _tipLife.Start(); // it leaves by itself after a few seconds
        Tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size tip = Tip.DesiredSize;

        // below the element, or above it when there's no room; kept inside the open panel. A small control at the
        // right edge (a close ×, a ⋯) gets it at its left instead, so it never covers the buttons under it.
        Point at = el.TranslatePoint(new Point(el.ActualWidth / 2, 0), Root);
        double panelLeft = (Root.ActualWidth - ExpandedSize.Width) / 2 + 6, panelRight = panelLeft + ExpandedSize.Width - 12;
        double x, y;
        bool under = true, beside = el.ActualWidth < 60 && at.X + el.ActualWidth / 2 > panelRight - 70 && at.X - el.ActualWidth / 2 - tip.Width - 8 > panelLeft;
        if (beside)
        {
            x = at.X - el.ActualWidth / 2 - tip.Width - 6;
            y = Math.Max(2, at.Y + el.ActualHeight / 2 - tip.Height / 2);
        }
        else
        {
            x = Math.Clamp(at.X - tip.Width / 2, panelLeft, Math.Max(panelLeft, panelRight - tip.Width));
            double below = at.Y + el.ActualHeight + 6;
            under = below + tip.Height <= ExpandedSize.Height - 4;
            y = under ? below : Math.Max(2, at.Y - tip.Height - 6);
        }
        Tip.Margin = new Thickness(x, y, 0, 0);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(160);
        Tip.BeginAnimation(OpacityProperty, new DoubleAnimation(Tip.Opacity, 1, dur) { EasingFunction = ease });
        TipShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(beside ? 0 : under ? -5 : 5, 0, dur) { EasingFunction = ease });
        TipScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
        TipScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
    }

    void OnTipOwnerLeave(object sender, MouseEventArgs e) => HideTip();
    void OnTipOwnerGone(object sender, DependencyPropertyChangedEventArgs e) { if (e.NewValue is false) HideTip(); }

    void HideTip()
    {
        _tipPending = null;
        _tipLife.Stop();
        if (_tipOwner != null) { _tipOwner.MouseLeave -= OnTipOwnerLeave; _tipOwner.IsVisibleChanged -= OnTipOwnerGone; _tipOwner = null; }
        if (Tip == null || Tip.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => { if (_tipOwner == null) Tip.Visibility = Visibility.Collapsed; };
        Tip.BeginAnimation(OpacityProperty, fade);
    }
}
