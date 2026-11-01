using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// A small button in Home's header: the desktop's icons, shown or hidden (an eye, or an eye struck through). It
/// switches Windows' own setting, so it agrees with the desktop's right-click menu and stays after a restart. Lit
/// in the accent while the icons are hidden, so you can tell at a glance why the desktop looks empty. Round in the
/// light themes, cut like its neighbours in Cyberpunk.
/// </summary>
public partial class NotchWindow
{
    void WireDesktopIcons()
    {
        DesktopBtn.Click += (_, _) =>
        {
            if (!DesktopIcons.Toggle()) { RefreshDesktopBtn(); return; }
            // Explorer switches a moment later: show the new state at once, then check what really happened
            PaintDesktopBtn(!(DesktopIcons.Shown ?? true));
            After(400, RefreshDesktopBtn);
        };
        // Cyberpunk: the plate twitches when the pointer arrives and on every press (nothing runs in between)
        QuickCluster.MouseEnter += (_, _) => ClusterGlitch(strong: false);
        foreach (var b in new[] { DesktopBtn, CyberBtn, LockBtn })
            b.PreviewMouseLeftButtonDown += (_, _) => ClusterGlitch(strong: true);
        RefreshDesktopBtn();
    }

    /// <summary>The plate behind the three buttons: a cut, cyan-rimmed slab with a yellow tick in Cyberpunk, a soft capsule elsewhere.</summary>
    void StyleCluster()
    {
        ClusterRound.Visibility = _cyber ? Visibility.Collapsed : Visibility.Visible;
        ClusterCut.Visibility = ClusterTick.Visibility = _cyber ? Visibility.Visible : Visibility.Collapsed;
        if (!_cyber) return;
        ClusterCut.Fill = Frozen(Color.FromArgb(0xD0, 0x03, 0x08, 0x0C));
        ClusterCut.Stroke = Frozen(Color.FromArgb(0x80, CyCyan.R, CyCyan.G, CyCyan.B));
        ClusterTick.Stroke = Frozen(CyYellow);
    }

    DateTime _clusterGlitchAt;

    /// <summary>A short signal hiccup: the plate jumps a pixel or two sideways and its rim flashes yellow, ~150 ms.</summary>
    void ClusterGlitch(bool strong)
    {
        if (!_cyber || Eco || (DateTime.UtcNow - _clusterGlitchAt).TotalMilliseconds < 250) return;
        _clusterGlitchAt = DateTime.UtcNow;
        double k = strong ? 1.6 : 1;
        var jump = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        foreach (var (ms, x) in new[] { (0, -1.5), (40, 1.2), (80, -0.7), (120, 0.4), (150, 0.0) })
            jump.KeyFrames.Add(new DiscreteDoubleKeyFrame(x * k, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
        ClusterShift.BeginAnimation(TranslateTransform.XProperty, jump);
        var rim = new SolidColorBrush(Color.FromArgb(0x80, CyCyan.R, CyCyan.G, CyCyan.B));
        ClusterCut.Stroke = rim;
        var flash = new ColorAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        flash.KeyFrames.Add(new DiscreteColorKeyFrame(CyYellow, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flash.KeyFrames.Add(new DiscreteColorKeyFrame(Color.FromArgb(0xFF, 0xFF, 0x2E, 0x88), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
        flash.KeyFrames.Add(new DiscreteColorKeyFrame(Color.FromArgb(0x80, CyCyan.R, CyCyan.G, CyCyan.B), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130))));
        rim.BeginAnimation(SolidColorBrush.ColorProperty, flash);
    }

    /// <summary>The real state (it may have been changed from the desktop's own menu), or no button without a desktop.</summary>
    void RefreshDesktopBtn()
    {
        StyleCluster();
        var shown = DesktopIcons.Shown;
        DesktopBtn.Visibility = shown == null ? Visibility.Collapsed : Visibility.Visible;
        if (shown is { } s) PaintDesktopBtn(s);
    }

    void PaintDesktopBtn(bool shown)
    {
        DesktopBtn.Content = shown ? "" : ""; // eye / eye struck through
        DesktopBtn.ToolTip = Loc.T(shown ? "Hide desktop icons" : "Show desktop icons");
        DesktopBtn.Tag = _cyber ? "cy" : null;
        if (shown)
        {
            // at rest: quiet, like a glass bead
            DesktopBtn.Background = Frozen(_cyber ? Color.FromArgb(0x30, CyCyan.R, CyCyan.G, CyCyan.B) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            DesktopBtn.BorderBrush = Frozen(_cyber ? Color.FromArgb(0x90, CyCyan.R, CyCyan.G, CyCyan.B) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            DesktopBtn.Foreground = Frozen(_cyber ? CyCyan : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            // hidden: lit, so an empty desktop has an obvious reason
            var c = _cyber ? CyCyan : ((SolidColorBrush)Blue).Color;
            DesktopBtn.Background = Frozen(c);
            DesktopBtn.BorderBrush = Frozen(Color.FromArgb(0xC0, c.R, c.G, c.B));
            DesktopBtn.Foreground = Frozen(_cyber ? Color.FromRgb(0x00, 0x06, 0x0E) : Colors.White);
        }
    }
}
