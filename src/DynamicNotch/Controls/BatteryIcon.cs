using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicNotch.Controls;

/// <summary>Vector battery glyph (crisp at any DPI), iOS style.</summary>
public sealed class BatteryIcon : Grid
{
    const double InnerWidth = 17;
    static readonly Brush Outline = Freeze(new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)));
    static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    static readonly Brush Yellow = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));

    readonly Border _fill;

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
        Children.Add(new Border
        {
            Width = 22.5,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(3.5),
            BorderThickness = new Thickness(1),
            BorderBrush = Outline,
            Child = _fill,
        });
        Children.Add(new Border
        {
            Width = 1.6,
            Height = 4.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(0, 1, 1, 0),
            Background = Outline,
        });
    }

    public void Set(double level, bool charging, bool saver)
    {
        level = Math.Clamp(level, 0, 1);
        _fill.Width = Math.Max(2.5, InnerWidth * level);
        _fill.Background = charging ? Green : saver ? Yellow : level <= 0.2 ? Red : Brushes.White;
    }

    static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
