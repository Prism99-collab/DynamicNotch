using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// Every notification the notch saw, kept for two weeks (notifications-history.jsonl, on this PC only): to look back,
/// search, and to know who's still waiting on an answer. A message from a person that asks something (a question, a
/// "can you…") is "waiting on you" until you mark it done (or it's two days old).
/// </summary>
public static class NotifHistory
{
    public sealed record Entry(DateTime At, string App, string Title, string Body)
    {
        public string Id => $"{At:yyyyMMddHHmmssfff}|{App}|{Title}".GetHashCode().ToString("x8");
    }

    static readonly object Gate = new();
    static List<Entry>? _all;
    static HashSet<string> _done = new();
    static Dictionary<string, DateTime> _snoozed = new();
    static string? _folder;

    static string Folder => _folder ?? Settings.Folder;
    static string FilePath => Path.Combine(Folder, "notifications-history.jsonl");
    static string StateFile => Path.Combine(Folder, "notifications-state.json");

    public static void UseFolder(string folder) { lock (Gate) { _folder = folder; _all = null; _done = new(); _snoozed = new(); _muted = new(StringComparer.OrdinalIgnoreCase); _imported = false; } }

    sealed class State
    {
        public HashSet<string> Done { get; set; } = new();
        public Dictionary<string, DateTime> Snoozed { get; set; } = new();
        public HashSet<string> Muted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Imported { get; set; }
    }

    static bool _imported;

    /// <summary>
    /// The first time only: today's notifications the notch already had (from before the history existed) are put in it,
    /// so it doesn't start empty. Never again after (a history you clear stays clear).
    /// </summary>
    public static void ImportOnce(IEnumerable<(DateTime At, string App, string Title, string Body)> earlier)
    {
        lock (Gate)
        {
            var all = All();
            if (_imported) return;
            _imported = true;
            var have = all.Select(e => (e.At, e.Title)).ToHashSet();
            var add = earlier.Where(t => !have.Contains((t.At, t.Title)) && !_muted.Contains(t.App)).Select(t => new Entry(t.At, t.App, t.Title, t.Body)).ToList();
            if (add.Count > 0)
            {
                all.AddRange(add);
                all.Sort((a, b) => a.At.CompareTo(b.At));
                Write(all);
            }
            SaveState();
        }
    }

    static HashSet<string> _muted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apps whose notifications aren't kept (you muted them here).</summary>
    public static IReadOnlyCollection<string> Muted() { lock (Gate) { All(); return _muted.ToList(); } }

    public static void Mute(string app, bool on)
    {
        lock (Gate)
        {
            All();
            if (on) { _muted.Add(app); RemoveWhere(e => e.App.Equals(app, StringComparison.OrdinalIgnoreCase)); }
            else _muted.Remove(app);
            SaveState();
        }
    }

    /// <summary>Some notifications out of the history (one, a person's, a whole app's).</summary>
    public static void RemoveWhere(Func<Entry, bool> which)
    {
        lock (Gate)
        {
            var all = All();
            if (all.RemoveAll(e => which(e)) == 0) return;
            Write(all);
        }
    }

    /// <summary>A person writing (a chat or mail app), not an app telling you something.</summary>
    public static bool FromPerson(Entry e) => ChatApp.IsMatch(e.App) && !NotPerson.IsMatch(e.Title + " " + e.Body);

    static List<Entry> All()
    {
        if (_all != null) return _all;
        _all = new();
        try
        {
            if (File.Exists(FilePath))
            {
                var cut = DateTime.Now.AddDays(-14);
                var lines = File.ReadAllLines(FilePath).Where(l => l.Trim().Length > 0).ToList();
                int unreadable = 0, old = 0;
                foreach (var l in lines)
                {
                    Entry? e = null;
                    try { e = JsonSerializer.Deserialize<Entry>(l); } catch { }
                    if (e == null) unreadable++;
                    else if (e.At < cut) old++;
                    else _all.Add(e);
                }
                // older than two weeks: they go. A line that couldn't be read never leads to rewriting (nothing is lost by accident)
                if (old > 0 && unreadable == 0) Write(_all);
            }
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } s) { _done = s.Done; _snoozed = s.Snoozed; _muted = new(s.Muted ?? new(), StringComparer.OrdinalIgnoreCase); _imported = s.Imported; }
        }
        catch { }
        return _all;
    }

    /// <summary>
    /// The whole history written again (after removing some, or bringing some in): the previous file is kept as a backup
    /// first, and the new one replaces it in one step (never a half-written file).
    /// </summary>
    static void Write(List<Entry> all)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 0) File.Copy(FilePath, FilePath + ".bak", true);
            File.WriteAllLines(FilePath + ".tmp", all.Select(e => JsonSerializer.Serialize(e)));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch { }
    }

    public static void Add(DateTime at, string app, string title, string body)
    {
        if (title.Trim().Length == 0 && body.Trim().Length == 0) return;
        var e = new Entry(at, app.Trim(), title.Trim(), body.Length > 600 ? body[..600] : body.Trim());
        lock (Gate)
        {
            var all = All();
            if (_muted.Contains(e.App)) return; // you muted this app here
            if (all.Count > 0 && all[^1].App == e.App && all[^1].Title == e.Title && all[^1].Body == e.Body && (e.At - all[^1].At).TotalSeconds < 30) return; // the same one twice
            all.Add(e);
            try { Directory.CreateDirectory(Folder); File.AppendAllText(FilePath, JsonSerializer.Serialize(e) + "\n"); } catch { }
        }
    }

    /// <summary>The last two weeks, newest first.</summary>
    public static List<Entry> Recent() { lock (Gate) return All().AsEnumerable().Reverse().ToList(); }

    static readonly Regex ChatApp = new(@"discord|whatsapp|teams|slack|telegram|messenger|signal|skype|outlook|mail|gmail|instagram|viber|wechat|line\b|thunderbird|zoom", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Asks = new(@"\?|\b(can you|could you|would you|please|pls|plz|send me|call me|let me know|are you|did you|when will|need you|reply|answer|tu peux|vous pouvez|peux[- ]tu|stp|svp|est[- ]ce que|tu es|t'es|réponds|envoie[- ]moi|appelle[- ]moi|wach|3afak|ila b9iti|b3at|sift|kteb)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NotPerson = new(@"\b(security|sécurité|verification|vérification|code|password|mot de passe|newsletter|promo|offer|offre|sale|soldes|update available|mise à jour|backup|sync|bot)\b|no-?reply", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Is this a person asking you something (in a chat or mail app)?</summary>
    public static bool AsksYou(Entry e) => ChatApp.IsMatch(e.App) && Asks.IsMatch(e.Title + " " + e.Body) && !NotPerson.IsMatch(e.Title + " " + e.Body);

    /// <summary>People still waiting on you: asked within two days, not marked done, not snoozed; one per person (their latest).</summary>
    public static List<Entry> Waiting()
    {
        lock (Gate)
        {
            var now = DateTime.Now;
            return All().Where(e => now - e.At < TimeSpan.FromDays(2) && AsksYou(e) && !_done.Contains(e.Id) && !(_snoozed.TryGetValue(e.Id, out var until) && until > now))
                .GroupBy(e => e.App + "|" + e.Title).Select(g => g.OrderByDescending(e => e.At).First())
                .OrderByDescending(e => e.At).Take(12).ToList();
        }
    }

    public static void Done(Entry e) { lock (Gate) { _done.Add(e.Id); SaveState(); } }
    public static void Snooze(Entry e, TimeSpan forHow) { lock (Gate) { _snoozed[e.Id] = DateTime.Now + forHow; SaveState(); } }

    public static void Clear()
    {
        lock (Gate)
        {
            _all = new();
            try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true); File.Delete(FilePath); } catch { }
        }
    }

    static void SaveState()
    {
        var cut = DateTime.Now.AddDays(-3);
        foreach (var k in _snoozed.Where(s => s.Value < cut).Select(s => s.Key).ToList()) _snoozed.Remove(k);
        var live = All().Select(e => e.Id).ToHashSet();
        _done.IntersectWith(live);
        try { Directory.CreateDirectory(Folder); File.WriteAllText(StateFile, JsonSerializer.Serialize(new State { Done = _done, Snoozed = _snoozed, Muted = _muted, Imported = _imported })); } catch { }
    }
}
