using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicNotch.Controls;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Now and then, Pixo's face shares one useful thing in a little bubble under the notch ("Drop a file on me to ask
/// about it", "Alt+Space finds apps, files and answers"...). Each tip is said once, the ones that fit what you're doing
/// first (a lot copied: the clipboard), never more than one every 40 minutes, never while
/// you type, in a call, a meeting, a game or full screen. The bubble goes by itself after a few seconds, or as soon as
/// you reach for the notch. Off in the preferences ("Tips from Pixo").
/// </summary>
public partial class NotchWindow
{
    sealed record FaceTip(string Id, string Text, Func<bool>? Fits = null);

    Border? _tipBubble;
    TextBlock? _tipBubbleText;
    readonly DispatcherTimer _tipClock = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
    DateTime _tipLast = DateTime.UtcNow.AddMinutes(-36); // the first one a few minutes after starting
    DateTime _tipShownAt;
    bool _tipUp;

    FaceTip[] Tips() => new FaceTip[]
    {
        new("drop-ask", "Drop a file on me: keep it on the shelf, or ask me about it", () => _settings.AiChat),
        new("search", $"{LauncherKeyName} finds apps, files, settings and answers", () => _settings.SearchHotkey != "off"),
        new("commands", "Type things like “volume 30” or “remind me at 5 to call” in the search", () => _settings.SearchHotkey != "off" && _settings.SearchCommands),
        new("open", "Win+Alt+N opens the control center from anywhere"),
        new("radio-hold", "Hold the Wi-Fi or Bluetooth button to pick a network or a device", () => _radio.HasWifi || _radio.HasBluetooth),
        new("song", "Hearing a song you like? The wave button on Home names it"),
        new("hat", "Right-click me for a new hat"),
        new("poke", "Click me, I don't mind… three times and I get dizzy"),
        new("clip", "Everything you copy is kept: the Clipboard page has it all", () => _clips.Count >= 5),
        new("chat-tab", "In the empty search, Tab brings back your last chat", () => _settings.AiChat && _settings.SearchHotkey != "off"),
        new("shift-enter", "Shift+Enter in the search asks the AI whatever you typed", () => _settings.AiChat && _settings.SearchHotkey != "off"),
        new("type-open", "With the notch open, just start typing to search", () => _settings.SearchTypeToOpen),
        new("dots", "The wheel over the dots moves between pages", null),
        new("work", "The Work page puts your files and tabs into projects by itself", null),
        new("colors", "The Colors page has a shuffle: a whole new palette in one click", null),
        new("lock", "Presenting? Lock me from the tray icon: I'll show things but won't react", null),
    };

    void WireFaceTips()
    {
        _tipClock.Tick += (_, _) => TipTick();
        _tipClock.Start();
    }

    /// <summary>Time for a tip? Only when the face is resting in the notch and you're around but not busy.</summary>
    void TipTick()
    {
        if (_tipUp) { if ((DateTime.UtcNow - _tipShownAt).TotalSeconds > 9 || !FaceTipAllowed()) TipHide(); return; }
        if (!_settings.FaceTips || (DateTime.UtcNow - _tipLast).TotalMinutes < 40 || !FaceTipAllowed()) return;
        double idle = IdleTime().TotalSeconds;
        if (idle > 40 || _faceKeys.Count(k => k) > 0) return; // away, or typing: later
        var seen = _settings.FaceTipsSeen;
        var tips = Tips().Where(t => !seen.Contains(t.Id) && (t.Fits?.Invoke() ?? true)).ToList();
        if (tips.Count == 0) { _tipClock.Stop(); return; } // all said
        // what fits the moment first (the ones with a condition), then the rest in order
        var tip = tips.FirstOrDefault(t => t.Fits != null && t.Id == "clip") ?? tips[0];
        _tipLast = DateTime.UtcNow;
        seen.Add(tip.Id);
        _settings.Save();
        TipShow(tip.Text);
    }

    bool FaceTipAllowed() => _settings.NotchFace && _settings.FaceTips && _faceShown && _mode is Mode.Idle or Mode.Media && !_cOut && !_hidden && !_peek && !_dodged && !_pulling && !_pressed && !_launcherOpen && _pop == null
        && !_fgFullscreen && !InGame && !OnCall && !_inMeeting && !_presenter && !_focus.IsOn && !Ringing;

    void TipShow(string text)
    {
        if (_tipBubble == null)
        {
            _tipBubbleText = new TextBlock { FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 270, VerticalAlignment = VerticalAlignment.Center };
            _tipBubble = new Border
            {
                Child = _tipBubbleText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                Padding = new Thickness(11, 5, 12, 6), BorderThickness = new Thickness(1), Opacity = 0, Visibility = Visibility.Collapsed,
                RenderTransformOrigin = new Point(0.5, 0), RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } },
            };
            Panel.SetZIndex(_tipBubble, 50);
            Root.Children.Add(_tipBubble);
        }
        var acc = _cyber ? CyCyan : PalAccent();
        _tipBubble.Background = Frozen(_cyber ? Color.FromArgb(0xF2, 0x03, 0x0A, 0x10) : Color.FromArgb(0xF2, 0x16, 0x16, 0x1A));
        _tipBubble.BorderBrush = Frozen(_cyber ? A(CyCyan, 0x90) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        _tipBubble.CornerRadius = new CornerRadius(_cyber ? 0 : 11);
        _tipBubble.Margin = new Thickness(_leanShift.X * 2, Math.Max(26, _sh.Value) + 7, 0, 0);
        _tipBubbleText!.FontFamily = _cyber ? CyFont : UiText;
        _tipBubbleText.Inlines.Clear();
        _tipBubbleText.Inlines.Add(new System.Windows.Documents.Run(_cyber ? "TIP  " : "Tip  ") { Foreground = Frozen(acc), FontWeight = FontWeights.SemiBold });
        _tipBubbleText.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = Frozen(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)) });
        _tipBubble.Visibility = Visibility.Visible;
        _tipUp = true;
        _tipShownAt = DateTime.UtcNow;
        // the face lights up, glances down at its bubble, then the bubble pops out of the notch
        _face?.Play(NotchFace.Emote.Happy, 1.2);
        _face?.LookAt(0, -0.8);
        FeelSound(Services.Sfx.Cue.Tap);
        var group = (TransformGroup)_tipBubble.RenderTransform;
        var scale = (ScaleTransform)group.Children[0];
        var shift = (TranslateTransform)group.Children[1];
        var t = TimeSpan.FromMilliseconds(Eco ? 1 : 380);
        var pop = new DoubleAnimation(0.6, 1, t) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, t) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _tipBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        // longer words, a little longer up
        int ms = Math.Clamp(3500 + text.Length * 45, 5000, 8000);
        After(ms, () => { if (_tipUp && (DateTime.UtcNow - _tipShownAt).TotalMilliseconds >= ms - 50) TipHide(); });
        _tipClock.Interval = TimeSpan.FromSeconds(1); // while it's up: gone the moment you reach for the notch
    }

    /// <summary>Each frame of the notch's spring: the bubble stays just under its edge, wherever the edge goes.</summary>
    void TipFollow()
    {
        if (!_tipUp || _tipBubble == null) return;
        _tipBubble.Margin = new Thickness(_leanShift.X * 2, Math.Max(26, _sh.Value) + 7, 0, 0);
    }

    void TipHide()
    {
        if (!_tipUp || _tipBubble == null) return;
        _tipUp = false;
        _tipClock.Interval = TimeSpan.FromSeconds(30);
        var b = _tipBubble;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Eco ? 1 : 180));
        fade.Completed += (_, _) => { if (!_tipUp) b.Visibility = Visibility.Collapsed; };
        b.BeginAnimation(OpacityProperty, fade);
        if (((TransformGroup)b.RenderTransform).Children[1] is TranslateTransform shift)
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, TimeSpan.FromMilliseconds(180)));
    }
}
