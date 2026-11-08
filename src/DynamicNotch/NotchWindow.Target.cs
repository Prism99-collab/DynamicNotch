using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Cyberpunk's scanner: four corner brackets snap around whatever control the pointer is on in the open notch, with
/// a tiny label under it ("// WI-FI · ON", "// VOL 62"), and glide to the next one. Built for speed: one layer on
/// top that never takes the mouse, four tiny corners that only move (no layout, no redrawing of anything under
/// them), and nothing at all unless the pointer moves onto another control.
/// </summary>
public partial class NotchWindow
{
    Canvas _target = null!;
    readonly TranslateTransform[] _cornerShift = { new(), new(), new(), new() };
    Path[] _corners = null!;
    TextBlock _targetLabel = null!;
    readonly TranslateTransform _labelShift = new();
    FrameworkElement? _locked;
    bool _targetShown;

    void BuildTarget()
    {
        _target = new Canvas { IsHitTestVisible = false, Opacity = 0, Visibility = Visibility.Collapsed };
        Grid.SetRowSpan(_target, 3);
        Panel.SetZIndex(_target, 50);
        string[] shapes = { "M0,6 L0,0 L6,0", "M-6,0 L0,0 L0,6", "M0,-6 L0,0 L6,0", "M-6,0 L0,0 L0,-6" }; // TL, TR, BL, BR
        _corners = new Path[4];
        for (int i = 0; i < 4; i++)
        {
            var g = Geometry.Parse(shapes[i]);
            g.Freeze();
            _corners[i] = new Path { Data = g, StrokeThickness = 1.6, StrokeEndLineCap = PenLineCap.Square, StrokeStartLineCap = PenLineCap.Square, RenderTransform = _cornerShift[i] };
            _target.Children.Add(_corners[i]);
        }
        _targetLabel = new TextBlock
        {
            FontSize = 8, FontWeight = FontWeights.SemiBold, Padding = new Thickness(3, 0, 3, 0),
            Background = Frozen(Color.FromArgb(0xD8, 0x00, 0x06, 0x0E)), RenderTransform = _labelShift,
        };
        _target.Children.Add(_targetLabel);
        Expanded.Children.Add(_target);

        Expanded.PreviewMouseMove += OnTargetMove;
        Expanded.MouseLeave += (_, _) => Untarget();
        Expanded.PreviewMouseLeftButtonDown += (_, _) => { if (_locked != null) TargetPulse(); };
    }

    void OnTargetMove(object sender, MouseEventArgs e)
    {
        if (!_cyber || _mode != Mode.Expanded || e.LeftButton == MouseButtonState.Pressed || _pageX.Active) { if (_targetShown && e.LeftButton != MouseButtonState.Pressed) Untarget(); return; }
        var hit = Targetable(e.OriginalSource as DependencyObject);
        if (ReferenceEquals(hit, _locked)) return; // the same control: nothing to do (the common case, almost free)
        if (hit == null) { Untarget(); return; }
        Lock(hit);
    }

    /// <summary>The control under the pointer worth locking onto: a button, a tile, a slider, a chip.</summary>
    FrameworkElement? Targetable(DependencyObject? d)
    {
        for (int depth = 0; d != null && depth < 24; depth++)
        {
            if (ReferenceEquals(d, Expanded)) return null;
            if (d is ButtonBase or PillSlider && d is FrameworkElement fe && fe.IsVisible && fe.ActualWidth is > 4 and < 300 && fe.ActualHeight is > 4 and < 120)
                return fe;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    void Lock(FrameworkElement el)
    {
        Rect r;
        try { r = el.TransformToAncestor(Expanded).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight)); }
        catch { Untarget(); return; }
        _locked = el;
        const double pad = 3;
        double l = r.Left - pad, t = r.Top - pad, rt = r.Right + pad, b = r.Bottom + pad;
        var edge = CustomEdge ?? CyYellow;
        var stroke = Frozen(edge);
        foreach (var c in _corners) c.Stroke = stroke;
        _targetLabel.Foreground = stroke;

        string? label = TargetLabel(el);
        _targetLabel.Text = label ?? "";
        _targetLabel.Visibility = label == null ? Visibility.Collapsed : Visibility.Visible;
        // the label under the control, or above it near the bottom edge
        double ly = b + 1 + 11 > ExpandedSize.Height ? t - 12 : b + 1;

        bool fresh = !_targetShown;
        _targetShown = true;
        _target.Visibility = Visibility.Visible;
        _target.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(fresh ? 60 : 90)));
        var pts = new[] { new Point(l, t), new Point(rt, t), new Point(l, b), new Point(rt, b) };
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < 4; i++)
        {
            var s = _cornerShift[i];
            if (fresh || Eco)
            {
                // a lock-on: from a little outside, the corners snap inward
                double ox = i % 2 == 0 ? -5 : 5, oy = i < 2 ? -5 : 5;
                s.BeginAnimation(TranslateTransform.XProperty, Eco ? null : new DoubleAnimation(pts[i].X + ox, pts[i].X, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease });
                s.BeginAnimation(TranslateTransform.YProperty, Eco ? null : new DoubleAnimation(pts[i].Y + oy, pts[i].Y, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease });
                if (Eco) { s.X = pts[i].X; s.Y = pts[i].Y; }
            }
            else
            {
                // gliding from the last control to this one
                s.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(pts[i].X, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
                s.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(pts[i].Y, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
            }
        }
        _labelShift.BeginAnimation(TranslateTransform.XProperty, null);
        _labelShift.BeginAnimation(TranslateTransform.YProperty, null);
        _labelShift.X = l;
        _labelShift.Y = ly;
        if (label != null && !Eco)
            Steps(_targetLabel, OpacityProperty, 30, 0, 1, 0.4, 1); // the label flickers on
    }

    void Untarget()
    {
        if (!_targetShown) return;
        _targetShown = false;
        _locked = null;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => { if (!_targetShown) _target.Visibility = Visibility.Collapsed; };
        _target.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>A press on the locked control: the corners squeeze in a pixel and spring back.</summary>
    void TargetPulse()
    {
        if (Eco) return;
        for (int i = 0; i < 4; i++)
        {
            var s = _cornerShift[i];
            double dx = i % 2 == 0 ? 2 : -2, dy = i < 2 ? 2 : -2;
            s.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(s.X + dx, s.X, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            s.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(s.Y + dy, s.Y, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    /// <summary>"// WI-FI · ON", "// VOL 62", "// NEXT": what the scanner reads off the control.</summary>
    string? TargetLabel(FrameworkElement el)
    {
        string? text = el switch
        {
            QuickToggle q => $"{q.Label} · {(q.IsOn ? "ON" : "OFF")}",
            PillSlider s when ReferenceEquals(s, VolSlider) => $"VOL {(int)Math.Round(s.Value * 100)}",
            PillSlider s when ReferenceEquals(s, BriSlider) || ReferenceEquals(s, ExtBriSlider) => $"BRI {(int)Math.Round(s.Value * 100)}",
            PillSlider s when ReferenceEquals(s, AppVolSlider) => $"APP VOL {(int)Math.Round(s.Value * 100)}",
            PillSlider s when ReferenceEquals(s, ProgressSlider) => "SEEK",
            PillSlider => null,
            _ when ReferenceEquals(el, PlayBtn) => _media.IsPlaying ? "PAUSE" : "PLAY",
            _ when ReferenceEquals(el, NextBtn) => "NEXT",
            _ when ReferenceEquals(el, PrevBtn) => "PREV",
            _ => el.ToolTip as string,
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        int cut = text.IndexOf(" · right", StringComparison.Ordinal);
        if (cut > 0) text = text[..cut];
        text = text.Trim().ToUpperInvariant();
        if (text.Length > 22) text = text[..21] + "…";
        return "// " + text;
    }
}
