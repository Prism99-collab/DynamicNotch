using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The Calls page between calls: ready for the next one.
///  • a header that says whether you're ready (your mic on or muted, one click to switch) and what you'll hear with;
///  • the mic studio: a live waveform of your voice and your mic's level, and a test two ways: Live (hear yourself in
///    your headset as you speak) or Replay (speak, pause, hear how the others will hear you); a verdict at the end
///    (sounds good, a bit quiet, too loud, nothing heard) with the fix one click away;
///  • your call apps, one click to open, and a new Meet;
///  • your last calls (a click opens that app again).
/// </summary>
public partial class NotchWindow
{
    TextBlock? _iTitle, _iSub, _iTestText, _iAudioName, _iMicState, _iMicPct, _iOrbGlyph;
    Border? _iMicPill;
    Ellipse? _iMicDot;
    VoiceAura? _iAura;
    VoiceWave? _iWave;
    PillSlider? _iMic;
    Grid? _iOrb;
    Shape? _iOrbDisc;
    Button? _iModeLive, _iModeReplay;
    WrapPanel? _iStart;
    StackPanel? _iRecent;
    CancellationTokenSource? _micCheckStop;
    bool _micChecking;
    bool? _micTestLive;            // your pick (null: Live with a headset, Replay on speakers)
    float _iLevelTarget;
    readonly VoiceGate _iGate = new();
    double _iLevelNow;
    string _iPhase = "";
    bool _iFrameOn;
    TimeSpan _iFrameLast;

    bool HeadsetOut => _audio.Outputs.FirstOrDefault(d => d.IsDefault)?.IsHeadphones == true;
    bool MicTestLive => _micTestLive ?? HeadsetOut;

    Grid BuildIdleCalls()
    {
        var look = CallsLook();
        var g = new Grid { Margin = new Thickness(20, 0, 20, 0) };
        foreach (var h in new[] { 34.0, 4, 92, 6, 46, double.NaN })
            g.RowDefinitions.Add(new RowDefinition { Height = double.IsNaN(h) ? new GridLength(1, GridUnitType.Star) : new GridLength(h) });

        // ── header: ready? (the mic), and what you'll hear with
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _iTitle = Own(new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontFamily = look.Font });
        _iSub = Own(new TextBlock { FontSize = 10.5, Foreground = Frozen(look.Label), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), FontFamily = look.Font });
        titles.Children.Add(_iTitle);
        titles.Children.Add(_iSub);
        head.Children.Add(titles);

        var micRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(9, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        _iMicDot = Own(new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        _iMicState = Own(new TextBlock { FontSize = 10.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontFamily = look.Font });
        micRow.Children.Add(_iMicDot);
        micRow.Children.Add(_iMicState);
        var micPill = CallPlate(micRow, look, null, out _iMicPill, 26);
        _iMicPill.CornerRadius = new CornerRadius(look.Neon ? 0 : 13);
        micPill.ToolTip = "Mute or unmute your microphone";
        micPill.VerticalAlignment = VerticalAlignment.Center;
        micPill.Click += (_, _) => { _audio.ToggleMic(); UpdateIdleCalls(); FlashRim(_audio.MicMuted ? MutedColor : LiveColor); };
        Grid.SetColumn(micPill, 1);
        head.Children.Add(micPill);

        var audioRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(9, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        audioRow.Children.Add(Own(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Frozen(look.Accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }));
        _iAudioName = Own(new TextBlock { FontSize = 10.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 92, FontFamily = look.Font });
        audioRow.Children.Add(_iAudioName);
        audioRow.Children.Add(Own(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 8, Foreground = Frozen(look.Label), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0) }));
        var audio = CallPlate(audioRow, look, null, out var audioPlate, 26);
        audioPlate.CornerRadius = new CornerRadius(look.Neon ? 0 : 13);
        audio.Margin = new Thickness(6, 0, 0, 0);
        audio.VerticalAlignment = VerticalAlignment.Center;
        audio.ToolTip = "Your speakers and microphones";
        audio.Click += (_, _) => ToggleCallSheet(Sheet.Audio);
        Grid.SetColumn(audio, 2);
        head.Children.Add(audio);
        g.Children.Add(head);

        // ── the mic studio
        var card = Own(new Border
        {
            CornerRadius = new CornerRadius(look.Neon ? 0 : look.Radius + 2), Background = Frozen(look.Plate), BorderBrush = Frozen(look.Edge), BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 7, 12, 7),
        });
        var studio = new Grid();
        studio.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        studio.ColumnDefinitions.Add(new ColumnDefinition());
        studio.Children.Add(BuildTestOrb(look));

        var right = new Grid();
        foreach (var h in new[] { 18.0, 3, 20, 3, 18, double.NaN })
            right.RowDefinitions.Add(new RowDefinition { Height = double.IsNaN(h) ? new GridLength(1, GridUnitType.Star) : new GridLength(h) });
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Own(new TextBlock { Text = _cyber ? Loc.T("Mic check").ToUpperInvariant() : Loc.T("Mic check"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontFamily = look.Font }));
        var modes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _iModeLive = ModeButton(Loc.T("Live"), "Hear yourself in your headset as you speak", look, live: true);
        _iModeReplay = ModeButton(Loc.T("Replay"), "Speak, pause, and hear how the others hear you (fine on speakers)", look, live: false);
        modes.Children.Add(_iModeLive);
        modes.Children.Add(_iModeReplay);
        Grid.SetColumn(modes, 1);
        top.Children.Add(modes);
        right.Children.Add(top);

        _iWave = new VoiceWave(34) { Height = 20, Sharp = look.Neon, ToolTip = "Your voice, live (while testing)" };
        Grid.SetRow(_iWave, 2);
        right.Children.Add(_iWave);

        var level = new Grid();
        level.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        level.ColumnDefinitions.Add(new ColumnDefinition());
        level.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        level.Children.Add(Own(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Frozen(look.Label), VerticalAlignment = VerticalAlignment.Center }));
        _iMic = new PillSlider { Height = 18, TrackHeight = 5, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Your microphone's level" };
        _iMic.ValueChanged += v => { _audio.SetMicVolume((float)v); if (_iMicPct != null) _iMicPct.Text = $"{(int)Math.Round(v * 100)}%"; };
        Grid.SetColumn(_iMic, 1);
        level.Children.Add(_iMic);
        _iMicPct = Own(new TextBlock { FontSize = 10, Foreground = Frozen(look.Label), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = look.Font });
        System.Windows.Documents.Typography.SetNumeralAlignment(_iMicPct, FontNumeralAlignment.Tabular);
        Grid.SetColumn(_iMicPct, 2);
        level.Children.Add(_iMicPct);
        Grid.SetRow(level, 4);
        right.Children.Add(level);

        _iTestText = Own(new TextBlock { FontSize = 10.5, Foreground = Frozen(look.Label), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom, FontFamily = look.Font });
        Grid.SetRow(_iTestText, 5);
        right.Children.Add(_iTestText);
        Grid.SetColumn(right, 1);
        studio.Children.Add(right);
        card.Child = studio;
        Grid.SetRow(card, 2);
        g.Children.Add(card);

        // ── start a call
        var start = new StackPanel();
        start.Children.Add(Own(new TextBlock { Text = _cyber ? Loc.T("Start a call").ToUpperInvariant() : Loc.T("Start a call"), FontSize = 10, Foreground = Frozen(look.Label), Margin = new Thickness(2, 0, 0, 4), FontFamily = look.Font }));
        _iStart = new WrapPanel();
        start.Children.Add(_iStart);
        Grid.SetRow(start, 4);
        g.Children.Add(start);

        _iRecent = new StackPanel();
        Grid.SetRow(_iRecent, 5);
        g.Children.Add(_iRecent);
        return g;
    }

    /// <summary>The test's orb: the mic inside an aura that breathes with your voice; a click starts or stops the test.</summary>
    FrameworkElement BuildTestOrb(CallLook look)
    {
        _iOrb = new Grid { Width = 70, Height = 70, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(), VerticalAlignment = VerticalAlignment.Center };
        _iAura = new VoiceAura { Core = 42 };
        _iOrb.Children.Add(_iAura);
        var fill = new RadialGradientBrush { GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.45, 0.4), RadiusX = 0.75, RadiusY = 0.75 };
        fill.GradientStops.Add(new GradientStop(Lighten(look.Accent, 0.35), 0));
        fill.GradientStops.Add(new GradientStop(Darken(look.Accent, 0.45), 1));
        fill.Freeze();
        _iOrbDisc = look.Neon ? new Rectangle { Width = 40, Height = 40, Fill = Frozen(A(look.Accent, 0x30)), Stroke = Frozen(look.Accent), StrokeThickness = 1.2 }
            : new Ellipse { Width = 42, Height = 42, Fill = fill, Stroke = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 };
        _iOrb.Children.Add(Own(_iOrbDisc));
        _iOrbGlyph = Own(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16, Foreground = look.Neon ? Frozen(look.Accent) : Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        _iOrb.Children.Add(_iOrbGlyph);
        var b = new Button { Template = Bare, Content = _iOrb, Cursor = Cursors.Hand, Focusable = false, ToolTip = "Start or stop the mic check" };
        b.PreviewMouseLeftButtonDown += (_, _) => Squeeze(_iOrb, 0.92);
        b.PreviewMouseLeftButtonUp += (_, _) => Squeeze(_iOrb, 1);
        b.Click += (_, _) => ToggleMicCheck();
        return b;
    }

    Button ModeButton(string text, string tip, CallLook look, bool live)
    {
        var t = Own(new TextBlock { Text = _cyber ? text.ToUpperInvariant() : text, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = look.Font });
        var plate = Own(new Border { Height = 18, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(look.Neon ? 0 : 9), Child = t, Background = Brushes.Transparent, BorderThickness = new Thickness(1) });
        var b = new Button { Template = Bare, Content = plate, Cursor = Cursors.Hand, Focusable = false, ToolTip = tip, Margin = new Thickness(4, 0, 0, 0) };
        b.Click += (_, _) =>
        {
            _micTestLive = live;
            bool wasRunning = _micChecking;
            if (wasRunning) _micCheckStop?.Cancel();
            PaintModes();
            if (wasRunning) After(250, () => { if (!_micChecking) ToggleMicCheck(); });
            else _iTestText!.Text = live && !HeadsetOut ? Loc.T("Live is best with a headset (speakers will echo)") : ReadyText();
        };
        return b;
    }

    void PaintModes()
    {
        var look = CallsLook();
        foreach (var (btn, live) in new[] { (_iModeLive, true), (_iModeReplay, false) })
        {
            if (btn?.Content is not Border p || p.Child is not TextBlock t) continue;
            bool on = MicTestLive == live;
            p.Background = on ? Frozen(A(look.Accent, 0x30)) : Brushes.Transparent;
            p.BorderBrush = on ? Frozen(A(look.Accent, 0x90)) : Frozen(look.Edge);
            t.Foreground = on ? Frozen(Lighten(look.Accent, 0.2)) : Frozen(look.Label);
        }
    }

    string ReadyText() => MicTestLive ? Loc.T("Tap the mic: you'll hear yourself as you speak") : Loc.T("Tap the mic, say a few words, pause: you'll hear it back");

    void UpdateIdleCalls()
    {
        if (_iTitle == null) return;
        var look = CallsLook();
        bool muted = _audio.MicMuted;
        _iTitle.Text = muted ? Loc.T("Your mic is muted") : Loc.T("Ready for calls");
        var last = _history.FirstOrDefault();
        _iSub!.Text = last != null
            ? string.Format(Loc.T("Last call: {0} · {1} · {2}"), last.Name, Duration(last.Seconds), Ago(last.Start.ToLocalTime()))
            : Loc.T("Calls from Discord, Teams, Zoom, WhatsApp, Meet… show up here");
        var c = muted ? MutedColor : LiveColor;
        _iMicDot!.Fill = Frozen(c);
        _iMicState!.Text = muted ? Loc.T("Mic muted") : Loc.T("Mic on");
        _iMicState.Foreground = muted ? Frozen(Lighten(c, 0.2)) : Brushes.White;
        _iMicPill!.BorderBrush = new SolidColorBrush(A(c, 0x80));
        if (_iMic is { IsDragging: false }) { _iMic.Value = _audio.MicVolume; _iMicPct!.Text = $"{(int)Math.Round(_iMic.Value * 100)}%"; }
        _iMic!.FillBrush = Frozen(look.Accent);
        PaintModes();
        if (!_micChecking && _iVerdictUntil < DateTime.UtcNow) { _iTestText!.Text = ReadyText(); _iTestText.Foreground = Frozen(look.Label); }
        RefreshAudioBits();
        RenderStartCall();
        RenderRecentCalls();
    }

    DateTime _iVerdictUntil;

    void ToggleMicCheck()
    {
        if (_micChecking) { _micCheckStop?.Cancel(); return; }
        if (_call != null) return;
        if (_audio.MicMuted) { _audio.SetMicMuted(false); UpdateIdleCalls(); } // a test of a muted mic tells you nothing
        _micChecking = true;
        _micCheckStop = new CancellationTokenSource();
        bool live = MicTestLive;
        var look = CallsLook();
        _iOrbGlyph!.Text = ""; // stop
        _iGate.Reset();
        IdleFrames(true);
        var run = MicCheck.RunAsync(live,
            level => { if (level > _iLevelTarget) _iLevelTarget = level; }, // the loudest since the last frame
            phase => Dispatcher.InvokeAsync(() =>
            {
                _iPhase = phase;
                if (_iTestText == null) return;
                _iTestText.Foreground = Frozen(look.Label);
                _iTestText.Text = phase switch
                {
                    "live" => HeadsetOut ? Loc.T("Live · you hear yourself as you speak") : Loc.T("Live · on speakers it may echo"),
                    "listen" => Loc.T("Listening… say something, then pause"),
                    "play" => Loc.T("Playing it back: this is you"),
                    _ => _iTestText.Text,
                };
            }),
            _micCheckStop.Token);
        run.ContinueWith(t => Dispatcher.InvokeAsync(() => FinishMicCheck(t.Result)));
    }

    /// <summary>The test is over: what it heard, in one line (with the fix, when there's one).</summary>
    void FinishMicCheck(MicCheck.Verdict? v)
    {
        _micChecking = false;
        _iLevelTarget = 0;
        _iPhase = "";
        if (_iOrbGlyph != null) _iOrbGlyph.Text = "";
        if (_iTestText == null) return;
        string text;
        Color c;
        Action? fix = null;
        if (v == null) { text = Loc.T("The microphone couldn't be opened"); c = MutedColor; }
        else if (!v.Heard) { text = Loc.T("Didn't hear you · check the mic in Audio"); c = Tint(CallBtn.Quiet); fix = () => ToggleCallSheet(Sheet.Audio); }
        else if (v.Clipping > 0.06f) { text = Loc.T("Too loud: you crackle · click to lower"); c = Tint(CallBtn.Quiet); fix = () => NudgeMic(-0.15f); }
        else if (v.Loudness < 0.3f && _audio.MicVolume < 0.97f) { text = Loc.T("A bit quiet · click to raise"); c = Tint(CallBtn.Hand); fix = () => NudgeMic(0.15f); }
        else { text = Loc.T("You sound good ✓"); c = LiveColor; }
        _iTestText.Text = text;
        _iTestText.Foreground = Frozen(c);
        _iTestText.Cursor = fix != null ? Cursors.Hand : null;
        _iTestText.Tag = fix;
        _iTestText.MouseLeftButtonUp -= OnVerdictClick;
        _iTestText.MouseLeftButtonUp += OnVerdictClick;
        _iTestText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        _iVerdictUntil = DateTime.UtcNow.AddSeconds(12);
        if (fix == null && v != null && _iOrb != null) Ripple(_iOrb, c);
    }

    void OnVerdictClick(object sender, MouseButtonEventArgs e)
    {
        if (_iTestText?.Tag is not Action fix) return;
        e.Handled = true;
        _iTestText.Tag = null;
        _iTestText.Cursor = null;
        fix();
    }

    void NudgeMic(float by)
    {
        float v = Math.Clamp(_audio.MicVolume + by, 0.05f, 1f);
        _audio.SetMicVolume(v);
        if (_iMic != null) { _iMic.Value = v; _iMicPct!.Text = $"{(int)Math.Round(v * 100)}%"; }
        _iTestText!.Text = string.Format(Loc.T("Mic level {0}% · test again?"), (int)Math.Round(v * 100));
        _iTestText.Foreground = Frozen(CallsLook().Label);
    }

    static void Ripple(Grid host, Color c)
    {
        var ring = new Ellipse { Name = "CallGlow", Width = 42, Height = 42, Stroke = Frozen(c), StrokeThickness = 2, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
        host.Children.Add(ring);
        var grow = new DoubleAnimation(1, 1.6, TimeSpan.FromMilliseconds(560)) { EasingFunction = Out };
        var fade = new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(560));
        fade.Completed += (_, _) => host.Children.Remove(ring);
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The waveform and the aura move every frame while a test runs (and settle after); then the hook goes.</summary>
    void IdleFrames(bool on)
    {
        if (on == _iFrameOn) return;
        _iFrameOn = on;
        if (on) { _iFrameLast = TimeSpan.Zero; CompositionTarget.Rendering += OnIdleFrame; }
        else CompositionTarget.Rendering -= OnIdleFrame;
    }

    void OnIdleFrame(object? sender, EventArgs e)
    {
        // leaving the page or closing the notch ends a test (nothing listens behind your back)
        if (_micChecking && (_mode != Mode.Expanded || _page != CallsPage || _call != null)) _micCheckStop?.Cancel();
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _iFrameLast == TimeSpan.Zero ? 1 / 60.0 : (now - _iFrameLast).TotalSeconds;
        if (dt <= 0) return;
        _iFrameLast = now;
        dt = Math.Min(dt, 1 / 20.0);
        _iGate.Feed(_iLevelTarget, dt);
        if (_micChecking) _iLevelTarget = 0;
        double target = _iGate.Voiced ? Math.Max(0.15, _iGate.Level) : 0;
        _iLevelNow += (target - _iLevelNow) * (1 - Math.Exp(-dt * (target > _iLevelNow ? 22 : 7)));
        var c = _iPhase == "play" ? Tint(CallBtn.Camera) : LiveColor;
        if (_iAura != null) { _iAura.Tint = c; _iAura.Step(_iLevelNow, dt); }
        if (_iWave != null) { _iWave.Tint = c; _iWave.Push(_iLevelNow, _iGate.Voiced, _iGate.Noise, dt); }
        if (!_micChecking && _iLevelNow < 0.005) { _iAura?.Step(0, 0); IdleFrames(false); }
    }

    /// <summary>The call apps you have, one click to open (and a new Meet, in the browser).</summary>
    void RenderStartCall()
    {
        if (_iStart == null) return;
        var look = CallsLook();
        _iStart.Children.Clear();
        var apps = new[] { "Discord", "Microsoft Teams", "Teams", "Zoom", "Zoom Workplace", "WhatsApp", "Telegram", "Slack", "Signal", "Skype" };
        var seen = new HashSet<string>();
        foreach (var name in apps)
        {
            var e = _index.Entries.FirstOrDefault(x => x.Kind == LauncherIndex.Kind.App && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (e == null || !seen.Add(name.Split(' ')[0])) continue;
            var icon = Own(new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(look.Neon ? 0 : 4), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            IconInto(icon, e.Target);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(icon);
            row.Children.Add(Own(new TextBlock { Text = e.Name.Replace("Microsoft ", "").Replace(" Workplace", ""), FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontFamily = look.Font }));
            var b = CallPlate(row, look, null, out _, 28);
            b.Margin = new Thickness(0, 0, 6, 0);
            var entry = e;
            b.Click += (_, _) => { _index.Used(entry); OpenTarget(entry.Target); Collapse(); };
            _iStart.Children.Add(b);
            if (_iStart.Children.Count >= 4) break;
        }
        var meetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center };
        meetRow.Children.Add(Own(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Foreground = Frozen(look.Accent), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center }));
        meetRow.Children.Add(Own(new TextBlock { Text = Loc.T("New Meet"), FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontFamily = look.Font }));
        var meet = CallPlate(meetRow, look, look.Accent, out var meetPlate, 28);
        meetPlate.BorderBrush = new SolidColorBrush(A(look.Accent, 0x60));
        meet.Margin = new Thickness(0, 0, 6, 0);
        meet.ToolTip = "A new Google Meet, in your browser";
        meet.Click += (_, _) => { Launch("https://meet.new"); Collapse(); };
        _iStart.Children.Add(meet);
    }

    /// <summary>Your last two calls: a click opens that app again.</summary>
    void RenderRecentCalls()
    {
        if (_iRecent == null) return;
        var look = CallsLook();
        _iRecent.Children.Clear();
        if (!_settings.CallHistory || _history.Count == 0) return;
        _iRecent.Children.Add(Own(new TextBlock { Text = _cyber ? Loc.T("Recent").ToUpperInvariant() : Loc.T("Recent"), FontSize = 10, Foreground = Frozen(look.Label), Margin = new Thickness(2, 4, 0, 2), FontFamily = look.Font }));
        foreach (var c in _history.Take(2))
        {
            var row = new Grid { Height = 18, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = Own(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(look.Neon ? 0 : 3), HorizontalAlignment = HorizontalAlignment.Left });
            if (c.Path != null) IconInto(icon, c.Path);
            row.Children.Add(icon);
            var name = Own(new TextBlock { Text = c.Name, FontSize = 11, Foreground = Frozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = look.Font });
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var when = Own(new TextBlock { Text = $"{Duration(c.Seconds)}  ·  {Ago(c.Start.ToLocalTime())}", FontSize = 10, Foreground = Frozen(look.Label), VerticalAlignment = VerticalAlignment.Center, FontFamily = look.Font });
            Grid.SetColumn(when, 2);
            row.Children.Add(when);
            if (c.Path != null && System.IO.File.Exists(c.Path))
            {
                string path = c.Path;
                row.Cursor = Cursors.Hand;
                row.ToolTip = string.Format(Loc.T("Open {0}"), c.Name);
                row.MouseEnter += (_, _) => name.Foreground = Brushes.White;
                row.MouseLeave += (_, _) => name.Foreground = Frozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));
                row.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenTarget(path); Collapse(); };
            }
            _iRecent.Children.Add(row);
        }
    }
}
