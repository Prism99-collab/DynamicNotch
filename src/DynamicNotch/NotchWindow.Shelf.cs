using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The shelf: drag a file (or text, or a link) up to the notch and it keeps it until you drag it out
/// into another app. The notch is click-through, so it only listens for drops while something is being
/// dragged toward it; a normal click on a tab under it never wakes this up.
/// </summary>
public partial class NotchWindow
{
    /// <summary>One thing on the shelf: a file / folder, or a piece of text.</summary>
    sealed record ShelfItem(string? Path, string? Text)
    {
        [JsonIgnore] public string Name => Path != null ? System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path : OneLine(Text ?? "");
        [JsonIgnore] public bool Exists => Path == null || File.Exists(Path) || Directory.Exists(Path);
        static string OneLine(string t) => string.Join(" ", t.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
    }

    const int ShelfMax = 12;
    readonly List<ShelfItem> _shelf = new();
    bool _dropArmed, _dropHot, _shelfTab = true;
    DateTime _pressAtTime, _lastDragOver;
    POINT _pressAtPoint;
    bool _leftWasDown;
    static string ShelfFile => Path.Combine(Settings.Folder, "shelf.json");

    void WireShelf()
    {
        LoadShelf();
        DragEnter += OnShelfDragEnter;
        DragOver += OnShelfDragOver;
        DragLeave += (_, e) => e.Handled = true; // leaving is decided by the drag going quiet (see UpdateDropArming)
        Drop += OnShelfDrop;
        ShelfTab.Click += (_, _) => { _shelfTab = true; RenderShelfOrRecent(); };
        RecentTab.Click += (_, _) => { _shelfTab = false; RenderShelfOrRecent(); };
        ShelfClear.Click += (_, _) => { foreach (var i in _shelf) Forget(i); _shelf.Clear(); SaveShelf(); RenderShelfOrRecent(); };

        // the drop zone's life: marching dashed outline and a bobbing arrow (only animate while it's on screen)
        DropPanel.IsVisibleChanged += (_, _) =>
        {
            if (DropPanel.IsVisible)
            {
                var march = new DoubleAnimation(0, -10.8, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(march, 40);
                DropOutline.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, march);
                var bob = new DoubleAnimation(-3, 3, TimeSpan.FromSeconds(0.55))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                DropArrowShift.BeginAnimation(TranslateTransform.YProperty, bob);
            }
            else
            {
                DropOutline.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
                DropArrowShift.BeginAnimation(TranslateTransform.YProperty, null);
            }
        };
    }

    // ───────────────────────────── catching a drag ─────────────────────────────

    /// <summary>
    /// Arms the drop catcher only for a real drag heading to the notch: left button held for a moment,
    /// the cursor travelled from where it was pressed, and it is now near the notch. Disarms on release.
    /// </summary>
    void UpdateDropArming(POINT p)
    {
        bool left = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        var now = DateTime.UtcNow;
        if (left && !_leftWasDown) { _pressAtTime = now; _pressAtPoint = p; }
        _leftWasDown = left;

        if (!left)
        {
            if (_dropArmed) SetDropArmed(false);
            if (_mode == Mode.Drop) LeaveDropZone();
            return;
        }
        double cx = _winLeft + _winW / 2.0;
        bool near = p.Y - _mon.Top < 110 * _scale && Math.Abs(p.X - cx) < 270 * _scale;
        // dragged away: the cursor left the area and the drag stopped reaching us (holding still over it is fine)
        if (_mode == Mode.Drop && !near && (now - _lastDragOver).TotalMilliseconds > 350) { LeaveDropZone(); return; }
        if (_dropArmed || _hidden || _mode == Mode.Expanded) return;

        double dx = p.X - _pressAtPoint.X, dy = p.Y - _pressAtPoint.Y;
        bool travelled = dx * dx + dy * dy > 60 * 60 * _scale * _scale;
        bool held = (now - _pressAtTime).TotalMilliseconds > 220;
        if (travelled && held && near) SetDropArmed(true);
    }

    void SetDropArmed(bool on)
    {
        _dropArmed = on;
        DropCatcher.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RefreshClickThrough();
    }

    static bool HasShelfData(IDataObject d) =>
        d.GetDataPresent(DataFormats.FileDrop) || d.GetDataPresent(DataFormats.UnicodeText) || d.GetDataPresent(DataFormats.Text) ||
        d.GetDataPresent(DataFormats.Bitmap) || d.GetDataPresent("PNG") || d.GetDataPresent("FileGroupDescriptorW");

    void OnShelfDragEnter(object sender, DragEventArgs e)
    {
        if (!HasShelfData(e.Data)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        _lastDragOver = DateTime.UtcNow;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        if (_mode == Mode.Drop) return;
        if (_mode == Mode.Expanded) Collapse();
        _osdTimer.Stop();
        _osd = null;
        RenderDropZone();
        SetMode(Mode.Drop);
    }

    void OnShelfDragOver(object sender, DragEventArgs e)
    {
        if (!HasShelfData(e.Data)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        _lastDragOver = DateTime.UtcNow;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        if (_mode != Mode.Drop) { OnShelfDragEnter(sender, e); return; }
        // "hot" once the file is over the receptacle itself, not just near it
        var pos = e.GetPosition(Pill);
        bool hot = pos.X >= 0 && pos.Y >= 0 && pos.X <= Pill.ActualWidth && pos.Y <= Pill.ActualHeight + 8;
        if (hot != _dropHot) SetDropHot(hot);
    }

    void SetDropHot(bool hot)
    {
        _dropHot = hot;
        ApplyShape();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DropFill.BeginAnimation(OpacityProperty, new DoubleAnimation(hot ? 0.16 : 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        DropOutline.Stroke = hot ? Blue : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        DropOutline.StrokeDashArray = hot ? null : new DoubleCollection { 3, 2.4 };
        DropTitle.Text = hot ? "Let go to keep it" : "Drop to keep it here";
        UpdateAura(hot ? ((SolidColorBrush)Blue).Color : null);
        if (hot) FlashRim(((SolidColorBrush)Blue).Color);
    }

    void CopyShelfItem(ShelfItem item, Button row)
    {
        _ignoreClipboardUntil = DateTime.UtcNow.AddMilliseconds(800);
        try { ShelfTransfer.CopyToClipboard(item.Path, item.Text); } catch { }
        FlashRow(row);
        ShowOsd("copied", "\uE8C8", null, "Copied", item.Path != null ? $"{item.Name} · Ctrl+V to paste" : "Ctrl+V to paste", null, glyphBrush: Blue, seconds: 1.6, prio: Prio.Low);
    }

    /// <summary>A picture the shelf saved itself is deleted with it; your own files are never touched.</summary>
    static void Forget(ShelfItem item)
    {
        if (item.Path == null || !ShelfTransfer.IsOwned(item.Path)) return;
        try { File.Delete(item.Path); } catch { }
    }

    void LeaveDropZone()
    {
        if (_mode != Mode.Drop) return;
        _dropHot = false;
        DropFill.BeginAnimation(OpacityProperty, null);
        DropFill.Opacity = 0;
        _lean.Target = 0;
        SetMode(BaseMode);
    }

    // ───────────────────────────── the drop ─────────────────────────────

    void OnShelfDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var added = new List<ShelfItem>();
        // files as they are; a picture or a file that only lives inside the app is saved as a real file first
        var (files, text) = ShelfTransfer.Capture(e.Data);
        added.AddRange(files.Select(f => new ShelfItem(f, null)));
        if (added.Count == 0 && text != null) added.Add(new ShelfItem(null, text));
        e.Effects = added.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropArmed(false);
        if (added.Count == 0) { LeaveDropZone(); return; }

        foreach (var item in added)
        {
            _shelf.RemoveAll(s => s.Path != null && string.Equals(s.Path, item.Path, StringComparison.OrdinalIgnoreCase) || s.Text != null && s.Text == item.Text);
            _shelf.Insert(0, item);
        }
        while (_shelf.Count > ShelfMax) { Forget(_shelf[^1]); _shelf.RemoveAt(_shelf.Count - 1); }
        SaveShelf();
        Gulp(added);
    }

    /// <summary>The notch swallows the drop: a quick squash, then it springs back into the "kept" pop-up with a shine.</summary>
    async void Gulp(List<ShelfItem> added)
    {
        _dropHot = false;
        _sw.Configure(SpringStyle.Press);
        _sh.Configure(SpringStyle.Press);
        _sw.Target = DropSize.Width - 40;
        _sh.Target = DropSize.Height - 26;
        StartAnimating();
        DropFill.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 0, TimeSpan.FromMilliseconds(320)));
        await Task.Delay(150);

        var first = added[0];
        ImageSource? art = first.Path != null ? await Task.Run(() => IconHelper.ForFile(first.Path)) : null;
        string title = added.Count == 1 ? "Kept on the shelf" : $"Kept {added.Count} things on the shelf";
        string sub = (added.Count == 1 ? first.Name : $"{first.Name} + {added.Count - 1} more") + " · open the notch to drag out";
        _lean.Target = 0;
        ShowOsd("shelf", art == null ? (first.Path == null ? "" : "") : null, null, title, sub, $"{_shelf.Count}",
            art: art, glyphBrush: Blue, seconds: 3, force: true, motion: Motion.Pulse,
            onClick: () => { _shelfTab = true; ExpandTo(3); });
    }

    // ───────────────────────────── what's shown ─────────────────────────────

    /// <summary>The drop zone's text and the fanned-out cards of what's already kept.</summary>
    void RenderDropZone()
    {
        DropTitle.Text = "Drop to keep it here";
        DropSub.Text = _shelf.Count == 0 ? "On the shelf until you drag it out" : $"Shelf · {_shelf.Count} kept";
        DropOutline.Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        DropOutline.StrokeDashArray = new DoubleCollection { 3, 2.4 };
        DropStack.Children.Clear();
        var items = _shelf.Take(3).Reverse().ToList();
        double[] angles = { -10, 4, 14 }, xs = { 6, 16, 26 };
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var card = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(angles[3 - items.Count + i]),
                Child = it.Path == null ? Glyph("", 12, DimBrush) : null,
            };
            Canvas.SetLeft(card, xs[3 - items.Count + i]);
            Canvas.SetTop(card, 6);
            DropStack.Children.Add(card);
            if (it.Path is { } path)
                _ = Task.Run(() => IconHelper.ForFile(path)).ContinueWith(t =>
                    Dispatcher.InvokeAsync(() => { if (t.Result != null) card.Background = IconBrush(t.Result); }));
        }
    }

    /// <summary>The right column of the Clipboard &amp; Files page: the shelf, or the newest files.</summary>
    void RenderShelfOrRecent()
    {
        _shelf.RemoveAll(s => !s.Exists); // moved or deleted elsewhere since
        ShelfTab.Content = _shelf.Count > 0 ? $"Shelf · {_shelf.Count}" : "Shelf";
        StyleTab(ShelfTab, _shelfTab);
        StyleTab(RecentTab, !_shelfTab);
        ShelfScroll.Visibility = _shelfTab ? Visibility.Visible : Visibility.Collapsed;
        FilesList.Visibility = _shelfTab ? Visibility.Collapsed : Visibility.Visible;
        ShelfClear.Visibility = _shelfTab && _shelf.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenDownloads.Visibility = _shelfTab ? Visibility.Collapsed : Visibility.Visible;
        if (_shelfTab) RenderShelf(); else _ = RenderFilesAsync();
    }

    void RenderShelf()
    {
        ShelfList.Children.Clear();
        if (_shelf.Count == 0)
        {
            ShelfList.Children.Add(new TextBlock
            {
                Text = "Drag a file, a picture or some text up to the notch: it waits here until you drag it out.",
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)), Margin = new Thickness(4, 8, 4, 8),
            });
            return;
        }
        foreach (var item in _shelf.ToList())
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            var icon = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)), Child = item.Path == null ? Glyph("", 11, DimBrush) : null };
            row.Children.Add(icon);
            if (item.Path is { } p)
                _ = Task.Run(() => IconHelper.ForFile(p)).ContinueWith(t => Dispatcher.InvokeAsync(() => { if (t.Result != null) icon.Background = IconBrush(t.Result); }));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = item.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            long size = item.Path != null && File.Exists(item.Path) ? SafeLength(item.Path) : -1;
            text.Children.Add(new TextBlock { Text = item.Path == null ? "Text" : size >= 0 ? SystemMonitor.Bytes(size) : "Folder", FontSize = 10, Foreground = DimBrush });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            // copy: for Ctrl+V into anything that pastes (cells, documents, chats...)
            var copy = new Button { Style = (Style)FindResource("CircleButton"), Width = 22, Height = 22, Content = Glyph("\uE8C8", 10, DimBrush), ToolTip = "Copy (then Ctrl+V anywhere)" };
            Grid.SetColumn(copy, 2);
            row.Children.Add(copy);
            var remove = new Button { Style = (Style)FindResource("CircleButton"), Width = 22, Height = 22, Content = Glyph("", 9, DimBrush), ToolTip = "Remove from the shelf" };
            Grid.SetColumn(remove, 3);
            remove.Click += (_, e) => { e.Handled = true; Forget(item); _shelf.Remove(item); SaveShelf(); RenderShelfOrRecent(); };
            row.Children.Add(remove);

            var btn = new Button { Style = (Style)FindResource("RowButton"), Content = row, Height = 36,
                ToolTip = item.Path != null ? "Drag into any app (Excel, Word, a chat, a folder) · click to open" : "Drag into any app · click to copy" };
            copy.Click += (_, e) => { e.Handled = true; CopyShelfItem(item, btn); };
            btn.Click += (_, _) =>
            {
                if (_dragOut) return;
                if (item.Path != null) OpenPath(item.Path);
                else CopyShelfItem(item, btn);
            };
            btn.PreviewMouseLeftButtonDown += (_, e) => _fileDown = e.GetPosition(this);
            btn.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _swiping || _dragOut) return;
                var pos = e.GetPosition(this);
                double dx = pos.X - _fileDown.X, dy = pos.Y - _fileDown.Y;
                if (Math.Abs(dy) < 8 || Math.Abs(dy) < Math.Abs(dx)) return;
                _dragOut = true;
                _maybeSwipe = false;
                try
                {
                    // the data answers each app in the shape it takes: a picture for Excel / Word, the file for a folder
                    var data = new DataObject(new ShelfTransfer.AdaptiveData(item.Path, item.Text));
                    // pictures we saved ourselves are copied, never moved away from the shelf
                    var allowed = item.Path != null && ShelfTransfer.IsOwned(item.Path) ? DragDropEffects.Copy : DragDropEffects.Copy | DragDropEffects.Move;
                    DragDrop.DoDragDrop(btn, data, allowed);
                }
                catch { }
                finally { _dragOut = false; }
                // moved into a folder: it isn't here any more, so it leaves the shelf too
                if (!item.Exists) { _shelf.Remove(item); SaveShelf(); RenderShelfOrRecent(); }
                e.Handled = true;
            };
            ShelfList.Children.Add(btn);
        }
    }

    // ───────────────────────────── kept across restarts ─────────────────────────────

    void LoadShelf()
    {
        try
        {
            if (!File.Exists(ShelfFile)) return;
            var items = JsonSerializer.Deserialize<List<ShelfItem>>(File.ReadAllText(ShelfFile));
            if (items != null) _shelf.AddRange(items.Where(i => (i.Path != null || i.Text != null) && i.Exists).Take(ShelfMax));
        }
        catch { }
    }

    void SaveShelf()
    {
        var snapshot = _shelf.ToList();
        _ = Task.Run(() =>
        {
            try { Directory.CreateDirectory(Settings.Folder); File.WriteAllText(ShelfFile, JsonSerializer.Serialize(snapshot)); }
            catch { }
        });
    }
}
