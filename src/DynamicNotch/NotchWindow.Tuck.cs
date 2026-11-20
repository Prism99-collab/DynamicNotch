using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace DynamicNotch;

/// <summary>
/// The side pills live tucked into the notch. Most of the time you see only the notch itself: a second live activity
/// (left) and the microphone / camera (right) show as a small light glowing in its bottom edge, in their colour.
/// They slide out as pills
///  • for a few seconds when they start (the mic comes on, a timer starts while music plays...), and
///  • while the pointer is near the notch (you're reaching for it: everything shows),
/// then they're swallowed back into the notch and become the light again.
/// The light breathes slowly (still in battery saving), and costs nothing between two breaths' frames.
/// </summary>
public partial class NotchWindow
{
    DateTime _budUntil;
    bool _pointerNear, _privacyRaw;
    LiveActivity _secondRaw;
    Border? _tuckL, _tuckR;
    Color _secondColor = Colors.White;

    /// <summary>The pills are out: tucking is off, one just started, or the pointer is near.</summary>
    bool IslandsOut => !_settings.TuckPills || DateTime.UtcNow < _budUntil || _peek || _pointerNear;

    /// <summary>Something started: the pills come out for a few seconds, then tuck back in.</summary>
    void Bud(double secs = 3.5)
    {
        if (!_settings.TuckPills) return;
        _budUntil = DateTime.UtcNow.AddSeconds(secs);
        After((int)(secs * 1000) + 60, RefreshIslands);
    }

    void PointerNear(bool near)
    {
        if (near == _pointerNear) return;
        _pointerNear = near;
        // reaching for the notch: out at once; moving away: they wait a moment before tucking in
        if (!near && _settings.TuckPills) { _budUntil = DateTime.UtcNow.AddSeconds(0.8); After(900, RefreshIslands); }
        RefreshIslands();
    }

    void RefreshIslands()
    {
        UpdateSecond();
        UpdatePrivacyBubble();
    }

    bool TuckMode => _settings.TuckPills && !_hidden && _mode is Mode.Idle or Mode.Slim or Mode.Media or Mode.Activity;

    void TuckLeft(bool on) => TuckLight(ref _tuckL, -1, on && TuckMode, _secondColor);

    void TuckRight(bool on) => TuckLight(ref _tuckR, 1, on && TuckMode,
        _privacy.CamInUse ? ((SolidColorBrush)Green).Color : ((SolidColorBrush)Orange).Color);

    void TuckLight(ref Border? light, int side, bool on, Color c)
    {
        if (_pillColor is { } mine) c = mine;
        else if (_skinOn) c = _skinMap(c);
        if (light == null)
        {
            if (!on) return;
            light = new Border
            {
                Width = 14, Height = 2.4, CornerRadius = new CornerRadius(_cyber ? 0 : 1.2), IsHitTestVisible = false, Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed, RenderTransform = new TranslateTransform(side * 36, 0),
                Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 7, Opacity = 0.9 },
            };
            Stage.Children.Add(light);
        }
        var b = light;
        b.Margin = new Thickness(0, 0, 0, _mode == Mode.Slim ? 1.5 : 2.5);
        if (on)
        {
            if (b.Background is not SolidColorBrush { } now || now.Color != c)
            {
                b.Background = new SolidColorBrush(c);
                ((DropShadowEffect)b.Effect).Color = c;
            }
            if (b.Visibility == Visibility.Visible && b.Tag is true) return;
            b.Tag = true;
            b.Visibility = Visibility.Visible;
            // swallowed: a flash as it lands in the notch, then a slow breath
            var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520))));
            flash.Completed += (_, _) =>
            {
                if (b.Tag is not true) return;
                if (Eco) { b.BeginAnimation(OpacityProperty, null); b.Opacity = 0.85; return; }
                var breathe = new DoubleAnimation(0.85, 0.4, TimeSpan.FromSeconds(1.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                Timeline.SetDesiredFrameRate(breathe, 20);
                b.BeginAnimation(OpacityProperty, breathe);
            };
            b.BeginAnimation(OpacityProperty, flash);
            var grow = new DoubleAnimation(4, 14, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
            b.BeginAnimation(WidthProperty, grow);
            return;
        }
        if (b.Tag is not true) return;
        b.Tag = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) => { if (b.Tag is not true) b.Visibility = Visibility.Collapsed; };
        b.BeginAnimation(OpacityProperty, fade);
    }
}
