using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// Deep search, still fast: several searches at the same time (the question as asked, its key words with the year or
/// "official", and Wikipedia), the results merged and ranked by trust (a page found by two searches counts more), up
/// to eight pages read at once (a slow one is dropped at 4.5 s), and from each page only the passages that answer the
/// question, not its first paragraphs. Five to seven seconds in all, and the AI gets 5-7 solid sources instead of 4
/// page openings.
/// </summary>
public static partial class WebTools
{
    static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "whats", "what's", "who", "whos", "when", "where", "which", "why", "how", "is", "are", "was", "were", "the", "a", "an", "of", "to", "in", "on",
        "for", "and", "or", "do", "does", "did", "can", "could", "you", "u", "me", "my", "i", "tell", "please", "pls", "give", "show", "find", "search", "about",
        "that", "this", "it", "its", "it's", "be", "been", "there", "with", "from", "by", "at", "as", "much", "many", "used", "use", "know", "explain",
        "le", "la", "les", "de", "des", "du", "un", "une", "est", "quel", "quelle", "quels", "quelles", "qui", "quand", "comment", "pourquoi", "combien", "et", "en",
    };

    /// <summary>The words that carry the question ("price tesla model"), for searching and for finding the passages.</summary>
    public static List<string> KeyWords(string question) =>
        Regex.Matches(question.ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}'\-\.]*").Select(m => m.Value.Trim('.', '\'', '-'))
            .Where(w => w.Length > 1 && !Filler.Contains(w)).Distinct().ToList();

    static readonly Regex TimeWords = new(@"\b(latest|current|currently|now|today|this year|newest|recent|price|cost|costs|release|released|record|ranking|best|top|actuel|dernier|prix|sortie)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The deep research for a question: sources found, and the report the AI answers from.</summary>
    public static async Task<(List<Hit> Hits, string Report)> Deep(string question, CancellationToken cancel = default, int pages = 8)
    {
        var keys = KeyWords(question);
        string core = keys.Count > 0 ? string.Join(" ", keys) : question;
        string second = TimeWords.IsMatch(question) && !Regex.IsMatch(question, @"\b(19|20)\d\d\b") ? core + " " + DateTime.Now.Year : core + " official";

        // the searches, at the same time: Google (one at a time, it's a browser) for the question, the others for the rest
        var first = Safe(() => Search(question, 8, cancel));
        var other = Safe(async () =>
        {
            var b = await Brave(second, 8, cancel);
            return b.Count > 0 ? b : await DuckDuckGo(second, 8, cancel);
        });
        var wiki = Safe(() => Wikipedia(core, 2, cancel));
        // the main search is needed; the extra ones join only if they're in by then (or shortly after): never waited long for
        await first;
        if (first.Result.Count(h => h.Title != "Google answer") < 3) await other; // the main search came back thin: the second one is needed
        await Task.WhenAny(Task.WhenAll(other, wiki), Task.Delay(700, cancel).ContinueWith(_ => { }));
        if (!other.IsCompleted) other = Task.FromResult(new List<Hit>());
        if (!wiki.IsCompleted) wiki = Task.FromResult(new List<Hit>());

        // merged: a page found by two searches is surer; trusted sites first; one page per site
        var seen = new Dictionary<string, (Hit Hit, int Score)>(StringComparer.OrdinalIgnoreCase);
        void Add(List<Hit> list, int bonus)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var h = list[i];
                if (h.Url.Length == 0 || h.Url.Contains("news.google.com")) continue;
                string key = Regex.Replace(h.Url, @"[#?].*$", "").TrimEnd('/');
                int score = (Rank?.Invoke(h.Url) ?? 40) + bonus - i * 3;
                if (seen.TryGetValue(key, out var was)) seen[key] = (was.Hit, Math.Max(was.Score, score) + 15);
                else seen[key] = (h, score);
            }
        }
        var answer = first.Result.FirstOrDefault(h => h.Title == "Google answer");
        Add(first.Result.Where(h => h.Title != "Google answer").ToList(), 4);
        Add(other.Result, 0);
        Add(wiki.Result, 6);
        var ranked = seen.Values.Where(v => v.Score >= 0).OrderByDescending(v => v.Score)
            .GroupBy(v => Domain(v.Hit.Url)).Select(g => g.First().Hit).ToList();
        if (ranked.Count == 0 && answer == null) return (new(), "No results for “" + question + "”.");

        // read at the same time; the passages that answer the question kept from each
        var reads = ranked.Take(pages).Select(async h =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                cts.CancelAfter(TimeSpan.FromSeconds(4.5));
                var page = await Read(h.Url, 16000, cts.Token);
                return (Hit: h, Text: Blocked(page.Text) ? "" : Passages(page.Text, keys, 1700));
            }
            catch { return (Hit: h, Text: ""); }
        }).ToList();
        // enough good pages after 2.5 s: the slow ones aren't waited for (each still stops at 4.5 s)
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pending = new List<Task<(Hit Hit, string Text)>>(reads);
        var done = new List<(Hit Hit, string Text)>();
        while (pending.Count > 0)
        {
            var wait = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, 2500 - clock.ElapsedMilliseconds)));
            var next = await Task.WhenAny(pending.Cast<Task>().Append(wait));
            if (next is Task<(Hit Hit, string Text)> page) { pending.Remove(page); done.Add(await page); }
            if (clock.ElapsedMilliseconds >= 2500 && done.Count(d => d.Text.Length > 150) >= 5) break;
            if (next == wait && clock.ElapsedMilliseconds >= 2500 && done.Count(d => d.Text.Length > 150) >= 5) break;
        }
        // in the order they were ranked, not the order they arrived
        var read = reads.Select(r => r.IsCompletedSuccessfully ? r.Result : (Hit: (Hit?)null, Text: "")).Where(r => r.Hit != null).Select(r => (Hit: r.Hit!, r.Text)).ToList();
        var good = read.Where(r => r.Text.Length > 150).Take(7).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"DEEP RESEARCH for “{question}” ({DateTime.Now.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}): {good.Count} pages read, the passages that answer it.");
        sb.AppendLine("Rules: a figure is solid when an official source gives it or two sources agree; one ordinary source: say \"according to …\". Sources disagree: give both. Newest first when things changed. Name only these sources.");
        if (answer != null) sb.AppendLine().AppendLine("[Google's own answer box] " + answer.Snippet);
        int n = 1;
        foreach (var (h, text) in good)
            sb.AppendLine().AppendLine($"[{n++}] {Domain(h.Url)}: {h.Title}").AppendLine(text);
        foreach (var h in ranked.Where(h => !good.Any(g => g.Hit == h)).Take(4))
            if (h.Snippet.Length > 0) sb.AppendLine().AppendLine($"[{n++}] {Domain(h.Url)}: {h.Title} (summary only)\n{h.Snippet}");
        // the search engines gave little (busy, blocking): the news on it, which always answers
        if (good.Count < 3 && !cancel.IsCancellationRequested)
        {
            var news = await Safe(() => News(core, 8, cancel));
            if (news.Count > 0)
            {
                sb.AppendLine().AppendLine("Recent news on it (headlines only):");
                foreach (var h in news) sb.AppendLine($"[{n++}] {h.Title} ({h.Snippet})");
                ranked.AddRange(news);
            }
        }
        var hits = good.Select(g => g.Hit).Concat(ranked.Where(h => !good.Any(g => g.Hit == h))).ToList();
        if (answer != null) hits.Insert(0, answer);
        return (hits, sb.ToString().TrimEnd());
    }

    static async Task<List<Hit>> Safe(Func<Task<List<Hit>>> search)
    {
        try { return await search(); } catch { return new(); }
    }

    /// <summary>
    /// The parts of a page that answer the question: its paragraphs scored by the question's words they hold (numbers,
    /// years and rare words count more), the best ones kept in page order, up to <paramref name="max"/> characters.
    /// </summary>
    public static string Passages(string text, List<string> keys, int max)
    {
        var paras = Regex.Split(text, @"\n\s*\n|\n(?=[A-Z0-9•\-–])").Select(p => Regex.Replace(p, @"\s+", " ").Trim())
            .Where(p => p.Length >= 40).ToList();
        if (paras.Count == 0) return text.Length > max ? text[..max] : text;
        // long blocks are cut into sentences' worth so one huge paragraph doesn't take all the room
        paras = paras.SelectMany(p => p.Length <= 700 ? new List<string> { p } : Regex.Split(p, @"(?<=[.!?])\s+(?=[A-Z0-9])").Aggregate(new List<string> { "" }, (acc, s) =>
        {
            if (acc[^1].Length + s.Length > 600) acc.Add(s); else acc[^1] = (acc[^1] + " " + s).Trim();
            return acc;
        })).Where(p => p.Length >= 40).ToList();
        if (keys.Count == 0) return Join(paras.Take(4), max);
        var scored = paras.Select((p, i) =>
        {
            string low = p.ToLowerInvariant();
            double s = 0;
            foreach (var k in keys)
                if (low.Contains(k)) s += Regex.IsMatch(k, @"\d") ? 3 : k.Length >= 6 ? 2 : 1;
            if (Regex.IsMatch(p, @"\d")) s += 0.5;                       // facts tend to have numbers
            if (i < 3) s += 0.3;                                         // a page's opening usually says what it's about
            if (Regex.IsMatch(low, @"cookie|subscribe|sign up|newsletter|all rights reserved|privacy policy")) s -= 3;
            return (p, i, s);
        }).Where(x => x.s > 0.6).OrderByDescending(x => x.s).ToList();
        var keep = new List<(string P, int I)>();
        int used = 0;
        foreach (var (p, i, _) in scored)
        {
            if (used + p.Length > max && keep.Count > 0) continue;
            keep.Add((p, i));
            used += p.Length + 1;
            if (used >= max) break;
        }
        if (keep.Count == 0) return Join(paras.Take(3), max);
        return Join(keep.OrderBy(k => k.I).Select(k => k.P), max);
    }

    static string Join(IEnumerable<string> parts, int max)
    {
        string s = string.Join("\n", parts);
        return s.Length > max ? s[..max] + "…" : s;
    }
}
