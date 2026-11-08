using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DynamicNotch.Controls;

/// <summary>
/// A theme's living background (particles, stars, bubbles, glitter, light), built to be smooth and nearly free.
/// It's made of sheets: each one drawn once into a cached picture, then only slid (a loop with no seam: the sheet
/// holds its items twice, one screen apart), swayed or faded by the graphics chip, in time with the screen. A sheet
/// costs a couple of numbers a frame however many specks are on it. On top, now and then, one sparkle flares
/// somewhere and goes out. Everything pauses where it is when stopped: nothing moves, nothing asks for frames.
/// </summary>
public sealed class Ambient : Grid
{
    readonly double _w, _h;
    readonly Random _rand = new();
    readonly List<AnimationClock> _clocks = new();
    readonly List<Action> _starters = new();
    readonly DispatcherTimer _flare;
    Canvas? _flareLayer;
    Func<Random, (Brush Fill, double Size)>? _flareLook;
    bool _running, _started;

    public Ambient(double width, double height)
    {
        _w = width;
        _h = height;
        Width = width;
        Height = height;
        IsHitTestVisible = false;
        ClipToBounds = true;
        _flare = new DispatcherTimer(DispatcherPriority.Background);
        _flare.Tick += (_, _) => Flare();
    }

    public bool Running => _running;

    /// <summary>
    /// A sheet of things that drift: up (negative speed) or down, swaying sideways, its whole sheet breathing between
    /// two opacities. Speed 0: they stay where they are and only breathe (stars).
    /// </summary>
    public void AddSheet(Func<Random, FrameworkElement> make, int count, double speed, double sway = 0, double swaySeconds = 8,
        double opacity = 1, double pulseLow = -1, double pulseSeconds = 3, double pulseDelay = 0, int fps = 30)
    {
        bool slides = Math.Abs(speed) > 0.01;
        var sheet = new Canvas { Width = _w, Height = slides ? _h * 2 : _h, Opacity = opacity, CacheMode = new BitmapCache(1.25) };
        for (int i = 0; i < count; i++)
        {
            double x = _rand.NextDouble() * _w, y = _rand.NextDouble() * _h;
            for (int k = 0; k < (slides ? 2 : 1); k++)
            {
                var el = make(_rand);
                Canvas.SetLeft(el, x - el.Width / 2);
                Canvas.SetTop(el, y + k * _h - el.Height / 2);
                sheet.Children.Add(el);
            }
        }
        var slide = new TranslateTransform();
        var swayT = new TranslateTransform();
        sheet.RenderTransform = new TransformGroup { Children = { slide, swayT } };
        Children.Add(sheet);

        _starters.Add(() =>
        {
            if (slides)
            {
                // up: from 0 to one screen higher; down: from one screen higher to 0. The copies take over exactly
                double from = speed < 0 ? 0 : -_h, to = speed < 0 ? -_h : 0;
                var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(_h / Math.Abs(speed))) { RepeatBehavior = RepeatBehavior.Forever };
                Run(slide, TranslateTransform.YProperty, a, fps);
            }
            if (sway > 0)
            {
                var s = new DoubleAnimation(-sway, sway, TimeSpan.FromSeconds(swaySeconds))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                Run(swayT, TranslateTransform.XProperty, s, Math.Min(fps, 30));
            }
            if (pulseLow >= 0)
            {
                var p = new DoubleAnimation(opacity, pulseLow, TimeSpan.FromSeconds(pulseSeconds))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(pulseDelay), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                Run(sheet, OpacityProperty, p, 20);
            }
        });
    }

    /// <summary>A soft band of light that crosses the whole background now and then (a reflection on glass).</summary>
    public void AddSweep(Brush band, double bandWidth, double angle, double crossSeconds, double everySeconds)
    {
        var b = new Border { Width = bandWidth, Height = _h * 2, Background = band, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        var move = new TranslateTransform(-bandWidth * 2, 0);
        b.RenderTransform = new TransformGroup { Children = { new RotateTransform(angle, bandWidth / 2, _h), move } };
        b.CacheMode = new BitmapCache();
        Children.Add(b);
        _starters.Add(() =>
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(everySeconds), RepeatBehavior = RepeatBehavior.Forever };
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(-bandWidth * 2, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(_w + bandWidth, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(crossSeconds)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(_w + bandWidth, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(everySeconds))));
            Run(move, TranslateTransform.XProperty, a, 40);
        });
    }

    /// <summary>Now and then (every few seconds) one sparkle flares up somewhere, turns a little and goes out.</summary>
    public void AddFlares(Func<Random, (Brush Fill, double Size)> look, double everySeconds)
    {
        _flareLook = look;
        _flareEvery = everySeconds;
        _flareLayer = new Canvas();
        Children.Add(_flareLayer);
        _flare.Interval = TimeSpan.FromSeconds(everySeconds * 0.5);
    }

    double _flareEvery = 3;

    void Flare()
    {
        if (_flareLayer == null || _flareLook == null) return;
        _flare.Interval = TimeSpan.FromSeconds(_flareEvery * (0.6 + _rand.NextDouble() * 0.8)); // irregular, like real glints
        var (fill, size) = _flareLook(_rand);
        var scale = new ScaleTransform(0, 0);
        var turn = new RotateTransform();
        var star = new Path
        {
            Data = Star, Fill = fill, Width = size, Height = size, Stretch = Stretch.Fill,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new TransformGroup { Children = { scale, turn } },
        };
        Canvas.SetLeft(star, 12 + _rand.NextDouble() * (_w - 24) - size / 2);
        Canvas.SetTop(star, 10 + _rand.NextDouble() * (_h - 20) - size / 2);
        _flareLayer.Children.Add(star);
        var life = TimeSpan.FromMilliseconds(1100);
        var grow = new DoubleAnimationUsingKeyFrames { Duration = life };
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.35), new CubicEase { EasingMode = EasingMode.EaseOut }));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseIn }));
        grow.Completed += (_, _) => _flareLayer.Children.Remove(star);
        Timeline.SetDesiredFrameRate(grow, 40);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var spin = new DoubleAnimation(0, 60, life);
        Timeline.SetDesiredFrameRate(spin, 40);
        turn.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    static readonly Geometry Star = Freeze(Geometry.Parse("M10,0 C10.6,7 13,9.4 20,10 C13,10.6 10.6,13 10,20 C9.4,13 7,10.6 0,10 C7,9.4 9.4,7 10,0 Z"));
    static Geometry Freeze(Geometry g) { g.Freeze(); return g; }

    void Run(Animatable target, DependencyProperty prop, AnimationTimeline a, int fps)
    {
        Timeline.SetDesiredFrameRate(a, fps);
        var clock = a.CreateClock();
        target.ApplyAnimationClock(prop, clock);
        _clocks.Add(clock);
    }

    void Run(UIElement target, DependencyProperty prop, AnimationTimeline a, int fps)
    {
        Timeline.SetDesiredFrameRate(a, fps);
        var clock = a.CreateClock();
        target.ApplyAnimationClock(prop, clock);
        _clocks.Add(clock);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        if (!_started) { _started = true; foreach (var s in _starters) s(); }
        else foreach (var c in _clocks) c.Controller?.Resume();
        if (_flareLayer != null) _flare.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        foreach (var c in _clocks) c.Controller?.Pause();
        _flare.Stop();
        _flareLayer?.Children.Clear();
    }

    // ───── the things on the sheets ─────

    /// <summary>A soft glowing dot (plankton, a mote of light).</summary>
    public static FrameworkElement Glow(double size, Color c) => new Ellipse
    {
        Width = size, Height = size,
        Fill = Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0xFF, c.R, c.G, c.B), 0), new(Color.FromArgb(0x80, c.R, c.G, c.B), 0.35), new(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
        })),
    };

    /// <summary>A crisp pin-prick of light (a star, a fleck of frost).</summary>
    public static FrameworkElement Speck(double size, Color c) => new Ellipse { Width = size, Height = size, Fill = Frozen(new SolidColorBrush(c)) };

    /// <summary>A tiny four-pointed star.</summary>
    public static FrameworkElement Twinkle(double size, Color c) => new Path
    {
        Data = Star, Width = size, Height = size, Stretch = Stretch.Fill,
        Fill = Frozen(new RadialGradientBrush(Color.FromArgb(0xFF, c.R, c.G, c.B), Color.FromArgb(0x60, c.R, c.G, c.B))),
    };

    /// <summary>A glass bubble: a thin bright ring with a highlight.</summary>
    public static FrameworkElement Bubble(double size, Color c) => new Ellipse
    {
        Width = size, Height = size, StrokeThickness = 0.8,
        Stroke = Frozen(new SolidColorBrush(Color.FromArgb(0x90, c.R, c.G, c.B))),
        Fill = Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF), 0), new(Color.FromArgb(0x08, c.R, c.G, c.B), 0.7), new(Color.FromArgb(0x20, c.R, c.G, c.B), 1),
        }) { GradientOrigin = new Point(0.35, 0.3) }),
    };

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
}
