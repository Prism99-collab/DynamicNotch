using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicNotch.Controls;

/// <summary>Round control-center toggle (Wi-Fi, Bluetooth, ...). Look is defined in App.xaml.</summary>
public sealed class QuickToggle : Button
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(QuickToggle), new PropertyMetadata(""));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(QuickToggle), new PropertyMetadata(""));

    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(QuickToggle), new PropertyMetadata(false));

    public static readonly DependencyProperty OnBrushProperty =
        DependencyProperty.Register(nameof(OnBrush), typeof(Brush), typeof(QuickToggle),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
    public Brush OnBrush { get => (Brush)GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }
}
