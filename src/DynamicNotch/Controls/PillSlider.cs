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
public sealed class PillSlider : Grid
{
    readonly Border _track = new(), _fill = new();
    readonly Rectangle _knob = new();
    readonly RotateTransform _knobTurn = new();

    /// <summary>The cyberpunk skin: square track and fill, a yellow diamond knob.</summary>
    public static bool Square;
    readonly ScaleTransform _knobScale = new(0, 0);
    readonly TranslateTransform _knobShift = new();

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(PillSlider), new PropertyMetadata(0.0, (d, _) => ((PillSlider)d).UpdateFill()));

    public static readonly DependencyProperty TrackHeightProperty = DependencyProperty.Register(
        nameof(TrackHeight), typeof(double), typeof(PillSlider), new PropertyMetadata(6.0, (d, _) => ((PillSlider)d).ApplyHeights()));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(PillSlider), new PropertyMetadata(Brushes.White, (d, e) => ((PillSlider)d)._fill.Background = (Brush)e.NewValue));

    /// <summary>Extra thickness while hovered / dragged (animated).</summary>
    static readonly DependencyProperty GrowProperty = DependencyProperty.Register(
        "Grow", typeof(double), typeof(PillSlider), new PropertyMetadata(0.0, (d, _) => ((PillSlider)d).ApplyHeights()));

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
    }

    void ApplyHeights()
    {
        double h = TrackHeight + Grow;
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
        ApplyHeights();
    }

    static readonly Brush KnobYellow = Frozen(new SolidColorBrush(Color.FromRgb(0xFE, 0xE8, 0x01)));
    static readonly Brush CyTrack = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x54, 0xC1, 0xE6)));
    static readonly Brush WhiteTrack = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));
    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    void UpdateFill()
    {
        double v = Math.Clamp(Value, 0, 1);
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
        UpdateFill();
    }

    // ───── alive under the pointer ─────

    void Settle()
    {
        if (!IsInteractive) return;
        double grow = IsDragging ? 3 : IsMouseOver ? 1.5 : 0;
        double knob = IsDragging ? 1.12 : IsMouseOver ? 1 : 0;
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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsInteractive) return;
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
        Value = Math.Clamp(Value + e.Delta / 120.0 * 0.04, 0, 1);
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
