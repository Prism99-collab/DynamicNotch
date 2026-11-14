using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Controls;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The Calls page's motion: the wave that crosses the page when you mute or unmute (soft rings of light in most
/// themes; in Neon a glitchy pulse: split-colour frames, scan beams, torn bars and a stamped status), the page
/// stepping back under a sheet, Esc to put a sheet away, and the page's entrance. Everything here is a short,
/// finished animation: nothing keeps running once it's played.
/// </summary>
public partial class NotchWindow
{
    Canvas? _callFx;

    Canvas BuildCallFx()
    {
        _callFx = new Canvas { IsHitTestVisible = false, ClipToBounds = true, Name = "CallGlow" };
        return _callFx;
    }

    /// <summary>Where the mute orb's centre is, on the page.</summary>
    Point OrbCentre()
    {
        if (_callOrb == null || _callsPage == null || !_callOrb.IsVisible) return new Point(PageWidth / 2.0, 108);
        try { return _callOrb.TranslatePoint(new Point(_callOrb.ActualWidth / 2, _callOrb.ActualHeight / 2), _callsPage); }
        catch { return new Point(PageWidth / 2.0, 108); }
    }

    /// <summary>The wave across the page when the call is muted or unmuted.</summary>
    void MuteWave(bool muted)
    {
        if (_callFx == null || _callsPage == null) return;
        var o = OrbCentre();
        double w = _callsPage.ActualWidth > 0 ? _callsPage.ActualWidth : PageWidth, h = _callsPage.ActualHeight > 0 ? _callsPage.ActualHeight : 216;
        // far enough to pass the page's farthest corner
        double reach = Math.Sqrt(Math.Pow(Math.Max(o.X, w - o.X), 2) + Math.Pow(Math.Max(o.Y, h - o.Y), 2)) + 20;
        if (_cyber) NeonWave(o, reach, w, h, muted);
        else SoftWave(o, reach, muted);
    }

    void FxAdd(FrameworkElement el) { el.Name = "CallGlow"; _callFx?.Children.Add(el); } // the skins leave it as drawn

    void FxRemove(UIElement el) { _callFx?.Children.Remove(el); }

    /// <summary>Rings of light that leave the orb and sweep the page, a soft flood of colour behind them.</summary>
    void SoftWave(Point o, double reach, bool muted)
    {
        var c = muted ? MutedColor : LiveColor;
        int light = Eco ? 1 : 3;
        // the flood: a soft disc of the new colour opening behind the rings
        var flood = new RadialGradientBrush { GradientStops = { new GradientStop(A(c, 0x00), 0.0), new GradientStop(A(c, 0x30), 0.82), new GradientStop(A(c, 0x00), 1.0) } };
        flood.Freeze();
        FxSpread(new Ellipse { Fill = flood }, o, 30, reach * 2.1, 0.9, 0, 760, 0, new CubicEase { EasingMode = EasingMode.EaseOut });
        for (int i = 0; i < light; i++)
        {
            var ring = new Ellipse { Stroke = Frozen(A(c, (byte)(0xE0 - i * 0x30))), StrokeThickness = 2.4 - i * 0.6 };
            FxSpread(ring, o, 44, reach * 2, 0.95 - i * 0.2, 0, 820 + i * 80, i * 95, new QuarticEase { EasingMode = EasingMode.EaseOut });
        }
    }

    /// <summary>An element growing from a point to a size while it fades (it removes itself when done).</summary>
    void FxSpread(Shape s, Point o, double from, double to, double op0, double op1, int ms, int delay, IEasingFunction ease)
    {
        s.Width = s.Height = to;
        Canvas.SetLeft(s, o.X - to / 2);
        Canvas.SetTop(s, o.Y - to / 2);
        s.RenderTransformOrigin = new Point(0.5, 0.5);
        var st = new ScaleTransform(from / to, from / to);
        s.RenderTransform = st;
        s.Opacity = 0;
        FxAdd(s);
        var begin = TimeSpan.FromMilliseconds(delay);
        var grow = new DoubleAnimation(from / to, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, BeginTime = begin };
        var fade = new DoubleAnimation(op0, op1, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => FxRemove(s);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        s.BeginAnimation(OpacityProperty, fade);
    }

    static readonly Random _glitch = new();

    /// <summary>
    /// Neon: a hard-edged frame bursts out of the orb in two split colours, beams scan out to the page's sides, a few
    /// torn bars flicker across it, and the new state is stamped in the corner.
    /// </summary>
    void NeonWave(Point o, double reach, double w, double h, bool muted)
    {
        Color a = muted ? CyRed : CyCyan, b = muted ? CyMagenta : CyYellow;
        var sharp = new QuarticEase { EasingMode = EasingMode.EaseOut };
        // the split frames: the same square twice, a few pixels apart, in the two colours
        foreach (var (col, dx, delay) in new[] { (a, -2.0, 0), (b, 2.0, 40) })
        {
            var frame = new Rectangle { Stroke = Frozen(A(col, 0xE6)), StrokeThickness = 1.6 };
            FxSpread(frame, new Point(o.X + dx, o.Y), 50, reach * 2, 1, 0, 620, delay, sharp);
        }
        // a soft scan glow behind it
        var glow = new RadialGradientBrush { GradientStops = { new GradientStop(A(a, 0x38), 0), new GradientStop(A(a, 0), 1) } };
        glow.Freeze();
        FxSpread(new Ellipse { Fill = glow }, o, 40, reach * 1.6, 0.9, 0, 520, 0, sharp);

        // the beams: two vertical lines running from the orb to each side
        foreach (int dir in new[] { -1, 1 })
        {
            var beam = new Rectangle
            {
                Width = 2, Height = h,
                Fill = Frozen(new LinearGradientBrush(new GradientStopCollection { new(A(a, 0), 0), new(A(a, 0xFF), 0.5), new(A(a, 0), 1) }, 90)),
            };
            var halo = new Rectangle
            {
                Width = 14, Height = h, Opacity = 0.5,
                Fill = Frozen(new LinearGradientBrush(new GradientStopCollection { new(A(a, 0), 0), new(A(a, 0x50), 0.5), new(A(a, 0), 1) }, 0)),
            };
            foreach (var (el, half) in new[] { (beam, 1.0), (halo, 7.0) })
            {
                Canvas.SetTop(el, 0);
                Canvas.SetLeft(el, o.X - half);
                var tt = new TranslateTransform();
                el.RenderTransform = tt;
                FxAdd(el);
                double to = dir < 0 ? -o.X - 20 : w - o.X + 20;
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, to, TimeSpan.FromMilliseconds(460)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
                var fade = new DoubleAnimation(el.Opacity, 0, TimeSpan.FromMilliseconds(460)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                fade.Completed += (_, _) => FxRemove(el);
                el.BeginAnimation(OpacityProperty, fade);
            }
        }

        // torn bars: a few thin slabs flicker across the page, here and gone
        int bars = Eco ? 2 : 6;
        for (int i = 0; i < bars; i++)
        {
            double bh = 1 + _glitch.Next(4), bw = 40 + _glitch.NextDouble() * (w * 0.55);
            var bar = new Rectangle { Width = bw, Height = bh, Fill = Frozen(A(i % 2 == 0 ? a : b, (byte)(0x70 + _glitch.Next(0x70)))) };
            Canvas.SetLeft(bar, _glitch.NextDouble() * (w - bw));
            Canvas.SetTop(bar, _glitch.NextDouble() * h);
            bar.Opacity = 0;
            FxAdd(bar);
            int at = 30 + _glitch.Next(260);
            var flick = new DoubleAnimationUsingKeyFrames();
            flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at))));
            flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.25, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at + 40))));
            flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at + 70))));
            flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at + 120))));
            flick.Completed += (_, _) => FxRemove(bar);
            var slide = new TranslateTransform();
            bar.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, (_glitch.NextDouble() - 0.5) * 30, TimeSpan.FromMilliseconds(at + 120)));
            bar.BeginAnimation(OpacityProperty, flick);
        }

        // the stamp: the new state, typed in the corner, flickering on and fading
        var stamp = new TextBlock
        {
            Text = muted ? "// MIC_CUT" : "// MIC_LIVE", FontFamily = CyFont, FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Frozen(a), Opacity = 0,
        };
        Canvas.SetRight(stamp, 22);
        Canvas.SetTop(stamp, 6);
        FxAdd(stamp);
        var on = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1100) };
        foreach (var (t, v) in new[] { (0, 0.0), (60, 1.0), (100, 0.2), (140, 1.0), (180, 0.4), (220, 1.0), (760, 1.0), (1100, 0.0) })
            on.KeyFrames.Add(new DiscreteDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
        on.KeyFrames[^1] = new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)));
        on.Completed += (_, _) => FxRemove(stamp);
        stamp.BeginAnimation(OpacityProperty, on);

        // the page itself jolts sideways for a frame or two
        if (_callLive != null && !Eco)
        {
            var jolt = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(160) };
            foreach (var (t, x) in new[] { (0, 0.0), (30, 3.0), (60, -2.0), (100, 1.0), (160, 0.0) })
                jolt.KeyFrames.Add(new DiscreteDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
            PageXf(_callLive).Move.BeginAnimation(TranslateTransform.XProperty, jolt);
        }
    }

    /// <summary>A face of the page's own scale and shift (made once).</summary>
    static (ScaleTransform Size, TranslateTransform Move) PageXf(Grid page)
    {
        if (page.RenderTransform is TransformGroup { Children.Count: 2 } g && g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t) return (s, t);
        s = new ScaleTransform();
        t = new TranslateTransform();
        page.RenderTransformOrigin = new Point(0.5, 0.45);
        page.RenderTransform = new TransformGroup { Children = { s, t } };
        return (s, t);
    }

    // ───────────────────────────── the page under a sheet ─────────────────────────────

    /// <summary>While a sheet is up, the page steps back (a little smaller, dimmer), so the sheet is clearly on top.</summary>
    void PageBehindSheet(bool back, bool instant = false)
    {
        foreach (var page in new[] { _callLive, _callIdle })
        {
            if (page == null) continue;
            var st = PageXf(page).Size;
            var span = TimeSpan.FromMilliseconds(instant ? 0 : back ? 260 : 200);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(back ? 0.965 : 1, span) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(back ? 0.965 : 1, span) { EasingFunction = ease });
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(back ? 0.28 : 1, span) { EasingFunction = ease });
        }
    }

    // ───────────────────────────── Esc puts a sheet away ─────────────────────────────

    IntPtr _sheetHook;
    HookProc? _sheetProc;

    /// <summary>While a sheet is up, Esc closes it (only Esc is taken, and only then; every other key goes where it went).</summary>
    void WatchSheetKeys(bool on)
    {
        if (on == (_sheetHook != IntPtr.Zero)) return;
        if (on)
        {
            _sheetProc ??= OnSheetKey;
            _sheetHook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _sheetProc, GetModuleHandle(null), 0);
        }
        else
        {
            UnhookWindowsHookEx(_sheetHook);
            _sheetHook = IntPtr.Zero;
        }
    }

    IntPtr OnSheetKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() == 0x0100 /* WM_KEYDOWN */ && Marshal.ReadInt32(lParam) == 0x1B /* Esc */ && _sheet != Sheet.None && _mode == Mode.Expanded)
        {
            Dispatcher.BeginInvoke(() => CloseCallSheet());
            return new IntPtr(1);
        }
        return CallNextHookEx(_sheetHook, code, wParam, lParam);
    }

    // ───────────────────────────── the page arriving ─────────────────────────────

    /// <summary>Swiping onto the page during a call: the orb swells in, the tiles follow one by one.</summary>
    void PlayCallsArrival()
    {
        if (Eco || _call == null || _callOrb == null) return;
        if (_callOrb.RenderTransform is ScaleTransform os)
        {
            var pop = new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(560)) { EasingFunction = new SpringEase { Damping = 0.5 } };
            os.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            os.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        int i = 0;
        foreach (var t in _tiles)
        {
            var st = ((TransformGroup)t.Plate.RenderTransform).Children.OfType<ScaleTransform>().First();
            var pop = new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(480)) { EasingFunction = new SpringEase { Damping = 0.55 }, BeginTime = TimeSpan.FromMilliseconds(60 + 35 * i) };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            t.Btn.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(60 + 35 * i) });
            i++;
        }
    }

    /// <summary>A tile's icon bounces when the tile turns on.</summary>
    static void IconPop(TextBlock glyph)
    {
        if (glyph.Parent is not Border icon) return;
        if (icon.RenderTransform is not ScaleTransform st) { icon.RenderTransformOrigin = new Point(0.5, 0.5); icon.RenderTransform = st = new ScaleTransform(); }
        var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new SpringEase { Damping = 0.42 } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>The orb's icon turns over when the state changes.</summary>
    void OrbGlyphTurn()
    {
        if (_cOrbGlyph == null) return;
        if (_cOrbGlyph.RenderTransform is not ScaleTransform st) { _cOrbGlyph.RenderTransformOrigin = new Point(0.5, 0.5); _cOrbGlyph.RenderTransform = st = new ScaleTransform(); }
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new SpringEase { Damping = 0.5 } });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new SpringEase { Damping = 0.5 } });
    }

    // ───────────────────────────── captions and snip (any call) ─────────────────────────────

    static readonly string LiveCaptionsExe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "LiveCaptions.exe");
    static bool HasLiveCaptions => File.Exists(LiveCaptionsExe);
    bool _captionsOn;

    /// <summary>Windows' live captions: what's said in the call, written out as it's said (Windows 11).</summary>
    async Task ToggleCaptionsAsync()
    {
        bool running = await Task.Run(() => Process.GetProcessesByName("LiveCaptions").Length > 0);
        if (running)
        {
            await Task.Run(() => { foreach (var p in Process.GetProcessesByName("LiveCaptions")) { try { p.CloseMainWindow(); if (!p.WaitForExit(800)) p.Kill(); } catch { } finally { p.Dispose(); } } });
            _captionsOn = false;
        }
        else
        {
            try { Process.Start(new ProcessStartInfo(LiveCaptionsExe) { UseShellExecute = true }); _captionsOn = true; }
            catch { Tremble(MutedColor); }
        }
        UpdateCallsPage();
    }

    async Task CheckCaptionsAsync()
    {
        if (!HasLiveCaptions) return;
        bool on = await Task.Run(() => Process.GetProcessesByName("LiveCaptions").Length > 0);
        if (on != _captionsOn) { _captionsOn = on; if (_page == CallsPage) UpdateCallsPage(); }
    }

    /// <summary>A screenshot of part of the screen, to paste in the call's chat.</summary>
    void SnipForCall()
    {
        Collapse();
        After(220, () => Launch("ms-screenclip:"));
    }
}
