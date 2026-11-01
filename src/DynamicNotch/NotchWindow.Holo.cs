using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;
using Path = System.Windows.Shapes.Path;

namespace DynamicNotch;

/// <summary>
/// Calls, the game's way, and a few small HUD touches.
///  • Ringing is caught three ways: a notification that says it's a call (any app, browsers included: WhatsApp Web,
///    Messenger, Meet, Discord in the browser…), a call app flashing in the taskbar while it rings, and now a call
///    app ringing out loud in the background (Discord, Teams, WhatsApp, Zoom…): a sound that keeps going for a few
///    seconds from an app that isn't in front and isn't on a call already. A message ping is far too short for that.
///  • Cyberpunk: the ring is a holocall: the caller's picture in a HUD frame, INCOMING CALL decoding in, a live
///    signal line beside it; answered, LINK ESTABLISHED.
///  • Cyberpunk: locking is BLACK ICE ENGAGED, unlocking BREACH SUCCESSFUL; the battery is a segmented health bar;
///    the memory gauge carries a row of RAM cells.
/// </summary>
public partial class NotchWindow
{
    // ───────────── ringing, by ear ─────────────

    readonly DispatcherTimer _ringEar = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
    readonly Dictionary<string, int> _ringHeard = new(StringComparer.OrdinalIgnoreCase);
    bool _ringListening;

    void WireHolo()
    {
        _ringEar.Tick += async (_, _) => await ListenForRing();
        _ringEar.Start();
        _holoTimer.Tick += (_, _) => HoloSignal();
    }

    async Task ListenForRing()
    {
        _ringEar.Interval = TimeSpan.FromSeconds(Eco ? 3 : 1.5);
        if (_ringListening || OnCall || Ringing || !_settings.NotificationAlerts || _mode == Mode.Game || InGame) { _ringHeard.Clear(); return; }
        _ringListening = true;
        try
        {
            var playing = await AudioService.ActiveRenderAppsAsync();
            var calling = playing.Where(p => IsCallApp(p) && !IsBrowser(p)).ToList();
            foreach (var key in _ringHeard.Keys.ToList()) if (!calling.Contains(key, StringComparer.OrdinalIgnoreCase)) _ringHeard.Remove(key);
            if (calling.Count == 0) return;
            GetWindowThreadProcessId(GetForegroundWindow(), out uint frontPid);
            string? front = frontPid != 0 ? ProcessPathById(frontPid) : null;
            var capturing = await AudioService.ActiveCaptureAppsAsync();
            foreach (var path in calling)
            {
                // in front: you see its own ringing screen; using the mic: already on a call
                if (string.Equals(path, front, StringComparison.OrdinalIgnoreCase) || capturing.Contains(path, StringComparer.OrdinalIgnoreCase)) { _ringHeard.Remove(path); continue; }
                bool loud = await AudioService.AppPeakAsync(path) > 0.004f;
                int heard = loud ? _ringHeard.GetValueOrDefault(path) + 1 : Math.Max(0, _ringHeard.GetValueOrDefault(path) - 1);
                _ringHeard[path] = heard;
                if (heard < 3 || OnCall || Ringing) continue; // ~4.5 s of ringing; a ping never gets there
                _ringHeard.Clear();
                var (app, icon) = await DescribeAppAsync(path);
                AppLog.Write($"ringing heard: {app}");
                StartRinging(path, app, app, icon, () => FocusApp(path));
                break;
            }
        }
        catch (Exception ex) { AppLog.Write("ring ear: " + ex.Message); }
        finally { _ringListening = false; }
    }

    /// <summary>Brings an app's window to the front (to answer).</summary>
    static void FocusApp(string path)
    {
        try
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            foreach (var p in Process.GetProcessesByName(name))
                using (p) if (p.MainWindowHandle != IntPtr.Zero) { FocusWindow(p.MainWindowHandle); return; }
        }
        catch { }
    }

    // ───────────── the holocall (Cyberpunk) ─────────────

    Path? _holoFrame;
    StackPanel? _holoSignal;
    readonly DispatcherTimer _holoTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(60) };
    double _holoPhase;

    /// <summary>Called whenever a pop-up is shown: a ringing call in the cyber skin becomes a holocall.</summary>
    void CyberHolo(bool ringing)
    {
        bool on = ringing && _cyber;
        if (on && _holoFrame == null)
        {
            // a HUD bracket frame around the caller's picture, a little bigger than it
            _holoFrame = new Path
            {
                Stretch = Stretch.Fill, Margin = new Thickness(-5), IsHitTestVisible = false, StrokeThickness = 1.4, Stroke = CyCyanBrush,
                Data = Geometry.Parse("M0,8 L0,0 L8,0 M32,0 L40,0 L40,8 M40,32 L40,40 L32,40 M8,40 L0,40 L0,32"),
            };
            OsdIconHost.Children.Add(_holoFrame);
            _holoSignal = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0), IsHitTestVisible = false };
            for (int i = 0; i < 12; i++)
                _holoSignal.Children.Add(new Rectangle { Width = 2, Height = 16, Margin = new Thickness(0, 0, 2, 0), Fill = CyCyanBrush, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 0.2) });
            Grid.SetColumn(_holoSignal, 2);
            OsdInner.Children.Add(_holoSignal);
        }
        if (_holoFrame == null || _holoSignal == null) return;
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        _holoFrame.Visibility = _holoSignal.Visibility = vis;
        if (on)
        {
            Steps(_holoFrame, OpacityProperty, 40, 0, 1, 0.3, 1, 0.6, 1);
            if (!Eco) _holoTimer.Start();
        }
        else _holoTimer.Stop();
    }

    /// <summary>The live signal: a transmission waveform rolling past, 16 frames a second, only while it rings.</summary>
    void HoloSignal()
    {
        if (_holoSignal == null || !_cyber || _mode != Mode.Osd || _osd?.Kind != "ring")
        {
            _holoTimer.Stop();
            if (_holoFrame != null) _holoFrame.Visibility = Visibility.Collapsed;
            if (_holoSignal != null) _holoSignal.Visibility = Visibility.Collapsed;
            return;
        }
        _holoPhase += 0.55;
        for (int i = 0; i < _holoSignal.Children.Count; i++)
        {
            double v = 0.25 + 0.75 * Math.Abs(Math.Sin(_holoPhase + i * 0.6) * 0.6 + Math.Sin(_holoPhase * 1.7 + i * 1.1) * 0.4);
            ((ScaleTransform)((Rectangle)_holoSignal.Children[i]).RenderTransform).ScaleY = Math.Clamp(v, 0.15, 1);
        }
    }

    // ───────────── RAM cells (Cyberpunk, System page) ─────────────

    StackPanel? _ramCells;

    void CyberRamCells(double load)
    {
        if (!_cyber)
        {
            if (_ramCells != null) _ramCells.Visibility = Visibility.Collapsed;
            return;
        }
        if (_ramCells == null)
        {
            _ramCells = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), IsHitTestVisible = false };
            for (int i = 0; i < 8; i++) _ramCells.Children.Add(new Rectangle { Width = 5, Height = 4, Margin = new Thickness(0.8, 0, 0.8, 0) });
            if (VisualTreeHelper.GetParent(RamLabel) is StackPanel column) column.Children.Add(_ramCells);
        }
        _ramCells.Visibility = Visibility.Visible;
        int lit = (int)Math.Round(Math.Clamp(load, 0, 1) * 8);
        var on = Frozen(load > 0.85 ? CyRed : CyCyan);
        var off = Frozen(Color.FromArgb(0x30, CyCyan.R, CyCyan.G, CyCyan.B));
        for (int i = 0; i < _ramCells.Children.Count; i++) ((Rectangle)_ramCells.Children[i]).Fill = i < lit ? on : off;
    }
}
