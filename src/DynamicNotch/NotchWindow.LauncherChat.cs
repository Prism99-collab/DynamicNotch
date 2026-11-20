using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The search turns into an AI chat (Google Gemini, free; or OpenRouter, any model): its own front end in the notch, with a header (the
/// chat's name, the model, your chats, a new one), answers written word by word, actions on every message (copy,
/// edit, retry), code blocks you can copy, the steps it takes on the PC shown as it takes them, smooth scrolling by
/// wheel and keys, and your chats kept on the PC to come back to (Ctrl+H) or delete.
/// Ways in: "Ask AI" in the results, Shift+Enter on anything typed, Tab in the empty search (back to the last chat).
/// Keys: Enter sends · Shift+Enter new line · Esc stops / goes back · Ctrl+N new · Ctrl+H chats · Ctrl+R retry ·
/// Ctrl+Shift+C copy the answer · ↑ edit your last message · PageUp/PageDown, Ctrl+↑/↓, Ctrl+Home/End scroll.
/// </summary>
public partial class NotchWindow
{
    enum ChatView { Chat, History }

    bool _chatMode;
    ChatView _chatView;
    Grid? _chatRoot, _chatHost;
    ScrollViewer? _chatScroll, _chatHistScroll;
    StackPanel? _chatStack, _chatHistList;
    TextBlock? _chatTitle, _chatModelChip, _chatHeadGlyph, _launchEsc;
    Border? _chatOrbHost;
    TextBlock? _moreButton;
    (string Name, string Text)? _chatAttach;   // a file to send with the next message
    TaskCompletionSource<bool>? _confirm;      // a command waiting for your OK
    Controls.AiOrb? _chatOrb;
    Border? _chatThumb, _chatLatest, _chatHead;
    ChatSession? _session;
    CancellationTokenSource? _chatCancel;
    bool _chatBusy, _chatAwaitingKey;
    string? _chatPending;          // asked before the key was set: sent once it is
    string _chatLastAnswer = "";
    string _chatLastUser = "";
    List<ChatStore.Info> _histItems = new();
    int _histSel;
    string? _histArmed;            // Delete pressed once on this chat: a second press deletes it
    PasswordBox? _keyBox;
    readonly HttpClient _web = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly DispatcherTimer _thumbFade = new() { Interval = TimeSpan.FromMilliseconds(900) };

    const int ChatToolRounds = 8;
    static readonly FontFamily ChatMono = new("Cascadia Mono, Consolas");
    static readonly Color Ok = Color.FromRgb(0x30, 0xD1, 0x58), Warn = Color.FromRgb(0xFF, 0xB0, 0x20);

    // scrolling: a target, and each frame the view eases toward it (the wheel moves the target, following the
    // answer keeps it pinned to the end): one mover, nothing to restart, nothing that fights
    double _chatTarget, _chatEase = 240;
    bool _chatGliding, _chatToEnd;
    TimeSpan _chatFrameAt;
    bool _chatFollow = true; // at the bottom: new text keeps it there. Scrolled up to read: it waits (Latest brings you back)

    void StopGlide()
    {
        if (!_chatGliding) return;
        _chatGliding = false;
        CompositionTarget.Rendering -= OnChatScrollFrame;
    }

    ScrollViewer? ActiveChatScroll => _chatView == ChatView.History ? _chatHistScroll : _chatScroll;
    Color ChatAccent => _cyber ? CyCyan : LaunchAccent;
    /// <summary>The model in use, for the provider in use (each keeps its own pick).</summary>
    string ChatModel
    {
        get
        {
            string m = OpenRouter.IsGemini ? _settings.AiGeminiModel : _settings.AiModel;
            return string.IsNullOrWhiteSpace(m) ? OpenRouter.DefaultModel : m.Trim();
        }
    }
    JsonArray ChatMsgs => (_session ??= NewSession()).Messages;

    // ───────────────────────────── building ─────────────────────────────

    FrameworkElement BuildChat()
    {
        _chatRoot = new Grid { Visibility = Visibility.Collapsed };
        _chatRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        _chatRoot.RowDefinitions.Add(new RowDefinition());

        // the header: what this chat is, the model, your chats, a new one
        var head = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _chatHeadGlyph = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(_chatHeadGlyph);
        _chatOrbHost = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(_chatOrbHost);
        _chatTitle = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(_chatTitle, 1);
        head.Children.Add(_chatTitle);
        _chatModelChip = new TextBlock { FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = "The model · click for the AI settings" };
        _chatModelChip.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _chatModelChip.MouseLeftButtonUp += (_, _) => ModelMenu();
        _chatModelChip.ToolTip = "Switch the model";
        var chip = new Border { Child = _chatModelChip, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chip, 2);
        head.Children.Add(chip);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(HeadButton("", "Your chats  ·  Ctrl+H", () => { if (_chatView == ChatView.History) ShowChatView(ChatView.Chat); else ShowChatView(ChatView.History); }));
        buttons.Children.Add(HeadButton("\uE710", "New chat  ·  Ctrl+N", NewChat));
        buttons.Children.Add(BuildSpeakerButton());
        _moreButton = HeadButton("\uE712", "More: attach a file, rename, export, copy, delete", MoreMenu);
        buttons.Children.Add(_moreButton);
        _pinButton = HeadButton("\uE718", "Keep it on top", TogglePin);
        _pinButton.Visibility = Visibility.Collapsed;
        buttons.Children.Add(_pinButton);
        _popButton = HeadButton("\uE8A7", "Pop out into its own window", TogglePop);
        buttons.Children.Add(_popButton);
        Grid.SetColumn(buttons, 3);
        head.Children.Add(buttons);
        _chatHead = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(2, 0, 2, 3), Background = Brushes.Transparent };
        _chatHead.MouseLeftButtonDown += (_, e) => DragPop(e);
        _chatRoot.Children.Add(_chatHead);

        // the conversation, and (in its place) your chats
        _chatHost = new Grid { ClipToBounds = true };
        Grid.SetRow(_chatHost, 1);
        _chatRoot.Children.Add(_chatHost);
        WireChatDrop(_chatHost);
        _chatStack = new StackPanel { Margin = new Thickness(2, 10, 10, 10) };
        _chatScroll = ChatScroller(_chatStack);
        _chatHost.Children.Add(_chatScroll);
        _chatHistList = new StackPanel { Margin = new Thickness(0, 4, 8, 8) };
        _chatHistScroll = ChatScroller(_chatHistList);
        _chatHistScroll.Visibility = Visibility.Collapsed;
        _chatHost.Children.Add(_chatHistScroll);

        // a thin bar that shows where you are while scrolling
        _chatThumb = new Border { Width = 2.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(1.25), Opacity = 0, IsHitTestVisible = false, RenderTransform = new TranslateTransform(), Margin = new Thickness(0, 0, 1, 0) };
        _chatHost.Children.Add(_chatThumb);
        _thumbFade.Tick += (_, _) => { _thumbFade.Stop(); _chatThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))); };

        // scrolled up while it writes: a way back to the newest
        var latest = new StackPanel { Orientation = Orientation.Horizontal };
        latest.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
        latest.Children.Add(new TextBlock { Text = "Latest", FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center });
        _chatLatest = new Border
        {
            Child = latest, Padding = new Thickness(9, 3, 10, 4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand, Visibility = Visibility.Collapsed, Opacity = 0,
        };
        _chatLatest.MouseLeftButtonUp += (_, _) => { _chatFollow = true; ChatScrollTo(double.MaxValue, 320); };
        _chatHost.Children.Add(_chatLatest);

        Grid.SetColumnSpan(_chatRoot, 2);
        return _chatRoot;
    }

    ScrollViewer ChatScroller(FrameworkElement content)
    {
        var sv = new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, CanContentScroll = false,
        };
        sv.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (e.Delta > 0 && sv == _chatScroll) _chatFollow = false;
            double from = _chatGliding && !_chatToEnd ? _chatTarget : sv.VerticalOffset;
            ChatScrollTo(from - e.Delta * 0.75, 240);
        };
        sv.ScrollChanged += (_, e) => { if (e.VerticalChange != 0 || e.ExtentHeightChange != 0) ChatScrolled(sv); };
        return sv;
    }

    Button ChatChip(string label, string? glyph)
    {
        object content = label;
        if (glyph != null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            content = row;
        }
        return new Button { Style = (Style)FindResource("Capsule"), Content = content, Height = 24, Padding = new Thickness(10, 0, 10, 0), FontSize = 11, Focusable = false };
    }

    TextBlock HeadButton(string glyph, string tip, Action act)
    {
        var b = new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11.5, Width = 24, Height = 20, TextAlignment = TextAlignment.Center,
            Padding = new Thickness(0, 4, 0, 0), Cursor = Cursors.Hand, ToolTip = tip, Foreground = DimBrush, Background = Brushes.Transparent,
        };
        b.MouseEnter += (_, _) => b.Foreground = Frozen(ChatAccent);
        b.MouseLeave += (_, _) => b.Foreground = DimBrush;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true; // a click, not a drag of the window
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); _launchBox?.Focus(); };
        return b;
    }

    /// <summary>The chat's colours for the theme in use.</summary>
    void StyleChat()
    {
        if (_chatHead == null || _chatTitle == null || _chatModelChip == null || _chatHeadGlyph == null || _chatThumb == null || _chatLatest == null) return;
        var acc = ChatAccent;
        _chatHead.BorderBrush = Frozen(A(acc, _cyber ? (byte)0x50 : (byte)0x22));
        _chatHeadGlyph.Foreground = Frozen(acc);
        _chatHeadGlyph.Visibility = Visibility.Collapsed;
        if (_chatOrbHost != null)
        {
            _chatOrb?.Stop();
            _chatOrb = new Controls.AiOrb(15, acc, AiSecond, _cyber);
            _chatOrbHost.Child = _chatOrb;
            if (_chatBusy && !Eco) _chatOrb.Busy(true);
        }
        _chatTitle.FontFamily = _cyber ? CyFont : UiText;
        _chatTitle.Foreground = _cyber ? Frozen(CyYellow) : Brushes.White;
        _chatModelChip.Foreground = Frozen(A(acc, 0xD0));
        _chatModelChip.FontFamily = _cyber ? CyFont : UiText;
        _chatThumb.Background = Frozen(A(acc, 0x90));
        _chatLatest.Background = Frozen(_cyber ? Color.FromArgb(0xF0, 0x06, 0x12, 0x1A) : Color.FromArgb(0xF0, 0x22, 0x22, 0x28));
        _chatLatest.BorderBrush = Frozen(A(acc, 0x80));
        _chatLatest.BorderThickness = new Thickness(1);
        _chatLatest.CornerRadius = new CornerRadius(_cyber ? 0 : 10);
        foreach (var t in ((StackPanel)_chatLatest.Child).Children.OfType<TextBlock>()) t.Foreground = Frozen(acc);
    }

    // ───────────────────────────── in and out ─────────────────────────────

    /// <summary>
    /// The search becomes the chat. <paramref name="first"/>: a question to ask at once. From the search, the last chat
    /// goes on if it was used lately (and Preferences say so); else a new one starts.
    /// </summary>
    void EnterChat(string? first, bool resume = false)
    {
        if (_launchBox == null || _chatRoot == null || _launchScroll == null || _launchStarters == null) return;
        if (!resume && first != null && (_session == null || !_settings.AiContinue || DateTime.Now - _session.Updated > TimeSpan.FromMinutes(30) || _session.UserTurns == 0))
            StartSession(null);
        if (resume && _session == null && _settings.AiHistory && ChatStore.List().FirstOrDefault() is { } last && ChatStore.Load(last.Id) is { } s) StartSession(s);
        _chatMode = true;
        PrepareAi();
        WireWebSearch();
        StopStartOrb();
        _launchScroll.Visibility = Visibility.Collapsed;
        ((UIElement?)_launchStarters.Parent ?? _launchStarters).Visibility = Visibility.Collapsed;
        SetPane(false);
        SetGhost(null);
        _chatRoot.Visibility = Visibility.Visible;
        StyleChat();
        ChatIn(); // it glides in (NotchWindow.LauncherChatFeel)
        _chatLineState = -1;
        _launchBox.Text = "";
        _launchBox.AcceptsReturn = true; // a pasted text keeps its lines
        if (!ChatPopped) SetChatExtra(_settings.AiChatExtra, save: false);
        ShowChatView(ChatView.Chat, animate: false);
        UpdatePopButtons();
        _chatFollow = true;
        if (_chatStack!.Children.Count == 0) RenderSession();
        OfferSelection(); // text you'd selected goes with your message (NotchWindow.LauncherChatHere)
        if (first is { Length: > 0 }) SendChat(first);
        else Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ChatScrollTo(double.MaxValue, 0));
        if (_cyber) CyberGlitch(strong: false, tint: CyCyan);
    }

    /// <summary>Back to the search (the chat is kept: Tab in the empty search returns to it).</summary>
    void LeaveChat()
    {
        if (!_chatMode) return;
        _chatMode = false;
        _chatAwaitingKey = false;
        ShowGrip(false);
        DropChatExtra();
        // back to the search: its area keeps one size again once it's laid out (smooth typing)
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!_chatMode && _launcherOpen && _launchBody is { ActualHeight: > 20, ActualWidth: > 20 } b) { b.Width = b.ActualWidth; b.Height = b.ActualHeight; }
        });
        ChatOut(); // it fades back into the search
        if (_launchBox != null) { _launchBox.AcceptsReturn = false; _launchBox.Text = ""; _launchBox.Focus(); }
        StyleLauncher();
        StyleEsc();
        Search();
    }

    ChatSession NewSession() => new() { Model = ChatModel };

    /// <summary>A command with several steps ("open brave and run a ping test", "ouvre word puis écris…").</summary>
    static readonly System.Text.RegularExpressions.Regex MultiStep = new(@"\b(and|then|after that|et|puis|ensuite|w|o)\b\s+(?:then\s+)?(open|run|play|search|find|send|write|make|start|type|click|go|do|check|test|show|put|set|close|ouvre|lance|cherche|fais|écris|ecris|mets|joue|envoie|teste)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>This question's brief (and what was searched for it), added to the AI's instructions for this answer only.</summary>
    string _turnBrief = "";

    /// <summary>Is the chat on screen (in the notch, or its own window, not minimised)?</summary>
    bool ChatOnScreen => _launcherOpen && _chatMode || _pop is { IsVisible: true, WindowState: not WindowState.Minimized };

    void StartSession(ChatSession? s)
    {
        _chatCancel?.Cancel();
        _session = s ?? NewSession();
        _chatLastAnswer = "";
        var lastUser = s?.Messages.LastOrDefault(m => m?["role"]?.ToString() == "user");
        _chatLastUser = lastUser?["shown"]?.ToString() ?? lastUser?["content"]?.ToString() ?? "";
        _chatStack?.Children.Clear();
        if (_chatMode) RenderSession();
    }

    void NewChat()
    {
        if (_chatView == ChatView.History) ShowChatView(ChatView.Chat);
        StartSession(null);
        RenderSession();
        _launchBox?.Focus();
    }

    /// <summary>The chat on screen: its messages, or the welcome (and the key's setup when there's none).</summary>
    void RenderSession()
    {
        if (_chatStack == null) return;
        _chatStack.Children.Clear();
        var s = _session ??= NewSession();
        UpdateChatHeader();
        if (!OpenRouter.HasKey) { ChatSetupCard(); return; }
        if (s.Messages.Count == 0) { ChatWelcome(); return; }
        foreach (var m in s.Messages)
        {
            string role = m?["role"]?.ToString() ?? "";
            string content = m?["content"] is JsonValue v && v.TryGetValue(out string? c) ? c : "";
            if (role == "user" && m?["auto"] != null) continue; // a picture the AI took for itself
            if (role == "user") ChatUser(m?["shown"]?.ToString() ?? content, animate: false, attached: m?["file"]?.ToString(), pictureCount: m?["pictures"] is JsonValue pv && pv.TryGetValue(out int pc) ? pc : 0,
                at: DateTime.TryParse(m?["at"]?.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var when) ? when : null);
            else if (role == "assistant")
            {
                if (m?["tool_calls"] is JsonArray calls)
                    foreach (var call in calls)
                        ChatStep(call?["function"]?["name"]?.ToString() ?? "", call?["function"]?["arguments"]?.ToString() ?? "", animate: false).Done(true, null);
                if (content.Trim().Length > 0)
                {
                    var b = ChatAssistant(animate: false);
                    b.Thinking(false);
                    RenderMarkdown(b.Body, content);
                    b.Answer = content;
                    _chatLastAnswer = content;
                }
            }
        }
        ChatKeys();
    }

    void UpdateChatHeader()
    {
        if (_chatTitle == null || _chatModelChip == null) return;
        if (_chatView == ChatView.History)
        {
            _chatTitle.Text = _cyber ? $"ARCHIVE // {_histItems.Count}" : $"Your chats · {_histItems.Count}";
            _chatModelChip.Text = "";
            return;
        }
        string t = _session is { Title.Length: > 0 } s ? s.Title : "New chat";
        _chatTitle.Text = _cyber ? t.ToUpperInvariant() : t;
        _chatModelChip.Text = (_cyber ? "◆ " : "") + ModelName(ChatModel);
        var (_, used) = OpenRouter.FreeUsedToday();
        bool free = ChatModel == OpenRouter.DefaultModel || ChatModel.EndsWith(":free");
        _chatModelChip.ToolTip = OpenRouter.IsGemini ? OpenRouter.GeminiToday() + " · click to switch"
            : free ? $"Free models · about {Math.Max(0, OpenRouter.FreeLimit - used)} of {OpenRouter.FreeLimit} requests left today (each step counts) · click to switch" : "Switch the model";
    }

    static string ModelName(string id) => id switch
    {
        OpenRouter.DefaultModel => OpenRouter.IsGemini ? (OpenRouter.HasGeminiKey && OpenRouter.HasGroqKey ? "Auto · Google + Groq" : OpenRouter.HasGroqKey && !OpenRouter.HasGeminiKey ? "Groq · Auto" : "Gemini · Auto") : "Free",
        _ when id.StartsWith("groq:") => OpenRouter.GroqName(id),
        _ when OpenRouter.IsGemini && !id.Contains('/') => OpenRouter.GeminiName(id),
        _ => id.Contains('/') ? id[(id.IndexOf('/') + 1)..].Replace(":free", " (free)") : id,
    };

    bool HasGearToShow()
    {
        try { return _devices.Devices.Count > 0 || _bt.ConnectedDevices.Any(b => b.Battery != null) || _power.HasBattery && !_power.PluggedIn; }
        catch { return false; }
    }

    /// <summary>What to offer on an empty chat: what fits this person's PC and day right now, not a fixed list.</summary>
    string[] WelcomeIdeas()
    {
        if (!_settings.AiTools) return new[] { "Explain this simply", "Write a short reply email", "Plan my day", "Translate to French" };
        var ideas = new List<string>();
        try
        {
            int toasts; lock (_recentToasts) toasts = _recentToasts.Count;
            if (toasts > 0) ideas.Add("What did I miss?");
            var low = _devices.Devices.Where(d => d.Level is < 25).Select(d => d.Kind == Services.DeviceKind.Mouse ? "mouse" : "headset").FirstOrDefault()
                ?? _bt.ConnectedDevices.Where(b => b.Battery is < 25).Select(b => b.Name).FirstOrDefault();
            if (low != null) ideas.Add($"How long will my {low} last?");
            if (_call != null) ideas.Add("Who am I in a call with?");
            else if (_history.Count > 0) ideas.Add("My last call");
            if (_reminders.Count > 0) ideas.Add("What's next today?");
            if (_media.Title.Length > 0) ideas.Add(_media.IsPlaying ? "What's this song about?" : "Play my music");
            if (_files.Items.Any()) ideas.Add("Summarise my latest download");
            ideas.Add("What's using my PC?");
            ideas.Add("My day so far");
            if (_settings.AiWeb) ideas.Add("Today's top news");
            ideas.Add("Remind me to stretch in 30 min");
            ideas.Add("Find the last PDF I opened");
        }
        catch { ideas = new() { "My day so far", "What's using my PC?", "Today's top news", "Remind me to stretch in 30 min" }; }
        return ideas.Distinct().Take(6).ToArray();
    }

    /// <summary>
    /// The empty chat: the AI's mark, alive in the middle (it orbits and breathes, with soft rings going out like a
    /// signal), a greeting for the time of day, and what it could do for you right now as cards, each with its icon in
    /// the colour of what it touches (the notch, the PC, the web, files). A click puts the words in the box.
    /// </summary>
    void ChatWelcome()
    {
        var acc = ChatAccent;
        var box = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Tag = "welcome", HorizontalAlignment = HorizontalAlignment.Stretch };

        // the AI's face (NotchWindow.LauncherChatFace)
        box.Children.Add(WelcomeFace(acc));

        // the greeting (NotchWindow.LauncherChatGreeting)
        box.Children.Add(Greeting());
        // what's left of the free AI today (NotchWindow.LauncherChatQuota)
        if (QuotaCard() is { } quota) box.Children.Add(quota);

        // what it could do right now, as cards
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 0) };
        int k = 0;
        foreach (var idea in WelcomeIdeas())
        {
            var (glyph, tone) = IdeaLook(idea);
            var icon = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(_cyber ? 0 : 7), VerticalAlignment = VerticalAlignment.Center,
                Background = Frozen(A(tone, 0x26)), BorderBrush = Frozen(A(tone, 0x60)), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Frozen(tone), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var words = new TextBlock { Text = idea, FontSize = 11.5, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 4, 0), FontFamily = _cyber ? CyFont : UiText };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(icon);
            Grid.SetColumn(words, 1);
            row.Children.Add(words);
            Brush rest = Frozen(new LinearGradientBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x05, 0xFF, 0xFF, 0xFF), 90));
            Brush lit = Frozen(new LinearGradientBrush(A(tone, 0x22), A(tone, 0x08), 0));
            var card = new Border
            {
                Child = row, Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(k % 2 == 0 ? 0 : 4, 0, k % 2 == 0 ? 4 : 0, 8), MinHeight = 42, Cursor = Cursors.Hand,
                CornerRadius = new CornerRadius(_cyber ? 0 : 10), Background = rest, BorderBrush = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                RenderTransform = new TranslateTransform(), Opacity = Eco ? 1 : 0,
            };
            card.MouseEnter += (_, _) => { card.Background = lit; card.BorderBrush = Frozen(A(tone, 0x90)); };
            card.MouseLeave += (_, _) => { card.Background = rest; card.BorderBrush = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)); };
            card.MouseLeftButtonDown += (_, e) => e.Handled = true;
            var text = idea;
            card.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_launchBox == null) return; _launchBox.Text = text; _launchBox.CaretIndex = text.Length; _launchBox.Focus(); };
            if (!Eco)
            {
                var begin = TimeSpan.FromMilliseconds(160 + 55 * k);
                card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin });
                ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            grid.Children.Add(card);
            k++;
        }
        box.Children.Add(grid);
        box.Children.Add(new TextBlock
        {
            Text = "Type / for commands  ·  Ctrl+H for your chats", FontSize = 10, Foreground = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4), FontFamily = _cyber ? CyFont : UiText,
        });
        _chatStack!.Children.Add(box);
        Arrive(box);
        ChatKeys();
    }

    /// <summary>An idea's icon and colour, by what it touches.</summary>
    (string Glyph, Color Tone) IdeaLook(string idea)
    {
        string i = idea.ToLowerInvariant();
        return i switch
        {
            _ when i.Contains("miss") => ("\uEA8F", StepTone("notch_data")),
            _ when i.Contains("call") => ("\uE717", StepTone("notch_data")),
            _ when i.Contains("last") && (i.Contains("mouse") || i.Contains("headset") || i.Contains("battery")) => ("\uE83F", StepTone("pc_status")),
            _ when i.Contains("next") || i.Contains("remind") => ("\uE823", StepTone("notch_data")),
            _ when i.Contains("song") || i.Contains("music") => ("\uE8D6", StepTone("notch_data")),
            _ when i.Contains("download") || i.Contains("pdf") || i.Contains("file") => ("\uE8A5", StepTone("find_files")),
            _ when i.Contains("pc") => ("\uE7F4", StepTone("pc_status")),
            _ when i.Contains("day") => ("\uE787", StepTone("notch_data")),
            _ when i.Contains("news") => ("\uE789", StepTone("news")),
            _ when i.Contains("email") || i.Contains("write") => ("\uE70F", StepTone("write_file")),
            _ when i.Contains("translate") => ("\uE8C1", StepTone("news")),
            _ => ("\uE945", ChatAccent),
        };
    }

    // ───────────────────────────── the key: set up by anyone, in the chat ─────────────────────────────

    /// <summary>A Google (AIza…) or OpenRouter (sk-or-…) key in what was pasted (quotes, spaces, "Bearer", text around it are fine).</summary>
    string? KeyIn(string text)
    {
        var q = Regex.Match(text, @"gsk_[A-Za-z0-9]{20,}");
        if (q.Success) return q.Value;
        var g = Regex.Match(text, @"AIza[0-9A-Za-z_\-]{30,}|AQ\.[0-9A-Za-z_\-]{30,}");
        if (g.Success) return g.Value;
        var m = Regex.Match(text, @"sk-or-[A-Za-z0-9_\-]{20,}");
        if (m.Success) return m.Value;
        string t = Regex.Replace(text.Trim().Trim('"', '\'', '`'), @"^Bearer\s+", "", RegexOptions.IgnoreCase);
        return _chatAwaitingKey && Regex.IsMatch(t, @"^[A-Za-z0-9_\-]{32,}$") ? t : null; // OpenRouter may change how keys start
    }

    // ───────────────────────────── your chats ─────────────────────────────

    void ShowChatView(ChatView view, bool animate = true)
    {
        if (_chatScroll == null || _chatHistScroll == null || _launchBox == null) return;
        bool changed = _chatView != view;
        _chatView = view;
        StopGlide();
        var show = view == ChatView.History ? _chatHistScroll : _chatScroll;
        var hide = view == ChatView.History ? _chatScroll : _chatHistScroll;
        hide.Visibility = Visibility.Collapsed;
        show.Visibility = Visibility.Visible;
        if (animate && changed && !Eco)
        {
            show.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            var slide = new TranslateTransform(view == ChatView.History ? 14 : -14, 0);
            show.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        if (view == ChatView.History)
        {
            _launchBox.Text = "";
            _histSel = 0;
            _histArmed = null;
            LoadHistory("");
        }
        else _launchBox.Text = "";
        StyleChatLine();
        UpdateChatHeader();
        ChatKeys();
        if (_chatLatest != null) _chatLatest.Visibility = Visibility.Collapsed;
    }

    readonly Dictionary<string, string> _histSnips = new();

    void LoadHistory(string filter)
    {
        _histSnips.Clear();
        if (filter.Trim().Length < 2) _histItems = ChatStore.List();
        else
        {
            var found = ChatStore.Search(filter.Trim());
            _histItems = found.Select(f => f.Info).ToList();
            foreach (var f in found) if (f.Snippet.Length > 0) _histSnips[f.Info.Id] = f.Snippet;
        }
        _histSel = Math.Clamp(_histSel, 0, Math.Max(0, _histItems.Count - 1));
        RenderHistory();
        UpdateChatHeader();
    }

    void RenderHistory()
    {
        if (_chatHistList == null) return;
        _chatHistList.Children.Clear();
        if (_histItems.Count == 0)
        {
            _chatHistList.Children.Add(new TextBlock
            {
                Text = !_settings.AiHistory ? "Keeping chats is off (Preferences → AI chat)." : _launchBox?.Text.Length > 0 ? "No chat mentions that." : "No chats yet. They're kept here as you go.",
                FontSize = 11.5, Foreground = DimBrush, Margin = new Thickness(4, 10, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        var acc = ChatAccent;
        var rows = new List<FrameworkElement>();
        string? lastGroup = null;
        for (int i = 0; i < _histItems.Count; i++)
        {
            var item = _histItems[i];
            int index = i;
            bool sel = i == _histSel, armed = _histArmed == item.Id, current = _session?.Id == item.Id;

            // a heading when the day changes: Today, Yesterday, This week, Earlier
            string group = HistGroup(item.Updated);
            if (group != lastGroup)
            {
                lastGroup = group;
                var head = new Grid { Margin = new Thickness(2, _chatHistList.Children.Count == 0 ? 2 : 10, 2, 5) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition());
                head.Children.Add(new TextBlock { Text = _cyber ? group.ToUpperInvariant() : group, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(_cyber ? CyYellow : A(acc, 0xD0)), FontFamily = _cyber ? CyFont : UiText, VerticalAlignment = VerticalAlignment.Center });
                var rule = new Border { Height = 1, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = Frozen(new LinearGradientBrush(A(acc, 0x50), A(acc, 0x00), 0)) };
                Grid.SetColumn(rule, 1);
                head.Children.Add(rule);
                _chatHistList.Children.Add(head);
            }

            var (glyph, tone) = ChatTopicLook(item.Title);
            if (armed) tone = Color.FromRgb(0xFF, 0x5A, 0x5F);
            var row = new Grid { Height = 46, Margin = new Thickness(0, 0, 0, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            var plate = new Border
            {
                CornerRadius = new CornerRadius(_cyber ? 0 : 10), BorderThickness = new Thickness(_cyber && sel ? 2 : 1, 1, 1, 1),
                Background = armed ? Frozen(new LinearGradientBrush(Color.FromArgb(0x38, 0xFF, 0x45, 0x3A), Color.FromArgb(0x10, 0xFF, 0x45, 0x3A), 0))
                    : sel ? Frozen(new LinearGradientBrush(A(tone, 0x2A), A(acc, 0x08), 0)) : Frozen(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF)),
                BorderBrush = Frozen(armed ? Color.FromArgb(0x90, 0xFF, 0x45, 0x3A) : sel ? A(tone, _cyber ? (byte)0xC0 : (byte)0x70) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            };
            Grid.SetColumnSpan(plate, 4);
            row.Children.Add(plate);

            // its topic, as a small tile
            var icon = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(_cyber ? 0 : 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Background = Frozen(new LinearGradientBrush(A(tone, sel ? (byte)0x44 : (byte)0x26), A(tone, 0x0C), 45)), BorderBrush = Frozen(A(tone, sel ? (byte)0x90 : (byte)0x50)), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = Frozen(tone), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            row.Children.Add(icon);

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
            words.Children.Add(new TextBlock { Text = item.Title.Length > 0 ? item.Title : "Untitled", FontSize = 12, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal, FontFamily = _cyber ? CyFont : UiText });
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            if (armed) meta.Children.Add(new TextBlock { Text = "Press Delete again to remove it", FontSize = 10, Foreground = Frozen(Color.FromRgb(0xFF, 0x7A, 0x70)) });
            else if (_histSnips.TryGetValue(item.Id, out var snip))
                meta.Children.Add(new TextBlock { Text = "…" + snip + "…", FontSize = 10, Foreground = Frozen(Lighten(acc, 0.3)), TextTrimming = TextTrimming.CharacterEllipsis, FontStyle = FontStyles.Italic });
            else
            {
                meta.Children.Add(new TextBlock { Text = "\uE8BD", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 4, 0) });
                meta.Children.Add(new TextBlock { Text = $"{item.Turns}", FontSize = 10, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center });
                meta.Children.Add(new TextBlock { Text = "  ·  " + HistWhen(item.Updated), FontSize = 10, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center });
            }
            Grid.SetColumn(words, 1);
            row.Children.Add(words);

            // the chat that's open now says so
            if (current && !armed)
            {
                var open = new Border
                {
                    CornerRadius = new CornerRadius(_cyber ? 0 : 7), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0),
                    Background = Frozen(A(acc, 0x26)), BorderBrush = Frozen(A(acc, 0x80)), BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = _cyber ? "OPEN" : "Open", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Frozen(acc), FontFamily = _cyber ? CyFont : UiText },
                };
                Grid.SetColumn(open, 2);
                row.Children.Add(open);
            }

            var bin = new TextBlock { Text = "\uE74D", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = armed ? Frozen(Color.FromRgb(0xFF, 0x7A, 0x70)) : DimBrush, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Opacity = sel || armed ? 0.9 : 0, ToolTip = "Delete this chat", Background = Brushes.Transparent, Width = 24, Height = 24, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 6, 0, 0) };
            bin.MouseEnter += (_, _) => bin.Foreground = Frozen(Color.FromRgb(0xFF, 0x7A, 0x70));
            bin.MouseLeave += (_, _) => { if (!armed) bin.Foreground = DimBrush; };
            bin.MouseLeftButtonUp += (_, e) => { e.Handled = true; DeleteChat(item.Id); };
            Grid.SetColumn(bin, 3);
            row.Children.Add(bin);
            row.MouseEnter += (_, _) =>
            {
                bin.Opacity = 0.9;
                if (!sel && !armed) { plate.Background = Frozen(new LinearGradientBrush(A(tone, 0x18), Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF), 0)); plate.BorderBrush = Frozen(A(tone, 0x40)); }
            };
            row.MouseLeave += (_, _) =>
            {
                if (sel || armed) return;
                bin.Opacity = 0;
                plate.Background = Frozen(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF));
                plate.BorderBrush = Frozen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            };
            row.MouseLeftButtonUp += (_, _) => { _histSel = index; OpenHistory(); };
            _chatHistList.Children.Add(row);
            rows.Add(row);
        }
        // the picked line stays in view
        if (_chatHistScroll != null && _histSel < rows.Count)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (_histSel >= rows.Count || _chatHistScroll == null) return;
                double y;
                try { y = rows[_histSel].TransformToAncestor(_chatHistList).Transform(new Point(0, 0)).Y; } catch { return; }
                double h = rows[_histSel].ActualHeight, view = _chatHistScroll.ViewportHeight;
                if (y - 24 < _chatHistScroll.VerticalOffset) ChatScrollTo(y - 28, 180);
                else if (y + h > _chatHistScroll.VerticalOffset + view) ChatScrollTo(y + h + 6 - view, 180);
            });
    }

    static string HistGroup(DateTime t)
    {
        var day = t.Date;
        if (day == DateTime.Today) return "Today";
        if (day == DateTime.Today.AddDays(-1)) return "Yesterday";
        if (day > DateTime.Today.AddDays(-7)) return "This week";
        if (day > DateTime.Today.AddDays(-31)) return "This month";
        return "Earlier";
    }

    /// <summary>Today: the time it was last used; this week: the day; else the date.</summary>
    static string HistWhen(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (t.Date == DateTime.Today) return t.ToString("HH:mm");
        if (t.Date > DateTime.Today.AddDays(-7)) return t.ToString("ddd HH:mm");
        return t.ToString("d MMM");
    }

    /// <summary>A chat's icon and colour, from what its name is about.</summary>
    (string Glyph, Color Tone) ChatTopicLook(string title)
    {
        string t = title.ToLowerInvariant();
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"picture|image|photo|screenshot|wallpaper|draw")) return ("\uEB9F", StepTone("create_image"));
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"code|script|bug|error|powershell|python|c#|function")) return ("\uE943", StepTone("run_powershell"));
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"table|excel|sheet|word|pdf|doc|file|folder|download")) return ("\uE8A5", StepTone("find_files"));
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"battery|pc|cpu|ram|memory|gpu|setup|laptop|computer|manufactur")) return ("\uE7F4", StepTone("pc_status"));
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"call|discord|teams|message|whatsapp|alert|notif|remind|timer|music|song|spotify")) return ("\uE717", StepTone("notch_data"));
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"news|when|who|what is|price|weather|release|latest|search|tab|brave|chrome|web")) return ("\uE774", StepTone("news"));
        return ("\uE8BD", ChatAccent);
    }

    static string ChatAgo(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "yesterday";
        return d.TotalDays < 7 ? t.ToString("dddd") : t.ToString("d MMM");
    }

    void OpenHistory()
    {
        if (_histSel >= _histItems.Count) return;
        var s = ChatStore.Load(_histItems[_histSel].Id);
        if (s == null) { Bump(1); return; }
        ShowChatView(ChatView.Chat);
        StartSession(s);
        RenderSession();
        _chatFollow = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ChatScrollTo(double.MaxValue, 0));
    }

    void DeleteChat(string id)
    {
        ChatStore.Delete(id);
        if (_session?.Id == id) { _session = null; _chatStack?.Children.Clear(); }
        _histArmed = null;
        LoadHistory(_launchBox?.Text ?? "");
    }

    // ───────────────────────────── keys ─────────────────────────────

    void StyleChatLine()
    {
        if (_launchPrompt == null || _launchHint == null || _launchBox == null) return;
        _launchPrompt.Text = _cyber ? "»" : _chatView == ChatView.History ? "" : "";
        _launchPrompt.FontFamily = _cyber ? CyFont : (FontFamily)FindResource("IconFont");
        _launchPrompt.FontSize = _cyber ? 22 : 14;
        _launchHint.Text = _launchBox.Text.Length > 0 ? "" : _chatView == ChatView.History ? (_cyber ? "SEARCH // ALL CHATS" : "Search your chats…")
            : _chatAwaitingKey ? (_cyber ? "KEY FIRST // SEE BELOW" : "Paste your key in the box below")
            : _cyber ? "ASK // OR GIVE AN ORDER" : "Ask anything, or tell it what to do…";
        StyleEsc();
    }

    /// <summary>The word at the end of the line: esc in the search; send, or stop while it writes, in the chat.</summary>
    void StyleEsc()
    {
        if (_launchEsc == null) return;
        if (!_chatMode) { _launchEsc.Text = "esc"; _launchEsc.Foreground = DimBrush; _launchEsc.FontWeight = FontWeights.Normal; _launchEsc.ToolTip = null; ChatTypeGlow(false); return; }
        bool typed = _launchBox?.Text.Trim().Length > 0;
        _launchEsc.Text = _chatBusy ? "■ stop" : _chatView == ChatView.History ? "back" : "send ↵";
        // send lights up only when there's something to send
        _launchEsc.Foreground = Frozen(_chatBusy ? Warn : _chatView == ChatView.History || typed ? A(ChatAccent, 0xF0) : Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
        _launchEsc.FontWeight = typed && !_chatBusy && _chatView == ChatView.Chat ? FontWeights.SemiBold : FontWeights.Normal;
        ChatTypeGlow(typed && _chatView == ChatView.Chat);
        _launchEsc.ToolTip = _chatBusy ? "Stop the answer  ·  Esc" : null;
    }

    void OnLaunchEscClick()
    {
        if (!_chatMode) { CloseLauncher(giveBack: true); return; }
        if (_chatBusy) { _chatCancel?.Cancel(); return; }
        if (_chatView == ChatView.History) { ShowChatView(ChatView.Chat); return; }
        SendTyped();
    }

    void ChatKeys()
    {
        if (_chatOrb != null) { if (_chatBusy && !Eco) _chatOrb.Busy(true); else { _chatOrb.Busy(false); _chatOrb.Stop(); } }
        if (!_chatMode) return;
        if (_confirm != null) SetKeys(("↵", "Run"), ("Esc", "Cancel"));
        else if (_chatView == ChatView.History) SetKeys(("↵", "Open"), ("Del", "Delete"), ("Ctrl N", "New"), ("Esc", "Back"));
        else if (_chatBusy) SetKeys(("Esc", "Stop"));
        else if (_chatAwaitingKey) SetKeys(("↵", "Save key"), ("Esc", "Back"));
        else SetKeys(("↵", "Send"), ("Ctrl H", "Chats"), ("Ctrl N", "New"), ("Esc", ChatPopped ? "Dock" : "Back"));
        StyleEsc();
    }

    void OnChatKey(KeyEventArgs e)
    {
        if (_launchBox == null) return;
        var mods = Keyboard.Modifiers;
        if (_chatView == ChatView.History) { OnHistoryKey(e); return; }
        if (e.Key == Key.M && mods == ModifierKeys.Control) { e.Handled = true; ToggleListening(); return; }
        if (e.Key == Key.Escape && _listening) { e.Handled = true; FinishListening(cancel: true); return; }
        if (e.Key == Key.Escape && VoiceOut.Speaking && !_chatBusy) { e.Handled = true; VoiceOut.Stop(); return; }
        if (e.Key == Key.Z && mods == ModifierKeys.Control && _launchBox.Text.Length == 0) { e.Handled = true; Toast(UndoLast()); return; }
        if (_confirm != null && e.Key is Key.Enter or Key.Escape && _launchBox.Text.Length == 0)
        {
            e.Handled = true;
            _confirm.TrySetResult(e.Key == Key.Enter);
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_chatBusy) { _chatCancel?.Cancel(); break; }
                if (_launchBox.Text.Length > 0) { _launchBox.Text = ""; break; }
                if (ChatPopped) PopIn(); else LeaveChat();
                break;
            case Key.Enter when mods == ModifierKeys.Shift:
                e.Handled = true;
                int at = _launchBox.CaretIndex;
                _launchBox.Text = _launchBox.Text.Insert(at, "\n");
                _launchBox.CaretIndex = at + 1;
                break;
            case Key.Enter:
                e.Handled = true;
                SendTyped();
                break;
            case Key.N when mods == ModifierKeys.Control:
                e.Handled = true;
                NewChat();
                break;
            case Key.V when mods == ModifierKeys.Control && !Clipboard.ContainsText():
                if (PastePicture()) e.Handled = true;
                break;
            case Key.H when mods == ModifierKeys.Control:
                e.Handled = true;
                ShowChatView(ChatView.History);
                break;
            case Key.R when mods == ModifierKeys.Control:
                e.Handled = true;
                Retry();
                break;
            case Key.C when mods == (ModifierKeys.Control | ModifierKeys.Shift):
            case Key.C when mods == ModifierKeys.Control && _launchBox.SelectionLength == 0:
                e.Handled = true;
                CopyText(_chatLastAnswer, "Answer copied");
                break;
            case Key.Up when _launchBox.Text.Length == 0 && _chatLastUser.Length > 0 && mods == ModifierKeys.None:
                e.Handled = true;
                EditMessage(_chatLastUser);
                break;
            case Key.PageUp:
            case Key.PageDown:
                e.Handled = true;
                if (e.Key == Key.PageUp) _chatFollow = false;
                if (_chatScroll != null) ChatScrollTo((_chatGliding && !_chatToEnd ? _chatTarget : _chatScroll.VerticalOffset) + (e.Key == Key.PageUp ? -1 : 1) * _chatScroll.ViewportHeight * 0.85, 300);
                break;
            case Key.Up when mods == ModifierKeys.Control:
            case Key.Down when mods == ModifierKeys.Control:
                e.Handled = true;
                if (e.Key == Key.Up) _chatFollow = false;
                if (_chatScroll != null) ChatScrollTo((_chatGliding && !_chatToEnd ? _chatTarget : _chatScroll.VerticalOffset) + (e.Key == Key.Up ? -48 : 48), 200);
                break;
            case Key.Home when mods == ModifierKeys.Control:
                e.Handled = true;
                _chatFollow = false;
                ChatScrollTo(0, 360);
                break;
            case Key.End when mods == ModifierKeys.Control:
                e.Handled = true;
                _chatFollow = true;
                ChatScrollTo(double.MaxValue, 360);
                break;
            case Key.Tab:
                e.Handled = true;
                break;
        }
    }

    void OnHistoryKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_launchBox!.Text.Length > 0) { _launchBox.Text = ""; LoadHistory(""); }
                else ShowChatView(ChatView.Chat);
                break;
            case Key.Down:
            case Key.Up:
                e.Handled = true;
                if (_histItems.Count == 0) break;
                _histSel = (_histSel + (e.Key == Key.Down ? 1 : -1) + _histItems.Count) % _histItems.Count;
                _histArmed = null;
                RenderHistory();
                break;
            case Key.Enter:
                e.Handled = true;
                OpenHistory();
                break;
            case Key.Delete:
                e.Handled = true;
                if (_histSel >= _histItems.Count) break;
                string id = _histItems[_histSel].Id;
                if (_histArmed == id) DeleteChat(id); else { _histArmed = id; RenderHistory(); }
                break;
            case Key.N when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                NewChat();
                break;
            case Key.H when Keyboard.Modifiers == ModifierKeys.Control:
            case Key.Tab:
                e.Handled = true;
                ShowChatView(ChatView.Chat);
                break;
        }
    }

    /// <summary>What's typed in the history's box filters it; in the chat, the hint goes away as you type.</summary>
    void OnChatTyped()
    {
        if (!_chatMode) return;
        if (_chatView == ChatView.Chat && _launchBox?.Text == "/") CommandMenu();
        if (_chatView == ChatView.History) { _histSel = 0; _histArmed = null; LoadHistory(_launchBox?.Text ?? ""); }
        StyleChatLineIfChanged(); // not on every key
    }

    void SendTyped()
    {
        if (_launchBox == null) return;
        string text = _launchBox.Text.Trim();
        if (text.Length == 0 && _chatPictures.Count == 0) { Nudge(); return; }
        if (text.Length == 0) text = "What's in this picture?";
        _launchBox.Text = "";
        SendChat(text);
    }

    void EditMessage(string text)
    {
        if (_launchBox == null) return;
        _launchBox.Text = text;
        _launchBox.CaretIndex = text.Length;
        _launchBox.Focus();
    }

    /// <summary>The last answer, asked again (the question and what followed it are taken back first).</summary>
    void Retry()
    {
        if (_chatBusy || _session == null) return;
        var msgs = _session.Messages;
        int lastUser = -1;
        for (int i = msgs.Count - 1; i >= 0; i--) if (msgs[i]?["role"]?.ToString() == "user") { lastUser = i; break; }
        if (lastUser < 0) { Nudge(); return; }
        string q = msgs[lastUser]?["shown"]?.ToString() ?? msgs[lastUser]?["content"]?.ToString() ?? "";
        while (msgs.Count > lastUser) msgs.RemoveAt(msgs.Count - 1);
        RenderSession();
        SendChat(q);
    }

    void CopyText(string text, string said)
    {
        if (text.Length == 0) { Nudge(); return; }
        try { Clipboard.SetText(text); } catch { }
        Toast(said);
    }

    /// <summary>A small word at the bottom of the chat for a moment ("Copied").</summary>
    void Toast(string text)
    {
        if (_chatHost == null) return;
        var t = new Border
        {
            Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = Brushes.White }, Padding = new Thickness(9, 3, 9, 4), CornerRadius = new CornerRadius(_cyber ? 0 : 9),
            Background = Frozen(A(ChatAccent, 0xC0)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8), IsHitTestVisible = false,
        };
        if (_cyber) ((TextBlock)t.Child).Foreground = Frozen(CyNight);
        _chatHost.Children.Add(t);
        var fade = new DoubleAnimationUsingKeyFrames { KeyFrames = { new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))), new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100))), new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1400))) } };
        fade.Completed += (_, _) => _chatHost.Children.Remove(t);
        t.Opacity = 0;
        t.BeginAnimation(OpacityProperty, fade);
    }

    // ───────────────────────────── sending ─────────────────────────────

    async void SendChat(string text)
    {
        if (CalendarFeed.FindLink(text) is { } calLink && text.Trim().Length < calLink.Length + 60) { ConnectCalendar(calLink); return; } // never sent to the AI
        if (KeyIn(text) is { } groqKey && groqKey.StartsWith("gsk_") && text.Trim().Length < 120 && _keyBox == null) { if (_launchBox != null) _launchBox.Text = ""; GroqKeyPasted(groqKey); return; } // the second engine, added on the spot
        if (KeyIn(text) is { } pasted && text.Trim().Length < 120) { _chatAwaitingKey = true; if (_keyBox == null) ChatSetupCard(); if (_keyBox != null) _keyBox.Password = pasted; return; }
        if (text.StartsWith('/') && ChatCommand(text)) return;
        if (!OpenRouter.HasKey || OpenRouter.LoadKey() is not { } key)
        {
            _chatPending = text;
            if (_keyBox == null) ChatSetupCard();
            return;
        }
        VoiceOut.Stop();
        if (_chatBusy) { _chatQueue.Enqueue(text); Toast(_chatQueue.Count == 1 ? "Sends when this answer is done" : $"{_chatQueue.Count} messages waiting"); return; }
        ClearFollowUps();
        var session = _session ??= NewSession();
        if (session.Title.Length == 0) session.Title = TitleOf(text);
        session.Model = ChatModel;
        _chatLastUser = text;
        // "what's on my screen?": a real picture of it goes with the question
        if (ScreenAsk.IsMatch(text) && _chatPictures.Count == 0 && _settings.AiTools && await CaptureScreen() is { } screenShot) _chatPictures.Add(screenShot);
        // the same question again right after itself (a retry): kept once
        if (session.Messages.Count > 0 && session.Messages[^1] is System.Text.Json.Nodes.JsonObject lastQ && lastQ["role"]?.ToString() == "user" && lastQ["shown"]?.ToString() == text) session.Messages.RemoveAt(session.Messages.Count - 1);
        string? attachedName = null;
        string content = text;
        // "what's on my screen?": the real app names go with the picture (Brave and Chrome look alike)
        if (ScreenAsk.IsMatch(text) && _settings.AiTools && ScreenFacts() is { } facts) content = text + "\n\n" + facts;
        TakeOfferedSelection(text, chip: false); // the selection goes only if the message is about it (NotchWindow.LauncherChatHere)
        if (_chatAttach is { } att)
        {
            attachedName = att.Name;
            content = text + "\n\n[Attached file: " + att.Name + "]\n" + att.Text;
            _chatAttach = null;
        }
        var pictures = _chatPictures.ToList();
        _chatPictures.Clear();
        ClearAttachNote();
        JsonNode body = content;
        if (pictures.Count > 0)
        {
            var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = content } };
            foreach (var jpeg in pictures) parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = DataUri(jpeg) } });
            body = parts;
        }
        session.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = body, ["shown"] = text, ["file"] = attachedName, ["pictures"] = pictures.Count > 0 ? pictures.Count : null, ["at"] = DateTime.Now.ToString("o") });
        foreach (var w in _chatStack?.Children.OfType<FrameworkElement>().Where(x => x.Tag as string == "welcome").ToList() ?? new()) _chatStack!.Children.Remove(w); // the welcome makes way
        ChatUser(text, attached: attachedName, pictures: pictures, at: DateTime.Now);
        UpdateChatHeader();
        if (pictures.Count == 0 && attachedName == null && await TryInstant(text, session)) return; // NotchWindow.LauncherChatInstant

        _chatBusy = true;
        _askedAt = DateTime.Now;
        _lastProblem = null;
        _actUnchecked = false;
        ChatKeys();
        _chatCancel?.Dispose();
        _chatCancel = new CancellationTokenSource();
        var cancel = _chatCancel.Token;
        string model = pictures.Count > 0 ? await ModelForPictures(ChatModel) : ChatModel;
        double temperature = _settings.AiCreativity switch { "precise" => 0.2, "creative" => 1.0, _ => 0.6 };
        string effort = "low";
        string finalAnswer = "";
        bool acted = false;
        _turnSources.Clear();
        var startedAt = DateTime.Now;
        int stepCount = 0, tokenCount = 0;
        var turnSteps = new List<(string Tool, string Args, bool Ok)>(); // what it did, for what it learns (AiLearn)
        bool goalChecked = false; // the "is it really done?" look, once per task
        var turnLog = new StringBuilder(); // where the time went (app.log), to find what makes an answer slow
        int turnStart = _chatStack?.Children.Count ?? 0;
        var stepNames = new List<string>();
        DispatcherTimer? liveTick = null;
        var ask = AskKind(text); // what kind of question (NotchWindow.LauncherChatQuality)
        effort = EffortFor(ask, text, _settings.AiStyle == "detailed"); // thinks more only where it changes the answer
        OpenRouter.SmartNext = ask == Ask.Correction || ScreenAsk.IsMatch(text) || effort != "low" || ask == Ask.Fact && HardWords.IsMatch(text) // getting these right matters more than a second
            ; // tasks stay on the quick order: Groq's strongest model first (fast, ~1,000 a day); Google's Flash has ~20 a day
        // a harder PC task: the tested way to do it goes with the question, and the smart model takes it (Services/AiPlaybook)
        string? knowHow = ask is Ask.Fact or Ask.News or Ask.Correction ? null : AiPlaybook.Brief(text);
        if (knowHow != null) { _turnBrief += "\n" + knowHow; OpenRouter.SmartNext = true; }
        if (LatinOnly(text) && DarijaWords.IsMatch(text)) _turnBrief += "\nThe user writes Darija in Latin letters: reply in Darija, in Latin letters (like 'Labas, hamdullah! W nta?'), never in Arabic script.";
        else if (LatinOnly(text)) _turnBrief += "\nThe user typed in Latin letters: answer in the same language they wrote (French stays French, English stays English), in Latin letters only (Darija typed in Latin letters gets Latin letters back, never Arabic script).";
        if (ChartWords.IsMatch(text)) _turnBrief += "\nTHIS MESSAGE wants a chart: write it as a chart block (see CHARTS) with the real numbers, never a picture.";
        if (ThinkWords.IsMatch(text)) _turnBrief += "\nThe user asked for depth: a fuller answer (short sections, the key numbers, what's certain and what isn't), still no filler.";
        _turnBrief = BriefFor(ask);
        try
        {
            for (int round = 0; round <= ChatToolRounds; round++)
            {
                var bubble = ChatAssistant();
                // a fact or the news: searched right away, so the answer comes in one go
                if (round == 0 && ask is Ask.Fact or Ask.News && _settings.AiWeb && pictures.Count == 0 && attachedName == null)
                {
                    bubble.Thinking(true, _cyber ? (ask == Ask.News ? "FETCHING HEADLINES" : "SEARCHING") : ask == Ask.News ? "Getting the headlines" : "Searching the web");
                    var ahead = await SearchAhead(ask, text, cancel);
                    if (ahead != null) _turnBrief += "\n" + ahead;
                    bubble.Thinking(true, _cyber ? "WRITING" : ahead != null ? $"Reading {Math.Max(1, _turnSources.Count)} sources" : "Thinking");
                }
                else if (round == 0 && ask == Ask.Day)
                {
                    bubble.Thinking(true, _cyber ? "SYNCING DAY" : "Checking your day");
                    try { _turnBrief += "\n" + await MyDayText(); } catch { }
                }
                else if (round == 0 && HereAsk(text) is { } here && !(attachedName?.StartsWith("Selected in") ?? false))
                {
                    // "summarize this page": the window behind the notch, read before the AI is asked
                    bubble.Thinking(true, _cyber ? "READING SCREEN" : here == "selection" ? "Reading the selection" : "Reading the page");
                    try { _turnBrief += "\nWHAT THE USER IS LOOKING AT (read just now; 'this' means this):\n" + await ReadHere(here, cancel); } catch { }
                }
                else if (round == 0) bubble.Thinking(true, _cyber ? "UNDERSTANDING" : "Understanding");
                var live = new StringBuilder();
                var tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
                bool dirty = false;
                tick.Tick += (_, _) =>
                {
                    if (!dirty) return;
                    // nobody's looking (you went to another app): nothing to draw; the whole answer is drawn once at the end
                    if (!ChatOnScreen) return;
                    // a long answer is redrawn less often, and less still on battery saver: the words still flow
                    var every = TimeSpan.FromMilliseconds(Math.Min(160, (Eco ? 120 : 50) + live.Length / 120)); // only the last paragraph is redrawn now: it can stay quick
                    if (tick.Interval != every) tick.Interval = every;
                    dirty = false;
                    string raw; lock (live) raw = live.ToString();
                    string shown = CleanAnswer(raw, streaming: true);
                    if (shown.Length == 0) { bubble.Thinking(true); return; } // only its thinking so far
                    bubble.Thinking(false);
                    RenderMarkdownLive(bubble.Body, shown); // only what changed is redrawn
                    FollowChat();
                };
                tick.Start();
                liveTick = tick;
                var tools = round < ChatToolRounds && ask != Ask.Talk ? ToolsForKind(ChatToolsFor(), knowHow != null ? Ask.Action : ask) : null; // only what this kind of question needs
                if (tools != null && ChartWords.IsMatch(text)) tools = WithoutTool(tools, "create_image"); // a chart is drawn, not painted
                OpenRouter.Turn turn;
                int attempt = 0;
                while (true)
                {
                    var request = BuildRequest(session, tools);
                    var roundClock = System.Diagnostics.Stopwatch.StartNew();
                    OpenRouter.ChatRequest.Value = true; // Groq gets the chat's short instructions (only the chat's)
                    turn = await OpenRouter.Stream(key, model, request, tools,
                        piece => { lock (live) live.Append(piece); dirty = true; }, // gathered here, drawn by the tick
                        () => Dispatcher.InvokeAsync(() => bubble.Thinking(true)),
                        cancel, temperature, effort);
                    turnLog.Append($" · AI {roundClock.Elapsed.TotalSeconds:0.0}s ({turn.Model ?? "?"}{(turn.Error != null ? ", " + turn.Error.Split(' ')[0] : "")})");
                    // busy or briefly down, before anything was written: tried once more on its own
                    if (turn.Error != null && live.Length == 0 && attempt == 0 && Retryable(turn.Error) && !cancel.IsCancellationRequested)
                    {
                        attempt++;
                        bubble.Thinking(true, "Busy, trying again…");
                        try { await Task.Delay(1600, cancel); } catch { }
                        if (cancel.IsCancellationRequested) break;
                        continue;
                    }
                    break;
                }
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); // the last pieces are in
                tick.Stop();
                bubble.Thinking(false);
                if (OpenRouter.IsGemini && turn.Error == null && turn.Model is { Length: > 0 } usedModel) model = usedModel; // the rest of this answer stays on it
                tokenCount += turn.Tokens;
                string said = KeepRealLinks(CleanAnswer(turn.Text), session); // a link no tool found and you didn't give can't reach you
                if (turn.Calls.Count == 0) said = Honest(said); // cards only when asked; an unchecked "done" says so
                if (said.Length > 0) { RenderMarkdown(bubble.Body, said); bubble.Answer = said; }
                else _chatStack?.Children.Remove(bubble.Root);

                if (cancel.IsCancellationRequested)
                {
                    if (said.Length > 0) session.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = said });
                    ChatNote("\uE71A", "Stopped");
                    break;
                }
                if (turn.Error != null)
                {
                    // the question stays unanswered: taken back, so it can simply be asked again
                    if (round == 0 && session.Messages.Count > 0 && session.Messages[^1]?["role"]?.ToString() == "user") session.Messages.RemoveAt(session.Messages.Count - 1);
                    if (turn.Error.Contains("/key") && await OpenRouter.CheckKey(key) == false) { OpenRouter.ForgetKey(); _chatPending = text; ChatSetupCard($"{OpenRouter.ProviderName} didn't accept the saved key: paste a new one."); }
                    else if (turn.Error.Contains("/key")) ChatProblem($"{OpenRouter.ProviderName} turned this request down, but the key itself is fine (it was checked). Try again in a moment.", round == 0 ? ("Try again", () => SendChat(text)) : null);
                    else ChatProblem(turn.Error, turn.Error.StartsWith("Today's 50") ? ("Add credit", () => OpenLink("https://openrouter.ai/settings/credits")) : turn.Error.StartsWith("Today's free Google") ? null : round == 0 ? ("Try again", () => SendChat(text)) : null);
                    break;
                }
                // it says it's done: is it? (once per task: it may have stopped short, or done it outside the app you meant)
                if (turn.Calls.Count == 0 && !goalChecked && ask == Ask.Action && round < ChatToolRounds && GoalUnmet(text, turnSteps) is { } unmet)
                {
                    goalChecked = true;
                    session.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = said.Length > 0 ? said : "(steps done)" });
                    session.Messages.Add(new JsonObject { ["role"] = "user", ["auto"] = true, ["content"] = "[Check before finishing] " + unmet });
                    _chatStack?.Children.Remove(bubble.Root); // its "done" goes: the finished answer comes next
                    continue;
                }
                if (turn.Calls.Count == 0)
                {
                    session.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = said });
                    if (said.Length > 0)
                    {
                        _chatLastAnswer = finalAnswer = said;
                        ShowSources(bubble);
                        AnswerFooter(bubble, turn.Model ?? model, DateTime.Now - startedAt, stepCount, tokenCount);
                        bubble.Finished?.Invoke();
                        if (said.Length > 80) ShowFollowUps(bubble, OnTopic(FollowUps(turn.Text))); // a one-line answer needs no "what next"
                        ShowAnswerActions(bubble, said); // a reminder for a time it names, the file it names
                        // it got done: how, kept for next time
                        if (_settings.AiMemory && turnSteps.Count > 0 && turnSteps[^1].Ok) AiLearn.Record(text, turnSteps, (DateTime.Now - startedAt).TotalSeconds);
                        if (stepCount >= 2) FoldSteps(turnStart, stepCount, stepNames, DateTime.Now - startedAt);
                    }
                    break;
                }

                // the AI asked the notch to do things: each one done, shown as a step, its result handed back
                var calls = new JsonArray();
                foreach (var c in turn.Calls)
                {
                    var call = new JsonObject { ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.Length > 0 ? c.Args.ToString() : "{}" } };
                    if (c.Extra != null) call["extra_content"] = c.Extra.DeepClone(); // Gemini wants its thought signature back with the call
                    calls.Add(call);
                }
                session.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = said.Length > 0 ? said : null, ["tool_calls"] = calls });
                foreach (var c in turn.Calls)
                {
                    string result;
                    var step = ChatStep(c.Name, c.Args.ToString());
                    stepCount++;
                    stepNames.Add(c.Name);
                    if (cancel.IsCancellationRequested) result = "Cancelled by the user.";
                    else
                    {
                        _pendingUndo = null;
                        _pendingCards.Clear();
                        var stepClock = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            var running = RunTool(c.Name, c.Args.ToString(), cancel);
                            // a step that hangs (an app not responding, a stuck command) is let go, and the AI told so
                            var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(c.Name is "run_powershell" or "create_image" or "write_file" ? 150 : 75), cancel));
                            result = finished == running ? await running : "Error: the step took too long and was stopped (the app or command didn't respond). Try another way.";
                        }
                        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { result = "Cancelled by the user."; }
                        catch (Exception ex) { result = "Error: " + ex.Message; }
                        turnLog.Append($" · {c.Name} {stepClock.Elapsed.TotalSeconds:0.0}s");
                        if (_pendingUndo is { } u && !result.StartsWith("Error")) AttachUndo(step, u);
                        _pendingUndo = null;
                        if (_pendingCards.Count > 0 && !result.StartsWith("Error")) ShowCards(_pendingCards.ToList());
                        _pendingCards.Clear();
                    }
                    acted = true;
                    if (result.Length > 12000) result = result[..12000] + "\n…(cut)";
                    string outcome = ToolOutcome(result);
                    bool stepOk = outcome is not ("failed" or "didn't work" or "not run");
                    step.Done(stepOk, outcome == "done" ? null : outcome);
                    turnSteps.Add((c.Name, c.Args.ToString(), stepOk));
                    session.Messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c.Id, ["content"] = result });
                }
                if (_pendingSight is { } sight)
                {
                    _pendingSight = null;
                    session.Messages.Add(new JsonObject
                    {
                        ["role"] = "user", ["auto"] = true,
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = "(Automatic, not from the user) The app's window right now, as you asked to see it:" },
                            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(sight) } },
                        },
                    });
                }
                if (cancel.IsCancellationRequested) { ChatNote("\uE71A", "Stopped"); break; }
            }
        }
        catch (Exception ex)
        {
            // nothing may leave the chat half-drawn: the problem is said, the question can be asked again
            AppLog.Write("chat: " + ex);
            try { ChatProblem("Something went wrong on this PC while answering (" + ex.GetBaseException().Message + ").", ("Try again", () => SendChat(text))); } catch { }
        }
        finally
        {
            if (turnLog.Length > 0) AppLog.Write($"ai answer {(DateTime.Now - startedAt).TotalSeconds:0.0}s [{ask}]{turnLog}");
            liveTick?.Stop();
            _turnBrief = "";
            OpenRouter.SmartNext = false;
            _chatBusy = false;
            session.Updated = DateTime.Now;
            if (_settings.AiHistory && session.UserTurns > 0)
            {
                var copy = new ChatSession { Id = session.Id, Title = session.Title, Created = session.Created, Updated = session.Updated, Model = session.Model, Messages = ForDisk(session.Messages) };
                _ = Task.Run(() => ChatStore.Save(copy));
            }
            ChatKeys();
            FollowChat();
            if (!cancel.IsCancellationRequested) { AnswerReady(finalAnswer, acted); MaybeSpeak(finalAnswer); }
            else _spokenTurn = false;
            // what you typed meanwhile goes next
            if (_chatQueue.Count > 0 && !cancel.IsCancellationRequested)
            {
                string next = _chatQueue.Dequeue();
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => SendChat(next));
            }
            else if (cancel.IsCancellationRequested) _chatQueue.Clear();
        }
    }

    static string TitleOf(string text)
    {
        string t = Regex.Replace(text.Trim(), @"\s+", " ");
        return t.Length > 42 ? t[..41].TrimEnd() + "…" : t;
    }

    /// <summary>/new, /model [id or free], /key, /chats: typed like messages.</summary>
    bool ChatCommand(string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/new":
            case "/clear":
                NewChat();
                return true;
            case "/undo":
                Toast(UndoLast());
                return true;
            case "/chats":
            case "/history":
                ShowChatView(ChatView.History);
                return true;
            case "/groq":
                ChatGroqCard();
                return true;
            case "/key":
                OpenRouter.ForgetKey();
                ChatSetupCard();
                return true;
            case "/model":
                if (parts.Length < 2) { ChatNote("", $"Model: {ModelName(ChatModel)}. Click the model's name above to switch, or type /model and a model id (/model auto for the automatic choice)."); return true; }
                string id = parts[1].Trim();
                if (id.Equals("free", StringComparison.OrdinalIgnoreCase) || id.Equals("auto", StringComparison.OrdinalIgnoreCase)) id = OpenRouter.DefaultModel;
                if (OpenRouter.IsGemini) _settings.AiGeminiModel = id; else _settings.AiModel = id;
                _settings.Save();
                UpdateChatHeader();
                ChatNote("", "Model: " + id, tone: ChatAccent);
                return true;
        }
        return false;
    }

    /// <summary>
    /// How the AI talks and what it knows: brief by default; the notch it lives in and what it can make the notch do;
    /// what it remembers about you; how its answers are drawn (so it formats for them).
    /// </summary>
    string SystemPrompt()
    {
        var now = DateTime.Now;
        string style = _settings.AiStyle switch
        {
            "detailed" => "Style: thorough. Explain step by step, with examples when they help. Headings and lists for long answers.",
            "balanced" => "Style: clear and concise. The answer first, then a short explanation only if it helps.",
            _ => "Style: ultra brief, like a sharp expert in a hurry. The answer first, in as few words as stay clear. Fragments are fine. No greeting, no restating the question, no filler, no apologies, no 'let me know if...', no closing question. A short list beats a paragraph; one line beats a list. Only go longer when asked, or when the task is to write something (then write it in full, nothing around it).",
        };
        var sb = new StringBuilder();
        sb.AppendLine("You are the AI of Pixo: a panel at the top of the user's Windows screen that shows music, calls, alerts, timers, reminders, focus sessions, system stats, the clipboard and files, and has a search. You are its brain: you answer, and you act on the PC for the user.");
        sb.AppendLine($"Now: {now:dddd d MMMM yyyy, HH:mm}. User's folders: Documents {Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}, Downloads {System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")}. Notch theme: {Skin.Key}{(_media.Title.Length > 0 ? $". Playing: {_media.Title}" : "")}.");
        sb.AppendLine(style);
        sb.AppendLine("How your answer is shown: a narrow dark panel. **bold** is drawn in the theme's highlight colour, so bold only the 1-3 words that matter. Lists and short paragraphs. Fenced code blocks get a copy button. Links: [text](url). Full file paths (C:\\...) become clickable, so write them in full. Use a markdown table (header row, |---| line) whenever you compare things or list items with values: it's drawn as a real table, so keep cells short. No emojis. A picture can be shown with ![what](https://url) on its own line; YouTube links get a video card. The user may attach pictures or their screen: describe and use what you see. The user may talk instead of typing (their words are dictated, so forgive small mistakes); when they did, your answer may be read aloud: keep it natural to hear. Answer in the user's language.");
        sb.AppendLine($"The user: Windows language {System.Globalization.CultureInfo.CurrentUICulture.DisplayName}, region {System.Globalization.RegionInfo.CurrentRegion.EnglishName}, time zone {TimeZoneInfo.Local.DisplayName}.");
        sb.AppendLine("How to work: 1) Understand what is really asked. 2) If it's about facts that change (news, prices, scores, weather, releases, people's roles, 'latest', 'today', anything after your training) or that you aren't sure of, look it up first with research or news: never answer such things from memory. 3) Call independent tools together in the same turn, and keep steps few: each step costs the user's limited daily requests. 4) Check what the tools returned; if a search finds nothing useful, try once with better words or another tool, then say plainly what you tried. 5) Answer the question in the first line, with specifics (names, numbers, dates, paths), then only what helps.");
        sb.AppendLine("You are the user's command center: you can see the notch's own records (notch_data), the PC (files, apps, windows, settings, browser history, PowerShell), and the web. RULES: 1) Never tell the user to go and check something themselves, never say you can't before trying: find it with your tools and answer with the actual details (names, times, durations, paths). 2) Questions about the user's calls, reminders, timer, focus, music, notifications, what they copied, the shelf, downloads or the PC's health: read notch_data first (with 'today' for a summary of their day). 3) Use notch_page only when the user asks to see a page. 4) If something truly isn't recorded, say exactly what is known and what isn't (e.g. a call's app and time are known, the person isn't).");
        sb.AppendLine("Reading between the lines: work out what the user means, not just the words. 'My setup', 'my gear', 'my stuff', 'my devices' = their peripherals (mouse, headset, keyboard, controller, earbuds), usually not the PC alone. A short follow-up ('not pc, I meant...', 'and the other one?', 'do it again', 'in French') changes the previous question: answer that again, corrected, without asking. When a question could mean several things, answer all of them in one table rather than picking one. Where to look, in this order: (1) LIVE NOW below, (2) notch_data, (3) the PC's tools, (4) the web. Never say the notch can't see something before checking LIVE NOW and notch_data. The user has no time to correct you twice: get it right the first time.");
        sb.AppendLine("Understanding commands: users type fast, casual, misspelled, in any language or mixed (English, French, Darija, Spanish...). Read the intent, not the words: 'baisse le son' / 'nkss sout' = volume down, 'skip' / 'next' = next song, 'ferme discord' = close Discord, 'mets le pc en sombre' = dark mode on, 'wifi off', 'lance spotify' = open Spotify. When a request has several parts ('open spotify, play lofi and set volume 40'), do every part, in order, in one go. If a word could be an app, a file or a website, prefer what's on this PC. Ask back only when two very different actions both fit, in one short question with the choices.");
        sb.AppendLine("Truth: never make up links, video IDs, file paths, names, numbers or facts. Use only what a tool returned or the user gave. Videos, clips, live streams: the youtube tool (real results), then give or open the one it found. If a tool found nothing, say so plainly.");
        sb.AppendLine("Honesty about actions: say something was sent, played, opened or done only when a tool result says so. Messages to people: only when the user asked to send something, and always with send_message (it checks the message arrived). Never type through PowerShell. After typing or clicking in an app, look or see before saying it worked. If you couldn't check, say it's not confirmed. When the user asks what's on their screen, a picture of it is attached: describe what you actually see.");
        sb.AppendLine("Web: today's news or headlines → news (not research). A page that turned out to be a login or paywall: use another source, never its menu text.");
        sb.AppendLine("Language: answer in the same language AND the same alphabet the user wrote in (Darija in Latin letters gets Latin letters, never Arabic script, unless they wrote in Arabic script).");
        sb.AppendLine("Stay on topic: answer only what was asked. Mention LIVE NOW details (volume, music, batteries, calls) only when the question is about them.");
        sb.AppendLine("Answering after doing something: one short line that says what changed, with the result ('Volume 40%, Spotify playing lofi.'), never 'I have successfully...', never steps you already showed. If something failed, say what and the one thing that would fix it. Don't repeat what the step lines already show.");
        sb.AppendLine("LIVE NOW (fresh from the notch, no tool needed):\n" + LiveNow());
        sb.AppendLine("Cards: when you give the user's device batteries, put the line [[card:batteries]] (it draws them as live meters) and keep your text to one short line beside it. When you tell how the PC is doing right now (processor, memory, disk), put [[card:pc]]. Never use a card for anything else.");
        sb.AppendLine("Next steps: end every answer that leads somewhere with a last line '>> a | b | c': two or three short things the user would likely want next, written as their own words (under 6 words each), e.g. '>> Close Chrome | Show top apps | Free up memory'. They become buttons. Skip it for small talk, and never offer what you can't do.");
        sb.AppendLine("The sources and files you use are shown to the user as cards automatically: don't list them at the end. Mention a source inline only when it matters (who said it, or when they disagree).");
        if (_settings.AiTools)
            sb.AppendLine("Working apps like a personal assistant: you can use ANY app the user has, not just open it. To do something inside an app (call or message someone, play a song, change a setting, fill a form, find something in a website): 1) open it if it isn't (open), 2) use_app look to see its buttons and boxes, 3) act: click, type, keys; use the app's well-known shortcuts when they're faster (Discord, Slack, Teams: ctrl+k jumps to a person or channel; browsers: ctrl+l address bar, ctrl+t new tab; Spotify: ctrl+l search; most apps: ctrl+f find), 4) look again (or see, for a picture) after every step that changes the screen, and keep going until it's really done, 5) only then say it's done, in one line. To play music or a video, always use play_music (one step, checked), never use_app. Example, 'call Nostra on Discord': open discord → use_app keys 'ctrl+k' → type 'Nostra' → keys 'enter' (opens the DM) → look → click the button named like 'Start Voice Call' → look to confirm the call started. If a name matches several people, ask which one. Never claim something happened that you didn't check. Don't touch the user's mouse and keyboard for long: a few steps, then report.");
        if (_settings.AiTools)
        {
            sb.AppendLine("Acting: when the user asks for something on the PC, DO it with your tools; never just explain how. Pick the right tool: do_on_pc for the notch's own commands (volume, media, wifi, timers, reminders, focus, open/close apps, screenshots...); manage_window to focus, minimise, maximise or close a window; run_powershell for anything else Windows can do (settings, files, processes, system info, installs); find_files/read_file/list_folder for files; write_file to create documents (.docx, .xlsx, .pdf are real Word / Excel / PDF files: write the content as markdown, with a markdown table or CSV rows for .xlsx); create_image to make a picture from a description; undo_last when the user says to undo or take back what you did; browser_history for what was downloaded or visited in a browser (never guess from folders); notch_page to show one of the notch's pages; recent_notifications for messages the PC received; notify to show a short message on the notch (when a long job finishes, or the user asks to be told something). Chain steps until the task is really done, check results, and fix errors yourself before giving up. Then confirm in a few words what was done.");
            sb.AppendLine("Limits: do only what was asked. Never delete, uninstall, change security settings, shut down or restart unless the user clearly asked for that exact thing. Commands that delete, install, kill, download or touch the registry or security are shown to the user to approve: keep them minimal and separate from harmless steps (so reading never needs approval), and if they decline, accept it. Never claim something was done unless a tool result says so.");
        }
        else sb.AppendLine("You can't act on the PC in this chat (the user turned that off): give the steps instead, briefly.");
        if (_settings.AiWeb) sb.AppendLine("The web: research answers questions (it searches and reads the pages in one step: one research is normally enough, so answer from its results and only search again if they don't cover the question); news gives the latest headlines; read_webpage reads a page the user names. Prefer recent, primary sources.");
        if (_settings.AiTools) sb.AppendLine("On the PC, to find something: find_files (names, also 'pdf last week'), browser_history (downloads and visited pages), list_folder (a folder's newest items); combine them when unsure, and give full paths.");
        if (_settings.AiMemory)
        {
            sb.AppendLine("Memory: whenever the user says remember, save this, keep in mind, don't forget, note this, retiens, n'oublie pas, souviens-toi (any language), save it at once with remember, the right kind: 'rule' for how to act from now on, 'note' for something to give back later, 'fact' about them. 'Remember this' with nothing after it means what was just said or answered. Confirm in one line. Also save lasting facts and preferences they mention on their own. Never passwords or card numbers. Use what you remember without being asked; update or forget what changes.");
            var rules = AiMemory.Rules();
            if (rules.Count > 0) sb.AppendLine("STANDING RULES from the user (always follow them; they come before the style above):\n- " + string.Join("\n- ", rules));
            var facts = AiMemory.Facts();
            if (facts.Count > 0) sb.AppendLine("What you remember about the user:\n- " + string.Join("\n- ", facts));
            var notes = AiMemory.Notes();
            if (notes.Count > 0) sb.AppendLine("Notes the user asked you to keep (give them back word for word when asked, e.g. 'what did I tell you to remember?'):\n- " + string.Join("\n- ", notes.TakeLast(25)));
        }
        if (_settings.AiMemory)
        {
            // what it learned on this PC: its own lessons, this person's habits, and how similar requests got done before
            sb.AppendLine("Learning: when the user corrects you ('no', 'not that', 'I meant…', 'pas ça', 'machi hadi'), or a way failed and another worked, fix it, then save the lesson with learn (one short rule about this PC or this user, e.g. 'Discord calls: open the DM with ctrl+k, then press Start Voice Call'). Use what you learned without being asked.");
            var lessons = AiLearn.Lessons();
            if (lessons.Count > 0) sb.AppendLine("Lessons you learned on this PC (follow them):\n- " + string.Join("\n- ", lessons.TakeLast(20)));
            var habits = AiLearn.Habits();
            if (habits.Count > 0) sb.AppendLine("This user's habits: " + string.Join("; ", habits) + ".");
            var like = AiLearn.Like(_chatLastUser);
            if (like.Count > 0) sb.AppendLine("Similar requests got done before like this (go straight to what worked, skip the failed steps, fewer looks):\n- " + string.Join("\n- ", like.Select(r => $"“{r.Ask}” → {r.Steps} ({r.Seconds:0} s{(r.Uses > 1 ? $", {r.Uses} times" : "")})")));
        }
        if (_settings.AiInstructions.Trim().Length > 0) sb.AppendLine("The user's own instructions (follow them): " + _settings.AiInstructions.Trim());
        sb.AppendLine("Before you answer: be sure what exactly is asked and what form fits it (one fact, a short list, a table, a file, an action). Answer exactly that, complete and correct, nothing around it.");
        sb.AppendLine(ReadTheConversation);
        sb.AppendLine(SoundHuman);
        sb.AppendLine(TaskMethod);
        sb.AppendLine(ChartRule);
        if (_turnBrief.Length > 0) sb.AppendLine(_turnBrief);
        return sb.ToString().TrimEnd();
    }

    /// <summary>What the notch knows this second, given to the AI with every message: its gear, the sound, what's on.</summary>
    string LiveNow()
    {
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine(DevicesText());
            sb.AppendLine($"- Volume {(int)Math.Round(_audio.Volume * 100)}%{(_audio.MicMuted ? ", mic muted" : "")}");
            if (DefaultBrowser() is { } browser) sb.AppendLine($"- Default browser: {BrowserName(browser)} (web pages open there; use_app app \"{browser}\")");
            if (_media.Title.Length > 0) sb.AppendLine($"- {(_media.IsPlaying ? "Playing" : "Paused")}: {_media.Title}{(_media.Artist.Length > 0 ? " — " + _media.Artist : "")}");
            if (_call is { } call) sb.AppendLine($"- On a call in {call.Name}{(_callWith != null ? " with " + _callWith : "")}");
            if (_focus.IsOn) sb.AppendLine($"- Focus on for {(int)_focus.Elapsed.TotalMinutes} min");
            if (_timerRunning) sb.AppendLine($"- Timer: {(int)Math.Ceiling((_timerEnd - DateTime.UtcNow).TotalMinutes)} min left");
            if (_reminders.Count > 0) { var next = _reminders.OrderBy(r => r.At).First(); sb.AppendLine($"- Next reminder: {next.At:ddd HH:mm} {next.Text}"); }
        }
        catch { }
        return sb.ToString().TrimEnd();
    }

    // ───────────────────────────── drawing the conversation ─────────────────────────────

    sealed class ChatBubble
    {
        public required Grid Root;
        public required RichTextBox Body;
        public required StackPanel Extras;
        public required FrameworkElement Dots;
        public string Answer = "";
        public Action<bool>? OnThinking;
        public Action<string>? OnWord;
        /// <summary>The answer is complete: a light runs down its edge.</summary>
        public Action? Finished;
        public void Thinking(bool on) => OnThinking?.Invoke(on);
        public void Thinking(bool on, string word) { OnWord?.Invoke(word); OnThinking?.Invoke(on); }
    }

    void ChatUser(string text, bool animate = true, string? attached = null, IReadOnlyList<byte[]>? pictures = null, int pictureCount = 0, DateTime? at = null)
    {
        if (_chatStack == null) return;
        var acc = ChatAccent;
        var box = MessageBox();
        var doc = box.Document;
        doc.Foreground = _cyber ? Frozen(CyYellow) : Brushes.White;
        doc.FontFamily = _cyber ? CyFont : UiText;
        var para = new Paragraph { Margin = new Thickness(0) };
        Inline(para.Inlines, text);
        doc.Blocks.Add(para);
        if (attached != null)
        {
            var file = new Paragraph { Margin = new Thickness(0, 5, 0, 0), FontSize = 10.5, Foreground = Frozen(A(acc, 0xE0)) };
            file.Inlines.Add(new Run("\uE8A5  ") { FontFamily = (FontFamily)FindResource("IconFont") });
            file.Inlines.Add(new Run(attached));
            doc.Blocks.Add(file);
        }
        // a bubble as wide as its text (a selectable box would otherwise take the whole row)
        var face = new Typeface(doc.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var measured = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 12.5, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double want = measured.WidthIncludingTrailingWhitespace;
        if (attached != null) want = Math.Max(want, 24 + attached.Length * 6);
        if (pictures is { Count: > 0 }) want = Math.Max(want, pictures.Count * 70);
        box.Width = Math.Min(296, Math.Ceiling(want) + 10);
        FrameworkElement content = box;
        if (pictures is { Count: > 0 } || pictureCount > 0)
        {
            var stack = new StackPanel();
            var row = new WrapPanel { Margin = new Thickness(0, 1, 0, 6) };
            if (pictures != null) foreach (var jpeg in pictures) row.Children.Add(PictureTile(jpeg));
            else row.Children.Add(new TextBlock { Text = pictureCount == 1 ? "\uEB9F  picture" : $"\uEB9F  {pictureCount} pictures", FontSize = 10.5, Foreground = Frozen(A(acc, 0xD0)), FontFamily = (FontFamily)FindResource("IconFont") });
            stack.Children.Add(row);
            stack.Children.Add(box);
            content = stack;
        }
        // your message, without a box around it. Neon: the words on a wash lit from a bar on the right (the answer's bar is
        // on the left), under a small "YOU // 10:41". Else: a soft bubble in the AI's colours, the time under the pointer.
        string? when = at is { } sent ? (sent.Date == DateTime.Today ? sent.ToString("HH:mm") : sent.ToString("d MMM, HH:mm")) : null;
        var bubble = new Border
        {
            Child = content, HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 320,
            Padding = _cyber ? new Thickness(14, 4, 10, 5) : new Thickness(11, 6, 11, 7),
            CornerRadius = _cyber ? new CornerRadius(0) : new CornerRadius(14, 14, 4, 14),
            Background = Frozen(_cyber ? new LinearGradientBrush(A(CyYellow, 0x00), A(CyYellow, 0x1C), 0) : new LinearGradientBrush(A(acc, 0x3C), A(AiSecond, 0x2A), 0)),
            BorderBrush = _cyber ? CyYellowBrush : null, BorderThickness = new Thickness(0, 0, _cyber ? 2 : 0, 0),
        };
        if (_cyber) NeonCorner(bubble, 0, 7);
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        if (_cyber)
            column.Children.Add(new TextBlock
            {
                Text = when != null ? "YOU // " + when : "YOU", FontSize = 8.5, FontFamily = CyFont, Foreground = Frozen(A(acc, 0x90)),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 2),
            });
        column.Children.Add(bubble);
        TextBlock? stamp = null;
        if (!_cyber && when != null)
        {
            stamp = new TextBlock { Text = when, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 3, 4, 0), FontFamily = UiText, Opacity = 0 };
            column.Children.Add(stamp);
        }
        var root = new Grid { Margin = new Thickness(36, 16, 0, 2), Tag = "user", Background = Brushes.Transparent };
        root.Children.Add(column);
        if (stamp != null) { root.MouseEnter += (_, _) => stamp.Opacity = 1; root.MouseLeave += (_, _) => stamp.Opacity = 0; }
        root.Children.Add(Actions(root, HorizontalAlignment.Right, ("\uE8C8", "Copy", () => CopyText(text, "Copied")), ("\uE70F", "Edit", () => EditMessage(text))));
        _chatStack.Children.Add(root);
        if (animate) { if (_cyber) GlitchIn(root); else Arrive(root, fromRight: true); }
        FollowChat(force: true);
    }

    /// <summary>A picture you sent, as a rounded tile; a click opens it big.</summary>
    FrameworkElement PictureTile(byte[] jpeg)
    {
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit();
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.DecodePixelHeight = 128;
        img.StreamSource = new System.IO.MemoryStream(jpeg);
        img.EndInit();
        img.Freeze();
        double w = Math.Clamp(64.0 * img.PixelWidth / Math.Max(1, img.PixelHeight), 48, 112);
        var tile = new Border
        {
            Width = w, Height = 64, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(_cyber ? 0 : 9), Cursor = Cursors.Hand,
            Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }, BorderBrush = Frozen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            ToolTip = "Open the picture",
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try
            {
                string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DynamicNotch-picture-" + (uint)jpeg.GetHashCode() + ".jpg");
                if (!System.IO.File.Exists(file)) System.IO.File.WriteAllBytes(file, jpeg);
                OpenLink(file);
            }
            catch { }
        };
        return tile;
    }

    ChatBubble ChatAssistant(bool animate = true)
    {
        var acc = ChatAccent;
        var body = MessageBox();
        // its little face while it works: thinking (eyes up), searching (eyes scanning), busy (a bob)
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 2, 0, 1), Visibility = Visibility.Collapsed };
        var face = new Controls.NotchFace(14) { Neon = _cyber, Accent = acc, Eco = Eco, VerticalAlignment = VerticalAlignment.Center };
        face.SetMood(Controls.NotchFace.Mood.Thinking);
        face.SetOutfit(FaceOutfitNow(), animate: false);
        dots.Children.Add(face);
        static Controls.NotchFace.Mood MoodFor(string w) =>
            System.Text.RegularExpressions.Regex.IsMatch(w, "search|read|fetch|getting|headline|source", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? Controls.NotchFace.Mood.Searching
            : System.Text.RegularExpressions.Regex.IsMatch(w, "busy|trying again", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? Controls.NotchFace.Mood.Error
            : System.Text.RegularExpressions.Regex.IsMatch(w, "running|opening|doing|checking|sync|writing", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? Controls.NotchFace.Mood.Working
            : Controls.NotchFace.Mood.Thinking;
        var label = new TextBlock { Text = _cyber ? "PROCESSING" : "Thinking", FontFamily = _cyber ? CyFont : UiText, FontSize = _cyber ? 9.5 : 10.5, Foreground = Frozen(A(acc, 0xB0)), Margin = new Thickness(1, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        dots.Children.Add(label);
        TextBlock? caret = null; // Neon: a block cursor blinking after what it's doing
        if (_cyber && !Eco) { caret = new TextBlock { Text = "\u258C", FontSize = 9, Foreground = Frozen(acc), Margin = new Thickness(3, -2, 0, 0), VerticalAlignment = VerticalAlignment.Center }; dots.Children.Add(caret); }
        Action<bool>? shimmer = null;
        var since = DateTime.UtcNow;
        string word = _cyber ? "PROCESSING" : "Thinking";
        var clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) =>
        {
            int sec = (int)(DateTime.UtcNow - since).TotalSeconds;
            label.Text = sec >= 2 ? $"{word} · {sec}s" : word;
        };
        var column = new StackPanel();
        column.Children.Add(dots);
        column.Children.Add(body);
        var extras = new StackPanel();
        column.Children.Add(extras);
        var rail = new Border
        {
            Child = column, BorderBrush = Frozen(new LinearGradientBrush(A(acc, _cyber ? (byte)0xE0 : (byte)0xB0), A(AiSecond, _cyber ? (byte)0x50 : (byte)0x30), 90)), BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
        };
        var root = new Grid { Margin = new Thickness(0, 16, 18, 2), Background = Brushes.Transparent };
        root.Children.Add(rail);
        if (!Eco)
        {
            // the line saying what it's doing has a light running through it
            var glow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            var dim = A(acc, 0xA0);
            var g1 = new GradientStop(dim, -0.3); var g2 = new GradientStop(Lighten(acc, 0.55), -0.15); var g3 = new GradientStop(dim, 0);
            glow.GradientStops.Add(new GradientStop(dim, 0)); glow.GradientStops.Add(g1); glow.GradientStops.Add(g2); glow.GradientStops.Add(g3); glow.GradientStops.Add(new GradientStop(dim, 1));
            label.Foreground = glow;
            shimmer = on =>
            {
                foreach (var (stop, from) in new[] { (g1, -0.3), (g2, -0.15), (g3, 0.0) })
                {
                    if (!on) { stop.BeginAnimation(GradientStop.OffsetProperty, null); continue; }
                    var run = new DoubleAnimation(from, from + 1.5, TimeSpan.FromMilliseconds(1400)) { RepeatBehavior = RepeatBehavior.Forever };
                    Timeline.SetDesiredFrameRate(run, 30);
                    stop.BeginAnimation(GradientStop.OffsetProperty, run);
                }
            };
        }
        // when it's done: a light runs down the edge, once
        var shine = new Border
        {
            Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0, IsHitTestVisible = false,
            Background = Frozen(new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Lighten(acc, 0.5), 0.5), new GradientStop(Colors.Transparent, 1) } }),
        };
        if (!_cyber) shine.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = acc, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 };
        root.Children.Add(shine);
        var bubble = new ChatBubble { Root = root, Body = body, Dots = dots, Extras = extras };
        string asked = _chatLastUser; // the question this answers (for 👎)
        bubble.Finished = () =>
        {
            Services.FaceSound.Say("done", _cyber); // "ta-daa": it's done
            if (Eco) return;
            if (_cyber)
            {
                // Neon: the edge flickers on once, like a signal locking in
                var flick = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
                flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50))));
                flick.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
                flick.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
                shine.BeginAnimation(OpacityProperty, flick);
                return;
            }
            var glow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900) };
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), new CubicEase { EasingMode = EasingMode.EaseIn }));
            shine.BeginAnimation(OpacityProperty, glow);
        };
        string Plain() => System.Text.RegularExpressions.Regex.Replace(bubble.Answer, @"^\s*\[\[card:[a-z]+\]\]\s*$\n?", "", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        root.Children.Add(Actions(root, HorizontalAlignment.Right,
            ("\uE738", "Shorter", () => SendChat("Say that shorter.")),
            ("\uE710", "More detail", () => SendChat("Go into more detail on that.")),
            ("\uE767", "Read aloud", () => { if (VoiceOut.Speaking) VoiceOut.Stop(); else Speak(Plain()); }),
            ("\uE8C8", "Copy", () => CopyText(Plain(), "Copied")),
            ("\uE72C", "Retry", Retry),
            ("\uE8E0", "Bad answer (kept for the AI's checks)", () => BadAnswer(asked, Plain()))));
        bubble.OnWord = w => { word = w; label.Text = w; face.SetMood(MoodFor(w)); };
        bubble.OnThinking = on =>
        {
            var v = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) clock.Start(); else clock.Stop();
            if (dots.Visibility == v) return;
            dots.Visibility = v;
            shimmer?.Invoke(on);
            if (caret != null)
            {
                if (!on) caret.BeginAnimation(OpacityProperty, null);
                else
                {
                    var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900), RepeatBehavior = RepeatBehavior.Forever };
                    blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                    blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450))));
                    caret.BeginAnimation(OpacityProperty, blink);
                }
            }
        };
        _chatStack!.Children.Add(root);
        bubble.Thinking(animate);
        if (animate) { if (_cyber) GlitchIn(root); else Arrive(root); }
        FollowChat();
        return bubble;
    }

    /// <summary>Copy / edit / retry, shown over a message while the pointer is on it.</summary>
    FrameworkElement Actions(Grid owner, HorizontalAlignment side, params (string Glyph, string Tip, Action Act)[] acts)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (glyph, tip, act) in acts)
        {
            var b = new TextBlock
            {
                Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10.5, Width = 22, Height = 20, TextAlignment = TextAlignment.Center,
                Padding = new Thickness(0, 4, 0, 0), Foreground = DimBrush, Cursor = Cursors.Hand, ToolTip = tip, Background = Brushes.Transparent,
            };
            b.MouseEnter += (_, _) => b.Foreground = Frozen(ChatAccent);
            b.MouseLeave += (_, _) => b.Foreground = DimBrush;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); _launchBox?.Focus(); };
            row.Children.Add(b);
        }
        var plate = new Border
        {
            Child = row, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -19, -4, 0),
            CornerRadius = new CornerRadius(_cyber ? 0 : 8), Background = Frozen(_cyber ? Color.FromArgb(0xF0, 0x06, 0x12, 0x1A) : Color.FromArgb(0xF0, 0x26, 0x26, 0x2C)),
            BorderBrush = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), Opacity = 0, IsHitTestVisible = false,
        };
        owner.MouseEnter += (_, _) => { plate.IsHitTestVisible = true; plate.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120))); };
        owner.MouseLeave += (_, _) => { plate.IsHitTestVisible = false; plate.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))); };
        Panel.SetZIndex(plate, 5);
        return plate;
    }

    /// <summary>A step's colour by what it touches: the web, files, the notch, the PC, memory.</summary>
    Color StepTone(string tool) => tool switch
    {
        "research" or "web_search" or "read_webpage" or "news" or "browser_history" => _cyber ? CyCyan : Color.FromRgb(0x5A, 0xB4, 0xFF),
        "find_files" or "read_file" or "list_folder" or "write_file" or "open" or "create_image" => _cyber ? CyYellow : Color.FromRgb(0xFF, 0xC4, 0x5A),
        "notch_data" or "notch_page" or "notify" or "recent_notifications" => _cyber ? CyMagenta : Color.FromRgb(0xC0, 0x8C, 0xFF),
        "run_powershell" or "do_on_pc" or "manage_window" or "use_app" or "send_message" or "see_screen" or "play_music" or "youtube" or "learn" or "clipboard" or "pc_status" or "undo_last" => _cyber ? Color.FromRgb(0x00, 0xFF, 0xA3) : Color.FromRgb(0x5A, 0xE0, 0x9A),
        _ => A(ChatAccent, 0xC0),
    };

    /// <summary>A line from the notch itself: stopped, an error, a setting changed.</summary>
    TextBlock ChatNote(string glyph, string text, Color? tone = null, (string Label, Action Act)? action = null)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var color = tone ?? Color.FromArgb(0x9C, 0xFF, 0xFF, 0xFF);
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Frozen(color), Margin = new Thickness(0, 2, 0, 0) });
        var words = new StackPanel();
        var tb = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Frozen(tone ?? Color.FromArgb(0x9C, 0xFF, 0xFF, 0xFF)), FontFamily = UiText };
        words.Children.Add(tb);
        if (action is { } a)
        {
            var b = ChatChip(a.Label, "");
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(0, 5, 0, 0);
            b.Click += (_, _) => { a.Act(); _launchBox?.Focus(); };
            words.Children.Add(b);
        }
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        _chatStack?.Children.Add(row);
        Arrive(row);
        FollowChat();
        return tb;
    }

    void Arrive(FrameworkElement el, bool fromRight = false)
    {
        if (Eco) return;
        var shift = new TranslateTransform(fromRight ? 10 : 0, 7);
        el.RenderTransform = shift;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        if (fromRight) shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    // ───────────────────────────── scrolling: smooth, by wheel and keys ─────────────────────────────

    void ChatScrollTo(double y, int ms)
    {
        var sv = ActiveChatScroll;
        if (sv == null) return;
        _chatToEnd = y >= sv.ScrollableHeight - 0.5;
        _chatTarget = Math.Clamp(y, 0, sv.ScrollableHeight);
        if (ms <= 0 || Eco || !sv.IsVisible) { StopGlide(); sv.ScrollToVerticalOffset(_chatToEnd ? sv.ScrollableHeight : _chatTarget); return; }
        _chatEase = ms;
        if (_chatGliding) return; // already moving: it just heads for the new target
        _chatGliding = true;
        _chatFrameAt = TimeSpan.Zero;
        CompositionTarget.Rendering += OnChatScrollFrame;
    }

    void OnChatScrollFrame(object? sender, EventArgs e)
    {
        var sv = ActiveChatScroll;
        if (sv == null || !sv.IsVisible) { StopGlide(); return; }
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _chatFrameAt) return;
        double dt = _chatFrameAt == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - _chatFrameAt).TotalSeconds, 0.05);
        _chatFrameAt = now;
        double target = _chatToEnd ? sv.ScrollableHeight : Math.Clamp(_chatTarget, 0, sv.ScrollableHeight);
        _chatTarget = target;
        double cur = sv.VerticalOffset;
        double k = 1 - Math.Exp(-dt * 3400 / Math.Max(80, _chatEase)); // ~240 ms to settle
        double next = cur + (target - cur) * k;
        if (Math.Abs(target - next) < 0.4) { sv.ScrollToVerticalOffset(target); StopGlide(); return; }
        sv.ScrollToVerticalOffset(next);
    }

    /// <summary>Keeps the newest in view while it writes, unless you scrolled up to read (then "Latest" waits for you).</summary>
    void FollowChat(bool force = false)
    {
        if (_chatScroll == null || _chatView != ChatView.Chat) return;
        if (force) _chatFollow = true;
        if (!_chatFollow) { ShowLatest(true); return; }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!_chatFollow) return;
            // pinned to the end: the smoother follows the end as it grows (no restart, no stutter)
            if (_chatScroll.ScrollableHeight - _chatScroll.VerticalOffset > 0.5) ChatScrollTo(double.MaxValue, 200);
        });
    }

    void ChatScrolled(ScrollViewer sv)
    {
        if (_chatThumb == null) return;
        // the thin bar: as tall as what's in view, where it is
        if (sv.ExtentHeight <= sv.ViewportHeight + 1) { _chatThumb.Opacity = 0; ShowLatest(false); return; }
        double h = Math.Max(18, sv.ViewportHeight * sv.ViewportHeight / sv.ExtentHeight);
        double y = (sv.ViewportHeight - h) * (sv.VerticalOffset / Math.Max(1, sv.ScrollableHeight));
        _chatThumb.Height = h;
        ((TranslateTransform)_chatThumb.RenderTransform).Y = y;
        if (_chatThumb.Opacity < 0.7) _chatThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(0.8, TimeSpan.FromMilliseconds(120)));
        _thumbFade.Stop();
        _thumbFade.Start();
        if (sv == _chatScroll && sv.VerticalOffset >= sv.ScrollableHeight - 3 && (!_chatGliding || _chatToEnd))
        {
            _chatFollow = true; // you came back down: it follows again
            ShowLatest(false);
        }
    }

    void ShowLatest(bool on)
    {
        if (_chatLatest == null) return;
        on &= _chatView == ChatView.Chat;
        if (on == (_chatLatest.Visibility == Visibility.Visible)) return;
        if (on) { _chatLatest.Visibility = Visibility.Visible; _chatLatest.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))); }
        else
        {
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            a.Completed += (_, _) => { if (_chatLatest.Opacity < 0.05) _chatLatest.Visibility = Visibility.Collapsed; };
            _chatLatest.BeginAnimation(OpacityProperty, a);
        }
    }

    // ── messages are read-only documents: selectable with the mouse, Ctrl+C / right-click to copy, links clickable

    /// <summary>A message's text: selectable like any chat, links live; a click that selects nothing gives the keyboard back.</summary>
    RichTextBox MessageBox()
    {
        var box = new RichTextBox
        {
            IsReadOnly = true, IsDocumentEnabled = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Foreground = Brushes.White, FocusVisualStyle = null, IsUndoEnabled = false, Cursor = Cursors.IBeam,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SelectionBrush = Frozen(ChatAccent), SelectionOpacity = 0.35, IsInactiveSelectionHighlightEnabled = true,
            Document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = UiText, FontSize = 12.5, LineHeight = 18, TextAlignment = TextAlignment.Left },
        };
        box.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (box.Selection.IsEmpty) _launchBox?.Focus(); // just a click: back to typing
        });
        // typing while a message has the focus goes to the message box
        box.PreviewTextInput += (_, e) =>
        {
            if (_launchBox == null) return;
            e.Handled = true;
            _launchBox.Focus();
            _launchBox.SelectedText = e.Text;
            _launchBox.CaretIndex = _launchBox.SelectionStart + _launchBox.SelectionLength;
            _launchBox.SelectionLength = 0;
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter) { e.Handled = true; box.Selection.Select(box.Document.ContentStart, box.Document.ContentStart); _launchBox?.Focus(); }
        };
        return box;
    }

    // the theme's colours for an answer
    Color TextTone => _cyber ? Color.FromRgb(0xE4, 0xF4, 0xFF) : Color.FromRgb(0xF1, 0xF1, 0xF4);
    Color StrongTone => _cyber ? CyYellow : Lighten(ChatAccent, 0.55);
    Color CodeTone => _cyber ? Color.FromRgb(0x9F, 0xF4, 0xFF) : Lighten(ChatAccent, 0.7);

    void RenderMarkdown(RichTextBox box, string md, bool streaming = false)
    {
        _liveDocs.Remove(box);
        RenderInto(box.Document, md, streaming);
    }

    sealed class LiveDoc { public string Prefix = ""; public int Blocks; }
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<RichTextBox, LiveDoc> _liveDocs = new();

    /// <summary>
    /// While an answer streams in: the paragraphs it has finished are drawn once and kept; only the one it's still
    /// writing is redrawn. A long answer costs no more per update than a short one (it used to redraw everything).
    /// </summary>
    void RenderMarkdownLive(RichTextBox box, string md)
    {
        int cut = StableCut(md);
        var doc = box.Document;
        if (!_liveDocs.TryGetValue(box, out var st) || !md.StartsWith(st.Prefix, StringComparison.Ordinal) || cut < st.Prefix.Length || st.Blocks > doc.Blocks.Count)
        {
            RenderInto(doc, md[..cut], streaming: true);
            st = new LiveDoc { Prefix = md[..cut], Blocks = doc.Blocks.Count };
            _liveDocs.AddOrUpdate(box, st);
        }
        else
        {
            TrimBlocks(doc, st.Blocks);
            if (cut > st.Prefix.Length)
            {
                AppendBlocks(doc, md[st.Prefix.Length..cut]);
                st.Prefix = md[..cut];
                st.Blocks = doc.Blocks.Count;
            }
        }
        TrimBlocks(doc, st.Blocks);
        if (cut < md.Length) AppendBlocks(doc, md[cut..]);
    }

    void AppendBlocks(FlowDocument doc, string part)
    {
        var tmp = new FlowDocument();
        RenderInto(tmp, part, streaming: true);
        foreach (var b in tmp.Blocks.ToList()) { tmp.Blocks.Remove(b); doc.Blocks.Add(b); }
    }

    static void TrimBlocks(FlowDocument doc, int keep)
    {
        while (doc.Blocks.Count > keep && doc.Blocks.LastBlock is { } last) doc.Blocks.Remove(last);
    }

    /// <summary>Where the finished part ends: after the last blank line that isn't inside a code block.</summary>
    static int StableCut(string md)
    {
        bool fence = false;
        int cut = 0, start = 0;
        while (start < md.Length)
        {
            int end = md.IndexOf('\n', start);
            if (end < 0) break; // the last line is still being written
            var line = md.AsSpan(start, end - start).Trim();
            if (line.StartsWith("```")) fence = !fence;
            else if (!fence && line.Length == 0) cut = end + 1;
            start = end + 1;
        }
        return cut;
    }

    void RenderInto(FlowDocument doc, string md, bool streaming)
    {
        doc.Blocks.Clear();
        doc.Foreground = Frozen(TextTone);
        var acc = ChatAccent;
        var lines = Untangle(md.Replace("\r", "")).Split('\n');
        var tableRows = new List<string>();
        var para = new StringBuilder();
        bool inCode = false;
        string lang = "";
        var code = new StringBuilder();

        void Flush()
        {
            if (para.Length == 0) return;
            var p = new Paragraph { Margin = new Thickness(0, 1, 0, 6) };
            Inline(p.Inlines, para.ToString().Trim());
            doc.Blocks.Add(p);
            para.Clear();
        }
        void EmitTable()
        {
            if (tableRows.Count == 0) return;
            if (tableRows.Count >= 2)
            {
                doc.Blocks.Add(MarkdownTable(tableRows.ToList()));
                if (!streaming) doc.Blocks.Add(TableTools(tableRows.ToList())); // copy, Excel (NotchWindow.LauncherChatRich)
            }
            else { para.Append(tableRows[0]); Flush(); }
            tableRows.Clear();
        }
        void Code(string body, string language, bool open)
        {
            if (language.Trim().Equals("chart", StringComparison.OrdinalIgnoreCase))
            {
                if (open || streaming)
                {
                    var wait = new Paragraph(new Run(_cyber ? "RENDERING CHART…" : "Drawing the chart…") { Foreground = Frozen(A(acc, 0xB0)), FontStyle = FontStyles.Italic }) { Margin = new Thickness(0, 2, 0, 6), FontSize = 11 };
                    doc.Blocks.Add(wait);
                    return;
                }
                if (ChartBlock(body) is { } chart) { doc.Blocks.Add(chart); return; } // NotchWindow.LauncherChatRich
            }
            var bar = new Grid { Margin = new Thickness(9, 4, 8, 0) };
            bar.Children.Add(new TextBlock { Text = language.Length > 0 ? language : "code", FontSize = 9.5, Foreground = Frozen(A(acc, 0xB0)), FontFamily = ChatMono });
            if (!open)
            {
                var copy = new TextBlock { Text = "Copy", FontSize = 9.5, Foreground = DimBrush, HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.Hand };
                copy.MouseEnter += (_, _) => copy.Foreground = Frozen(acc);
                copy.MouseLeave += (_, _) => copy.Foreground = DimBrush;
                copy.MouseLeftButtonUp += (_, e) => { e.Handled = true; CopyText(body, "Code copied"); };
                bar.Children.Add(copy);
            }
            // the code itself is selectable too
            var text = new TextBox
            {
                Text = body, IsReadOnly = true, FontFamily = ChatMono, FontSize = 11, Foreground = Frozen(CodeTone), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 1, 6, 6), Padding = new Thickness(0), SelectionBrush = Frozen(acc), SelectionOpacity = 0.35, FocusVisualStyle = null,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, IsUndoEnabled = false, CaretBrush = Brushes.Transparent,
            };
            text.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (text.SelectionLength == 0) _launchBox?.Focus(); });
            var stack = new StackPanel();
            stack.Children.Add(bar);
            stack.Children.Add(text);
            if (!open && !streaming && Runnable(language))
            {
                var run = new TextBlock { Text = _cyber ? "\u25B6 RUN" : "\u25B6 Run", FontSize = 9.5, Foreground = Frozen(A(acc, 0xE0)), HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 44, 0), ToolTip = "Run it on this PC (anything risky asks first)" };
                run.MouseEnter += (_, _) => run.Foreground = Frozen(Lighten(acc, 0.3));
                run.MouseLeave += (_, _) => run.Foreground = Frozen(A(acc, 0xE0));
                run.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (run.IsEnabled) RunCodeBlock(body, stack, run); };
                bar.Children.Add(run);
            }
            doc.Blocks.Add(new BlockUIContainer(new Border
            {
                Background = Frozen(Color.FromArgb(0x50, 0x00, 0x00, 0x00)), BorderBrush = Frozen(A(acc, _cyber ? (byte)0x50 : (byte)0x2A)), BorderThickness = new Thickness(_cyber ? 1 : 1, 1, 1, 1),
                CornerRadius = new CornerRadius(_cyber ? 0 : 8), Child = stack,
            }) { Margin = new Thickness(0, 3, 0, 7) });
        }

        foreach (var raw in lines)
        {
            string line = raw.TrimEnd();
            // a table: its rows are gathered, then drawn as one
            if (!inCode && IsTableRow(line)) { Flush(); tableRows.Add(line); continue; }
            if (tableRows.Count > 0) EmitTable();
            // a card: drawn from the notch's live numbers once the answer is complete
            if (!inCode && CardLine.Match(line) is { Success: true } card)
            {
                Flush();
                if (!streaming && AnswerCard(card.Groups["k"].Value) is { } block) doc.Blocks.Add(block);
                continue;
            }
            if (!inCode && streaming && line.TrimStart().StartsWith("[[")) continue; // a card line still arriving
            if (line.TrimStart().StartsWith("```"))
            {
                if (inCode) { Code(code.ToString().TrimEnd('\n'), lang, open: false); code.Clear(); inCode = false; }
                else { Flush(); inCode = true; lang = line.TrimStart()[3..].Trim(); }
                continue;
            }
            if (inCode) { code.Append(raw).Append('\n'); continue; }
            if (line.Trim().Length == 0) { Flush(); continue; }
            // a picture on its own line: ![what](https://...)
            var pic = Regex.Match(line, @"^\s*!\[([^\]]*)\]\((https?://[^)\s]+)\)\s*$");
            if (pic.Success)
            {
                Flush();
                if (!streaming && MediaBlock(pic.Groups[2].Value, pic.Groups[1].Value, video: false) is { } b) doc.Blocks.Add(b);
                else { var pp = new Paragraph { Margin = new Thickness(0, 1, 0, 6) }; pp.Inlines.Add(Link("\U0001F5BC " + (pic.Groups[1].Value.Length > 0 ? pic.Groups[1].Value : "picture"), pic.Groups[2].Value)); doc.Blocks.Add(pp); }
                continue;
            }
            var head = Regex.Match(line, @"^\s*#{1,6}\s+(.*)$");
            if (head.Success)
            {
                Flush();
                var h = new Paragraph { Margin = new Thickness(0, 4, 0, 4), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(_cyber ? CyCyan : Lighten(acc, 0.35)) };
                if (_cyber) h.FontFamily = CyFont;
                Inline(h.Inlines, head.Groups[1].Value.Trim().TrimEnd(':'));
                doc.Blocks.Add(h);
                continue;
            }
            if (Regex.IsMatch(line, @"^\s*(?:---+|\*\*\*+|___+)\s*$"))
            {
                Flush();
                doc.Blocks.Add(new BlockUIContainer(new Border { Height = 1, Background = Frozen(A(acc, 0x30)) }) { Margin = new Thickness(0, 4, 0, 8) });
                continue;
            }
            var quote = Regex.Match(line, @"^\s*>\s?(.*)$");
            if (quote.Success)
            {
                Flush();
                var q = new Paragraph { Margin = new Thickness(0, 1, 0, 5), Padding = new Thickness(9, 1, 0, 1), BorderBrush = Frozen(A(acc, 0x90)), BorderThickness = new Thickness(2, 0, 0, 0), Foreground = Frozen(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)), FontStyle = FontStyles.Italic };
                Inline(q.Inlines, quote.Groups[1].Value);
                doc.Blocks.Add(q);
                continue;
            }
            var item = Regex.Match(line, @"^(\s*)([-*•+]|\d+[.)])\s+(.*)$");
            if (item.Success)
            {
                Flush();
                int depth = Math.Min(3, item.Groups[1].Value.Replace("\t", "    ").Length / 2);
                bool numbered = char.IsDigit(item.Groups[2].Value[0]);
                var li = new Paragraph { Margin = new Thickness(16 + depth * 14, 0, 0, 3), TextIndent = -16 };
                li.Inlines.Add(new Run(numbered ? item.Groups[2].Value.TrimEnd(')', '.') + ".\u2002" : "•\u2002\u2002") { Foreground = Frozen(_cyber ? CyCyan : acc), FontWeight = FontWeights.SemiBold });
                Inline(li.Inlines, item.Groups[3].Value);
                doc.Blocks.Add(li);
                continue;
            }
            if (para.Length > 0) para.Append(' ');
            para.Append(line.Trim());
        }
        EmitTable();
        Flush();
        if (inCode && code.Length > 0) Code(code.ToString(), lang, open: true);
        // videos it mentions get a card (once the answer is written)
        if (!streaming)
            foreach (var url in YouTube.Matches(md).Select(m => m.Value).Distinct().Take(3))
            {
                var full = Regex.Match(md, @"https?://(?:www\.|m\.)?" + Regex.Escape(url) + @"[^\s)\]]*");
                if (MediaBlock(full.Success ? full.Value : "https://" + url, "Video", video: true) is { } v) doc.Blocks.Add(v);
            }
        // still writing: a caret in the theme's colour after the last word
        if (streaming)
        {
            var caret = new Run("\u2009▍") { Foreground = Frozen(_cyber ? CyYellow : Lighten(ChatAccent, 0.2)) };
            if (doc.Blocks.LastBlock is Paragraph lastPara) lastPara.Inlines.Add(caret);
            else doc.Blocks.Add(new Paragraph(caret) { Margin = new Thickness(0) });
        }
        if (doc.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
    }

    /// <summary>
    /// **bold** (the theme's highlight), *italic*, `code`, [links](url), and plain web addresses and file paths, which
    /// become links too (a path opens; a file shows in its folder with Ctrl).
    /// </summary>
    void Inline(InlineCollection to, string s)
    {
        int at = 0;
        var acc = ChatAccent;
        foreach (Match m in Regex.Matches(s, @"\*\*(.+?)\*\*|__(.+?)__|`([^`]+)`|\[([^\]]+)\]\(((?:https?://|file:)(?:[^()\s]|\([^()\s]*\))+)\)|(?<![\w*])\*([^*\s][^*]*?)\*(?!\w)|(?<url>https?://[^\s<>()\[\]""']+[^\s<>()\[\]""'.,;:!?])|(?<path>\b[A-Za-z]:\\[^\s<>|""*?]*[^\s<>|""*?.,;:)])"))
        {
            if (m.Index < at) continue; // swallowed by a path that went on past a space
            if (m.Index > at) to.Add(new Run(s[at..m.Index]));
            if (m.Groups[1].Success || m.Groups[2].Success)
            {
                // bold can hold a link, a path or code: drawn inside it
                var span = new Span { FontWeight = FontWeights.SemiBold, Foreground = Frozen(StrongTone) };
                Inline(span.Inlines, m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                to.Add(span);
            }
            else if (m.Groups[3].Success && Regex.IsMatch(m.Groups[3].Value.Trim(), @"^[A-Za-z]:\\|^https?://\S+$"))
            {
                // a path or address in `code` is still something to open
                var link = Link(m.Groups[3].Value.Trim(), m.Groups[3].Value.Trim());
                link.FontFamily = ChatMono;
                link.FontSize = 11.5;
                to.Add(link);
            }
            else if (m.Groups[3].Success)
                to.Add(new Run("\u200A" + m.Groups[3].Value + "\u200A") { FontFamily = ChatMono, FontSize = 11.5, Foreground = Frozen(CodeTone), Background = Frozen(A(acc, _cyber ? (byte)0x1E : (byte)0x26)) });
            else if (m.Groups[4].Success) to.Add(Link(m.Groups[4].Value, m.Groups[5].Value));
            else if (m.Groups[6].Success) to.Add(new Run(m.Groups[6].Value) { FontStyle = FontStyles.Italic });
            else if (m.Groups["url"].Success) to.Add(Link(m.Groups["url"].Value, m.Groups["url"].Value));
            else if (m.Groups["path"].Success)
            {
                string path = WholePath(s, m.Index, m.Groups["path"].Value);
                to.Add(Link(path, path));
                at = m.Index + path.Length;
                continue;
            }
            at = m.Index + m.Length;
        }
        if (at < s.Length) to.Add(new Run(s[at..]));
    }

    /// <summary>
    /// "C:\Users\Me\My Documents\Trip plan.docx" stops at the first space as text: when that part doesn't exist, the
    /// following words are added one by one until it does (at most 8 words, only real files and folders count).
    /// </summary>
    static string WholePath(string s, int start, string found)
    {
        try
        {
            if (System.IO.File.Exists(found) || System.IO.Directory.Exists(found)) return found;
            int end = start + found.Length;
            string best = found;
            for (int words = 0; words < 8 && end < s.Length && s[end] == ' '; words++)
            {
                int next = end + 1;
                while (next < s.Length && !char.IsWhiteSpace(s[next]) && s[next] is not ('"' or '<' or '>' or '|' or '*' or '?')) next++;
                string candidate = s[start..next].TrimEnd('.', ',', ';', ':', ')', '!', '?');
                if (System.IO.File.Exists(candidate) || System.IO.Directory.Exists(candidate)) best = candidate;
                end = next;
            }
            return best;
        }
        catch { return found; }
    }

    Hyperlink Link(string text, string target)
    {
        var h = new Hyperlink(new Run(text)) { Foreground = Frozen(_cyber ? CyCyan : Lighten(ChatAccent, 0.25)), Cursor = Cursors.Hand, ToolTip = target, TextDecorations = null };
        h.TextDecorations = new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Underline, Pen = new Pen(Frozen(A(ChatAccent, 0x90)), 1), PenOffset = 1 } };
        h.Click += (_, _) => OpenLink(target);
        return h;
    }

    void OpenLink(string target)
    {
        try
        {
            bool path = Regex.IsMatch(target, @"^[A-Za-z]:\\");
            if (path && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && System.IO.File.Exists(target)) { RevealInFolder(target); return; }
            if (path && !System.IO.File.Exists(target) && !System.IO.Directory.Exists(target)) { Toast("Not found: " + System.IO.Path.GetFileName(target)); return; }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { Toast("Couldn't open it: " + ex.Message); }
    }

}
