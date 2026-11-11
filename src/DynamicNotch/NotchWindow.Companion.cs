using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicNotch.Controls;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The companion: a small 3D hacker on a neon swing that hangs from behind the notch (see <see cref="Companion"/>).
/// She lives there: she is out whenever the notch is at rest, and only steps away
///  • while you use the notch (the pointer near it, the notch open, a pop-up showing): back the moment you're done;
///  • in full screen and in games;
///  • in meeting mode, while a call rings, and during calls if that switch is off;
///  • when she is switched off in the preferences.
/// What she does up there: mostly she sits still and blinks (a still picture: no frames, no cost), and every
/// little while she has a moment: looks around, swings her legs, sometimes waves. With music she nods along, her
/// headphones pulsing. Left alone for a few minutes she dozes off, and wakes when you're back. You plug the charger
/// in: she cheers. After a strain warning she glitches red, holding her head.
/// While you type she opens a holo-deck on her lap and hacks along (only the fact that input happens is noticed,
/// never which keys). Every couple of minutes she moves along the notch to another spot, the swing trailing behind.
/// She never takes a click: the window lets everything through.
/// </summary>
public partial class NotchWindow
{
    Companion? _pal;
    CompanionHost? _palHost;
    readonly TranslateTransform _palAt = new();
    readonly DispatcherTimer _palTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Random _palRand = new();
    DateTime _palShyUntil, _palActUntil, _palStirEnd, _palNextStir, _palNextBlink, _palStrainUntil, _palReadyAt;
    Companion.Act? _palMoment;   // a passing act (cheering, the strain) that runs until _palActUntil
    bool _palPlugged, _palFirst = true;
    double _palX, _palY;

    // her spots along the notch (a share of its width, from its centre); she moves between them now and then
    static readonly double[] PalSpots = { 0.29, -0.29, 0.41, -0.41, 0.18 };
    double _palSide = 0.29;
    DateTime _palNextMove;

    // typing: input keeps arriving while the pointer stays put. Only that is looked at, never which keys.
    readonly DispatcherTimer _palKeys = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    readonly bool[] _palHits = new bool[12];
    int _palHitAt;
    uint _palLastInput;
    POINT _palLastCursor;
    bool _palTyping, _palDevTyping, _palDevAwake, _palMousing, _palDevMouse;
    readonly bool[] _palMoves = new bool[6];

    void WireCompanion()
    {
        _palPlugged = _power.PluggedIn;
        _palReadyAt = DateTime.UtcNow.AddSeconds(8); // once the notch has settled after starting
        _palTimer.Tick += (_, _) => PalTick();
        _palTimer.Start();
        _palKeys.Tick += (_, _) => PalKeys();
        _power.Changed += PalPower;
    }

    Companion PalGet()
    {
        if (_pal != null) return _pal;
        _pal = new Companion { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, RenderTransform = _palAt };
        _pal.Gone += () => { _palMoment = null; _palHost?.HideQuiet(); };
        _palHost = new CompanionHost(_pal); // her own small window under the notch: cheap to redraw at the screen's rate
        return _pal;
    }

    /// <summary>Where her swing hangs: under the right part of the notch (DIPs: from its centre, and its bottom edge).</summary>
    (double X, double Y) PalSpot()
    {
        double h = _sh.Target, bottom = h - (_cyber && h > 26 ? 3.5 : 0); // the Neon shape steps up on that side
        return (OnPixel(_sw.Target * _palSide), OnPixel(bottom - Companion.AnchorY));
    }

    /// <summary>A length on whole screen pixels at this scale (a half-pixel offset would blur her whole picture).</summary>
    double OnPixel(double dips) => Math.Round(dips * _scale) / Math.Max(0.5, _scale);

    /// <summary>The notch is at rest and on screen, and nobody is using it.</summary>
    bool PalAllowed() =>
        _settings.Companion && !_hidden && !_offScreen && !_fgFullscreen && _game == null && _mode is Mode.Idle or Mode.Slim or Mode.Media or Mode.Activity
        && (!OnCall || _settings.CompanionInCalls) && !Ringing && !_inMeeting && !_dodged && !_peek && !_dropArmed && _osdQueue.Count == 0
        && DateTime.UtcNow >= _palShyUntil && DateTime.UtcNow >= _palReadyAt;

    Color PalAccent()
    {
        var accent = CustomAccent ?? (_cyber ? CyCyan : _look?.Accent) ?? Color.FromRgb(0x3D, 0xE8, 0xFF);
        return accent.R > 235 && accent.G > 235 && accent.B > 235 ? Color.FromRgb(0x3D, 0xE8, 0xFF) : accent; // plain white would wash her out
    }

    void PalTick()
    {
        var now = DateTime.UtcNow;
        if (_pal is not { IsOut: true } pal)
        {
            if (!PalAllowed()) return;
            pal = PalGet();
            PalPlace();
            _palHost!.ShowQuiet();
            (_palX, _palY) = PalSpot();
            _palAt.X = _palX;
            _palAt.Y = _palY;
            pal.Vivid = !Eco; // on battery saving the swing keeps one colour
            pal.Tucked = _overWindow; // over a window (a browser's tabs...) she tucks up: only her boots show
        pal.SetAccent(PalAccent());
            pal.Fps = Eco ? 30 : 0;
            // coming back she picks a spot away from where you're working, and comes in her own way: dropping in,
            // peeking out first, or flickering in like a hologram
            if (!_palFirst) { _palSide = PalPickSpot(); (_palX, _palY) = PalSpot(); _palAt.X = _palX; }
            int roll = _palRand.Next(10);
            var how = _palFirst || roll < 5 ? Companion.Entrance.Drop : roll < 8 ? Companion.Entrance.Peek : Companion.Entrance.Glitch;
            pal.Appear(Companion.Act.Chill, how);
            pal.Stir(wave: _palFirst);
            _palFirst = false;
            _palStirEnd = now.AddSeconds(5);
            _palNextStir = now.AddSeconds(18 + _palRand.NextDouble() * 20);
            _palNextBlink = now.AddSeconds(8);
            _palNextMove = now.AddSeconds(70 + _palRand.NextDouble() * 70);
            PalKeysReset();
            _palKeys.Start();
            return;
        }

        if (pal.Leaving) return;
        if (!PalAllowed()) { pal.Leave(fast: true); return; }
        PalPlace(); // the notch may have moved (another screen, another scale)
        if (Eco) pal.LookAt(0, 0); else PalGaze(pal); // on battery saving she doesn't follow the pointer
        var spot = PalSpot();
        if (spot != (_palX, _palY)) { (_palX, _palY) = spot; pal.Slide(_palX, _palY); } // the notch changed size: she follows its edge
        pal.Vivid = !Eco; // on battery saving the swing keeps one colour
        pal.Tucked = _overWindow;
        pal.SetAccent(PalAccent());

        bool eco0 = Eco;
        // a passing moment first: the strain, the cheer
        if (_palStrainUntil > now)
        {
            _palStrainUntil = default;
            _palMoment = Companion.Act.Strain;
            _palActUntil = now.AddSeconds(5.5);
        }
        if (_palMoment is { } moment)
        {
            if (now < _palActUntil) { pal.Fps = eco0 ? 30 : 0; pal.SetAct(moment); return; }
            _palMoment = null;
            _palStirEnd = now; // then back to sitting quietly
        }

        bool eco = Eco;
        if (IdleTime().TotalSeconds >= 180 && !_palDevAwake)
        {
            pal.Fps = 12; // asleep only her z's move
            pal.SetAct(Companion.Act.Sleep);
            return;
        }
        if (_palTyping && !eco)
        {
            pal.Fps = eco ? 30 : 0;
            pal.SetAct(Companion.Act.Hack); // you're typing: she hacks along on her holo-deck
            return;
        }
        if (_palMousing && !eco)
        {
            pal.Fps = 30; // her head and hand follow the pointer: 30 frames are plenty, and half the cost
            pal.SetAct(Companion.Act.Scan); // you're on the mouse: visor down, she tracks your pointer
            return;
        }
        if (_media.IsPlaying && !eco)
        {
            PalWander(pal, now);
            pal.Fps = 0;
            pal.SetAct(Companion.Act.Music);
            return;
        }

        pal.Fps = eco ? 30 : 0;
        if (pal.Current != Companion.Act.Chill)
        {
            // she woke up, or the music stopped: a little moment (a wave if she had been asleep), then quiet again
            bool woke = pal.Current == Companion.Act.Sleep;
            pal.SetAct(Companion.Act.Chill);
            pal.Stir(wave: woke);
            _palStirEnd = now.AddSeconds(woke ? 5 : 3);
            _palNextStir = now.AddSeconds(18 + _palRand.NextDouble() * 20);
            return;
        }
        if (!pal.Resting)
        {
            if (now >= _palStirEnd) { pal.Rest(); _palNextBlink = now.AddSeconds(3); }
            return;
        }
        if (!eco && PalWander(pal, now)) return;
        if (now >= _palNextStir && !eco)
        {
            bool wave = _palRand.Next(5) == 0;
            pal.Stir(wave);
            _palStirEnd = now.AddSeconds(wave ? 5 : 3 + _palRand.NextDouble() * 1.5);
            _palNextStir = now.AddSeconds(18 + _palRand.NextDouble() * 22);
        }
        else if (now >= _palNextBlink)
        {
            pal.Blink();
            _palNextBlink = now.AddSeconds(3 + _palRand.NextDouble() * 3.5);
        }
    }

    /// <summary>The notch fades out of the pointer's way: she fades with it, in the same instant.</summary>
    void PalDodge(bool on)
    {
        if (_palHost == null) return;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        _palHost.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(on ? 0 : 1, TimeSpan.FromMilliseconds(on ? 140 : 240)) { EasingFunction = ease });
    }

    /// <summary>Her window's top centre on the notch's.</summary>
    void PalPlace() => _palHost?.Place(_winLeft + _winW / 2, _winTop, _scale);

    /// <summary>Now and then she moves to another spot along the notch.</summary>
    bool PalWander(Companion pal, DateTime now)
    {
        if (now < _palNextMove || pal.Traveling) return false;
        _palNextMove = now.AddSeconds(90 + _palRand.NextDouble() * 90);
        _palSide = PalPickSpot(exceptNow: true);
        (_palX, _palY) = PalSpot();
        pal.Slide(_palX, _palY);
        return true;
    }

    /// <summary>A spot along the notch away from the pointer (with a little chance in it, so she isn't predictable).</summary>
    double PalPickSpot(bool exceptNow = false)
    {
        GetCursorPos(out var cur);
        double cx = _winLeft + _winW / 2.0, best = PalSpots[0], bestScore = double.MinValue;
        foreach (double side in PalSpots)
        {
            if (exceptNow && side == _palSide) continue;
            double x = cx + Math.Round(_sw.Target * side) * _scale;
            double score = Math.Min(Math.Abs(x - cur.X), 600 * _scale) + _palRand.NextDouble() * 140 * _scale;
            if (score > bestScore) { bestScore = score; best = side; }
        }
        return best;
    }

    /// <summary>Her head follows the pointer.</summary>
    void PalGaze(Companion pal)
    {
        if (!GetCursorPos(out var cur)) return;
        double hx = _winLeft + _winW / 2.0 + _palAt.X * _scale, hy = _winTop + (_palAt.Y + 70) * _scale;
        pal.LookAt((cur.X - hx) / _scale, (cur.Y - hy) / _scale);
    }

    void PalKeysReset()
    {
        Array.Clear(_palHits);
        Array.Clear(_palMoves);
        _palTyping = false;
        _palMousing = false;
        _palLastInput = 0;
    }

    void PalKeys()
    {
        if (_pal is not { IsOut: true, Leaving: false }) { _palKeys.Stop(); PalKeysReset(); return; }
        var li = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li) || !GetCursorPos(out var cur)) return;
        bool input = _palLastInput != 0 && li.dwTime != _palLastInput;
        bool still = cur.X == _palLastCursor.X && cur.Y == _palLastCursor.Y;
        _palLastInput = li.dwTime;
        _palLastCursor = cur;
        // the keyboard: input while the pointer stays put; the mouse: the pointer moving
        _palHits[_palHitAt % _palHits.Length] = input && still;
        _palMoves[_palHitAt % _palMoves.Length] = !still;
        _palHitAt = (_palHitAt + 1) % (_palHits.Length * _palMoves.Length);
        int keys = 0, moves = 0;
        for (int age = 0; age < 6; age++) // the last 1.5 seconds
        {
            if (_palHits[(_palHitAt - 1 - age + _palHits.Length * 6) % _palHits.Length]) keys++;
            if (_palMoves[(_palHitAt - 1 - age + _palMoves.Length * 12) % _palMoves.Length]) moves++;
        }
        // typing starts after 3 keystrokes in 1.5 s and ends after 1.5 quiet seconds; the mouse the same with movement.
        // Typing wins: a hand on the mouse between words doesn't pull her away from her deck
        bool typing = _palDevTyping || (_palTyping ? keys > 0 : keys >= 3);
        bool mousing = !typing && (_palDevMouse || (_palMousing ? moves > 0 : moves >= 3));
        if (typing == _palTyping && mousing == _palMousing) return;
        _palTyping = typing;
        _palMousing = mousing;
        PalTick(); // she reacts at once, not at the next tick
    }

    /// <summary>You're using the notch (the pointer is at it): she steps away at once, and is back when you're done.</summary>
    void PalPointer(POINT p)
    {
        if (!_settings.Companion) return;
        double x = Math.Abs(_pal is { IsOut: true } ? _palAt.X : PalSpot().X);
        double cx = _winLeft + _winW / 2.0, half = Math.Max(_sw.Target / 2 + 24, x + 60) * _scale;
        if (p.X < cx - half || p.X > cx + half || p.Y - _mon.Top > (_sh.Target + 104) * _scale) return;
        _palShyUntil = DateTime.UtcNow.AddSeconds(0.7);
        if (_pal is { IsOut: true } pal) pal.Leave(fast: true);
    }

    /// <summary>The notch changes: opened or a pop-up, she's gone that instant; another size, she zips back up.</summary>
    void PalMode()
    {
        if (_pal is not { IsOut: true } pal) return;
        if (_mode is Mode.Idle or Mode.Slim or Mode.Media or Mode.Activity) pal.Leave(fast: true);
        else pal.HideNow();
    }

    void PalPower()
    {
        bool plugged = _power.PluggedIn, was = _palPlugged;
        _palPlugged = plugged;
        if (!plugged || was || !_power.HasBattery || _pal is not { IsOut: true, Leaving: false }) return;
        _palMoment = Companion.Act.Cheer; // power: she cheers
        _palActUntil = DateTime.UtcNow.AddSeconds(3.6);
        PalTick();
    }

    /// <summary>A strain warning went by: once the notch is calm again, she feels it too.</summary>
    void PalStrain() => _palStrainUntil = DateTime.UtcNow.AddSeconds(30);

    /// <summary>Developer switch (DYNAMICNOTCH_OPEN=pal-strain, pal-cheer, pal-hack, pal-walk): one of her passing moments, 12 s after starting.</summary>
    void PalDevShow(string what) => After(12000, () =>
    {
        _palDevAwake = true; // a developer look: she stays awake
        if (what == "pal-walk") { _palNextMove = DateTime.UtcNow; return; }        // she moves to another spot now
        if (what == "pal-hack") { _palTyping = true; _palDevTyping = true; return; } // as if you were typing
        if (what == "pal-mouse") { _palMousing = true; _palDevMouse = true; return; } // as if you were on the mouse
        if (what is not ("pal-strain" or "pal-cheer")) return;
        _palMoment = what == "pal-strain" ? Companion.Act.Strain : Companion.Act.Cheer;
        _palActUntil = DateTime.UtcNow.AddSeconds(6);
    });
}
