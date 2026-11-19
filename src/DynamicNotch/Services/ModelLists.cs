using System.IO;
using System.Text.Json;

namespace DynamicNotch.Services;

/// <summary>
/// The AI's model lists (Google's, Groq's), kept on disk (ai-models.json) so nothing ever waits for them: the usage card
/// and the first answer use the last known lists at once, and fresh ones are read from the internet in the background
/// (at start, then every 6 hours). A list that couldn't be read is asked for again a couple of minutes later.
/// </summary>
public static partial class OpenRouter
{
    sealed class ModelStore
    {
        public List<GeminiModel> Gemini { get; set; } = new();
        public List<GroqModel> Groq { get; set; } = new();
    }

    static readonly object ModelGate = new();
    static ModelStore? _stored;
    static Task? _geminiRefresh, _groqRefresh;
    static string ModelsFile => Path.Combine(Settings.Folder, "ai-models.json");

    static ModelStore StoredModels()
    {
        lock (ModelGate)
        {
            if (_stored != null) return _stored;
            try { if (File.Exists(ModelsFile)) _stored = JsonSerializer.Deserialize<ModelStore>(File.ReadAllText(ModelsFile)); } catch { }
            return _stored ??= new ModelStore();
        }
    }

    static void StoreModels(List<GeminiModel>? gemini, List<GroqModel>? groq)
    {
        string json;
        lock (ModelGate)
        {
            var s = StoredModels();
            if (gemini != null) s.Gemini = gemini.ToList();
            if (groq != null) s.Groq = groq.ToList();
            json = JsonSerializer.Serialize(s);
        }
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(ModelsFile + ".tmp", json);
            File.Move(ModelsFile + ".tmp", ModelsFile, true);
        }
        catch { }
    }

    /// <summary>Google's models now, without waiting: the list in memory, else the one saved, else the known names (a fresh read starts behind).</summary>
    public static List<GeminiModel> GeminiModelsNow()
    {
        bool stale = _gemini == null || DateTime.UtcNow - _geminiAt >= TimeSpan.FromHours(6);
        if (stale && HasGeminiKey && _geminiRefresh is not { IsCompleted: false }) _geminiRefresh = GeminiModels();
        if (_gemini != null) return _gemini;
        if (StoredModels().Gemini is { Count: > 0 } stored) return stored;
        return new()
        {
            new("gemini-flash-lite-latest", "Gemini Flash-Lite", true, true, 3000),
            new("gemini-flash-latest", "Gemini Flash", true, true, 2000),
            new("gemma-4-31b-it", "Gemma 4 31B", true, false, 1031),
            new("gemma-4-26b-a4b-it", "Gemma 4 26B", true, false, 1026),
        };
    }

    /// <summary>Groq's models now, without waiting (same as Google's).</summary>
    public static List<GroqModel> GroqModelsNow()
    {
        bool stale = _groq == null || DateTime.UtcNow - _groqAt >= TimeSpan.FromHours(6);
        if (stale && HasGroqKey && _groqRefresh is not { IsCompleted: false }) _groqRefresh = GroqModels();
        if (_groq != null) return _groq;
        if (StoredModels().Groq is { Count: > 0 } stored) return stored;
        return new() { new("openai/gpt-oss-120b", "GPT-OSS 120B", 300), new("openai/gpt-oss-20b", "GPT-OSS 20B", 240) };
    }

    /// <summary>Both lists read fresh (or already fresh): what the usage card waits for in the background, never in front.</summary>
    public static async Task RefreshModelLists()
    {
        var jobs = new List<Task>();
        if (HasGeminiKey) jobs.Add(_geminiRefresh is { IsCompleted: false } g ? g : _geminiRefresh = GeminiModels());
        if (HasGroqKey) jobs.Add(_groqRefresh is { IsCompleted: false } q ? q : _groqRefresh = GroqModels());
        try { await Task.WhenAll(jobs); } catch { }
    }

    /// <summary>Are the lists in memory fresh (read from the internet in the last 6 hours)?</summary>
    public static bool ModelListsFresh =>
        (!HasGeminiKey || _gemini != null && DateTime.UtcNow - _geminiAt < TimeSpan.FromHours(6)) &&
        (!HasGroqKey || _groq != null && DateTime.UtcNow - _groqAt < TimeSpan.FromHours(6));
}
