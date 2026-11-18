using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// Google's Gemini API as the AI's provider: a free key from Google AI Studio gives thousands of requests a day,
/// spread over several models (Gemini Flash-Lite, Gemma 4, Gemini Flash). "Auto" uses the best one that still has
/// requests left today and moves to the next when one runs out (Google resets them at midnight, Pacific time).
/// It speaks the same language as the rest of the chat (chat completions, streamed, with tools, pictures and audio),
/// through Google's OpenAI-compatible endpoint.
/// </summary>
public static partial class OpenRouter
{
    const string GeminiEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
    const string GeminiModelsUrl = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=200&key=";

    /// <summary>"gemini" or "openrouter": Gemini when its key is there (unless the user chose OpenRouter).</summary>
    public static string Provider = "gemini";
    public static bool IsGemini => Provider == "gemini";
    public static string ProviderName => IsGemini ? "Google Gemini" : "OpenRouter";

    static string GeminiKeyFile => Path.Combine(Settings.Folder, "gemini.key");
    static string OpenRouterKeyFile => Path.Combine(Settings.Folder, "openrouter.key");

    public static bool HasGeminiKey => File.Exists(GeminiKeyFile);
    public static bool HasOpenRouterKey => File.Exists(OpenRouterKeyFile);

    /// <summary>The provider in use: the user's choice, or Gemini when its key is set, else OpenRouter.</summary>
    public static void Choose(string preference) =>
        Provider = preference == "openrouter" && HasOpenRouterKey ? "openrouter"
            : preference == "gemini" || HasGeminiKey ? "gemini"
            : HasOpenRouterKey ? "openrouter" : "gemini";

    /// <summary>Which kind of key this is: "gemini" (AIza… or the newer AQ.…), "openrouter" (sk-or-…), or null.</summary>
    public static string? KindOf(string key) =>
        Regex.IsMatch(key, @"^(?:AIza[0-9A-Za-z_\-]{30,}|AQ\.[0-9A-Za-z_\-]{30,})$") ? "gemini" : Regex.IsMatch(key, @"^sk-or-[A-Za-z0-9_\-]{20,}$") ? "openrouter" : null;

    // ───────────────────────────── the models, and which still have requests today ─────────────────────────────

    public sealed record GeminiModel(string Id, string Name, bool Tools, bool Audio, int Rank);

    static List<GeminiModel>? _gemini;
    static DateTime _geminiAt;
    static readonly Dictionary<string, DateTime> _exhausted = new();   // model → when its requests come back
    static readonly object _gate = new();

    /// <summary>
    /// Google's models for this key, every version kept (each version has its own free requests a day, so using them
    /// all goes much further than one): the fast tier (Flash-Lite: 3.5, 3.1, 2.5...), the smart tier (Flash: 3.8, 3.7,
    /// 3.6, 3.5, 2.5...), then Gemma 4 (many requests, slower) and Pro. Newest first in each tier.
    /// </summary>
    public static async Task<List<GeminiModel>> GeminiModels()
    {
        if (_gemini != null && DateTime.UtcNow - _geminiAt < TimeSpan.FromHours(6)) return _gemini;
        var list = new List<GeminiModel>();
        try
        {
            if (LoadGeminiKey() is { } key)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                string json = await Http.GetStringAsync(GeminiModelsUrl + key, cts.Token);
                foreach (var m in JsonNode.Parse(json)?["models"]?.AsArray() ?? new JsonArray())
                {
                    string id = (m?["name"]?.ToString() ?? "").Replace("models/", "");
                    var methods = (m?["supportedGenerationMethods"] as JsonArray)?.Select(x => x?.ToString()).ToList() ?? new();
                    if (!methods.Contains("generateContent") || Regex.IsMatch(id, @"embedding|aqa|imagen|veo|tts|live|image|banana|native-audio|robotics|computer-use|learnlm|transcribe|omni|lyria|antigravity|deep-research|customtools|thinking|exp-\d|-001$")) continue;
                    var (rank, name) = RankOf(id);
                    if (rank <= 0) continue;
                    list.Add(new GeminiModel(id, name, Tools: true, Audio: id.StartsWith("gemini"), rank));
                }
            }
        }
        catch { }
        if (list.Count == 0)
            list = new() // known names, in case the list can't be read
            {
                new("gemini-flash-lite-latest", "Gemini Flash-Lite", true, true, 3000),
                new("gemini-flash-latest", "Gemini Flash", true, true, 2000),
                new("gemma-4-31b-it", "Gemma 4 31B", true, false, 1031),
                new("gemma-4-26b-a4b-it", "Gemma 4 26B", true, false, 1026),
            };
        // "-latest" names point at a numbered version (same requests): left out when that family has numbered ones
        var families = list.Where(m => !m.Id.Contains("latest")).Select(m => Tier(m.Id)).ToHashSet();
        list = list.Where(m => !m.Id.Contains("latest") || !families.Contains(Tier(m.Id)))
                   .OrderByDescending(m => m.Rank).ThenBy(m => m.Id.Contains("preview")).ToList();
        _gemini = list;
        _geminiAt = DateTime.UtcNow;
        return list;
    }

    /// <summary>"lite", "flash", "gemma", "pro" or "".</summary>
    static string Tier(string id) => id.Contains("flash-lite") ? "lite" : id.Contains("flash") ? "flash" : id.StartsWith("gemma") ? "gemma" : id.Contains("-pro") ? "pro" : "";

    /// <summary>A model's place (tier, then the newer the higher) and its readable name ("Gemini 3.5 Flash-Lite").</summary>
    static (int Rank, string Name) RankOf(string id)
    {
        double v = Version(id);
        string ver = v > 0 && v < 100 ? " " + v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "";
        string pre = id.Contains("preview") ? " preview" : "";
        return Tier(id) switch
        {
            "lite" => (3000 + (int)(v * 10), $"Gemini{ver} Flash-Lite{pre}"),
            "flash" => (2000 + (int)(v * 10), $"Gemini{ver} Flash{pre}"),
            "gemma" when Regex.Match(id, @"gemma-(\d+)-(\d+)b") is { Success: true } g => (1000 + int.Parse(g.Groups[1].Value) * 10 + Math.Min(9, int.Parse(g.Groups[2].Value) / 4), $"Gemma {g.Groups[1].Value} {g.Groups[2].Value.ToUpperInvariant()}B"),
            "pro" => (500 + (int)(v * 10), $"Gemini{ver} Pro{pre}"),
            _ => (0, id),
        };
    }

    static double Version(string id) => double.TryParse(Regex.Match(id, @"-(\d+(?:\.\d+)?)").Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : id.Contains("latest") ? 100 : 0;

    /// <summary>
    /// Does this question need the smart tier? Long or demanding work (writing, code, analysis, planning, comparing,
    /// explaining, maths, a document to work on) does; quick questions, commands and lookups go to the fast tier,
    /// which answers in a second or two and has many more free requests.
    /// </summary>
    public static bool NeedsSmart(JsonArray messages)
    {
        var last = messages.LastOrDefault(m => m?["role"]?.ToString() == "user");
        string text = last?["content"] is JsonArray parts ? string.Join(" ", parts.Select(p => p?["text"]?.ToString() ?? "")) : last?["content"]?.ToString() ?? "";
        if (text.Contains("[Attached file:") || text.Length > 350) return true;
        return Regex.IsMatch(text, @"\b(write|draft|rewrite|essay|letter|report|code|script|program|debug|fix (this|my) code|function|analy[sz]e|analysis|compare|comparison|pros and cons|plan|strategy|design|architect|optimi[sz]e|explain (why|how|in detail)|step by step|calculate|solve|prove|equation|math|translate (this|the following)|summari[sz]e (this|the) (document|file|pdf|article|text)|r[ée]dige|[ée]cris|explique|analyse|compare|plan)\b", RegexOptions.IgnoreCase);
    }

    /// <summary>When Google's daily requests come back: midnight in California, in local time.</summary>
    public static DateTime GeminiReset
    {
        get
        {
            try
            {
                var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
                var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.Utc, pacific);
                return TimeZoneInfo.ConvertTime(now.Date.AddDays(1), pacific, TimeZoneInfo.Local);
            }
            catch { return DateTime.Today.AddDays(1); }
        }
    }

    static bool Spent(string id) { lock (_gate) return _exhausted.TryGetValue(id, out var until) && DateTime.Now < until; }
    static void Spend(string id, bool forTheDay) { lock (_gate) _exhausted[id] = forTheDay ? GeminiReset : DateTime.Now.AddSeconds(65); }

    /// <summary>
    /// The models to try for a request, best first, skipping those that ran out: the one you picked (or the one this
    /// answer started with), then the tier the question needs (fast or smart), then the other tier, then Gemma and Pro.
    /// </summary>
    static async Task<List<string>> GeminiChain(string chosen, bool audio, bool smart = false)
    {
        var all = await GeminiModels();
        var order = new List<string>();
        if (chosen != DefaultModel && !chosen.Contains('/')) order.Add(chosen);
        var usable = all.Where(m => !audio || m.Audio).ToList();
        string[] tiers = smart ? new[] { "flash", "lite", "gemma", "pro" } : new[] { "lite", "flash", "gemma", "pro" };
        foreach (var tier in tiers) order.AddRange(usable.Where(m => Tier(m.Id) == tier).Select(m => m.Id));
        order.AddRange(usable.Select(m => m.Id));
        var chain = order.Distinct().Where(id => !Spent(id)).ToList();
        return chain.Count > 0 ? chain : order.Distinct().Take(1).ToList(); // all out: the first, so the error says so
    }

    public static string GeminiName(string id) =>
        _gemini?.FirstOrDefault(m => m.Id == id)?.Name ?? RankOf(id).Name;

    /// <summary>A summary for the model's name in the chat: which models still answer today.</summary>
    public static string GeminiToday()
    {
        var all = _gemini ?? new();
        int left = all.Count(m => !Spent(m.Id));
        return left == all.Count ? "All Google models have requests left today" : left == 0 ? $"Every Google model is used up until {GeminiReset:HH:mm}" : $"{left} of {all.Count} Google models still answering today";
    }

    // ───────────────────────────── a turn through Gemini ─────────────────────────────

    static async Task<Turn> StreamGemini(string key, string model, JsonArray messages, JsonArray? tools,
        Action<string> onText, Action? onThinking, CancellationToken cancel, double? temperature, string? effort)
    {
        var chain = await GeminiChain(model, audio: false, smart: NeedsSmart(messages));
        Turn? last = null;
        foreach (var id in chain)
        {
            // Gemma models don't take Gemini's thinking setting, and want a tool's empty parameters left out
            var body = new JsonObject { ["model"] = id, ["messages"] = Adapted(messages, id), ["stream"] = true };
            if (temperature is { } t) body["temperature"] = t;
            if (effort != null && id.StartsWith("gemini") && !id.Contains("flash-lite")) body["reasoning_effort"] = effort;
            if (tools != null && tools.Count > 0) body["tools"] = ToolsFor(tools);
            last = await Send(GeminiEndpoint, key, body, onText, onThinking, cancel, gemini: true);
            if (last.Error == null || cancel.IsCancellationRequested || last.Text.Length > 0) return last with { Model = id };
            // this one is out (for the day, or the minute) or can't do it: the next one answers
            if (last.Error.StartsWith("quota-day")) { Spend(id, forTheDay: true); continue; }
            if (last.Error.StartsWith("quota-minute")) { Spend(id, forTheDay: false); continue; }
            if (last.Error.StartsWith("unsupported")) continue;
            return last;
        }
        string reason = last?.Error ?? "";
        if (reason.StartsWith("quota"))
            return new Turn("", new(), $"Today's free Google requests are used up on every model. They come back at {GeminiReset:HH:mm}. Or switch to OpenRouter in Preferences → AI chat.", null);
        return new Turn("", new(), reason.StartsWith("unsupported") ? "None of the Google models could take this request: " + reason[11..] : reason, null);
    }

    /// <summary>The conversation as a Gemma / Gemini model takes it (Gemma has no system role: it goes first, as the user).</summary>
    static JsonArray Adapted(JsonArray messages, string id)
    {
        var copy = messages.DeepClone().AsArray();
        if (id.StartsWith("gemma") && copy.Count > 0 && copy[0]?["role"]?.ToString() == "system")
            copy[0]!["role"] = "user";
        return copy;
    }

    static JsonArray ToolsFor(JsonArray tools)
    {
        var copy = tools.DeepClone().AsArray();
        foreach (var t in copy)
            if (t?["function"]?["parameters"]?["properties"] is JsonObject { Count: 0 }) t["function"]!.AsObject().Remove("parameters");
        return copy;
    }

    /// <summary>Google's errors in plain words, or a marker the chain acts on (quota-day, quota-minute, unsupported).</summary>
    static string ExplainGemini(int code, string body)
    {
        string message = body;
        try
        {
            var node = JsonNode.Parse(body);
            var e = node is JsonArray arr ? arr.FirstOrDefault()?["error"] : node?["error"];
            message = e?["message"]?.ToString() ?? body;
        }
        catch { }
        if (code == 429 || message.Contains("RESOURCE_EXHAUSTED") || message.Contains("quota", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(message, @"per ?day|PerDay|daily", RegexOptions.IgnoreCase) || !Regex.IsMatch(message, @"per ?minute|PerMinute", RegexOptions.IgnoreCase) ? "quota-day " + message : "quota-minute " + message;
        if (code is 400 or 404 && Regex.IsMatch(message, @"not supported|isn't supported|is not found|not found for API|does not support|Function calling is not enabled|Developer instruction|reasoning", RegexOptions.IgnoreCase))
            return "unsupported " + message;
        if (code is 400 or 401 or 403 && Regex.IsMatch(message, @"API key not valid|valid API key|API_KEY_INVALID|PERMISSION_DENIED|API key expired|API key was reported as leaked", RegexOptions.IgnoreCase))
            return "Google didn't accept the key. Type /key to enter it again.";
        if (code == 503 || message.Contains("overloaded", StringComparison.OrdinalIgnoreCase)) return "quota-minute The model is overloaded";
        return message.Length > 300 ? message[..300] : message;
    }

    public static async Task<bool?> CheckGeminiKey(string key)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var res = await Http.GetAsync(GeminiModelsUrl + key, cts.Token);
            if (res.IsSuccessStatusCode) return true;
            return (int)res.StatusCode is 400 or 401 or 403 ? false : null;
        }
        catch { return null; }
    }

    static string? LoadGeminiKey() => ReadKey(GeminiKeyFile);
}
