using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// Groq, the AI's second engine: a free key (gsk_…) from console.groq.com, very fast (an answer in well under a second),
/// about 1,000 requests a day per model, but only 8,000 tokens a minute and no pictures. So it gets a slimmer version
/// of each request (short instructions, the recent messages, the tools this question needs), and it's only asked when
/// that fits what it has left this minute (it says, with every answer). Google takes everything else. See StreamSmart
/// (Gemini.cs) for how the two share the work.
/// </summary>
public static partial class OpenRouter
{
    const string GroqEndpoint = "https://api.groq.com/openai/v1/chat/completions";
    const string GroqModelsUrl = "https://api.groq.com/openai/v1/models";
    const string GroqAudioUrl = "https://api.groq.com/openai/v1/audio/transcriptions";

    static string GroqKeyFile => Path.Combine(Settings.Folder, "groq.key");
    public static bool HasGroqKey => File.Exists(GroqKeyFile);
    public static string? LoadGroqKey() => ReadKey(GroqKeyFile);

    /// <summary>The Groq key when there is one and Groq hasn't turned it down.</summary>
    static string? GroqKeyIfUsable() => HasGroqKey && LoadGroqKey() is { } k && !AiUsage.KeyRefused(k) ? k : null;

    /// <summary>The short version of the AI's instructions, for Groq (set by the chat window).</summary>
    public static Func<string>? CompactSystem;

    public sealed record GroqModel(string Id, string Name, int Rank);
    static List<GroqModel>? _groq;
    static DateTime _groqAt;

    /// <summary>Groq's chat models for this key (read from Groq, so retired or new ones are followed), best first.</summary>
    public static async Task<List<GroqModel>> GroqModels()
    {
        if (_groq != null && DateTime.UtcNow - _groqAt < TimeSpan.FromHours(6)) return _groq;
        var list = new List<GroqModel>();
        try
        {
            if (LoadGroqKey() is { } key)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, GroqModelsUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var res = await Http.SendAsync(req, cts.Token);
                if ((int)res.StatusCode == 401) AiUsage.RefuseKey(key);
                if (res.IsSuccessStatusCode)
                    foreach (var m in JsonNode.Parse(await res.Content.ReadAsStringAsync(cts.Token))?["data"]?.AsArray() ?? new JsonArray())
                    {
                        string id = m?["id"]?.ToString() ?? "";
                        int ctx = m?["context_window"] is JsonValue cv && cv.TryGetValue(out int c) ? c : 0;
                        bool active = m?["active"] is not JsonValue av || !av.TryGetValue(out bool a) || a;
                        // chat models only: not speech, voice, safety filters or tiny-context ones
                        if (!active || ctx < 16000 || Regex.IsMatch(id, @"whisper|orpheus|tts|guard|safeguard|distil|playai|allam", RegexOptions.IgnoreCase)) continue;
                        list.Add(new GroqModel(id, GroqPretty(id), GroqRank(id)));
                    }
            }
        }
        catch { }
        if (list.Count == 0) list = new() { new("openai/gpt-oss-120b", "GPT-OSS 120B", 300), new("openai/gpt-oss-20b", "GPT-OSS 20B", 240) };
        _groq = list.OrderByDescending(m => m.Rank).ToList();
        _groqAt = DateTime.UtcNow;
        return _groq;
    }

    /// <summary>The strongest first (it's fast either way): GPT-OSS 120B, then Qwen, Kimi, Llama, then the small ones.</summary>
    static int GroqRank(string id)
    {
        string s = id.ToLowerInvariant();
        int size = Regex.Match(s, @"(\d+)b\b") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 30;
        int family = s.Contains("gpt-oss") ? 200 : s.Contains("kimi") ? 190 : s.Contains("qwen") ? 180 : s.Contains("llama") ? 150 : s.Contains("deepseek") ? 140 : 100;
        return family + Math.Min(99, size / 2);
    }

    static string GroqPretty(string id)
    {
        string s = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        s = Regex.Replace(s, @"^gpt-oss", "GPT-OSS", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^qwen(\d)", "Qwen $1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(\d+)b\b", m => m.Groups[1].Value + "B");
        s = s.Replace('-', ' ').Replace("GPT OSS", "GPT-OSS").Trim();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    public static string GroqName(string id) => id.StartsWith("groq:") ? "Groq · " + (_groq?.FirstOrDefault(m => m.Id == id[5..])?.Name ?? GroqPretty(id[5..])) : id;

    static async Task<List<string>> GroqChain(string? chosen)
    {
        string? key = LoadGroqKey();
        var models = await GroqModels();
        var order = new List<string>();
        if (chosen is { Length: > 0 }) order.Add(chosen);
        order.AddRange(models.Select(m => m.Id));
        return order.Distinct().Where(id => !Spent("groq:" + id) && !AiUsage.Retired("groq:" + id, key)).ToList();
    }

    // ───────────────────────────── a request, made to fit ─────────────────────────────

    static readonly string[] GroqCoreTools = { "do_on_pc", "play_music", "use_app", "notch_data", "open", "find_files", "research", "run_powershell", "manage_window", "learn" };

    /// <summary>
    /// The conversation as Groq gets it: the short instructions, the recent messages (from a question on, so no step
    /// loses its question), long step results cut, pictures as a word, and only the tools this question needs.
    /// </summary>
    static (JsonArray Messages, JsonArray? Tools) ForGroq(JsonArray messages, JsonArray? tools, int keep)
    {
        var rest = messages.Where(m => m?["role"]?.ToString() != "system").ToList();
        int start = Math.Max(0, rest.Count - keep);
        while (start < rest.Count && rest[start]?["role"]?.ToString() != "user") start++;
        if (start >= rest.Count) start = Math.Max(0, rest.FindLastIndex(m => m?["role"]?.ToString() == "user"));
        string system = CompactSystem?.Invoke() ?? (messages.FirstOrDefault()?["content"]?.ToString() is { } full ? (full.Length > 5000 ? full[..5000] : full) : "");
        var list = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        var used = new HashSet<string>();
        string ask = "";
        foreach (var m in rest.Skip(start))
        {
            if (m is not JsonObject o) continue;
            var copy = o.DeepClone().AsObject();
            if (copy["content"] is JsonArray parts)
                copy["content"] = string.Join(" ", parts.Where(p => p?["type"]?.ToString() == "text").Select(p => p?["text"]?.ToString())) + " [a picture was attached]";
            string role = copy["role"]?.ToString() ?? "";
            if (role == "user") ask = copy["content"]?.ToString() ?? ask;
            if (role == "tool" && copy["content"]?.ToString() is { Length: > 1500 } big) copy["content"] = big[..1500] + "\n…(cut)";
            if (copy["tool_calls"] is JsonArray calls)
                foreach (var c in calls)
                    if (c is JsonObject co)
                    {
                        co.Remove("extra_content"); // Google's signature: not Groq's business
                        if (co["function"]?["name"]?.ToString() is { } n) used.Add(n);
                    }
            list.Add(copy);
        }
        JsonArray? slim = null;
        if (tools != null)
        {
            var want = new HashSet<string>(GroqCoreTools);
            want.UnionWith(used);
            string a = ask.ToLowerInvariant();
            void If(string pattern, params string[] names) { if (Regex.IsMatch(a, pattern)) want.UnionWith(names); }
            If(@"file|folder|document|word|excel|pdf|docx|xlsx|csv|save|write|create a|make a|txt", "write_file", "read_file", "list_folder");
            If(@"picture|image|photo|draw|wallpaper|logo|dessin", "create_image");
            If(@"news|headline|actu", "news");
            If(@"video|youtube|stream|watch|clip|live|vidéo|regarde", "youtube");
            If(@"remember|forget|souviens|oublie", "remember", "forget");
            If(@"notification|missed|miss|alert", "recent_notifications");
            If(@"download|visited|history|historique|website i", "browser_history");
            If(@"http|www\.|\.com|link|page|article|site", "read_webpage", "web_search");
            If(@"undo|take back|annule", "undo_last");
            If(@"tell me when|notify|let me know", "notify");
            If(@"clipboard|copied|copie", "clipboard");
            If(@"battery|cpu|ram|memory|disk|pc status|slow", "pc_status");
            If(@"show me the|open the .* page|notch page", "notch_page");
            slim = new JsonArray();
            foreach (var t in tools)
                if (t?["function"]?["name"]?.ToString() is { } n && want.Contains(n)) slim.Add(t.DeepClone());
        }
        return (list, slim);
    }

    /// <summary>About how many tokens a request is (Groq counts what's sent plus what comes back).</summary>
    static int TokensIn(JsonArray messages, JsonArray? tools) => (int)((messages.ToJsonString().Length + (tools?.ToJsonString().Length ?? 0)) / 3.4);

    static async Task<Turn> AskGroq(string key, string id, JsonArray messages, JsonArray? tools, Action<string> onText, Action? onThinking,
        CancellationToken cancel, double? temperature, bool smart)
    {
        // made to fit: fewer messages until it does, or not Groq this time
        var (msgs, slim) = ForGroq(messages, tools, 10);
        int need = TokensIn(msgs, slim) + 900;
        if (need > 7000) { (msgs, slim) = ForGroq(messages, tools, 4); need = TokensIn(msgs, slim) + 900; }
        var state = AiUsage.GroqNow(key, id);
        if (need > 7400 || state is { } s && (s.MinuteLeft < need && DateTime.UtcNow < s.MinuteBack || s.DayLeft <= 0 && DateTime.UtcNow < s.DayBack))
            return new Turn("", new(), "too-big Groq can't take this one right now", null);

        var body = new JsonObject
        {
            ["model"] = id, ["messages"] = msgs, ["stream"] = true, ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["max_completion_tokens"] = smart ? 4096 : 1500,
        };
        if (temperature is { } t) body["temperature"] = Math.Min(1, t);
        if (id.Contains("gpt-oss")) body["reasoning_effort"] = smart ? "medium" : "low"; // it thinks before it answers: briefly for quick ones
        if (slim != null && slim.Count > 0) body["tools"] = slim;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double firstWord = 0;
        void Text(string piece) { if (firstWord == 0) firstWord = clock.Elapsed.TotalMilliseconds; onText(piece); }
        var turn = await Send(GroqEndpoint, key, body, Text, onThinking, cancel, gemini: false, groq: true);
        if (turn.Headers is { Count: > 0 } h) AiUsage.GroqSays(key, id, h);
        string mark = "groq:" + id;
        if (turn.Error?.StartsWith("groq-key") == true) AiUsage.RefuseKey(key);
        if (turn.Error == null && !cancel.IsCancellationRequested) AiUsage.Took(mark, firstWord > 0 ? firstWord : clock.Elapsed.TotalMilliseconds);
        if (turn.Error == null || !Regex.IsMatch(turn.Error, @"^(quota|too-big|unsupported|groq-key)")) AiUsage.Count(mark, key, turn.Tokens);
        return turn;
    }

    /// <summary>Groq's errors, as the shared markers (see ExplainGemini) and a few of its own.</summary>
    static string ExplainGroq(int code, string body)
    {
        string message = body, kind = "";
        try
        {
            var e = JsonNode.Parse(body)?["error"];
            message = e?["message"]?.ToString() ?? body;
            kind = e?["code"]?.ToString() ?? e?["type"]?.ToString() ?? "";
        }
        catch { }
        if (code == 401 || kind == "invalid_api_key") return "groq-key Groq didn't accept its key";
        if (code == 404 || kind == "model_not_found" || Regex.IsMatch(message, @"decommissioned|no longer supported|does not exist", RegexOptions.IgnoreCase)) return "unsupported retired " + message;
        if (code == 413 || Regex.IsMatch(message, @"Request too large|reduce your message size|context_length", RegexOptions.IgnoreCase)) return "too-big " + message;
        if (code == 429 || kind == "rate_limit_exceeded")
            return (Regex.IsMatch(message, @"per day|\(RPD\)|\(TPD\)", RegexOptions.IgnoreCase) ? "quota-day " : "quota-minute ") + message;
        if (code >= 500) return "quota-minute Groq had a hiccup (" + code + ")";
        if (kind is "tool_use_failed" || Regex.IsMatch(message, @"Failed to call a function|failed_generation|tool call validation", RegexOptions.IgnoreCase)) return "quota-minute its tool call didn't come out right";
        if (code == 400 && Regex.IsMatch(message, @"does not support|not supported|image|vision", RegexOptions.IgnoreCase)) return "unsupported " + message;
        return message.Length > 300 ? message[..300] : message;
    }

    /// <summary>Is the Groq key there but refused by Groq?</summary>
    public static bool GroqRefused => HasGroqKey && LoadGroqKey() is { } k && AiUsage.KeyRefused(k);

    /// <summary>
    /// Groq's requests left, all its chat models together (each has its own ~1,000 a day). The numbers are Groq's own,
    /// from its last answer; its day refills a little every minute, so what's left grows back as time passes.
    /// </summary>
    public static async Task<List<TierUsage>> GroqUsage()
    {
        if (GroqKeyIfUsable() is not { } key) return new();
        var models = (await GroqModels()).Where(m => !AiUsage.Retired("groq:" + m.Id, key)).ToList();
        int limit = 0, left = 0, minuteLeft = 0, minuteLimit = 0;
        long tokens = 0;
        bool exact = false;
        foreach (var m in models)
        {
            var st = AiUsage.GroqNow(key, m.Id);
            int day = st is { DayLimit: > 0 } ? st.DayLimit : 1000;
            int now = day;
            if (st is { DayLeft: >= 0 })
            {
                exact = true;
                double back = (DateTime.UtcNow - st.At).TotalSeconds * day / 86400.0; // what refilled since
                now = (int)Math.Min(day, st.DayLeft + back);
            }
            limit += day;
            left += now;
            if (st is { MinuteLimit: > 0 }) { minuteLimit += st.MinuteLimit; minuteLeft += DateTime.UtcNow >= st.MinuteBack ? st.MinuteLimit : Math.Max(0, st.MinuteLeft); }
            tokens += AiUsage.TokensOf("groq:" + m.Id, key);
        }
        if (models.Count == 0) return new();
        return new() { new TierUsage("groq", "Groq", limit - left, limit, models.Count, minuteLimit - minuteLeft, minuteLimit, tokens, exact) };
    }

    public static async Task<bool?> CheckGroqKey(string key)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, GroqModelsUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var res = await Http.SendAsync(req, cts.Token);
            if (res.IsSuccessStatusCode) return true;
            return (int)res.StatusCode is 401 or 403 ? false : null;
        }
        catch { return null; }
    }

    /// <summary>Speech to text with Groq's Whisper: fast, many languages, written as it was said.</summary>
    static async Task<(string Text, string? Error)> TranscribeGroq(string key, byte[] wav, CancellationToken cancel)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "speech.wav");
            form.Add(new StringContent("whisper-large-v3-turbo"), "model");
            form.Add(new StringContent("json"), "response_format");
            form.Add(new StringContent("0"), "temperature");
            using var req = new HttpRequestMessage(HttpMethod.Post, GroqAudioUrl) { Content = form };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var res = await Http.SendAsync(req, cts.Token);
            string text = await res.Content.ReadAsStringAsync(cts.Token);
            if (!res.IsSuccessStatusCode)
            {
                string err = ExplainGroq((int)res.StatusCode, text);
                if (err.StartsWith("groq-key")) AiUsage.RefuseKey(key);
                return ("", err);
            }
            AiUsage.Count("groq:whisper-large-v3-turbo", key);
            return ((JsonNode.Parse(text)?["text"]?.ToString() ?? "").Trim(), null);
        }
        catch (Exception ex) { return ("", "Groq couldn't write it down: " + ex.Message); }
    }
}
