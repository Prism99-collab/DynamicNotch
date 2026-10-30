using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The System page, live: a download card with the real progress (the browser's own total), the last minute of
/// network traffic scrolling behind the speeds, and the battery card's power going in or out.
/// </summary>
public partial class NotchWindow
{
    string? _dlDone;          // a download that just finished: the card offers to open it for a few seconds
    long _dlDoneSize;
    bool _dlShown;
    int _dlDoneVersion;
    double _dlSheenSecs;
    Brush? _dlFillBrush;
    static readonly Brush PowerOut = Frozen(Color.FromRgb(0xFF, 0xB3, 0x40));

    void WireSystemLive()
    {
        LiveGraph.ClipRounded(NetGraphHost, 10);
        LiveGraph.ClipRounded(PowerCardHost, 14);
        LiveGraph.ClipRounded(DlHost, 14);
        NetDownGraph.Color = ((SolidColorBrush)Green).Color;
        NetUpGraph.FillOpacity = 0.16;
        NetUpGraph.LineOpacity = 0.75;
        NetUpGraph.Color = ((SolidColorBrush)Blue).Color;
        PowerGraph.FillOpacity = 0.22;
        PowerGraph.Color = ((SolidColorBrush)Green).Color;
        DlGraph.FillOpacity = 0.2;
        DlGraph.LineOpacity = 0.55;
        DlGraph.Color = ((SolidColorBrush)Blue).Color;
        _dlFillBrush = DlFill.Background;

        _files.DownloadsChanged += () => { if (SystemVisible) RenderDownload(); };
        _files.DownloadCompleted += path =>
        {
            _dlDone = path;
            try { _dlDoneSize = new FileInfo(path).Length; } catch { _dlDoneSize = 0; }
            int v = ++_dlDoneVersion;
            if (SystemVisible) RenderDownload();
            After(9000, () => { if (v != _dlDoneVersion) return; _dlDone = null; if (SystemVisible) RenderDownload(); });
        };
        DlOpen.Click += (_, _) => { if (_dlDone != null) Launch(_dlDone); };
        DlFolder.Click += (_, _) =>
        {
            if (_dlDone != null) Launch("explorer.exe", $"/select,\"{_dlDone}\"");
            else if (_files.DownloadsDir != null) Launch(_files.DownloadsDir);
        };
    }

    bool SystemVisible => _mode == Mode.Expanded && _page == 1;

    /// <summary>The page just came into view: fresh graphs (they only sample while you look) and the download card.</summary>
    void OnSystemShown()
    {
        NetDownGraph.Clear();
        NetUpGraph.Clear();
        PowerGraph.Clear();
        DlGraph.Clear();
        _dlShown = false;
        DlCard.Visibility = Visibility.Collapsed;
        SysCards.BeginAnimation(OpacityProperty, null);
        SysCards.Opacity = 1;
        RenderDownload();
    }

    /// <summary>Feeds the network graph: download and upload share one scale so they compare honestly.</summary>
    void PushNetwork(double down, double up)
    {
        double max = Math.Max(Math.Max(NetDownGraph.Peak, NetUpGraph.Peak), Math.Max(down, up)) * 1.2;
        NetDownGraph.Max = NetUpGraph.Max = Math.Max(max, 64 * 1024); // a quiet line stays low instead of filling the strip
        NetDownGraph.Push(down, _sysTimer.Interval);
        NetUpGraph.Push(up, _sysTimer.Interval);
    }

    /// <summary>Power in (charging, green) or out (on battery, amber), and a bolt that breathes while it charges.</summary>
    void PushPower(BatteryDetails b)
    {
        if (!_power.HasBattery || !b.Present) { PowerGraph.Visibility = Visibility.Collapsed; ChargeBolt.Visibility = Visibility.Collapsed; return; }
        PowerGraph.Visibility = Visibility.Visible;
        bool charging = b.Charging;
        PowerGraph.Color = charging ? ((SolidColorBrush)Green).Color : ((SolidColorBrush)PowerOut).Color;
        PowerGraph.Floor = 5;
        PowerGraph.Push(b.WattsNow ?? 0, _sysTimer.Interval);

        bool bolt = charging && !Eco;
        if (bolt == (ChargeBolt.Visibility == Visibility.Visible)) return;
        ChargeBolt.Visibility = bolt ? Visibility.Visible : Visibility.Collapsed;
        if (bolt)
            ChargeBolt.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        else ChargeBolt.BeginAnimation(OpacityProperty, null);
    }

    // ───────────────────────────── the download card ─────────────────────────────

    void RenderDownload()
    {
        var items = _files.Items;
        var d = items.FirstOrDefault();
        bool done = d == null && _dlDone != null;
        ShowDownloadCard(d != null || done);
        if (d == null && !done) return;

        DlGraph.Push(_files.BytesPerSecond, TimeSpan.FromSeconds(1));
        if (done)
        {
            string name = Path.GetFileName(_dlDone!);
            DlName.Text = name;
            DlDetail.Text = _dlDoneSize > 0 ? $"Done · {SystemMonitor.Bytes(_dlDoneSize)}" : "Done";
            DlPercent.Visibility = Visibility.Collapsed;
            DlActions.Visibility = Visibility.Visible;
            SetDlIcon(name);
            SetDlProgress(1, Green);
            return;
        }

        DlPercent.Visibility = Visibility.Visible;
        DlActions.Visibility = Visibility.Collapsed;
        DlName.Text = d!.Name;
        SetDlIcon(d.Name);
        string speed = d.Speed > 1024 ? $" · {SystemMonitor.Bytes(d.Speed)}/s" : "";
        string more = items.Count > 1 ? $" · +{items.Count - 1} more" : "";
        if (d.Progress is { } p)
        {
            string left = d.Left is { } l ? $" · {ShortDuration(l)} left" : "";
            DlDetail.Text = $"{SystemMonitor.Bytes(d.Bytes)} of {SystemMonitor.Bytes(d.Total)}{speed}{left}{more}";
            DlPercent.Text = $"{p * 100:0}%";
        }
        else
        {
            DlDetail.Text = $"{SystemMonitor.Bytes(d.Bytes)}{speed}{more}";
            DlPercent.Text = "";
        }
        SetDlProgress(d.Progress, null);
    }

    static string ShortDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(1, (int)t.TotalSeconds)} s" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalHours} h {t.Minutes} min";

    void ShowDownloadCard(bool on)
    {
        if (on == _dlShown) return;
        _dlShown = on;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (on)
        {
            DlCard.Visibility = Visibility.Visible;
            DlCardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(520)) { EasingFunction = new SpringEase { Damping = 0.7 } });
            DlCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            SysCards.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
            SysCards.IsHitTestVisible = false;
            DlFill.BeginAnimation(WidthProperty, null);
            DlFill.Width = 0;
            _dlSheenSecs = 0;
        }
        else
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) => { if (!_dlShown) DlCard.Visibility = Visibility.Collapsed; };
            DlCard.BeginAnimation(OpacityProperty, fade);
            DlCardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, TimeSpan.FromMilliseconds(160)));
            SysCards.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(240)) { BeginTime = TimeSpan.FromMilliseconds(80) });
            SysCards.IsHitTestVisible = true;
            DlSheenShift.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }

    /// <summary>The bar glides to the real share; a light keeps travelling along it so you can see it's alive.</summary>
    void SetDlProgress(double? progress, Brush? tint)
    {
        double track = DlTrack.ActualWidth;
        if (track <= 0) { DlTrack.UpdateLayout(); track = DlTrack.ActualWidth; }
        double target = progress is { } p ? Math.Max(5, track * p) : 0;
        DlFill.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(progress >= 1 ? 350 : 950))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        DlFill.Background = tint ?? _dlFillBrush;

        // the light: across the whole track while the size is unknown, quicker when it's going fast; gone when done
        double secs = progress >= 1 || Eco ? 0 : progress == null ? 1.1 : 1.8;
        if (secs == _dlSheenSecs) return;
        _dlSheenSecs = secs;
        if (secs == 0) { DlSheenShift.BeginAnimation(TranslateTransform.XProperty, null); DlSheen.Opacity = 0; return; }
        DlSheen.Opacity = 1;
        DlSheenShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-DlSheen.Width, track, TimeSpan.FromSeconds(secs))
            { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    string? _dlIconFor;

    void SetDlIcon(string name)
    {
        string ext = Path.GetExtension(name);
        if (ext == _dlIconFor) return;
        _dlIconFor = ext;
        DlIcon.Background = Frozen(Color.FromRgb(0x26, 0x26, 0x29));
        DlGlyph.Visibility = Visibility.Visible;
        if (ext.Length == 0) return;
        _ = Task.Run(() => IconHelper.ForType(ext)).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            if (_dlIconFor != ext || t.Result == null) return;
            DlIcon.Background = IconBrush(t.Result);
            DlGlyph.Visibility = Visibility.Collapsed;
        }));
    }
}
