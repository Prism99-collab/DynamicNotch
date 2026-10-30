using System.Diagnostics;
using System.Globalization;
using System.IO;
using Forms = System.Windows.Forms;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// More of what happens on the laptop: apps starting, the keyboard language changing, screens being
/// plugged in or out, and the battery reaching full on the charger.
/// </summary>
public partial class NotchWindow
{
    void WireAwareness()
    {
        _screens = CurrentScreens();
        _power.Changed += CheckFullCharge;
        _power.PluggedChanged += plugged => { _fullAnnounced = false; _chargedSincePlug = false; };
    }

    // ───────────────────────────── apps starting ─────────────────────────────

    readonly Dictionary<uint, DateTime> _launchSeen = new();

    // shell pieces and helpers that come and go all day: never "opening"
    static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "LockApp", "TextInputHost",
        "ApplicationFrameHost", "dllhost", "rundll32", "consent", "OpenWith", "PickerHost", "ShellHost", "SystemSettingsBroker",
        "DynamicNotch", "conhost", "smartscreen", "WerFault", "CredentialUIBroker", "UserOOBEBroker",
    };

    /// <summary>
    /// An app's first window came to the front within a few seconds of the app starting: a short
    /// "Opening ..." with its icon. Switching between apps that were already open never triggers it.
    /// </summary>
    async void OnForegroundChanged(IntPtr hwnd)
    {
        if (!_settings.AppLaunchAlerts || !_alertsReady || hwnd == IntPtr.Zero || hwnd == _hwnd) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || _launchSeen.ContainsKey(pid)) return;
        var now = DateTime.UtcNow;
        _launchSeen[pid] = now;
        if (_launchSeen.Count > 300)
            foreach (var old in _launchSeen.Where(kv => (now - kv.Value).TotalHours > 12).Select(kv => kv.Key).ToList()) _launchSeen.Remove(old);
        if (WindowTitle(hwnd).Trim().Length == 0) return;

        string? path = ProcessPathById(pid);
        if (path == null || NotApps.Contains(Path.GetFileNameWithoutExtension(path))) return;
        // only apps that really just started (the process is a few seconds old)
        var age = await Task.Run(() =>
        {
            try { using var p = Process.GetProcessById((int)pid); return DateTime.Now - p.StartTime; }
            catch { return TimeSpan.MaxValue; }
        });
        if (age.TotalSeconds > 8) return;
        var (app, icon) = await DescribeAppAsync(path);
        ShowOsd("launch", icon == null ? "" : null, null, app, "Opening", null,
            art: icon, seconds: 1.8, prio: Prio.Low, motion: Motion.Pulse);
    }

    // ───────────────────────────── keyboard language ─────────────────────────────

    IntPtr _kbWindow;
    int _kbLang;

    /// <summary>
    /// Win+Space / Alt+Shift changed the keyboard language of the window you're typing in (handy when a
    /// password suddenly "doesn't work"). Moving to another window with its own language is not news.
    /// </summary>
    void CheckKeyboardLayout()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return;
        uint thread = GetWindowThreadProcessId(fg, out _);
        int lang = (int)((long)GetKeyboardLayout(thread) & 0xFFFF);
        if (lang == 0) return; // console windows and the like don't say
        if (fg != _kbWindow) { _kbWindow = fg; _kbLang = lang; return; }
        if (lang == _kbLang) return;
        _kbLang = lang;
        if (!_settings.SystemAlerts || !_alertsReady) return;
        string name, code;
        try
        {
            var culture = new CultureInfo(lang);
            name = culture.NativeName.Length > 0 ? char.ToUpper(culture.NativeName[0]) + culture.NativeName[1..] : culture.Name;
            code = culture.TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch { name = "Keyboard changed"; code = ""; }
        ShowOsd("keyboard", "", null, name, "Keyboard language", code, glyphBrush: Blue, seconds: 1.6, prio: Prio.Low);
    }

    // ───────────────────────────── screens ─────────────────────────────

    Dictionary<string, (int W, int H)> _screens = new();
    int _screensVersion;

    static Dictionary<string, (int W, int H)> CurrentScreens() =>
        Forms.Screen.AllScreens.GroupBy(s => s.DeviceName).ToDictionary(g => g.Key, g => (g.First().Bounds.Width, g.First().Bounds.Height));

    /// <summary>
    /// A screen was plugged in or unplugged. Windows shuffles displays for a moment on wake / unlock, so it
    /// waits for the setup to settle and only reports a real difference.
    /// </summary>
    async void OnScreensChanged()
    {
        int version = ++_screensVersion;
        await Task.Delay(2500);
        if (version != _screensVersion) return;
        var now = CurrentScreens();
        var added = now.Keys.Except(_screens.Keys).ToList();
        var removed = _screens.Keys.Except(now.Keys).ToList();
        _screens = now;
        if (!_settings.SystemAlerts || !_alertsReady || (added.Count == 0 && removed.Count == 0)) return;
        if (added.Count > 0)
        {
            var (w, h) = now[added[0]];
            int hz = RefreshRate(added[0]);
            ShowOsd("display", "", null, "Display connected", $"{w} × {h} · {hz} Hz", null,
                glyphBrush: Blue, seconds: 2.6, onClick: () => Launch("ms-settings:display"));
        }
        else ShowOsd("display", "", null, "Display disconnected", $"{now.Count} screen{(now.Count == 1 ? "" : "s")} left", null,
            seconds: 2.2, prio: Prio.Low, onClick: () => Launch("ms-settings:display"));
    }

    // ───────────────────────────── full battery ─────────────────────────────

    bool _fullAnnounced, _chargedSincePlug;

    /// <summary>On the charger, the battery reached 100 %: once per charge, and only after it actually charged.</summary>
    void CheckFullCharge()
    {
        if (!_power.HasBattery || !_power.PluggedIn) return;
        if (_power.Percent < 98) { _chargedSincePlug = true; return; }
        if (_power.Percent < 100 || _fullAnnounced || !_chargedSincePlug) return;
        _fullAnnounced = true;
        if (!_settings.SystemAlerts || !_alertsReady) return;
        ShowOsd("full-charge", null, null, "Fully charged", "You can unplug the charger", "100%",
            battery: (1.0, true), seconds: 3.5, onClick: () => ExpandTo(1));
    }
}
