using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicNotch.Controls;

/// <summary>Thumb-less rounded slider (volume, brightness, track progress). Drag, click or scroll.</summary>
public sealed class PillSlider : Grid
{
    readonly Border _track = new(), _fill = new();

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(PillSlider), new PropertyMetadata(0.0, (d, _) => ((PillSlider)d).UpdateFill()));

    public static readonly DependencyProperty TrackHeightProperty = DependencyProperty.Register(
        nameof(TrackHeight), typeof(double), typeof(PillSlider), new PropertyMetadata(6.0, (d, _) => ((PillSlider)d).ApplyHeights()));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(PillSlider), new PropertyMetadata(Brushes.White, (d, e) => ((PillSlider)d)._fill.Background = (Brush)e.NewValue));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double TrackHeight { get => (double)GetValue(TrackHeightProperty); set => SetValue(TrackHeightProperty, value); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

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
        Children.Add(_track);
        Children.Add(_fill);
        ApplyHeights();
    }

    void ApplyHeights()
    {
        double h = TrackHeight;
        _track.Height = _fill.Height = h;
        _track.CornerRadius = _fill.CornerRadius = new CornerRadius(h / 2);
        UpdateFill();
    }

    void UpdateFill()
    {
        double v = Math.Clamp(Value, 0, 1);
        _fill.Width = Math.Max(TrackHeight, v * ActualWidth);
        _fill.Opacity = v <= 0.001 ? 0 : 1;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        UpdateFill();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsInteractive) return;
        CaptureMouse();
        IsDragging = true;
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
        ValueCommitted?.Invoke(Value);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        IsDragging = false;
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
