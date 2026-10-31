using System.Runtime.InteropServices;
using System.Windows.Media;

namespace DynamicNotch.Services;

/// <summary>
/// The colour of what you're looking at: a tiny, smoothed copy of a screen area (48 x 27 cells, made by the
/// graphics driver in a few milliseconds), averaged with a preference for vivid tones so a page's accent
/// shows through its white background. Cells under the notch are left out. Safe off the UI thread.
/// </summary>
public static class ScreenColor
{
    public readonly record struct Area(int Left, int Top, int Right, int Bottom);

    const int W = 48, H = 27;

    /// <summary>The representative colour of <paramref name="area"/> (physical pixels), ignoring <paramref name="skip"/>.</summary>
    public static Color? Sample(Area area, Area skip)
    {
        int aw = area.Right - area.Left, ah = area.Bottom - area.Top;
        if (aw < 32 || ah < 32) return null;
        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, W, H);
            old = SelectObject(mem, bmp);
            SetStretchBltMode(mem, 4 /* HALFTONE: each cell is the average of its pixels */);
            SetBrushOrgEx(mem, 0, 0, IntPtr.Zero);
            if (!StretchBlt(mem, 0, 0, W, H, screen, area.Left, area.Top, aw, ah, 0x00CC0020 /* SRCCOPY */)) return null;
            SelectObject(mem, old);
            old = IntPtr.Zero;

            var info = new BITMAPINFOHEADER { biSize = 40, biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32 };
            var px = new byte[W * H * 4];
            if (GetDIBits(mem, bmp, 0, H, px, ref info, 0) == 0) return null;

            double r = 0, g = 0, b = 0, total = 0;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    // leave out cells that overlap the notch (its black would darken everything)
                    double sx = area.Left + (x + 0.5) * aw / W, sy = area.Top + (y + 0.5) * ah / H;
                    if (sx >= skip.Left - aw / W && sx <= skip.Right + aw / W && sy >= skip.Top && sy <= skip.Bottom + ah / H) continue;
                    int i = (y * W + x) * 4;
                    double bb = px[i], gg = px[i + 1], rr = px[i + 2];
                    double max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
                    double sat = max <= 0 ? 0 : (max - min) / max;
                    // vivid cells count much more: a green button outweighs a page of white
                    double w = 0.12 + sat * sat * 3.0 * (0.35 + max / 255);
                    r += rr * w; g += gg * w; b += bb * w; total += w;
                }
            }
            if (total <= 0) return null;
            return Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(b / total));
        }
        catch { return null; }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// A colour that glows on black: same hue, lifted to a lit, saturated tone. Near-greys stay a soft neutral
    /// (a white page gives a gentle silver, not a fake colour).
    /// </summary>
    public static Color Glow(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 0.06) return Neutral; // a white, grey or dark app: soft silver
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h /= 6;
        s = Math.Clamp(Math.Max(s, 0.45), 0, 0.95);
        l = 0.58;
        return FromHsl(h, s, l);
    }

    /// <summary>What a colourless app (white page, dark editor) glows with.</summary>
    public static readonly Color Neutral = Color.FromRgb(0xB8, 0xBC, 0xC6);

    static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Hue(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        return Color.FromRgb((byte)(Hue(h + 1 / 3.0) * 255), (byte)(Hue(h) * 255), (byte)(Hue(h - 1 / 3.0) * 255));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, int start, int lines, byte[] bits, ref BITMAPINFOHEADER info, int usage);
}
