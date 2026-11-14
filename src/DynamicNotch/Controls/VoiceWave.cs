using System.Windows;
using System.Windows.Media;

namespace DynamicNotch.Controls;

/// <summary>
/// A tiny live waveform of your voice: the last second or so, scrolling right to left. Bars that were your voice
/// light up in the state's colour (live, or red when the call can't hear you), as tall as you were loud; the room's
/// own sound (fans, keyboard, hum) shows only as faint low ticks, and silence as dots. So a dead mic, an open one or a
/// noisy room shows at a glance. Drawn directly; redrawn only when something moves.
/// </summary>
public sealed class VoiceWave : FrameworkElement
{
    readonly int Bars;
    readonly double[] _levels;
    readonly bool[] _voiced;
    int _head;
    double _acc, _lit, _maxLevel, _maxNoise;
    bool _anyVoice;
    readonly SolidColorBrush[] _brushes;

    public Color Tint { get; set; } = Colors.White;

    /// <summary>Square bars instead of rounded ones (Neon).</summary>
    public bool Sharp { get; set; }

    public VoiceWave() : this(15) { }

    public VoiceWave(int bars)
    {
        Bars = bars;
        _levels = new double[bars];
        _voiced = new bool[bars];
        _brushes = new SolidColorBrush[bars];
        IsHitTestVisible = false;
        for (int i = 0; i < Bars; i++) _brushes[i] = new SolidColorBrush();
    }

    /// <summary>This frame's voice level (0–1), whether it was a voice, and the room's sound (0–1); a bar every ~55 ms.</summary>
    public void Push(double level, bool voiced, double noise, double dt)
    {
        _lit += ((voiced ? 1 : 0) - _lit) * Math.Min(1, dt * (voiced ? 16 : 4));
        // each bar keeps the loudest moment of its slice, so short syllables aren't missed between bars
        _maxLevel = Math.Max(_maxLevel, level);
        _maxNoise = Math.Max(_maxNoise, noise);
        _anyVoice |= voiced;
        _acc += dt;
        if (_acc < 0.055) return;
        _acc = 0;
        bool before = false;
        for (int i = 0; i < Bars; i++) if (_levels[i] > 0.02) { before = true; break; }
        _voiced[_head] = _anyVoice;
        _levels[_head] = _anyVoice ? Math.Max(0.12, _maxLevel) : Math.Min(0.22, _maxNoise * 0.22);
        _head = (_head + 1) % Bars;
        bool now = _levels[(_head + Bars - 1) % Bars] > 0.02;
        _maxLevel = _maxNoise = 0;
        _anyVoice = false;
        if (before || now || _lit > 0.01) InvalidateVisual();
    }

    /// <summary>Just a level (treated as voice when it's there).</summary>
    public void Push(double level, double dt) => Push(level, level > 0.08, 0, dt);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double step = w / Bars, bw = Math.Max(1.5, step * 0.55), mid = h / 2;
        var dim = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
        for (int i = 0; i < Bars; i++)
        {
            int k = (_head + i) % Bars;                        // oldest on the left, newest on the right
            double l = _levels[k];
            bool v = _voiced[k];
            double age = (i + 1) / (double)Bars;               // older bars fade a little
            double bh = Math.Max(bw, 1.5 + Math.Min(1, l) * (h - 1.5));
            byte a = (byte)(Math.Clamp(0.35 + 0.65 * age, 0, 1) * (v ? 255 : 150));
            var c = v ? Mix(dim, Tint, Math.Max(_lit, 0.6)) : dim;
            _brushes[i].Color = Color.FromArgb((byte)(a * c.A / 255), c.R, c.G, c.B);
            var r = new Rect(i * step + (step - bw) / 2, mid - bh / 2, bw, bh);
            double rad = Sharp ? 0 : bw / 2;
            dc.DrawRoundedRectangle(_brushes[i], null, r, rad, rad);
        }
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
