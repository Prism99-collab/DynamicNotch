using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Files everywhere, and the desktop first when you've hidden it:
///  • the notch's own list of names on every drive (built in the background, kept up to date) joins Windows' index,
///    so a file on D: or in a folder Windows never indexed is found the same way, on any PC;
///  • with the desktop's icons hidden (the eye button), what's on the desktop comes first in the results, sits under
///    its own "Desktop" label, and shows as a row of tiles in the empty search, so hiding the icons never hides the
///    files; "desktop:" lists only what's on it.
/// </summary>
public partial class NotchWindow
{
    readonly FileIndex _fileIndex = new();
    List<FileIndex.Hit> _fiItems = new();
    string? _fiAsked;
    bool _filesStarted;

    void WireFileIndex()
    {
        _fileIndex.Results += (q, items) => Dispatcher.InvokeAsync(() =>
        {
            if (!_launcherOpen || !string.Equals(_fiAsked, q, StringComparison.Ordinal)) return;
            _fiItems = items;
            if (_actionsFor == null) Search(merge: true);
        });
        EnsureFileIndex();
    }

    /// <summary>Starts the list of names (once) when "Files across the PC" is on. It only works while you're not gaming or saving battery.</summary>
    /// <summary>How many tiles (about 77 pixels each, with the button's padding) fit across the results area.</summary>
    int TilesPerRow => Math.Max(3, (int)(((_launchBody?.ActualWidth ?? 0) is > 100 and var w ? w : ExpandedSize.Width - 36) / 77));

    bool Started(FileIndex f) { EnsureFileIndex(); return f.Ready; }

    void EnsureFileIndex()
    {
        if (_filesStarted || !_settings.SearchWholePc) return;
        _filesStarted = true;
        _fileIndex.Start(() => !InGame && !Eco);
    }

    // ───────────────────────────── the desktop ─────────────────────────────

    static readonly string UserDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    static readonly string PublicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <summary>Right on the desktop (what would be an icon there), not somewhere deep inside a folder that's on it.</summary>
    static bool OnDesktop(string path)
    {
        string? dir = Path.GetDirectoryName(path.TrimEnd('\\'));
        return dir != null && (UserDesktop.Length > 0 && string.Equals(dir, UserDesktop, StringComparison.OrdinalIgnoreCase)
            || PublicDesktop.Length > 0 && string.Equals(dir, PublicDesktop, StringComparison.OrdinalIgnoreCase));
    }

    static bool IsShortcut(string path) => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);

    bool _desktopHidden;
    List<LauncherIndex.Entry>? _desktopItems;

    /// <summary>What's on the desktop gets a lift: a big one while its icons are hidden (it's the only way to reach them), a small one otherwise.</summary>
    double DesktopLift(LauncherIndex.Entry e) =>
        e.Kind is LauncherIndex.Kind.File or LauncherIndex.Kind.Folder && OnDesktop(e.Target) ? (_desktopHidden ? 250 : 40) : 0;

    /// <summary>Everything on the desktop (yours and the shared one), newest first, read once per opening.</summary>
    List<LauncherIndex.Entry> DesktopItems()
    {
        if (_desktopItems != null) return _desktopItems;
        var list = new List<(LauncherIndex.Entry E, DateTime When)>();
        foreach (var dir in new[] { UserDesktop, PublicDesktop }.Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || info.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    bool folder = info is DirectoryInfo;
                    list.Add((new LauncherIndex.Entry(info.Name, info.FullName, folder ? LauncherIndex.Kind.Folder : LauncherIndex.Kind.File, "Desktop"), info.LastWriteTime));
                }
            }
            catch { }
        }
        return _desktopItems = list.OrderByDescending(x => x.When).Select(x => x.E).ToList();
    }

    /// <summary>"desktop:" and what follows: only the desktop, filtered by the rest.</summary>
    void DesktopResults(string filter)
    {
        var items = DesktopItems();
        var picked = filter.Length == 0
            ? items.Select((e, i) => (e, s: 1000.0 - i))
            : items.Select(e => (e, s: LauncherIndex.Score(filter, e))).Where(x => x.s > 0);
        foreach (var (e, _) in picked.OrderByDescending(x => x.s).Take(40))
            _hits.Add(EntryHit(e, filter.Length > 0 ? filter : null) with { Section = null });
        if (_hits.Count == 0)
            _hits.Add(new Hit(filter.Length > 0 ? $"Nothing on the desktop called “{filter}”" : "The desktop is empty", "", "", null, () => Nudge()));
        _launchSel = 0;
        RenderHits(animate: true);
        UpdateGhost();
    }

    /// <summary>The empty search while the desktop's icons are hidden: what's on it, as tiles, so hiding never hides your files.</summary>
    void AddDesktopTiles(Func<double> gap)
    {
        if (!_desktopHidden || _launchStarters == null) return;
        var items = DesktopItems();
        if (items.Count == 0) return;
        _launchStarters.Children.Add(new TextBlock { Text = _cyber ? "DESKTOP // ICONS HIDDEN" : "Your desktop  ·  icons hidden", FontSize = 10, Foreground = DimBrush, Margin = new Thickness(4, gap(), 0, 6) });
        var row = new WrapPanel();
        int room = items.Count > TilesPerRow ? TilesPerRow - 1 : TilesPerRow; // one row, the last tile "+N more" when there's more
        foreach (var e in items.Take(room)) row.Children.Add(DesktopTile(e));
        if (items.Count > room)
        {
            // the rest, one key away
            var more = new Button { Style = (Style)FindResource("RowButton"), Height = 54, Width = 54, ToolTip = "Everything on the desktop" };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = $"+{items.Count - room}", FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = _cyber ? Frozen(CyYellow) : Brushes.White, FontFamily = _cyber ? CyFont : UiText });
            stack.Children.Add(new TextBlock { Text = "more", FontSize = 9.5, Foreground = DimBrush, HorizontalAlignment = HorizontalAlignment.Center });
            more.Content = stack;
            more.Click += (_, _) => { if (_launchBox == null) return; _launchBox.Text = "desktop: "; _launchBox.CaretIndex = _launchBox.Text.Length; _launchBox.Focus(); };
            row.Children.Add(more);
        }
        _launchStarters.Children.Add(row);
    }

    FrameworkElement DesktopTile(LauncherIndex.Entry e)
    {
        string name = IsShortcut(e.Target) ? Path.GetFileNameWithoutExtension(e.Name) : e.Name;
        var tile = new StackPanel { Width = 54 };
        var icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(_cyber ? 0 : 6), HorizontalAlignment = HorizontalAlignment.Center };
        IconInto(icon, e.Target);
        if (e.Kind == LauncherIndex.Kind.File && IsPicture(e.Target)) ShowThumb(icon, e.Target);
        tile.Children.Add(icon);
        tile.Children.Add(new TextBlock { Text = name, FontSize = 9.5, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 4, 2, 0), Foreground = DimBrush });
        var b = new Button { Style = (Style)FindResource("RowButton"), Content = tile, Height = 54, Margin = new Thickness(0, 0, 2, 0), ToolTip = name + "  ·  right-click shows it in its folder" };
        b.Click += (_, _) => { _index.Used(e); OpenTarget(e.Target); };
        b.MouseRightButtonUp += (_, ev) => { ev.Handled = true; CloseLauncher(giveBack: false); RevealInFolder(e.Target); };
        return b;
    }
}
