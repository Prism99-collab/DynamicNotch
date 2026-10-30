using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch.Services;

/// <summary>Shell icons and image thumbnails as frozen, thread-safe WPF images.</summary>
public static class IconHelper
{
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    public static bool IsImage(string path) => ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Icon for a file, folder or exe: the shell's large artwork (128 px, crisp at any scaling), else the 32 px icon.</summary>
    public static ImageSource? ForPath(string path)
    {
        string key = Path.GetExtension(path) is { Length: > 0 } ext && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            ? ext : path;
        lock (Cache)
            if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? img = OnSta(() => Large(path, 128));
        if (img == null)
        {
            try
            {
                var info = new SHFILEINFO();
                // SHGFI_ICON | SHGFI_LARGEICON
                if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    DestroyIcon(info.hIcon);
                    img = src;
                }
            }
            catch { }
        }
        lock (Cache) Cache[key] = img;
        return img;
    }

    /// <summary>The icon for a kind of file (".zip", ".exe"...) without the file existing yet: a download in progress.</summary>
    public static ImageSource? ForType(string ext)
    {
        string key = "type:" + ext;
        lock (Cache)
            if (Cache.TryGetValue(key, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            var info = new SHFILEINFO();
            // SHGFI_ICON | SHGFI_USEFILEATTRIBUTES, as a normal file
            if (SHGetFileInfo("file" + ext, 0x80, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x10) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                DestroyIcon(info.hIcon);
                img = src;
            }
        }
        catch { }
        lock (Cache) Cache[key] = img;
        return img;
    }

    // ───────────── the shell's big icon artwork (IShellItemImageFactory) ─────────────

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    /// <summary>Shell extensions expect a single-threaded apartment: run there, briefly.</summary>
    static T? OnSta<T>(Func<T?> work) where T : class
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return work();
        T? result = null;
        var t = new Thread(() => { try { result = work(); } catch { } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(3000);
        return result;
    }

    static BitmapSource? Large(string path, int px)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            // SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY: the app's own icon art, never a stretched small one
            if (factory.GetImage(new NativeSize { Cx = px, Cy = px }, 0x1 | 0x4, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.Bits == IntPtr.Zero || bm.BitsPixel != 32) return null;

            int w = bm.Width, h = Math.Abs(bm.Height), stride = w * 4;
            var buf = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                // DIB sections are usually stored bottom-up
                int srcRow = bm.Height > 0 ? h - 1 - y : y;
                Marshal.Copy(bm.Bits + srcRow * bm.WidthBytes, buf, y * stride, stride);
            }
            bool anyAlpha = false;
            for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;

            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, stride);
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
    }

    public static ImageSource? Thumbnail(string path, int px = 120)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = px;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public static ImageSource? ForFile(string path) => IsImage(path) ? Thumbnail(path, 80) ?? ForPath(path) : ForPath(path);

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageSource, object> AccentCache = new();
    static readonly object NoAccent = new();

    /// <summary>The most vivid colour of an icon / picture (Teams → purple, WhatsApp → green...), or null if it's grey.</summary>
    public static Color? Accent(ImageSource img)
    {
        if (AccentCache.TryGetValue(img, out var cached)) return cached as Color?;
        Color? result = null;
        try
        {
            if (img is BitmapSource src)
            {
                var small = new TransformedBitmap(src, new ScaleTransform(16.0 / Math.Max(1, src.PixelWidth), 16.0 / Math.Max(1, src.PixelHeight)));
                var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                var px = new byte[w * h * 4];
                conv.CopyPixels(px, w * 4, 0);
                double r = 0, g = 0, b = 0, total = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    if (px[i + 3] < 128) continue; // transparent edges of an icon
                    double bb = px[i], gg = px[i + 1], rr = px[i + 2];
                    double max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
                    if (max < 40) continue;
                    double sat = (max - min) / max;
                    double wgt = sat * sat * (max / 255) + 0.005;
                    r += rr * wgt; g += gg * wgt; b += bb * wgt; total += wgt;
                }
                if (total > 0)
                {
                    r /= total; g /= total; b /= total;
                    double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                    if (mx > 0 && (mx - mn) / mx >= 0.2)
                    {
                        double k = 245 / mx; // bright enough to glow on black
                        result = Color.FromRgb((byte)Math.Min(255, r * k), (byte)Math.Min(255, g * k), (byte)Math.Min(255, b * k));
                    }
                }
            }
        }
        catch { }
        AccentCache.AddOrUpdate(img, result.HasValue ? result.Value : NoAccent);
        return result;
    }
}
