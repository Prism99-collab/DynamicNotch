using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// How the search's results are drawn, made to stay smooth while you type:
///  • a line that's still there after a key is the same line (kept, not rebuilt): it glides to its new place;
///  • only lines that are new come in (Neon: they flicker on like a HUD, sliding in from the left);
///  • one highlight bar slides from line to line (Neon: a cyan wash with a yellow tick and a scan sweep);
///  • icons and file details are read off the UI thread and fade in when ready;
///  • the search itself runs once per pause between keys, after the letter is on screen.
/// </summary>
public partial class NotchWindow
{
    sealed class RowParts
    {
        public required Border Plate;
        public required TextBlock Title;
        public required TextBlock Detail;
        public required string Look;
        public required Border Slot;
        public required TextBlock SlotText;
        public Hit? Hit;
        public string? Shown; // the highlight the title was drawn with
    }

    readonly Dictionary<string, RowParts> _rows = new();
    readonly Dictionary<string, TextBlock> _labels = new();
    readonly Dictionary<UIElement, string> _rowKey = new();
    Border? _selBar;
    Rectangle? _selScan;
    System.Windows.Shapes.Path? _selBrackets;
    Grid? _launchLayer;

    static string RowKey(Hit h) => h.Key ?? "t|" + h.Title + "|" + h.Detail;
    static string LookOf(Hit h) => $"{h.Big}|{h.Glyph}|{h.IconPath}|{h.Swatch}|{h.Preview}";

    // ───────────────────────────── the list ─────────────────────────────

    void RenderHits(bool animate = true)
    {
        if (_launchList == null) return;
        // where every line is now, to glide it from there (positions are worked out, never asked of the layout:
        // asking makes WPF lay out the whole notch on every key)
        var before = _rows.Count == 0 ? new Dictionary<string, double>() : new Dictionary<string, double>(_keyY); // a fresh opening glides nothing

        bool labels = _actionsFor != null || _hits.Select(h => h.Section).Where(s => s != null).Distinct().Count() > 1;
        var next = new List<(UIElement El, string Key, bool Fresh)>();
        var usedRows = new HashSet<string>();
        var usedLabels = new HashSet<string>();
        if (_actionsFor != null) next.Add((Label("‹  " + Trim(_actionsFor.Title, 60), back: true, usedLabels), "lbl|back|" + _actionsFor.Title, !before.ContainsKey("lbl|back|" + _actionsFor.Title)));
        string? section = null;
        // only what fits (and a little more) is drawn now; the rest the moment you scroll or arrow down to it
        int upTo = _renderAll ? _hits.Count : Math.Min(_hits.Count, RenderFirst);
        for (int i = 0; i < upTo; i++)
        {
            var h = _hits[i];
            if (labels && _actionsFor == null && h.Section != null && h.Section != section)
            {
                string lk = "lbl|" + h.Section;
                next.Add((Label(h.Section, back: false, usedLabels), lk, !before.ContainsKey(lk)));
            }
            section = h.Section;
            string key = RowKey(h);
            if (!usedRows.Add(key)) key += "|" + i; // two lines that look alike stay two lines
            bool isFresh = !_rows.TryGetValue(key, out var parts) || parts.Look != LookOf(h) || !before.ContainsKey(key);
            if (parts == null || parts.Look != LookOf(h)) _rows[key] = parts = BuildRow(h);
            UpdateRow(parts, h, i);
            next.Add((parts.Plate, key, isFresh));
        }
        // lines that are gone are forgotten (nothing piles up while you type)
        foreach (var gone in _rows.Keys.Where(k => !usedRows.Contains(k) && !next.Any(n => n.Key == k)).ToList()) _rows.Remove(gone);
        foreach (var gone in _labels.Keys.Where(k => !usedLabels.Contains(k)).ToList()) _labels.Remove(gone);

        // the same lines in the same order: nothing to rebuild
        // only lines that come, go or move are touched (re-adding every line costs WPF more than the search itself)
        var wanted = new HashSet<UIElement>(next.Select(n => n.El));
        for (int i = _launchList.Children.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_launchList.Children[i])) _launchList.Children.RemoveAt(i);
        for (int i = 0; i < next.Count; i++)
        {
            var el = next[i].El;
            if (i < _launchList.Children.Count && ReferenceEquals(_launchList.Children[i], el)) continue;
            if (el is FrameworkElement { Parent: Panel } && _launchList.Children.Contains(el)) _launchList.Children.Remove(el);
            _launchList.Children.Insert(Math.Min(i, _launchList.Children.Count), el);
        }
        _rowKey.Clear();
        _keyY.Clear();
        _elY.Clear();
        double y = 0;
        foreach (var (el, key, _) in next)
        {
            double h = HeightOf(el);
            _rowKey[el] = key;
            _keyY[key] = y;
            _elY[el] = (y, h);
            y += h;
        }

        int fresh = 0;
        foreach (var (el, key, isNew) in next)
        {
            var tt = el.RenderTransform as TranslateTransform ?? new TranslateTransform();
            el.RenderTransform = tt;
            if (!isNew && before.TryGetValue(key, out double was))
            {
                // still here: from where it was to where it is
                double dy = was - _elY[el].Y;
                el.BeginAnimation(OpacityProperty, null);
                el.Opacity = 1;
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = 0;
                if (Math.Abs(dy) > 0.5)
                    tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = Out });
            }
            else if (animate || fresh < 12) ArriveRow(el, tt, fresh++);
        }
        if (animate) _launchScroll?.ScrollToTop();
        PaintSelection(glide: !animate);
    }

    static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };

    readonly Dictionary<string, double> _keyY = new();
    const int RenderFirst = 10;
    bool _renderAll;

    /// <summary>The lines past the first ten, drawn when they're about to be needed.</summary>
    void RenderRest()
    {
        if (_renderAll || _hits.Count <= RenderFirst) return;
        _renderAll = true;
        RenderHits(animate: false);
    }
    readonly Dictionary<UIElement, (double Y, double H)> _elY = new();

    /// <summary>A line's height is fixed (28, or 36 for a big one, plus 2); a label is measured once on its own.</summary>
    static double HeightOf(UIElement el)
    {
        if (el is Border { Child: Grid g } b) return g.Height + b.Margin.Top + b.Margin.Bottom;
        if (el is FrameworkElement fe)
        {
            if (fe.DesiredSize.Height <= 0) fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return fe.DesiredSize.Height;
        }
        return 0;
    }

    static string Trim(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;

    TextBlock Label(string text, bool back, HashSet<string> used)
    {
        string key = (back ? "back|" : "") + text + "|" + Skin.Key;
        used.Add(key);
        if (_labels.TryGetValue(key, out var t)) return t;
        t = new TextBlock
        {
            Text = _cyber ? text.ToUpperInvariant() : text, FontSize = 9.5, Foreground = Frozen(back ? Skin.Accent : Skin.Label),
            Margin = new Thickness(8, 7, 0, 3), FontWeight = back ? FontWeights.SemiBold : FontWeights.Normal,
            FontFamily = _cyber ? CyFont : UiText,
        };
        if (back) { t.Cursor = Cursors.Hand; t.MouseLeftButtonUp += (_, e) => { e.Handled = true; LeaveActions(); }; }
        return _labels[key] = t;
    }

    static readonly FontFamily UiText = new("Segoe UI Variable Text, Segoe UI");

    // ───────────────────────────── one line ─────────────────────────────

    RowParts BuildRow(Hit h)
    {
        var grid = new Grid { Height = h.Big ? 36 : 28 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

        FrameworkElement lead;
        if (h.Swatch != null)
            lead = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(_cyber ? 0 : 6), Background = Frozen((Color)ColorConverter.ConvertFromString(h.Swatch)),
                BorderBrush = Frozen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1) };
        else if (h.IconPath != null)
        {
            var box = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4) };
            IconInto(box, h.IconPath);
            if (h.Preview != null) ShowThumb(box, h.Preview); // a picture shows itself
            lead = box;
        }
        else
            lead = new TextBlock { Text = h.Glyph ?? "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, Foreground = Frozen(Skin.Glyph) };
        lead.HorizontalAlignment = HorizontalAlignment.Left;
        lead.VerticalAlignment = VerticalAlignment.Center;
        lead.Margin = new Thickness(6, 0, 0, 0);
        grid.Children.Add(lead);

        var title = new TextBlock
        {
            FontSize = h.Big ? 16 : 12.5, FontWeight = h.Big ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 8, 0),
            FontFamily = _cyber && h.Big ? CyFont : UiText, Foreground = _cyber && h.Big ? Frozen(CyYellow) : Brushes.White,
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var detail = new TextBlock { FontSize = 10, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), MaxWidth = h.Big ? 330 : 200, TextTrimming = TextTrimming.CharacterEllipsis };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (TypeTag(h) is { } tag) right.Children.Add(tag);
        right.Children.Add(detail);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        // the slot: ⋯ under the pointer (its second level), ↵ on the picked line (what Enter does)
        var slotText = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var slot = new Border
        {
            Width = 20, Height = 18, CornerRadius = new CornerRadius(_cyber ? 0 : 5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = slotText, Opacity = 0, Background = Brushes.Transparent,
        };
        Grid.SetColumn(slot, 3);
        grid.Children.Add(slot);

        var plate = new Border { Child = grid, Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        if (h.Entry is { Kind: LauncherIndex.Kind.File or LauncherIndex.Kind.Folder } fe && !fe.Target.StartsWith("shell:")) plate.ToolTip = fe.Target; // the whole path, on hover
        var parts = new RowParts { Plate = plate, Title = title, Detail = detail, Look = LookOf(h), Slot = slot, SlotText = slotText };
        // the line's place in the list changes as you type: the handlers read it from the plate
        plate.MouseEnter += (_, _) =>
        {
            _hovered = parts;
            if (plate.Tag is int i && i != _launchSel) { _launchSel = i; PaintSelection(scroll: false); }
            else PaintSlots();
        };
        plate.MouseLeave += (_, _) => { if (ReferenceEquals(_hovered, parts)) { _hovered = null; PaintSlots(); } };
        plate.MouseLeftButtonUp += (_, e) => { if (plate.Tag is int i) { e.Handled = true; Run(i, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); } };
        plate.MouseRightButtonUp += (_, e) => { if (plate.Tag is int i) { e.Handled = true; _launchSel = i; EnterActions(); } };
        slot.MouseLeftButtonUp += (_, e) =>
        {
            if (plate.Tag is not int i) return;
            e.Handled = true;
            _launchSel = i;
            if (parts.Hit?.Actions != null && _actionsFor == null) EnterActions(); else Run(i, reveal: false);
        };
        return parts;
    }

    /// <summary>A small tag with a file's type, in its family's colour (PDF red, Word blue, Excel green, pictures violet…).</summary>
    FrameworkElement? TypeTag(Hit h)
    {
        if (h.Entry is not { Kind: LauncherIndex.Kind.File } e || IsShortcut(e.Target)) return null;
        string ext = System.IO.Path.GetExtension(e.Target).TrimStart('.').ToLowerInvariant();
        if (ext.Length is 0 or > 5) return null;
        Color c = ext switch
        {
            "pdf" => Color.FromRgb(0xFF, 0x5A, 0x5F),
            "doc" or "docx" or "odt" or "rtf" => Color.FromRgb(0x4C, 0x8D, 0xFF),
            "xls" or "xlsx" or "csv" or "ods" => Color.FromRgb(0x2E, 0xCC, 0x71),
            "ppt" or "pptx" or "odp" or "key" => Color.FromRgb(0xFF, 0x8A, 0x3D),
            "zip" or "rar" or "7z" or "tar" or "gz" or "iso" => Color.FromRgb(0xB0, 0xB4, 0xBC),
            "png" or "jpg" or "jpeg" or "gif" or "webp" or "bmp" or "svg" or "heic" or "tif" or "tiff" or "ico" or "psd" => Color.FromRgb(0xC0, 0x84, 0xFC),
            "mp4" or "mkv" or "mov" or "avi" or "webm" or "wmv" => Color.FromRgb(0xFF, 0x6F, 0xB5),
            "mp3" or "wav" or "flac" or "m4a" or "ogg" or "aac" => Color.FromRgb(0x2D, 0xD4, 0xBF),
            "exe" or "msi" or "bat" or "cmd" or "ps1" => Color.FromRgb(0x9A, 0xA8, 0xBC),
            "txt" or "md" or "log" => Color.FromRgb(0xE2, 0xE4, 0xEA),
            "cs" or "js" or "ts" or "py" or "java" or "cpp" or "c" or "h" or "html" or "css" or "json" or "xml" or "go" or "rs" or "php" or "rb" or "xaml" or "sql" => Color.FromRgb(0xFA, 0xCC, 0x15),
            _ => Color.FromRgb(0x9A, 0x9A, 0xA6),
        };
        return new Border
        {
            CornerRadius = new CornerRadius(_cyber ? 0 : 3), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 6, 0), Height = 14, VerticalAlignment = VerticalAlignment.Center,
            Background = Frozen(Color.FromArgb(_cyber ? (byte)0x14 : (byte)0x2A, c.R, c.G, c.B)),
            BorderBrush = _cyber ? Frozen(Color.FromArgb(0xA0, c.R, c.G, c.B)) : null, BorderThickness = new Thickness(_cyber ? 1 : 0),
            Child = new TextBlock { Text = ext.ToUpperInvariant(), FontSize = 8.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(c), VerticalAlignment = VerticalAlignment.Center,
                FontFamily = _cyber ? CyFont : UiText },
        };
    }

    /// <summary>A kept line takes its new place in the list, its new highlight and its new detail (only what changed is touched).</summary>
    void UpdateRow(RowParts p, Hit h, int index)
    {
        p.Plate.Tag = index;
        if (!ReferenceEquals(p.Hit, h) && (p.Hit == null || p.Hit.Title != h.Title || p.Shown != h.Highlight))
        {
            p.Title.Inlines.Clear();
            p.Title.Text = "";
            FillTitle(p.Title, h);
            p.Shown = h.Highlight;
        }
        if (p.Detail.Text != h.Detail) p.Detail.Text = h.Detail;
        p.Hit = h;
    }

    // ───────────────────────────── the highlight bar ─────────────────────────────

    /// <summary>One bar that slides to the picked line (Neon: a cyan wash, a yellow tick, a scan sweeping across).</summary>
    void PaintSelection(bool scroll = true, bool glide = true)
    {
        if (_launchList == null || _selBar == null) return;
        var picked = _launchList.Children.OfType<Border>().FirstOrDefault(b => b.Tag is int i && i == _launchSel);
        if (picked == null) { _selBar.Visibility = Visibility.Collapsed; }
        else
        {
            var skin = Skin;
            _selBar.Background = skin.SelFill;
            _selBar.CornerRadius = new CornerRadius(skin.SelRadius);
            _selBar.BorderBrush = skin.SelEdge;
            _selBar.BorderThickness = skin.SelEdgeWidth;
            if (_selBrackets != null) _selBrackets.Visibility = _cyber ? Visibility.Visible : Visibility.Collapsed;
            var (y, h) = _elY.TryGetValue(picked, out var at) ? at : (VisualTreeHelper.GetOffset(picked).Y, picked.ActualHeight);
            var tt = (TranslateTransform)_selBar.RenderTransform;
            bool wasHidden = _selBar.Visibility != Visibility.Visible;
            _selBar.Visibility = Visibility.Visible;
            if (wasHidden || !glide && Math.Abs(tt.Y - y) > 200)
            {
                tt.BeginAnimation(TranslateTransform.YProperty, null);
                _selBar.BeginAnimation(HeightProperty, null);
                tt.Y = y;
                _selBar.Height = h;
            }
            else
            {
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(120)) { EasingFunction = Out });
                _selBar.BeginAnimation(HeightProperty, new DoubleAnimation(h, TimeSpan.FromMilliseconds(120)) { EasingFunction = Out });
            }
            if (skin.Scan != null && _selScan != null && !wasHidden)
            {
                // a thin scan sweeps across the bar when it lands
                var st = (ScaleTransform)_selScan.RenderTransform;
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = Out });
                _selScan.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(320)));
            }
            if (scroll)
            {
                // the line and, above it, its section's label
                if (_launchSel == 0) _launchScroll?.ScrollToTop();
                else picked.BringIntoView(new Rect(0, -22, Math.Max(1, picked.ActualWidth), Math.Max(1, picked.ActualHeight) + 22));
            }
        }
        ShowPathOnPicked(picked);
        PaintSlots();
        if (Skin.Arrive == Arrival.Rise && picked != null && _selBar.Visibility == Visibility.Visible)
            _selBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(260))); // a soft glow as it lands
        var hit = _launchSel < _hits.Count ? _hits[_launchSel] : null;
        if (hit == null) { SetFoot(""); return; }
        if (_actionsFor != null) { SetFoot("↵ run  ·  ← back"); return; }
        var tips = new List<string> { "↵ open" };
        if (hit.Reveal != null) tips.Add("Ctrl+↵ folder");
        if (hit.Actions != null) tips.Add("→ more");
        if (_ghost != null || hit.Complete != null) tips.Add("Tab complete");
        SetFoot(string.Join("  ·  ", tips));
    }

    /// <summary>The list's layer: the sliding bar behind the lines.</summary>
    Grid BuildListLayer()
    {
        _selScan = new Rectangle
        {
            Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0, IsHitTestVisible = false,
            Fill = Frozen(CyCyan), RenderTransform = new ScaleTransform(0, 1), RenderTransformOrigin = new Point(0, 0.5),
        };
        var inside = new Grid();
        inside.Children.Add(_selScan);
        _selBrackets = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L7,0 L7,5 M7,15 L7,20 L0,20"), Stretch = Stretch.None, Stroke = Frozen(CyCyan), StrokeThickness = 1.2,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0), IsHitTestVisible = false,
        };
        inside.Children.Add(_selBrackets);
        _selBar = new Border
        {
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            RenderTransform = new TranslateTransform(), Child = inside,
        };
        _launchLayer = new Grid();
        _launchLayer.Children.Add(_selBar);
        _launchLayer.Children.Add(_launchList!);
        return _launchLayer;
    }

    // ───────────────────────────── icons, off the UI thread ─────────────────────────────

    readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly BlockingCollection<(string Path, Action<ImageSource?> Done)> IconQueue = new();
    static Thread? _iconThread;

    /// <summary>The icon now if it's known; else a soft placeholder, and the icon fades in when it's read.</summary>
    void IconInto(Border box, string target)
    {
        if (_iconCache.TryGetValue(target, out var known)) { box.Background = IconBrush(known); return; }
        box.Background = Frozen(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        if (_iconThread == null)
        {
            // one quiet thread reads icons from the shell, one after another
            _iconThread = new Thread(() =>
            {
                foreach (var (path, done) in IconQueue.GetConsumingEnumerable())
                {
                    ImageSource? img = null;
                    try { img = IconHelper.ForPath(path); } catch { }
                    done(img);
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Launcher icons" };
            _iconThread.SetApartmentState(ApartmentState.STA);
            _iconThread.Start();
        }
        IconQueue.Add((target, img => Dispatcher.InvokeAsync(() =>
        {
            _iconCache[target] = img;
            if (img == null) return;
            box.Background = IconBrush(img);
            box.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(140)));
        })));
    }

    /// <summary>For the empty search's tiles: the same cache, the same quiet thread.</summary>
    ImageSource? LauncherIcon(string target)
    {
        if (_iconCache.TryGetValue(target, out var img)) return img;
        try { img = IconHelper.ForPath(target); } catch { img = null; }
        _iconCache[target] = img;
        return img;
    }

    // ───────────────────────────── typing ─────────────────────────────

    int _searchAsked, _searchDone;

    /// <summary>
    /// A key was typed: the letter shows at once, and the search runs right after (at a lower priority than input), once
    /// for a burst of keys. Enter, Tab and the arrows wait for it, so they always act on what's typed.
    /// </summary>
    void QueueSearch()
    {
        int v = ++_searchAsked;
        TypePulse();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { if (v == _searchAsked && _searchDone != v) Search(); });
    }

    void SearchNowIfBehind() { if (_searchDone != _searchAsked) Search(); }

    /// <summary>Neon: each key makes the underline and the prompt blink, like a terminal taking input.</summary>
    void TypePulse()
    {
        if (!_cyber) { _launchLine?.BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, 1, TimeSpan.FromMilliseconds(180))); return; }
        _launchLine?.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(170)));
        _launchPrompt?.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(130)));
    }

    Border? _launchLine;
    Border? _launchScanLine;

    /// <summary>Neon: as the search opens, a scan line runs down the panel.</summary>
    void ScanIn()
    {
        if (_launchScanLine == null || _launch == null) return;
        if (!_cyber) { _launchScanLine.Visibility = Visibility.Collapsed; return; }
        _launchScanLine.Visibility = Visibility.Visible;
        double h = Math.Max(120, _launch.ActualHeight > 0 ? _launch.ActualHeight : 260);
        var tt = (TranslateTransform)_launchScanLine.RenderTransform;
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, h - 4, TimeSpan.FromMilliseconds(320)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(380) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(30))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380))));
        _launchScanLine.BeginAnimation(OpacityProperty, fade);
    }

    readonly Dictionary<string, (DateTime When, long Size)> _fileFacts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A file's date and size, read once per search session (not on every key).</summary>
    (DateTime When, long Size) FileFacts(string path, bool folder)
    {
        if (_fileFacts.TryGetValue(path, out var f)) return f;
        f = (DateTime.MinValue, -1);
        try
        {
            if (folder) { var di = new DirectoryInfo(path); if (di.Exists) f = (di.LastWriteTime, -1); }
            else { var fi = new FileInfo(path); if (fi.Exists) f = (fi.LastWriteTime, fi.Length); }
        }
        catch { }
        return _fileFacts[path] = f;
    }

    // ───────────────────────────── the slot on the right: ⋯ and ↵ ─────────────────────────────

    RowParts? _hovered;

    /// <summary>
    /// Under the pointer, a line with a second level shows ⋯ (a click opens it, like → or a right-click); the picked
    /// line shows ↵, what Enter does. Only the lines whose slot changes are touched.
    /// </summary>
    void PaintSlots()
    {
        var skin = Skin;
        foreach (var p in _rows.Values)
        {
            bool picked = p.Plate.Tag is int i && i == _launchSel && _actionsFor == null;
            bool hover = ReferenceEquals(p, _hovered) && p.Hit?.Actions != null && _actionsFor == null;
            string want = hover ? "\uE712" : picked ? "\uE751" : "";
            if (p.SlotText.Text == want) continue;
            p.SlotText.Text = want;
            if (want.Length == 0) { p.Slot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120))); continue; }
            p.Slot.Background = hover ? Frozen(Color.FromArgb(0x30, skin.Accent.R, skin.Accent.G, skin.Accent.B)) : Brushes.Transparent;
            p.Slot.BorderBrush = hover ? null : Frozen(Color.FromArgb(0x50, skin.Label.R, skin.Label.G, skin.Label.B));
            p.Slot.BorderThickness = new Thickness(hover ? 0 : 1);
            p.SlotText.Foreground = Frozen(hover ? skin.Accent : skin.Label);
            p.Slot.ToolTip = hover ? "More  ·  → or right-click" : "Enter";
            p.Slot.Cursor = Cursors.Hand;
            p.Slot.BeginAnimation(OpacityProperty, new DoubleAnimation(hover ? 1 : 0.8, TimeSpan.FromMilliseconds(140)));
        }
    }

    // ───────────────────────────── where a file is ─────────────────────────────

    RowParts? _pathShown;

    /// <summary>The picked file or folder shows where it lives ("D:\Downloads V2\…\Project AI docs"); the others keep their short detail.</summary>
    void ShowPathOnPicked(Border? picked)
    {
        var p = picked == null ? null : _rows.Values.FirstOrDefault(r => ReferenceEquals(r.Plate, picked));
        if (_pathShown != null && !ReferenceEquals(_pathShown, p) && _pathShown.Hit != null) _pathShown.Detail.Text = _pathShown.Hit.Detail;
        _pathShown = null;
        if (_actionsFor != null || p?.Hit?.Entry is not { Kind: LauncherIndex.Kind.File or LauncherIndex.Kind.Folder } e || e.Target.StartsWith("shell:")) return;
        if (System.IO.Path.GetDirectoryName(e.Target.TrimEnd('\\')) is not { Length: > 0 } dir) return;
        p.Detail.Text = CompactPath(dir, 40);
        _pathShown = p;
    }

    /// <summary>A long folder path, its start and its end kept: "D:\Downloads V2\…\Project AI docs".</summary>
    static string CompactPath(string dir, int max)
    {
        if (dir.Length <= max) return dir;
        var parts = dir.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return "…" + dir[^(max - 1)..];
        string head = parts[0] + "\\", tail = parts[^1];
        for (int i = parts.Length - 2; i >= 1; i--)
        {
            string longer = parts[i] + "\\" + tail;
            if (head.Length + 2 + longer.Length > max) break;
            tail = longer;
        }
        string compact = head + "…\\" + tail;
        return compact.Length <= max ? compact : "…" + tail[^Math.Min(tail.Length, max - 1)..];
    }

    // ───────────────────────────── more below, more above ─────────────────────────────

    Border? _scrollThumb;

    /// <summary>A slim line on the right shows where you are in a long list; the edges fade where there's more to scroll.</summary>
    void AttachScrollHints(Grid body)
    {
        _scrollThumb = new Border
        {
            Width = 2, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, -9, 0), IsHitTestVisible = false, Opacity = 0, RenderTransform = new TranslateTransform(),
        };
        body.Children.Add(_scrollThumb);
        if (_launchScroll != null) _launchScroll.ScrollChanged += (_, _) => UpdateScrollHints();
    }

    bool _thumbShown;

    void UpdateScrollHints()
    {
        var sv = _launchScroll;
        if (sv == null || _scrollThumb == null) return;
        double extent = sv.ExtentHeight, view = sv.ViewportHeight, off = sv.VerticalOffset;
        bool scrolls = sv.Visibility == Visibility.Visible && extent > view + 1 && view > 20;
        if (!scrolls)
        {
            sv.OpacityMask = null;
            if (_thumbShown) { _thumbShown = false; _scrollThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150))); }
            return;
        }
        bool up = off > 1, down = off + view < extent - 1;
        double f = Math.Min(0.16, 16 / view);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(up ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, up ? f : 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, down ? 1 - f : 1));
        mask.GradientStops.Add(new GradientStop(down ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        sv.OpacityMask = mask;
        double h = Math.Max(16, view * view / extent);
        double y = extent > view ? off / (extent - view) * (view - h) : 0;
        _scrollThumb.Height = h;
        ((TranslateTransform)_scrollThumb.RenderTransform).Y = y;
        if (!_thumbShown) { _thumbShown = true; _scrollThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(180))); }
    }
}
