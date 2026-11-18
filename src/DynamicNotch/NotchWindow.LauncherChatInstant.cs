using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicNotch.Services;
using Do = DynamicNotch.Services.LauncherIntent.Do;

namespace DynamicNotch;

/// <summary>
/// Simple PC commands done at once, without asking the AI first: "volume 30", "mute", "next song", "dark mode on",
/// "timer 10 min", "open spotify and discord", "remind me to call mum at 6". The notch's own command reader knows
/// these; when every part is clear, safe, and can be done right now, it's done in a blink, shown as a step, and
/// answered in one line. Anything else (a question, a name it isn't sure of, something inside an app, power,
/// deleting) goes to the AI as before.
/// </summary>
public partial class NotchWindow
{
    static readonly HashSet<Do> InstantKinds = new()
    {
        Do.Volume, Do.Brightness, Do.Media, Do.Wifi, Do.Bluetooth, Do.Airplane, Do.DarkMode, Do.Focus, Do.Timer, Do.Remind,
        Do.Screenshot, Do.DesktopIcons, Do.Open, Do.Close,
    };

    static readonly Regex NotACommand = new(@"^\s*(what|what's|whats|why|how|who|when|where|which|is|are|was|does|do i|did|can i|should|explain|tell|show me|find|search|look|summari[sz]e|write|draft|make|create|help|give|list|compare|translate|call|message|send|text|reply|email|play\s+\S+\s+on)\b|\b(on|in|inside|from)\s+(discord|teams|whatsapp|spotify|slack|telegram|chrome|brave|edge|firefox|youtube)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Is the app name it read surely the one you meant? ("spot" → Spotify yes; "discord and call sam" → no)</summary>
    bool SureName(LauncherIntent.Intent t)
    {
        if (t.Text is not { Length: > 1 } text) return false;
        var names = t.Op == "switch" ? new List<string> { text } : OpenNames(text);
        if (names.Count == 0) return false;
        foreach (var n in names)
        {
            var e = ResolveEntry(n);
            if (e == null) return false;
            string a = Norm(n), b = Norm(e.Name);
            if (a.Length < 2 || !(b.StartsWith(a) || b.Contains(a) && a.Length >= 4 || a == b)) return false;
        }
        return true;
    }

    /// <summary>Done at once when it's a plain, safe command; false hands it to the AI.</summary>
    async Task<bool> TryInstant(string text, ChatSession session)
    {
        // "find me a live stream of X", "a video about X", "watch X": YouTube's real results, the best one opened
        if (_settings.AiTools && text.Length <= 120 && !text.Contains('?') && WatchRequest(text) is { } watch)
            return await RunInstant(session, "youtube", new JsonObject { ["query"] = watch.Query, ["live"] = watch.Live }.ToJsonString(), () => YouTubeTool(watch.Query, watch.Live, open: true, CancellationToken.None));
        // "play X (on spotify / youtube)": straight there, checked (NotchWindow.LauncherChatPlay)
        if (_settings.AiTools && text.Length <= 90 && !text.Contains('?') && PlayRequest(text) is { } play)
            return await RunInstant(session, "play_music", new JsonObject { ["query"] = play.Query, ["where"] = play.Where }.ToJsonString(), () => PlayMusic(play.Query, play.Where));
        if (!_settings.AiTools || text.Length > 90 || text.Contains('?') || text.Contains('\n') || NotACommand.IsMatch(text)) return false;
        IReadOnlyList<LauncherIntent.Intent> list;
        try { list = LauncherIntent.Parse(text, DateTime.Now); } catch { return false; }
        if (list.Count == 0) return false;
        foreach (var t in list)
        {
            if (!InstantKinds.Contains(t.Kind)) return false;
            if (t.Kind == Do.Media && t.Text is { Length: > 0 }) return false;   // "play X": a search, the AI picks where
            if (t.Kind == Do.Remind && t.At == null) return false;
            if (t.Kind is Do.Open or Do.Close && !SureName(t)) return false;
            var v = Describe(t);
            if (!v.Ready && !v.Done) return false;
        }

        return await RunInstant(session, "do_on_pc", new JsonObject { ["command"] = text }.ToJsonString(), () => ToolDo(text));
    }

    /// <summary>One step done at once, shown as a step, answered in one line, kept in the chat.</summary>
    async Task<bool> RunInstant(ChatSession session, string tool, string args, Func<Task<string>> act)
    {
        _chatBusy = true;
        _askedAt = DateTime.Now;
        ChatKeys();
        var started = DateTime.Now;
        try
        {
            var step = ChatStep(tool, args);
            _pendingUndo = null;
            string result;
            try { result = await act(); } catch (Exception ex) { result = "Error: " + ex.Message; }
            if (_pendingUndo is { } u && !result.StartsWith("Error")) AttachUndo(step, u);
            _pendingUndo = null;
            bool ok = !result.StartsWith("Error") && !result.Contains(": couldn't") && !result.Contains("didn't show up") && !result.Contains("couldn't be started");
            step.Done(ok, null);
            if (ok && _settings.AiMemory && _chatLastUser.Length > 0) AiLearn.Record(_chatLastUser, new[] { (tool, args, true) }, (DateTime.Now - started).TotalSeconds); // habits learn from these too

            // the answer, in one line
            if (tool == "youtube" && Regex.Match(result, @"^Opened: (?<what>.+?) (?<url>https://www\.youtube\.com/watch\?v=[\w-]{11})") is { Success: true } yt)
                result = "Done: " + yt.Groups["what"].Value + "\n" + yt.Groups["url"].Value;
            var parts = result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("- ") && !l.StartsWith("These links")).Select(l =>
                l.StartsWith("Done: ") ? $"**{l[6..].Trim()}**" :
                Regex.Match(l, @"^(.+?): already so \((.+)\)$") is { Success: true } m ? $"{m.Groups[1].Value}: already {m.Groups[2].Value}" :
                Regex.Match(l, @"^(.+?): couldn't \((.+)\)$") is { Success: true } c ? $"Couldn't: {c.Groups[1].Value} ({c.Groups[2].Value})" :
                l.StartsWith("Error: ") ? l[7..] : l.StartsWith("https://") ? "\n" + l : l).ToList();
            string reply = parts.Count == 0 ? "Done." : string.Join("  ·  ", parts);
            var bubble = ChatAssistant();
            bubble.Thinking(false);
            RenderMarkdown(bubble.Body, reply);
            bubble.Answer = reply;
            AnswerFooter(bubble, "⚡ Instant, on this PC", DateTime.Now - started, 0);
            bubble.Finished?.Invoke();
            _chatLastAnswer = reply;
            session.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply });
            AnswerReady(reply, acted: true);
        }
        finally
        {
            _chatBusy = false;
            session.Updated = DateTime.Now;
            if (_settings.AiHistory && session.UserTurns > 0)
            {
                var copy = new ChatSession { Id = session.Id, Title = session.Title, Created = session.Created, Updated = session.Updated, Model = session.Model, Messages = ForDisk(session.Messages) };
                _ = Task.Run(() => ChatStore.Save(copy));
            }
            ChatKeys();
            FollowChat();
            if (_chatQueue.Count > 0)
            {
                string next = _chatQueue.Dequeue();
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => SendChat(next));
            }
        }
        return true;
    }
}
