using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Answers that are quick, clean and right:
///  • understood first: what kind of question it is (a fact to check, the news, something to find on the PC, a file
///    or a table to build, something to do, or just talk), and the AI is told exactly what a good answer looks like;
///  • a fact or the news is searched the moment you press Enter, so the AI answers in one go (not "search, then
///    answer": half the time), and it only gets the tools that kind of question needs (far fewer tokens);
///  • sources are ranked by how much they can be trusted: official sites, encyclopedias, major newsrooms first;
///    social networks and search engines' own summaries are never shown as sources.
/// </summary>
public partial class NotchWindow
{
    enum Ask { Chat, Fact, News, FindOnPc, Build, Action, Correction, HowTo, Day, Talk }

    static readonly Regex NewsWords = new(@"\b(news|headlines?|top stories|breaking|actualit[ée]s?|actus?|infos du jour|akhbar|nouvelles)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FactWordsQ = new(@"\b(who|what|when|where|which|why|how (much|many|long|old|big|tall|far)|price|cost|budget|salary|net worth|release|launch|launched|latest|current|newest|today|tonight|score|result|weather|population|ceo|president|record|version|vs\.?|versus|compare|difference|best|top \d+|statistics|stats|is it true|qui|quand|combien|quel(le)?s?|pourquoi|prix|chno|chhal|fin|imta|wach)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex MineWords = new(@"\b(my|mine|me|i|i'm|mes?|mon|ma|je|j'|dyali|dyal)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FindWords = new(@"\b(find|where('?s| is| are)|locate|look for|search (for|my)|trouve|o[uù] est|cherche)\b.*\b(files?|folders?|pdf|docs?|documents?|photos?|pictures?|images?|videos?|downloads?|downloaded|screenshots?|on (my|this) (pc|computer|laptop)|in my)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex BuildWords = new(@"\b(create|make|build|write|generate|draft|prepare|cr[ée]e|fais|r[ée]dige|g[ée]n[ée]re)\b.*\b(files?|documents?|docs?|word|docx|excel|xlsx|pdf|sheets?|spreadsheets?|reports?|cv|resume|résumé|letters?|lettre|presentations?|tables?|tableaux?|lists?|plans?|csv|notes?)\b|\b(tables?|tableau)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DoWords = new(@"^(?:(?:hey|hi|yo|hello|ok|okay|bro|salut|salam)[,!.\s]+)?(?:please\s+|pls\s+|plz\s+|can you\s+|could you\s+|can u\s+|would you\s+|i want you to\s+|i need you to\s+|go\s+)?(open|close|launch|start|play|pause|resume|stop|skip|send|message|call|set|turn|mute|unmute|switch|lock|shut|restart|reboot|install|uninstall|update|upgrade|delete|remove|move|copy|rename|click|type|go to|navigate|search for .* on|change|empty|clean|clear|enable|disable|activate|deactivate|hide|minimi[sz]e|maximi[sz]e|snap|pin|unpin|download|zip|unzip|extract|compress|organi[sz]e|tidy|connect|disconnect|kill|record|take a|increase|decrease|raise|lower|free up|flush|reset|schedule|remind|ouvre|ferme|lance|joue|mets|envoie|appelle|supprime|installe|d[ée]sinstalle|t[ée]l[ée]charge|range|trie|nettoie|vide|[ée]teins|red[ée]marre|active|d[ée]sactive|change|augmente|baisse|7ell|sedd|tfi|chegh?el)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Think carefully", "deep search", "in depth": the user wants the thorough answer, not the quick one.</summary>
    static readonly Regex ThinkWords = new(@"\b(think (hard|harder|deeply|carefully|about it|it through)|deep(ly)? (think|search|research|dive)|search (deeply|in depth|thoroughly|everywhere)|in[- ]depth|thoroughly|carefully|take your time|step by step|r[ée]fl[ée]chis|en d[ée]tail|approfondi[e]?|recherche approfondie|cherche bien)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Questions where reasoning pays: comparing, why, planning, maths, code, choosing.</summary>
    static readonly Regex HardWords = new(@"\b(why|compare|comparison|vs\.?|versus|difference between|analy[sz]e|analysis|explain (why|how)|calculate|solve|prove|equation|math|plan|strategy|should i|pros and cons|which (one|is better|should)|better than|debug|code|script|function|bug|logic|riddle|optimi[sz]e|estimate|forecast|pourquoi|comparer?|calcule[rz]?|lequel|vaut mieux)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// How much the model thinks before answering: little for chat, commands and plain facts (an answer in a couple of
    /// seconds), more for questions that need reasoning, the most when asked to think carefully. More thinking is slower,
    /// so it's spent only where it changes the answer.
    /// </summary>
    static string EffortFor(Ask kind, string text, bool detailed)
    {
        if (ThinkWords.IsMatch(text)) return "high";
        // a question answered from research: the sources do the work, so the smart model with light thinking is enough
        if (kind == Ask.Build || HardWords.IsMatch(text) && kind is not (Ask.Action or Ask.Correction or Ask.Day or Ask.News or Ask.Fact)) return "medium";
        return detailed ? "medium" : "low";
    }

    static readonly Regex HowWords = new(@"^\s*(how (do|can|could|should|would) (i|we|one|u|you)\b|how to\b|what'?s the (best |easiest |fastest )?way to\b|is there a way to\b|comment (je |on |est-ce qu'on |faire|fait-on|peut-on|puis-je)|comment\s+\w+(er|ir|re)\b|kifach|kifash|kif n(dir|9der))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>What kind of question this is (local and instant: no request spent on it).</summary>
    static Ask AskKind(string text)
    {
        string t = text.Trim();
        if (IsSmallTalk(t)) return Ask.Talk; // "hey hru today": a chat, never a search (NotchWindow.LauncherChatUnderstand)
        if (DoWords.IsMatch(t)) return Ask.Action;
        if (ScreenAsk.IsMatch(t)) return Ask.Chat; // the screen: a picture of it, not a web search
        if (HowWords.IsMatch(t)) return Ask.HowTo;
        if (Regex.IsMatch(t, @"\b(deep(ly)? (search|research)|search (deeply|in depth|thoroughly|everywhere)|research|recherche approfondie|cherche bien)\b", RegexOptions.IgnoreCase) && !MineWords.IsMatch(t) && !FindWords.IsMatch(t)) return Ask.Fact; // "deep search X": the web, thoroughly // "how do I…": the steps, and an offer to do it, not done unasked
        if (IsCorrection(t)) return Ask.Correction;
        if (DayWords.IsMatch(t)) return Ask.Day; // the calendar and the day (NotchWindow.LauncherChatHere)
        if (HereWords.IsMatch(t)) return Ask.Chat; // "this page": read from the screen, not searched on the web // "it's Brave not Chrome": about the last answer, not an order (NotchWindow.LauncherChatUnderstand)
        if (BuildWords.IsMatch(t)) return Ask.Build;
        if (FindWords.IsMatch(t)) return Ask.FindOnPc;
        if (NewsWords.IsMatch(t)) return Ask.News;
        // a fact to check: a question about the world (not about "my" calls, PC, files: the notch knows those)
        if ((FactWordsQ.IsMatch(t) || t.EndsWith('?')) && !MineWords.IsMatch(t) && t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3
            && !Regex.IsMatch(t, @"^\s*[\d\s.,%+\-*/x×÷()]+$|\d+\s*%\s*of|\bin\s+(km|miles|euros?|dollars?|usd|mad|dh)\b", RegexOptions.IgnoreCase)) return Ask.Fact;
        return Ask.Chat;
    }

    /// <summary>What a good answer to this kind of question looks like, told to the AI with the question.</summary>
    static string BriefFor(Ask kind) => kind switch
    {
        Ask.Fact => "THIS QUESTION: a fact to get right. Answer from the research below (already done for this question; search again only if it doesn't cover it). First line: the direct answer (the number, name, date), in bold. Then at most 2-3 short lines: who says it (official source or reports) and as of when. If there's no official figure, or sources disagree, say so plainly and give the range. Every number must come from the sources. Name only sources listed below, never a site or newsroom that isn't in them. No filler, no history lesson.",
        Ask.News => "THIS QUESTION: the news. Answer from the headlines below (already fetched): 5-8 short bullets, newest and most important first, each with its source name (only names listed below). No commentary.",
        Ask.FindOnPc => "THIS QUESTION: find something on this PC. Use find_files (and browser_history for downloads, list_folder for a folder). Answer with full paths (they're clickable), newest first; several matches: a table (name, folder, date, size). Never invent a path.",
        Ask.Build => "THIS QUESTION: build something (a file, a table, a list). First plan it: the right structure (title, sections, table columns with units), then fill it with real, complete content: no placeholders like [Name] or 'Lorem'. Facts in it that can change: research first. A table: clear headers, short cells, sorted sensibly. A file: write_file, then one line with its path.",
        Ask.Correction => "THIS MESSAGE is most likely about your last answer: a correction or a no. If it corrects you: say so in a few words ('You\'re right, it\'s Brave.') and give the corrected answer. A name in it is NOT an order: don't open, close, play or send anything unless it clearly asks you to do that. If it actually asks something new, answer that.",
        Ask.HowTo => "THIS QUESTION asks HOW to do something: answer with the steps (numbered, short, 3-6 lines, the exact names of menus and buttons). If it's something on this PC your tools can do, end with one line: 'Want me to do it?'. Don't do it unasked.",
        Ask.Talk => "THIS MESSAGE is small talk. Reply like a friend texting back: one or two short sentences, warm and natural, in their language and tone (slang if they use it), maybe one light question back or an offer to help. You're an AI: no made-up personal life, but be friendly ('Doing good, thanks! What's up with you?'). No lists, no bold, no sources, no definitions, no tools.",
        Ask.Day => "THIS QUESTION: the user's day or schedule. Answer from MY DAY below (the calendar tool for other days): what's next first, then today as a short timeline (time · title · place), then reminders. A briefing: also missed notifications worth knowing, and one line on yesterday. Calendar not connected: say once, in one line, how to connect it.",
        Ask.Action => "THIS QUESTION: something to do on the PC. Do what was meant, not just the words: when the message names the app to do it in ('open Brave and run a ping test', 'in Word, write…', 'on Spotify play…'), the task happens IN that app: a ping or speed test in a browser means opening a test site there (https://www.speedtest.net shows ping, download and upload), never PowerShell instead. An app that's already open is brought to the front, not started again. Do it with the right tool, check it worked, then confirm in one short line.",
        _ => "",
    };

    /// <summary>Only the tools this kind of question needs (each tool's description costs tokens, and time, on every request).</summary>
    static JsonArray? ToolsForKind(JsonArray? all, Ask kind)
    {
        if (all == null) return null;
        string[]? keep = kind switch
        {
            Ask.Fact => new[] { "research", "web_search", "read_webpage", "news", "youtube", "notch_data", "remember", "learn" },
            Ask.News => new[] { "news", "research", "read_webpage", "web_search", "remember" },
            Ask.FindOnPc => new[] { "find_files", "list_folder", "read_file", "browser_history", "open", "notch_data", "run_powershell", "remember", "learn" },
            Ask.Day => new[] { "calendar", "notch_data", "recent_notifications", "do_on_pc", "remember" },
            Ask.Build => new[] { "write_file", "read_file", "find_files", "list_folder", "research", "web_search", "read_webpage", "create_image", "notch_data", "open", "remember" },
            _ => null, // actions and talk: everything
        };
        if (keep == null) return all;
        var slim = new JsonArray();
        foreach (var t in all) if (t?["function"]?["name"]?.ToString() is { } n && keep.Contains(n)) slim.Add(t.DeepClone());
        return slim.Count > 0 ? slim : all;
    }

    /// <summary>
    /// Searched before the AI is even asked, so it answers in one go: research for a fact, the headlines for the
    /// news. The sources it found go in the answer's sources row.
    /// </summary>
    async Task<string?> SearchAhead(Ask kind, string text, CancellationToken cancel)
    {
        try
        {
            if (kind == Ask.News)
            {
                string topic = Regex.Replace(NewsWords.Replace(text, " "), @"\b(today'?s?|top|the|latest|give me|show me|what are|what's|whats|for today|du jour|d'aujourd'hui|aujourd'hui)\b|[?!.]", " ", RegexOptions.IgnoreCase);
                topic = Regex.Replace(topic, @"\s+", " ").Trim();
                var hits = await WebTools.News(topic.Length > 2 ? topic : "", 10, cancel);
                if (hits.Count == 0) return null;
                Found(WebCards(hits.Take(6), "news"));
                return "HEADLINES (just fetched):\n" + string.Join("\n", hits.Select((h, i) => $"[{i + 1}] {h.Title} — {h.Snippet}"));
            }
            var (found, report) = await WebTools.Deep(text, cancel, pages: ThinkWords.IsMatch(text) ? 10 : 8); // several searches at once, the passages that answer it
            if (found.Count == 0) return null;
            Found(WebCards(found.Take(6)));
            return "RESEARCH (just done for this question):\n" + report;
        }
        catch { return null; }
    }

    // ───────────────────────────── trusted sources ─────────────────────────────

    static readonly Regex Untrusted = new(@"(^|\.)(facebook|instagram|tiktok|pinterest|quora|threads|x|twitter|linkedin|snapchat|google|bing|duckduckgo|yahoo|msn|scribd|slideshare)\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Reference = new(@"(^|\.)(wikipedia|britannica|who\.int|un\.org|europa\.eu|worldbank|imf|oecd|nasa|nih|cdc|statista)\.|\.gov(\.|$)|\.edu(\.|$)|\.gouv\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Newsroom = new(@"(^|\.)(reuters|apnews|bbc|nytimes|theguardian|washingtonpost|bloomberg|ft|wsj|cnbc|cnn|aljazeera|lemonde|lefigaro|france24|forbes|economist|theverge|wired|arstechnica|techcrunch|engadget|ign|gamespot|polygon|eurogamer|pcgamer|theatlantic|npr|nbcnews|cbsnews|abcnews|axios|hespress|le360|medias24)\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>How much a source can be trusted for facts (higher first); below zero: not shown as a source.</summary>
    static int TrustOf(string url, string question)
    {
        string host = WebTools.Domain(url);
        if (Untrusted.IsMatch(host)) return -100;
        int score = Reference.IsMatch(host) ? 90 : Newsroom.IsMatch(host) ? 70 : Regex.IsMatch(host, @"reddit|medium|youtube|fandom|blogspot|wordpress") ? 20 : 40;
        // the subject's own site ("tesla.com" for a question about Tesla): the official word
        foreach (var w in Regex.Matches(question.ToLowerInvariant(), @"[a-z]{4,}").Select(m => m.Value))
            if (host.Replace("-", "").Contains(w)) { score += 40; break; }
        return score;
    }

    /// <summary>The answer's sources: trusted ones first, at most four shown (a "+2" opens the rest), no social networks.</summary>
    void ShowSources(ChatBubble bubble)
    {
        var ranked = _turnSources
            .Where(s => s.Target.StartsWith("http") && !s.Title.StartsWith("Google answer"))
            .Select(s => (S: s, Name: s.Kind == "news" ? s.Sub.Split(" · ")[0] : WebTools.Domain(s.Target), T: TrustOf(s.Target, _chatLastUser)))
            .Where(x => x.T >= 0)
            .GroupBy(x => x.Name).Select(g => g.First())
            .OrderByDescending(x => x.T).ToList();
        if (ranked.Count == 0) return;
        var acc = ChatAccent;
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Border Chip(ResultCard s, string name)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new Border { Width = 11, Height = 11, Margin = new Thickness(0, 0, 5, 0), Child = Favicon(s.Target, acc), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock { Text = name.Length > 22 ? name[..21] + "…" : name, FontSize = 9.5, Foreground = Frozen(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center });
            var chip = new Border
            {
                Child = line, Padding = new Thickness(5, 1, 7, 2), Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(_cyber ? 0 : 8), Cursor = Cursors.Hand, ToolTip = s.Title + "\n" + s.Target,
                Background = Frozen(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)), BorderBrush = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            };
            string target = s.Target;
            chip.MouseEnter += (_, _) => { chip.Background = Frozen(A(acc, 0x26)); chip.BorderBrush = Frozen(A(acc, 0x60)); };
            chip.MouseLeave += (_, _) => { chip.Background = Frozen(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)); chip.BorderBrush = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)); };
            chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenLink(target); };
            return chip;
        }
        foreach (var x in ranked.Take(4)) row.Children.Add(Chip(x.S, x.Name));
        if (ranked.Count > 4)
        {
            var more = new Border
            {
                Child = new TextBlock { Text = "+" + (ranked.Count - 4), FontSize = 9.5, Foreground = DimBrush }, Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 4, 4), Cursor = Cursors.Hand,
                CornerRadius = new CornerRadius(_cyber ? 0 : 8), BorderBrush = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), ToolTip = "Show the other sources",
            };
            more.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                row.Children.Remove(more);
                foreach (var x in ranked.Skip(4)) row.Children.Add(Chip(x.S, x.Name));
            };
            row.Children.Add(more);
        }
        bubble.Extras.Children.Add(row);
    }

    /// <summary>Under an answer, small and quiet: how long it took and which model (steps and tokens on hover).</summary>
    void AnswerFooter(ChatBubble bubble, string? model, TimeSpan took, int steps, int tokens = 0)
    {
        string name = model == null || model == OpenRouter.DefaultModel ? "" : model.StartsWith("groq:") ? OpenRouter.GroqName(model) : OpenRouter.IsGemini && !model.Contains('/') ? OpenRouter.GeminiName(model) : model.Contains('/') ? model[(model.IndexOf('/') + 1)..] : model;
        string brief = Regex.Replace(name, @"^Gemini\s+[\d.]+\s+|^Groq · ", "");
        string time = took.TotalSeconds < 10 ? $"{took.TotalSeconds:0.0} s" : $"{took.TotalSeconds:0} s";
        var more = new List<string>();
        if (name.Length > 0) more.Add(name);
        if (steps > 0) more.Add(steps == 1 ? "1 step" : $"{steps} steps");
        if (tokens > 0) more.Add(tokens >= 1000 ? $"{tokens / 1000.0:0.#}K tokens" : $"{tokens} tokens");
        bubble.Extras.Children.Add(new TextBlock
        {
            Text = brief.Length > 0 ? $"{time}  ·  {brief}" : time, FontSize = 9, Margin = new Thickness(0, 5, 0, 0), FontFamily = _cyber ? CyFont : UiText,
            Foreground = Frozen(Color.FromArgb(0x58, 0xFF, 0xFF, 0xFF)), ToolTip = string.Join("  ·  ", more), HorizontalAlignment = HorizontalAlignment.Left,
        });
    }

    /// <summary>Next steps that are about this question (not the PC, unless the question was), at most two.</summary>
    List<string> OnTopic(List<string> next)
    {
        bool aboutPc = PcAsk.IsMatch(_chatLastUser), aboutGear = BatteryAsk.IsMatch(_chatLastUser);
        // "open Chrome", "open Discord" after an answer are noise: only follow-ups that take the topic further
        return next.Where(n => (aboutPc || !PcAsk.IsMatch(n)) && (aboutGear || !BatteryAsk.IsMatch(n)) && !Regex.IsMatch(n, @"^\s*(open|launch|close|start|quit)\s+\S+\s*$", RegexOptions.IgnoreCase)).Take(2).ToList();
    }
}
