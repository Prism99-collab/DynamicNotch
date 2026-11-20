using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The free AI you have left today, on the chat's home page: a ring that shows what's left of the models the AI uses
/// first (it fills in as it appears), the number left, a line for each kind of model (fast, smart, backup), and when it
/// all comes back, counting down. The numbers are what this PC sent and what Google answered (see AiUsage).
/// </summary>
public partial class NotchWindow
{
    FrameworkElement? QuotaCard()
    {
        if (!OpenRouter.HasKey) return null;
        var acc = ChatAccent;
        var icons = (FontFamily)FindResource("IconFont");
        var font = _cyber ? CyFont : UiText;

        // the ring
        const double size = 78, stroke = 7;
        var ring = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        ring.Children.Add(new Ellipse { Stroke = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), StrokeThickness = stroke, Margin = new Thickness(stroke / 2) });
        var arc = new Path
        {
            StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Stroke = Frozen(new LinearGradientBrush(Lighten(acc, 0.15), AiSecond, 45)),
        };
        if (!_cyber && !Eco) arc.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = acc, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.6 };
        ring.Children.Add(arc);
        const double inStroke = 4, inGap = 3;
        var groqTone = StepTone("news");
        var innerTrack = new Ellipse { Stroke = Frozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), StrokeThickness = inStroke, Margin = new Thickness(stroke + inGap + inStroke / 2), Visibility = Visibility.Collapsed };
        var innerArc = new Path { StrokeThickness = inStroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Stroke = Frozen(new LinearGradientBrush(Lighten(groqTone, 0.25), groqTone, 45)), Visibility = Visibility.Collapsed };
        ring.Children.Add(innerTrack);
        ring.Children.Add(innerArc);
        var mid = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var pct = new TextBlock { Text = "…", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = font };
        var pctWord = new TextBlock { Text = _cyber ? "LEFT" : "left", FontSize = 9, Foreground = DimBrush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0), FontFamily = font };
        mid.Children.Add(pct);
        mid.Children.Add(pctWord);
        ring.Children.Add(mid);

        // the words
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var live = new Ellipse { Width = 6, Height = 6, Fill = Frozen(Ok), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) };
        head.Children.Add(live);
        var headWord = new TextBlock { Text = _cyber ? "AI // TODAY" : "Your AI today", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Frozen(_cyber ? CyYellow : A(acc, 0xE0)), FontFamily = font, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(headWord);
        var planText = new TextBlock { FontSize = 8.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(acc), FontFamily = font };
        var plan = new Border { Child = planText, Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(7, 0, 0, 0), CornerRadius = new CornerRadius(_cyber ? 0 : 6), VerticalAlignment = VerticalAlignment.Center, Background = Frozen(A(acc, 0x22)), BorderBrush = Frozen(A(acc, 0x60)), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
        head.Children.Add(plan);
        void Plan(string text, string tip) { planText.Text = _cyber ? text.ToUpperInvariant() : text; plan.ToolTip = tip; plan.Visibility = Visibility.Visible; }
        var plan2Text = new TextBlock { FontSize = 8.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(StepTone("news")), FontFamily = font };
        var plan2 = new Border { Child = plan2Text, Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(5, 0, 0, 0), CornerRadius = new CornerRadius(_cyber ? 0 : 6), VerticalAlignment = VerticalAlignment.Center, Background = Frozen(A(StepTone("news"), 0x22)), BorderBrush = Frozen(A(StepTone("news"), 0x60)), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
        head.Children.Add(plan2);
        void Plan2(string text, string tip, bool bad = false)
        {
            plan2Text.Text = _cyber ? text.ToUpperInvariant() : text;
            plan2Text.Foreground = Frozen(bad ? Warn : StepTone("news"));
            plan2.BorderBrush = Frozen(A(bad ? Warn : StepTone("news"), 0x60));
            plan2.ToolTip = tip;
            plan2.Visibility = Visibility.Visible;
        }
        info.Children.Add(head);
        var bigLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var big = new TextBlock { Text = "…", FontSize = 20, FontWeight = FontWeights.Bold, FontFamily = font, Foreground = _cyber ? Frozen(CyYellow) : Frozen(new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Lighten(acc, 0.35), 0)), VerticalAlignment = VerticalAlignment.Bottom };
        var bigOf = new TextBlock { FontSize = 11, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 3), FontFamily = font };
        bigLine.Children.Add(big);
        bigLine.Children.Add(bigOf);
        info.Children.Add(bigLine);
        var tiers = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        info.Children.Add(tiers);
        var resetLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        resetLine.Children.Add(new TextBlock { Text = "", FontFamily = icons, FontSize = 9.5, Foreground = Frozen(A(acc, 0xD0)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
        var reset = new TextBlock { FontSize = 10.5, Foreground = Frozen(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, FontFamily = font };
        resetLine.Children.Add(reset);
        info.Children.Add(resetLine);
        var today = new TextBlock { FontSize = 9.5, Foreground = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 3, 0, 0), FontFamily = font };
        info.Children.Add(today);

        var addGroq = ChatChip("Add Groq: free, faster, a backup", "\uE710");
        addGroq.HorizontalAlignment = HorizontalAlignment.Left;
        addGroq.Height = 22;
        addGroq.FontSize = 10;
        addGroq.Click += (_, _) => ChatGroqCard();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(ring);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        var card = new Border
        {
            Child = grid, Padding = new Thickness(12, 10, 14, 11), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(_cyber ? 0 : 14), BorderThickness = new Thickness(1),
            Background = Frozen(new LinearGradientBrush(A(acc, 0x18), Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF), 90)), BorderBrush = Frozen(new LinearGradientBrush(A(acc, 0x70), A(AiSecond, 0x24), 45)),
            ToolTip = "Counted from what this PC sent with your key, and what Google answered.\nRequests made with the same key elsewhere aren't included.",
        };

        // the ring fills in to its share
        double shown = 0, target = 0, innerShown = 0, innerTarget = 0, innerFrom = 0;
        void DrawOn(Path p, double share, double r)
        {
            double c = size / 2;
            if (share <= 0.001) { p.Data = null; return; }
            if (share >= 0.999) { p.Data = new EllipseGeometry(new Point(c, c), r, r); return; }
            double a = share * 2 * Math.PI;
            var fig = new PathFigure { StartPoint = new Point(c, c - r) };
            fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, share > 0.5, SweepDirection.Clockwise, true));
            p.Data = new PathGeometry(new[] { fig });
        }
        void Draw(double share)
        {
            DrawOn(arc, share, (size - stroke) / 2);
            if (innerArc.Visibility == Visibility.Visible) DrawOn(innerArc, innerShown, (size - stroke) / 2 - stroke / 2 - inGap - inStroke / 2);
        }
        var fill = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        var fillStart = DateTime.UtcNow;
        double from = 0;
        fill.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.UtcNow - fillStart).TotalMilliseconds / 750);
            double eased = 1 - Math.Pow(1 - t, 3);
            shown = from + (target - from) * eased;
            innerShown = innerFrom + (innerTarget - innerFrom) * eased;
            Draw(shown);
            if (t >= 1) fill.Stop();
        };

        DateTime resetAt = DateTime.Now.AddDays(1);
        int minuteUsed = 0, minuteLimit = 0;
        bool hasGroq = false, groqOnly = false;
        void Countdown()
        {
            if (groqOnly) { reset.Text = "Groq refills a little every minute, all day"; return; }
            if (OpenRouter.IsGemini && OpenRouter.GeminiPlan == "paid") return;
            var left = resetAt - DateTime.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            string inText = left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes:00} min" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";
            reset.Text = (hasGroq ? $"Google resets in {inText} ({resetAt:HH:mm})  ·  Groq refills as you go" : $"Full reset in {inText}  ·  {resetAt:HH:mm}") + (minuteLimit > 0 && minuteUsed >= minuteLimit * 0.8 ? "  ·  busy this minute" : "");
        }

        const string EstimateTip = "≈ An estimate: counted from what this PC sent with this key. Google doesn't say how much is left, so uses of the same key elsewhere (AI Studio, another app or PC) aren't seen, and the daily limit is Google's published one until Google states this key's own (the first time a model runs out). Then the number is exact.";

        Border TierRow(string name, int leftCount, int limit, bool current, Color tone, int? sentToday = null, bool estimate = false)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = Frozen(tone), Margin = new Thickness(0, 1, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(new TextBlock { Text = name, FontSize = 10, Foreground = current ? Brushes.White : DimBrush, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal, FontFamily = font });
            row.Children.Add(label);
            double share = sentToday != null ? 1 : limit <= 0 ? 0 : (double)leftCount / limit;
            var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)) });
            var bar = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = Frozen(new LinearGradientBrush(tone, Lighten(tone, 0.3), 0)) };
            track.Children.Add(bar);
            track.SizeChanged += (_, _) => bar.Width = Math.Max(share > 0 ? 3 : 0, track.ActualWidth * share);
            Grid.SetColumn(track, 1);
            row.Children.Add(track);
            var value = new TextBlock { Text = sentToday is { } n ? $"{Compact(n)} today" : $"{(estimate ? "≈" : "")}{Compact(leftCount)} / {Compact(limit)}", ToolTip = estimate ? EstimateTip : null, FontSize = 9.5, Foreground = current ? Frozen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)) : DimBrush, FontFamily = font, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            return new Border { Child = row };
        }

        async void Fill() => await FillFrom(quick: true);

        async Task FillFrom(bool quick)
        {
            List<OpenRouter.TierUsage> usage;
            List<OpenRouter.TierUsage> groqUse = new();
            OpenRouter.KeyAccount? account = null;
            if (OpenRouter.IsGemini)
            {
                // never waits on the internet: the model lists known (saved on disk) are used at once
                try { usage = await OpenRouter.GeminiUsage(quick); } catch { usage = new(); }
                try { groqUse = await OpenRouter.GroqUsage(quick); } catch { }
                resetAt = OpenRouter.GeminiReset;
                string? keyPlan = OpenRouter.GeminiPlan;
                if (usage.Count > 0)
                {
                    if (keyPlan == "paid") Plan("Google · paid", "This Google key is on a paid plan: no daily cap, Google bills what's used.");
                    else if (keyPlan == "free") Plan("Google · free", "This Google key is on the free plan (Google said so). Its daily allowance is shown.");
                    else Plan("Google · free?", "Google hasn't said this key's plan yet: the free allowance is shown until it does (the first time a model runs out, Google gives its exact numbers).");
                }
                if (groqUse.Count > 0) Plan2("Groq · free", "Groq: about 1,000 requests a day per model, 8,000 tokens a minute. Its own numbers, from its last answer.");
                else if (OpenRouter.GroqRefused) Plan2("Groq key ✕", "Groq didn't accept its key. Type /groq to enter a new one.", bad: true);
                hasGroq = groqUse.Count > 0 && usage.Count > 0;
                groqOnly = groqUse.Count > 0 && usage.Count == 0;
                if (groqOnly) usage = groqUse;
                // no Groq yet: one click to add it
                if (!OpenRouter.HasGroqKey && usage.Count > 0 && addGroq.Parent == null)
                {
                    addGroq.Margin = new Thickness(0, 6, 0, 0);
                    info.Children.Add(addGroq);
                }
            }
            else
            {
                account = await OpenRouter.OpenRouterAccount();
                var (_, used) = OpenRouter.FreeUsedToday();
                int perDay = account?.FreePerDay ?? 50;
                usage = new() { new OpenRouter.TierUsage("free", "Free", used, perDay, 1, 0, 20, Exact: account != null) };
                resetAt = DateTime.UtcNow.Date.AddDays(1).ToLocalTime();
                if (account == null) Plan("OpenRouter", "OpenRouter's account details couldn't be read right now.");
                else if (account.FreeTier) Plan("Free plan", "This OpenRouter key has no credit bought: 50 requests a day to free models.");
                else Plan(account.CreditsLeft is { } c ? $"${c:0.00} left" : $"${account.Used:0.00} used", "This OpenRouter key has credit: 1,000 requests a day to free models, paid models use the credit.");
            }
            if (usage.Count == 0) { card.Visibility = Visibility.Collapsed; return; }
            // a paid Google key: no daily cap to count down, what was sent today instead
            if (usage[0].Paid)
            {
                pct.Text = "∞";
                pctWord.Text = _cyber ? "NO CAP" : "no cap";
                big.Text = Compact(usage.Sum(u => u.Used));
                bigOf.Text = "requests today · billed by use";
                live.Fill = Frozen(Ok);
                long paidTokens = usage.Sum(u => u.Tokens);
                today.Text = paidTokens > 0 ? $"Today: {(paidTokens >= 1000 ? $"{paidTokens / 1000.0:#,0.#}K" : paidTokens.ToString())} tokens" : "";
                tiers.Children.Clear();
                var paidTones = new Dictionary<string, Color> { ["lite"] = acc, ["flash"] = AiSecond, ["gemma"] = StepTone("notch_data") };
                foreach (var u in usage) tiers.Children.Add(TierRow(u.Name, 0, 0, false, paidTones.GetValueOrDefault(u.Tier, acc), u.Used));
                foreach (var q in groqUse) tiers.Children.Add(TierRow("Groq", q.Left, q.Limit, false, groqTone));
                minuteUsed = minuteLimit = 0;
                reset.Text = "Counted from Google's day, which starts at " + resetAt.ToString("HH:mm");
                from = shown; target = 1;
                if (Eco) { shown = 1; Draw(1); } else { fillStart = DateTime.UtcNow; fill.Start(); }
                return;
            }
            // the ring is for the models the AI uses first: the fast ones while they last, then the next
            var current = usage.FirstOrDefault(u => u.Left > 0) ?? usage[0];
            minuteUsed = current.MinuteUsed;
            minuteLimit = current.MinuteLimit;
            bool none = usage.All(u => u.Left == 0);
            pct.Text = none ? "0%" : $"{Math.Round(current.LeftShare * 100):0}%";
            pctWord.Text = _cyber ? current.Name.ToUpperInvariant() : current.Name.ToLowerInvariant() + " left";
            bool guess = OpenRouter.IsGemini && !current.Exact && current.Tier != "groq";
            big.Text = none ? "Used up" : (guess ? "≈" : "") + Compact(current.Left);
            big.ToolTip = guess ? EstimateTip : null;
            bigOf.Text = none ? "back at reset" : $"{(current.Left == 1 ? "request" : "requests")} left of {Compact(current.Limit)}";
            live.Fill = Frozen(none ? Warn : current.LeftShare < 0.2 ? Warn : Ok);
            int requests = AiUsage.SentToday(); // what was really sent today (Google's and Groq's), not what's missing from a limit
            long tokens = usage.Sum(u => u.Tokens);
            today.Text = $"Today: {requests:#,0} request{(requests == 1 ? "" : "s")}" + (tokens > 0 ? $"  ·  {(tokens >= 1000 ? $"{tokens / 1000.0:#,0.#}K" : tokens.ToString())} tokens" : "")
                + (OpenRouter.IsGemini && usage.All(u => u.Exact) ? "  ·  Google's exact limits" : "");
            tiers.Children.Clear();
            var tones = new Dictionary<string, Color> { ["lite"] = acc, ["flash"] = AiSecond, ["gemma"] = StepTone("notch_data"), ["free"] = acc };
            if (usage.Count > 1 || hasGroq)
                foreach (var u in usage) tiers.Children.Add(TierRow(u.Name, u.Left, u.Limit, u == current, tones.GetValueOrDefault(u.Tier, acc), estimate: OpenRouter.IsGemini && !u.Exact));
            if (hasGroq)
            {
                var q = groqUse[0];
                tiers.Children.Add(TierRow("Groq", q.Left, q.Limit, false, groqTone));
                // everything the AI can still use today: every Google kind (it moves to the next as one runs out) and Groq
                int total = usage.Sum(u => u.Left) + q.Left;
                // today's requests: everything sent today, Google's and Groq's
                int all = requests;
                today.Text = $"Today: {all:#,0} request{(all == 1 ? "" : "s")}" + (tokens > 0 ? $"  ·  {(tokens >= 1000 ? $"{tokens / 1000.0:#,0.#}K" : tokens.ToString())} tokens" : "")
                    + (OpenRouter.IsGemini && usage.All(u => u.Exact) ? "  ·  Google's exact limits" : "");
                pct.Text = Compact(total);
                pct.FontSize = total >= 1000 ? 14 : 17;
                pctWord.Text = _cyber ? "LEFT" : "left";
                // Groq's numbers are its own; Google's are counted here until Google states them: marked when they're an estimate
                bool estimated = usage.Any(u => !u.Exact);
                big.Text = (estimated ? "≈" : "") + Compact(total);
                big.ToolTip = estimated ? EstimateTip : "Exact: Google's and Groq's own numbers";
                bigOf.Text = "requests left · Google + Groq";
                live.Fill = Frozen(total == 0 ? Warn : Ok);
                long qTokens = q.Tokens;
                if (qTokens > 0) today.Text += $"  ·  Groq {(qTokens >= 1000 ? $"{qTokens / 1000.0:#,0.#}K" : qTokens.ToString())} tokens";
                innerTrack.Visibility = innerArc.Visibility = Visibility.Visible;
                innerFrom = innerShown;
                innerTarget = q.LeftShare;
                ring.ToolTip = $"Outer ring: Google, {current.Name.ToLowerInvariant()} models ({current.Left} of {current.Limit} left)\nInner ring: Groq ({q.Left} of {q.Limit} left)" + (q.MinuteLimit > 0 ? $"\nGroq this minute: {q.MinuteLimit - q.MinuteUsed:#,0} of {q.MinuteLimit:#,0} tokens" : "");
            }
            else if (groqOnly)
            {
                pctWord.Text = _cyber ? "GROQ LEFT" : "groq left";
                bigOf.Text = $"requests left of {Compact(current.Limit)} · Groq";
            }
            Countdown();
            from = shown;
            target = none ? 0 : current.LeftShare;
            if (Eco) { shown = target; innerShown = innerTarget; Draw(target); }
            else { fillStart = DateTime.UtcNow; fill.Start(); }
            // the lists were old or not read yet: once fresh ones come, drawn again (only if they change anything)
            if (quick && OpenRouter.IsGemini && !OpenRouter.ModelListsFresh)
            {
                string before = Compact(usage.Sum(u => u.Limit)) + "|" + Compact(groqUse.Sum(u => u.Limit));
                await OpenRouter.RefreshModelLists();
                var g2 = await OpenRouter.GeminiUsage(true);
                var q2 = await OpenRouter.GroqUsage(true);
                if (Compact(g2.Sum(u => u.Limit)) + "|" + Compact(q2.Sum(u => u.Limit)) != before) await FillFrom(quick: false);
            }
        }

        var clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        clock.Tick += (_, _) =>
        {
            Countdown();
            if (DateTime.Now >= resetAt) Fill(); // it came back: the ring fills up again
        };
        card.Loaded += (_, _) => { Fill(); clock.Start(); };
        card.Unloaded += (_, _) => { clock.Stop(); fill.Stop(); };
        return card;
    }

    static string Compact(int n) => n >= 10000 ? $"{n / 1000.0:0.#}K" : n >= 1000 ? $"{n:#,0}" : n.ToString();
}
