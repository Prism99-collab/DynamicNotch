using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Richer answers, drawn by the notch itself:
///  • charts: the AI writes the numbers (a ```chart block), the notch draws a line, area, bar or pie chart from them,
///    exact, instant, offline, with the value under the pointer, and copies or saves it as a picture;
///  • tables: sort by clicking a column, copy (pastes into Excel), save as a real Excel file;
///  • PowerShell in an answer: a Run button (anything risky still asks first), its output shown under it;
///  • under an answer: a reminder for a time it mentions, and the file it mentions, to open or show in its folder.
/// </summary>
public partial class NotchWindow
{
    const string ChartRule = "CHARTS: a graph, chart or plot is never a picture (never create_image for it). Write the numbers as a code block marked chart, with JSON: ```chart\n{\"type\":\"line\",\"title\":\"…\",\"unit\":\"°C\",\"labels\":[\"Thu 19\",\"Fri 20\"],\"series\":[{\"name\":\"High\",\"values\":[23,21]},{\"name\":\"Low\",\"values\":[18,16]}]}\n``` type: line (change over time), area, bar (comparing things), pie (parts of a whole: one series). Real numbers only, from the conversation or research; then one short sentence on what it shows.";

    static readonly Regex ChartWords = new(@"\b(make|draw|create|show|give|plot|generate|build|put|turn|display|visuali[sz]e|fais|dessine|trace|montre|affiche)\b.{0,50}\b(graphs?|charts?|plot|diagram|histogram|pie|graphique|courbe|diagramme|camembert)\b|\b(as|in|into|with) a (bar |line |pie |)(graph|chart|plot|diagram)\b|\b(graph|chart|plot) (of|for|showing|comparing)\b|\b(plot|graph|chart) (the|my|a|an|these|this|it|them|how)\b|\bvisuali[sz]e\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The tools without one of them (the list is copied: the full list is shared).</summary>
    static JsonArray WithoutTool(JsonArray tools, string name)
    {
        var copy = new JsonArray();
        foreach (var t in tools) if (t?["function"]?["name"]?.ToString() != name) copy.Add(t?.DeepClone());
        return copy;
    }

    // ───────────────────────────── charts ─────────────────────────────

    /// <summary>A chart from the AI's numbers, with its buttons; null when the numbers can't be read (the block shows as code).</summary>
    Block? ChartBlock(string json)
    {
        ChartData? data;
        try { data = ChartData.Parse(json); } catch { data = null; }
        if (data == null) return null;
        var acc = ChatAccent;
        var palette = new[] { _cyber ? CyCyan : acc, _cyber ? CyYellow : AiSecond, Color.FromRgb(0xFF, 0xB7, 0x4D), Color.FromRgb(0x81, 0xC7, 0x84), Color.FromRgb(0xE5, 0x73, 0x73), Color.FromRgb(0x64, 0xB5, 0xF6) };
        var view = new ChartView(data, palette, _cyber ? CyFont : UiText, TextTone, Eco) { Height = data.Type == "pie" ? 168 : 186, Margin = new Thickness(8, 6, 8, 4) };
        var frame = new Border
        {
            Child = view, Background = Frozen(Color.FromArgb(0x50, 0x00, 0x00, 0x00)), BorderBrush = Frozen(A(acc, _cyber ? (byte)0x55 : (byte)0x2A)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_cyber ? 0 : 9),
        };
        var stack = new StackPanel();
        stack.Children.Add(frame);
        stack.Children.Add(SmallButtons(("Copy image", () => { if (Snapshot(frame) is { } png) { Clipboard.SetImage(png); Toast("Chart copied"); } }),
            ("Save", () =>
            {
                if (Snapshot(frame) is not { } png) return;
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pixo");
                System.IO.Directory.CreateDirectory(folder);
                string file = System.IO.Path.Combine(folder, $"Chart {DateTime.Now:yyyy-MM-dd HHmmss}.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(png));
                using (var fs = System.IO.File.Create(file)) enc.Save(fs);
                Toast("Saved in Pictures\\Pixo");
                OpenLink(file);
            })));
        return new BlockUIContainer(stack) { Margin = new Thickness(0, 3, 0, 7) };
    }

    /// <summary>A sharp picture of a part of the chat (twice the screen's pixels), on the chat's dark background.</summary>
    static BitmapSource? Snapshot(FrameworkElement el)
    {
        try
        {
            int w = (int)Math.Ceiling(el.ActualWidth * 2), h = (int)Math.Ceiling(el.ActualHeight * 2);
            if (w < 4 || h < 4) return null;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            }
            var bmp = new RenderTargetBitmap(w, h, 192, 192, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>A row of small text buttons on the right ("Copy · Excel").</summary>
    FrameworkElement SmallButtons(params (string Label, Action Act)[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 2, 0) };
        foreach (var (label, act) in buttons)
        {
            var b = new TextBlock { Text = _cyber ? label.ToUpperInvariant() : label, FontSize = 9.5, Foreground = DimBrush, Cursor = Cursors.Hand, Margin = new Thickness(10, 0, 0, 0), FontFamily = _cyber ? CyFont : UiText };
            b.MouseEnter += (_, _) => b.Foreground = Frozen(ChatAccent);
            b.MouseLeave += (_, _) => b.Foreground = DimBrush;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; try { act(); } catch { Toast("Couldn't do that"); } _launchBox?.Focus(); };
            row.Children.Add(b);
        }
        return row;
    }

    // ───────────────────────────── tables ─────────────────────────────

    /// <summary>Under a table: copy it (tab-separated: it pastes into Excel as cells) or save it as an Excel file.</summary>
    Block TableTools(List<string> rows)
    {
        string Clean(string s) => Regex.Replace(s, @"\*\*|__|`", "").Trim();
        var cells = rows.Where(r => !IsTableRule(r)).Select(r => Cells(r).Select(Clean).ToArray()).ToList();
        // a block in a document can't have a negative margin (it throws): the buttons themselves move up to the table
        var buttons = SmallButtons(
            ("Copy", () => CopyText(string.Join("\r\n", cells.Select(c => string.Join("\t", c))), "Table copied: paste it in Excel or Sheets")),
            ("Excel", () =>
            {
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Pixo");
                System.IO.Directory.CreateDirectory(folder);
                string file = System.IO.Path.Combine(folder, $"Table {DateTime.Now:yyyy-MM-dd HHmmss}.xlsx");
                DocWriter.Write(file, string.Join("\n", rows.Select(Clean)));
                Toast("Saved in Documents\\Pixo");
                OpenLink(file);
            }));
        buttons.Margin = new Thickness(0, -6, 2, 0);
        return new BlockUIContainer(buttons) { Margin = new Thickness(0, 0, 0, 8) };
    }

    /// <summary>A click on a column's title sorts the table by it (again: the other way).</summary>
    void MakeSortable(Table table, TableRowGroup group, bool header, Func<int, Brush> stripe)
    {
        if (!header || group.Rows.Count < 3) return;
        var head = group.Rows[0];
        int sortedBy = -1;
        bool down = false;
        for (int c = 0; c < head.Cells.Count; c++)
        {
            int col = c;
            var cell = head.Cells[c];
            cell.Cursor = Cursors.Hand;
            cell.ToolTip = "Sort by this column";
            cell.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                down = sortedBy == col ? !down : false;
                sortedBy = col;
                var body = group.Rows.Skip(1).ToList();
                string Key(TableRow r) => col < r.Cells.Count ? new TextRange(r.Cells[col].ContentStart, r.Cells[col].ContentEnd).Text.Trim() : "";
                static double? Num(string s) => double.TryParse(Regex.Replace(s.Replace(',', '.'), @"[^\d.\-]", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && Regex.IsMatch(s, @"\d") ? d : null;
                bool numeric = body.All(r => Num(Key(r)) != null);
                var sorted = numeric ? body.OrderBy(r => Num(Key(r))).ToList() : body.OrderBy(r => Key(r), StringComparer.CurrentCultureIgnoreCase).ToList();
                if (down) sorted.Reverse();
                foreach (var r in body) group.Rows.Remove(r);
                for (int i = 0; i < sorted.Count; i++) { sorted[i].Background = stripe(i); group.Rows.Add(sorted[i]); }
            };
        }
    }

    // ───────────────────────────── code ─────────────────────────────

    static bool Runnable(string language) => language.Trim().ToLowerInvariant() is "powershell" or "ps1" or "pwsh" or "ps";

    /// <summary>Runs a PowerShell block from an answer (risky commands still ask first) and shows what it printed.</summary>
    async void RunCodeBlock(string script, StackPanel under, TextBlock button)
    {
        button.Text = _cyber ? "RUNNING…" : "Running…";
        button.IsEnabled = false;
        string result;
        try { result = await RunPowerShell(script, "Run the code from the answer", CancellationToken.None); }
        catch (Exception ex) { result = "Error: " + ex.Message; }
        button.Text = _cyber ? "RUN AGAIN" : "Run again";
        button.IsEnabled = true;
        if (under.Children.OfType<TextBox>().FirstOrDefault(t => Equals(t.Tag, "output")) is { } old) under.Children.Remove(old);
        under.Children.Add(new TextBox
        {
            Text = result.Length > 3000 ? result[..3000] + "\n…" : result, Tag = "output", IsReadOnly = true, FontFamily = ChatMono, FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
            Foreground = Frozen(result.StartsWith("Error") ? Warn : Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), Background = Frozen(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Frozen(A(ChatAccent, 0x30)), Padding = new Thickness(7, 4, 7, 5), MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        FollowChat();
    }

    // ───────────────────────────── under an answer: a reminder, a file ─────────────────────────────

    static readonly Regex TimeMention = new(@"\b(?:(?:today|tonight|tomorrow|(?:on |next )?(?:mon|tues|wednes|thurs|fri|satur|sun)day)(?: (?:morning|afternoon|evening|night))? at \d{1,2}(?::\d{2})?(?:\s?(?:am|pm))?|at \d{1,2}:\d{2}(?:\s?(?:am|pm))?|at \d{1,2}\s?(?:am|pm)|at \d{1,2}h(?:\d{2})?)\b(?!\s*(?:°|%|degrees|km|kg|mph|euros?|dollars?|€|\$))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Once an answer is written: a reminder for a time it names, and the file it names (open, show in folder).</summary>
    void ShowAnswerActions(ChatBubble bubble, string said)
    {
        var buttons = new List<(string, Action)>();
        // a time it mentions ("tomorrow at 9", "on Friday at 15:30"): a reminder in one click
        foreach (Match m in TimeMention.Matches(said))
        {
            if (LauncherIntent.When(m.Value, DateTime.Now) is not { } at || at <= DateTime.Now.AddMinutes(1) || at > DateTime.Now.AddDays(60)) continue;
            string sentence = Regex.Match(said, @"[^.\n!?]*" + Regex.Escape(m.Value) + @"[^.\n!?]*").Value;
            string what = Regex.Replace(Regex.Replace(sentence, @"\*\*|__|`|\[|\]\([^)]*\)", ""), @"\s+", " ").Trim();
            if (what.Length > 60) what = what[..60].TrimEnd() + "…";
            if (what.Length == 0) what = "Reminder";
            buttons.Add(($"Remind me {at:ddd HH:mm}", () => { AddReminder(what, at); Toast($"Reminder set: {at:dddd HH:mm}"); }));
            break;
        }
        // a file it names that exists (one it just made, one it found)
        foreach (Match m in Regex.Matches(said, @"\b[A-Za-z]:\\[^\s<>|""*?]*[^\s<>|""*?.,;:)\]]"))
        {
            string path = m.Value;
            if (!System.IO.File.Exists(path)) continue;
            string name = System.IO.Path.GetFileName(path);
            if (name.Length > 28) name = name[..26] + "…";
            buttons.Add(($"Open {name}", () => OpenLink(path)));
            buttons.Add(("Show in folder", () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"")));
            break;
        }
        if (buttons.Count == 0) return;
        var acc = ChatAccent;
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, act) in buttons)
        {
            var chip = new Border
            {
                Child = new TextBlock { Text = label, FontSize = 10.5, Foreground = Frozen(Lighten(acc, 0.35)), FontFamily = _cyber ? CyFont : UiText },
                Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(0, 0, 6, 4), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(_cyber ? 0 : 10),
                Background = Frozen(A(acc, 0x16)), BorderBrush = Frozen(A(acc, 0x50)), BorderThickness = new Thickness(1),
            };
            chip.MouseEnter += (_, _) => chip.BorderBrush = Frozen(acc);
            chip.MouseLeave += (_, _) => chip.BorderBrush = Frozen(A(acc, 0x50));
            chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; try { act(); } catch { Toast("Couldn't do that"); } _launchBox?.Focus(); };
            row.Children.Add(chip);
        }
        bubble.Extras.Children.Add(row);
    }
}

/// <summary>A chart's numbers, as the AI writes them.</summary>
sealed class ChartData
{
    public string Type = "line", Title = "", Unit = "";
    public List<string> Labels = new();
    public List<(string Name, double?[] Values)> Series = new();

    public static ChartData? Parse(string json)
    {
        var n = JsonNode.Parse(json.Trim());
        if (n is not JsonObject o) return null;
        var d = new ChartData
        {
            Type = (o["type"]?.ToString() ?? "line").ToLowerInvariant() switch { "bar" or "column" or "bars" => "bar", "pie" or "donut" or "doughnut" => "pie", "area" => "area", _ => "line" },
            Title = o["title"]?.ToString() ?? "", Unit = o["unit"]?.ToString() ?? "",
        };
        if (o["labels"] is JsonArray labels) d.Labels = labels.Select(l => l?.ToString() ?? "").ToList();
        static double? Num(JsonNode? v)
        {
            if (v == null) return null;
            if (v is JsonValue jv && jv.TryGetValue(out double x)) return double.IsFinite(x) ? x : null;
            return double.TryParse(Regex.Replace(v.ToString().Replace(',', '.'), @"[^\d.\-]", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ? y : null;
        }
        if (o["series"] is JsonArray series)
            foreach (var s in series.Take(6))
                if (s?["values"] is JsonArray vals) d.Series.Add((s["name"]?.ToString() ?? "", vals.Select(Num).ToArray()));
        if (d.Series.Count == 0 && o["values"] is JsonArray only) d.Series.Add(("", only.Select(Num).ToArray()));
        int count = d.Series.Count == 0 ? 0 : d.Series.Max(s => s.Values.Length);
        if (count == 0 || d.Series.All(s => s.Values.All(v => v == null))) return null;
        while (d.Labels.Count < count) d.Labels.Add((d.Labels.Count + 1).ToString());
        if (d.Labels.Count > count) d.Labels = d.Labels.Take(count).ToList();
        return d;
    }
}

/// <summary>Draws a chart (line, area, bar, pie) to fit its width; the value under the pointer shows on it.</summary>
sealed class ChartView : FrameworkElement
{
    readonly ChartData _d;
    readonly Color[] _palette;
    readonly Typeface _face;
    readonly Color _text;
    int _hover = -1;

    static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal), typeof(double), typeof(ChartView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    public ChartView(ChartData data, Color[] palette, FontFamily font, Color text, bool eco)
    {
        _d = data;
        _palette = palette;
        _face = new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _text = text;
        Cursor = Cursors.Cross;
        if (!eco)
        {
            Reveal = 0;
            Loaded += (_, _) =>
            {
                var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                BeginAnimation(RevealProperty, a);
            };
        }
        MouseMove += (_, e) => { int i = IndexAt(e.GetPosition(this)); if (i != _hover) { _hover = i; InvalidateVisual(); } };
        MouseLeave += (_, _) => { _hover = -1; InvalidateVisual(); };
    }

    const double Left = 38, Right = 10, Bottom = 22;
    double Top => (_d.Title.Length > 0 ? 18 : 2) + (_d.Series.Count > 1 && _d.Type != "pie" ? 16 : 0) + (_d.Type == "pie" ? 2 : 8);
    int Count => _d.Labels.Count;

    int IndexAt(Point p)
    {
        if (_d.Type == "pie" || Count == 0) return -1;
        double w = ActualWidth - Left - Right;
        if (p.X < Left - 6 || p.X > ActualWidth - Right + 6) return -1;
        return _d.Type == "bar"
            ? Math.Clamp((int)((p.X - Left) / (w / Count)), 0, Count - 1)
            : Math.Clamp((int)Math.Round((p.X - Left) / (Count > 1 ? w / (Count - 1) : w)), 0, Count - 1);
    }

    FormattedText Text(string s, double size, Color c, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? new Typeface(_face.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : _face, size,
            new SolidColorBrush(c), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    static Color Fade(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static string Short(double v)
    {
        double a = Math.Abs(v);
        return a >= 1e9 ? (v / 1e9).ToString("0.#", CultureInfo.CurrentCulture) + "B" : a >= 1e6 ? (v / 1e6).ToString("0.#", CultureInfo.CurrentCulture) + "M"
            : a >= 1e4 ? (v / 1e3).ToString("0.#", CultureInfo.CurrentCulture) + "k" : v.ToString(a < 10 && v % 1 != 0 ? "0.##" : "0.#", CultureInfo.CurrentCulture);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight;
        if (W < 60 || H < 60) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H)); // the whole area feels the pointer
        double y0 = 0;
        if (_d.Title.Length > 0) { var t = Text(_d.Title, 11.5, _text, bold: true); t.MaxTextWidth = W - 8; t.MaxLineCount = 1; t.Trimming = TextTrimming.CharacterEllipsis; dc.DrawText(t, new Point(2, 0)); y0 = 18; }
        // the legend
        if (_d.Series.Count > 1 && _d.Type != "pie") // a pie names its parts beside it
        {
            double x = 2;
            var names = _d.Series.Select(s => s.Name).ToList();
            for (int i = 0; i < names.Count && x < W - 30; i++)
            {
                var c = _palette[i % _palette.Length];
                dc.DrawRoundedRectangle(new SolidColorBrush(c), null, new Rect(x, y0 + 4, 8, 8), 2, 2);
                var t = Text(names[i], 10, Fade(_text, 0xC8));
                dc.DrawText(t, new Point(x + 11, y0 + 1));
                x += 11 + t.WidthIncludingTrailingWhitespace + 10;
            }
        }
        if (_d.Type == "pie") { Pie(dc, W, H); return; }

        var all = _d.Series.SelectMany(s => s.Values).Where(v => v != null).Select(v => v!.Value).ToList();
        double lo = all.Min(), hi = all.Max();
        if (_d.Type == "bar") { lo = Math.Min(0, lo); hi = Math.Max(0, hi); }
        if (hi - lo < 1e-9) { hi += 1; lo -= 1; }
        double step = Nice((hi - lo) / 4);
        lo = Math.Floor(lo / step) * step;
        hi = Math.Ceiling(hi / step) * step;
        double top = Top, plotH = H - top - Bottom, plotW = W - Left - Right;
        double Y(double v) => top + plotH - (v - lo) / (hi - lo) * plotH;

        // the grid and the scale
        var gridPen = new Pen(new SolidColorBrush(Fade(_text, 0x1A)), 1);
        for (double v = lo; v <= hi + step / 2; v += step)
        {
            double y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(Left, y), new Point(W - Right, y));
            var t = Text(Short(v) + (_d.Unit.Length is > 0 and <= 3 ? _d.Unit : ""), 9, Fade(_text, 0x90));
            dc.DrawText(t, new Point(Left - 5 - t.Width, y - t.Height / 2));
        }
        // the labels along the bottom (some skipped when they'd overlap)
        double slot = _d.Type == "bar" ? plotW / Count : (Count > 1 ? plotW / (Count - 1) : plotW);
        double X(int i) => _d.Type == "bar" ? Left + slot * (i + 0.5) : Left + (Count > 1 ? slot * i : plotW / 2);
        double widest = _d.Labels.Max(l => Text(l, 9, _text).Width) + 6;
        int every = Math.Max(1, (int)Math.Ceiling(widest / Math.Max(1, slot)));
        for (int i = 0; i < Count; i += every)
        {
            var t = Text(_d.Labels[i], 9, Fade(_text, 0xA8));
            double x = Math.Clamp(X(i) - t.Width / 2, 0, W - t.Width);
            dc.DrawText(t, new Point(x, H - Bottom + 5));
        }

        // reveal: drawn from the left as it comes in
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, Left + plotW * Reveal + 4, H)));
        if (_d.Type == "bar")
        {
            int n = _d.Series.Count;
            double group = slot * 0.72, bw = group / n;
            for (int s = 0; s < n; s++)
            {
                var c = _palette[s % _palette.Length];
                var fill = new LinearGradientBrush(c, Fade(c, 0x90), 90);
                for (int i = 0; i < Count; i++)
                {
                    if (i >= _d.Series[s].Values.Length || _d.Series[s].Values[i] is not { } v) continue;
                    double x = X(i) - group / 2 + s * bw, yv = Y(v), yz = Y(Math.Max(lo, Math.Min(0, hi)));
                    var r = new Rect(x + 1, Math.Min(yv, yz), Math.Max(1, bw - 2), Math.Max(1, Math.Abs(yz - yv)));
                    dc.DrawRoundedRectangle(i == _hover ? new SolidColorBrush(Lighter(c)) : fill, null, r, 2, 2);
                }
            }
        }
        else
        {
            for (int s = 0; s < _d.Series.Count; s++)
            {
                var c = _palette[s % _palette.Length];
                var pts = new List<Point>();
                for (int i = 0; i < Count; i++) if (i < _d.Series[s].Values.Length && _d.Series[s].Values[i] is { } v) pts.Add(new Point(X(i), Y(v)));
                if (pts.Count == 0) continue;
                var line = new StreamGeometry();
                using (var g = line.Open()) { g.BeginFigure(pts[0], false, false); g.PolyLineTo(pts.Skip(1).ToList(), true, true); }
                if (_d.Type == "area" || _d.Series.Count == 1)
                {
                    var area = new StreamGeometry();
                    using (var g = area.Open())
                    {
                        g.BeginFigure(new Point(pts[0].X, top + plotH), true, true);
                        g.PolyLineTo(pts.Append(new Point(pts[^1].X, top + plotH)).ToList(), false, true);
                    }
                    dc.DrawGeometry(new LinearGradientBrush(Fade(c, _d.Type == "area" ? (byte)0x60 : (byte)0x38), Fade(c, 0x00), 90), null, area);
                }
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(c), 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
                foreach (var p in pts) dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1C)), new Pen(new SolidColorBrush(c), 1.6), p, 2.6, 2.6);
            }
        }
        dc.Pop();

        // the values under the pointer
        if (_hover >= 0 && _hover < Count)
        {
            double x = X(_hover);
            if (_d.Type != "bar") dc.DrawLine(new Pen(new SolidColorBrush(Fade(_text, 0x50)), 1) { DashStyle = DashStyles.Dash }, new Point(x, top), new Point(x, top + plotH));
            var lines = new List<FormattedText> { Text(_d.Labels[_hover], 10, _text, bold: true) };
            for (int s = 0; s < _d.Series.Count; s++)
                if (_hover < _d.Series[s].Values.Length && _d.Series[s].Values[_hover] is { } v)
                    lines.Add(Text($"{(_d.Series[s].Name.Length > 0 ? _d.Series[s].Name + ": " : "")}{v.ToString("0.##", CultureInfo.CurrentCulture)}{(_d.Unit.Length > 0 ? " " + _d.Unit : "")}", 10, _palette[s % _palette.Length]));
            double bw = lines.Max(l => l.Width) + 14, bh = lines.Sum(l => l.Height) + 8;
            double bx = x + 10 + bw > W ? x - 10 - bw : x + 10, by = top + 4;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xEE, 0x16, 0x16, 0x1A)), new Pen(new SolidColorBrush(Fade(_text, 0x30)), 1), new Rect(bx, by, bw, bh), 5, 5);
            double yy = by + 4;
            foreach (var l in lines) { dc.DrawText(l, new Point(bx + 7, yy)); yy += l.Height; }
            if (_d.Type != "bar")
                for (int s = 0; s < _d.Series.Count; s++)
                    if (_hover < _d.Series[s].Values.Length && _d.Series[s].Values[_hover] is { } v)
                        dc.DrawEllipse(new SolidColorBrush(_palette[s % _palette.Length]), null, new Point(x, Y(v)), 4, 4);
        }
    }

    static Color Lighter(Color c) => Color.FromRgb((byte)Math.Min(255, c.R + 40), (byte)Math.Min(255, c.G + 40), (byte)Math.Min(255, c.B + 40));

    void Pie(DrawingContext dc, double W, double H)
    {
        var vals = _d.Series[0].Values.Select(v => Math.Max(0, v ?? 0)).ToList();
        double total = vals.Sum();
        if (total <= 0) return;
        double top = Top, size = Math.Min(H - top - 4, W * 0.5), r = size / 2;
        var center = new Point(r + 8, top + r + 2);
        double angle = -90, sweepAll = 360 * Reveal;
        double lx = center.X + r + 18, ly = top + 4;
        for (int i = 0; i < vals.Count; i++)
        {
            double sweep = vals[i] / total * sweepAll;
            var c = _palette[i % _palette.Length];
            if (sweep > 0.01)
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    Point P(double a) => new(center.X + r * Math.Cos(a * Math.PI / 180), center.Y + r * Math.Sin(a * Math.PI / 180));
                    g.BeginFigure(center, true, true);
                    g.LineTo(P(angle), true, true);
                    g.ArcTo(P(angle + Math.Min(sweep, 359.99)), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, true);
                }
                // a ring, not a disc: a real hole in the middle (whatever the theme's background)
                var ring = Geometry.Combine(geo, new EllipseGeometry(center, r * 0.52, r * 0.52), GeometryCombineMode.Exclude, null);
                dc.DrawGeometry(new SolidColorBrush(c), null, ring);
            }
            angle += sweep;
            // the share of each part, beside it
            if (ly < H - 14)
            {
                var t = Text($"{_d.Labels[i]}  {vals[i] / total * 100:0.#}%", 10, Fade(_text, 0xD8));
                dc.DrawRoundedRectangle(new SolidColorBrush(c), null, new Rect(lx, ly + 3, 8, 8), 2, 2);
                dc.DrawText(t, new Point(lx + 12, ly));
                ly += 15;
            }
        }
    }

    static double Nice(double raw)
    {
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double f = raw / mag;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * mag;
    }
}
