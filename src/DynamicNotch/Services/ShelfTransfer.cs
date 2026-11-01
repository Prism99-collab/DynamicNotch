using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch.Services;

/// <summary>
/// Getting things onto the shelf and back out in the shape each app wants.
/// In: real files, pictures (a browser image, a screenshot, a picture copied out of Word...) saved as PNG files,
/// "virtual" files (an e-mail attachment, a browser image) written out, or text.
/// Out: a drag that adapts to where it's dropped: Office and other editors get the picture / text itself,
/// Explorer and upload boxes get the file. And a copy for Ctrl+V anywhere.
/// </summary>
public static class ShelfTransfer
{
    public static string Folder
    {
        get
        {
            string f = Path.Combine(Settings.Folder, "shelf");
            Directory.CreateDirectory(f);
            return f;
        }
    }

    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" };
    public static bool IsImage(string? path) => path != null && ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsOwned(string path) => path.StartsWith(Folder, StringComparison.OrdinalIgnoreCase);

    // ───────────────────────────── in ─────────────────────────────

    /// <summary>What was dropped: file paths (some written by us from pictures / virtual files), or a piece of text.</summary>
    public static (List<string> Files, string? Text) Capture(IDataObject data)
    {
        var files = new List<string>();
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0)
            {
                files.AddRange(dropped.Where(f => !string.IsNullOrWhiteSpace(f)));
                return (files, null);
            }
        }
        catch { }

        // a file that only exists inside the app (Outlook attachment, an image dragged out of a browser)
        try { if (VirtualFile(data) is { } v) { files.Add(v); return (files, null); } } catch { }

        // a picture itself (copied / dragged out of Word, Paint, a browser...)
        try { if (Picture(data) is { } png) { files.Add(png); return (files, null); } } catch { }

        try
        {
            if ((data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text)) is string text && text.Trim().Length > 0)
            {
                text = text.Trim();
                return (files, text.Length > 20000 ? text[..20000] : text);
            }
        }
        catch { }
        return (files, null);
    }

    static readonly string[] WebFileExts =
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".avif", ".ico",
        ".pdf", ".zip", ".rar", ".7z", ".mp3", ".mp4", ".mov", ".wav", ".docx", ".xlsx", ".pptx", ".txt", ".csv", ".exe", ".msi",
    };

    /// <summary>
    /// Something dragged out of a web page that's really a file: an image (its address in the page's HTML, or a
    /// picture link), a download link (Chrome's DownloadURL), or a picture carried inline as data. Read while the drop
    /// lasts; fetched afterwards with <see cref="DownloadAsync"/>. A plain page link isn't a file: it stays text.
    /// </summary>
    public static (string Url, string? Name)? WebSource(IDataObject data)
    {
        try
        {
            // Chrome: "image/png:photo.png:https://…" for links marked downloadable
            if (data.GetDataPresent("DownloadURL") && data.GetData("DownloadURL") is { } dl && ReadString(dl) is { } d)
            {
                int a = d.IndexOf(':'), b = a < 0 ? -1 : d.IndexOf(':', a + 1);
                if (b > 0 && d[(b + 1)..].StartsWith("http", StringComparison.OrdinalIgnoreCase)) return (d[(b + 1)..], d[(a + 1)..b]);
            }
            // an image in the page: its source, from the HTML the browser hands over
            if (data.GetDataPresent(DataFormats.Html) && data.GetData(DataFormats.Html) is string html)
            {
                var m = System.Text.RegularExpressions.Regex.Match(html, "<img[^>]+src\\s*=\\s*[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string src = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                    if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase) || src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return (src, null);
                }
            }
            // a link that points straight at a file (a picture, a PDF, a zip…)
            string? text = (data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text)) as string;
            text = text?.Trim();
            if (text != null && !text.Contains('\n') && Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")
                && WebFileExts.Contains(Path.GetExtension(uri.AbsolutePath).ToLowerInvariant()))
                return (text, null);
            if (text != null && text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return (text, null);
        }
        catch { }
        return null;

        static string? ReadString(object o) => o switch
        {
            string s => s,
            MemoryStream ms => Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0'),
            _ => null,
        };
    }

    static readonly System.Net.Http.HttpClient Web = MakeClient();
    static System.Net.Http.HttpClient MakeClient()
    {
        var c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DynamicNotch");
        return c;
    }
    const long MaxDownload = 200L * 1024 * 1024;

    /// <summary>Fetches a web file (or decodes an inline picture) into the shelf folder; null if it can't.</summary>
    public static async Task<string?> DownloadAsync(string url, string? name)
    {
        try
        {
            if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                int comma = url.IndexOf(',');
                if (comma < 0 || !url[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
                string mime = url[5..url.IndexOf(';')];
                byte[] bytes = Convert.FromBase64String(url[(comma + 1)..]);
                string path = Unique($"Picture {DateTime.Now:yyyy-MM-dd HH.mm.ss}{ExtFor(mime) ?? ".png"}");
                await File.WriteAllBytesAsync(path, bytes);
                return path;
            }
            using var response = await Web.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength > MaxDownload) return null;
            string? file = name ?? response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
            if (string.IsNullOrWhiteSpace(file)) file = Path.GetFileName(new Uri(url).AbsolutePath);
            file = string.Concat((file ?? "").Split(Path.GetInvalidFileNameChars()));
            if (file.Length == 0) file = $"Download {DateTime.Now:yyyy-MM-dd HH.mm.ss}";
            if (Path.GetExtension(file).Length == 0 && ExtFor(response.Content.Headers.ContentType?.MediaType) is { } ext) file += ext;
            string target = Unique(file);
            await using (var src = await response.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(target))
            {
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    total += n;
                    if (total > MaxDownload) { dst.Close(); File.Delete(target); return null; }
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                }
            }
            return target;
        }
        catch (Exception ex)
        {
            AppLog.Write("shelf download: " + ex.Message);
            return null;
        }
    }

    static string? ExtFor(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", "image/svg+xml" => ".svg",
        "image/bmp" => ".bmp", "image/avif" => ".avif", "application/pdf" => ".pdf", "application/zip" => ".zip", "video/mp4" => ".mp4",
        "audio/mpeg" => ".mp3", "text/plain" => ".txt", _ => null,
    };

    static string? Picture(IDataObject data)
    {
        BitmapSource? bmp = null;
        foreach (var fmt in new[] { "PNG", "image/png" })
        {
            if (!data.GetDataPresent(fmt) || data.GetData(fmt) is not MemoryStream ms) continue;
            ms.Position = 0;
            var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            bmp = dec.Frames[0];
            break;
        }
        if (bmp == null && data.GetDataPresent(DataFormats.Bitmap)) bmp = data.GetData(DataFormats.Bitmap) as BitmapSource;
        if (bmp == null) return null;
        string path = Unique($"Picture {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(fs);
        return path;
    }

    /// <summary>The first file of a FileGroupDescriptor / FileContents pair, written into the shelf folder.</summary>
    static string? VirtualFile(IDataObject data)
    {
        if (!data.GetDataPresent("FileGroupDescriptorW") || data.GetData("FileGroupDescriptorW") is not MemoryStream desc) return null;
        if (data.GetData("FileContents") is not MemoryStream contents || contents.Length == 0) return null;
        // FILEGROUPDESCRIPTORW: a count, then FILEDESCRIPTORW records (592 bytes) with the name at offset 72
        var b = desc.ToArray();
        if (b.Length < 4 + 592 || BitConverter.ToInt32(b, 0) < 1) return null;
        string name = Encoding.Unicode.GetString(b, 4 + 72, 520).Split('\0')[0];
        name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        if (name.Length == 0) name = "File";
        string path = Unique(name);
        using var fs = File.Create(path);
        contents.Position = 0;
        contents.CopyTo(fs);
        return path;
    }

    static string Unique(string name)
    {
        string path = Path.Combine(Folder, name);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(Folder, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return path;
    }

    // ───────────────────────────── out ─────────────────────────────

    /// <summary>Puts an item on the clipboard the way Ctrl+V expects it: the file, and for a picture the picture too.</summary>
    public static void CopyToClipboard(string? path, string? text)
    {
        var data = new DataObject();
        if (path != null)
        {
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
            if (IsImage(path) && LoadImage(path) is { } img) data.SetImage(img);
        }
        else data.SetText(text ?? "");
        Clipboard.SetDataObject(data, copy: true);
    }

    static BitmapSource? LoadImage(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>
    /// The data for a drag out of the shelf. It answers "what do you have?" by looking at the app under the
    /// cursor: editors (Excel, Word, PowerPoint, OneNote, Paint...) get the picture or the text itself, so it lands
    /// in the document instead of opening as a file; everything else (Explorer, the desktop, upload boxes,
    /// Discord, WhatsApp, browsers) gets the file.
    /// </summary>
    public sealed class AdaptiveData : IDataObject
    {
        readonly string? _path;
        readonly string? _text;
        BitmapSource? _image;
        bool _imageLoaded;

        public AdaptiveData(string? path, string? text) { _path = path; _text = text; }

        static readonly HashSet<string> Editors = new(StringComparer.OrdinalIgnoreCase)
        {
            "excel", "winword", "powerpnt", "onenote", "outlook", "mspub", "visio", "mspaint", "paint", "wordpad", "write",
            "soffice.bin", "scalc", "swriter", "simpress", "wps", "et", "wpp", "photoshop", "illustrator", "figma", "canva",
        };

        static IntPtr _lastWindow;
        static bool _lastWasEditor;
        static long _lastCheck;

        /// <summary>
        /// The app under the cursor right now (the drop target asks while the drag is over it, many times a second:
        /// the answer is reused for 150 ms, and for as long as the cursor stays over the same window).
        /// </summary>
        static bool OverEditor()
        {
            try
            {
                GetCursorPos(out var p);
                IntPtr h = WindowFromPoint(p);
                if (h == IntPtr.Zero) return false;
                long now = Environment.TickCount64;
                if (h == _lastWindow && now - _lastCheck < 150) return _lastWasEditor;
                GetWindowThreadProcessId(h, out uint pid);
                using var proc = Process.GetProcessById((int)pid);
                _lastWindow = h;
                _lastCheck = now;
                _lastWasEditor = Editors.Contains(proc.ProcessName);
                return _lastWasEditor;
            }
            catch { return false; }
        }

        BitmapSource? Image
        {
            get
            {
                if (!_imageLoaded) { _imageLoaded = true; _image = IsImage(_path) ? LoadImage(_path!) : null; }
                return _image;
            }
        }

        string[] Formats()
        {
            if (_path == null) return new[] { DataFormats.UnicodeText, DataFormats.Text, DataFormats.StringFormat };
            bool editor = OverEditor();
            if (IsImage(_path) && Image != null)
                return editor ? new[] { DataFormats.Bitmap, DataFormats.Dib, "PNG" } : new[] { DataFormats.FileDrop, DataFormats.Bitmap, "PNG" };
            return new[] { DataFormats.FileDrop };
        }

        public object? GetData(string format) => GetData(format, true);
        public object? GetData(Type format) => GetData(format.FullName ?? "", true);

        public object? GetData(string format, bool autoConvert)
        {
            try
            {
                if (format == DataFormats.FileDrop && _path != null) return new[] { _path };
                if (format is var f && (f == DataFormats.UnicodeText || f == DataFormats.Text || f == DataFormats.StringFormat) && _text != null) return _text;
                if (format == DataFormats.Bitmap && Image != null) return Image;
                if (format == DataFormats.Dib && Image != null) return Dib(Image);
                if (format == "PNG" && Image != null)
                {
                    var ms = new MemoryStream();
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(Image));
                    enc.Save(ms);
                    ms.Position = 0;
                    return ms;
                }
            }
            catch { }
            return null;
        }

        public bool GetDataPresent(string format) => Formats().Contains(format);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? "");
        public bool GetDataPresent(string format, bool autoConvert) => GetDataPresent(format);
        public string[] GetFormats() => Formats();
        public string[] GetFormats(bool autoConvert) => Formats();
        public void SetData(object data) { }
        public void SetData(string format, object data) { }
        public void SetData(Type format, object data) { }
        public void SetData(string format, object data, bool autoConvert) { }

        /// <summary>A packed device-independent bitmap (header + pixels), the format Office reads most reliably.</summary>
        static MemoryStream Dib(BitmapSource src)
        {
            var bgra = new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(px.Length); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = h - 1; y >= 0; y--) bw.Write(px, y * stride, stride); // bottom-up
            bw.Flush();
            ms.Position = 0;
            return ms;
        }
    }
}
