using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicNotch;

/// <summary>
/// The cyberpunk theme's album cover: the art on a round record that turns slowly while the music plays (one turn
/// every 14 s) and stops where it is when you pause, a soft glow behind it in the album's own colour, and a dashed
/// HUD ring with a yellow tick turning the other way. The closed notch's little cover is the same record in small,
/// turning with it. No blur effects (the glows are gradients), 30 frames a second at most, and nothing turns unless
/// a record is on screen, the music plays and the PC isn't saving power. Every other theme keeps the square covers.
/// </summary>
public partial class NotchWindow
{
    readonly RotateTransform _discSpin = new(), _hudSpin = new();
    bool _discWired, _discTurning, _hudTurning;
    ImageSource? _discArtFor;

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
            ArtDisc.IsVisibleChanged += (_, _) => SpinDisc();
            MiniDisc.IsVisibleChanged += (_, _) => SpinDisc();
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
        bool playing = _cyber && _media.IsPlaying && !Eco && !_offScreen;
        bool big = playing && ArtDisc.IsVisible;
        bool turn = big || (playing && MiniDisc.IsVisible);
        if (turn != _discTurning) { _discTurning = turn; Rotate(_discSpin, 360, 14, turn); }
        if (big == _hudTurning) return;
        _hudTurning = big;
        // the big one's HUD ring and breathing glow: only while it's on screen
        Rotate(_hudSpin, -360, 30, big);
        if (big)
        {
            var breathe = new DoubleAnimation(0.65, 1, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Timeline.SetDesiredFrameRate(breathe, 30);
            DiscGlow.BeginAnimation(OpacityProperty, breathe);
        }
        else DiscGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, TimeSpan.FromMilliseconds(400)));

        static void Rotate(RotateTransform r, double by, double seconds, bool on)
        {
            double at = r.Angle % 360; // the angle on screen right now (the animated one)
            r.BeginAnimation(RotateTransform.AngleProperty, null);
            r.Angle = at;
            if (!on) return;
            var spin = new DoubleAnimation(at, at + by, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, 30);
            r.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
    }
}
