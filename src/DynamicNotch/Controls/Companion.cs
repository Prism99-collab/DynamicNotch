using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Media.Media3D;

namespace DynamicNotch.Controls;

/// <summary>
/// The notch's companion: a small hacker (our own character) on a neon swing that lowers from behind the notch.
/// She is real 3D: a little rigged figure built from code (no picture files), lit by two neon lights, drawn by the
/// graphics chip. One pose is worked out per frame (a few dozen numbers), and only while she is out: gone, she costs
/// nothing at all. She never takes the mouse: everything under her keeps working.
/// </summary>
public sealed class Companion : Grid
{
    public enum Act { Chill, Music, Sleep, Strain, Cheer, Hack, Scan, Point, Pump, Ears, Tired, Stretch, Yawn, Spin, Nod }
    public enum Entrance { Drop, Glitch, Peek }

    /// <summary>Her box, and where in it the swing's ropes meet the notch's bottom edge (everything above is cut).</summary>
    public const double BoxWidth = 148, BoxHeight = 148, AnchorY = 12;
    const double Ppu = 17;                 // screen units per model unit
    const double Full = 4.55, Hidden = -1.9; // rope length: sitting below the notch / tucked away behind it

    struct Pose
    {
        public double Len, Sway, Twist, Bob, Yaw, Pitch, Roll, ShinL, ShinR, Eye, Mouth, Pulse, Hot, Deck, Visor;
        public Vector3D HandL, HandR;

        public static Pose Lerp(in Pose a, in Pose b, double k) => new()
        {
            Len = L(a.Len, b.Len, k), Sway = L(a.Sway, b.Sway, k), Twist = L(a.Twist, b.Twist, k), Bob = L(a.Bob, b.Bob, k),
            Yaw = L(a.Yaw, b.Yaw, k), Pitch = L(a.Pitch, b.Pitch, k), Roll = L(a.Roll, b.Roll, k),
            ShinL = L(a.ShinL, b.ShinL, k), ShinR = L(a.ShinR, b.ShinR, k), Eye = L(a.Eye, b.Eye, k), Mouth = L(a.Mouth, b.Mouth, k),
            Pulse = L(a.Pulse, b.Pulse, k), Hot = L(a.Hot, b.Hot, k), Deck = L(a.Deck, b.Deck, k), Visor = L(a.Visor, b.Visor, k),
            HandL = a.HandL + (b.HandL - a.HandL) * k, HandR = a.HandR + (b.HandR - a.HandR) * k,
        };
        static double L(double a, double b, double k) => a + (b - a) * k;
    }

    // ───── the figure's measures (model units, the swing's bar at the origin, +Z toward you) ─────
    static readonly Point3D Neck = new(0, 1.55, 0), Head = new(0, 2.32, 0.05);
    static readonly Point3D ShoulderL = new(-0.6, 1.42, 0), ShoulderR = new(0.6, 1.42, 0);
    static readonly Point3D KneeL = new(-0.34, 0.14, 1.0), KneeR = new(0.34, 0.14, 1.0);
    static readonly Vector3D HoldL = new(-1.2, 1.74, 0.04), HoldR = new(1.2, 1.74, 0.04);
    const double UpperArm = 0.72, ForeArm = 0.68, Shin = 0.85, RopeX = 1.25;

    readonly Viewport3D _view = new() { IsHitTestVisible = false };
    readonly Canvas _fx = new() { IsHitTestVisible = false };
    readonly TextBlock[] _zs = new TextBlock[3];
    readonly ScaleTransform _scan = new(1, 1, BoxWidth / 2, AnchorY);
    readonly TranslateTransform _jitter = new();

    readonly MatrixTransform3D _swingT = new(), _headT = new(), _upperLT = new(), _foreLT = new(), _upperRT = new(), _foreRT = new(),
        _shinLT = new(), _shinRT = new(), _bootLT = new(), _bootRT = new();
    readonly TranslateTransform3D _seatT = new(), _bodyT = new(), _handLT = new(), _handRT = new();
    readonly ScaleTransform3D _ropeT = new(), _eyeT = new(1, 1, 1, 0, Head.Y - 0.12, 0), _mouthT = new(1, 1, 1, 0, Head.Y - 0.5, 0);
    readonly SolidColorBrush _glow = new(Colors.Cyan), _cup = new(Colors.Cyan), _deckGlass = new(Colors.Cyan) { Opacity = 0.16 };
    readonly ScaleTransform3D _deckT = new(0, 0, 0);
    // the swing: two ropes and the seat, one neon line whose colour keeps flowing (her colour, pink, violet, yellow).
    // Drawn flat behind her, so the colour changing never redraws her 3D picture
    readonly Canvas _swing = new() { IsHitTestVisible = false };
    readonly Polyline _swingLine = new() { StrokeThickness = 1.9, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    readonly Polyline _swingGlow = new() { StrokeThickness = 5, StrokeLineJoin = PenLineJoin.Round, Opacity = 0.32 };
    readonly SolidColorBrush _swingPaint = new(Colors.Cyan);
    static readonly Brush StrainRed = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x2B, 0x3A)));
    AnimationClock? _swingClock;
    Color _swingFor;
    double _camDist, _camY;
    static readonly Color Pink = Color.FromRgb(0xFF, 0x2B, 0xD6), Violet = Color.FromRgb(0x9D, 0x5C, 0xFF), Yellow = Color.FromRgb(0xFE, 0xE8, 0x01);

    /// <summary>The swing's colour flows (off on battery saving: it stays her colour).</summary>
    public bool Vivid { get; set; } = true;
    bool _vividWas = true;
    readonly ScaleTransform3D[] _code = new ScaleTransform3D[5];
    readonly DropShadowEffect _halo = new() { ShadowDepth = 0, BlurRadius = 9, Opacity = 0.42, Color = Colors.Cyan, RenderingBias = RenderingBias.Quality };

    Color _accent = Color.FromRgb(0x3D, 0xE8, 0xFF), _glowNow, _cupNow;
    static readonly Color HotRed = Color.FromRgb(0xFF, 0x2B, 0x3A);

    Act _act = Act.Chill;
    Entrance _how = Entrance.Drop;
    Pose _from, _shown;
    long _t0, _actAt, _phaseAt, _lastFrame;
    TimeSpan _lastRender;
    int _phase;           // 0 gone, 1 arriving / out, 2 leaving
    bool _fastLeave, _hooked;
    // between her moments she sits still, and a still picture costs nothing: no frames are asked for at all
    double _calm;
    bool _calmWanted, _wave = true;
    long _blinkAt, _lastTick;
    // moving along the notch to another spot: the swing trails behind and sways when she stops
    bool _traveling;
    double _tx0, _tx1, _travelSecs, _travelDir, _ty0, _ty1;
    long _riseAt = -1;
    long _travelAt, _arriveAt;
    // where she's looking: her head turns toward the pointer, eased, a few degrees at a time
    double _gazeYaw, _gazePitch, _gazeYawTo, _gazePitchTo;
    bool _gazeOnly;

    /// <summary>She has gone back behind the notch (after <see cref="Leave"/>).</summary>
    public event Action? Gone;

    public bool IsOut => _phase != 0;
    public bool Leaving => _phase == 2;
    public Act Current => _act;
    /// <summary>Frames a second she moves at; 0: every frame of the screen (60, 120, 144 Hz...), in step with it.</summary>
    public int Fps { get; set; }

    public Companion()
    {
        Width = BoxWidth;
        Height = BoxHeight;
        IsHitTestVisible = false;
        UseLayoutRounding = true; // her picture sits on whole screen pixels: not blurred by a half-pixel offset
        Visibility = Visibility.Collapsed;
        Clip = new RectangleGeometry(new Rect(-90, AnchorY, BoxWidth + 180, BoxHeight * 1.6)); // she comes from behind the notch
        Clip.Freeze();
        _view.RenderTransform = new TransformGroup { Children = { _scan, _jitter, _size } };
        _fx.RenderTransform = _size;
        _view.Effect = _halo; // a soft neon halo around her: she glows against any background
        _swing.RenderTransform = _view.RenderTransform;
        _swingLine.Stroke = _swingGlow.Stroke = _swingPaint;
        for (int i = 0; i < 4; i++) { _swingLine.Points.Add(default); _swingGlow.Points.Add(default); }
        _swing.Children.Add(_swingGlow);
        _swing.Children.Add(_swingLine);
        Children.Add(_swing); // behind her: she sits on it and holds it
        Children.Add(_view);
        Children.Add(_fx);
        for (int i = 0; i < _zs.Length; i++)
        {
            _zs[i] = new TextBlock { Text = "z", FontSize = 8 + i * 1.5, FontWeight = FontWeights.Bold, Foreground = _glow, Opacity = 0 };
            _fx.Children.Add(_zs[i]);
        }
        Build();
        _shown = Target(Act.Chill, 0);
        _shown.Len = Hidden;
        Apply(_shown);
    }

    public void SetAccent(Color c)
    {
        if (c == _accent && Vivid == _vividWas) return;
        _accent = c;
        _vividWas = Vivid;
        if (_phase != 0) SwingColours(true);
        if (_phase == 0) Paint(0, 0.4);
    }

    /// <summary>She comes out: lowered on her swing (boots first), or flickering into place like a hologram.</summary>
    public void Appear(Act act, Entrance how)
    {
        if (_phase == 1) { SetAct(act); return; }
        SwingColours(true);
        // drawn at exactly twice the screen's pixels, then halved: every screen pixel is the average of four, so
        // edges are smooth and nothing shimmers as she moves (an uneven ratio skips pixels)
        Sharpen();
        _act = act;
        _how = how;
        _phase = 1;
        _t0 = _actAt = _phaseAt = _lastTick = Now;
        _from = Target(act, 0);
        _calm = 0;
        _calmWanted = false;
        _wave = true;
        _blinkAt = 0;
        Visibility = Visibility.Visible;
        Hook(true);
        Frame(force: true);
    }

    public void SetAct(Act act)
    {
        if (act == _act || _phase != 1) return;
        _from = _shown;
        _act = act;
        _t0 = _actAt = Now; // each act starts from its beginning (the wave hello, the first beat)
        _calm = 0;
        _calmWanted = false;
        _wave = false;
        Wake();
    }

    public bool Traveling => _traveling || _arriveAt != 0 || _riseAt >= 0;

    // tucked: over a window she sits up behind the notch, only her boots dangling below its edge
    const double TuckLen = -0.12;
    double _tuck;
    bool _tucked;

    /// <summary>Over a window: she pulls herself up behind the notch, only her boots showing. Off: back down.</summary>
    /// <summary>Which way the notch's middle is from her: -1 left, +1 right (she points that way).</summary>
    public double Toward { get => _toward; set => _toward = value < 0 ? -1 : 1; }
    double _toward = -1;

    /// <summary>Her size: 0.85 small, 1 medium, 1.2 large (drawn sharp at every size).</summary>
    public double Size
    {
        get => _size.ScaleX;
        set { double v = Math.Clamp(value, 0.7, 1.4); if (Math.Abs(v - _size.ScaleX) < 0.001) return; _size.ScaleX = _size.ScaleY = v; Sharpen(); }
    }
    readonly ScaleTransform _size = new(1, 1, BoxWidth / 2, AnchorY);

    void Sharpen()
    {
        double sharp = 2 * VisualTreeHelper.GetDpi(this).DpiScaleX * _size.ScaleX;
        if (_view.CacheMode is not BitmapCache { } cache || Math.Abs(cache.RenderAtScale - sharp) > 0.01)
            _view.CacheMode = new BitmapCache(sharp);
    }

    public bool Tucked
    {
        get => _tucked;
        set { if (value == _tucked) return; _tucked = value; if (_phase == 1) Wake(); else _tuck = value ? 1 : 0; }
    }

    /// <summary>Where the pointer is from her (screen units): she turns her head toward it.</summary>
    public void LookAt(double dx, double dy)
    {
        _gazeYawTo = Math.Clamp(dx * 0.11, -38, 38);
        _gazePitchTo = Math.Clamp(dy * 0.07, -14, 18);
        if (_phase == 1 && (Math.Abs(_gazeYawTo - _gazeYaw) > 4 || Math.Abs(_gazePitchTo - _gazePitch) > 4)) Wake();
    }

    /// <summary>She moves along the notch to another spot (screen units, from the notch's centre): the swing trails.</summary>
    public void Slide(double toX, double toY)
    {
        if (_phase != 1 || RenderTransform is not TranslateTransform tt) return;
        if (Math.Abs(toY - tt.Y) >= 0.5)
        {
            // the notch got taller or shorter: her ropes stay on its edge, in step with its own spring
            _ty0 = tt.Y;
            _ty1 = toY;
            _riseAt = Now;
            Wake();
        }
        double d = toX - (_traveling ? _tx1 : tt.X);
        if (Math.Abs(d) < 1) return;
        d = toX - tt.X;
        _tx0 = tt.X;
        _tx1 = toX;
        _travelDir = Math.Sign(d);
        _travelSecs = 1.0 + Math.Abs(d) / 110;
        _travelAt = Now;
        _traveling = true;
        _arriveAt = 0;
        Wake();
    }

    /// <summary>Sitting still (once she has eased into it, she is a still picture: no frames, no cost).</summary>
    public bool Resting => _calmWanted;

    /// <summary>She eases into sitting still.</summary>
    public void Rest()
    {
        if (_phase != 1 || _calmWanted) return;
        _calmWanted = true;
        Wake();
    }

    /// <summary>A moment of life: she looks around and swings her legs, and may wave hello.</summary>
    public void Stir(bool wave)
    {
        if (_phase != 1) return;
        _from = _shown;
        _t0 = _actAt = Now;
        _calm = 0;
        _calmWanted = false;
        _wave = wave;
        Wake();
    }

    /// <summary>One blink (she does that even while sitting still).</summary>
    public void Blink()
    {
        if (_phase != 1 || _blinkAt != 0) return;
        _blinkAt = Now;
        Wake();
    }

    void Wake()
    {
        if (_hooked) return;
        _lastTick = Now;
        Hook(true);
    }

    /// <summary>Back up behind the notch. Fast: at once (the pointer came close, the notch is about to change).</summary>
    public void Leave(bool fast = false)
    {
        if (_phase == 0) return;
        if (_phase == 2) { if (fast && !_fastLeave) { _fastLeave = true; _phaseAt = Now; _leaveFrom = _shown.Len; } return; }
        _phase = 2;
        StopTravel();
        _fastLeave = fast;
        _phaseAt = Now;
        _leaveFrom = _shown.Len;
        Wake();
    }

    double _leaveFrom = Full;

    /// <summary>Gone this instant, no animation (the notch opened over her, a game started).</summary>
    public void HideNow()
    {
        if (_phase == 0) return;
        End(raise: true);
    }

    void StopTravel()
    {
        if (_riseAt >= 0 && RenderTransform is TranslateTransform ty) ty.Y = _ty1;
        _riseAt = -1;
        if (_traveling && RenderTransform is TranslateTransform tt) tt.X = _tx1;
        _traveling = false;
        _arriveAt = 0;
    }

    void End(bool raise)
    {
        SwingColours(false);
        StopTravel();
        _phase = 0;
        Hook(false);
        Visibility = Visibility.Collapsed;
        _view.Opacity = 1;
        _scan.ScaleY = 1;
        _jitter.X = 0;
        foreach (var z in _zs) z.Opacity = 0;
        if (raise) Gone?.Invoke();
    }

    /// <summary>For looking at her without running her: one act at one moment, fully out.</summary>
    public void Freeze(Act act, double seconds)
    {
        Visibility = Visibility.Visible;
        var p = Target(act, seconds);
        _act = act;
        Apply(p);
        Extras(p, act == Act.Sleep ? 1 : 0, seconds);
    }

    static long Now => Environment.TickCount64;

    void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        // once per frame of the screen (WPF can call twice for one frame)
        if (e is RenderingEventArgs r) { if (r.RenderingTime == _lastRender) return; _lastRender = r.RenderingTime; }
        Frame(false);
    }

    void Frame(bool force)
    {
        long now = Now;
        if (!force && Fps > 0 && now - _lastFrame < 1000 / Math.Max(10, Fps) - 3) return; // a slower pace only when asked (battery, sleep)
        if (!force && _gazeOnly && now - _lastFrame < 30) return; // only her head turning: 30 frames are plenty
        _lastFrame = now;
        if (_phase == 0) return;

        double t = (now - _t0) / 1000.0, sinceAct = (now - _actAt) / 1000.0, since = (now - _phaseAt) / 1000.0;
        var p = Target(_act, t, _wave, _toward);
        double blend = Smooth(Math.Min(1, sinceAct / 0.3));
        if (blend < 1) p = Pose.Lerp(_from, p, blend);
        double opacity = 1, scan = 1, jitter = 0;

        double dt = Math.Min(0.1, (now - _lastTick) / 1000.0);
        _lastTick = now;
        _calm = Math.Clamp(_calm + (_calmWanted ? dt : -dt) / 0.8, 0, 1);
        if (_calm > 0 && _act == Act.Chill) p = Pose.Lerp(p, Still(), Smooth(_calm));
        if (_blinkAt != 0)
        {
            double b = (now - _blinkAt) / 1000.0;
            if (b >= 0.18) _blinkAt = 0;
            else p.Eye = Math.Min(p.Eye, 0.1 + 0.9 * Math.Abs(b - 0.09) / 0.09);
        }

        if (_phase == 1)
        {
            if (_how == Entrance.Drop)
            {
                double u = Math.Min(1, since / 0.8);
                p.Len = Hidden + (Full - Hidden) * BackOut(u);
                p.Sway += 7 * Math.Exp(-1.3 * since) * Math.Sin(since * 4.2); // the swing still carries the drop
            }
            else if (_how == Entrance.Peek)
            {
                // she peeks out first: lowers a little, looks left and right, then drops down to her seat
                const double Peek = Full - 1.7;
                if (since < 0.45) p.Len = Hidden + (Peek - Hidden) * (1 - Math.Pow(1 - since / 0.45, 3));
                else if (since < 1.45)
                {
                    double u = (since - 0.45) / 1.0;
                    p.Len = Peek + 0.06 * Math.Sin(u * Math.PI * 2);
                    p.Yaw = 34 * Math.Sin(u * Math.PI * 2);
                    p.Pitch = -6;
                    p.Eye = 1.1;
                }
                else
                {
                    double u = Math.Min(1, (since - 1.45) / 0.6);
                    p.Len = Peek + (Full - Peek) * BackOut(u);
                    p.Sway += 5 * Math.Exp(-1.6 * (since - 1.45)) * Math.Sin((since - 1.45) * 4.4);
                }
            }
            else if (since < 0.5)
            {
                double u = since / 0.5;
                (opacity, scan, jitter) = Flicker(u);
            }
        }
        else
        {
            if (_how == Entrance.Glitch && !_fastLeave)
            {
                double u = 1 - Math.Min(1, since / 0.3);
                (opacity, scan, jitter) = Flicker(u);
                if (u <= 0) { _shown = p; End(raise: true); return; }
            }
            else
            {
                double dip = _fastLeave ? 0 : 0.16, rise = _fastLeave ? 0.2 : 0.36;
                if (since < dip) p.Len = _leaveFrom + 0.3 * Math.Sin(Math.PI * since / dip / 2);
                else
                {
                    double u = Math.Min(1, (since - dip) / rise);
                    double start = _leaveFrom + (_fastLeave ? 0 : 0.3);
                    p.Len = start + (Hidden - start) * u * u * u;
                    if (u >= 1) { _shown = p; End(raise: true); return; }
                }
            }
        }

        if (_phase == 1) Travel(ref p, now, t);

        // tucked up behind the notch, or back down: eased, the swing bouncing a little as it settles
        double tuckTo = _tucked ? 1 : 0;
        _tuck += (tuckTo - _tuck) * (1 - Math.Exp(-dt * 7));
        if (Math.Abs(tuckTo - _tuck) < 0.003) _tuck = tuckTo;
        if (_tuck > 0 && _phase == 1)
        {
            double k = Smooth(_tuck);
            p.Len += (TuckLen - p.Len) * k;
            p.ShinL += 10 * k * Math.Sin(t * 2.2);          // up there her boots swing idly
            p.ShinR += 10 * k * Math.Sin(t * 2.2 + 1.3);
        }

        // her gaze, eased toward the pointer (asleep or in a strain she doesn't look; hacking, only half)
        double look = _act switch
        {
            Act.Sleep or Act.Strain or Act.Point or Act.Ears or Act.Stretch or Act.Spin or Act.Nod => 0,
            Act.Hack or Act.Yawn or Act.Tired => 0.35, Act.Pump => 0.5, _ => 1,
        };
        double ease = 1 - Math.Exp(-dt * 7);
        _gazeYaw += (_gazeYawTo * look - _gazeYaw) * ease;
        _gazePitch += (_gazePitchTo * look - _gazePitch) * ease;
        bool gazeSettled = Math.Abs(_gazeYawTo * look - _gazeYaw) < 0.4 && Math.Abs(_gazePitchTo * look - _gazePitch) < 0.4;
        p.Yaw += _gazeYaw * (1 - Math.Min(1, Math.Abs(p.Yaw) / 60));
        p.Pitch += _gazePitch;
        if (_act == Act.Scan && _phase == 1)
        {
            double gy = _gazeYaw * Math.PI / 180, gp = _gazePitch * Math.PI / 180;
            var aim = new Vector3D(0.6 + 1.3 * Math.Sin(gy) * Math.Cos(gp), 1.55 - 1.3 * Math.Sin(gp), 1.3 * Math.Cos(gy) * Math.Cos(gp));
            p.HandR += (aim - p.HandR) * blend; // she points at your pointer
        }


        _shown = p;
        double settleAfter = _how == Entrance.Peek ? 2.1 : 1;
        bool still = _phase == 1 && since > settleAfter && blend >= 1 && _blinkAt == 0 && !Traveling;
        bool settled = still && gazeSettled && _tuck == tuckTo && !(_tucked && _act == Act.Chill && _calm < 1);
        _gazeOnly = still && !gazeSettled && _act == Act.Chill && _calm >= 1;
        bool asleep = settled && _act == Act.Sleep && sinceAct > 1.5; // asleep she doesn't move: only her z's drift
        if (!asleep) Apply(p);
        if (settled && _act == Act.Chill && _calm >= 1) Hook(false); // sitting still: a still picture, until her next moment
        if (_view.Opacity != opacity) _view.Opacity = _swing.Opacity = opacity;
        if (_scan.ScaleY != scan) _scan.ScaleY = scan;
        if (_jitter.X != jitter) _jitter.X = jitter;
        Extras(p, _act == Act.Sleep && _phase == 1 ? blend : 0, t);
    }

    void Travel(ref Pose p, long now, double t)
    {
        if (_riseAt >= 0 && RenderTransform is TranslateTransform ty)
        {
            double r = Math.Min(1, (now - _riseAt) / 300.0);
            ty.Y = _ty0 + (_ty1 - _ty0) * (1 - Math.Pow(1 - r, 3));
            if (r >= 1) _riseAt = -1;
        }
        if (_traveling && RenderTransform is TranslateTransform tt)
        {
            double u = Math.Min(1, (now - _travelAt) / 1000.0 / _travelSecs);
            double e = u * u * u * (u * (u * 6 - 15) + 10);           // smootherstep
            double v = 30 * u * u * (1 - u) * (1 - u) / 1.875;       // its speed, 0..1
            tt.X = _tx0 + (_tx1 - _tx0) * e;
            p.Sway -= _travelDir * 13 * v;                           // the seat trails behind the ropes
            p.Yaw = p.Yaw * (1 - v) + _travelDir * 34 * v;           // she looks where she's going
            p.Roll -= _travelDir * 5 * v;
            p.ShinL += 22 * v * Math.Sin(t * 11);
            p.ShinR -= 22 * v * Math.Sin(t * 11);
            p.Mouth = Math.Max(p.Mouth, 1 + 0.6 * v);
            p.Len -= 0.75 * v;                                       // carried along, the swing lifts
            if (u >= 1) { _traveling = false; _arriveAt = now; }
        }
        else if (_arriveAt != 0)
        {
            double a = (now - _arriveAt) / 1000.0;                   // stopped: the swing rocks on, and settles
            p.Sway += _travelDir * 7 * Math.Exp(-1.7 * a) * Math.Sin(a * 4.6);
            p.Len += 0.16 * Math.Exp(-3 * a) * Math.Sin(a * 9);      // and bounces once on the rope
            if (a > 2.6) _arriveAt = 0;
        }
    }

    /// <summary>A hologram switching on: it scans in from the top, flickering and twitching sideways.</summary>
    static (double Opacity, double Scan, double Jitter) Flicker(double u)
    {
        u = Math.Clamp(u, 0, 1);
        ReadOnlySpan<double> steps = stackalloc double[] { 0.0, 0.75, 0.25, 0.9, 0.4, 1, 0.65, 1 };
        int i = Math.Min(steps.Length - 1, (int)(u * steps.Length));
        double jitter = (1 - u) * 5 * Math.Sin(u * 61);
        return (u >= 1 ? 1 : steps[i], 0.06 + 0.94 * (1 - Math.Pow(1 - u, 3)), u >= 1 ? 0 : Math.Round(jitter));
    }

    static double Smooth(double x) => x * x * (3 - 2 * x);
    static double BackOut(double x) { const double c = 1.4; double y = x - 1; return 1 + (c + 1) * y * y * y + c * y * y; }
    static double Wave(double t, double period, double phase = 0) => Math.Sin(t * 2 * Math.PI / period + phase);

    static double Blink(double t, double every = 3.4)
    {
        double ph = t % every;
        return ph < 0.16 ? 0.1 + 0.9 * Math.Abs(ph - 0.08) / 0.08 : 1;
    }

    /// <summary>What she is doing, as a pose at a moment in time.</summary>
    static Pose Still() => new() { Len = Full, Eye = 1, Mouth = 1, Pulse = 0.35, HandL = HoldL, HandR = HoldR, Roll = 3, Yaw = -7, Twist = 5 };

    static Pose Target(Act act, double t, bool wave = true, double toward = -1)
    {
        var p = new Pose { Len = Full, Eye = 1, Mouth = 1, Pulse = 0.35, HandL = HoldL, HandR = HoldR };
        switch (act)
        {
            case Act.Scan:
            {
                // you're on the mouse: she follows the pointer with her eyes and points at it (aimed in Frame)
                p.Pitch = -3;
                p.Sway = 1.2 * Wave(t, 4.2);
                p.Twist = 3 * Wave(t, 7);
                p.ShinL = 6 * Wave(t, 2.1);
                p.ShinR = 6 * Wave(t, 2.1, 1.1);
                p.Eye = Math.Min(Blink(t, 4.4), 1.05);
                p.Mouth = 0.9;
                p.Pulse = 0.75 + 0.25 * Math.Sin(t * 5);
                p.HandR = new Vector3D(1.1, 2.0, 0.9);
                break;
            }
            case Act.Hack:
            {
                // a holo-deck open on her lap, both hands tapping away, eyes on her work
                double tl = Math.Max(0, Math.Sin(t * 2 * Math.PI * 4.6)), tr = Math.Max(0, Math.Sin(t * 2 * Math.PI * 4.6 + 2.2));
                double drift = 0.05 * Math.Sin(t * 1.3);
                p.Deck = 1;
                p.HandL = new Vector3D(-0.3 + drift, 1.08 + 0.09 * tl, 1.32);
                p.HandR = new Vector3D(0.3 + drift, 1.08 + 0.09 * tr, 1.32);
                p.Pitch = 17 + 2 * Math.Sin(t * 0.9);
                p.Yaw = 9 * Math.Sin(t * 0.7) + 4 * Math.Sin(t * 3.1);
                p.Roll = 4 * Math.Sin(t * 0.5);
                p.Sway = 1.6 * Wave(t, 3.8);
                p.Twist = 4 * Wave(t, 7);
                p.ShinL = 9 * Wave(t, 1.6);
                p.ShinR = 9 * Wave(t, 1.6, 1.4);
                p.Eye = Math.Min(Blink(t, 3.9), 0.72);
                p.Mouth = 0.85;
                p.Pulse = 0.6 + 0.4 * Math.Sin(t * 9);
                break;
            }
            case Act.Chill:
            {
                p.Sway = 3.5 * Wave(t, 3.4);
                p.Twist = 11 * Wave(t, 5.6, 0.6);
                p.ShinL = 20 * Wave(t, 1.25);
                p.ShinR = 20 * Wave(t, 1.25, Math.PI);
                p.Yaw = 22 * Math.Sin(t * 0.8);
                p.Pitch = 4 * Math.Sin(t * 0.53);
                p.Roll = 6 * Math.Sin(t * 0.67 + 1);
                p.Bob = 0.025 * Wave(t, 2.6);
                p.Eye = Blink(t);
                // a little wave hello, once
                double w = Smooth(Math.Clamp((t - 2.0) / 0.35, 0, 1)) * Smooth(Math.Clamp((4.4 - t) / 0.35, 0, 1));
                if (w > 0 && wave)
                {
                    var up = new Vector3D(1.5 + 0.3 * Math.Sin(t * 2 * Math.PI * 2.6), 2.95, 0.5);
                    p.HandR = HoldR + (up - HoldR) * w;
                    p.Roll += 7 * w;
                    p.Yaw *= 1 - w;
                    p.Mouth = 1 + 1.2 * w;
                }
                break;
            }
            case Act.Music:
            {
                const double beat = 1.9; // per second
                double b = t * beat * Math.PI;
                p.Sway = 4.5 * Wave(t, 3.4);
                p.Twist = 9 * Wave(t, 6.2, 0.4);
                p.Pitch = 2 + 9 * Math.Pow(Math.Sin(b), 2);
                p.Roll = 7 * Math.Sin(b);
                p.Yaw = 10 * Wave(t, 4.2);
                p.Bob = 0.05 * Math.Sin(b * 2);
                p.ShinL = 24 * Math.Sin(b);
                p.ShinR = -24 * Math.Sin(b);
                p.Pulse = 0.55 + 0.45 * Math.Sin(b * 2);
                p.Eye = Math.Min(Blink(t, 4.1), 0.82);
                p.Mouth = 1.3;
                break;
            }
            case Act.Sleep:
            {
                p.Sway = 1.2 * Wave(t, 5);
                p.Twist = 4 * Wave(t, 9);
                p.Pitch = 25 + 2 * Wave(t, 4);
                p.Roll = 10;
                p.Yaw = -8;
                p.Bob = 0.035 * Wave(t, 4);
                p.ShinL = 3 * Wave(t, 5);
                p.ShinR = 3 * Wave(t, 5, 1);
                p.Eye = 0.1;
                p.Mouth = 0.8;
                p.Pulse = 0.15;
                break;
            }
            case Act.Strain:
            {
                p.Hot = 1;
                p.Sway = 1.3 * Wave(t, 0.14);
                p.Yaw = 13 * Wave(t, 0.22);
                p.Pitch = 10;
                p.Bob = 0.02 * Wave(t, 0.11);
                p.ShinL = p.ShinR = 24 + 4 * Wave(t, 0.3);
                p.Eye = 1.15;
                p.Mouth = 1.9;
                p.Pulse = 0.5 + 0.5 * Wave(t, 0.25);
                p.HandL = new Vector3D(-1.02, 2.55, 0.6);
                p.HandR = new Vector3D(1.02, 2.55, 0.6);
                break;
            }
            case Act.Point:
            {
                // a notification: she points up at it, toward the middle of the notch, eyes wide
                double up = Smooth(Math.Min(1, t / 0.3));
                var hand = new Vector3D(toward * 1.25, 2.6 + 0.45 * up + 0.05 * Math.Sin(t * 9), 0.55);
                if (toward < 0) p.HandL = HoldL + (hand - HoldL) * up; else p.HandR = HoldR + (hand - HoldR) * up;
                p.Yaw = toward * 26 * up;
                p.Pitch = -13 * up;
                p.Roll = -toward * 5 * up;
                p.Eye = 1.15;
                p.Mouth = 1.5;
                p.Bob = 0.03 * Math.Abs(Math.Sin(t * 6));
                p.ShinL = 10 * Wave(t, 0.9);
                p.ShinR = 10 * Wave(t, 0.9, 1.2);
                break;
            }
            case Act.Pump:
            {
                // something finished: a fist pump, happy eyes
                double pump = Math.Abs(Math.Sin(t * Math.PI * 2.2));
                p.HandR = new Vector3D(0.95, 1.95 + 0.55 * pump, 0.95);
                p.Eye = 0.22;
                p.Mouth = 2.1;
                p.Pitch = -5;
                p.Roll = 6 * Math.Sin(t * 4.4);
                p.Bob = 0.06 * pump;
                p.ShinL = p.ShinR = 14 + 18 * pump;
                p.Pulse = 0.9;
                break;
            }
            case Act.Ears:
            {
                // the volume hit the top: hands over her headphones, eyes squeezed shut
                p.HandL = new Vector3D(-1.05, 2.26, 0.2);
                p.HandR = new Vector3D(1.05, 2.26, 0.2);
                p.Eye = 0.1;
                p.Mouth = 0.55;
                p.Yaw = 9 * Math.Sin(t * 15);
                p.Pitch = 7;
                p.Sway = 1.5 * Wave(t, 0.5);
                p.ShinL = p.ShinR = 22;
                p.Pulse = 1;
                break;
            }
            case Act.Tired:
            {
                // low battery: slumped, heavy eyelids, a slow sway
                p.Pitch = 17 + 2 * Wave(t, 3.5);
                p.Roll = 9;
                p.Eye = 0.38 + 0.08 * Wave(t, 2.7);
                p.Mouth = 0.75;
                p.Sway = 1.5 * Wave(t, 5.5);
                p.Bob = -0.03;
                p.ShinL = 2 * Wave(t, 6);
                p.ShinR = 2 * Wave(t, 6, 1);
                p.Pulse = 0.08;
                break;
            }
            case Act.Stretch:
            {
                // a morning stretch: arms up high, a big yawn, legs out
                double up = Smooth(Math.Clamp(t / 0.8, 0, 1)) * Smooth(Math.Clamp((3.3 - t) / 0.6, 0, 1));
                p.HandL = HoldL + (new Vector3D(-1.55, 3.25, 0.45) - HoldL) * up;
                p.HandR = HoldR + (new Vector3D(1.55, 3.25, 0.45) - HoldR) * up;
                p.Pitch = -11 * up;
                p.Roll = 4 * Math.Sin(t * 1.4) * up;
                p.Eye = 1 - 0.9 * up;
                p.Mouth = 1 + 1.6 * up;
                p.ShinL = p.ShinR = 34 * up;
                break;
            }
            case Act.Yawn:
            {
                // late at night: a hand over a big yawn
                double y = Smooth(Math.Clamp(t / 0.5, 0, 1)) * Smooth(Math.Clamp((2.4 - t) / 0.5, 0, 1));
                p.HandL = HoldL + (new Vector3D(-0.22, 1.98, 1.02) - HoldL) * y;
                p.Mouth = 1 + 1.8 * y;
                p.Eye = 1 - 0.85 * y;
                p.Pitch = -9 * y;
                p.Roll = 5 * y;
                break;
            }
            case Act.Spin:
            {
                // a rare one: the whole swing twirls round once, legs out
                double u = Math.Clamp(t / 1.9, 0, 1);
                p.Twist = 360 * (u * u * u * (u * (u * 6 - 15) + 10));
                p.ShinL = p.ShinR = 38 * Math.Sin(Math.PI * u);
                p.Mouth = 2.0;
                p.Eye = 0.3;
                p.Pitch = -6;
                p.Sway = 3 * Math.Sin(Math.PI * u);
                break;
            }
            case Act.Nod:
            {
                // another rare one: she nods off... and catches herself
                if (t < 1.7)
                {
                    double d = Smooth(t / 1.7);
                    p.Pitch = 30 * d;
                    p.Eye = 1 - 0.9 * d;
                    p.Roll = 6 * d;
                }
                else
                {
                    double back = Math.Min(1, (t - 1.7) / 0.12), settle = Smooth(Math.Clamp((t - 1.9) / 0.7, 0, 1));
                    p.Pitch = 30 + (-12 - 30) * back + 12 * settle;
                    p.Eye = 1.25 - 0.25 * settle;
                    p.Mouth = 1.5 - 0.5 * settle;
                    p.Yaw = 10 * Math.Sin((t - 1.7) * 18) * (1 - settle);
                }
                break;
            }
            case Act.Cheer:
            {
                double hop = Math.Abs(Math.Sin(t * Math.PI * 2.5));
                p.Sway = 5 * Wave(t, 3.4);
                p.Twist = 8 * Wave(t, 5);
                p.Roll = 6 * Wave(t, 0.8);
                p.Pitch = -6;
                p.Bob = 0.09 * hop;
                p.ShinL = p.ShinR = 12 + 26 * Wave(t, 0.4);
                p.Eye = 0.22;
                p.Mouth = 2.3;
                p.Pulse = 1;
                p.HandL = new Vector3D(-1.6, 3.3 + 0.14 * hop, 0.6);
                p.HandR = new Vector3D(1.6, 3.3 + 0.14 * hop, 0.6);
                break;
            }
        }
        return p;
    }

    // ───────────────────────────── posing ─────────────────────────────

    void Apply(in Pose p)
    {
        var swing = Matrix3D.Identity;
        swing.Rotate(new Quaternion(new Vector3D(0, 1, 0), p.Twist));
        swing.Rotate(new Quaternion(new Vector3D(0, 0, 1), p.Sway));
        _swingT.Matrix = swing;
        _seatT.OffsetY = -p.Len;
        _ropeT.ScaleY = Math.Max(0.001, p.Len);
        _bodyT.OffsetY = p.Bob;

        var head = Matrix3D.Identity;
        head.Translate(new Vector3D(-Neck.X, -Neck.Y, -Neck.Z));
        head.Rotate(new Quaternion(new Vector3D(0, 0, 1), p.Roll));
        head.Rotate(new Quaternion(new Vector3D(1, 0, 0), p.Pitch));
        head.Rotate(new Quaternion(new Vector3D(0, 1, 0), p.Yaw));
        head.Translate(new Vector3D(Neck.X, Neck.Y + p.Bob, Neck.Z));
        _headT.Matrix = head;
        _eyeT.ScaleY = p.Eye;
        _mouthT.ScaleY = p.Mouth;
        _mouthT.ScaleX = 1 + (p.Mouth - 1) * 0.35;

        var bob = new Vector3D(0, p.Bob, 0);
        Arm(ShoulderL + bob, (Point3D)p.HandL, new Vector3D(-0.5, -1, -0.3), _upperLT, _foreLT, _handLT);
        Arm(ShoulderR + bob, (Point3D)p.HandR, new Vector3D(0.5, -1, -0.3), _upperRT, _foreRT, _handRT);
        Leg(KneeL, p.ShinL, _shinLT, _bootLT);
        Leg(KneeR, p.ShinR, _shinRT, _bootRT);
        double deck = Math.Clamp(p.Deck, 0, 1);
        _deckT.ScaleX = deck;
        _deckT.ScaleZ = deck;
        _deckT.ScaleY = deck * deck;
        SwingLines(swing, p.Len, p.Hot);
        Paint(p.Hot, p.Pulse);
    }

    static void Arm(Point3D shoulder, Point3D target, Vector3D pole, MatrixTransform3D upper, MatrixTransform3D fore, TranslateTransform3D hand)
    {
        var v = target - shoulder;
        double d = v.Length, max = UpperArm + ForeArm - 0.01;
        if (d > max) { v *= max / d; d = max; }
        if (d < 0.25) { v = new Vector3D(0, -0.25, 0); d = 0.25; }
        var dir = v / d;
        double along = (UpperArm * UpperArm + d * d - ForeArm * ForeArm) / (2 * d);
        double off = Math.Sqrt(Math.Max(0, UpperArm * UpperArm - along * along));
        var side = pole - Vector3D.DotProduct(pole, dir) * dir;
        if (side.LengthSquared < 1e-8) side = new Vector3D(0, 0, -1);
        side.Normalize();
        var elbow = shoulder + dir * along + side * off;
        var end = shoulder + v;
        upper.Matrix = Aim(shoulder, elbow);
        fore.Matrix = Aim(elbow, end);
        hand.OffsetX = end.X; hand.OffsetY = end.Y; hand.OffsetZ = end.Z;
    }

    static void Leg(Point3D knee, double degrees, MatrixTransform3D shin, MatrixTransform3D boot)
    {
        double a = degrees * Math.PI / 180;
        var foot = knee + new Vector3D(0, -Math.Cos(a), Math.Sin(a)) * Shin;
        shin.Matrix = Aim(knee, foot);
        var y = knee - foot; y.Normalize();
        var z = Vector3D.CrossProduct(new Vector3D(1, 0, 0), y);
        boot.Matrix = new Matrix3D(1, 0, 0, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, foot.X, foot.Y, foot.Z, 1);
    }

    /// <summary>The matrix that lays a part built along +Y from one joint toward the next.</summary>
    static Matrix3D Aim(Point3D from, Point3D to)
    {
        var y = to - from;
        if (y.LengthSquared < 1e-10) y = new Vector3D(0, 1, 0);
        y.Normalize();
        var r = Math.Abs(y.Z) > 0.95 ? new Vector3D(1, 0, 0) : new Vector3D(0, 0, 1);
        var x = Vector3D.CrossProduct(y, r); x.Normalize();
        var z = Vector3D.CrossProduct(x, y);
        return new Matrix3D(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, from.X, from.Y, from.Z, 1);
    }

    void Paint(double hot, double pulse)
    {
        var glow = Mix(_accent, HotRed, hot);
        if (glow != _glowNow)
        {
            _glowNow = glow; _glow.Color = glow; _deckGlass.Color = glow; _halo.Color = glow;
        }
        double k = 0.25 + 0.75 * Math.Clamp(pulse, 0, 1);
        var cup = Color.FromRgb((byte)(glow.R * k), (byte)(glow.G * k), (byte)(glow.B * k));
        if (cup != _cupNow) { _cupNow = cup; _cup.Color = cup; }
    }

    /// <summary>The swing's ropes and seat, where the 3D swing puts them: top of each rope, seat, top of the other.</summary>
    void SwingLines(Matrix3D swing, double len, double hot)
    {
        Point At2D(double x, double y)
        {
            var q = swing.Transform(new Point3D(x, y, 0));
            double f = BoxWidth / 2 / Math.Tan(10 * Math.PI / 180) / (_camDist - q.Z);
            return new Point(BoxWidth / 2 + q.X * f, BoxHeight / 2 - (q.Y - _camY) * f);
        }
        var a = At2D(-RopeX, 0.6); var b = At2D(-RopeX - 0.06, -len); var c = At2D(RopeX + 0.06, -len); var d = At2D(RopeX, 0.6);
        var pts = _swingLine.Points;
        if (pts[0] != a) { pts[0] = a; _swingGlow.Points[0] = a; }
        if (pts[1] != b) { pts[1] = b; _swingGlow.Points[1] = b; }
        if (pts[2] != c) { pts[2] = c; _swingGlow.Points[2] = c; }
        if (pts[3] != d) { pts[3] = d; _swingGlow.Points[3] = d; }
        var stroke = hot > 0.5 ? StrainRed : _swingPaint;
        if (_swingLine.Stroke != stroke) _swingLine.Stroke = _swingGlow.Stroke = stroke;
    }

    /// <summary>The swing's colour flows slowly through the neon colours, in her own colour's company.</summary>
    void SwingColours(bool run)
    {
        if (!Vivid)
        {
            // one steady colour: the flow is taken off (a paused flow would still hold its colour)
            if (_swingClock != null) { _swingPaint.ApplyAnimationClock(SolidColorBrush.ColorProperty, null); _swingClock = null; }
            _swingPaint.Color = _accent;
            return;
        }
        if (!run) { _swingClock?.Controller?.Pause(); return; }
        if (_swingClock == null || _swingFor != _accent)
        {
            _swingFor = _accent;
            var flow = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(8), RepeatBehavior = RepeatBehavior.Forever };
            foreach (var (c, at) in new[] { (_accent, 0.0), (Pink, 0.25), (Violet, 0.5), (Yellow, 0.75), (_accent, 1.0) })
                flow.KeyFrames.Add(new LinearColorKeyFrame(c, KeyTime.FromPercent(at)));
            Timeline.SetDesiredFrameRate(flow, 30); // a slow flow of colour: 30 steps a second are seamless
            _swingClock = flow.CreateClock();
            _swingPaint.ApplyAnimationClock(SolidColorBrush.ColorProperty, _swingClock);
        }
        else _swingClock.Controller?.Resume();
    }

    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    static Color Mix(Color a, Color b, double k)
    {
        k = Math.Clamp(k, 0, 1);
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
    }

    /// <summary>The little things around her: the z's of her sleep.</summary>
    void Extras(in Pose p, double sleep, double t)
    {
        if (p.Deck > 0.01)
            for (int i = 0; i < _code.Length; i++)
            {
                double step = Math.Floor(t * 7 + i * 1.7);
                double n = Math.Abs(Math.Sin(step * 12.9898 + i * 78.233) * 43758.5453) % 1;
                _code[i].ScaleX = 0.2 + 0.8 * n;
            }
        if (sleep <= 0.01)
        {
            foreach (var z in _zs) if (z.Opacity != 0) z.Opacity = 0;
            return;
        }
        double hx = BoxWidth / 2 + 9, hy = AnchorY + (p.Len - Head.Y - 0.2) * Ppu;
        for (int i = 0; i < _zs.Length; i++)
        {
            double ph = (t / 3.2 + i / 3.0) % 1;
            Canvas.SetLeft(_zs[i], hx + 4 + 13 * ph + 2 * Math.Sin(ph * 7 + i));
            Canvas.SetTop(_zs[i], hy - 6 - 17 * ph);
            _zs[i].Opacity = sleep * 0.9 * Math.Sin(Math.PI * ph);
        }
    }

    // ───────────────────────────── building her ─────────────────────────────

    void Build()
    {
        double width = BoxWidth / Ppu, dist = width / (2 * Math.Tan(10 * Math.PI / 180));
        double cy = AnchorY / Ppu - BoxHeight / Ppu / 2;
        _camDist = dist;
        _camY = cy;
        _view.Camera = new PerspectiveCamera(new Point3D(0, cy, dist), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 20) { NearPlaneDistance = 2, FarPlaneDistance = 80 };

        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x74, 0x6E, 0x82)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xC4, 0xBC, 0xB4), new Vector3D(-0.35, -0.55, -0.75)));   // key: front, above left
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x9A, 0x22, 0x66), new Vector3D(-0.9, -0.1, 0.1)));     // neon pink from the right
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x18, 0x6C, 0x8E), new Vector3D(0.9, 0.15, 0.05)));     // neon cyan from the left
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x2A, 0x22, 0x4A), new Vector3D(0.1, 0.9, -0.4)));      // a violet bounce from below
        lights.Freeze();

        // colours: all her own
        Material skin = Mat(0xFF, 0xD6, 0xC2, 0.12, 14), hair = Mat(0xB4, 0x52, 0xFF, 0.45, 26), jacket = Mat(0x2C, 0x38, 0x6A, 0.35, 22),
            trim = Mat(0x58, 0x6A, 0xB8, 0.3, 20), dark = Mat(0x1C, 0x1A, 0x2A, 0.4, 30), tights = Mat(0x34, 0x28, 0x52, 0.3, 18),
            warm = Mat(0xEC, 0xE8, 0xF8, 0.2, 16), eye = Mat(0x16, 0x0E, 0x2C, 0.9, 60), white = Lit(Colors.White),
            blush = Mat(0xFF, 0xA8, 0xA6, 0, 1), lip = Mat(0x9C, 0x3A, 0x52, 0, 1), metal = Mat(0x3A, 0x3E, 0x52, 0.7, 40),
            chrome = Mat(0xB4, 0xBC, 0xD0, 1, 50), hazard = Mat(0xFE, 0xE8, 0x01, 0.3, 20);
        Material glow = Lit(_glow), cups = Lit(_cup);

        // (the swing's ropes and seat are drawn flat behind her: see SwingLines)
        var seat = new Model3DGroup { Transform = _seatT };

        var body = new Model3DGroup { Transform = _bodyT };
        Add(body, Ball(0.6, 0.4, 0.5, 20), dark, Move(0, 0.34, 0.02));                 // shorts
        Add(body, Ball(0.6, 0.72, 0.47, 22), jacket, Move(0, 1.02, 0));                // jacket
        Add(body, Ball(0.04, 0.5, 0.05, 8), glow, Move(0, 1.0, 0.45));                 // its zip, lit
        Add(body, Ball(0.61, 0.1, 0.49, 20), hazard, Move(0, 0.6, 0.01));              // a hazard-yellow hem
        Add(body, Ball(0.1, 0.1, 0.06, 10), glow, Move(0, 1.52, -0.36));               // the port at the back of her neck
        Add(body, Ball(0.44, 0.2, 0.41, 18), trim, Move(0, 1.62, 0.02));               // high collar
        Add(body, Capsule(0.3, 0.15, 0.15, 10), skin, Move(0, 1.5, 0.02));             // neck
        foreach (int s in new[] { -1, 1 })
        {
            Add(body, Ball(0.24, 0.22, 0.24, 14), trim, Move(s * 0.6, 1.44, 0));       // shoulder
            var hip = new Point3D(s * 0.3, 0.26, 0.25);
            var knee = s < 0 ? KneeL : KneeR;
            Add(body, Capsule((knee - hip).Length, 0.25, 0.21, 12), tights, new MatrixTransform3D(Aim(hip, knee)));
            Add(body, Ball(0.25, 0.25, 0.25, 14), tights, Move(knee.X, knee.Y, knee.Z));
        }
        seat.Children.Add(body);

        // arms: sleeve, sleeve, hand (laid from joint to joint every frame)
        // (one of them chrome from the elbow down: cyberware, its joints lit)
        foreach (var (upper, fore, hand, cyber) in new[] { (_upperLT, _foreLT, _handLT, true), (_upperRT, _foreRT, _handRT, false) })
        {
            Add(seat, Capsule(UpperArm, 0.19, 0.16, 12), jacket, upper);
            Add(seat, Capsule(ForeArm, 0.16, cyber ? 0.13 : 0.15, 12), cyber ? chrome : jacket, fore);
            var band = new Model3DGroup { Transform = upper };
            Add(band, Ball(0.2, 0.09, 0.2, 12), hazard, Move(0, 0.3, 0));
            seat.Children.Add(band);
            var cuff = new Model3DGroup { Transform = fore };
            Add(cuff, Ball(0.175, 0.07, 0.175, 12), glow, Move(0, cyber ? 0.02 : ForeArm - 0.12, 0));
            if (cyber) Add(cuff, Ball(0.15, 0.05, 0.15, 12), glow, Move(0, ForeArm * 0.55, 0));
            seat.Children.Add(cuff);
            Add(seat, Ball(0.17, 0.17, 0.17, 12), cyber ? chrome : skin, hand);
        }

        // legs below the knee: warmers and chunky boots with lit soles
        foreach (var (shin, boot) in new[] { (_shinLT, _bootLT), (_shinRT, _bootRT) })
        {
            Add(seat, Capsule(Shin, 0.2, 0.19, 12), warm, shin);
            var b = new Model3DGroup { Transform = boot };
            Add(b, Ball(0.23, 0.22, 0.36, 14), dark, Move(0, -0.02, 0.13));
            Add(b, Ball(0.22, 0.06, 0.35, 12), glow, Move(0, -0.22, 0.13));
            Add(b, Ball(0.215, 0.05, 0.215, 12), glow, Move(0, 0.2, 0));
            seat.Children.Add(b);
        }

        // head
        var head = new Model3DGroup { Transform = _headT };
        Add(head, Ball(1.0, 0.94, 0.95, 32), skin, Move(Head.X, Head.Y, Head.Z));
        // hair: a shell over the top, sides and back, a ragged fringe, two buns
        Add(head, Ball(1.07, 1.02, 1.04, 48, d =>
        {
            double flat = Math.Sqrt(d.X * d.X + d.Z * d.Z) + 1e-9, front = d.Z / flat;
            double k = Math.Clamp((front - 0.2) / 0.6, 0, 1);
            return d.Y > -0.5 + 0.9 * Smooth(k);
        }), hair, Move(Head.X, Head.Y + 0.03, Head.Z - 0.03), both: true);
        double[] fringe = { -58, -32, -6, 22, 48 };
        for (int i = 0; i < fringe.Length; i++)
        {
            double az = fringe[i] * Math.PI / 180, ring = 0.95;
            var at = new Vector3D(Head.X + 1.03 * ring * Math.Sin(az), Head.Y + 0.33 + (i % 2) * 0.05, Head.Z + 0.97 * ring * Math.Cos(az));
            Add(head, Ball(0.31, 0.34, 0.13, 14), hair, At(at.X, at.Y, at.Z, yaw: fringe[i], pitch: -16, roll: (i - 2) * 9));
            if (i == 3) // one lock dyed in light
                Add(head, Ball(0.1, 0.33, 0.06, 10), glow, At(at.X + 0.05, at.Y, at.Z + 0.1, yaw: fringe[i], pitch: -16, roll: 14));
        }
        foreach (int s in new[] { -1, 1 })
        {
            Add(head, Ball(0.19, 0.5, 0.24, 14), hair, At(Head.X + s * 0.9, Head.Y - 0.3, Head.Z + 0.36, roll: s * 8));    // a lock by the cheek
            Add(head, Ball(0.42, 0.42, 0.42, 18), hair, Move(Head.X + s * 0.76, Head.Y + 0.84, Head.Z - 0.42));          // bun
            Add(head, Ball(0.3, 0.075, 0.3, 14), glow, At(Head.X + s * 0.57, Head.Y + 0.62, Head.Z - 0.36, roll: -s * 42)); // its tie
            Add(head, Ball(0.2, 0.4, 0.4, 18), metal, Move(Head.X + s * 1.08, Head.Y - 0.08, Head.Z));                    // headphone cup
            Add(head, Ball(0.08, 0.31, 0.31, 18), dark, Move(Head.X + s * 1.25, Head.Y - 0.08, Head.Z));                   // its face plate
            Add(head, Arc(0.3, 0.3, 0, 360, 0.032, 28), cups, At(Head.X + s * 1.31, Head.Y - 0.08, Head.Z, yaw: 90));       // a lit ring round it
            Add(head, Ball(0.04, 0.12, 0.12, 12), cups, Move(Head.X + s * 1.33, Head.Y - 0.08, Head.Z));                   // and a light in the middle
            Add(head, Ball(0.17, 0.1, 0.05, 10), blush, At(Head.X + s * 0.6, Head.Y - 0.36, Head.Z + 0.72, yaw: s * 36));
        }
        Add(head, Arc(1.15, 1.12, 12, 168, 0.085, 26), metal, Move(Head.X, Head.Y - 0.02, Head.Z), both: true);          // headphone band
        Add(head, Arc(1.17, 1.14, 62, 118, 0.12, 12), dark, Move(Head.X, Head.Y - 0.02, Head.Z), both: true);           // its padded top
        Add(head, Arc(1.24, 1.21, 22, 158, 0.024, 26), glow, Move(Head.X, Head.Y - 0.02, Head.Z), both: true);          // a light strip along it
        var micFrom = new Point3D(Head.X - 1.2, Head.Y - 0.28, Head.Z + 0.2);
        var micTo = new Point3D(Head.X - 0.5, Head.Y - 0.64, Head.Z + 0.88);
        Add(head, Capsule((micTo - micFrom).Length, 0.035, 0.03, 8), chrome, new MatrixTransform3D(Aim(micFrom, micTo))); // a mic on the left
        Add(head, Ball(0.075, 0.075, 0.075, 10), cups, Move(micTo.X, micTo.Y, micTo.Z));
        Add(head, Capsule(0.62, 0.03, 0.022, 8), chrome, At(Head.X + 1.12, Head.Y + 0.1, Head.Z - 0.1, pitch: -14, roll: -12)); // an antenna on one cup
        Add(head, Ball(0.07, 0.07, 0.07, 8), glow, Move(Head.X + 1.26, Head.Y + 0.74, Head.Z - 0.26));
        Add(head, Ball(0.035, 0.13, 0.03, 8), glow, At(Head.X + 0.66, Head.Y - 0.3, Head.Z + 0.7, yaw: 38));               // a circuit line down her cheek
        Add(head, Ball(0.035, 0.08, 0.03, 8), glow, At(Head.X + 0.75, Head.Y - 0.24, Head.Z + 0.6, yaw: 44));

        var eyes = new Model3DGroup { Transform = _eyeT };
        foreach (int s in new[] { -1, 1 })
        {
            var e = new Model3DGroup { Transform = At(Head.X + s * 0.39, Head.Y - 0.12, Head.Z + 0.83, yaw: s * 22) };
            Add(e, Ball(0.2, 0.27, 0.1, 16), eye);
            Add(e, Ball(0.13, 0.13, 0.06, 12), glow, Move(0, -0.09, 0.06));
            Add(e, Ball(0.06, 0.07, 0.04, 8), white, Move(0.07, 0.11, 0.08));
            Add(e, Ball(0.028, 0.028, 0.02, 6), white, Move(-0.06, -0.13, 0.09));          // a second, smaller catchlight
            Add(e, Ball(0.23, 0.045, 0.07, 12), eye, At(s * 0.03, 0.25, 0.02, roll: -s * 10)); // her lashes
            eyes.Children.Add(e);
        }
        head.Children.Add(eyes);
        var mouth = new Model3DGroup { Transform = _mouthT };
        Add(mouth, Ball(0.085, 0.035, 0.04, 10), lip, Move(Head.X, Head.Y - 0.5, Head.Z + 0.86));
        head.Children.Add(mouth);
        seat.Children.Add(head);

        // a flat glass slate floating over her lap, tilted up toward her: lit edges, five lines of code
        var deckAt = new Transform3DGroup();
        deckAt.Children.Add(_deckT);
        deckAt.Children.Add(At(0, 0.98, 1.5, pitch: 34));
        var deck = new Model3DGroup { Transform = deckAt };
        const double dw = 0.62, dd = 0.36, edge = 0.022;
        Add(deck, Box(dw * 2, edge, edge), glow, Move(0, 0, -dd));
        Add(deck, Box(dw * 2, edge, edge), glow, Move(0, 0, dd));
        Add(deck, Box(edge, edge, dd * 2), glow, Move(-dw, 0, 0));
        Add(deck, Box(edge, edge, dd * 2), glow, Move(dw, 0, 0));
        for (int i = 0; i < _code.Length; i++)
        {
            _code[i] = new ScaleTransform3D(1, 1, 1, -0.5, 0, 0);
            var line = new Model3DGroup { Transform = _code[i] };
            double w = i % 2 == 0 ? 0.62 : 0.44;
            Add(line, Box(w, 0.008, 0.035), i == 2 ? hazard : white, Move(-0.5 + w / 2, 0.012, -0.26 + i * 0.13));
            deck.Children.Add(line);
        }
        Add(deck, Box(dw * 2, 0.006, dd * 2), new EmissiveMaterial(_deckGlass), both: true); // the glass: light only, see-through
        seat.Children.Add(deck);

        var swing = new Model3DGroup { Transform = _swingT };
        swing.Children.Add(seat);

        var scene = new Model3DGroup();
        scene.Children.Add(lights);
        scene.Children.Add(swing);
        _view.Children.Add(new ModelVisual3D { Content = scene });
    }

    static void Add(Model3DGroup g, MeshGeometry3D mesh, Material m, Transform3D? t = null, bool both = false)
    {
        var model = new GeometryModel3D(mesh, m);
        if (both) model.BackMaterial = m;
        if (t != null) model.Transform = t;
        g.Children.Add(model);
    }

    static Transform3D Move(double x, double y, double z) { var t = new TranslateTransform3D(x, y, z); t.Freeze(); return t; }

    static Transform3D At(double x, double y, double z, double yaw = 0, double pitch = 0, double roll = 0)
    {
        var m = Matrix3D.Identity;
        if (roll != 0) m.Rotate(new Quaternion(new Vector3D(0, 0, 1), roll));
        if (pitch != 0) m.Rotate(new Quaternion(new Vector3D(1, 0, 0), pitch));
        if (yaw != 0) m.Rotate(new Quaternion(new Vector3D(0, 1, 0), yaw));
        m.Translate(new Vector3D(x, y, z));
        var t = new MatrixTransform3D(m);
        t.Freeze();
        return t;
    }

    static Material Mat(byte r, byte g, byte b, double shine, double power)
    {
        var m = new MaterialGroup();
        m.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(r, g, b))));
        if (shine > 0)
        {
            byte v = (byte)(255 * shine);
            m.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(v, v, v)), power));
        }
        m.Freeze();
        return m;
    }

    /// <summary>Something that gives its own light (solid underneath, so it never turns see-through).</summary>
    static Material Lit(Brush b)
    {
        var m = new MaterialGroup();
        m.Children.Add(new DiffuseMaterial(Brushes.Black));
        m.Children.Add(new EmissiveMaterial(b));
        return m;
    }

    static Material Lit(Color c) { var m = Lit(new SolidColorBrush(c)); m.Freeze(); return m; }

    /// <summary>A box centred on the origin.</summary>
    static MeshGeometry3D Box(double w, double h, double d)
    {
        var mesh = new MeshGeometry3D();
        double x = w / 2, y = h / 2, z = d / 2;
        void Face(Vector3D n, Point3D a, Point3D b, Point3D c, Point3D e)
        {
            int k = mesh.Positions.Count;
            foreach (var q in new[] { a, b, c, e }) { mesh.Positions.Add(q); mesh.Normals.Add(n); }
            mesh.TriangleIndices.Add(k); mesh.TriangleIndices.Add(k + 1); mesh.TriangleIndices.Add(k + 2);
            mesh.TriangleIndices.Add(k); mesh.TriangleIndices.Add(k + 2); mesh.TriangleIndices.Add(k + 3);
        }
        Face(new(0, 0, 1), new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z));
        Face(new(0, 0, -1), new(x, -y, -z), new(-x, -y, -z), new(-x, y, -z), new(x, y, -z));
        Face(new(1, 0, 0), new(x, -y, z), new(x, -y, -z), new(x, y, -z), new(x, y, z));
        Face(new(-1, 0, 0), new(-x, -y, -z), new(-x, -y, z), new(-x, y, z), new(-x, y, -z));
        Face(new(0, 1, 0), new(-x, y, z), new(x, y, z), new(x, y, -z), new(-x, y, -z));
        Face(new(0, -1, 0), new(-x, -y, -z), new(x, -y, -z), new(x, -y, z), new(-x, -y, z));
        mesh.Freeze();
        return mesh;
    }

    /// <summary>A shape turned on a lathe around the Y axis: its outline from the top down, as (height, radius).</summary>
    static MeshGeometry3D Lathe(IList<(double Y, double R)> outline, int around)
    {
        around = (int)Math.Ceiling(around * 1.6);
        var mesh = new MeshGeometry3D();
        int n = outline.Count;
        for (int i = 0; i < n; i++)
        {
            var (y, r) = outline[i];
            var a = outline[Math.Max(0, i - 1)];
            var b = outline[Math.Min(n - 1, i + 1)];
            double nr = -(b.Y - a.Y), ny = b.R - a.R, len = Math.Sqrt(nr * nr + ny * ny);
            if (len < 1e-9) { nr = 0; ny = 1; len = 1; }
            nr /= len; ny /= len;
            for (int j = 0; j <= around; j++)
            {
                double f = 2 * Math.PI * j / around, c = Math.Cos(f), s = Math.Sin(f);
                mesh.Positions.Add(new Point3D(r * c, y, r * s));
                mesh.Normals.Add(new Vector3D(nr * c, ny, nr * s));
                mesh.TextureCoordinates.Add(new Point((double)j / around, (double)i / Math.Max(1, n - 1)));
            }
        }
        for (int i = 0; i < n - 1; i++)
            for (int j = 0; j < around; j++)
            {
                int a = i * (around + 1) + j, b = a + around + 1, c = a + 1, d = b + 1;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(d);
            }
        mesh.Freeze();
        return mesh;
    }

    /// <summary>A rounded limb standing on the origin: from 0 up to its length, one radius at each end.</summary>
    static MeshGeometry3D Capsule(double length, double bottom, double top, int around)
    {
        var o = new List<(double, double)>();
        const int cap = 8;
        for (int i = 0; i <= cap; i++) { double a = Math.PI / 2 * i / cap; o.Add((length + top * Math.Cos(a) * 0.9, top * Math.Sin(a))); }
        for (int i = 0; i <= cap; i++) { double a = Math.PI / 2 + Math.PI / 2 * i / cap; o.Add((bottom * Math.Cos(a) * 0.9, bottom * Math.Sin(a))); }
        return Lathe(o, around);
    }

    /// <summary>A ball stretched to three radii; with a test, only the part of it that passes (her hairline).</summary>
    static MeshGeometry3D Ball(double rx, double ry, double rz, int around, Func<Vector3D, bool>? keep = null)
    {
        var mesh = new MeshGeometry3D();
        around = (int)Math.Ceiling(around * 1.6); // round enough to stay round at three times the screen's sharpness
        int rings = Math.Max(8, around * 2 / 3);
        var dirs = new List<Vector3D>();
        for (int i = 0; i <= rings; i++)
        {
            double th = Math.PI * i / rings, st = Math.Sin(th), ct = Math.Cos(th);
            for (int j = 0; j <= around; j++)
            {
                double f = 2 * Math.PI * j / around;
                var d = new Vector3D(st * Math.Cos(f), ct, st * Math.Sin(f));
                dirs.Add(d);
                mesh.Positions.Add(new Point3D(d.X * rx, d.Y * ry, d.Z * rz));
                var nrm = new Vector3D(d.X / rx, d.Y / ry, d.Z / rz);
                nrm.Normalize();
                mesh.Normals.Add(nrm);
            }
        }
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < around; j++)
            {
                int a = i * (around + 1) + j, b = a + around + 1, c = a + 1, d = b + 1;
                if (keep != null)
                {
                    var mid = dirs[a] + dirs[b] + dirs[c] + dirs[d];
                    mid.Normalize();
                    if (!keep(mid)) continue;
                }
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(d);
            }
        mesh.Freeze();
        return mesh;
    }

    /// <summary>A tube bent along an arc in the XY plane (the band of her headphones).</summary>
    static MeshGeometry3D Arc(double rx, double ry, double fromDeg, double toDeg, double tube, int steps)
    {
        var mesh = new MeshGeometry3D();
        const int around = 8;
        for (int i = 0; i <= steps; i++)
        {
            double a = (fromDeg + (toDeg - fromDeg) * i / steps) * Math.PI / 180;
            var c = new Point3D(rx * Math.Cos(a), ry * Math.Sin(a), 0);
            var n = new Vector3D(Math.Cos(a), Math.Sin(a), 0);
            for (int j = 0; j <= around; j++)
            {
                double f = 2 * Math.PI * j / around;
                var dir = n * Math.Cos(f) + new Vector3D(0, 0, 1) * Math.Sin(f);
                mesh.Positions.Add(c + dir * tube);
                mesh.Normals.Add(dir);
            }
        }
        for (int i = 0; i < steps; i++)
            for (int j = 0; j < around; j++)
            {
                int a = i * (around + 1) + j, b = a + around + 1, c = a + 1, d = b + 1;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(d);
            }
        mesh.Freeze();
        return mesh;
    }
}
