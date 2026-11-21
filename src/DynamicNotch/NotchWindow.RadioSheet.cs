using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Hold the Wi-Fi or Bluetooth tile on Home (like a phone): a compact sheet zooms out of the tile.
///  • Wi-Fi: the networks around (signal, lock, the one you're on), tap to join; a new secured network opens a
///    password line right under it (Enter joins, the eye shows it); a wrong password shakes it. The network you're
///    on: Disconnect / Forget. Windows only names the networks around when Location is on: then a line offers the
///    switch, and your saved networks are listed meanwhile (they still join).
///  • Bluetooth: your devices (connected first, battery), tap to connect or disconnect a headset or speaker; Nearby:
///    devices in pairing mode, tap to pair (the code to compare is shown when the device has one).
/// A little face in the header looks for networks, thinks while joining, is proud when it's done.
/// Scrolling glides; the edges fade. Back, a click outside, another page or closing the notch puts it away.
/// </summary>
public partial class NotchWindow
{
    enum SheetKind { Wifi, Bt }

    Grid? _radioSheet;
    Border? _sheetBack;
    ScrollViewer? _sheetScroll;
    StackPanel? _sheetList;
    TextBlock? _sheetTitle, _sheetSub, _sheetSpin;
    Border? _sheetSwitch, _sheetKnob;
    NotchFace? _sheetFace;
    SheetKind _sheetKind;
    bool _sheetOpen;
    CancellationTokenSource? _sheetCts;
    readonly DispatcherTimer _sheetTick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
    readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    QuickToggle? _holdTile;
    bool _holdFired;

    // Wi-Fi state
    List<WifiNet> _nets = new();
    string? _netOpen;          // the row showing its password line or its actions
    string? _netJoining;       // joining this one
    string? _netNote;          // a line under a row ("Wrong password", "Couldn't join")
    string? _netNoteFor;
    bool _netScanning, _netShowPassword;
    PasswordBox? _netPassword;
    TextBox? _netPasswordShown;
    DateTime _netScannedAt;

    // Bluetooth state
    List<BtNearby> _nearby = new();
    readonly HashSet<string> _btBusy = new();
    string? _btNote, _btNoteFor, _btForget;
    CancellationTokenSource? _btScan;

    bool _btWasOn = true;

    bool PasswordOpen => _sheetKind == SheetKind.Wifi && _netOpen != null && _netPassword != null;

    Color SheetAccent => _cyber ? CyCyan : _sheetKind == SheetKind.Wifi ? WifiBlue : Color.FromRgb(0x7D, 0x7B, 0xFF);

    void WireRadioSheet()
    {
        foreach (var (tile, kind) in new[] { (WifiTile, SheetKind.Wifi), (BtTile, SheetKind.Bt) })
        {
            Stylus.SetIsPressAndHoldEnabled(tile, false); // a finger held down is ours (not Windows' right-click)
            tile.PreviewMouseLeftButtonDown += (_, _) => { _holdFired = false; _holdTile = tile; _holdTimer.Stop(); _holdTimer.Tag = kind; _holdTimer.Start(); };
            tile.PreviewMouseLeftButtonUp += (_, e) =>
            {
                _holdTimer.Stop();
                if (!_holdFired) return;
                _holdFired = false;
                e.Handled = true; // the hold opened the sheet: no toggle
                if (tile.IsMouseCaptured) tile.ReleaseMouseCapture();
            };
            tile.MouseLeave += (_, _) => _holdTimer.Stop();
            tile.ToolTip = kind == SheetKind.Wifi ? "Wi-Fi · hold for networks" : "Bluetooth · hold for devices";
        }
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            if (_holdTile is not { IsMouseOver: true } || Mouse.LeftButton != MouseButtonState.Pressed || _swiping) return;
            _holdFired = true;
            OpenRadioSheet((SheetKind)_holdTimer.Tag!, _holdTile);
        };
        _sheetTick.Tick += (_, _) => SheetRefresh(scan: false);
        // the radios changed (a network came or went): the list follows, unless a password is being typed in it
        _radio.Changed += () => { if (_sheetOpen && !PasswordOpen) RenderSheet(); };
        _bt.Changed += () => { if (_sheetOpen && _sheetKind == SheetKind.Bt) RenderSheet(); };
    }

    // ───────────────────────────── the sheet ─────────────────────────────

    void BuildSheet()
    {
        if (_radioSheet != null || MediaRow.Parent is not Grid host) return;
        _radioSheet = new Grid { Visibility = Visibility.Collapsed, Opacity = 0, RenderTransform = new ScaleTransform(1, 1) };
        Grid.SetRowSpan(_radioSheet, Math.Max(1, host.RowDefinitions.Count));
        Panel.SetZIndex(_radioSheet, 20);
        _radioSheet.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        _radioSheet.RowDefinitions.Add(new RowDefinition());
        _sheetBack = new Border { Margin = new Thickness(-8, -4, -8, -2), IsHitTestVisible = true, Background = Brushes.Transparent };
        Grid.SetRowSpan(_sheetBack, 2);
        _radioSheet.Children.Add(_sheetBack);

        // header: back · face · title / what it's on · spinner · switch
        var head = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var w in new[] { 26.0, 22, 0, 22, 40 }) head.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        var back = SheetIcon("", 11, "Back");
        back.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseRadioSheet(); };
        head.Children.Add(back);
        _sheetFace = new NotchFace(11) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_sheetFace, 1);
        head.Children.Add(_sheetFace);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        _sheetTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        _sheetSub = new TextBlock { FontSize = 10, Foreground = DimBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, -1, 0, 0) };
        words.Children.Add(_sheetTitle);
        words.Children.Add(_sheetSub);
        Grid.SetColumn(words, 2);
        head.Children.Add(words);
        _sheetSpin = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = DimBrush, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(), Cursor = Cursors.Hand,
            Background = Brushes.Transparent, ToolTip = "Look again",
        };
        _sheetSpin.MouseLeftButtonUp += (_, e) => { e.Handled = true; SheetRefresh(scan: true); };
        Grid.SetColumn(_sheetSpin, 3);
        head.Children.Add(_sheetSpin);
        _sheetKnob = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(6.5), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0), RenderTransform = new TranslateTransform() };
        _sheetSwitch = new Border { Width = 32, Height = 17, CornerRadius = new CornerRadius(8.5), Child = _sheetKnob, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _sheetSwitch.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            bool on = !(_sheetKind == SheetKind.Wifi ? _radio.WifiOn : _radio.BluetoothOn);
            PaintSwitch(on, animate: true);
            FeelIt(on ? Feel.On : Feel.Off);
            if (_sheetKind == SheetKind.Wifi) await _radio.SetWifiAsync(on); else await _radio.SetBluetoothAsync(on);
            await Task.Delay(700);
            if (!_sheetOpen) return;
            RenderSheet();
            SheetRefresh(scan: true);
            if (_sheetKind == SheetKind.Bt) { if (on) StartBtScan(); else { _btScan?.Cancel(); _btScan = null; _nearby = new(); SpinSheet(false); } }
        };
        Grid.SetColumn(_sheetSwitch, 4);
        head.Children.Add(_sheetSwitch);
        _radioSheet.Children.Add(head);

        _sheetList = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };
        _sheetScroll = new ScrollViewer
        {
            Content = _sheetList, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly, Focusable = false, CanContentScroll = false,
            OpacityMask = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                GradientStops = { new GradientStop(Color.FromArgb(0x30, 0, 0, 0), 0), new GradientStop(Colors.Black, 0.07), new GradientStop(Colors.Black, 0.88), new GradientStop(Colors.Transparent, 1) },
            },
        };
        _sheetScroll.PreviewMouseWheel += (_, e) => { e.Handled = true; SheetGlide(-e.Delta / 120.0 * 46); };
        Grid.SetRow(_sheetScroll, 1);
        _radioSheet.Children.Add(_sheetScroll);
        host.Children.Add(_radioSheet);
    }

    TextBlock SheetIcon(string glyph, double size, string tip)
    {
        var t = new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = size, Width = 24, Height = 24, TextAlignment = TextAlignment.Center,
            Padding = new Thickness(0, (24 - size) / 2 - 1, 0, 0), Foreground = Frozen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), Background = Brushes.Transparent,
            Cursor = Cursors.Hand, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        };
        t.MouseEnter += (_, _) => t.Foreground = Brushes.White;
        t.MouseLeave += (_, _) => t.Foreground = Frozen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
        return t;
    }

    void OpenRadioSheet(SheetKind kind, FrameworkElement? from)
    {
        BuildSheet();
        if (_radioSheet == null) return;
        if (_devicesOpen) ShowDevices(false);
        _sheetKind = kind;
        _sheetOpen = true;
        _netOpen = _netJoining = _netNote = null;
        _btNote = _btForget = null;
        _sheetCts?.Cancel();
        _sheetCts = new CancellationTokenSource();
        StyleSheet();
        RenderSheet();
        if (_sheetScroll != null) { StopSheetGlide(); _sheetScroll.ScrollToVerticalOffset(0); }

        // it zooms out of the tile you held
        _radioSheet.Visibility = Visibility.Visible;
        var st = (ScaleTransform)_radioSheet.RenderTransform;
        if (from != null && _radioSheet.Parent is UIElement host && from.IsVisible)
        {
            var c = from.TranslatePoint(new Point(from.ActualWidth / 2, 21), host);
            double w = Math.Max(1, ((FrameworkElement)host).ActualWidth), h = Math.Max(1, ((FrameworkElement)host).ActualHeight);
            _radioSheet.RenderTransformOrigin = new Point(Math.Clamp(c.X / w, 0, 1), Math.Clamp(c.Y / h, 0, 1));
        }
        else _radioSheet.RenderTransformOrigin = new Point(0.5, 0.6);
        var t = TimeSpan.FromMilliseconds(Eco ? 1 : 420);
        var spring = new SpringEase { Damping = 0.78 };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.55, 1, t) { EasingFunction = spring });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, 1, t) { EasingFunction = spring });
        _radioSheet.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Eco ? 1 : 160)));
        // everything under it makes way (the sliders too: nothing shows through)
        if (_radioSheet.Parent is Panel home)
            foreach (var el in home.Children.OfType<FrameworkElement>())
            {
                if (el == _radioSheet || el == DevicesPanel || el.Visibility != Visibility.Visible || _sheetHid.Any(h => h.El == el)) continue;
                _sheetHid.Add((el, el.Opacity, el.IsHitTestVisible));
                el.IsHitTestVisible = false;
                Fade(el, 0, 120);
            }
        FeelIt(Feel.Pick);
        HideTip();

        SheetRefresh(scan: true);
        _sheetTick.Start();
        if (kind == SheetKind.Bt) StartBtScan();
    }

    void CloseRadioSheet(bool instant = false)
    {
        if (!_sheetOpen || _radioSheet == null) return;
        _sheetOpen = false;
        _sheetTick.Stop();
        _sheetCts?.Cancel();
        _btScan?.Cancel();
        _btScan = null;
        EndWorkTyping(giveBack: true);
        SpinSheet(false);
        StopSheetGlide();
        var sheet = _radioSheet;
        var st = (ScaleTransform)sheet.RenderTransform;
        var t = TimeSpan.FromMilliseconds(instant || Eco ? 1 : 160);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.8, t) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.7, t) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, t);
        fade.Completed += (_, _) => { if (!_sheetOpen) { sheet.Visibility = Visibility.Collapsed; _sheetList?.Children.Clear(); } };
        sheet.BeginAnimation(OpacityProperty, fade);
        RestoreHomeRows(instant);
    }

    /// <summary>The rows under a sheet or a list come back (the music row stays away while a song card is up).</summary>
    /// <summary>What made way for the sheet, with how it was (some rows are meant to be faint, or not clickable).</summary>
    readonly List<(FrameworkElement El, double Opacity, bool Hit)> _sheetHid = new();

    void RestoreHomeRows(bool instant = false)
    {
        int ms = instant ? 1 : 180;
        foreach (var (el, opacity, hit) in _sheetHid)
            if (el != MediaRow && el != CallRow && el != TogglesRow && el != _song) { Fade(el, opacity, ms); el.IsHitTestVisible = hit; }
        _sheetHid.Clear();
        bool song = _song is { Visibility: Visibility.Visible };
        Fade(MediaRow, song ? 0 : 1, ms);
        Fade(CallRow, 1, ms);
        Fade(TogglesRow, 1, ms);
        if (_song is { Visibility: Visibility.Visible }) Fade(_song, 1, ms);
        MediaRow.IsHitTestVisible = !song;
        CallRow.IsHitTestVisible = TogglesRow.IsHitTestVisible = true;
        if (_song != null) _song.IsHitTestVisible = true;
    }

    void StyleSheet()
    {
        if (_sheetBack == null || _sheetTitle == null || _sheetSub == null || _sheetFace == null) return;
        var acc = SheetAccent;
        _sheetBack.CornerRadius = new CornerRadius(_cyber ? 0 : 14);
        _sheetBack.Background = Frozen(_cyber ? Color.FromArgb(0xF6, 0x03, 0x08, 0x0C) : Color.FromArgb(0xF4, 0x12, 0x12, 0x15));
        _sheetBack.BorderBrush = _cyber ? Frozen(A(CyCyan, 0x70)) : Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        _sheetBack.BorderThickness = new Thickness(1);
        _sheetTitle.FontFamily = _sheetSub.FontFamily = _cyber ? CyFont : UiText;
        _sheetTitle.Text = _sheetKind == SheetKind.Wifi ? (_cyber ? "WI-FI" : "Wi-Fi") : (_cyber ? "BLUETOOTH" : "Bluetooth");
        _sheetFace.Neon = _cyber;
        _sheetFace.Accent = acc;
        _sheetFace.Eco = Eco;
        _sheetFace.Visibility = _settings.NotchFace ? Visibility.Visible : Visibility.Collapsed;
        _sheetFace.SetOutfit(FaceOutfitNow(), animate: false);
        PaintSwitch(_sheetKind == SheetKind.Wifi ? _radio.WifiOn : _radio.BluetoothOn, animate: false);
    }

    void PaintSwitch(bool on, bool animate)
    {
        if (_sheetSwitch == null || _sheetKnob == null) return;
        var acc = SheetAccent;
        _sheetSwitch.CornerRadius = new CornerRadius(_cyber ? 0 : 8.5);
        _sheetKnob.CornerRadius = new CornerRadius(_cyber ? 0 : 6.5);
        _sheetSwitch.Background = Frozen(on ? acc : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        _sheetKnob.Background = on && _cyber ? Frozen(CyNight) : Brushes.White;
        var tt = (TranslateTransform)_sheetKnob.RenderTransform;
        double x = on ? 15 : 0;
        if (animate && !Eco) tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(260)) { EasingFunction = new SpringEase { Damping = 0.7 } });
        else { tt.BeginAnimation(TranslateTransform.XProperty, null); tt.X = x; }
        _sheetSwitch.ToolTip = on ? "Turn off" : "Turn on";
    }

    void SpinSheet(bool on)
    {
        if (_sheetSpin == null) return;
        var rt = (RotateTransform)_sheetSpin.RenderTransform;
        if (on && !Eco)
        {
            if (rt.HasAnimatedProperties) return;
            rt.BeginAnimation(RotateTransform.AngleProperty, Capped(new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever }, 30));
            _sheetSpin.Foreground = Frozen(SheetAccent);
        }
        else
        {
            rt.BeginAnimation(RotateTransform.AngleProperty, null);
            rt.Angle = 0;
            _sheetSpin.Foreground = DimBrush;
        }
    }

    void SheetMood(NotchFace.Mood mood) { if (_sheetFace != null && _sheetFace.Current != mood) _sheetFace.SetMood(mood); }

    // ───────────────────────────── refreshing ─────────────────────────────

    /// <summary>Wi-Fi: reads the list again (and asks the adapter to look around now and then). Bluetooth: re-renders.</summary>
    async void SheetRefresh(bool scan)
    {
        if (!_sheetOpen) return;
        if (_sheetKind == SheetKind.Bt)
        {
            // what Windows says now (a device that went to sleep shows as such)
            if (_radio.BluetoothOn) await _bt.ResyncAsync(); else _bt.RadioOff();
            if (_sheetOpen && _btBusy.Count == 0) RenderSheet();
            return;
        }
        if (!_radio.WifiOn || _netScanning) { RenderSheet(); return; }
        // a password being typed or a join in progress: the list holds still
        if (_netOpen != null && _netPassword != null || _netJoining != null) return;
        bool look = scan || (DateTime.UtcNow - _netScannedAt).TotalSeconds > 12;
        _netScanning = true;
        SpinSheet(true);
        SheetMood(NotchFace.Mood.Searching);
        var ct = _sheetCts?.Token ?? default;
        try
        {
            var nets = await Task.Run(async () =>
            {
                if (look) { WifiNetworks.Scan(); _netScannedAt = DateTime.UtcNow; }
                var first = WifiNetworks.List();
                if (look && !WifiNetworks.NeedsLocation) { await Task.Delay(2600, ct); first = WifiNetworks.List(); }
                return WifiNetworks.NeedsLocation ? WifiNetworks.Saved() : first;
            }, ct);
            if (ct.IsCancellationRequested || !_sheetOpen) return;
            _nets = nets;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { AppLog.Write("wifi list: " + ex.Message); }
        finally
        {
            _netScanning = false;
            if (_sheetOpen) { SpinSheet(false); SheetMood(NotchFace.Mood.Idle); }
        }
        if (_sheetOpen && _sheetKind == SheetKind.Wifi && !(_netOpen != null && _netPassword != null) && _netJoining == null) RenderSheet();
    }

    void RenderSheet()
    {
        if (_sheetList == null || !_sheetOpen) return;
        PaintSwitch(_sheetKind == SheetKind.Wifi ? _radio.WifiOn : _radio.BluetoothOn, animate: false);
        if (_sheetKind == SheetKind.Wifi) RenderWifi(); else RenderBt();
    }

    // ───────────────────────────── Wi-Fi ─────────────────────────────

    string? WifiOnNow => WifiNetworks.CurrentName() ?? _radio.NetworkName;

    void RenderWifi()
    {
        if (_sheetList == null || _sheetSub == null) return;
        _sheetList.Children.Clear();
        _netPassword = null;
        _netPasswordShown = null;
        string? on = WifiOnNow;
        _sheetSub.Text = !_radio.WifiOn ? "Off" : on != null ? on : "Not connected";
        if (!_radio.WifiOn)
        {
            SheetEmpty("", "Wi-Fi is off", "Turn it on with the switch above");
            return;
        }
        bool located = !WifiNetworks.NeedsLocation;
        if (!located)
        {
            // Windows only names the networks around when Location is on: say so, once, kindly
            var line = SheetBanner("", "Turn on Location to see networks around you", "Turn on", () => OpenUri("ms-settings:privacy-location"));
            _sheetList.Children.Add(line);
        }
        var list = _nets.Select(n => n with { Connected = n.Connected || on != null && (n.Ssid == on || n.ProfileName == on) }).ToList();
        if (!located) list = list.OrderByDescending(n => n.Connected).ToList();
        if (list.Count == 0)
        {
            if (located) SheetEmpty("", _netScanning ? "Looking for networks…" : "No networks found", _netScanning ? "" : "Tap ↻ to look again");
            return;
        }
        if (!located) _sheetList.Children.Add(SheetSection("Saved networks"));
        bool sectioned = false;
        foreach (var n in list)
        {
            if (located && !n.Connected && !sectioned) { _sheetList.Children.Add(SheetSection(list.Any(x => x.Connected) ? "Other networks" : "Networks")); sectioned = true; }
            _sheetList.Children.Add(WifiRow(n));
            if (_netOpen == n.Ssid) _sheetList.Children.Add(n.Connected ? WifiActions(n) : WifiPasswordLine(n));
            if (_netNoteFor == n.Ssid && _netNote != null) _sheetList.Children.Add(SheetNote(_netNote));
        }
    }

    FrameworkElement WifiRow(WifiNet n)
    {
        var acc = SheetAccent;
        bool joining = _netJoining == n.Ssid;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // the bars: the full fan dim, the strength lit over it
        var bars = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        var icon = (FontFamily)FindResource("IconFont");
        bars.Children.Add(new TextBlock { Text = "", FontFamily = icon, FontSize = 13, Foreground = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) });
        if (n.Signal >= 0)
            bars.Children.Add(new TextBlock { Text = n.Bars switch { 4 => "", 3 => "", 2 => "", _ => "" }, FontFamily = icon, FontSize = 13, Foreground = n.Connected ? Frozen(acc) : Brushes.White });
        g.Children.Add(bars);
        var name = new TextBlock
        {
            Text = n.Ssid, FontSize = 12, FontFamily = _cyber ? CyFont : UiText, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = n.Connected ? Frozen(Lighten(acc, 0.25)) : Brushes.White, FontWeight = n.Connected ? FontWeights.SemiBold : FontWeights.Normal, ToolTip = n.Ssid,
        };
        Grid.SetColumn(name, 1);
        g.Children.Add(name);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (joining) right.Children.Add(SheetDots(acc));
        else if (n.Connected) right.Children.Add(new TextBlock { Text = "", FontFamily = icon, FontSize = 11, Foreground = Frozen(acc), VerticalAlignment = VerticalAlignment.Center });
        else if (n.Saved && n.Signal >= 0) right.Children.Add(new TextBlock { Text = "Saved", FontSize = 9.5, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        if (n.Secured && !n.Connected && !joining && n.Signal >= 0) right.Children.Add(new TextBlock { Text = "", FontFamily = icon, FontSize = 9.5, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        var row = SheetRow(g, n.Connected);
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; WifiTap(n); };
        return row;
    }

    void WifiTap(WifiNet n)
    {
        if (_netJoining != null) return;
        _netNote = null;
        if (n.Connected || _netOpen == n.Ssid) { _netOpen = _netOpen == n.Ssid ? null : n.Ssid; FeelSound(Sfx.Cue.Tap); RenderSheet(); FocusPassword(); return; }
        if (n.Enterprise) { _netNote = "A work or school network: Windows' Wi-Fi page joins it"; _netNoteFor = n.Ssid; RenderSheet(); OpenUri("ms-settings:network-wifi"); return; }
        if (n.Saved || !n.NeedsPassword) { JoinWifi(n, null); return; }
        _netOpen = n.Ssid; // a password, first
        _netShowPassword = false;
        FeelSound(Sfx.Cue.Tap);
        RenderSheet();
        FocusPassword();
    }

    void FocusPassword()
    {
        var box = (Control?)_netPasswordShown ?? _netPassword;
        if (box == null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_sheetOpen && box.IsVisible) { BeginWorkTyping(box); KeepRowInView(box); } });
    }

    FrameworkElement WifiPasswordLine(WifiNet n)
    {
        var acc = SheetAccent;
        var g = new Grid { Margin = new Thickness(24, 1, 2, 6), Height = 28 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var frame = new Border
        {
            CornerRadius = new CornerRadius(_cyber ? 0 : 8), BorderThickness = new Thickness(1), BorderBrush = Frozen(A(acc, 0x80)),
            Background = Frozen(_cyber ? Color.FromArgb(0x90, 0, 0, 0) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
        };
        Grid.SetColumnSpan(frame, 2);
        g.Children.Add(frame);
        var pw = new PasswordBox
        {
            FontSize = 12, Foreground = Brushes.White, CaretBrush = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 4, 4), VerticalContentAlignment = VerticalAlignment.Center, FocusVisualStyle = null, SelectionBrush = Frozen(acc), PasswordChar = '●',
        };
        var shown = new TextBox
        {
            FontSize = 12, Foreground = Brushes.White, CaretBrush = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 4, 4), VerticalContentAlignment = VerticalAlignment.Center, FocusVisualStyle = null, SelectionBrush = Frozen(acc),
            Visibility = Visibility.Collapsed, FontFamily = _cyber ? CyFont : UiText,
        };
        var ghost = new TextBlock { Text = "Password", FontSize = 11.5, Foreground = DimBrush, IsHitTestVisible = false, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(ghost);
        g.Children.Add(pw);
        g.Children.Add(shown);
        void Ghost() => ghost.Visibility = (_netShowPassword ? shown.Text : pw.Password).Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        pw.PasswordChanged += (_, _) => { if (!_netShowPassword) { shown.Text = pw.Password; } Ghost(); };
        shown.TextChanged += (_, _) => { if (_netShowPassword && pw.Password != shown.Text) pw.Password = shown.Text; Ghost(); };
        void Go()
        {
            string p = pw.Password;
            if (p.Length == 0) { Jiggle(frame); return; }
            if (n.Auth != 1 && n.Auth != 2 && p.Length < 8) { _netNote = "Wi-Fi passwords have at least 8 characters"; _netNoteFor = n.Ssid; Jiggle(frame); ShowNoteUnder(g); return; }
            JoinWifi(n, p);
        }
        foreach (var box in new Control[] { pw, shown })
        {
            box.PreviewMouseLeftButtonDown += (_, _) => { _maybeSwipe = false; BeginWorkTyping(box); };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Go(); }
                else if (e.Key == Key.Escape) { e.Handled = true; _netOpen = null; EndWorkTyping(giveBack: true); RenderSheet(); }
            };
        }
        var eye = SheetIcon("", 11, "Show the password");
        eye.HorizontalAlignment = HorizontalAlignment.Center;
        eye.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _netShowPassword = !_netShowPassword;
            if (_netShowPassword) shown.Text = pw.Password;
            shown.Visibility = _netShowPassword ? Visibility.Visible : Visibility.Collapsed;
            pw.Visibility = _netShowPassword ? Visibility.Collapsed : Visibility.Visible;
            eye.Text = _netShowPassword ? "" : "";
            var box = _netShowPassword ? (Control)shown : pw;
            BeginWorkTyping(box);
            if (box is TextBox tb) tb.CaretIndex = tb.Text.Length;
        };
        Grid.SetColumn(eye, 1);
        g.Children.Add(eye);
        var join = SheetChip("Join", strong: true, Go);
        join.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(join, 2);
        g.Children.Add(join);
        _netPassword = pw;
        _netPasswordShown = null;
        // it opens under its row
        if (!Eco)
        {
            g.Opacity = 0;
            g.RenderTransform = new TranslateTransform(0, -6);
            g.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            ((TranslateTransform)g.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new SpringEase { Damping = 0.75 } });
        }
        return g;
    }

    void ShowNoteUnder(FrameworkElement after)
    {
        if (_sheetList == null || _netNote == null) return;
        int i = _sheetList.Children.IndexOf(after);
        if (i < 0) return;
        if (i + 1 < _sheetList.Children.Count && _sheetList.Children[i + 1] is FrameworkElement { Tag: "note" }) _sheetList.Children.RemoveAt(i + 1);
        _sheetList.Children.Insert(i + 1, SheetNote(_netNote));
    }

    FrameworkElement WifiActions(WifiNet n)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 6) };
        row.Children.Add(SheetChip("Disconnect", strong: false, async () =>
        {
            _netOpen = null;
            FeelIt(Feel.Off);
            await Task.Run(WifiNetworks.Disconnect);
            await Task.Delay(600);
            SheetRefresh(scan: false);
        }));
        string? profile = n.ProfileName ?? (n.Saved ? n.Ssid : null);
        if (profile != null)
        {
            bool asking = _btForget == "wifi:" + n.Ssid;
            var forget = SheetChip(asking ? "Forget it?" : "Forget", strong: false, async () =>
            {
                if (_btForget != "wifi:" + n.Ssid) { _btForget = "wifi:" + n.Ssid; RenderSheet(); return; }
                _btForget = null;
                _netOpen = null;
                FeelIt(Feel.Eat);
                await Task.Run(() => WifiNetworks.Forget(profile));
                await Task.Delay(500);
                SheetRefresh(scan: false);
            }, danger: asking);
            forget.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(forget);
        }
        return row;
    }

    async void JoinWifi(WifiNet n, string? password)
    {
        if (_netJoining != null) return;
        _netJoining = n.Ssid;
        _netNote = null;
        _netOpen = null;
        EndWorkTyping(giveBack: false);
        FeelSound(Sfx.Cue.Tap);
        SheetMood(NotchFace.Mood.Thinking);
        RenderSheet();
        var ct = _sheetCts?.Token ?? default;
        WifiNetworks.JoinResult r;
        try { r = await Task.Run(() => WifiNetworks.Join(n, password, ct)); }
        catch (Exception ex) { AppLog.Write("wifi join: " + ex.Message); r = WifiNetworks.JoinResult.Failed; }
        password = null;
        _netJoining = null;
        if (!_sheetOpen) return;
        SheetMood(NotchFace.Mood.Idle);
        switch (r)
        {
            case WifiNetworks.JoinResult.Joined:
                FeelIt(Feel.Done);
                _sheetFace?.Play(NotchFace.Emote.Proud, 1.2);
                break;
            case WifiNetworks.JoinResult.WrongPassword:
                FeelIt(Feel.Oops);
                _sheetFace?.Play(NotchFace.Emote.Confused, 1.2);
                _netOpen = n.NeedsPassword && !(n.Saved && password == null && n.Signal < 0) ? n.Ssid : null;
                _netNote = n.Saved ? "Couldn't join: its password may have changed" : "Wrong password";
                _netNoteFor = n.Ssid;
                if (n.Saved) _netOpen = n.Ssid; // a saved one whose password changed: type the new one
                break;
            case WifiNetworks.JoinResult.NeedsSettings:
                OpenUri("ms-settings:network-wifi");
                break;
            default:
                FeelIt(Feel.Oops);
                _netNote = n.Signal < 0 ? "Couldn't join: is it in range?" : "Couldn't join it";
                _netNoteFor = n.Ssid;
                break;
        }
        // the list as it is now (the network you're on moves to the top)
        try { _nets = await Task.Run(() => WifiNetworks.NeedsLocation ? WifiNetworks.Saved() : WifiNetworks.List()); } catch { }
        if (!_sheetOpen) return;
        RenderSheet();
        if (_netOpen != null)
        {
            FocusPassword();
            if (_netPassword?.Parent is Grid line && line.Children.OfType<Border>().FirstOrDefault() is { } frame) Jiggle(frame);
        }
    }

    // ───────────────────────────── Bluetooth ─────────────────────────────

    void RenderBt()
    {
        if (_sheetList == null || _sheetSub == null) return;
        _sheetList.Children.Clear();
        var paired = _bt.Paired;
        int connected = paired.Count(d => d.Connected);
        _sheetSub.Text = !_radio.BluetoothOn ? "Off" : connected == 0 ? "Nothing connected" : connected == 1 ? paired.First(d => d.Connected).Name : $"{connected} connected";
        if (!_radio.BluetoothOn)
        {
            SheetEmpty("", "Bluetooth is off", "Turn it on with the switch above");
            return;
        }
        if (paired.Count > 0)
        {
            _sheetList.Children.Add(SheetSection("My devices"));
            foreach (var d in paired)
            {
                _sheetList.Children.Add(BtRow(d));
                if (_btNoteFor == d.Id && _btNote != null) _sheetList.Children.Add(SheetNote(_btNote));
            }
        }
        _sheetList.Children.Add(SheetSection(_nearby.Count > 0 ? "Nearby" : "Nearby · put a device in pairing mode", spinning: _btScan != null));
        var known = new HashSet<string>(paired.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var n in _nearby.Where(n => !known.Contains(n.Name)))
        {
            _sheetList.Children.Add(NearbyRow(n));
            if (_btNoteFor == n.Id && _btNote != null) _sheetList.Children.Add(SheetNote(_btNote));
        }
    }

    FrameworkElement BtRow(BtDevice d)
    {
        var acc = SheetAccent;
        bool busy = _btBusy.Contains(d.Id);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = (FontFamily)FindResource("IconFont");
        g.Children.Add(new TextBlock { Text = BluetoothService.GlyphFor(d), FontFamily = icon, FontSize = 13, Foreground = d.Connected ? Frozen(acc) : Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock
        {
            Text = d.Name, FontSize = 12, FontFamily = _cyber ? CyFont : UiText, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = d.Name,
            Foreground = d.Connected ? Frozen(Lighten(acc, 0.3)) : Brushes.White, FontWeight = d.Connected ? FontWeights.SemiBold : FontWeights.Normal,
        });
        Grid.SetColumn(words, 1);
        g.Children.Add(words);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (busy) right.Children.Add(SheetDots(acc));
        else if (d.Connected)
        {
            if (d.Battery is { } b) right.Children.Add(new TextBlock { Text = $"{b}%", FontSize = 10, Foreground = b <= 15 ? Red : DimBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            right.Children.Add(new TextBlock { Text = "Connected", FontSize = 9.5, Foreground = Frozen(A(acc, 0xE0)), VerticalAlignment = VerticalAlignment.Center });
        }
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        // forget: a small × on hover, asked twice
        bool asking = _btForget == d.Id;
        var x = new TextBlock
        {
            Text = asking ? "Forget?" : "", FontFamily = asking ? (_cyber ? CyFont : UiText) : icon, FontSize = asking ? 9.5 : 8.5, Foreground = asking ? Red : DimBrush,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(3), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Opacity = asking ? 1 : 0, ToolTip = asking ? "Click again to remove the pairing" : "Forget this device",
        };
        x.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            if (_btForget != d.Id) { _btForget = d.Id; RenderSheet(); return; }
            _btForget = null;
            _btBusy.Add(d.Id);
            RenderSheet();
            bool ok = await BtLink.UnpairAsync(d.Id);
            _btBusy.Remove(d.Id);
            if (ok) FeelIt(Feel.Eat); else { _btNote = "Couldn't forget it"; _btNoteFor = d.Id; FeelIt(Feel.Oops); }
            if (_sheetOpen) RenderSheet();
        };
        Grid.SetColumn(x, 3);
        g.Children.Add(x);
        var row = SheetRow(g, d.Connected);
        row.MouseEnter += (_, _) => x.Opacity = 1;
        row.MouseLeave += (_, _) => { if (_btForget != d.Id) x.Opacity = 0; };
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!busy) BtToggle(d); };
        return row;
    }

    async void BtToggle(BtDevice d)
    {
        if (_btBusy.Contains(d.Id)) return;
        _btNote = null;
        _btForget = null;
        _btBusy.Add(d.Id);
        SheetMood(NotchFace.Mood.Working);
        FeelSound(Sfx.Cue.Tap);
        RenderSheet();
        bool want = !d.Connected;
        string? problem = null;
        try
        {
            Guid? container = d.Container;
            bool audio = container != null && await Task.Run(() => BtLink.IsAudio(container.Value));
            if (!want)
            {
                if (audio && await Task.Run(() => BtLink.SetConnected(container!.Value, false)) == BtLink.Result.Done) await WaitBt(d, false, 6);
                if (IsBtConnected(d)) problem = audio ? "It didn't let go" : "Turn it off to disconnect it (Windows keeps it while it's on)";
            }
            else
            {
                // a headset Windows knows the sound of: its driver connects it; else the link is woken and it follows
                if (audio && await Task.Run(() => BtLink.SetConnected(container!.Value, true)) == BtLink.Result.Done) await WaitBt(d, true, 8);
                if (!IsBtConnected(d))
                {
                    bool reached = await BtLink.WakeAsync(d.Id, d.LowEnergy, _sheetCts?.Token ?? default);
                    if (reached) await WaitBt(d, true, 6);
                    if (!IsBtConnected(d) && container != null && await Task.Run(() => BtLink.IsAudio(container.Value)))
                    {
                        if (await Task.Run(() => BtLink.SetConnected(container.Value, true)) == BtLink.Result.Done) await WaitBt(d, true, 6);
                    }
                    if (!IsBtConnected(d)) problem = reached ? "It answered but didn't connect: try it from the device" : "Couldn't reach it: is it on and nearby?";
                }
            }
        }
        catch (Exception ex) { AppLog.Write("bluetooth: " + ex.Message); problem = "Something went wrong"; }
        _btBusy.Remove(d.Id);
        if (!_sheetOpen) return;
        SheetMood(NotchFace.Mood.Idle);
        if (problem == null) { FeelIt(want ? Feel.Done : Feel.Off); if (want) _sheetFace?.Play(NotchFace.Emote.Happy, 1.0); }
        else { _btNote = problem; _btNoteFor = d.Id; FeelIt(Feel.Oops); }
        RenderSheet();
    }

    bool IsBtConnected(BtDevice d) => _bt.Paired.FirstOrDefault(p => p.Id == d.Id || p.Container != null && p.Container == d.Container)?.Connected == true;

    async Task WaitBt(BtDevice d, bool connected, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && _sheetOpen && IsBtConnected(d) != connected) await Task.Delay(250);
    }

    FrameworkElement NearbyRow(BtNearby n)
    {
        var acc = SheetAccent;
        bool busy = _btBusy.Contains(n.Id);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = (FontFamily)FindResource("IconFont");
        g.Children.Add(new TextBlock { Text = BluetoothService.GlyphFor(new BtDevice(n.Id, n.Name, false, n.Category, null, n.LowEnergy, null)), FontFamily = icon, FontSize = 13, Foreground = Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { Text = n.Name, FontSize = 12, FontFamily = _cyber ? CyFont : UiText, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = Frozen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), ToolTip = n.Name };
        Grid.SetColumn(name, 1);
        g.Children.Add(name);
        FrameworkElement right = busy ? SheetDots(acc) : new TextBlock { Text = "Pair", FontSize = 10, Foreground = Frozen(A(acc, 0xE0)), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        var row = SheetRow(g, false);
        row.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            if (_btBusy.Contains(n.Id)) return;
            _btBusy.Add(n.Id);
            _btNote = null;
            SheetMood(NotchFace.Mood.Working);
            FeelSound(Sfx.Cue.Tap);
            RenderSheet();
            var (ok, why) = await BtLink.PairAsync(n.Id, code => Dispatcher.InvokeAsync(() =>
            {
                _btNote = $"Code {code}: check it matches on the device";
                _btNoteFor = n.Id;
                if (_sheetOpen) RenderSheet();
            }));
            _btBusy.Remove(n.Id);
            if (!_sheetOpen) return;
            SheetMood(NotchFace.Mood.Idle);
            if (ok)
            {
                _nearby.RemoveAll(x => x.Id == n.Id);
                _btNote = null;
                FeelIt(Feel.Love, acc);
            }
            else { _btNote = why; _btNoteFor = n.Id; FeelIt(Feel.Oops); }
            RenderSheet();
        };
        return row;
    }

    void StartBtScan()
    {
        _btScan?.Cancel();
        _nearby = new();
        if (!_radio.BluetoothOn) return;
        var cts = new CancellationTokenSource();
        _btScan = cts;
        SpinSheet(true);
        SheetMood(NotchFace.Mood.Searching);
        BtLink.Discover(list => Dispatcher.InvokeAsync(() =>
        {
            if (cts.IsCancellationRequested || !_sheetOpen) return;
            _nearby = list;
            if (!_btBusy.Any()) RenderSheet();
        }), cts.Token);
        // a minute of looking is plenty (and it's light on the radio)
        After(60000, () => { if (_btScan == cts) { cts.Cancel(); _btScan = null; if (_sheetOpen) { SpinSheet(false); SheetMood(NotchFace.Mood.Idle); RenderSheet(); } } });
        After(4000, () => { if (_btScan == cts && _sheetOpen) SheetMood(NotchFace.Mood.Idle); });
    }

    // ───────────────────────────── pieces ─────────────────────────────

    Border SheetRow(FrameworkElement content, bool lit)
    {
        var acc = SheetAccent;
        var idle = lit ? Frozen(A(acc, 0x1A)) : Brushes.Transparent;
        var hot = Frozen(lit ? A(acc, 0x2A) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        var row = new Border { Child = content, Height = 32, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(_cyber ? 0 : 9), Background = idle, Cursor = Cursors.Hand };
        if (_cyber && lit) { row.BorderBrush = Frozen(A(acc, 0x90)); row.BorderThickness = new Thickness(2, 0, 0, 0); }
        row.MouseEnter += (_, _) => row.Background = hot;
        row.MouseLeave += (_, _) => row.Background = idle;
        return row;
    }

    FrameworkElement SheetSection(string text, bool spinning = false)
    {
        var t = new TextBlock
        {
            Text = _cyber ? text.ToUpperInvariant() : text, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(8, 6, 0, 3), FontFamily = _cyber ? CyFont : UiText,
        };
        if (!spinning) return t;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(t);
        var dots = SheetDots(SheetAccent);
        dots.Margin = new Thickness(6, 4, 0, 0);
        row.Children.Add(dots);
        return row;
    }

    FrameworkElement SheetNote(string text)
    {
        bool bad = !text.StartsWith("Code");
        return new TextBlock
        {
            Text = text, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(32, -1, 6, 5), Tag = "note", FontFamily = _cyber ? CyFont : UiText,
            Foreground = bad ? Frozen(Color.FromRgb(0xFF, 0x8A, 0x80)) : Frozen(Lighten(SheetAccent, 0.3)),
        };
    }

    void SheetEmpty(string glyph, string title, string line)
    {
        if (_sheetList == null) return;
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        s.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 20, Foreground = Frozen(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center });
        s.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), FontFamily = _cyber ? CyFont : UiText });
        if (line.Length > 0) s.Children.Add(new TextBlock { Text = line, FontSize = 10.5, Foreground = DimBrush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        _sheetList.Children.Add(s);
    }

    FrameworkElement SheetBanner(string glyph, string text, string action, Action act)
    {
        var acc = SheetAccent;
        var g = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = Frozen(acc), VerticalAlignment = VerticalAlignment.Center });
        var t = new TextBlock { Text = text, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Foreground = Frozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, FontFamily = _cyber ? CyFont : UiText };
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        var chip = SheetChip(action, strong: true, act);
        chip.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(chip, 2);
        g.Children.Add(chip);
        return new Border { Child = g, Padding = new Thickness(8, 5, 6, 5), CornerRadius = new CornerRadius(_cyber ? 0 : 10), Background = Frozen(A(acc, 0x16)), Margin = new Thickness(0, 0, 0, 4) };
    }

    Border SheetChip(string text, bool strong, Action act, bool danger = false)
    {
        var acc = danger ? Color.FromRgb(0xFF, 0x45, 0x3A) : SheetAccent;
        var idle = Frozen(strong || danger ? A(acc, 0x40) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        var hot = Frozen(strong || danger ? A(acc, 0x68) : Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF));
        var chip = new Border
        {
            Child = new TextBlock { Text = text, FontSize = 10.5, FontFamily = _cyber ? CyFont : UiText, Foreground = strong || danger ? Frozen(Lighten(acc, 0.4)) : Brushes.White, VerticalAlignment = VerticalAlignment.Center },
            Height = 24, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(_cyber ? 0 : 12), Background = idle, Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1), BorderBrush = Frozen(strong || danger ? A(acc, 0x80) : Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center,
        };
        chip.MouseEnter += (_, _) => chip.Background = hot;
        chip.MouseLeave += (_, _) => chip.Background = idle;
        chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
        return chip;
    }

    /// <summary>Three dots taking turns: working on it.</summary>
    FrameworkElement SheetDots(Color acc)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 3; i++)
        {
            var dot = new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(_cyber ? 0 : 2), Background = Frozen(acc), Margin = new Thickness(1.5, 0, 1.5, 0), Opacity = 0.3 };
            if (!Eco)
                dot.BeginAnimation(OpacityProperty, Capped(new DoubleAnimationUsingKeyFrames
                {
                    RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(900), BeginTime = TimeSpan.FromMilliseconds(i * 150),
                    KeyFrames = { new LinearDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.Zero)), new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))), new LinearDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))) },
                }, 20));
            row.Children.Add(dot);
        }
        return row;
    }

    static void Jiggle(FrameworkElement el)
    {
        var tt = el.RenderTransform as TranslateTransform;
        if (tt == null || tt.IsFrozen) el.RenderTransform = tt = new TranslateTransform();
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(380) };
        double[] xs = { -6, 5, -4, 3, -1.5, 0 };
        for (int i = 0; i < xs.Length; i++) a.KeyFrames.Add(new EasingDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60 * (i + 1))), new SineEase()));
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }

    // ───────────────────────────── gliding scroll ─────────────────────────────

    double _sheetScrollTo;
    bool _sheetGliding;
    TimeSpan _sheetGlideAt;

    void SheetGlide(double by)
    {
        if (_sheetScroll == null) return;
        double max = _sheetScroll.ScrollableHeight;
        if (max <= 0) return;
        _sheetScrollTo = Math.Clamp((_sheetGliding ? _sheetScrollTo : _sheetScroll.VerticalOffset) + by, 0, max);
        if (Eco) { _sheetScroll.ScrollToVerticalOffset(_sheetScrollTo); return; }
        if (_sheetGliding) return;
        _sheetGliding = true;
        _sheetGlideAt = TimeSpan.Zero;
        CompositionTarget.Rendering += OnSheetGlide;
    }

    void OnSheetGlide(object? sender, EventArgs e)
    {
        var sv = _sheetScroll;
        if (sv == null || !_sheetOpen) { StopSheetGlide(); return; }
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _sheetGlideAt) return;
        double dt = _sheetGlideAt == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - _sheetGlideAt).TotalSeconds, 0.05);
        _sheetGlideAt = now;
        double cur = sv.VerticalOffset, k = 1 - Math.Exp(-dt * 16);
        double next = cur + (_sheetScrollTo - cur) * k;
        if (Math.Abs(_sheetScrollTo - next) < 0.4) { sv.ScrollToVerticalOffset(_sheetScrollTo); StopSheetGlide(); return; }
        sv.ScrollToVerticalOffset(next);
    }

    void StopSheetGlide()
    {
        if (!_sheetGliding) return;
        _sheetGliding = false;
        CompositionTarget.Rendering -= OnSheetGlide;
    }

    /// <summary>The row being typed in stays in view (the list glides to it).</summary>
    void KeepRowInView(FrameworkElement el)
    {
        if (_sheetScroll == null || _sheetList == null) return;
        try
        {
            var p = el.TranslatePoint(new Point(0, 0), _sheetList);
            double top = p.Y - 36, bottom = p.Y + el.ActualHeight + 10, view = _sheetScroll.ViewportHeight, cur = _sheetScroll.VerticalOffset;
            if (bottom > cur + view) SheetGlide(bottom - view - cur);
            else if (top < cur) SheetGlide(top - cur);
        }
        catch { }
    }
}
