using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Everything the notch knows, readable by the AI in one tool: your calls (app, who, when, how long), reminders, the
/// timer, focus, what you copied, the shelf, notifications, what's playing, downloads, the PC's health and its
/// heavy moments, and a "today" summary of all of it. The AI reads these instead of telling you to go and look.
/// Also here: who a call was with, taken from the call app's window title while the call runs.
/// </summary>
public partial class NotchWindow
{
    string? _callWith;

    /// <summary>Who the call is with: the call window's title (a Discord channel or person, a Teams meeting...), cleaned.</summary>
    async void RememberCallPeople()
    {
        if (_call is not { } call) return;
        _callWith = null;
        foreach (int wait in new[] { 1500, 15000, 60000 })
        {
            await Task.Delay(wait);
            if (_call == null || _call.Key != call.Key) return;
            try
            {
                bool packaged = call.Path == null || !call.Key.Contains('\\');
                string[] parts = call.Key.Split('_', StringSplitOptions.RemoveEmptyEntries);
                var (hwnd, _) = await Task.Run(() => FindAppWindow(p => call.Path != null
                    ? string.Equals(p, call.Path, StringComparison.OrdinalIgnoreCase)
                    : parts.All(part => p.Contains(part, StringComparison.OrdinalIgnoreCase))));
                if (hwnd == IntPtr.Zero) continue;
                string title = WindowTitle(hwnd).Trim();
                // the app's own name and decorations aren't "who"
                title = Regex.Replace(title, @"\s*[|\-–—]\s*(Discord|Microsoft Teams|Teams|Zoom( Meeting| Workplace)?|WhatsApp|Skype|Slack|Google Meet|Messenger|Telegram|Signal)\s*$", "", RegexOptions.IgnoreCase);
                title = Regex.Replace(title, @"^\(\d+\)\s*", "");
                if (title.Length > 1 && !string.Equals(title, call.Name, StringComparison.OrdinalIgnoreCase)) _callWith = title.Length > 80 ? title[..80] : title;
            }
            catch { }
        }
    }

    // ───────────────────────────── the notch's data, for the AI ─────────────────────────────

    string NotchData(string what, string filter)
    {
        what = what.Trim().ToLowerInvariant();
        filter = filter.Trim();
        var sb = new StringBuilder();
        bool all = what is "" or "all" or "today" or "summary";
        bool Want(string part) => all || what.Contains(part);
        string When(DateTime local) => local.Date == DateTime.Today ? "today " + local.ToString("HH:mm") : local.Date == DateTime.Today.AddDays(-1) ? "yesterday " + local.ToString("HH:mm") : local.ToString("ddd d MMM HH:mm");
        string Long(int seconds) => seconds >= 3600 ? $"{seconds / 3600} h {seconds % 3600 / 60:00} min" : seconds >= 60 ? $"{seconds / 60} min {seconds % 60:00} s" : $"{seconds} s";
        // a day in the filter ("yesterday", "today", "monday") filters by day; other words by text
        DateTime? day = filter.ToLowerInvariant() switch
        {
            "today" => DateTime.Today, "yesterday" => DateTime.Today.AddDays(-1),
            var f when Enum.TryParse<DayOfWeek>(f, true, out var dow) => Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(-i)).First(d => d.DayOfWeek == dow),
            _ => null,
        };
        string text = day != null ? "" : filter;
        bool Match(string? s) => text.Length == 0 || s != null && s.Contains(text, StringComparison.OrdinalIgnoreCase);
        bool OnDay(DateTime local) => day == null || local.Date == day.Value;

        if (Want("call"))
        {
            sb.AppendLine("CALLS (newest first; recorded when they last 10 s or more" + (_settings.CallHistory ? "" : "; call history is OFF in Preferences") + "):");
            if (_call is { } now) sb.AppendLine($"- NOW: on a call in {now.Name}{(_callWith != null ? " with " + _callWith : "")}, for {Long((int)(DateTime.UtcNow - now.Started).TotalSeconds)}");
            var calls = _history.Where(c => (Match(c.Name) || Match(c.With)) && OnDay(c.Start.ToLocalTime())).Take(all ? 5 : 30).ToList();
            if (calls.Count == 0 && filter.Length > 0 && _history.Count > 0)
            {
                sb.AppendLine($"- none matching “{filter}”; all recent calls:");
                calls = _history.Take(10).ToList();
            }
            if (calls.Count == 0) sb.AppendLine("- none recorded");
            foreach (var c in calls) sb.AppendLine($"- {When(c.Start.ToLocalTime())} · {c.Name}{(c.With is { Length: > 0 } w ? " · with/where: " + w : "")} · {Long(c.Seconds)}");
            if (calls.Count > 0 && calls.All(c => c.With == null)) sb.AppendLine("  (who a call was with is known only for calls from now on: it's read from the call app's window title)");
        }
        if (Want("remind"))
        {
            sb.AppendLine("REMINDERS:");
            if (_reminders.Count == 0) sb.AppendLine("- none");
            foreach (var r in _reminders.OrderBy(r => r.At)) sb.AppendLine($"- {When(r.At)} · {r.Text}");
        }
        if (Want("timer") || Want("focus"))
        {
            sb.AppendLine($"TIMER: {(_timerRunning ? $"running, {(int)Math.Ceiling((_timerEnd - DateTime.UtcNow).TotalMinutes)} min left of {(int)_timerTotal.TotalMinutes}" : "not running")}");
            sb.AppendLine($"FOCUS: {(_focus.IsOn ? $"on for {(int)_focus.Elapsed.TotalMinutes} min" + (_focus.Held.Count > 0 ? $", {_focus.Held.Count} notifications held from " + string.Join(", ", _focus.Held.Distinct().Take(5)) : "") : "off")}");
        }
        if (Want("media") || Want("music") || Want("play"))
            sb.AppendLine(_media.Title.Length > 0 ? $"PLAYING: {_media.Title}{(_media.Artist.Length > 0 ? " — " + _media.Artist : "")} ({(_media.IsPlaying ? "playing" : "paused")})" : "PLAYING: nothing");
        if (Want("notif"))
        {
            sb.AppendLine("NOTIFICATIONS (today, newest first):");
            sb.AppendLine(RecentToastsText().Split('\n').Where(Match).Take(all ? 8 : 60).Aggregate(new StringBuilder(), (b, l) => b.AppendLine("- " + l)).ToString().TrimEnd());
        }
        if (Want("clip") || Want("copied"))
        {
            sb.AppendLine("CLIPBOARD HISTORY (newest first):");
            var clips = _clips.Where(c => c.Text != null && Match(c.Text) && OnDay(c.At.ToLocalTime())).Take(all ? 4 : 20).ToList();
            if (clips.Count == 0 && filter.Length > 0) clips = _clips.Where(c => c.Text != null).Take(10).ToList();
            if (clips.Count == 0) sb.AppendLine("- nothing");
            foreach (var c in clips) sb.AppendLine($"- {When(c.At.ToLocalTime())}{(c.Pinned ? " (pinned)" : "")}: {Cut1(c.Text!, all ? 120 : 400)}");
            int pictures = _clips.Count(c => c.Image != null);
            if (pictures > 0) sb.AppendLine($"- and {pictures} copied picture(s)");
        }
        if (Want("shelf"))
        {
            sb.AppendLine("SHELF (kept files and text):");
            if (_shelf.Count == 0) sb.AppendLine("- empty");
            foreach (var s in _shelf.Take(20)) sb.AppendLine("- " + (s.Path ?? Cut1(s.Text ?? "", 160)));
        }
        if (Want("download"))
        {
            sb.AppendLine("DOWNLOADS (the notch's view: in progress and recent):");
            var items = _files.Items.Take(10).ToList();
            if (items.Count == 0) sb.AppendLine("- none tracked right now (use browser_history for what was downloaded)");
            foreach (var d in items) sb.AppendLine($"- {Path.GetFileName(d.Path)} · {SystemMonitor.Bytes(d.Bytes)}{(d.Total > 0 && d.Bytes < d.Total ? $" of {SystemMonitor.Bytes(d.Total)}" : "")} · {When(d.Started.ToLocalTime())} · {d.Path}");
        }
        if (Want("device") || Want("batter") || Want("mouse") || Want("headset") || Want("bluetooth") || Want("setup") || Want("system"))
            sb.AppendLine("DEVICES:\n" + DevicesText());
        if (Want("app") || Want("usage") || Want("time") || Want("used") || Want("screen time"))
            sb.AppendLine(AppTimeText(day));
        if (Want("system") || Want("health") || Want("pc") || Want("strain"))
        {
            sb.AppendLine("PC NOW: " + PcStatus().Replace("\n", " · "));
            var events = StrainLog.Events.OrderByDescending(e => e.At).Take(all ? 2 : 8).ToList();
            if (events.Count > 0)
            {
                sb.AppendLine("HEAVY MOMENTS the notch noticed (newest first):");
                foreach (var e in events)
                    sb.AppendLine($"- {When(e.At.ToLocalTime())} · {e.Name} {e.Value:0}{e.Unit} for {Long(e.Seconds)} · top: {string.Join(", ", e.TopCpu.Take(3).Select(p => p.Name))}{(e.Causes.Count > 0 ? " · likely: " + string.Join("; ", e.Causes.Take(2)) : "")}");
            }
        }
        if (sb.Length == 0) return "Nothing called “" + what + "”. Ask for: today, calls, reminders, timer, focus, media, notifications, clipboard, shelf, downloads, devices, apps (time in each app), system.";
        return sb.ToString().TrimEnd();
    }

    /// <summary>The user's gear as the notch sees it: the PC's battery, the wireless mouse and headset (read from their receivers), Bluetooth devices, the sound output.</summary>
    string DevicesText()
    {
        var lines = new List<string>();
        lines.Add(_power.HasBattery ? $"- PC (laptop) battery: {_power.Percent}%{(_power.PluggedIn ? ", plugged in" : ", on battery")}" : "- PC: desktop, no battery");
        foreach (var d in _devices.Devices)
            lines.Add($"- {(d.Kind == DeviceKind.Mouse ? "Mouse" : "Headset")} {d.Name}: " + d.State switch
            {
                BatteryState.Live => $"{d.Level}%{(d.Charging ? ", charging" : "")}",
                BatteryState.Stale => d.Level is { } l ? $"{l}% (last reading, it's asleep or off now)" : "asleep or off",
                _ => "off (its receiver is plugged in)" + (d.Level is { } l2 ? $", last known {l2}%" : ""),
            });
        foreach (var b in _bt.ConnectedDevices)
            lines.Add($"- Bluetooth {b.Name}{(b.Category.Length > 0 ? $" ({b.Category})" : "")}: connected{(b.Battery is { } pct ? $", {pct}% battery" : ", doesn't report its battery")}");
        if (_devices.Devices.Count == 0 && _bt.ConnectedDevices.Count == 0) lines.Add("- no wireless mouse/headset receiver or Bluetooth device connected");
        if (_audio.Outputs.FirstOrDefault(o => o.IsDefault) is { } output) lines.Add($"- Sound plays on: {output.Name}{(output.IsHeadphones ? " (headphones)" : "")}");
        return string.Join("\n", lines);
    }

    static string Cut1(string s, int max) { s = Regex.Replace(s, @"\s+", " ").Trim(); return s.Length > max ? s[..max] + "…" : s; }
}
