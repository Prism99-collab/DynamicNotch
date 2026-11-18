using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The chat's extras, like any AI chat app: a model switcher on the model's name, a ⋯ menu (attach a file, rename,
/// export as Markdown, copy the whole chat, delete it), the approval card for risky commands, and the notifications
/// the AI can read when you ask ("what did I miss?"), kept in memory only.
/// </summary>
public partial class NotchWindow
{
    bool _chatModal;          // a file dialog is up: the search stays open behind it
    Grid? _attachNote;
    readonly List<(DateTime At, string App, string Title, string Body)> _recentToasts = new();

    void KeepToast(ToastInfo t)
    {
        if (!_settings.AiChat) return;
        lock (_recentToasts)
        {
            _recentToasts.Add((DateTime.Now, t.App, t.Title, t.Body));
            if (_recentToasts.Count > 25) _recentToasts.RemoveAt(0);
        }
    }

    internal string RecentToastsText()
    {
        lock (_recentToasts)
        {
            if (_recentToasts.Count == 0) return "No notifications since the notch started.";
            return string.Join("\n", _recentToasts.AsEnumerable().Reverse().Select(t => $"{t.At:HH:mm} · {t.App}: {t.Title}{(t.Body.Length > 0 ? " — " + (t.Body.Length > 200 ? t.Body[..200] + "…" : t.Body) : "")}"));
        }
    }

    // ───────────────────────────── a small menu ─────────────────────────────

    /// <summary>
    /// The chat's small menus, drawn as one card: each row an icon in its own tile, the name and a dim line under it;
    /// settings that switch on and off get a little switch, "Name: value" rows show the value as a chip, Delete is red.
    /// It opens right under its button, aligned to the side the button is on (above it when there's no room below),
    /// sliding in a few pixels.
    /// </summary>
    Popup Menu(UIElement under, IEnumerable<(string Glyph, string Text, string? Note, bool On, Action Act)> items, ICollection<string>? toggles = null)
    {
        var acc = ChatAccent;
        var icons = (FontFamily)FindResource("IconFont");
        var danger = Color.FromRgb(0xFF, 0x5A, 0x5F);
        var list = new StackPanel { Margin = new Thickness(5) };
        var popup = new Popup { PlacementTarget = under, Placement = PlacementMode.Custom, AllowsTransparency = true, StaysOpen = false, PopupAnimation = PopupAnimation.Fade };
        bool rightSide = under is FrameworkElement fe && fe.ActualWidth < 160;
        popup.CustomPopupPlacementCallback = (size, target, _) => new[]
        {
            new CustomPopupPlacement(new Point(rightSide ? target.Width - size.Width + 8 : -8, target.Height - 2), PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(new Point(rightSide ? target.Width - size.Width + 8 : -8, -size.Height + 2), PopupPrimaryAxis.Vertical),
        };
        foreach (var (glyph, text, note, on, act) in items)
        {
            if (text == "-")
            {
                list.Children.Add(new Border { Height = 1, Margin = new Thickness(10, 5, 10, 5), Background = Frozen(new LinearGradientBrush(A(acc, 0x00), A(acc, 0x50), 0) { GradientStops = { new GradientStop(A(acc, 0x00), 0), new GradientStop(A(acc, 0x50), 0.5), new GradientStop(A(acc, 0x00), 1) } }) });
                continue;
            }
            bool isToggle = toggles?.Contains(text) == true;
            bool bad = text.StartsWith("Delete", StringComparison.OrdinalIgnoreCase);
            var tone = bad ? danger : acc;
            string name = text, value = "";
            if (!isToggle && text.IndexOf(": ", StringComparison.Ordinal) is int colon and > 0 and < 24) { name = text[..colon]; value = text[(colon + 2)..]; }

            var row = new Grid { Height = note != null ? 42 : 32, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var plate = new Border { CornerRadius = new CornerRadius(_cyber ? 0 : 8), BorderThickness = new Thickness(_cyber ? 2 : 0, 0, 0, 0) };
            Grid.SetColumnSpan(plate, 3);
            row.Children.Add(plate);

            // the icon, in its own little tile: lit when the row is on
            bool lit = on && !isToggle || isToggle && on;
            var tile = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(_cyber ? 0 : 7), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Background = Frozen(lit ? A(tone, 0x2C) : bad ? A(danger, 0x18) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderBrush = Frozen(lit ? A(tone, 0x70) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = glyph, FontFamily = icons, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Frozen(lit || bad ? tone : Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)),
                },
            };
            row.Children.Add(tile);

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 10, 0) };
            words.Children.Add(new TextBlock
            {
                Text = name, FontSize = 12, TextAlignment = TextAlignment.Left, HorizontalAlignment = HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = bad ? Frozen(danger) : on && !isToggle ? Frozen(acc) : Brushes.White, FontWeight = on && !isToggle ? FontWeights.SemiBold : FontWeights.Normal, FontFamily = _cyber ? CyFont : UiText,
            });
            if (note != null)
                words.Children.Add(new TextBlock { Text = note, FontSize = 10, Margin = new Thickness(0, 1, 0, 0), TextAlignment = TextAlignment.Left, HorizontalAlignment = HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Frozen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)) });
            Grid.SetColumn(words, 1);
            row.Children.Add(words);

            FrameworkElement? side = null;
            if (isToggle)
            {
                // a small switch
                var knob = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(_cyber ? 0 : 5), Background = on ? Brushes.White : Frozen(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(2, 0, 2, 0) };
                side = new Border
                {
                    Width = 26, Height = 14, CornerRadius = new CornerRadius(_cyber ? 0 : 7), Child = knob,
                    Background = Frozen(on ? new LinearGradientBrush(acc, AiSecond, 0) : (Brush)new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF))),
                    BorderBrush = Frozen(on ? A(acc, 0xC0) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                };
            }
            else if (value.Length > 0)
                side = new Border
                {
                    CornerRadius = new CornerRadius(_cyber ? 0 : 6), Padding = new Thickness(7, 1, 7, 2), Background = Frozen(A(acc, 0x22)), BorderBrush = Frozen(A(acc, 0x60)), BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = _cyber ? value.ToUpperInvariant() : value, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Frozen(acc), FontFamily = _cyber ? CyFont : UiText },
                };
            else if (on)
                side = new TextBlock { Text = "\uE73E", FontFamily = icons, FontSize = 10.5, Foreground = Frozen(acc) };
            if (side != null)
            {
                side.VerticalAlignment = VerticalAlignment.Center;
                side.Margin = new Thickness(0, 0, 8, 0);
                Grid.SetColumn(side, 2);
                row.Children.Add(side);
            }

            row.MouseEnter += (_, _) => { plate.Background = Frozen(new LinearGradientBrush(A(tone, 0x30), A(tone, 0x0C), 0)); plate.BorderBrush = Frozen(tone); };
            row.MouseLeave += (_, _) => { plate.Background = Brushes.Transparent; plate.BorderBrush = null; };
            var action = act;
            row.MouseLeftButtonDown += (_, e) => e.Handled = true;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; popup.IsOpen = false; action(); };
            list.Children.Add(row);
        }
        var card = new Border
        {
            Child = list, MinWidth = 250, MaxWidth = 320, CornerRadius = new CornerRadius(_cyber ? 0 : 12), BorderThickness = new Thickness(1), Margin = new Thickness(8, 6, 8, 10),
            Background = Frozen(_cyber
                ? new LinearGradientBrush(Color.FromArgb(0xFA, 0x07, 0x12, 0x1B), Color.FromArgb(0xFA, 0x03, 0x08, 0x0D), 90)
                : new LinearGradientBrush(Color.FromArgb(0xFA, 0x26, 0x26, 0x2E), Color.FromArgb(0xFA, 0x18, 0x18, 0x1D), 90)),
            BorderBrush = Frozen(new LinearGradientBrush(A(acc, _cyber ? (byte)0xB0 : (byte)0x70), A(AiSecond, _cyber ? (byte)0x50 : (byte)0x24), 90)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.55, Color = Colors.Black },
            RenderTransform = new TranslateTransform(0, -6),
        };
        popup.Child = card;
        // text in the menu is text: it mustn't inherit the icon font of the button it opened from
        System.Windows.Documents.TextElement.SetFontFamily(popup.Child, UiText);
        System.Windows.Documents.TextElement.SetForeground(popup.Child, Brushes.White);
        popup.Opened += (_, _) =>
        {
            if (Eco) { card.RenderTransform = Transform.Identity; return; }
            ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
        };
        popup.Closed += (_, _) => _launchBox?.Focus();
        popup.IsOpen = true;
        return popup;
    }

    static readonly (string Id, string Name, string Note)[] QuickModels =
    {
        (OpenRouter.DefaultModel, "Free", "No cost · can be slow or busy"),
        ("deepseek/deepseek-v4-flash", "DeepSeek Flash", "Very cheap · fast"),
        ("google/gemini-3-flash-preview", "Gemini Flash", "Cheap · fast, good with tools"),
        ("anthropic/claude-haiku-5.5", "Claude Haiku", "Cheap · quick and careful"),
        ("anthropic/claude-sonnet-5.5", "Claude Sonnet", "Best at doing things on the PC"),
    };

    async void ModelMenu()
    {
        if (_chatModelChip == null) return;
        if (OpenRouter.IsGemini)
        {
            var models = await OpenRouter.GeminiModels();
            var g = new List<(string, string, string?, bool, Action)> { ("\uE945", "Auto", "The best that still has free requests today", ChatModel == OpenRouter.DefaultModel, () => SetModel(OpenRouter.DefaultModel)) };
            foreach (var m in models)
                g.Add(("\uE950", m.Name, m.Id.Contains("flash-lite") ? "Fast · hears and sees · ~500 free a day" : m.Id.StartsWith("gemma") ? "Most free requests · ~1,500 a day" : m.Id.Contains("flash") ? "Smartest · only ~20 free a day" : m.Id, ChatModel == m.Id, () => SetModel(m.Id)));
            g.Add(("", "-", null, false, () => { }));
            g.Add(("\uE713", "All AI settings…", null, false, () => { CloseLauncher(giveBack: false); PreferencesWindow.ShowFor(this, PreferencesWindow.AiSection); }));
            Menu(_chatModelChip, g);
            return;
        }
        var items = QuickModels.Select(m => ("", m.Name, (string?)m.Note, ChatModel == m.Id, (Action)(() => SetModel(m.Id)))).ToList();
        if (!QuickModels.Any(m => m.Id == ChatModel)) items.Insert(0, ("", ModelName(ChatModel), "Your own pick", true, () => { }));
        items.Add(("", "-", null, false, () => { }));
        items.Add(("", "All AI settings…", null, false, () => { CloseLauncher(giveBack: false); PreferencesWindow.ShowFor(this, PreferencesWindow.AiSection); }));
        Menu(_chatModelChip, items);
    }

    void SetModel(string id)
    {
        if (OpenRouter.IsGemini) _settings.AiGeminiModel = id; else _settings.AiModel = id;
        _settings.Save();
        UpdateChatHeader();
        Toast("Model: " + ModelName(id));
    }

    /// <summary>Typing "/" alone: the chat's commands, one click to use.</summary>
    void CommandMenu()
    {
        if (_launchBox == null) return;
        void Use(string text) { _launchBox.Text = text; _launchBox.CaretIndex = text.Length; _launchBox.Focus(); }
        Menu(_launchBox, new List<(string, string, string?, bool, Action)>
        {
            ("\uE710", "/new", "A new chat", false, () => { _launchBox.Text = ""; NewChat(); }),
            ("\uE81C", "/chats", "Your saved chats", false, () => { _launchBox.Text = ""; ShowChatView(ChatView.History); }),
            ("\uE7A7", "/undo", "Take back the last thing it did", false, () => { _launchBox.Text = ""; Toast(UndoLast()); }),
            ("\uE950", "/model", "Which model answers", false, () => { _launchBox.Text = ""; ModelMenu(); }),
            ("\uE8D7", "/key", "Enter the key again", false, () => Use("/key")),
            ("\uE8BD", "My day so far", "Calls, reminders, notifications, PC", false, () => { _launchBox.Text = ""; SendChat("Give me a short summary of my day so far."); }),
        });
    }

    void MoreMenu()
    {
        if (_moreButton == null) return;
        bool has = _session is { UserTurns: > 0 };
        var items = new List<(string, string, string?, bool, Action)>
        {
            ("\uE723", "Attach a file or picture…", "Text, documents or images, with your next message", false, AttachFile),
            ("\uE7F4", "Ask about my screen", "A picture of your screen goes with your next message", false, AttachScreen),
            ("", "-", null, false, () => { }),
            ("\uE8D2", "Answers: " + (_settings.AiStyle switch { "detailed" => "Detailed", "balanced" => "Balanced", _ => "Brief" }), "Click to change how much it writes", false, () =>
            {
                _settings.AiStyle = _settings.AiStyle switch { "brief" => "balanced", "balanced" => "detailed", _ => "brief" };
                _settings.Save();
                Toast("Answers: " + (_settings.AiStyle switch { "detailed" => "detailed", "balanced" => "balanced", _ => "brief" }));
            }),
            ("\uE774", "Use the web", _settings.AiWeb ? "On: it searches when it needs to" : "Off: answers from what it knows", _settings.AiWeb, () => { _settings.AiWeb = !_settings.AiWeb; _settings.Save(); Toast(_settings.AiWeb ? "Web on" : "Web off"); }),
            ("\uE945", "Act on my PC", _settings.AiTools ? "On: commands, files, windows (risky ones ask)" : "Off: it only talks", _settings.AiTools, () => { _settings.AiTools = !_settings.AiTools; _settings.Save(); Toast(_settings.AiTools ? "Acting on the PC: on" : "Acting on the PC: off"); }),
            ("\uE767", "Read answers aloud", _settings.AiSpeak == "always" ? "Always" : "Only when you talk", _settings.AiSpeak == "always", () => { _settings.AiSpeak = _settings.AiSpeak == "always" ? "voice" : "always"; _settings.Save(); PaintSpeaker(false); }),
        };
        if (has)
        {
            items.Add(("", "Rename this chat", null, false, RenameChat));
            items.Add(("", "Export as Markdown…", null, false, ExportChat));
            items.Add(("", "Copy the whole chat", null, false, () => CopyText(Transcript(), "Chat copied")));
            items.Add(("", "-", null, false, () => { }));
            items.Add(("", "Delete this chat", null, false, () => { if (_session != null) ChatStore.Delete(_session.Id); NewChat(); Toast("Chat deleted"); }));
        }
        Menu(_moreButton, items, new[] { "Use the web", "Act on my PC", "Read answers aloud" });
    }

    // ───────────────────────────── attach, rename, export ─────────────────────────────

    void AttachFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Attach a file to your next message", Filter = "Text, documents and pictures|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.txt;*.md;*.csv;*.json;*.log;*.xml;*.html;*.docx;*.cs;*.py;*.js;*.ts;*.java;*.cpp;*.c;*.h;*.sql;*.ini;*.yaml;*.yml;*.ps1;*.bat|All files|*.*" };
        _chatModal = true;
        bool ok;
        try { ok = dlg.ShowDialog(ChatPopped ? _pop : this) == true; }
        finally { _chatModal = false; }
        if (ChatPopped) _pop!.Activate(); else { TakeForeground(); Activate(); }
        _launchBox?.Focus();
        if (!ok) return;
        if (PictureExts.Contains(Path.GetExtension(dlg.FileName).ToLowerInvariant())) { AddPictureFile(dlg.FileName); return; }
        string text = ReadAnyText(dlg.FileName);
        if (text.StartsWith("Error") || text.StartsWith("This is not a text file")) { Toast("Can't read that file as text"); return; }
        if (text.Length > 24000) text = text[..24000] + "\n…(cut)";
        _chatAttach = (Path.GetFileName(dlg.FileName), text);
        ShowAttachments();
    }

    void ShowAttachNote(string name, int chars)
    {
        ClearAttachNote();
        if (_chatStack == null) return;
        var acc = ChatAccent;
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(_cyber ? 0 : 9), Padding = new Thickness(9, 4, 6, 4), Background = Frozen(A(acc, 0x20)), BorderBrush = Frozen(A(acc, 0x70)), BorderThickness = new Thickness(1),
        };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10.5, Foreground = Frozen(acc), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
        line.Children.Add(new TextBlock { Text = $"{name} · {chars / 1000.0:0.#}k characters · goes with your next message", FontSize = 10.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        var x = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = DimBrush, Cursor = Cursors.Hand, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Remove" };
        x.MouseLeftButtonUp += (_, e) => { e.Handled = true; _chatAttach = null; ClearAttachNote(); _launchBox?.Focus(); };
        line.Children.Add(x);
        chip.Child = line;
        row.Children.Add(chip);
        _attachNote = row;
        _chatStack.Children.Add(row);
        Arrive(row, fromRight: true);
        FollowChat(force: true);
    }

    void ClearAttachNote()
    {
        if (_attachNote != null) _chatStack?.Children.Remove(_attachNote);
        _attachNote = null;
    }

    /// <summary>The chat's name becomes a box in the header: Enter keeps the new name, Esc leaves it.</summary>
    void RenameChat()
    {
        if (_session == null || _chatTitle?.Parent is not Grid head) return;
        var box = new TextBox
        {
            Text = _session.Title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Background = Frozen(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            BorderBrush = Frozen(A(ChatAccent, 0x90)), BorderThickness = new Thickness(1), Padding = new Thickness(4, 1, 4, 1), CaretBrush = Frozen(ChatAccent), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(box, 1);
        head.Children.Add(box);
        _chatTitle.Visibility = Visibility.Hidden;
        bool done = false;
        void Finish(bool keep)
        {
            if (done) return;
            done = true;
            if (keep && box.Text.Trim().Length > 0 && _session != null)
            {
                _session.Title = box.Text.Trim().Length > 60 ? box.Text.Trim()[..60] : box.Text.Trim();
                if (_settings.AiHistory && _session.UserTurns > 0) { var s = _session; _ = Task.Run(() => ChatStore.Save(s)); }
            }
            head.Children.Remove(box);
            _chatTitle.Visibility = Visibility.Visible;
            UpdateChatHeader();
            _launchBox?.Focus();
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        box.Focus();
        box.SelectAll();
    }

    string Transcript()
    {
        if (_session == null) return "";
        var sb = new StringBuilder();
        sb.AppendLine("# " + (_session.Title.Length > 0 ? _session.Title : "Chat"));
        sb.AppendLine($"_{_session.Created:d MMMM yyyy, HH:mm} · {ModelName(_session.Model)}_").AppendLine();
        foreach (var m in _session.Messages)
        {
            string role = m?["role"]?.ToString() ?? "";
            string text = m?["shown"]?.ToString() ?? (m?["content"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? c) ? c : "");
            if (text.Trim().Length == 0 || role is not ("user" or "assistant")) continue;
            sb.AppendLine(role == "user" ? "**You:** " + text : text).AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    void ExportChat()
    {
        if (_session == null) return;
        string name = string.Concat((_session.Title.Length > 0 ? _session.Title : "Chat").Split(Path.GetInvalidFileNameChars())).Trim();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export the chat", FileName = (name.Length > 50 ? name[..50] : name) + ".md", Filter = "Markdown|*.md|Text|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        _chatModal = true;
        bool ok;
        try { ok = dlg.ShowDialog(ChatPopped ? _pop : this) == true; }
        finally { _chatModal = false; }
        if (ChatPopped) _pop!.Activate(); else { TakeForeground(); Activate(); }
        _launchBox?.Focus();
        if (!ok) return;
        try { File.WriteAllText(dlg.FileName, Transcript()); ChatNote("", "Saved " + dlg.FileName, tone: ChatAccent); }
        catch (Exception ex) { Toast("Couldn't save: " + ex.Message); }
    }

    // ───────────────────────────── your OK before a risky command ─────────────────────────────

    /// <summary>
    /// Shows what's about to run, with Run (Enter), Allow for this chat, and Cancel (Esc). Stopping the answer cancels
    /// it too. Once decided, it folds into one line. 0 = no, 1 = yes, 2 = yes and don't ask again for these in this chat.
    /// </summary>
    async Task<int> ConfirmInChat(string why, string code, CancellationToken cancel)
    {
        if (_chatStack == null) return 0;
        var card = new StackPanel();
        card.Children.Add(new TextBlock { Text = (_cyber ? "APPROVAL // " : "Allow this? ") + why, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Frozen(Warn), TextWrapping = TextWrapping.Wrap, FontFamily = _cyber ? CyFont : UiText });
        var codeBox = new Border
        {
            Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(8, 5, 8, 6), Background = Frozen(Color.FromArgb(0x50, 0x00, 0x00, 0x00)), CornerRadius = new CornerRadius(_cyber ? 0 : 6),
            Child = new TextBox { Text = code.Length > 1500 ? code[..1500] + "\n…" : code, IsReadOnly = true, FontFamily = ChatMono, FontSize = 10.5, Foreground = Frozen(CodeTone), Background = Brushes.Transparent, BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        card.Children.Add(codeBox);
        var buttons = new WrapPanel();
        var run = ChatChip("Run  ↵", "\uE768");
        var always = ChatChip("Allow for this chat", "\uE8FB");
        var no = ChatChip("Cancel  Esc", "\uE711");
        always.Margin = no.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(run);
        buttons.Children.Add(always);
        buttons.Children.Add(no);
        card.Children.Add(buttons);
        var border = new Border { Tag = "step", Child = card, Padding = new Thickness(10, 8, 10, 9), Margin = new Thickness(12, 8, 18, 2), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(_cyber ? 0 : 9), Background = Frozen(Color.FromArgb(0x18, Warn.R, Warn.G, Warn.B)), BorderBrush = Frozen(Color.FromArgb(0x80, Warn.R, Warn.G, Warn.B)) };
        _chatStack.Children.Add(border);
        Arrive(border);
        FollowChat(force: true);
        // the notch says so if you're not looking
        if (!(_launcherOpen && _chatMode) && _pop is not { IsActive: true })
            ShowOsd("ai", "\uE7BA", null, "AI needs your OK", why, null, glyphBrush: Frozen(Warn), seconds: 6, force: true, prio: Prio.Normal, onClick: ReopenChat);
        var tcs = new TaskCompletionSource<int>();
        _confirm = new TaskCompletionSource<bool>();
        _ = _confirm.Task.ContinueWith(t => tcs.TrySetResult(t.Result ? 1 : 0), TaskScheduler.FromCurrentSynchronizationContext());
        run.Click += (_, _) => tcs.TrySetResult(1);
        always.Click += (_, _) => tcs.TrySetResult(2);
        no.Click += (_, _) => tcs.TrySetResult(0);
        using var reg = cancel.Register(() => tcs.TrySetResult(0));
        ChatKeys();
        int answer = await tcs.Task;
        _confirm = null;
        // decided: one quiet line instead of the card
        card.Children.Clear();
        card.Children.Add(new TextBlock
        {
            Text = (answer > 0 ? "\u2713  " : "\u2715  ") + why + (answer == 2 ? "  ·  allowed for this chat" : answer == 1 ? "" : "  ·  not run"),
            FontSize = 11, Foreground = Frozen(answer > 0 ? Ok : Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = code,
        });
        border.Padding = new Thickness(10, 4, 10, 4);
        border.Background = Brushes.Transparent;
        border.BorderBrush = Frozen(Color.FromArgb(0x30, Warn.R, Warn.G, Warn.B));
        ChatKeys();
        _launchBox?.Focus();
        return answer;
    }

}
