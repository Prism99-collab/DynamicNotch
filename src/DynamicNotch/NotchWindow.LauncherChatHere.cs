using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The AI works on what you're looking at, and knows your day:
///  • text selected in the app you were in when the search opened is picked up (read through Windows' accessibility,
///    no keys pressed, no clipboard touched): the chat opens with it attached and quick actions (explain, summarize,
///    fix, rewrite, translate, reply);
///  • "this page", "summarize this", "translate this article": the window behind the notch is read (a browser's page
///    as shown, even one you're signed in to; Word, Notepad, a PDF in the browser…);
///  • "what's my day": the calendar (from its private iCal address, pasted once in the chat), reminders, missed
///    notifications and yesterday's apps;
///  • 👎 on an answer keeps it (on this PC) for the AI's checks.
/// </summary>
public partial class NotchWindow
{
    (string App, string Text, DateTime At)? _hereSelection;
    /// <summary>The selection shown with its quick actions in the chat: it goes with a message only when one is
    /// clicked, or when the message is about it ("explain this", "translate it").</summary>
    (string App, string Text, DateTime At)? _hereOffered;
    int _hereStamp;

    static readonly System.Text.RegularExpressions.Regex AboutIt = new(@"\b(this|that|it|these|selected|selection|highlighted|ça|ca|ceci|cela|le texte|ce texte|la s[ée]lection)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Before a message goes: the offered selection goes with it if the message is about it; the offer ends either way.</summary>
    void TakeOfferedSelection(string text, bool chip)
    {
        if (_hereOffered is { } sel && _chatAttach == null && (chip || AboutIt.IsMatch(text) || HereWords.IsMatch(text)))
            _chatAttach = ($"Selected in {sel.App}", sel.Text);
        _hereOffered = null;
    }

    static readonly Regex BrowserApp = new(@"brave|chrome|edge|firefox|opera|vivaldi|\barc\b|browser|navigateur", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ───────────────────────────── the selected text ─────────────────────────────

    /// <summary>
    /// As the search opens: the text selected in the app that was in front, if it shares it. The focused element is
    /// asked at once (a few ms; at most 120 ms are waited, so the search never feels slower), the rest off the window's thread.
    /// </summary>
    void CaptureSelection(IntPtr fg)
    {
        _hereSelection = null;
        int stamp = ++_hereStamp;
        if (!_settings.AiChat || fg == IntPtr.Zero || fg == _hwnd) return;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return;
        var asked = Task.Run(() => { try { return AutomationElement.FocusedElement; } catch { return null; } });
        asked.Wait(120);
        string app = AppNameOf(pid);
        void Found(string text)
        {
            if (text.Trim().Length < 2) return;
            if (text.Length > 20000) text = text[..20000] + "\n…(cut)";
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (stamp != _hereStamp || !_launcherOpen || _hereSelection != null) return;
                _hereSelection = (app.Length > 0 ? app : "the app", text.Trim(), DateTime.Now);
                if (_chatMode) OfferSelection();
                else if (_launchBox is { Text.Length: 0 }) RenderStarters(); // the AI card says it's ready
            });
        }
        if (BrowserApp.IsMatch(app))
            _ = Task.Run(async () => { try { if (await _webLink.ReadPage(WebMediaLink.LinkNameOf(app), TimeSpan.FromSeconds(1.2), selectionOnly: true) is { Selected.Length: > 1 } page) Found(page.Selected); } catch { } });
        _ = Task.Run(async () =>
        {
            try
            {
                var el = await asked;
                if (el == null) return;
                string? text = await Limited(() =>
                {
                    try { if (el.Current.ProcessId != pid || el.Current.IsPassword) return null; } catch { return null; }
                    return SelectionOf(el);
                }, null, 2);
                if (text != null) Found(text);
            }
            catch { }
        });
    }

    /// <summary>The selected text of an element (or of the text area it sits in).</summary>
    static string? SelectionOf(AutomationElement? el)
    {
        for (int up = 0; up < 5 && el != null; up++)
        {
            try
            {
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out var p) && p is TextPattern tp)
                {
                    var parts = tp.GetSelection().Select(r => r.GetText(20000)).Where(s => s.Trim().Length > 0).ToList();
                    if (parts.Count > 0) return string.Join("\n", parts);
                }
                el = TreeWalker.ControlViewWalker.GetParent(el);
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>In the chat: the selection goes with the next message, with quick things to do with it.</summary>
    void OfferSelection()
    {
        if (_hereSelection is not { } sel || DateTime.Now - sel.At > TimeSpan.FromMinutes(3) || _chatStack == null) return;
        _hereSelection = null;
        _hereOffered = sel;
        ClearFollowUps();
        var acc = ChatAccent;
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        string preview = System.Text.RegularExpressions.Regex.Replace(sel.Text, @"\s+", " ");
        if (preview.Length > 46) preview = preview[..46] + "…";
        row.Children.Add(new TextBlock
        {
            Text = (_cyber ? $"SELECTED // {sel.App.ToUpperInvariant()}: " : $"Selected in {sel.App}: ") + "“" + preview + "”", FontSize = 10.5, Foreground = Frozen(A(acc, 0xC0)),
            FontFamily = _cyber ? CyFont : UiText, Margin = new Thickness(0, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "Ask about it: pick one, or type a question about it (\"translate this\")",
        });
        row.Children.Add(new Border { Width = 9999, Height = 0 }); // the quick actions on the next line
        var asks = new (string Label, string Prompt)[]
        {
            ("Explain", "Explain this simply."),
            ("Summarize", "Summarize this in a few short points."),
            ("Fix", "Fix the spelling and grammar of this. Give only the corrected text."),
            ("Rewrite", "Rewrite this to read better, same meaning and language. Give only the new text."),
            ("Translate", "Translate this to English (to French if it's already in English). Give only the translation."),
            ("Reply", "Write a short, natural reply to this message, in its language."),
        };
        int i = 0;
        foreach (var (label, prompt) in asks)
        {
            var chip = new Border
            {
                Child = new TextBlock { Text = _cyber ? label.ToUpperInvariant() : label, FontSize = 11, Foreground = Brushes.White, FontFamily = _cyber ? CyFont : UiText },
                Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(6, 0, 0, 6), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(_cyber ? 0 : 11),
                Background = Frozen(new LinearGradientBrush(A(acc, 0x22), A(AiSecond, 0x10), 0)), BorderBrush = Frozen(A(acc, 0x60)), BorderThickness = new Thickness(1),
                ToolTip = prompt, Opacity = Eco ? 1 : 0,
            };
            chip.MouseEnter += (_, _) => chip.BorderBrush = Frozen(acc);
            chip.MouseLeave += (_, _) => chip.BorderBrush = Frozen(A(acc, 0x60));
            chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; ClearFollowUps(); TakeOfferedSelection(prompt, chip: true); SendChat(prompt); };
            if (!Eco) chip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(60 * i++) });
            row.Children.Add(chip);
        }
        _followUps = row;
        _chatStack.Children.Add(row);
        FollowChat();
    }

    // ───────────────────────────── the window behind the notch ─────────────────────────────

    static readonly Regex HereWords = new(
        @"\b(this|that|the current|current|the open|my current)\s+(page|article|tab|site|website|web ?page|post|thread|email|e-mail|mail|document|doc|pdf|paper|text|paragraph|code|video|recipe|product|listing|review|essay|story|news|blog)\b"
        + @"|\b(summari[sz]e|explain|translate|rewrite|proofread|reply to|answer|check|read)\s+(this|that|it)\b"
        + @"|\bwhat i('ve| have)?\s+(selected|highlighted)\b|\b(selected|highlighted) text\b|\bthe selection\b|\bwhat'?s this (page|article|about)\b"
        + @"|\b(cette|la) page\b|\bcet article\b|\bce (texte|document|mail|pdf|site)\b|\bla s[ée]lection\b|\bce que j'ai s[ée]lectionn[ée]|\b(r[ée]sume|traduis|explique|corrige)[- ](ça|ca|ceci|le|la)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"selection", "page", or null when the message isn't about what's on screen.</summary>
    static string? HereAsk(string text)
    {
        if (!HereWords.IsMatch(text)) return null;
        return Regex.IsMatch(text, @"select|highlight|s[ée]lection", RegexOptions.IgnoreCase) ? "selection" : "page";
    }

    /// <summary>The window you were in, behind the notch: the one the search opened over, else the top one.</summary>
    IntPtr BehindNotch()
    {
        if (_launchPrevFg != IntPtr.Zero && IsWindow(_launchPrevFg)) return _launchPrevFg;
        uint me = (uint)Environment.ProcessId;
        IntPtr found = IntPtr.Zero;
        Interop.Native.EnumWindows((h, _) =>
        {
            if (!Interop.Native.IsWindowVisible(h) || IsMinimisedWindow(h) || Interop.Native.GetWindow(h, 4) != IntPtr.Zero) return true;
            string title = Interop.Native.WindowTitle(h);
            if (title.Length == 0 || title is "Program Manager" || title.Contains("Overlay", StringComparison.OrdinalIgnoreCase)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == me) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>For read_here and "summarize this page": what the window behind the notch shows.</summary>
    async Task<string> ReadHere(string what, CancellationToken cancel)
    {
        if (what == "selection" && _hereSelection is { } s) return $"SELECTED TEXT (in {s.App}):\n{s.Text}";
        if (what == "selection" && _chatAttach is { } a && a.Name.StartsWith("Selected in")) return $"SELECTED TEXT ({a.Name}):\n{a.Text}";
        var hwnd = BehindNotch();
        if (hwnd == IntPtr.Zero) return "Error: there's no window behind the notch to read.";
        GetWindowThreadProcessId(hwnd, out uint pid);
        string app = AppNameOf(pid), title = Interop.Native.WindowTitle(hwnd);
        bool browser = BrowserApp.IsMatch(app);
        // a browser: its own extension reads the tab (signed-in pages too), when it's installed
        if (browser && await _webLink.ReadPage(WebMediaLink.LinkNameOf(app), TimeSpan.FromSeconds(1.5)) is { } tab && (tab.Text.Trim().Length > 0 || tab.Selected.Length > 0))
        {
            if (what == "selection")
                return tab.Selected.Trim().Length > 0 ? $"SELECTED TEXT (in {app}, {tab.Title}):\n{tab.Selected}" : $"Nothing is selected on the page open in {app} (“{tab.Title}”).";
            return $"OPEN IN {app}: “{tab.Title}”\nAddress: {tab.Url}\n---\n{Cut(Regex.Replace(tab.Text, @"\n{3,}", "\n\n").Trim(), 20000)}";
        }
        var (url, page, selected) = await Limited(() => ReadWindow(hwnd, browser), ((string?)null, (string?)null, (string?)null), 5);
        if (what == "selection")
            return selected is { Length: > 0 } ? $"SELECTED TEXT (in {app}):\n{Cut(selected, 20000)}"
                : $"Nothing selected could be read in {app} (some apps don't share their selection). Tell the user to copy it (Ctrl+C), then use clipboard read.";
        string text = page ?? "";
        // a page Windows couldn't read (or barely): fetched from its address instead
        if (text.Trim().Length < 400 && url != null && !cancel.IsCancellationRequested)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(6));
            try { var (t, body) = await WebTools.Read(url, 20000, limit.Token); if (body.Length > text.Length && !WebTools.Blocked(body)) { text = body; if (title.Length == 0) title = t; } } catch { }
        }
        // still nothing (a signed-in page, an app that shares nothing): the words visible in the window, read by Windows
        if (text.Trim().Length < 400 && await WindowPicture(hwnd) is { } shot)
        {
            string seen = await Task.Run(() => Ocr.Bytes(shot));
            if (seen.Length > text.Trim().Length) text = "(read from what's visible on screen: only the part shown)\n" + seen;
        }
        if (text.Trim().Length == 0) return $"Error: couldn't read what {app} shows. Use see_screen to look at it instead.";
        return $"OPEN IN {app}: “{title}”{(url != null ? "\nAddress: " + url : "")}\n---\n{Cut(Regex.Replace(text, @"\n{3,}", "\n\n").Trim(), 20000)}";
    }

    /// <summary>A sharp picture of one window as it's shown (the notch steps aside for it), for reading its words.</summary>
    async Task<byte[]?> WindowPicture(IntPtr hwnd)
    {
        if (!Interop.Native.GetWindowRect(hwnd, out var r) || r.Right - r.Left < 50 || r.Bottom - r.Top < 50) return null;
        double was = Root.Opacity;
        Root.Opacity = 0;
        await Task.Delay(140);
        try
        {
            using var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return null; }
        finally { Root.Opacity = was; }
    }

    /// <summary>A window's address (browsers), its document's text, and what's selected in it.</summary>
    static (string? Url, string? Text, string? Selected) ReadWindow(IntPtr hwnd, bool browser)
    {
        string? url = null, text = null, selected = null;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (browser)
            {
                var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                foreach (AutomationElement e in edits)
                {
                    if (!e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) continue;
                    string v = ((ValuePattern)vp).Current.Value.Trim();
                    if (!Regex.IsMatch(v, @"^(https?://)?[\w-]+(\.[\w-]+)+(:\d+)?(/\S*)?$")) continue;
                    url = v.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? v : "https://" + v;
                    break;
                }
            }
            var doc = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            if (doc != null && doc.TryGetCurrentPattern(TextPattern.Pattern, out var tp) && tp is TextPattern t)
            {
                text = t.DocumentRange.GetText(80000);
                var sel = t.GetSelection().Select(r => r.GetText(20000)).Where(x => x.Trim().Length > 0).ToList();
                if (sel.Count > 0) selected = string.Join("\n", sel);
            }
            // no text from the document as a whole: the words it shows, in order (browsers share them this way)
            if (string.IsNullOrWhiteSpace(text) && doc != null)
            {
                var cache = new CacheRequest { TreeScope = TreeScope.Element };
                cache.Add(AutomationElement.NameProperty);
                AutomationElementCollection words;
                using (cache.Activate())
                    words = doc.FindAll(TreeScope.Descendants, new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink)));
                var sb = new StringBuilder();
                string last = "";
                foreach (AutomationElement w in words)
                {
                    string n = (w.Cached.Name ?? "").Trim();
                    if (n.Length == 0 || n == last) continue;
                    sb.AppendLine(n);
                    last = n;
                    if (sb.Length > 40000) break;
                }
                text = sb.ToString();
            }
            // not a browser: the app's own labels and texts (a browser's would be its buttons: its page is fetched instead)
            if (string.IsNullOrWhiteSpace(text) && !browser)
                text = string.Join("\n", Elements(hwnd).Select(x => x.Name).Where(n => n.Length > 2).Distinct().Take(400));
        }
        catch { }
        return (url, text?.Replace("￼", "").Trim(), selected);
    }

    // ───────────────────────────── your day ─────────────────────────────

    static readonly Regex DayWords = new(
        @"\b(my day|today'?s (plan|schedule|agenda|meetings?)|what'?s (on )?(my|the) (agenda|schedule|calendar|plan|day)|agenda|calendar|calendrier|my schedule|emploi du temps|meetings?|r[ée]unions?|rendez-vous|\brdv\b|appointments?|what do i have (today|tomorrow|this week|next week|on \w+)|am i (free|busy)|(free|busy) (today|tomorrow|this|on|at)|brief( me)?|briefing|morning brief|ma journ[ée]e|mon planning|qu'est-ce que j'ai (aujourd'hui|demain))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string When(CalendarFeed.Event e) => e.AllDay ? "all day" : e.End > e.Start ? $"{e.Start:HH:mm}–{e.End:HH:mm}" : $"{e.Start:HH:mm}";
    static string Line(CalendarFeed.Event e) => $"- {When(e)} {e.Title}{(e.Location.Length > 0 ? " · " + e.Location : "")}";

    const string CalendarHowTo = "Calendar: not connected. To connect it, the user pastes the calendar's private iCal address in this chat once (Google Calendar: Settings → their calendar → Integrate calendar → Secret address in iCal format; Outlook.com: Settings → Calendar → Shared calendars → Publish a calendar → ICS). It's read-only and stays encrypted on this PC.";

    /// <summary>The day at a glance: calendar, reminders, missed notifications, yesterday.</summary>
    async Task<string> MyDayText()
    {
        var sb = new StringBuilder($"MY DAY (read just now). Now: {DateTime.Now:dddd d MMMM yyyy, HH:mm}\n");
        if (CalendarFeed.Connected)
        {
            var asked = CalendarFeed.Events(DateTime.Today, DateTime.Today.AddDays(2));
            if (await Task.WhenAny(asked, Task.Delay(4000)) != asked) { sb.AppendLine("Calendar: didn't answer in time (no internet?). Say so in one line."); asked = Task.FromResult(new List<CalendarFeed.Event>()); }
            var events = await asked;
            var today = events.Where(e => e.Start.Date == DateTime.Today || e.AllDay && e.Start <= DateTime.Today && e.End > DateTime.Today).ToList();
            var tomorrow = events.Where(e => e.Start.Date == DateTime.Today.AddDays(1)).ToList();
            var next = events.FirstOrDefault(e => !e.AllDay && e.Start > DateTime.Now);
            sb.AppendLine(today.Count == 0 ? "Calendar today: nothing." : "Calendar today:\n" + string.Join("\n", today.Select(e => Line(e) + (e.End <= DateTime.Now && !e.AllDay ? " (done)" : ""))));
            if (next != null) sb.AppendLine($"Next: {next.Title} at {next.Start:HH:mm}{(next.Start.Date != DateTime.Today ? " tomorrow" : $", in {(int)(next.Start - DateTime.Now).TotalMinutes} min")}");
            if (tomorrow.Count > 0) sb.AppendLine("Tomorrow:\n" + string.Join("\n", tomorrow.Take(8).Select(Line)));
        }
        else sb.AppendLine(CalendarHowTo);
        var due = _reminders.Where(r => r.At.Date <= DateTime.Today.AddDays(1)).ToList();
        if (due.Count > 0) sb.AppendLine("Reminders: " + string.Join("; ", due.Select(r => $"{r.Text} at {r.At:ddd HH:mm}")));
        string missed = RecentToastsText();
        if (missed.Length > 0) sb.AppendLine(Cut(missed, 1200));
        sb.AppendLine(AppTimeText(DateTime.Today.AddDays(-1)).Replace("APP TIME", "YESTERDAY'S APPS"));
        return sb.ToString().TrimEnd();
    }

    /// <summary>For the calendar tool: the events of the next days (or matching a word).</summary>
    async Task<string> CalendarText(int days, string search)
    {
        if (!CalendarFeed.Connected) return CalendarHowTo;
        days = Math.Clamp(days <= 0 ? 7 : days, 1, 62);
        var events = await CalendarFeed.Events(DateTime.Today, DateTime.Today.AddDays(days));
        if (search.Trim().Length > 0) events = events.Where(e => (e.Title + " " + e.Location).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (events.Count == 0) return $"No events in the next {days} days{(search.Length > 0 ? " matching “" + search + "”" : "")}.";
        var sb = new StringBuilder($"CALENDAR, next {days} days:\n");
        foreach (var g in events.GroupBy(e => e.Start.Date))
        {
            sb.AppendLine(g.Key == DateTime.Today ? "Today" : g.Key == DateTime.Today.AddDays(1) ? "Tomorrow" : g.Key.ToString("dddd d MMMM"));
            foreach (var e in g) sb.AppendLine(Line(e));
            if (sb.Length > 6000) { sb.AppendLine("…"); break; }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>A calendar address pasted in the chat: checked, kept encrypted, never sent to the AI or kept in the chat.</summary>
    async void ConnectCalendar(string link)
    {
        if (_launchBox != null) _launchBox.Text = "";
        var bubble = ChatAssistant();
        bubble.Thinking(true, _cyber ? "LINKING CALENDAR" : "Checking the calendar");
        var (ok, name, week) = await CalendarFeed.Try(link);
        string said;
        if (ok)
        {
            CalendarFeed.Add(link);
            said = $"**Calendar connected:** {name}, {week} event{(week == 1 ? "" : "s")} in the next 7 days. Ask *what's my day?* anytime.\n\n_The link is kept encrypted on this PC and never sent to the AI. To remove it: ⋯ → Disconnect the calendar._";
        }
        else said = "That link didn't give a calendar. Use the calendar's **secret address in iCal format** (Google Calendar → Settings → your calendar → Integrate calendar).";
        bubble.Thinking(false);
        RenderMarkdown(bubble.Body, said);
        bubble.Answer = said;
        bubble.Finished?.Invoke();
        FollowChat();
    }

    // ───────────────────────────── 👎 ─────────────────────────────

    static string FeedbackFile => System.IO.Path.Combine(Settings.Folder, "ai-feedback.jsonl");

    /// <summary>A bad answer, kept on this PC (never sent anywhere) to become one of the AI's checks.</summary>
    void BadAnswer(string question, string answer)
    {
        try
        {
            if (_session?.Messages is { } msgs)
                for (int i = msgs.Count - 1; i >= 0; i--)
                {
                    if (msgs[i]?["role"]?.ToString() != "assistant" || msgs[i]?["content"]?.ToString() is not { } said || !said.Trim().StartsWith(answer.Trim()[..Math.Min(60, answer.Trim().Length)])) continue;
                    for (int j = i - 1; j >= 0; j--)
                        if (msgs[j]?["role"]?.ToString() == "user") { question = msgs[j]?["shown"]?.ToString() ?? msgs[j]?["content"]?.ToString() ?? question; break; }
                    break;
                }
            var line = new JsonObject
            {
                ["at"] = DateTime.Now.ToString("s"), ["question"] = question, ["answer"] = answer.Length > 1500 ? answer[..1500] : answer,
                ["kind"] = AskKind(question).ToString(),
            }.ToJsonString();
            System.IO.Directory.CreateDirectory(Settings.Folder);
            System.IO.File.AppendAllText(FeedbackFile, line + "\n");
            Toast("Noted: it goes into the AI's checks");
        }
        catch { Toast("Couldn't save that"); }
    }
}
