using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// What you work on, learned from the window in front (the notch already looks every 5 seconds for app time):
/// documents (Word, Excel, PowerPoint, Google Docs), PDFs (in a reader or the browser), code, and browser tabs. Videos,
/// social and chat sites aren't work and aren't kept. Things you use together in the same stretch of work, or that
/// share a word or a folder, become a project on their own. Projects you touch (rename, pin, drag something in or out)
/// are kept as you made them and still grow with what you use alongside them. Everything stays on this PC
/// (work.json, work-projects.json), two weeks of it.
/// </summary>
public static class WorkLog
{
    public sealed class Item
    {
        public string Key { get; set; } = "";
        public string Kind { get; set; } = "";     // doc, sheet, slides, pdf, code, tab
        public string Name { get; set; } = "";
        public string App { get; set; } = "";
        public string Hint { get; set; } = "";     // code: its folder
        public string? Path { get; set; }
        public string? Url { get; set; }
        public int Seconds { get; set; }
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
    }

    public sealed class Session
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public Dictionary<string, int> Secs { get; set; } = new();
    }

    /// <summary>A project as you shaped it (or as you left it: pinned, renamed, a stage, a note, an answer).</summary>
    public sealed class Meta
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
        public string Name { get; set; } = "";
        public bool Pinned { get; set; }
        public int Stage { get; set; }               // 0 working, 1 review, 2 done
        public string Note { get; set; } = "";
        public HashSet<string> Keys { get; set; } = new();
        public HashSet<string> Excluded { get; set; } = new();
        /// <summary>Hidden (deleted, or a found project you didn't want): it stays out, and what it held doesn't come back as a project.</summary>
        public bool Hidden { get; set; }
        public string LastAsk { get; set; } = "";
        public string LastAnswer { get; set; } = "";
        public DateTime Created { get; set; } = DateTime.Now;
    }

    /// <summary>A project as shown: its items, newest first.</summary>
    public sealed record Project(string Id, string Name, List<Item> Items, DateTime Last, int Seconds, Meta? Kept)
    {
        public bool Pinned => Kept?.Pinned ?? false;
        public int Stage => Kept?.Stage ?? 0;
    }

    sealed class Store
    {
        public List<Item> Items { get; set; } = new();
        public List<Session> Sessions { get; set; } = new();
        public DateTime RecentFrom { get; set; }
    }

    static DateTime _recentFrom;

    /// <summary>Recent starts empty from now on (nothing is deleted: what you use again shows up again).</summary>
    public static void ClearRecent() { lock (Gate) { EnsureLoaded(); _recentFrom = DateTime.Now; } }

    static readonly object Gate = new();
    static Dictionary<string, Item> _items = new();
    static List<Session> _sessions = new();
    static List<Meta> _metas = new();
    static bool _loaded;
    static string? _folder;

    static string Folder => _folder ?? Settings.Folder;
    static string WorkFile => System.IO.Path.Combine(Folder, "work.json");
    static string MetaFile => System.IO.Path.Combine(Folder, "work-projects.json");

    /// <summary>For the checks: keep everything in another folder.</summary>
    public static void UseFolder(string folder) { lock (Gate) { _folder = folder; _loaded = false; _items = new(); _sessions = new(); _metas = new(); _recentFrom = default; } }

    // ───────────────────────────── reading a window's title ─────────────────────────────

    static readonly Regex BrowserSuffix = new(@"\s[-—–]\s(Brave|Google Chrome|Chrome|Microsoft​?\s?Edge|Mozilla Firefox|Firefox|Opera|Vivaldi|Arc)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // videos, social, chat and mail aren't work (mail and chats have their place: the Inbox)
    static readonly Regex NotWork = new(@"\s[-—–|•]\s(YouTube|YouTube Music|Twitch|Netflix|TikTok|Instagram|Facebook|X|Twitter|Discord|WhatsApp|WhatsApp Web|Messenger|Telegram|Telegram Web|Slack|Spotify|Prime Video|Disney\+|Reddit|Kick|Snapchat|Pinterest|Gmail|Outlook|Mail|Yahoo Mail|Proton Mail)$|^(YouTube|Twitch|Netflix|Discord|WhatsApp|WhatsApp Web|Spotify|Instagram|Facebook|Messenger|Telegram|Telegram Web|Gmail|Outlook|Snapchat|Pinterest|TikTok|X)$|^(Inbox|Boîte de réception)\b.*\b(Gmail|Outlook|Mail)\b|@\S+\.\w+ [-—–] (Gmail|Outlook)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Blank = new(@"^(New Tab|Nouvel onglet|Nueva pestaña|Neuer Tab|Untitled|Sans titre|about:blank|Start Page|Speed Dial|Home|Accueil)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex OfficeNoise = new(@"\s*(\[(Compatibility Mode|Mode de compatibilité|Read-Only|Lecture seule)\]|\((Read-Only|Lecture seule)\)|[-•]\s*(Saved|Enregistré|Saving|Enregistrement)[^-]*|-\s*AutoSave[^-]*)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// What a window is about, as a work item: (kind, shown name, key, hint), or null when it isn't work (a video,
    /// a chat, a blank tab, an app that isn't a document).
    /// </summary>
    public static (string Kind, string Name, string Key, string Hint)? Parse(string exe, string title)
    {
        string e = System.IO.Path.GetFileNameWithoutExtension(exe ?? "").ToLowerInvariant();
        string t = Regex.Replace(title ?? "", @"\s+", " ").Trim();
        if (t.Length == 0) return null;

        if (e is "brave" or "chrome" or "msedge" or "firefox" or "opera" or "vivaldi" or "arc")
        {
            t = Regex.Replace(t, @"\s+(and|et) \d+ (more pages?|autres? pages?)", "", RegexOptions.IgnoreCase);
            t = BrowserSuffix.Replace(t, "");
            t = Regex.Replace(t, @"\s[-—–]\s(Personal|Personnel|Work|Travail|Profile \d+|Profil \d+)$", "", RegexOptions.IgnoreCase).Trim();
            t = Regex.Replace(t, @"^\(\d+\+?\)\s*", ""); // "(67) WhatsApp": an unread count is not part of the page
            if (t.Length < 2 || Blank.IsMatch(t) || NotWork.IsMatch(t)) return null;
            if (Regex.IsMatch(t, @"\.pdf$", RegexOptions.IgnoreCase)) return ("pdf", t, "pdf:" + t.ToLowerInvariant(), "");
            var g = Regex.Match(t, @"^(.+?) [-—–] Google (Docs|Sheets|Slides|Docs |Sheets )$", RegexOptions.IgnoreCase);
            if (g.Success)
            {
                string kind = g.Groups[2].Value.StartsWith("Sheets", StringComparison.OrdinalIgnoreCase) ? "sheet" : g.Groups[2].Value.StartsWith("Slides", StringComparison.OrdinalIgnoreCase) ? "slides" : "doc";
                return (kind, g.Groups[1].Value.Trim(), "gdoc:" + g.Groups[1].Value.Trim().ToLowerInvariant(), "");
            }
            return ("tab", t, "tab:" + t.ToLowerInvariant(), "");
        }
        if (e is "winword" or "excel" or "powerpnt")
        {
            string kind = e == "winword" ? "doc" : e == "excel" ? "sheet" : "slides";
            t = Regex.Replace(t, @"\s[-—–]\s(Microsoft\s)?(Word|Excel|PowerPoint)$", "", RegexOptions.IgnoreCase);
            t = OfficeNoise.Replace(t, " ").Trim().Trim('-', '•').Trim();
            if (t.Length == 0 || Regex.IsMatch(t, @"^(Word|Excel|PowerPoint|Document\s?\d*|Book\s?\d*|Presentation\s?\d*|Classeur\s?\d*|Présentation\s?\d*|Libro\s?\d*|Mappe\s?\d*)$", RegexOptions.IgnoreCase)) return null;
            if (!System.IO.Path.HasExtension(t)) t += kind == "doc" ? ".docx" : kind == "sheet" ? ".xlsx" : ".pptx";
            return (kind, t, "file:" + t.ToLowerInvariant(), "");
        }
        if (e is "acrord32" or "acrobat" or "foxitpdfreader" or "foxitphantompdf" or "sumatrapdf" or "pdfxedit" or "pdfgear" or "nitropdf")
        {
            var m = Regex.Match(t, @"[^\\/:*?""<>|]+\.pdf", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return ("pdf", m.Value.Trim(), "pdf:" + m.Value.Trim().ToLowerInvariant(), "");
        }
        if (e is "code" or "cursor" or "windsurf" or "vscodium")
        {
            // "● file.cs - folder - Visual Studio Code"
            var parts = t.TrimStart('●', '•', ' ').Split(" - ").Select(p => p.Trim()).ToList();
            if (parts.Count >= 3) return ("code", parts[0], "code:" + parts[1].ToLowerInvariant() + "/" + parts[0].ToLowerInvariant(), parts[1]);
            if (parts.Count == 2 && !Regex.IsMatch(parts[0], "^(Welcome|Bienvenue|Settings|Paramètres)$")) return ("code", parts[0], "code:/" + parts[0].ToLowerInvariant(), "");
            return null;
        }
        if (e is "notepad" or "notepad++" or "sublime_text")
        {
            var m = Regex.Match(t.TrimStart('*', ' '), @"^(.+?\.\w{1,5})\b");
            if (!m.Success) return null;
            return ("doc", m.Groups[1].Value, "file:" + m.Groups[1].Value.ToLowerInvariant(), "");
        }
        return null;
    }

    /// <summary>A browser tab's page title (any page, a video too), or null when it isn't a browser or the tab is blank.</summary>
    public static string? TabTitle(string exe, string title)
    {
        string e = System.IO.Path.GetFileNameWithoutExtension(exe ?? "").ToLowerInvariant();
        if (e is not ("brave" or "chrome" or "msedge" or "firefox" or "opera" or "vivaldi" or "arc")) return null;
        string t = Regex.Replace(title ?? "", @"\s+", " ").Trim();
        t = Regex.Replace(t, @"\s+(and|et) \d+ (more pages?|autres? pages?)", "", RegexOptions.IgnoreCase);
        t = BrowserSuffix.Replace(t, "");
        t = Regex.Replace(t, @"\s[-—–]\s(Personal|Personnel|Work|Travail|Profile \d+|Profil \d+)$", "", RegexOptions.IgnoreCase).Trim();
        t = Regex.Replace(t, @"^\(\d+\)\s*", ""); // "(3) YouTube": the unread count
        return t.Length < 2 || Blank.IsMatch(t) ? null : t;
    }

    // ───────────────────────────── keeping it ─────────────────────────────

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(WorkFile) && JsonSerializer.Deserialize<Store>(File.ReadAllText(WorkFile)) is { } s)
            {
                _items = s.Items.Where(i => i.Key.Length > 0).GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
                _sessions = s.Sessions;
                _recentFrom = s.RecentFrom;
            }
        }
        catch { }
        try { if (File.Exists(MetaFile)) _metas = JsonSerializer.Deserialize<List<Meta>>(File.ReadAllText(MetaFile)) ?? new(); } catch { }
        // tabs kept by an older version that aren't work after all (a chat with its unread count, mail)
        foreach (var k in _items.Values.Where(i => i.Kind == "tab" && Parse("brave.exe", i.Name + " - Brave") == null).Select(i => i.Key).ToList()) _items.Remove(k);
        Prune();
    }

    static void Prune()
    {
        var cut = DateTime.Now.AddDays(-14);
        _sessions.RemoveAll(s => s.End < cut);
        var kept = _metas.SelectMany(m => m.Keys).ToHashSet();
        foreach (var k in _items.Where(i => i.Value.Last < DateTime.Now.AddDays(-30) && !kept.Contains(i.Key)).Select(i => i.Key).ToList()) _items.Remove(k);
    }

    /// <summary>Five more seconds on whatever window is in front (if it's work).</summary>
    public static Item? Seen(string exe, string app, string title, int seconds, DateTime now)
    {
        if (Parse(exe, title) is not { } p) return null;
        lock (Gate)
        {
            EnsureLoaded();
            if (!_items.TryGetValue(p.Key, out var it)) _items[p.Key] = it = new Item { Key = p.Key, Kind = p.Kind, Name = p.Name, Hint = p.Hint, First = now };
            it.App = app;
            it.Seconds += seconds;
            it.Last = now;
            var s = _sessions.LastOrDefault();
            if (s == null || now - s.End > TimeSpan.FromMinutes(20)) { s = new Session { Start = now }; _sessions.Add(s); }
            s.End = now;
            s.Secs[p.Key] = s.Secs.GetValueOrDefault(p.Key) + seconds;
            return it;
        }
    }

    /// <summary>The work items, most recent first.</summary>
    public static List<Item> Recent(int max = 12)
    {
        lock (Gate) { EnsureLoaded(); return _items.Values.Where(i => i.Last > _recentFrom).OrderByDescending(i => i.Last).Take(max).ToList(); }
    }

    public static Item? ItemOf(string key) { lock (Gate) { EnsureLoaded(); return _items.GetValueOrDefault(key); } }

    /// <summary>Remembers where an item really is (found on disk, or in the browser's history), for next time.</summary>
    public static void Located(string key, string? path, string? url)
    {
        lock (Gate)
        {
            if (!_items.TryGetValue(key, out var it)) return;
            if (path != null) it.Path = path;
            if (url != null) it.Url = url;
        }
    }

    /// <summary>A file dropped on a project: kept as an item of its own.</summary>
    public static Item AddFile(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        string kind = ext switch { ".pdf" => "pdf", ".xlsx" or ".xls" or ".csv" or ".xlsm" => "sheet", ".pptx" or ".ppt" => "slides", ".cs" or ".py" or ".js" or ".ts" or ".java" or ".cpp" or ".html" or ".css" => "code", _ => "doc" };
        string name = System.IO.Path.GetFileName(path);
        string key = (kind == "pdf" ? "pdf:" : "file:") + name.ToLowerInvariant();
        lock (Gate)
        {
            EnsureLoaded();
            if (!_items.TryGetValue(key, out var it)) _items[key] = it = new Item { Key = key, Kind = kind, Name = name, First = DateTime.Now, App = "File" };
            it.Path = path;
            it.Last = DateTime.Now;
            return it;
        }
    }

    public static void Save()
    {
        Store s;
        List<Meta> metas;
        lock (Gate)
        {
            if (!_loaded) return;
            Prune();
            s = new Store { Items = _items.Values.ToList(), Sessions = _sessions.ToList(), RecentFrom = _recentFrom };
            metas = _metas.ToList();
        }
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(WorkFile + ".tmp", JsonSerializer.Serialize(s));
            File.Move(WorkFile + ".tmp", WorkFile, true);
            File.WriteAllText(MetaFile + ".tmp", JsonSerializer.Serialize(metas));
            File.Move(MetaFile + ".tmp", MetaFile, true);
        }
        catch { }
    }

    // ───────────────────────────── projects ─────────────────────────────

    static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "your", "this", "that", "what", "how", "new", "page", "home", "google", "docs", "sheets", "slides",
        "search", "untitled", "document", "copy", "final", "draft", "version", "file", "files", "pdf", "docx", "xlsx", "pptx", "html", "www",
        "com", "les", "des", "une", "pour", "dans", "avec", "sur", "est", "wikipedia", "online", "free", "best", "guide", "2024", "2025", "2026",
        "login", "sign", "account", "dashboard", "inbox", "mail", "gmail", "outlook", "chatgpt", "claude", "gemini", "results", "about",
    };

    /// <summary>The words that say what an item is about ("pricing", "biology").</summary>
    public static IEnumerable<string> Words(Item it) =>
        Regex.Matches((System.IO.Path.GetFileNameWithoutExtension(it.Name) + " " + it.Hint).ToLowerInvariant(), @"\p{L}[\p{L}\d]{3,}")
            .Select(m => m.Value).Where(w => !Common.Contains(w)).Distinct();

    /// <summary>Your projects: the ones you kept, then the ones found in what you use together; pinned first, then the latest.</summary>
    public static List<Project> Projects()
    {
        lock (Gate)
        {
            EnsureLoaded();
            var clusters = Clusters();
            var shown = new List<Project>();
            var used = new HashSet<string>();
            foreach (var m in _metas)
            {
                var keys = new HashSet<string>(m.Keys);
                // it grows with what's used alongside it
                foreach (var c in clusters.Where(c => c.Overlaps(m.Keys))) keys.UnionWith(c);
                keys.ExceptWith(m.Excluded);
                foreach (var c in clusters.Where(c => c.Overlaps(m.Keys)).ToList()) clusters.Remove(c);
                var items = keys.Select(k => _items.GetValueOrDefault(k)).Where(i => i != null).Select(i => i!).OrderByDescending(i => i.Last).ToList();
                used.UnionWith(keys);
                if (m.Hidden) { m.Keys.UnionWith(keys); continue; } // it keeps what it held, so none of it comes back as a project
                shown.Add(new Project(m.Id, m.Name, items, items.Count > 0 ? items.Max(i => i.Last) : m.Created, items.Sum(i => i.Seconds), m));
            }
            foreach (var c in clusters)
            {
                var items = c.Where(k => !used.Contains(k)).Select(k => _items[k]).OrderByDescending(i => i.Last).ToList();
                if (items.Count < 2) continue;
                shown.Add(new Project("auto:" + string.Join("|", c.OrderBy(k => k).Take(3)), NameFor(items), items, items.Max(i => i.Last), items.Sum(i => i.Seconds), null));
            }
            return shown.OrderByDescending(p => p.Pinned).ThenBy(p => p.Stage == 2).ThenByDescending(p => p.Last).ToList();
        }
    }

    /// <summary>Things used together (same stretch of work, a shared folder or word), grouped.</summary>
    static List<HashSet<string>> Clusters()
    {
        var live = _items.Values.Where(i => i.Seconds >= 30 || _metas.Any(m => m.Keys.Contains(i.Key))).Select(i => i.Key).ToHashSet();
        var weight = new Dictionary<(string, string), double>();
        void Add(string a, string b, double w)
        {
            if (a == b || !live.Contains(a) || !live.Contains(b)) return;
            var k = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
            weight[k] = weight.GetValueOrDefault(k) + w;
        }
        foreach (var s in _sessions)
        {
            var keys = s.Secs.Where(x => x.Value >= 15 && live.Contains(x.Key)).OrderByDescending(x => x.Value).Take(25).Select(x => x.Key).ToList();
            for (int i = 0; i < keys.Count; i++)
                for (int j = i + 1; j < keys.Count; j++) Add(keys[i], keys[j], 1);
        }
        var list = live.Select(k => _items[k]).ToList();
        var words = list.ToDictionary(i => i.Key, i => Words(i).ToHashSet());
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                if (list[i].Hint.Length > 0 && list[i].Hint.Equals(list[j].Hint, StringComparison.OrdinalIgnoreCase)) Add(list[i].Key, list[j].Key, 3);
                if (words[list[i].Key].Overlaps(words[list[j].Key])) Add(list[i].Key, list[j].Key, 1);
            }
        // joined where the link is strong enough (used together twice, or together and about the same thing)
        var parent = live.ToDictionary(k => k, k => k);
        string Find(string x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var ((a, b), w) in weight.Where(x => x.Value >= 2)) parent[Find(a)] = Find(b);
        return live.GroupBy(Find).Select(g => g.ToHashSet()).Where(g => g.Count >= 2)
            // a cluster of tabs only, with nothing in common, is just browsing
            .Where(g => g.Any(k => _items[k].Kind != "tab") || g.Select(k => words[k]).Aggregate((x, y) => x.Intersect(y).ToHashSet()).Count > 0 || g.Count >= 3 && g.SelectMany(k => words[k]).GroupBy(w => w).Any(w => w.Count() >= 2))
            .ToList();
    }

    /// <summary>A project's name: its folder (code), else the word its things share most, else its main document.</summary>
    public static string NameFor(List<Item> items)
    {
        var hint = items.Where(i => i.Hint.Length > 0).GroupBy(i => i.Hint).OrderByDescending(g => g.Sum(i => i.Seconds)).FirstOrDefault();
        if (hint != null) return hint.Key;
        var word = items.SelectMany(i => Words(i).Select(w => (w, i.Seconds + 60))).GroupBy(x => x.w)
            .Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Sum(x => x.Item2)).FirstOrDefault();
        if (word != null) return char.ToUpperInvariant(word.Key[0]) + word.Key[1..];
        var main = items.OrderBy(i => i.Kind == "tab").ThenByDescending(i => i.Seconds).First();
        string n = System.IO.Path.GetFileNameWithoutExtension(main.Name);
        return n.Length > 28 ? n[..28] + "…" : n;
    }

    // ───────────────────────────── your edits ─────────────────────────────

    /// <summary>The kept form of a project (made from what's shown when you first touch it).</summary>
    public static Meta Keep(Project p)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (p.Kept != null) return p.Kept;
            var m = new Meta { Name = p.Name, Keys = p.Items.Select(i => i.Key).ToHashSet() };
            _metas.Add(m);
            return m;
        }
    }

    public static Meta NewProject(string name)
    {
        lock (Gate) { EnsureLoaded(); var m = new Meta { Name = name }; _metas.Add(m); return m; }
    }

    /// <summary>Moves an item into a project (out of the one it was in).</summary>
    public static void Move(string key, Meta to)
    {
        lock (Gate)
        {
            foreach (var m in _metas.Where(m => m != to && m.Keys.Contains(key))) { m.Keys.Remove(key); m.Excluded.Add(key); }
            to.Keys.Add(key);
            to.Excluded.Remove(key);
        }
    }

    public static void Remove(string key, Meta from) { lock (Gate) { from.Keys.Remove(key); from.Excluded.Add(key); } }

    /// <summary>Two projects become one (the second goes into the first).</summary>
    public static void Merge(Meta into, Meta other)
    {
        lock (Gate)
        {
            into.Keys.UnionWith(other.Keys);
            into.Excluded.ExceptWith(other.Keys);
            if (into.Note.Length == 0) into.Note = other.Note;
            _metas.Remove(other);
        }
    }

    public static void Forget(Meta m) { lock (Gate) _metas.Remove(m); }

    /// <summary>Hides a project (a found one too): it's gone from the list, and its things don't come back as a project.</summary>
    public static void Hide(Project p) { lock (Gate) { var m = Keep(p); m.Hidden = true; m.Pinned = false; } }

    public static int HiddenCount() { lock (Gate) { EnsureLoaded(); return _metas.Count(m => m.Hidden); } }

    public static void UnhideAll() { lock (Gate) foreach (var m in _metas) m.Hidden = false; }

    /// <summary>Forgets one thing (out of Recent and every project; it comes back only if you use it again).</summary>
    public static void ForgetItem(string key)
    {
        lock (Gate)
        {
            EnsureLoaded();
            _items.Remove(key);
            foreach (var s in _sessions) s.Secs.Remove(key);
            foreach (var m in _metas) m.Keys.Remove(key);
        }
    }
}
