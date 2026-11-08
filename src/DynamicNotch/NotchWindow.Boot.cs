using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DynamicNotch;

/// <summary>
/// Cyberpunk's boot-up as the notch opens (with the scan line that already sweeps down, and the CRT switch-off as it
/// closes): a row of hex flickers along the top edge and settles into "LINK // OK", and the panels click in one after
/// another the way a screen powers up, in hard flickers and a sideways snap instead of a soft fade. ~0.4 s, only
/// while opening.
/// </summary>
public partial class NotchWindow
{
    TextBlock? _bootHex;
    DispatcherTimer? _bootTimer;
    int _bootStep;

    void CyberBoot()
    {
        if (!_cyber || Eco) return;
        if (_bootHex == null)
        {
            _bootHex = new TextBlock
            {
                FontFamily = new FontFamily("Consolas, Segoe UI"), FontSize = 7.5, IsHitTestVisible = false,
                Foreground = CyCyanBrush, Opacity = 0,
            };
            _hud.Children.Add(_bootHex);
            _bootTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(34) };
            _bootTimer.Tick += (_, _) => BootStep();
        }
        Canvas.SetLeft(_bootHex, 30);
        Canvas.SetTop(_bootHex, 1);
        _bootHex.BeginAnimation(OpacityProperty, null);
        _bootHex.Opacity = 0.85;
        _bootStep = 0;
        _bootTimer!.Stop();
        _bootTimer.Start();
        BootStep();
    }

    void BootStep()
    {
        if (_bootHex == null || _bootTimer == null) return;
        const int steps = 11;
        if (_mode != Mode.Expanded) { _bootTimer.Stop(); _bootHex.Opacity = 0; return; }
        if (_bootStep < steps)
        {
            // random hex, a little longer each step, like a stream being read in
            int groups = 6 + _bootStep * 3;
            var sb = new System.Text.StringBuilder(groups * 3);
            for (int i = 0; i < groups; i++) sb.Append(_cyRand.Next(256).ToString("X2")).Append(' ');
            _bootHex.Text = sb.ToString();
            _bootHex.Foreground = _bootStep % 3 == 2 ? CyYellowBrush : CyCyanBrush;
            _bootStep++;
            return;
        }
        _bootTimer.Stop();
        _bootHex.Foreground = CyYellowBrush;
        _bootHex.Text = $"LINK // OK · 0x{_cyRand.Next(0x1000, 0xFFFF):X4} · NC-2077";
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(500)) { BeginTime = TimeSpan.FromMilliseconds(450) };
        _bootHex.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>A panel clicks in like a powering screen: dark until its turn, two hard flickers, a sideways snap.</summary>
    static void ClickIn(FrameworkElement el, int delayMs)
    {
        if (el.Opacity < 0.99) return; // fading for its own reason (the device list, a card flip): leave it alone
        el.BeginAnimation(OpacityProperty, null);
        el.Opacity = 1;
        KeyTime At(int ms) => KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms));
        var flick = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, At(0)));
        flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.75, At(delayMs)));
        flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.15, At(delayMs + 34)));
        flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.9, At(delayMs + 68)));
        flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, At(delayMs + 100)));
        el.BeginAnimation(OpacityProperty, flick);

        TranslateTransform? tt = el.RenderTransform as TranslateTransform;
        if (tt == null && (el.RenderTransform == null || el.RenderTransform.Value.IsIdentity && el.RenderTransform is MatrixTransform))
            el.RenderTransform = tt = new TranslateTransform();
        if (tt == null || tt.IsFrozen) return;
        tt.BeginAnimation(TranslateTransform.XProperty, null);
        tt.X = 0;
        var snap = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        snap.KeyFrames.Add(new DiscreteDoubleKeyFrame(4, At(0)));
        snap.KeyFrames.Add(new DiscreteDoubleKeyFrame(4, At(delayMs)));
        snap.KeyFrames.Add(new DiscreteDoubleKeyFrame(-2, At(delayMs + 34)));
        snap.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, At(delayMs + 68)));
        tt.BeginAnimation(TranslateTransform.XProperty, snap);
    }
}
