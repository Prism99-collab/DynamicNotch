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
            : preference is "gemini" or "groq" || HasGeminiKey || HasGroqKey ? "gemini"   // Google and Groq: one engine, two keys
            : HasOpenRouterKey ? "openrouter" : "gemini";

    /// <summary>Which kind of key this is: "gemini" (AIza… or the newer AQ.…), "openrouter" (sk-or-…), or null.</summary>
    public static string? KindOf(string key) =>
        Regex.IsMatch(key, @"^(?:AIza[0-9A-Za-z_\-]{30,}|AQ\.[0-9A-Za-z_\-]{30,})$") ? "gemini" : Regex.IsMatch(key, @"^gsk_[A-Za-z0-9]{20,}$") ? "groq"
        : Regex.IsMatch(key, @"^sk-or-[A-Za-z0-9_\-]{20,}$") ? "openrouter" : null;

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
        bool fetched = list.Count > 0;
        if (!fetched && StoredModels().Gemini is { Count: > 0 } stored) list = stored.ToList(); // the last list Google gave
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
        // read from Google: kept for 6 hours (and on disk); couldn't be read: asked again in 2 minutes
        _geminiAt = fetched ? DateTime.UtcNow : DateTime.UtcNow - TimeSpan.FromHours(6) + TimeSpan.FromMinutes(2);
        if (fetched) StoreModels(list, null);
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
    /// <summary>This turn needs the smart model whatever its length (a correction, the screen): set by the chat for one answer.</summary>
    public static bool SmartNext;

    public static bool NeedsSmart(JsonArray messages)
    {
        if (SmartNext) return true;
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
        // in each tier the ones that answered fastest lately go first (one not tried yet gets its chance)
        foreach (var tier in tiers)
        {
            var inTier = usable.Where(m => Tier(m.Id) == tier).ToList(); // newest first already
            order.AddRange(inTier.Where(m => AiUsage.SpeedOf(m.Id) != null).OrderBy(m => AiUsage.SpeedOf(m.Id)).Select(m => m.Id));
            order.AddRange(inTier.Where(m => AiUsage.SpeedOf(m.Id) == null).Select(m => m.Id));
        }
        order.AddRange(usable.Select(m => m.Id));
        string? key = LoadGeminiKey();
        var chain = order.Distinct().Where(id => !Spent(id) && !AiUsage.Retired(id, key)).ToList();
        if (chain.Count == 0 && order.All(id => AiUsage.Retired(id, key)) && DateTime.UtcNow - _geminiAt > TimeSpan.FromMinutes(1))
        {
            _gemini = null; // every one it knew was retired: Google's list again
            return await GeminiChain(chosen, audio, smart);
        }
        // all out for now: the ones only resting for the minute, so the error says the right thing
        return chain.Count > 0 ? chain : order.Distinct().Where(id => !AiUsage.Retired(id, key)).Take(1).ToList();
    }

    /// <summary>One kind of model's free requests today: how many, out of how many, and this minute.</summary>
    public sealed record TierUsage(string Tier, string Name, int Used, int Limit, int Versions, int MinuteUsed, int MinuteLimit, long Tokens = 0, bool Exact = false, bool Paid = false)
    {
        public int Left => Math.Max(0, Limit - Used);
        public double LeftShare => Limit <= 0 ? 0 : Math.Clamp((double)Left / Limit, 0, 1);
    }

    /// <summary>
    /// Google's free allowance by kind of model (requests a day and a minute, per model version, as AI Studio lists
    /// them for the free tier): Flash-Lite 500 / 15, Flash 20 / 5, Gemma 14,400 / 30. Every version has its own.
    /// </summary>
    static (int Day, int Minute) GoogleFreeLimit(string tier) => tier switch { "lite" => (500, 10), "flash" => (20, 5), "gemma" => (14400, 30), _ => (0, 0) };

    /// <summary>
    /// Today's use of the Google key, by kind of model, from what this PC sent and what Google answered. A version
    /// Google turned away before any request went through isn't on the free tier for this key: it doesn't count.
    /// </summary>
    public static async Task<List<TierUsage>> GeminiUsage(bool quick = false)
    {
        if (!HasGeminiKey) return new();
        var models = quick ? GeminiModelsNow() : await GeminiModels();
        string? key = LoadGeminiKey();
        var list = new List<TierUsage>();
        foreach (var (tier, name) in new[] { ("lite", "Fast"), ("flash", "Smart"), ("gemma", "Backup") })
        {
            var (tierDay, tierMinute) = GoogleFreeLimit(tier);
            int used = 0, limit = 0, versions = 0, minute = 0, minuteLimit = 0, sent = 0;
            long tokens = 0;
            bool exact = true;
            foreach (var m in models.Where(m => Tier(m.Id) == tier && !AiUsage.Retired(m.Id, key)))
            {
                var (u, full) = AiUsage.Of(m.Id, key);
                if (full && u == 0) continue; // not free for this key
                var learned = AiUsage.LimitOf(m.Id, key);
                int perDay = learned.Day ?? tierDay, perMinute = learned.Minute ?? tierMinute;
                exact &= learned.Day != null;
                versions++;
                limit += perDay;
                used += full ? perDay : Math.Min(u, perDay);
                sent += u;
                minute += AiUsage.LastMinute(m.Id);
                minuteLimit += perMinute;
                tokens += AiUsage.TokensOf(m.Id, key);
            }
            bool paid = AiUsage.PlanOf(key) == "paid";
            if (versions > 0) list.Add(new TierUsage(tier, name, paid ? sent : used, limit, versions, minute, minuteLimit, tokens, exact, paid));
        }
        return list;
    }

    /// <summary>Has Google retired this model (or does it not offer it to the key in use)?</summary>
    public static bool IsRetired(string id) => AiUsage.Retired(id, LoadGeminiKey());

    /// <summary>The plan of the Google key in use: "free", "paid", or null until Google says.</summary>
    public static string? GeminiPlan => AiUsage.PlanOf(LoadGeminiKey());

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

    /// <summary>How long a fast model gets to start answering before a second one is asked too.</summary>
    static readonly TimeSpan HedgeAfter = TimeSpan.FromSeconds(5);

    /// <summary>One engine and model to ask: Google's (gemini-…, gemma-…) or Groq's (groq:…).</summary>
    sealed record Cand(bool Groq, string Id)
    {
        public string Mark => Groq ? "groq:" + Id : Id;
        public bool Cheap => Groq || Tier(Id) is "lite" or "gemma";
    }

    /// <summary>Is there a picture in what's being asked now (Groq can't see them: Google answers)?</summary>
    static bool NeedsVision(JsonArray messages)
    {
        int lastUser = -1;
        for (int i = messages.Count - 1; i >= 0; i--) if (messages[i]?["role"]?.ToString() == "user") { lastUser = i; break; }
        if (lastUser < 0) return false;
        for (int i = lastUser; i < messages.Count; i++)
            if (messages[i]?["content"] is JsonArray parts && parts.Any(p => p?["type"]?.ToString() == "image_url")) return true;
        return false;
    }

    /// <summary>
    /// Google and Groq, working as one. Who answers first depends on the question:
    ///  • quick ones (commands, short questions): Groq, it answers in well under a second and spares Google's requests;
    ///  • demanding ones (writing, code, analysis, a document): Google's smart model, Groq's strongest right behind;
    ///  • a picture or the screen: Google (Groq can't see);
    ///  • a model you picked: that one first.
    /// Then each hands over to the other the moment something goes wrong: a slow start (the other is asked alongside,
    /// the first to write wins), used up for the minute or the day, a hiccup, a retired model, a request too big for
    /// Groq's minute, even a key that stopped working. Mid-task too: the conversation is made to suit whoever's next.
    /// </summary>
    static async Task<Turn> StreamSmart(string model, JsonArray messages, JsonArray? tools,
        Action<string> onText, Action? onThinking, CancellationToken cancel, double? temperature, string? effort)
    {
        string? gKey = HasGeminiKey ? LoadGeminiKey() : null;
        if (gKey != null && AiUsage.KeyRefused(gKey)) gKey = null; // Google already said this key is wrong: Groq alone
        string? qKey = GroqKeyIfUsable();
        bool vision = NeedsVision(messages), smart = NeedsSmart(messages);
        bool pickedGroq = model.StartsWith("groq:");
        var google = gKey != null ? (await GeminiChain(pickedGroq ? DefaultModel : model, audio: false, smart)).Select(id => new Cand(false, id)).ToList() : new();
        var groq = qKey != null && !vision ? (await GroqChain(pickedGroq ? model[5..] : null)).Select(id => new Cand(true, id)).ToList() : new();
        bool pickedGoogle = !pickedGroq && model != DefaultModel && !model.Contains('/') && google.Count > 0 && google[0].Id == model;

        var order = new List<Cand>();
        if (pickedGroq && groq.Count > 0) order.Add(groq[0]);
        if (pickedGoogle) order.Add(google[0]);
        if (smart)
        {
            order.AddRange(google.Where(c => Tier(c.Id) == "flash"));
            order.AddRange(groq);
            order.AddRange(google);
        }
        else
        {
            order.AddRange(groq.Take(1));
            order.AddRange(google.Where(c => Tier(c.Id) == "lite"));
            order.AddRange(groq);
            order.AddRange(google);
        }
        var chain = order.DistinctBy(c => c.Mark).ToList();
        if (chain.Count == 0)
            return new Turn("", new(), gKey == null && qKey == null ? "No AI key works right now. Type /key to enter one again." : "No AI model is available right now. Try again in a minute.", null);

        bool auto = !pickedGroq && !pickedGoogle;
        Turn? last = null;
        var tried = new HashSet<string>();
        bool anyMinute = false, mended = false, skipGroq = false, skipGoogle = false;
        string? googleKeyProblem = null;
        var started = DateTime.UtcNow;
        for (int i = 0; i < chain.Count; i++)
        {
            var c = chain[i];
            if (c.Groq ? skipGroq : skipGoogle) continue;
            if (!tried.Add(c.Mark) || Spent(c.Mark) || AiUsage.Retired(c.Mark, c.Groq ? qKey : gKey)) continue;
            // a slow start: someone else is asked alongside, the other engine first (it fails for other reasons)
            Cand? backup = auto
                ? chain.Skip(i + 1).Where(x => x.Cheap && !tried.Contains(x.Mark) && !Spent(x.Mark) && !(x.Groq ? skipGroq : skipGoogle))
                       .OrderBy(x => x.Groq == c.Groq ? 1 : 0).FirstOrDefault() : null;
            var (turn, by) = await Race(c, backup, gKey, qKey, messages, tools, onText, onThinking, cancel, temperature, effort, smart, tried);
            last = turn;
            if (turn.Error == null || cancel.IsCancellationRequested || turn.Text.Length > 0) return turn with { Model = by.Mark };
            string err = turn.Error;
            var keyOf = by.Groq ? qKey : gKey;
            // Google refused the tool steps' signatures (another model made them): told to skip that check, asked again once
            if (!by.Groq && err.StartsWith("signature") && !mended)
            {
                mended = true;
                messages = WithoutSignatureCheck(messages);
                tried.Remove(by.Mark);
                i = chain.IndexOf(by) - 1;
                continue;
            }
            if (by.Groq && err.StartsWith("groq-key")) { skipGroq = true; continue; }          // Groq's key: Google carries on
            if (by.Groq && err.StartsWith("too-big")) { skipGroq = true; continue; }          // not Groq's size this minute: Google
            if (!by.Groq && err.StartsWith("Google didn't accept the key")) { googleKeyProblem = err; skipGoogle = true; continue; }
            if (!by.Groq && err.StartsWith("region ")) { googleKeyProblem = err[7..]; skipGoogle = true; continue; }
            if (err.StartsWith("quota-day")) { Spend(by.Mark, forTheDay: true); continue; }
            if (err.StartsWith("quota-minute")) { Spend(by.Mark, forTheDay: false); anyMinute = true; continue; }
            if (err.StartsWith("unsupported"))
            {
                if (err.StartsWith("unsupported retired") || err.StartsWith("unsupported denied") || Regex.IsMatch(err, @"not found|no longer", RegexOptions.IgnoreCase)) { Spend(by.Mark, forTheDay: true); AiUsage.Retire(by.Mark, keyOf); }
                continue;
            }
            if (err.StartsWith("No answer from") && (DateTime.UtcNow - started).TotalSeconds < 60) { Spend(by.Mark, forTheDay: false); anyMinute = true; continue; }
            // can't reach this engine: the other one may still be reachable
            if (err.StartsWith("Can't reach")) { if (by.Groq) skipGroq = true; else skipGoogle = true; continue; }
            return turn;
        }
        // nobody could: the most useful reason
        if (googleKeyProblem != null && (qKey == null || skipGroq)) return new Turn("", new(), googleKeyProblem, null);
        string reason = last?.Error ?? "";
        if (reason.StartsWith("Can't reach")) return new Turn("", new(), "Can't reach the AI. Check your internet connection.", null);
        if (anyMinute && !reason.StartsWith("quota-day"))
            return new Turn("", new(), "The AI models are all busy right now (too many requests this minute). Try again in a minute.", null);
        if (reason.StartsWith("quota") || reason.StartsWith("too-big"))
            return new Turn("", new(), $"Today's free AI requests are used up on every model. Google's come back at {GeminiReset:HH:mm}." + (qKey == null ? " A free Groq key adds a second allowance: type /groq." : ""), null);
        if (reason.StartsWith("signature")) return new Turn("", new(), "The AI refused to continue this conversation's steps. Start a new chat (Ctrl+N) and ask again.", null);
        if (reason.StartsWith("groq-key")) return new Turn("", new(), "Groq didn't accept its key. Type /groq to enter it again.", null);
        return new Turn("", new(), reason.StartsWith("unsupported") ? "None of the AI models could take this request: " + reason[11..].Replace("retired ", "").Replace("denied ", "") : reason, null);
    }

    /// <summary>One request to one engine and model.</summary>
    static Task<Turn> Ask(Cand c, string? gKey, string? qKey, JsonArray messages, JsonArray? tools, Action<string> onText, Action? onThinking,
        CancellationToken cancel, double? temperature, string? effort, bool smart) =>
        c.Groq ? AskGroq(qKey!, c.Id, messages, tools, onText, onThinking, cancel, temperature, smart)
               : AskModel(gKey!, c.Id, messages, tools, onText, onThinking, cancel, temperature, effort);

    /// <summary>
    /// The first one, and if it's slow to start, a second one alongside: the first to write is the one you see (the
    /// other stops at once); if one fails before writing, the other's answer counts.
    /// </summary>
    static async Task<(Turn Turn, Cand By)> Race(Cand first, Cand? backup, string? gKey, string? qKey, JsonArray messages, JsonArray? tools,
        Action<string> onText, Action? onThinking, CancellationToken cancel, double? temperature, string? effort, bool smart, HashSet<string> tried)
    {
        int owner = 0;
        Action<string> Writer(int who) => piece =>
        {
            int was = Interlocked.CompareExchange(ref owner, who, 0);
            if (was == 0 || was == who) onText(piece);
        };
        using var c1 = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var t1 = Ask(first, gKey, qKey, messages, tools, Writer(1), onThinking, c1.Token, temperature, effort, smart);
        if (backup == null) return (await t1, first);
        // Groq starts in well under a second: a shorter wait before asking someone else
        // a slower model (Flash thinks before it writes) gets a little longer, but never the whole 40 s on its own
        try { await Task.WhenAny(t1, Task.Delay(first.Groq ? TimeSpan.FromSeconds(3) : first.Cheap ? HedgeAfter : TimeSpan.FromSeconds(8), cancel)); } catch { }
        if (t1.IsCompleted || Volatile.Read(ref owner) != 0 || cancel.IsCancellationRequested) return (await t1, first);

        tried.Add(backup.Mark);
        using var c2 = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var t2 = Ask(backup, gKey, qKey, messages, tools, Writer(2), onThinking, c2.Token, temperature, effort, smart);
        var done = await Task.WhenAny(t1, t2);
        int who = done == t1 ? 1 : 2;
        var result = await done;
        var other = who == 1 ? t2 : t1;
        int writer = Volatile.Read(ref owner);
        if (writer == who || writer == 0 && result.Error == null)
        {
            (who == 1 ? c2 : c1).Cancel();
            try { await other; } catch { }
            return (result, who == 1 ? first : backup);
        }
        var second = await other;
        bool secondOk = second.Error == null || second.Text.Length > 0;
        return secondOk || writer != 0 ? (second, who == 1 ? backup : first) : (result, who == 1 ? first : backup);
    }

    /// <summary>The conversation with every tool call's signature set to Google's documented "skip the check" value.</summary>
    static JsonArray WithoutSignatureCheck(JsonArray messages)
    {
        var copy = messages.DeepClone().AsArray();
        foreach (var m in copy)
            foreach (var call in m?["tool_calls"] as JsonArray ?? new JsonArray())
                if (call is JsonObject c)
                    c["extra_content"] = new JsonObject { ["google"] = new JsonObject { ["thought_signature"] = "skip_thought_signature_validator" } };
        return copy;
    }

    /// <summary>One request to one model, counted the way Google counts it (requests, tokens, used up).</summary>
    static async Task<Turn> AskModel(string key, string id, JsonArray messages, JsonArray? tools, Action<string> onText, Action? onThinking,
        CancellationToken cancel, double? temperature, string? effort)
    {
        var body = new JsonObject { ["model"] = id, ["messages"] = Adapted(messages, id), ["stream"] = true, ["stream_options"] = new JsonObject { ["include_usage"] = true } };
        if (temperature is { } t) body["temperature"] = t;
        if (effort != null && id.StartsWith("gemini") && !id.Contains("flash-lite")) body["reasoning_effort"] = effort;
        if (tools != null && tools.Count > 0) body["tools"] = ToolsFor(tools);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double firstWord = 0;
        void Text(string piece) { if (firstWord == 0) firstWord = clock.Elapsed.TotalMilliseconds; onText(piece); }
        var turn = await Send(GeminiEndpoint, key, body, Text, onThinking, cancel, gemini: true);
        if (turn.ErrorBody is { } said) LearnLimits(said, key);
        // how fast it was (to the first word, or the whole thing when it only called tools): the faster ones go first
        if (turn.Error == null && !cancel.IsCancellationRequested) AiUsage.Took(id, firstWord > 0 ? firstWord : clock.Elapsed.TotalMilliseconds);
        // a free key can't go past the free allowance: past it without being stopped, it's a paid one
        int freeDay = AiUsage.LimitOf(id, key).Day ?? GoogleFreeLimit(Tier(id)).Day;
        if (turn.Error == null && AiUsage.PlanOf(key) == null && freeDay > 0 && AiUsage.Of(id, key).Used + 1 > freeDay) AiUsage.SetPlan(key, "paid");
        // what Google counted: a request that reached the model (one it turned away doesn't count, but says it's used up)
        if (turn.Error?.StartsWith("quota-day") == true) AiUsage.MarkFull(id, key);
        else if (turn.Error == null || !turn.Error.StartsWith("quota-minute") && !turn.Error.StartsWith("unsupported")) AiUsage.Count(id, key, turn.Tokens);
        return turn;
    }

    /// <summary>The conversation as a Gemma / Gemini model takes it (Gemma has no system role: it goes first, as the user).</summary>
    static JsonArray Adapted(JsonArray messages, string id)
    {
        var copy = messages.DeepClone().AsArray();
        if (id.StartsWith("gemma") && copy.Count > 0 && copy[0]?["role"]?.ToString() == "system")
            copy[0]!["role"] = "user";
        if (id.StartsWith("gemini"))
            foreach (var m in copy)
                foreach (var call in m?["tool_calls"] as JsonArray ?? new JsonArray())
                    if (call is JsonObject c && c["extra_content"] == null)
                        c["extra_content"] = new JsonObject { ["google"] = new JsonObject { ["thought_signature"] = "skip_thought_signature_validator" } };
        return copy;
    }

    static JsonArray ToolsFor(JsonArray tools)
    {
        var copy = tools.DeepClone().AsArray();
        foreach (var t in copy)
            if (t?["function"]?["parameters"]?["properties"] is JsonObject { Count: 0 }) t["function"]!.AsObject().Remove("parameters");
        return copy;
    }

    /// <summary>
    /// Google's errors in plain words, or a marker the chain acts on:
    ///  • quota-day / quota-minute: this model ran out, for the day or the minute (read from the allowance Google names
    ///    and how long it says to wait, not guessed: a minute's limit must never shut a model for the day);
    ///  • unsupported: this model can't take it (retired, not offered to this key, can't do tools...): the next one;
    ///  • transient: Google had a hiccup (500s, overloaded): the next one, this one rests a minute;
    ///  • signature: a model refused the conversation's tool-call signatures (they came from another model);
    ///  • region: Google's AI isn't offered where the user is; the key is wrong only when Google says so about the key.
    /// </summary>
    static string ExplainGemini(int code, string body)
    {
        string message = body, status = "";
        JsonNode? error = null;
        try
        {
            var node = JsonNode.Parse(body);
            error = node is JsonArray arr ? arr.FirstOrDefault()?["error"] : node?["error"];
            message = error?["message"]?.ToString() ?? body;
            status = error?["status"]?.ToString() ?? "";
        }
        catch { }
        if (code == 429 || status == "RESOURCE_EXHAUSTED")
            return (QuotaIsDaily(error, message) ? "quota-day " : "quota-minute ") + message;
        if (Regex.IsMatch(message, @"API key not valid|API_KEY_INVALID|API key expired|API key was reported as leaked|API key not found|invalid api key|Please pass a valid API key", RegexOptions.IgnoreCase))
            return "Google didn't accept the key. Type /key to enter it again.";
        if (Regex.IsMatch(message, @"User location is not supported|not available in your (?:country|region)", RegexOptions.IgnoreCase))
            return "region Google's AI isn't offered in your region for this key. Switch to OpenRouter in Preferences → AI chat.";
        if (Regex.IsMatch(message, @"thought.?signature", RegexOptions.IgnoreCase))
            return "signature " + message;
        if (code is 400 or 403 or 404 or 410 && Regex.IsMatch(message, @"no longer available|not available to new users|has been (?:deprecated|retired|shut down|discontinued)|is deprecated|update your code to use", RegexOptions.IgnoreCase))
            return "unsupported retired " + message;
        if (code is 400 or 404 && Regex.IsMatch(message, @"not supported|isn't supported|is not found|not found for API|does not support|Function calling is not enabled|Developer instruction|reasoning|modality|audio input|image input|not enabled for this model", RegexOptions.IgnoreCase))
            return "unsupported " + message;
        if (code == 403 || status == "PERMISSION_DENIED")
            return "unsupported denied " + message; // this model, not the key (the key itself is checked before anything is forgotten)
        if (code >= 500 || status is "UNAVAILABLE" or "INTERNAL" or "DEADLINE_EXCEEDED" || message.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
            return "quota-minute Google had a hiccup (" + code + ")";
        return message.Length > 300 ? message[..300] : message;
    }

    /// <summary>A day's allowance, or only this minute's? Google's details say which (and how long to wait).</summary>
    static bool QuotaIsDaily(JsonNode? error, string message)
    {
        bool day = false, minute = false;
        foreach (var d in error?["details"] as JsonArray ?? new JsonArray())
        {
            string type = d?["@type"]?.ToString() ?? "";
            if (type.EndsWith("QuotaFailure"))
                foreach (var v in d?["violations"] as JsonArray ?? new JsonArray())
                {
                    string q = (v?["quotaId"]?.ToString() ?? "") + " " + (v?["quotaMetric"]?.ToString() ?? "");
                    if (Regex.IsMatch(q, @"PerDay|per_day|daily", RegexOptions.IgnoreCase)) day = true;
                    if (Regex.IsMatch(q, @"PerMinute|per_minute", RegexOptions.IgnoreCase)) minute = true;
                }
            // "retry in 37s": a short wait is a minute's limit
            if (type.EndsWith("RetryInfo") && Regex.Match(d?["retryDelay"]?.ToString() ?? "", @"^(\d+(?:\.\d+)?)s$") is { Success: true } r
                && double.TryParse(r.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double secs) && secs <= 120) minute = true;
        }
        if (day) return true;
        if (minute) return false;
        if (Regex.IsMatch(message, @"per ?day|PerDay|daily", RegexOptions.IgnoreCase)) return true;
        return false; // not said: a minute (wrongly shutting a model for the day would cost far more)
    }

    /// <summary>
    /// A "used up" answer from Google lists the allowance it hit, per model ("GenerateRequestsPerDayPerProjectPerModel-
    /// FreeTier", 500): written down, so the usage card shows Google's own numbers from then on.
    /// </summary>
    static void LearnLimits(string body, string key)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var details = (node is JsonArray arr ? arr.FirstOrDefault()?["error"] : node?["error"])?["details"] as JsonArray;
            foreach (var d in details ?? new JsonArray())
            {
                if (d?["@type"]?.ToString()?.EndsWith("QuotaFailure") != true) continue;
                foreach (var v in d["violations"] as JsonArray ?? new JsonArray())
                {
                    string quota = v?["quotaId"]?.ToString() ?? v?["quotaMetric"]?.ToString() ?? "";
                    string? model = v?["quotaDimensions"]?["model"]?.ToString();
                    if (model == null || !int.TryParse(v?["quotaValue"]?.ToString(), out int value) || value <= 0) continue;
                    if (quota.Contains("Token", StringComparison.OrdinalIgnoreCase)) continue;
                    bool perDay = quota.Contains("PerDay", StringComparison.OrdinalIgnoreCase);
                    bool perMinute = quota.Contains("PerMinute", StringComparison.OrdinalIgnoreCase);
                    if (perDay || perMinute) AiUsage.LearnLimit(model, perDay, value, key);
                    // Google names the plan in the allowance it hit: "...-FreeTier", or a paid one
                    AiUsage.SetPlan(key, quota.Contains("FreeTier", StringComparison.OrdinalIgnoreCase) ? "free" : "paid");
                }
            }
        }
        catch { }
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
