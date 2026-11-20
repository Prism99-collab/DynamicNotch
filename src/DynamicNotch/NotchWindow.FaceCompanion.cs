using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Controls;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The companion: Pixo's face comes down from the notch now and then, hanging on a fine thread, and has a little
/// moment, then climbs back up.
///  • When: the notch has been left alone for 10 seconds (and isn't over a full-screen app or a game); then every
///    minute or so while it stays quiet. Reaching for the notch sends it back up at once.
///  • What it does: a scene that fits the moment: it dances with its headphones on when music plays, yawns late at
///    night, and otherwise waves hello, looks around curiously, sends hearts, beams proudly, winks, or bounces.
///  • Away for a minute: it comes down and falls asleep there, z's drifting up, swaying slowly; when you're back it
///    wakes, says hello and climbs back up.
///  • You can play with it: it watches your pointer, smiles when you come close, and a click pokes it (three quick
///    pokes and it's dizzy).
///  • Neon: it projects down like a hologram, flickering on and off, its thread a cyan beam.
/// While it's out, the face isn't in the notch (it's down here). Off in Settings ("Companion").
/// </summary>
public partial class NotchWindow
{
    FaceHost? _cHost;
    NotchFace? _cFace;
    Canvas? _cRig;
    Line? _cThread;
    readonly RotateTransform _cSwing = new(0, FaceHost.HostWidth / 2, 0);
    readonly DispatcherTimer _compTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _cLook = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
    DateTime _cQuietSince = DateTime.UtcNow, _cNextVisit = DateTime.UtcNow.AddSeconds(10), _cLeaveAt;
    bool _cOut, _cAsleep, _cLeaving, _cSmiled;
    int _cToken;

    const double CFaceSize = 30, CHangY = 30; // the face's size; where it hangs (its top, below the notch)

    void WireFaceCompanion()
    {
        _compTimer.Tick += (_, _) => CompanionTick();
        _compTimer.Start();
        _cLook.Tick += (_, _) => CompanionLook();
    }

    /// <summary>It may be out: switched on, the notch resting on screen (not full screen, not a game, nobody using it).</summary>
    bool CompanionAllowed() =>
        _settings.FaceCompanion && !_hidden && !_offScreen && !_fgFullscreen && !InGame && _mode is Mode.Idle or Mode.Media
        && !_peek && !_dodged && !Ringing && !_inMeeting && !_presenter && !_focus.IsOn; // never in a meeting, presenting, or focus

    void CompanionTick()
    {
        var now = DateTime.UtcNow;
        bool allowed = CompanionAllowed() && !PointerNearNotch();
        if (!allowed)
        {
            _cQuietSince = now;
            if (_cOut && !_cLeaving) { if (!_cAsleep) CompanionLearn(shooed: PointerNearNotch()); CompanionLeave(fast: true); }
            return;
        }
        bool away = IdleTime().TotalSeconds >= 60;
        if (_cOut)
        {
            if (_cLeaving) return;
            CompanionPlace();
            if (_cAsleep && !away)
            {
                // you're back: it wakes, says hello, and goes home
                _cAsleep = false;
                _cFace!.SetMood(NotchFace.Mood.Idle);
                _cFace.Play(NotchFace.Emote.Hello, 1.6);
                _cLeaveAt = now.AddSeconds(2.2);
            }
            if (!_cAsleep && now >= _cLeaveAt) { CompanionLearn(shooed: false); CompanionLeave(fast: false); }
            return;
        }
        if (away && !_inMeeting && !_presenter && !OnCall) { CompanionArrive(sleepy: true); return; }
        if ((now - _cQuietSince).TotalSeconds >= 10 && now >= _cNextVisit)
        {
            if (CompanionBusy()) { _cNextVisit = now.AddSeconds(20); return; } // not now: it tries again in a little while
            CompanionArrive(sleepy: false);
        }
    }

    /// <summary>The pointer at the notch or just under it: you're reaching for it.</summary>
    bool PointerNearNotch()
    {
        if (!GetCursorPos(out var p)) return false;
        double cx = _winLeft + _winW / 2.0, half = (_sw.Target / 2 + 40) * _scale;
        return Math.Abs(p.X - cx) < half && p.Y - _mon.Top < (_sh.Target + 12) * _scale;
    }

    void CompanionBuild()
    {
        if (_cHost != null) return;
        _cHost = new FaceHost();
        _cRig = new Canvas { Width = FaceHost.HostWidth, Height = FaceHost.HostHeight, RenderTransform = _cSwing };
        _cThread = new Line { X1 = FaceHost.HostWidth / 2, X2 = FaceHost.HostWidth / 2, Y1 = 0, Y2 = 0, StrokeThickness = 1.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        _cFace = new NotchFace(CFaceSize) { Cursor = Cursors.Hand };
        _cFace.IsHitTestVisible = true;
        _cFace.Voice += what => { if (what is "poke" or "dizzy" or "happy" or "hello") Services.FaceSound.Say(what, _cyber); };
        _cRig.Children.Add(_cThread);
        _cRig.Children.Add(_cFace);
        Canvas.SetLeft(_cFace, FaceHost.HostWidth / 2 - _cFace.Width / 2);
        Canvas.SetTop(_cFace, -_cFace.Height - 4);
        _cHost.Stage.Children.Add(_cRig);
        _cHost.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; _cEngaged = true; _cFace.Poke(); _cLeaveAt = DateTime.UtcNow.AddSeconds(3); };
    }

    /// <summary>Its window's top centre at the notch's bottom centre.</summary>
    void CompanionPlace()
    {
        try
        {
            var bottom = Pill.PointToScreen(new Point(Pill.ActualWidth / 2, Pill.ActualHeight - 2));
            _cHost?.Place((int)Math.Round(bottom.X), (int)Math.Round(bottom.Y), _scale);
        }
        catch { }
    }

    void CompanionArrive(bool sleepy)
    {
        CompanionBuild();
        var face = _cFace!;
        int token = ++_cToken;
        _cOut = true; _cLeaving = false; _cAsleep = sleepy; _cSmiled = false; _cEngaged = false; _cArrivedAt = DateTime.UtcNow;
        face.Neon = _cyber; face.Visor = false; face.Eco = Eco;
        face.Accent = PalAccent();
        face.SetOutfit(FaceOutfitNow(), animate: false);
        face.SetMood(NotchFace.Mood.Idle);
        var tone = _cyber ? CyCyan : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        _cThread!.Stroke = Frozen(Color.FromArgb(_cyber ? (byte)0xC0 : (byte)0x70, tone.R, tone.G, tone.B));
        _cThread.Effect = _cyber && !Eco ? new System.Windows.Media.Effects.DropShadowEffect { Color = CyCyan, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.8 } : null;
        CompanionPlace();
        _cHost!.Opacity = 1;
        _cHost.ShowQuiet();
        FaceShow(false); // the face has left the notch: it's down here now

        // it comes down on its thread, a touch of bounce at the end
        double to = CHangY;
        var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
        var drop = new DoubleAnimation(-face.Height - 4, to, TimeSpan.FromMilliseconds(Eco ? 1 : 700)) { EasingFunction = ease };
        face.BeginAnimation(Canvas.TopProperty, drop);
        _cThread.BeginAnimation(System.Windows.Shapes.Line.Y2Property, new DoubleAnimation(0, to + 3, TimeSpan.FromMilliseconds(Eco ? 1 : 700)) { EasingFunction = ease });
        if (_cyber && !Eco) Hologram(_cHost, on: true);
        // a slow pendulum sway while it hangs there
        if (!Eco)
        {
            var sway = new DoubleAnimation(-5, 5, TimeSpan.FromSeconds(sleepy ? 3.6 : 2.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Timeline.SetDesiredFrameRate(sway, 30);
            _cSwing.BeginAnimation(RotateTransform.AngleProperty, sway);
        }
        _cLook.Start();

        if (sleepy)
        {
            After(800, () => { if (token == _cToken) face.SetMood(NotchFace.Mood.Sleep); });
            return;
        }
        // its moment: what fits what you're doing and the mood of your day (NotchWindow.CompanionMind)
        var scene = CompanionPick();
        bool Alive() => token == _cToken && !_cLeaving;
        After(650, () => { if (Alive()) scene.Play(face, Alive); });
        _cLeaveAt = DateTime.UtcNow.AddSeconds(scene.Secs);
        _cNextVisit = DateTime.UtcNow + CompanionGap();
    }

    void CompanionLeave(bool fast)
    {
        if (!_cOut || _cHost == null || _cFace == null || _cThread == null) return;
        _cLeaving = true;
        int token = ++_cToken;
        _cLook.Stop();
        _cHost.Clickable(false);
        var t = TimeSpan.FromMilliseconds(fast || Eco ? 200 : 520);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        double up = -_cFace.Height - 4;
        _cFace.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(up, t) { EasingFunction = ease });
        _cThread.BeginAnimation(System.Windows.Shapes.Line.Y2Property, new DoubleAnimation(0, t) { EasingFunction = ease });
        if (_cyber && !Eco && !fast) Hologram(_cHost, on: false);
        After((int)t.TotalMilliseconds + 40, () =>
        {
            if (token != _cToken) return;
            _cSwing.BeginAnimation(RotateTransform.AngleProperty, null);
            _cSwing.Angle = 0;
            _cHost.HideQuiet();
            _cOut = false; _cLeaving = false; _cAsleep = false;
            _cQuietSince = DateTime.UtcNow; // the next visit waits for quiet again
        });
    }

    /// <summary>Neon: a hologram switching on (or off): it flickers before it holds.</summary>
    static void Hologram(UIElement el, bool on)
    {
        var flick = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(on ? 420 : 380) };
        double[] pattern = on ? new[] { 0, .7, .15, .9, .4, 1 } : new[] { 1, .3, .8, .1, .5, 0 };
        for (int i = 0; i < pattern.Length; i++)
            flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(pattern[i], KeyTime.FromPercent(i / (double)(pattern.Length - 1))));
        el.BeginAnimation(OpacityProperty, flick);
    }

    /// <summary>While it's out: it watches your pointer, smiles when you come close, and takes a poke.</summary>
    void CompanionLook()
    {
        if (!_cOut || _cLeaving || _cHost == null || _cFace == null || !GetCursorPos(out var p)) return;
        var (x, y, w, _) = _cHost.Placed;
        double s = Math.Max(0.5, _scale);
        double fx = x + w / 2.0, fy = y + (Canvas.GetTop(_cFace) + _cFace.Height / 2) * s;
        double dx = (p.X - fx) / s, dy = (p.Y - fy) / s, dist = Math.Sqrt(dx * dx + dy * dy);
        if (!_cAsleep && dist < 260) _cFace.LookAt(dx / 160, -dy / 110);
        // close by: a smile (once a visit), and it stays a little longer for you
        if (!_cAsleep && dist < 60 && !_cSmiled) { _cSmiled = true; _cEngaged = true; _cFace.Play(NotchFace.Emote.Happy, 1.3); _cLeaveAt = DateTime.UtcNow.AddSeconds(4); }
        bool on = dist < _cFace.Width / 2 + 4;
        _cHost.Clickable(on);
    }
}
