using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicNotch.Services;
using Do = DynamicNotch.Services.LauncherIntent.Do;

namespace DynamicNotch;

/// <summary>
/// "Remember…", saved the moment you say it, in any of the ways people say it (remember, save this, keep in mind,
/// don't forget, note that, retiens, n'oublie pas, souviens-toi…):
///  • how to act from now on ("remember to always answer in French", "never open Edge") → a rule, followed in every chat;
///  • something to do at a time ("remember to call mum at 6") → a reminder;
///  • something about you ("remember that I work at…") → a fact;
///  • anything else, or "remember this" alone (what was just said or answered) → a note, given back when you ask.
/// Passwords and card numbers are never kept.
/// </summary>
public partial class NotchWindow
{
    static readonly Regex RememberWords = new(@"^(?:please\s+|pls\s+|hey\s+|ok\s+)?(?:can you\s+|could you\s+|i want you to\s+)?(?<verb>remember|memorize|memorise|keep in mind|don'?t forget|do not forget|never forget|note(?: down)?|save|store|retiens|souviens[- ]toi(?: de| que)?|rappelle[- ]toi(?: de| que)?|n'?oublie pas(?: de| que)?|garde en m[ée]moire|m[ée]morise|recuerda|no olvides)\b[\s,:]*(?:that\s+|this\s+|que\s+|qu'|ça\s*:?\s*|ceci\s*:?\s*|this\s*:\s*)?(?<what>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex RuleWords = new(@"\b(always|never|from now on|whenever|every time|each time|stop\s+\w+ing|don'?t ever|by default|toujours|jamais|à partir de maintenant|désormais|chaque fois|par défaut|siempre|nunca)\b|^(?:to\s+)?(?:always|never|answer|reply|talk|speak|call me|use\s|open\s.+\s(?:instead|rather))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FactWords = new(@"^(?:my\s|i\s+(?:am|work|live|study|have|use|like|prefer|hate)\b|i'm\s|mon\s|ma\s|mes\s|je\s+(?:suis|travaille|vis|habite|pr[ée]f[èe]re)|j'(?:habite|aime|utilise))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A "remember" request, as the step to take: save a rule / fact / note, or set a reminder. Null: not one.</summary>
    (string Tool, string Args, Func<Task<string>> Act)? RememberRequest(string text, ChatSession session)
    {
        string t = text.Trim();
        if (t.Length > 1500 || t.Contains('?') && !t.Contains('\n')) return null;
        var m = RememberWords.Match(t);
        if (!m.Success) return null;
        string what = m.Groups["what"].Value.Trim().TrimEnd('.', '!');
        // "save this file as report.docx", "save the picture to my desktop": files, not memory
        if (Regex.IsMatch(m.Groups["verb"].Value, @"^(save|store|note)", RegexOptions.IgnoreCase)
            && Regex.IsMatch(what, @"\b(file|document|pdf|docx|xlsx|csv|picture|image|photo|screenshot|video|tab|page|changes?)\b|\.\w{2,4}\b|\b(as|to|in|into)\s+(?:my\s+|the\s+)?(desktop|documents|downloads|folder|drive|pc)\b", RegexOptions.IgnoreCase)) return null;
        // "remember this" / "save that" alone: what was just said or answered
        if (what.Length == 0 || Regex.IsMatch(what, @"^(it|this|that|ça|ceci|cela|le|la|les)$", RegexOptions.IgnoreCase))
        {
            what = PreviousWords(session);
            if (what.Length == 0) return null;
            return ("remember", new JsonObject { ["fact"] = Brief(what), ["kind"] = "note" }.ToJsonString(), () => Task.FromResult(KeepIt(what, "note")));
        }
        // "remember to call mum at 6": a reminder, at that time
        string remindAs = Regex.Replace(what, @"^(?:to|de|d')\s+", "", RegexOptions.IgnoreCase);
        try
        {
            if (Regex.IsMatch(what, @"^(?:to|de|d')\s", RegexOptions.IgnoreCase) && !RuleWords.IsMatch(what)
                && LauncherIntent.Parse("remind me to " + remindAs, DateTime.Now) is { Count: 1 } r && r[0] is { Kind: Do.Remind, At: not null })
                return ("do_on_pc", new JsonObject { ["command"] = "remind me to " + remindAs }.ToJsonString(), () => ToolDo("remind me to " + remindAs));
        }
        catch { }
        string kind = RuleWords.IsMatch(what) ? "rule" : FactWords.IsMatch(what) ? "fact" : "note";
        string saved = kind == "rule" ? Regex.Replace(what, @"^(?:to|que|de)\s+", "", RegexOptions.IgnoreCase) : what;
        if (kind != "note") saved = char.ToUpperInvariant(saved[0]) + saved[1..];
        return ("remember", new JsonObject { ["fact"] = Brief(saved), ["kind"] = kind }.ToJsonString(), () => Task.FromResult(KeepIt(saved, kind)));
    }

    static string Brief(string s) => s.Length > 120 ? s[..120] + "…" : s;

    /// <summary>Saved, said the way you'd want to hear it, and undoable.</summary>
    string KeepIt(string what, string kind)
    {
        string kept = AiMemory.Remember(what, kind);
        if (!kept.StartsWith("Remembered: ")) return "Error: " + kept;
        string stored = kept[12..];
        Undoable("what it remembered", () => { AiMemory.Remove(stored); return "Forgot it again"; });
        return kind switch
        {
            "rule" => "Done: from now on: " + what,
            "fact" => "Done: I'll remember that " + what,
            _ => "Done: saved. Ask “what did I tell you to remember?” any time",
        };
    }

    /// <summary>What was just said: the last answer, or else your previous message.</summary>
    static string PreviousWords(ChatSession session)
    {
        var msgs = session.Messages;
        for (int i = msgs.Count - 2; i >= 0; i--) // the last one is this request
        {
            string role = msgs[i]?["role"]?.ToString() ?? "";
            string content = msgs[i]?["shown"]?.ToString() ?? (msgs[i]?["content"] is JsonValue v && v.TryGetValue(out string? c) ? c : "");
            if (role is "assistant" or "user" && content.Trim().Length > 0) return content.Trim();
        }
        return "";
    }
}
