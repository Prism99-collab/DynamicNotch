using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
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
        OutputTab.Click += (_, _) => { _devicesInput = false; RenderDevices(); };
        InputTab.Click += (_, _) => { _devicesInput = true; RenderDevices(); };
        _audio.DevicesChanged += () => { if (_devicesOpen) RenderDevices(); };
        BriSlider.ValueChanged += v => { _lastUserChange = DateTime.UtcNow; _bri.Set((int)Math.Round(v * 100)); BriText.Text = $"{(int)Math.Round(v * 100)}"; };
        AppVolSlider.ValueChanged += v => { _audio.SetAppVolume((float)v); AppVolGlyph.Text = GlyphForLevel(v); };
        ExtBriSlider.ValueChanged +=v => { _lastUserChange = DateTime.UtcNow; _extBri.Set((int)Math.Round(v * 100)); ExtBriText.Text = $"{(int)Math.Round(v * 100)}"; };
        _extBri.Changed += RefreshExtBrightness;

        WifiTile.Click += async (_, _) => { bool on = WifiTile.IsOn = !WifiTile.IsOn; await _radio.SetWifiAsync(on); await ConfirmToggleAsync(() => _radio.WifiOn, on); };
        BtTile.Click += async (_, _) => { bool on = BtTile.IsOn = !BtTile.IsOn; await _radio.SetBluetoothAsync(on); await ConfirmToggleAsync(() => _radio.BluetoothOn, on); };
        AirTile.Click += async (_, _) => { bool on = AirTile.IsOn = !AirTile.IsOn; await _radio.SetAirplaneAsync(on); await ConfirmToggleAsync(() => _radio.AirplaneOn, on); };
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

    /// <summary>Windows refused to switch a radio (hardware switch, driver, policy): flip the tile back with a small shiver.</summary>
    async Task ConfirmToggleAsync(Func<bool> isOn, bool wanted)
    {
        await Task.Delay(800);
        if (isOn() == wanted) return;
        RefreshToggles();
        Tremble(((SolidColorBrush)Red).Color);
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
        RefreshExtBrightness();
        RefreshPrivacyStatus();
        UpdateCallUi();
        _ = _bri.RefreshAsync();
        _ = _bt.RefreshBatteriesAsync(); // a controller's battery is fresh whenever you look
        _ = _extBri.RefreshAsync(MonitorInUse()); // picks up changes made with the monitor's own buttons
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
        FlipArt(ArtBox, _media.Art, () =>
        {
            ArtBox.Background = brush ?? (Brush)new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x21));
            ArtPlaceholder.Visibility = brush == null ? Visibility.Visible : Visibility.Collapsed;
        });
        TitleText.Text = has && _media.Title.Length > 0 ? _media.Title : "Nothing playing";
        ArtistText.Text = has ? _media.Artist : "Play something in Spotify, YouTube…";
        PlayIcon.Visibility = _media.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseIcon.Visibility = _media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        PrevBtn.IsEnabled = PlayBtn.IsEnabled = NextBtn.IsEnabled = has;
        RefreshAppVolume();
        RefreshClockAndProgress();
    }

    string? _appVolLogged;

    /// <summary>The small slider under the media buttons: volume of only the app that's playing.</summary>
    int _appVolVersion;

    async void RefreshAppVolume()
    {
        if (AppVolSlider.IsDragging) return;
        int version = ++_appVolVersion;
        // looked up off the UI thread; only the newest answer is applied
        float? v = _media.HasSession ? await _audio.FindAppVolumeAsync(_media.SourceApp) : null;
        if (version != _appVolVersion || AppVolSlider.IsDragging) return;
        if (_media.SourceApp != _appVolLogged)
        {
            _appVolLogged = _media.SourceApp;
            if (_appVolLogged != null) AppLog.Write($"media app '{_appVolLogged}': {(v == null ? "no sound session found" : "app volume available")}");
        }
        AppVolRow.Visibility = v != null ? Visibility.Visible : Visibility.Collapsed;
        if (v is not { } level) return;
        AppVolSlider.Value = level;
        AppVolGlyph.Text = GlyphForLevel(level);
    }

    static string GlyphForLevel(double v) => v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";

    void RefreshToggles()
    {
        WifiTile.Visibility = _radio.HasWifi ? Visibility.Visible : Visibility.Collapsed;
        WifiTile.IsOn = _radio.WifiOn;
        WifiTile.Label = !_radio.WifiOn ? "Wi-Fi off" : _radio.NetworkName ?? "Wi-Fi";

        BtTile.Visibility = _radio.HasBluetooth ? Visibility.Visible : Visibility.Collapsed;
        BtTile.IsOn = _radio.BluetoothOn;
        // the connected device, battery first so it survives the label's trimming: "50% · Xbox Wireless…"
        var bt = _radio.BluetoothOn ? _bt.Primary : null;
        BtTile.Label = !_radio.BluetoothOn ? "BT off" : bt == null ? "Bluetooth" : bt.Battery is { } b ? $"{b}% · {bt.Name}" : bt.Name;
        BtTile.ToolTip = bt == null ? "Bluetooth (right-click for settings)"
            : $"{bt.Name}{(bt.Battery is { } level ? $" · {level}% battery" : "")} (right-click for settings)";

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

    void RefreshExtBrightness()
    {
        bool ext = _extBri.Supported;
        ExtBriRow.Visibility = ext ? Visibility.Visible : Visibility.Collapsed;
        // three sliders only fit with slightly tighter rows; with two, keep the roomier layout
        HomeGap1.Height = new GridLength(ext ? 8 : 14);
        HomeGap2.Height = new GridLength(ext ? 6 : 10);
        VolRow.Height = BriRow.Height = ExtBriRow.Height = ext ? 24 : 30;
        BriRow.Margin = ExtBriRow.Margin = new Thickness(0, ext ? 1 : 2, 0, 0);
        // with two brightness sliders, tell them apart: laptop vs monitor
        BriGlyph.Text = ext ? "" : "";
        BriRow.ToolTip = ext ? "Laptop screen brightness" : null;
        if (!ext) return;
        if (!ExtBriSlider.IsDragging && !RecentlyTouched) ExtBriSlider.Value = _extBri.Level / 100.0;
        ExtBriText.Text = $"{_extBri.Level}";
    }

    string VolumeGlyph()
    {
        float v = _audio.Muted ? 0 : _audio.Volume;
        return v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
    }

    void OnMediaChanged()
    {
        var brush = ArtBrush(_media.Art);
        FlipArt(CompactArt, _media.Art, () => CompactArt.Background = brush ?? (Brush)new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)));
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

    // ───────────────────────────── visualizer: follows the real sound ─────────────────────────────

    readonly DispatcherTimer _meter = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    readonly double[] _eqLevel = { 0.35, 0.35, 0.35, 0.35 };
    double _beat;
    bool _meterWired, _eqFallback, _slimBeat;

    void UpdateEqualizer()
    {
        bool run = _mode == Mode.Media && _media.IsPlaying && _settings.AnimatedVisualizer && !Eco;
        if (run == _eqRunning) return;
        _eqRunning = run;
        if (!run)
        {
            _eqFallback = false;
            for (int i = 0; i < _eqBars.Length; i++)
            {
                _eqBars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _eqBars[i].ScaleY = _eqLevel[i] = 0.35;
            }
        }
        UpdateMeter();
    }

    /// <summary>The meter runs only while something on screen dances to it: the bars, or the slim glow over a window.</summary>
    void UpdateMeter()
    {
        if (!_meterWired) { _meterWired = true; _meter.Tick += (_, _) => OnMeter(); }
        if (_eqRunning || _slimBeat || _callBarsOn) { if (!_meter.IsEnabled) _meter.Start(); }
        else _meter.Stop();
    }

    void OnMeter()
    {
        bool callGlow = OnCall && CurrentActivity == LiveActivity.Call;
        if (_callBarsOn || (_slimBeat && callGlow)) OnCallMeter();
        if (callGlow && _slimBeat)
        {
            double v = 0.35 + 0.65 * _voice;
            if (Math.Abs(SlimGlow.Opacity - v) > 0.01) SlimGlow.Opacity = v;
        }
        if (callGlow && _mode == Mode.Activity) BeatAura(_voice * 1.2);
        if (!_eqRunning && !(_slimBeat && !callGlow)) return; // nothing else needs the speakers' level
        if (!_audio.TryGetPeaks(out float left, out float right))
        {
            // no meter on this device: fall back to the gentle looping bars
            if (_eqRunning && !_eqFallback) { _eqFallback = true; StartLoopingBars(); }
            _meter.Stop();
            return;
        }
        double t = Environment.TickCount64 / 1000.0;
        double loud = Math.Sqrt(Math.Max(left, right)); // perceived loudness, not raw amplitude

        if (_eqRunning && !_eqFallback)
        {
            for (int i = 0; i < _eqBars.Length; i++)
            {
                // outer bars follow each channel, inner ones the mix; a slow wobble keeps neighbours apart
                double src = i == 0 ? left : i == _eqBars.Length - 1 ? right : (left + right) / 2;
                double wobble = 0.82 + 0.18 * Math.Sin(t * (4.3 + i * 1.9) + i * 1.3);
                double target = 0.22 + 0.78 * Math.Clamp(Math.Sqrt(src) * 1.3 * wobble, 0, 1);
                // quick to jump on a beat, slower to fall: reads as music, not noise
                _eqLevel[i] += (target - _eqLevel[i]) * (target > _eqLevel[i] ? 0.7 : 0.2);
                if (Math.Abs(_eqBars[i].ScaleY - _eqLevel[i]) > 0.008) _eqBars[i].ScaleY = _eqLevel[i];
            }
        }
        if (_mode == Mode.Media) BeatAura(loud * 1.4);
        if (_slimBeat && !callGlow)
        {
            _beat += (Math.Clamp(loud * 1.35, 0, 1) - _beat) * 0.3;
            double o = 0.35 + 0.65 * _beat;
            if (Math.Abs(SlimGlow.Opacity - o) > 0.01) SlimGlow.Opacity = o;
        }
    }

    void StartLoopingBars()
    {
        double[] periods = { 0.42, 0.58, 0.36, 0.5 };
        for (int i = 0; i < _eqBars.Length; i++)
        {
            var a = new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(periods[i % periods.Length]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 90),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(a, 30);
            _eqBars[i].BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    void ShowDevices(bool open)
    {
        if (open == _devicesOpen) return;
        _devicesOpen = open;
        if (open)
        {
            _devicesInput = false; // always opens on speakers / headphones
            RenderDevices();
            _audio.RefreshDevices(); // just in case; the list on screen updates if anything changed
            DevicesPanel.Visibility = Visibility.Visible;
            Fade(DevicesPanel, 1, 160);
            Fade(MediaRow, 0, 120);
            Fade(CallRow, 0, 120);
            Fade(TogglesRow, 0, 120);
            MediaRow.IsHitTestVisible = CallRow.IsHitTestVisible = TogglesRow.IsHitTestVisible = false;
        }
        else
        {
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            a.Completed += (_, _) => { if (!_devicesOpen) DevicesPanel.Visibility = Visibility.Collapsed; };
            DevicesPanel.BeginAnimation(OpacityProperty, a);
            Fade(MediaRow, 1, 180);
            Fade(CallRow, 1, 180);
            Fade(TogglesRow, 1, 180);
            MediaRow.IsHitTestVisible = CallRow.IsHitTestVisible = TogglesRow.IsHitTestVisible = true;
        }
    }

    bool _devicesInput;

    /// <summary>Fills the device panel from the lists the audio service keeps ready (no waiting on Windows).</summary>
    void RenderDevices()
    {
        StyleTab(OutputTab, !_devicesInput);
        StyleTab(InputTab, _devicesInput);
        DevicesList.Children.Clear();
        var devices = _devicesInput ? _audio.Inputs : _audio.Outputs;
        if (devices.Count == 0)
        {
            DevicesList.Children.Add(new TextBlock
            {
                Text = _devicesInput ? "No microphone found" : "No sound output found",
                FontSize = 11.5, Foreground = DimBrush, Margin = new Thickness(10, 8, 10, 0),
            });
            return;
        }
        foreach (var d in devices)
        {
            var row = new Grid { Height = 30 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string glyph = _devicesInput ? (d.IsHeadphones ? "" : "") : d.IsHeadphones ? "" : "";
            row.Children.Add(Glyph(glyph, 14));
            var name = new TextBlock { Text = d.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            if (d.IsDefault)
            {
                var check = Glyph("", 12, Blue);
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
            }
            var btn = new Button { Style = (Style)FindResource("RowButton"), Content = row, Height = 32, ToolTip = d.Name };
            string id = d.Id;
            btn.Click += (_, _) => { _audio.SetDefaultDevice(id); ShowDevices(false); };
            DevicesList.Children.Add(btn);
        }
    }

    void StyleTab(Button tab, bool on)
    {
        tab.Background = on ? Brushes.White : (Brush)FindResource("Tile");
        tab.Foreground = on ? Brushes.Black : Brushes.White;
        tab.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
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
