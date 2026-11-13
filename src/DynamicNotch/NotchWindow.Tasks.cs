using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicNotch.Services;
using Microsoft.Win32;
using static DynamicNotch.Interop.Native;
using Do = DynamicNotch.Services.LauncherIntent.Do;
using Intent = DynamicNotch.Services.LauncherIntent.Intent;

namespace DynamicNotch;

/// <summary>
/// The search doing what you tell it: a preview line says exactly what will happen ("Volume to 30% · now 64%",
/// "Remind you: stretch · today at 14:40, in 20 min", "Close Spotify · 1 window open"), Enter does it, and the notch
/// confirms. Several at once with "and" / "then". Reminders are kept on disk, ring even after a restart (late ones say
/// they're late), and a click on one snoozes it ten minutes. Nothing here runs unless you pressed Enter.
/// </summary>
public partial class NotchWindow
{
    static class G
    {
        public const string Vol = "", Mute = "", Bright = "", Play = "", Pause = "", Next = "", Prev = "",
            Music = "", Wifi = "", Bt = "", Plane = "", Moon = "", Focus = "", Timer = "", Bell = "",
            Open = "", Close = "", Web = "", Power = "", Restart = "", Lock = "", Shot = "", Bin = "",
            Desk = "", Clip = "", Check = "", Steps = "", Info = "", Map = "", Battery = "";
    }

    static readonly Brush Amber = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20));

    /// <summary>A line about a task. <c>Done</c>: nothing to do, it's that way already (fine inside a chain).</summary>
    sealed record TaskView(string Title, string Detail, string Glyph, string? Icon, bool Ready, bool Done = false);

    // ───────────────────────────── in the search ─────────────────────────────

    /// <summary>
    /// What the typed words ask the notch to do, as result lines. Returns the text the normal search should look for
    /// (for "open spotify" that's "spotify": the app's own line is the answer, with its icon).
    /// </summary>
    string AddTaskHits(string q, out bool understood, out Hit? soft)
    {
        understood = false;
        soft = null;
        var now = DateTime.Now;
        var list = LauncherIntent.Parse(q, now);
        if (list.Count == 0) return q;

        if (list.Count == 1)
        {
            var t = list[0];
            if (t.Kind == Do.Open && t.Op == "open" && !Regex.IsMatch(t.Text!, @"\s+and\s+|,"))
            {
                // "open X": X's own line is the answer, unless it's a web address
                if (LauncherAnswers.For(t.Text!) is { Url: { } url }) { _hits.Add(new Hit($"Open {t.Text}", "In your browser", G.Web, null, () => OpenTarget(url), Big: true)); understood = true; return q; }
                return Regex.Replace(t.Text!, @"\s*\b(?:settings?|app|folder)$", "").Trim() is { Length: > 0 } name ? name : q;
            }
            if (t.Kind == Do.Reminders) { AddReminderRows(); understood = true; return q; }
            var v = Describe(t);
            var hit = new Hit(v.Title, v.Detail, v.Icon == null ? v.Glyph : null, v.Icon, v.Ready ? () => RunTasks(list) : () => Nudge(), Big: true);
            // a web search that only might be meant ("youtube music" is also an app): after the app, not above it
            if (t.Kind == Do.Web && Regex.IsMatch(LauncherIntent.Clean(q), @"^(?:youtube|yt|wiki|wikipedia|amazon|reddit|maps|images|imdb|pinterest|news|play) ")) soft = hit;
            else _hits.Add(hit);
            understood = true;
            return q;
        }

        // a chain: one line, every step in it
        var views = list.Select(Describe).ToList();
        bool ready = views.All(x => x.Ready || x.Done) && views.Any(x => x.Ready);
        string title = string.Join("  →  ", views.Select(x => Short(x.Title)));
        int todo = views.Count(x => x.Ready);
        string detail = ready ? (todo == list.Count ? $"{list.Count} steps · Enter does them all" : $"{todo} to do · {views.First(x => x.Done).Detail}")
            : views.FirstOrDefault(x => !x.Ready && !x.Done)?.Detail ?? "All done already";
        _hits.Add(new Hit(title, detail, G.Steps, null, ready ? () => RunTasks(list) : () => Nudge(), Big: true));
        understood = true;
        return q;
    }

    static string Short(string s) => s.Length > 28 ? s[..27] + "…" : s;

    /// <summary>A task that can't run yet: a small shake says "not quite".</summary>
    void Nudge() { if (_launch != null) Bump(1); }

    /// <summary>What the notch knows about itself and the PC: battery, the song, how much is left.</summary>
    LauncherAnswers.Answer? NotchFact(string q)
    {
        string s = LauncherIntent.Clean(q);
        if (Regex.IsMatch(s, @"^(?:battery|battery level|battery life|battery left|how much battery(?: (?:is )?left| do i have)?|charge|charge level|power level)$"))
        {
            if (!_power.HasBattery) return new("No battery", "This PC runs on mains power", "");
            string state = _power.PluggedIn ? (_power.Percent >= 100 ? "Plugged in · full" : "Charging") : _power.Saver ? "On battery · saver on" : "On battery";
            return new($"{_power.Percent}%", state, $"{_power.Percent}%");
        }
        if (Regex.IsMatch(s, @"^(?:what(?:'s| is) the )?(?:volume|sound level)(?: level| now| at)?$"))
            return new($"{(int)Math.Round((_audio.Muted ? 0 : _audio.Volume) * 100)}%", (_audio.Muted ? "Muted · " : "") + _audio.OutputName, "");
        return null;
    }

    // ───────────────────────────── describing ─────────────────────────────

    TaskView Describe(Intent t)
    {
        switch (t.Kind)
        {
            case Do.Volume:
            {
                int cur = (int)Math.Round((_audio.Muted ? 0 : _audio.Volume) * 100);
                int to = t.Op switch { "set" => (int)t.Num, "up" => Math.Min(100, cur + (int)t.Num), "down" => Math.Max(0, cur - (int)t.Num), _ => cur };
                return t.Op switch
                {
                    "mute" => new("Mute the sound", _audio.Muted ? "It's muted already" : $"Now at {cur}%", G.Mute, null, !_audio.Muted, _audio.Muted),
                    "unmute" => new("Sound back on", _audio.Muted ? $"Back to {(int)Math.Round(_audio.Volume * 100)}%" : "It isn't muted", G.Vol, null, _audio.Muted, !_audio.Muted),
                    "set" => new($"Volume to {to}%", $"Now {cur}%", G.Vol, null, true),
                    _ => new($"Volume {(t.Op == "up" ? "up" : "down")} to {to}%", to == cur ? $"It's at {cur}% already" : $"Now {cur}%", G.Vol, null, to != cur, to == cur),
                };
            }
            case Do.Brightness:
            {
                bool own = _bri.Supported, ext = !own && _extBri.Supported;
                if (!own && !ext) return new("Screen brightness", "This screen's brightness can't be set from Windows", G.Bright, null, false);
                int cur = own ? _bri.Level : _extBri.Level;
                int to = t.Op switch { "set" => (int)t.Num, "up" => Math.Min(100, cur + (int)t.Num), _ => Math.Max(0, cur - (int)t.Num) };
                return new($"Brightness to {to}%", $"Now {cur}%", G.Bright, null, true);
            }
            case Do.Media:
            {
                if (!_media.HasSession) return new(t.Op switch { "next" => "Next track", "prev" => "Previous track", "pause" => "Pause", _ => "Play" },
                    "Nothing is open to play · try “play lofi on youtube”", G.Music, null, false);
                string what = _media.Title.Length > 0 ? _media.Title + (_media.Artist.Length > 0 ? " · " + _media.Artist : "") : "the player";
                return t.Op switch
                {
                    "play" => new(_media.IsPlaying ? "It's playing" : "Play", what, G.Play, null, !_media.IsPlaying, _media.IsPlaying),
                    "pause" => new(_media.IsPlaying ? "Pause" : "It's paused", what, G.Pause, null, _media.IsPlaying, !_media.IsPlaying),
                    "next" => new("Next track", what, G.Next, null, _media.CanNext),
                    "prev" => new("Previous track", what, G.Prev, null, _media.CanPrevious),
                    _ => new(_media.IsPlaying ? "Pause" : "Play", what, _media.IsPlaying ? G.Pause : G.Play, null, true),
                };
            }
            case Do.NowPlaying:
                return _media.HasSession && _media.Title.Length > 0
                    ? new(_media.Title, (_media.Artist.Length > 0 ? _media.Artist + " · " : "") + "Enter copies it", G.Music, null, true)
                    : new("Nothing playing", "Start a song and ask again", G.Music, null, false);
            case Do.Wifi:
                if (!_radio.HasWifi) return new("Wi-Fi", "No Wi-Fi on this PC", G.Wifi, null, false);
                return Switch("Wi-Fi", t.On ?? !_radio.WifiOn, _radio.WifiOn, G.Wifi, _radio.WifiOn && !string.IsNullOrEmpty(_radio.NetworkName) ? "on " + _radio.NetworkName : null);
            case Do.Bluetooth:
                if (!_radio.HasBluetooth) return new("Bluetooth", "No Bluetooth on this PC", G.Bt, null, false);
                return Switch("Bluetooth", t.On ?? !_radio.BluetoothOn, _radio.BluetoothOn, G.Bt, null);
            case Do.Airplane:
                return Switch("Airplane mode", t.On ?? !_radio.AirplaneOn, _radio.AirplaneOn, G.Plane, null);
            case Do.DarkMode:
            {
                bool dark = WindowsDark(), want = t.On ?? !dark;
                return new(want ? "Dark mode" : "Light mode", want == dark ? $"Windows is in {(dark ? "dark" : "light")} mode already" : "For Windows and your apps", want ? G.Moon : G.Bright, null, want != dark, want == dark);
            }
            case Do.Focus:
                if (t.On == false) return new("Focus off", _focus.IsOn ? "Notifications come back" : "Focus isn't on", G.Focus, null, _focus.IsOn, !_focus.IsOn);
                return t.For is { } f
                    ? new($"Focus for {LauncherDur(f)}", $"Until {(DateTime.Now + f).ToString("t", CultureInfo.CurrentCulture)} · notifications wait", G.Focus, null, true)
                    : new(_focus.IsOn && t.On == null ? "Focus off" : "Focus on", _focus.IsOn && t.On == true ? "Focus is on already" : "Notifications wait until you're done", G.Focus, null, t.On == null || !_focus.IsOn, t.On == true && _focus.IsOn);
            case Do.Timer:
                return new($"{t.Label ?? "Timer"} · {LauncherDur(t.For!.Value)}", _timerRunning ? "Replaces the timer that's running" : "The notch counts down", G.Timer, null, true);
            case Do.Remind:
                if (string.IsNullOrEmpty(t.Text)) return new("Remind you…", "Say what and when: “remind me to stretch in 20 min”", G.Bell, null, false);
                if (t.At is not { } at) return new($"Remind you: {t.Text}", "When? Add “in 20 min”, “at 5pm” or “tomorrow”", G.Bell, null, false);
                return new(t.Text == "Alarm" ? "Alarm" : $"Remind you: {t.Text}", WhenText(at), G.Bell, null, true);
            case Do.CancelReminders:
                return new(_reminders.Count == 0 ? "No reminders" : $"Cancel {(_reminders.Count == 1 ? "your reminder" : $"all {_reminders.Count} reminders")}",
                    _reminders.Count == 0 ? "Nothing to cancel" : string.Join(" · ", _reminders.Take(3).Select(r => r.Text)), G.Bell, null, _reminders.Count > 0);
            case Do.Open:
            {
                if (t.Op == "switch")
                {
                    var win = RunningWindows(t.Text!);
                    var e = ResolveEntry(t.Text!);
                    if (win.Count > 0) return new($"Switch to {win[0].Name}", "It's open", G.Open, e?.Kind == LauncherIndex.Kind.App ? e.Target : null, true);
                    return e == null ? new($"Switch to “{t.Text}”", "Nothing open or installed by that name", G.Open, null, false)
                        : new($"Open {e.Name}", "It isn't open yet", G.Open, e.Kind == LauncherIndex.Kind.App ? e.Target : null, true);
                }
                var names = OpenNames(t.Text!);
                var found = names.Select(n => (n, e: ResolveEntry(n), url: LauncherAnswers.For(n)?.Url)).ToList();
                var missing = found.Where(x => x.e == null && x.url == null).Select(x => x.n).ToList();
                string list = string.Join(", ", found.Select(x => x.e?.Name ?? x.n));
                var icon = found.Select(x => x.e).FirstOrDefault(e => e?.Kind == LauncherIndex.Kind.App)?.Target;
                return missing.Count > 0 ? new($"Open {list}", $"Can't find “{missing[0]}”", G.Open, icon, false) : new($"Open {list}", "Enter opens", G.Open, icon, true);
            }
            case Do.Close:
            {
                var win = RunningWindows(t.Text!);
                bool force = t.Op == "force";
                if (win.Count == 0) return new($"{(force ? "Force-quit" : "Close")} “{t.Text}”", "Not open right now", G.Close, null, false);
                int n = win.Count;
                return new($"{(force ? "Force-quit" : "Close")} {win[0].Name}",
                    force ? "Unsaved work is lost" : n == 1 ? "1 window · it asks first if there's unsaved work" : $"{n} windows · each asks first if there's unsaved work", G.Close, win[0].Path, true);
            }
            case Do.Web:
                if (t.Label?.StartsWith("Ask ") == true) return new(t.Label, "Opens in your browser, already asked", "", null, true);
                return new(t.Label ?? "Open in your browser", "Opens in your browser", t.Url?.Contains("/maps/") == true ? G.Map : t.Label?.StartsWith("Play") == true ? G.Play : G.Web, null, true);
            case Do.Power:
            {
                string verb = t.Op switch { "restart" => "Restart", "signout" => "Sign out", "cancel" => "Cancel the shutdown", _ => "Shut down" };
                if (t.Op == "cancel") return new(verb, "Stops a shutdown or restart that's on its way", G.Power, null, true);
                if (t.Op == "signout") return new("Sign out of Windows", "Open apps close · save your work first", G.Power, null, true);
                var d = t.For ?? TimeSpan.FromSeconds(30);
                string when = d < TimeSpan.FromMinutes(1) ? $"in {d.TotalSeconds:0} s" : $"at {(DateTime.Now + d).ToString("t", CultureInfo.CurrentCulture)} · in {LauncherDur(d)}";
                return new($"{verb} the PC", $"{char.ToUpper(when[0])}{when[1..]} · “cancel shutdown” stops it", t.Op == "restart" ? G.Restart : G.Power, null, true);
            }
            case Do.Sleep: return new("Put the PC to sleep", "Now", G.Moon, null, true);
            case Do.Lock: return new("Lock the PC", "Back to the sign-in screen", G.Lock, null, true);
            case Do.Screenshot: return new("Take a screenshot", "Snip part of the screen", G.Shot, null, true);
            case Do.RecycleBin: return new("Empty the Recycle Bin", "Frees the space for good", G.Bin, null, true);
            case Do.DesktopIcons:
            {
                bool want = t.On ?? true, now = DesktopIcons.Shown ?? true;
                return new(want ? "Show the desktop icons" : "Hide the desktop icons", want == now ? $"They're {(now ? "showing" : "hidden")} already" : "Your files stay where they are", G.Desk, null, want != now, want == now);
            }
            case Do.ClipText:
            {
                string? text = ClipText();
                if (t.Op == "clear") return new("Clear the clipboard", "What you copied goes (its history stays)", G.Clip, null, true);
                if (text == null) return new("Clipboard", "There's no copied text", G.Clip, null, false);
                if (t.Op == "count")
                {
                    int words = Regex.Matches(text, @"\S+").Count;
                    return new($"{words:N0} words · {text.Length:N0} characters", $"In what you copied · {text.Split('\n').Length} lines", G.Clip, null, false);
                }
                string after = ClipTransform(text, t.Op);
                string label = t.Op switch { "upper" => "UPPERCASE", "lower" => "lowercase", "title" => "Title Case", "sentence" => "Sentence case", _ => "plain text" };
                return new($"Clipboard to {label}", Preview(after), G.Clip, null, true);
            }
        }
        return new("…", "", G.Info, null, false);
    }

    static TaskView Switch(string name, bool want, bool now, string glyph, string? extra) =>
        new($"{(want ? "Turn on" : "Turn off")} {name}", want == now ? $"{name} is {(now ? "on" : "off")} already" : $"It's {(now ? "on" : "off")} now{(extra != null ? " · " + extra : "")}", glyph, null, want != now, want == now);

    static string Preview(string s)
    {
        string one = Regex.Replace(s, @"\s+", " ").Trim();
        return one.Length > 46 ? one[..45] + "…" : one;
    }

    static string LauncherDur(TimeSpan d) =>
        d.TotalSeconds < 60 ? $"{d.TotalSeconds:0} s"
        : d.TotalMinutes < 60 ? $"{d.TotalMinutes:0} min" + (d.Seconds > 0 ? $" {d.Seconds} s" : "")
        : d.TotalHours < 48 ? $"{(int)d.TotalHours} h" + (d.Minutes > 0 ? $" {d.Minutes} min" : "")
        : $"{(int)d.TotalDays} days";

    static string WhenText(DateTime at)
    {
        var c = CultureInfo.CurrentCulture;
        var now = DateTime.Now;
        string day = at.Date == now.Date ? "today" : at.Date == now.Date.AddDays(1) ? "tomorrow" : at.ToString("ddd d MMM", c);
        string text = $"{day} at {at.ToString("t", c)}";
        var left = at - now;
        if (left < TimeSpan.FromHours(24)) text += $" · in {LauncherDur(TimeSpan.FromMinutes(Math.Max(1, Math.Round(left.TotalMinutes))))}";
        return text;
    }

    // ───────────────────────────── doing ─────────────────────────────

    async void RunTasks(IReadOnlyList<Intent> list)
    {
        bool handsOff = list.Any(t => t.Kind is Do.Open or Do.Web or Do.Screenshot or Do.Lock or Do.Sleep or Do.Power);
        CloseLauncher(giveBack: !handsOff);
        await Task.Delay(260); // the notch settles before it says anything
        var said = new List<string>();
        foreach (var t in list)
        {
            if (list.Count > 1 && Describe(t).Done) continue; // that one's that way already
            try { if (await Execute(t) is { Length: > 0 } s) said.Add(s); }
            catch (Exception ex) { AppLog.Write("task " + t.Kind + ": " + ex.Message); }
            if (list.Count > 1) await Task.Delay(t.Kind == Do.Open ? 700 : 160);
        }
        if (list.Count == 1 && list[0].Kind == Do.Volume) { RefreshVolume(); ShowVolumeOsd(); return; }
        if (list.Count == 1 && list[0].Kind == Do.Brightness && _bri.Supported) { ShowBrightnessOsd(); return; }
        if (said.Count == 0) return;
        ShowOsd("task", G.Check, null, said[0], said.Count > 1 ? string.Join(" · ", said.Skip(1)) : "Done", null, glyphBrush: Blue, seconds: 2.4, force: true);
    }

    /// <summary>Does one step; returns what to say about it (null: it speaks for itself).</summary>
    async Task<string?> Execute(Intent t)
    {
        switch (t.Kind)
        {
            case Do.Volume:
            {
                float cur = _audio.Muted ? 0 : _audio.Volume;
                _lastUserChange = DateTime.UtcNow;
                switch (t.Op)
                {
                    case "mute": if (!_audio.Muted) _audio.ToggleMute(); RefreshVolume(); return "Muted";
                    case "unmute": if (_audio.Muted) _audio.ToggleMute(); RefreshVolume(); return "Sound on";
                    case "set": _audio.SetVolume((float)(t.Num / 100)); break;
                    case "up": _audio.SetVolume(cur + (float)(t.Num / 100)); break;
                    default: _audio.SetVolume(cur - (float)(t.Num / 100)); break;
                }
                RefreshVolume();
                return $"Volume {(int)Math.Round(_audio.Volume * 100)}%";
            }
            case Do.Brightness:
            {
                bool own = _bri.Supported;
                int cur = own ? _bri.Level : _extBri.Level;
                int to = Math.Clamp(t.Op switch { "set" => (int)t.Num, "up" => cur + (int)t.Num, _ => cur - (int)t.Num }, 0, 100);
                _lastUserChange = DateTime.UtcNow;
                if (own) _bri.Set(to); else if (_extBri.Supported) _extBri.Set(to); else return null;
                return $"Brightness {to}%";
            }
            case Do.Media:
                switch (t.Op)
                {
                    case "play": if (!_media.IsPlaying) await _media.TogglePlayPauseAsync(); break;
                    case "pause": if (_media.IsPlaying) await _media.TogglePlayPauseAsync(); break;
                    case "next": await _media.NextAsync(); break;
                    case "prev": await _media.PreviousAsync(); break;
                    default: await _media.TogglePlayPauseAsync(); break;
                }
                RefreshMedia();
                return null; // the song's own pop-up says it
            case Do.NowPlaying:
                try { Clipboard.SetText(_media.Title + (_media.Artist.Length > 0 ? " — " + _media.Artist : "")); } catch { }
                return "Copied the song's name";
            case Do.Wifi:
            {
                bool on = t.On ?? !_radio.WifiOn;
                await _radio.SetWifiAsync(on);
                return $"Wi-Fi {(on ? "on" : "off")}";
            }
            case Do.Bluetooth:
            {
                bool on = t.On ?? !_radio.BluetoothOn;
                await _radio.SetBluetoothAsync(on);
                return $"Bluetooth {(on ? "on" : "off")}";
            }
            case Do.Airplane:
            {
                bool on = t.On ?? !_radio.AirplaneOn;
                await _radio.SetAirplaneAsync(on);
                return $"Airplane mode {(on ? "on" : "off")}";
            }
            case Do.DarkMode:
            {
                bool dark = t.On ?? !WindowsDark();
                await Task.Run(() => SetWindowsDark(dark));
                return dark ? "Dark mode" : "Light mode";
            }
            case Do.Focus:
            {
                bool on = t.On ?? !_focus.IsOn;
                _focus.Set(on);
                _focusEnds?.Stop();
                if (on && t.For is { } f)
                {
                    _focusEnds ??= new DispatcherTimer(DispatcherPriority.Background);
                    _focusEnds.Interval = f;
                    _focusEnds.Tick -= OnFocusEnds;
                    _focusEnds.Tick += OnFocusEnds;
                    _focusEnds.Start();
                    return $"Focus until {(DateTime.Now + f).ToString("t", CultureInfo.CurrentCulture)}";
                }
                return on ? "Focus on" : "Focus off";
            }
            case Do.Timer:
                SetTimer(t.For!.Value);
                StartTimer();
                return $"{t.Label ?? "Timer"} · {LauncherDur(t.For.Value)}";
            case Do.Remind:
                AddReminder(t.Text!, t.At!.Value);
                return t.Text == "Alarm" ? $"Alarm set · {WhenText(t.At.Value)}" : $"I'll remind you · {WhenText(t.At.Value)}";
            case Do.CancelReminders:
            {
                int n = _reminders.Count;
                _reminders.Clear();
                SaveReminders();
                ScheduleReminders();
                return n == 1 ? "Reminder cancelled" : $"{n} reminders cancelled";
            }
            case Do.Open:
                if (t.Op == "switch" && RunningWindows(t.Text!) is { Count: > 0 } open) { FocusWindow(open[0].Hwnd); return null; }
                foreach (var n in t.Op == "switch" ? new List<string> { t.Text! } : OpenNames(t.Text!))
                {
                    if (LauncherAnswers.For(n)?.Url is { } url) { OpenTarget(url); continue; }
                    if (ResolveEntry(n) is { } e) { _index.Used(e); OpenTarget(e.Target); await Task.Delay(120); }
                }
                return null;
            case Do.Close:
            {
                var win = RunningWindows(t.Text!);
                if (win.Count == 0) return null;
                if (t.Op == "force")
                {
                    foreach (var pid in win.Select(w => w.Pid).Distinct())
                        try { using var p = Process.GetProcessById((int)pid); if (!p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { }
                    return $"Force-quit {win[0].Name}";
                }
                foreach (var w in win) PostMessage(w.Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                return $"Closing {win[0].Name}";
            }
            case Do.Web:
                OpenTarget(t.Url!);
                return null;
            case Do.Power:
            {
                string args = t.Op switch
                {
                    "cancel" => "/a",
                    "signout" => "/l",
                    "restart" => $"/r /t {(int)(t.For ?? TimeSpan.FromSeconds(30)).TotalSeconds}",
                    _ => $"/s /t {(int)(t.For ?? TimeSpan.FromSeconds(30)).TotalSeconds}",
                };
                try { Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false }); }
                catch (Exception ex) { AppLog.Write("shutdown: " + ex.Message); return null; }
                if (t.Op == "cancel") return "Shutdown cancelled";
                if (t.Op == "signout") return null;
                var d = t.For ?? TimeSpan.FromSeconds(30);
                return $"{(t.Op == "restart" ? "Restarting" : "Shutting down")} {(d < TimeSpan.FromMinutes(1) ? $"in {d.TotalSeconds:0} s" : "at " + (DateTime.Now + d).ToString("t", CultureInfo.CurrentCulture))}";
            }
            case Do.Sleep:
                After(500, () => SetSuspendState(false, false, false));
                return null;
            case Do.Lock:
                LockWorkStation();
                return null;
            case Do.Screenshot:
                After(350, () => Launch("ms-screenclip:"));
                return null;
            case Do.RecycleBin:
                await Task.Run(() => SHEmptyRecycleBin(IntPtr.Zero, null, 0x7));
                return "Recycle Bin emptied";
            case Do.DesktopIcons:
                if ((DesktopIcons.Shown ?? true) != (t.On ?? true)) DesktopIcons.Toggle();
                After(400, RefreshDesktopBtn);
                return t.On == false ? "Desktop icons hidden" : "Desktop icons showing";
            case Do.ClipText:
            {
                if (t.Op == "clear") { try { Clipboard.Clear(); } catch { } return "Clipboard cleared"; }
                if (ClipText() is not { } text || t.Op == "count") return null;
                try { Clipboard.SetText(ClipTransform(text, t.Op)); } catch { return null; }
                return "Clipboard changed";
            }
        }
        return null;
    }

    DispatcherTimer? _focusEnds;

    void OnFocusEnds(object? sender, EventArgs e)
    {
        _focusEnds?.Stop();
        if (!_focus.IsOn) return;
        _focus.Set(false);
        ShowOsd("task", G.Focus, null, "Focus done", "Notifications are back", null, glyphBrush: Blue, seconds: 2.6, force: true);
    }

    // ───────────────────────────── apps and windows ─────────────────────────────

    static List<string> OpenNames(string text) =>
        Regex.Split(text, @"\s*,\s*(?:and\s+)?|\s+and\s+|\s+&\s+").Select(n => n.Trim()).Where(n => n.Length > 0).ToList();

    /// <summary>The app, setting or folder a name most likely means (typos allowed for apps).</summary>
    LauncherIndex.Entry? ResolveEntry(string name)
    {
        string n = name.Trim();
        bool wantSetting = Regex.IsMatch(n, @"\bsettings?$");
        n = Regex.Replace(n, @"\s*\b(?:settings?|app|application|folder|window)$", "").Trim();
        if (n.Length < 2) return null;
        LauncherIndex.Entry? best = null;
        double bestScore = 0;
        foreach (var e in _index.Entries)
        {
            if (wantSetting && e.Kind != LauncherIndex.Kind.Setting) continue;
            double s = LauncherIndex.Score(n, e);
            if (s < 380) continue;
            s += _index.Habit(e);
            if (s > bestScore) { bestScore = s; best = e; }
        }
        return best;
    }

    static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");

    readonly Dictionary<uint, (string Exe, string Desc, string Name, string? Path)> _procInfo = new();

    /// <summary>The visible windows of the app a name means: by its file, its description or its title.</summary>
    List<(IntPtr Hwnd, uint Pid, string Name, string? Path)> RunningWindows(string name)
    {
        var found = new List<(IntPtr, uint, string, string?)>();
        string n = Norm(Regex.Replace(name, @"\s*\b(?:app|application|window|windows)$", ""));
        if (n.Length < 2) return found;
        var entry = ResolveEntry(name);
        string rn = entry?.Kind == LauncherIndex.Kind.App ? Norm(entry.Name) : "";
        uint me = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || Interop.Native.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
            string title = WindowTitle(h);
            if (title.Length == 0) return true;
            if (ClassName(h) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true; // the desktop and the taskbar are never "an app"
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == me) return true;
            if (!_procInfo.TryGetValue(pid, out var info))
            {
                string? path = ProcessPathById(pid);
                string exe = path != null ? Path.GetFileNameWithoutExtension(path) : "";
                string desc = "";
                try { if (path != null) desc = FileVersionInfo.GetVersionInfo(path).FileDescription ?? ""; } catch { }
                _procInfo[pid] = info = (Norm(exe), Norm(desc), desc.Trim().Length > 0 ? desc.Trim() : exe, path);
            }
            string nt = Norm(title);
            bool frame = info.Exe == "applicationframehost"; // a Store app: its name is in the title
            bool match = frame
                ? nt == n || rn.Length > 0 && nt == rn || nt.EndsWith(n) && n.Length >= 4
                : info.Exe == n || info.Desc == n || n.Length >= 3 && info.Exe.StartsWith(n) || n.Length >= 4 && info.Desc.Contains(n)
                  || rn.Length > 0 && (info.Desc == rn || info.Exe == rn);
            if (match) found.Add((h, pid, frame ? title : info.Name, frame ? null : info.Path));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

    // ───────────────────────────── Windows' light and dark ─────────────────────────────

    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    static bool WindowsDark()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey); return k?.GetValue("AppsUseLightTheme") is int v && v == 0; }
        catch { return false; }
    }

    static void SetWindowsDark(bool dark)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            // every open window hears about it, the way the Settings app tells them
            SendMessageTimeoutText(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "ImmersiveColorSet", 0x2 /* ABORTIFHUNG */, 200, out _);
        }
        catch (Exception ex) { AppLog.Write("dark mode: " + ex.Message); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, int msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

    // ───────────────────────────── the clipboard's text ─────────────────────────────

    static string? ClipText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { return null; }
    }

    static string ClipTransform(string text, string op)
    {
        var c = CultureInfo.CurrentCulture;
        return op switch
        {
            "upper" => text.ToUpper(c),
            "lower" => text.ToLower(c),
            "title" => c.TextInfo.ToTitleCase(text.ToLower(c)),
            "sentence" => Regex.Replace(text.ToLower(c), @"(^\s*|[.!?]\s+)(\p{Ll})", m => m.Groups[1].Value + m.Groups[2].Value.ToUpper(c)),
            _ => text,
        };
    }

    /// <summary>A password goes to the clipboard but not into any history, ours or Windows'.</summary>
    void CopySecret(string text)
    {
        try
        {
            var d = new DataObject();
            d.SetText(text);
            d.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
            d.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            d.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            Clipboard.SetDataObject(d, true);
        }
        catch { }
        CloseLauncher(giveBack: true);
        After(200, () => ShowOsd("copied", G.Lock, null, "Password copied", "Kept out of clipboard history", null, glyphBrush: Blue, seconds: 1.6, prio: Prio.Low));
    }

    // ───────────────────────────── reminders ─────────────────────────────

    sealed class ReminderItem
    {
        public string Text { get; set; } = "";
        public DateTime At { get; set; }
    }

    readonly List<ReminderItem> _reminders = new();
    DispatcherTimer? _remindTimer;
    static string RemindersFile => Path.Combine(Settings.Folder, "reminders.json");

    void LoadReminders()
    {
        try
        {
            if (File.Exists(RemindersFile) && JsonSerializer.Deserialize<List<ReminderItem>>(File.ReadAllText(RemindersFile)) is { } list)
                _reminders.AddRange(list.Where(r => r.Text.Length > 0));
        }
        catch (Exception ex) { AppLog.Write("reminders: " + ex.Message); }
        // the PC was off when one was due: say so a few seconds after the notch is up
        if (_reminders.Any(r => r.At <= DateTime.Now)) After(6000, CheckReminders);
        else ScheduleReminders();
    }

    void SaveReminders()
    {
        var snapshot = _reminders.Select(r => new ReminderItem { Text = r.Text, At = r.At }).ToList();
        _ = Task.Run(() =>
        {
            try { Directory.CreateDirectory(Settings.Folder); File.WriteAllText(RemindersFile, JsonSerializer.Serialize(snapshot)); }
            catch (Exception ex) { AppLog.Write("reminders: " + ex.Message); }
        });
    }

    void AddReminder(string text, DateTime at)
    {
        _reminders.Add(new ReminderItem { Text = text, At = at });
        _reminders.Sort((a, b) => a.At.CompareTo(b.At));
        SaveReminders();
        ScheduleReminders();
    }

    /// <summary>One timer, set for the next one due (checked at least every minute, so a clock change or sleep can't fool it).</summary>
    void ScheduleReminders(TimeSpan? retry = null)
    {
        _remindTimer ??= NewRemindTimer();
        _remindTimer.Stop();
        if (_reminders.Count == 0) return;
        var next = retry ?? _reminders.Min(r => r.At) - DateTime.Now;
        _remindTimer.Interval = next < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : next > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : next;
        _remindTimer.Start();
    }

    DispatcherTimer NewRemindTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background);
        t.Tick += (_, _) => CheckReminders();
        return t;
    }

    void CheckReminders()
    {
        var now = DateTime.Now;
        var due = _reminders.Where(r => r.At <= now).ToList();
        if (due.Count == 0) { ScheduleReminders(); return; }
        if (InGame) { ScheduleReminders(TimeSpan.FromSeconds(30)); return; } // after the game, not in the middle of it
        foreach (var r in due) _reminders.Remove(r);
        SaveReminders();
        for (int i = 0; i < due.Count; i++)
        {
            var r = due[i];
            bool late = now - r.At > TimeSpan.FromMinutes(2);
            if (i == 0) ShowReminder(r, late);
            else After(10500 * i, () => ShowReminder(r, late));
        }
        ScheduleReminders();
    }

    void ShowReminder(ReminderItem r, bool late)
    {
        try { _notificationSound.Play(); } catch { }
        PalReact(Controls.Companion.Act.Point, 3);
        string sub = late ? $"Was due {(r.At.Date == DateTime.Today ? "at " + r.At.ToString("t", CultureInfo.CurrentCulture) : r.At.ToString("ddd d MMM, ", CultureInfo.CurrentCulture) + r.At.ToString("t", CultureInfo.CurrentCulture))} · click: 10 more min"
            : "Reminder · click: 10 more min";
        ShowOsd("reminder", G.Bell, null, r.Text, sub, null, glyphBrush: Amber, seconds: 10, force: true, prio: Prio.High, motion: Motion.DoublePulse,
            onClick: () =>
            {
                AddReminder(r.Text, DateTime.Now.AddMinutes(10));
                After(150, () => ShowOsd("task", G.Bell, null, "Snoozed", "Again at " + DateTime.Now.AddMinutes(10).ToString("t", CultureInfo.CurrentCulture), null, glyphBrush: Amber, seconds: 1.8, force: true));
            });
    }

    /// <summary>"reminders": each one a line, Enter (or a click) cancels it.</summary>
    void AddReminderRows()
    {
        if (_reminders.Count == 0)
        {
            _hits.Add(new Hit("No reminders", "Try “remind me in 20 min to stretch”", G.Bell, null, () =>
            {
                if (_launchBox == null) return;
                _launchBox.Text = "remind me in 20 min to stretch";
                _launchBox.CaretIndex = _launchBox.Text.Length;
            }, Big: true));
            return;
        }
        foreach (var r in _reminders.Take(6))
        {
            var item = r;
            _hits.Add(new Hit(item.Text, WhenText(item.At) + " · Enter cancels", G.Bell, null, () =>
            {
                _reminders.Remove(item);
                SaveReminders();
                ScheduleReminders();
                Search();
            }));
        }
    }
}
