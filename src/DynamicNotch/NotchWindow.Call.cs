using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Live call activity: while a calling app (Discord, Teams, Zoom, WhatsApp, a browser for Meet...) holds the
/// microphone, the notch becomes a call pill with the app's icon, a running timer and bars that move with
/// your voice. The control center shows a call card with a big mute button and "back to the call".
/// </summary>
public partial class NotchWindow
{
    sealed record CallState(string Key, string Name, ImageSource? Icon, string? Path, DateTime Started);

    // apps whose use of the microphone means "a call" (a browser: Meet, Teams web, Discord web...)
    static readonly string[] CallApps =
    {
        "discord", "teams", "zoom", "slack", "skype", "whatsapp", "telegram", "signal", "webex", "messenger", "viber", "element",
        "chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "opera.exe", "vivaldi.exe", "arc.exe",
    };

    CallState? _call;
    bool _callBarsOn;
    readonly DispatcherTimer _callTick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    readonly double[] _callLevel = new double[9];
    double _voice;

    bool OnCall => _call != null;

    void WireCall()
    {
        _privacy.Changed += OnCallCheck;
        _callWatch.Tick += (_, _) =>
        {
            _callWatch.Interval = TimeSpan.FromSeconds(Eco ? 5 : 2.5);
            OnCallCheck();
        };
        _callWatch.Start();
        _audio.MicChanged += () => { if (OnCall) { UpdateCallUi(); if (_mode == Mode.Activity) UpdateActivityContent(); UpdateRim(); UpdateSlimGlow(); } };
        _callTick.Tick += (_, _) =>
        {
            if (_mode == Mode.Activity && CurrentActivity == LiveActivity.Call) UpdateActivityContent();
            if (_mode == Mode.Expanded && _page == 0) UpdateCallUi();
        };
        CallMuteBtn.Click += (_, _) =>
        {
            _audio.ToggleMic();
            UpdateCallUi();
            // a squeeze on the button, and the notch's rim answers in the new state's colour
            var pop = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new Controls.SpringEase { Damping = 0.5 } };
            if (CallMuteBtn.RenderTransform is not ScaleTransform st) { CallMuteBtn.RenderTransformOrigin = new Point(0.5, 0.5); CallMuteBtn.RenderTransform = st = new ScaleTransform(); }
            st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            FlashRim(((SolidColorBrush)(_audio.MicMuted ? Red : Green)).Color);
        };
        CallBackBtn.Click += async (_, _) => { await BackToCallAsync(); Collapse(); };
        for (int i = 0; i < _callLevel.Length; i++)
        {
            _callLevel[i] = 0.2;
            CallVoice.Children.Add(new Rectangle
            {
                Width = 3, Height = 14, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0, 0, 3, 0), Fill = Green,
                VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 0.2),
            });
        }
        foreach (var r in CallBars.Children.OfType<Rectangle>())
        {
            r.RenderTransformOrigin = new Point(0.5, 0.5);
            r.RenderTransform = new ScaleTransform(1, 0.25);
        }
    }

    static bool IsCallApp(string key) => CallApps.Any(a => key.Contains(a, StringComparison.OrdinalIgnoreCase));

    int _callHits, _callMisses;
    bool _callChecking;
    readonly DispatcherTimer _callWatch = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.5) };

    /// <summary>
    /// Is a calling app recording from a microphone? Asked of Windows' audio engine (every active capture
    /// stream, so it works behind virtual mics too) when the privacy indicator changes and every couple of
    /// seconds. A call starts after two checks in a row (not a quick voice search) and ends after two misses.
    /// </summary>
    async void OnCallCheck()
    {
        if (_callChecking) return;
        _callChecking = true;
        try
        {
            var apps = await AudioService.ActiveCaptureAppsAsync();
            string? path = apps.FirstOrDefault(IsCallApp);
            if (path != null && string.Equals(_call?.Key, path, StringComparison.OrdinalIgnoreCase)) { _callMisses = 0; return; }
            if (path != null)
            {
                _callMisses = 0;
                if (++_callHits >= 2) { _callHits = 0; await StartCallAsync(path); }
            }
            else
            {
                _callHits = 0;
                if (_call != null && ++_callMisses >= 2) { _callMisses = 0; EndCall(); }
            }
        }
        finally { _callChecking = false; }
    }

    async Task StartCallAsync(string key)
    {
        bool packaged = !key.Contains('\\');
        // Store apps are known by their package ("MSTeams_8wekyb3d8bbwe"): find the running exe from its folder
        string[] parts = packaged ? key.Split('_', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        var (_, path) = await Task.Run(() => FindAppWindow(p => packaged
            ? parts.All(part => p.Contains(part, StringComparison.OrdinalIgnoreCase))
            : string.Equals(p, key, StringComparison.OrdinalIgnoreCase)));
        path ??= packaged ? null : key;
        var (name, icon) = await DescribeAppAsync(path);
        if (path == null) name = packaged ? parts.FirstOrDefault()?.Split('.').Last() ?? "Call" : System.IO.Path.GetFileNameWithoutExtension(key);

        bool switched = _call != null;
        _call = new CallState(key, name, icon, path, switched ? _call!.Started : DateTime.UtcNow);
        _callTick.Start();
        UpdateCallUi();
        RefreshBase();
        if (switched) return;
        AppLog.Write($"call started: {name}");
        ShowOsd("call", icon == null ? "" : null, null, "On a call", name, null, art: icon, glyphBrush: Green,
            seconds: 2.6, motion: Motion.Pulse, onClick: () => ExpandTo(0));
    }

    void EndCall()
    {
        var call = _call!;
        _call = null;
        _callTick.Stop();
        var length = DateTime.UtcNow - call.Started;
        AppLog.Write($"call ended: {call.Name} after {length:hh\\:mm\\:ss}");
        UpdateCallUi();
        RefreshBase();
        ShowOsd("call-end", call.Icon == null ? "" : null, null, $"Call ended · {FormatTimer(length)}", call.Name, null,
            art: call.Icon, seconds: 2.4, prio: Prio.Low);
    }

    async Task BackToCallAsync()
    {
        if (_call is not { } call) return;
        bool packaged = call.Path == null || !call.Key.Contains('\\');
        string[] parts = call.Key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var (hwnd, _) = await Task.Run(() => FindAppWindow(p => call.Path != null
            ? string.Equals(p, call.Path, StringComparison.OrdinalIgnoreCase)
            : parts.All(part => p.Contains(part, StringComparison.OrdinalIgnoreCase))));
        if (hwnd != IntPtr.Zero) FocusWindow(hwnd);
        else Tremble(((SolidColorBrush)Red).Color); // the call's window is gone
    }

    // ───────────────────────────── what's shown ─────────────────────────────

    Brush CallBrush => _audio.MicMuted ? Red : Green;

    /// <summary>The compact call pill: icon + live dot on the left, voice bars and the timer on the right.</summary>
    void FillCallPill()
    {
        if (_call is not { } call) return;
        var brush = CallBrush;
        ActGlyph.Visibility = call.Icon == null ? Visibility.Visible : Visibility.Collapsed;
        ActGlyph.Text = _audio.MicMuted ? "" : "";
        ActGlyph.Foreground = brush;
        CallArtHost.Visibility = call.Icon != null ? Visibility.Visible : Visibility.Collapsed;
        if (call.Icon != null) CallArt.Background = IconBrush(call.Icon);
        CallDot.Fill = brush;
        CallBars.Visibility = Visibility.Visible;
        foreach (var r in CallBars.Children.OfType<Rectangle>()) r.Fill = brush;
        ActText.Foreground = brush;
        ActText.Text = FormatTimer(DateTime.UtcNow - call.Started);
    }

    /// <summary>Back to the plain activity look (timer, download, focus).</summary>
    void ResetCallPill()
    {
        CallArtHost.Visibility = Visibility.Collapsed;
        CallBars.Visibility = Visibility.Collapsed;
        ActGlyph.Visibility = Visibility.Visible;
    }

    /// <summary>The call card on Home, and whether the voice meter needs to run.</summary>
    void UpdateCallUi()
    {
        bool on = OnCall;
        CallRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MediaRow.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on && _call is { } call)
        {
            var brush = CallBrush;
            CallCardArt.Background = call.Icon != null ? IconBrush(call.Icon) : new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x21));
            CallTitle.Text = call.Name;
            CallSub.Text = $"{(_audio.MicMuted ? "Muted" : "On a call")} · {FormatTimer(DateTime.UtcNow - call.Started)}";
            CallCardDot.Fill = brush;
            CallRing.BorderBrush = brush;
            foreach (var r in CallVoice.Children.OfType<Rectangle>()) r.Fill = brush;
            CallMuteBg.Fill = _audio.MicMuted ? Red : (Brush)FindResource("Tile");
            CallMuteGlyph.Text = _audio.MicMuted ? "" : "";
            CallMuteGlyph.Foreground = _audio.MicMuted ? Brushes.Black : Brushes.White;
            CallMuteBtn.ToolTip = _audio.MicMuted ? "Unmute your microphone" : "Mute your microphone";
        }
        bool bars = on && !Eco && ((_mode == Mode.Activity && CurrentActivity == LiveActivity.Call) || (_mode == Mode.Expanded && _page == 0));
        if (bars != _callBarsOn) { _callBarsOn = bars; UpdateMeter(); }
    }

    /// <summary>Called by the shared ~30 Hz meter: your voice moves the bars, the ring, and the slim glow.</summary>
    void OnCallMeter()
    {
        if (!_audio.TryGetMicPeak(out float peak)) return;
        _voice += (Math.Clamp(Math.Sqrt(peak) * 1.5, 0, 1) - _voice) * 0.35;
        double t = Environment.TickCount64 / 1000.0;
        if (_mode == Mode.Activity)
        {
            int i = 0;
            foreach (var r in CallBars.Children.OfType<Rectangle>())
            {
                double shape = 0.75 + 0.25 * Math.Sin(t * (6.1 + i * 2.3) + i * 1.7);
                double target = 0.25 + 0.75 * _voice * shape;
                var st = (ScaleTransform)r.RenderTransform;
                if (Math.Abs(st.ScaleY - target) > 0.01) st.ScaleY = st.ScaleY + (target - st.ScaleY) * 0.6;
                i++;
            }
        }
        if (_mode == Mode.Expanded)
        {
            // a little "spoken" waveform: centre bars tallest, each with its own wobble
            for (int i = 0; i < _callLevel.Length; i++)
            {
                double centre = 1 - Math.Abs(i - (_callLevel.Length - 1) / 2.0) / _callLevel.Length;
                double target = 0.18 + 0.82 * _voice * centre * (0.7 + 0.3 * Math.Sin(t * (5 + i * 1.3) + i));
                _callLevel[i] += (target - _callLevel[i]) * (target > _callLevel[i] ? 0.65 : 0.25);
                var st = (ScaleTransform)((Rectangle)CallVoice.Children[i]).RenderTransform;
                if (Math.Abs(st.ScaleY - _callLevel[i]) > 0.01) st.ScaleY = _callLevel[i];
            }
            double ring = 0.3 + 0.7 * _voice;
            if (Math.Abs(CallRing.Opacity - ring) > 0.02) CallRing.Opacity = ring;
        }
    }
}
