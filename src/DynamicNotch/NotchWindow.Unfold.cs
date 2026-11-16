using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// How the open notch's content arrives and leaves, so it always moves with the shape and never leaves it empty:
///  • opening, it pours out of the notch: a soft edge wipes down from the top while the content unfolds from the
///    top edge (a little short and narrow, springing to full size); the rows then rise in a cascade (Flourish);
///  • closing, it's drawn back up into the notch: the edge wipes up while the content folds toward the top, all
///    quicker than the shape, so the black never shows empty. Neon closes like a tube switching off instead
///    (NotchWindow.CyberFx): the whole shape drops to a band of light, which then pulls in sideways.
/// Everything runs on the compositor; the mask is removed the moment it's done.
/// </summary>
public partial class NotchWindow
{
    int _unfoldId;

    void PourExpanded(int delayMs)
    {
        int id = ++_unfoldId;
        var panel = Expanded;
        panel.Visibility = Visibility.Visible;
        panel.RenderTransformOrigin = new Point(0.5, 0);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)) { BeginTime = begin, EasingFunction = ease });
        if (panel.RenderTransform is ScaleTransform st && !st.IsFrozen)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(440)) { BeginTime = begin, EasingFunction = new SpringEase { Damping = 0.76 } });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.84, 1, TimeSpan.FromMilliseconds(460)) { BeginTime = begin, EasingFunction = new SpringEase { Damping = 0.72 } });
        }

        // the soft edge sweeping down
        var (mask, front, back) = Wipe(-0.25);
        panel.OpacityMask = mask;
        var down = new DoubleAnimation(-0.25, 1.05, TimeSpan.FromMilliseconds(330)) { BeginTime = begin, EasingFunction = ease };
        var downBack = new DoubleAnimation(0, 1.3, TimeSpan.FromMilliseconds(330)) { BeginTime = begin, EasingFunction = ease };
        down.Completed += (_, _) => { if (id == _unfoldId && _activePanel == panel) panel.OpacityMask = null; };
        front.BeginAnimation(GradientStop.OffsetProperty, down);
        back.BeginAnimation(GradientStop.OffsetProperty, downBack);
    }

    void RetractExpanded()
    {
        int id = ++_unfoldId;
        var panel = Expanded;
        panel.RenderTransformOrigin = new Point(0.5, 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var d = TimeSpan.FromMilliseconds(170);

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (id != _unfoldId || _activePanel == panel) return;
            panel.Visibility = Visibility.Collapsed;
            panel.OpacityMask = null;
        };
        panel.BeginAnimation(OpacityProperty, fade);
        if (panel.RenderTransform is ScaleTransform st && !st.IsFrozen)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.93, d) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.76, d) { EasingFunction = ease });
        }

        // the soft edge sweeping back up
        var (mask, front, back) = Wipe(1.05);
        panel.OpacityMask = mask;
        front.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(1.05, -0.25, d) { EasingFunction = ease });
        back.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(1.3, 0, d) { EasingFunction = ease });
    }

    /// <summary>A top-down mask: shown above the front, fading out over a short band, hidden below it.</summary>
    static (LinearGradientBrush Mask, GradientStop Front, GradientStop Back) Wipe(double at)
    {
        var front = new GradientStop(Colors.Black, at);
        var back = new GradientStop(Colors.Transparent, at + 0.25);
        var mask = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
        mask.GradientStops.Add(front);
        mask.GradientStops.Add(back);
        return (mask, front, back);
    }
}
