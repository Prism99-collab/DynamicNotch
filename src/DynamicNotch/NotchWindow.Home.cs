using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>Page 1: now playing, quick toggles, volume / brightness, sound output picker.</summary>
public partial class NotchWindow
{
    DateTime _lastUserChange;
    bool _devicesOpen;
    ImageSource? _brushFor;
    ImageBrush? _artBrush;

    bool RecentlyTouched => (DateTime.UtcNow - _lastUserChange).TotalMilliseconds < 900;

    void WireHome()
    {
        PlayBtn.Click += async (_, _) => { await _media.TogglePlayPauseAsync(); RefreshMedia(); };
        NextBtn.Click += async (_, _) => await _media.NextAsync();
        PrevBtn.Click += async (_, _) => await _media.PreviousAsync();
        ProgressSlider.ValueCommitted += async v =>
        {
            if (_media.Duration > TimeSpan.Zero) await _media.SeekAsync(TimeSpan.FromTicks((long)(_media.Duration.Ticks * v)));
        };

        VolSlider.ValueChanged += v => { _lastUserChange = DateTime.UtcNow; _audio.SetVolume((float)v); VolGlyph.Text = VolumeGlyph(); };
        MuteBtn.Click += (_, _) => { _lastUserChange = DateTime.UtcNow; _audio.ToggleMute(); RefreshVolume(); };
        OutputBtn.Click += (_, _) => ShowDevices(!_devicesOpen);
        BriSlider.ValueChanged += v => { _lastUserChange = DateTime.UtcNow; _bri.Set((int)Math.Round(v * 100)); BriText.Text = $"{(int)Math.Round(v * 100)}"; };

        WifiTile.Click += async (_, _) => { WifiTile.IsOn = !WifiTile.IsOn; await _radio.SetWifiAsync(WifiTile.IsOn); };
        BtTile.Click += async (_, _) => { BtTile.IsOn = !BtTile.IsOn; await _radio.SetBluetoothAsync(BtTile.IsOn); };
        AirTile.Click += async (_, _) => { AirTile.IsOn = !AirTile.IsOn; await _radio.SetAirplaneAsync(AirTile.IsOn); };
        FocusTile.Click += (_, _) => _focus.Toggle();
        MicTile.Click += (_, _) => { _audio.ToggleMic(); RefreshToggles(); };
        PowerTile.Click += (_, _) => { _power.CycleMode(); RefreshToggles(); };

        // right-click a tile → the matching Windows settings page
        WifiTile.MouseRightButtonUp += (_, e) => OpenUri("ms-settings:network-wifi", e);
        BtTile.MouseRightButtonUp += (_, e) => OpenUri("ms-settings:bluetooth", e);
        AirTile.MouseRightButtonUp += (_, e) => OpenUri("ms-settings:network-airplanemode", e);
        FocusTile.MouseRightButtonUp += (_, e) => OpenUri("ms-settings:quietmomentshome", e);
        MicTile.MouseRightButtonUp += (_, e) => OpenUri("ms-settings:privacy-microphone", e);
        PowerTile.MouseRightButtonUp += (_, e) => OpenUri("ms-settings:powersleep", e);
        BatteryPanel.MouseLeftButtonUp += (_, e) => OpenUri("ms-settings:batterysaver", e);
    }

    void OpenUri(string uri, MouseButtonEventArgs? e = null)
    {
        if (e != null) e.Handled = true;
        Launch(uri);
    }

    void Launch(string target, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = args ?? "" }); } catch { }
        Collapse();
    }

    void RefreshExpanded()
    {
        RefreshClockAndProgress();
        RefreshBattery();
        RefreshMedia();
        RefreshToggles();
        RefreshVolume();
        RefreshBrightness();
        RefreshPrivacyStatus();
        _ = _bri.RefreshAsync();
        _ = RefreshAirplaneAsync();
    }

    async Task RefreshAirplaneAsync()
    {
        await _radio.RefreshAirplaneAsync();
        RefreshToggles();
    }

    void RefreshClockAndProgress()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToShortTimeString();
        DateText.Text = now.ToString("ddd d MMM");
        // the progress bar lives on Home; don't redraw it while another page is showing
        if (_mode == Mode.Expanded && _page != 0) return;

        if (!_media.HasSession || _media.Duration <= TimeSpan.Zero)
        {
            ProgressRow.Visibility = Visibility.Collapsed;
            return;
        }
        ProgressRow.Visibility = Visibility.Visible;
        if (ProgressSlider.IsDragging) return;
        var pos = _media.Position;
        ProgressSlider.Value = pos.TotalSeconds / _media.Duration.TotalSeconds;
        ElapsedText.Text = Format(pos);
        RemainText.Text = "-" + Format(_media.Duration - pos);
    }

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void RefreshBattery()
    {
        BatteryText.Visibility = Battery.Visibility = _power.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        BatteryText.Text = $"{_power.Percent}%";
        Battery.Set(_power.Percent / 100.0, _power.PluggedIn, _power.Saver);
    }

    void RefreshMedia()
    {
        bool has = _media.HasSession;
        var brush = ArtBrush(_media.Art);
        ArtBox.Background = brush ?? (Brush)new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x21));
        ArtPlaceholder.Visibility = brush == null ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = has && _media.Title.Length > 0 ? _media.Title : "Nothing playing";
        ArtistText.Text = has ? _media.Artist : "Play something in Spotify, YouTube…";
        PlayIcon.Visibility = _media.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseIcon.Visibility = _media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        PrevBtn.IsEnabled = PlayBtn.IsEnabled = NextBtn.IsEnabled = has;
        RefreshClockAndProgress();
    }

    void RefreshToggles()
    {
        WifiTile.Visibility = _radio.HasWifi ? Visibility.Visible : Visibility.Collapsed;
        WifiTile.IsOn = _radio.WifiOn;
        WifiTile.Label = !_radio.WifiOn ? "Wi-Fi off" : _radio.NetworkName ?? "Wi-Fi";

        BtTile.Visibility = _radio.HasBluetooth ? Visibility.Visible : Visibility.Collapsed;
        BtTile.IsOn = _radio.BluetoothOn;
        BtTile.Label = !_radio.BluetoothOn ? "BT off" : _bt.ConnectedName ?? "Bluetooth";

        AirTile.IsOn = _radio.AirplaneOn;

        FocusTile.IsOn = _focus.IsOn;
        FocusTile.Label = _focus.IsOn ? "Focus on" : "Focus";

        MicTile.Visibility = _audio.HasMic ? Visibility.Visible : Visibility.Collapsed;
        MicTile.IsOn = _audio.MicMuted;
        MicTile.Glyph = _audio.MicMuted ? "" : "";
        MicTile.Label = _audio.MicMuted ? "Muted" : "Mic on";

        var mode = _power.Mode;
        PowerTile.IsOn = mode != PowerMode.Balanced;
        PowerTile.OnBrush = mode == PowerMode.Efficiency
            ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        PowerTile.Glyph = mode switch { PowerMode.Efficiency => "", PowerMode.Performance => "", _ => "" };
        PowerTile.Label = mode switch { PowerMode.Efficiency => "Efficiency", PowerMode.Performance => "Performance", _ => "Balanced" };
    }

    void RefreshVolume()
    {
        if (!VolSlider.IsDragging) VolSlider.Value = _audio.Muted ? 0 : _audio.Volume;
        VolGlyph.Text = VolumeGlyph();
        OutputGlyph.Text = _audio.OutputIsHeadphones ? "" : "";
        OutputBtn.ToolTip = _audio.OutputName;
    }

    void RefreshBrightness()
    {
        BriRow.Visibility = _bri.Supported ? Visibility.Visible : Visibility.Collapsed;
        if (!BriSlider.IsDragging) BriSlider.Value = _bri.Level / 100.0;
        BriText.Text = $"{_bri.Level}";
    }

    string VolumeGlyph()
    {
        float v = _audio.Muted ? 0 : _audio.Volume;
        return v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
    }

    void OnMediaChanged()
    {
        var brush = ArtBrush(_media.Art);
        CompactArt.Background = brush ?? (Brush)new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E));
        var accent = new SolidColorBrush(_media.Accent);
        accent.Freeze();
        foreach (var r in Eq.Children.OfType<Rectangle>()) r.Fill = accent;

        ApplyMediaColor();
        if (_mode == Mode.Expanded) RefreshMedia();
        RefreshBase();
        UpdateEqualizer();
        UpdateRim();
    }

    Color _glowFor = Colors.Transparent;

    /// <summary>The open notch picks up the playing song's colours: a soft glow from behind the cover and a tinted progress bar.</summary>
    void ApplyMediaColor()
    {
        bool tinted = _media.HasSession && _media.Art != null && _media.Accent != Colors.White;
        var c = _media.Accent;
        if (tinted && c != _glowFor)
        {
            var glow = new RadialGradientBrush
            {
                Center = new Point(0.14, 0.32), GradientOrigin = new Point(0.14, 0.32), RadiusX = 0.85, RadiusY = 0.95,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x48, c.R, c.G, c.B), 0),
                    new GradientStop(Color.FromArgb(0x14, c.R, c.G, c.B), 0.55),
                    new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
                },
            };
            glow.Freeze();
            MediaGlow.Background = glow;
            ProgressSlider.FillBrush = Frozen(c);
            _glowFor = c;
        }
        else if (!tinted)
        {
            ProgressSlider.FillBrush = Brushes.White;
            _glowFor = Colors.Transparent;
        }
        Fade(MediaGlow, tinted ? 1 : 0, 400);
    }

    ImageBrush? ArtBrush(ImageSource? img)
    {
        if (img == null) return null;
        if (ReferenceEquals(img, _brushFor)) return _artBrush;
        var b = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.HighQuality);
        b.Freeze();
        _brushFor = img;
        _artBrush = b;
        return b;
    }

    static ImageBrush? IconBrush(ImageSource? img)
    {
        if (img == null) return null;
        var b = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.HighQuality);
        b.Freeze();
        return b;
    }

    void UpdateEqualizer()
    {
        bool run = _mode == Mode.Media && _media.IsPlaying && _settings.AnimatedVisualizer && !Eco;
        if (run == _eqRunning) return;
        _eqRunning = run;
        double[] periods = { 0.42, 0.58, 0.36, 0.5 };
        for (int i = 0; i < _eqBars.Length; i++)
        {
            if (run)
            {
                var a = new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(periods[i % periods.Length]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 90),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                Timeline.SetDesiredFrameRate(a, 30); // smooth enough for bars, half the redraw cost
                _eqBars[i].BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }
            else
            {
                _eqBars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _eqBars[i].ScaleY = 0.35;
            }
        }
    }

    void ShowDevices(bool open)
    {
        if (open == _devicesOpen) return;
        _devicesOpen = open;
        if (open)
        {
            DevicesList.Children.Clear();
            foreach (var d in _audio.GetOutputs())
            {
                var row = new Grid { Height = 30 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Glyph(d.IsHeadphones ? "" : "", 14));
                var name = new TextBlock { Text = d.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                if (d.IsDefault)
                {
                    var check = Glyph("", 12, Blue);
                    Grid.SetColumn(check, 2);
                    row.Children.Add(check);
                }
                var btn = new Button { Style = (Style)FindResource("RowButton"), Content = row, Height = 32 };
                string id = d.Id;
                btn.Click += (_, _) => { _audio.SetDefaultOutput(id); ShowDevices(false); };
                DevicesList.Children.Add(btn);
            }
            DevicesPanel.Visibility = Visibility.Visible;
            Fade(DevicesPanel, 1, 160);
            Fade(MediaRow, 0, 120);
            Fade(TogglesRow, 0, 120);
            MediaRow.IsHitTestVisible = TogglesRow.IsHitTestVisible = false;
        }
        else
        {
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            a.Completed += (_, _) => { if (!_devicesOpen) DevicesPanel.Visibility = Visibility.Collapsed; };
            DevicesPanel.BeginAnimation(OpacityProperty, a);
            Fade(MediaRow, 1, 180);
            Fade(TogglesRow, 1, 180);
            MediaRow.IsHitTestVisible = TogglesRow.IsHitTestVisible = true;
        }
    }

    // shared little helpers ------------------------------------------------------------

    static readonly Brush Blue = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));
    static readonly Brush Orange = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    static readonly Brush Green = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    static readonly Brush Red = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    static readonly Brush Purple = Frozen(Color.FromRgb(0x5E, 0x5C, 0xE6));
    static readonly Brush DimBrush = Frozen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    TextBlock Glyph(string glyph, double size, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)FindResource("IconFont"),
        FontSize = size,
        Foreground = brush ?? Brushes.White,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
