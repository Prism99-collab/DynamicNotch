using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The Work page, second in the control center. Three tabs:
///  • Now: what you're on (a document, a PDF, a page, code), how long today, its project, and the things worth doing
///    with it (summarize, check, explain, chart, translate…), answered right on the card; or ask anything about it;
///  • Projects: what you work on, grouped by itself from what you use together; pin, rename, set a stage, drag a
///    file or a tab from one project to another (or a file in from Explorer), reopen everything in one click, or ask
///    a question about the whole project (its documents, PDFs and pages read together);
///  • Inbox: people waiting on an answer (with a reply drafted on request, done, or later), and every notification of
///    the last two weeks, by day, by app, searchable.
/// The AI is only asked when you click; the rest is local and instant.
/// </summary>
public partial class NotchWindow
{
    const int WorkPage = 7;
    Grid? _workPage, _workBody;
    string? _workBuiltFor;
    int _workTab;
    TextBlock? _workNote;
    readonly List<(Border Hit, TextBlock Label, Border Line)> _workTabs = new();
    TextBlock? _inboxBadge, _workTitle;
    readonly List<TextBlock> _workGlyphs = new();
    CancellationTokenSource? _workAsk;
    string _inboxView = "waiting", _inboxApp = "", _inboxFind = "";
    readonly HashSet<string> _inboxOpen = new();
    bool _workTyping;
    IntPtr _workTypingPrev;
    readonly Dictionary<string, (string Q, string Md, DateTime At)> _nowAnswers = new();
    string? _nowShownKey;
    bool _workAskRunning;

    Color WorkAcc => _cyber ? CyCyan : ChatAccent;
    Color WorkHot => _cyber ? CyYellow : Lighten(ChatAccent, 0.35);
    FontFamily WorkFont => _cyber ? CyFont : UiText;
    string Cap(string s) => _cyber ? s.ToUpperInvariant() : s;

    Grid BuildWorkPage()
    {
        _workPage = new Grid { Width = PageWidth, Height = 216 };
        Deactivated += (_, _) => { if (_workTyping) EndWorkTyping(giveBack: false); }; // a click elsewhere: the keyboard is that app's again
        RebuildWorkPage();
        return _workPage;
    }

    void RebuildWorkPage()
    {
        if (_workPage == null) return;
        _workBuiltFor = Skin.Key + "|" + _accent;
        _workPage.Children.Clear();
        _workTabs.Clear();
        _workGlyphs.Clear();
        var acc = WorkAcc;
        // a slim rail of three tiles on the left, the tab's own space on the right
        var root = new Grid { Margin = new Thickness(10, 2, 12, 4) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var rail = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        var railFrame = new Border
        {
            Child = rail, Padding = new Thickness(4), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top,
            Background = Frozen(_cyber ? Color.FromArgb(0xB0, 0x02, 0x08, 0x0C) : Color.FromArgb(0x1C, 0x00, 0x00, 0x00)),
            BorderBrush = Frozen(_cyber ? A(CyCyan, 0x45) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(_cyber ? 0 : 14),
        };
        if (_cyber) NeonCorner(railFrame, 7, 7);
        (string Name, string Glyph)[] tabs = { ("Now", ""), ("Projects", ""), ("Inbox", "") };
        for (int i = 0; i < tabs.Length; i++)
        {
            int idx = i;
            var glyph = new TextBlock { Text = tabs[i].Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center };
            var label = new TextBlock { Text = Cap(tabs[i].Name), FontFamily = WorkFont, FontSize = _cyber ? 8.5 : 9, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            var line = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-4, 6, 0, 6), Opacity = 0 };
            var cell = new Grid();
            cell.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { glyph, label } });
            cell.Children.Add(line);
            if (i == 2)
            {
                _inboxBadge = new TextBlock { FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Frozen(_cyber ? Colors.Black : Colors.White), FontFamily = WorkFont, HorizontalAlignment = HorizontalAlignment.Center };
                cell.Children.Add(new Border
                {
                    Child = _inboxBadge, Background = Frozen(_cyber ? CyYellow : Color.FromRgb(0xFF, 0x5A, 0x5F)), CornerRadius = new CornerRadius(_cyber ? 0 : 7), MinWidth = 14, Height = 14,
                    Padding = new Thickness(3, 0, 3, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -2, 0),
                });
            }
            var hit = new Border { Child = cell, Height = 52, Margin = new Thickness(0, 0, 0, i < tabs.Length - 1 ? 4 : 0), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(_cyber ? 0 : 11), Background = Brushes.Transparent, ToolTip = tabs[i].Name };
            hit.MouseEnter += (_, _) => { if (idx != _workTab) hit.Background = Frozen(_cyber ? A(CyCyan, 0x12) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)); };
            hit.MouseLeave += (_, _) => { if (idx != _workTab) hit.Background = Brushes.Transparent; };
            hit.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
            hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowWorkTab(idx); };
            _workTabs.Add((hit, label, line));
            _workGlyphs.Add(glyph);
            rail.Children.Add(hit);
        }
        root.Children.Add(railFrame);

        // the tab's title and a line for what just happened, then its content
        var side = new Grid();
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        side.RowDefinitions.Add(new RowDefinition());
        var head = new Grid();
        _workTitle = new TextBlock { FontFamily = WorkFont, FontSize = _cyber ? 13 : 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Frozen(_cyber ? CyYellow : Colors.White) };
        head.Children.Add(_workTitle);
        _workNote = new TextBlock { FontSize = 10, FontFamily = WorkFont, Foreground = Frozen(A(acc, 0xD0)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190, Opacity = 0 };
        head.Children.Add(_workNote);
        side.Children.Add(head);
        _workBody = new Grid { Margin = new Thickness(0, 2, 0, 0), ClipToBounds = true };
        Grid.SetRow(_workBody, 1);
        side.Children.Add(_workBody);
        Grid.SetColumn(side, 1);
        root.Children.Add(side);
        _workPage.Children.Add(root);
        RenderWorkTab(animate: false);
    }

    void OnWorkShown()
    {
        // the first time: today's notifications from before the history existed go in it
        LoadTodaysToasts();
        List<(DateTime, string, string, string)> earlier;
        lock (_recentToasts) earlier = _recentToasts.ToList();
        NotifHistory.ImportOnce(earlier);
        if (_workBuiltFor != Skin.Key + "|" + _accent) RebuildWorkPage();
        else RenderWorkTab(animate: true);
        if (_cyber && _workTitle != null) CyberDecode(_workTitle);
    }

    void ShowWorkTab(int tab)
    {
        if (tab == _workTab) { RenderWorkTab(animate: false); return; }
        _workTab = tab;
        EndWorkTyping(giveBack: true);
        RenderWorkTab(animate: true);
        if (_cyber) { CyberGlitch(strong: false, light: true); if (_workTitle != null) CyberDecode(_workTitle); }
    }

    /// <summary>A short line at the top right ("Reopened 5"), for a few seconds.</summary>
    void WorkSay(string text)
    {
        if (_workNote == null) return;
        _workNote.Text = Cap(text);
        _workNote.BeginAnimation(OpacityProperty, null);
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(3.2) };
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Eco ? 0 : 160))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.6))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.2))));
        _workNote.BeginAnimation(OpacityProperty, a);
    }

    void RenderWorkTab(bool animate)
    {
        if (_workBody == null) return;
        for (int i = 0; i < _workTabs.Count; i++)
        {
            var (hit, label, line) = _workTabs[i];
            bool on = i == _workTab;
            var tone = Frozen(on ? (_cyber ? CyYellow : Colors.White) : A(_cyber ? CyCyan : Colors.White, (byte)(_cyber ? 0x90 : 0x80)));
            label.Foreground = tone;
            if (i < _workGlyphs.Count) _workGlyphs[i].Foreground = tone;
            line.Background = Frozen(_cyber ? CyYellow : WorkAcc);
            line.Opacity = on ? 1 : 0;
            hit.Background = !on ? Brushes.Transparent : _cyber ? Frozen(A(CyYellow, 0x16)) : Frozen(new LinearGradientBrush(A(WorkAcc, 0x55), A(AiSecond, 0x30), 90));
            hit.BorderBrush = Frozen(on && _cyber ? A(CyYellow, 0x60) : Colors.Transparent);
            hit.BorderThickness = new Thickness(on && _cyber ? 1 : 0);
        }
        if (_workTitle != null)
        {
            string[] titles = { "Now", "Projects", "Inbox" };
            _workTitle.Text = _cyber ? titles[_workTab].ToUpperInvariant() + " //" : titles[_workTab];
        }
        int waiting = NotifHistory.Waiting().Count;
        if (_inboxBadge?.Parent is Border b) { _inboxBadge.Text = waiting.ToString(); b.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed; }

        _workBody.Children.Clear();
        var content = _workTab switch { 0 => BuildNowTab(), 1 => BuildProjectsTab(), _ => BuildInboxTab() };
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
        _workBody.Children.Add(scroll);
        if (animate && !Eco)
        {
            if (_cyber) GlitchIn(scroll);
            else
            {
                var rise = new TranslateTransform(0, 6);
                scroll.RenderTransform = rise;
                rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                scroll.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            }
        }
    }

    // ───────────────────────────── shared pieces ─────────────────────────────

    /// <summary>A card in the page's look: neon cut corner and a lit edge, or soft glass.</summary>
    Border WorkCard(UIElement child, bool hot = false, Thickness? margin = null)
    {
        var acc = WorkAcc;
        var card = new Border
        {
            Child = child, Padding = new Thickness(12, 9, 12, 10), Margin = margin ?? new Thickness(0, 0, 0, 8),
            Background = Frozen(_cyber ? new LinearGradientBrush(Color.FromArgb(0xC8, 0x04, 0x0C, 0x12), Color.FromArgb(0x90, 0x08, 0x10, 0x16), 0) : new LinearGradientBrush(A(acc, 0x1A), Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0)),
            BorderBrush = Frozen(_cyber ? A(hot ? CyYellow : CyCyan, hot ? (byte)0x90 : (byte)0x55) : A(acc, hot ? (byte)0x70 : (byte)0x30)),
            BorderThickness = _cyber ? new Thickness(hot ? 2 : 1, 1, 1, 1) : new Thickness(1), CornerRadius = new CornerRadius(_cyber ? 0 : 11),
        };
        if (_cyber) NeonCorner(card, 8, 0);
        return card;
    }

    /// <summary>A small badge for what something is (DOC, XLS, PDF, WEB, CODE).</summary>
    FrameworkElement KindBadge(string kind)
    {
        (string text, Color color) = kind switch
        {
            "doc" => ("DOC", Color.FromRgb(0x5B, 0x9B, 0xFF)),
            "sheet" => ("XLS", Color.FromRgb(0x4C, 0xC3, 0x7A)),
            "slides" => ("PPT", Color.FromRgb(0xFF, 0x8A, 0x4C)),
            "pdf" => ("PDF", Color.FromRgb(0xFF, 0x5A, 0x5F)),
            "code" => ("CODE", Color.FromRgb(0xC7, 0x8B, 0xFF)),
            "tab" => ("WEB", _cyber ? CyCyan : Color.FromRgb(0x6F, 0xD6, 0xFF)),
            _ => ("APP", Color.FromRgb(0xB0, 0xB0, 0xB8)),
        };
        if (_cyber) color = kind == "tab" ? CyCyan : kind == "pdf" ? CyRed : kind == "doc" ? CyYellow : color;
        return new Border
        {
            Child = new TextBlock { Text = text, FontSize = 7.5, FontWeight = FontWeights.Bold, Foreground = Frozen(color), FontFamily = _cyber ? CyFont : UiText },
            BorderBrush = Frozen(A(color, 0x90)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(_cyber ? 0 : 3), Padding = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center, Background = Frozen(A(color, 0x18)),
        };
    }

    /// <summary>A small action ("Summarize"): a pill, or a neon tag.</summary>
    Border WorkChip(string label, Action act, string? tip = null, bool strong = false)
    {
        var acc = WorkAcc;
        var text = new TextBlock { Text = Cap(label), FontSize = 11.5, FontFamily = WorkFont, VerticalAlignment = VerticalAlignment.Center, Foreground = Frozen(strong ? (_cyber ? Colors.Black : Colors.White) : Lighten(acc, 0.3)), FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal };
        var chip = new Border
        {
            Child = text, Padding = new Thickness(11, 4, 11, 5), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(_cyber ? 0 : 11), ToolTip = tip, MinHeight = 24,
            Background = Frozen(strong ? (_cyber ? CyYellow : A(acc, 0xC0)) : A(acc, 0x16)), BorderBrush = Frozen(strong ? Colors.Transparent : A(acc, 0x55)), BorderThickness = new Thickness(1),
        };
        chip.MouseEnter += (_, _) => { if (!strong) chip.BorderBrush = Frozen(_cyber ? CyYellow : acc); };
        chip.MouseLeave += (_, _) => { if (!strong) chip.BorderBrush = Frozen(A(acc, 0x55)); };
        chip.PreviewMouseLeftButtonDown += (_, e) => { _maybeSwipe = false; };
        chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; try { act(); } catch { WorkSay("Couldn't do that"); } };
        return chip;
    }

    TextBlock WorkText(string text, double size = 11, Color? color = null, bool bold = false, bool wrap = true) => new()
    {
        Text = text, FontSize = size, FontFamily = WorkFont, Foreground = Frozen(color ?? TextTone), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    static string Spent(int seconds) => seconds >= 3600 ? $"{seconds / 3600} h {seconds % 3600 / 60:00}" : seconds >= 60 ? $"{seconds / 60} min" : "<1 min";

    static string WorkAgo(DateTime at)
    {
        var d = DateTime.Now - at;
        return d.TotalMinutes < 1 ? "now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago" : d.TotalHours < 24 && at.Date == DateTime.Today ? at.ToString("HH:mm") : at.Date == DateTime.Today.AddDays(-1) ? "yesterday " + at.ToString("HH:mm") : at.ToString("ddd d MMM");
    }

    // ───────────────────────────── typing on the page ─────────────────────────────

    /// <summary>The notch takes the keyboard for a box on the page (and gives it back after).</summary>
    void BeginWorkTyping(TextBox box)
    {
        if (!_workTyping)
        {
            var fg = GetForegroundWindow();
            _workTypingPrev = fg != _hwnd ? fg : IntPtr.Zero;
            _workTyping = true;
            UpdateTypeToSearch(false); // the keys go to the box, never to a new search
            SetActivatable(true);
            TakeForeground();
            Activate();
        }
        box.Focus();
        Keyboard.Focus(box);
    }

    void EndWorkTyping(bool giveBack)
    {
        if (!_workTyping) return;
        _workTyping = false;
        if (!_launcherOpen) SetActivatable(false);
        if (giveBack && _workTypingPrev != IntPtr.Zero && IsWindow(_workTypingPrev)) SetForegroundWindow(_workTypingPrev);
        _workTypingPrev = IntPtr.Zero;
    }

    /// <summary>A one-line box to type in (a question, a name, a note): Enter sends, Esc leaves.</summary>
    TextBox WorkBox(string hint, Action<string> done, string start = "")
    {
        var acc = WorkAcc;
        var box = new TextBox
        {
            Text = start, FontSize = 11.5, FontFamily = WorkFont, Foreground = Frozen(TextTone), CaretBrush = Frozen(_cyber ? CyYellow : Colors.White), Background = Frozen(_cyber ? Color.FromArgb(0x90, 0x00, 0x00, 0x00) : Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            BorderBrush = Frozen(A(acc, 0x60)), BorderThickness = new Thickness(_cyber ? 0 : 1, _cyber ? 0 : 1, _cyber ? 0 : 1, 1), Padding = new Thickness(9, 5, 9, 6), FocusVisualStyle = null, Tag = hint,
            SelectionBrush = Frozen(acc),
        };
        var ghost = new TextBlock { Text = hint, FontSize = 11, FontFamily = WorkFont, Foreground = Frozen(A(TextTone, 0x70)), IsHitTestVisible = false, Margin = new Thickness(11, 6, 0, 0) };
        box.PreviewMouseLeftButtonDown += (_, _) => { _maybeSwipe = false; BeginWorkTyping(box); };
        box.TextChanged += (_, _) => ghost.Visibility = box.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; string t = box.Text.Trim(); if (t.Length > 0) { EndWorkTyping(giveBack: true); done(t); } }
            else if (e.Key == Key.Escape) { e.Handled = true; EndWorkTyping(giveBack: true); }
        };
        box.Loaded += (_, _) => { if (box.Parent is Grid g && !g.Children.Contains(ghost)) { g.Children.Add(ghost); ghost.Visibility = box.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible; } };
        return box;
    }

    Grid BoxHost(TextBox box) { var g = new Grid(); g.Children.Add(box); return g; }

    // ───────────────────────────── the AI, on a card ─────────────────────────────

    /// <summary>
    /// A question answered right on the card (streamed in), from the text it's about. The answer stays there, and
    /// goes to the chat with one click to carry on.
    /// </summary>
    /// <summary>An answer on a card: what was asked, the answer, and Copy · Continue in chat · Clear.</summary>
    Border AnswerBlock(string asked, string md, Action? cleared, bool live = false)
    {
        var acc = WorkAcc;
        var inner = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        head.Children.Add(WorkText((_cyber ? "> " : "") + (asked.Length > 70 ? asked[..70] + "…" : asked), 10, _cyber ? CyYellow : Lighten(acc, 0.35), bold: true, wrap: false).Also(t => t.Margin = new Thickness(0, 0, 22, 0)));
        var x = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = Frozen(A(TextTone, 0x80)), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Clear this answer", Padding = new Thickness(4, 2, 2, 2), Background = Brushes.Transparent };
        head.Children.Add(x);
        inner.Children.Add(head);
        var box = MessageBox();
        box.FontSize = 11.5;
        if (md.Length > 0) RenderMarkdown(box, md);
        box.Tag = "body";
        inner.Children.Add(box);
        var block = new Border
        {
            Child = inner, Tag = "answer", Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(8, 5, 8, 6), CornerRadius = new CornerRadius(_cyber ? 0 : 8),
            Background = Frozen(_cyber ? Color.FromArgb(0x80, 0x00, 0x00, 0x00) : Color.FromArgb(0x22, 0x00, 0x00, 0x00)),
            BorderBrush = Frozen(A(acc, _cyber ? (byte)0x60 : (byte)0x30)), BorderThickness = new Thickness(_cyber ? 2 : 1, _cyber ? 0 : 1, _cyber ? 0 : 1, _cyber ? 0 : 1),
        };
        void Clear()
        {
            _workAsk?.Cancel();
            cleared?.Invoke();
            if (block.Parent is Panel p) p.Children.Remove(block);
        }
        x.MouseLeftButtonUp += (_, e) => { e.Handled = true; Clear(); };
        if (!live && md.Length > 0) inner.Children.Add(AnswerButtons(asked, md, Clear));
        return block;
    }

    FrameworkElement AnswerButtons(string asked, string md, Action clear) =>
        SmallButtons(("Copy", () => { CopyText(md, "Copied"); WorkSay("Copied"); }),
            ("Continue in chat", () => ContinueInChat(asked, md)),
            ("Clear", clear));

    async void AskOnCard(StackPanel host, string asked, string question, Func<CancellationToken, Task<string>> context, Action<string>? keep = null, Action? cleared = null)
    {
        _workAsk?.Cancel();
        var cts = _workAsk = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(45)); // never a card that thinks forever
        foreach (var old in host.Children.OfType<FrameworkElement>().Where(c => Equals(c.Tag, "answer")).ToList()) host.Children.Remove(old);
        var wrapBorder = AnswerBlock(asked, "", cleared, live: true);
        var inner = (StackPanel)wrapBorder.Child;
        var box = (System.Windows.Controls.RichTextBox)inner.Children.OfType<FrameworkElement>().First(c => Equals(c.Tag, "body"));
        var thinking = WorkText(_cyber ? "PROCESSING…" : "Thinking…", 10.5, A(WorkAcc, 0xC0));
        thinking.FontStyle = _cyber ? FontStyles.Normal : FontStyles.Italic;
        inner.Children.Insert(1, thinking);
        var wrap = inner;
        host.Children.Add(wrapBorder);
        FollowWork(wrapBorder);
        if (!Eco) thinking.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

        string answer;
        _workAskRunning = true;
        try
        {
            PrepareAi();
            if (!OpenRouter.HasKey || OpenRouter.LoadKey() is not { } key) answer = "Connect the AI first: open the chat (Alt+Space, then Tab) and add a free key.";
            else
            {
                string ctx = await context(cts.Token);
                var msgs = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = $"You help the user with what they're working on, answering in a small panel. The question is about the thing described below (a document, a page, a message…), never about this panel or the app it's in; if its text couldn't be read, answer from what you know about it by its name and say so in a few words. Today is {DateTime.Now:dddd d MMMM yyyy}. {SoundHuman} Be useful and short: a few lines or a short list, no preamble. {ChartRule}\n\nWHAT IT'S ABOUT:\n{(ctx.Length > 24000 ? ctx[..24000] + "\n…(cut)" : ctx)}" },
                    new JsonObject { ["role"] = "user", ["content"] = question },
                };
                var live = new StringBuilder();
                var tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
                tick.Tick += (_, _) => { string s; lock (live) s = live.ToString(); if (s.Length > 0) { thinking.Visibility = Visibility.Collapsed; RenderMarkdown(box, s, streaming: true); } };
                tick.Start();
                try
                {
                    OpenRouter.ChatRequest.Value = false; // its own instructions, whoever answers (Google or Groq)
                    var turn = await OpenRouter.Stream(key, ChatModel, msgs, null, d => { lock (live) live.Append(d); }, null, cts.Token, 0.4, "low");
                    answer = turn.Error is { Length: > 0 } err && turn.Text.Trim().Length == 0 ? "Couldn't answer: " + err : turn.Text.Trim();
                }
                finally { tick.Stop(); }
            }
        }
        catch (OperationCanceledException)
        {
            _workAskRunning = false;
            if (_workAsk != cts) return; // another question took over
            answer = "No answer in time (the AI service may be busy). Try again in a moment.";
        }
        catch (Exception ex) { answer = "Couldn't answer: " + ex.Message; }
        _workAskRunning = false;
        if (_workAsk != cts) return; // another question took over this card
        thinking.BeginAnimation(OpacityProperty, null);
        thinking.Visibility = Visibility.Collapsed;
        RenderMarkdown(box, answer);
        keep?.Invoke(answer);
        wrap.Children.Add(AnswerButtons(asked, answer, () => { cleared?.Invoke(); if (wrapBorder.Parent is Panel p) p.Children.Remove(wrapBorder); }));
        FollowWork(wrapBorder);
    }

    /// <summary>The page scrolls to show something that just appeared on it (an answer, a menu).</summary>
    void FollowWork(FrameworkElement el) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { try { el.BringIntoView(); } catch { } });

    /// <summary>The AI engine set up (the service you picked, its models, a warm connection): the chat, and the Work page's cards.</summary>
    void PrepareAi()
    {
        OpenRouter.Warm(); // the first answer doesn't wait for the handshake
        OpenRouter.CompactSystem ??= CompactSystemPrompt; // Groq's short instructions
        WebTools.Rank ??= url => TrustOf(url, _chatLastUser); // research reads the trusted pages first
        OpenRouter.Choose(_settings.AiProvider);
        _ = OpenRouter.IsGemini ? OpenRouter.GeminiModels() : (Task)OpenRouter.Models();
    }

    /// <summary>
    /// The chat, opened on a new conversation that already holds this question and its answer: you just go on talking
    /// (nothing is pasted, nothing is sent until you write).
    /// </summary>
    void ContinueInChat(string asked, string answer)
    {
        EndWorkTyping(giveBack: false);
        OpenLauncher(null);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var s = NewSession();
            s.Title = TitleOf(asked);
            s.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = asked, ["shown"] = asked, ["at"] = DateTime.Now.ToString("o") });
            s.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer });
            s.Updated = DateTime.Now;
            StartSession(s);
            _session = s;
            EnterChat(null, resume: true);
        });
    }

    /// <summary>The AI chat, opened on a question (it answers there, with its tools).</summary>
    void OpenChatWith(string text)
    {
        EndWorkTyping(giveBack: false);
        OpenLauncher(null);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => EnterChat(text));
    }

    // ───────────────────────────── Now ─────────────────────────────

    /// <summary>What Now is about: the thing, and whether it's the window in front or what you worked on just before.</summary>
    sealed record NowSubject(string Kind, string Name, string Key, string App, IntPtr Hwnd, bool InFront, WorkLog.Item? Item, string FrontApp);

    /// <summary>
    /// What you're working on: the document, PDF, code or page in front; any browser page in front (a video too); and
    /// when a chat, the AI, music or another app is in front, the last thing you worked on (within 3 hours), so its
    /// buttons are still one click away. Null when there's nothing at all.
    /// </summary>
    NowSubject? PickNow()
    {
        var hwnd = BehindNotch();
        string app = "", title = "", exe = "";
        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            exe = Interop.Native.ProcessPathById(pid) ?? "";
            app = AppNameOf(pid);
            title = Interop.Native.WindowTitle(hwnd);
        }
        if (hwnd != IntPtr.Zero && WorkLog.Parse(exe, title) is { } w)
            return new NowSubject(w.Kind, w.Name, w.Key, app, hwnd, true, WorkLog.ItemOf(w.Key), app);
        if (hwnd != IntPtr.Zero && WorkLog.TabTitle(exe, title) is { } page)
            return new NowSubject("tab", page, "tab:" + page.ToLowerInvariant(), app, hwnd, true, null, app);
        var last = WorkLog.Recent(1).FirstOrDefault(i => DateTime.Now - i.Last < TimeSpan.FromHours(3));
        if (last != null) return new NowSubject(last.Kind, last.Name, last.Key, last.App, IntPtr.Zero, false, last, app);
        if (hwnd != IntPtr.Zero) return new NowSubject("app", title.Length > 0 ? title : app, "app:" + app + "|" + title, app, hwnd, true, null, app);
        return null;
    }

    static readonly Dictionary<string, (string Label, string Prompt)[]> NowActions = new()
    {
        ["doc"] = new[] { ("Summarize", "Summarize this document in a few short points."), ("Check writing", "Check this document's spelling, grammar and clarity: list the fixes worth making (quote, then the fix)."), ("What's next", "What's missing or worth improving in this document? Short, concrete steps.") },
        ["sheet"] = new[] { ("Explain", "Explain what this spreadsheet shows, and the key numbers."), ("Chart it", "Make a chart of the main numbers in this spreadsheet."), ("Find issues", "Look for mistakes or odd values in this spreadsheet.") },
        ["slides"] = new[] { ("Summarize", "Summarize this presentation slide by slide, one line each."), ("Improve", "How could this presentation be clearer? Concrete fixes.") },
        ["pdf"] = new[] { ("Summarize", "Summarize this PDF in a few short points."), ("Key numbers", "List the key numbers, dates and names in this PDF."), ("Explain simply", "Explain this PDF simply, as to a friend.") },
        ["tab"] = new[] { ("Key points", "Give the key points of this page."), ("Translate", "Translate the main text of this page to English (to French if it's already English), short."), ("Is it reliable?", "Is this page a reliable source? Who's behind it, and anything to double-check.") },
        ["code"] = new[] { ("Explain", "Explain what this code does, simply."), ("Find bugs", "Look for bugs or risky spots in this code, with the fix for each.") },
        ["app"] = new[] { ("What's this?", "Explain what's on this window and what I can do here.") },
    };

    UIElement BuildNowTab()
    {
        var stack = new StackPanel();
        var subject = PickNow();
        if (subject == null || subject.Name.Length == 0)
        {
            _nowShownKey = null;
            stack.Children.Add(WorkCard(WorkText("Open a document, a PDF or a page: it shows up here, with what's worth doing with it.", 11.5, A(TextTone, 0xB0))));
            stack.Children.Add(RecentStrip());
            return stack;
        }
        string kind = subject.Kind, name = subject.Name, app = subject.App;
        var item = subject.Item;
        var project = item != null ? WorkLog.Projects().FirstOrDefault(p => p.Items.Any(i => i.Key == item.Key)) : null;

        var body = new StackPanel();
        // what it is: kind, name, app · time today · its project
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = KindBadge(kind);
        top.Children.Add(badge);
        var nameText = WorkText(name, 12.5, _cyber ? CyYellow : Colors.White, bold: true, wrap: false);
        nameText.Margin = new Thickness(7, 0, 6, 0);
        Grid.SetColumn(nameText, 1);
        top.Children.Add(nameText);
        // live: a dot that breathes while you're on it
        var live = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Ellipse { Width = 6, Height = 6, Fill = Frozen(_cyber ? CyCyan : Color.FromRgb(0x4C, 0xD9, 0x64)), VerticalAlignment = VerticalAlignment.Center };
        if (!Eco) dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        if (subject.InFront) live.Children.Add(dot); // not in front: what you worked on last, no live dot
        live.Children.Add(WorkText(Cap(subject.InFront ? (_cyber ? "LIVE" : "Now") : "Last worked on"), 9, A(TextTone, 0xA0), wrap: false).Also(t => t.Margin = new Thickness(4, 0, 0, 0)));
        Grid.SetColumn(live, 2);
        top.Children.Add(live);
        body.Children.Add(top);

        var meta = new List<string> { kind == "app" && name == app ? "The app in front" : app };
        if (item != null && item.Seconds > 0) meta.Add(Spent(item.Seconds) + " on it");
        if (!subject.InFront && item != null) meta.Add(WorkAgo(item.Last));
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 6) };
        sub.Children.Add(WorkText(string.Join(" · ", meta), 10, A(TextTone, 0x90), wrap: false));
        if (project != null)
        {
            var link = WorkText("  ·  " + Cap("in " + project.Name), 10, WorkHot, wrap: false);
            link.Cursor = Cursors.Hand;
            link.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowWorkTab(1); };
            sub.Children.Add(link);
        }
        body.Children.Add(sub);

        // what's worth doing with it, answered here
        var acts = new WrapPanel();
        string ctxKey = subject.Key;
        _nowShownKey = ctxKey;
        Func<CancellationToken, Task<string>> context = ct => NowContext(subject, ct);
        // under the card: Recent, or (while an answer is there) one short line for it, so the answer has the room
        var recentHost = new ContentControl { Focusable = false };
        void ShowRecent(bool folded) => recentHost.Content = folded && !_recentOpen ? RecentFolded(() => { _recentOpen = true; ShowRecent(true); }) : RecentStrip();
        void Answered() => _recentOpen = false;
        void Cleared() { _nowAnswers.Remove(ctxKey); _recentOpen = false; ShowRecent(false); }
        void Ask(string asked, string prompt)
        {
            _recentOpen = false;
            ShowRecent(true);
            AskOnCard(body, asked, prompt, context, a => { _nowAnswers[ctxKey] = (asked, a, DateTime.Now); Answered(); }, Cleared);
        }
        foreach (var (label, prompt) in NowActions.GetValueOrDefault(kind) ?? NowActions["app"])
            acts.Children.Add(WorkChip(label, () => Ask(label, prompt), prompt));
        body.Children.Add(acts);
        body.Children.Add(BoxHost(WorkBox(_cyber ? "ASK ABOUT IT…" : "Ask anything about it…", q => Ask(q, q))).Also(g => g.Margin = new Thickness(0, 2, 0, 0)));
        // the last answer about it, still here (until you clear it)
        bool hasAnswer = _nowAnswers.TryGetValue(ctxKey, out var kept) && DateTime.Now - kept.At < TimeSpan.FromHours(6);
        if (hasAnswer) body.Children.Add(AnswerBlock(kept.Q, kept.Md, Cleared));
        stack.Children.Add(WorkCard(body, hot: true));
        ShowRecent(hasAnswer);
        stack.Children.Add(recentHost);
        return stack;
    }

    /// <summary>The text of what you're on: the file itself when it can be found, else what the window shows.</summary>
    async Task<string> NowContext(NowSubject s, CancellationToken ct)
    {
        // what it is, by name, always first: even when its text can't be read, the answer is about the right thing
        string kindName = s.Kind switch { "doc" => "a document", "sheet" => "a spreadsheet", "slides" => "a presentation", "pdf" => "a PDF", "code" => "a code file", "tab" => "a page in the browser", _ => "an app's window" };
        string about = $"THE USER IS ASKING ABOUT: “{s.Name}”, {kindName}, in {s.App}{(s.Item?.Url is { } u ? " (" + u + ")" : "")}.\n";
        return about + await NowText(s, ct);
    }

    async Task<string> NowText(NowSubject s, CancellationToken ct)
    {
        var item = s.Item;
        if (item != null && item.Kind is "doc" or "sheet" or "slides" or "pdf" && await Task.Run(() => FindItemFile(item)) is { } path)
        {
            string text = await Task.Run(() => ReadAnyText(path));
            if (!text.StartsWith("Error") && text.Trim().Length > 40) return $"{item.Name} ({path}):\n{text}";
        }
        if (s.InFront) return await ReadHere("page", ct);
        // not in front any more: a page is read from its address (found in the browser's history)
        if (item != null)
        {
            await FindUrls(new[] { item });
            if (item.Url != null)
                try { using var lim = CancellationTokenSource.CreateLinkedTokenSource(ct); lim.CancelAfter(8000); var (_, body) = await WebTools.Read(item.Url, 20000, lim.Token); if (!WebTools.Blocked(body)) return $"{item.Name} ({item.Url}):\n{body}"; } catch { }
        }
        return $"{s.Name} (only its name is known: it isn't open any more, and it couldn't be read. Answer from the name if you can, and say so).";
    }

    /// <summary>Under it: the last things you worked on, to jump back to.</summary>
    bool _recentOpen;

    /// <summary>Recent folded to one line ("Recent · 6 · Show") while an answer takes the room.</summary>
    UIElement RecentFolded(Action open)
    {
        int n = WorkLog.Recent(8).Count;
        var row = new Grid { Margin = new Thickness(2, 0, 0, 2) };
        row.Children.Add(WorkText(Cap(n > 0 ? $"Recent · {n}" : "Recent"), 9.5, A(TextTone, 0x70), bold: true, wrap: false).Also(t => t.VerticalAlignment = VerticalAlignment.Center));
        if (n > 0) row.Children.Add(SmallButtons(("Show", open)).Also(b => b.Margin = new Thickness(0)));
        return row;
    }

    UIElement RecentStrip()
    {
        var recent = WorkLog.Recent(8);
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        // the line over it: what it is, and what you can do with it (clear, hide, pause noting)
        var head = new Grid { Margin = new Thickness(2, 0, 0, 4) };
        string what = _settings.WorkPaused ? "Recent · paused" : _settings.WorkRecentHidden ? $"Recent · {recent.Count} hidden" : "Recent";
        head.Children.Add(WorkText(Cap(what), 9.5, _settings.WorkPaused ? WorkHot : A(TextTone, 0x80), bold: true, wrap: false).Also(t => t.VerticalAlignment = VerticalAlignment.Center));
        var tools = new List<(string, Action)>();
        if (!_settings.WorkRecentHidden && recent.Count > 0)
            tools.Add((_confirmDelete == "recent" ? "Sure? Clear" : "Clear", () =>
            {
                if (_confirmDelete != "recent") { AskToConfirm("recent"); return; }
                _confirmDelete = null;
                WorkLog.ClearRecent();
                SaveWorkSoon();
                WorkSay("Recent cleared");
                RenderWorkTab(animate: false);
            }));
        if (recent.Count > 0 || _settings.WorkRecentHidden)
            tools.Add((_settings.WorkRecentHidden ? "Show" : "Hide", () => { _settings.WorkRecentHidden = !_settings.WorkRecentHidden; _settings.Save(); RenderWorkTab(animate: false); }));
        tools.Add((_settings.WorkPaused ? "Resume" : "Pause", () =>
        {
            _settings.WorkPaused = !_settings.WorkPaused;
            _settings.Save();
            WorkSay(_settings.WorkPaused ? "Paused: what you work on isn't noted" : "Noting what you work on again");
            RenderWorkTab(animate: false);
        }));
        head.Children.Add(SmallButtons(tools.ToArray()).Also(b => { b.Margin = new Thickness(0); ((FrameworkElement)b).ToolTip = "Clear restarts Recent (nothing is deleted). Hide folds it away. Pause stops noting what you work on."; }));
        panel.Children.Add(head);
        if (_settings.WorkRecentHidden) return panel;
        if (recent.Count == 0)
        {
            panel.Children.Add(WorkText(_settings.WorkPaused ? "Paused: documents, PDFs and pages you open aren't noted until you resume." : "The documents, PDFs and pages you work on show up here.", 10.5, A(TextTone, 0x80)).Also(t => t.Margin = new Thickness(2, 0, 0, 2)));
            return panel;
        }
        var row = new WrapPanel();
        foreach (var it in recent) row.Children.Add(ItemChip(it, null));
        panel.Children.Add(row);
        return panel;
    }

    // ───────────────────────────── finding things again ─────────────────────────────

    /// <summary>A document's real file (searched by name among the files on the PC), remembered once found.</summary>
    string? FindItemFile(WorkLog.Item it)
    {
        if (it.Path is { } known && System.IO.File.Exists(known)) return known;
        if (it.Kind is "tab" or "code" && it.Key.StartsWith("tab:")) return null;
        try
        {
            string name = it.Name;
            var hits = _fileIndex.Find(System.IO.Path.GetFileNameWithoutExtension(name), null, 30);
            var best = hits.Where(h => !h.Folder && (h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || System.IO.Path.GetFileNameWithoutExtension(h.Name).Equals(System.IO.Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)
                       && System.IO.Path.GetExtension(h.Name).StartsWith(System.IO.Path.GetExtension(name)[..Math.Min(4, System.IO.Path.GetExtension(name).Length)], StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(h => h.Modified).FirstOrDefault();
            if (best == null || !System.IO.File.Exists(best.Path)) return null;
            WorkLog.Located(it.Key, best.Path, null);
            return best.Path;
        }
        catch { return null; }
    }

    /// <summary>The addresses of tabs seen by their title (read from the browsers' history once, then remembered).</summary>
    async Task FindUrls(IEnumerable<WorkLog.Item> items)
    {
        var tabs = items.Where(i => i.Url == null && (i.Kind == "tab" || i.Key.StartsWith("gdoc:") || i.Key.StartsWith("pdf:") && i.Path == null)).ToList();
        if (tabs.Count == 0) return;
        var titles = tabs.SelectMany(t => new[] { t.Name, t.Name + " - Google Docs", t.Name + " - Google Sheets", t.Name + " - Google Slides" }).ToList();
        var found = await Task.Run(() => BrowserHistory.UrlsFor(titles));
        foreach (var t in tabs)
            foreach (var title in new[] { t.Name, t.Name + " - Google Docs", t.Name + " - Google Sheets", t.Name + " - Google Slides" })
                if (found.TryGetValue(title, out var url)) { t.Url = url; WorkLog.Located(t.Key, null, url); break; }
    }

    async Task<bool> OpenItem(WorkLog.Item it)
    {
        if (await Task.Run(() => FindItemFile(it)) is { } path) { OpenLink(path); return true; }
        await FindUrls(new[] { it });
        if (it.Url != null) { OpenLink(it.Url); return true; }
        return false;
    }

    /// <summary>A file or tab of a project: click opens it, drag moves it to another project, right-click takes it out.</summary>
    Border ItemChip(WorkLog.Item it, WorkLog.Project? project)
    {
        string name = it.Name.Length > 30 ? it.Name[..28] + "…" : it.Name;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(KindBadge(it.Kind));
        row.Children.Add(WorkText(name, 10.5, A(TextTone, 0xE0), wrap: false).Also(t => t.Margin = new Thickness(5, 0, 0, 0)));
        var x = IconButton("\uE711", project != null ? "Take it out of " + project.Name : "Forget it (out of Recent)", () =>
        {
            if (project != null) { WorkLog.Remove(it.Key, WorkLog.Keep(project)); WorkSay("Taken out of " + project.Name); }
            else { WorkLog.ForgetItem(it.Key); WorkSay("Forgotten: " + name); }
            SaveWorkSoon();
            RenderWorkTab(animate: false);
        }, 7.5);
        x.Margin = new Thickness(3, 0, -4, 0);
        x.Visibility = Visibility.Hidden;
        row.Children.Add(x);
        var chip = new Border
        {
            Child = row, Padding = new Thickness(5, 2, 8, 3), Margin = new Thickness(0, 0, 5, 4), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(_cyber ? 0 : 7),
            Background = Frozen(_cyber ? Color.FromArgb(0x60, 0x00, 0x00, 0x00) : Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), BorderBrush = Frozen(A(WorkAcc, 0x30)), BorderThickness = new Thickness(1),
            ToolTip = $"{it.Name} · {WorkAgo(it.Last)}", // short: the × and a click say the rest
        };
        chip.MouseEnter += (_, _) => { chip.BorderBrush = Frozen(_cyber ? CyYellow : WorkAcc); x.Visibility = Visibility.Visible; };
        chip.MouseLeave += (_, _) => { chip.BorderBrush = Frozen(A(WorkAcc, 0x30)); x.Visibility = Visibility.Hidden; };
        Point down = default;
        bool pressed = false;
        chip.PreviewMouseLeftButtonDown += (_, e) => { _maybeSwipe = false; pressed = true; down = e.GetPosition(chip); };
        chip.MouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(chip);
            if (Math.Abs(p.X - down.X) < 6 && Math.Abs(p.Y - down.Y) < 6) return;
            pressed = false;
            _dragOut = true;
            chip.Opacity = 0.45;
            try { DragDrop.DoDragDrop(chip, new DataObject("dn-work-item", it.Key), DragDropEffects.Move); }
            finally { _dragOut = false; chip.Opacity = 1; }
        };
        chip.MouseLeftButtonUp += async (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            e.Handled = true;
            WorkSay(await OpenItem(it) ? "Opened " + name : "Couldn't find it on the PC");
        };
        chip.MouseRightButtonUp += (_, e) =>
        {
            if (project == null)
            {
                // in Recent: forgotten (it comes back only if you use it again)
                e.Handled = true;
                WorkLog.ForgetItem(it.Key);
                SaveWorkSoon();
                WorkSay("Forgotten: " + name);
                RenderWorkTab(animate: false);
                return;
            }
            e.Handled = true;
            WorkLog.Remove(it.Key, WorkLog.Keep(project));
            SaveWorkSoon();
            WorkSay("Taken out of " + project.Name);
            RenderWorkTab(animate: false);
        };
        return chip;
    }

    // ───────────────────────────── Projects ─────────────────────────────

    static readonly string[] Stages = { "Working", "Review", "Done" };
    string? _renaming, _askingIn, _noting, _managing, _merging, _confirmDelete;
    bool _showFinished;

    UIElement BuildProjectsTab()
    {
        var stack = new StackPanel();
        var projects = WorkLog.Projects();
        var active = projects.Where(p => p.Stage < 2).ToList();
        var finished = projects.Where(p => p.Stage == 2).ToList();
        int hidden = WorkLog.HiddenCount();

        var bar = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        bar.Children.Add(WorkText(Cap(active.Count == 0 ? "No active project" : active.Count == 1 ? "1 active project" : $"{active.Count} active projects"), 9.5, A(TextTone, 0x80), bold: true, wrap: false).Also(t => t.VerticalAlignment = VerticalAlignment.Center));
        var add = WorkChip("+ New project", () =>
        {
            var m = WorkLog.NewProject("New project");
            SaveWorkSoon();
            _renaming = m.Id;
            RenderWorkTab(animate: false);
        }, "An empty project: drag files or tabs into it");
        add.HorizontalAlignment = HorizontalAlignment.Right;
        add.Margin = new Thickness(0);
        bar.Children.Add(add);
        stack.Children.Add(bar);

        if (projects.Count == 0)
        {
            var empty = WorkCard(new StackPanel
            {
                Children =
                {
                    WorkText(Cap("Your projects build themselves"), 11.5, _cyber ? CyYellow : Colors.White, bold: true),
                    WorkText("The documents, PDFs and pages you use together become a project here. Or drag a file from Explorer onto this card to start one.", 11, A(TextTone, 0xB0)).Also(t => t.Margin = new Thickness(0, 3, 0, 0)),
                },
            });
            MakeDropTarget(empty, null);
            stack.Children.Add(empty);
        }
        int i = 0;
        foreach (var p in active.Take(12)) stack.Children.Add(Arriving(ProjectCard(p), i++));
        if (finished.Count > 0)
        {
            var toggle = WorkChip((_showFinished ? "▾ " : "▸ ") + $"Finished · {finished.Count}", () => { _showFinished = !_showFinished; RenderWorkTab(animate: false); }, "Projects you marked done");
            toggle.Margin = new Thickness(0, 4, 0, 5);
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(toggle);
            if (_showFinished) foreach (var p in finished.Take(12)) stack.Children.Add(ProjectCard(p));
        }
        if (hidden > 0)
        {
            var restore = WorkChip($"Show {hidden} hidden", () => { WorkLog.UnhideAll(); SaveWorkSoon(); WorkSay("Hidden projects are back"); RenderWorkTab(animate: false); }, "Bring back the projects you hid or deleted");
            restore.HorizontalAlignment = HorizontalAlignment.Left;
            restore.Margin = new Thickness(0, 2, 0, 4);
            stack.Children.Add(restore);
        }
        return stack;
    }

    /// <summary>A card coming in: a quick flicker in Neon, a soft rise elsewhere (none on a light PC).</summary>
    T Arriving<T>(T el, int order) where T : UIElement
    {
        if (Eco || order > 8) return el;
        if (_cyber)
        {
            el.Opacity = 0;
            var on = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(45 * order), Duration = TimeSpan.FromMilliseconds(170) };
            on.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            on.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50))));
            on.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170))));
            el.BeginAnimation(OpacityProperty, on);
        }
        else
        {
            el.Opacity = 0;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(40 * order) });
        }
        return el;
    }

    /// <summary>A small round icon button (⋯, ×, pin) with a hover.</summary>
    TextBlock IconButton(string glyph, string tip, Action act, double size = 11, Color? color = null)
    {
        var c = color ?? A(TextTone, 0x90);
        var b = new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = size, Foreground = Frozen(c), Cursor = Cursors.Hand, ToolTip = tip,
            Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
        };
        b.MouseEnter += (_, _) => b.Foreground = Frozen(_cyber ? CyYellow : Colors.White);
        b.MouseLeave += (_, _) => b.Foreground = Frozen(c);
        b.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
        return b;
    }

    Border ProjectCard(WorkLog.Project p)
    {
        bool done = p.Stage == 2;
        string id = p.Kept?.Id ?? p.Id;
        var body = new StackPanel();
        // the title line: pin, name (double-click to rename), stage, time, ⋯
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(IconButton(p.Pinned ? "" : "", p.Pinned ? "Unpin" : "Pin to the top", () => { var m = WorkLog.Keep(p); m.Pinned = !m.Pinned; SaveWorkSoon(); WorkSay(m.Pinned ? "Pinned" : "Unpinned"); RenderWorkTab(animate: false); }, 10.5, p.Pinned ? WorkHot : A(TextTone, 0x60)).Also(t => t.Margin = new Thickness(-4, 0, 3, 0)));
        if (_renaming == id)
        {
            var box = WorkBox("Name", name => { var m = WorkLog.Keep(p); m.Name = name; _renaming = null; SaveWorkSoon(); WorkSay("Renamed"); RenderWorkTab(animate: false); }, p.Name);
            var host = BoxHost(box);
            Grid.SetColumn(host, 1);
            head.Children.Add(host);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { BeginWorkTyping(box); box.SelectAll(); });
        }
        else
        {
            var name = WorkText((done ? "✓ " : "") + p.Name, 12.5, _cyber ? CyYellow : Colors.White, bold: true, wrap: false);
            name.ToolTip = "Double-click to rename";
            name.Cursor = Cursors.IBeam;
            name.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { e.Handled = true; _renaming = WorkLog.Keep(p).Id; SaveWorkSoon(); RenderWorkTab(animate: false); } };
            Grid.SetColumn(name, 1);
            head.Children.Add(name);
        }
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(StageControl(p));
        if (p.Seconds > 0) right.Children.Add(WorkText(Spent(p.Seconds), 9.5, A(TextTone, 0x80), wrap: false).Also(t => t.Margin = new Thickness(7, 0, 2, 0)));
        right.Children.Add(IconButton(_managing == id ? "" : "", _managing == id ? "Close" : "More: rename, done, merge, delete", () => { _managing = _managing == id ? null : id; _merging = null; _confirmDelete = null; RenderWorkTab(animate: false); }));
        Grid.SetColumn(right, 2);
        head.Children.Add(right);
        body.Children.Add(head);

        // ⋯: everything you can do with it, right here (no pop-up)
        if (_managing == id) body.Children.Add(ManageStrip(p, id));
        if (p.Kept == null) body.Children.Add(WorkText(Cap("Found for you · pin or rename it to keep it"), 9, A(WorkAcc, 0xA0), wrap: false).Also(t => t.Margin = new Thickness(0, 1, 0, 0)));
        if (p.Kept?.Note is { Length: > 0 } note)
        {
            var noteRow = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            noteRow.Children.Add(WorkText("✎ " + note, 10.5, A(TextTone, 0xB8)).Also(t => t.Margin = new Thickness(0, 0, 20, 0)));
            noteRow.Children.Add(IconButton("", "Remove the note", () => { var m = WorkLog.Keep(p); m.Note = ""; SaveWorkSoon(); RenderWorkTab(animate: false); }, 8).Also(b => b.HorizontalAlignment = HorizontalAlignment.Right));
            body.Children.Add(noteRow);
        }

        // its files and tabs
        var items = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var it in p.Items.Take(9)) items.Children.Add(ItemChip(it, p));
        if (p.Items.Count > 9) items.Children.Add(WorkText($"+{p.Items.Count - 9}", 10, A(TextTone, 0x90), wrap: false).Also(t => t.Margin = new Thickness(2, 3, 0, 0)));
        if (p.Items.Count == 0) items.Children.Add(WorkText("Empty: drag files from Explorer, or a chip from another project, onto this card", 10.5, A(TextTone, 0x80)));
        body.Children.Add(items);

        // what to do with it
        var acts = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        if (p.Items.Count > 0)
        {
            acts.Children.Add(WorkChip("Reopen all", async () => await ReopenAll(p), "Open every file and page of it again", strong: true));
            acts.Children.Add(WorkChip(_askingIn == id ? "Ask ×" : "Ask", () => { _askingIn = _askingIn == id ? null : id; RenderWorkTab(animate: false); }, "A question about the whole project: its files and pages read together"));
            acts.Children.Add(WorkChip("Summarize", () => AskOnCard(body, "Summarize", "Summarize this project: what it's about, where it stands, what's left.", ct => ProjectContext(p, ct), a => KeepAnswer(p, "Summarize", a), () => ClearAnswer(p)), "Where it stands, in a few lines"));
        }
        acts.Children.Add(WorkChip(p.Kept?.Note is { Length: > 0 } ? "Edit note" : "Note", () => { _noting = _noting == id ? null : WorkLog.Keep(p).Id; SaveWorkSoon(); RenderWorkTab(animate: false); }, "A note to yourself on this project"));
        body.Children.Add(acts);
        if (_askingIn == id)
        {
            var box = WorkBox(_cyber ? "ASK THE WHOLE PROJECT…" : "Ask the whole project… (do the numbers match? what's missing?)", q => AskOnCard(body, q, q, ct => ProjectContext(p, ct), a => KeepAnswer(p, q, a), () => ClearAnswer(p)));
            body.Children.Add(BoxHost(box).Also(g => g.Margin = new Thickness(0, 2, 0, 0)));
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => BeginWorkTyping(box));
        }
        if (_noting == id)
        {
            var box = WorkBox("A note to yourself (Enter to save)", text => { var m = WorkLog.Keep(p); m.Note = text; _noting = null; SaveWorkSoon(); WorkSay("Note saved"); RenderWorkTab(animate: false); }, p.Kept?.Note ?? "");
            body.Children.Add(BoxHost(box).Also(g => g.Margin = new Thickness(0, 2, 0, 0)));
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => BeginWorkTyping(box));
        }
        // the last answer about it, kept (until you clear it)
        if (p.Kept is { LastAnswer.Length: > 0 } k) body.Children.Add(AnswerBlock(k.LastAsk, k.LastAnswer, () => ClearAnswer(p)));
        if (done) body.Opacity = 0.7;
        var card = WorkCard(body, hot: p.Pinned);
        card.MouseRightButtonUp += (_, e) => { if (e.Handled) return; e.Handled = true; _managing = _managing == id ? null : id; RenderWorkTab(animate: false); };
        MakeDropTarget(card, p);
        return card;
    }

    void ClearAnswer(WorkLog.Project p)
    {
        if (p.Kept == null) return;
        p.Kept.LastAnswer = "";
        p.Kept.LastAsk = "";
        SaveWorkSoon();
    }

    /// <summary>⋯ on a project: rename, done / reopen, merge into another, hide or delete (asks once).</summary>
    FrameworkElement ManageStrip(WorkLog.Project p, string id)
    {
        var strip = new StackPanel { Margin = new Thickness(0, 5, 0, 2) };
        var row = new WrapPanel();
        row.Children.Add(WorkChip("Rename", () => { _renaming = WorkLog.Keep(p).Id; _managing = null; SaveWorkSoon(); RenderWorkTab(animate: false); }));
        row.Children.Add(WorkChip(p.Stage == 2 ? "Reopen" : "Mark done", () => { var m = WorkLog.Keep(p); m.Stage = m.Stage == 2 ? 0 : 2; _managing = null; SaveWorkSoon(); WorkSay(m.Stage == 2 ? "Done: " + m.Name : "Back to work: " + m.Name); if (_cyber && m.Stage == 2) CyberGlitch(strong: false, tint: CyCyan); RenderWorkTab(animate: false); }));
        var others = WorkLog.Projects().Where(o => (o.Kept?.Id ?? o.Id) != id).ToList();
        if (others.Count > 0) row.Children.Add(WorkChip(_merging == id ? "Merge into… ×" : "Merge into…", () => { _merging = _merging == id ? null : id; RenderWorkTab(animate: false); }, "Put this project's files and tabs into another one"));
        bool kept = p.Kept != null;
        if (_confirmDelete == id)
            row.Children.Add(WorkChip(kept ? "Sure? Delete it" : "Sure? Hide it", () =>
            {
                WorkLog.Hide(p);
                _managing = _confirmDelete = null;
                SaveWorkSoon();
                WorkSay(kept ? "Deleted (your files stay where they are)" : "Hidden");
                RenderWorkTab(animate: false);
            }, "Your files and pages aren't touched: only the project goes", strong: true).Also(c => c.Background = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D))));
        else
            row.Children.Add(WorkChip(kept ? "Delete" : "Hide", () => { _confirmDelete = id; RenderWorkTab(animate: false); After(4000, () => { if (_confirmDelete == id) { _confirmDelete = null; if (_page == WorkPage && _workTab == 1) RenderWorkTab(animate: false); } }); }, kept ? "Delete this project (your files stay)" : "Hide this found project"));
        strip.Children.Add(row);
        if (_merging == id)
        {
            var into = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            into.Children.Add(WorkText(Cap("Into: "), 10, A(TextTone, 0x90), wrap: false).Also(t => t.Margin = new Thickness(0, 3, 4, 0)));
            foreach (var o in others.Take(6))
                into.Children.Add(WorkChip(o.Name, () => { WorkLog.Merge(WorkLog.Keep(o), WorkLog.Keep(p)); _managing = _merging = null; SaveWorkSoon(); WorkSay("Merged into " + o.Name); RenderWorkTab(animate: false); }));
            strip.Children.Add(into);
        }
        return new Border { Child = strip, BorderBrush = Frozen(A(WorkAcc, 0x30)), BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(0, 4, 0, 1), Margin = new Thickness(0, 4, 0, 0) };
    }

    /// <summary>A button that deletes asks once: it turns into "Sure?…" for 4 seconds.</summary>
    void AskToConfirm(string what)
    {
        _confirmDelete = what;
        RenderWorkTab(animate: false);
        After(4000, () => { if (_confirmDelete == what) { _confirmDelete = null; if (_page == WorkPage) RenderWorkTab(animate: false); } });
    }

    void KeepAnswer(WorkLog.Project p, string asked, string answer)
    {
        var m = WorkLog.Keep(p);
        m.LastAsk = asked.Length > 80 ? asked[..80] + "…" : asked;
        m.LastAnswer = answer.Length > 3000 ? answer[..3000] : answer;
        SaveWorkSoon();
    }

    /// <summary>The stage: three steps (Working, Review, Done) to click through; in Neon, three lit bars.</summary>
    FrameworkElement StageControl(WorkLog.Project p)
    {
        var acc = WorkAcc;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, ToolTip = "Stage: " + Stages[p.Stage] + " (click for the next)" };
        if (_cyber)
            for (int s = 0; s < 3; s++)
                row.Children.Add(new Border { Width = 9, Height = 4, Margin = new Thickness(0, 0, 2, 0), Background = Frozen(s <= p.Stage ? (p.Stage == 2 ? CyCyan : CyYellow) : A(CyCyan, 0x30)) });
        else
            row.Children.Add(new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Fill = Frozen(p.Stage == 0 ? Color.FromRgb(0x4C, 0xD9, 0x64) : p.Stage == 1 ? Color.FromRgb(0xFF, 0xB7, 0x4D) : A(acc, 0xE0)) });
        row.Children.Add(WorkText(Cap(Stages[p.Stage]), 9, p.Stage == 2 ? A(acc, 0xE0) : A(TextTone, 0xA0), wrap: false).Also(t => t.Margin = new Thickness(_cyber ? 3 : 0, 0, 0, 0)));
        row.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var m = WorkLog.Keep(p);
            m.Stage = (m.Stage + 1) % 3;
            SaveWorkSoon();
            WorkSay(Stages[m.Stage] + ": " + m.Name);
            if (m.Stage == 2 && _cyber) CyberGlitch(strong: false, tint: CyCyan);
            RenderWorkTab(animate: false);
        };
        return row;
    }

    /// <summary>A project's files and pages, read together (the first part of each), for a question about all of it.</summary>
    async Task<string> ProjectContext(WorkLog.Project p, CancellationToken ct)
    {
        await FindUrls(p.Items);
        var sb = new StringBuilder($"THE USER IS ASKING ABOUT THEIR PROJECT “{p.Name}”: {p.Items.Count} items.\n");
        if (p.Kept?.Note is { Length: > 0 } note) sb.AppendLine("The user's note: " + note);
        int files = 0, pages = 0;
        foreach (var it in p.Items.OrderBy(i => i.Kind == "tab").ThenByDescending(i => i.Seconds))
        {
            if (ct.IsCancellationRequested) break;
            string text = "";
            if (it.Kind != "tab" && files < 5 && await Task.Run(() => FindItemFile(it)) is { } path)
            {
                text = await Task.Run(() => ReadAnyText(path));
                if (text.StartsWith("Error") || text.StartsWith("This is not")) text = "";
                files++;
            }
            else if (it.Url != null && pages < 4)
            {
                try { using var lim = CancellationTokenSource.CreateLinkedTokenSource(ct); lim.CancelAfter(5000); var (_, body) = await WebTools.Read(it.Url, 5000, lim.Token); if (!WebTools.Blocked(body)) text = body; } catch { }
                pages++;
            }
            sb.AppendLine().AppendLine($"── {it.Name} ({it.Kind}{(it.Url != null ? ", " + it.Url : "")}{(it.Path != null ? ", " + it.Path : "")})");
            sb.AppendLine(text.Length > 0 ? (text.Length > 5000 ? text[..5000] + "…" : text) : "(only its name is known)");
        }
        return sb.ToString();
    }

    async Task ReopenAll(WorkLog.Project p)
    {
        WorkSay("Opening…");
        await FindUrls(p.Items);
        int opened = 0, missing = 0;
        foreach (var it in p.Items.Take(14))
        {
            if (await OpenItem(it)) { opened++; await Task.Delay(220); } else missing++;
        }
        WorkSay(opened == 0 ? "Couldn't find them on the PC" : $"Reopened {opened}{(missing > 0 ? $" · {missing} not found" : "")}");
    }

    /// <summary>A project card takes a dragged file or tab (from another project, or files from Explorer).</summary>
    void MakeDropTarget(Border card, WorkLog.Project? p)
    {
        card.AllowDrop = true;
        var idle = card.BorderBrush;
        bool Wanted(DragEventArgs e) => e.Data.GetDataPresent("dn-work-item") || e.Data.GetDataPresent(DataFormats.FileDrop);
        card.DragEnter += (_, e) => { if (!Wanted(e)) return; card.BorderBrush = Frozen(_cyber ? CyYellow : WorkAcc); e.Effects = e.Data.GetDataPresent("dn-work-item") ? DragDropEffects.Move : DragDropEffects.Link; e.Handled = true; };
        card.DragOver += (_, e) => { if (!Wanted(e)) return; e.Effects = e.Data.GetDataPresent("dn-work-item") ? DragDropEffects.Move : DragDropEffects.Link; e.Handled = true; };
        card.DragLeave += (_, _) => card.BorderBrush = idle;
        card.Drop += (_, e) =>
        {
            card.BorderBrush = idle;
            if (!Wanted(e)) return;
            e.Handled = true;
            var target = p != null ? WorkLog.Keep(p) : WorkLog.NewProject("New project");
            if (e.Data.GetData("dn-work-item") is string key) { WorkLog.Move(key, target); WorkSay("Moved to " + target.Name); }
            else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var f in files.Where(System.IO.File.Exists).Take(20)) WorkLog.Move(WorkLog.AddFile(f).Key, target);
                WorkSay($"Added {files.Length} to {target.Name}");
            }
            SaveWorkSoon();
            if (_cyber) CyberGlitch(strong: false, light: true);
            RenderWorkTab(animate: false);
        };
    }

    int _workSaveV;
    async void SaveWorkSoon()
    {
        int v = ++_workSaveV;
        await Task.Delay(600);
        if (v == _workSaveV) await Task.Run(WorkLog.Save);
    }

    // ───────────────────────────── Inbox ─────────────────────────────

    (string Md, DateTime At, int Count)? _catchUp;

    /// <summary>A person's (or an app's) notifications in a row, shown as one line with how many.</summary>
    sealed record NotifGroup(string App, string Title, List<NotifHistory.Entry> Items)
    {
        public NotifHistory.Entry Latest => Items[0];
        public string Key => App + "|" + Title + "|" + Items[^1].At.Ticks;
    }

    static List<NotifGroup> Grouped(IEnumerable<NotifHistory.Entry> newestFirst)
    {
        var groups = new List<NotifGroup>();
        foreach (var e in newestFirst)
        {
            var last = groups.LastOrDefault();
            // the same sender, the same day, within 2 hours of the one after it: one group
            if (last != null && last.App == e.App && last.Title == e.Title && last.Items[^1].At.Date == e.At.Date && last.Items[^1].At - e.At < TimeSpan.FromHours(2)) last.Items.Add(e);
            else groups.Add(new NotifGroup(e.App, e.Title, new List<NotifHistory.Entry> { e }));
        }
        return groups;
    }

    UIElement BuildInboxTab()
    {
        var stack = new StackPanel();
        var all = NotifHistory.Recent();
        var waiting = NotifHistory.Waiting();
        int people = all.Count(NotifHistory.FromPerson), apps = all.Count - people;
        // the views, and a search (in the lists)
        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        void View(string id, string label, string tip) => filters.Children.Add(FilterChip(label, _inboxView == id && _inboxApp.Length == 0, () => { _inboxView = id; _inboxApp = ""; _inboxFind = ""; RenderWorkTab(animate: false); }).Also(c => c.ToolTip = tip));
        View("waiting", $"Waiting · {waiting.Count}", "People who asked you something and haven't had an answer");
        View("people", $"People · {people}", "Messages and mail");
        View("apps", $"Apps · {apps}", "Everything apps and Windows told you");
        View("all", "All", "Every notification of the last two weeks");
        if (_inboxApp.Length > 0) filters.Children.Add(FilterChip(_inboxApp + " ×", true, () => { _inboxApp = ""; RenderWorkTab(animate: false); }));
        stack.Children.Add(filters);

        if (_inboxView == "waiting" && _inboxApp.Length == 0)
        {
            if (waiting.Count == 0)
            {
                stack.Children.Add(WorkCard(new StackPanel
                {
                    Children =
                    {
                        WorkText(Cap("All caught up"), 11.5, _cyber ? CyCyan : Color.FromRgb(0x4C, 0xD9, 0x64), bold: true),
                        WorkText("Nobody's waiting on you. When someone asks you something in a chat or a mail, it lands here with a reply ready to draft.", 11, A(TextTone, 0xB0)).Also(t => t.Margin = new Thickness(0, 3, 0, 0)),
                    },
                }));
            }
            int i = 0;
            foreach (var n in waiting) stack.Children.Add(Arriving(WaitingCard(n), i++));
            // the catch-up: what happened, in a few lines (written once, kept until new ones come)
            if (all.Count > 0) stack.Children.Add(CatchUpCard(all));
            return stack;
        }

        var find = WorkBox(_cyber ? "SEARCH NOTIFICATIONS…" : "Search the last two weeks…", q => { _inboxFind = q; RenderWorkTab(animate: false); }, _inboxFind);
        find.TextChanged += (_, _) => { if (find.Text.Length == 0 && _inboxFind.Length > 0) { _inboxFind = ""; RenderWorkTab(animate: false); } };
        stack.Children.Add(BoxHost(find).Also(g => g.Margin = new Thickness(0, 0, 0, 5)));

        var list = all.Where(n => _inboxApp.Length > 0 ? n.App == _inboxApp : _inboxView switch { "people" => NotifHistory.FromPerson(n), "apps" => !NotifHistory.FromPerson(n), _ => true })
            .Where(n => _inboxFind.Length == 0 || (n.App + " " + n.Title + " " + n.Body).Contains(_inboxFind, StringComparison.CurrentCultureIgnoreCase)).Take(200).ToList();
        if (list.Count == 0)
        {
            stack.Children.Add(WorkText(_inboxFind.Length > 0 ? "Nothing matches." : "Nothing here yet. Notifications are kept for two weeks, on this PC only.", 11, A(TextTone, 0x90)).Also(t => t.Margin = new Thickness(2, 2, 0, 4)));
        }
        else
            foreach (var day in Grouped(list).GroupBy(g => g.Latest.At.Date))
            {
                string label = day.Key == DateTime.Today ? "Today" : day.Key == DateTime.Today.AddDays(-1) ? "Yesterday" : day.Key.ToString("dddd d MMMM");
                var dayHead = new Grid { Margin = new Thickness(2, 5, 0, 3) };
                dayHead.Children.Add(WorkText(Cap(label) + $"  ·  {day.Sum(g => g.Items.Count)}", 9.5, _cyber ? CyYellow : A(TextTone, 0x90), bold: true, wrap: false));
                var dayList = day.SelectMany(g => g.Items).ToList();
                string dayId = "day:" + day.Key.ToString("yyyyMMdd");
                dayHead.Children.Add(SmallButtons((_confirmDelete == dayId ? "Sure? Clear " + label.ToLowerInvariant() : "Clear day", () =>
                {
                    if (_confirmDelete != dayId) { AskToConfirm(dayId); return; }
                    _confirmDelete = null;
                    var ids = dayList.Select(e => e.Id).ToHashSet();
                    NotifHistory.RemoveWhere(e => ids.Contains(e.Id));
                    WorkSay($"Cleared {label.ToLowerInvariant()}");
                    RenderWorkTab(animate: false);
                })).Also(b => b.Margin = new Thickness(0)));
                stack.Children.Add(dayHead);
                foreach (var g in day) stack.Children.Add(NotifRow(g));
            }
        // muted apps, to unmute; and clearing everything
        var foot = new WrapPanel { Margin = new Thickness(0, 6, 0, 2) };
        foreach (var m in NotifHistory.Muted()) foot.Children.Add(WorkChip("Unmute " + m, () => { NotifHistory.Mute(m, false); WorkSay("Unmuted " + m); RenderWorkTab(animate: false); }, "Keep this app's notifications again"));
        if (all.Count > 0)
            foot.Children.Add(WorkChip(_confirmDelete == "history" ? "Sure? Clear all of it" : "Clear all history", () =>
            {
                if (_confirmDelete != "history") { AskToConfirm("history"); return; }
                _confirmDelete = null;
                NotifHistory.Clear();
                _catchUp = null;
                WorkSay("History cleared");
                RenderWorkTab(animate: false);
            }, "Delete every kept notification (they're only on this PC)", strong: _confirmDelete == "history"));
        stack.Children.Add(foot);
        return stack;
    }

    /// <summary>
    /// "Catch me up": the AI reads today's notifications and says what matters (who needs an answer, what happened, what
    /// can wait), in a few lines. Kept until new notifications come in.
    /// </summary>
    Border CatchUpCard(List<NotifHistory.Entry> all)
    {
        var today = all.Where(n => n.At > DateTime.Now.AddHours(-18)).ToList();
        var body = new StackPanel();
        var head = new Grid();
        head.Children.Add(WorkText(Cap("Catch me up"), 11.5, _cyber ? CyYellow : Colors.White, bold: true, wrap: false));
        head.Children.Add(WorkText(today.Count == 0 ? "nothing new" : $"{today.Count} since this morning", 9.5, A(TextTone, 0x80), wrap: false).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
        body.Children.Add(head);
        bool fresh = _catchUp is { } c && c.Count == today.Count && DateTime.Now - c.At < TimeSpan.FromHours(6);
        if (!fresh)
            body.Children.Add(WorkText(today.Count == 0 ? "Nothing to catch up on." : "What matters in your notifications, in a few lines: who's waiting on you, what happened, what can wait.", 10.5, A(TextTone, 0xA8)).Also(t => t.Margin = new Thickness(0, 2, 0, 4)));
        if (today.Count > 0)
        {
            var acts = new WrapPanel();
            acts.Children.Add(WorkChip(fresh ? "Again" : "Catch me up", () =>
            {
                string list = string.Join("\n", today.Take(80).Select(n => $"{n.At:HH:mm} · {n.App} · {n.Title}: {(n.Body.Length > 160 ? n.Body[..160] + "…" : n.Body)}"));
                AskOnCard(body, "Catch me up", "Catch me up on these notifications: first who's waiting on an answer from me (name, what they want), then what happened that I should know, then one line on what can be ignored. Short bullets, no filler.",
                    _ => Task.FromResult("THE USER'S NOTIFICATIONS SINCE THIS MORNING (newest first):\n" + list), a => _catchUp = (a, DateTime.Now, today.Count), () => _catchUp = null);
            }, "A short summary of what you missed", strong: !fresh));
            body.Children.Add(acts);
        }
        if (fresh && _catchUp is { } kept) body.Children.Add(AnswerBlock("Catch me up", kept.Md, () => _catchUp = null));
        return WorkCard(body, hot: !fresh && today.Count > 0);
    }

    Border FilterChip(string label, bool on, Action act)
    {
        var c = WorkChip(label, act, strong: on);
        c.Padding = new Thickness(9, 2, 9, 3);
        c.MinHeight = 22;
        return c;
    }

    /// <summary>A letter tile for an app, in a colour of its own.</summary>
    FrameworkElement AppTile(string app)
    {
        int h = Math.Abs(app.ToLowerInvariant().GetHashCode());
        var palette = new[] { Color.FromRgb(0x58, 0x65, 0xF2), Color.FromRgb(0x25, 0xD3, 0x66), Color.FromRgb(0xFF, 0x8A, 0x4C), Color.FromRgb(0xE5, 0x4B, 0x8F), Color.FromRgb(0x2E, 0xA8, 0xFF), Color.FromRgb(0xF2, 0xC1, 0x4E) };
        var c = _cyber ? (h % 2 == 0 ? CyCyan : CyYellow) : palette[h % palette.Length];
        return new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(_cyber ? 0 : 5), Background = Frozen(A(c, _cyber ? (byte)0x22 : (byte)0xD0)), BorderBrush = Frozen(_cyber ? A(c, 0xB0) : Colors.Transparent), BorderThickness = new Thickness(_cyber ? 1 : 0),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 7, 0),
            Child = new TextBlock { Text = app.Length > 0 ? app[..1].ToUpperInvariant() : "?", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Frozen(_cyber ? c : Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = WorkFont },
        };
    }

    Border WaitingCard(NotifHistory.Entry n)
    {
        var body = new StackPanel();
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(AppTile(n.App));
        var words = new StackPanel();
        var top = new Grid();
        top.Children.Add(WorkText(n.Title, 11.5, _cyber ? CyYellow : Colors.White, bold: true, wrap: false).Also(t => t.Margin = new Thickness(0, 0, 110, 0)));
        top.Children.Add(WorkText($"{n.App} · {WorkAgo(n.At)}", 9.5, A(TextTone, 0x80), wrap: false).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
        words.Children.Add(top);
        if (n.Body.Length > 0) words.Children.Add(WorkText(n.Body.Length > 220 ? n.Body[..220] + "…" : n.Body, 11, A(TextTone, 0xD0)).Also(t => t.MaxHeight = 46));
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        body.Children.Add(row);
        var acts = new WrapPanel { Margin = new Thickness(25, 5, 0, 0) };
        acts.Children.Add(WorkChip("Draft a reply", () => DraftReply(body, n), "A reply written for you, in their language: copy it, then send it yourself", strong: true));
        acts.Children.Add(WorkChip("Open " + n.App, async () => await OpenNotifApp(n.App), "Go to the conversation's app"));
        acts.Children.Add(WorkChip("✓ Done", () => { NotifHistory.Done(n); WorkSay("Done"); RenderWorkTab(animate: false); }, "Answered: take it off the list"));
        acts.Children.Add(WorkChip("Later", () => { NotifHistory.Snooze(n, TimeSpan.FromHours(3)); WorkSay("Back in 3 hours"); RenderWorkTab(animate: false); }, "Hide it for 3 hours"));
        body.Children.Add(acts);
        return WorkCard(body, hot: DateTime.Now - n.At < TimeSpan.FromHours(1));
    }

    /// <summary>The app a notification came from, in front (open already: its window; else started).</summary>
    async Task OpenNotifApp(string app)
    {
        var hwnd = FindUiWindow(app.ToLowerInvariant());
        if (hwnd != IntPtr.Zero && await Front(hwnd)) { WorkSay(app); return; }
        try { WorkSay((await ToolDo("open " + app)).StartsWith("Error") ? "Couldn't open " + app : app); } catch { WorkSay("Couldn't open " + app); }
    }

    void DraftReply(StackPanel host, NotifHistory.Entry n)
    {
        string question = "Write a short, natural reply to this message, in its language and tone. Only the reply text, nothing around it.";
        AskOnCard(host, "Reply to " + n.Title, question, _ => Task.FromResult($"A message on {n.App} from {n.Title}, at {n.At:HH:mm}:\n{n.Body}"));
    }

    readonly HashSet<string> _inboxManage = new();

    /// <summary>One sender's notifications in a row (×N): click to open them, × to remove; ⋯ to mute or clear the app.</summary>
    Border NotifRow(NotifGroup g)
    {
        var n = g.Latest;
        bool open = _inboxOpen.Contains(g.Key);
        var row = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(AppTile(n.App));
        var words = new StackPanel();
        var top = new Grid();
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 112, 0) };
        titleLine.Children.Add(WorkText(n.Title.Length > 0 ? n.Title : n.App, 11, Colors.White, bold: true, wrap: false));
        if (g.Items.Count > 1)
            titleLine.Children.Add(new Border
            {
                Child = WorkText("×" + g.Items.Count, 8.5, _cyber ? Colors.Black : Colors.White, bold: true, wrap: false), Background = Frozen(_cyber ? CyCyan : A(WorkAcc, 0xB0)),
                CornerRadius = new CornerRadius(_cyber ? 0 : 6), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        top.Children.Add(titleLine);
        var side = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        side.Children.Add(WorkText($"{n.App} · {n.At:HH:mm}", 9.5, A(TextTone, 0x80), wrap: false).Also(t => { t.MaxWidth = 92; t.VerticalAlignment = VerticalAlignment.Center; }));
        side.Children.Add(IconButton("", "Remove " + (g.Items.Count > 1 ? $"these {g.Items.Count}" : "it"), () => { var ids = g.Items.Select(e => e.Id).ToHashSet(); NotifHistory.RemoveWhere(e => ids.Contains(e.Id)); WorkSay("Removed"); RenderWorkTab(animate: false); }, 8));
        top.Children.Add(side);
        words.Children.Add(top);
        if (!open)
        {
            if (n.Body.Length > 0) words.Children.Add(WorkText(n.Body, 10.5, A(TextTone, 0xC0), wrap: false));
        }
        else
        {
            foreach (var e in g.Items.Take(12))
                words.Children.Add(WorkText((g.Items.Count > 1 ? $"{e.At:HH:mm}  " : "") + (e.Body.Length > 0 ? e.Body : "(no text)"), 10.5, A(TextTone, 0xD0)).Also(t => t.Margin = new Thickness(0, 1, 0, 1)));
            var acts = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
            acts.Children.Add(WorkChip("Copy", () => { CopyText(string.Join("\n", g.Items.Select(e => $"{e.Title}: {e.Body}")), "Copied"); WorkSay("Copied"); }));
            if (NotifHistory.FromPerson(n)) acts.Children.Add(WorkChip("Draft a reply", () => DraftReply(words, n)));
            acts.Children.Add(WorkChip("Open " + n.App, async () => await OpenNotifApp(n.App)));
            acts.Children.Add(WorkChip("Only " + n.App, () => { _inboxApp = n.App; RenderWorkTab(animate: false); }, "Show only this app's notifications"));
            acts.Children.Add(WorkChip("Mute " + n.App, () => { NotifHistory.Mute(n.App, true); WorkSay("Muted " + n.App + ": not kept any more"); RenderWorkTab(animate: false); }, "Stop keeping this app's notifications here (and remove the ones kept)"));
            words.Children.Add(acts);
        }
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        var card = new Border
        {
            Child = row, Padding = new Thickness(6, 4, 6, 5), Margin = new Thickness(0, 0, 0, 3), CornerRadius = new CornerRadius(_cyber ? 0 : 8),
            Background = Frozen(open ? (_cyber ? Color.FromArgb(0x70, 0x00, 0x00, 0x00) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)) : Colors.Transparent),
            BorderBrush = Frozen(_cyber ? A(NotifHistory.FromPerson(n) ? CyYellow : CyCyan, open ? (byte)0x70 : (byte)0x28) : Color.FromArgb(open ? (byte)0x20 : (byte)0x00, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(_cyber ? 2 : 1, 0, 0, _cyber ? 0 : 1),
            ToolTip = open ? null : "Click to open",
        };
        card.MouseEnter += (_, _) => { if (!open) card.Background = Frozen(_cyber ? A(CyCyan, 0x10) : Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)); };
        card.MouseLeave += (_, _) => { if (!open) card.Background = Brushes.Transparent; };
        card.PreviewMouseLeftButtonDown += (_, _) => _maybeSwipe = false;
        card.MouseLeftButtonUp += (_, e) => { if (e.Handled) return; e.Handled = true; if (!_inboxOpen.Remove(g.Key)) _inboxOpen.Add(g.Key); RenderWorkTab(animate: false); };
        return card;
    }

    // ───────────────────────────── learning what you work on ─────────────────────────────

    readonly Dictionary<uint, string> _exeOf = new();

    /// <summary>Every 5 s (with the app time): the window in front, as work, if it is.</summary>
    void TrackWork(IntPtr fg, uint pid, string app)
    {
        if (_settings.WorkPaused) return; // paused on the Work page: nothing is noted
        try
        {
            if (!_exeOf.TryGetValue(pid, out var exe)) { if (_exeOf.Count > 300) _exeOf.Clear(); _exeOf[pid] = exe = Interop.Native.ProcessPathById(pid) ?? ""; }
            if (exe.Length == 0) return;
            string title = Interop.Native.WindowTitle(fg);
            var it = WorkLog.Seen(exe, app, title, 5, DateTime.Now);
            // Now follows what you're on, but never redraws under an answer being written or a question being typed
            if (_mode == Mode.Expanded && _page == WorkPage && _workTab == 0 && !_workTyping && !_workAskRunning && PickNow()?.Key != _nowShownKey) RenderWorkTab(animate: true);
        }
        catch { }
    }
}

static class WorkUiExtensions
{
    /// <summary>Sets something up on an element and hands it back (for one-line building).</summary>
    public static T Also<T>(this T el, Action<T> set) { set(el); return el; }
}
