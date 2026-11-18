using System.IO;
using System.Text.Json;

namespace DynamicNotch.Services;

/// <summary>
/// How much of the free Google key is used today, counted where the requests are made: every request that reached a
/// model counts for that model, on Google's own day (it starts at midnight in California). When Google says a model
/// is used up for the day, that's written down too, so the count can't drift from Google's. Kept in ai-usage.json; a
/// new key starts from zero (it has its own allowance). Requests made with the same key outside this PC can't be seen.
/// </summary>
public static class AiUsage
{
    sealed class Day
    {
        public string Date { get; set; } = "";
        public string Key { get; set; } = "";
        public Dictionary<string, int> Counts { get; set; } = new();
        public Dictionary<string, long> Tokens { get; set; } = new();
        public List<string> Full { get; set; } = new();
    }

    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(Settings.Folder, "ai-usage.json");
    static readonly Dictionary<string, List<DateTime>> Minute = new();
    static Day? _day;
    static int _saveVersion;

    /// <summary>Google's date now (Pacific time).</summary>
    static string GoogleDate()
    {
        try
        {
            var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            return TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.Utc, pacific).ToString("yyyy-MM-dd");
        }
        catch { return DateTime.UtcNow.AddHours(-8).ToString("yyyy-MM-dd"); }
    }

    static string KeyTag(string? key) => key is { Length: > 8 } k ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(k)))[..12] : "";

    /// <summary>Today's record for this key (a new day or a new key: a fresh one).</summary>
    static Day Today(string? key)
    {
        string date = GoogleDate(), tag = KeyTag(key);
        if (_day == null)
            try { if (File.Exists(FilePath)) _day = JsonSerializer.Deserialize<Day>(File.ReadAllText(FilePath)); } catch { }
        if (_day == null || _day.Date != date || tag.Length > 0 && _day.Key != tag) _day = new Day { Date = date, Key = tag };
        return _day;
    }

    /// <summary>A request reached this model.</summary>
    public static void Count(string model, string? key, int tokens = 0)
    {
        lock (Gate)
        {
            var d = Today(key);
            d.Counts[model] = d.Counts.GetValueOrDefault(model) + 1;
            if (tokens > 0) d.Tokens[model] = d.Tokens.GetValueOrDefault(model) + tokens;
            if (!Minute.TryGetValue(model, out var times)) Minute[model] = times = new();
            times.Add(DateTime.UtcNow);
            times.RemoveAll(t => (DateTime.UtcNow - t).TotalSeconds > 60);
        }
        Save();
    }

    /// <summary>Google said this model is used up for today.</summary>
    public static void MarkFull(string model, string? key)
    {
        lock (Gate)
        {
            var d = Today(key);
            if (!d.Full.Contains(model)) d.Full.Add(model);
        }
        Save();
    }

    public static (int Used, bool Full) Of(string model, string? key)
    {
        lock (Gate)
        {
            var d = Today(key);
            return (d.Counts.GetValueOrDefault(model), d.Full.Contains(model));
        }
    }

    public static long TokensOf(string model, string? key)
    {
        lock (Gate) return Today(key).Tokens.GetValueOrDefault(model);
    }

    // ── what's known about each key and model, kept across days: Google's stated allowances (each key has its own),
    //    the key's plan (free or paid), and how fast each model starts answering
    sealed class Known
    {
        public Dictionary<string, int[]> Limits { get; set; } = new();   // "key|model" → [per day, per minute]
        public Dictionary<string, string> Plans { get; set; } = new();   // key → "free" / "paid"
        public Dictionary<string, double> Speed { get; set; } = new();   // model → ms to the first word (averaged)
        public List<string> Retired { get; set; } = new();
        public List<string> Refused { get; set; } = new();               // key tags a provider said aren't valid
        public Dictionary<string, GroqState> Groq { get; set; } = new(); // "key|model": what Groq said last               // "key|model": Google retired it, or doesn't offer it to this key
    }
    static readonly string KnownPath = Path.Combine(Settings.Folder, "ai-limits.json");
    static Known? _known;

    static Known K
    {
        get
        {
            if (_known != null) return _known;
            try { if (File.Exists(KnownPath)) _known = JsonSerializer.Deserialize<Known>(File.ReadAllText(KnownPath)); } catch { }
            return _known ??= new Known();
        }
    }

    static void SaveKnown()
    {
        string json;
        lock (Gate) json = JsonSerializer.Serialize(K);
        try { Directory.CreateDirectory(Settings.Folder); File.WriteAllText(KnownPath, json); } catch { }
    }

    /// <summary>Google said this model allows this many requests a day (or a minute) for this key.</summary>
    public static void LearnLimit(string model, bool perDay, int value, string? key)
    {
        lock (Gate)
        {
            string id = KeyTag(key) + "|" + model;
            if (!K.Limits.TryGetValue(id, out var v)) K.Limits[id] = v = new int[2];
            if (v[perDay ? 0 : 1] == value) return;
            v[perDay ? 0 : 1] = value;
        }
        SaveKnown();
    }

    public static (int? Day, int? Minute) LimitOf(string model, string? key)
    {
        lock (Gate)
        {
            if (!K.Limits.TryGetValue(KeyTag(key) + "|" + model, out var v)) return (null, null);
            return (v[0] > 0 ? v[0] : null, v[1] > 0 ? v[1] : null);
        }
    }

    /// <summary>What Groq says with every answer: requests left today, tokens left this minute, and when each refills.</summary>
    public sealed record GroqState(int DayLimit, int DayLeft, DateTime DayBack, int MinuteLimit, int MinuteLeft, DateTime MinuteBack, DateTime At);

    /// <summary>"1m26.4s", "2.325s", "7.66ms", "1h2m3s" → how long.</summary>
    static TimeSpan Span(string s)
    {
        double total = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s, @"(\d+(?:\.\d+)?)(ms|h|m|s)"))
        {
            double v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            total += m.Groups[2].Value switch { "h" => v * 3600, "m" => v * 60, "ms" => v / 1000, _ => v };
        }
        return TimeSpan.FromSeconds(total);
    }

    public static void GroqSays(string? key, string model, IReadOnlyDictionary<string, string> h)
    {
        int I(string name) => h.TryGetValue(name, out var v) && int.TryParse(v, out int n) ? n : -1;
        string S(string name) => h.TryGetValue(name, out var v) ? v : "";
        int dayLimit = I("x-ratelimit-limit-requests"), dayLeft = I("x-ratelimit-remaining-requests");
        int minLimit = I("x-ratelimit-limit-tokens"), minLeft = I("x-ratelimit-remaining-tokens");
        if (dayLimit < 0 && minLimit < 0) return;
        var now = DateTime.UtcNow;
        var state = new GroqState(dayLimit, dayLeft, now + Span(S("x-ratelimit-reset-requests")), minLimit, minLeft, now + Span(S("x-ratelimit-reset-tokens")), now);
        lock (Gate) K.Groq[KeyTag(key) + "|" + model] = state;
        _ = Task.Run(SaveKnown);
    }

    /// <summary>What Groq said last about this model for this key (null: never asked yet).</summary>
    public static GroqState? GroqNow(string? key, string model) { lock (Gate) return K.Groq.GetValueOrDefault(KeyTag(key) + "|" + model); }

    /// <summary>A provider said this key isn't valid: it isn't asked with it again (a new key starts fresh).</summary>
    public static void RefuseKey(string? key)
    {
        lock (Gate)
        {
            string tag = KeyTag(key);
            if (tag.Length == 0 || K.Refused.Contains(tag)) return;
            K.Refused.Add(tag);
        }
        SaveKnown();
    }

    public static bool KeyRefused(string? key) { lock (Gate) return K.Refused.Contains(KeyTag(key)); }

    /// <summary>Google retired this model (or doesn't offer it to this key): it's never asked again.</summary>
    public static void Retire(string model, string? key)
    {
        lock (Gate)
        {
            string id = KeyTag(key) + "|" + model;
            if (K.Retired.Contains(id)) return;
            K.Retired.Add(id);
            K.Speed.Remove(model);
        }
        SaveKnown();
    }

    public static bool Retired(string model, string? key) { lock (Gate) return K.Retired.Contains(KeyTag(key) + "|" + model); }

    /// <summary>"free", "paid", or null while it isn't known yet.</summary>
    public static string? PlanOf(string? key) { lock (Gate) return K.Plans.GetValueOrDefault(KeyTag(key)); }

    public static void SetPlan(string? key, string plan)
    {
        lock (Gate)
        {
            string tag = KeyTag(key);
            if (tag.Length == 0 || K.Plans.GetValueOrDefault(tag) == plan) return;
            K.Plans[tag] = plan;
        }
        SaveKnown();
    }

    /// <summary>How long this model took to start answering (it's averaged, recent answers counting more).</summary>
    public static void Took(string model, double ms)
    {
        if (ms <= 0) return;
        lock (Gate) K.Speed[model] = K.Speed.TryGetValue(model, out var was) ? was * 0.65 + ms * 0.35 : ms;
        _ = Task.Run(SaveKnown);
    }

    public static double? SpeedOf(string model) { lock (Gate) return K.Speed.TryGetValue(model, out var v) ? v : null; }

    public static int LastMinute(string model)
    {
        lock (Gate)
        {
            if (!Minute.TryGetValue(model, out var times)) return 0;
            times.RemoveAll(t => (DateTime.UtcNow - t).TotalSeconds > 60);
            return times.Count;
        }
    }

    static async void Save()
    {
        int v = Interlocked.Increment(ref _saveVersion);
        await Task.Delay(600);
        if (v != _saveVersion) return;
        string json;
        lock (Gate) json = JsonSerializer.Serialize(_day);
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            await File.WriteAllTextAsync(FilePath + ".tmp", json);
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch { }
    }
}
