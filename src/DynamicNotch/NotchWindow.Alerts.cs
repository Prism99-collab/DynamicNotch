using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Everything the notch reacts to: live activities, clickable pop-ups (with a priority queue) and the mic/camera bubble.
/// </summary>
public partial class NotchWindow
{
    enum LiveActivity { None, Media, Download, Timer, Focus }
    enum Prio { Low, Normal, High }

    /// <summary>One pop-up. <see cref="OnClick"/> makes it clickable; <see cref="OnWheel"/> lets the wheel adjust it.</summary>
    sealed record OsdRequest(
        string Kind, string? Glyph, double? Level, string? Title, string? Subtitle, string? Right,
        ImageSource? Art, Brush? GlyphBrush, (double Level, bool Charging)? Battery,
        double Seconds, bool Force, Prio Priority, Action? OnClick, Action<int>? OnWheel, Motion Motion = Motion.Auto);

    static readonly FontFamily SymbolFont = new("Segoe UI Symbol, Segoe UI");

    bool _alertsReady;
    string? _lastNetwork;
    int _batteryAlertedAt = 101;
    bool? _capsLock;
    bool _privacyShown;
    readonly Dictionary<IntPtr, DateTime> _flashSeen = new();
    (string App, DateTime At) _lastToast;
    uint _fmtExclude, _fmtIgnore;

    OsdRequest? _osd;
    DateTime _osdShownAt;
    readonly List<OsdRequest> _osdQueue = new();
    Point _pressAt;

    LiveActivity CurrentActivity => Activities() is { Count: > 0 } a ? a[0] : LiveActivity.None;

    void WireAlerts()
    {
        _fmtExclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _fmtIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
        WireOsdInput();

        _media.Changed += OnMediaChanged;
        _media.TrackChanged += () =>
        {
            if (!_media.IsPlaying || !_settings.ShowMediaActivity) return;
            ShowOsd("media", null, null, _media.Title, _media.Artist, null, art: _media.Art, seconds: 2.6, onClick: () => ExpandTo(0));
        };

        _audio.VolumeChanged += () =>
        {
            if (_mode == Mode.Expanded) { RefreshVolume(); return; }
            if (!RecentlyTouched) ShowVolumeOsd();
        };
        _audio.OutputChanged += () =>
        {
            RefreshVolume();
            if (_mode != Mode.Expanded)
                ShowOsd("output", _audio.OutputIsHeadphones ? "" : "", null, _audio.OutputName, "Sound output · click to change", null,
                    onClick: () => { ExpandTo(0); ShowDevices(true); });
        };
        _audio.MicChanged += () =>
        {
            RefreshToggles();
            if (_mode != Mode.Expanded)
                ShowOsd("mic", _audio.MicMuted ? "" : "", null, _audio.MicMuted ? "Microphone muted" : "Microphone on", null, null,
                    glyphBrush: _audio.MicMuted ? Red : null, prio: Prio.Low, onClick: () => { _audio.ToggleMic(); });
        };

        _bri.Changed += external =>
        {
            if (_mode == Mode.Expanded) { RefreshBrightness(); return; }
            if (external && !RecentlyTouched) ShowBrightnessOsd();
        };

        _radio.Changed += () => { RefreshToggles(); OnNetworkMaybeChanged(); };
        _focus.Changed += OnFocusChanged;

        _bt.Changed += RefreshToggles;
        _bt.ConnectionChanged += (name, connected) =>
            ShowOsd("bt", BluetoothService.GlyphFor(name), null, name, connected ? "Connected" : "Disconnected", null,
                glyphBrush: connected ? Blue : null, seconds: 2.4, onClick: () => Launch("ms-settings:bluetooth"));

        _power.Changed += () => { RefreshBattery(); CheckBatteryLow(); ApplyPowerProfile(); };
        _power.PluggedChanged += plugged =>
        {
            ApplyPowerProfile();
            _batteryAlertedAt = 101;
            ShowOsd("power", null, null, plugged ? "Charging" : "On battery", plugged ? null : RemainingHint(), $"{_power.Percent}%",
                battery: (_power.Percent / 100.0, plugged), seconds: 2.2, onClick: () => ExpandTo(1));
        };

        _privacy.Changed += OnPrivacyChanged;

        _files.DownloadsChanged += RefreshBase;
        _files.DownloadCompleted += path =>
        {
            RefreshBase();
            if (_settings.SystemAlerts)
                ShowOsd("download", null, null, "Download complete", Path.GetFileName(path), SystemMonitor.Bytes(SafeLength(path)),
                    art: IconHelper.ForPath(path), seconds: 4, prio: Prio.High, onClick: () => OpenPath(path));
        };
        _files.ScreenshotSaved += path =>
        {
            if (_settings.SystemAlerts)
                ShowOsd("screenshot", null, null, "Screenshot saved", "Click to open · also in your clipboard", null,
                    art: IconHelper.Thumbnail(path, 96), seconds: 3, onClick: () => OpenPath(path));
        };

        _notifications.Received += t =>
        {
            if (!_settings.NotificationAlerts) return;
            if (_focus.IsOn) { _focus.Hold(t.App); return; } // kept for the end-of-focus summary
            _lastToast = (t.App, DateTime.UtcNow);
            ShowOsd("toast:" + t.App, t.Icon == null ? "" : null, null, t.Title, string.IsNullOrEmpty(t.Body) ? t.App : t.Body, null,
                art: t.Icon, seconds: 4, prio: Prio.High, onClick: () => OpenApp(t.Aumid));
        };
    }

    static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    string? RemainingHint()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryLifeTime <= 0) return null;
        var t = TimeSpan.FromSeconds(s.BatteryLifeTime);
        return $"About {(t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min")} left";
    }

    // ───────────────────────────── actions ─────────────────────────────

    void ExpandTo(int page)
    {
        GoToPage(page, instant: true);
        Expand(byHotkey: false);
    }

    void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        // never run a freshly downloaded program from a click on a banner: show it instead
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".js" or ".scr" or ".com")
            Launch("explorer.exe", $"/select,\"{path}\"");
        else Launch(path);
    }

    void OpenApp(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return;
        Launch("explorer.exe", $"shell:AppsFolder\\{aumid}");
    }

    static void FocusWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SwitchToThisWindow(hwnd, true);
    }

    void ShowVolumeOsd()
    {
        double v = _audio.Muted ? 0 : _audio.Volume;
        ShowOsd("volume", VolumeGlyph(), v, null, null, $"{(int)Math.Round(v * 100)}", prio: Prio.Low,
            onClick: () => ExpandTo(0),
            onWheel: d => { _lastUserChange = DateTime.UtcNow; _audio.SetVolume(_audio.Volume + d * 0.04f); ShowVolumeOsd(); });
    }

    void ShowBrightnessOsd()
    {
        ShowOsd("brightness", "", _bri.Level / 100.0, null, null, $"{_bri.Level}", prio: Prio.Low,
            onClick: () => ExpandTo(0),
            onWheel: d => { _lastUserChange = DateTime.UtcNow; _bri.Set(_bri.Level + d * 5); ShowBrightnessOsd(); });
    }

    // ───────────────────────────── live activities ─────────────────────────────

    void UpdateActivityContent()
    {
        if (_secondShown != LiveActivity.None) FillSecond(_secondShown);
        switch (CurrentActivity)
        {
            case LiveActivity.Timer:
                var left = _timerEnd - DateTime.UtcNow;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                ActGlyph.Text = "";
                ActGlyph.Foreground = ActText.Foreground = Orange;
                ActText.Text = FormatTimer(left);
                break;
            case LiveActivity.Focus:
                ActGlyph.Text = "";
                ActGlyph.Foreground = ActText.Foreground = FocusPurple;
                int mins = (int)_focus.Elapsed.TotalMinutes;
                ActText.Text = mins < 1 ? "Focus" : mins < 60 ? $"Focus {mins} min" : $"Focus {mins / 60} h {mins % 60:00}";
                break;
            case LiveActivity.Download:
                ActGlyph.Text = "";
                ActGlyph.Foreground = ActText.Foreground = Blue;
                string count = _files.ActiveDownloads > 1 ? $"{_files.ActiveDownloads} · " : "";
                ActText.Text = _files.BytesPerSecond > 1024
                    ? $"{count}{SystemMonitor.Bytes(_files.BytesPerSecond)}/s"
                    : $"{count}{SystemMonitor.Bytes(_files.ActiveBytes)}";
                break;
        }
    }

    // ───────────────────────────── pop-ups ─────────────────────────────

    void ShowOsd(string kind, string? glyph, double? level, string? title, string? subtitle, string? right,
        ImageSource? art = null, Brush? glyphBrush = null, (double Level, bool Charging)? battery = null,
        double seconds = 1.8, bool force = false, Prio prio = Prio.Normal, Action? onClick = null, Action<int>? onWheel = null, Motion motion = Motion.Auto)
    {
        var req = new OsdRequest(kind, glyph, level, title, subtitle, right, art, glyphBrush, battery, seconds, force, prio, onClick, onWheel, motion);
        if (SilencedByMeeting(kind, force)) return;
        if (_focus.IsOn && !force && !FocusAllows(kind)) return;
        if (_hidden && !force) return;
        if (_mode == Mode.Expanded)
        {
            if (!force) return;
            Collapse();
        }

        if (_mode == Mode.Osd && _osd != null && _osd.Kind != kind)
        {
            double elapsed = (DateTime.UtcNow - _osdShownAt).TotalSeconds;
            bool fresh = elapsed < _osd.Seconds * 0.7;
            // something more important is on screen: wait in line (level pop-ups never wait — you're pressing keys)
            if (fresh && prio < _osd.Priority && level == null) { Enqueue(req); return; }
            // an important pop-up got interrupted early: show it again afterwards
            if (fresh && _osd.Priority >= Prio.Normal && !_osdHover)
                Enqueue(_osd with { Seconds = Math.Max(1.6, _osd.Seconds - elapsed), Motion = Motion.None }, front: true);
        }
        Present(req);
    }

    void Enqueue(OsdRequest req, bool front = false)
    {
        _osdQueue.RemoveAll(q => q.Kind == req.Kind);
        if (front) _osdQueue.Insert(0, req); else _osdQueue.Add(req);
        while (_osdQueue.Count > 4) _osdQueue.RemoveAt(_osdQueue.FindIndex(q => q.Priority == _osdQueue.Min(x => x.Priority)));
    }

    void Present(OsdRequest r)
    {
        var previous = _mode == Mode.Osd ? _osd : null;
        bool sameKind = previous != null && previous.Kind == r.Kind;
        _osd = r;
        _osdShownAt = DateTime.UtcNow;

        bool hasArt = r.Art != null;
        bool symbol = r.Glyph is { Length: > 0 } && r.Glyph[0] < ''; // e.g. ⇪ needs a normal symbol font
        OsdGlyph.Text = r.Glyph ?? "";
        OsdGlyph.FontFamily = symbol ? SymbolFont : (FontFamily)FindResource("IconFont");
        OsdGlyph.FontSize = symbol ? 17 : 15;
        OsdGlyph.Foreground = r.GlyphBrush ?? Brushes.White;
        OsdGlyph.Visibility = !hasArt && r.Battery == null && r.Glyph != null ? Visibility.Visible : Visibility.Collapsed;
        OsdArt.Background = hasArt ? IconBrush(r.Art) : null;
        OsdArt.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;
        OsdBattery.Visibility = r.Battery != null ? Visibility.Visible : Visibility.Collapsed;
        if (r.Battery is { } b) OsdBattery.Set(b.Level, b.Charging, false);
        OsdIconHost.Width = OsdIconHost.Height = hasArt ? 30 : r.Battery != null ? 26 : 22;

        bool isLevel = r.Level.HasValue;
        OsdBar.Visibility = isLevel ? Visibility.Visible : Visibility.Collapsed;
        if (isLevel)
        {
            // glide the bar instead of jumping between key presses
            double to = Math.Clamp(r.Level!.Value, 0, 1);
            if (sameKind) OsdBar.BeginAnimation(PillSlider.ValueProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else { OsdBar.BeginAnimation(PillSlider.ValueProperty, null); OsdBar.Value = to; }
        }
        OsdTextPanel.Visibility = isLevel ? Visibility.Collapsed : Visibility.Visible;
        OsdTitle.Text = r.Title ?? "";
        OsdSub.Text = r.Subtitle ?? "";
        OsdSub.Visibility = string.IsNullOrEmpty(r.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        OsdRight.Text = r.Right ?? "";

        double w = isLevel ? 300 : hasArt ? 360 : 330;
        double h = isLevel ? 36 : hasArt ? 50 : string.IsNullOrEmpty(r.Subtitle) ? 36 : 46;
        Osd.Width = w;
        Osd.Height = h;
        _osdSize = new Size(w, h);

        // a different pop-up replacing this one: slide the new content in instead of a hard swap
        if (previous != null && !sameKind)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            OsdInner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            OsdInnerShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        }

        TintOsd(r.Level.HasValue ? null : AccentFor(r));

        _osdTimer.Stop();
        _osdTimer.Interval = TimeSpan.FromSeconds(r.Seconds);
        if (!_osdHover) _osdTimer.Start();
        if (_hidden) Fade(Shell, 1, 150);
        SetMode(Mode.Osd);
        if (!sameKind) PlayMotion(ResolveMotion(r.Kind, r.Motion));
    }

    // ───────────────────────────── colour ─────────────────────────────

    static readonly Brush FocusPurple = Frozen(Color.FromRgb(0x8E, 0x8C, 0xFF));

    /// <summary>Each pop-up's own colour: its icon colour (Teams purple, WhatsApp green...) or its glyph colour.</summary>
    static Color? AccentFor(OsdRequest r)
    {
        if (r.Battery is { } b) return b.Charging ? Color.FromRgb(0x30, 0xD1, 0x58) : b.Level <= 0.2 ? Color.FromRgb(0xFF, 0x45, 0x3A) : null;
        if (r.GlyphBrush is SolidColorBrush s && s.Color != Colors.White) return s.Color;
        if (r.Art != null) return IconHelper.Accent(r.Art);
        return null;
    }

    /// <summary>A soft glow behind the pop-up's icon plus a thin coloured rim around the notch.</summary>
    void TintOsd(Color? accent)
    {
        if (accent is { } c)
        {
            var glow = new RadialGradientBrush
            {
                Center = new Point(0.1, 0.5), GradientOrigin = new Point(0.1, 0.5), RadiusX = 0.55, RadiusY = 1.6,
                GradientStops = { new GradientStop(Color.FromArgb(0x5A, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1) },
            };
            glow.Freeze();
            OsdGlow.Background = glow;
            Fade(OsdGlow, 1, 220);
        }
        else Fade(OsdGlow, 0, 160);
        _osdAccent = accent;
    }

    Color? _osdAccent;

    /// <summary>The thin rim follows whatever is on screen: the pop-up's colour, or the song's colour.</summary>
    void UpdateRim()
    {
        Color? c = _mode switch
        {
            Mode.Osd => _osdAccent,
            Mode.Media or Mode.Expanded when _media.HasSession && _media.Art != null && _media.Accent != Colors.White => _media.Accent,
            Mode.Activity when CurrentActivity == LiveActivity.Focus => ((SolidColorBrush)FocusPurple).Color,
            _ => null,
        };
        byte alpha = _mode == Mode.Expanded ? (byte)0x55 : (byte)0x80;
        var target = c is { } k ? Color.FromArgb(alpha, k.R, k.G, k.B) : Color.FromArgb(0, 0, 0, 0);
        if (Rim.BorderBrush is not SolidColorBrush rb || rb.IsFrozen) Rim.BorderBrush = rb = new SolidColorBrush(Colors.Transparent);
        rb.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(260)));
    }

    // ───────────────────────────── focus ─────────────────────────────

    /// <summary>What still gets through while you focus: your own key presses and things that can't wait.</summary>
    static bool FocusAllows(string kind) =>
        kind is "volume" or "brightness" or "caps" or "mic" or "output" or "focus" or "meeting" or "timer" or "battery" or "smart-charge";

    void OnFocusChanged()
    {
        RefreshToggles();
        RefreshBase();
        if (!_alertsReady) return;
        if (_focus.IsOn)
        {
            ShowOsd("focus", "", null, "Focus on", "Notifications held until you're done · click to end", null,
                glyphBrush: FocusPurple, seconds: 3, force: true, onClick: () => _focus.Set(false));
            return;
        }
        var spent = DateTime.Now - _focus.StartedAt;
        int held = _focus.Held.Count;
        string duration = spent.TotalMinutes < 1 ? "under a minute" : SystemMonitor.Duration(spent);
        ShowOsd("focus", "", null, $"Focus ended · {duration}",
            held == 0 ? "Nothing came in. Nice and quiet." : $"{held} held: {_focus.HeldSummary()} · click to see them", null,
            glyphBrush: FocusPurple, seconds: held == 0 ? 3.5 : 8, force: true,
            onClick: held == 0 ? null : OpenNotificationCenter);
    }

    static void OpenNotificationCenter()
    {
        // Win+N: Windows' notification list, where everything that arrived during focus is waiting
        Keybd(0x5B, false); Keybd(0x4E, false); Keybd(0x4E, true); Keybd(0x5B, true);
    }

    /// <summary>Current pop-up is done: show the next queued one, or settle back to the resting shape.</summary>
    void NextOsd()
    {
        _osdTimer.Stop();
        if (_osdQueue.Count > 0 && !_hidden)
        {
            var next = _osdQueue.OrderByDescending(q => q.Priority).First();
            _osdQueue.Remove(next);
            Present(next with { Seconds = Math.Min(next.Seconds, 2.6) });
            return;
        }
        _osd = null;
        SetMode(BaseMode);
        if (_hidden) Fade(Shell, 0, 200);
    }

    void UpdateOsdChrome()
    {
        bool clickable = _mode == Mode.Osd && _osd?.OnClick != null;
        double to = !clickable ? 0 : _osdHover ? 0.85 : 0.3;
        OsdChevron.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)));
    }

    void WireOsdInput()
    {
        _osdTimer.Tick -= OnOsdTimer;
        _osdTimer.Tick += OnOsdTimer;

        Pill.MouseEnter += (_, _) =>
        {
            if (_mode != Mode.Osd || _osd?.OnClick == null) return;
            _osdHover = true;
            _osdTimer.Stop(); // stays while you look at it
            ApplyShape();
            UpdateOsdChrome();
        };
        Pill.MouseLeave += (_, _) =>
        {
            if (_mode != Mode.Osd || !_osdHover) return;
            _osdHover = _pressed = false;
            ApplyShape();
            UpdateOsdChrome();
            _osdTimer.Interval = TimeSpan.FromSeconds(1.2);
            _osdTimer.Start();
        };
        Pill.MouseLeftButtonDown += (_, e) =>
        {
            if (_mode != Mode.Osd || _osd?.OnClick == null) return;
            _pressed = true;
            _pressAt = e.GetPosition(this);
            ApplyShape();
            e.Handled = true;
        };
        Pill.MouseMove += (_, e) =>
        {
            // flick up to dismiss
            if (!_pressed || _mode != Mode.Osd) return;
            if (_pressAt.Y - e.GetPosition(this).Y > 10) { _pressed = false; NextOsd(); }
        };
        Pill.MouseLeftButtonUp += (_, e) =>
        {
            if (_mode != Mode.Osd || !_pressed) return;
            _pressed = false;
            var action = _osd?.OnClick;
            e.Handled = true;
            _osdHover = false;
            NextOsd();
            action?.Invoke();
        };
        Pill.MouseRightButtonUp += (_, e) =>
        {
            if (_mode != Mode.Osd) return;
            e.Handled = true;
            _osdHover = false;
            NextOsd();
        };
        Pill.MouseWheel += (_, e) =>
        {
            if (_mode != Mode.Osd || _osd?.OnWheel == null) return;
            _osd.OnWheel(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        };
    }

    void OnOsdTimer(object? sender, EventArgs e)
    {
        if (_mode != Mode.Osd) { _osdTimer.Stop(); return; }
        if (_osdHover) { _osdTimer.Stop(); return; }
        NextOsd();
    }

    // ───────────────────────────── system reactions ─────────────────────────────

    void OnNetworkMaybeChanged()
    {
        if (!_alertsReady) { _lastNetwork = _radio.NetworkName; return; }
        var now = _radio.NetworkName;
        if (now == _lastNetwork) return;
        var before = _lastNetwork;
        _lastNetwork = now;
        if (!_settings.SystemAlerts) return;
        if (now != null) ShowOsd("wifi", "", null, now, "Wi-Fi connected", null, glyphBrush: Blue, seconds: 2, onClick: () => Launch("ms-settings:network-wifi"));
        else if (before != null && _radio.WifiOn)
            ShowOsd("wifi", "", null, "Wi-Fi disconnected", before, null, glyphBrush: Red, seconds: 2.4, motion: Motion.Shake, onClick: () => Launch("ms-settings:network-wifi"));
    }

    void CheckBatteryLow()
    {
        if (!_power.HasBattery || _power.PluggedIn || !_settings.SystemAlerts) return;
        int p = _power.Percent;
        int threshold = p <= 10 ? 10 : p <= 20 ? 20 : 101;
        if (threshold >= _batteryAlertedAt || threshold == 101) return;
        _batteryAlertedAt = threshold;
        ShowOsd("battery", null, null, threshold == 10 ? "Battery critically low" : "Battery low", RemainingHint() ?? "Plug in your charger", $"{p}%",
            battery: (p / 100.0, false), seconds: 5, force: true, prio: Prio.High, onClick: () => Launch("ms-settings:batterysaver"));
    }

    void CheckLockKeys()
    {
        bool caps = (GetKeyState(VK_CAPITAL) & 1) != 0;
        if (_capsLock == null) { _capsLock = caps; return; }
        if (caps == _capsLock) return;
        _capsLock = caps;
        if (_settings.SystemAlerts)
            ShowOsd("caps", "⇪", null, caps ? "Caps Lock on" : "Caps Lock off", null, null, glyphBrush: caps ? Orange : null, seconds: 1.2, prio: Prio.Low);
    }

    void OnClipboardChanged()
    {
        if (DateTime.UtcNow < _ignoreClipboardUntil) return;
        // password managers mark secrets so clipboard tools leave them alone
        if (IsClipboardFormatAvailable(_fmtExclude) || IsClipboardFormatAvailable(_fmtIgnore)) return;

        string? text = null;
        string? kind = null;
        for (int i = 0; i < 3 && text == null && kind == null; i++)
        {
            try
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
                else if (Clipboard.ContainsImage()) kind = "Image";
                else if (Clipboard.ContainsFileDropList()) { var n = Clipboard.GetFileDropList().Count; kind = n == 1 ? "1 file" : $"{n} files"; }
                else return;
            }
            catch (COMException) { Thread.Sleep(40); } // another app still holds the clipboard
            catch { return; }
        }

        if (text != null) AddClipboardText(text);
        if (!_settings.ClipboardAlerts || _mode == Mode.Expanded || !_alertsReady) return;
        if (_osd?.Kind == "screenshot") return; // the screenshot alert already says it
        string preview = text != null
            ? string.Join(" ", text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            : kind!;
        ShowOsd("copied", "", null, "Copied", preview.Length > 80 ? preview[..80] : preview, null, glyphBrush: Blue, seconds: 1.4,
            prio: Prio.Low, onClick: () => ExpandTo(3));
    }

    void OnDeviceChange(int evt, IntPtr lParam)
    {
        if (!_settings.SystemAlerts || lParam == IntPtr.Zero || (evt != DBT_DEVICEARRIVAL && evt != DBT_DEVICEREMOVECOMPLETE)) return;
        if (Marshal.ReadInt32(lParam, 4) != DBT_DEVTYP_VOLUME) return;
        var vol = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
        if ((vol.dbcv_flags & 0x2) != 0) return; // network drive
        var letters = Enumerable.Range(0, 26).Where(i => (vol.dbcv_unitmask & (1 << i)) != 0).Select(i => (char)('A' + i)).ToList();
        if (letters.Count == 0) return;
        string drive = $"{letters[0]}:";
        if (evt == DBT_DEVICEARRIVAL)
        {
            string sub = drive;
            try
            {
                var d = new DriveInfo(drive);
                if (d.IsReady) sub = $"{drive} {d.VolumeLabel} · {SystemMonitor.Bytes(d.TotalSize)}".Replace("  ", " ");
            }
            catch { }
            ShowOsd("usb", "", null, "USB drive connected", sub + " · click to open", null, glyphBrush: Blue, seconds: 3,
                onClick: () => Launch(drive + "\\"));
        }
        else ShowOsd("usb", "", null, "Drive removed", drive, null, seconds: 1.8, prio: Prio.Low);
    }

    /// <summary>An app flashed its taskbar button (new message in Teams, Discord, WhatsApp, a finished task...).</summary>
    void OnWindowFlash(IntPtr hwnd)
    {
        if (!_settings.NotificationAlerts || hwnd == IntPtr.Zero || hwnd == GetForegroundWindow()) return;
        var now = DateTime.UtcNow;
        if (_flashSeen.TryGetValue(hwnd, out var at) && (now - at).TotalSeconds < 20) return;
        _flashSeen[hwnd] = now;
        foreach (var k in _flashSeen.Where(kv => (now - kv.Value).TotalMinutes > 5).Select(kv => kv.Key).ToList()) _flashSeen.Remove(k);

        string? path = ProcessPath(hwnd);
        string app = "App";
        if (path != null)
        {
            try { app = FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d ? d : Path.GetFileNameWithoutExtension(path); }
            catch { app = Path.GetFileNameWithoutExtension(path); }
        }
        if (_lastToast.App is { Length: > 0 } && (now - _lastToast.At).TotalSeconds < 5 &&
            (app.Contains(_lastToast.App, StringComparison.OrdinalIgnoreCase) || _lastToast.App.Contains(app, StringComparison.OrdinalIgnoreCase))) return;

        if (_focus.IsOn) { _focus.Hold(app); return; }
        string title = WindowTitle(hwnd);
        ShowOsd("flash:" + app, path == null ? "" : null, null, app, string.IsNullOrWhiteSpace(title) || title == app ? "Needs your attention" : title, null,
            art: path != null ? IconHelper.ForPath(path) : null, seconds: 3.5, prio: Prio.High, onClick: () => FocusWindow(hwnd));
    }

    // ───────────────────────────── mic / camera bubble ─────────────────────────────

    void OnPrivacyChanged()
    {
        UpdatePrivacyBubble();
        RefreshPrivacyStatus();
        if (!_alertsReady || !_settings.SystemAlerts) return;
        if (_privacy.CamInUse && _privacy.CamApp != null)
            ShowOsd("privacy", "", null, "Camera in use", _privacy.CamApp, null, glyphBrush: Green, seconds: 2.2,
                onClick: () => Launch("ms-settings:privacy-webcam"));
        else if (_privacy.MicInUse && _privacy.MicApp != null)
            ShowOsd("privacy", "", null, "Microphone in use", _privacy.MicApp, null, glyphBrush: Orange, seconds: 2.2,
                onClick: () => Launch("ms-settings:privacy-microphone"));
    }

    void RefreshPrivacyStatus()
    {
        PrivacyText.Text = _privacy.CamInUse ? "" : _privacy.MicInUse ? "" : "";
        PrivacyText.Foreground = _privacy.CamInUse ? Green : Orange;
    }

    void UpdatePrivacyBubble()
    {
        bool show = (_privacy.MicInUse || _privacy.CamInUse) && _mode != Mode.Expanded && !_hidden;
        MicDot.Visibility = _privacy.MicInUse ? Visibility.Visible : Visibility.Collapsed;
        CamDot.Visibility = _privacy.CamInUse ? Visibility.Visible : Visibility.Collapsed;
        CamDot.Margin = new Thickness(_privacy.MicInUse ? 6 : 0, 0, 0, 0);
        if (show == _privacyShown) return;
        _privacyShown = show;
        if (show)
        {
            PrivacyBubble.Visibility = Visibility.Visible;
            PlacePrivacyBubble();
            var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(480)) { EasingFunction = new SpringEase { Damping = 0.62 } };
            PrivacyScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            PrivacyScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            Fade(PrivacyBubble, 1, 180);
        }
        else
        {
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            a.Completed += (_, _) => { if (!_privacyShown) PrivacyBubble.Visibility = Visibility.Collapsed; };
            PrivacyBubble.BeginAnimation(OpacityProperty, a);
        }
    }

    void PlacePrivacyBubble()
    {
        if (PrivacyBubble.Visibility != Visibility.Visible) return;
        double earW = _mode == Mode.Slim ? 0 : 10;
        PrivacyShift.X = Pill.ActualWidth / 2 + earW + 6 + PrivacyBubble.ActualWidth / 2;
    }
}
