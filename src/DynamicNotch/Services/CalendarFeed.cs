using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// Your calendar, read from its private iCal address (Google Calendar, Outlook.com, iCloud, Proton… all give one):
/// no sign-in, read-only. The address is a secret, so it's kept encrypted for this Windows account only
/// (calendar.bin) and never sent to the AI. Repeating events, time zones and all-day events are understood.
/// </summary>
public static class CalendarFeed
{
    public sealed record Event(DateTime Start, DateTime End, bool AllDay, string Title, string Location, string Calendar);

    static string LinkFile => Path.Combine(Settings.Folder, "calendar.bin");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static (DateTime At, List<(string Text, string Name)> Feeds)? _cache;

    static readonly Regex LinkLike = new(@"(?:webcal|https?)://\S+?(?:\.ics\b|/ical/|/calendar/ical)\S*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A calendar's private address in this text, if there is one.</summary>
    public static string? FindLink(string text) => LinkLike.Match(text) is { Success: true } m ? m.Value.TrimEnd('.', ',', ')', '>') : null;

    public static List<string> Links()
    {
        try
        {
            if (!File.Exists(LinkFile)) return new();
            var plain = OpenRouter.Unseal(File.ReadAllBytes(LinkFile));
            return plain == null ? new() : Encoding.UTF8.GetString(plain).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch { return new(); }
    }

    public static bool Connected => File.Exists(LinkFile);

    public static void Add(string link)
    {
        var all = Links();
        if (!all.Contains(link)) all.Add(link);
        Save(all);
    }

    public static void Forget() { try { File.Delete(LinkFile); } catch { } _cache = null; }

    static void Save(List<string> links)
    {
        Directory.CreateDirectory(Settings.Folder);
        var sealedBytes = OpenRouter.Seal(Encoding.UTF8.GetBytes(string.Join("\n", links)));
        if (sealedBytes != null) File.WriteAllBytes(LinkFile, sealedBytes);
        _cache = null;
    }

    /// <summary>Checks a link works (and how many events it has in the coming week) before keeping it.</summary>
    public static async Task<(bool Ok, string Name, int Week)> Try(string link)
    {
        try
        {
            string text = await Http.GetStringAsync(Plain(link));
            if (!text.Contains("BEGIN:VCALENDAR")) return (false, "", 0);
            var events = Parse(text, DateTime.Today, DateTime.Today.AddDays(7));
            return (true, CalName(text), events.Count);
        }
        catch { return (false, "", 0); }

        static string Plain(string l) => l.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + l[9..] : l;
    }

    /// <summary>The events between two moments, from every calendar connected (read again every 10 minutes at most).</summary>
    public static async Task<List<Event>> Events(DateTime from, DateTime to)
    {
        var links = Links();
        if (links.Count == 0) return new();
        if (_cache is not { } c || DateTime.UtcNow - c.At > TimeSpan.FromMinutes(10))
        {
            var feeds = new List<(string, string)>();
            foreach (var l in links)
            {
                try
                {
                    string url = l.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + l[9..] : l;
                    string text = await Http.GetStringAsync(url);
                    feeds.Add((text, CalName(text)));
                }
                catch { if (_cache is { } old) feeds.AddRange(old.Feeds.Where(f => !feeds.Contains(f))); }
            }
            _cache = (DateTime.UtcNow, feeds);
        }
        var all = new List<Event>();
        foreach (var (text, _) in _cache!.Value.Feeds) all.AddRange(Parse(text, from, to));
        return all.DistinctBy(e => (e.Start, e.Title)).OrderBy(e => e.Start).ToList();
    }

    static string CalName(string ics) => Regex.Match(ics, @"^X-WR-CALNAME:(.+)$", RegexOptions.Multiline) is { Success: true } m ? Unescape(m.Groups[1].Value.Trim()) : "Calendar";

    // ───────────────────────────── reading the iCal text ─────────────────────────────

    /// <summary>Every event (repeats unrolled) that overlaps [from, to), in this PC's time.</summary>
    public static List<Event> Parse(string ics, DateTime from, DateTime to)
    {
        string cal = CalName(ics);
        // lines folded onto the next one (starting with a space) are joined back
        var lines = Regex.Replace(ics.Replace("\r\n", "\n"), @"\n[ \t]", "").Split('\n');
        var raw = new List<Dictionary<string, List<(string Params, string Value)>>>();
        Dictionary<string, List<(string, string)>>? cur = null;
        foreach (var line in lines)
        {
            if (line == "BEGIN:VEVENT") { cur = new(StringComparer.OrdinalIgnoreCase); continue; }
            if (line == "END:VEVENT") { if (cur != null) raw.Add(cur); cur = null; continue; }
            if (cur == null) continue;
            int colon = IndexOfValue(line);
            if (colon < 0) continue;
            string head = line[..colon], value = line[(colon + 1)..];
            int semi = head.IndexOf(';');
            string key = semi < 0 ? head : head[..semi], prms = semi < 0 ? "" : head[(semi + 1)..];
            if (!cur.TryGetValue(key, out var list)) cur[key] = list = new();
            list.Add((prms, value));
        }

        // moved or changed single occurrences of a repeating event
        var moved = new HashSet<(string Uid, DateTime At)>();
        foreach (var e in raw)
            if (e.TryGetValue("RECURRENCE-ID", out var rid) && e.TryGetValue("UID", out var uid) && When(rid[0].Params, rid[0].Value) is { } at)
                moved.Add((uid[0].Value, at.Time));

        var found = new List<Event>();
        foreach (var e in raw)
        {
            try
            {
                if (e.TryGetValue("STATUS", out var st) && st[0].Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.TryGetValue("DTSTART", out var ds) || When(ds[0].Params, ds[0].Value) is not { } start) continue;
                TimeSpan length;
                if (e.TryGetValue("DTEND", out var de) && When(de[0].Params, de[0].Value) is { } end) length = end.Time - start.Time;
                else if (e.TryGetValue("DURATION", out var du)) length = Duration(du[0].Value);
                else length = start.AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;
                if (length < TimeSpan.Zero) length = TimeSpan.Zero;
                string title = e.TryGetValue("SUMMARY", out var s) ? Unescape(s[0].Value) : "(no title)";
                string place = e.TryGetValue("LOCATION", out var l) ? Unescape(l[0].Value) : "";
                string uidV = e.TryGetValue("UID", out var u) ? u[0].Value : "";
                bool isOverride = e.ContainsKey("RECURRENCE-ID");
                var skip = new HashSet<DateTime>();
                if (e.TryGetValue("EXDATE", out var ex))
                    foreach (var (p, v) in ex)
                        foreach (var one in v.Split(','))
                            if (When(p, one) is { } x) skip.Add(x.Time);

                IEnumerable<(DateTime Wall, TimeZoneInfo? Zone)> starts = !isOverride && e.TryGetValue("RRULE", out var rr)
                    ? Repeat(start, rr[0].Value, from.AddDays(-2), to)
                    : new[] { (start.Wall, start.Zone) };
                foreach (var (wall, zone) in starts)
                {
                    DateTime at = start.AllDay ? wall.Date : ToLocal(wall, zone);
                    if (skip.Contains(at) || !isOverride && wall != start.Wall && moved.Contains((uidV, at))) continue;
                    var endAt = at + length;
                    if (endAt <= from && !(length == TimeSpan.Zero && at >= from) || at >= to) continue;
                    found.Add(new Event(at, endAt, start.AllDay, title, place, cal));
                }
            }
            catch { }
        }
        return found.OrderBy(f => f.Start).ToList();
    }

    /// <summary>The ':' that starts the value (a ':' inside a quoted parameter doesn't).</summary>
    static int IndexOfValue(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) return i;
        }
        return -1;
    }

    sealed record Moment(DateTime Wall, TimeZoneInfo? Zone, bool AllDay)
    {
        /// <summary>In this PC's time (all-day: the date).</summary>
        public DateTime Time => AllDay ? Wall.Date : ToLocal(Wall, Zone);
    }

    /// <summary>"20261119T090000Z", "…T090000" with TZID=Europe/Paris, or "20261119" (all day).</summary>
    static Moment? When(string prms, string value)
    {
        value = value.Trim();
        bool allDay = prms.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) && !prms.Contains("DATE-TIME", StringComparison.OrdinalIgnoreCase) || value.Length == 8;
        if (allDay)
            return DateTime.TryParseExact(value[..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? new Moment(d, null, true) : null;
        bool utc = value.EndsWith("Z");
        if (!DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", null, System.Globalization.DateTimeStyles.None, out var t)) return null;
        if (utc) return new Moment(t, TimeZoneInfo.Utc, false);
        var tz = Regex.Match(prms, @"TZID=""?([^;:""]+)""?", RegexOptions.IgnoreCase);
        return new Moment(t, tz.Success ? Zone(tz.Groups[1].Value) : null, false);
    }

    static readonly Dictionary<string, TimeZoneInfo?> _zones = new();

    static TimeZoneInfo? Zone(string id)
    {
        lock (_zones)
        {
            if (_zones.TryGetValue(id, out var z)) return z;
            try { z = TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch
            {
                try { z = TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win) ? TimeZoneInfo.FindSystemTimeZoneById(win) : null; } catch { z = null; }
            }
            return _zones[id] = z;
        }
    }

    static DateTime ToLocal(DateTime wall, TimeZoneInfo? zone)
    {
        if (zone == null) return wall; // "floating": already this PC's time
        try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local); }
        catch { return wall; }
    }

    static TimeSpan Duration(string v)
    {
        var m = Regex.Match(v, @"P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?");
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        return new TimeSpan(G(1) * 7 + G(2), G(3), G(4), G(5));
    }

    static readonly string[] Days = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

    /// <summary>The start of each repeat (in the event's own wall time), from the first up to <paramref name="until"/>.</summary>
    static IEnumerable<(DateTime, TimeZoneInfo?)> Repeat(Moment start, string rule, DateTime from, DateTime until)
    {
        var parts = rule.Split(';').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0].ToUpperInvariant(), p => p[1]);
        string freq = parts.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        int every = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out int iv) && iv > 0 ? iv : 1;
        int count = int.TryParse(parts.GetValueOrDefault("COUNT"), out int cn) ? cn : int.MaxValue;
        DateTime last = until.AddDays(1);
        if (parts.TryGetValue("UNTIL", out var u) && When(u.Length == 8 ? "VALUE=DATE" : "", u) is { } um)
        {
            var lim = um.AllDay ? um.Wall.Date.AddDays(1).AddTicks(-1) : (start.Zone != null && um.Zone == TimeZoneInfo.Utc ? TimeZoneInfo.ConvertTimeFromUtc(um.Wall, start.Zone) : um.Wall);
            if (lim < last) last = lim;
        }
        var byDay = parts.TryGetValue("BYDAY", out var bd) ? bd.Split(',').Select(d => d.Trim().ToUpperInvariant()).ToList() : new List<string>();
        var byMonthDay = parts.TryGetValue("BYMONTHDAY", out var bm) ? bm.Split(',').Select(x => int.TryParse(x, out int n) ? n : 0).Where(n => n != 0).ToList() : new List<int>();
        var t0 = start.Wall;
        int made = 0, first = 0;
        // no count to keep: start a little before the window instead of at the first time (a daily event since 2012…)
        if (count == int.MaxValue && from > t0)
        {
            double days = (from - t0).TotalDays;
            first = freq switch
            {
                "DAILY" => (int)(days / every) - 2,
                "WEEKLY" => (int)(days / (7.0 * every)) - 2,
                "MONTHLY" => (int)(days / (31.0 * every)) - 2,
                "YEARLY" => (int)(days / (366.0 * every)) - 2,
                _ => 0,
            };
            first = Math.Max(0, first);
        }
        for (int step = first; step < first + 5000 && made < count; step++)
        {
            IEnumerable<DateTime> batch;
            switch (freq)
            {
                case "DAILY": batch = new[] { t0.AddDays((double)step * every) }; break;
                case "WEEKLY":
                {
                    var weekStart = t0.Date.AddDays(-(int)t0.DayOfWeek).AddDays(7.0 * step * every);
                    batch = byDay.Count == 0 ? new[] { t0.AddDays(7.0 * step * every) }
                        : byDay.Select(d => Array.IndexOf(Days, d[^2..])).Where(i => i >= 0).OrderBy(i => i).Select(i => weekStart.AddDays(i) + t0.TimeOfDay);
                    break;
                }
                case "MONTHLY":
                {
                    var month = new DateTime(t0.Year, t0.Month, 1).AddMonths(step * every);
                    if (byDay.Count > 0) batch = byDay.Select(d => NthWeekday(month, d)).Where(d => d != null).Select(d => d!.Value + t0.TimeOfDay);
                    else if (byMonthDay.Count > 0) batch = byMonthDay.Select(n => n > 0 ? (n <= DateTime.DaysInMonth(month.Year, month.Month) ? month.AddDays(n - 1) : (DateTime?)null) : month.AddMonths(1).AddDays(n)).Where(d => d != null).Select(d => d!.Value + t0.TimeOfDay);
                    else batch = t0.Day <= DateTime.DaysInMonth(month.Year, month.Month) ? new[] { month.AddDays(t0.Day - 1) + t0.TimeOfDay } : Array.Empty<DateTime>();
                    break;
                }
                case "YEARLY":
                {
                    int y = t0.Year + step * every;
                    batch = t0.Month == 2 && t0.Day == 29 && !DateTime.IsLeapYear(y) ? Array.Empty<DateTime>() : new[] { new DateTime(y, t0.Month, t0.Day) + t0.TimeOfDay };
                    break;
                }
                default: yield return (t0, start.Zone); yield break;
            }
            bool past = false;
            foreach (var t in batch.OrderBy(x => x))
            {
                if (t < t0) continue;
                if (t > last) { past = true; break; }
                if (++made > count) yield break;
                yield return (t, start.Zone);
            }
            if (past) yield break;
        }
    }

    /// <summary>"2MO" → the 2nd Monday of the month, "-1FR" → the last Friday, "MO" → the first.</summary>
    static DateTime? NthWeekday(DateTime month, string code)
    {
        int day = Array.IndexOf(Days, code[^2..]);
        if (day < 0) return null;
        int n = code.Length > 2 && int.TryParse(code[..^2], out int k) ? k : 1;
        if (n > 0)
        {
            var d = month.AddDays((day - (int)month.DayOfWeek + 7) % 7).AddDays(7 * (n - 1));
            return d.Month == month.Month ? d : null;
        }
        var lastDay = month.AddMonths(1).AddDays(-1);
        var b = lastDay.AddDays(-(((int)lastDay.DayOfWeek - day + 7) % 7)).AddDays(7 * (n + 1));
        return b.Month == month.Month ? b : null;
    }

    static string Unescape(string s) => s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
}
