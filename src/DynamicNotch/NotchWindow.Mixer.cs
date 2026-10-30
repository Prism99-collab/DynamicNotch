using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The mini mixer: the third tab of the sound picker. Every app making sound on the current output gets a row,
/// its icon, its name and its own volume; click the icon to mute just that app. Apps playing right now come first.
/// The list follows what changes elsewhere (a new app starts, a volume moves in Windows' own mixer) while it's open.
/// </summary>
public partial class NotchWindow
{
    sealed class MixerRow
    {
        public required PillSlider Slider;
        public required TextBlock Percent;
        public required Border Icon;
        public required TextBlock MuteMark;
        public bool Muted;
    }

    bool _devicesMixer;
    int _mixerVersion;
    string _mixerKeys = "";
    readonly Dictionary<string, MixerRow> _mixerRows = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer _mixerTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };

    void WireMixer()
    {
        MixerTab.Click += (_, _) => { _devicesMixer = true; _devicesInput = false; RenderDevices(); };
        _mixerTimer.Tick += (_, _) =>
        {
            if (!_devicesOpen || !_devicesMixer || _mode != Mode.Expanded) { StopMixer(); return; }
            _ = LoadMixerAsync(rebuild: false);
        };
    }

    void RenderMixer()
    {
        StyleTab(OutputTab, false);
        StyleTab(InputTab, false);
        StyleTab(MixerTab, true);
        _mixerKeys = "";
        _ = LoadMixerAsync(rebuild: true);
        _mixerTimer.Start();
    }

    void StopMixer()
    {
        if (!_mixerTimer.IsEnabled && _mixerRows.Count == 0) return;
        _mixerTimer.Stop();
        _mixerRows.Clear();
        _mixerKeys = "";
        _audio.ReleaseMixer();
    }

    async Task LoadMixerAsync(bool rebuild)
    {
        int version = ++_mixerVersion;
        var apps = await _audio.MixerAppsAsync();
        if (version != _mixerVersion || !_devicesOpen || !_devicesMixer) return;

        // playing now first, then the rest; Windows' system sounds last
        apps = apps.OrderBy(a => a.IsSystem).ThenByDescending(a => a.Active).ThenBy(a => a.Key, StringComparer.OrdinalIgnoreCase).ToList();
        string keys = string.Join("|", apps.Select(a => a.Key));
        if (!rebuild && keys == _mixerKeys)
        {
            // same apps: just follow volumes / mutes changed elsewhere (never under a finger that's dragging)
            foreach (var a in apps)
                if (_mixerRows.TryGetValue(a.Key, out var row) && !row.Slider.IsDragging) ApplyMixerValues(row, a.Volume, a.Muted);
            return;
        }
        if (rebuild) AppLog.Write($"mixer: {apps.Count} apps ({apps.Count(a => a.Active)} playing)");
        _mixerKeys = keys;
        _mixerRows.Clear();
        DevicesList.Children.Clear();
        if (apps.Count == 0)
        {
            DevicesList.Children.Add(new TextBlock
            {
                Text = "No app is making sound right now", FontSize = 11.5, Foreground = DimBrush, Margin = new Thickness(10, 8, 10, 0),
            });
            return;
        }
        foreach (var a in apps) DevicesList.Children.Add(MixerRowFor(a));
    }

    FrameworkElement MixerRowFor(MixerApp app)
    {
        var grid = new Grid { Height = 34, Margin = new Thickness(4, 0, 6, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        // the app's icon doubles as its mute button
        var icon = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = (Brush)FindResource("Tile"),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
            ToolTip = "Mute / unmute",
        };
        var glyph = Glyph(app.IsSystem ? "" : "", 11, DimBrush);
        icon.Child = glyph;
        var muteMark = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -4, -4),
            Background = Red, Padding = new Thickness(2), Visibility = Visibility.Collapsed,
        };
        var iconHost = new Grid { Width = 26, HorizontalAlignment = HorizontalAlignment.Left };
        iconHost.Children.Add(icon);
        iconHost.Children.Add(muteMark);
        grid.Children.Add(iconHost);

        var name = new TextBlock
        {
            Text = app.IsSystem ? "System sounds" : app.Path != null ? System.IO.Path.GetFileNameWithoutExtension(app.Path) : "App",
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = app.Active || app.IsSystem ? Brushes.White : DimBrush, Margin = new Thickness(2, 0, 8, 0),
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var slider = new PillSlider { Height = 20, TrackHeight = 6, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(slider, 2);
        grid.Children.Add(slider);

        var pct = new TextBlock
        {
            FontSize = 10.5, Foreground = DimBrush, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
        };
        Grid.SetColumn(pct, 3);
        grid.Children.Add(pct);

        var row = new MixerRow { Slider = slider, Percent = pct, Icon = icon, MuteMark = muteMark };
        _mixerRows[app.Key] = row;
        ApplyMixerValues(row, app.Volume, app.Muted);

        string key = app.Key;
        slider.ValueChanged += v =>
        {
            _audio.SetMixerVolume(key, (float)v);
            pct.Text = $"{Math.Round(v * 100)}";
            if (row.Muted && v > 0) SetMixerMuteLook(row, false); // dragging up unmutes
        };
        iconHost.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            bool mute = !row.Muted;
            _audio.SetMixerMute(key, mute);
            SetMixerMuteLook(row, mute);
            Bump(mute ? -1 : 1);
        };

        // the real name and icon ("Spotify", "Discord"...), looked up off the UI thread
        if (app.Path != null)
            _ = DescribeAppAsync(app.Path).ContinueWith(t => Dispatcher.InvokeAsync(() =>
            {
                if (t.IsFaulted) return;
                name.Text = t.Result.App;
                if (t.Result.Icon is { } img) { icon.Background = IconBrush(img); icon.Child = null; }
            }));

        return new Border { Child = grid, Background = Brushes.Transparent };
    }

    void ApplyMixerValues(MixerRow row, float volume, bool muted)
    {
        if (Math.Abs(row.Slider.Value - volume) > 0.005) row.Slider.Value = volume;
        row.Percent.Text = $"{Math.Round(volume * 100)}";
        SetMixerMuteLook(row, muted);
    }

    static void SetMixerMuteLook(MixerRow row, bool muted)
    {
        row.Muted = muted;
        row.MuteMark.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        row.Icon.Opacity = muted ? 0.45 : 1;
        row.Slider.Opacity = muted ? 0.45 : 1;
    }
}
