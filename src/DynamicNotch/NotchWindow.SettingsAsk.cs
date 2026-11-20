using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The Settings page, compact, with "Ask Pixo" on top:
///  • the top line holds the ask box on the left and the tabs (Display · Behaviour · Smart · ⚙) on the right;
///  • rows are short and the switches small, so every tab fits without anything spilling over;
///  • typing in the box finds settings on every tab at once (by name, by what they do, by everyday words: "sound"
///    finds Face sounds, "startup" Start with Windows) and shows them as working switches;
///  • Enter asks Pixo: one or two short lines on how to do it, from a real list of what Pixo has (its settings, keys
///    and gestures), never made up; when a setting does it, its switch is right there under the answer.
/// Esc clears the box and gives the keyboard back.
/// </summary>
public partial class NotchWindow
{
    sealed record SettingEntry(string Label, string Hint, int Tab, Func<bool> Get, Action<bool> Set);
    readonly List<SettingEntry> _settingIndex = new();
    TextBox? _setAsk;
    Grid? _setResults;
    StackPanel? _setRows;
    Border? _setAnswer;
    NotchFace? _setFace;
    CancellationTokenSource? _setAskCts;

    void RegisterSetting(string label, string hint, int tab, Func<bool> get, Action<bool> set)
    {
        if (_settingIndex.Any(e => e.Label == label)) return;
        _settingIndex.Add(new SettingEntry(label, hint, tab, get, set));
    }

    /// <summary>Lays the page out compact and puts the ask box on its top line (once, after the switches are built).</summary>
    void CompactSettings()
    {
        if (SettingsTabs.Parent is not Grid page || _setAsk != null) return;
        page.RowDefinitions[0].Height = new GridLength(30);
        page.RowDefinitions[2].Height = new GridLength(0);
        SettingsHint.Visibility = Visibility.Collapsed;

        // the tabs: a slim pill row on the right
        page.Children.Remove(SettingsTabs);
        foreach (var b in SettingsTabs.Children.OfType<Button>())
        {
            b.Height = 20;
            b.FontSize = 10.5;
            if (b.Content is string) b.Padding = new Thickness(9, 0, 9, 0);
            else b.Width = 22;
            b.Margin = new Thickness(2, 0, 0, 0);
        }
        SettingsTabs.HorizontalAlignment = HorizontalAlignment.Right;
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(SettingsTabs, 1);
        head.Children.Add(AskBox());
        head.Children.Add(SettingsTabs);
        page.Children.Add(head);

        // short rows, small switches, nothing spilling past the page
        foreach (var grid in new[] { SettingsGrid, BehaviourGrid })
        {
            grid.Rows = 0;
            grid.VerticalAlignment = VerticalAlignment.Top;
            grid.Margin = new Thickness(0, 3, 0, 0);
            foreach (var cell in grid.Children.OfType<FrameworkElement>()) Compact(cell, 25);
        }
        SmartGrid.Rows = 0;
        foreach (var cell in SmartGrid.Children.OfType<FrameworkElement>()) Compact(cell, 0);

        // where search results and answers show, over the tab's switches
        _setRows = new StackPanel();
        _setResults = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0), Background = Brushes.Transparent };
        _setResults.Children.Add(new ScrollViewer { Content = _setRows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetRow(_setResults, 1);
        page.Children.Add(_setResults);

        static void Compact(FrameworkElement cell, double height)
        {
            if (height > 0) cell.Height = height;
            if (cell is not Panel p) return;
            foreach (var sw in p.Children.OfType<ToggleButton>()) sw.LayoutTransform = new ScaleTransform(0.8, 0.8);
            foreach (var t in p.Children.OfType<TextBlock>()) t.FontSize = 11.5;
        }
    }

    FrameworkElement AskBox()
    {
        _setFace = new NotchFace(10) { Neon = _cyber, Accent = _cyber ? CyCyan : ChatAccent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 1, 0) };
        _setFace.SetOutfit(FaceOutfitNow(), animate: false);
        var hint = new TextBlock { Text = Loc.T("Ask Pixo or find a setting"), FontSize = 11, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 1) };
        var box = new TextBox
        {
            FontSize = 11, Foreground = Brushes.White, CaretBrush = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 4, 1), FocusVisualStyle = null, SelectionBrush = Frozen(_cyber ? CyCyan : ChatAccent),
            ToolTip = Loc.T("Win+Alt+N opens the notch · drag sideways or swipe the touchpad to change page"),
        };
        _setAsk = box;
        var field = new Grid();
        field.Children.Add(hint);
        field.Children.Add(box);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_setFace, Dock.Left);
        row.Children.Add(_setFace);
        row.Children.Add(field);
        var pill = new Border
        {
            Child = row, Height = 22, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(_cyber ? 0 : 11), Cursor = Cursors.IBeam,
            Background = (Brush)FindResource("Tile"), BorderBrush = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
        };
        pill.MouseLeftButtonDown += (_, e) => { e.Handled = true; BeginWorkTyping(box); };
        box.GotKeyboardFocus += (_, _) => { pill.BorderBrush = Frozen(A(_cyber ? CyCyan : ChatAccent, 0x90)); _setFace.SetMood(NotchFace.Mood.Focus); };
        box.LostKeyboardFocus += (_, _) => { pill.BorderBrush = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)); _setFace.SetMood(NotchFace.Mood.Idle); };
        box.TextChanged += (_, _) =>
        {
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _setAskCts?.Cancel();
            ShowSettingMatches(box.Text);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; box.Text = ""; EndWorkTyping(giveBack: true); return; }
            if (e.Key == Key.Enter && box.Text.Trim().Length > 1) { e.Handled = true; _ = AskSettings(box.Text.Trim()); }
        };
        return pill;
    }

    // everyday words for what the settings do
    static readonly (string Words, string Label)[] SettingWords =
    {
        ("sound sounds voice audio noise mute quiet", "Face sounds"),
        ("face character mascot eyes cute", "Face in the notch"),
        ("startup boot login sign in autostart launch windows", "Start with Windows"),
        ("crash restart closes keep alive background", "Always keep running"),
        ("battery power saver laptop energy", "Battery mode"),
        ("fullscreen full screen movie video game hide", "Hide in full screen"),
        ("fps frames game performance", "FPS in games"),
        ("hover edge open mouse top", "Open at top edge"),
        ("drop drag file files shelf", "Drop files on the notch"),
        ("notification notifications messages popups alert", "Notification alerts"),
        ("copied clipboard copy", "“Copied” alerts"),
        ("lock locked frozen click", "Lock the notch"),
        ("small shrink compact tiny rest idle", "Compact when resting"),
        ("pills bubbles islands side mic camera", "Tuck pills into the notch"),
        ("thin bar minimal slim line", "Minimal when idle"),
        ("tabs browser windows slim title", "Slim over windows"),
        ("music spotify song player artwork", "Music activity"),
        ("visualizer bars equalizer", "Music visualizer"),
        ("companion girl swing netrunner", "Companion"),
        ("apps opening launch app", "App launch alerts"),
        ("system cpu heat usb", "System alerts"),
    };

    IEnumerable<SettingEntry> MatchSettings(string query)
    {
        var words = query.ToLowerInvariant().Split(new[] { ' ', ',', '?', '.', '!', '\'' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToArray();
        if (words.Length == 0) return Array.Empty<SettingEntry>();
        return _settingIndex
            .Select(e =>
            {
                string label = e.Label.ToLowerInvariant(), hint = e.Hint.ToLowerInvariant(), local = Loc.T(e.Label).ToLowerInvariant();
                string everyday = string.Join(' ', SettingWords.Where(s => s.Label == e.Label).Select(s => s.Words));
                int score = 0;
                foreach (var w in words)
                {
                    if (label.Contains(w) || local.Contains(w)) score += 3;
                    else if (everyday.Split(' ').Any(x => x.StartsWith(w) || w.StartsWith(x) && x.Length > 3)) score += 2;
                    else if (hint.Contains(w)) score += 1;
                }
                return (e, score);
            })
            .Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.e).Take(5);
    }

    /// <summary>What's typed: the settings it finds, on every tab, as switches; and "Enter: ask Pixo".</summary>
    void ShowSettingMatches(string query)
    {
        if (_setResults == null || _setRows == null) return;
        bool on = query.Trim().Length > 0;
        bool was = _setResults.Visibility == Visibility.Visible;
        _setResults.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        // the tab's own switches go away completely while results show (not just see-through: a tab's fade-in
        // animation holds its opacity, and they'd show through), and come back as they were
        if (on) foreach (var g in new FrameworkElement[] { SettingsGrid, BehaviourGrid, SmartPanel }) g.Visibility = Visibility.Collapsed;
        else { if (was) SelectSettingsTab(_settingsTab, animate: false); return; }
        _setRows.Children.Clear();
        _setAnswer = null;
        foreach (var e in MatchSettings(query)) _setRows.Children.Add(SettingRow(e));
        _setRows.Children.Add(new TextBlock
        {
            Text = (_setRows.Children.Count == 0 ? Loc.T("No setting by that name") + "  ·  " : "") + "↵  " + Loc.T("Ask Pixo"),
            FontSize = 10.5, Foreground = DimBrush, Margin = new Thickness(4, 6, 0, 0),
        });
    }

    /// <summary>A setting as a short row: its name, its tab, its switch (the same setting as on its tab).</summary>
    FrameworkElement SettingRow(SettingEntry e)
    {
        var cell = new Grid { Height = 26, Margin = new Thickness(4, 0, 4, 0) };
        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = Loc.T(e.Label), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(new TextBlock { Text = "  " + Loc.T(SettingsTabNames[Math.Clamp(e.Tab, 0, SettingsTabNames.Length - 1)]), FontSize = 10, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center });
        cell.Children.Add(name);
        var sw = new ToggleButton { Style = (Style)FindResource("Switch"), IsChecked = e.Get(), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, LayoutTransform = new ScaleTransform(0.8, 0.8) };
        sw.Click += (_, _) =>
        {
            e.Set(sw.IsChecked == true);
            _settings.Save();
            OnSettingsChanged();
            SyncSettingsPage();
            SyncSmartPage();
        };
        cell.Children.Add(sw);
        return cell;
    }

    /// <summary>Enter: Pixo answers in a line or two, from what it really has; the setting that does it comes with it.</summary>
    async Task AskSettings(string question)
    {
        if (_setRows == null || _setResults == null) return;
        _setAskCts?.Cancel();
        var cts = _setAskCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _setRows.Children.Clear();
        var text = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Text = Loc.T("Thinking…"), Opacity = 0.6 };
        var card = new Border
        {
            Child = text, Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 2, 0, 4), CornerRadius = new CornerRadius(_cyber ? 0 : 10),
            Background = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
            BorderBrush = Frozen(A(_cyber ? CyCyan : ChatAccent, 0x50)), BorderThickness = new Thickness(_cyber ? 0 : 0, 0, 0, 0),
        };
        if (_cyber) { card.BorderThickness = new Thickness(2, 0, 0, 0); }
        _setAnswer = card;
        _setRows.Children.Add(card);
        _setFace?.SetMood(NotchFace.Mood.Thinking);

        string answer;
        try
        {
            PrepareAi();
            if (!OpenRouter.HasKey || OpenRouter.LoadKey() is not { } key)
                answer = Loc.T("Connect the AI first: open the search (Alt+Space), press Tab, and add a free key.");
            else
            {
                var msgs = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = SettingsHelpPrompt() },
                    new JsonObject { ["role"] = "user", ["content"] = question },
                };
                OpenRouter.ChatRequest.Value = false; // its own instructions
                var turn = await OpenRouter.Stream(key, ChatModel, msgs, null, _ => { }, null, cts.Token, 0.2, "low");
                answer = turn.Error is { Length: > 0 } err && turn.Text.Trim().Length == 0 ? Loc.T("Couldn't answer right now. Try again in a moment.") : turn.Text.Trim();
            }
        }
        catch (OperationCanceledException) { if (_setAskCts != cts) return; answer = Loc.T("No answer in time. Try again in a moment."); }
        catch { answer = Loc.T("Couldn't answer right now. Try again in a moment."); }
        if (_setAskCts != cts || _setAnswer != card) return; // another question took over

        // [[setting:Name]]: the switch that does it, right under the answer
        var tags = System.Text.RegularExpressions.Regex.Matches(answer, @"\[\[setting:([^\]]+)\]\]");
        answer = System.Text.RegularExpressions.Regex.Replace(answer, @"\s*\[\[setting:[^\]]+\]\]", "").Replace("**", "").Trim();
        text.Text = answer;
        text.Opacity = 1;
        if (!Eco) text.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        foreach (System.Text.RegularExpressions.Match m in tags)
            if (_settingIndex.FirstOrDefault(e => e.Label.Equals(m.Groups[1].Value.Trim(), StringComparison.OrdinalIgnoreCase)) is { } e)
                _setRows.Children.Add(SettingRow(e));
        _setFace?.SetMood(NotchFace.Mood.Idle);
        _setFace?.Play(NotchFace.Emote.Happy, 1.2);
    }

    /// <summary>What Pixo really has (its settings with their state, keys, gestures), so the answer is never made up.</summary>
    string SettingsHelpPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Pixo's help, inside its Settings page. Pixo is a notch app at the top of the Windows screen. Answer how to do what the user asks, in the user's language.");
        sb.AppendLine("The user is ON the Settings page already: never tell them to open Settings. When a setting does it, its switch is shown right under your answer, so just say what it does and that they can flip it below (e.g. \"Turn on Companion below: a little character drops by on a swing.\").");
        sb.AppendLine("Rules: one or two short sentences, 30 words at most, plain words, no lists, no markdown, no greeting. Use ONLY the facts below. If a setting below does it, name it exactly and end with [[setting:<its exact name>]]. If it isn't in the facts, say it's not something Pixo has, or point to Preferences (the gear on this page) if it may be there. Never invent buttons, keys or settings.");
        sb.AppendLine();
        sb.AppendLine("SETTINGS (name: on/off · tab · what it does):");
        foreach (var e in _settingIndex)
            sb.AppendLine($"- {e.Label}: {(e.Get() ? "on" : "off")} · {SettingsTabNames[Math.Clamp(e.Tab, 0, 2)]}{(e.Hint.Length > 0 ? " · " + e.Hint : "")}");
        sb.AppendLine("- Theme: the theme chip on the Display tab moves to the next theme (Original, Neon, and glass themes).");
        sb.AppendLine("- Break reminder every N minutes: chips on the Smart tab.");
        sb.AppendLine();
        sb.AppendLine("HOW PIXO WORKS:");
        sb.AppendLine("- Open the notch: move the mouse to the top edge over it (if Open at top edge is on), or press Win+Alt+N.");
        sb.AppendLine("- Pages (open notch): Home, Work, Calls, System, Tools, Clipboard & Files, Colors, Settings. Change page by dragging sideways, swiping the touchpad, or the dots at the bottom.");
        sb.AppendLine("- Search: Alt+Space (the key can be changed in Preferences). In the search, Tab opens the AI chat; Ctrl+N starts a new chat.");
        sb.AppendLine("- Files: drop a file on the notch to keep it on the shelf (Clipboard & Files page); drag it out to use it.");
        sb.AppendLine("- The face in the notch: click it to poke it, right-click it for another hat. Its hat can also be chosen in Preferences.");
        sb.AppendLine("- The buttons at the top right of the open notch: desktop icons on/off, the theme, and the lock (a locked notch ignores the mouse).");
        sb.AppendLine("- Preferences (the gear on this page, or the tray icon's menu): language, colours, which pages show, shortcuts, the face's hat, the companion's size, the AI's key and provider.");
        sb.AppendLine("- Quit Pixo: the tray icon's menu, Exit.");
        return sb.ToString();
    }
}
