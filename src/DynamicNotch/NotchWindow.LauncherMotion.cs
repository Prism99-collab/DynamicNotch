using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicNotch;

/// <summary>
/// How the search moves, made to feel physical and never jump:
///  • the highlight bar and the list's scrolling follow springs (the same kind of motion as the notch itself), stepped
///    once per frame the screen draws (60, 120, 144 Hz…) and only while something is moving;
///  • arrow keys glide the list to keep the picked line (and its label) in view; the mouse wheel glides too;
///  • a line sliding under a pointer that isn't moving never steals the highlight while you type;
///  • Enter flashes the picked line, and the search folds away when it closes (Neon flickers out).
/// </summary>
public partial class NotchWindow
{
    double _barY, _barH, _barVY, _barVH, _barTY, _barTH;
    double _scrollY, _scrollV, _scrollT;
    bool _scrolling, _springOn, _barFresh = true;
    TimeSpan _lastTick;
    double _listHeight;
    Border? _selFlash;

    /// <summary>Sends the bar to a line: it springs there (or is simply there, the first time).</summary>
    void BarTo(double y, double h, bool jump)
    {
        _barTY = y;
        _barTH = h;
        if (jump || _barFresh)
        {
            _barFresh = false;
            _barY = y; _barH = h; _barVY = _barVH = 0;
            if (_selBar != null)
            {
                ((TranslateTransform)_selBar.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
                _selBar.BeginAnimation(HeightProperty, null);
            }
            ApplyBar();
            return;
        }
        StartSprings();
    }

    /// <summary>Glides the list to an offset (clamped to what there is to scroll).</summary>
    void ScrollTo(double target, bool jump = false)
    {
        var sv = _launchScroll;
        if (sv == null) return;
        double max = Math.Max(0, Math.Max(_listHeight, sv.ExtentHeight) - sv.ViewportHeight);
        target = Math.Clamp(target, 0, max);
        if (!_scrolling) { _scrollY = sv.VerticalOffset; _scrollV = 0; }
        _scrollT = target;
        if (jump)
        {
            _scrolling = false;
            _scrollY = target;
            sv.ScrollToVerticalOffset(target);
            return;
        }
        if (Math.Abs(_scrollY - target) < 0.5) return;
        _scrolling = true;
        StartSprings();
    }

    void StartSprings()
    {
        if (_springOn) return;
        _springOn = true;
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += OnSpringFrame;
    }

    void StopSprings()
    {
        if (!_springOn) return;
        _springOn = false;
        CompositionTarget.Rendering -= OnSpringFrame;
    }

    void OnSpringFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastTick == TimeSpan.Zero ? 1 / 120.0 : (now - _lastTick).TotalSeconds;
        if (dt <= 0) return; // the same frame, told twice
        _lastTick = now;
        dt = Math.Min(dt, 1 / 30.0);
        // the bar: quick with the slightest give; its height: no give at all
        bool bar = Step(ref _barY, ref _barVY, _barTY, 560, 0.84, dt) | Step(ref _barH, ref _barVH, _barTH, 560, 1.0, dt);
        ApplyBar();
        bool scroll = false;
        if (_scrolling && _launchScroll != null)
        {
            scroll = Step(ref _scrollY, ref _scrollV, _scrollT, 320, 1.0, dt);
            _launchScroll.ScrollToVerticalOffset(_scrollY);
            if (!scroll) _scrolling = false;
        }
        if (!bar && !scroll) StopSprings();
    }

    /// <summary>One frame of a damped spring (two half steps, steady at any frame rate). False once it has arrived.</summary>
    static bool Step(ref double x, ref double v, double target, double stiffness, double damping, double dt)
    {
        double c = 2 * damping * Math.Sqrt(stiffness);
        for (int i = 0; i < 2; i++)
        {
            double h = dt / 2;
            double a = -stiffness * (x - target) - c * v;
            v += a * h;
            x += v * h;
        }
        if (Math.Abs(x - target) < 0.2 && Math.Abs(v) < 3) { x = target; v = 0; return false; }
        return true;
    }

    void ApplyBar()
    {
        if (_selBar == null) return;
        ((TranslateTransform)_selBar.RenderTransform).Y = _barY;
        _selBar.Height = Math.Max(0, _barH);
    }

    /// <summary>Keeps the picked line, and the label above it, in view: the list glides there.</summary>
    void KeepInView(double y, double h)
    {
        var sv = _launchScroll;
        if (sv == null || sv.ViewportHeight <= 0) return;
        double view = sv.ViewportHeight, cur = _scrolling ? _scrollT : sv.VerticalOffset;
        double top = _launchSel == 0 ? 0 : y - 22, bottom = y + h + 4;
        double t = cur;
        if (top < cur) t = top;
        else if (bottom > cur + view) t = bottom - view;
        if (_launchSel == 0) t = 0;
        if (Math.Abs(t - cur) > 0.5) ScrollTo(t);
    }

    /// <summary>The wheel over the results: the list glides by about two lines per notch of the wheel.</summary>
    void OnResultsWheel(object sender, MouseWheelEventArgs e)
    {
        if (_launchScroll == null || _launchScroll.ExtentHeight <= _launchScroll.ViewportHeight) return;
        e.Handled = true;
        double from = _scrolling ? _scrollT : _launchScroll.VerticalOffset;
        ScrollTo(from - e.Delta / 120.0 * 60);
    }

    // ───────────────────────────── the pointer ─────────────────────────────

    Point _pointer = new(double.NaN, double.NaN);
    DateTime _pointerMovedAt;

    /// <summary>Only a pointer that really moved picks lines (rows sliding under a still one don't).</summary>
    void TrackPointer(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (p == _pointer) return;
        _pointer = p;
        _pointerMovedAt = DateTime.UtcNow;
    }

    bool PointerMoving => DateTime.UtcNow - _pointerMovedAt < TimeSpan.FromMilliseconds(150);

    // ───────────────────────────── Enter, and closing ─────────────────────────────

    /// <summary>The picked line flashes as Enter takes it.</summary>
    void FlashPicked()
    {
        if (_selFlash == null || _selBar is not { Visibility: Visibility.Visible }) return;
        var s = Skin;
        var c = _cyber ? CyYellow : s.Accent;
        _selFlash.Background = Frozen(Color.FromArgb(_cyber ? (byte)0x66 : (byte)0x55, c.R, c.G, c.B));
        _selFlash.CornerRadius = new CornerRadius(s.SelRadius);
        _selFlash.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(_cyber ? 220 : 280)) { EasingFunction = Out });
    }

    /// <summary>The panel folds away (Neon flickers out) and is hidden once it's gone, unless it was opened again meanwhile.</summary>
    void FoldAway()
    {
        if (_launch == null) return;
        var panel = _launch;
        StopSprings();
        _scrolling = false;
        AnimationTimeline fade;
        if (_cyber)
        {
            var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(130) };
            k.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(25))));
            k.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55))));
            k.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130))));
            fade = k;
        }
        else fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            if (_launcherOpen) return; // opened again meanwhile: its own animation has taken over
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 0;
            panel.Visibility = Visibility.Collapsed;
        };
        panel.BeginAnimation(OpacityProperty, fade);
        _launchScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    // ───────────────────────────── icons ready before they're needed ─────────────────────────────

    /// <summary>Reads an icon into the cache in the background (for the first Alt+Space to show it at once).</summary>
    void WarmIcon(string target)
    {
        if (_iconCache.ContainsKey(target)) return;
        EnsureIconThread();
        IconQueue.Add((target, img => _iconCache[target] = img));
    }
}
