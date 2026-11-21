using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// The hello when Pixo starts (the PC started, or Pixo did): short and quiet.
///  • the notch gathers into a small point of light at the top edge, then breathes open into a slim pill;
///  • inside, the face wakes (eyes closed, then open with a little hop) and a greeting for the time of day fades in
///    beside it ("Good morning"); a single line of light runs along the bottom edge;
///  • about two seconds later the words go, the face slides back to the middle and the notch rests.
/// Never over a full-screen app, a game, a meeting, Focus or a hidden notch (then the face just appears); light motion
/// keeps it, without the light and the slides. Anything that needs the notch meanwhile ends it at once.
/// </summary>
public partial class NotchWindow
{
    int _welcomePhase; // 0 none, 1 a point, 2 open
    TextBlock? _welcomeText;
    Rectangle? _welcomeLine;
    double _welcomeW;

    bool _welcomeDev; // DYNAMICNOTCH_OPEN=welcome: shown whatever is going on (for a look)

    bool WelcomeAllowed() => _welcomeDev && _face != null || _face != null && _faceShown && _mode == Mode.Idle && !_fgFullscreen && !InGame && !OnCall && !_inMeeting && !_presenter && !_focus.IsOn && !_hidden && !_settings.Locked;

    /// <summary>The start's greeting (instead of the big wave). False: not now (the caller greets the old way, or not).</summary>
    bool StartWelcome()
    {
        if (!WelcomeAllowed()) return false;
        var face = _face!;
        int hour = DateTime.Now.Hour;
        string words = hour < 5 ? "Hello, night owl" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        if (_cyber) words = hour < 5 ? "NIGHT SHIFT // ONLINE" : "ONLINE // " + (hour < 12 ? "MORNING" : hour < 18 ? "AFTERNOON" : "EVENING");

        if (_welcomeText == null)
        {
            _welcomeText = new TextBlock
            {
                FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, IsHitTestVisible = false, Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, RenderTransform = new TranslateTransform(),
            };
            Stage.Children.Add(_welcomeText);
            _welcomeLine = new Rectangle { Height = 1.6, IsHitTestVisible = false, Opacity = 0, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = new TranslateTransform() };
            Stage.Children.Add(_welcomeLine);
        }
        _welcomeText.Text = words;
        _welcomeText.FontFamily = _cyber ? CyFont : new FontFamily("Segoe UI Variable Display, Segoe UI");
        _welcomeText.FontSize = _cyber ? 11 : 12.5;
        _welcomeText.Foreground = _cyber ? CyCyanBrush : Brushes.White;
        _welcomeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = Math.Ceiling(_welcomeText.DesiredSize.Width);

        // the group: face (24) + a gap + the words, centred; the pill hugs it
        const double faceW = 24, gap = 12, h = 34;
        double group = faceW + gap + tw;
        _welcomeW = Math.Max(160, group + 44);
        double faceX = -group / 2 + faceW / 2, textX = -group / 2 + faceW + gap + tw / 2;
        _welcomeText.Margin = new Thickness(0, (h - _welcomeText.DesiredSize.Height) / 2, 0, 0);

        Services.AppLog.Write("welcome");
        // 1 · a point of light
        _welcomePhase = 1;
        face.SetMood(NotchFace.Mood.Sleep);
        face.Opacity = 0;
        ApplyShape();

        // 2 · it breathes open; the face is there, asleep, and wakes
        After(Eco ? 60 : 230, () =>
        {
            if (_welcomePhase != 1) return;
            _welcomePhase = 2;
            ApplyShape();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var slide = TimeSpan.FromMilliseconds(Eco ? 1 : 420);
            _faceShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(faceX, slide) { EasingFunction = ease });
            _faceShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, slide) { EasingFunction = ease });
            // its own size, whatever it was before (a call's pill shows it bigger)
            _faceScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, slide) { EasingFunction = ease });
            _faceScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, slide) { EasingFunction = ease });
            face.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Eco ? 1 : 260)) { BeginTime = TimeSpan.FromMilliseconds(Eco ? 0 : 120) });
            After(Eco ? 200 : 620, () =>
            {
                if (_welcomePhase != 2) return;
                face.SetMood(NotchFace.Mood.Idle);
                face.Play(NotchFace.Emote.Happy, 1.4); // a smile and a little hop (no sparks drifting into the words)
                Services.FaceSound.Say("hello", _cyber);
            });

            // the words arrive from just beside the face
            var tt = (TranslateTransform)_welcomeText.RenderTransform;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(textX - 10, textX, TimeSpan.FromMilliseconds(Eco ? 1 : 520)) { BeginTime = TimeSpan.FromMilliseconds(Eco ? 0 : 520), EasingFunction = new SpringEase { Damping = 0.8 } });
            _welcomeText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Eco ? 1 : 360)) { BeginTime = TimeSpan.FromMilliseconds(Eco ? 0 : 520), EasingFunction = ease });

            // one line of light along the bottom edge
            if (!Eco && _welcomeLine != null)
            {
                var acc = _cyber ? CyCyan : PalAccent();
                _welcomeLine.Width = 90;
                _welcomeLine.Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                    GradientStops = { new GradientStop(Color.FromArgb(0, acc.R, acc.G, acc.B), 0), new GradientStop(Lighten(acc, 0.35), 0.5), new GradientStop(Color.FromArgb(0, acc.R, acc.G, acc.B), 1) },
                };
                double left = (Stage.ActualWidth - _welcomeW) / 2;
                _welcomeLine.Margin = new Thickness(0, h - 1.6, 0, 0);
                var run = new DoubleAnimation(left - 90, left + _welcomeW, TimeSpan.FromMilliseconds(1100)) { BeginTime = TimeSpan.FromMilliseconds(420), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                ((TranslateTransform)_welcomeLine.RenderTransform).BeginAnimation(TranslateTransform.XProperty, run);
                var glow = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(420), Duration = TimeSpan.FromMilliseconds(1100) };
                glow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
                glow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800))));
                glow.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100))));
                _welcomeLine.BeginAnimation(OpacityProperty, glow);
            }
        });

        // 3 · the words go, the face goes home, the notch rests
        After(Eco ? 1800 : 2700, () => EndWelcome(quick: false));
        return true;
    }

    void EndWelcome(bool quick)
    {
        if (_welcomePhase == 0) return;
        _welcomePhase = 0;
        var t = TimeSpan.FromMilliseconds(quick || Eco ? 1 : 360);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _welcomeText?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(quick || Eco ? 1 : 200)));
        _welcomeLine?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(1)));
        _faceShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, t) { EasingFunction = ease });
        _faceShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, t) { EasingFunction = ease });
        if (_face != null) { _face.BeginAnimation(OpacityProperty, null); _face.Opacity = 1; if (_face.Current == NotchFace.Mood.Sleep) _face.SetMood(NotchFace.Mood.Idle); }
        ApplyShape();
    }

    /// <summary>Developer look (DYNAMICNOTCH_OPEN=welcome): the welcome, whatever is going on, once Pixo has settled.</summary>
    void DevWelcome()
    {
        _welcomeDev = true;
        SetMode(Mode.Idle);
        FaceGet();
        _faceHelloOn = DateTime.Today; _faceGreetedStart = true; // (the daily wave stays out of it)
        if (!_faceShown) FaceShow(true);
        Services.AppLog.Write($"welcome dev: started={StartWelcome()} base={BaseMode} shown={IsVisible} root={Root.Opacity:0.00} shell={Shell.Opacity:0.00}");
    }

    /// <summary>The notch's shape while it says hello: a point, then a slim pill around the face and the words.</summary>
    bool WelcomeShape(ref double w, ref double h, ref double r)
    {
        if (_welcomePhase == 0) return false;
        if (_mode != Mode.Idle && !_welcomeDev) { After(1, () => EndWelcome(quick: true)); return false; } // something needs the notch: it steps aside
        if (_welcomePhase == 1) { w = 14; h = 6; r = 3; return true; }
        w = _welcomeW; h = 34; r = 17;
        return true;
    }
}
