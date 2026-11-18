using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// What the AI finds, shown as what it is rather than only described: web results and news as rows with the site's
/// icon (and the outlet and time for news), files and folders with their own icons; each opens on a click (a file
/// shows in its folder with Ctrl). The sites an answer drew on follow it as small "Sources" chips.
/// </summary>
public partial class NotchWindow
{
    sealed record ResultCard(string Kind, string Title, string Sub, string Target);

    readonly List<ResultCard> _pendingCards = new();   // what the tool that just ran found
    readonly List<ResultCard> _turnSources = new();    // the web pages this answer drew on
    static readonly Dictionary<string, BitmapImage> Favicons = new();

    void Found(IEnumerable<ResultCard> cards)
    {
        foreach (var c in cards)
        {
            _pendingCards.Add(c);
            if (c.Kind is "web" or "news" && !_turnSources.Any(s => s.Target == c.Target)) _turnSources.Add(c);
        }
    }

    static IEnumerable<ResultCard> WebCards(IEnumerable<WebTools.Hit> hits, string kind = "web") =>
        hits.Select(h => new ResultCard(kind, h.Title, kind == "news" ? h.Snippet : WebTools.Domain(h.Url), h.Url));

    /// <summary>A step's findings, under its line: four at most, the rest one click away.</summary>
    void ShowCards(List<ResultCard> cards)
    {
        if (_chatStack == null || cards.Count == 0) return;
        var acc = ChatAccent;
        var list = new StackPanel { Margin = new Thickness(30, 2, 18, 2), Tag = "step" };
        int shown = Math.Min(4, cards.Count);
        for (int i = 0; i < cards.Count; i++)
        {
            var row = CardRow(cards[i]);
            if (i >= shown) row.Visibility = Visibility.Collapsed;
            list.Children.Add(row);
        }
        if (cards.Count > shown)
        {
            var more = new TextBlock { Text = $"+{cards.Count - shown} more", FontSize = 10.5, Foreground = Frozen(A(acc, 0xC0)), Cursor = Cursors.Hand, Margin = new Thickness(26, 2, 0, 0) };
            more.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                foreach (UIElement r in list.Children) r.Visibility = Visibility.Visible;
                more.Visibility = Visibility.Collapsed;
            };
            list.Children.Add(more);
        }
        _chatStack.Children.Add(list);
        Arrive(list);
        FollowChat();
    }

    FrameworkElement CardRow(ResultCard c)
    {
        var acc = ChatAccent;
        var row = new Grid { Height = 30, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = c.Target + (c.Kind is "file" ? "  ·  Ctrl+click: show in its folder" : "") };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var plate = new Border { CornerRadius = new CornerRadius(_cyber ? 0 : 6) };
        Grid.SetColumnSpan(plate, 2);
        row.Children.Add(plate);
        var icon = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(_cyber ? 0 : 3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        if (c.Kind is "file" or "folder") IconInto(icon, c.Target);
        else icon.Child = Favicon(c.Target, acc);
        row.Children.Add(icon);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
        words.Children.Add(new TextBlock { Text = c.Title, FontSize = 11.5, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        if (c.Sub.Length > 0) words.Children.Add(new TextBlock { Text = c.Sub, FontSize = 9.5, Foreground = DimBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        row.MouseEnter += (_, _) => plate.Background = Frozen(A(acc, 0x1E));
        row.MouseLeave += (_, _) => plate.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenLink(c.Target); };
        return row;
    }

    /// <summary>A site's little icon (fetched once), or a globe while it comes.</summary>
    FrameworkElement Favicon(string url, Color acc)
    {
        var globe = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = Frozen(A(acc, 0xC0)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        string domain = WebTools.Domain(url);
        if (domain.Length == 0 || domain.Contains("news.google.com")) return globe;
        try
        {
            if (!Favicons.TryGetValue(domain, out var img))
            {
                img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri($"https://icons.duckduckgo.com/ip3/{domain}.ico");
                img.DecodePixelWidth = 32;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                Favicons[domain] = img;
            }
            var host = new Grid();
            host.Children.Add(globe);
            var pic = new Image { Source = img, Width = 16, Height = 16 };
            RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.HighQuality);
            if (img.IsDownloading) { pic.Opacity = 0; img.DownloadCompleted += (_, _) => { pic.Opacity = 1; globe.Visibility = Visibility.Collapsed; }; }
            else globe.Visibility = Visibility.Collapsed;
            host.Children.Add(pic);
            return host;
        }
        catch { return globe; }
    }

    /// <summary>Under an answer: the sites it drew on, as small chips.</summary>
    void ShowSources(ChatBubble bubble)
    {
        if (_turnSources.Count == 0) return;
        var acc = ChatAccent;
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(new TextBlock { Text = _cyber ? "SOURCES" : "Sources", FontSize = 9.5, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4), FontFamily = _cyber ? CyFont : UiText });
        foreach (var s in _turnSources.GroupBy(s => s.Kind == "news" ? s.Sub.Split(" · ")[0] : WebTools.Domain(s.Target)).Take(5).Select(g => g.First()))
        {
            string name = s.Kind == "news" ? s.Sub.Split(" · ")[0] : WebTools.Domain(s.Target);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = new Border { Width = 12, Height = 12, Margin = new Thickness(0, 0, 5, 0), Child = Favicon(s.Target, acc), VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(ic);
            line.Children.Add(new TextBlock { Text = name.Length > 26 ? name[..25] + "…" : name, FontSize = 10, Foreground = Frozen(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center });
            var chip = new Border
            {
                Child = line, Padding = new Thickness(6, 2, 8, 3), Margin = new Thickness(0, 0, 5, 4), CornerRadius = new CornerRadius(_cyber ? 0 : 9), Cursor = Cursors.Hand, ToolTip = s.Title + "\n" + s.Target,
                Background = Frozen(A(acc, 0x16)), BorderBrush = Frozen(A(acc, 0x45)), BorderThickness = new Thickness(1),
            };
            string target = s.Target;
            chip.MouseEnter += (_, _) => chip.Background = Frozen(A(acc, 0x30));
            chip.MouseLeave += (_, _) => chip.Background = Frozen(A(acc, 0x16));
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenLink(target); };
            row.Children.Add(chip);
        }
        bubble.Extras.Children.Add(row);
    }

    /// <summary>Under an answer: which model wrote it, how long it took, how many steps it took (small, dim).</summary>
    void AnswerFooter(ChatBubble bubble, string? model, TimeSpan took, int steps, int tokens = 0)
    {
        string name = model == null || model == OpenRouter.DefaultModel ? "" : model.StartsWith("groq:") ? OpenRouter.GroqName(model) : OpenRouter.IsGemini && !model.Contains('/') ? OpenRouter.GeminiName(model) : model.Contains('/') ? model[(model.IndexOf('/') + 1)..] : model;
        var parts = new List<string>();
        if (name.Length > 0) parts.Add(name);
        parts.Add(took.TotalSeconds < 10 ? $"{took.TotalSeconds:0.0} s" : $"{took.TotalSeconds:0} s");
        if (steps > 0) parts.Add(steps == 1 ? "1 step" : $"{steps} steps");
        if (tokens > 0) parts.Add(tokens >= 1000 ? $"{tokens / 1000.0:0.#}K tokens" : $"{tokens} tokens");
        var line = new TextBlock
        {
            Text = string.Join("  ·  ", parts), FontSize = 9.5, Margin = new Thickness(0, 4, 0, 0), FontFamily = _cyber ? CyFont : UiText,
            Foreground = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), ToolTip = model,
        };
        bubble.Extras.Children.Add(line);
    }

    /// <summary>An answer's steps, once it's there: folded into one line ("3 steps · web, notch"), a click unfolds them.</summary>
    void FoldSteps(int from, int count, List<string> names, TimeSpan took)
    {
        if (_chatStack == null) return;
        var steps = _chatStack.Children.OfType<FrameworkElement>().Skip(from).Where(e => e.Tag as string == "step").ToList();
        if (steps.Count < 2) return;
        var acc = ChatAccent;
        string kinds = string.Join(", ", names.Select(n => n switch
        {
            "research" or "web_search" or "read_webpage" => "web", "news" => "news", "browser_history" => "browser",
            "find_files" or "read_file" or "list_folder" => "files", "write_file" => "a file", "create_image" => "a picture",
            "notch_data" or "notch_page" or "notify" or "recent_notifications" => "the notch",
            "run_powershell" => "PowerShell", "use_app" => "an app", "play_music" => "music", "do_on_pc" or "manage_window" or "clipboard" or "pc_status" => "the PC", "remember" or "forget" => "memory", _ => n,
        }).Distinct());
        var chevron = new TextBlock { Text = "\uE76C", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = Frozen(A(acc, 0xC0)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(0) };
        var label = new TextBlock { Text = $"{count} steps  ·  {kinds}", FontSize = 11, Foreground = Frozen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = _cyber ? CyFont : UiText };
        var check = new TextBlock { Text = "\uE73E", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Foreground = Frozen(Ok), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(chevron);
        line.Children.Add(label);
        line.Children.Add(check);
        var summary = new Border
        {
            Child = line, Margin = new Thickness(12, 6, 18, 0), Padding = new Thickness(8, 3, 10, 4), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand,
            CornerRadius = new CornerRadius(_cyber ? 0 : 9), Background = Frozen(A(acc, 0x12)), BorderBrush = Frozen(A(acc, 0x40)), BorderThickness = new Thickness(1), ToolTip = "Show the steps",
        };
        bool open = false;
        summary.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            open = !open;
            foreach (var s in steps) s.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            ((RotateTransform)chevron.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(open ? 90 : 0, TimeSpan.FromMilliseconds(160)));
            summary.ToolTip = open ? "Hide the steps" : "Show the steps";
        };
        summary.MouseEnter += (_, _) => summary.Background = Frozen(A(acc, 0x24));
        summary.MouseLeave += (_, _) => summary.Background = Frozen(A(acc, 0x12));
        _chatStack.Children.Insert(_chatStack.Children.IndexOf(steps[0]), summary);
        foreach (var s in steps) s.Visibility = Visibility.Collapsed;
    }

    /// <summary>What some models let slip: their thinking (&lt;think&gt;…), special tokens. Never shown.</summary>
    static string CleanAnswer(string text, bool streaming = false)
    {
        string s = Regex.Replace(text, @"<(think|thinking|thought)>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (Regex.Match(s, @"<(?:think|thinking|thought)>", RegexOptions.IgnoreCase) is { Success: true } open) s = s[..open.Index]; // still thinking: nothing to show yet
        s = Regex.Replace(s, @"<\|[^|>]{1,40}\|>", "");
        s = Regex.Replace(s, @"^\s*(?:Assistant|AI)\s*:\s*", "", RegexOptions.IgnoreCase);
        s = WithoutFollowUps(s, streaming); // the ">> next | steps" line becomes buttons
        return streaming ? s.TrimStart() : s.Trim();
    }
}
