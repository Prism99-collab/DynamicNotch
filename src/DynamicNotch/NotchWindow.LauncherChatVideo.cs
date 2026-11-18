using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Videos and live streams, for real: YouTube's own results (title, channel, live or not, the real link), never a
/// link the AI made up. Used by the youtube tool, by "play X on youtube", and by the instant route ("find me a live
/// stream of X", "watch X", "a video about X"), which opens the best one at once. And a guard for the AI's answers:
/// a deep link that no tool found and you didn't give is taken out (a made-up video ID can't reach you).
/// </summary>
public partial class NotchWindow
{
    public sealed record Video(string Id, string Title, string Channel, bool Live, string Url);

    /// <summary>YouTube's results for a search (live streams only, when asked), best first.</summary>
    static async Task<List<Video>> YouTubeFind(string query, bool live, CancellationToken cancel)
    {
        var found = new List<Video>();
        string url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query) + (live ? "&sp=EgJAAQ%253D%253D" : "");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
            using var res = await PlayHttp.SendAsync(req, cancel);
            string html = await res.Content.ReadAsStringAsync(cancel);
            foreach (Match m in Regex.Matches(html, @"""videoRenderer"":\{""videoId"":""(?<id>[\w-]{11})""(?<body>.{0,6000}?)""(?:navigationEndpoint|thumbnailOverlays)""", RegexOptions.Singleline))
            {
                string body = m.Groups["body"].Value;
                string title = Regex.Match(body, @"""title"":\{""runs"":\[\{""text"":""(?<t>(?:[^""\\]|\\.)*)""") is { Success: true } t ? Unescape(t.Groups["t"].Value) : "";
                if (title.Length == 0) continue;
                string channel = Regex.Match(body, @"""(?:ownerText|longBylineText)"":\{""runs"":\[\{""text"":""(?<c>(?:[^""\\]|\\.)*)""") is { Success: true } c ? Unescape(c.Groups["c"].Value) : "";
                bool isLive = body.Contains("BADGE_STYLE_TYPE_LIVE_NOW") || body.Contains("\"LIVE\"") || live && !body.Contains("lengthText");
                string id = m.Groups["id"].Value;
                if (found.Any(f => f.Id == id)) continue;
                found.Add(new Video(id, title, channel, isLive, "https://www.youtube.com/watch?v=" + id));
                if (found.Count >= 6) break;
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch { }
        return found;
    }

    static string Unescape(string s) { try { return Regex.Unescape(s); } catch { return s; } }

    /// <summary>The youtube tool: real results, and the best one opened (unless only a list was wanted).</summary>
    async Task<string> YouTubeTool(string query, bool live, bool open, CancellationToken cancel)
    {
        if (query.Trim().Length == 0) return "Error: what should be searched?";
        var videos = await YouTubeFind(query, live, cancel);
        // a live search that finds no stream now: the latest videos instead, and said so
        bool fellBack = false;
        if (videos.Count == 0 && live) { videos = await YouTubeFind(query + " live", false, cancel); fellBack = true; }
        if (videos.Count == 0) return $"No YouTube results for “{query}” right now.";
        var pick = live ? videos.FirstOrDefault(v => v.Live) ?? videos[0] : videos[0];
        if (open) OpenLink(pick.Url);
        var sb = new StringBuilder();
        sb.AppendLine(open ? $"Opened: {pick.Title} — {pick.Channel}{(pick.Live ? " (LIVE now)" : "")} {pick.Url}" : "Found:");
        if (fellBack) sb.AppendLine("(No live stream for that right now: these are the latest videos.)");
        foreach (var v in videos.Take(5)) sb.AppendLine($"- {v.Title} — {v.Channel}{(v.Live ? " · LIVE" : "")} · {v.Url}");
        sb.Append("These links are real: use only them.");
        return sb.ToString();
    }

    static readonly Regex WatchWords = new(@"^(?:(?:find|show|get|open|put|search|look for|play|watch|trouve|montre|mets)\s+)?(?:me\s+|moi\s+)?(?:a\s+|an\s+|some\s+|the\s+|une?\s+|des\s+)?(?:good\s+|nice\s+|great\s+|cool\s+|bon(?:ne)?\s+)?(?<kind>live\s*streams?|livestreams?|streams?|lives?|videos?|vids?|clips?|vidéos?)\s+(?:of|about|on|for|from|de|sur|du|des|d')\s*(?<q>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex WatchPlain = new(@"^(?:watch|regarde)\s+(?<q>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"find me a good live stream of the match", "a video about black holes", "watch the eclipse": what, and live or not.</summary>
    static (string Query, bool Live)? WatchRequest(string text)
    {
        string s = LauncherIntent.Clean(text);
        if (Regex.IsMatch(s, @"\b(on|in|sur)\s+spotify\b", RegexOptions.IgnoreCase)) return null;
        s = Regex.Replace(s, @"\s+(?:on|in|sur)\s+(?:youtube|yt)$", "", RegexOptions.IgnoreCase);
        if (WatchWords.Match(s) is { Success: true } m)
            return (m.Groups["q"].Value.Trim(), Regex.IsMatch(m.Groups["kind"].Value, @"live|stream", RegexOptions.IgnoreCase));
        if (WatchPlain.Match(s) is { Success: true } w && w.Groups["q"].Value.Split(' ').Length <= 8)
            return (w.Groups["q"].Value.Trim(), Regex.IsMatch(s, @"\blive\b", RegexOptions.IgnoreCase));
        return null;
    }

    // ───────────────────────────── no made-up links ─────────────────────────────

    static readonly Regex MdLink = new(@"\[(?<text>[^\]]+)\]\((?<url>https?://[^)\s]+)\)", RegexOptions.Compiled);
    static readonly Regex BareUrl = new(@"(?<![\(\w])https?://[^\s<>()\[\]""']+[^\s<>()\[\]""'.,;:!?]", RegexOptions.Compiled);

    /// <summary>Everything a link could rightly come from: what you wrote and what the tools found, in this chat.</summary>
    static string Seen(ChatSession? session)
    {
        if (session == null) return "";
        var sb = new StringBuilder();
        foreach (var m in session.Messages)
        {
            string role = m?["role"]?.ToString() ?? "";
            if (role is not ("user" or "tool")) continue;
            if (m?["content"] is JsonArray parts) foreach (var p in parts) sb.Append(p?["text"]?.ToString()).Append('\n');
            else sb.Append(m?["content"]?.ToString()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>A link it may give: one it was given or found, or a site's plain address (nothing to make up there).</summary>
    static bool Real(string url, string seen)
    {
        url = url.TrimEnd('.', ',', ')', ';');
        if (seen.Contains(url, StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        // a video link must carry an ID a search returned
        string? vid = Regex.Match(url, @"(?:v=|youtu\.be/|/shorts/|/live/)([\w-]{6,})") is { Success: true } vm ? vm.Groups[1].Value : null;
        if (vid != null) return vid.Length == 11 && seen.Contains(vid);
        string path = u.AbsolutePath.TrimEnd('/');
        if (path.Length == 0 && u.Query.Length == 0) return true;                  // just the site
        if (Regex.IsMatch(u.Host, @"youtube\.com|youtu\.be|twitch\.tv|tiktok\.com|instagram\.com|x\.com|twitter\.com")) return false; // channels and posts: only real ones
        if (seen.Contains(u.Host + u.AbsolutePath, StringComparison.OrdinalIgnoreCase)) return true;
        return path.Count(c => c == '/') <= 1 && path.Length <= 24 && u.Query.Length == 0; // a short, well-known page ("/pricing")
    }

    /// <summary>The answer with any made-up link taken out (its words stay, the link goes).</summary>
    static string KeepRealLinks(string said, ChatSession? session)
    {
        if (!said.Contains("http")) return said;
        string seen = Seen(session);
        said = MdLink.Replace(said, m => Real(m.Groups["url"].Value, seen) ? m.Value : m.Groups["text"].Value);
        said = BareUrl.Replace(said, m => Real(m.Value, seen) ? m.Value : (Uri.TryCreate(m.Value, UriKind.Absolute, out var u) ? u.Host : ""));
        return said;
    }
}
