using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Cyberpunk's data rain (Controls/DataRain: three sliding sheets the graphics chip moves), in two places:
/// · behind the open notch's cards: falling faster and turning red as the PC works harder, brightening with the beat
///   of what's playing, holding still for a moment when a notification arrives;
/// · in the closed notch while nothing plays: short bursts of rain inside the pill (a flicker of bits in the slim
///   bar), fading in and out, with nothing running at all between two bursts.
/// Never in a game, hidden, off screen, in power-saving mode, or in the other themes.
/// </summary>
public partial class NotchWindow
{
    DataRain? _rainPanel, _rainIdle;
    DispatcherTimer? _rainLoad, _rainBeat, _rainBurst;
    long _cpuIdle, _cpuTotal;
    bool _idleRainOn, _bursting;
    const double PanelRainOpacity = 0.45, IdleRainOpacity = 0.7;
    static readonly Color RainRed = Color.FromRgb(0xFF, 0x2A, 0x4F);
    // development only: DYNAMICNOTCH_RAIN=off (no rain, to compare its cost) or =idle (the idle rain whatever is live)
    static readonly string? RainDev = Environment.GetEnvironmentVariable("DYNAMICNOTCH_RAIN");

    void BuildRain()
    {
        _rainPanel = new DataRain(ExpandedSize.Width, ExpandedSize.Height, cell: 15, fontSize: 11.5, fps: 60,
            minSpeed: 50, maxSpeed: 90, minLen: 5, maxLen: 13, density: 0.9, CyCyan)
            { Opacity = PanelRainOpacity, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRowSpan(_rainPanel, 3);
        Expanded.Children.Insert(Math.Min(1, Expanded.Children.Count), _rainPanel); // behind every card, above the mood glow

        _rainIdle = new DataRain(IdleSize.Width + 24, IdleSize.Height + 4, cell: 9, fontSize: 7, fps: 24,
            minSpeed: 16, maxSpeed: 30, minLen: 2, maxLen: 4, density: 0.9, CyCyan)
            { Opacity = 0, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        Stage.Children.Insert(0, _rainIdle); // under everything in the pill (the shape clips it)

        // the idle notch rains in bursts: a few seconds of data fading in and out, then quiet with nothing running
        _rainBurst = new DispatcherTimer(DispatcherPriority.Background);
        _rainBurst.Tick += (_, _) => NextBurstStep();
        // the beat: the open notch's rain brightens with the music (only while something plays)
        _rainBeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _rainBeat.Tick += (_, _) =>
        {
            if (_rainPanel == null) return;
            double level = _media.IsPlaying && _audio.TryGetPeaks(out float l, out float r) ? Math.Sqrt(Math.Max(l, r)) : 0;
            double o = PanelRainOpacity * (0.75 + 0.7 * level);
            if (Math.Abs(_rainPanel.Opacity - o) > 0.02) _rainPanel.Opacity = o;
        };
    }

    /// <summary>Which rain runs right now (called on every mode, theme, play / pause, power and visibility change).</summary>
    void UpdateRain()
    {
        if (_rainPanel == null || _rainIdle == null || _rainBurst == null || _rainBeat == null) return;
        bool live = _cyber && !Eco && !_offScreen && !_hidden && RainDev != "off";
        bool panel = live && _mode == Mode.Expanded;
        bool idle = live && (_mode is Mode.Idle or Mode.Slim && !_media.IsPlaying && !InGame || RainDev == "idle" && _mode != Mode.Expanded);

        if (panel != _rainPanel.Running)
        {
            if (panel) { _rainPanel.Visibility = Visibility.Visible; _rainPanel.Start(); }
            else { _rainPanel.Stop(); _rainPanel.Visibility = Visibility.Collapsed; }
        }
        if (panel && _media.IsPlaying) _rainBeat.Start();
        else { _rainBeat.Stop(); _rainPanel.Opacity = PanelRainOpacity; }

        if (idle != _idleRainOn)
        {
            _idleRainOn = idle;
            _rainBurst.Stop();
            _bursting = false;
            if (idle)
            {
                _rainIdle.Visibility = Visibility.Visible;
                _rainBurst.Interval = TimeSpan.FromMilliseconds(500); // the first burst soon after it goes quiet
                _rainBurst.Start();
            }
            else
            {
                _rainIdle.BeginAnimation(OpacityProperty, null);
                _rainIdle.Opacity = 0;
                _rainIdle.Stop();
                _rainIdle.Visibility = Visibility.Collapsed;
            }
        }

        if (panel)
        {
            _rainLoad ??= NewLoadTimer();
            if (!_rainLoad.IsEnabled) { ReadCpu(out _); _rainLoad.Start(); }
        }
        else _rainLoad?.Stop();
    }

    DispatcherTimer NewLoadTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => RainLoad();
        return t;
    }

    /// <summary>Quiet → fade in and fall for 3–5 s → fade out and stop → quiet 5–10 s → ...</summary>
    void NextBurstStep()
    {
        var burst = _rainBurst!;
        burst.Stop();
        if (!_idleRainOn || _rainIdle == null) return;
        if (!_bursting)
        {
            _bursting = true;
            _rainIdle.Start();
            RainLoad(); // faster and redder when the PC is busy
            _rainIdle.BeginAnimation(OpacityProperty, new DoubleAnimation(IdleRainOpacity, TimeSpan.FromMilliseconds(450)));
            burst.Interval = TimeSpan.FromSeconds(3 + _cyRand.NextDouble() * 2);
        }
        else
        {
            _bursting = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(700));
            var rain = _rainIdle;
            fade.Completed += (_, _) => { if (!_bursting) rain.Stop(); }; // nothing moves until the next burst
            rain.BeginAnimation(OpacityProperty, fade);
            burst.Interval = TimeSpan.FromSeconds(5 + _cyRand.NextDouble() * 5);
        }
        burst.Start();
    }

    /// <summary>Harder work, faster and redder rain (from 60 % CPU, red at 95 %).</summary>
    void RainLoad()
    {
        if (!ReadCpu(out double load)) return;
        double k = Math.Clamp((load - 0.6) / 0.35, 0, 1);
        var c = Color.FromRgb((byte)(CyCyan.R + (RainRed.R - CyCyan.R) * k), (byte)(CyCyan.G + (RainRed.G - CyCyan.G) * k), (byte)(CyCyan.B + (RainRed.B - CyCyan.B) * k));
        // in steps, so the colour (and the sheets' cached pictures) only change when it's noticeable
        c = Color.FromRgb((byte)(c.R / 32 * 32), (byte)(c.G / 32 * 32), (byte)(c.B / 32 * 32));
        foreach (var rain in new[] { _rainPanel, _rainIdle })
        {
            if (rain == null || !rain.Running) continue;
            rain.SpeedFactor = 1 + load * 1.4;
            rain.SetColor(c);
        }
    }

    /// <summary>The whole PC's CPU load since the last reading (Windows' own idle / kernel / user times).</summary>
    bool ReadCpu(out double load)
    {
        load = 0;
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return false;
        long total = kernel + user; // kernel time includes idle time
        long dIdle = idle - _cpuIdle, dTotal = total - _cpuTotal;
        _cpuIdle = idle;
        _cpuTotal = total;
        if (dTotal <= 0) return false;
        load = Math.Clamp(1 - (double)dIdle / dTotal, 0, 1);
        return true;
    }

    /// <summary>A notification: the rain holds its breath for a second.</summary>
    void RainFreeze()
    {
        _rainPanel?.Freeze(1000);
        _rainIdle?.Freeze(1000);
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
