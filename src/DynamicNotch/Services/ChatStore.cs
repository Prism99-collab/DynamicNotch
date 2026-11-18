using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicNotch.Services;

/// <summary>One conversation with the AI: its messages as sent to the model (the system prompt is added at send time).</summary>
public sealed class ChatSession
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
    public string Title { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public string Model { get; set; } = "";
    public JsonArray Messages { get; set; } = new();

    public int UserTurns => Messages.Count(m => m?["role"]?.ToString() == "user");
}

/// <summary>
/// The chats kept on this PC (when Preferences allow it): one small file each in %APPDATA%\DynamicNotch\chats, the
/// newest 60 kept. Nothing leaves the PC from here.
/// </summary>
public static class ChatStore
{
    public sealed record Info(string Id, string Title, DateTime Updated, int Turns, string Model);

    const int Keep = 60;
    static string Folder => Path.Combine(Settings.Folder, "chats");
    static string FileOf(string id) => Path.Combine(Folder, id + ".json");

    public static void Save(ChatSession s)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var o = new JsonObject
            {
                ["id"] = s.Id, ["title"] = s.Title, ["created"] = s.Created.ToString("o"), ["updated"] = s.Updated.ToString("o"),
                ["model"] = s.Model, ["messages"] = s.Messages.DeepClone(),
            };
            string tmp = FileOf(s.Id) + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString());
            File.Move(tmp, FileOf(s.Id), true);
            Trim();
        }
        catch (Exception ex) { AppLog.Write("chats: " + ex.Message); }
    }

    public static ChatSession? Load(string id)
    {
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(FileOf(id)));
            if (o == null) return null;
            return new ChatSession
            {
                Id = id, Title = o["title"]?.ToString() ?? "", Model = o["model"]?.ToString() ?? "",
                Created = DateTime.TryParse(o["created"]?.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var c) ? c : DateTime.Now,
                Updated = DateTime.TryParse(o["updated"]?.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var u) ? u : DateTime.Now,
                Messages = o["messages"]?.AsArray().DeepClone().AsArray() ?? new JsonArray(),
            };
        }
        catch { return null; }
    }

    /// <summary>Every kept chat, newest first (read from the files' own headers).</summary>
    public static List<Info> List()
    {
        var list = new List<Info>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var f in Directory.EnumerateFiles(Folder, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var r = doc.RootElement;
                    int turns = 0;
                    if (r.TryGetProperty("messages", out var ms))
                        foreach (var m in ms.EnumerateArray()) if (m.TryGetProperty("role", out var role) && role.GetString() == "user") turns++;
                    list.Add(new Info(Path.GetFileNameWithoutExtension(f), r.GetProperty("title").GetString() ?? "",
                        DateTime.TryParse(r.GetProperty("updated").GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var u) ? u : File.GetLastWriteTime(f),
                        turns, r.TryGetProperty("model", out var mo) ? mo.GetString() ?? "" : ""));
                }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(i => i.Updated).ToList();
    }

    /// <summary>The newest kept chat, read from that one file only (cheap enough for every empty search).</summary>
    public static Info? Latest()
    {
        try
        {
            if (!Directory.Exists(Folder)) return null;
            var newest = new DirectoryInfo(Folder).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest == null) return null;
            var s = Load(Path.GetFileNameWithoutExtension(newest.Name));
            return s == null ? null : new Info(s.Id, s.Title, s.Updated, s.UserTurns, s.Model);
        }
        catch { return null; }
    }

    /// <summary>Chats whose name or messages contain the words, newest first, with the bit that matched.</summary>
    public static List<(Info Info, string Snippet)> Search(string text)
    {
        var found = new List<(Info, string)>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var info in List())
        {
            if (words.All(w => info.Title.Contains(w, StringComparison.OrdinalIgnoreCase))) { found.Add((info, "")); continue; }
            var s = Load(info.Id);
            if (s == null) continue;
            var texts = s.Messages.Select(m => m?["shown"]?.ToString() ?? (m?["role"]?.ToString() is "user" or "assistant" ? m?["content"]?.ToString() : null)).Where(t => t != null).Cast<string>().ToList();
            string all = string.Join("\n", texts);
            if (!words.All(w => all.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            int at = all.IndexOf(words[0], StringComparison.OrdinalIgnoreCase);
            int from = Math.Max(0, at - 24);
            string snip = all.Substring(from, Math.Min(80, all.Length - from)).Replace('\n', ' ').Trim();
            found.Add((info, snip));
        }
        return found;
    }

    public static int Count => Directory.Exists(Folder) ? Directory.EnumerateFiles(Folder, "*.json").Count() : 0;

    public static void Delete(string id) { try { File.Delete(FileOf(id)); } catch { } }

    public static void DeleteAll()
    {
        try { if (Directory.Exists(Folder)) foreach (var f in Directory.EnumerateFiles(Folder)) File.Delete(f); } catch { }
    }

    static void Trim()
    {
        foreach (var old in List().Skip(Keep)) Delete(old.Id);
    }
}
