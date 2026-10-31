using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// The little islands beside the notch (a second live activity on the left, the microphone / camera on the right)
/// are one family with it: they hang from the top edge with the same ears and the same rim light fading up the
/// sides, cast a soft glow of their colour, and move like liquid: splitting off, a neck of black stretches and
/// pinches between them; merging back, it reaches out again. They follow the notch as it morphs with a slight lag,
/// swell with it when the mouse reaches the top edge, and bounce along when it pulses.
/// </summary>
public partial class NotchWindow
{
    /// <summary>The island's rim: bright along its bottom, fading up the sides, like the notch's.</summary>
    static void IslandRim(Border rim, Color c, double strength = 0.75)
    {
        if (_pillColor is { } mine) c = mine; // your pills colour (Colors page)
        else if (_skinOn) c = CyMap(c);
        PlateColor(rim, c); // the cyber plate's frame wears it too
        var g = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * strength * 0.45), c.R, c.G, c.B), 0.45));
        g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * strength), c.R, c.G, c.B), 1));
        g.Freeze();
        rim.BorderBrush = g;
    }

    /// <summary>A soft glow of the island's colour just below it.</summary>
    static void IslandAura(Border aura, Color c)
    {
        if (_pillColor is { } mine) c = mine;
        else if (_skinOn) c = CyMap(c);
        var g = new RadialGradientBrush
        {
            Center = new Point(0.5, 0), GradientOrigin = new Point(0.5, 0), RadiusX = 0.5, RadiusY = 1,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x50, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(0x16, c.R, c.G, c.B), 0.6),
                new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
            },
        };
        g.Freeze();
        aura.Background = g;
    }

    /// <summary>
    /// The liquid moment between notch and island. Splitting: a neck of black joins them and pinches thin as the
    /// island drifts away. Merging: it reaches out from the island and fills in as they meet.
    /// </summary>
    void IslandNeck(Border neck, double height, bool splitting)
    {
        if (Eco) { neck.BeginAnimation(HeightProperty, null); neck.Height = 0; return; }
        var a = splitting
            ? new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(height * 0.9, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(height * 0.55, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)), new SineEase { EasingMode = EasingMode.EaseOut }),
                    new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new CubicEase { EasingMode = EasingMode.EaseIn }),
                },
            }
            : new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(height * 0.8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)), new CubicEase { EasingMode = EasingMode.EaseIn }),
                },
            };
        neck.BeginAnimation(HeightProperty, a);
    }

    /// <summary>An island stays right beside the notch as it changes size, frame for frame (it's moved by the same springs).</summary>
    static void IslandFollow(TranslateTransform shift, double target, bool instant = false)
    {
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = target;
    }

    /// <summary>The mouse reached the top edge: the islands swell a touch with the notch.</summary>
    void IslandsPeek(bool on)
    {
        var a = new DoubleAnimation(on ? 1.07 : 1, TimeSpan.FromMilliseconds(on ? 320 : 260)) { EasingFunction = new SpringEase { Damping = 0.62 } };
        foreach (var s in new[] { SecondScale, PrivacyScale })
        {
            s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    /// <summary>The notch pulsed: its islands bounce along, a little after it.</summary>
    void IslandsBounce()
    {
        if (Eco) return;
        var a = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(60) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new SpringEase { Damping = 0.5 }));
        foreach (var s in new[] { SecondScale, PrivacyScale })
        {
            s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    bool _privacyBreathing;

    /// <summary>The mic / camera island's colour (orange for the microphone, green for the camera alone), breathing softly.</summary>
    void StylePrivacyIsland()
    {
        var c = _privacy.MicInUse ? Color.FromRgb(0xFF, 0x9F, 0x0A) : Color.FromRgb(0x30, 0xD1, 0x58);
        IslandRim(PrivacyRim, c, 0.7);
        IslandAura(PrivacyAura, c);
        bool breathe = _privacyShown && !Eco;
        if (breathe == _privacyBreathing) return;
        _privacyBreathing = breathe;
        if (breathe)
            PrivacyAura.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 0.7, TimeSpan.FromMilliseconds(1300))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        else PrivacyAura.BeginAnimation(OpacityProperty, null);
    }

    /// <summary>A ring arc around the side island's glyph showing a share (a download's progress), or nothing.</summary>
    void SecondProgress(double? share)
    {
        if (share is not { } p || p <= 0)
        {
            SecondRing.Visibility = Visibility.Collapsed;
            return;
        }
        SecondRing.Data = Arc(19, 1.8, Math.Clamp(p, 0, 1));
        SecondRing.Visibility = Visibility.Visible;
    }
}
