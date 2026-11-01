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
        OutputTab.Click += (_, _) => { _devicesInput = false; _devicesMixer = false; StopMixer(); RenderDevices(); };
        InputTab.Click += (_, _) => { _devicesInput = true; _devicesMixer = false; StopMixer(); RenderDevices(); };
        _audio.DevicesChanged += () => { if (_devicesOpen) RenderDevices(); };
        BriSlider.ValueChanged += v => { _lastUserChange = DateTime.UtcNow; _bri.Set((int)Math.Round(v * 100)); BriText.Text = $"{(int)Math.Round(v * 100)}"; BrightnessGlyphs(); };
        AppVolSlider.ValueChanged += v => { _audio.SetAppVolume((float)v); AppVolGlyph.Text = GlyphForLevel(v); };
        ExtBriSlider.ValueChanged +=v => { _lastUserChange = DateTime.UtcNow; _extBri.Set((int)Math.Round(v * 100)); ExtBriText.Text = $"{(int)Math.Round(v * 100)}"; BrightnessGlyphs(); };
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
        CyberSkinSoon();
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
        CyberSkinSoon();
        var now = DateTime.Now;
        SetDigits(ClockText, now.ToShortTimeString()); // Cyberpunk: changed digits decode in
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
        SetDigits(BatteryText, $"{_power.Percent}%");
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
        BtTile.ToolTip = bt == null ? "Bluetooth · right-click for settings"
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
        ColorTiles();
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
        BrightnessGlyphs();
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
        PaintBars();
        // the cyber skin answers the music too: a new song or play / pause
        string key = $"{_media.Title}|{_media.IsPlaying}";
        if (key != _mediaGlitchKey) { _mediaGlitchKey = key; CyberPulse(); }

        ApplyMediaColor();
        if (_mode == Mode.Expanded) RefreshMedia();
        RefreshBase();
        UpdateEqualizer();
        UpdateRim();
    }

    /// <summary>The song changed colour (or went away): the open panel's tint follows.</summary>
    void ApplyMediaColor() => UpdatePanelTint();

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

    string _mediaGlitchKey = "";

    /// <summary>
    /// The bars in the cover's colours: from its main colour on the left to its second colour on the right, each
    /// bar deeper at its foot and catching light at its tip. A colourless cover gets white fading to silver.
    /// </summary>
    void PaintBars()
    {
        var bars = Eq.Children.OfType<Rectangle>().ToArray();
        Color a = _media.Accent, b = _media.Accent2;
        for (int i = 0; i < bars.Length; i++)
        {
            double t = bars.Length > 1 ? i / (double)(bars.Length - 1) : 0;
            var c = Mix(a, b, t);
            var g = new LinearGradientBrush { StartPoint = new Point(0.5, 1), EndPoint = new Point(0.5, 0) };
            g.GradientStops.Add(new GradientStop(Scale(c, 0.55), 0));
            g.GradientStops.Add(new GradientStop(c, 0.62));
            g.GradientStops.Add(new GradientStop(Mix(c, Colors.White, 0.35), 1));
            g.Freeze();
            bars[i].Fill = g;
        }
    }

    static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static Color Scale(Color c, double k) => Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));

    // the bars' physics: each listens to a different part of the sound, rises on it fast and falls with gravity
    double _lowEnv, _prevLoud;
    readonly double[] _eqFall = new double[4];

    readonly DispatcherTimer _meter = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
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
            // Windows gives one loudness per channel, not a spectrum: the bars are split by time instead.
            // the body of the sound (slow), the sound itself, its punches (what rises above the body), its sparkle
            // (how fast it changes). Each leans toward its side's channel.
            _lowEnv += (loud - _lowEnv) * 0.10;
            double punch = Math.Clamp((loud - _lowEnv) * 2.8, 0, 1);
            double sparkle = Math.Clamp(Math.Abs(loud - _prevLoud) * 4.5, 0, 1);
            _prevLoud = loud;
            double l = Math.Sqrt(left), r = Math.Sqrt(right), lr = Math.Max(0.001, Math.Max(l, r));
            double[] drive = { _lowEnv * 1.25, loud * 1.05, punch * 0.9 + loud * 0.35, sparkle * 0.8 + loud * 0.4 };
            for (int i = 0; i < _eqBars.Length && i < drive.Length; i++)
            {
                double side = i < _eqBars.Length / 2 ? l / lr : r / lr;
                double target = 0.18 + 0.82 * Math.Clamp(drive[i] * (0.85 + 0.15 * side), 0, 1);
                if (target >= _eqLevel[i])
                {
                    _eqLevel[i] += (target - _eqLevel[i]) * 0.68; // up on the beat
                    _eqFall[i] = 0;
                }
                else
                {
                    _eqFall[i] = Math.Min(_eqFall[i] + 0.009, 0.09); // then down, faster and faster, like a weight
                    _eqLevel[i] = Math.Max(target, _eqLevel[i] - _eqFall[i]);
                }
                if (Math.Abs(_eqBars[i].ScaleY - _eqLevel[i]) > 0.006) _eqBars[i].ScaleY = _eqLevel[i];
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
            _devicesMixer = false;
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
            StopMixer();
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
        if (_devicesMixer) { RenderMixer(); return; }
        StyleTab(OutputTab, !_devicesInput);
        StyleTab(InputTab, _devicesInput);
        StyleTab(MixerTab, false);
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
