using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// The album cover as a record, in every theme: the art on a round record that turns while the music plays (one turn
/// every 14 s), like a turntable: it spins up when the music starts and slows to a stop when you pause. On the big
/// one a tonearm swings onto the record as it plays and lifts off at pause, and the glow behind it, in the album's own
/// colour, pulses with the music. Neon wears a dashed HUD ring with a yellow tick turning the other way; the other
/// themes a thin ring in the song's colour. The closed notch's little cover is the same record in small, turning with it.
/// No blur effects (the glows are gradients), the records are cached pictures the graphics chip turns, and nothing
/// moves unless a record is on screen and the music plays.
/// </summary>
public partial class NotchWindow
{
    readonly RotateTransform _discSpin = new(), _hudSpin = new();
    bool _discWired, _discTurning, _hudTurning;
    int _discFps;
    ImageSource? _discArtFor;
    // the player Windows reports is paused, but its app is still making sound (another browser tab playing)
    bool _discSounding, _discListenBusy;
    int _discQuiet;
    DispatcherTimer? _discListen;

    /// <summary>
    /// Once a second, only while a record is on screen and Windows says the player in front is paused: is its app
    /// still making sound? A browser tells Windows about one tab only, so a paused tab can hide one that plays.
    /// Three quiet seconds in a row before it counts as stopped (songs have quiet moments).
    /// </summary>
    void ListenForSound(bool on)
    {
        if (!on)
        {
            _discListen?.Stop();
            _discSounding = false;
            _discQuiet = 0;
            return;
        }
        if (_discListen == null)
        {
            _discListen = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _discListen.Tick += async (_, _) =>
            {
                if (_discListenBusy) return;
                _discListenBusy = true;
                float peak = 0;
                try { peak = await Task.Run(_audio.AppPeak); } catch { }
                finally { _discListenBusy = false; }
                bool sound = peak > 0.004f;
                _discQuiet = sound ? 0 : _discQuiet + 1;
                bool was = _discSounding;
                if (sound) _discSounding = true;
                else if (_discQuiet >= 3) _discSounding = false;
                if (was != _discSounding) SpinDisc();
            };
        }
        if (!_discListen.IsEnabled) _discListen.Start();
    }

    readonly RotateTransform _armTurn = new(-30, 60, 2);
    readonly ScaleTransform _glowBeat = new(1, 1, 44, 44);
    System.Windows.Shapes.Path? _arm;
    System.Windows.Shapes.Ellipse? _armPivot;
    System.Windows.Shapes.Rectangle? _armHead;
    DispatcherTimer? _discBeat;
    int _spinToken;

    void StyleArtDisc()
    {
        bool on = true; // every theme has the record now
        ArtBox.Visibility = CompactArt.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ArtDisc.Visibility = MiniDisc.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!_discWired)
        {
            _discWired = true;
            DiscArt.RenderTransform = MiniArt.RenderTransform = _discSpin; // one turn, both records
            DiscHud.RenderTransform = _hudSpin;
            // drawn once into a picture (sharp at this screen's scaling), then only turned: no redrawing per frame
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            DiscArt.CacheMode = new BitmapCache(dpi * 1.5);
            DiscHud.CacheMode = new BitmapCache(dpi * 1.5);
            MiniArt.CacheMode = new BitmapCache(dpi * 2);
            ArtDisc.IsVisibleChanged += (_, _) => SpinDisc();
            // the tonearm: from its pivot at the top right, down onto the record
            var armLayer = new System.Windows.Controls.Canvas { Width = 64, Height = 64, IsHitTestVisible = false, RenderTransform = _armTurn, ClipToBounds = false };
            _arm = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 60,2 L 58.5,20 L 47,37"), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _armHead = new System.Windows.Shapes.Rectangle { Width = 4.5, Height = 6.5, RadiusX = 1, RadiusY = 1, RenderTransform = new RotateTransform(35, 2.25, 3.25) };
            System.Windows.Controls.Canvas.SetLeft(_armHead, 44.5); System.Windows.Controls.Canvas.SetTop(_armHead, 35);
            _armPivot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7 };
            System.Windows.Controls.Canvas.SetLeft(_armPivot, 56.5); System.Windows.Controls.Canvas.SetTop(_armPivot, -1.5);
            armLayer.Children.Add(_arm); armLayer.Children.Add(_armHead); armLayer.Children.Add(_armPivot);
            ArtDisc.Children.Add(armLayer);
            DiscGlow.RenderTransform = _glowBeat;
            MiniDisc.IsVisibleChanged += (_, _) => SpinDisc();
            _power.Changed += SpinDisc; // plugged in, unplugged, battery saver: the right pace at once
            _power.Changed += UpdateRain; // the rain rests in power-saving mode
        }
        if (!on) { SpinDisc(); return; }
        if (_cyber)
        {
            var cyan = Frozen(Color.FromArgb(0x8C, CyCyan.R, CyCyan.G, CyCyan.B));
            DiscRing.Stroke = MiniRing.Stroke = cyan;
            DiscRing.StrokeDashArray = new DoubleCollection { 5, 3 };
            DiscTick.Stroke = Frozen(CyYellow);
            DiscHub.Stroke = Frozen(Color.FromArgb(0xB0, CyCyan.R, CyCyan.G, CyCyan.B));
            DiscPin.Fill = MiniPin.Fill = Frozen(CyYellow);
            if (_arm != null) { _arm.Stroke = Frozen(Color.FromArgb(0xE0, CyCyan.R, CyCyan.G, CyCyan.B)); _armHead!.Fill = Frozen(CyYellow); _armPivot!.Fill = Frozen(Color.FromRgb(0x05, 0x08, 0x0C)); _armPivot.Stroke = Frozen(CyCyan); _armPivot.StrokeThickness = 1.2; }
        }
        else
        {
            // a clean record: a thin ring (the song's colour, set with the art), a silver arm
            DiscRing.StrokeDashArray = null;
            DiscTick.Stroke = Frozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));
            DiscHub.Stroke = Frozen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
            DiscPin.Fill = MiniPin.Fill = Frozen(Color.FromRgb(0xE8, 0xE8, 0xEC));
            if (_arm != null) { _arm.Stroke = Frozen(Color.FromRgb(0xC9, 0xCD, 0xD6)); _armHead!.Fill = Frozen(Color.FromRgb(0xE8, 0xE8, 0xEC)); _armPivot!.Fill = Frozen(Color.FromRgb(0x3A, 0x3C, 0x42)); _armPivot.Stroke = Frozen(Color.FromRgb(0xC9, 0xCD, 0xD6)); _armPivot.StrokeThickness = 1; }
        }
        _discArtFor = null; // paint it fresh
        RefreshDisc();
    }

    /// <summary>The art and the glow follow the song; the turning follows play / pause.</summary>
    void RefreshDisc()
    {
        var art = _media.HasSession ? _media.Art : null;
        if (!ReferenceEquals(art, _discArtFor) || DiscArt.Fill == null)
        {
            _discArtFor = art;
            if (art != null)
            {
                var b = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.HighQuality);
                b.Freeze();
                DiscArt.Fill = MiniArt.Fill = b;
            }
            else DiscArt.Fill = MiniArt.Fill = Frozen(Color.FromRgb(0x0A, 0x0F, 0x14)); // a blank record: just the grooves
        }
        var c = art != null ? _media.Accent : _cyber ? CyCyan : Color.FromRgb(0x8E, 0x8E, 0x93);
        if (!_cyber) { var ring = Frozen(Color.FromArgb(0x70, c.R, c.G, c.B)); DiscRing.Stroke = MiniRing.Stroke = ring; }
        DiscGlow.Fill = Glow(c, 0.52, 0.62, 0.78);
        MiniGlow.Fill = Glow(c, 0.5, 0.62, 0.8);
        SpinDisc();

        static Brush Glow(Color c, double from, double peak, double tail)
        {
            var g = new RadialGradientBrush();
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), from));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, c.R, c.G, c.B), peak));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x22, c.R, c.G, c.B), tail));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1));
            g.Freeze();
            return g;
        }
    }

    /// <summary>Turning or still: from wherever it is now, so a pause never makes it jump.</summary>
    void SpinDisc()
    {
        // hidden (a full-screen video, a game) counts as off screen: the window is hidden, WPF still thinks it's shown
        bool shown = !_offScreen && _media.HasSession && (ArtDisc.IsVisible || MiniDisc.IsVisible);
        ListenForSound(shown && !_media.IsPlaying);
        bool playing = shown && (_media.IsPlaying || _discSounding);
        // 60 frames a second plugged in; on battery (or battery saver) 30, and the HUD ring and the breathing glow
        // rest; with no graphics chip to draw with (every frame on the CPU) 20. The record is a cached picture
        // turned by the graphics chip, so a frame costs next to nothing either way
        bool eco = Eco;
        int fps = LowGraphics ? 20 : eco ? 30 : 60;
        bool big = playing && ArtDisc.IsVisible && !eco;
        bool turn = playing && (ArtDisc.IsVisible || MiniDisc.IsVisible);
        if (turn != _discTurning || (turn && fps != _discFps)) { bool fresh = turn != _discTurning; _discTurning = turn; _discFps = fps; SpinRecord(turn, fps, fresh && !eco); }
        // the tonearm: on the record while it plays, lifted off when it stops
        var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
        double armTo = playing ? 0 : -30;
        if (Math.Abs(_armTurn.Angle - armTo) > 0.5 || _armTurn.HasAnimatedProperties)
            _armTurn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(armTo, TimeSpan.FromMilliseconds(eco ? 1 : 520)) { EasingFunction = ease });
        if (big == _hudTurning) return;
        _hudTurning = big;
        // the big one's HUD ring and breathing glow: only while it's on screen
        Rotate(_hudSpin, -360, 30, big, 30);
        if (big)
        {
            // the glow pulses with the music itself (the sound's level, 20 times a second)
            _discBeat ??= NewDiscBeat();
            _discBeat.Start();
        }
        else
        {
            _discBeat?.Stop();
            _glowBeat.ScaleX = _glowBeat.ScaleY = 1;
            DiscGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, TimeSpan.FromMilliseconds(400)));
        }

    DispatcherTimer NewDiscBeat()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        double level = 0;
        t.Tick += (_, _) =>
        {
            double now = _audio.TryGetPeaks(out float l, out float r) ? Math.Sqrt(Math.Max(l, r)) : 0;
            level = now > level ? level + (now - level) * 0.6 : level + (now - level) * 0.18; // quick up, slow down: a pulse
            DiscGlow.BeginAnimation(OpacityProperty, null);
            DiscGlow.Opacity = 0.5 + 0.5 * level;
            _glowBeat.ScaleX = _glowBeat.ScaleY = 1 + 0.09 * level;
        };
        return t;
    }

    /// <summary>The record, like a turntable: it spins up when the music starts and slows to a stop at pause.</summary>
    void SpinRecord(bool on, int fps, bool smooth)
    {
        int token = ++_spinToken;
        double at = _discSpin.Angle % 360;
        _discSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        _discSpin.Angle = at;
        const double turn = 14; // seconds a turn at full speed
        if (!on)
        {
            if (!smooth) return;
            // coasting to a stop: about a quarter turn, slowing all the way
            var stop = new DoubleAnimation(at, at + 50, TimeSpan.FromMilliseconds(1300)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            Timeline.SetDesiredFrameRate(stop, fps);
            _discSpin.BeginAnimation(RotateTransform.AngleProperty, stop);
            return;
        }
        void Loop(double from)
        {
            var spin = new DoubleAnimation(from, from + 360, TimeSpan.FromSeconds(turn)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, fps);
            _discSpin.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        if (!smooth) { Loop(at); return; }
        // spinning up: from still to full speed in a second (a quadratic ease-in ends at exactly the loop's speed)
        double upSecs = 1.0, upBy = 360 / turn * upSecs / 2;
        var up = new DoubleAnimation(at, at + upBy, TimeSpan.FromSeconds(upSecs)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        Timeline.SetDesiredFrameRate(up, fps);
        up.Completed += (_, _) => { if (token == _spinToken) Loop(at + upBy); };
        _discSpin.BeginAnimation(RotateTransform.AngleProperty, up);
    }

        static void Rotate(RotateTransform r, double by, double seconds, bool on, int fps)
        {
            double at = r.Angle % 360; // the angle on screen right now (the animated one)
            r.BeginAnimation(RotateTransform.AngleProperty, null);
            r.Angle = at;
            if (!on) return;
            var spin = new DoubleAnimation(at, at + by, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, fps);
            r.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
    }
}
