using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// What the AI learns on this PC, as it works, so it gets it right faster each time:
///  • recipes: for a request that got done, the steps that did it ("call nox on discord" → use_app keys ctrl+k, type
///    "nox", keys enter, click "Start Voice Call"). A similar request later gets them as a head start;
///  • lessons: short rules it saved itself after a correction or after finding what works on this PC;
///  • habits: what this person reaches for (their music app, the apps they open, the people they call).
/// All on this PC (ai-learn.json), small, and wiped with one button in Preferences.
/// </summary>
public static class AiLearn
{
    public sealed class Recipe
    {
        public string Ask { get; set; } = "";
        public string Steps { get; set; } = "";
        public DateTime At { get; set; }
        public int Uses { get; set; } = 1;
        public double Seconds { get; set; }
    }

    sealed class Store
    {
        public List<Recipe> Recipes { get; set; } = new();
        public List<string> Lessons { get; set; } = new();
        public Dictionary<string, Dictionary<string, int>> Habits { get; set; } = new();
    }

    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(Settings.Folder, "ai-learn.json");
    static Store? _store;

    static Store S
    {
        get
        {
            if (_store != null) return _store;
            try { if (File.Exists(FilePath)) _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)); } catch { }
            return _store ??= new Store();
        }
    }

    static void Save()
    {
        string json;
        lock (Gate) json = JsonSerializer.Serialize(S);
        _ = Task.Run(() =>
        {
            try { Directory.CreateDirectory(Settings.Folder); File.WriteAllText(FilePath + ".tmp", json); File.Move(FilePath + ".tmp", FilePath, true); } catch { }
        });
    }

    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    { "the", "a", "an", "to", "on", "in", "of", "and", "my", "me", "for", "it", "is", "please", "can", "you", "i", "le", "la", "les", "de", "du", "des", "et", "sur", "mon", "ma", "mes", "un", "une", "pour", "stp", "bro", "with", "at", "this", "that" };

    static HashSet<string> Words(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}'\-]*").Select(m => m.Value).Where(w => w.Length > 1 && !Stop.Contains(w)).ToHashSet();

    /// <summary>A request got done with these steps: kept (the same kind of request again updates it).</summary>
    public static void Record(string ask, IReadOnlyList<(string Tool, string Args, bool Ok)> steps, double seconds)
    {
        if (steps.Count == 0 || ask.Trim().Length < 3) return;
        string recipe = string.Join(" → ", steps.Select(s => (s.Ok ? "" : "(failed) ") + Brief(s.Tool, s.Args)));
        if (recipe.Length > 400) recipe = recipe[..400] + "…";
        lock (Gate)
        {
            var w = Words(ask);
            var same = S.Recipes.FirstOrDefault(r => Similarity(Words(r.Ask), w) >= 0.75);
            if (same != null)
            {
                same.Steps = recipe; same.At = DateTime.Now; same.Uses++; same.Seconds = seconds;
            }
            else S.Recipes.Add(new Recipe { Ask = ask.Length > 120 ? ask[..120] : ask, Steps = recipe, At = DateTime.Now, Seconds = seconds });
            // the newest 60, and the ones used most
            if (S.Recipes.Count > 60) S.Recipes = S.Recipes.OrderByDescending(r => r.Uses * 3 + (DateTime.Now - r.At).TotalDays * -0.2).Take(60).ToList();
            foreach (var (tool, args, ok) in steps.Where(s => s.Ok)) NoteHabits(tool, args);
        }
        Save();
    }

    /// <summary>A step, the way a person would note it: "use_app discord: keys ctrl+k", "play_music spotify: lofi".</summary>
    static string Brief(string tool, string args)
    {
        try
        {
            var n = System.Text.Json.Nodes.JsonNode.Parse(args);
            string Get(string k) => n?[k]?.ToString() ?? "";
            return tool switch
            {
                "use_app" => $"use_app {Get("app")}: {Get("action")} {(Get("target") + " " + Get("text")).Trim()}".Trim(),
                "play_music" => $"play_music {Get("where")}: {Get("query")}".Trim(),
                "do_on_pc" => $"do_on_pc: {Get("command")}",
                "run_powershell" => $"run_powershell: {Cut(Get("command"), 120)}",
                "open" => $"open: {Get("target")}{Get("path")}",
                "find_files" => $"find_files: {Get("query")}",
                "manage_window" => $"manage_window {Get("app")}: {Get("action")}",
                _ => tool + (args.Length > 2 ? ": " + Cut(Regex.Replace(args, @"[{}""]", ""), 80) : ""),
            };
        }
        catch { return tool; }
    }

    static string Cut(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    static double Similarity(HashSet<string> a, HashSet<string> b) => a.Count == 0 || b.Count == 0 ? 0 : (double)a.Intersect(b).Count() / a.Union(b).Count();

    /// <summary>The recipes for requests most like this one (at most three).</summary>
    public static List<Recipe> Like(string ask)
    {
        var w = Words(ask);
        lock (Gate)
            return S.Recipes.Select(r => (r, s: Similarity(Words(r.Ask), w))).Where(x => x.s >= 0.34).OrderByDescending(x => x.s).ThenByDescending(x => x.r.Uses).Take(3).Select(x => x.r).ToList();
    }

    // ── lessons it saved itself

    public static string Lesson(string text)
    {
        text = Regex.Replace(text.Trim(), @"\s+", " ");
        if (text.Length < 8) return "Error: say the lesson in a sentence.";
        if (text.Length > 240) text = text[..240];
        lock (Gate)
        {
            // a newer lesson on the same thing replaces the old one
            var w = Words(text);
            S.Lessons.RemoveAll(l => Similarity(Words(l), w) >= 0.6);
            S.Lessons.Add(text);
            if (S.Lessons.Count > 40) S.Lessons.RemoveAt(0);
        }
        Save();
        return "Learned: " + text;
    }

    public static IReadOnlyList<string> Lessons() { lock (Gate) return S.Lessons.ToList(); }

    // ── habits

    static void NoteHabits(string tool, string args)
    {
        try
        {
            var n = System.Text.Json.Nodes.JsonNode.Parse(args);
            void Add(string kind, string? value)
            {
                if (value is not { Length: > 1 } v) return;
                v = v.Trim().ToLowerInvariant();
                if (v.Length > 40) return;
                if (!S.Habits.TryGetValue(kind, out var d)) S.Habits[kind] = d = new();
                d[v] = d.GetValueOrDefault(v) + 1;
            }
            if (tool == "play_music") Add("music app", n?["where"]?.ToString() is { Length: > 0 } w ? w : null);
            if (tool == "use_app") Add("apps used", n?["app"]?.ToString());
            if (tool == "use_app" && n?["action"]?.ToString() == "type" && (n?["text"]?.ToString()?.Length ?? 0) <= 24) Add("names typed in apps", n?["text"]?.ToString());
            if (tool == "do_on_pc" && n?["command"]?.ToString() is { } c && Regex.Match(c, @"^(?:open|lance|ouvre)\s+(.+)$", RegexOptions.IgnoreCase) is { Success: true } m) Add("apps opened", m.Groups[1].Value);
        }
        catch { }
    }

    /// <summary>One line per habit: the top few of each.</summary>
    public static List<string> Habits()
    {
        lock (Gate)
            return S.Habits.Where(h => h.Value.Count > 0)
                .Select(h => $"{h.Key}: {string.Join(", ", h.Value.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Value > 1 ? $"{kv.Key} ({kv.Value}×)" : kv.Key))}").ToList();
    }

    public static int Count { get { lock (Gate) return S.Recipes.Count + S.Lessons.Count; } }

    public static void Clear()
    {
        lock (Gate) _store = new Store();
        try { File.Delete(FilePath); } catch { }
    }
}
