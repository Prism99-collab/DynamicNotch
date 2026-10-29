using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Expressive motion (pulse for "something new", shake for "something's wrong") and the split island
/// that shows a second live activity next to the main one.
/// </summary>
public partial class NotchWindow
{
    enum Motion { Auto, None, Pulse, DoublePulse, Shake }

    TranslateTransform? _shellShake;
    LiveActivity _secondShown = LiveActivity.None;

    // ───────────────────────────── pulse & shake ─────────────────────────────

    /// <summary>Picks the motion that fits a pop-up when the caller didn't choose one.</summary>
    static Motion ResolveMotion(string kind, Motion m)
    {
        if (m != Motion.Auto) return m;
        if (kind.StartsWith("toast:") || kind.StartsWith("flash:") || kind is "download" or "smart-break") return Motion.Pulse;
        if (kind == "timer") return Motion.DoublePulse;
        if (kind is "battery" or "smart-net" or "smart-drain") return Motion.Shake;
        return Motion.None;
    }

    /// <summary>Plays the motion once the pop-up has finished growing, so the two don't fight.</summary>
    void PlayMotion(Motion m)
    {
        if (m is Motion.None or Motion.Auto) return;
        After(380, () =>
        {
            if (_mode != Mode.Osd) return;
            switch (m)
            {
                case Motion.Pulse: Kick(); break;
                case Motion.DoublePulse: Kick(); After(340, Kick); break;
                case Motion.Shake: Shake(); break;
            }
        });
    }

    /// <summary>
    /// A physical "breath": push the shape springs outward and let them settle back by themselves.
    /// Because it's the same spring that drives every shape change, it blends with anything else in motion.
    /// </summary>
    void Kick()
    {
        _sw.Configure(SpringStyle.Pop);
        _sh.Configure(SpringStyle.Pop);
        _sw.Velocity += 340;
        _sh.Velocity += 90;
        StartAnimating();
        FlashRim();
    }

    /// <summary>The rim lights up for a moment with the pop-up's colour, then settles back.</summary>
    void FlashRim()
    {
        var c = _osdAccent ?? Colors.White;
        if (Rim.BorderBrush is not SolidColorBrush rb || rb.IsFrozen) Rim.BorderBrush = rb = new SolidColorBrush(Colors.Transparent);
        var settle = rb.Color;
        var a = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
        a.KeyFrames.Add(new EasingColorKeyFrame(Color.FromArgb(0xE0, c.R, c.G, c.B), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        a.KeyFrames.Add(new EasingColorKeyFrame(settle.A == 0 ? Color.FromArgb(0x80, c.R, c.G, c.B) : settle, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        rb.BeginAnimation(SolidColorBrush.ColorProperty, a);
    }

    /// <summary>A short, decaying "no" shake.</summary>
    void Shake()
    {
        _shellShake ??= new TranslateTransform();
        if (Shell.RenderTransform != _shellShake) Shell.RenderTransform = _shellShake;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        double[] xs = { -9, 8, -6.5, 5, -3.5, 2, -1, 0 };
        for (int i = 0; i < xs.Length; i++)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(65 * (i + 1))),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
        _shellShake.BeginAnimation(TranslateTransform.XProperty, a);
    }

    void After(int ms, Action action)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); action(); };
        t.Start();
    }

    // ───────────────────────────── split island ─────────────────────────────

    /// <summary>Everything live right now, most important first.</summary>
    List<LiveActivity> Activities()
    {
        var list = new List<LiveActivity>(4);
        if (_timerRunning) list.Add(LiveActivity.Timer);
        if (_focus.IsOn) list.Add(LiveActivity.Focus);
        if (_files.ActiveDownloads > 0) list.Add(LiveActivity.Download);
        if (_settings.ShowMediaActivity && _media.IsPlaying) list.Add(LiveActivity.Media);
        return list;
    }

    LiveActivity SecondActivity
    {
        get
        {
            var list = Activities();
            return list.Count > 1 ? list[1] : LiveActivity.None;
        }
    }

    /// <summary>Shows, updates or hides the little second island next to the main pill.</summary>
    void UpdateSecond()
    {
        var second = _mode is Mode.Media or Mode.Activity && !_hidden ? SecondActivity : LiveActivity.None;
        if (second != LiveActivity.None) FillSecond(second);

        if (second == _secondShown) return;
        bool wasShown = _secondShown != LiveActivity.None;
        _secondShown = second;

        if (second != LiveActivity.None && !wasShown)
        {
            // split off: start tucked against the pill's edge, then slide out with a springy pop
            SecondBubble.Visibility = Visibility.Visible;
            SecondBubble.UpdateLayout();
            double target = SecondTargetX();
            double from = -(_sw.Target / 2 - SecondBubble.ActualWidth / 2);
            var slide = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(620)) { EasingFunction = new SpringEase { Damping = 0.66 } };
            _secondSliding = true;
            slide.Completed += (_, _) =>
            {
                // hand control back from the animation so the island can keep following the pill
                _secondSliding = false;
                SecondShift.BeginAnimation(TranslateTransform.XProperty, null);
                SecondShift.X = SecondTargetX();
            };
            SecondShift.BeginAnimation(TranslateTransform.XProperty, slide);
            var pop = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new SpringEase { Damping = 0.6 } };
            SecondScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            SecondScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            Fade(SecondBubble, 1, 200);
        }
        else if (second == LiveActivity.None && wasShown)
        {
            // merge back into the pill
            double into = -(_sw.Target / 2 - SecondBubble.ActualWidth / 2);
            SecondShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(into, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            var shrink = new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(300));
            SecondScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            SecondScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (_, _) => { if (_secondShown == LiveActivity.None) SecondBubble.Visibility = Visibility.Collapsed; };
            SecondBubble.BeginAnimation(OpacityProperty, fade);
        }
    }

    bool _secondSliding;

    // half the pill + its ear + a clear gap so it reads as two islands, not one shape
    double SecondTargetX() => -(Pill.ActualWidth / 2 + 10 + 9 + SecondBubble.ActualWidth / 2);

    /// <summary>Keeps the second island beside the pill while the pill breathes, resizes, or the island's text changes.</summary>
    void PlaceSecond()
    {
        if (_secondShown == LiveActivity.None || _secondSliding) return;
        SecondShift.X = SecondTargetX();
    }

    void FillSecond(LiveActivity a)
    {
        SecondArt.Visibility = Visibility.Collapsed;
        SecondGlyph.Visibility = Visibility.Visible;
        Color rim;
        switch (a)
        {
            case LiveActivity.Timer:
                var left = _timerEnd - DateTime.UtcNow;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                SecondGlyph.Text = "";
                SecondGlyph.Foreground = SecondText.Foreground = Orange;
                SecondText.Text = FormatTimer(left);
                rim = ((SolidColorBrush)Orange).Color;
                break;
            case LiveActivity.Focus:
                SecondGlyph.Text = "";
                SecondGlyph.Foreground = SecondText.Foreground = FocusPurple;
                int mins = (int)_focus.Elapsed.TotalMinutes;
                SecondText.Text = mins < 1 ? "Focus" : $"{mins}m";
                rim = ((SolidColorBrush)FocusPurple).Color;
                break;
            case LiveActivity.Download:
                SecondGlyph.Text = "";
                SecondGlyph.Foreground = SecondText.Foreground = Blue;
                SecondText.Text = _files.BytesPerSecond > 1024 ? $"{Services.SystemMonitor.Bytes(_files.BytesPerSecond)}/s" : Services.SystemMonitor.Bytes(_files.ActiveBytes);
                rim = ((SolidColorBrush)Blue).Color;
                break;
            default: // music: the album cover as a tiny round disc
                SecondArt.Background = ArtBrush(_media.Art) ?? (Brush)new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E));
                SecondArt.Visibility = Visibility.Visible;
                SecondGlyph.Visibility = Visibility.Collapsed;
                SecondText.Text = "";
                SecondText.Margin = new Thickness(0);
                rim = _media.Accent;
                SecondRim.BorderBrush = Frozen(Color.FromArgb(0x80, rim.R, rim.G, rim.B));
                return;
        }
        SecondText.Margin = new Thickness(6, 0, 0, 0);
        SecondRim.BorderBrush = Frozen(Color.FromArgb(0x70, rim.R, rim.G, rim.B));
    }
}
