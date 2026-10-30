using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Small motion touches: the open notch's contents rise in one after another instead of all at once,
/// album covers flip to the next song, and the switcher's chips pop in.
/// </summary>
public partial class NotchWindow
{
    /// <summary>As the notch opens, each row (and each toggle, left to right) rises into place a beat after the last.</summary>
    void PlayEntrance()
    {
        if (Eco || PagesStrip.Children.Count <= _page) return;
        if (PagesStrip.Children[_page] is not Panel page || page.Children.OfType<Panel>().FirstOrDefault() is not Panel content) return;

        var items = new List<FrameworkElement>();
        foreach (var c in content.Children.OfType<FrameworkElement>())
        {
            if (c.Visibility != Visibility.Visible || c.Opacity < 0.5 || !c.IsHitTestVisible) continue;
            if (c is UniformGrid tiles) items.AddRange(tiles.Children.OfType<FrameworkElement>().Where(t => t.Visibility == Visibility.Visible));
            else items.Add(c);
        }

        for (int i = 0; i < items.Count && i < 16; i++)
            Rise(items[i], 60 + i * 32);
    }

    static void Rise(FrameworkElement el, int delayMs)
    {
        // land exactly where it was: take over the current (maybe animated) values as the element's own first
        double opacity = el.Opacity;
        if (opacity < 0.99) return; // fading in or out for its own reason (the device list, a card flip): leave it alone
        el.BeginAnimation(OpacityProperty, null);
        el.Opacity = 1;
        var delay = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs));
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, delay));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + 260)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        el.BeginAnimation(OpacityProperty, fade);

        // ride on the element's own translate (the Home cards have one), or give it one; never fight another transform
        TranslateTransform? tt = el.RenderTransform as TranslateTransform;
        if (tt == null && (el.RenderTransform == null || el.RenderTransform.Value.IsIdentity && el.RenderTransform is MatrixTransform))
            el.RenderTransform = tt = new TranslateTransform();
        if (tt == null || tt.IsFrozen) return;
        tt.BeginAnimation(TranslateTransform.YProperty, null);
        tt.Y = 0;
        var rise = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(12, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(12, delay));
        rise.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + 560)), new SpringEase { Damping = 0.72 }));
        tt.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    readonly Dictionary<Border, ImageSource?> _artShown = new();

    /// <summary>A new cover turns over like a card: the old one folds away edge-on and the new one swings in.</summary>
    void FlipArt(Border box, ImageSource? art, Action apply)
    {
        bool had = _artShown.TryGetValue(box, out var shown);
        _artShown[box] = art;
        if (!had || ReferenceEquals(shown, art) || Eco || !box.IsVisible || shown == null || art == null) { apply(); return; }

        if (box.RenderTransform is not ScaleTransform st || st.IsFrozen)
        {
            box.RenderTransformOrigin = new Point(0.5, 0.5);
            box.RenderTransform = st = new ScaleTransform();
        }
        // one self-contained turn that always ends at full size (it never holds a value, so it can't get stuck folded)
        var turn = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        turn.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        turn.KeyFrames.Add(new EasingDoubleKeyFrame(0.02, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        turn.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(580)), new SpringEase { Damping = 0.62 }));
        st.ScaleX = st.ScaleY = 1;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, turn);
        // swap the picture while it's edge-on, unless a newer song already took over
        After(120, () => { if (ReferenceEquals(_artShown.GetValueOrDefault(box), art)) apply(); });
        // a touch of depth: it dips slightly while edge-on
        var dip = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        dip.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        dip.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new SpringEase { Damping = 0.7 }));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, dip);
    }

    /// <summary>The switcher's chips pop in one after another when it first appears.</summary>
    void PopChipsIn()
    {
        int i = 0;
        foreach (var chip in LiveChips.Children.OfType<FrameworkElement>())
        {
            if (chip.RenderTransform is not ScaleTransform st || st.IsFrozen) continue;
            int delay = 40 + i++ * 45;
            var pop = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            pop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.4, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            pop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + 480)), new SpringEase { Damping = 0.55 }));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }
}
