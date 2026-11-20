using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace DynamicNotch;

/// <summary>
/// The open notch's top line keeps its room:
///  • the quick buttons (desktop icons, theme, lock) fold into one small button on every page but Home, and fan out
///    the moment the pointer reaches them; so a page's name in the middle never runs under them;
///  • the page's name takes only the room left between the clock and the buttons, and trims itself if it must.
/// </summary>
public partial class NotchWindow
{
    StackPanel? _clusterRow;
    Controls.NotchFace? _headFace;
    readonly System.Windows.Media.TranslateTransform _headFaceAt = new();
    int _headLastPos;
    bool _clusterFolded;
    readonly Dictionary<Button, Thickness> _clusterMargins = new();

    void WireHeader()
    {
        _clusterRow = QuickCluster.Children.OfType<StackPanel>().FirstOrDefault();
        QuickCluster.MouseEnter += (_, _) => ClusterFold(false);
        QuickCluster.MouseLeave += (_, _) => After(450, ClusterFit);
        PageTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        if (PageTitle.Parent is Grid header) header.SizeChanged += (_, _) => FitTitle();
        BatteryPanel.SizeChanged += (_, _) => FitTitle();

        // a tiny face beside the page's name: it glances the way you swipe, winks when you turn something on
        if (PageTitle.Parent is Grid head)
        {
            _headFace = new Controls.NotchFace(10) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = _headFaceAt, Visibility = Visibility.Collapsed };
            Grid.SetColumnSpan(_headFace, 3);
            head.Children.Add(_headFace);
            PageTitle.SizeChanged += (_, _) => _headFaceAt.X = -(PageTitle.ActualWidth / 2 + 11);
        }
        Expanded.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (_headFace is not { IsVisible: true } face || e.OriginalSource is not System.Windows.Controls.Primitives.ToggleButton t) return;
            if (t.IsChecked == true) face.Play(Controls.NotchFace.Emote.Wink, .8); else face.Hop(.35);
        }), handledEventsToo: true);
    }

    /// <summary>Folded everywhere but Home (and while the pointer isn't on them).</summary>
    void ClusterFit() => ClusterFold(_page != 0 && !QuickCluster.IsMouseOver);

    void ClusterFold(bool fold)
    {
        if (_clusterRow == null || fold == _clusterFolded) return;
        _clusterFolded = fold;
        var t = TimeSpan.FromMilliseconds(Eco ? 1 : fold ? 200 : 260);
        IEasingFunction ease = fold ? new CubicEase { EasingMode = EasingMode.EaseIn } : new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        foreach (var b in _clusterRow.Children.OfType<Button>())
        {
            if (ReferenceEquals(b, LockBtn)) continue; // the lock stays: it's the one that folds the others
            if (!_clusterMargins.ContainsKey(b)) _clusterMargins[b] = b.Margin;
            var m = _clusterMargins[b];
            b.BeginAnimation(WidthProperty, new DoubleAnimation(fold ? 0 : 18, t) { EasingFunction = ease });
            b.BeginAnimation(MarginProperty, new ThicknessAnimation(fold ? new Thickness(0) : m, t) { EasingFunction = ease });
            b.BeginAnimation(OpacityProperty, new DoubleAnimation(fold ? 0 : 1, t));
            b.IsHitTestVisible = !fold;
        }
        After((int)t.TotalMilliseconds + 20, FitTitle);
    }

    /// <summary>The page's name: centred, in the room between the clock and the buttons.</summary>
    void FitTitle()
    {
        if (PageTitle.Parent is not Grid header || header.ActualWidth <= 0) return;
        double left = header.Children.OfType<StackPanel>().FirstOrDefault() is { IsVisible: true } l ? l.ActualWidth : 0;
        double right = BatteryPanel.IsVisible ? BatteryPanel.ActualWidth : 0;
        double room = 2 * (header.ActualWidth / 2 - Math.Max(left, right) - 10);
        PageTitle.MaxWidth = Math.Max(48, room);
    }

    /// <summary>A page changed: the buttons fold or open, the name refits.</summary>
    void HeaderPage()
    {
        if (_headFace != null)
        {
            bool show = _page != 0 && _settings.NotchFace;
            _headFace.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                _headFace.Neon = _cyber; _headFace.Accent = PalAccent(); _headFace.Eco = Eco;
                int pos = PagePos(_page), dir = Math.Sign(pos - _headLastPos);
                _headFace.LookAt(dir, 0);         // it looks the way the pages went
                _headFace.Play(Controls.NotchFace.Emote.Happy, .9);
            }
            _headLastPos = PagePos(_page);
        }
        ClusterFit();
        Dispatcher.BeginInvoke(FitTitle, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
