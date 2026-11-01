using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DynamicNotch.Controls;

/// <summary>
/// Vector battery glyph (crisp at any DPI), iOS style. In the cyberpunk skin (<see cref="Health"/>) it becomes
/// the game's health bar instead: six segments that light up with the charge, red, teal while charging.
/// </summary>
public sealed class BatteryIcon : Grid
{
    const double InnerWidth = 17;
    const int Segments = 6;
    static readonly Brush Outline = Freeze(new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)));
    static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    static readonly Brush Yellow = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));
    static readonly Brush HealthRed = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x2A, 0x4F)));
    static readonly Brush HealthTeal = Freeze(new SolidColorBrush(Color.FromRgb(0x39, 0xC4, 0xB6)));
    static readonly Brush HealthOff = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));

    /// <summary>The cyberpunk skin: a segmented health bar (call <see cref="Restyle"/> after changing it).</summary>
    public static bool Health;

    readonly Border _fill, _case, _cap;
    readonly StackPanel _segments = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    double _level = 1;
    bool _charging, _saver;

    public BatteryIcon()
    {
        Width = 25;
        Height = 12;
        _fill = new Border
        {
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.White,
            Width = InnerWidth,
        };
        _case = new Border
        {
            Width = 22.5,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(3.5),
            BorderThickness = new Thickness(1),
            BorderBrush = Outline,
            Child = _fill,
        };
        _cap = new Border
        {
            Width = 1.6,
            Height = 4.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(0, 1, 1, 0),
            Background = Outline,
        };
        Children.Add(_case);
        Children.Add(_cap);
        for (int i = 0; i < Segments; i++)
            _segments.Children.Add(new Rectangle { Width = 3, Height = 9, Margin = new Thickness(0, 0, i < Segments - 1 ? 1 : 0, 0) });
        Children.Add(_segments);
    }

    public void Set(double level, bool charging, bool saver)
    {
        _level = Math.Clamp(level, 0, 1);
        _charging = charging;
        _saver = saver;
        Render();
    }

    /// <summary>The skin changed: redraw in it.</summary>
    public void Restyle() => Render();

    void Render()
    {
        bool health = Health;
        _case.Visibility = _cap.Visibility = health ? Visibility.Collapsed : Visibility.Visible;
        _segments.Visibility = health ? Visibility.Visible : Visibility.Collapsed;
        if (health)
        {
            int lit = Math.Max(1, (int)Math.Ceiling(_level * Segments - 0.001));
            var on = _charging ? HealthTeal : HealthRed;
            for (int i = 0; i < Segments; i++) ((Rectangle)_segments.Children[i]).Fill = i < lit ? on : HealthOff;
            return;
        }
        _fill.Width = Math.Max(2.5, InnerWidth * _level);
        _fill.Background = _charging ? Green : _saver ? Yellow : _level <= 0.2 ? Red : Brushes.White;
    }

    static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
