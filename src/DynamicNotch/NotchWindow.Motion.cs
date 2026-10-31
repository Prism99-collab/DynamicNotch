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
    enum Motion { Auto, None, Pulse, DoublePulse, Shake, Tremble }

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
        if (kind.StartsWith("app-alert:") || kind == "download-failed") return Motion.Tremble;
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
                case Motion.Tremble: Tremble(); break;
            }
        });
    }

    /// <summary>
    /// A tiny, quick shiver: "that didn't work" (wrong password dialog, failed download, a toggle that
    /// wouldn't switch). Much smaller than <see cref="Shake"/>, which is kept for real problems.
    /// </summary>
    void Tremble(Color? tint = null)
    {
        double[] xs = { -3.5, 3.2, -2.4, 1.6, -0.8, 0 };
        PlayShellX(xs, 46, 0);
        FlashRim(tint);
    }

    /// <summary>A little bump in one direction, like hitting a wall: volume / brightness already at 100 % or 0 %.</summary>
    void Bump(int direction)
    {
        double d = Math.Sign(direction);
        PlayShellX(new[] { 3.5 * d, -1.2 * d, 0.4 * d, 0 }, 60, 0);
    }

    void PlayShellX(double[] xs, int stepMs, int delayMs)
    {
        _shellShake ??= new TranslateTransform();
        if (Shell.RenderTransform != _shellShake) Shell.RenderTransform = _shellShake;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(stepMs * xs.Length), BeginTime = TimeSpan.FromMilliseconds(delayMs) };
        for (int i = 0; i < xs.Length; i++)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(stepMs * (i + 1))),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
        _shellShake.BeginAnimation(TranslateTransform.XProperty, a);
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
        IslandsBounce();
    }

    /// <summary>The rim lights up for a moment with the pop-up's colour, then settles back.</summary>
    void FlashRim(Color? color = null)
    {
        var c = color ?? _osdAccent ?? Colors.White;
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

    // ───────────────────────────── alive: lean, shine, aura ─────────────────────────────

    /// <summary>
    /// The notch leans a few pixels toward the cursor while you touch the top edge or hover a clickable
    /// alert, like something that noticed you. It settles back to the centre otherwise.
    /// </summary>
    void UpdateLean(Interop.Native.POINT p, bool engaged)
    {
        double target = 0;
        if (engaged && !_hidden && _mode != Mode.Expanded)
        {
            double cx = _winLeft + _winW / 2.0;
            target = Math.Clamp((p.X - cx) / _scale * 0.06, -7, 7);
        }
        if (Math.Abs(target - _lean.Target) < 0.25) return;
        _lean.Target = target;
        StartAnimating();
    }

    /// <summary>Good news gets a soft band of light sweeping across the notch once, in its colour.</summary>
    void PlayShine(Color c)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0.2), EndPoint = new Point(1, 0.8) };
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0x3C, c.R, c.G, c.B), 0.42));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0.5));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0x3C, c.R, c.G, c.B), 0.58));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        g.Freeze();
        Shine.Background = g;
        double across = Math.Max(Pill.ActualWidth, _osdSize.Width) + 20;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        ShineShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-120, across, TimeSpan.FromMilliseconds(820)) { EasingFunction = ease });
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(820) };
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(820))));
        Shine.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Things worth a little celebration: done, connected, charging, full.</summary>
    static bool IsGoodNews(string kind, Brush? glyph, (double Level, bool Charging)? battery) =>
        kind is "download" or "timer" or "full-charge" or "screenshot" or "shelf" or "call"
        || (kind == "power" && battery is { Charging: true })
        || (kind is "bt" or "wifi" or "usb" or "display" && glyph == Blue);

    Color? _auraFor;

    /// <summary>An alert with a colour casts a faint glow of it just below the notch; it fades with the alert.</summary>
    void UpdateAura(Color? c)
    {
        if (_cyber && c is { } cc) c = CyMap(cc);
        _auraLive = false;
        // (not with a finger: the notch takes taps then, and the glow would catch the ones just below it)
        if (c == null && !_hidden && !Eco && !_touchMode && _mode is Mode.Media or Mode.Activity)
        {
            c = LiveAuraColor();
            _auraLive = c != null;
        }
        if (c is { } k && _mode is Mode.Osd or Mode.Drop or Mode.Media or Mode.Activity && !_hidden)
        {
            var size = _mode switch { Mode.Drop => DropSize, Mode.Osd => _osdSize, Mode.Media => MediaSize, _ => ActivitySize };
            Aura.Width = size.Width + (_auraLive ? 70 : 90);
            Aura.Height = size.Height + (_auraLive ? 30 : 44);
            if (k != _auraFor)
            {
                var g = new RadialGradientBrush
                {
                    Center = new Point(0.5, 0), GradientOrigin = new Point(0.5, 0), RadiusX = 0.5, RadiusY = 1,
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x46, k.R, k.G, k.B), 0),
                        new GradientStop(Color.FromArgb(0x18, k.R, k.G, k.B), 0.55),
                        new GradientStop(Color.FromArgb(0, k.R, k.G, k.B), 1),
                    },
                };
                g.Freeze();
                Aura.Background = g;
                _auraFor = k;
            }
            if (_auraLive && AuraBeats && _auraDriven) return; // the sound drives it already
            _auraDriven = false;
            Fade(Aura, _auraLive ? 0.45 : 1, 260);
        }
        else { _auraDriven = false; Fade(Aura, 0, 220); }
    }

    bool _auraLive, _auraDriven;
    double _auraLevel;

    /// <summary>Music with a cover glows in its colour, a call in green (red muted), Focus in purple.</summary>
    Color? LiveAuraColor() => _mode switch
    {
        Mode.Media when _media.Art != null && _media.Accent != Colors.White => _media.Accent,
        Mode.Activity when CurrentActivity == LiveActivity.Call => ((SolidColorBrush)CallBrush).Color,
        Mode.Activity when CurrentActivity == LiveActivity.Focus => ((SolidColorBrush)FocusPurple).Color,
        _ => null,
    };

    bool AuraBeats => _mode == Mode.Media ? _eqRunning && !_eqFallback : CurrentActivity == LiveActivity.Call && _callBarsOn;

    /// <summary>From the ~30 Hz meter: the glow under the notch swells with the beat or your voice.</summary>
    void BeatAura(double level)
    {
        if (!_auraLive || !AuraBeats) return;
        _auraLevel += (level - _auraLevel) * (level > _auraLevel ? 0.45 : 0.12);
        if (!_auraDriven)
        {
            // take over from the fade-in without a jump
            double cur = Aura.Opacity;
            Aura.BeginAnimation(OpacityProperty, null);
            Aura.Opacity = cur;
            _auraDriven = true;
        }
        double o = 0.28 + 0.62 * Math.Clamp(_auraLevel, 0, 1);
        o = Aura.Opacity + (o - Aura.Opacity) * 0.35;
        if (Math.Abs(Aura.Opacity - o) > 0.004) Aura.Opacity = o;
    }

    // ───────────────────────────── stepping aside ─────────────────────────────

    Color? _slimGlowFor;
    bool _slimGlowOn, _dodged;

    /// <summary>
    /// The slim bar over a window still says something's live: a thin line in the song's colour
    /// (or the timer / download / focus colour) that pulses with the music while it plays.
    /// </summary>
    void UpdateSlimGlow()
    {
        Color? c = _mode != Mode.Slim || _hidden ? null : CurrentActivity switch
        {
            LiveActivity.Media => _media.Accent,
            LiveActivity.Timer => ((SolidColorBrush)TimerBrush).Color,
            LiveActivity.Download => ((SolidColorBrush)Blue).Color,
            LiveActivity.Focus => ((SolidColorBrush)FocusPurple).Color,
            LiveActivity.Call => ((SolidColorBrush)CallBrush).Color,
            _ => null,
        };
        if (c is not { } k)
        {
            if (!_slimGlowOn) return;
            _slimGlowOn = false;
            if (_slimBeat) { _slimBeat = false; UpdateMeter(); }
            SlimGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
            return;
        }
        if (k != _slimGlowFor)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, k.R, k.G, k.B), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, k.R, k.G, k.B), 0.5));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, k.R, k.G, k.B), 1));
            g.Freeze();
            SlimGlow.Background = g;
            _slimGlowFor = k;
        }
        // music pulses the glow to the song, a call to your voice
        bool breathe = (CurrentActivity == LiveActivity.Media && _media.IsPlaying || CurrentActivity == LiveActivity.Call) && !Eco;
        if (_slimGlowOn && breathe == _slimGlowBreathing) return;
        _slimGlowOn = true;
        _slimGlowBreathing = breathe;
        // fade in; with music playing, the glow then pulses to the song's real loudness (the visualizer meter)
        var a = new DoubleAnimation(breathe ? 0.6 : 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (breathe) a.Completed += (_, _) =>
        {
            if (!_slimGlowBreathing || !_slimGlowOn) return;
            SlimGlow.BeginAnimation(OpacityProperty, null); // hand the opacity to the meter
            SlimGlow.Opacity = 0.6;
            _slimBeat = true;
            UpdateMeter();
        };
        else if (_slimBeat) { _slimBeat = false; UpdateMeter(); }
        SlimGlow.BeginAnimation(OpacityProperty, a);
    }

    bool _slimGlowBreathing;

    /// <summary>
    /// The cursor heading for a tab or button under the notch: fade almost away so you can see and click
    /// what's underneath (the notch is click-through there anyway). Pushing against the very top edge still
    /// brings it back and opens it.
    /// </summary>
    void UpdateDodge(Interop.Native.POINT p, bool atEdge)
    {
        bool want = false;
        if (IsBase(_mode) && !_hidden && !atEdge && !_peek && _settings.SlimOverWindows && !_touchMode) // a finger has no hover to step aside from
        {
            double margin = (_dodged ? 40 : 26) * _scale; // a little hysteresis so it doesn't flicker at the border
            double half = Math.Max(_sw.Value / 2 + 10, 64) * _scale + margin;
            if (_secondShown != LiveActivity.None) half += (SecondBubble.ActualWidth + 22) * _scale;
            if (_privacyShown) half += (PrivacyBubble.ActualWidth + 20) * _scale;
            double cx = _winLeft + _winW / 2.0;
            double edge = _mon.Top + Math.Max(1, (int)Math.Round(_scale));
            want = p.Y > edge && p.Y <= _mon.Top + _sh.Value * _scale + margin && Math.Abs(p.X - cx) <= half;
        }
        SetDodged(want);
    }

    void SetDodged(bool on)
    {
        if (on == _dodged) return;
        _dodged = on;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0.06 : 1, TimeSpan.FromMilliseconds(on ? 140 : 240)) { EasingFunction = ease });
    }

    // ───────────────────────────── split island ─────────────────────────────

    /// <summary>Everything live right now, most important first.</summary>
    List<LiveActivity> Activities()
    {
        var list = new List<LiveActivity>(6);
        if (InGame) { list.Add(LiveActivity.Game); return list; } // you're playing: the frame rate alone, nothing to distract
        if (OnCall) list.Add(LiveActivity.Call); // a call beats everything
        if (_timerRunning) list.Add(LiveActivity.Timer);
        if (_focus.IsOn) list.Add(LiveActivity.Focus);
        if (_files.ActiveDownloads > 0) list.Add(LiveActivity.Download);
        if (_settings.ShowMediaActivity && _media.IsPlaying) list.Add(LiveActivity.Media);
        ApplyPin(list); // what you picked on Home goes first
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
        int more = second != LiveActivity.None ? Activities().Count - 2 : 0;
        SecondMore.Text = more > 0 ? $"+{more}" : "";
        SecondMore.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (second == _secondShown) return;
        bool wasShown = _secondShown != LiveActivity.None;
        _secondShown = second;

        if (second != LiveActivity.None && wasShown)
        {
            // the two islands traded places: a quick squeeze-and-pop says so
            var swap = new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(480)) { EasingFunction = new SpringEase { Damping = 0.55 } };
            SecondScale.BeginAnimation(ScaleTransform.ScaleXProperty, swap);
            SecondScale.BeginAnimation(ScaleTransform.ScaleYProperty, swap);
            SecondBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(260)));
        }
        else if (second != LiveActivity.None && !wasShown)
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
            IslandNeck(SecondNeck, 30, splitting: true);
            var pop = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new SpringEase { Damping = 0.6 } };
            SecondScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            SecondScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            Fade(SecondBubble, 1, 200);
        }
        else if (second == LiveActivity.None && wasShown && !(_mode is Mode.Media or Mode.Activity) || _hidden && wasShown && second == LiveActivity.None)
        {
            // the notch is opening, showing an alert or stepping aside: the island is simply absorbed, quickly,
            // instead of a slow merge playing against the notch's own morph
            _secondSliding = false;
            SecondNeck.BeginAnimation(HeightProperty, null);
            SecondNeck.Height = 0;
            var quick = new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(120));
            SecondScale.BeginAnimation(ScaleTransform.ScaleXProperty, quick);
            SecondScale.BeginAnimation(ScaleTransform.ScaleYProperty, quick);
            var gone = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
            gone.Completed += (_, _) => { if (_secondShown == LiveActivity.None) SecondBubble.Visibility = Visibility.Collapsed; };
            SecondBubble.BeginAnimation(OpacityProperty, gone);
        }
        else if (second == LiveActivity.None && wasShown)
        {
            // the activity ended: it merges back into the pill, the neck reaching out first
            IslandNeck(SecondNeck, 30, splitting: false);
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
        IslandFollow(SecondShift, SecondTargetX());
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
                SecondGlyph.Foreground = SecondText.Foreground = TimerBrush;
                SecondText.Text = FormatTimer(left);
                rim = ((SolidColorBrush)TimerBrush).Color;
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
                var share = _files.Items.FirstOrDefault()?.Progress;
                SecondProgress(share);
                SecondText.Text = share is { } sh ? $"{sh * 100:0}%"
                    : _files.BytesPerSecond > 1024 ? $"{Services.SystemMonitor.Bytes(_files.BytesPerSecond)}/s" : Services.SystemMonitor.Bytes(_files.ActiveBytes);
                rim = ((SolidColorBrush)Blue).Color;
                break;
            case LiveActivity.Call when _call is { } call:
                // the calling app's icon and the call's running time, green (red when muted)
                SecondArt.Background = call.Icon != null ? IconBrush(call.Icon) : null;
                SecondArt.CornerRadius = new CornerRadius(5);
                SecondArt.Visibility = call.Icon != null ? Visibility.Visible : Visibility.Collapsed;
                SecondGlyph.Visibility = call.Icon != null ? Visibility.Collapsed : Visibility.Visible;
                SecondGlyph.Text = "\uE717";
                SecondGlyph.Foreground = SecondText.Foreground = CallBrush;
                SecondText.Text = FormatTimer(DateTime.UtcNow - call.Started);
                rim = ((SolidColorBrush)CallBrush).Color;
                break;
            default: // music: the album cover as a tiny round disc
                SecondArt.CornerRadius = new CornerRadius(9);
                SecondArt.Background = ArtBrush(_media.Art) ?? (Brush)new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E));
                SecondArt.Visibility = Visibility.Visible;
                SecondGlyph.Visibility = Visibility.Collapsed;
                SecondText.Text = "";
                SecondText.Margin = new Thickness(0);
                rim = _media.Accent;
                IslandRim(SecondRim, rim);
                IslandAura(SecondAura, rim);
                SecondProgress(null);
                return;
        }
        SecondText.Margin = new Thickness(6, 0, 0, 0);
        IslandRim(SecondRim, rim);
        IslandAura(SecondAura, rim);
        if (a != LiveActivity.Download) SecondProgress(null);
    }
}
