using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DynamicNotch.Controls;

/// <summary>
/// A little face that lives in the notch: a soft bun with two eyes that turn as if it were round (they slide toward
/// where it looks and narrow at the edge). It blinks, glances around, follows the pointer, squashes when it hops,
/// and shows how it feels with its eyes, a tiny mouth, its cheeks and a few particles (hearts, stars, sparks, z's).
/// The Neon look makes it a dark bun with a neon outline and glowing eyes, glitching when its mood changes.
/// It is drawn in code, a few shapes per frame, and only while something moves: sitting still it costs nothing.
/// </summary>
public sealed class NotchFace : FrameworkElement
{
    public enum Mood { Idle, Thinking, Searching, Working, Error, Sleep, Focus, Music, Talk, Listen, Muted }
    public enum Emote { Happy, Hello, Love, Surprised, Proud, Done, Annoyed, Tired, Wink, Dizzy, Yawn, Wave, Sad, Alert, Excited, Gulp, Overload }
    /// <summary>What it can wear on its head ("auto" is decided outside: by the season).</summary>
    public static readonly string[] Outfits = { "none", "beanie", "cap", "party", "crown", "santa", "witch" };
    enum Eye { Pill, Wide, Dot, Happy, Closed, Flat, Chevron, Heart, Star, Spiral, Tired, Wink, Sad }
    enum Mouth { None, Smile, O, BigO, Frown }
    enum Kind { Heart, Star, Spark, Sweat, Z, Note, Tear, Bang }

    // the box around the bun: room for a squash, a tilt and the badge
    const double BoxW = 1.62, BoxH = 1.3;
    readonly double _s;

    // the values that move: eyes open, squash, hop, shake, tilt, eye size, cheeks
    const int Open = 0, Sx = 1, Sy = 2, Oy = 3, Ox = 4, Tilt = 5, Es = 6, Blush = 7, Hands = 8, Hat = 9;
    readonly double[] _v = { 1, 1, 1, 0, 0, 0, 1, 0, 0, 0 };
    double _waveFrom, _waveUntil, _phones;
    string _hat = "none";
    string? _hatNext;
    readonly List<double> _pokes = new();
    sealed class Tween { public int Prop, I; public (double V, double Ms, Func<double, double> E)[] Keys = []; public double From, Start; }
    readonly Dictionary<int, Tween> _tw = new();
    double _t; // its own clock: moves only while it is drawing

    Mood _mood = Mood.Idle;
    Eye? _eye; double _eyeUntil;
    Mouth _mouth; double _mouthUntil;
    double _yaw, _pitch, _lookX, _lookY, _gazeAt = -99, _glX, _glY, _glUntil, _dizzyUntil, _glitchUntil;
    Color _col = Colors.White, _accent = Color.FromRgb(0x3D, 0xE8, 0xFF);
    double _tint;

    struct Bit { public Kind Kind; public double X, Y, Vx, Vy, Age, Life, Rot, Size; }
    readonly List<Bit> _bits = new();
    static readonly Random Rand = new();

    bool _hooked;
    double _lastDraw;
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    readonly DispatcherTimer _life = new(DispatcherPriority.Background);

    Geometry? _body;
    Brush? _fill, _edge, _shine;

    /// <summary>Particles drift out sideways (inside the notch) instead of rising.</summary>
    public bool Sideways { get; set; }
    /// <summary>Battery saving: fewer frames, no glancing around on its own.</summary>
    public bool Eco { get; set; }
    public bool Neon { get => _neon; set { if (_neon == value) return; _neon = value; _fill = null; InvalidateVisual(); } }
    bool _neon;
    public Color Accent { get => _accent; set { if (_accent == value) return; _accent = value; _fill = null; InvalidateVisual(); } }
    public Mood Current => _mood;

    double _levelTo, _level;
    /// <summary>How loud the voice is right now (0..1): in a call, its mouth moves with yours, its nod with theirs.</summary>
    public double Level { get => _levelTo; set { _levelTo = Math.Clamp(value, 0, 1); if (_mood is Mood.Talk or Mood.Listen) Kick(); } }

    Color? _wear;
    double _wearUntil, _bounceUntil;
    /// <summary>Wears a colour for a few seconds (an alert's own: WhatsApp green, a warning's red...), then its own again.</summary>
    public void Tint(Color c, double secs)
    {
        _wear = c;
        _wearUntil = _t + secs;
        Kick();
    }
    /// <summary>It feels something out loud: the feeling's name ("happy", "love", "poke", "think"...), for its voice.</summary>
    public event Action<string>? Voice;
    /// <summary>The visor look: just the eyes, glowing behind scanlines (Neon, in the notch).</summary>
    public bool Visor { get => _visor; set { if (_visor == value) return; _visor = value; InvalidateVisual(); } }
    bool _visor;

    public NotchFace(double size)
    {
        _s = size;
        Width = Math.Ceiling(size * BoxW);
        Height = Math.Ceiling(size * BoxH);
        IsHitTestVisible = false;
        _life.Tick += (_, _) => Live();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { NextLife(); Kick(); }
            else { _life.Stop(); Unhook(); }
        };
    }

    // ───────────────────────────── what it does ─────────────────────────────

    public void SetMood(Mood m)
    {
        if (m == _mood) return;
        var was = _mood;
        _mood = m;
        Glitch();
        if (m is Mood.Thinking or Mood.Searching && was is Mood.Idle or Mood.Focus) Voice?.Invoke("think");
        if (m == Mood.Sleep) Voice?.Invoke("sleep");
        switch (m)
        {
            case Mood.Error: Anim(Ox, (.09, 50, Out), (-.09, 70, InOut), (.05, 70, InOut), (0, 90, Out)); break;
            case Mood.Thinking or Mood.Searching or Mood.Working when was is Mood.Idle or Mood.Focus: Blink(); break;
            case Mood.Idle when was == Mood.Sleep: Play(Emote.Hello, 1.6); break;
            case Mood.Idle or Mood.Focus: Blink(); break;
        }
        Kick();
    }

    public void Play(Emote e, double secs = 1.8)
    {
        Glitch();
        Voice?.Invoke(e.ToString().ToLowerInvariant());
        switch (e)
        {
            case Emote.Happy:
                Show(Eye.Happy, Mouth.Smile, secs);
                Anim(Blush, (.6, 200, Out), (0, 700, InOut));
                Hop(0.6);
                break;
            case Emote.Hello:
                Show(Eye.Happy, Mouth.Smile, secs);
                Hop(1);
                Anim(Tilt, (-.12, 160, Out), (.1, 220, InOut), (-.06, 200, InOut), (0, 220, InOut));
                Emit(Kind.Spark, 3);
                break;
            case Emote.Love:
                Show(Eye.Heart, Mouth.Smile, secs);
                Anim(Blush, (1, 300, Out), (1, Math.Max(100, (secs - .6) * 1000), Lin), (0, 300, InOut));
                Anim(Oy, (-.12, 160, Out), (0, 320, Back));
                Emit(Kind.Heart, 4);
                break;
            case Emote.Surprised:
                Show(Eye.Dot, Mouth.O, secs);
                Anim(Oy, (-.3, 140, Out), (0, 380, Back));
                Anim(Es, (1.3, 120, Out), (1, 500, InOut));
                break;
            case Emote.Proud:
                Show(Eye.Star, Mouth.Smile, secs);
                Anim(Tilt, (-.14, 220, Out), (-.14, Math.Max(100, (secs - .5) * 1000), Lin), (0, 280, InOut));
                Anim(Blush, (.7, 250, Out), (.7, Math.Max(100, (secs - .5) * 1000), Lin), (0, 300, InOut));
                Emit(Kind.Star, 4);
                break;
            case Emote.Done:
                Show(Eye.Happy, Mouth.Smile, secs);
                Hop(1.2);
                Anim(Blush, (.5, 200, Out), (0, 800, InOut));
                Emit(Kind.Spark, 5);
                break;
            case Emote.Annoyed:
                Show(Eye.Chevron, Mouth.None, secs);
                Anim(Sx, (1.12, 80, Out), (1, 260, Back));
                Anim(Sy, (.86, 80, Out), (1, 260, Back));
                Anim(Ox, (-.07, 50, Out), (.07, 80, InOut), (-.04, 70, InOut), (0, 90, Out));
                break;
            case Emote.Tired:
                Show(Eye.Tired, Mouth.None, secs);
                Anim(Sy, (.92, 500, InOut), (.92, Math.Max(100, (secs - 1) * 1000), Lin), (1, 500, InOut));
                Emit(Kind.Sweat, 1);
                break;
            case Emote.Wink:
                Show(Eye.Wink, Mouth.Smile, Math.Min(secs, .9));
                Anim(Tilt, (.13, 120, Out), (.13, 380, Lin), (0, 220, InOut));
                break;
            case Emote.Dizzy:
                Show(Eye.Spiral, Mouth.None, secs);
                _dizzyUntil = _t + secs;
                break;
            case Emote.Wave:
                // hello: its little hands come out and one waves
                Show(Eye.Happy, Mouth.Smile, secs);
                Anim(Hands, (1, 260, Out), (1, Math.Max(200, (secs - .55) * 1000), Lin), (0, 260, InOut));
                _waveFrom = _t + .3; _waveUntil = _t + secs - .35;
                Hop(.5);
                Emit(Kind.Spark, 3);
                break;
            case Emote.Sad:
                // droopy eyes, a little frown, a tear rolling down
                Show(Eye.Sad, Mouth.Frown, secs);
                Anim(Sy, (.94, 300, InOut), (.94, Math.Max(100, (secs - .7) * 1000), Lin), (1, 400, InOut));
                Anim(Oy, (.05, 300, InOut), (.05, Math.Max(100, (secs - .7) * 1000), Lin), (0, 400, InOut));
                Emit(Kind.Tear, 1);
                break;
            case Emote.Alert:
                // something needs you: a jump, eyes wide, a "!"
                Show(Eye.Wide, Mouth.O, secs);
                Anim(Oy, (-.32, 120, Out), (0, 380, Back));
                Anim(Es, (1.25, 110, Out), (1, 450, InOut));
                Emit(Kind.Bang, 1);
                break;
            case Emote.Excited:
                // bouncing on the spot, beaming
                Show(Eye.Happy, Mouth.BigO, secs);
                _bounceUntil = _t + secs;
                Emit(Kind.Spark, 4);
                break;
            case Emote.Gulp:
                // it eats it: mouth wide, a gulp (squash), then a happy chew
                Show(Eye.Happy, Mouth.BigO, .35);
                Anim(Sy, (.78, 90, Out), (1.18, 140, Out), (.92, 150, InOut), (1.04, 150, InOut), (1, 200, Back));
                Anim(Sx, (1.26, 90, Out), (.9, 140, Out), (1.06, 150, InOut), (.98, 150, InOut), (1, 200, Back));
                _eyeUntil = _t + secs; _eye = Eye.Happy; _mouth = Mouth.BigO; _mouthUntil = _t + .35;
                Emit(Kind.Spark, 2);
                break;
            case Emote.Overload:
                // too much: spiral eyes, red, sweating, the signal tearing
                Show(Eye.Spiral, Mouth.Frown, secs);
                _dizzyUntil = _t + secs;
                Tint(Color.FromRgb(0xFF, 0x2A, 0x4F), secs);
                Emit(Kind.Sweat, 2);
                if (_neon) _glitchUntil = _t + .5;
                break;
            case Emote.Yawn:
                Show(Eye.Closed, Mouth.BigO, secs);
                Anim(Sy, (1.12, 500, InOut), (1, 600, InOut));
                Anim(Sx, (.94, 500, InOut), (1, 600, InOut));
                Emit(Kind.Z, 2);
                break;
        }
        Kick();
    }

    /// <summary>You poked it: a squash and a frown. Three pokes quickly and it gets dizzy (then true).</summary>
    public bool Poke()
    {
        double now = Clock.Elapsed.TotalSeconds;
        _pokes.RemoveAll(p => now - p > 1.7);
        _pokes.Add(now);
        Glitch();
        Anim(Sy, (.76, 70, Out), (1.1, 130, Out), (1, 170, InOut));
        Anim(Sx, (1.18, 70, Out), (.95, 130, Out), (1, 170, InOut));
        double side = Rand.Next(2) == 0 ? -1 : 1;
        Anim(Ox, (side * .12, 70, Out), (0, 380, Back));
        Anim(Tilt, (side * .14, 70, Out), (0, 420, Back));
        if (_pokes.Count >= 3) { _pokes.Clear(); Play(Emote.Dizzy, 2.2); return true; }
        Show(Eye.Chevron, Mouth.None, .7);
        Voice?.Invoke("poke");
        Kick();
        return false;
    }

    /// <summary>Puts on a hat (one of <see cref="Outfits"/>): the old one lifts off, the new one drops on with a squash.</summary>
    public void SetOutfit(string outfit, bool animate = true)
    {
        if (Array.IndexOf(Outfits, outfit) < 0) outfit = "none";
        if (outfit == (_hatNext ?? _hat)) return;
        if (!animate) { _hat = outfit; _hatNext = null; _v[Hat] = outfit == "none" ? 0 : 1; InvalidateVisual(); return; }
        _hatNext = outfit;
        if (_hat == "none") _v[Hat] = 0; else Anim(Hat, (0, 170, InOut));
        Kick();
    }

    public string Outfit => _hatNext ?? _hat;

    public void Blink()
    {
        if (_tw.ContainsKey(Open)) return;
        Anim(Open, (.06, 70, InOut), (1, 130, Out));
        Kick();
    }

    /// <summary>A little jump: it squashes, springs up and lands (strength 0..1.5).</summary>
    public void Hop(double k = 1)
    {
        Anim(Sy, (1 - .2 * k, 70, Out), (1 + .12 * k, 130, Out), (1, 180, InOut));
        Anim(Sx, (1 + .14 * k, 70, Out), (1 - .05 * k, 130, Out), (1, 180, InOut));
        Anim(Oy, (.02, 70, Out), (-.24 * k, 150, Out), (0, 280, Back));
        Kick();
    }

    /// <summary>Where it looks: -1..1 each way (up is +y). Eased; it looks back ahead a moment after the last call.</summary>
    public void LookAt(double x, double y)
    {
        x = Math.Clamp(x, -1, 1); y = Math.Clamp(y, -1, 1);
        bool moved = Math.Abs(x - _lookX) > .01 || Math.Abs(y - _lookY) > .01;
        _lookX = x; _lookY = y; _gazeAt = _t;
        if (moved) Kick();
    }

    void Show(Eye eye, Mouth mouth, double secs)
    {
        _eye = eye; _eyeUntil = _t + secs;
        _mouth = mouth; _mouthUntil = _t + secs;
    }

    void Glitch() { if (_neon && !Eco) _glitchUntil = _t + .22; }

    void Emit(Kind kind, int n)
    {
        if (Eco) n = Math.Min(n, 2);
        for (int i = 0; i < n; i++)
        {
            var b = new Bit { Kind = kind, Age = -i * .14, Life = 1.2 + Rand.NextDouble() * .5, Rot = Rand.NextDouble() * Math.PI * 2, Size = .17 + Rand.NextDouble() * .08 };
            if (kind == Kind.Tear)
            {
                b.X = -.32; b.Y = .1; b.Vx = 0; b.Vy = .5; b.Life = 1.1; b.Size = .14;
            }
            else if (kind == Kind.Bang)
            {
                b.X = .55; b.Y = -.45; b.Vx = .12; b.Vy = -.25; b.Life = 1.2; b.Size = .28;
            }
            else if (Sideways)
            {
                double side = kind == Kind.Z ? 1 : Rand.Next(2) == 0 ? -1 : 1;
                b.X = side * .6; b.Y = (Rand.NextDouble() - .5) * .3;
                b.Vx = side * (.55 + Rand.NextDouble() * .45); b.Vy = (Rand.NextDouble() - .5) * .18;
            }
            else
            {
                bool z = kind == Kind.Z;
                b.X = (Rand.NextDouble() - .5) * .9 + (z ? .5 : 0); b.Y = -.6 - Rand.NextDouble() * .2;
                b.Vx = (Rand.NextDouble() - .5) * .35 + (z ? .18 : 0); b.Vy = -(.45 + Rand.NextDouble() * .35);
            }
            _bits.Add(b);
        }
    }

    void Anim(int prop, params (double V, double Ms, Func<double, double> E)[] keys)
        => _tw[prop] = new Tween { Prop = prop, Keys = keys, From = _v[prop], Start = _t };

    // ───────────────────────────── living ─────────────────────────────

    void NextLife()
    {
        _life.Interval = TimeSpan.FromSeconds(2.4 + Rand.NextDouble() * 3.4);
        _life.Start();
    }

    /// <summary>Every few seconds, sitting still: a blink, or a glance somewhere.</summary>
    void Live()
    {
        NextLife();
        if (!IsVisible) { _life.Stop(); return; }
        if (_mood == Mood.Sleep || _eye != null && _t < _eyeUntil) return;
        if (_neon && !Eco && Rand.NextDouble() < .2) { Glitch(); Kick(); } // Neon: a flicker of signal now and then
        double r = Rand.NextDouble();
        if (r < .55 || Eco)
        {
            Blink();
            if (Rand.NextDouble() < .22) Dispatcher.InvokeAsync(async () => { await Task.Delay(240); Blink(); });
        }
        else if (_t - _gazeAt > 1.5 && _mood is Mood.Idle or Mood.Focus)
        {
            if (r > .95) { Play(Emote.Wink, .8); return; }
            _glX = (Rand.NextDouble() * 2 - 1) * .8; _glY = (Rand.NextDouble() * 2 - 1) * .35;
            _glUntil = _t + .7 + Rand.NextDouble() * .7;
            Kick();
        }
    }

    void Kick()
    {
        if (_hooked || !IsVisible) return;
        _hooked = true;
        _lastDraw = Clock.Elapsed.TotalSeconds - 1 / 60.0;
        CompositionTarget.Rendering += OnFrame;
    }

    void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        double now = Clock.Elapsed.TotalSeconds;
        if (now - _lastDraw < 0.9 / Fps) return;
        double dt = Math.Min(.05, now - _lastDraw);
        _lastDraw = now;
        Step(dt);
        InvalidateVisual();
        if (!Busy) Unhook();
    }

    /// <summary>Frames a second: asleep only its breath moves; a loop (searching) needs fewer than a hop.</summary>
    int Fps => _tw.Count > 0 || _bits.Count > 0 ? (Eco ? 30 : 60) : _mood == Mood.Sleep ? 15 : Eco ? 20 : 30;

    bool Moving => _mood is Mood.Talk or Mood.Listen || _t < _waveUntil || _t < _bounceUntil || _wear != null && _t < _wearUntil + .3 || _hatNext != null || Math.Abs(_phones - (_mood == Mood.Music ? 1 : 0)) > .01 || _mood is Mood.Searching or Mood.Working or Mood.Sleep || _mood == Mood.Music && !Eco || _t < _dizzyUntil || _t < _glitchUntil || Badge;
    bool Badge => _s >= 16 && _mood is Mood.Thinking or Mood.Searching or Mood.Working;

    bool Busy
    {
        get
        {
            if (_tw.Count > 0 || _bits.Count > 0 || Moving) return true;
            var (ty, tp) = LookTarget();
            if (Math.Abs(_yaw - ty) > .003 || Math.Abs(_pitch - tp) > .003) return true;
            if (Math.Abs(_tint - MoodTint(_mood)) > .004 || !Near(_col, MoodColor(_mood))) return true;
            if (_eye != null && _t >= _eyeUntil || _mouth != Mouth.None && _t >= _mouthUntil) return true; // to go back to its own eyes
            if (_eye != null && _t < _eyeUntil) return true; // the clock has to run to end it
            return Math.Abs(_v[Sx] - 1) > .002 || Math.Abs(_v[Sy] - 1) > .002 || Math.Abs(_v[Oy]) > .002 || Math.Abs(_v[Tilt] - TiltTarget()) > .002;
        }
    }

    static bool Near(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 3;

    (double Yaw, double Pitch) LookTarget()
    {
        double x = 0, y = 0;
        if (_t - _gazeAt < 1.6) { x = _lookX * .62; y = _lookY * .5; }
        if (_t < _glUntil) { x = _glX * .62; y = _glY * .5; }
        double t = _t;
        switch (_mood)
        {
            case Mood.Thinking: x = x * .3 + .5; y = y * .3 + .45; break;
            case Mood.Searching: x = Math.Sin(t * 2.6) * .6; y = -.05; break;
            case Mood.Working: x *= .4; y = -.2; break;
            case Mood.Focus: x *= .4; y = -.34; break;
            case Mood.Sleep: x = 0; y = -.14; break;
            case Mood.Listen: x = -.55 + Math.Sin(t * 1.3) * .08; y = .05; break;   // toward the caller (their icon is on the left)
            case Mood.Talk: x *= .3; y = .1; break;
            case Mood.Muted: x *= .3; y = -.1; break;
            case Mood.Music when !Eco: x = x * .4 + Math.Sin(t * Math.PI * Beat / 2) * .3; break;
        }
        if (_t < _dizzyUntil) x = Math.Sin(t * 9) * .25;
        return (x, y);
    }

    double TiltTarget() => _mood == Mood.Listen ? Math.Sin(_t * 3.2) * (.04 + _level * .06) : _t < _dizzyUntil ? Math.Sin(_t * 8) * .16 : _mood == Mood.Thinking ? .1 : _mood == Mood.Music && !Eco ? Math.Sin(_t * Math.PI * Beat / 2) * .12 : 0;

    /// <summary>Beats a second it bobs to with music (about 112 a minute).</summary>
    const double Beat = 1.87;

    void Step(double dt)
    {
        _t += dt;
        foreach (var tw in _tw.Values.ToList())
        {
            var k = tw.Keys[tw.I];
            double f = k.Ms <= 0 ? 1 : Math.Clamp((_t - tw.Start) * 1000 / k.Ms, 0, 1);
            _v[tw.Prop] = tw.From + (k.V - tw.From) * k.E(f);
            if (f < 1) continue;
            tw.From = k.V; tw.I++; tw.Start = _t;
            if (tw.I >= tw.Keys.Length) _tw.Remove(tw.Prop);
        }
        double t = _t, ease = 1 - Math.Pow(.0008, dt), look = 1 - Math.Pow(.0025, dt);
        var (ty, tp) = LookTarget();
        _yaw += (ty - _yaw) * look;
        _pitch += (tp - _pitch) * look;

        // what its mood does to its body: a busy little bob, the breath of sleep
        _level += (_levelTo - _level) * (1 - Math.Pow(.0004, dt)); // the voice, smoothed
        double oy = _mood == Mood.Talk ? -_level * .07 : _t < _bounceUntil ? -Math.Abs(Math.Sin(t * 11)) * .16 : _mood == Mood.Working ? -Math.Abs(Math.Sin(t * 7)) * .04 : _mood == Mood.Music && !Eco ? -Math.Abs(Math.Sin(t * Math.PI * Beat)) * .09 : 0;
        double sy = 1, sx = 1;
        if (_mood == Mood.Sleep) { sy = 1 + Math.Sin(t * 1.8) * .045; sx = 1 - Math.Sin(t * 1.8) * .026; }
        if (_mood == Mood.Music && !Eco) { double land = Math.Pow(1 - Math.Abs(Math.Sin(t * Math.PI * Beat)), 6); sy = 1 - .07 * land; sx = 1 + .05 * land; } // it lands on each beat
        if (!_tw.ContainsKey(Oy)) _v[Oy] += (oy - _v[Oy]) * ease;
        if (!_tw.ContainsKey(Sy)) _v[Sy] += (sy - _v[Sy]) * ease;
        if (!_tw.ContainsKey(Sx)) _v[Sx] += (sx - _v[Sx]) * ease;
        if (!_tw.ContainsKey(Tilt)) _v[Tilt] += (TiltTarget() - _v[Tilt]) * ease;

        var c = MoodColor(_mood);
        double k2 = 1 - Math.Pow(.002, dt);
        _col = Color.FromRgb((byte)(_col.R + (c.R - _col.R) * k2), (byte)(_col.G + (c.G - _col.G) * k2), (byte)(_col.B + (c.B - _col.B) * k2));
        if (Near(_col, c)) _col = c;
        _tint += (MoodTint(_mood) - _tint) * k2;

        // the hat change: once the old one is off, the new one drops on
        if (_hatNext != null && !_tw.ContainsKey(Hat))
        {
            _hat = _hatNext; _hatNext = null;
            if (_hat != "none")
            {
                _v[Hat] = 0;
                Anim(Hat, (1, 340, Back));
                Anim(Sy, (.84, 90, Out), (1.06, 140, Out), (1, 180, InOut));
                Anim(Sx, (1.12, 90, Out), (.96, 140, Out), (1, 180, InOut));
            }
        }
        // headphones while it dances to music
        double phonesTo = _mood == Mood.Music ? 1 : 0;
        _phones += (phonesTo - _phones) * (1 - Math.Pow(.0005, dt));
        if (Math.Abs(_phones - phonesTo) < .01) _phones = phonesTo;
        if (_eye != null && _t >= _eyeUntil) _eye = null;
        if (_mouth != Mouth.None && _t >= _mouthUntil) _mouth = Mouth.None;

        // asleep, a z now and then
        if (_mood == Mood.Sleep && t - _lastZ > 1.5) { _lastZ = t; Emit(Kind.Z, 1); }
        if (_mood == Mood.Music && !Eco && t - _lastZ > 2.2) { _lastZ = t; Emit(Kind.Note, 1); } // a note now and then
        for (int i = 0; i < _bits.Count; i++) { var b = _bits[i]; b.Age += dt; _bits[i] = b; }
        _bits.RemoveAll(b => b.Age >= b.Life);
    }
    double _lastZ;

    Color MoodColor(Mood m) => _wear is { } w && _t < _wearUntil ? w : m switch
    {
        Mood.Thinking => Color.FromRgb(0x8B, 0x5C, 0xF6),
        Mood.Searching => Color.FromRgb(0x63, 0x66, 0xF1),
        Mood.Working => Color.FromRgb(0x3B, 0x9E, 0xFF),
        Mood.Error => Color.FromRgb(0xF4, 0x50, 0x5E),
        Mood.Sleep => Color.FromRgb(0x94, 0xA3, 0xB8),
        Mood.Focus or Mood.Music or Mood.Listen => _accent,
        Mood.Talk => Color.FromRgb(0x30, 0xD1, 0x58),
        Mood.Muted => Color.FromRgb(0xFF, 0x45, 0x3A),
        _ => _neon ? _accent : Colors.White,
    };

    double MoodTint(Mood m) => _wear != null && _t < _wearUntil ? Math.Max(.6, BaseTint(m)) : BaseTint(m);
    static double BaseTint(Mood m) => m switch
    {
        Mood.Idle => 0, Mood.Focus => .3, Mood.Music => .3, Mood.Talk => .45, Mood.Listen => .25, Mood.Muted => .45, Mood.Sleep => .32, Mood.Error => .78, _ => .7,
    };

    // ───────────────────────────── drawing ─────────────────────────────

    static double Lin(double t) => t;
    static double Out(double t) => 1 - Math.Pow(1 - t, 3);
    static double InOut(double t) => t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    static double Back(double t) { const double c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2); }

    static Color Mix(Color a, Color b, double k) => Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
    static Color A(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);
    static T F<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // brushes reused, frozen (no new paint objects every frame: no garbage, no cleanup pauses while it moves)
    static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
    static SolidColorBrush Br(Color c)
    {
        if (BrushCache.TryGetValue(c, out var b)) return b;
        if (BrushCache.Count > 512) BrushCache.Clear();
        b = new SolidColorBrush(c);
        b.Freeze();
        return BrushCache[c] = b;
    }

    /// <summary>The bun: a rounded square-ish oval, a touch wider at the bottom, as if it sat on something.</summary>
    static Geometry BodyGeometry(double rx, double ry)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            for (int i = 0; i <= 64; i++)
            {
                double a = i / 64.0 * Math.PI * 2, co = Math.Cos(a), si = Math.Sin(a);
                double x = rx * Sp(co) * (1 + .05 * si), y = ry * Sp(si);
                if (i == 0) c.BeginFigure(new Point(x, y), true, true); else c.LineTo(new Point(x, y), true, true);
            }
        }
        g.Freeze();
        return g;
        static double Sp(double v) => Math.Sign(v) * Math.Pow(Math.Abs(v), .78);
    }

    void Paints()
    {
        if (_fill != null) return;
        if (_neon)
        {
            _fill = F(new LinearGradientBrush(Color.FromRgb(0x14, 0x1C, 0x27), Color.FromRgb(0x05, 0x08, 0x0C), new Point(.8, 0), new Point(.2, 1)));
            _shine = F(new RadialGradientBrush(A(Colors.White, .16), A(Colors.White, 0)) { Center = new Point(.72, .24), GradientOrigin = new Point(.72, .24), RadiusX = .34, RadiusY = .4 });
            _edge = null;
        }
        else
        {
            _fill = F(new LinearGradientBrush(Mix(Color.FromRgb(0xF6, 0xF6, 0xF8), _accent, .05), Mix(Color.FromRgb(0xC2, 0xC4, 0xCC), _accent, .12), new Point(.85, .05), new Point(.15, .95)));
            _edge = F(new RadialGradientBrush { GradientStops = { new GradientStop(A(Colors.Black, 0), 0), new GradientStop(A(Colors.Black, 0), .62), new GradientStop(A(Colors.Black, .2), 1) } });
            _shine = F(new RadialGradientBrush(A(Colors.White, .6), A(Colors.White, 0)) { Center = new Point(.7, .24), GradientOrigin = new Point(.7, .24), RadiusX = .34, RadiusY = .42 });
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = _s, w = Width, h = Height;
        double rx = s * .62, ry = s * .5;
        double cx = w / 2 + _v[Ox] * s, cy = h / 2 + s * .06 + _v[Oy] * s;
        bool glitch = _neon && _t < _glitchUntil;
        if (glitch) cx += (Rand.NextDouble() - .5) * s * .14;
        _body ??= BodyGeometry(rx, ry);
        Paints();

        dc.PushTransform(new TranslateTransform(cx, cy));
        dc.PushTransform(new RotateTransform(_v[Tilt] * 180 / Math.PI));
        dc.PushTransform(new ScaleTransform(_v[Sx], _v[Sy], 0, ry)); // it squashes on its bottom
        if (_visor) { DrawVisor(dc, rx, ry); dc.Pop(); dc.Pop(); dc.Pop(); DrawBits(dc, cx, cy); return; }
        DrawHands(dc, rx, ry); // behind it

        // the body
        var moodCol = _col;
        if (_neon)
        {
            var rim = Mix(_accent, moodCol, Math.Min(1, _tint * 1.3));
            if (glitch)
            {
                // a signal tearing for a moment: the outline splits into magenta and cyan
                dc.PushTransform(new TranslateTransform(-s * .06, 0));
                dc.DrawGeometry(null, new Pen(Br(A(Color.FromRgb(0xFF, 0x2B, 0xD6), .7)), 1), _body);
                dc.Pop();
                dc.PushTransform(new TranslateTransform(s * .06, 0));
                dc.DrawGeometry(null, new Pen(Br(A(Color.FromRgb(0x3D, 0xE8, 0xFF), .7)), 1), _body);
                dc.Pop();
            }
            dc.DrawGeometry(null, new Pen(Br(A(rim, .28)), Math.Max(2.4, s * .2)), _body);
            dc.DrawGeometry(_fill, new Pen(Br(rim), Math.Max(1, s * .065)), _body);
            if (_tint > .01) dc.DrawGeometry(new LinearGradientBrush(A(moodCol, .45 * _tint), A(moodCol, 0), 90) { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) }, null, _body);
            dc.DrawGeometry(_shine, null, _body);
        }
        else
        {
            dc.DrawGeometry(_fill, null, _body);
            if (_tint > .01) dc.DrawGeometry(new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0), GradientStops = { new GradientStop(A(moodCol, .72 * _tint), 0), new GradientStop(A(moodCol, 0), 1) } }, null, _body);
            dc.DrawGeometry(_edge, null, _body);
            dc.DrawGeometry(_shine, null, _body);
        }

        dc.PushClip(_body);
        // cheeks
        double blush = Math.Max(_v[Blush], _tint * .35 * (_neon ? 0 : 1));
        if (blush > .01)
        {
            var cheek = Br(A(Color.FromRgb(0xFF, 0x78, 0x96), (_neon ? .6 : .5) * blush));
            double shift = Math.Sin(_yaw) * rx * .8;
            foreach (int side in new[] { -1, 1 })
                dc.DrawEllipse(cheek, null, new Point(side * rx * .56 + shift, ry * .26), s * .16, s * .09);
        }
        var ink = _neon ? Mix(_accent, moodCol, Math.Min(1, _tint * 1.3)) : Color.FromRgb(0x1C, 0x17, 0x15);
        var inkBrush = Br(ink);
        var glowPen = _neon ? new Pen(Br(A(ink, .35)), Math.Max(1.6, s * .1)) { LineJoin = PenLineJoin.Round } : null;
        DrawEyes(dc, rx, ry, inkBrush, glowPen);
        DrawMouth(dc, rx, ry, ink);
        dc.Pop(); // clip
        if (_phones > .01) DrawPhones(dc, rx, ry);
        if (_hat != "none" && _v[Hat] > .01) DrawHat(dc, rx, ry);

        dc.Pop(); dc.Pop(); dc.Pop();

        if (Badge) DrawBadge(dc, cx + rx * .8 * _v[Sx], cy - ry * .95 * _v[Sy]);
        DrawBits(dc, cx, cy);
    }

    void DrawHands(DrawingContext dc, double rx, double ry)
    {
        double k = _v[Hands];
        if (k < .01) return;
        double hw = _s * .15 * k, hh = _s * .125 * k;
        var pen = _neon ? new Pen(Br(_accent), Math.Max(.8, _s * .05)) : new Pen(Br(A(Colors.Black, .14)), .6);
        foreach (int side in new[] { -1, 1 })
        {
            double x = side * rx * 1.02, y = ry * .42, rot = 0;
            if (side > 0 && _t >= _waveFrom && _t < _waveUntil)
            {
                // up beside its head, waving
                double w = _t - _waveFrom, e = 1 - Math.Pow(1 - Math.Min(1, w / .18), 3);
                double tx = rx * 1.14 + Math.Cos(13 * w) * rx * .07, ty = -ry * .32 - Math.Sin(13 * w) * ry * .16;
                x += (tx - x) * e; y += (ty - y) * e; rot = (-.5 + Math.Sin(13 * w) * .35) * e;
            }
            else if (side < 0 && _t >= _waveFrom && _t < _waveUntil) y += Math.Sin(6 * (_t - _waveFrom)) * ry * .05;
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new RotateTransform(rot * 57.3));
            dc.DrawEllipse(_fill, pen, new Point(0, 0), hw, hh);
            dc.Pop(); dc.Pop();
        }
    }

    void DrawPhones(DrawingContext dc, double rx, double ry)
    {
        double k = _phones, s = _s;
        dc.PushOpacity(k);
        dc.PushTransform(new TranslateTransform(0, -(1 - k) * ry * .5));
        var cup = Br(_neon ? Color.FromRgb(0xFF, 0x2B, 0xD6) : _accent);
        if (_hat == "none")
        {
            var band = new StreamGeometry();
            using (var c = band.Open()) { c.BeginFigure(new Point(-rx * .98, -ry * .05), false, false); c.BezierTo(new Point(-rx * 1.02, -ry * 1.55), new Point(rx * 1.02, -ry * 1.55), new Point(rx * .98, -ry * .05), true, true); }
            dc.DrawGeometry(null, new Pen(Br(_neon ? _accent : Color.FromRgb(0x2A, 0x2D, 0x34)), Math.Max(1, s * .075)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, band);
        }
        foreach (int side in new[] { -1, 1 })
        {
            var r = new Rect(side * rx * 1.0 - s * .09, -ry * .26, s * .18, s * .3);
            dc.DrawRoundedRectangle(cup, new Pen(Br(A(Colors.Black, .35)), Math.Max(.6, s * .03)), r, s * .07, s * .07);
        }
        dc.Pop(); dc.Pop();
    }

    void DrawHat(DrawingContext dc, double rx, double ry)
    {
        double p = _v[Hat], s = _s;
        dc.PushOpacity(Math.Clamp(p, 0, 1));
        dc.PushTransform(new TranslateTransform(0, -(1 - p) * ry * .8)); // it drops on from above
        var line = _neon ? new Pen(Br(A(_accent, .8)), Math.Max(.7, s * .035)) : new Pen(Br(A(Colors.Black, .18)), Math.Max(.5, s * .025));
        SolidColorBrush B(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
        Geometry Poly(params Point[] pts)
        {
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(pts[0], true, true); for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i], true, true); }
            return g;
        }
        Geometry Dome(double w, double y0, double up)
        {
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(new Point(-w, y0), true, true); c.BezierTo(new Point(-w, y0 - up), new Point(w, y0 - up), new Point(w, y0), true, true); }
            return g;
        }
        switch (_hat)
        {
            case "beanie":
                dc.DrawGeometry(B(0xE0, 0x45, 0x4B), line, Dome(rx * .86, -ry * .36, ry * 1.25));
                dc.DrawRoundedRectangle(B(0xF2, 0x6D, 0x72), line, new Rect(-rx * .93, -ry * .52, rx * 1.86, ry * .3), ry * .15, ry * .15);
                dc.DrawEllipse(B(0xFF, 0xF1, 0xF1), line, new Point(0, -ry * 1.3), s * .1, s * .1);
                break;
            case "cap":
            {
                var col = _neon ? Color.FromRgb(0x10, 0x18, 0x22) : _accent;
                dc.DrawGeometry(Br(col), line, Dome(rx * .82, -ry * .42, ry * 1.2));
                dc.PushTransform(new RotateTransform(-6));
                dc.DrawRoundedRectangle(Br(Mix(col, Colors.Black, .25)), line, new Rect(rx * .1, -ry * .55, rx * 1.05, ry * .2), ry * .1, ry * .1);
                dc.Pop();
                dc.DrawEllipse(Br(Mix(col, Colors.White, .4)), null, new Point(0, -ry * 1.3), s * .05, s * .035);
                break;
            }
            case "party":
            {
                dc.PushTransform(new RotateTransform(10));
                var cone = Poly(new Point(-rx * .36, -ry * .78), new Point(rx * .36, -ry * .78), new Point(0, -ry * 1.85));
                dc.DrawGeometry(B(0xFF, 0x5F, 0xA2), line, cone);
                dc.PushClip(cone);
                var stripe = new Pen(B(0xFF, 0xD2, 0x3F), s * .07);
                dc.DrawLine(stripe, new Point(-rx * .4, -ry * 1.0), new Point(rx * .4, -ry * 1.18));
                dc.DrawLine(stripe, new Point(-rx * .4, -ry * 1.35), new Point(rx * .4, -ry * 1.53));
                dc.Pop();
                dc.DrawEllipse(B(0xFF, 0xD2, 0x3F), null, new Point(0, -ry * 1.85), s * .07, s * .07);
                dc.Pop();
                break;
            }
            case "crown":
                dc.DrawGeometry(B(0xFF, 0xC2, 0x33), line, Poly(new Point(-rx * .52, -ry * .72), new Point(-rx * .58, -ry * 1.3), new Point(-rx * .26, -ry * 1.02), new Point(0, -ry * 1.42), new Point(rx * .26, -ry * 1.02), new Point(rx * .58, -ry * 1.3), new Point(rx * .52, -ry * .72)));
                dc.DrawEllipse(B(0xFF, 0x4D, 0x6D), null, new Point(0, -ry * .9), s * .05, s * .05);
                dc.DrawEllipse(B(0x3D, 0xE8, 0xFF), null, new Point(-rx * .32, -ry * .86), s * .035, s * .035);
                dc.DrawEllipse(B(0x3D, 0xE8, 0xFF), null, new Point(rx * .32, -ry * .86), s * .035, s * .035);
                break;
            case "santa":
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(-rx * .86, -ry * .45), true, true);
                    c.BezierTo(new Point(-rx * .7, -ry * 1.55), new Point(rx * .5, -ry * 1.6), new Point(rx * 1.05, -ry * .95), true, true);
                    c.BezierTo(new Point(rx * .7, -ry * 1.1), new Point(rx * .8, -ry * .7), new Point(rx * .86, -ry * .45), true, true);
                }
                dc.DrawGeometry(B(0xE0, 0x31, 0x3B), line, g);
                dc.DrawRoundedRectangle(B(0xFA, 0xFA, 0xFA), line, new Rect(-rx * .95, -ry * .6, rx * 1.9, ry * .3), ry * .15, ry * .15);
                dc.DrawEllipse(B(0xFA, 0xFA, 0xFA), line, new Point(rx * 1.05, -ry * .95), s * .09, s * .09);
                break;
            }
            case "witch":
            {
                var purple = B(0x3B, 0x2A, 0x5C);
                dc.DrawEllipse(purple, line, new Point(0, -ry * .66), rx * 1.22, ry * .16);
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(-rx * .5, -ry * .68), true, true);
                    c.BezierTo(new Point(-rx * .3, -ry * 1.2), new Point(0, -ry * 1.5), new Point(rx * .42, -ry * 1.7), true, true);
                    c.BezierTo(new Point(rx * .2, -ry * 1.3), new Point(rx * .4, -ry * 1.0), new Point(rx * .5, -ry * .68), true, true);
                }
                dc.DrawGeometry(purple, line, g);
                dc.DrawRectangle(B(0xF7, 0xB3, 0x2B), null, new Rect(-rx * .46, -ry * .86, rx * .94, ry * .13));
                break;
            }
        }
        dc.Pop(); dc.Pop();
    }

    /// <summary>
    /// The visor look (Neon, in the notch): no body at all, the notch itself is its face. Two glowing eyes behind
    /// scanlines, splitting into magenta and cyan when it glitches.
    /// </summary>
    void DrawVisor(DrawingContext dc, double rx, double ry)
    {
        double s = _s;
        var ink = Mix(_accent, _col, Math.Min(1, _tint * 1.3));
        var glow = new Pen(Br(A(ink, .4)), Math.Max(2, s * .17)) { LineJoin = PenLineJoin.Round };
        dc.PushTransform(new ScaleTransform(1.28, 1.28));
        if (_neon && _t < _glitchUntil)
        {
            dc.PushTransform(new TranslateTransform(-s * .09, 0));
            DrawEyes(dc, rx, ry, Br(A(Color.FromRgb(0xFF, 0x2B, 0xD6), .75)), null);
            dc.Pop();
            dc.PushTransform(new TranslateTransform(s * .09, 0));
            DrawEyes(dc, rx, ry, Br(A(Color.FromRgb(0x3D, 0xE8, 0xFF), .75)), null);
            dc.Pop();
        }
        DrawEyes(dc, rx, ry, Br(ink), glow);
        DrawMouth(dc, rx, ry, ink);
        // cheeks: two little pink dashes
        double blush = _v[Blush];
        if (blush > .02)
        {
            var pen = new Pen(Br(A(Color.FromRgb(0xFF, 0x5F, 0xA2), .9 * blush)), Math.Max(.8, s * .06)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double shift = Math.Sin(_yaw) * rx * .8;
            foreach (int side in new[] { -1, 1 })
                dc.DrawLine(pen, new Point(side * rx * .62 + shift - s * .07, ry * .34), new Point(side * rx * .62 + shift + s * .05, ry * .28));
        }
        dc.Pop();
        // scanlines across it
        var line = Br(A(Colors.Black, .5));
        for (double y = -ry * 1.3; y < ry * 1.3; y += 1.7)
            dc.DrawRectangle(line, null, new Rect(-rx * 1.4, y, rx * 2.8, .6));
    }

    Eye CurrentEye()
    {
        if (_eye is { } e) return e;
        return _mood switch { Mood.Error => Eye.Flat, Mood.Sleep => Eye.Closed, Mood.Music => Eye.Happy, _ => Eye.Pill };
    }

    void DrawEyes(DrawingContext dc, double rx, double ry, Brush ink, Pen? glow)
    {
        var eye = CurrentEye();
        double s = _s;
        foreach (int side in new[] { -1, 1 })
        {
            // the eyes sit on a round face: they slide toward where it looks, and narrow as they turn away
            double a = side * .36 + _yaw, p = -.1 + _pitch;
            double cp = Math.Cos(p);
            if (Math.Cos(a) * cp <= .04) continue;
            double x = Math.Sin(a) * cp * rx, y = -Math.Sin(p) * ry;
            double fx = Math.Max(.18, Math.Cos(a)), fy = Math.Max(.18, cp);
            double ew = s * .22 * _v[Es], eh = s * .28 * _v[Es];
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new ScaleTransform(fx, fy));
            DrawEye(dc, eye, ew, eh, side, ink, glow);
            dc.Pop(); dc.Pop();
        }
    }

    void DrawEye(DrawingContext dc, Eye eye, double ew, double eh, int side, Brush ink, Pen? glow)
    {
        void Fill(Geometry g) { if (glow != null) dc.DrawGeometry(null, glow, g); dc.DrawGeometry(ink, null, g); }
        void Stroke(Geometry g, double width)
        {
            if (glow != null) dc.DrawGeometry(null, new Pen(glow.Brush, width + glow.Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g);
            dc.DrawGeometry(null, new Pen(ink, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g);
        }
        Geometry Pill(double w, double h) { double r = Math.Min(w, h) / 2; return new RectangleGeometry(new Rect(-w / 2, -h / 2, w, h), r, r); }
        Geometry Curve(double w, double y0, double bend)
        {
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(new Point(-w, y0), false, false); c.QuadraticBezierTo(new Point(0, y0 + bend), new Point(w, y0), true, true); }
            return g;
        }
        switch (eye)
        {
            case Eye.Pill: Fill(Pill(ew, Math.Max(eh * _v[Open], ew * .3))); break;
            case Eye.Wide: Fill(Pill(ew * 1.15, Math.Max(eh * 1.12 * _v[Open], ew * .3))); break;
            case Eye.Dot: Fill(new EllipseGeometry(new Point(0, 0), ew * .42, ew * .42)); break;
            case Eye.Happy: Stroke(Curve(ew * .62, eh * .14, -eh * .62), ew * .42); break;
            case Eye.Closed: Stroke(Curve(ew * .6, -eh * .06, eh * .4), ew * .34); break;
            case Eye.Flat: Fill(Pill(ew * 1.4, ew * .38)); break;
            case Eye.Chevron:
            {
                // > <: squeezed shut
                var g = new StreamGeometry();
                double d = side; // the left eye points right, the right eye points left
                using (var c = g.Open()) { c.BeginFigure(new Point(-d * ew * .42, -eh * .34), false, false); c.LineTo(new Point(d * ew * .42, 0), true, true); c.LineTo(new Point(-d * ew * .42, eh * .34), true, true); }
                Stroke(g, ew * .32);
                break;
            }
            case Eye.Heart:
                dc.DrawGeometry(Br(Color.FromRgb(0xFF, 0x4D, 0x6D)), null, HeartGeometry(ew * 1.25));
                break;
            case Eye.Star:
            {
                var star = StarGeometry(ew * 1.05, ew * .46);
                dc.PushTransform(new RotateTransform(_t * 90 * side));
                dc.DrawGeometry(Br(Color.FromRgb(0xFF, 0xC2, 0x33)), null, star);
                dc.Pop();
                break;
            }
            case Eye.Spiral:
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                    for (double r = 0; r < 4.4 * Math.PI; r += .25)
                    {
                        double l = ew * .05 + r * ew * .055, u = r + _t * 9 * side;
                        var pt = new Point(Math.Cos(u) * l, Math.Sin(u) * l);
                        if (r == 0) c.BeginFigure(pt, false, false); else c.LineTo(pt, true, true);
                    }
                Stroke(g, ew * .2);
                break;
            }
            case Eye.Tired:
                Fill(new RectangleGeometry(new Rect(-ew / 2, -eh * .02, ew, eh * .4), ew / 2, ew / 2));
                Fill(new RectangleGeometry(new Rect(-ew * .62, -eh * .1, ew * 1.24, eh * .2), eh * .1, eh * .1));
                break;
            case Eye.Wink:
                if (side < 0) Fill(Pill(ew, Math.Max(eh * _v[Open], ew * .3)));
                else Stroke(Curve(ew * .62, eh * .14, -eh * .62), ew * .42);
                break;
            case Eye.Sad:
            {
                // half-shut, the inner corners lifted: sad
                dc.PushTransform(new RotateTransform(side * 18));
                Fill(new RectangleGeometry(new Rect(-ew / 2, -eh * .05, ew, Math.Max(eh * .5 * _v[Open], ew * .3)), ew / 2, ew / 2));
                dc.Pop();
                break;
            }
        }
    }

    void DrawMouth(DrawingContext dc, double rx, double ry, Color ink)
    {
        double s = _s, x = Math.Sin(_yaw) * rx * .75, y = ry * .4 - _pitch * ry * .5;
        var brush = Br(ink);
        if (_mouth == Mouth.None)
        {
            // in a call: talking, the mouth opens with the voice; muted, it's zipped
            if (_mood == Mood.Talk)
                dc.DrawEllipse(brush, null, new Point(x, y + s * .03), s * (.06 + .02 * _level), s * (.015 + .09 * _level));
            else if (_mood == Mood.Muted)
            {
                var pen = new Pen(brush, Math.Max(.9, s * .045)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                double w = s * .11;
                dc.DrawLine(pen, new Point(x - w, y + s * .03), new Point(x + w, y + s * .03));
                for (int i = -1; i <= 1; i++) dc.DrawLine(pen, new Point(x + i * w * .55, y - s * .005), new Point(x + i * w * .55, y + s * .065));
            }
            return;
        }
        switch (_mouth)
        {
            case Mouth.Smile:
            {
                var g = new StreamGeometry();
                double m = s * .09;
                using (var c = g.Open()) { c.BeginFigure(new Point(x - m, y), false, false); c.QuadraticBezierTo(new Point(x, y + m * 1.1), new Point(x + m, y), true, true); }
                dc.DrawGeometry(null, new Pen(brush, Math.Max(1, s * .055)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
                break;
            }
            case Mouth.O: dc.DrawEllipse(brush, null, new Point(x, y + s * .03), s * .055, s * .07); break;
            case Mouth.BigO: dc.DrawEllipse(brush, null, new Point(x, y + s * .04), s * .08, s * .11); break;
            case Mouth.Frown:
            {
                var g = new StreamGeometry();
                double m = s * .08;
                using (var c = g.Open()) { c.BeginFigure(new Point(x - m, y + m * .7), false, false); c.QuadraticBezierTo(new Point(x, y - m * .4), new Point(x + m, y + m * .7), true, true); }
                dc.DrawGeometry(null, new Pen(brush, Math.Max(1, s * .05)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
                break;
            }
        }
    }

    void DrawBadge(DrawingContext dc, double x, double y)
    {
        double s = _s, bw = s * .62, bh = s * .3;
        dc.DrawRoundedRectangle(Br(Colors.Black), null, new Rect(x - bw / 2 - 1.2, y - bh / 2 - 1.2, bw + 2.4, bh + 2.4), bh / 2 + 1.2, bh / 2 + 1.2);
        dc.DrawRoundedRectangle(Br(_col), null, new Rect(x - bw / 2, y - bh / 2, bw, bh), bh / 2, bh / 2);
        for (int i = 0; i < 3; i++)
        {
            double ph = ((_t * 2.4 - i * .22) % 1 + 1) % 1, r = s * .048 * (1 + .45 * Math.Max(0, Math.Sin(ph * Math.PI * 2)));
            dc.DrawEllipse(Brushes.White, null, new Point(x + (i - 1) * s * .16, y), r, r);
        }
    }

    void DrawBits(DrawingContext dc, double cx, double cy)
    {
        if (_bits.Count == 0) return;
        double s = _s;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var b in _bits)
        {
            if (b.Age <= 0) continue;
            double o = b.Age / b.Life, alpha = o < .2 ? o / .2 : 1 - (o - .2) / .8;
            double x = cx + (b.X + b.Vx * b.Age) * s * 1.3, y = cy + (b.Y + b.Vy * b.Age) * s * 1.3, u = Math.Max(2.2, s * b.Size * (1 + o * .4));
            dc.PushOpacity(Math.Clamp(alpha, 0, 1));
            dc.PushTransform(new TranslateTransform(x, y));
            switch (b.Kind)
            {
                case Kind.Heart:
                    dc.PushTransform(new RotateTransform(Math.Sin(b.Age * 6) * 17));
                    dc.DrawGeometry(Br(Color.FromRgb(0xFF, 0x4D, 0x6D)), null, HeartGeometry(u));
                    dc.Pop();
                    break;
                case Kind.Star:
                    dc.PushTransform(new RotateTransform((b.Rot + b.Age * 2) * 57.3));
                    dc.DrawGeometry(Br(Color.FromRgb(0xFF, 0xC2, 0x33)), null, StarGeometry(u, u * .45));
                    dc.Pop();
                    break;
                case Kind.Spark:
                    dc.PushTransform(new RotateTransform(b.Rot * 57.3));
                    dc.DrawGeometry(Br(_neon ? _accent : Colors.White), null, StarGeometry(u * .85, u * .2, 4));
                    dc.Pop();
                    break;
                case Kind.Tear:
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open()) { c.BeginFigure(new Point(0, -u), true, true); c.QuadraticBezierTo(new Point(u * .8, u * .2), new Point(0, u * .6), true, true); c.QuadraticBezierTo(new Point(-u * .8, u * .2), new Point(0, -u), true, true); }
                    dc.DrawGeometry(Br(Color.FromRgb(0x6F, 0xC3, 0xFF)), null, g);
                    break;
                }
                case Kind.Bang:
                {
                    var text = new FormattedText("!", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal), u * 2.2,
                        Br(_neon ? Color.FromRgb(0xFE, 0xE8, 0x01) : Color.FromRgb(0xFF, 0xC2, 0x33)), dip);
                    dc.DrawText(text, new Point(-text.Width / 2, -text.Height / 2));
                    break;
                }
                case Kind.Sweat:
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open()) { c.BeginFigure(new Point(0, -u), true, true); c.QuadraticBezierTo(new Point(u * .8, u * .2), new Point(0, u * .6), true, true); c.QuadraticBezierTo(new Point(-u * .8, u * .2), new Point(0, -u), true, true); }
                    dc.DrawGeometry(Br(Color.FromRgb(0x7C, 0xC7, 0xFF)), null, g);
                    break;
                }
                case Kind.Z or Kind.Note:
                {
                    var text = new FormattedText(b.Kind == Kind.Z ? "z" : "♪", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), u * 1.9,
                        Br(_neon ? _accent : Color.FromRgb(0xD1, 0xDB, 0xEB)), dip);
                    dc.DrawText(text, new Point(-text.Width / 2, -text.Height / 2));
                    break;
                }
            }
            dc.Pop(); dc.Pop();
        }
    }

    static Geometry HeartGeometry(double e)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, e * .38), true, true);
            c.BezierTo(new Point(-e * 1.05, -e * .15), new Point(-e * .5, -e * .95), new Point(0, -e * .38), true, true);
            c.BezierTo(new Point(e * .5, -e * .95), new Point(e * 1.05, -e * .15), new Point(0, e * .38), true, true);
        }
        return g;
    }

    static Geometry StarGeometry(double outer, double inner, int points = 5)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
            for (int i = 0; i < points * 2; i++)
            {
                double r = i % 2 == 1 ? inner : outer, a = -Math.PI / 2 + i * Math.PI / points;
                var pt = new Point(Math.Cos(a) * r, Math.Sin(a) * r);
                if (i == 0) c.BeginFigure(pt, true, true); else c.LineTo(pt, true, true);
            }
        return g;
    }

    // ───────────────────────────── a sheet of all its faces (DYNAMICNOTCH_FACESHEET=file.png) ─────────────────────────────

    /// <summary>Every mood and feeling, light and Neon, at notch size and big, into one picture (to look them over).</summary>
    public static void RenderSheet(string path)
    {
        var accent = Color.FromRgb(0x3D, 0xE8, 0xFF);
        var rows = new StackPanel { Background = Brushes.Black };
        foreach (bool neon in new[] { false, true })
            foreach (double size in new[] { 15.0, 34.0 })
            {
                var row = new WrapPanel { Margin = new Thickness(8) };
                foreach (var mood in Enum.GetValues<Mood>()) row.Children.Add(Tile(mood.ToString(), neon, size, f => f.SetMood(mood), 0.5));
                foreach (var emote in Enum.GetValues<Emote>()) row.Children.Add(Tile(emote.ToString(), neon, size, f => f.Play(emote, 3), emote is Emote.Hello or Emote.Done ? 0.16 : emote == Emote.Wave ? 0.9 : 0.45));
                foreach (var hat in Outfits.Skip(1).Take(0)) row.Children.Add(Tile(hat, neon, size, f => f.SetOutfit(hat, false), 0.1));
                row.Children.Add(Tile("music+hat", neon, size, f => { f.SetOutfit("crown", false); f.SetMood(Mood.Music); }, 0.8));
                if (neon) foreach (var v in new[] { "visor", "visor-love", "visor-music", "visor-sleep" })
                    row.Children.Add(Tile(v, neon, size, f => { f.Visor = true; if (v == "visor-love") f.Play(Emote.Love, 3); if (v == "visor-music") f.SetMood(Mood.Music); if (v == "visor-sleep") f.SetMood(Mood.Sleep); }, 0.5));
                rows.Children.Add(row);
            }
        rows.Measure(new Size(1900, double.PositiveInfinity));
        rows.Arrange(new Rect(rows.DesiredSize));
        rows.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(rows.ActualWidth * 2), (int)Math.Ceiling(rows.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(rows);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var file = System.IO.File.Create(path);
        enc.Save(file);

        FrameworkElement Tile(string name, bool neon, double size, Action<NotchFace> set, double at)
        {
            var face = new NotchFace(size) { Neon = neon, Accent = accent, HorizontalAlignment = HorizontalAlignment.Center };
            set(face);
            for (double t = 0; t < at; t += 1 / 60.0) face.Step(1 / 60.0);
            var box = new StackPanel { Width = Math.Max(64, size * 2.2), Margin = new Thickness(2, 4, 2, 4) };
            box.Children.Add(new Border { Background = Br(Color.FromRgb(0x05, 0x05, 0x06)), Padding = new Thickness(4), Child = face, CornerRadius = new CornerRadius(6) });
            box.Children.Add(new TextBlock { Text = name, Foreground = Brushes.Gray, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center });
            return box;
        }
    }
}
