using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The search, shown the way a good launcher shows things:
///  • a preview on the right for the picked line: a picture, a video's frame or a document's first page (Windows' own
///    thumbnails, so it works for any kind of file Explorer can show), a text file's first lines, a folder's size, an
///    app's big icon, with what it is, how big, how old and where;
///  • files in the list wear their real thumbnail, not a generic icon;
///  • the best match sits on top as a bigger card;
///  • the notch fits the results: a short list makes it short, a long one makes it tall (it springs, never jumps);
///  • the keys you can press are shown as keys, and change with what's picked.
/// Thumbnails and folder sizes are read off the screen's thread, one at a time, and only for the line you're on.
/// </summary>
public partial class NotchWindow
{
    Border? _pane, _paneMedia, _paneSwatch;
    Image? _paneImg, _paneIcon;
    TextBlock? _paneGlyph, _paneText, _paneKind, _paneTitle, _paneFacts, _panePlace;
    StackPanel? _paneInfo;
    bool _paneOn;
    string? _paneFor;
    int _paneGen;
    readonly Dictionary<string, ImageSource?> _bigThumbs = new(StringComparer.OrdinalIgnoreCase);

    StackPanel? _launchKeys;
    string? _keysShown;
    readonly TranslateTransform _footShift = new();
    FrameworkElement? _launchLineRow, _launchFootRow;
    double _launchWantH;

    const double PaneWidth = 148, PaneNeed = 160;

    static readonly HashSet<string> ThumbExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".heic", ".heif", ".tif", ".tiff", ".avif", ".jxl", ".psd", ".svg",
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v", ".pdf", ".docx", ".xlsx", ".pptx", ".odt", ".epub",
    };

    static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".csv", ".ini", ".cfg", ".json", ".xml", ".yml", ".yaml", ".toml", ".cs", ".js", ".ts", ".py",
        ".java", ".cpp", ".c", ".h", ".html", ".css", ".go", ".rs", ".php", ".rb", ".xaml", ".sql", ".ps1", ".bat", ".cmd", ".sh",
    };

    static bool Previewable(Hit h) => h.Entry != null || h.Preview != null || h.Section is "Open windows" or "Clipboard & shelf";

    /// <summary>A file the shell can draw a real thumbnail of (not a shortcut, not a program).</summary>
    static string? ThumbFile(Hit h) =>
        h.Entry is { Kind: LauncherIndex.Kind.File } e && !e.Target.StartsWith("shell:") && ThumbExts.Contains(Path.GetExtension(e.Target)) ? e.Target : null;

    // ───────────────────────────── the preview ─────────────────────────────

    FrameworkElement BuildPane()
    {
        var media = new Grid();
        _paneImg = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(_paneImg, BitmapScalingMode.HighQuality);
        _paneIcon = new Image { Width = 52, Height = 52, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(_paneIcon, BitmapScalingMode.HighQuality);
        _paneGlyph = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _paneText = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 9, Margin = new Thickness(7, 6, 6, 4), Foreground = Frozen(Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF)), TextWrapping = TextWrapping.NoWrap, Visibility = Visibility.Collapsed };
        _paneSwatch = new Border { Visibility = Visibility.Collapsed };
        media.Children.Add(_paneSwatch);
        media.Children.Add(_paneImg);
        media.Children.Add(_paneIcon);
        media.Children.Add(_paneGlyph);
        media.Children.Add(_paneText);
        _paneMedia = new Border { MinHeight = 64, Child = media, ClipToBounds = true };

        _paneKind = new TextBlock { FontSize = 9, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        _paneTitle = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34, FontFamily = UiText };
        _paneFacts = new TextBlock { FontSize = 10, Foreground = DimBrush, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = UiText };
        _panePlace = new TextBlock { FontSize = 10, Foreground = DimBrush, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = UiText };
        _paneInfo = new StackPanel { Margin = new Thickness(2, 9, 2, 1), RenderTransform = new TranslateTransform() };
        _paneInfo.Children.Add(_paneKind);
        _paneInfo.Children.Add(_paneTitle);
        _paneInfo.Children.Add(_paneFacts);
        _paneInfo.Children.Add(_panePlace);

        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition());
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.Children.Add(_paneMedia);
        Grid.SetRow(_paneInfo, 1);
        stack.Children.Add(_paneInfo);
        _pane = new Border
        {
            Width = PaneWidth, Margin = new Thickness(10, 1, 0, 2), Padding = new Thickness(7), VerticalAlignment = VerticalAlignment.Stretch,
            Child = stack, Visibility = Visibility.Collapsed, RenderTransform = new TranslateTransform(), IsHitTestVisible = false,
        };
        return _pane;
    }

    /// <summary>The preview's plate and type in this theme's manner.</summary>
    void StylePane()
    {
        if (_pane == null || _paneMedia == null || _paneKind == null) return;
        var s = Skin;
        _pane.CornerRadius = new CornerRadius(_cyber ? 0 : 11);
        _pane.Background = Frozen(_cyber ? Color.FromArgb(0x90, 0x09, 0x17, 0x21) : Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF));
        _pane.BorderBrush = Frozen(_cyber ? Color.FromArgb(0x58, CyCyan.R, CyCyan.G, CyCyan.B) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        _pane.BorderThickness = new Thickness(1);
        _paneMedia.CornerRadius = new CornerRadius(_cyber ? 0 : 7);
        _paneMedia.Background = Frozen(_cyber ? Color.FromArgb(0xC0, 0x03, 0x0B, 0x11) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        _paneKind.Foreground = Frozen(_cyber ? CyCyan : s.Accent);
        _paneKind.FontFamily = _cyber ? CyFont : UiText;
        if (_paneGlyph != null) _paneGlyph.Foreground = Frozen(s.Glyph);
        if (_paneTitle != null) { _paneTitle.FontFamily = _cyber ? CyFont : UiText; _paneTitle.Foreground = _cyber ? Frozen(CyYellow) : Brushes.White; }
    }

    /// <summary>The preview comes when the results have something to show in it, and goes when they don't.</summary>
    void SetPane(bool on)
    {
        if (_pane == null || on == _paneOn) return;
        _paneOn = on;
        _paneFor = null;
        if (_scrollThumb != null) _scrollThumb.Margin = new Thickness(0, 0, on ? -6 : -9, 0); // in the gap, clear of the preview
        if (!on) { _pane.BeginAnimation(OpacityProperty, null); _pane.Visibility = Visibility.Collapsed; return; }
        StylePane();
        _pane.Visibility = Visibility.Visible;
        if (Eco) { _pane.Opacity = 1; return; }
        _pane.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = Out });
        ((TranslateTransform)_pane.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = Out });
    }

    /// <summary>The picked line, shown big on the right.</summary>
    void ShowPreview(Hit? h)
    {
        if (!_paneOn || _pane == null || h == null || _paneInfo == null) return;
        string key = RowKey(h);
        if (key == _paneFor) return;
        _paneFor = key;
        int gen = ++_paneGen;

        _paneImg!.Visibility = _paneIcon!.Visibility = _paneGlyph!.Visibility = _paneText!.Visibility = _paneSwatch!.Visibility = Visibility.Collapsed;
        _paneImg.Source = _paneIcon.Source = null;
        _paneTitle!.Text = h.Title.Trim('“', '”');
        _paneFacts!.Text = "";
        _panePlace!.Text = "";
        string kind = "";

        if (h.Entry is { } e && (!e.Target.StartsWith("shell:") || e.Kind == LauncherIndex.Kind.App))
        {
            switch (e.Kind)
            {
                case LauncherIndex.Kind.File:
                {
                    string ext = Path.GetExtension(e.Target);
                    kind = KindOf(ext);
                    Place(e.Target);
                    Facts(gen, () =>
                    {
                        var (when, size) = FileFacts(e.Target, folder: false);
                        return string.Join("  ·  ", new[] { size >= 0 ? SizeText(size) : "", when > DateTime.MinValue ? Ago(when) : "" }.Where(x => x.Length > 0));
                    });
                    if (h.Preview != null || IconHelper.IsImage(e.Target)) BigPicture(gen, e.Target);
                    else if (TextExts.Contains(ext)) FirstLines(gen, e.Target);
                    else if (ThumbExts.Contains(ext)) BigThumb(gen, e.Target, h.IconPath);
                    else BigIcon(gen, h.IconPath ?? e.Target);
                    break;
                }
                case LauncherIndex.Kind.Folder:
                    kind = "Folder";
                    Place(e.Target);
                    Facts(gen, () =>
                    {
                        int n = 0;
                        try { n = Directory.EnumerateFileSystemEntries(e.Target).Take(1000).Count(); } catch { return ""; }
                        var (when, _) = FileFacts(e.Target, folder: true);
                        string items = n == 1 ? "1 item" : n >= 1000 ? "1,000+ items" : $"{n} items";
                        return when > DateTime.MinValue ? items + "  ·  " + Ago(when) : items;
                    });
                    BigIcon(gen, h.IconPath ?? e.Target);
                    break;
                case LauncherIndex.Kind.App:
                {
                    kind = "App";
                    var win = WindowFor(e);
                    _paneFacts.Text = win != null ? "Running now" : "";
                    Facts(gen, () =>
                    {
                        // a desktop app shows where it's installed; a Store app says so
                        string? exe = DesktopAppExe(e);
                        string place = exe != null && Path.GetDirectoryName(exe) is { Length: > 0 } d ? CompactPath(d, 26) : "From the Microsoft Store";
                        return (win != null ? "Running now" : "", place);
                    });
                    BigIcon(gen, h.IconPath ?? e.Target);
                    break;
                }
                default:
                    kind = "Setting";
                    _paneFacts.Text = h.Detail;
                    if (h.IconPath != null) BigIcon(gen, h.IconPath); else BigGlyph(h.Glyph);
                    break;
            }
        }
        else
        {
            kind = h.Section switch { "Open windows" => "Open window", "Clipboard & shelf" => "Copied", "Web" => "Web", "Settings" => "Setting", "Apps" => "App", "Files" => "File", _ => h.Section ?? "" };
            _paneFacts.Text = h.Detail;
            if (h.Swatch != null) { _paneSwatch.Background = Frozen((Color)ColorConverter.ConvertFromString(h.Swatch)); _paneSwatch.Visibility = Visibility.Visible; }
            else if (h.Section == "Clipboard & shelf" && h.IconPath == null) { _paneText.Text = Lines(h.Title, 7); _paneText.Visibility = Visibility.Visible; }
            else if (h.IconPath != null) BigIcon(gen, h.IconPath);
            else BigGlyph(h.Glyph);
        }
        _paneKind!.Text = _cyber ? kind.ToUpperInvariant() : kind;
        _paneKind.Visibility = kind.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (string.Equals(_paneFacts.Text, kind, StringComparison.OrdinalIgnoreCase)) _paneFacts.Text = ""; // "App" under "APP" says nothing
        _paneFacts.Visibility = _paneFacts.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _panePlace.Visibility = _panePlace.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (Eco) return;
        // the words settle in; what's drawn above them fades in when it's ready
        _paneMedia!.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = Out });
        _paneInfo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = Out });
        ((TranslateTransform)_paneInfo.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = Out });
    }

    void Place(string path)
    {
        if (Path.GetDirectoryName(path.TrimEnd('\\')) is { Length: > 0 } dir) _panePlace!.Text = CompactPath(dir, 26);
        _panePlace!.ToolTip = path;
    }

    /// <summary>Facts read off the screen's thread (a file's size and age, a folder's count), shown if the line is still picked.</summary>
    void Facts(int gen, Func<string> read) => Facts(gen, () => (read(), (string?)null));

    void Facts(int gen, Func<(string Facts, string? Place)> read)
    {
        Task.Run(read).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            if (gen != _paneGen || t.IsFaulted || _paneFacts == null || _panePlace == null) return;
            _paneFacts.Text = t.Result.Facts;
            _paneFacts.Visibility = t.Result.Facts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (t.Result.Place is { } pl) { _panePlace.Text = pl; _panePlace.Visibility = Visibility.Visible; }
        }), TaskScheduler.Default);
    }

    void Reveal(UIElement el)
    {
        el.Visibility = Visibility.Visible;
        if (!Eco) el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = Out });
    }

    void BigGlyph(string? glyph)
    {
        if (string.IsNullOrEmpty(glyph) || _paneGlyph == null) return;
        _paneGlyph.Text = glyph;
        Reveal(_paneGlyph);
    }

    void BigIcon(int gen, string? path)
    {
        if (path == null || _paneIcon == null) return;
        void Show(ImageSource? img)
        {
            if (img == null || gen != _paneGen) return;
            _paneIcon.Source = img;
            Reveal(_paneIcon);
        }
        if (_iconCache.TryGetValue(path, out var known)) { Show(known); return; }
        EnsureIconThread();
        IconQueue.Add((path, img => Dispatcher.InvokeAsync(() => { _iconCache[path] = img; Show(img); }), 0));
    }

    /// <summary>A picture, decoded at the preview's size.</summary>
    void BigPicture(int gen, string path)
    {
        string key = "pic|" + path;
        if (_bigThumbs.TryGetValue(key, out var known)) { ShowBig(gen, known, path); return; }
        Task.Run(() => IconHelper.Thumbnail(path, 300)).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            _bigThumbs[key] = t.IsFaulted ? null : t.Result;
            ShowBig(gen, _bigThumbs[key], path);
        }), TaskScheduler.Default);
    }

    /// <summary>Windows' own thumbnail (a video's frame, a document's first page); the big icon if there is none.</summary>
    void BigThumb(int gen, string path, string? iconPath)
    {
        string key = "shell|" + path;
        if (_bigThumbs.TryGetValue(key, out var known)) { ShowBig(gen, known, iconPath ?? path); return; }
        EnsureIconThread();
        IconQueue.Add((path, img => Dispatcher.InvokeAsync(() => { _bigThumbs[key] = img; ShowBig(gen, img, iconPath ?? path); }), 256));
    }

    void ShowBig(int gen, ImageSource? img, string fallbackIcon)
    {
        if (gen != _paneGen || _paneImg == null) return;
        if (img == null) { BigIcon(gen, fallbackIcon); return; }
        _paneImg.Source = img;
        Reveal(_paneImg);
    }

    /// <summary>A text or code file's first lines, as they are.</summary>
    void FirstLines(int gen, string path)
    {
        Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buf = new byte[2048];
                int n = fs.Read(buf, 0, buf.Length);
                if (Array.IndexOf(buf, (byte)0, 0, n) >= 0) return null; // not text after all
                return Lines(System.Text.Encoding.UTF8.GetString(buf, 0, n), 7);
            }
            catch { return null; }
        }).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            if (gen != _paneGen || _paneText == null) return;
            if (t.IsFaulted || string.IsNullOrWhiteSpace(t.Result)) { BigIcon(gen, path); return; }
            _paneText.Text = t.Result;
            Reveal(_paneText);
        }), TaskScheduler.Default);
    }

    static string Lines(string text, int max) =>
        string.Join("\n", text.Replace("\r", "").Replace("\t", "  ").Split('\n').Take(max).Select(l => l.Length > 34 ? l[..34] : l));

    static string SizeText(long b) => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0} KB" : b < 1024L * 1024 * 1024 ? $"{b / 1048576.0:0.#} MB" : $"{b / 1073741824.0:0.##} GB";

    static string KindOf(string ext) => ext.TrimStart('.').ToLowerInvariant() switch
    {
        "pdf" => "PDF document",
        "doc" or "docx" or "odt" or "rtf" => "Document",
        "xls" or "xlsx" or "csv" or "ods" => "Spreadsheet",
        "ppt" or "pptx" or "odp" => "Presentation",
        "png" or "jpg" or "jpeg" or "gif" or "webp" or "bmp" or "heic" or "heif" or "tif" or "tiff" or "avif" or "svg" or "psd" or "ico" => "Picture",
        "mp4" or "mkv" or "mov" or "avi" or "webm" or "wmv" or "m4v" => "Video",
        "mp3" or "wav" or "flac" or "m4a" or "ogg" or "aac" => "Audio",
        "zip" or "rar" or "7z" or "tar" or "gz" or "iso" => "Archive",
        "exe" or "msi" => "Program",
        "lnk" or "url" => "Shortcut",
        "txt" or "md" or "log" => "Text",
        "" => "File",
        var x when TextExts.Contains("." + x) => "Code",
        var x => x.ToUpperInvariant() + " file",
    };

    /// <summary>The little word under a top hit's name.</summary>
    static string KindWord(Hit h) => h.Entry switch
    {
        { Kind: LauncherIndex.Kind.App } => "App",
        { Kind: LauncherIndex.Kind.Folder } => "Folder",
        { Kind: LauncherIndex.Kind.Setting } => "Setting",
        { Kind: LauncherIndex.Kind.File } e => KindOf(Path.GetExtension(e.Target)),
        _ => h.Section switch { "Open windows" => "Open window", "Clipboard & shelf" => "Copied", _ => h.Detail },
    };

    /// <summary>A file in the list wears its real thumbnail when Windows has one (a video's frame, a PDF's page).</summary>
    void RowThumb(Border box, string path)
    {
        if (_thumbs.TryGetValue(path, out var known)) { if (known != null) ThumbOn(box, known); return; }
        _thumbs[path] = null;
        EnsureIconThread();
        IconQueue.Add((path, img => Dispatcher.InvokeAsync(() =>
        {
            if (img == null) return;
            _thumbs[path] = img;
            ThumbOn(box, img);
        }), 64));
    }

    static void ThumbOn(Border box, ImageSource img)
    {
        box.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        box.Width = box.Height = Math.Max(22, box.Width);
        box.BorderBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        box.BorderThickness = new Thickness(0.5);
        box.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(160)));
    }

    // ───────────────────────────── keys as keys ─────────────────────────────

    FrameworkElement BuildFoot()
    {
        _launchKeys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var row = new Grid { RenderTransform = _footShift };
        row.Children.Add(_launchFoot!);
        row.Children.Add(_launchKeys);
        _launchFootRow = row;
        return row;
    }

    /// <summary>What you can press now, as small keys: "↵ Open   Ctrl ↵ Folder   → More".</summary>
    void SetKeys(params (string Key, string What)[] keys)
    {
        if (_launchKeys == null || _launchFoot == null) return;
        string sig = Skin.Key + "|" + string.Join("|", keys.Select(k => k.Key + k.What));
        _launchFoot.Visibility = Visibility.Collapsed;
        if (sig == _keysShown && _launchKeys.Visibility == Visibility.Visible) return;
        bool was = _launchKeys.Visibility == Visibility.Visible;
        _keysShown = sig;
        _launchKeys.Children.Clear();
        foreach (var (key, what) in keys)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(_cyber ? 0 : 4), Padding = new Thickness(5, 0, 5, 0), Height = 15, VerticalAlignment = VerticalAlignment.Center,
                Background = Frozen(_cyber ? Color.FromArgb(0x1A, CyCyan.R, CyCyan.G, CyCyan.B) : Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
                BorderBrush = Frozen(_cyber ? Color.FromArgb(0x70, CyCyan.R, CyCyan.G, CyCyan.B) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = key, FontSize = 9.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = _cyber ? Frozen(CyYellow) : Frozen(Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF)), FontFamily = _cyber ? CyFont : UiText,
                },
            };
            _launchKeys.Children.Add(chip);
            _launchKeys.Children.Add(new TextBlock
            {
                Text = _cyber ? what.ToUpperInvariant() : what, FontSize = 10, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 13, 0), FontFamily = _cyber ? CyFont : UiText,
            });
        }
        _launchKeys.Visibility = Visibility.Visible;
        if (!was && !Eco) _launchKeys.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    /// <summary>Plain words at the bottom instead of keys (a tip, or what just happened).</summary>
    void FootText(string text)
    {
        if (_launchFoot != null) { _launchFoot.Text = text; _launchFoot.Visibility = Visibility.Visible; }
        if (_launchKeys != null) _launchKeys.Visibility = Visibility.Collapsed;
    }

    /// <summary>The keys for the picked line: what Enter does to it, and what else there is.</summary>
    void KeysFor(Hit? hit)
    {
        if (hit == null) { FootText(""); return; }
        if (_actionsFor != null) { SetKeys(("↵", "Run"), ("←", "Back"), ("Esc", "Close")); return; }
        string enter = hit.Section switch
        {
            "Open windows" => "Switch",
            "Web" => "Search",
            "AI" => "Ask",
            "Clipboard & shelf" => "Copy",
            _ when hit.Key == "answer" || hit.Swatch != null => "Copy",
            _ when hit.Big => "Do it",
            _ => "Open",
        };
        var keys = new List<(string, string)> { ("↵", enter) };
        if (hit.Reveal != null) keys.Add(("Ctrl ↵", "Folder"));
        if (hit.Actions != null) keys.Add(("→", "More"));
        if (_ghost != null || hit.Complete != null) keys.Add(("Tab", "Complete"));
        SetKeys(keys.ToArray());
    }

    // ───────────────────────────── the notch fits the results ─────────────────────────────

    /// <summary>
    /// How tall the notch should be for what's shown: the search line, the list (or the preview, whichever is taller)
    /// and the keys. A short list makes a short notch; the tallest is the open notch's own size.
    /// </summary>
    void FitLauncher()
    {
        if (!_launcherOpen || _launchScroll == null) return;
        double want = ExpandedSize.Height + _chatExtra;
        if (_launchScroll.Visibility == Visibility.Visible)
        {
            double body = Math.Max(_listHeight, _paneOn ? PaneNeed : 0);
            if (_hits.Count == 0) body = 34;
            want = Math.Clamp(body + 92, 132, ExpandedSize.Height);
        }
        if (Math.Abs(want - _launchWantH) < 1) return;
        _launchWantH = want;
        if (_mode == Mode.Expanded) ApplyShape();
    }

    /// <summary>Each frame of the notch's spring: the keys ride its bottom edge.</summary>
    void FollowLauncherEdge()
    {
        if (_launchFootRow == null) return;
        double y = _launcherOpen ? Math.Min(0, _sh.Value - ExpandedSize.Height - _chatExtra) : 0;
        if (Math.Abs(_footShift.Y - y) > 0.05) _footShift.Y = y;
    }

    /// <summary>Opening: the search line comes first, the results a beat later, the keys last.</summary>
    void OpenStaged()
    {
        if (Eco) return;
        void In(FrameworkElement? el, int delay, double dy, int ms)
        {
            if (el == null) return;
            var begin = TimeSpan.FromMilliseconds(delay);
            el.Opacity = 0;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = Out });
            if (dy == 0) return;
            if (el.RenderTransform is not TranslateTransform tt || tt.IsFrozen || ReferenceEquals(tt, _footShift)) return;
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(ms + 80)) { BeginTime = begin, EasingFunction = Out });
        }
        In(_launchLineRow, 0, -7, 220);
        In(_launchBody, 70, 0, 220);
        In(_launchFootRow, 150, 0, 200);
    }
}
