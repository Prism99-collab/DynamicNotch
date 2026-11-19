using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicNotch.Services;

/// <summary>
/// The AI chat's connection, to Google's Gemini (the default: a free key, thousands of requests a day; see Gemini.cs)
/// or OpenRouter (one key, hundreds of models): chat completions streamed word by word, with tool calls (the AI asking
/// the notch to do something). Keys are kept encrypted for this Windows user only.
/// </summary>
public static partial class OpenRouter
{
    const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    public const string DefaultModel = "openrouter/free";

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>One tool call the AI asked for.</summary>
    public sealed class ToolCall
    {
        public string Id = "", Name = "";
        public readonly StringBuilder Args = new();
        /// <summary>What the provider attached to the call and wants back with it (Gemini's thought signature).</summary>
        public JsonNode? Extra;
    }

    /// <summary>What one turn of the AI came back with.</summary>
    public sealed record Turn(string Text, List<ToolCall> Calls, string? Error, string? Model)
    {
        /// <summary>The tokens Google counted for this turn (what was sent plus what came back), when it said.</summary>
        public int Tokens { get; init; }
        /// <summary>The provider's own error, word for word (Google says which allowance ran out in it).</summary>
        public string? ErrorBody { get; init; }
        /// <summary>The provider's rate headers (Groq: requests left today, tokens left this minute, when they refill).</summary>
        public Dictionary<string, string>? Headers { get; init; }
    }

    /// <summary>
    /// Sends the conversation and streams the answer: <paramref name="onText"/> gets each new piece of the reply,
    /// <paramref name="onThinking"/> fires while the model reasons before writing. Never throws: errors come back as text.
    /// </summary>
    public static async Task<Turn> Stream(string key, string model, JsonArray messages, JsonArray? tools,
        Action<string> onText, Action? onThinking, CancellationToken cancel, double? temperature = null, string? effort = null)
    {
        if (IsGemini) return await StreamSmart(model, messages, tools, onText, onThinking, cancel, temperature, effort); // Google + Groq
        bool plain = model == "openrouter/free-plain"; // the fallback: OpenRouter's own free pick, no list
        if (plain) model = DefaultModel;
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone(),
            ["stream"] = true,
        };
        if (model == DefaultModel && !plain && FreeChain(messages) is { } chain) { body["model"] = chain[0]?.ToString(); body["models"] = chain; }
        CountFree(model);
        if (temperature is { } t) body["temperature"] = t;
        // reasoning models think less before answering a quick question (the free router often picks one)
        if (effort != null) body["reasoning"] = new JsonObject { ["effort"] = effort };
        if (tools != null && tools.Count > 0) body["tools"] = tools.DeepClone();
        var turn = await Send(Endpoint, key, body, onText, onThinking, cancel, gemini: false);
        // the list of free models wasn't accepted (one was retired): OpenRouter's own free pick instead
        if (turn.Error == "retry-plain") return await Stream(key, "openrouter/free-plain", messages, tools, onText, onThinking, cancel, temperature, effort);
        return turn;
    }

    /// <summary>One request, its answer streamed: shared by both providers (they speak the same chat format).</summary>
    /// <summary>What OpenRouter says about the key: its plan, credits left (null: no limit set), daily free requests.</summary>
    public sealed record KeyAccount(bool FreeTier, double? CreditsLeft, double Used, int FreePerDay);
    static (KeyAccount Info, DateTime At)? _account;

    public static async Task<KeyAccount?> OpenRouterAccount()
    {
        if (_account is { } a && DateTime.UtcNow - a.At < TimeSpan.FromMinutes(5)) return a.Info;
        if (LoadOpenRouterKey() is not { } key) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/key");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var res = await Http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            var d = JsonNode.Parse(await res.Content.ReadAsStringAsync(cts.Token))?["data"];
            bool free = d?["is_free_tier"]?.GetValue<bool>() ?? true;
            double? left = d?["limit_remaining"] is JsonValue lv && lv.TryGetValue(out double l) ? l : null;
            double used = d?["usage"] is JsonValue uv && uv.TryGetValue(out double u) ? u : 0;
            var info = new KeyAccount(free, left, used, free ? 50 : 1000); // OpenRouter: 50 free-model requests a day, 1000 once credit was bought
            _account = (info, DateTime.UtcNow);
            return info;
        }
        catch { return null; }
    }

    static DateTime _warmedAt;

    /// <summary>
    /// Opens the connection to the AI ahead of the first message (the secure handshake takes a moment): called when
    /// the chat opens. Once a minute at most; nothing is asked, nothing counts.
    /// </summary>
    public static void Warm()
    {
        if ((DateTime.UtcNow - _warmedAt).TotalSeconds < 60 || !HasKey) return;
        _warmedAt = DateTime.UtcNow;
        string host = IsGemini ? "https://generativelanguage.googleapis.com/" : "https://openrouter.ai/";
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var req = new HttpRequestMessage(HttpMethod.Head, host);
                using var res = await Http.SendAsync(req, cts.Token);
            }
            catch { }
            if (IsGemini) try { await GeminiModels(); } catch { }
        });
    }

    static async Task<Turn> Send(string endpoint, string key, JsonObject body, Action<string> onText, Action? onThinking, CancellationToken cancel, bool gemini, bool groq = false)
    {
        string who = groq ? "Groq" : gemini ? "Google" : "OpenRouter";
        Dictionary<string, string>? rate = null;
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, ToolCall>();
        string? usedModel = null;
        int tokens = 0;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(40)); // until the answer starts
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            rate = res.Headers.Where(h => h.Key.StartsWith("x-ratelimit", StringComparison.OrdinalIgnoreCase)).ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.FirstOrDefault() ?? "");
            if (!res.IsSuccessStatusCode)
            {
                string err = await res.Content.ReadAsStringAsync(cancel);
                if (groq) return new Turn("", new(), ExplainGroq((int)res.StatusCode, err), null) { ErrorBody = err, Headers = rate };
                if (gemini) return new Turn("", new(), ExplainGemini((int)res.StatusCode, err), null) { ErrorBody = err };
                if ((int)res.StatusCode == 400 && body.ContainsKey("models") && !cancel.IsCancellationRequested) return new Turn("", new(), "retry-plain", null);
                return new Turn("", new(), Explain((int)res.StatusCode, err), null);
            }
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            using var stream = await res.Content.ReadAsStreamAsync(cancel);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            using var reg = quiet.Token.Register(() => { try { stream.Dispose(); } catch { } });
            while (!cancel.IsCancellationRequested)
            {
                quiet.CancelAfter(TimeSpan.FromSeconds(75)); // keep-alives count: only true silence ends it
                string? line;
                try { line = await reader.ReadLineAsync(quiet.Token); }
                catch (Exception) when (!cancel.IsCancellationRequested && quiet.IsCancellationRequested)
                {
                    return new Turn(text.ToString(), new(), "The answer stopped coming (no data for over a minute). Try again, or another model.", usedModel);
                }
                if (line == null) break;
                if (line.Length == 0 || line[0] == ':') continue;       // keep-alive comments (": OPENROUTER PROCESSING")
                if (!line.StartsWith("data:")) continue;
                string data = line[5..].Trim();
                if (data == "[DONE]") break;
                JsonNode? node;
                try { node = JsonNode.Parse(data); } catch { continue; }
                if (node == null) continue;
                if (node["error"] is JsonObject e)
                {
                    int code = e["code"] is JsonValue ev && ev.TryGetValue(out int c0) ? c0 : 0;
                    return new Turn(text.ToString(), new(), groq ? ExplainGroq(code, new JsonObject { ["error"] = e.DeepClone() }.ToJsonString()) : gemini ? ExplainGemini(code, e.ToJsonString()) : Friendly(code, e["message"]?.ToString() ?? "Something went wrong"), usedModel) { ErrorBody = e.ToJsonString(), Headers = rate };
                }
                usedModel ??= node["model"]?.ToString();
                if (node["usage"]?["total_tokens"] is JsonValue tv && tv.TryGetValue(out int total)) tokens = total; // it grows as it writes: the last one counts
                if (node["choices"] is not JsonArray choices || choices.Count == 0) continue;
                var delta = choices[0]?["delta"];
                if (delta == null) continue;
                if (delta["content"] is JsonValue cv && cv.TryGetValue(out string? piece) && piece.Length > 0)
                {
                    text.Append(piece);
                    onText(piece);
                }
                else if ((delta["reasoning"] ?? delta["reasoning_content"]) is JsonValue rv && rv.TryGetValue(out string? r) && r.Length > 0)
                    onThinking?.Invoke();
                // Gemini puts its thinking in the text, between <thought> marks: kept out
                if (gemini && text.ToString().Contains("<thought>") && !text.ToString().Contains("</thought>")) onThinking?.Invoke();
                if (delta["tool_calls"] is JsonArray tcs)
                    foreach (var tc in tcs)
                    {
                        if (tc == null) continue;
                        int index = tc["index"] is JsonValue iv && iv.TryGetValue(out int i) ? i : calls.Count;
                        if (!calls.TryGetValue(index, out var call)) calls[index] = call = new ToolCall();
                        if (tc["id"]?.ToString() is { Length: > 0 } id) call.Id = id;
                        if (tc["function"]?["name"]?.ToString() is { Length: > 0 } name) call.Name = name;
                        if (tc["function"]?["arguments"]?.ToString() is { Length: > 0 } args) call.Args.Append(args);
                        if (tc["extra_content"] is JsonNode extra) call.Extra = extra.DeepClone();
                    }
            }
            if (cancel.IsCancellationRequested) return new Turn(text.ToString(), new(), null, usedModel) { Tokens = tokens };
            foreach (var c in calls.Values) if (c.Id.Length == 0) c.Id = "call_" + Guid.NewGuid().ToString("N")[..12];
            return new Turn(text.ToString(), calls.Values.Where(c => c.Name.Length > 0).ToList(), null, usedModel) { Tokens = tokens, Headers = rate };
        }
        catch (Exception) when (cancel.IsCancellationRequested) { return new Turn(text.ToString(), new(), null, usedModel) { Tokens = tokens }; }
        catch (OperationCanceledException) { return new Turn(text.ToString(), new(), $"No answer from {who} in time. Check your connection and try again.", usedModel); }
        catch (HttpRequestException) { return new Turn(text.ToString(), new(), $"Can't reach {who}. Check your internet connection.", usedModel); }
        catch (Exception ex) { AppLog.Write("ai: " + ex.Message); return new Turn(text.ToString(), new(), "Something went wrong: " + ex.Message, usedModel); }
    }

    /// <summary>One model from OpenRouter's list: what it takes in, what it gives back, whether it's free, its price per output token.</summary>
    public sealed record ModelInfo(string Id, HashSet<string> In, HashSet<string> Out, bool Free, double Price);

    static List<ModelInfo>? _models;
    static DateTime _modelsAt;

    /// <summary>OpenRouter's list of models (read once a day). Null when it can't be read.</summary>
    public static async Task<List<ModelInfo>?> Models()
    {
        if (_models != null && DateTime.UtcNow - _modelsAt < TimeSpan.FromHours(12)) return _models;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string json = await Http.GetStringAsync("https://openrouter.ai/api/v1/models", cts.Token);
            var list = new List<ModelInfo>();
            foreach (var m in JsonNode.Parse(json)?["data"]?.AsArray() ?? new JsonArray())
            {
                if (m?["id"]?.ToString() is not { } id) continue;
                var ins = new HashSet<string>((m["architecture"]?["input_modalities"] as JsonArray)?.Select(x => x?.ToString() ?? "") ?? Array.Empty<string>());
                var outs = new HashSet<string>((m["architecture"]?["output_modalities"] as JsonArray)?.Select(x => x?.ToString() ?? "") ?? Array.Empty<string>());
                double.TryParse(m["pricing"]?["completion"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double price);
                double.TryParse(m["pricing"]?["prompt"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pin);
                bool free = id.EndsWith(":free") || price == 0 && pin == 0 && !id.StartsWith("openrouter/auto");
                list.Add(new ModelInfo(id, ins, outs, free, price));
            }
            _models = list;
            _modelsAt = DateTime.UtcNow;
            return list;
        }
        catch { return _models; }
    }

    /// <summary>The models that can look at pictures. Null when the list can't be read.</summary>
    public static async Task<HashSet<string>?> VisionModels() =>
        await Models() is { } all ? new HashSet<string>(all.Where(m => m.In.Contains("image")).Select(m => m.Id), StringComparer.OrdinalIgnoreCase) : null;

    /// <summary>The model to hear speech: yours if it can, else a free one that can, else the cheapest that can.</summary>
    public static async Task<string?> AudioModel(string current)
    {
        if (IsGemini) return (await GeminiChain(current, audio: true)).FirstOrDefault();
        var all = await Models();
        if (all == null) return "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free";
        if (all.FirstOrDefault(m => m.Id == current && m.In.Contains("audio")) is { } mine) return mine.Id;
        return all.Where(m => m.In.Contains("audio") && m.Out.Contains("text")).OrderByDescending(m => m.Free).ThenBy(m => m.Price).FirstOrDefault()?.Id;
    }

    /// <summary>The models that make pictures: free ones first, then the cheapest.</summary>
    public static async Task<List<ModelInfo>> ImageModels() =>
        (await Models() ?? new()).Where(m => m.Out.Contains("image") && !m.Id.StartsWith("openrouter/")).OrderByDescending(m => m.Free).ThenBy(m => m.Price).ToList();

    /// <summary>Speech to text: the recording, written down word for word in the language it was spoken.</summary>
    public static async Task<(string Text, string? Error)> Transcribe(string key, string model, byte[] wav, CancellationToken cancel)
    {
        // Google: the model asked first, then the next ones that can hear, until one writes it down
        if (IsGemini && !model.StartsWith("#") && GroqKeyIfUsable() is { } groqKey)
        {
            var heard = await TranscribeGroq(groqKey, wav, cancel);
            if (heard.Error == null || !HasGeminiKey || cancel.IsCancellationRequested) return heard;
        }
        if (IsGemini && !model.StartsWith("#") && HasGeminiKey)
        {
            var chain = await GeminiChain(model, audio: true);
            (string Text, string? Error) last = ("", "No Google model can hear speech right now.");
            foreach (var id in chain.Take(5))
            {
                last = await Transcribe(key, "#" + id, wav, cancel);
                if (last.Error == null || cancel.IsCancellationRequested) return last;
                if (last.Error.Contains("key", StringComparison.OrdinalIgnoreCase) && last.Error.Contains("accept", StringComparison.OrdinalIgnoreCase)) return last;
            }
            return last;
        }
        model = model.TrimStart('#');
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "You transcribe speech. Reply with exactly what was said, in the language it was said, with normal punctuation. No comments, no quotes, no translation. If nothing intelligible was said, reply with nothing." },
                new JsonObject { ["role"] = "user", ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "Transcribe this." },
                    new JsonObject { ["type"] = "input_audio", ["input_audio"] = new JsonObject { ["data"] = Convert.ToBase64String(wav), ["format"] = "wav" } },
                } },
            },
        };
        if (!IsGemini) body["reasoning"] = new JsonObject { ["effort"] = "low", ["exclude"] = true };
        var (json, error) = await Post(IsGemini ? GeminiEndpoint : Endpoint, key, body, TimeSpan.FromSeconds(40), cancel);
        if (error != null)
        {
            if (IsGemini && error.StartsWith("quota-day")) AiUsage.MarkFull(model, key);
            if (IsGemini && error.StartsWith("unsupported")) { if (error.StartsWith("unsupported retired") || error.StartsWith("unsupported denied")) AiUsage.Retire(model, key); return ("", "That model can't hear speech."); }
            if (IsGemini && error.StartsWith("quota")) { Spend(model, error.StartsWith("quota-day")); return ("", "That model's free requests are used up for now: try again (another one will listen)."); }
            return ("", error);
        }
        if (IsGemini) AiUsage.Count(model, key);
        string text = json?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
        return (text.Trim().Trim('"', '“', '”'), null);
    }

    /// <summary>A picture made from a description (and, optionally, a picture to start from). PNG / JPEG bytes.</summary>
    public static async Task<(List<byte[]> Images, string Text, string? Error)> MakeImage(string key, string model, string prompt, byte[]? from, CancellationToken cancel)
    {
        JsonNode content = prompt;
        if (from != null)
            content = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = prompt },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(from) } },
            };
        var body = new JsonObject
        {
            ["model"] = model,
            ["modalities"] = new JsonArray { "image", "text" },
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } },
        };
        var (json, error) = await Post(Endpoint, key, body, TimeSpan.FromSeconds(120), cancel); // pictures: OpenRouter's models
        if (error != null) return (new(), "", error);
        var msg = json?["choices"]?[0]?["message"];
        var images = new List<byte[]>();
        foreach (var img in msg?["images"] as JsonArray ?? new JsonArray())
        {
            string url = img?["image_url"]?["url"]?.ToString() ?? "";
            int comma = url.IndexOf(',');
            if (url.StartsWith("data:") && comma > 0)
                try { images.Add(Convert.FromBase64String(url[(comma + 1)..])); } catch { }
            else if (url.StartsWith("http"))
                try { images.Add(await Http.GetByteArrayAsync(url, cancel)); } catch { }
        }
        return (images, msg?["content"]?.ToString() ?? "", images.Count == 0 ? "The model didn't return a picture." : null);
    }

    /// <summary>One request, one answer (no streaming), with the same plain-words errors.</summary>
    static async Task<(JsonNode? Json, string? Error)> Post(string endpoint, string key, JsonObject body, TimeSpan limit, CancellationToken cancel)
    {
        bool gemini = endpoint == GeminiEndpoint;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(limit);
            using var res = await Http.SendAsync(req, cts.Token);
            string text = await res.Content.ReadAsStringAsync(cts.Token);
            if (!res.IsSuccessStatusCode) return (null, gemini ? ExplainGemini((int)res.StatusCode, text) : Explain((int)res.StatusCode, text));
            var json = JsonNode.Parse(text);
            if (json?["error"] is JsonObject e) return (null, Friendly(e["code"] is JsonValue ev && ev.TryGetValue(out int code) ? code : 0, e["message"]?.ToString() ?? "Something went wrong"));
            return (json, null);
        }
        catch (Exception) when (cancel.IsCancellationRequested) { return (null, "Cancelled"); }
        catch (OperationCanceledException) { return (null, "No answer in time."); }
        catch (HttpRequestException) { return (null, "Can't reach the AI. Check your internet connection."); }
        catch (Exception ex) { return (null, "Something went wrong: " + ex.Message); }
    }

    /// <summary>
    /// "Free": capable free models that use tools well, best first, then OpenRouter's own pick, instead of a random
    /// free model (some are tiny). With pictures, only ones that can see them.
    /// </summary>
    static JsonArray? FreeChain(JsonArray messages)
    {
        bool pictures = messages.Any(m => m?["content"] is JsonArray parts && parts.Any(p => p?["type"]?.ToString() == "image_url"));
        string[] best = pictures
            ? new[] { "google/gemma-4-31b-it:free", "nvidia/nemotron-3.5-super-vl:free", DefaultModel }
            : new[] { "google/gemma-4-31b-it:free", "nvidia/nemotron-3-ultra-550b-a55b:free", "inclusionai/ling-3.2-flash:free", DefaultModel };
        // only those OpenRouter still lists (free models come and go)
        var known = _models?.Select(m => m.Id).ToHashSet();
        var chain = best.Where(id => id == DefaultModel || known == null || known.Contains(id)).Take(3).ToList();
        return chain.Count > 0 ? new JsonArray(chain.Select(id => (JsonNode?)id).ToArray()) : null;
    }

    // what was sent to free models today (the allowance is per day, UTC)
    static readonly string CountFile = Path.Combine(Settings.Folder, "ai-free-count.txt");

    static void CountFree(string model)
    {
        if (IsGemini || !(model == DefaultModel || model.EndsWith(":free"))) return;
        try
        {
            var (day, n) = FreeUsedToday();
            File.WriteAllText(CountFile, $"{DateTime.UtcNow:yyyy-MM-dd} {n + 1}");
        }
        catch { }
    }

    /// <summary>(the UTC day, requests sent to free models that day).</summary>
    public static (string Day, int Used) FreeUsedToday()
    {
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        try
        {
            var parts = File.Exists(CountFile) ? File.ReadAllText(CountFile).Split(' ') : Array.Empty<string>();
            if (parts.Length == 2 && parts[0] == today && int.TryParse(parts[1], out int n)) return (today, n);
        }
        catch { }
        return (today, 0);
    }

    /// <summary>Asks the key's provider whether it works: true yes, false no, null couldn't tell (no connection).</summary>
    public static async Task<bool?> CheckKey(string key)
    {
        if (KindOf(key) == "gemini") return await CheckGeminiKey(key);
        if (KindOf(key) == "groq") return await CheckGroqKey(key);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/key");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var res = await Http.SendAsync(req, cts.Token);
            if (res.IsSuccessStatusCode) return true;
            return (int)res.StatusCode is 401 or 403 ? false : null;
        }
        catch { return null; }
    }

    /// <summary>The free daily allowance: what's known of it (from OpenRouter's own answers, and what was sent today).</summary>
    public static int FreeLimit = 50;
    public static DateTime? FreeResetsAt;

    /// <summary>An error in plain words; the daily free limit says when it comes back and how to raise it.</summary>
    static string Explain(int code, string body)
    {
        string message = ErrorText(body);
        try
        {
            var meta = JsonNode.Parse(body)?["error"]?["metadata"];
            if (message.Contains("free-models-per-day", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(meta?["headers"]?["X-RateLimit-Limit"]?.ToString(), out int limit)) FreeLimit = limit;
                if (long.TryParse(meta?["headers"]?["X-RateLimit-Reset"]?.ToString(), out long ms)) FreeResetsAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                string back = FreeResetsAt is { } at ? (at.Date == DateTime.Today ? $"at {at:HH:mm}" : $"tomorrow at {at:HH:mm}") : "tomorrow";
                return $"Today's {FreeLimit} free AI requests are used up (each step, like a search, is one). They come back {back}. $10 of credit on OpenRouter raises it to 1000 a day, or pick a paid model.";
            }
            if (code == 429 && meta?["provider_name"]?.ToString() is { Length: > 0 } provider)
                return $"The free model is busy at {provider} right now. Try again in a moment, or pick another model.";
        }
        catch { }
        return Friendly(code, message);
    }

    static string ErrorText(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.ToString() ?? body; } catch { return body.Length > 200 ? body[..200] : body; }
    }

    static string Friendly(int code, string message) => code switch
    {
        401 => "OpenRouter didn't accept the key. Type /key to enter it again.",
        402 => "Your OpenRouter credits ran out. Add credits, or switch to the free model with /model free.",
        429 => "Too many requests right now (free models are busy). Wait a moment, or pick another model with /model.",
        404 => "That model isn't available: " + message + ". Pick another with /model.",
        _ => message.Length > 0 ? message : "OpenRouter answered with an error (" + code + ").",
    };

    // ───────────────────────────── the key, encrypted for this Windows user ─────────────────────────────

    static string KeyFile => IsGemini ? (HasGeminiKey || !HasGroqKey ? GeminiKeyFile : GroqKeyFile) : OpenRouterKeyFile;

    /// <summary>Is there a key for the provider in use?</summary>
    public static bool HasKey => File.Exists(KeyFile);

    public static string? LoadKey() => ReadKey(KeyFile);
    public static string? LoadOpenRouterKey() => ReadKey(OpenRouterKeyFile);

    static string? ReadKey(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var plain = Unprotect(File.ReadAllBytes(file));
            return plain == null ? null : Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    /// <summary>Keeps a key for its own provider (told apart by its shape), and makes that provider the one in use.</summary>
    public static bool SaveKey(string key)
    {
        try
        {
            key = key.Trim();
            string kind = KindOf(key) ?? Provider;
            Directory.CreateDirectory(Settings.Folder);
            var sealedBytes = Protect(Encoding.UTF8.GetBytes(key));
            if (sealedBytes == null) return false;
            File.WriteAllBytes(kind == "gemini" ? GeminiKeyFile : kind == "groq" ? GroqKeyFile : OpenRouterKeyFile, sealedBytes);
            // Google and Groq work as one (the smart router); OpenRouter on its own
            Provider = kind == "openrouter" ? "openrouter" : "gemini";
            _groq = null;
            // a new key: nothing carried over from the old one (its used-up models, its list, its account)
            _gemini = null;
            _account = null;
            lock (_gate) _exhausted.Clear();
            return true;
        }
        catch { return false; }
    }

    public static void ForgetKey() { try { File.Delete(KeyFile); } catch { } }

    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    /// <summary>Encrypted for this Windows account only (other secrets: the calendar's address).</summary>
    public static byte[]? Seal(byte[] data) => Protect(data);
    public static byte[]? Unseal(byte[] data) => Unprotect(data);
    static byte[]? Protect(byte[] data) => Crypt(data, true);
    static byte[]? Unprotect(byte[] data) => Crypt(data, false);

    static byte[]? Crypt(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
            const int UiForbidden = 0x1;
            bool ok = protect ? CryptProtectData(ref input, "Dynamic Notch", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                              : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) return null;
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            LocalFree(output.Data);
            return result;
        }
        finally { h.Free(); }
    }
}
