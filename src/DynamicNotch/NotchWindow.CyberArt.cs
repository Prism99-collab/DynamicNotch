using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace DynamicNotch;

/// <summary>
/// Cyberpunk only: a faded picture behind each page of the control center. The app ships with a set for the first
/// five pages; yours win: the notch looks in %APPDATA%\DynamicNotch\Backgrounds for page1.*, page2.* … page6.*
/// (png, jpg, bmp, webp) first. Replace or add a file and it shows the next time the theme comes on; a page with
/// neither stays clean.
/// They sit on their own strip behind the pages and slide with them (the same transform), drawn once and reused
/// while they move. Each is decoded at the size it's shown (not the photo's full size), barely there (14 %),
/// fading out toward the edges, with the theme's scanlines over it. Off in every other theme, and released when
/// Cyberpunk is switched off.
/// Also: the pop-ups arrive the way they do in the game: a scan wipes them in from the left, the icon flickers
/// on like a hologram, and the title and the line under it decode behind a blinking block cursor.
/// </summary>
public partial class NotchWindow
{
    static string BackgroundsFolder => Path.Combine(Settings.Folder, "Backgrounds");
    static readonly string[] ArtExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
    Grid? _artHost;
    Border _artA = null!, _artB = null!, _artFront = null!;
    readonly ImageBrush?[] _art = new ImageBrush?[6];
    const double ArtLevel = 0.15;

    void CyberArt(bool on)
    {
        try
        {
            if (!on)
            {
                if (_artHost == null) return;
                _artHost.Visibility = Visibility.Collapsed;
                _artA.Background = _artB.Background = null; // the pictures go with the theme
                Array.Clear(_art);
                return;
            }
            if (_artHost == null)
            {
                // behind everything in the open panel, header and dots included: the picture covers the whole page
                _artHost = new Grid { IsHitTestVisible = false };
                Grid.SetRowSpan(_artHost, 3);
                _artA = new Border { Opacity = 0, CacheMode = new BitmapCache() };
                _artB = new Border { Opacity = 0, CacheMode = new BitmapCache() };
                _artHost.Children.Add(_artA);
                _artHost.Children.Add(_artB);
                _artFront = _artA;
                Expanded.Children.Insert(0, _artHost);
            }
            for (int i = 0; i < _art.Length; i++) _art[i] = LoadArt(i + 1);
            _artHost.Visibility = Visibility.Visible;
            _artA.Background = _artB.Background = null;
            _artA.Opacity = _artB.Opacity = 0;
            CyberArtShow();
        }
        catch (Exception ex) { Services.AppLog.Write("cyber backgrounds: " + ex.Message); } // a bad picture never breaks the theme
    }

    /// <summary>The current page's picture: a soft cross-fade from the last one.</summary>
    void CyberArtShow()
    {
        if (_artHost == null || !_cyber) return;
        var brush = _page < _art.Length ? _art[_page] : null;
        if (ReferenceEquals(_artFront.Background, brush) && _artFront.Opacity > 0) return;
        var back = ReferenceEquals(_artFront, _artA) ? _artB : _artA;
        back.Background = brush;
        var span = TimeSpan.FromMilliseconds(Eco ? 1 : 340);
        back.BeginAnimation(OpacityProperty, new DoubleAnimation(brush == null ? 0 : ArtLevel, span));
        _artFront.BeginAnimation(OpacityProperty, new DoubleAnimation(0, span));
        _artFront = back;
    }

    void CyberArtPages() => CyberArtShow();

    /// <summary>The picture for a page, decoded at twice the page's size (sharp, and a few MB at most), or nothing.</summary>
    static ImageBrush? LoadArt(int page)
    {
        try
        {
            // yours first (Backgrounds folder), else the one the app ships with, else none
            string? file = Directory.Exists(BackgroundsFolder)
                ? Directory.EnumerateFiles(BackgroundsFolder, $"page{page}.*").FirstOrDefault(f => ArtExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                : null;
            Stream? bundled = null;
            if (file == null)
            {
                var asm = typeof(NotchWindow).Assembly;
                string? name = asm.GetManifestResourceNames().FirstOrDefault(n => n.StartsWith($"DynamicNotch.Backgrounds.page{page}.", StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;
                bundled = asm.GetManifestResourceStream(name);
                if (bundled == null) return null;
            }
            using var _ = bundled;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            if (file != null) bmp.UriSource = new Uri(file); else bmp.StreamSource = bundled;
            bmp.CacheOption = BitmapCacheOption.OnLoad;          // read now, don't keep the file open
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = (int)(PageWidth * 2);
            bmp.EndInit();
            bmp.Freeze();
            var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
            brush.Freeze();
            return brush;
        }
        catch (Exception ex)
        {
            Services.AppLog.Write($"cyber background {page}: {ex.Message}");
            return null;
        }
    }

    DateTime _lastPulse;
    IntPtr _pulseWindow;
    LiveActivity _pulseActivity;

    /// <summary>
    /// Something happened on the PC (another app came to the front, a song changed or paused, a new activity, a
    /// device's battery, a side pill appearing): the cyber skin flickers a tiny glitch. At most one every 0.7 s,
    /// never in a game, hidden, or on battery mode.
    /// </summary>
    void CyberPulse()
    {
        if (!_cyber || Eco || _hidden || _mode == Mode.Game) return;
        var now = DateTime.UtcNow;
        if ((now - _lastPulse).TotalMilliseconds < 700) return;
        _lastPulse = now;
        CyberGlitch(strong: false, light: true);
    }

    /// <summary>
    /// A new pop-up in the cyber skin: a scan wipes it in from the left, its icon flickers on like a hologram, the
    /// line under the title decodes a beat after it.
    /// </summary>
    void CyberAlertIn()
    {
        if (!_cyber || Eco) return;
        // the wipe: an opacity mask whose edge sweeps across, then it's gone (nothing stays applied)
        var edge = new GradientStop(Colors.White, 0);
        var fade = new GradientStop(Colors.Transparent, 0.06);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5), GradientStops = { edge, fade } };
        Osd.OpacityMask = mask;
        var span = TimeSpan.FromMilliseconds(300);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var run = new DoubleAnimation(0, 1.06, span) { EasingFunction = ease };
        run.Completed += (_, _) => { if (ReferenceEquals(Osd.OpacityMask, mask)) Osd.OpacityMask = null; };
        edge.BeginAnimation(GradientStop.OffsetProperty, run);
        fade.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(0.06, 1.12, span) { EasingFunction = ease });

        // the icon (or the app's picture) comes on like a hologram finding its signal
        foreach (UIElement icon in new UIElement[] { OsdGlyph, OsdArt })
            if (icon.Visibility == Visibility.Visible) Steps(icon, OpacityProperty, 36, 0, 1, 0.25, 1, 0.55, 1);
        CyberDecode(OsdSub, cursor: true, holdMs: 120);
    }
}
