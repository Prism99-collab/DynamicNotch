using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicNotch;

/// <summary>
/// How the chat feels:
///  • a long message wraps: the typing line grows up to five lines, then scrolls; in the search, the results give up
///    the room, so the notch keeps its size;
///  • the chat glides in from the search and fades back out into it;
///  • typing only restyles the line when it goes from empty to typed (or back), not on every key;
///  • in the Neon look, messages switch on like a HUD (a short flicker and a sideways tear).
/// </summary>
public partial class NotchWindow
{
    /// <summary>The typing line grew or shrank a line: in the search (its area has a set size) the results make the room.</summary>
    void OnLineGrew(object? sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged || _launchBody == null || double.IsNaN(_launchBody.Height)) return;
        double d = Math.Max(40, e.NewSize.Height) - Math.Max(40, e.PreviousSize.Height);
        if (Math.Abs(d) > 0.5) _launchBody.Height = Math.Max(40, _launchBody.Height - d);
    }

    /// <summary>The chat comes in: it rises and settles from a little smaller (Neon: it flickers on, a scan line passes).</summary>
    void ChatIn()
    {
        if (_chatRoot == null) return;
        _chatRoot.IsHitTestVisible = true;
        if (Eco) { _chatRoot.BeginAnimation(OpacityProperty, null); _chatRoot.Opacity = 1; return; }
        var scale = new ScaleTransform(0.96, 0.96);
        var rise = new TranslateTransform(0, 14);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rise);
        _chatRoot.RenderTransformOrigin = new Point(0.5, 0);
        _chatRoot.RenderTransform = group;
        var ease = new Controls.SpringEase { Damping = 0.8 };
        var time = TimeSpan.FromMilliseconds(460);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, time) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, time) { EasingFunction = ease });
        rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, time) { EasingFunction = new Controls.SpringEase { Damping = 0.86 } });
        if (_cyber)
        {
            var on = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(220) };
            on.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            on.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))));
            on.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(85))));
            on.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
            _chatRoot.BeginAnimation(OpacityProperty, on);
            ScanIn();
        }
        else _chatRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>The chat goes: it fades and sinks a little while the search comes back under it.</summary>
    void ChatOut()
    {
        var root = _chatRoot;
        if (root == null) return;
        if (Eco || root.Visibility != Visibility.Visible) { root.Visibility = Visibility.Collapsed; return; }
        root.IsHitTestVisible = false;
        var rise = new TranslateTransform(0, 0);
        root.RenderTransform = rise;
        var time = TimeSpan.FromMilliseconds(150);
        rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 8, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        var fade = new DoubleAnimation(root.Opacity, 0, time);
        fade.Completed += (_, _) =>
        {
            if (_chatMode) return; // back in already
            root.Visibility = Visibility.Collapsed;
            root.BeginAnimation(OpacityProperty, null);
            root.Opacity = 1;
            root.IsHitTestVisible = true;
        };
        root.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>What the typing line shows last depended on: empty or not, only spaces or not.</summary>
    int _chatLineState = -1;

    /// <summary>Restyles the typing line only when what it shows would change (not on every key).</summary>
    void StyleChatLineIfChanged()
    {
        string t = _launchBox?.Text ?? "";
        int state = (t.Length > 0 ? 1 : 0) | (!string.IsNullOrWhiteSpace(t) ? 2 : 0);
        if (state == _chatLineState) return;
        _chatLineState = state;
        StyleChatLine();
    }

    /// <summary>Neon: a message switches on like a HUD line, a short flicker with the picture tearing sideways.</summary>
    void GlitchIn(FrameworkElement el)
    {
        if (Eco) return;
        var tear = new TranslateTransform();
        el.RenderTransform = tear;
        Steps(tear, TranslateTransform.XProperty, 30, 5, -3, 1.5, 0);
        var on = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(180) };
        on.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        on.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(30))));
        on.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
        on.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        el.BeginAnimation(OpacityProperty, on);
    }
}
