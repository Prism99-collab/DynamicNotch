using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicNotch.Controls;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The face in the notch (see <see cref="NotchFace"/>): it lives in the idle notch and gives it a little life.
///  • It blinks and glances around, and follows the pointer when it comes near.
///  • You reach for the notch: it smiles up at you. You type: it looks down, focused.
///  • Away for a few minutes: it dozes off, z's drifting out, and says hello when you're back.
///  • Things happening get a reaction: done (a hop and sparks), an alert (surprised), too loud (squeezed shut),
///    low battery (tired), the charger in (hearts). A reaction while the notch is busy waits for it to be idle again.
/// It shows in the idle notch and the music pill (dancing along), and draws nothing while it sits still.
/// </summary>
public partial class NotchWindow
{
    NotchFace? _face;
    readonly DispatcherTimer _faceTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    readonly ScaleTransform _faceScale = new(1, 1);
    bool _faceShown, _facePeek;
    NotchFace.Emote? _facePending;
    DateTime _facePendingUntil, _faceNextYawn, _faceHelloOn;
    readonly bool[] _faceKeys = new bool[6];
    int _faceKeyAt;
    uint _faceLastInput;
    POINT _faceLastCursor;

    void WireFace()
    {
        _faceTimer.Tick += (_, _) => FaceTick();
        _faceTimer.Start();
        Services.FaceSound.Warm(); // its sounds made ahead of time: no wait the first time
        _media.TrackChanged += FaceWake; // a new song: the notch shows it at full size
        WireFacePokes();
    }

    bool FaceAllowed() => _settings.NotchFace && _mode is Mode.Idle or Mode.Media && !_hidden && !_offScreen && !_fgFullscreen && !_dodged;

    NotchFace FaceGet()
    {
        if (_face != null) return _face;
        _face = new NotchFace(15)
        {
            Sideways = true, // inside the notch its sparks and z's drift out to the sides
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0), Opacity = 0, Visibility = Visibility.Collapsed,
            RenderTransform = new TransformGroup { Children = { _faceScale, _faceShift } }, RenderTransformOrigin = new Point(0.5, 0.3),
        };
        _face.SetOutfit(FaceOutfitNow(), animate: false);
        Stage.Children.Insert(0, _face);
        // in the notch it only speaks for the big moments (hello, a poke, dizzy, hearts): never for every little thing
        _face.Voice += what => { if (what is "hello" or "wave" or "poke" or "dizzy" or "love") Services.FaceSound.Say(what, _cyber); };
        return _face;
    }

    /// <summary>The notch changed: out of the idle notch the face is gone at once (it comes back on the next tick).</summary>
    void FaceMode()
    {
        _restWake = DateTime.UtcNow;
        _rest = false;
        if (_faceGreet && _mode is not (Mode.Idle or Mode.Media)) { _faceGreet = false; FaceGrow(1, 0, 1); }
        if (_faceShown && !FaceAllowed()) FaceShow(false);
    }

    void FaceShow(bool on)
    {
        if (on == _faceShown) return;
        _faceShown = on;
        if (_cyber) After(1, () => CyberShape(_sw.Value, _sh.Value, _sr.Value)); // the visor's brackets come and go with it
        var face = FaceGet();
        if (on)
        {
            face.Visibility = Visibility.Visible;
            face.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            // it pops in, a little springy
            var pop = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
            if (!Eco) { _faceScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop); _faceScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop); }
            // the first time after starting, and the first time each day: it comes out to say hello
            var today = DateTime.Today;
            if (_faceHelloOn != today || !_faceGreetedStart) { _faceHelloOn = today; _faceGreetedStart = true; After(700, FaceGreet); }
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => { if (!_faceShown) face.Visibility = Visibility.Collapsed; };
        face.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>A reaction: now if it's showing, else when it's next back (within a few seconds).</summary>
    void FaceReact(NotchFace.Emote e, double secs = 1.8)
    {
        if (!_settings.NotchFace) return;
        if (_faceShown && _face != null) { _face.Play(e, secs); return; }
        _facePending = e;
        _facePendingUntil = DateTime.UtcNow.AddSeconds(8);
    }

    void FaceTick()
    {
        GetCursorPos(out var cur);
        RestTick(cur);
        bool allowed = FaceAllowed();
        if (!allowed) { if (_faceShown) FaceShow(false); _faceTimer.Interval = TimeSpan.FromMilliseconds(400); return; }
        var face = FaceGet();
        face.Neon = _cyber;
        face.Visor = _cyber; // Neon: the notch is its visor, just the eyes glowing
        Services.FaceSound.Enabled = _settings.FaceSounds;
        face.Accent = PalAccent();
        face.Eco = Eco;
        if (!_faceShown)
        {
            FaceShow(true);
            if (_facePending is { } waiting && DateTime.UtcNow < _facePendingUntil) face.Play(waiting);
            _facePending = null;
        }
        _faceTimer.Interval = TimeSpan.FromMilliseconds(Eco ? 400 : 100);
        face.SetOutfit(FaceOutfitNow());
        // typing: input keeps coming while the pointer stays put (which keys is never looked at)
        var li = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        bool input = GetLastInputInfo(ref li) && _faceLastInput != 0 && li.dwTime != _faceLastInput;
        bool still = cur.X == _faceLastCursor.X && cur.Y == _faceLastCursor.Y;
        _faceLastInput = li.dwTime;
        _faceLastCursor = cur;
        _faceKeys[_faceKeyAt++ % _faceKeys.Length] = input && still;
        int keys = _faceKeys.Count(k => k);

        double idle = IdleTime().TotalSeconds;
        // with music on it dances along (and never dozes off)
        bool music = _mode == Mode.Media && _media.IsPlaying;
        bool woke = face.Current == NotchFace.Mood.Sleep && idle < 180;
        var mood = music ? NotchFace.Mood.Music : idle >= 180 ? NotchFace.Mood.Sleep : keys >= 3 ? NotchFace.Mood.Focus : NotchFace.Mood.Idle;
        if (!music && face.Current == NotchFace.Mood.Focus && mood == NotchFace.Mood.Idle && keys > 0) mood = NotchFace.Mood.Focus; // a pause between words
        face.SetMood(mood);
        if (woke) FaceGreet(); // you're back: it comes out to say hello
        if (mood == NotchFace.Mood.Sleep) return;

        // you reach for the notch: it smiles up at you
        if (_peek && !_facePeek) face.Play(NotchFace.Emote.Happy, 1.2);
        _facePeek = _peek;

        // its eyes follow the pointer when it's near
        if (!Eco && !still)
        {
            double fx = _winLeft + _winW / 2.0, fy = _winTop + 13 * _scale;
            double dx = (cur.X - fx) / _scale, dy = (cur.Y - fy) / _scale;
            if (Math.Abs(dx) < 700 && dy < 500) face.LookAt(dx / 260, -dy / 160);
        }

        // late at night, now and then a yawn
        int hour = DateTime.Now.Hour;
        if ((hour >= 23 || hour < 4) && !Eco)
        {
            var now = DateTime.UtcNow;
            if (_faceNextYawn == default) _faceNextYawn = now.AddSeconds(40);
            else if (now >= _faceNextYawn) { _faceNextYawn = now.AddSeconds(70 + Random.Shared.NextDouble() * 80); face.Play(NotchFace.Emote.Yawn, 2.4); }
        }
    }

    /// <summary>The companion's moments, as the face feels them.</summary>
    void FaceFromAct(Companion.Act act)
    {
        NotchFace.Emote? e = act switch
        {
            Companion.Act.Pump => NotchFace.Emote.Done,
            Companion.Act.Point => NotchFace.Emote.Surprised,
            Companion.Act.Ears => NotchFace.Emote.Annoyed,
            Companion.Act.Tired => NotchFace.Emote.Tired,
            Companion.Act.Cheer => NotchFace.Emote.Love,
            Companion.Act.Strain => NotchFace.Emote.Dizzy,
            _ => null,
        };
        if (e is { } emote) FaceReact(emote, emote is NotchFace.Emote.Tired or NotchFace.Emote.Dizzy ? 3 : 1.8);
    }

    // ───────────────────────────── the greeting: it pops out of the notch and waves ─────────────────────────────

    readonly TranslateTransform _faceShift = new();
    bool _faceGreet, _faceGreetedStart;

    /// <summary>The notch opens a little, the face comes out big, waves hello, and tucks back in.</summary>
    void FaceGreet()
    {
        if (_face is not { } face || !_faceShown) return;
        if (Eco) { face.Play(NotchFace.Emote.Wave, 2); return; }
        _faceGreet = true;
        ApplyShape();
        After(160, () =>
        {
            if (!_faceGreet) return;
            FaceGrow(2.3, 15, 480);
            face.Play(NotchFace.Emote.Wave, 2.5);
        });
        After(2800, () =>
        {
            if (!_faceGreet) return;
            _faceGreet = false;
            FaceGrow(1, 0, 340);
            ApplyShape();
        });
    }

    void FaceGrow(double k, double y, int ms)
    {
        IEasingFunction ease = k > 1 ? new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseInOut };
        var t = TimeSpan.FromMilliseconds(ms);
        _faceScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(k, t) { EasingFunction = ease });
        _faceScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(k, t) { EasingFunction = ease });
        _faceShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, t) { EasingFunction = ease });
    }

    /// <summary>The notch's shape while the face says hello: a little room for it below.</summary>
    bool FaceGreetShape(ref double w, ref double h, ref double r)
    {
        if (!_faceGreet || _mode is not (Mode.Idle or Mode.Media)) return false;
        w = Math.Max(w, 190); h = 66; r = 26;
        return true;
    }

    // ───────────────────────────── poking it, and its hats ─────────────────────────────

    bool _faceClicks;

    /// <summary>The pointer is right on the face: the notch takes that click (a poke) instead of letting it through.</summary>
    void FacePointer(POINT p)
    {
        bool on = _faceShown && _face != null && !_touchMode && IsBase(_mode) && !_settings.Locked && FaceHit(p);
        if (on == _faceClicks) return;
        _faceClicks = on;
        if (on) SetClickThrough(false); else RefreshClickThrough();
    }

    bool FaceHit(POINT p)
    {
        double k = _faceScale.ScaleX;
        double cx = _winLeft + _winW / 2.0 + _leanShift.X * _scale;
        double bottom = _winTop + (3 + _faceShift.Y + 21 * k) * _scale;
        return Math.Abs(p.X - cx) <= 13 * k * _scale && p.Y >= _winTop && p.Y <= bottom;
    }

    void WireFacePokes()
    {
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!_faceClicks || _face == null) return;
            e.Handled = true;
            FaceWake();
            _face.Poke();
        };
        PreviewMouseLeftButtonUp += (_, e) => { if (_faceClicks) e.Handled = true; };
        PreviewMouseRightButtonDown += (_, e) => { if (_faceClicks) e.Handled = true; };
        PreviewMouseRightButtonUp += (_, e) => { if (!_faceClicks) return; e.Handled = true; FaceNextOutfit(); };
    }

    /// <summary>The hat it wears: yours, or with "auto" the season's (Christmas, New Year, Halloween).</summary>
    string FaceOutfitNow()
    {
        var o = _settings.FaceOutfit ?? "auto";
        if (o != "auto") return o;
        var d = DateTime.Today;
        if (d.Month == 12 && d.Day <= 26) return "santa";
        if (d.Month == 12 && d.Day == 31 || d.Month == 1 && d.Day == 1) return "party";
        if (d.Month == 10 && d.Day >= 24) return "witch";
        return "none";
    }

    /// <summary>A right-click on the face: the next hat.</summary>
    void FaceNextOutfit()
    {
        if (_face == null) return;
        var list = NotchFace.Outfits;
        string next = list[(Array.IndexOf(list, _face.Outfit) + 1) % list.Length];
        _settings.FaceOutfit = next;
        _settings.Save();
        _face.SetOutfit(next);
    }

    // ───────────────────────────── resting: unused for 5 s, the notch tucks in small ─────────────────────────────

    bool _rest;
    DateTime _restWake = DateTime.UtcNow;

    void RestTick(POINT cur)
    {
        var now = DateTime.UtcNow;
        bool can = _settings.CompactWhenResting && _mode is Mode.Idle or Mode.Media && !_faceGreet && !_hidden && !_peek;
        double cx = _winLeft + _winW / 2.0, half = (_sw.Value / 2 + 90) * _scale;
        bool near = Math.Abs(cur.X - cx) < half && cur.Y - _mon.Top < 130 * _scale;
        PointerNear(near && IsBase(_mode));
        if (near || !can) _restWake = now;
        bool rest = can && (now - _restWake).TotalSeconds >= 5;
        if (rest == _rest) return;
        _rest = rest;
        ApplyShape();
        UpdateRim(); // the glow below fits the new size
    }

    /// <summary>Something happened (the pointer came, the song changed, the notch changed): back to full size.</summary>
    void FaceWake()
    {
        _restWake = DateTime.UtcNow;
        if (!_rest) return;
        _rest = false;
        ApplyShape();
        UpdateRim();
    }
}
