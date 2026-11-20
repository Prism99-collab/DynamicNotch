using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;

using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The Clipboard &amp; Files page's look, shared by its three lists (clipboard, shelf, recent files):
///  • one line per item: its picture or icon, a readable name, a small type tag, and how long ago on the right;
///  • readable names: a screenshot is "Screenshot · 13:05", not "Capture d'écran 2026-11-20 130512.png"; a link
///    shows its site; the file type is a small coloured tag (PDF, ZIP, PNG...);
///  • actions (pin, copy, remove) appear under the pointer, so the list stays calm;
///  • the rows arrive in a quick cascade when the page opens, and something just copied glows as it lands;
///  • an empty list says so in one line, with Pixo's face.
/// </summary>
public partial class NotchWindow
{
    const double ClipRowHeight = 30;

    /// <summary>"now", "4 min", "2 h", "Yesterday", "20 Nov".</summary>
    static string ShortAgo(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return Loc.T("now");
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min";
        if (t.Date == DateTime.Today) return $"{(int)d.TotalHours} h";
        if (t.Date == DateTime.Today.AddDays(-1)) return Loc.T("Yesterday");
        return t.ToString("d MMM");
    }

    static readonly Regex ScreenshotName = new(@"^(capture d.?[ée]cran|screenshot|screen shot|captura de pantalla|bildschirmfoto|schermata)", RegexOptions.IgnoreCase);

    /// <summary>A file's name as a person would say it, and its type as a short tag.</summary>
    static (string Name, string? Tag) NiceFileName(string fileName, DateTime when)
    {
        string ext = System.IO.Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        string bare = System.IO.Path.GetFileNameWithoutExtension(fileName);
        if (ScreenshotName.IsMatch(bare)) return ($"{Loc.T("Screenshot")} · {when:HH:mm}", ext.Length is > 0 and <= 4 ? ext : null);
        if (bare.Length == 0) bare = fileName;
        return (bare, ext.Length is > 0 and <= 4 ? ext : null);
    }

    /// <summary>A copied text in one readable line: a link shows its site and path, anything else its words.</summary>
    static string NiceClipText(string text)
    {
        string line = string.Join(" ", text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (LooksLikeLink(line) && Uri.TryCreate(line, UriKind.Absolute, out var u))
            return u.Host.Replace("www.", "") + (u.AbsolutePath.Length > 1 ? u.AbsolutePath : "");
        return line;
    }

    static Color TagColor(string tag) => tag switch
    {
        "PDF" => Color.FromRgb(0xFF, 0x5A, 0x4F),
        "PNG" or "JPG" or "JPEG" or "GIF" or "WEBP" or "HEIC" or "BMP" => Color.FromRgb(0x30, 0xD1, 0x58),
        "MP4" or "MOV" or "MKV" or "AVI" or "WEBM" => Color.FromRgb(0xBF, 0x5A, 0xF2),
        "MP3" or "WAV" or "FLAC" or "M4A" or "OGG" => Color.FromRgb(0xFF, 0x37, 0x5F),
        "ZIP" or "RAR" or "7Z" or "TAR" or "GZ" => Color.FromRgb(0xFF, 0x9F, 0x0A),
        "DOC" or "DOCX" or "TXT" or "RTF" or "MD" => Color.FromRgb(0x0A, 0x84, 0xFF),
        "XLS" or "XLSX" or "CSV" => Color.FromRgb(0x32, 0xD7, 0x4B),
        "PPT" or "PPTX" => Color.FromRgb(0xFF, 0x6B, 0x2C),
        "EXE" or "MSI" => Color.FromRgb(0x8E, 0x8E, 0x93),
        "LINK" => Color.FromRgb(0x64, 0xD2, 0xFF),
        _ => Color.FromRgb(0x8E, 0x8E, 0x93),
    };

    /// <summary>One line: lead (picture or icon), the name, a type tag, and on the right the time (or the actions under the pointer).</summary>
    Grid ClipLine(FrameworkElement lead, string title, string? tag, string right, params FrameworkElement[] actions)
    {
        var row = new Grid { Height = ClipRowHeight };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(lead.Width > 24 ? lead.Width + 8 : 28) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lead.VerticalAlignment = VerticalAlignment.Center;
        lead.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(lead);

        var words = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        if (tag != null)
        {
            var c = TagColor(tag);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(_cyber ? 0 : 4), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Frozen(Color.FromArgb(0x2A, c.R, c.G, c.B)),
                Child = new TextBlock { Text = tag, FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Frozen(Lighten(c, 0.25)) },
            };
            DockPanel.SetDock(chip, Dock.Right);
            words.Children.Add(chip);
        }
        words.Children.Add(new TextBlock { Text = title, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(words, 1);
        row.Children.Add(words);

        // the right end: the time; under the pointer, the actions take its place
        var side = new Grid { Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        var time = new TextBlock { Text = right, FontSize = 10, Foreground = DimBrush, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        side.Children.Add(time);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0, IsHitTestVisible = false };
        foreach (var a in actions) bar.Children.Add(a);
        side.Children.Add(bar);
        Grid.SetColumn(side, 2);
        row.Children.Add(side);
        row.Background = Brushes.Transparent;
        row.MouseEnter += (_, _) => { if (actions.Length == 0) return; bar.Opacity = 1; bar.IsHitTestVisible = true; time.Opacity = 0; };
        row.MouseLeave += (_, _) => { bar.Opacity = 0; bar.IsHitTestVisible = false; time.Opacity = 1; };
        return row;
    }

    /// <summary>An empty list, said in one line with the face.</summary>
    FrameworkElement ClipEmptyState(string title, string sub)
    {
        var face = new NotchFace(13) { Neon = _cyber, Accent = PalAccent(), Eco = Eco, HorizontalAlignment = HorizontalAlignment.Center };
        face.SetOutfit(FaceOutfitNow(), animate: false);
        var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 16, 4, 0) };
        box.Children.Add(face);
        box.Children.Add(new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        box.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = DimBrush, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 170, Margin = new Thickness(0, 2, 0, 0) });
        return box;
    }

    /// <summary>A small round action under the pointer (pin, copy, remove).</summary>
    Button ClipAction(string glyph, string tip, Action act, Brush? tone = null)
    {
        var b = new Button { Style = (Style)FindResource("CircleButton"), Width = 22, Height = 22, Margin = new Thickness(1, 0, 0, 0), ToolTip = tip, Content = Glyph(glyph, 10, tone ?? Brushes.White) };
        b.Click += (_, e) => { e.Handled = true; act(); };
        return b;
    }

    /// <summary>The rows arrive one after another, quickly (only the first few: a long list shouldn't wait).</summary>
    void ClipCascade(Panel list)
    {
        if (Eco) return;
        int i = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (UIElement el in list.Children)
        {
            if (i >= 8) break;
            var begin = TimeSpan.FromMilliseconds(22 * i++);
            var shift = new TranslateTransform(0, 5);
            el.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(240)) { BeginTime = begin, EasingFunction = ease });
            el.Opacity = 0;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = begin });
        }
    }

    /// <summary>Something just copied: its row glows once in the theme's colour as it lands at the top.</summary>
    void ClipGlow(FrameworkElement row)
    {
        if (Eco) return;
        var c = _cyber ? CyCyan : PalAccent();
        var wash = new SolidColorBrush(Color.FromArgb(0, c.R, c.G, c.B));
        if (row is Control ctl) ctl.Background = wash; else if (row is Panel p) p.Background = wash;
        var glow = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1100) };
        glow.KeyFrames.Add(new EasingColorKeyFrame(Color.FromArgb(0x40, c.R, c.G, c.B), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        glow.KeyFrames.Add(new EasingColorKeyFrame(Color.FromArgb(0, c.R, c.G, c.B), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        wash.BeginAnimation(SolidColorBrush.ColorProperty, glow);
    }
}
