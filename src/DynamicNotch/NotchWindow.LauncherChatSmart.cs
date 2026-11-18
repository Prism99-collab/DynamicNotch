using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Answers that do more than talk:
///  • follow-up chips: the AI ends an answer with "&gt;&gt; Close Chrome | Show top apps"; that line becomes buttons
///    under the answer, one click sends it (they go away once you send anything);
///  • cards inside answers: a line "[[card:batteries]]" draws your gear's batteries as ring meters, "[[card:pc]]" the
///    PC's load as gauges, from the notch's own live numbers (not the model's words);
///  • the same gear card greets you on an empty chat.
/// </summary>
public partial class NotchWindow
{
    FrameworkElement? _followUps;

    static readonly Regex FollowUpLine = new(@"(?:^|\n)[ \t]*>>[ \t]*(?<l>[^\n]*?)[ \t]*$", RegexOptions.Compiled);
    static readonly Regex CardLine = new(@"^\s*\[\[card:(?<k>[a-z]+)\]\]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The next steps the AI offered (its last "&gt;&gt; a | b | c" line), at most three.</summary>
    static List<string> FollowUps(string raw)
    {
        var m = FollowUpLine.Match(raw.TrimEnd());
        if (!m.Success) return new();
        return m.Groups["l"].Value.Split('|')
            .Select(x => Regex.Replace(x, @"[*`_""“”]", "").Trim().TrimEnd('.'))
            .Where(x => x.Length is > 1 and <= 44).Distinct().Take(3).ToList();
    }

    /// <summary>The "&gt;&gt;" line isn't part of the answer (also while it's still arriving).</summary>
    static string WithoutFollowUps(string s, bool streaming)
    {
        s = FollowUpLine.Replace(s.TrimEnd(), "");
        if (streaming) s = Regex.Replace(s, @"\n[ \t]*>?[ \t]*$", "");
        return s;
    }

    void ClearFollowUps()
    {
        if (_followUps?.Parent is Panel p) p.Children.Remove(_followUps);
        _followUps = null;
    }

    void ShowFollowUps(ChatBubble bubble, List<string> next)
    {
        ClearFollowUps();
        if (next.Count == 0) return;
        var acc = ChatAccent;
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        int i = 0;
        foreach (var text in next)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 8.5, Foreground = Frozen(acc), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
            label.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontFamily = _cyber ? CyFont : UiText });
            var chip = new Border
            {
                Child = label, Padding = new Thickness(9, 3, 11, 4), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(_cyber ? 0 : 11),
                Background = Frozen(new LinearGradientBrush(A(acc, 0x22), A(AiSecond, 0x10), 0)), BorderBrush = Frozen(A(acc, 0x60)), BorderThickness = new Thickness(1),
                ToolTip = "Send: " + text, Opacity = Eco ? 1 : 0,
            };
            chip.MouseEnter += (_, _) => { chip.Background = Frozen(new LinearGradientBrush(A(acc, 0x44), A(AiSecond, 0x24), 0)); chip.BorderBrush = Frozen(acc); };
            chip.MouseLeave += (_, _) => { chip.Background = Frozen(new LinearGradientBrush(A(acc, 0x22), A(AiSecond, 0x10), 0)); chip.BorderBrush = Frozen(A(acc, 0x60)); };
            chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; ClearFollowUps(); SendChat(text); };
            if (!Eco) chip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(120 + 70 * i++) });
            row.Children.Add(chip);
        }
        _followUps = row;
        bubble.Extras.Children.Add(row);
        FollowChat();
    }

    // ───────────────────────────── cards ─────────────────────────────

    /// <summary>A card asked for in an answer ("[[card:batteries]]"), or null when there's no such card.</summary>
    Block? AnswerCard(string kind) => kind.ToLowerInvariant() switch
    {
        "batteries" or "battery" or "gear" or "devices" => new BlockUIContainer(GearCard(inAnswer: true) ?? CardShell("Your gear", new TextBlock { Text = "No wireless device is connected right now.", FontSize = 11, Foreground = DimBrush })) { Margin = new Thickness(0, 3, 0, 7) },
        "pc" or "system" or "load" => new BlockUIContainer(PcCard()) { Margin = new Thickness(0, 3, 0, 7) },
        _ => null,
    };

    Border CardShell(string title, UIElement content)
    {
        var acc = ChatAccent;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = _cyber ? title.ToUpperInvariant() : title, FontSize = _cyber ? 9.5 : 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7),
            Foreground = Frozen(_cyber ? CyYellow : A(acc, 0xE0)), FontFamily = _cyber ? CyFont : UiText,
        });
        stack.Children.Add(content);
        return new Border
        {
            Child = stack, Padding = new Thickness(11, 8, 11, 9), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(_cyber ? 0 : 10), BorderThickness = new Thickness(1),
            Background = Frozen(new LinearGradientBrush(A(acc, 0x16), Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 90)),
            BorderBrush = Frozen(new LinearGradientBrush(A(acc, 0x60), A(AiSecond, 0x20), 0)),
        };
    }

    /// <summary>Green when there's plenty, amber when it's getting low, red when it's nearly out.</summary>
    static Color LevelTone(double level) => level >= 0.5 ? Color.FromRgb(0x4A, 0xE0, 0x8A) : level >= 0.2 ? Color.FromRgb(0xFF, 0xB8, 0x3D) : Color.FromRgb(0xFF, 0x55, 0x5E);

    /// <summary>A ring meter: the number in the middle, the name under it.</summary>
    FrameworkElement Ring(double level, string number, string name, string glyph, Color tone, bool dim = false, bool charging = false, string? tip = null)
    {
        const double size = 46, stroke = 4;
        double r = (size - stroke) / 2, c = size / 2;
        level = Math.Clamp(level, 0, 1);
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse { Stroke = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), StrokeThickness = stroke });
        if (level > 0.004)
        {
            Shape arc;
            if (level >= 0.999) arc = new Ellipse { StrokeThickness = stroke };
            else
            {
                double a = level * 2 * Math.PI;
                var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
                var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
                fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, level > 0.5, SweepDirection.Clockwise, true));
                arc = new Path { Data = new PathGeometry(new[] { fig }), StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            }
            arc.Stroke = Frozen(new LinearGradientBrush(tone, Lighten(tone, 0.35), 90));
            if (!_cyber && !Eco) arc.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = tone, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.55 };
            g.Children.Add(arc);
        }
        var mid = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = number, FontSize = number.Length > 3 ? 10 : 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = _cyber ? CyFont : UiText });
        if (charging) mid.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 8, Foreground = Frozen(tone), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) });
        g.Children.Add(mid);
        var col = new StackPanel { Margin = new Thickness(0, 0, 12, 0), Width = 64, Opacity = dim ? 0.55 : 1, ToolTip = tip ?? name };
        col.Children.Add(g);
        var under = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        under.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 4, 0) });
        under.Children.Add(new TextBlock { Text = name, FontSize = 9.5, Foreground = Frozen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), MaxWidth = 50, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        col.Children.Add(under);
        return col;
    }

    static string ShortName(string name)
    {
        name = Regex.Replace(name, @"\b(Wireless|Bluetooth|Gaming|Headset|Mouse|Core|Pro|LE)\b", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return name.Length > 0 ? name : "Device";
    }

    /// <summary>Your gear's batteries as rings (mouse, headset, Bluetooth devices, the PC): null when there's nothing to show.</summary>
    FrameworkElement? GearCard(bool inAnswer)
    {
        var rings = new WrapPanel();
        try
        {
            foreach (var d in _devices.Devices)
            {
                string glyph = d.Kind == DeviceKind.Mouse ? "" : "";
                string kind = d.Kind == DeviceKind.Mouse ? "Mouse" : "Headset";
                if (d.Level is { } l)
                    rings.Children.Add(Ring(l / 100.0, $"{l}%", kind, glyph, LevelTone(l / 100.0), dim: d.State != BatteryState.Live, charging: d.Charging && d.State == BatteryState.Live,
                        tip: $"{d.Name}: {l}%" + (d.State == BatteryState.Live ? d.Charging ? ", charging" : "" : " (last reading: it's asleep or off)")));
                else rings.Children.Add(Ring(0, "off", kind, glyph, LevelTone(0), dim: true, tip: d.Name + ": off"));
            }
            foreach (var b in _bt.ConnectedDevices.Where(b => b.Battery != null))
                rings.Children.Add(Ring(b.Battery!.Value / 100.0, $"{b.Battery}%", ShortName(b.Name), BluetoothService.GlyphFor(b), LevelTone(b.Battery.Value / 100.0), tip: $"{b.Name}: {b.Battery}%"));
            if (_power.HasBattery)
                rings.Children.Add(Ring(_power.Percent / 100.0, $"{_power.Percent}%", "PC", "", LevelTone(_power.Percent / 100.0), charging: _power.PluggedIn, tip: $"This PC: {_power.Percent}%" + (_power.PluggedIn ? ", plugged in" : "")));
        }
        catch { }
        if (rings.Children.Count == 0) return null;
        if (rings.Children[^1] is FrameworkElement last) last.Margin = new Thickness(0);
        return CardShell(inAnswer ? "Your gear" : "Your gear right now", rings);
    }

    /// <summary>The PC's load now, as gauges: processor, memory, disk (measured when the card is drawn).</summary>
    FrameworkElement PcCard()
    {
        var rings = new WrapPanel();
        var note = new TextBlock { Text = "Measuring…", FontSize = 10, Foreground = DimBrush, Margin = new Thickness(0, 6, 0, 0) };
        var content = new StackPanel();
        content.Children.Add(rings);
        content.Children.Add(note);
        void Fill(SystemSnapshot? snap)
        {
            rings.Children.Clear();
            double N(double v) => v > 1.5 ? v / 100 : v;
            // busy is red, idle is green: the opposite of a battery
            Color Busy(double v) => LevelTone(1 - v);
            double cpu = N(snap?.Cpu ?? 0), mem = N(snap?.Memory ?? 0), disk = N(snap?.Disk ?? 0);
            rings.Children.Add(Ring(cpu, $"{cpu * 100:0}%", "Processor", "", Busy(cpu)));
            rings.Children.Add(Ring(mem, $"{mem * 100:0}%", "Memory", "", Busy(mem), tip: snap != null ? $"{SystemMonitor.Bytes(snap.MemUsed)} of {SystemMonitor.Bytes(snap.MemTotal)}" : null));
            var lastRing = Ring(disk, $"{disk * 100:0}%", "Disk", "", Busy(disk), tip: snap != null ? $"{SystemMonitor.Bytes(snap.DiskFree)} free" : null);
            lastRing.Margin = new Thickness(0);
            rings.Children.Add(lastRing);
            note.Text = snap == null ? "Couldn't measure right now." : $"Up for {SystemMonitor.Duration(snap.Uptime)}{(snap.DiskFree > 0 ? " · " + SystemMonitor.Bytes(snap.DiskFree) + " free on the disk" : "")}";
        }
        Fill(null);
        note.Text = "Measuring…";
        _ = Measure();
        async Task Measure()
        {
            await _monitor.SampleAsync(); // the processor is measured between two readings
            await Task.Delay(600);
            var snap = await _monitor.SampleAsync();
            if (snap == null) { await Task.Delay(400); snap = await _monitor.SampleAsync(); }
            Fill(snap);
        }
        return CardShell("Your PC now", content);
    }
}
