using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Page 5 · Smart: small helpers that connect the dots (unusual battery drain, shaky internet,
/// time-based charge reminder, Downloads clean-up, breaks, optional meeting mode).
/// Each one can be switched off; meeting mode is off until the user turns it on.
/// </summary>
public partial class NotchWindow
{
    readonly DispatcherTimer _smartTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
    readonly List<(ToggleButton Switch, Func<bool> Get)> _smartSwitches = new();

    // battery
    double? _typicalWatts;
    int _highDrainStreak;
    DateTime _lastDrainAlert, _lastDownloadsCheck = DateTime.UtcNow.AddHours(-5.9); // first check ~6 min after start
    bool _chargeReminded;

    // connection
    int _badNet, _goodNet;
    bool _netAlerted;
    DateTime _lastNetAlert;

    // breaks
    DateTime? _activeSince;

    // meeting mode
    bool _inMeeting, _pausedForMeeting, _meetingSkipped;

    void WireSmart()
    {
        _smartTimer.Tick += async (_, _) => await SmartTickAsync();
        _smartTimer.Start();
        _privacy.Changed += OnMeetingCheck;
        _power.PluggedChanged += plugged => { if (plugged) { _chargeReminded = false; _highDrainStreak = 0; } };
        BuildSmartPage();
    }

    async Task SmartTickAsync()
    {
        _smartTimer.Interval = TimeSpan.FromSeconds(Eco ? 60 : 30);
        if (!_alertsReady) return;
        if (_mode == Mode.Activity) UpdateActivityContent(); // keeps "Focus 12 min" current
        try
        {
            CheckBreak();
            if (_power.HasBattery && !_power.PluggedIn) await CheckBatteryAsync();
            if (_settings.SmartConnection) await CheckConnectionAsync();
            if (_settings.SmartDownloads && (DateTime.UtcNow - _lastDownloadsCheck).TotalHours >= 6) await CheckDownloadsAsync();
        }
        catch (Exception ex) { AppLog.Write("smart check failed: " + ex.Message); }
    }

    // ───────────────────────────── battery ─────────────────────────────

    async Task CheckBatteryAsync()
    {
        var b = await _monitor.BatteryAsync();
        if (!b.Present || b.Charging || b.WattsNow is not { } w) return;

        // learn what "normal" looks like on this laptop, then flag clear outliers
        double typical = _typicalWatts ?? w;
        bool high = w >= Math.Max(13, typical * 1.6);
        _highDrainStreak = high ? _highDrainStreak + 1 : 0;
        if (!high) _typicalWatts = typical * 0.92 + w * 0.08;

        if (_settings.SmartBatteryDrain && _highDrainStreak >= 3 && (DateTime.UtcNow - _lastDrainAlert).TotalMinutes > 30)
        {
            _lastDrainAlert = DateTime.UtcNow;
            _highDrainStreak = 0;
            string? hog = await Task.Run(BusiestApp);
            string left = b.TimeLeft is { } tl ? $" · about {SystemMonitor.Duration(tl)} left" : "";
            ShowOsd("smart-drain", "", null, "Battery draining fast",
                hog != null ? $"{hog} is working hard ({w:0} W){left}" : $"Using {w:0} W, more than usual{left}",
                $"{_power.Percent}%", glyphBrush: Orange, seconds: 6, prio: Prio.High, onClick: () => ExpandTo(1));
        }

        if (_settings.SmartChargeReminder && !_chargeReminded && _power.Percent > 10 && b.TimeLeft is { } t && t.TotalMinutes <= 45)
        {
            _chargeReminded = true;
            ShowOsd("smart-charge", null, null, $"About {SystemMonitor.Duration(t)} left at this pace", "Plug in soon", $"{_power.Percent}%",
                battery: (_power.Percent / 100.0, false), seconds: 6, prio: Prio.High, onClick: () => ExpandTo(1));
        }
    }

    /// <summary>The app using the most CPU right now (sampled over one second), as a friendly name.</summary>
    static string? BusiestApp()
    {
        try
        {
            var first = new Dictionary<int, TimeSpan>();
            foreach (var p in Process.GetProcesses()) using (p) { try { first[p.Id] = p.TotalProcessorTime; } catch { } }
            Thread.Sleep(1000);
            var usage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var paths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.Id <= 4 || p.ProcessName is "Idle" or "System" or "Memory Compression" || p.Id == Environment.ProcessId) continue;
                        if (!first.TryGetValue(p.Id, out var t0)) continue;
                        double ms = (p.TotalProcessorTime - t0).TotalMilliseconds;
                        usage[p.ProcessName] = usage.GetValueOrDefault(p.ProcessName) + ms;
                        if (!paths.ContainsKey(p.ProcessName)) { try { paths[p.ProcessName] = p.MainModule?.FileName; } catch { paths[p.ProcessName] = null; } }
                    }
                    catch { }
                }
            }
            var top = usage.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (top.Key == null || top.Value < 150) return null; // nothing clearly busy
            if (paths.GetValueOrDefault(top.Key) is { } path)
            {
                try { if (FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d) return d; } catch { }
            }
            return char.ToUpper(top.Key[0]) + top.Key[1..];
        }
        catch { return null; }
    }

    // ───────────────────────────── connection ─────────────────────────────

    async Task CheckConnectionAsync()
    {
        var c = await _monitor.ConnectionAsync();
        if (!c.Online) { _badNet = _goodNet = 0; return; } // offline is already announced by the Wi-Fi alert
        bool bad = c.PingMs is null || c.PingMs > 250;
        if (bad) { _badNet++; _goodNet = 0; } else { _goodNet++; _badNet = 0; }

        if (_badNet >= 3 && !_netAlerted && (DateTime.UtcNow - _lastNetAlert).TotalMinutes > 15)
        {
            _netAlerted = true;
            _lastNetAlert = DateTime.UtcNow;
            ShowOsd("smart-net", "", null, "Connection unstable",
                c.PingMs is { } ms ? $"Ping {ms} ms on {c.Name} · calls may stutter" : $"Requests timing out on {c.Name}",
                null, glyphBrush: Orange, seconds: 5, onClick: () => ExpandTo(1));
        }
        else if (_netAlerted && _goodNet >= 3)
        {
            _netAlerted = false;
            ShowOsd("smart-net", "", null, "Connection stable again", c.PingMs is { } ms ? $"Ping {ms} ms" : null, null,
                glyphBrush: Green, seconds: 2.5, prio: Prio.Low, onClick: () => ExpandTo(1));
        }
    }

    // ───────────────────────────── downloads ─────────────────────────────

    async Task CheckDownloadsAsync()
    {
        _lastDownloadsCheck = DateTime.UtcNow;
        if (_settings.LastDownloadsNag is { } last && (DateTime.Now - last).TotalDays < 7) return;
        string? dir = _files.DownloadsDir;
        if (dir == null || !Directory.Exists(dir)) return;
        long size = await Task.Run(() =>
        {
            long total = 0;
            try
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    total += f.Length;
            }
            catch { }
            return total;
        });
        if (size < 5L * 1024 * 1024 * 1024) return;
        _settings.LastDownloadsNag = DateTime.Now;
        _settings.Save();
        ShowOsd("smart-downloads", "", null, $"Downloads folder is {SystemMonitor.Bytes(size)}", "Click to open it and tidy up", null,
            glyphBrush: Blue, seconds: 6, onClick: () => Launch(dir));
    }

    // ───────────────────────────── breaks ─────────────────────────────

    void CheckBreak()
    {
        if (!_settings.SmartBreaks) { _activeSince = null; return; }
        if (IdleTime().TotalMinutes >= 5) { _activeSince = null; return; } // stepped away: that counts as a break
        _activeSince ??= DateTime.UtcNow;
        var worked = DateTime.UtcNow - _activeSince.Value;
        if (worked.TotalMinutes < _settings.BreakMinutes) return;
        // never interrupt a game, a film or a call; try again a bit later
        if (_hidden || _privacy.MicInUse || _privacy.CamInUse) return;
        _activeSince = DateTime.UtcNow;
        ShowOsd("smart-break", "", null, "Time for a short break",
            $"{(int)worked.TotalMinutes} min at the screen · stand up, look away for a bit", null, glyphBrush: Green, seconds: 7);
    }

    // ───────────────────────────── meeting mode (opt-in) ─────────────────────────────

    void OnMeetingCheck()
    {
        if (!_settings.MeetingMode && !_inMeeting) return;
        if (_privacy.CamInUse && !_inMeeting && _settings.MeetingMode && !_meetingSkipped)
        {
            _inMeeting = true;
            _pausedForMeeting = false;
            if (_media.IsPlaying) { _ = _media.TogglePlayPauseAsync(); _pausedForMeeting = true; }
            ShowOsd("meeting", "", null, "Meeting mode on",
                (_pausedForMeeting ? "Music paused · " : "") + "pop-ups silenced · click to turn off for this call", null,
                glyphBrush: Green, seconds: 4, force: true, onClick: EndMeetingMode);
        }
        else if (!_privacy.CamInUse)
        {
            _meetingSkipped = false;
            if (_inMeeting) EndMeetingMode();
        }
    }

    void EndMeetingMode()
    {
        bool camStillOn = _privacy.CamInUse;
        _inMeeting = false;
        if (camStillOn) _meetingSkipped = true; // user turned it off for this call
        if (_pausedForMeeting && !_media.IsPlaying) _ = _media.TogglePlayPauseAsync();
        bool resumed = _pausedForMeeting;
        _pausedForMeeting = false;
        ShowOsd("meeting", "", null, camStillOn ? "Meeting mode off for this call" : "Meeting ended",
            resumed ? "Music resumed" : null, null, seconds: 2.5, force: true);
    }

    /// <summary>While in a meeting only critical pop-ups (and your own volume / brightness keys) get through.</summary>
    bool SilencedByMeeting(string kind, bool force) =>
        _inMeeting && !force && kind is not ("volume" or "brightness" or "meeting" or "battery" or "smart-charge");

    // ───────────────────────────── the Smart page ─────────────────────────────

    void BuildSmartPage()
    {
        Add("Battery drain", "Spots apps eating power", () => _settings.SmartBatteryDrain, v => _settings.SmartBatteryDrain = v);
        Add("Bad connection", "Shaky Wi-Fi before your calls", () => _settings.SmartConnection, v => _settings.SmartConnection = v);
        Add("Charge reminder", "When ~45 min are left", () => _settings.SmartChargeReminder, v => _settings.SmartChargeReminder = v);
        Add("Downloads tidy-up", "Weekly, if it's over 5 GB", () => _settings.SmartDownloads, v => _settings.SmartDownloads = v);
        Add("Break reminder", "After long stretches of work", () => _settings.SmartBreaks, v => { _settings.SmartBreaks = v; _activeSince = null; });
        Add("PC strain warnings", "CPU, GPU, memory, heat · Preferences", () => _settings.StrainWarnings, v => PrefSetStrain("all", v));
        Add("Meeting mode", "Camera on: pause music, quiet", () => _settings.MeetingMode, v =>
        {
            _settings.MeetingMode = v;
            if (!v && _inMeeting) EndMeetingMode();
        });

        foreach (int minutes in new[] { 45, 60, 90, 120 })
        {
            var chip = new Button { Style = (Style)FindResource("Capsule"), Content = $"{minutes}m", Margin = new Thickness(3, 0, 0, 0), Padding = new Thickness(9, 0, 9, 0), Height = 24, Tag = minutes };
            chip.Click += (_, _) => { _settings.BreakMinutes = minutes; _settings.Save(); SyncSmartPage(); };
            BreakChips.Children.Add(chip);
        }
        SyncSmartPage();

        void Add(string title, string hint, Func<bool> get, Action<bool> set)
        {
            var cell = new Grid { Margin = new Thickness(4, 0, 10, 0) };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 44, 0) };
            text.Children.Add(Localized(new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, title));
            text.Children.Add(Localized(new TextBlock { FontSize = 10, Foreground = DimBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) }, hint));
            cell.Children.Add(text);
            var sw = new ToggleButton { Style = (Style)FindResource("Switch"), IsChecked = get(), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            sw.Click += (_, _) => { set(sw.IsChecked == true); _settings.Save(); };
            cell.Children.Add(sw);
            _smartSwitches.Add((sw, get));
            SmartGrid.Children.Add(cell);
        }
    }

    void SyncSmartPage()
    {
        foreach (var (sw, get) in _smartSwitches)
            if (sw.IsChecked != get()) sw.IsChecked = get();
        foreach (var chip in BreakChips.Children.OfType<Button>())
        {
            bool on = (int)chip.Tag == _settings.BreakMinutes;
            chip.Background = on ? Green : (Brush)FindResource("Tile");
            chip.Foreground = on ? Brushes.Black : Brushes.White;
            chip.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}
