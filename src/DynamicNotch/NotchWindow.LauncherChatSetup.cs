using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The first thing a new user sees: connecting the AI, once. The AI's mark and what it is, three numbered steps on a
/// line that fills as you go (get a free key → copy it → paste it here), one big button for the first step, a key box
/// with a Paste button, and a ✓ that draws itself when the key works.
/// </summary>
public partial class NotchWindow
{
    void ChatSetupCard(string? problem = null)
    {
        if (_chatStack == null) return;
        _chatAwaitingKey = true;
        var acc = ChatAccent;
        var icons = (FontFamily)FindResource("IconFont");
        var card = new StackPanel();

        // who it is
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        var orb = new Controls.AiOrb(34, acc, AiSecond, _cyber) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        head.Children.Add(orb);
        orb.Loaded += (_, _) => { if (!Eco) orb.Start(); };
        orb.Unloaded += (_, _) => orb.Stop();
        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(new TextBlock { Text = _cyber ? "LINK THE AI // ONE TIME" : "Connect your AI", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = _cyber ? Frozen(CyYellow) : Brushes.White, FontFamily = _cyber ? CyFont : UiText });
        who.Children.Add(new TextBlock { Text = "Free with a Google key: thousands of answers a day. Takes a minute.", FontSize = 11, Foreground = DimBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(who, 1);
        head.Children.Add(who);
        card.Children.Add(head);

        // the three steps, on a line that fills as you go
        var steps = new Grid { Margin = new Thickness(0, 14, 0, 4) };
        for (int i = 0; i < 3; i++) steps.ColumnDefinitions.Add(new ColumnDefinition());
        var track = new Border { Height = 2, Margin = new Thickness(0, 10, 0, 0), VerticalAlignment = VerticalAlignment.Top, Background = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(1) };
        var fill = new Border { Height = 2, Margin = new Thickness(0, 10, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(1), Background = Frozen(new LinearGradientBrush(acc, AiSecond, 0)) };
        var trackHost = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        Grid.SetColumnSpan(trackHost, 3);
        trackHost.Children.Add(track);
        trackHost.Children.Add(fill);
        steps.Children.Add(trackHost);
        var circles = new List<(Border Circle, TextBlock Number, TextBlock Label)>();
        string[] names = { "Get a free key", "Copy it", "Paste it here" };
        for (int i = 0; i < 3; i++)
        {
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var number = new TextBlock { Text = (i + 1).ToString(), FontSize = 10.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = _cyber ? CyFont : UiText };
            var circle = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(_cyber ? 0 : 11), BorderThickness = new Thickness(1.5), Child = number, HorizontalAlignment = HorizontalAlignment.Center };
            var label = new TextBlock { Text = names[i], FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0), FontFamily = _cyber ? CyFont : UiText };
            col.Children.Add(circle);
            col.Children.Add(label);
            Grid.SetColumn(col, i);
            steps.Children.Add(col);
            circles.Add((circle, number, label));
        }
        card.Children.Add(steps);
        int stage = -1;
        void Stage(int s)
        {
            if (s == stage) return;
            stage = s;
            for (int i = 0; i < 3; i++)
            {
                var (circle, number, label) = circles[i];
                bool done = i < s, now = i == s;
                circle.Background = done ? Frozen(new LinearGradientBrush(acc, AiSecond, 45)) : now ? Frozen(A(acc, 0x30)) : Frozen(Color.FromArgb(0xF0, 0x14, 0x16, 0x1C));
                circle.BorderBrush = Frozen(done || now ? acc : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                number.Text = done ? "" : (i + 1).ToString();
                number.FontFamily = done ? icons : _cyber ? CyFont : UiText;
                number.FontSize = done ? 9 : 10.5;
                number.Foreground = done ? Brushes.White : now ? Frozen(acc) : DimBrush;
                label.Foreground = done || now ? Brushes.White : DimBrush;
                label.FontWeight = now ? FontWeights.SemiBold : FontWeights.Normal;
            }
            // the line fills to the step you're on
            // the line runs from the first circle's centre to the last one's
            double width = steps.ActualWidth > 0 ? steps.ActualWidth : 280, edge = width / 6;
            track.Margin = new Thickness(edge, 10, edge, 0);
            fill.Margin = new Thickness(edge, 10, 0, 0);
            double to = width * 2 / 3 * Math.Clamp(s / 2.0, 0, 1);
            if (Eco) fill.Width = to;
            else fill.BeginAnimation(WidthProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        steps.Loaded += (_, _) => { int s = stage; stage = -1; Stage(Math.Max(0, s)); };
        steps.SizeChanged += (_, _) => { int s = stage; stage = -1; Stage(Math.Max(0, s)); };

        // step 1: one big button
        var goLine = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        goLine.Children.Add(new TextBlock { Text = "", FontFamily = icons, FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) });
        goLine.Children.Add(new TextBlock { Text = _cyber ? "GET MY FREE KEY" : "Get my free key", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontFamily = _cyber ? CyFont : UiText });
        Brush goRest = Frozen(new LinearGradientBrush(A(acc, 0xD0), A(AiSecond, 0xB0), 0)), goHover = Frozen(new LinearGradientBrush(acc, AiSecond, 0));
        var go = new Border
        {
            Child = goLine, Height = 32, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(_cyber ? 0 : 10), Background = goRest,
            BorderBrush = Frozen(Lighten(acc, 0.3)), BorderThickness = new Thickness(1), ToolTip = "Opens Google AI Studio: sign in, then “Create API key”",
        };
        if (!_cyber && !Eco) go.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = acc, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.45 };
        go.MouseEnter += (_, _) => go.Background = goHover;
        go.MouseLeave += (_, _) => go.Background = goRest;
        go.MouseLeftButtonDown += (_, e) => e.Handled = true;
        go.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try { Process.Start(new ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true }); } catch { }
            Stage(1);
        };
        card.Children.Add(go);
        card.Children.Add(new TextBlock { Text = "Sign in with Google, click “Create API key”, copy it.", FontSize = 10, Foreground = DimBrush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });

        // step 3: the key box
        var field = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        field.ColumnDefinitions.Add(new ColumnDefinition());
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _keyBox = new PasswordBox
        {
            Height = 30, FontSize = 12, Padding = new Thickness(28, 3, 6, 3), Foreground = Brushes.White, CaretBrush = Frozen(acc),
            Background = Frozen(Color.FromArgb(0x50, 0x00, 0x00, 0x00)), BorderBrush = Frozen(A(acc, 0x70)), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Your Google AI Studio key (AQ.… or AIza…), or an OpenRouter key (sk-or-v1-…)",
        };
        var lockIcon = new TextBlock { Text = "", FontFamily = icons, FontSize = 11, Foreground = Frozen(A(acc, 0xC0)), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var placeholder = new TextBlock { Text = "Paste your key: AQ.… or AIza…", FontSize = 12, Foreground = DimBrush, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(30, 0, 0, 0) };
        _keyBox.PasswordChanged += (_, _) =>
        {
            placeholder.Visibility = _keyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_keyBox.Password.Length > 0) Stage(2);
        };
        _keyBox.GotKeyboardFocus += (_, _) => _keyBox.BorderBrush = Frozen(acc);
        _keyBox.LostKeyboardFocus += (_, _) => { if (_keyBox != null) _keyBox.BorderBrush = Frozen(A(acc, 0x70)); };
        var status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        var paste = ChatChip("Paste", "");
        paste.Margin = new Thickness(6, 0, 0, 0);
        paste.Height = 30;
        paste.Click += (_, _) =>
        {
            try { if (Clipboard.ContainsText() && _keyBox != null) { _keyBox.Password = Clipboard.GetText().Trim(); _keyBox.Focus(); } } catch { }
        };
        var save = ChatChip("Connect", "");
        save.Margin = new Thickness(6, 0, 0, 0);
        save.Height = 30;

        async void Submit()
        {
            string raw = _keyBox!.Password.Trim();
            string? key = KeyIn(raw);
            status.Visibility = Visibility.Visible;
            if (key == null)
            {
                status.Foreground = Frozen(Warn);
                status.Text = raw.Contains("...") || raw.Contains('…')
                    ? "That's a shortened key (from a list of keys). Copy the whole key: in Google AI Studio, click it to see it in full."
                    : "That isn't a key: a Google key starts with AQ. or AIza (an OpenRouter key starts with sk-or-v1-).";
                Bump(1);
                return;
            }
            status.Foreground = DimBrush;
            string provider = OpenRouter.KindOf(key) == "openrouter" ? "OpenRouter" : "Google";
            status.Text = $"Checking with {provider}…";
            _keyBox.IsEnabled = false;
            bool? ok = await OpenRouter.CheckKey(key);
            if (_keyBox == null) return; // the card went meanwhile
            _keyBox.IsEnabled = true;
            if (ok == false)
            {
                status.Foreground = Frozen(Warn);
                status.Text = $"{provider} says this key isn't valid (deleted, or not copied whole). Create a new one and paste it here.";
                _keyBox.Focus();
                Bump(1);
                return;
            }
            if (!OpenRouter.SaveKey(key)) { status.Foreground = Frozen(Warn); status.Text = "Couldn't save the key on this PC."; return; }
            _settings.AiProvider = OpenRouter.KindOf(key) ?? "gemini";
            _settings.Save();
            OpenRouter.Choose(_settings.AiProvider);
            if (OpenRouter.IsGemini) await OpenRouter.GeminiModels();
            _chatAwaitingKey = false;
            _keyBox = null;
            // the ✓, drawn, before the chat takes its place
            await Connected(card, ok == true ? $"Connected · {ModelName(ChatModel)}" : $"Saved ({provider} couldn't be reached to check it now)");
            RenderSession();
            _launchBox?.Focus();
            if (_chatPending is { } q) { _chatPending = null; SendChat(q); }
        }
        save.Click += (_, _) => Submit();
        _keyBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Submit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; LeaveChat(); }
        };
        var boxHost = new Grid();
        boxHost.Children.Add(_keyBox);
        boxHost.Children.Add(lockIcon);
        boxHost.Children.Add(placeholder);
        field.Children.Add(boxHost);
        Grid.SetColumn(paste, 1);
        field.Children.Add(paste);
        Grid.SetColumn(save, 2);
        field.Children.Add(save);
        card.Children.Add(field);
        card.Children.Add(status);
        if (problem != null) { status.Text = problem; status.Foreground = Frozen(Warn); status.Visibility = Visibility.Visible; }
        var privacy = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        privacy.Children.Add(new TextBlock { Text = "", FontFamily = icons, FontSize = 9, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0) });
        privacy.Children.Add(new TextBlock { Text = "Kept on this PC, encrypted for your Windows account, and only ever sent to the AI's own provider. An OpenRouter key works too.", FontSize = 9.5, Foreground = DimBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 });
        card.Children.Add(privacy);

        var border = new Border
        {
            Child = card, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 6, 8, 4), CornerRadius = new CornerRadius(_cyber ? 0 : 14),
            Background = Frozen(new LinearGradientBrush(A(acc, 0x1C), Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 90)), BorderBrush = Frozen(new LinearGradientBrush(A(acc, 0x80), A(AiSecond, 0x30), 45)), BorderThickness = new Thickness(1),
        };
        _chatStack.Children.Add(border);
        Arrive(border);
        Stage(0);
        StyleChatLine();
        ChatKeys();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { _keyBox?.Focus(); Keyboard.Focus(_keyBox); });
    }

    /// <summary>The key works: the card gives way to a ✓ that draws itself, and a word on what's connected.</summary>
    async Task Connected(StackPanel card, string text)
    {
        var acc = ChatAccent;
        card.Children.Clear();
        var mark = new Grid { Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var ring = new Ellipse { Stroke = Frozen(Ok), StrokeThickness = 2, Fill = Frozen(A(Ok, 0x20)), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(Eco ? 1 : 0.4, Eco ? 1 : 0.4) };
        var tick = new Polyline
        {
            Points = new PointCollection { new(17, 29), new(25, 37), new(40, 21) }, Stroke = Frozen(Ok), StrokeThickness = 3.2,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            StrokeDashArray = new DoubleCollection { 20, 20 }, StrokeDashOffset = Eco ? 0 : 20,
        };
        mark.Children.Add(ring);
        mark.Children.Add(tick);
        card.Children.Add(mark);
        card.Children.Add(new TextBlock { Text = _cyber ? "LINKED" : "You're all set", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = _cyber ? Frozen(CyYellow) : Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), FontFamily = _cyber ? CyFont : UiText });
        card.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = Frozen(Ok), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 6) });
        if (Eco) { await Task.Delay(700); return; }
        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } };
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        tick.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(360)) { BeginTime = TimeSpan.FromMilliseconds(220), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        await Task.Delay(1500);
    }
}
