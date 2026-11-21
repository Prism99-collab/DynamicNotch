using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Clipboard history (Tools page): the texts and pictures you copied, newest first.
///  • kept across restarts (clipboard.json, pictures as PNGs in the clips folder next to it);
///  • pin an item and it stays at the top, through Clear and through the limit;
///  • a click copies it again; a right-click removes it;
///  • what password managers mark as secret is never kept (checked where the clipboard is read).
/// </summary>
public partial class NotchWindow
{
    sealed class ClipEntry
    {
        public string? Text { get; set; }
        public string? Image { get; set; }   // file name in the clips folder
        public DateTime At { get; set; }
        public bool Pinned { get; set; }
        /// <summary>A picture's fingerprint (its pixels, without transparency): the same picture copied again is the same entry.</summary>
        public string? Hash { get; set; }
    }

    /// <summary>Two copies of the same text: the same entry, even if one app ended lines differently or left spaces at a line's end.</summary>
    static string ClipKey(string text) => string.Join("\n", text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ').Split('\n').Select(l => l.TrimEnd()));

    static string PixelHash(BitmapSource bgra)
    {
        int stride = bgra.PixelWidth * 4;
        var px = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(px, stride, 0);
        for (int i = 3; i < px.Length; i += 4) px[i] = 0; // the clipboard often drops transparency: it doesn't count
        return $"{bgra.PixelWidth}x{bgra.PixelHeight}:" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(px));
    }

    const int ClipTexts = 40, ClipImages = 12;
    readonly List<ClipEntry> _clips = new();
    static string ClipFile => Path.Combine(Settings.Folder, "clipboard.json");
    static string ClipFolder => Path.Combine(Settings.Folder, "clips");
    int _clipSaveVersion;

    void LoadClips()
    {
        try
        {
            if (!File.Exists(ClipFile)) return;
            var items = JsonSerializer.Deserialize<List<ClipEntry>>(File.ReadAllText(ClipFile));
            if (items == null) return;
            foreach (var c in items)
                if (c.Text is { Length: > 0 } || c.Image != null && File.Exists(Path.Combine(ClipFolder, c.Image)))
                    _clips.Add(c);
        }
        catch { }
        // texts saved twice before: kept once (the newest place)
        var seen = new HashSet<string>();
        _clips.RemoveAll(c => c.Text != null && !seen.Add(ClipKey(c.Text)));
        FingerprintOldClips();
    }

    async void FingerprintOldClips()
    {
        var old = _clips.Where(c => c.Image != null && c.Hash == null).ToList();
        if (old.Count == 0) return;
        var hashes = await Task.Run(() => old.Select(c =>
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(Path.Combine(ClipFolder, c.Image!));
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                var bgra = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
                bgra.Freeze();
                return PixelHash(bgra);
            }
            catch { return null; }
        }).ToList());
        for (int i = 0; i < old.Count; i++) old[i].Hash = hashes[i];
        var seen = new HashSet<string>();
        int before = _clips.Count;
        _clips.RemoveAll(c => c.Hash != null && !seen.Add(c.Hash) && !c.Pinned);
        if (_clips.Count != before || hashes.Count > 0) ClipsChanged();
    }

    /// <summary>Saved shortly after the last change, off the UI thread, swapped in whole.</summary>
    async void SaveClips()
    {
        int v = ++_clipSaveVersion;
        await Task.Delay(800);
        if (v != _clipSaveVersion) return;
        var snapshot = _clips.Select(c => new ClipEntry { Text = c.Text, Image = c.Image, At = c.At, Pinned = c.Pinned, Hash = c.Hash }).ToList();
        await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                string tmp = ClipFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
                File.Move(tmp, ClipFile, true);
                // pictures nobody points at any more
                if (Directory.Exists(ClipFolder))
                {
                    var keep = snapshot.Where(c => c.Image != null).Select(c => c.Image!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in Directory.EnumerateFiles(ClipFolder, "*.png"))
                        if (!keep.Contains(Path.GetFileName(f))) { try { File.Delete(f); } catch { } }
                }
            }
            catch { }
        });
    }

    void AddClipboardText(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 4000) text = text[..4000];
        string key = ClipKey(text);
        var same = _clips.FirstOrDefault(c => c.Text != null && (c.Text == text || ClipKey(c.Text) == key));
        bool pinned = same?.Pinned ?? false;
        if (same != null) _clips.Remove(same);
        _clips.Insert(0, new ClipEntry { Text = text, At = DateTime.Now, Pinned = pinned });
        _clipFresh = _clips[0]; _clipFreshAt = DateTime.UtcNow;
        TrimClips();
        ClipsChanged();
    }

    /// <summary>A copied picture: kept as a PNG (at most 2560 px wide), off the UI thread.</summary>
    async void AddClipboardImage(BitmapSource image)
    {
        try
        {
            if (image.PixelWidth < 8 || image.PixelHeight < 8) return;
            BitmapSource src = image;
            if (src.PixelWidth > 2560)
            {
                double k = 2560.0 / src.PixelWidth;
                src = new TransformedBitmap(src, new ScaleTransform(k, k));
            }
            var frozen = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            frozen.Freeze();
            string name = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
            string hash = await Task.Run(() => PixelHash(frozen));
            // the same picture again (an app announcing it twice, or one of these copied back): it just moves to the top
            if (_clips.FirstOrDefault(c => c.Hash == hash) is { } known) { BumpClip(known); return; }
            if (_pendingClipHashes.Contains(hash)) return;
            _pendingClipHashes.Add(hash);
            bool ok;
            try
            {
                ok = await Task.Run(() =>
                {
                try
                {
                    Directory.CreateDirectory(ClipFolder);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(frozen));
                    using var fs = File.Create(Path.Combine(ClipFolder, name));
                    enc.Save(fs);
                    return true;
                }
                catch { return false; }
                });
            }
            finally { _pendingClipHashes.Remove(hash); }
            if (!ok) return;
            if (_clips.FirstOrDefault(c => c.Hash == hash) is { } meanwhile) { BumpClip(meanwhile); return; } // saved twice at once
            _clips.Insert(0, new ClipEntry { Image = name, At = DateTime.Now, Hash = hash });
            TrimClips();
            ClipsChanged();
        }
        catch { }
    }

    readonly HashSet<string> _pendingClipHashes = new();

    /// <summary>Copied again: to the top with the new time (a pinned one keeps its place).</summary>
    void BumpClip(ClipEntry c)
    {
        c.At = DateTime.Now;
        if (!c.Pinned) { _clips.Remove(c); _clips.Insert(0, c); }
        ClipsChanged();
    }

    /// <summary>The newest texts and pictures stay, pinned ones always.</summary>
    void TrimClips()
    {
        int texts = 0, images = 0;
        for (int i = 0; i < _clips.Count; i++)
        {
            var c = _clips[i];
            if (c.Pinned) continue;
            bool drop = c.Image != null ? ++images > ClipImages : ++texts > ClipTexts;
            if (drop) { _clips.RemoveAt(i); i--; }
        }
    }

    void ClipsChanged()
    {
        SaveClips();
        if (_mode == Mode.Expanded && _page == 3) RenderClipboard();
    }

    void ClearClips()
    {
        int gone = _clips.RemoveAll(c => !c.Pinned);
        ClipsChanged();
        RenderClipboard();
        if (gone > 0) FeelIt(Feel.EatAll); // it gulps the lot
    }

    ClipEntry? _clipFresh;
    DateTime _clipFreshAt, _clipCascadeAt;

    void RenderClipboard()
    {
        ClipList.Children.Clear();
        ClipEmpty.Visibility = Visibility.Collapsed;
        if (_clips.Count == 0)
        {
            ClipList.Children.Add(ClipEmptyState(Loc.T("Nothing copied yet"), Loc.T("Copy text or a picture and it appears here.")));
            return;
        }
        // pinned first (in the order they were pinned), then the rest, newest first
        foreach (var c in _clips.Where(c => c.Pinned).Concat(_clips.Where(c => !c.Pinned)))
        {
            var row = ClipRow(c);
            ClipList.Children.Add(row);
            if (ReferenceEquals(c, _clipFresh) && (DateTime.UtcNow - _clipFreshAt).TotalSeconds < 3) { _clipFresh = null; ClipGlow(row); }
        }
        // opening the page: the rows arrive in a quick cascade (not on every small change after)
        if ((DateTime.UtcNow - _clipCascadeAt).TotalSeconds > 4) ClipCascade(ClipList);
        _clipCascadeAt = DateTime.UtcNow;
    }

    Button ClipRow(ClipEntry c)
    {
        FrameworkElement lead;
        if (c.Image != null && LoadThumb(c.Image) is { } thumb)
            lead = new Border { Width = 30, Height = 22, CornerRadius = new CornerRadius(_cyber ? 0 : 5), Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill } };
        else
            lead = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(_cyber ? 0 : 6), Background = (Brush)FindResource("Tile"),
                Child = Glyph(c.Text != null && LooksLikeLink(c.Text.Trim()) ? "\uE71B" : "\uE8C8", 10, DimBrush),
            };
        bool link = c.Text != null && LooksLikeLink(c.Text.Trim());
        string title = c.Text != null ? NiceClipText(c.Text) : Loc.T("Picture");

        var pin = ClipAction(c.Pinned ? "\uE77A" : "\uE718", c.Pinned ? Loc.T("Unpin") : Loc.T("Pin to keep it at the top"), () =>
        {
            c.Pinned = !c.Pinned;
            ClipsChanged();
            RenderClipboard();
            FeelIt(c.Pinned ? Feel.Pin : Feel.Unpin);
        });
        var remove = ClipAction("\uE711", Loc.T("Remove"), () =>
        {
            _clips.Remove(c);
            ClipsChanged();
            RenderClipboard();
            FeelIt(Feel.Eat); // nom
        }, DimBrush);
        var row = ClipLine(lead, title, link ? "LINK" : null, c.Pinned ? Loc.T("Pinned") : ShortAgo(c.At), pin, remove);

        var btn = new Button
        {
            Style = (Style)FindResource("RowButton"), Content = row, Height = ClipRowHeight + 2,
            ToolTip = (c.Text != null ? (c.Text.Length > 300 ? c.Text[..300] + "…" : c.Text) + "\n\n" : "") + Loc.T("Click to copy it again"),
        };
        btn.Click += (_, _) =>
        {
            _ignoreClipboardUntil = DateTime.UtcNow.AddMilliseconds(600);
            try
            {
                if (c.Text != null) Clipboard.SetText(c.Text);
                else if (c.Image != null) Clipboard.SetImage(new BitmapImage(new Uri(Path.Combine(ClipFolder, c.Image))));
            }
            catch { }
            FeelIt(Feel.Copy);
            // copied again: it moves to the top (pinned ones keep their place)
            if (!c.Pinned) { c.At = DateTime.Now; _clips.Remove(c); _clips.Insert(0, c); _clipFresh = c; _clipFreshAt = DateTime.UtcNow; ClipsChanged(); RenderClipboard(); }
            else ClipGlow(btn);
        };
        btn.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            _clips.Remove(c);
            ClipsChanged();
            RenderClipboard();
            FeelIt(Feel.Eat);
        };
        return btn;
    }

    static BitmapImage? LoadThumb(string name)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(Path.Combine(ClipFolder, name));
            img.DecodePixelWidth = 96; // a thumbnail, not the whole picture in memory
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }
}
