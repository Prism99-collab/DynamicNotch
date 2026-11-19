using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The chat's finer parts:
///  • steps as a live timeline: a dot per step on a line that links them, the dot pulsing and the words shimmering
///    while it works, then ✓ or ✕, how long it took, and what came of it on a dim line under it;
///  • the approval card: a risk badge (orange for changes, red for what can't be taken back), what will run in a code
///    panel, and three clear choices; a dangerous one breathes red at its edge until you decide;
///  • problems as cards: used-up free requests say when they come back, no connection says so, with a way out;
///  • the input: a glow under the line while you type, send lit only when there's something to send, attachments as
///    chips (each picture its own, each with its ✕), and the chat lights up with "Drop to attach" under a dragged file.
/// </summary>
public partial class NotchWindow
{
    // ───────────────────────────── steps, as a timeline ─────────────────────────────

    sealed class StepView
    {
        public StackPanel? Extras;
        public Action<bool, string?>? OnDone;
        public void AddExtra(UIElement e) => Extras?.Children.Add(e);
        public void Done(bool ok, string? note) => OnDone?.Invoke(ok, note);
    }

    Grid? _lastStepRow;
    Border? _lastStepTail;
    Color _lastStepTone;

    StepView ChatStep(string name, string args, bool animate = true)
    {
        var tone = StepTone(name);
        var icons = (FontFamily)FindResource("IconFont");
        bool linked = _chatStack!.Children.Count > 0 && ReferenceEquals(_chatStack.Children[^1], _lastStepRow) && _lastStepRow!.Visibility == Visibility.Visible;
        bool live = animate && !Eco;

        var row = new Grid { Margin = new Thickness(12, linked ? 0 : 6, 18, 0), Tag = "step", MinHeight = 26 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // the rail: a line from the step before, the dot, a line on to the next
        var rail = new Grid();
        rail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(13) });
        rail.RowDefinitions.Add(new RowDefinition());
        if (linked)
        {
            var top = new Border { Width = 1.5, HorizontalAlignment = HorizontalAlignment.Center, Background = Frozen(new LinearGradientBrush(A(_lastStepTone, 0x90), A(tone, 0x90), 90)) };
            rail.Children.Add(top);
            if (_lastStepTail != null) _lastStepTail.Background = Frozen(new LinearGradientBrush(A(_lastStepTone, 0x90), A(_lastStepTone, 0x90), 90)); // the line before goes on, solid
        }
        var tail = new Border { Width = 1.5, HorizontalAlignment = HorizontalAlignment.Center, Background = Frozen(new LinearGradientBrush(A(tone, 0x70), A(tone, 0x00), 90)) };
        Grid.SetRow(tail, 1);
        rail.Children.Add(tail);
        var halo = new Ellipse { Width = 11, Height = 11, Stroke = Frozen(tone), StrokeThickness = 1, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
        var core = new Ellipse { Width = 9, Height = 9, Fill = Frozen(A(tone, 0x50)), Stroke = Frozen(tone), StrokeThickness = 1.4 };
        var mark = new TextBlock { FontFamily = icons, FontSize = 7, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var dot = new Grid { Width = 13, Height = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6.5, 0, 0) };
        dot.Children.Add(halo);
        dot.Children.Add(core);
        dot.Children.Add(mark);
        Grid.SetRowSpan(dot, 2);
        rail.Children.Add(dot);
        row.Children.Add(rail);

        // what it does
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 4, 6, 4) };
        var first = new StackPanel { Orientation = Orientation.Horizontal };
        first.Children.Add(new TextBlock { Text = ToolGlyph(name), FontFamily = icons, FontSize = 10, Foreground = Frozen(tone), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
        var shimmer = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var baseText = Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF);
        var g1 = new GradientStop(baseText, -0.4); var g2 = new GradientStop(Lighten(tone, 0.55), -0.2); var g3 = new GradientStop(baseText, 0);
        shimmer.GradientStops.Add(new GradientStop(baseText, 0)); shimmer.GradientStops.Add(g1); shimmer.GradientStops.Add(g2); shimmer.GradientStops.Add(g3); shimmer.GradientStops.Add(new GradientStop(baseText, 1));
        var label = new TextBlock { Text = ToolLabel(name, args), FontSize = 11, Foreground = live ? shimmer : Frozen(baseText), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontFamily = _cyber ? CyFont : UiText, ToolTip = ToolLabel(name, args) };
        first.Children.Add(label);
        words.Children.Add(first);
        var note = new TextBlock { FontSize = 10, Foreground = DimBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16, 1, 0, 0), Visibility = Visibility.Collapsed };
        words.Children.Add(note);
        Grid.SetColumn(words, 1);
        row.Children.Add(words);

        var extras = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetColumn(extras, 2);
        row.Children.Add(extras);
        var took = new TextBlock { FontSize = 9.5, Foreground = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 5, 0, 0), FontFamily = _cyber ? CyFont : UiText };
        Grid.SetColumn(took, 3);
        row.Children.Add(took);

        // alive while it works
        if (live)
        {
            var grow = new DoubleAnimation(1, 2.1, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var fade = new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever };
            var sweep = new DoubleAnimation(-0.4, 1.2, TimeSpan.FromMilliseconds(1300)) { RepeatBehavior = RepeatBehavior.Forever };
            var sweep2 = new DoubleAnimation(-0.2, 1.4, TimeSpan.FromMilliseconds(1300)) { RepeatBehavior = RepeatBehavior.Forever };
            var sweep3 = new DoubleAnimation(0, 1.6, TimeSpan.FromMilliseconds(1300)) { RepeatBehavior = RepeatBehavior.Forever };
            foreach (var t in new Timeline[] { grow, fade, sweep, sweep2, sweep3 }) Timeline.SetDesiredFrameRate(t, 30);
            ((ScaleTransform)halo.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            ((ScaleTransform)halo.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            halo.BeginAnimation(OpacityProperty, fade);
            g1.BeginAnimation(GradientStop.OffsetProperty, sweep);
            g2.BeginAnimation(GradientStop.OffsetProperty, sweep2);
            g3.BeginAnimation(GradientStop.OffsetProperty, sweep3);
        }

        var started = DateTime.UtcNow;
        var view = new StepView { Extras = extras };
        view.OnDone = (ok, outcome) =>
        {
            var halo2 = (ScaleTransform)halo.RenderTransform;
            halo2.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            halo2.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            halo.BeginAnimation(OpacityProperty, null);
            halo.Opacity = 0;
            label.Foreground = Frozen(baseText);
            var end = ok ? Ok : Warn;
            core.Fill = Frozen(end);
            core.Stroke = Frozen(Lighten(end, 0.2));
            core.Width = core.Height = 11;
            mark.Text = ok ? "" : "";
            mark.Visibility = Visibility.Visible;
            if (!Eco)
            {
                var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
                dot.RenderTransformOrigin = new Point(0.5, 0.5);
                dot.RenderTransform = new ScaleTransform();
                ((ScaleTransform)dot.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                ((ScaleTransform)dot.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
            double sec = (DateTime.UtcNow - started).TotalSeconds;
            if (animate && sec >= 0.3) took.Text = sec < 10 ? $"{sec:0.0}s" : $"{sec:0}s";
            if (outcome is { Length: > 0 })
            {
                note.Text = outcome;
                note.Foreground = Frozen(ok ? Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF) : Lighten(Warn, 0.2));
                note.Visibility = Visibility.Visible;
            }
        };

        _chatStack.Children.Add(row);
        _lastStepRow = row;
        _lastStepTail = tail;
        _lastStepTone = tone;
        if (animate) Arrive(row);
        FollowChat();
        return view;
    }

    // ───────────────────────────── your OK, as a card ─────────────────────────────

    static readonly Regex HighRisk = new(@"delet|remov|uninstall|format|erase|wipe|registry|hklm|hkcu|\breg\b|runas|admin|kill|stop-|shutdown|restart|-enc|certutil|cmd /c|pay|purchase|buy|transfer|sign out|log ?out|leave|block|ban\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    async Task<int> ConfirmInChat(string why, string code, CancellationToken cancel)
    {
        if (_chatStack == null) return 0;
        bool high = HighRisk.IsMatch(why + " " + code);
        var tone = high ? Color.FromRgb(0xFF, 0x55, 0x5E) : Warn;
        var icons = (FontFamily)FindResource("IconFont");
        var card = new StackPanel();

        // the badge and what it is
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        var badgeLine = new StackPanel { Orientation = Orientation.Horizontal };
        badgeLine.Children.Add(new TextBlock { Text = high ? "" : "", FontFamily = icons, FontSize = 9.5, Foreground = Frozen(tone), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
        badgeLine.Children.Add(new TextBlock { Text = high ? (_cyber ? "HIGH RISK" : "High risk") : (_cyber ? "NEEDS YOUR OK" : "Needs your OK"), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(tone), FontFamily = _cyber ? CyFont : UiText, VerticalAlignment = VerticalAlignment.Center });
        var badge = new Border { Child = badgeLine, Padding = new Thickness(7, 2, 8, 3), CornerRadius = new CornerRadius(_cyber ? 0 : 8), Background = Frozen(A(tone, 0x24)), BorderBrush = Frozen(A(tone, 0x80)), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(badge);
        card.Children.Add(top);
        card.Children.Add(new TextBlock { Text = why, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0), FontFamily = _cyber ? CyFont : UiText });

        // what will run
        bool shell = Regex.IsMatch(code, @"\b[A-Z][a-z]+-[A-Z][A-Za-z]+\b|\|\s*\w|\$\w");
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = shell ? "PowerShell" : "Details", FontSize = 9, Foreground = Frozen(A(tone, 0xC0)), FontFamily = _cyber ? CyFont : UiText, Margin = new Thickness(0, 0, 0, 3) });
        panel.Children.Add(new TextBox
        {
            Text = code.Length > 1500 ? code[..1500] + "\n…" : code, IsReadOnly = true, FontFamily = ChatMono, FontSize = 10.5, Foreground = Frozen(CodeTone), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0),
        });
        card.Children.Add(new Border
        {
            Child = panel, Margin = new Thickness(0, 7, 0, 9), Padding = new Thickness(9, 5, 9, 7), CornerRadius = new CornerRadius(_cyber ? 0 : 7),
            Background = Frozen(Color.FromArgb(0x60, 0x00, 0x00, 0x00)), BorderBrush = Frozen(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1, 1, 1, 1),
        });

        // the choices
        Border Choice(string glyph, string text, string? key, bool primary)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = glyph, FontFamily = icons, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0), Foreground = primary ? Brushes.White : Frozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)) });
            line.Children.Add(new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = primary ? Brushes.White : Frozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, FontFamily = _cyber ? CyFont : UiText });
            if (key != null) line.Children.Add(new TextBlock { Text = key, FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0), Foreground = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)) });
            Brush rest = primary ? Frozen(new LinearGradientBrush(A(tone, 0xB0), A(tone, 0x70), 90)) : Frozen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            Brush hover = primary ? Frozen(new LinearGradientBrush(A(tone, 0xE0), A(tone, 0x90), 90)) : Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            var b = new Border
            {
                Child = line, Padding = new Thickness(10, 4, 11, 5), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(_cyber ? 0 : 8),
                Background = rest, BorderBrush = Frozen(primary ? A(tone, 0xE0) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            };
            b.MouseEnter += (_, _) => b.Background = hover;
            b.MouseLeave += (_, _) => b.Background = rest;
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;
            return b;
        }
        var buttons = new WrapPanel();
        var run = Choice("", "Allow once", "↵", true);
        var always = Choice("", "Allow for this chat", null, false);
        var no = Choice("", "Don't", "Esc", false);
        buttons.Children.Add(run);
        buttons.Children.Add(always);
        buttons.Children.Add(no);
        card.Children.Add(buttons);

        var border = new Border
        {
            Tag = "step", Child = card, Padding = new Thickness(12, 10, 12, 11), Margin = new Thickness(12, 8, 18, 4), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(_cyber ? 0 : 12),
            Background = Frozen(new LinearGradientBrush(A(tone, 0x1C), A(tone, 0x06), 90)), BorderBrush = Frozen(A(tone, 0x90)),
        };
        // a dangerous one breathes red at its edge until you decide
        var edge = new Border { BorderThickness = new Thickness(1.5), CornerRadius = border.CornerRadius, BorderBrush = Frozen(tone), IsHitTestVisible = false, Opacity = 0, Margin = border.Margin };
        var host = new Grid { Tag = "step" };
        border.Tag = null;
        host.Children.Add(border);
        if (high && !Eco)
        {
            host.Children.Add(edge);
            var breathe = new DoubleAnimation(0.15, 0.85, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Timeline.SetDesiredFrameRate(breathe, 30);
            edge.BeginAnimation(OpacityProperty, breathe);
        }
        _chatStack.Children.Add(host);
        Arrive(host);
        FollowChat(force: true);
        // the notch says so if you're not looking
        if (!(_launcherOpen && _chatMode) && _pop is not { IsActive: true })
            ShowOsd("ai", "", null, "AI needs your OK", why, null, glyphBrush: Frozen(tone), seconds: 6, force: true, prio: Prio.Normal, onClick: ReopenChat);
        var tcs = new TaskCompletionSource<int>();
        _confirm = new TaskCompletionSource<bool>();
        _ = _confirm.Task.ContinueWith(t => tcs.TrySetResult(t.Result ? 1 : 0), TaskScheduler.FromCurrentSynchronizationContext());
        run.MouseLeftButtonUp += (_, e) => { e.Handled = true; tcs.TrySetResult(1); };
        always.MouseLeftButtonUp += (_, e) => { e.Handled = true; tcs.TrySetResult(2); };
        no.MouseLeftButtonUp += (_, e) => { e.Handled = true; tcs.TrySetResult(0); };
        using var reg = cancel.Register(() => tcs.TrySetResult(0));
        ChatKeys();
        int answer = await tcs.Task;
        _confirm = null;
        edge.BeginAnimation(OpacityProperty, null);
        host.Children.Remove(edge);

        // decided: one quiet line instead of the card
        var done = answer > 0 ? Ok : Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF);
        var summary = new StackPanel { Orientation = Orientation.Horizontal };
        summary.Children.Add(new TextBlock { Text = answer > 0 ? "" : "", FontFamily = icons, FontSize = 9.5, Foreground = Frozen(done), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) });
        summary.Children.Add(new TextBlock
        {
            Text = why + (answer == 2 ? "  ·  allowed for this chat" : answer == 1 ? "  ·  allowed" : "  ·  not run"), FontSize = 11, Foreground = Frozen(answer > 0 ? Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = code, VerticalAlignment = VerticalAlignment.Center, FontFamily = _cyber ? CyFont : UiText,
        });
        border.Child = summary;
        border.Padding = new Thickness(10, 4, 10, 5);
        border.Background = Frozen(A(answer > 0 ? Ok : tone, 0x0C));
        border.BorderBrush = Frozen(A(answer > 0 ? Ok : tone, 0x40));
        ChatKeys();
        _launchBox?.Focus();
        return answer;
    }

    // ───────────────────────────── problems, as cards ─────────────────────────────

    /// <summary>Why it couldn't answer, what to expect, and a way out.</summary>
    void ChatProblem(string error, (string Label, Action Act)? action)
    {
        _lastProblem = error; // the notch says it if you're elsewhere (AnswerReady)
        if (_chatStack == null) return;
        var icons = (FontFamily)FindResource("IconFont");
        string glyph, title;
        string? when = null;
        Color tone = Warn;
        if (error.StartsWith("Today's") || Regex.IsMatch(error, @"used up|daily|limit|quota", RegexOptions.IgnoreCase))
        {
            glyph = ""; title = "Free requests used up for now";
            var back = OpenRouter.IsGemini ? OpenRouter.GeminiReset : DateTime.UtcNow.Date.AddDays(1).ToLocalTime();
            when = back.Date == DateTime.Today ? $"Back at {back:HH:mm}" : $"Back tomorrow at {back:HH:mm}";
        }
        else if (Regex.IsMatch(error, @"internet|connect|network|offline|resolve|unreachable|No answer|timed out", RegexOptions.IgnoreCase))
        { glyph = ""; title = "Can't reach the AI"; tone = Color.FromRgb(0x8C, 0xA8, 0xFF); }
        else if (Regex.IsMatch(error, @"busy|too many|overloaded|try again", RegexOptions.IgnoreCase))
        { glyph = ""; title = "The AI is busy"; }
        else { glyph = ""; title = "Something went wrong"; tone = Color.FromRgb(0xFF, 0x6B, 0x6B); }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(_cyber ? 0 : 9), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0),
            Background = Frozen(new LinearGradientBrush(A(tone, 0x40), A(tone, 0x12), 45)), BorderBrush = Frozen(A(tone, 0x80)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = glyph, FontFamily = icons, FontSize = 13, Foreground = Frozen(tone), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = _cyber ? title.ToUpperInvariant() : title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontFamily = _cyber ? CyFont : UiText });
        words.Children.Add(new TextBlock { Text = error, FontSize = 10.5, Foreground = DimBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var bottom = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (when != null)
        {
            var w = new StackPanel { Orientation = Orientation.Horizontal };
            w.Children.Add(new TextBlock { Text = "", FontFamily = icons, FontSize = 9, Foreground = Frozen(tone), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
            w.Children.Add(new TextBlock { Text = when, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Frozen(tone), VerticalAlignment = VerticalAlignment.Center });
            bottom.Children.Add(new Border { Child = w, Padding = new Thickness(7, 2, 8, 3), Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(_cyber ? 0 : 8), Background = Frozen(A(tone, 0x1E)), BorderBrush = Frozen(A(tone, 0x60)), BorderThickness = new Thickness(1) });
        }
        if (action is { } a)
        {
            var b = ChatChip(a.Label, "");
            b.Click += (_, _) => { a.Act(); _launchBox?.Focus(); };
            bottom.Children.Add(b);
        }
        if (bottom.Children.Count > 0) words.Children.Add(bottom);
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        var card = new Border
        {
            Child = grid, Padding = new Thickness(11, 9, 12, 10), Margin = new Thickness(0, 10, 18, 2), CornerRadius = new CornerRadius(_cyber ? 0 : 12), BorderThickness = new Thickness(1),
            Background = Frozen(new LinearGradientBrush(A(tone, 0x18), A(tone, 0x05), 90)), BorderBrush = Frozen(A(tone, 0x55)),
        };
        _chatStack.Children.Add(card);
        Arrive(card);
        FollowChat(force: true);
    }

    // ───────────────────────────── the input ─────────────────────────────

    Border? _typeGlow;

    /// <summary>A soft glow under the line while there's something typed in the chat.</summary>
    void ChatTypeGlow(bool on)
    {
        if (_launchLineRow is not Grid) return;
        // the theme changed since it was made: made again in the new colours
        if (_typeGlow != null && !(_typeGlow.Tag is Color made && made == ChatAccent))
        {
            ((Grid)_launchLineRow).Children.Remove(_typeGlow);
            _typeGlow = null;
            _typeGlowOn = false;
        }
        if (_typeGlow == null)
        {
            if (!on) return;
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            brush.GradientStops.Add(new GradientStop(ChatAccent, 0.35));
            brush.GradientStops.Add(new GradientStop(AiSecond, 0.7));
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            _typeGlow = new Border { Height = 1.5, VerticalAlignment = VerticalAlignment.Bottom, Background = brush, Opacity = 0, IsHitTestVisible = false, Tag = ChatAccent };
            if (!_cyber) _typeGlow.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = ChatAccent, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.8 };
            Grid.SetColumnSpan(_typeGlow, 4);
            Grid.SetRowSpan(_typeGlow, 2);
            ((Grid)_launchLineRow).Children.Add(_typeGlow);
        }
        if (on == _typeGlowOn) return;
        _typeGlowOn = on;
        double want = on ? 1 : 0;
        if (Eco) { _typeGlow.BeginAnimation(OpacityProperty, null); _typeGlow.Opacity = want; return; }
        _typeGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(want, TimeSpan.FromMilliseconds(on ? 200 : 260)));
    }

    bool _typeGlowOn;

    /// <summary>What goes with the next message: a chip for each picture and for the file, each with its own ✕.</summary>
    void ShowAttachments()
    {
        ClearAttachNote();
        if (_chatStack == null || _chatAttach == null && _chatPictures.Count == 0) return;
        var acc = ChatAccent;
        var icons = (FontFamily)FindResource("IconFont");
        var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Border X(Action remove)
        {
            var x = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, ToolTip = "Remove", Background = Frozen(Color.FromArgb(0xD0, 0x10, 0x10, 0x14)),
                BorderBrush = Frozen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -5, -5, 0),
                Child = new TextBlock { Text = "", FontFamily = icons, FontSize = 7, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            x.MouseEnter += (_, _) => x.Background = Frozen(Color.FromRgb(0xE0, 0x40, 0x48));
            x.MouseLeave += (_, _) => x.Background = Frozen(Color.FromArgb(0xD0, 0x10, 0x10, 0x14));
            x.MouseLeftButtonDown += (_, e) => e.Handled = true;
            x.MouseLeftButtonUp += (_, e) => { e.Handled = true; remove(); ShowAttachments(); _launchBox?.Focus(); };
            return x;
        }
        foreach (var jpeg in _chatPictures.ToList())
        {
            var cell = new Grid { Margin = new Thickness(6, 6, 0, 0) };
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.DecodePixelHeight = 96;
            img.StreamSource = new MemoryStream(jpeg);
            img.EndInit();
            img.Freeze();
            cell.Children.Add(new Border
            {
                Width = 48, Height = 48, CornerRadius = new CornerRadius(_cyber ? 0 : 9), BorderBrush = Frozen(A(acc, 0x90)), BorderThickness = new Thickness(1),
                Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill },
            });
            var picture = jpeg;
            cell.Children.Add(X(() => _chatPictures.Remove(picture)));
            wrap.Children.Add(cell);
        }
        if (_chatAttach is { } f)
        {
            var cell = new Grid { Margin = new Thickness(6, 6, 0, 0) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(_cyber ? 0 : 7), Margin = new Thickness(0, 0, 8, 0), Background = Frozen(A(StepTone("find_files"), 0x30)),
                Child = new TextBlock { Text = "", FontFamily = icons, FontSize = 12, Foreground = Frozen(StepTone("find_files")), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = f.Name, FontSize = 11, Foreground = Brushes.White, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis });
            words.Children.Add(new TextBlock { Text = $"{f.Text.Length / 1000.0:0.#}k characters", FontSize = 9.5, Foreground = DimBrush });
            Grid.SetColumn(words, 1);
            g.Children.Add(words);
            cell.Children.Add(new Border { Child = g, Height = 48, Padding = new Thickness(8, 0, 12, 0), CornerRadius = new CornerRadius(_cyber ? 0 : 9), Background = Frozen(A(acc, 0x16)), BorderBrush = Frozen(A(acc, 0x70)), BorderThickness = new Thickness(1) });
            cell.Children.Add(X(() => _chatAttach = null));
            wrap.Children.Add(cell);
        }
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        stack.Children.Add(wrap);
        stack.Children.Add(new TextBlock { Text = "Goes with your next message", FontSize = 9.5, Foreground = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) });
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(stack);
        _attachNote = row;
        _chatStack.Children.Add(row);
        Arrive(row, fromRight: true);
        FollowChat(force: true);
    }

    // ───────────────────────────── a file dragged over the chat ─────────────────────────────

    Border? _dropVeil;

    /// <summary>The veil that says "Drop to attach", in the theme's colours of the moment.</summary>
    void BuildDropVeil(Grid host)
    {
        if (_dropVeil != null && _dropVeil.Tag is Color made && made == ChatAccent) return;
        if (_dropVeil != null) host.Children.Remove(_dropVeil);
        var acc = ChatAccent;
        var icons = (FontFamily)FindResource("IconFont");
        var words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = "", FontFamily = icons, FontSize = 26, Foreground = Frozen(acc), HorizontalAlignment = HorizontalAlignment.Center });
        words.Children.Add(new TextBlock { Text = _cyber ? "DROP TO ATTACH" : "Drop to attach", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontFamily = _cyber ? CyFont : UiText });
        words.Children.Add(new TextBlock { Text = "Pictures, documents or text, with your next message", FontSize = 10.5, Foreground = DimBrush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        var dash = new Rectangle { Stroke = Frozen(A(acc, 0xC0)), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 5, 4 }, RadiusX = _cyber ? 0 : 12, RadiusY = _cyber ? 0 : 12, IsHitTestVisible = false };
        var inner = new Grid();
        inner.Children.Add(dash);
        inner.Children.Add(words);
        _dropVeil = new Border
        {
            Child = inner, Margin = new Thickness(4), Visibility = Visibility.Collapsed, Opacity = 0, IsHitTestVisible = false, CornerRadius = new CornerRadius(_cyber ? 0 : 12),
            Background = Frozen(new RadialGradientBrush(A(acc, 0x40), Color.FromArgb(0xD8, 0x06, 0x08, 0x0C))), Tag = acc,
        };
        Panel.SetZIndex(_dropVeil, 20);
        host.Children.Add(_dropVeil);
    }

    void WireChatDrop(Grid host)
    {
        bool Wanted(DragEventArgs e) => _chatMode && _chatView == ChatView.Chat && !_chatAwaitingKey &&
            (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap) || e.Data.GetDataPresent(DataFormats.UnicodeText));
        void Veil(bool on)
        {
            if (on) BuildDropVeil(host);
            if (_dropVeil == null) return;
            if (on && _dropVeil.Visibility == Visibility.Visible) return;
            if (on) _dropVeil.Visibility = Visibility.Visible;
            var a = new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 140 : 160));
            if (!on) a.Completed += (_, _) => { if (_dropVeil.Opacity < 0.05) _dropVeil.Visibility = Visibility.Collapsed; };
            _dropVeil.BeginAnimation(OpacityProperty, a);
        }
        host.AllowDrop = true;
        host.DragEnter += (_, e) => { if (!Wanted(e)) return; e.Effects = DragDropEffects.Copy; e.Handled = true; Veil(true); };
        host.DragOver += (_, e) => { if (!Wanted(e)) return; e.Effects = DragDropEffects.Copy; e.Handled = true; Veil(true); };
        host.DragLeave += (_, e) =>
        {
            if (!Wanted(e)) return;
            e.Handled = true;
            var p = e.GetPosition(host);
            if (p.X <= 1 || p.Y <= 1 || p.X >= host.ActualWidth - 1 || p.Y >= host.ActualHeight - 1) Veil(false);
        };
        host.Drop += async (_, e) =>
        {
            if (!Wanted(e)) return;
            e.Handled = true;
            Veil(false);
            try
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                    foreach (var path in files.Take(5))
                    {
                        if (PictureExts.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant())) { AddPictureFile(path); continue; }
                        string text = await Task.Run(() => ReadAnyText(path));
                        if (text.StartsWith("Error") || text.StartsWith("This is not a text file")) { Toast("Can't read " + System.IO.Path.GetFileName(path) + " as text"); continue; }
                        if (text.Length > 24000) text = text[..24000] + "\n…(cut)";
                        _chatAttach = (System.IO.Path.GetFileName(path), text);
                        ShowAttachments();
                    }
                else if (e.Data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bmp) AddPicture(bmp, "Picture attached");
                else if (e.Data.GetData(DataFormats.UnicodeText) is string s && _launchBox != null)
                {
                    _launchBox.Text = (_launchBox.Text.Length > 0 ? _launchBox.Text.TrimEnd() + " " : "") + s.Trim();
                    _launchBox.CaretIndex = _launchBox.Text.Length;
                }
            }
            catch { Toast("Couldn't attach that"); }
            _launchBox?.Focus();
        };
    }
}
