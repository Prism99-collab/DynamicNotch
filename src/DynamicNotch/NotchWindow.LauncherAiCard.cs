using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The AI's way in from the empty search: a card on top with its living mark, what it would continue ("Continue · your
/// last chat"), a "New" button, and Tab as its key. The empty page itself arrives in a quick cascade.
/// </summary>
public partial class NotchWindow
{
    AiOrb? _startOrb;

    Color AiSecond => _cyber ? CyMagenta : Color.FromRgb(0xBF, 0x5A, 0xF2);

    /// <summary>The card at the top of the empty search (nothing when the AI chat is off).</summary>
    FrameworkElement? AiStartCard()
    {
        _startOrb?.Stop();
        _startOrb = null;
        if (!_settings.AiChat) return null;
        var acc = ChatAccent;
        var second = AiSecond;

        // what a click goes on with: the chat open now, else the last one kept
        string? title = null;
        DateTime when = default;
        if (_session is { } s && s.UserTurns > 0) { title = s.Title; when = s.Updated; }
        else if (_settings.AiHistory && ChatStore.Latest() is { } last && last.Turns > 0) { title = last.Title; when = last.Updated; }
        bool connected = OpenRouter.HasKey;

        var row = new Grid { Height = 38 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var orb = new AiOrb(24, acc, second, _cyber) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _startOrb = orb;
        row.Children.Add(orb);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
        words.Children.Add(new TextBlock
        {
            Text = _cyber ? (title != null ? "AI // RESUME LINK" : "AI // ASK OR ORDER") : title != null ? "Ask AI" : "Ask AI anything",
            FontSize = 12.5, FontWeight = FontWeights.SemiBold, FontFamily = _cyber ? CyFont : UiText, Foreground = _cyber ? Frozen(CyYellow) : Brushes.White,
        });
        words.Children.Add(new TextBlock
        {
            Text = !connected ? "Connect it once with a free key" : _hereSelection is { } picked ? $"Selected text from {picked.App} · Tab to ask about it" : title != null ? $"Continue · {title} · {ChatAgo(when)}" : _settings.AiTools ? "Answers, writes, and does things on your PC" : "Answers and writes for you",
            FontSize = 10.5, Foreground = Frozen(A(acc, 0xC8)), TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = _cyber ? CyFont : UiText,
        });
        Grid.SetColumn(words, 1);
        row.Children.Add(words);

        // the key, and a fresh chat
        var side = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        side.Children.Add(KeyCap("Tab", acc));
        if (connected && title != null)
        {
            var plus = new TextBlock { Text = "\uE710", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = Frozen(acc), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = _cyber ? "NEW LINK" : "New chat", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(acc), FontFamily = _cyber ? CyFont : UiText, Margin = new Thickness(5, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center };
            var fresh = new Border
            {
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { plus, label } },
                Height = 22, Padding = new Thickness(8, 0, 9, 0), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = "Start a new chat  ·  Ctrl+N",
                CornerRadius = new CornerRadius(_cyber ? 0 : 11), Background = Frozen(A(acc, 0x18)), BorderBrush = Frozen(A(acc, 0x70)), BorderThickness = new Thickness(1),
            };
            fresh.MouseEnter += (_, _) => { fresh.Background = Frozen(A(acc, 0x40)); label.Foreground = plus.Foreground = Brushes.White; };
            fresh.MouseLeave += (_, _) => { fresh.Background = Frozen(A(acc, 0x18)); label.Foreground = plus.Foreground = Frozen(acc); };
            fresh.MouseLeftButtonUp += (_, e) => { e.Handled = true; StartSession(null); EnterChat(null); };
            side.Children.Add(fresh);
        }
        Grid.SetColumn(side, 2);
        row.Children.Add(side);

        // the card: a faint wash of the AI's two colours, a brighter edge, lifting a little under the pointer
        var wash = new LinearGradientBrush(A(acc, _cyber ? (byte)0x1E : (byte)0x26), A(second, _cyber ? (byte)0x14 : (byte)0x1C), new Point(0, 0.5), new Point(1, 0.5));
        var edge = new LinearGradientBrush(A(acc, 0x90), A(second, 0x50), new Point(0, 0.5), new Point(1, 0.5));
        var card = new Border
        {
            Child = row, Background = wash, BorderBrush = edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(_cyber ? 0 : 11),
            Margin = new Thickness(0, 2, 2, 2), Cursor = Cursors.Hand, RenderTransform = new TranslateTransform(), Tag = "ai", ToolTip = connected ? "Tab  ·  or Shift+Enter on anything you type" : null,
        };
        if (_cyber) NeonCorner(card, 9, 6);
        var lift = (TranslateTransform)card.RenderTransform;
        card.MouseEnter += (_, _) =>
        {
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-1.5, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            card.Background = new LinearGradientBrush(A(acc, 0x3A), A(second, 0x2A), new Point(0, 0.5), new Point(1, 0.5));
            orb.Busy(true);
        };
        card.MouseLeave += (_, _) =>
        {
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
            card.Background = wash;
            orb.Busy(false);
        };
        card.MouseLeftButtonUp += (_, _) => EnterChat(null, resume: true);
        if (!Eco) orb.Start();
        return card;
    }

    Border KeyCap(string key, Color acc) => new()
    {
        Child = new TextBlock { Text = key, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(A(acc, 0xE0)), FontFamily = _cyber ? CyFont : UiText },
        Padding = new Thickness(6, 1, 6, 2), CornerRadius = new CornerRadius(_cyber ? 0 : 5), BorderBrush = Frozen(A(acc, 0x70)), BorderThickness = new Thickness(1),
        Background = Frozen(A(acc, 0x10)), VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The empty page arrives in a quick cascade, top to bottom.</summary>
    void CascadeStarters()
    {
        if (_launchStarters == null || Eco) return;
        int i = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (UIElement el in _launchStarters.Children)
        {
            var begin = TimeSpan.FromMilliseconds(28 * i++);
            // the AI card moves its own transform (the one it lifts with on hover)
            var shift = el is FrameworkElement { Tag: "ai" } && el.RenderTransform is TranslateTransform own ? own : new TranslateTransform(0, 6);
            el.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(260)) { BeginTime = begin, EasingFunction = ease });
            el.Opacity = 0;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = begin });
        }
    }

    void StopStartOrb() => _startOrb?.Stop();
}
