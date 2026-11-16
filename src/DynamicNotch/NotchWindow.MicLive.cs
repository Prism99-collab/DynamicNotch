using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The Home mic tile tells you the mic is live: while it isn't muted a small light sits on its corner (ready to
/// speak) and the label says "Live"; a ring around the tile lights up with your voice as you talk (the label stays put). Muted, both go and the tile turns red
/// as before. It listens only while Home is open on screen, twenty times a second, and stops the moment it isn't.
/// </summary>
public partial class NotchWindow
{
    readonly DispatcherTimer _micLiveTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
    readonly VoiceGate _micLiveGate = new();
    Grid? _micLiveHost;          // the tile's disc, where the ring and the light go
    Shape? _micRing, _micLight;
    bool _micLiveWired, _micLiveVoiced;
    double _micLiveLevel;
    string? _micLiveFor;

    Color MicLiveColor => _cyber ? Color.FromRgb(0x00, 0xFF, 0xA3) : Color.FromRgb(0x30, 0xD1, 0x58);

    /// <summary>Starts or stops the live mic look (Home on screen, a mic, not muted).</summary>
    void MicLive(bool want)
    {
        want = want && _mode == Mode.Expanded && _page == 0 && _audio.HasMic && !_audio.MicMuted;
        if (!_micLiveWired)
        {
            _micLiveWired = true;
            _micLiveTimer.Tick += (_, _) => MicLiveTick();
        }
        if (!want)
        {
            _micLiveTimer.Stop();
            _micLiveGate.Reset();
            _micLiveVoiced = false;
            _micLiveLevel = 0;
            _micRing?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
            _micLight?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
            return;
        }
        if (!EnsureMicLiveParts()) return;
        _micLight!.BeginAnimation(OpacityProperty, new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(220)));
        if (!_micLiveTimer.IsEnabled) _micLiveTimer.Start();
    }

    /// <summary>The ring and the light, in the tile's own shape (round, or cut in Neon); rebuilt when the theme changes the tile.</summary>
    bool EnsureMicLiveParts()
    {
        MicTile.ApplyTemplate();
        if (MicTile.Template?.FindName("Disc", MicTile) is not Grid disc) return false;
        string key = Skin.Key + "|" + _cyber + "|" + Eco;
        if (ReferenceEquals(disc, _micLiveHost) && _micLiveFor == key && _micRing != null) return true;
        if (_micRing != null && _micRing.Parent is Panel p0) p0.Children.Remove(_micRing);
        if (_micLight != null && _micLight.Parent is Panel p1) p1.Children.Remove(_micLight);
        _micLiveHost = disc;
        _micLiveFor = key;
        var c = MicLiveColor;
        var glow = new System.Windows.Media.Effects.DropShadowEffect { Color = c, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.7, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        if (_cyber)
        {
            _micRing = new Path { Data = Geometry.Parse("M0,0 L32,0 L42,10 L42,42 L10,42 L0,32 Z"), Stroke = Frozen(c), StrokeThickness = 1.6, Margin = new Thickness(-0.8), Stretch = Stretch.Fill };
            _micLight = new Rectangle { Width = 5, Height = 5 };
        }
        else
        {
            _micRing = new Ellipse { Stroke = Frozen(c), StrokeThickness = 1.8, Margin = new Thickness(-1) };
            _micLight = new Ellipse { Width = 6, Height = 6 };
        }
        _micRing.Name = "MicLiveGlow"; // the skins leave it alone
        _micRing.IsHitTestVisible = false;
        _micRing.Opacity = 0;
        _micRing.Effect = Eco ? null : glow; // battery saver: the ring alone
        _micLight.Name = "MicLiveGlow";
        _micLight.Fill = Frozen(c);
        _micLight.IsHitTestVisible = false;
        _micLight.HorizontalAlignment = HorizontalAlignment.Right;
        _micLight.VerticalAlignment = VerticalAlignment.Top;
        _micLight.Margin = _cyber ? new Thickness(0, 13, 4, 0) : new Thickness(0, 4, 4, 0);
        _micLight.Opacity = 0;
        disc.Children.Add(_micRing);
        disc.Children.Add(_micLight);
        return true;
    }

    void MicLiveTick()
    {
        if (_mode != Mode.Expanded || _page != 0 || _audio.MicMuted || !_audio.HasMic) { MicLive(false); return; }
        if (!_audio.TryGetMicPeak(out float peak)) return;
        _micLiveGate.Feed(peak, 0.05);
        double target = _micLiveGate.Voiced ? 0.35 + 0.65 * _micLiveGate.Level : 0;
        // quick to light, gentle to fade: it follows the voice without flicker
        _micLiveLevel += (target - _micLiveLevel) * (target > _micLiveLevel ? 0.6 : 0.22);
        if (_micLiveLevel < 0.02) _micLiveLevel = 0;
        if (_micRing != null && Math.Abs(_micRing.Opacity - _micLiveLevel) > 0.015)
            _micRing.BeginAnimation(OpacityProperty, new DoubleAnimation(_micLiveLevel, TimeSpan.FromMilliseconds(60)));
        if (_micLiveGate.Voiced != _micLiveVoiced && _micLight != null)
        {
            _micLiveVoiced = _micLiveGate.Voiced;
            _micLight.BeginAnimation(OpacityProperty, new DoubleAnimation(_micLiveVoiced ? 1 : 0.85, TimeSpan.FromMilliseconds(150)));
        }
    }
}
