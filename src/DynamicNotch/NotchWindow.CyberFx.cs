using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// Cyberpunk motion, part three:
///  • CRT power-off: closing the notch switches it off like an old monitor: the picture squashes into a bright
///    line that rides the centre of the shrinking shape, the line pinches into a glowing dot, the dot fades.
///  • Decoding digits: when the clock, a timer, a call's length or the battery changes, only the digits that
///    changed scramble for a moment and lock in (140 ms). Anything else that writes the text meanwhile wins.
///  • Quickhack bars: a download or a running timer in the activity pill shows the game's segmented upload bar
///    beside its number, the leading segment blinking as it advances.
/// All of it runs only at the moment something happens and stops on its own; none of it in a game, on battery
/// mode, or in another theme.
/// </summary>
public partial class NotchWindow
{
    // ───────────── CRT power-off ─────────────

    Rectangle? _crtLine;
    Ellipse? _crtDot;
    bool _crtActive;

    /// <summary>The notch is closing in the cyber skin: switch it off like a CRT. False when it can't (then the usual glitch plays).</summary>
    bool CyberCrtOff()
    {
        if (!_cyber || Eco || _hud == null) return false;
        double w = Pill.ActualWidth, h = Pill.ActualHeight;
        if (w < 120 || h < 60) return false;
        if (_crtLine == null)
        {
            var line = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            line.GradientStops.Add(new GradientStop(Color.FromArgb(0, CyCyan.R, CyCyan.G, CyCyan.B), 0));
            line.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, CyCyan.R, CyCyan.G, CyCyan.B), 0.2));
            line.GradientStops.Add(new GradientStop(Colors.White, 0.5));
            line.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, CyCyan.R, CyCyan.G, CyCyan.B), 0.8));
            line.GradientStops.Add(new GradientStop(Color.FromArgb(0, CyCyan.R, CyCyan.G, CyCyan.B), 1));
            line.Freeze();
            _crtLine = new Rectangle { Height = 2, Fill = line, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
            var dot = new RadialGradientBrush();
            dot.GradientStops.Add(new GradientStop(Colors.White, 0));
            dot.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, CyCyan.R, CyCyan.G, CyCyan.B), 0.45));
            dot.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            dot.Freeze();
            _crtDot = new Ellipse { Width = 10, Height = 10, Fill = dot, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
            _hud.Children.Add(_crtLine);
            _hud.Children.Add(_crtDot);
        }
        _crtActive = true;
        CrtPlace(w, h);

        // the picture squashes toward its middle line
        if (Expanded.RenderTransform is ScaleTransform st)
        {
            Expanded.RenderTransformOrigin = new Point(0.5, 0.5);
            // fast at first (it has to read as a collapse before the panel's own fade-out takes it)
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.02, TimeSpan.FromMilliseconds(95)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
        }

        // the line flares, then pinches to a point
        var lineScale = (ScaleTransform)_crtLine.RenderTransform;
        var pinch = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(260) };
        pinch.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.75, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pinch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        pinch.KeyFrames.Add(new EasingDoubleKeyFrame(0.015, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        lineScale.BeginAnimation(ScaleTransform.ScaleXProperty, pinch);
        var lineGlow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(280), FillBehavior = FillBehavior.Stop };
        lineGlow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))));
        lineGlow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230))));
        lineGlow.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280))));
        _crtLine.BeginAnimation(OpacityProperty, lineGlow);

        // and what's left is a dot of light, fading
        var dotGlow = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(220), Duration = TimeSpan.FromMilliseconds(320), FillBehavior = FillBehavior.Stop };
        dotGlow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(30))));
        dotGlow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        _crtDot!.BeginAnimation(OpacityProperty, dotGlow);
        var shrink = new DoubleAnimation(1.3, 0.3, TimeSpan.FromMilliseconds(320)) { BeginTime = TimeSpan.FromMilliseconds(220), FillBehavior = FillBehavior.Stop };
        ((ScaleTransform)_crtDot.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        ((ScaleTransform)_crtDot.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, shrink);

        After(600, () =>
        {
            _crtActive = false;
            // the open panel gets its own scale back for the next opening (unless it's already opening again)
            // (if it's already opening again, its opening animation owns the transform: leave it be)
            if (_mode == Mode.Expanded) return;
            Expanded.RenderTransformOrigin = new Point(0.5, 0);
            if (Expanded.RenderTransform is ScaleTransform s2) s2.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        });
        return true;
    }

    /// <summary>Called with the shape every frame while it shrinks: the line and the dot stay on its centre.</summary>
    void CrtPlace(double w, double h)
    {
        if (!_crtActive || _crtLine == null || _crtDot == null) return;
        _crtLine.Width = Math.Max(1, w - 8);
        Canvas.SetLeft(_crtLine, 4);
        Canvas.SetTop(_crtLine, h / 2 - 1);
        Canvas.SetLeft(_crtDot, w / 2 - 5);
        Canvas.SetTop(_crtDot, h / 2 - 5);
    }

    // ───────────── decoding digits ─────────────

    readonly Dictionary<TextBlock, DispatcherTimer> _digits = new();

    /// <summary>Sets a number's text; in the cyber skin the digits that changed scramble for a moment first.</summary>
    void SetDigits(TextBlock tb, string text)
    {
        string old = tb.Text;
        if (old == text) return;
        if (_digits.Remove(tb, out var running)) running.Stop();
        if (!_cyber || Eco || _mode == Mode.Game || old.Length != text.Length || _decoding.ContainsKey(tb)) { tb.Text = text; return; }
        var changed = new List<int>();
        for (int i = 0; i < text.Length; i++) if (old[i] != text[i] && char.IsDigit(text[i])) changed.Add(i);
        if (changed.Count == 0) { tb.Text = text; return; }

        string Scrambled()
        {
            var c = text.ToCharArray();
            foreach (int i in changed) c[i] = (char)('0' + _cyRand.Next(10));
            return new string(c);
        }
        string written = Scrambled();
        tb.Text = written;
        int ticks = 0;
        var t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(35) };
        t.Tick += (_, _) =>
        {
            // something else wrote the text meanwhile: it wins
            if (tb.Text != written) { t.Stop(); _digits.Remove(tb); return; }
            if (++ticks >= 4 || !_cyber) { tb.Text = text; t.Stop(); _digits.Remove(tb); return; }
            written = Scrambled();
            tb.Text = written;
        };
        _digits[tb] = t;
        t.Start();
    }

    // ───────────── quickhack bars ─────────────

    StackPanel? _qhBar;
    bool _qhBlink;
    const int QhCells = 10;

    /// <summary>The activity pill's progress as the game's segmented upload bar (Cyberpunk); null hides it.</summary>
    void CyberQuickhack(double? share, Color color)
    {
        if (!_cyber || share is not { } p)
        {
            if (_qhBar != null) _qhBar.Visibility = Visibility.Collapsed;
            return;
        }
        if (_qhBar == null)
        {
            _qhBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), IsHitTestVisible = false };
            for (int i = 0; i < QhCells; i++) _qhBar.Children.Add(new Rectangle { Width = 2.6, Height = 9, Margin = new Thickness(0, 0, 1.2, 0) });
            if (VisualTreeHelper.GetParent(ActText) is Panel row) row.Children.Insert(row.Children.IndexOf(ActText), _qhBar);
        }
        _qhBar.Visibility = Visibility.Visible;
        int lit = (int)Math.Round(Math.Clamp(p, 0, 1) * QhCells);
        _qhBlink = !_qhBlink;
        var on = Frozen(color);
        var lead = Frozen(Color.FromArgb(_qhBlink ? (byte)0xFF : (byte)0x70, color.R, color.G, color.B));
        var off = Frozen(Color.FromArgb(0x30, color.R, color.G, color.B));
        for (int i = 0; i < QhCells; i++)
            ((Rectangle)_qhBar.Children[i]).Fill = i < lit - 1 ? on : i == lit - 1 ? lead : off;
    }
}
