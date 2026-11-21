using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DynamicNotch.Controls;

/// <summary>
/// Rounded slider (volume, brightness, track progress). Drag, click or scroll. It comes alive under the pointer:
/// the track thickens a little on hover and more while dragging, and a soft knob rides the end of the fill.
/// </summary>
public enum SliderLook { Pill, Wave, Bars, Sun, Dots }

public sealed class PillSlider : Grid
{
    readonly Border _track = new(), _fill = new();
    readonly Rectangle _knob = new();
    readonly RotateTransform _knobTurn = new();

    /// <summary>The neon skin: square track and fill, a yellow diamond knob.</summary>
    public static bool Square;
    readonly ScaleTransform _knobScale = new(0, 0);
    readonly TranslateTransform _knobShift = new();

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(PillSlider), new PropertyMetadata(0.0, (d, _) => ((PillSlider)d).UpdateFill()));

    public static readonly DependencyProperty TrackHeightProperty = DependencyProperty.Register(
        nameof(TrackHeight), typeof(double), typeof(PillSlider), new PropertyMetadata(6.0, (d, _) => ((PillSlider)d).ApplyHeights()));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(PillSlider), new PropertyMetadata(Brushes.White, (d, e) => { var s = (PillSlider)d; s._fill.Background = (Brush)e.NewValue; if (s._wave != null) s._wave.Stroke = (Brush)e.NewValue; s._art?.InvalidateVisual(); }));

    /// <summary>Extra thickness while hovered / dragged (animated).</summary>
    static readonly DependencyProperty GrowProperty = DependencyProperty.Register(
        "Grow", typeof(double), typeof(PillSlider), new PropertyMetadata(0.0, (d, _) => { var s = (PillSlider)d; s.ApplyHeights(); s._art?.InvalidateVisual(); }));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double TrackHeight { get => (double)GetValue(TrackHeightProperty); set => SetValue(TrackHeightProperty, value); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    double Grow => (double)GetValue(GrowProperty);

    public bool IsInteractive { get; set; } = true;
    public bool IsDragging { get; private set; }

    /// <summary>Raised continuously while the user drags.</summary>
    public event Action<double>? ValueChanged;
    /// <summary>Raised when the user releases (or scrolls).</summary>
    public event Action<double>? ValueCommitted;

    public PillSlider()
    {
        Background = Brushes.Transparent; // whole row is hit-testable, not just the thin bar
        _track.Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        _track.VerticalAlignment = VerticalAlignment.Center;
        _fill.Background = FillBrush;
        _fill.HorizontalAlignment = HorizontalAlignment.Left;
        _fill.VerticalAlignment = VerticalAlignment.Center;

        // the knob: hidden until the pointer is over the slider, then it grows in at the end of the fill
        _knob.Fill = Brushes.White;
        _knob.HorizontalAlignment = HorizontalAlignment.Left;
        _knob.VerticalAlignment = VerticalAlignment.Center;
        _knob.IsHitTestVisible = false;
        _knob.Visibility = Visibility.Collapsed; // out of the render pass entirely until the pointer arrives
        _knob.RenderTransformOrigin = new Point(0.5, 0.5);
        _knob.RenderTransform = new TransformGroup { Children = { _knobTurn, _knobScale, _knobShift } };
        _knob.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Direction = 270, Opacity = 0.45, Color = Colors.Black };

        Children.Add(_track);
        Children.Add(_fill);
        Children.Add(_knob);
        ApplyHeights();
        IsVisibleChanged += (_, _) => RunWave();
    }

    // ───────────────────────────── the looks ─────────────────────────────

    SliderLook _look;
    SliderArt? _art;
    Canvas? _waveHost;
    Path? _wave;
    readonly ScaleTransform _waveAmp = new(1, 0);
    readonly TranslateTransform _wavePhase = new();
    readonly RectangleGeometry _waveClip = new();
    Border? _head;
    bool _live, _waveRunning;
    const double WaveLength = 22, WaveAmp = 2.7;

    /// <summary>
    /// How it looks: a pill (default); Wave (a song's progress: a line that flows while it plays, flat when paused,
    /// with a playhead); Bars (a rising wedge of bars: volume); Sun (a warm ramp of light with a sun that shines more
    /// the brighter it is: brightness); Dots (a row of dots lighting up: an app's own volume). Same hand on all.
    /// </summary>
    public SliderLook Look
    {
        get => _look;
        set
        {
            if (_look == value) return;
            _look = value;
            bool drawn = value is SliderLook.Bars or SliderLook.Sun or SliderLook.Dots;
            _fill.Visibility = value == SliderLook.Pill ? Visibility.Visible : Visibility.Collapsed;
            _track.Visibility = value is SliderLook.Pill or SliderLook.Wave ? Visibility.Visible : Visibility.Collapsed;
            if (drawn && _art == null) { _art = new SliderArt(this); Children.Insert(0, _art); }
            if (_art != null) _art.Visibility = drawn ? Visibility.Visible : Visibility.Collapsed;
            if (value == SliderLook.Wave && _waveHost == null) BuildWave();
            if (_waveHost != null) _waveHost.Visibility = value == SliderLook.Wave ? Visibility.Visible : Visibility.Collapsed;
            if (_head != null) _head.Visibility = value == SliderLook.Wave ? Visibility.Visible : Visibility.Collapsed;
            if (value == SliderLook.Wave) ShapeWave();
            ApplyHeights();
        }
    }

    /// <summary>Wave: the song is playing (and on screen): the line flows. Off: it eases flat.</summary>
    public bool Live
    {
        get => _live;
        set
        {
            if (_live == value) return;
            _live = value;
            if (_waveHost == null) return;
            var amp = new DoubleAnimation(value ? 1 : 0, TimeSpan.FromMilliseconds(value ? 520 : 360)) { EasingFunction = value ? new SpringEase { Damping = 0.62 } : new CubicEase { EasingMode = EasingMode.EaseOut } };
            if (!value) amp.Completed += (_, _) => RunWave();
            _waveAmp.BeginAnimation(ScaleTransform.ScaleYProperty, amp);
            RunWave();
        }
    }

    void BuildWave()
    {
        _wave = new Path { StrokeThickness = 2.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        _wave.RenderTransform = new TransformGroup { Children = { _waveAmp, _wavePhase } };
        _waveHost = new Canvas { IsHitTestVisible = false, Clip = _waveClip };
        _waveHost.Children.Add(_wave);
        _head = new Border
        {
            Width = 3.5, Height = 12, CornerRadius = new CornerRadius(1.75), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false, RenderTransform = new TranslateTransform(), Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black },
        };
        Children.Add(_waveHost);
        Children.Add(_head);
    }

    void ShapeWave()
    {
        if (_wave == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        double mid = ActualHeight / 2, w = ActualWidth;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            bool first = true;
            for (double x = -WaveLength; x <= w + WaveLength; x += Square ? WaveLength / 4 : 1.5)
            {
                double ph = x / WaveLength;
                double y = Square ? Math.Abs((ph % 1 + 1) % 1 * 4 - 2) - 1 : Math.Sin(ph * Math.PI * 2); // Neon: a sharp zigzag
                var pt = new Point(x, mid + y * WaveAmp);
                if (first) { c.BeginFigure(pt, false, false); first = false; }
                else c.LineTo(pt, true, true);
            }
        }
        g.Freeze();
        _wave.Data = g;
        _waveAmp.CenterY = mid;
        _wave.Stroke = FillBrush;
    }

    void RunWave()
    {
        bool want = _waveHost != null && _look == SliderLook.Wave && _live && IsVisible;
        if (want == _waveRunning) return;
        _waveRunning = want;
        if (want)
        {
            var flow = new DoubleAnimation(0, -WaveLength, TimeSpan.FromMilliseconds(1400)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(flow, 30); // a gentle flow: 30 steps a second is plenty
            _wavePhase.BeginAnimation(TranslateTransform.XProperty, flow);
        }
        else _wavePhase.BeginAnimation(TranslateTransform.XProperty, null);
    }

    /// <summary>The slider's colour along its length (0..1), from its fill (a gradient or one colour).</summary>
    internal Color ColorAt(double t)
    {
        if (FillBrush is SolidColorBrush sb) return sb.Color;
        if (FillBrush is GradientBrush gb && gb.GradientStops.Count > 0)
        {
            var stops = gb.GradientStops.OrderBy(x => x.Offset).ToList();
            if (t <= stops[0].Offset) return stops[0].Color;
            for (int i = 1; i < stops.Count; i++)
                if (t <= stops[i].Offset)
                {
                    double k = (t - stops[i - 1].Offset) / Math.Max(1e-6, stops[i].Offset - stops[i - 1].Offset);
                    return Mix(stops[i - 1].Color, stops[i].Color, k);
                }
            return stops[^1].Color;
        }
        return Colors.White;
    }

    internal static Color Mix(Color a, Color b, double k) => Color.FromArgb((byte)(a.A + (b.A - a.A) * k), (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
    internal double GrowNow => Grow;
    internal bool Hot => IsMouseOver || IsDragging;

    void ApplyHeights()
    {
        if (_look is SliderLook.Bars or SliderLook.Sun or SliderLook.Dots) { UpdateFill(); return; }
        double h = _look == SliderLook.Wave ? 2.4 + Grow * 0.4 : TrackHeight + Grow;
        _track.Height = _fill.Height = h;
        _track.CornerRadius = _fill.CornerRadius = new CornerRadius(Square ? 0 : h / 2);
        double k = Square ? Math.Round((TrackHeight + 8) * 0.78) : TrackHeight + 8;
        _knob.Width = _knob.Height = k;
        _knob.RadiusX = _knob.RadiusY = Square ? 0 : k / 2;
        _knobTurn.Angle = Square ? 45 : 0;
        UpdateFill();
    }

    /// <summary>The skin changed (see <see cref="Square"/>).</summary>
    public void Restyle()
    {
        _knob.Fill = Square ? KnobYellow : Brushes.White;
        _track.Background = Square ? CyTrack : WhiteTrack;
        if (_head != null) _head.CornerRadius = new CornerRadius(Square ? 0 : 1.75);
        ShapeWave();
        _art?.InvalidateVisual();
        ApplyHeights();
    }

    static readonly Brush KnobYellow = Frozen(new SolidColorBrush(Color.FromRgb(0xFE, 0xE8, 0x01)));
    static readonly Brush CyTrack = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x3C, 0xD5, 0xFF)));
    static readonly Brush WhiteTrack = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));
    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    void UpdateFill()
    {
        double v = Math.Clamp(Value, 0, 1);
        if (_look is SliderLook.Bars or SliderLook.Sun or SliderLook.Dots) { _art?.InvalidateVisual(); return; }
        if (_look == SliderLook.Wave)
        {
            // played: the wave, up to the playhead; ahead: a quiet straight line
            double x = v * ActualWidth, hw = _head?.Width ?? 3.5;
            double hx = Math.Clamp(x - hw / 2, 0, Math.Max(0, ActualWidth - hw));
            if (_head != null) ((TranslateTransform)_head.RenderTransform).X = hx;
            _waveClip.Rect = new Rect(0, -4, Math.Max(0, hx - 2), Math.Max(1, ActualHeight + 8));
            _track.Margin = new Thickness(Math.Min(ActualWidth, hx + hw + 2), 0, 0, 0);
            return;
        }
        double h = TrackHeight + Grow;
        double w = Math.Max(h, v * ActualWidth);
        _fill.Width = w;
        _fill.Opacity = v <= 0.001 ? 0 : 1;
        // the knob's centre sits on the end of the fill, kept inside the slider
        double k = _knob.Width;
        _knobShift.X = Math.Clamp(v * ActualWidth - k / 2, 0, Math.Max(0, ActualWidth - k));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_look == SliderLook.Wave) ShapeWave();
        UpdateFill();
    }

    // ───── alive under the pointer ─────

    void Settle()
    {
        if (!IsInteractive) return;
        double grow = IsDragging ? 3 : IsMouseOver ? 1.5 : 0;
        double knob = _look != SliderLook.Pill ? 0 : IsDragging ? 1.12 : IsMouseOver ? 1 : 0; // the other looks carry their own
        if (_head != null && _look == SliderLook.Wave)
        {
            // the playhead grows under the pointer
            _head.BeginAnimation(HeightProperty, new DoubleAnimation(IsDragging ? 15 : IsMouseOver ? 14 : 12, TimeSpan.FromMilliseconds(260)) { EasingFunction = new SpringEase { Damping = 0.6 } });
            _head.BeginAnimation(WidthProperty, new DoubleAnimation(IsMouseOver || IsDragging ? 4.5 : 3.5, TimeSpan.FromMilliseconds(200)));
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new SpringEase { Damping = 0.6 };
        BeginAnimation(GrowProperty, new DoubleAnimation(grow, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        var k = new DoubleAnimation(knob, TimeSpan.FromMilliseconds(knob > 0 ? 380 : 160)) { EasingFunction = knob > 0 ? spring : ease };
        if (knob > 0) _knob.Visibility = Visibility.Visible;
        else k.Completed += (_, _) => { if (!IsMouseOver && !IsDragging) _knob.Visibility = Visibility.Collapsed; };
        _knobScale.BeginAnimation(ScaleTransform.ScaleXProperty, k);
        _knobScale.BeginAnimation(ScaleTransform.ScaleYProperty, k);
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); Settle(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); Settle(); }

    /// <summary>
    /// Moves to a value smoothly over <paramref name="seconds"/> (a playing song's progress between two ticks), at
    /// only as many frames as the movement needs: a long song's bar crawls a pixel a second and costs nothing.
    /// A jump (a seek, a new song) or a still bar is set at once. Never while you drag it.
    /// </summary>
    public void Glide(double to, double seconds)
    {
        if (IsDragging) return;
        to = Math.Clamp(to, 0, 1);
        double from = Value; // where it is on screen right now
        double px = Math.Abs(to - from) * ActualWidth;
        if (seconds <= 0 || px < 0.5 || px > 30)
        {
            BeginAnimation(ValueProperty, null);
            Value = to;
            return;
        }
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds));
        Timeline.SetDesiredFrameRate(a, (int)Math.Clamp(px / seconds * 2, 2, 30));
        BeginAnimation(ValueProperty, a);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsInteractive) return;
        BeginAnimation(ValueProperty, null); // a glide in progress stops under your finger
        CaptureMouse();
        IsDragging = true;
        Settle();
        SetFromMouse(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsDragging) SetFromMouse(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsDragging) return;
        IsDragging = false;
        ReleaseMouseCapture();
        Settle();
        ValueCommitted?.Invoke(Value);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!IsDragging) return;
        IsDragging = false;
        Settle();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!IsInteractive) return;
        double from = Value;
        BeginAnimation(ValueProperty, null);
        Value = Math.Clamp(from + e.Delta / 120.0 * 0.04, 0, 1);
        ValueChanged?.Invoke(Value);
        ValueCommitted?.Invoke(Value);
        e.Handled = true;
    }

    void SetFromMouse(MouseEventArgs e)
    {
        if (ActualWidth <= 0) return;
        Value = Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1);
        ValueChanged?.Invoke(Value);
    }
}

/// <summary>The drawn looks (Bars, Sun, Dots): painted once per change of value, colour, size or hover (nothing runs between).</summary>
sealed class SliderArt : FrameworkElement
{
    readonly PillSlider _s;
    public SliderArt(PillSlider s) { _s = s; IsHitTestVisible = false; }

    static readonly Dictionary<Color, SolidColorBrush> Cache = new();
    static SolidColorBrush B(Color c)
    {
        if (Cache.TryGetValue(c, out var b)) return b;
        if (Cache.Count > 512) Cache.Clear();
        b = new SolidColorBrush(c);
        b.Freeze();
        return Cache[c] = b;
    }
    static Color A(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>
    /// The level's colour from low (0) to high (1), grown from the slider's own colour (the panel's, or yours): the
    /// hue turns from a cooler neighbour to a warmer one and the colour gets brighter as it rises, so the bar reads
    /// as "more" at a glance. A colourless theme (white, grey) gets a gentle teal → violet → pink ramp instead.
    /// Brightness rises from a deep warm tone to almost white light.
    /// </summary>
    Color Ramp(double t, bool light)
    {
        var baseC = _s.ColorAt(1);
        ToHsl(baseC, out double h, out double sat, out double l);
        bool grey = sat < 0.18;
        if (light)
        {
            if (grey) { h = 35; sat = 0.85; }
            double hue = h - 18 + 30 * t;
            var c = FromHsl(hue, Math.Clamp(Math.Max(sat, 0.6) - 0.25 * t, 0, 1), 0.36 + 0.46 * t);
            return t > 0.75 ? PillSlider.Mix(c, Colors.White, (t - 0.75) / 0.25 * 0.45) : c;
        }
        if (grey) return FromHsl(170 + 150 * t, 0.62, 0.5 + 0.14 * t);
        return FromHsl(h - 48 + 78 * t, Math.Clamp(Math.Max(sat, 0.55), 0, 0.95), 0.42 + 0.24 * t);
    }

    static void ToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        l = (max + min) / 2;
        s = d < 1e-6 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        h = d < 1e-6 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    static Color FromHsl(double h, double s, double l)
    {
        h = (h % 360 + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        (double r, double g, double b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return Color.FromRgb((byte)Math.Clamp((r + m) * 255, 0, 255), (byte)Math.Clamp((g + m) * 255, 0, 255), (byte)Math.Clamp((b + m) * 255, 0, 255));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 4 || h <= 2) return;
        double v = Math.Clamp(_s.Value, 0, 1), grow = _s.GrowNow;
        bool sq = PillSlider.Square;
        var dim = sq ? Color.FromArgb(0x48, 0x3C, 0xD5, 0xFF) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        switch (_s.Look)
        {
            case SliderLook.Bars:
            {
                // a rising wedge: short bars at the quiet end, tall at the loud end; lit up to the volume
                const double bw = 3, gap = 2.2;
                int n = Math.Max(4, (int)((w + gap) / (bw + gap)));
                double step = (w - bw) / (n - 1), area = Math.Min(h - 4, 14 + grow * 1.2), bottom = h / 2 + area / 2;
                for (int i = 0; i < n; i++)
                {
                    double x = i * step, t = (double)i / (n - 1);
                    double bh = area * (0.38 + 0.62 * t);
                    double lit = Math.Clamp((v * w - x) / bw, 0, 1); // the bar under the edge lights partly
                    var on = sq ? PillSlider.Mix(_s.ColorAt(t), Colors.White, 0.3 + 0.25 * t) : Ramp(t, light: false); // low to high: cooler to warmer, dim to bright
                    var c = lit >= 1 ? on : lit <= 0 ? dim : PillSlider.Mix(dim, on, lit);
                    if (lit > 0 && _s.Hot) c = PillSlider.Mix(c, Colors.White, 0.18);
                    double r = sq ? 0 : bw / 2;
                    dc.DrawRoundedRectangle(B(c), null, new Rect(x, bottom - bh, bw, bh), r, r);
                }
                break;
            }
            case SliderLook.Sun:
            {
                // a warm ramp of light, and a sun at its end whose rays reach further the brighter it is
                double th = _s.TrackHeight + grow, mid = h / 2, rr = sq ? 0 : th / 2;
                double core = (_s.Hot ? 4.8 : 4.1) + grow * 0.2, ray = 1.4 + 3.2 * v, reach = core + 2 + ray;
                double fx = Math.Clamp(v * w, reach, w - reach);
                dc.DrawRoundedRectangle(B(dim), null, new Rect(0, mid - th / 2, w, th), rr, rr);
                var ramp = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(w, 0), MappingMode = BrushMappingMode.Absolute };
                if (sq) { ramp.GradientStops.Add(new GradientStop(PillSlider.Mix(_s.ColorAt(0), Color.FromRgb(0xFF, 0xA8, 0x45), 0.45), 0)); ramp.GradientStops.Add(new GradientStop(PillSlider.Mix(_s.ColorAt(1), Colors.White, 0.55), 1)); }
                else for (int k = 0; k <= 4; k++) ramp.GradientStops.Add(new GradientStop(Ramp(k / 4.0, light: true), k / 4.0));
                ramp.Freeze();
                var to = sq ? PillSlider.Mix(_s.ColorAt(1), Colors.White, 0.55) : Ramp(v, light: true);
                dc.DrawRoundedRectangle(ramp, null, new Rect(0, mid - th / 2, Math.Max(th, fx), th), rr, rr);
                var glow = new RadialGradientBrush(A(to, 0.22 + 0.33 * v), A(to, 0));
                glow.Freeze();
                dc.DrawEllipse(glow, null, new Point(fx, mid), reach + 4, reach + 4);
                var pen = new Pen(B(sq ? Color.FromRgb(0xFE, 0xE8, 0x01) : Colors.White), 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();
                int rays = sq ? 4 : 8;
                for (int i = 0; i < rays; i++)
                {
                    double a = i * Math.PI * 2 / rays + (sq ? Math.PI / 4 : 0);
                    double cx = Math.Cos(a), cy = Math.Sin(a);
                    dc.DrawLine(pen, new Point(fx + cx * (core + 2), mid + cy * (core + 2)), new Point(fx + cx * reach, mid + cy * reach));
                }
                if (sq)
                {
                    var d = new StreamGeometry();
                    using (var c = d.Open()) { c.BeginFigure(new Point(fx, mid - core), true, true); c.LineTo(new Point(fx + core, mid), true, false); c.LineTo(new Point(fx, mid + core), true, false); c.LineTo(new Point(fx - core, mid), true, false); }
                    d.Freeze();
                    dc.DrawGeometry(B(Color.FromRgb(0xFE, 0xE8, 0x01)), null, d);
                }
                else dc.DrawEllipse(B(Colors.White), null, new Point(fx, mid), core, core);
                break;
            }
            case SliderLook.Dots:
            {
                // a row of dots, lit up to the level
                double d = (_s.Hot ? 6 : 5) + grow * 0.3;
                int n = Math.Max(5, Math.Min(14, (int)(w / 8.5)));
                double step = (w - d) / (n - 1), mid = h / 2;
                for (int i = 0; i < n; i++)
                {
                    double x = i * step, t = (double)i / (n - 1);
                    double lit = Math.Clamp((v * w - x) / d, 0, 1);
                    var on = sq ? _s.ColorAt(t) : Ramp(t, light: false);
                    var c = lit >= 1 ? on : lit <= 0 ? dim : PillSlider.Mix(dim, on, lit);
                    double size = lit > 0 ? d : d * 0.8;
                    if (sq) dc.DrawRectangle(B(c), null, new Rect(x + (d - size) / 2, mid - size / 2, size, size));
                    else dc.DrawEllipse(B(c), null, new Point(x + d / 2, mid), size / 2, size / 2);
                }
                break;
            }
        }
    }
}
