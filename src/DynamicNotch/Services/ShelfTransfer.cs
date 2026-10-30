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

        /// <summary>The app under the cursor right now (the drop target asks while the drag is over it).</summary>
        static bool OverEditor()
        {
            try
            {
                GetCursorPos(out var p);
                IntPtr h = WindowFromPoint(p);
                if (h == IntPtr.Zero) return false;
                GetWindowThreadProcessId(h, out uint pid);
                using var proc = Process.GetProcessById((int)pid);
                return Editors.Contains(proc.ProcessName);
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
