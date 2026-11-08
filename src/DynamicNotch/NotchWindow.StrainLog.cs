using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The strain log (Cyberpunk: the Cyberpsychosis log), on the Tools page. Every strain warning is kept with what led
/// to it (Services/StrainLog): the readings then and over the minute before, the apps doing the most CPU, GPU and
/// memory work, the power state and the app in front, and the likely causes in plain words. A small button in the
/// focus card's corner opens it (a red dot while there's something new); a warning opens to its full story, and the
/// whole log can be opened as a text report or cleared.
/// </summary>
public partial class NotchWindow
{
    readonly Queue<StrainSample> _strainHistory = new();
    Border? _logPanel;
    StackPanel? _logBody;
    TextBlock? _logTitle;
    Button? _logBack, _logBtn;
    Ellipse? _logDot;
    bool _logClearArmed;

    /// <summary>Keeps the last minute of readings (12 × 5 s).</summary>
    void RememberStrain(StrainService.Reading r)
    {
        _strainHistory.Enqueue(new StrainSample(DateTime.UtcNow, r.Cpu, r.GpuKnown ? r.Gpu : null, r.Ram, r.GpuTemp, r.CpuTemp));
        while (_strainHistory.Count > 12) _strainHistory.Dequeue();
    }

    async Task LogStrainAsync(StrainTarget t, double value, int seconds)
    {
        try
        {
            var before = _strainHistory.ToList();
            var now = before.Count > 0 ? before[^1] : new StrainSample(DateTime.UtcNow, 0, 0, 0, null, null);
            var gpuByPid = _strain?.GpuByProcess ?? new Dictionary<uint, double>();
            string? front = ForegroundName();
            bool? plugged = _power.HasBattery ? _power.PluggedIn : null;
            int? battery = _power.HasBattery ? _power.Percent : null;
            string mode = _power.Mode.ToString();
            double line = t.Line(_settings);
            var (cpu, gpu, mem) = await Task.Run(() => StrainLog.TopApps(gpuByPid));
            var causes = StrainLog.Explain(t.Key, value, line, seconds, now, before, cpu, gpu, mem, plugged, mode);
            long gb = (long)Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0); // the PC's memory
            var e = new StrainEvent(DateTime.UtcNow, t.Key, t.Name, t.Unit, Math.Round(value), line, seconds, now, before,
                cpu, gpu, mem, front, plugged, battery, mode, StrainLog.CpuName(), Environment.ProcessorCount, gb, causes);
            StrainLog.Add(e);
            AppLog.Write($"strain log: {t.Key} {value:0}{t.Unit} saved ({causes.Count} cause(s))");
            RefreshLogDot();
            if (_logPanel?.Visibility == Visibility.Visible && _logBack?.Visibility != Visibility.Visible) RenderLogList();
        }
        catch (Exception ex) { AppLog.Write("strain log: " + ex.Message); }
    }

    static string? ForegroundName()
    {
        try
        {
            Interop.Native.GetWindowThreadProcessId(Interop.Native.GetForegroundWindow(), out uint pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    string LogName => Loc.T(_cyber ? "Cyberpsychosis log" : "Strain log");

    // ───────────── the button and the panel ─────────────

    void WireStrainLog()
    {
        if (TimerFace.Parent is not Grid card || card.Children.OfType<StackPanel>().FirstOrDefault() is not StackPanel tabs) return;
        // the focus rounds move under the time, the log's button takes the corner
        if (RoundDots.Parent is Panel old) old.Children.Remove(RoundDots);
        RoundDots.HorizontalAlignment = HorizontalAlignment.Center;
        RoundDots.Margin = new Thickness(0, 4, 0, 0);
        if (TimerFace.Children.OfType<StackPanel>().FirstOrDefault() is StackPanel face) face.Children.Add(RoundDots);

        var glyph = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        _logDot = new Ellipse { Width = 6, Height = 6, Fill = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -3, 0), Visibility = Visibility.Collapsed };
        var content = new Grid();
        content.Children.Add(glyph);
        content.Children.Add(_logDot);
        _logBtn = new Button { Style = (Style)FindResource("Capsule"), Height = 22, Width = 30, Padding = new Thickness(0), Content = content, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        _logBtn.Click += (_, _) => OpenStrainLog();
        card.Children.Add(_logBtn);
        RefreshLogDot();
    }

    void RefreshLogDot()
    {
        if (_logDot == null || _logBtn == null) return;
        int n = StrainLog.Unseen;
        _logDot.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        _logBtn.ToolTip = n > 0 ? $"{LogName} · {string.Format(Loc.T("{0} new"), n)}" : LogName;
    }

    void BuildLogPanel()
    {
        if (_logPanel != null) return;
        if (ActionsGrid.Parent is not Grid inner || inner.Parent is not Grid page) return;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        root.RowDefinitions.Add(new RowDefinition());

        var head = new Grid { Margin = new Thickness(6, 0, 4, 0) };
        _logBack = new Button { Style = (Style)FindResource("CircleButton"), Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed,
            Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11 } };
        _logBack.Click += (_, _) => RenderLogList();
        _logTitle = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(_logBack);
        titleRow.Children.Add(_logTitle);
        head.Children.Add(titleRow);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button Icon(string g, string tip, Action act)
        {
            var b = new Button { Style = (Style)FindResource("CircleButton"), Width = 24, Height = 24, Margin = new Thickness(2, 0, 0, 0), ToolTip = tip,
                Content = new TextBlock { Text = g, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11 } };
            b.Click += (_, _) => act();
            return b;
        }
        actions.Children.Add(Icon("", Loc.T("Open the full report"), () =>
        {
            string path = StrainLog.WriteReport();
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { AppLog.Write("strain report: " + ex.Message); }
            Collapse();
        }));
        var clear = Icon("", Loc.T("Clear the log"), () => { });
        clear.Click += (_, _) =>
        {
            if (!_logClearArmed) { _logClearArmed = true; clear.ToolTip = Loc.T("Click again to clear"); Tremble(((SolidColorBrush)Red).Color); After(2500, () => { _logClearArmed = false; clear.ToolTip = Loc.T("Clear the log"); }); return; }
            _logClearArmed = false;
            StrainLog.Clear();
            RefreshLogDot();
            RenderLogList();
        };
        actions.Children.Add(clear);
        actions.Children.Add(Icon("", Loc.T("Close"), CloseStrainLog));
        head.Children.Add(actions);
        root.Children.Add(head);

        _logBody = new StackPanel { Margin = new Thickness(4, 0, 4, 10) };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, PanningMode = PanningMode.VerticalOnly, Content = _logBody };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // a solid panel over the whole page: nothing of the tiles underneath shows through
        _logPanel = new Border
        {
            Margin = new Thickness(16, 0, 16, 4), CornerRadius = new CornerRadius(18), Padding = new Thickness(6, 4, 6, 0),
            Background = Frozen(Color.FromArgb(0xF7, 0x12, 0x13, 0x17)), BorderBrush = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Child = root, Visibility = Visibility.Collapsed, Opacity = 0,
        };
        Panel.SetZIndex(_logPanel, 20);
        page.Children.Add(_logPanel);
    }

    void OpenStrainLog()
    {
        BuildLogPanel();
        if (_logPanel == null) return;
        // the theme's own plate: the Cyberpunk night with a red rim, the glass themes their dark glass
        _logPanel.Background = Frozen(_cyber ? Color.FromArgb(0xF7, 0x03, 0x08, 0x0C) : Color.FromArgb(0xF7, 0x12, 0x13, 0x17));
        _logPanel.BorderBrush = Frozen(_cyber ? Color.FromArgb(0x90, 0xFF, 0x2A, 0x4F) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        _logPanel.CornerRadius = new CornerRadius(_cyber ? 0 : 18);
        RenderLogList();
        _logPanel.Visibility = Visibility.Visible;
        Fade(_logPanel, 1, 160);
        StrainLog.MarkSeen();
        RefreshLogDot();
        CyberSkinSoon();
        if (_cyber) CyberGlitch(strong: false, tint: CyRed);
    }

    void CloseStrainLog()
    {
        if (_logPanel == null || _logPanel.Visibility != Visibility.Visible) return;
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        a.Completed += (_, _) => { if (_logPanel.Opacity < 0.05) _logPanel.Visibility = Visibility.Collapsed; };
        _logPanel.BeginAnimation(OpacityProperty, a);
    }

    static Color KindColor(string key) => key switch
    {
        "cpu" => Color.FromRgb(0x0A, 0x84, 0xFF), "gpu" => Color.FromRgb(0x30, 0xD1, 0x58), "ram" => Color.FromRgb(0xBF, 0x5A, 0xF2),
        _ => Color.FromRgb(0xFF, 0x45, 0x3A),
    };

    static string KindGlyph(string key) => key switch { "cpu" => "", "gpu" => "", "ram" => "", _ => "" };

    string When(DateTime utc)
    {
        var t = utc.ToLocalTime();
        var day = t.Date == DateTime.Today ? Loc.T("Today") : t.Date == DateTime.Today.AddDays(-1) ? Loc.T("Yesterday") : t.ToString("ddd d MMM");
        return $"{day} · {t:HH:mm}";
    }

    void RenderLogList()
    {
        if (_logBody == null || _logTitle == null || _logBack == null) return;
        _logBack.Visibility = Visibility.Collapsed;
        _logTitle.Text = LogName;
        _logBody.Children.Clear();
        var events = StrainLog.Events;
        if (events.Count == 0)
        {
            _logBody.Children.Add(new TextBlock
            {
                Text = Loc.T(_cyber
                    ? "No cyberpsychosis episodes yet. When one hits, everything that led to it is kept here: the readings, the minute before, the apps behind it."
                    : "Nothing yet. When a strain warning comes, everything that led to it is kept here: the readings, the minute before, the apps behind it."),
                TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = DimBrush, Margin = new Thickness(6, 8, 6, 0),
            });
            if (!_settings.StrainWarnings)
                _logBody.Children.Add(new TextBlock { Text = Loc.T("Strain warnings are off: turn them on in Preferences → Alerts."), TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Orange, Margin = new Thickness(6, 8, 6, 0) });
            return;
        }
        foreach (var e in events)
        {
            var c = KindColor(e.Key);
            var row = new Grid { Margin = new Thickness(0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(_cyber ? 0 : 7), Background = Frozen(Color.FromArgb(0x30, c.R, c.G, c.B)),
                Child = new TextBlock { Text = KindGlyph(e.Key), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Frozen(c), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = $"{Loc.T(e.Name)} {e.Value:0}{e.Unit} · {string.Format(Loc.T("{0} s"), e.Seconds)}", FontSize = 12, FontWeight = FontWeights.SemiBold });
            string lead = (e.Key is "gpu" or "gputemp" ? e.TopGpu : e.Key == "ram" ? e.TopMemory : e.TopCpu).FirstOrDefault()?.Name ?? e.Foreground ?? "";
            text.Children.Add(new TextBlock { Text = When(e.At) + (lead.Length > 0 ? " · " + lead : ""), FontSize = 10.5, Foreground = DimBrush, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var chev = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
            Grid.SetColumn(chev, 2);
            row.Children.Add(chev);
            var btn = new Button { Style = (Style)FindResource("RowButton"), Content = row, Height = 38, Margin = new Thickness(0, 1, 0, 1) };
            var ev = e;
            btn.Click += (_, _) => RenderLogDetail(ev);
            _logBody.Children.Add(btn);
        }
    }

    void RenderLogDetail(StrainEvent e)
    {
        if (_logBody == null || _logTitle == null || _logBack == null) return;
        _logBack.Visibility = Visibility.Visible;
        _logTitle.Text = When(e.At);
        _logBody.Children.Clear();
        var c = KindColor(e.Key);
        var hot = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));

        _logBody.Children.Add(new TextBlock
        {
            Text = $"{Loc.T(e.Name)} {e.Value:0}{e.Unit} {Loc.T("for")} {e.Seconds} s",
            FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Frozen(c), Margin = new Thickness(6, 2, 6, 0),
        });
        _logBody.Children.Add(new TextBlock { Text = string.Format(Loc.T("Your line: {0}"), $"{e.Line:0}{e.Unit}") + $" · {e.At.ToLocalTime():HH:mm:ss}", FontSize = 10.5, Foreground = DimBrush, Margin = new Thickness(6, 0, 6, 6) });

        Section(Loc.T("What led to it"));
        foreach (var cause in e.Causes)
            _logBody.Children.Add(new TextBlock { Text = "•  " + cause, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(8, 1, 6, 1) });

        Section(Loc.T("At that moment"));
        var stats = new WrapPanel { Margin = new Thickness(6, 0, 0, 0) };
        void Stat(string name, double? v, string unit, string key)
        {
            if (v is not { } value) return;
            bool over = key == e.Key;
            stats.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(_cyber ? 0 : 9), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 5, 5),
                Background = over ? Frozen(Color.FromArgb(0x38, 0xFF, 0x45, 0x3A)) : Frozen(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock { Text = $"{name} {value:0}{unit}", FontSize = 11, FontWeight = over ? FontWeights.SemiBold : FontWeights.Normal, Foreground = over ? hot : Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)) },
            });
        }
        var n = e.Now;
        Stat("CPU", n.Cpu, "%", "cpu");
        Stat("GPU", n.Gpu, "%", "gpu");
        Stat(Loc.T("Memory"), n.Ram, "%", "ram");
        Stat("CPU", n.CpuTemp, "°C", "cputemp");
        Stat("GPU", n.GpuTemp, "°C", "gputemp");
        _logBody.Children.Add(stats);

        // the minute before: one bar every 5 s, the line drawn across
        var values = e.Before.Select(s => StrainLog.Pick(s, e.Key)).ToList();
        if (values.Count >= 2)
        {
            Section(Loc.T("The minute before"));
            double max = Math.Max(e.Line * 1.1, values.Max(v => v ?? 0));
            var chart = new Grid { Height = 42, Margin = new Thickness(8, 2, 8, 2) };
            var bars = new UniformGrid { Rows = 1, Columns = values.Count };
            foreach (var v in values)
            {
                double share = v is { } x ? x / max : 0;
                bars.Children.Add(new Border
                {
                    Height = Math.Max(2, 42 * share), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1.5, 0, 1.5, 0),
                    CornerRadius = new CornerRadius(_cyber ? 0 : 2), Background = v >= e.Line ? hot : Frozen(Color.FromArgb(0xB0, c.R, c.G, c.B)),
                });
            }
            chart.Children.Add(bars);
            chart.Children.Add(new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 42 * e.Line / max), Fill = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)) }); // your line
            _logBody.Children.Add(chart);
            _logBody.Children.Add(new TextBlock { Text = string.Join("  →  ", new[] { values.First(), values.Last() }.Select(v => v is { } x ? $"{x:0}{e.Unit}" : "–")), FontSize = 10.5, Foreground = DimBrush, Margin = new Thickness(8, 0, 6, 0) });
        }

        void Apps(string title, List<ProcUse> list, Func<ProcUse, double> share, Func<ProcUse, string> label)
        {
            if (list.Count == 0) return;
            Section(title);
            double top = Math.Max(1, list.Max(share));
            foreach (var a in list.Take(5))
            {
                var g = new Grid { Margin = new Thickness(8, 1, 8, 1) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
                g.Children.Add(new TextBlock { Text = a.Name + (a.Count > 1 ? $" ×{a.Count}" : ""), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
                // the bar: its share of the biggest, as two star columns (no measuring needed)
                double k = Math.Clamp(share(a) / top, 0.02, 1);
                var track = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(k, GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - k + 0.0001, GridUnitType.Star) });
                track.Children.Add(new Border { CornerRadius = new CornerRadius(_cyber ? 0 : 2.5), Background = Frozen(c) });
                Grid.SetColumn(track, 1);
                g.Children.Add(track);
                var v = new TextBlock { Text = label(a), FontSize = 11, Foreground = DimBrush, TextAlignment = TextAlignment.Right };
                Grid.SetColumn(v, 2);
                g.Children.Add(v);
                _logBody.Children.Add(g);
            }
        }
        // the kind that crossed its line first, then the others
        var order = e.Key switch { "gpu" or "gputemp" => new[] { "gpu", "cpu", "ram" }, "ram" => new[] { "ram", "cpu", "gpu" }, _ => new[] { "cpu", "gpu", "ram" } };
        foreach (var k in order)
        {
            if (k == "cpu") Apps(Loc.T("Biggest CPU users"), e.TopCpu, a => a.Cpu, a => $"{a.Cpu:0.0}%");
            if (k == "gpu") Apps(Loc.T("Biggest GPU users"), e.TopGpu, a => a.Gpu, a => $"{a.Gpu:0.0}%");
            if (k == "ram") Apps(Loc.T("Biggest memory users"), e.TopMemory, a => a.MemoryMb, a => a.MemoryMb >= 1024 ? $"{a.MemoryMb / 1024.0:0.0} GB" : $"{a.MemoryMb} MB");
        }

        Section(Loc.T("Context"));
        string power = e.PluggedIn == null ? Loc.T("Desktop power") : e.PluggedIn == true ? Loc.T("Plugged in") : Loc.T("On battery");
        foreach (var line in new[]
        {
            $"{power}{(e.Battery is { } b ? $" · {b}%" : "")} · {Loc.T("Power mode")}: {Loc.T(e.PowerMode)}",
            e.Foreground is { } f ? $"{Loc.T("In front")}: {f}" : null,
            $"{e.CpuName} · {e.Cores} {Loc.T("threads")}{(e.MemoryGb > 0 ? $" · {e.MemoryGb} GB" : "")}",
        })
            if (line != null) _logBody.Children.Add(new TextBlock { Text = line, FontSize = 11, Foreground = DimBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 1, 6, 1) });

        void Section(string title) => _logBody!.Children.Add(new TextBlock
        {
            Text = _cyber ? "// " + title.ToUpperInvariant() : title, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
            Foreground = _cyber ? CyYellowBrush : DimBrush, Margin = new Thickness(6, 9, 6, 3),
        });
    }
}
