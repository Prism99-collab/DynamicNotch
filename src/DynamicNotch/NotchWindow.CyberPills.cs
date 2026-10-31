using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// The cyberpunk skin, finishing touches:
///  • the side pills (second activity, mic / camera) get their own cut plate and frame, the same shape language
///    as the notch, drawn behind their content. They are never clipped, so their ears, glow and the liquid neck
///    to the notch stay intact. The frame wears the pill's colour, and flickers on when a pill appears;
///  • a data blip: every few seconds a short pulse of light runs along the notch's bottom frame (idle, music,
///    activities; never slim, hidden, in a game or on battery mode);
///  • the HUD tag names the page you're on (HOME, SYS.MON, TOOLS, DATA.LINK, CHROMA, CONFIG).
/// </summary>
public partial class NotchWindow
{
    sealed class Plate
    {
        public Border Bubble = null!, Rim = null!;
        public Path Fill = null!, Frame = null!;
        public Brush? BackgroundBefore;
    }

    readonly List<Plate> _plates = new();
    static readonly Dictionary<Border, Plate> _plateByRim = new();
    static readonly string[] PageCodes = { "HOME", "SYS.MON", "TOOLS", "DATA.LINK", "CHROMA", "CONFIG" };

    Rectangle _cyBlip = null!;
    readonly TranslateTransform _cyBlipShift = new();
    readonly DispatcherTimer _blipTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(7) };

    void BuildCyberPills()
    {
        foreach (var (bubble, rim) in new[] { (SecondBubble, SecondRim), (PrivacyBubble, PrivacyRim) })
        {
            if (bubble.Child is not Grid g) continue;
            var pl = new Plate
            {
                Bubble = bubble, Rim = rim,
                Fill = new Path { Fill = Brushes.Black, IsHitTestVisible = false, Visibility = Visibility.Collapsed },
                Frame = new Path { Stroke = CyYellowBrush, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Miter, IsHitTestVisible = false, Visibility = Visibility.Collapsed },
            };
            // the plate covers the whole pill, padding included (its content sits inside the padding). It's drawn on
            // canvases, which take no room: the pill keeps exactly its own size (a shape sized from the pill would
            // make the pill grow, and the shape with it, again and again)
            var m = new Thickness(-bubble.Padding.Left, 0, 0, 0);
            var under = new Canvas { Margin = m, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var over = new Canvas { Margin = m, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            under.Children.Add(pl.Fill);
            over.Children.Add(pl.Frame);
            int rimAt = g.Children.IndexOf(rim);
            g.Children.Insert(Math.Max(0, rimAt), under);     // above the glow and the ears, under the content
            g.Children.Insert(g.Children.IndexOf(rim) + 1, over);
            bubble.SizeChanged += (_, _) => ShapePlate(pl);
            bubble.IsVisibleChanged += (_, e) =>
            {
                // switching on: a short flicker of its frame, like a HUD panel coming up
                if (_cyber && !Eco && e.NewValue is true) Steps(pl.Frame, OpacityProperty, 34, 0, 1, 0.2, 1, 0.5, 1);
            };
            _plates.Add(pl);
            _plateByRim[rim] = pl;
        }

        // the data blip: a bright sliver running along the bottom frame
        var blip = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        blip.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        blip.GradientStops.Add(new GradientStop(CyCyan, 0.55));
        blip.GradientStops.Add(new GradientStop(Colors.White, 0.85));
        blip.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        blip.Freeze();
        _cyBlip = new Rectangle { Width = 26, Height = 2, Fill = blip, Opacity = 0, RenderTransform = _cyBlipShift };
        _hud.Children.Add(_cyBlip);
        _blipTimer.Tick += (_, _) => CyberBlip();
    }

    /// <summary>The theme was switched: the pills take their plates (or give them back).</summary>
    void CyberPills(bool on)
    {
        foreach (var pl in _plates)
        {
            if (on)
            {
                pl.BackgroundBefore ??= pl.Bubble.Background;
                pl.Bubble.Background = Brushes.Transparent;
                pl.Rim.Visibility = Visibility.Collapsed;
                pl.Fill.Visibility = pl.Frame.Visibility = Visibility.Visible;
                ShapePlate(pl);
            }
            else
            {
                pl.Bubble.Background = pl.BackgroundBefore ?? Brushes.Black;
                pl.Rim.Visibility = Visibility.Visible;
                pl.Fill.Visibility = pl.Frame.Visibility = Visibility.Collapsed;
            }
        }
        if (on) _blipTimer.Start(); else { _blipTimer.Stop(); _cyBlip.BeginAnimation(OpacityProperty, null); _cyBlip.Opacity = 0; }
    }

    void ShapePlate(Plate pl)
    {
        if (!_cyber) return;
        double w = pl.Bubble.ActualWidth, h = pl.Bubble.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double c = Math.Min(8, h * 0.32);
        pl.Fill.Data = Poly(true, new(0, -1), new(w, -1), new(w, h - c), new(w - c, h), new(c, h), new(0, h - c));
        pl.Frame.Data = Poly(false, new(0, 0), new(0, h - c), new(c, h), new(w - c, h), new(w, h - c), new(w, 0));
    }

    /// <summary>The pill's colour reaches its frame (called with every rim colour change).</summary>
    static void PlateColor(Border rim, Color c)
    {
        if (_plateByRim.TryGetValue(rim, out var pl)) pl.Frame.Stroke = Frozen(Color.FromArgb(0xE6, c.R, c.G, c.B));
    }

    /// <summary>A pulse of light along the bottom frame, every 6–11 s while the notch rests.</summary>
    void CyberBlip()
    {
        _blipTimer.Interval = TimeSpan.FromSeconds(6 + _cyRand.NextDouble() * 5);
        if (!_cyber || Eco || _hidden || _mode is not (Mode.Idle or Mode.Media or Mode.Activity)) return;
        double w = Pill.ActualWidth, h = Pill.ActualHeight;
        if (w < 60 || h < 8) return;
        Canvas.SetTop(_cyBlip, h - 2);
        var run = new DoubleAnimation(4, w * 0.6 - _cyBlip.Width, TimeSpan.FromMilliseconds(560)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        _cyBlipShift.BeginAnimation(TranslateTransform.XProperty, run);
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(560) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(560))));
        _cyBlip.BeginAnimation(OpacityProperty, fade);
    }

    void CyberTag()
    {
        if (_cyber) _cyTag.Text = "DN-2077 // " + PageCodes[Math.Clamp(_page, 0, PageCodes.Length - 1)];
    }
}
