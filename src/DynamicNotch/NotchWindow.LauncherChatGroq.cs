using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Groq, the second engine, from the chat's side: its short instructions (it takes 8,000 tokens a minute, so it gets
/// the essentials: who it is, how to act, what's live now, what it learned), and adding its key, three ways: the card
/// offered once after Google is connected, typing /groq, or simply pasting a gsk_ key into the chat.
/// </summary>
public partial class NotchWindow
{
    /// <summary>The AI's instructions, the short way (for Groq): everything it needs to act well, nothing more.</summary>
    string CompactSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the AI of Dynamic Notch, a panel at the top of the user's Windows screen (music, calls, alerts, timers, reminders, focus, files, clipboard). You answer and you act on the PC with your tools.");
        sb.AppendLine($"Now: {DateTime.Now:dddd d MMMM yyyy, HH:mm}. Documents: {Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}. Downloads: {System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")}.");
        sb.AppendLine(_settings.AiStyle == "detailed" ? "Style: thorough, clear." : _settings.AiStyle == "balanced" ? "Style: clear and concise." : "Style: ultra brief. The answer first, in as few words as stay clear. No filler, no greeting, no closing question.");
        sb.AppendLine("Format: markdown; **bold** only the key 1-3 words; tables for comparisons; full file paths; no emojis. Answer in the user's language (they may mix English, French, Darija...). Read the intent, forgive typos.");
        sb.AppendLine("Rules: DO things with your tools, never just explain how. Never send the user to check something: look it up (notch_data for calls, reminders, notifications, music, PC; research for facts that change). Several parts in one request: do them all, in order. Play music or a video: play_music (one step). Inside an app: use_app look, then click/type/keys, then look again to check. Never claim something happened without a tool result saying so. Ask back only when two very different actions fit.");
        sb.AppendLine("Truth: never make up links, video IDs, paths, names or numbers; use only what a tool returned or the user gave. Videos and live streams: the youtube tool. Stay on topic: mention LIVE NOW details only when asked about them.");
        sb.AppendLine("Honesty: say sent/played/done only when a tool result says so. Messages: only when asked, with send_message. After typing or clicking, look before saying it worked. Answer in the user's language and alphabet (Darija in Latin letters → Latin letters).");
        sb.AppendLine("After acting: one short line saying what changed.");
        sb.AppendLine("End answers that lead somewhere with a last line '>> a | b | c': 2-3 short next steps in the user's words. Device batteries: put [[card:batteries]]. PC load now: [[card:pc]].");
        sb.AppendLine("LIVE NOW:\n" + LiveNow());
        if (_settings.AiMemory)
        {
            var rules = AiMemory.Rules();
            if (rules.Count > 0) sb.AppendLine("STANDING RULES from the user (always follow):\n- " + string.Join("\n- ", rules));
            var facts = AiMemory.Facts();
            if (facts.Count > 0) sb.AppendLine("About the user: " + string.Join("; ", facts.TakeLast(10)));
            var notes = AiMemory.Notes();
            if (notes.Count > 0) sb.AppendLine("Notes kept for the user (give back when asked):\n- " + string.Join("\n- ", notes.TakeLast(10).Select(n => n.Length > 300 ? n[..300] + "…" : n)));
            sb.AppendLine("When the user says remember / save this / don't forget / retiens: save it with remember (rule, note or fact) and confirm in one line.");
            var lessons = AiLearn.Lessons();
            if (lessons.Count > 0) sb.AppendLine("Lessons learned on this PC (follow them):\n- " + string.Join("\n- ", lessons.TakeLast(8)));
            var habits = AiLearn.Habits();
            if (habits.Count > 0) sb.AppendLine("Habits: " + string.Join("; ", habits));
            var like = AiLearn.Like(_chatLastUser);
            if (like.Count > 0) sb.AppendLine("Done before like this (go straight to what worked):\n- " + string.Join("\n- ", like.Take(2).Select(r => $"“{r.Ask}” → {r.Steps}")));
            sb.AppendLine("When the user corrects you, fix it and save the rule with learn.");
        }
        if (_settings.AiInstructions.Trim().Length > 0) sb.AppendLine("The user's own instructions: " + _settings.AiInstructions.Trim());
        sb.AppendLine(ReadTheConversation);
        sb.AppendLine(SoundHuman);
        sb.AppendLine(TaskMethod);
        sb.AppendLine(ChartRule);
        if (_turnBrief.Length > 0) sb.AppendLine(_turnBrief);
        return sb.ToString().TrimEnd();
    }

    /// <summary>A Groq key: checked with Groq, kept encrypted, and from then on the two engines share the work.</summary>
    async Task<string?> AddGroqKey(string key)
    {
        bool? ok = await OpenRouter.CheckKey(key);
        if (ok == false) return "Groq says this key isn't valid (deleted, or not copied whole). Create a new one and paste it.";
        if (!OpenRouter.SaveKey(key)) return "Couldn't save the key on this PC.";
        _settings.AiProvider = "gemini";
        _settings.AiGroqAsked = true;
        _settings.Save();
        OpenRouter.Choose(_settings.AiProvider);
        _ = OpenRouter.GroqModels();
        return null;
    }

    /// <summary>Pasted into the chat: added on the spot, said in one line.</summary>
    async void GroqKeyPasted(string key)
    {
        var note = ChatNote("", "Checking the Groq key…", tone: ChatAccent);
        string? problem = await AddGroqKey(key);
        note.Text = problem ?? (OpenRouter.HasGeminiKey
            ? "Groq added. Google and Groq now share the work: quick things go to Groq (under a second), big or visual ones to Google, and each covers for the other."
            : "Groq connected. It answers in under a second. Add a free Google key too (/key) for pictures and a second allowance.");
        note.Foreground = Frozen(problem == null ? Ok : Warn);
        UpdateChatHeader();
    }

    /// <summary>The offer: a free Groq key as the second engine, in three steps, with its own box.</summary>
    void ChatGroqCard(string? problem = null)
    {
        if (_chatStack == null) return;
        var acc = ChatAccent;
        var icons = (FontFamily)FindResource("IconFont");
        var tone = StepTone("news");
        var card = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(_cyber ? 0 : 8), Margin = new Thickness(0, 0, 10, 0), Background = Frozen(new LinearGradientBrush(A(tone, 0x50), A(tone, 0x18), 45)),
            Child = new TextBlock { Text = "", FontFamily = icons, FontSize = 13, Foreground = Frozen(tone), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = _cyber ? "ADD A SECOND ENGINE // GROQ" : "Add a second AI (free, optional)", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _cyber ? Frozen(CyYellow) : Brushes.White, FontFamily = _cyber ? CyFont : UiText });
        titles.Children.Add(new TextBlock { Text = "Groq answers in under a second and takes over when Google is busy or used up.", FontSize = 10.5, Foreground = DimBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
        head.Children.Add(titles);
        card.Children.Add(head);

        var steps = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Step(string n, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(_cyber ? 0 : 9), Margin = new Thickness(0, 0, 8, 0), Background = Frozen(A(tone, 0x30)), BorderBrush = Frozen(tone), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = n, FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Frozen(tone), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            row.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            steps.Children.Add(row);
        }
        Step("1", "Open Groq's site and sign in (free, Google sign-in works)");
        Step("2", "Click “Create API Key”, name it, copy it");
        Step("3", "Paste it below");
        card.Children.Add(steps);

        var row2 = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var open = ChatChip("Get a Groq key", "");
        open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("https://console.groq.com/keys") { UseShellExecute = true }); } catch { } };
        row2.Children.Add(open);
        card.Children.Add(row2);

        var field = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        field.ColumnDefinitions.Add(new ColumnDefinition());
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new PasswordBox
        {
            Height = 30, FontSize = 12, Padding = new Thickness(8, 3, 6, 3), Foreground = Brushes.White, CaretBrush = Frozen(tone),
            Background = Frozen(Color.FromArgb(0x50, 0x00, 0x00, 0x00)), BorderBrush = Frozen(A(tone, 0x70)), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center,
        };
        var hint = new TextBlock { Text = "gsk_…", FontSize = 12, Foreground = DimBrush, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        box.PasswordChanged += (_, _) => hint.Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var host = new Grid();
        host.Children.Add(box);
        host.Children.Add(hint);
        field.Children.Add(host);
        var paste = ChatChip("Paste", "");
        paste.Height = 30;
        paste.Margin = new Thickness(6, 0, 0, 0);
        paste.Click += (_, _) => { try { if (Clipboard.ContainsText()) box.Password = Clipboard.GetText().Trim(); } catch { } box.Focus(); };
        Grid.SetColumn(paste, 1);
        field.Children.Add(paste);
        var add = ChatChip("Add", "");
        add.Height = 30;
        add.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(add, 2);
        field.Children.Add(add);
        card.Children.Add(field);
        var status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible, Text = problem ?? "", Foreground = Frozen(Warn) };
        card.Children.Add(status);
        var later = new TextBlock { Text = "Not now (type /groq any time)", FontSize = 10, Foreground = DimBrush, Cursor = Cursors.Hand, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        card.Children.Add(later);

        var border = new Border
        {
            Child = card, Padding = new Thickness(13, 11, 13, 11), Margin = new Thickness(0, 8, 8, 4), CornerRadius = new CornerRadius(_cyber ? 0 : 14), BorderThickness = new Thickness(1),
            Background = Frozen(new LinearGradientBrush(A(tone, 0x16), Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF), 90)), BorderBrush = Frozen(new LinearGradientBrush(A(tone, 0x80), A(acc, 0x30), 45)),
        };
        later.MouseLeftButtonUp += (_, e) => { e.Handled = true; _settings.AiGroqAsked = true; _settings.Save(); _chatStack?.Children.Remove(border); _launchBox?.Focus(); };
        async void Submit()
        {
            string raw = box.Password.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"gsk_[A-Za-z0-9]{20,}");
            status.Visibility = Visibility.Visible;
            if (!m.Success) { status.Foreground = Frozen(Warn); status.Text = "That isn't a Groq key: it starts with gsk_."; Bump(1); return; }
            status.Foreground = DimBrush;
            status.Text = "Checking with Groq…";
            add.IsEnabled = false;
            string? problem2 = await AddGroqKey(m.Value);
            add.IsEnabled = true;
            if (problem2 != null) { status.Foreground = Frozen(Warn); status.Text = problem2; Bump(1); return; }
            await Connected(card, "Google and Groq now share the work");
            _chatStack?.Children.Remove(border);
            if (_session is { UserTurns: 0 } || _session == null) RenderSession();
            UpdateChatHeader();
            _launchBox?.Focus();
        }
        add.Click += (_, _) => Submit();
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        _chatStack.Children.Add(border);
        Arrive(border);
        FollowChat(force: true);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => box.Focus());
    }
}
