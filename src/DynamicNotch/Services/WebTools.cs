using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DynamicNotch.Services;

/// <summary>
/// The AI's view of the web, no keys needed: a search (Google first, then Brave and DuckDuckGo, then the news and
/// Wikipedia; results unrelated to the question are never passed on), a page read down to its
/// article (menus, footers and scripts left out), research (search and read the best pages at once, one step instead
/// of four), and the news (Google News, in your language).
/// </summary>
public static class WebTools
{
    public sealed record Hit(string Title, string Url, string Snippet, string Source = "", DateTime? When = null);

    static readonly HttpClient Http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(12) };
    const string Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    static async Task<string> Get(string url, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", Agent);
        req.Headers.TryAddWithoutValidation("Accept-Language", CultureInfo.CurrentUICulture.Name + "," + CultureInfo.CurrentUICulture.TwoLetterISOLanguageName + ";q=0.9,en;q=0.8");
        using var res = await Http.SendAsync(req, cancel);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"the page answered {(int)res.StatusCode}");
        if (res.Content.Headers.ContentType?.MediaType is { } type && !type.Contains("html") && !type.Contains("xml") && !type.Contains("text") && !type.Contains("json")) throw new HttpRequestException("not a web page (" + type + ")");
        return await res.Content.ReadAsStringAsync(cancel);
    }

    public static string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(html, @"<[^>]+>", "")).Trim();

    // ───────────────────────────── search ─────────────────────────────

    /// <summary>Google, when the notch provides it (it needs a browser engine, which lives with the window).</summary>
    public static Func<string, int, CancellationToken, Task<List<Hit>>>? Google;
    /// <summary>Which engine is asked first: "google", "brave" or "duckduckgo" (the others follow if it fails).</summary>
    public static string Engine = "google";

    public static async Task<List<Hit>> Search(string query, int max = 8, CancellationToken cancel = default)
    {
        var order = new List<Func<Task<List<Hit>>>>();
        Func<Task<List<Hit>>> google = async () => Google != null ? await Google(query, max, cancel) : new List<Hit>();
        Func<Task<List<Hit>>> brave = () => Brave(query, max, cancel);
        Func<Task<List<Hit>>> ddg = () => DuckDuckGo(query, max, cancel);
        order.AddRange(Engine switch { "brave" => new[] { brave, google, ddg }, "duckduckgo" => new[] { ddg, google, brave }, _ => new[] { google, brave, ddg } });
        foreach (var engine in order)
        {
            try { var found = Relevant(query, await engine()); if (found.Count > 0) return found; } catch { }
            if (cancel.IsCancellationRequested) return new();
        }
        return await Fallback(query, max, cancel);
    }

    static async Task<List<Hit>> Fallback(string query, int max, CancellationToken cancel)
    {
        // every engine busy or blocking: the news and Wikipedia, which always answer
        var hits = new List<Hit>();
        {
            var news = Task.Run(async () => { try { return await News(query, 5, cancel); } catch { return new List<Hit>(); } });
            var wiki = Task.Run(async () => { try { return await Wikipedia(query, 4, cancel); } catch { return new List<Hit>(); } });
            hits = Relevant(query, (await wiki).Concat(await news).ToList());
        }
        return hits;
    }

    /// <summary>Only results that share a real word with the question (a search engine that blocks robots may answer with anything).</summary>
    static List<Hit> Relevant(string query, List<Hit> hits)
    {
        var words = Regex.Matches(query.ToLowerInvariant(), @"\p{L}[\p{L}\d']{3,}|\d{4}").Select(m => m.Value.Replace("'", "")).Where(w => w is not ("what" or "when" or "where" or "which" or "with" or "from" or "that" or "this" or "the" or "about" or "today" or "latest")).ToList();
        if (words.Count == 0) return hits;
        bool Has(Hit h) { string t = (h.Title + " " + h.Snippet + " " + h.Url).ToLowerInvariant().Replace("'", ""); return words.Any(w => t.Contains(w)); }
        if (hits.FirstOrDefault()?.Title == "Google answer") return hits;
        return hits.Take(3).Any(Has) ? hits.Where(Has).Concat(hits.Where(h => !Has(h)).Take(2)).ToList() : new List<Hit>();
    }

    static async Task<List<Hit>> Wikipedia(string query, int max, CancellationToken cancel)
    {
        string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "fr" or "ar" or "es" or "de" ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : "en";
        var hits = new List<Hit>();
        foreach (var l in new[] { lang, "en" }.Distinct())
        {
            string json = await Get($"https://{l}.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit={max}&srsearch=" + Uri.EscapeDataString(query), cancel);
            foreach (var r in System.Text.Json.Nodes.JsonNode.Parse(json)?["query"]?["search"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
            {
                string title = r?["title"]?.ToString() ?? "";
                hits.Add(new Hit(title + " — Wikipedia", $"https://{l}.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')), Plain(r?["snippet"]?.ToString() ?? "")));
            }
            if (hits.Count > 0) break;
        }
        return hits;
    }

    static string Clean(string html) => Regex.Replace(Plain(html), @"\s+", " ").Trim();

    static async Task<List<Hit>> Brave(string query, int max, CancellationToken cancel)
    {
        string html = await Get("https://search.brave.com/search?q=" + Uri.EscapeDataString(query) + "&source=web", cancel);
        var hits = new List<Hit>();
        foreach (var block in Regex.Split(html, @"<div class=""snippet[^""]*"" data-pos=""\d+"" data-type=""web""").Skip(1))
        {
            var a = Regex.Match(block, @"<a href=""(https?://[^""]+)""");
            if (!a.Success || a.Groups[1].Value.Contains("search.brave.com")) continue;
            var t = Regex.Match(block, @"class=""title[^""]*""[^>]*>(.*?)</div>", RegexOptions.Singleline);
            var d = Regex.Match(block, @"class=""(?:snippet-description|content|generic-snippet)[^""]*""[^>]*>(.*?)</div>", RegexOptions.Singleline);
            hits.Add(new Hit(t.Success ? Clean(t.Groups[1].Value) : Domain(a.Groups[1].Value), WebUtility.HtmlDecode(a.Groups[1].Value), d.Success ? Clean(d.Groups[1].Value) : ""));
            if (hits.Count >= max) break;
        }
        return hits;
    }

    static async Task<List<Hit>> DuckDuckGo(string query, int max, CancellationToken cancel)
    {
        string html = await Get("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query), cancel);
        var hits = new List<Hit>();
        var found = Regex.Matches(html, @"<a[^>]*class=""result__a""[^>]*href=""(?<href>[^""]+)""[^>]*>(?<title>.*?)</a>", RegexOptions.Singleline);
        for (int i = 0; i < found.Count && hits.Count < max; i++)
        {
            var m = found[i];
            int end = i + 1 < found.Count ? found[i + 1].Index : Math.Min(html.Length, m.Index + 4000);
            var sn = Regex.Match(html[m.Index..end], @"class=""result__snippet""[^>]*>(.*?)</a>", RegexOptions.Singleline);
            string href = WebUtility.HtmlDecode(m.Groups["href"].Value);
            var uddg = Regex.Match(href, @"[?&]uddg=([^&]+)");
            if (uddg.Success) href = Uri.UnescapeDataString(uddg.Groups[1].Value);
            if (href.Contains("duckduckgo.com/y.js") || !href.StartsWith("http")) continue; // ads
            hits.Add(new Hit(Plain(m.Groups["title"].Value), href, sn.Success ? Plain(sn.Groups[1].Value) : ""));
        }
        return hits;
    }

    // ───────────────────────────── reading a page ─────────────────────────────

    /// <summary>A page's title and its main text (the article, not the menus), at most <paramref name="max"/> characters.</summary>
    public static async Task<(string Title, string Text)> Read(string url, int max = 12000, CancellationToken cancel = default)
    {
        string html = await Get(url, cancel);
        string title = Plain(Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value);
        string description = WebUtility.HtmlDecode(Regex.Match(html, @"<meta[^>]+(?:name|property)=""(?:og:)?description""[^>]+content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value);
        html = Regex.Replace(html, @"<(script|style|noscript|svg|nav|footer|header|aside|form|iframe|button|select)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<!--.*?-->", " ", RegexOptions.Singleline);
        // the article if the page marks it, else the main part, else everything
        string body = Largest(html, "article") ?? Largest(html, "main") ?? html;
        body = Regex.Replace(body, @"<(br|/p|/div|/li|/h\d|/tr|/section|/blockquote)[^>]*>", "\n", RegexOptions.IgnoreCase);
        body = Regex.Replace(body, @"<li[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        body = Regex.Replace(body, @"<h(\d)[^>]*>", "\n## ", RegexOptions.IgnoreCase);
        string text = Plain(body);
        text = Regex.Replace(text, @"[ \t ]+", " ");
        // lines worth reading: drop the short leftovers of menus and buttons
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 2).Where(l => l.Length >= 40 || l.StartsWith("## ") || l.StartsWith("- ") || Regex.IsMatch(l, @"\d"));
        text = string.Join("\n", lines);
        if (description.Length > 0 && !text.StartsWith(description[..Math.Min(40, description.Length)])) text = description + "\n" + text;
        return (title, text.Length > max ? text[..max] + "\n…" : text);
    }

    static string? Largest(string html, string tag)
    {
        var found = Regex.Matches(html, $@"<{tag}[\s>].*?</{tag}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var best = found.OrderByDescending(m => m.Length).FirstOrDefault();
        return best != null && Plain(best.Value).Length > 400 ? best.Value : null;
    }

    // ───────────────────────────── research: search and read, in one step ─────────────────────────────

    public static async Task<(List<Hit> Hits, string Report)> Research(string query, CancellationToken cancel = default)
    {
        var hits = await Search(query, 8, cancel);
        if (hits.Count == 0) return (hits, "No results for “" + query + "”. Try other words.");
        // the best few pages, read at the same time (a slow one is left out rather than waited for)
        // news links are redirects that can't be read: their headline and source are what counts
        var readable = hits.Where(h => !h.Url.Contains("news.google.com") && h.Title != "Google answer").Take(4).ToList();
        var reads = readable.Select(async h =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                cts.CancelAfter(TimeSpan.FromSeconds(7));
                var page = await Read(h.Url, 2800, cts.Token);
                return (Hit: h, Text: page.Text);
            }
            catch { return (Hit: h, Text: ""); }
        }).ToList();
        var pages = await Task.WhenAll(reads);
        var sb = new StringBuilder();
        sb.AppendLine($"Research for “{query}” ({DateTime.Now:d MMM yyyy}). Sources are numbered; use them, and say when they disagree.");
        int n = 1;
        foreach (var (h, text) in pages)
        {
            sb.AppendLine().AppendLine($"[{n++}] {h.Title} — {h.Url}");
            sb.AppendLine(text.Length > 200 ? text : (h.Snippet.Length > 0 ? h.Snippet : "(couldn't read the page)"));
        }
        foreach (var h in hits.Where(h => !readable.Contains(h))) sb.AppendLine().AppendLine($"[{n++}] {h.Title} — {h.Url}\n{h.Snippet}");
        return (hits, sb.ToString().TrimEnd());
    }

    // ───────────────────────────── news ─────────────────────────────

    /// <summary>The latest headlines (on a subject, or the top stories), from Google News in your language and country.</summary>
    public static async Task<List<Hit>> News(string query, int max = 12, CancellationToken cancel = default)
    {
        var ui = CultureInfo.CurrentUICulture;
        string lang = ui.TwoLetterISOLanguageName is "fr" or "ar" or "es" or "de" or "it" or "pt" ? ui.TwoLetterISOLanguageName : "en";
        string country = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        string ceid = $"{country}:{lang}";
        string url = query.Trim().Length > 0
            ? $"https://news.google.com/rss/search?q={Uri.EscapeDataString(query + " when:7d")}&hl={lang}&gl={country}&ceid={ceid}"
            : $"https://news.google.com/rss?hl={lang}&gl={country}&ceid={ceid}";
        string xml = await Get(url, cancel);
        var hits = new List<Hit>();
        foreach (var item in XDocument.Parse(xml).Descendants("item").Take(max))
        {
            string title = item.Element("title")?.Value ?? "";
            string source = item.Element("source")?.Value ?? "";
            if (source.Length > 0 && title.EndsWith(" - " + source)) title = title[..^(source.Length + 3)];
            DateTime? when = DateTime.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d.ToLocalTime() : null;
            hits.Add(new Hit(title, item.Element("link")?.Value ?? "", $"{source}{(when is { } w ? " · " + w.ToString("d MMM HH:mm", CultureInfo.CurrentCulture) : "")}", source, when));
        }
        return hits;
    }

    public static string Domain(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.StartsWith("www.") ? u.Host[4..] : u.Host : url;
}
