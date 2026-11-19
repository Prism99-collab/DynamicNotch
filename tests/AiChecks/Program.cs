// The AI's checks: how messages are read, the calendar, and reading documents. Exit code 1 when something fails.
using System.IO;
using System.Reflection;
using DynamicNotch;
using System.Text.Json.Nodes;
using DynamicNotch.Services;

int failed = 0, passed = 0;
void Check(bool ok, string what) { if (ok) passed++; else { failed++; Console.WriteLine("FAIL " + what); } }

// ───────── how a message is read ─────────
var askKind = typeof(DynamicNotch.NotchWindow).GetMethod("AskKind", BindingFlags.NonPublic | BindingFlags.Static)!;
foreach (var raw in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "cases.txt")))
{
    if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith('#')) continue;
    var parts = raw.Split('|', 3).Select(p => p.Trim()).ToArray();
    if (parts.Length < 3) continue;
    var (kind, play, msg) = (parts[0], parts[1], parts[2]);
    string got = askKind.Invoke(null, new object[] { msg })!.ToString()!;
    var plays = AiPlaybook.For(msg).Select(p => p.Name).ToList();
    Check(kind == "*" || got == kind, $"“{msg}” read as {got}, should be {kind}");
    if (play == "-") Check(plays.Count == 0, $"“{msg}” pulls in know-how it shouldn't: {string.Join(", ", plays)}");
    else if (play.Length > 0) Check(plays.Contains(play), $"“{msg}” misses the know-how “{play}” (got: {string.Join(", ", plays)})");
}

// ───────── the calendar ─────────
var d = DateTime.Today;
string Day(DateTime t) => t.ToString("yyyyMMdd");
string ics = $"""
BEGIN:VCALENDAR
X-WR-CALNAME:Test
BEGIN:VEVENT
UID:one
DTSTART:{Day(d)}T{DateTime.Today.AddHours(10).ToUniversalTime():HHmmss}Z
DTEND:{Day(d)}T{DateTime.Today.AddHours(11).ToUniversalTime():HHmmss}Z
SUMMARY:Standup\, team
LOCATION:Room 2
END:VEVENT
BEGIN:VEVENT
UID:weekly
DTSTART;TZID=Africa/Casablanca:{Day(d.AddDays(-21))}T140000
DTEND;TZID=Africa/Casablanca:{Day(d.AddDays(-21))}T150000
RRULE:FREQ=WEEKLY;BYDAY={string.Join(",", new[] { "SU", "MO", "TU", "WE", "TH", "FR", "SA" })}
EXDATE;TZID=Africa/Casablanca:{Day(d.AddDays(1))}T140000
SUMMARY:Every day but tomorrow
END:VEVENT
BEGIN:VEVENT
UID:daily3
DTSTART:{Day(d.AddDays(-1))}T080000
RRULE:FREQ=DAILY;COUNT=3
SUMMARY:Three mornings
END:VEVENT
BEGIN:VEVENT
UID:allday
DTSTART;VALUE=DATE:{Day(d)}
DTEND;VALUE=DATE:{Day(d.AddDays(1))}
SUMMARY:Holiday
END:VEVENT
BEGIN:VEVENT
UID:gone
DTSTART:{Day(d)}T170000
SUMMARY:Cancelled one
STATUS:CANCELLED
END:VEVENT
BEGIN:VEVENT
UID:weekly
RECURRENCE-ID;TZID=Africa/Casablanca:{Day(d.AddDays(2))}T140000
DTSTART;TZID=Africa/Casablanca:{Day(d.AddDays(2))}T160000
DTEND;TZID=Africa/Casablanca:{Day(d.AddDays(2))}T170000
SUMMARY:Moved one
END:VEVENT
END:VCALENDAR
""";
var today = CalendarFeed.Parse(ics, d, d.AddDays(1));
Check(today.Any(e => e.Title == "Standup, team" && e.Start == d.AddHours(10) && e.Location == "Room 2"), "calendar: a timed event (UTC) at its local time, with its place");
Check(today.Count(e => e.Title == "Every day but tomorrow") == 1, "calendar: a weekly repeat shows today");
Check(today.Any(e => e.Title == "Holiday" && e.AllDay), "calendar: an all-day event");
Check(!today.Any(e => e.Title == "Cancelled one"), "calendar: a cancelled event is left out");
Check(today.Count(e => e.Title == "Three mornings") == 1, "calendar: a daily repeat with a count");
var tomorrow = CalendarFeed.Parse(ics, d.AddDays(1), d.AddDays(2));
Check(!tomorrow.Any(e => e.Title == "Every day but tomorrow"), "calendar: an excluded date (EXDATE)");
Check(tomorrow.Any(e => e.Title == "Three mornings"), "calendar: the count's last day");
Check(!CalendarFeed.Parse(ics, d.AddDays(2), d.AddDays(3)).Any(e => e.Title == "Three mornings"), "calendar: nothing after the count");
var later = CalendarFeed.Parse(ics, d.AddDays(2), d.AddDays(3));
Check(later.Any(e => e.Title == "Moved one") && !later.Any(e => e.Title == "Every day but tomorrow"), "calendar: a moved occurrence replaces the usual one");
string old = $"""
BEGIN:VCALENDAR
BEGIN:VEVENT
UID:o1
DTSTART:20120105T073000
RRULE:FREQ=DAILY
SUMMARY:Since 2012
END:VEVENT
BEGIN:VEVENT
UID:o2
DTSTART:{d.AddYears(-12):yyyyMMdd}T090000
RRULE:FREQ=MONTHLY
SUMMARY:Monthly for years
END:VEVENT
BEGIN:VEVENT
UID:o3
DTSTART:{d.AddYears(-30):yyyyMMdd}
RRULE:FREQ=YEARLY
SUMMARY:Birthday
END:VEVENT
END:VCALENDAR
""";
var oldToday = CalendarFeed.Parse(old, d, d.AddDays(1));
Check(oldToday.Any(e => e.Title == "Since 2012" && e.Start == d.AddHours(7.5)), "calendar: a daily event started years ago still shows today");
Check(oldToday.Any(e => e.Title == "Monthly for years" && e.Start == d.AddHours(9)), "calendar: a monthly event started years ago shows on its day");
Check(oldToday.Any(e => e.Title == "Birthday" && e.AllDay), "calendar: a yearly all-day event (a birthday) from long ago");
var sw = System.Diagnostics.Stopwatch.StartNew();
for (int i = 0; i < 50; i++) CalendarFeed.Parse(old, d, d.AddDays(7));
Check(sw.ElapsedMilliseconds < 1500, $"calendar: reading is quick ({sw.ElapsedMilliseconds} ms for 50 reads)");
Check(CalendarFeed.FindLink("here https://calendar.google.com/calendar/ical/abc%40gmail.com/private-123/basic.ics thanks") is { } l && l.EndsWith("basic.ics"), "calendar: a pasted Google link is recognised");
Check(CalendarFeed.FindLink("open https://www.youtube.com/watch?v=abc") == null, "calendar: other links aren't taken for a calendar");

// ───────── deep search: the passages that answer the question ─────────
string page = "Welcome to our site. Subscribe to our newsletter for updates.\n\nThe company was founded in 2003 and makes cars.\n\nThe Model Y price starts at $44,990 in the US, the company said in its latest update.\n\nOur privacy policy and cookie settings. All rights reserved.";
string kept = WebTools.Passages(page, WebTools.KeyWords("what's the price of the tesla model y"), 400);
Check(kept.Contains("$44,990") && !kept.Contains("newsletter") && !kept.Contains("cookie"), "deep search: the passage with the answer is kept, the page's clutter isn't (got: " + kept + ")");
Check(string.Join(" ", WebTools.KeyWords("whast the price that tesla used for the model y")) == "whast price tesla model", "deep search: the question's key words (got: " + string.Join(" ", WebTools.KeyWords("whast the price that tesla used for the model y")) + ")");
var effortFor = typeof(DynamicNotch.NotchWindow).GetMethod("EffortFor", BindingFlags.NonPublic | BindingFlags.Static)!;
var kindOf = (string q) => askKind.Invoke(null, new object[] { q })!;
string Effort(string q) => (string)effortFor.Invoke(null, new object[] { kindOf(q), q, false })!;
Check(Effort("open spotify") == "low" && Effort("how are you") == "low" && Effort("who won the ballon d'or 2026") == "low", "thinking: little for commands, chat and plain facts");
Check(Effort("should i buy a ps5 or a gaming pc for 600 euros") == "medium" && Effort("make me an excel table of my expenses") == "medium", "thinking: more for choices and things to build");
Check(Effort("think carefully: is it worth learning rust in 2026") == "high", "thinking: the most when asked to think carefully");

// ───────── charts: the AI's numbers are read, bad ones refused ─────────
var chartParse = typeof(DynamicNotch.NotchWindow).Assembly.GetType("DynamicNotch.ChartData")!.GetMethod("Parse")!;
object? Chart(string json) { try { return chartParse.Invoke(null, new object[] { json }); } catch { return null; } }
Check(Chart("{\"type\":\"line\",\"labels\":[\"Thu\",\"Fri\"],\"series\":[{\"name\":\"High\",\"values\":[23,21]}]}") != null, "charts: a normal line chart is read");
Check(Chart("{\"type\":\"bar\",\"series\":[{\"values\":[\"3.4M\",\"1,9\",null]}]}") != null, "charts: numbers written as text, and gaps, are read");
Check(Chart("{\"type\":\"line\",\"series\":[]}") == null && Chart("not json") == null, "charts: no numbers or broken data is refused (shown as text instead)");
var chartRule = (string)typeof(DynamicNotch.NotchWindow).GetField("ChartWords", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!.ToString()!;
Check(System.Text.RegularExpressions.Regex.IsMatch("make me a graph of these changes in function of time", chartRule, System.Text.RegularExpressions.RegexOptions.IgnoreCase), "charts: \"make me a graph\" is seen as a chart request");

var chartWords = (System.Text.RegularExpressions.Regex)typeof(DynamicNotch.NotchWindow).GetField("ChartWords", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
Check(chartWords.IsMatch("show me this as a bar chart") && chartWords.IsMatch("plot the price of gold this year") && chartWords.IsMatch("fais moi un graphique des ventes"), "charts: asking for a chart, in any wording");
Check(!chartWords.IsMatch("what's top of the billboard charts this week") && !chartWords.IsMatch("who topped the music charts in 2025"), "charts: \"the charts\" (music) isn't a chart request");
var timeMention = (System.Text.RegularExpressions.Regex)typeof(DynamicNotch.NotchWindow).GetField("TimeMention", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
Check(timeMention.IsMatch("Your meeting is tomorrow at 9") && timeMention.IsMatch("the store opens at 9:30") && timeMention.IsMatch("call him at 6pm"), "reminders: a real time in an answer is offered");
Check(!timeMention.IsMatch("temperatures peak at 23°C on Saturday") && !timeMention.IsMatch("battery at 50%") && !timeMention.IsMatch("it sells at 23 dollars"), "reminders: a number that isn't a time (23°C, 50%) isn't");

// ───────── reading documents ─────────
string dir = Path.Combine(Path.GetTempPath(), "dn-checks");
Directory.CreateDirectory(dir);
try
{
    string xlsx = Path.Combine(dir, "t.xlsx");
    DocWriter.Write(xlsx, "| City | Population |\n|---|---|\n| Casablanca | 3360000 |\n| Rabat | 577000 |");
    string x = DocReader.Read(xlsx) ?? "";
    Check(x.Contains("Casablanca") && x.Contains("3360000") && x.Contains("Population"), "documents: an Excel sheet is read (got: " + x[..Math.Min(80, x.Length)] + ")");

    string pdf = Path.Combine(dir, "t.pdf");
    DocWriter.Write(pdf, "# Invoice 2041\nTotal due: 1,250 MAD\nPaid by transfer.");
    string p = DocReader.Read(pdf) ?? "";
    Check(p.Contains("Invoice 2041") && p.Contains("1,250"), "documents: a PDF's text is read (got: " + p[..Math.Min(80, p.Length)] + ")");

    string png = Path.Combine(dir, "t.png");
    using (var bmp = new System.Drawing.Bitmap(900, 160))
    using (var g = System.Drawing.Graphics.FromImage(bmp))
    {
        g.Clear(System.Drawing.Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.DrawString("Meeting at 15:30 with Karim", new System.Drawing.Font("Segoe UI", 34), System.Drawing.Brushes.Black, 20, 40);
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
    }
    string o = DocReader.Read(png) ?? "";
    Check(o.Contains("Karim") && o.Contains("15:30"), "documents: the text in a picture is read (got: " + o.Replace('\n', ' ')[..Math.Min(90, o.Length)] + ")");
}
finally { try { Directory.Delete(dir, true); } catch { } }

// ───────── 👎 kept on this PC ─────────
string feedback = Path.Combine(Settings.Folder, "ai-feedback.jsonl");
if (File.Exists(feedback))
{
    var lines = File.ReadAllLines(feedback).Where(l => l.Trim().Length > 0).ToList();
    Console.WriteLine($"\n{lines.Count} answer(s) marked bad, to turn into checks:");
    foreach (var line in lines.TakeLast(10))
        try { var n = JsonNode.Parse(line)!; Console.WriteLine($"  {n["at"]}  [{n["kind"]}]  {n["question"]}"); } catch { }
}

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
