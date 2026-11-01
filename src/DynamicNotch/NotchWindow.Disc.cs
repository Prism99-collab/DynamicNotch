using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// The cyberpunk theme's album cover: the art on a round record that turns slowly while the music plays (one turn
/// every 14 s) and stops where it is when you pause, a soft glow behind it in the album's own colour, and a dashed
/// HUD ring with a yellow tick turning the other way. The closed notch's little cover is the same record in small,
/// turning with it. No blur effects (the glows are gradients), the records are cached pictures the graphics chip turns
/// (60 frames a second plugged in, 30 on battery), and nothing turns unless a record is on screen and the music
/// plays. Every other theme keeps the square covers.
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

    void StyleArtDisc()
    {
        bool on = _cyber;
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
            MiniDisc.IsVisibleChanged += (_, _) => SpinDisc();
            _power.Changed += SpinDisc; // plugged in, unplugged, battery saver: the right pace at once
        }
        if (!on) { SpinDisc(); return; }
        var cyan = Frozen(Color.FromArgb(0x8C, CyCyan.R, CyCyan.G, CyCyan.B));
        DiscRing.Stroke = MiniRing.Stroke = cyan;
        DiscTick.Stroke = Frozen(CyYellow);
        DiscHub.Stroke = Frozen(Color.FromArgb(0xB0, CyCyan.R, CyCyan.G, CyCyan.B));
        DiscPin.Fill = MiniPin.Fill = Frozen(CyYellow);
        _discArtFor = null; // paint it fresh
        RefreshDisc();
    }

    /// <summary>The art and the glow follow the song; the turning follows play / pause.</summary>
    void RefreshDisc()
    {
        if (!_cyber) return;
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
        var c = art != null ? _media.Accent : CyCyan;
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
        bool shown = _cyber && !_offScreen && _media.HasSession && (ArtDisc.IsVisible || MiniDisc.IsVisible);
        ListenForSound(shown && !_media.IsPlaying);
        bool playing = shown && (_media.IsPlaying || _discSounding);
        // 60 frames a second plugged in; on battery (or battery saver) 30, and the HUD ring and the breathing glow
        // rest; with no graphics chip to draw with (every frame on the CPU) 20. The record is a cached picture
        // turned by the graphics chip, so a frame costs next to nothing either way
        bool eco = Eco;
        int fps = LowGraphics ? 20 : eco ? 30 : 60;
        bool big = playing && ArtDisc.IsVisible && !eco;
        bool turn = playing && (ArtDisc.IsVisible || MiniDisc.IsVisible);
        if (turn != _discTurning || (turn && fps != _discFps)) { _discTurning = turn; _discFps = fps; Rotate(_discSpin, 360, 14, turn, fps); }
        if (big == _hudTurning) return;
        _hudTurning = big;
        // the big one's HUD ring and breathing glow: only while it's on screen
        Rotate(_hudSpin, -360, 30, big, 30);
        if (big)
        {
            var breathe = new DoubleAnimation(0.65, 1, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Timeline.SetDesiredFrameRate(breathe, 30);
            DiscGlow.BeginAnimation(OpacityProperty, breathe);
        }
        else DiscGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, TimeSpan.FromMilliseconds(400)));

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
