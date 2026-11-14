using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Controls;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The Calls page, second in the control center: your call, run from the notch.
/// During a call:
///  • a header: the app inside a voice aura, a live timer, badges that pop in (muted, camera, presenting, deafened)
///    and how much of the call you've talked; a way back to the call;
///  • the mute orb: it glows with your voice; a click mutes the call (only the call: the notch still hears you, so it
///    can tell you when you talk while muted), holding it while muted lets you talk;
///  • tiles for the app: camera, share, raise hand, chat and end (hold to confirm) through the app's own shortcuts,
///    deafen, presenter mode and quieting the other apps in any app;
///  • your mic level (your voice moving under it) and the call's own volume;
///  • a dock: Audio (your speakers and mics, one click) and Quick replies (a grid, one click: copied, the call's chat
///    opened, Ctrl+V sends), both sliding up over the page, nothing to scroll.
/// Between calls: a "hear yourself" mic test, your devices, a way to start a call in the apps you have (and a new
/// Meet), and your last calls. Every theme dresses it its own way.
/// </summary>
public partial class NotchWindow
{
    const int CallsPage = 6;

    Grid? _callsPage, _callLive, _callIdle;
    string? _callsBuiltFor;

    // ───────────────────────────── the look, per theme ─────────────────────────────

    /// <summary>The page's colours and shapes for the theme in use (the skins leave these parts alone: they're the page's own).</summary>
    sealed record CallLook(Color Accent, Color Plate, Color Edge, Color Label, double Radius, FontFamily Font, bool Neon);

    CallLook CallsLook()
    {
        var s = Skin;
        return s.Key switch
        {
            "neon" => new CallLook(CyCyan, Color.FromArgb(0x1C, CyCyan.R, CyCyan.G, CyCyan.B), Color.FromArgb(0x55, CyCyan.R, CyCyan.G, CyCyan.B), Color.FromArgb(0xB0, CyCyan.R, CyCyan.G, CyCyan.B), 0, CyFont, true),
            "liquid" => new CallLook(s.Accent, Color.FromArgb(0x1E, 0x7F, 0xE7, 0xD9), Color.FromArgb(0x40, 0xA7, 0x8B, 0xFA), s.Label, 12, UiText, false),
            "crystal" => new CallLook(s.Accent, Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF), s.Label, 10, UiText, false),
            "abyss" => new CallLook(s.Accent, Color.FromArgb(0x1A, 0x2E, 0xE6, 0xD6), Color.FromArgb(0x38, 0x2E, 0xE6, 0xD6), s.Label, 9, UiText, false),
            _ => new CallLook(s.Accent, Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), 10, UiText, false),
        };
    }

    /// <summary>Each action's own colour (Neon: its neon cousin).</summary>
    Color Tint(CallBtn kind)
    {
        bool neon = _cyber;
        return kind switch
        {
            CallBtn.Camera => neon ? CyCyan : Color.FromRgb(0x0A, 0x84, 0xFF),
            CallBtn.Share => neon ? Color.FromRgb(0x00, 0xFF, 0xC6) : Color.FromRgb(0x30, 0xD5, 0xC8),
            CallBtn.Hand => neon ? CyYellow : Color.FromRgb(0xFF, 0xB0, 0x20),
            CallBtn.Deafen => neon ? CyMagenta : Color.FromRgb(0xFF, 0x37, 0x5F),
            CallBtn.Presenter => neon ? CyMagenta : Color.FromRgb(0x8E, 0x7C, 0xFF),
            CallBtn.Quiet => neon ? CyYellow : Color.FromRgb(0xFF, 0x9F, 0x0A),
            CallBtn.Messages => neon ? CyCyan : Color.FromRgb(0x30, 0xD1, 0x58),
            CallBtn.Captions => neon ? CyCyan : Color.FromRgb(0x64, 0xD2, 0xFF),
            CallBtn.Snip => neon ? CyYellow : Color.FromRgb(0xFF, 0x64, 0x82),
            CallBtn.HangUp => neon ? CyRed : Color.FromRgb(0xFF, 0x45, 0x3A),
            _ => neon ? CyCyan : Colors.White,
        };
    }

    Color LiveColor => _cyber ? Color.FromRgb(0x00, 0xFF, 0xA3) : Color.FromRgb(0x30, 0xD1, 0x58);
    Color MutedColor => _cyber ? CyRed : Color.FromRgb(0xFF, 0x45, 0x3A);
    Color StateColor => CallMuted ? MutedColor : _presenter ? Tint(CallBtn.Presenter) : LiveColor;

    static T Own<T>(T el) where T : FrameworkElement { el.Name = "CallGlow"; return el; } // the page's own look: the skins pass it by

    static ControlTemplate? _bare;
    static ControlTemplate Bare => _bare ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><ContentPresenter/></ControlTemplate>");

    static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    // ───────────────────────────── building ─────────────────────────────

    /// <summary>Makes the page (it goes second in the strip; its number stays 6, every other page keeps its own).</summary>
    Grid BuildCallsPage()
    {
        _callsPage = new Grid { Width = PageWidth, Height = 216 };
        _callTalk.Tick += (_, _) => SampleTalk();
        _audio.DevicesChanged += () => { if (_page == CallsPage && _mode == Mode.Expanded) RefreshAudioBits(); };
        _privacy.Changed += () => { if (_page == CallsPage && _mode == Mode.Expanded) UpdateCallsPage(); };
        _audio.MicChanged += () => { if (_call == null && _page == CallsPage && _mode == Mode.Expanded) UpdateIdleCalls(); };
        LoadCallHistory();
        RebuildCallsPage();
        return _callsPage;
    }

    /// <summary>(Re)builds both faces of the page in the theme's look.</summary>
    void RebuildCallsPage()
    {
        if (_callsPage == null) return;
        _callsBuiltFor = Skin.Key + "|" + _accent;
        CloseCallSheet(instant: true);
        _badges.Clear();
        _callsPage.Children.Clear();
        _callLive = BuildLiveCall();
        _callIdle = BuildIdleCalls();
        _callsPage.Children.Add(_callLive);
        _callsPage.Children.Add(_callIdle);
        _callsPage.Children.Add(BuildCallFx());
        _callsPage.Children.Add(BuildSheetHost());
        if (_call != null) BuildCallButtons();
    }

    // the live face's parts
    VoiceAura? _headAura, _orbAura;
    Border? _cIcon;
    TextBlock? _cName, _cTimer, _cOrbGlyph, _cOrbLabel;
    StackPanel? _cBadges;
    Ellipse? _orbLive, _orbMute;
    Grid? _callOrb;
    UniformGrid? _cTiles;
    PillSlider? _cMic, _cVol;
    Rectangle? _cMicVoice;
    TextBlock? _cMicPct, _cVolPct, _cAudioName, _cRepliesCount;

    Grid BuildLiveCall()
    {
        var look = CallsLook();
        var g = new Grid { Margin = new Thickness(20, 0, 20, 0), Visibility = Visibility.Collapsed };
        foreach (var h in new[] { 46.0, 4, 84, 4, 26, 4, double.NaN })
            g.RowDefinitions.Add(new RowDefinition { Height = double.IsNaN(h) ? new GridLength(1, GridUnitType.Star) : new GridLength(h) });

        // ── header: the app in its aura, the name, the timer and the badges, the way back
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconHost = new Grid { Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Left };
        _headAura = new VoiceAura { Core = 30 };
        iconHost.Children.Add(_headAura);
        _cIcon = Own(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(look.Neon ? 0 : 9), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        iconHost.Children.Add(_cIcon);
        head.Children.Add(iconHost);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
        var line1 = new StackPanel { Orientation = Orientation.Horizontal };
        _cName = Own(new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120, VerticalAlignment = VerticalAlignment.Center, FontFamily = look.Font });
        _cTimer = Own(new TextBlock { FontSize = 12.5, Foreground = Frozen(look.Label), Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = look.Neon ? CyFont : new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = FontWeights.SemiBold });
        System.Windows.Documents.Typography.SetNumeralAlignment(_cTimer, FontNumeralAlignment.Tabular);
        line1.Children.Add(_cName);
        line1.Children.Add(_cTimer);
        _cWave = new VoiceWave { Width = 40, Height = 14, Margin = new Thickness(9, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Sharp = look.Neon, ToolTip = "Your voice, live" };
        line1.Children.Add(_cWave);
        names.Children.Add(line1);
        _cBadges = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Height = 15 };
        names.Children.Add(_cBadges);
        Grid.SetColumn(names, 1);
        head.Children.Add(names);
        var back = RoundIcon("\uE8A7", look, "Back to the call's window");
        back.Click += async (_, _) => { await BackToCallAsync(); Collapse(); };
        back.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(back, 2);
        head.Children.Add(back);
        var leave = BuildLeaveButton(look);
        Grid.SetColumn(leave, 3);
        head.Children.Add(leave);
        g.Children.Add(head);

        // ── the deck: the mute orb, and the tiles
        var deck = new Grid();
        deck.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        deck.ColumnDefinitions.Add(new ColumnDefinition());
        deck.Children.Add(BuildOrb(look));
        _cTiles = new UniformGrid { Columns = 3, Rows = 2, Margin = new Thickness(4, 2, 0, 2) };
        Grid.SetColumn(_cTiles, 1);
        deck.Children.Add(_cTiles);
        Grid.SetRow(deck, 2);
        g.Children.Add(deck);

        // ── levels: your mic (your voice under it), the call's volume
        var levels = new Grid();
        levels.ColumnDefinitions.Add(new ColumnDefinition());
        levels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        levels.ColumnDefinitions.Add(new ColumnDefinition());
        _cMic = new PillSlider { Height = 20, TrackHeight = 5, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Your microphone's level" };
        _cMic.ValueChanged += v => { _audio.SetMicVolume((float)v); if (_cMicPct != null) _cMicPct.Text = $"{(int)Math.Round(v * 100)}"; };
        _cMicVoice = Own(new Rectangle { Height = 2, RadiusX = 1, RadiusY = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Width = 0, IsHitTestVisible = false, Margin = new Thickness(0, 0, 0, 1) });
        levels.Children.Add(LevelRow("\uE720", _cMic, _cMicVoice, look, out _cMicPct));
        _cVol = new PillSlider { Height = 20, TrackHeight = 5, VerticalAlignment = VerticalAlignment.Center, ToolTip = "The call's volume (only the call)" };
        _cVol.ValueChanged += v => { if (_callMixKey != null) _audio.SetMixerVolume(_callMixKey, (float)v); if (_cVolPct != null) _cVolPct.Text = $"{(int)Math.Round(v * 100)}"; };
        var vol = LevelRow("\uE767", _cVol, null, look, out _cVolPct);
        Grid.SetColumn(vol, 2);
        levels.Children.Add(vol);
        Grid.SetRow(levels, 4);
        g.Children.Add(levels);

        // ── the dock: Audio, Quick replies (each opens a sheet over the page)
        var dock = new Grid { VerticalAlignment = VerticalAlignment.Top };
        dock.ColumnDefinitions.Add(new ColumnDefinition());
        dock.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        dock.ColumnDefinitions.Add(new ColumnDefinition());
        var audio = DockButton("\uE7F6", "Audio", look, out _cAudioName);
        audio.Click += (_, _) => ToggleCallSheet(Sheet.Audio);
        dock.Children.Add(audio);
        var replies = DockButton("\uE8BD", Loc.T("Quick replies"), look, out _cRepliesCount);
        replies.Click += (_, _) => ToggleCallSheet(Sheet.Replies);
        Grid.SetColumn(replies, 2);
        dock.Children.Add(replies);
        Grid.SetRow(dock, 6);
        g.Children.Add(dock);
        return g;
    }

    /// <summary>The mute orb: it glows with your voice, turns red when the call is muted, ripples when it changes.</summary>
    FrameworkElement BuildOrb(CallLook look)
    {
        var col = new Grid();
        col.RowDefinitions.Add(new RowDefinition());
        col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        _callOrb = new Grid { Width = 84, Height = 66, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform() };
        _orbAura = new VoiceAura { Core = 50 };
        _callOrb.Children.Add(_orbAura);
        RadialGradientBrush Fill(Color a, Color b)
        {
            var r = new RadialGradientBrush { GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.45, 0.4), RadiusX = 0.75, RadiusY = 0.75 };
            r.GradientStops.Add(new GradientStop(a, 0));
            r.GradientStops.Add(new GradientStop(b, 1));
            r.Freeze();
            return r;
        }
        Color live = LiveColor, muted = MutedColor;
        _orbLive = Own(new Ellipse { Width = 50, Height = 50, Fill = Fill(Lighten(live, 0.35), Darken(live, 0.35)) });
        _orbMute = Own(new Ellipse { Width = 50, Height = 50, Fill = Fill(Lighten(muted, 0.3), Darken(muted, 0.4)), Opacity = 0 });
        var rim = Own(new Ellipse { Width = 50, Height = 50, Stroke = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), StrokeThickness = look.Neon ? 1.6 : 1 });
        var sheen = Own(new Ellipse { Width = 44, Height = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 22), IsHitTestVisible = false,
            Fill = Frozen(new LinearGradientBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90)) });
        _cOrbGlyph = Own(new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 20, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        foreach (var el in new UIElement[] { _orbLive, _orbMute, sheen, rim, _cOrbGlyph }) _callOrb.Children.Add(el);
        var btn = new Button { Template = Bare, Content = _callOrb, Cursor = Cursors.Hand, Focusable = false, ToolTip = "Mute the call (the notch still hears you) · hold while muted to talk" };
        WireMute(btn);
        col.Children.Add(btn);
        _cOrbLabel = Own(new TextBlock { FontSize = 10.5, Foreground = Frozen(look.Label), HorizontalAlignment = HorizontalAlignment.Center, FontFamily = look.Font, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 92 });
        Grid.SetRow(_cOrbLabel, 1);
        col.Children.Add(_cOrbLabel);
        return col;
    }

    static Color Lighten(Color c, double t) => Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
    static Color Darken(Color c, double t) => Color.FromRgb((byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));

    Grid LevelRow(string glyph, PillSlider slider, FrameworkElement? under, CallLook look, out TextBlock pct)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        g.Children.Add(Own(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11.5, Foreground = Frozen(look.Label), VerticalAlignment = VerticalAlignment.Center }));
        var host = new Grid();
        host.Children.Add(slider);
        if (under != null) host.Children.Add(under);
        Grid.SetColumn(host, 1);
        g.Children.Add(host);
        pct = Own(new TextBlock { FontSize = 10, Foreground = Frozen(look.Label), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = look.Font });
        System.Windows.Documents.Typography.SetNumeralAlignment(pct, FontNumeralAlignment.Tabular);
        Grid.SetColumn(pct, 2);
        g.Children.Add(pct);
        return g;
    }

    /// <summary>A plate that lifts a little under the pointer and gives under the finger (any content inside).</summary>
    Button CallPlate(FrameworkElement content, CallLook look, Color? tint, out Border plate, double height)
    {
        var pb = new SolidColorBrush(look.Plate);
        var eb = new SolidColorBrush(look.Edge);
        plate = Own(new Border
        {
            Height = height, CornerRadius = new CornerRadius(look.Radius), Background = pb, BorderBrush = eb, BorderThickness = new Thickness(1),
            Child = content, RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } },
        });
        var p = plate;
        var b = new Button { Template = Bare, Content = p, Cursor = Cursors.Hand, Focusable = false };
        Color hover = tint ?? look.Accent;
        b.MouseEnter += (_, _) =>
        {
            if (p.Tag is "on") return;
            eb.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(A(hover, 0x90), TimeSpan.FromMilliseconds(140)));
            ((TranslateTransform)((TransformGroup)p.RenderTransform).Children[1]).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-1.5, TimeSpan.FromMilliseconds(160)) { EasingFunction = Out });
        };
        b.MouseLeave += (_, _) =>
        {
            if (p.Tag is not "on") eb.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(look.Edge, TimeSpan.FromMilliseconds(220)));
            ((TranslateTransform)((TransformGroup)p.RenderTransform).Children[1]).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = Out });
        };
        b.PreviewMouseLeftButtonDown += (_, _) => Squeeze(p, 0.95);
        b.PreviewMouseLeftButtonUp += (_, _) => Squeeze(p, 1);
        return b;
    }

    static void Squeeze(FrameworkElement el, double to)
    {
        var st = el.RenderTransform as ScaleTransform ?? (el.RenderTransform as TransformGroup)?.Children.OfType<ScaleTransform>().FirstOrDefault();
        if (st == null) return;
        var a = to < 1 ? new DoubleAnimation(to, TimeSpan.FromMilliseconds(90)) : new DoubleAnimation(to, TimeSpan.FromMilliseconds(380)) { EasingFunction = new SpringEase { Damping = 0.45 } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    Button RoundIcon(string glyph, CallLook look, string tip)
    {
        var t = Own(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = Frozen(look.Accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var b = CallPlate(t, look, null, out var plate, 30);
        plate.Width = 30;
        plate.CornerRadius = new CornerRadius(look.Neon ? 0 : 15);
        b.ToolTip = tip;
        return b;
    }

    Button DockButton(string glyph, string text, CallLook look, out TextBlock right)
    {
        var row = new Grid { Margin = new Thickness(10, 0, 10, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row.Children.Add(Own(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = Frozen(look.Accent), VerticalAlignment = VerticalAlignment.Center }));
        var label = Own(new TextBlock { Text = text, FontSize = 11.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontFamily = look.Font, Margin = new Thickness(0, 0, 8, 0) });
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        right = Own(new TextBlock { FontSize = 10.5, Foreground = Frozen(look.Label), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = look.Font });
        Grid.SetColumn(right, 2);
        row.Children.Add(right);
        var chev = Own(new TextBlock { Text = "\uE70E", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = Frozen(look.Label), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(chev, 3);
        row.Children.Add(chev);
        return CallPlate(row, look, null, out _, 30);
    }

    // ───────────────────────────── the tiles ─────────────────────────────

    enum CallBtn { Mute, Camera, Share, Hand, Deafen, Presenter, Quiet, Messages, Captions, Snip, HangUp }

    sealed class Tile
    {
        public required Button Btn;
        public required Border Plate;
        public required CallBtn Kind;
        public required SolidColorBrush Icon, Glyph;
        public required TextBlock Label, GlyphText;
        public bool On;
    }

    readonly List<Tile> _tiles = new();

    /// <summary>The tiles for this call's app: what it can do, the most useful first, end always last.</summary>
    void BuildCallButtons()
    {
        if (_cTiles == null) return;
        _cTiles.Children.Clear();
        _tiles.Clear();
        string? app = _call != null && _settings.CallShortcuts ? CallKeys.AppOf(_call.Key, _call.Name) : null;
        bool Has(CallKeys.Act a) => app != null && CallKeys.For(app, a) != null;
        var want = new List<CallBtn>();
        if (Has(CallKeys.Act.Camera)) want.Add(CallBtn.Camera);
        if (Has(CallKeys.Act.Share)) want.Add(CallBtn.Share);
        if (Has(CallKeys.Act.Chat)) want.Add(CallBtn.Messages); // only where the app really opens its chat
        want.Add(CallBtn.Deafen);
        if (HasLiveCaptions) want.Add(CallBtn.Captions);
        want.Add(CallBtn.Presenter);
        if (Has(CallKeys.Act.Hand)) want.Add(CallBtn.Hand);
        want.Add(CallBtn.Quiet);
        want.Add(CallBtn.Snip);
        bool end = false; // leaving has its own button now (any app), in the header
        var shown = want.Take(end ? 5 : 6).ToList();
        if (end) shown.Add(CallBtn.HangUp);
        var look = CallsLook();
        int i = 0;
        foreach (var kind in shown)
        {
            var t = MakeTile(kind, look);
            _tiles.Add(t);
            _cTiles.Children.Add(t.Btn);
            PaintTile(t, animate: false);
            // they come in one after another
            t.Btn.Opacity = 0;
            t.Btn.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(30 * i) });
            i++;
        }
    }

    Tile MakeTile(CallBtn kind, CallLook look)
    {
        var tint = Tint(kind);
        var iconBrush = new SolidColorBrush(A(tint, 0x2C));
        var glyphBrush = new SolidColorBrush(tint);
        var glyph = Own(new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = glyphBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var icon = Own(new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(look.Neon ? 0 : Math.Max(0, look.Radius - 3)), Background = iconBrush, Child = glyph });
        var label = Own(new TextBlock { FontSize = 11, Foreground = Frozen(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = look.Font });
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        row.Children.Add(icon);
        row.Children.Add(label);
        var btn = CallPlate(row, look, tint, out var plate, 36);
        btn.Margin = new Thickness(3);
        var t = new Tile { Btn = btn, Plate = plate, Kind = kind, Icon = iconBrush, Glyph = glyphBrush, Label = label, GlyphText = glyph };
        if (kind == CallBtn.HangUp) WireHangUp(t);
        else btn.Click += (_, _) => OnCallButton(kind);
        return t;
    }

    /// <summary>A tile's words, and its colours: lit in its own colour when it's on (the change fades).</summary>
    void PaintTile(Tile t, bool animate = true)
    {
        var look = CallsLook();
        var tint = Tint(t.Kind);
        bool on;
        switch (t.Kind)
        {
            case CallBtn.Camera: t.GlyphText.Text = "\uE714"; t.Label.Text = Loc.T("Camera"); on = _privacy.CamInUse; t.Btn.ToolTip = "Turn the call's camera on or off"; break;
            case CallBtn.Share: t.GlyphText.Text = "\uE72D"; t.Label.Text = Loc.T("Share"); on = false; t.Btn.ToolTip = "Share your screen in the call"; break;
            case CallBtn.Hand: t.GlyphText.Text = "\uE7C9"; t.Label.Text = Loc.T("Raise hand"); on = false; t.Btn.ToolTip = "Raise or lower your hand"; break;
            case CallBtn.Deafen: t.GlyphText.Text = "\uE7F6"; t.Label.Text = _deafened ? Loc.T("Deafened") : Loc.T("Deafen"); on = _deafened; t.Btn.ToolTip = "Hear nothing and say nothing, in one click"; break;
            case CallBtn.Presenter: t.GlyphText.Text = "\uE7F4"; t.Label.Text = Loc.T("Present"); on = _presenter; t.Btn.ToolTip = "Presenter mode: notifications held, no pop-ups, a clean desktop (for screen sharing)"; break;
            case CallBtn.Quiet: t.GlyphText.Text = "\uE74F"; t.Label.Text = Loc.T("Quiet others"); on = _ducked.Count > 0; t.Btn.ToolTip = "Turn the other apps down to a whisper"; break;
            case CallBtn.Messages: t.GlyphText.Text = "\uE8BD"; t.Label.Text = Loc.T("Chat"); on = false; t.Btn.ToolTip = "Open the call's chat panel"; break;
            case CallBtn.Captions: t.GlyphText.Text = "\uE7F0"; t.Label.Text = Loc.T("Captions"); on = _captionsOn; t.Btn.ToolTip = "Windows live captions: everything said in the call, written out"; break;
            case CallBtn.Snip: t.GlyphText.Text = "\uE924"; t.Label.Text = Loc.T("Snip"); on = false; t.Btn.ToolTip = "Capture part of the screen, to paste in the call"; break;
            default: t.GlyphText.Text = "\uE778"; t.Label.Text = _hangHolding ? Loc.T("Hold…") : Loc.T("End"); on = true; t.Btn.ToolTip = "Hold to end the call"; break;
        }
        if (on == t.On && animate) return;
        if (animate && on && !t.On) IconPop(t.GlyphText);
        t.On = on;
        t.Plate.Tag = on ? "on" : null;
        var plateTo = on ? A(tint, 0x30) : look.Plate;
        var edgeTo = on ? A(tint, 0xA0) : look.Edge;
        var iconTo = on ? tint : A(tint, 0x2C);
        var glyphTo = on ? (_cyber ? Colors.Black : Colors.White) : tint;
        var span = TimeSpan.FromMilliseconds(animate ? 220 : 0);
        ((SolidColorBrush)t.Plate.Background).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(plateTo, span));
        ((SolidColorBrush)t.Plate.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(edgeTo, span));
        t.Icon.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(iconTo, span));
        t.Glyph.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(glyphTo, span));
    }

    async void OnCallButton(CallBtn kind)
    {
        switch (kind)
        {
            case CallBtn.Camera: await CallShortcutAsync(CallKeys.Act.Camera, keepFocus: false); break;
            case CallBtn.Share: await CallShortcutAsync(CallKeys.Act.Share, keepFocus: true); break; // the app's picker needs you
            case CallBtn.Hand: await CallShortcutAsync(CallKeys.Act.Hand, keepFocus: false); break;
            case CallBtn.Deafen: await SetDeafenedAsync(!_deafened); break;
            case CallBtn.Presenter: SetPresenter(!_presenter); break;
            case CallBtn.Quiet: await SetQuietOthersAsync(_ducked.Count == 0); break;
            case CallBtn.Messages: await OpenCallChatAsync(); break;
            case CallBtn.Captions: await ToggleCaptionsAsync(); break;
            case CallBtn.Snip: SnipForCall(); break;
        }
        UpdateCallsPage();
    }

    // ───────────────────────────── what's shown ─────────────────────────────

    /// <summary>The page shows the call, or what's useful between calls (rebuilt first if the theme changed).</summary>
    void UpdateCallsPage()
    {
        if (_callsPage == null) return;
        if (_callsBuiltFor != Skin.Key + "|" + _accent) RebuildCallsPage();
        bool live = _call != null;
        _callLive!.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        _callIdle!.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        if (live) UpdateLiveCall(); else UpdateIdleCalls();
        UpdateCallAnimation();
    }

    void UpdateLiveCall()
    {
        if (_call is not { } call) return;
        var look = CallsLook();
        _cIcon!.Background = call.Icon != null ? IconBrush(call.Icon) : Frozen(Color.FromRgb(0x1E, 0x1E, 0x21));
        _cName!.Text = call.Name;
        _cTimer!.Text = FormatTimer(DateTime.UtcNow - call.Started);
        _cTimer.Foreground = Frozen(A(StateColor, 0xE6));
        // the badges: only what's true, popping in as it becomes true
        var want = new List<(string Key, string Text, Color C)>();
        if (CallMuted) want.Add(("muted", Loc.T("MUTED"), MutedColor));
        if (_privacy.CamInUse) want.Add(("cam", Loc.T("CAMERA"), Tint(CallBtn.Camera)));
        if (_presenter) want.Add(("present", Loc.T("PRESENTING"), Tint(CallBtn.Presenter)));
        if (_deafened) want.Add(("deaf", Loc.T("DEAFENED"), Tint(CallBtn.Deafen)));
        if (_talkAll >= 40) want.Add(("talk", string.Format(Loc.T("TALKED {0}%"), (int)Math.Round(100.0 * _talkOn / _talkAll)), look.Accent));
        SyncBadges(want, look);
        // the orb
        bool muted = CallMuted;
        string glyph = muted && !_pttTalking ? "\uF781" : "\uE720";
        if (_cOrbGlyph!.Text != glyph) { bool first = _cOrbGlyph.Text.Length == 0; _cOrbGlyph.Text = glyph; if (!first) OrbGlyphTurn(); }
        _orbMute!.BeginAnimation(OpacityProperty, new DoubleAnimation(muted && !_pttTalking ? 1 : 0, TimeSpan.FromMilliseconds(200)));
        _cOrbLabel!.Text = _orbHint != null ? _orbHint : _mutedWarning ? Loc.T("You're muted!") : _pttTalking ? Loc.T("Talking…") : muted ? Loc.T("Muted · hold to talk") : Loc.T("Mute");
        _cOrbLabel.Foreground = Frozen(_orbHint != null ? _orbHintColor : _mutedWarning ? MutedColor : look.Label);
        foreach (var t in _tiles) PaintTile(t);
        if (_cMic is { IsDragging: false }) { _cMic.Value = _audio.MicVolume; _cMicPct!.Text = $"{(int)Math.Round(_cMic.Value * 100)}"; }
        _cMic!.FillBrush = Frozen(StateColor);
        if (_cMicVoice != null) _cMicVoice.Fill = Frozen(A(StateColor, 0xE0));
        RefreshAudioBits();
    }

    readonly Dictionary<string, Border> _badges = new();

    void SyncBadges(List<(string Key, string Text, Color C)> want, CallLook look)
    {
        if (_cBadges == null) return;
        foreach (var gone in _badges.Keys.Where(k => want.All(w => w.Key != k)).ToList())
        {
            var b = _badges[gone];
            _badges.Remove(gone);
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) => _cBadges.Children.Remove(b);
            b.BeginAnimation(OpacityProperty, fade);
        }
        foreach (var (key, text, c) in want)
        {
            if (_badges.TryGetValue(key, out var have)) { ((TextBlock)have.Child).Text = text; continue; }
            var b = Own(new Border
            {
                CornerRadius = new CornerRadius(look.Neon ? 0 : 7.5), Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 5, 0), Height = 15,
                Background = Frozen(A(c, 0x2E)), BorderBrush = Frozen(A(c, 0x90)), BorderThickness = new Thickness(look.Neon ? 1 : 0.8),
                Child = Own(new TextBlock { Text = text, FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Frozen(Lighten(c, 0.25)), VerticalAlignment = VerticalAlignment.Center, FontFamily = look.Font }),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.6, 0.6),
            });
            _badges[key] = b;
            _cBadges.Children.Add(b);
            var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new SpringEase { Damping = 0.5 } };
            ((ScaleTransform)b.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            ((ScaleTransform)b.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }

    /// <summary>The device and reply counts on the dock.</summary>
    void RefreshAudioBits()
    {
        var output = _audio.Outputs.FirstOrDefault(d => d.IsDefault);
        if (_cAudioName != null) _cAudioName.Text = output != null ? ShortDevice(output.Name) : "";
        if (_cRepliesCount != null) _cRepliesCount.Text = $"{_settings.CallReplies.Count(r => !string.IsNullOrWhiteSpace(r))}";
        if (_iAudioName != null) _iAudioName.Text = output != null ? ShortDevice(output.Name) : "";
        if (_sheet == Sheet.Audio) FillAudioSheet();
    }

    static string Duration(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{t.Seconds} s";
    }

    void OnCallsShown()
    {
        UpdateCallsPage();
        if (_call != null) { _ = FindCallMixAsync(); _ = CheckCaptionsAsync(); PlayCallsArrival(); }
    }

    // ───────────────────────────── the voice, every frame ─────────────────────────────

    bool _callAnimOn;
    double _voiceTarget, _voiceNow;
    TimeSpan _callAnimLast;

    /// <summary>The auras run only while the page is on screen during a call.</summary>
    void UpdateCallAnimation()
    {
        bool want = _call != null && _mode == Mode.Expanded && _page == CallsPage && !Eco;
        if (want == _callAnimOn) return;
        _callAnimOn = want;
        if (want) { _callAnimLast = TimeSpan.Zero; CompositionTarget.Rendering += OnCallFrame; }
        else
        {
            CompositionTarget.Rendering -= OnCallFrame;
            _voiceNow = 0;
            _headAura?.Step(0, 0);
            _orbAura?.Step(0, 0);
        }
    }

    void OnCallFrame(object? sender, EventArgs e)
    {
        if (_call == null || _mode != Mode.Expanded || _page != CallsPage) { UpdateCallAnimation(); return; }
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _callAnimLast == TimeSpan.Zero ? 1 / 60.0 : (now - _callAnimLast).TotalSeconds;
        if (dt <= 0) return;
        _callAnimLast = now;
        dt = Math.Min(dt, 1 / 20.0);
        double k = 1 - Math.Exp(-dt * (_voiceTarget > _voiceNow ? 18 : 6));
        _voiceNow += (_voiceTarget - _voiceNow) * k;
        var c = StateColor;
        if (_headAura != null) { _headAura.Tint = c; _headAura.Step(_voiceNow, dt); }
        if (_orbAura != null) { _orbAura.Tint = c; _orbAura.Step(Math.Max(_voiceNow, CallMuted ? 0.08 : 0), dt); }
        StepWave(dt);
        if (_cMicVoice?.Parent is Grid host) _cMicVoice.Width = Math.Max(0, (host.ActualWidth - 2) * _voiceNow);
    }

    // ───────────────────────────── mute: the call only ─────────────────────────────

    bool _appMuted;

    /// <summary>Is the call muted (its own mic stream, or the whole mic)?</summary>
    bool CallMuted => _appMuted || _audio.MicMuted;

    /// <summary>
    /// Mutes the call itself, not the microphone: the call hears nothing, the notch still hears you (and can tell you
    /// you're talking while muted). If the call's own stream can't be reached, the whole microphone is muted instead.
    /// </summary>
    async Task SetCallMutedAsync(bool mute)
    {
        if (_call is not { } call) { if (_audio.MicMuted != mute) _audio.ToggleMic(); return; }
        if (!mute && _audio.MicMuted) _audio.SetMicMuted(false); // muted elsewhere (the Home toggle, Windows): on again
        bool reached = await AudioService.SetAppMicMutedAsync(call.Key, mute);
        if (reached) _appMuted = mute;
        else { _appMuted = false; _audio.SetMicMuted(mute); }
        _mutedWarning = false;
        UpdateCallUi();
        UpdateCallsPage();
        if (_mode == Mode.Activity) UpdateActivityContent();
        UpdateRim();
    }

    // the orb: a click mutes or unmutes; while muted, holding it talks until you let go
    bool _pttTalking, _pttArmed, _swallowClick;
    DispatcherTimer? _pttTimer;

    void WireMute(Button b)
    {
        b.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (_callOrb != null) Squeeze(_callOrb, 0.92);
            if (!CallMuted) return;
            _pttArmed = true;
            _pttTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            _pttTimer.Tick -= PttStart;
            _pttTimer.Tick += PttStart;
            _pttTimer.Start();
        };
        b.PreviewMouseLeftButtonUp += async (_, _) =>
        {
            if (_callOrb != null) Squeeze(_callOrb, 1);
            _pttTimer?.Stop();
            _pttArmed = false;
            if (!_pttTalking) return;
            _pttTalking = false;
            _swallowClick = true; // it was a hold, not a click: the click that follows is ignored
            await SetCallMutedAsync(true);
        };
        b.Click += async (_, _) =>
        {
            if (_swallowClick) { _swallowClick = false; return; }
            bool mute = !CallMuted;
            await SetCallMutedAsync(mute);
            if (!mute && _deafened) await SetDeafenedAsync(false);
            Ripple();
            MuteWave(mute);
            FlashRim(mute ? MutedColor : LiveColor);
        };
        b.MouseLeave += async (_, _) =>
        {
            _pttTimer?.Stop();
            if (_pttTalking) { _pttTalking = false; await SetCallMutedAsync(true); }
        };
    }

    async void PttStart(object? sender, EventArgs e)
    {
        _pttTimer?.Stop();
        if (!_pttArmed || Mouse.LeftButton != MouseButtonState.Pressed) return;
        _pttTalking = true;
        await SetCallMutedAsync(false);
        _pttTalking = true; // (SetCallMuted repaints; we're still holding)
        UpdateCallsPage();
        FlashRim(LiveColor);
    }

    /// <summary>A ring that leaves the orb when it changes state.</summary>
    void Ripple()
    {
        if (_callOrb == null) return;
        var ring = new Ellipse { Name = "CallGlow", Width = 50, Height = 50, Stroke = Frozen(StateColor), StrokeThickness = 2, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
        _callOrb.Children.Add(ring);
        var grow = new DoubleAnimation(1, 1.6, TimeSpan.FromMilliseconds(520)) { EasingFunction = Out };
        var fade = new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(520));
        fade.Completed += (_, _) => _callOrb.Children.Remove(ring);
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, fade);
    }

    // talking while muted: a nudge (on the page: the orb shakes; elsewhere: a pop-up you can click to unmute)
    bool _mutedWarning;
    double _mutedTalk;
    DateTime _lastMutedWarning;

    void WatchMutedTalk(float peak, double seconds)
    {
        if (!_appMuted || _audio.MicMuted || _pttTalking) { _mutedTalk = 0; return; }
        if (peak > 0.09f) _mutedTalk += seconds; else _mutedTalk = Math.Max(0, _mutedTalk - seconds * 0.5);
        if (_mutedTalk < 0.6 || DateTime.UtcNow - _lastMutedWarning < TimeSpan.FromSeconds(20)) return;
        _mutedTalk = 0;
        _lastMutedWarning = DateTime.UtcNow;
        if (_mode == Mode.Expanded && _page == CallsPage)
        {
            _mutedWarning = true;
            UpdateCallsPage();
            if (_callOrb != null) Shake(_callOrb);
            After(4000, () => { _mutedWarning = false; if (_page == CallsPage) UpdateCallsPage(); });
        }
        else
            ShowOsd("muted-talk", "\uF781", null, Loc.T("You're muted"), Loc.T("Click to unmute"), null, glyphBrush: Red, seconds: 3.2, prio: Prio.High,
                motion: Motion.Shake, onClick: () => _ = SetCallMutedAsync(false));
    }

    static void Shake(FrameworkElement el)
    {
        var tt = new TranslateTransform();
        var group = el.RenderTransform as ScaleTransform;
        el.RenderTransform = new TransformGroup { Children = { group ?? new ScaleTransform(), tt } };
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        foreach (var (t, x) in new[] { (0, 0.0), (60, -5.0), (120, 5.0), (180, -4.0), (240, 3.0), (300, -2.0), (420, 0.0) })
            a.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
        a.Completed += (_, _) => el.RenderTransform = group ?? new ScaleTransform();
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }

    // end: hold to confirm (a mis-click never ends a call)
    bool _hangHolding;
    DispatcherTimer? _hangTimer;

    void WireHangUp(Tile t)
    {
        t.Btn.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _hangHolding = true;
            PaintTile(t, animate: false);
            var s = (ScaleTransform)((TransformGroup)t.Plate.RenderTransform).Children[0];
            var squeeze = new DoubleAnimation(1, 0.88, TimeSpan.FromMilliseconds(700));
            s.BeginAnimation(ScaleTransform.ScaleXProperty, squeeze);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, squeeze);
            ((SolidColorBrush)t.Plate.Background).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(A(Tint(CallBtn.HangUp), 0x80), TimeSpan.FromMilliseconds(700)));
            _hangTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _hangTimer.Tick -= HangUpNow;
            _hangTimer.Tick += HangUpNow;
            _hangTimer.Start();
            t.Btn.CaptureMouse();
        };
        void Cancel()
        {
            _hangTimer?.Stop();
            if (t.Btn.IsMouseCaptured) t.Btn.ReleaseMouseCapture();
            if (!_hangHolding) return;
            _hangHolding = false;
            Squeeze(t.Plate, 1);
            PaintTile(t, animate: false);
        }
        t.Btn.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; Cancel(); };
        t.Btn.LostMouseCapture += (_, _) => Cancel();
    }

    async void HangUpNow(object? sender, EventArgs e)
    {
        _hangTimer?.Stop();
        _hangHolding = false;
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        Mouse.Capture(null);
        await CallShortcutAsync(CallKeys.Act.HangUp, keepFocus: true);
    }

    // ───────────────────────────── the call app's own shortcuts ─────────────────────────────

    /// <summary>Brings the call's window forward, presses its shortcut, and (for quick ones) gives the keyboard back.</summary>
    async Task<bool> CallShortcutAsync(CallKeys.Act act, bool keepFocus)
    {
        if (_call is not { } call) return false;
        string? app = CallKeys.AppOf(call.Key, call.Name);
        if (!_settings.CallShortcuts || CallKeys.For(app, act) is not { } shortcut) return false;
        var before = GetForegroundWindow();
        var hwnd = await Task.Run(() => CallWindow(call, meetOnly: app == "meet"));
        if (hwnd == IntPtr.Zero)
        {
            ShowOsd("call", "\uE717", null, app == "meet" ? Loc.T("Open the Meet tab first") : Loc.T("Can't find the call's window"), call.Name, null, glyphBrush: Red, seconds: 2.2, force: true, motion: Motion.Shake);
            return false;
        }
        FocusWindow(hwnd);
        for (int i = 0; i < 12 && GetForegroundWindow() != hwnd; i++) await Task.Delay(25);
        // a browser: only if the tab in front really is the meeting
        if (app == "meet" && !WindowTitle(hwnd).Contains("Meet", StringComparison.OrdinalIgnoreCase))
        {
            ShowOsd("call", "\uE717", null, Loc.T("Open the Meet tab first"), call.Name, null, glyphBrush: Red, seconds: 2.2, force: true, motion: Motion.Shake);
            return false;
        }
        if (!CallKeys.Press(hwnd, shortcut)) { Tremble(MutedColor); return false; }
        FlashRim(LiveColor);
        if (keepFocus || before == IntPtr.Zero || before == hwnd) return true;
        await Task.Delay(180);
        FocusWindow(before); // back to what you were doing
        return true;
    }

    /// <summary>The call's own window (for a browser call: the window whose tab in front is Meet, when asked).</summary>
    static IntPtr CallWindow(CallState call, bool meetOnly)
    {
        string[] parts = call.Key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        IntPtr found = IntPtr.Zero, any = IntPtr.Zero;
        var pathOf = new Dictionary<uint, string?>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || Interop.Native.GetWindow(h, 4) != IntPtr.Zero) return true;
            string title = WindowTitle(h);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!pathOf.TryGetValue(pid, out var path)) pathOf[pid] = path = ProcessPathById(pid);
            if (path == null) return true;
            bool mine = call.Path != null ? string.Equals(path, call.Path, StringComparison.OrdinalIgnoreCase) : parts.All(p => path.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (!mine) return true;
            if (!meetOnly) { found = h; return false; }
            if (title.Contains("Meet", StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            if (any == IntPtr.Zero) any = h;
            return true;
        }, IntPtr.Zero);
        return found != IntPtr.Zero ? found : meetOnly ? any : IntPtr.Zero;
    }

    /// <summary>
    /// The call's own chat: Discord goes back to the call's DM or channel, Zoom and Meet open their chat panel (their own
    /// shortcuts); for the others the call window is the chat, so it's brought up.
    /// </summary>
    async Task OpenCallChatAsync()
    {
        if (_call == null) return;
        Collapse();
        if (await CallShortcutAsync(CallKeys.Act.Chat, keepFocus: true)) return;
        await BackToCallAsync();
    }

    // ───────────────────────────── deafen, quiet others, presenter ─────────────────────────────

    bool _deafened, _muteBeforeDeafen;
    string? _callMixKey;

    /// <summary>Which of the sound mixer's apps is the call (its sessions are what the call volume and deafen change).</summary>
    async Task FindCallMixAsync()
    {
        if (_call is not { } call) return;
        var apps = await _audio.MixerAppsAsync();
        string[] parts = call.Key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var mine = apps.FirstOrDefault(a => a.Path != null && (call.Path != null ? string.Equals(a.Path, call.Path, StringComparison.OrdinalIgnoreCase)
            : parts.All(p => a.Path.Contains(p, StringComparison.OrdinalIgnoreCase))));
        _callMixKey = mine?.Key;
        if (_cVol != null)
        {
            _cVol.IsInteractive = mine != null;
            _cVol.Opacity = mine != null ? 1 : 0.4;
            if (mine != null && !_cVol.IsDragging) { _cVol.Value = mine.Volume; if (_cVolPct != null) _cVolPct.Text = $"{(int)Math.Round(mine.Volume * 100)}"; }
        }
    }

    bool IsCallMix(MixerApp a) => _callMixKey != null && a.Key == _callMixKey;

    /// <summary>Deafened: the call's sound and your voice both go quiet; again, and both come back as they were.</summary>
    async Task SetDeafenedAsync(bool on)
    {
        if (on == _deafened) return;
        if (_callMixKey == null) await FindCallMixAsync();
        _deafened = on;
        if (on) { _muteBeforeDeafen = CallMuted; await SetCallMutedAsync(true); }
        else await SetCallMutedAsync(_muteBeforeDeafen);
        if (_callMixKey != null) _audio.SetMixerMute(_callMixKey, on);
        FlashRim(on ? Tint(CallBtn.Deafen) : LiveColor);
    }

    readonly Dictionary<string, float> _ducked = new();

    /// <summary>Every other app turned down to a quarter (and back exactly where it was).</summary>
    async Task SetQuietOthersAsync(bool on)
    {
        var apps = await _audio.MixerAppsAsync();
        if (on)
        {
            foreach (var a in apps)
            {
                if (a.IsSystem || IsCallMix(a) || a.Volume < 0.05f || _ducked.ContainsKey(a.Key)) continue;
                _ducked[a.Key] = a.Volume;
                _audio.SetMixerVolume(a.Key, a.Volume * 0.25f);
            }
        }
        else
        {
            foreach (var (key, vol) in _ducked) _audio.SetMixerVolume(key, vol);
            _ducked.Clear();
        }
    }

    bool _presenter, _presenterHeldFocus, _presenterHidIcons;

    /// <summary>
    /// Presenter mode, for sharing your screen: notifications held (Focus), the notch's own pop-ups kept away (what's
    /// on your screen is on theirs), and the desktop icons hidden if you like. Off again: all as it was.
    /// </summary>
    void SetPresenter(bool on)
    {
        if (on == _presenter) return;
        _presenter = on;
        if (on)
        {
            _presenterHeldFocus = !_focus.IsOn;
            if (_presenterHeldFocus) _focus.Set(true);
            _presenterHidIcons = _settings.PresenterHidesIcons && DesktopIcons.Shown == true && DesktopIcons.Toggle();
        }
        else
        {
            if (_presenterHeldFocus && _focus.IsOn) _focus.Set(false);
            if (_presenterHidIcons && DesktopIcons.Shown == false) DesktopIcons.Toggle();
            _presenterHeldFocus = _presenterHidIcons = false;
        }
        After(400, RefreshDesktopBtn);
        FlashRim(on ? Tint(CallBtn.Presenter) : LiveColor);
    }

    // ───────────────────────────── the sheets: audio, quick replies ─────────────────────────────

    enum Sheet { None, Audio, Replies }
    Sheet _sheet;
    Grid? _sheetHost;
    Border? _sheetCard;
    Grid? _sheetBody;

    /// <summary>A sheet that slides up over the page (a click outside it, or its button again, puts it away).</summary>
    Grid BuildSheetHost()
    {
        var look = CallsLook();
        _sheetHost = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(12, 0, 12, 4) };
        var shade = Own(new Border { Background = Frozen(Color.FromArgb(0x01, 0, 0, 0)) });
        shade.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseCallSheet(); };
        _sheetHost.Children.Add(shade);
        _sheetBody = new Grid { Margin = new Thickness(12, 8, 12, 10) };
        Color plate = _cyber ? Color.FromArgb(0xF4, 0x04, 0x0B, 0x10) : Skin.Key switch
        {
            "liquid" => Color.FromArgb(0xF4, 0x10, 0x1A, 0x22),
            "crystal" => Color.FromArgb(0xF4, 0x1A, 0x1F, 0x29),
            "abyss" => Color.FromArgb(0xF6, 0x04, 0x14, 0x19),
            _ => Color.FromArgb(0xF6, 0x16, 0x17, 0x1C),
        };
        _sheetCard = Own(new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(look.Neon ? 0 : 14), Background = Frozen(plate),
            BorderBrush = Frozen(A(look.Accent, look.Neon ? (byte)0x90 : (byte)0x40)), BorderThickness = new Thickness(1), Child = _sheetBody,
            RenderTransform = new TranslateTransform(), Effect = null,
        });
        _sheetCard.MouseLeftButtonUp += (_, e) => e.Handled = true;
        _sheetHost.Children.Add(_sheetCard);
        return _sheetHost;
    }

    void ToggleCallSheet(Sheet which)
    {
        if (_sheet == which) { CloseCallSheet(); return; }
        if (_sheetHost == null || _sheetCard == null || _sheetBody == null) return;
        _sheet = which;
        if (which == Sheet.Audio) FillAudioSheet(); else FillRepliesSheet();
        _sheetHost.Visibility = Visibility.Visible;
        PageBehindSheet(true);
        WatchSheetKeys(true);
        _sheetHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        ((TranslateTransform)_sheetCard.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 } });
    }

    void CloseCallSheet(bool instant = false)
    {
        if (_sheetHost == null || _sheet == Sheet.None && _sheetHost.Visibility != Visibility.Visible) return;
        _sheet = Sheet.None;
        WatchSheetKeys(false);
        PageBehindSheet(false, instant);
        if (instant) { _sheetHost.Visibility = Visibility.Collapsed; return; }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        var host = _sheetHost;
        fade.Completed += (_, _) => { if (_sheet == Sheet.None) host.Visibility = Visibility.Collapsed; };
        host.BeginAnimation(OpacityProperty, fade);
        if (_sheetCard != null) ((TranslateTransform)_sheetCard.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, TimeSpan.FromMilliseconds(130)));
    }

    TextBlock SheetTitle(string text, CallLook look) => Own(new TextBlock { Text = _cyber ? text.ToUpperInvariant() : text, FontSize = 10, Foreground = Frozen(look.Label), FontFamily = look.Font, Margin = new Thickness(2, 0, 0, 6) });

    /// <summary>Your speakers and headsets, your microphones: a click switches (Windows' default for calls too).</summary>
    void FillAudioSheet()
    {
        if (_sheetBody == null) return;
        var look = CallsLook();
        _sheetBody.Children.Clear();
        _sheetBody.ColumnDefinitions.Clear();
        _sheetBody.RowDefinitions.Clear();
        _sheetBody.ColumnDefinitions.Add(new ColumnDefinition());
        _sheetBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        _sheetBody.ColumnDefinitions.Add(new ColumnDefinition());
        _sheetBody.Children.Add(DeviceList(Loc.T("Hear with"), _audio.Outputs, look, output: true));
        var mics = DeviceList(Loc.T("Speak with"), _audio.Inputs, look, output: false);
        Grid.SetColumn(mics, 2);
        _sheetBody.Children.Add(mics);
    }

    StackPanel DeviceList(string title, IReadOnlyList<AudioDevice> devices, CallLook look, bool output)
    {
        var list = new StackPanel();
        list.Children.Add(SheetTitle(title, look));
        int i = 0;
        foreach (var d in devices.Take(4))
        {
            var row = new Grid { Margin = new Thickness(8, 0, 6, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.Children.Add(Own(new TextBlock { Text = output ? (d.IsHeadphones ? "\uE7F6" : "\uE7F5") : "\uE720", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Frozen(d.IsDefault ? look.Accent : look.Label), VerticalAlignment = VerticalAlignment.Center }));
            var name = Own(new TextBlock { Text = ShortDevice(d.Name), FontSize = 11, Foreground = d.IsDefault ? Brushes.White : Frozen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), FontWeight = d.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = look.Font });
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            if (d.IsDefault)
            {
                var check = Own(new TextBlock { Text = "\uE73E", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Foreground = Frozen(look.Accent), VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
            }
            var b = CallPlate(row, look, null, out var plate, 26);
            plate.Margin = new Thickness(0, 0, 0, 4);
            if (d.IsDefault) { ((SolidColorBrush)plate.Background).Color = A(look.Accent, 0x26); ((SolidColorBrush)plate.BorderBrush).Color = A(look.Accent, 0x80); plate.Tag = "on"; }
            b.ToolTip = d.Name;
            string id = d.Id;
            b.Click += (_, _) => { _audio.SetDefaultDevice(id); FlashRim(look.Accent); };
            b.Opacity = 0;
            b.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(25 * i++) });
            list.Children.Add(b);
        }
        if (devices.Count == 0) list.Children.Add(Own(new TextBlock { Text = "—", Foreground = Frozen(look.Label), Margin = new Thickness(4, 2, 0, 0) }));
        return list;
    }

    /// <summary>Your quick replies, all at once in a grid: a click copies one and opens the call's chat (Ctrl+V sends it).</summary>
    void FillRepliesSheet()
    {
        if (_sheetBody == null) return;
        var look = CallsLook();
        _sheetBody.Children.Clear();
        _sheetBody.ColumnDefinitions.Clear();
        _sheetBody.RowDefinitions.Clear();
        _sheetBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _sheetBody.RowDefinitions.Add(new RowDefinition());
        var head = new Grid();
        head.Children.Add(SheetTitle(Loc.T("Quick replies"), look));
        var edit = Own(new TextBlock { Text = Loc.T("Edit"), FontSize = 10, Foreground = Frozen(look.Accent), HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.Hand, FontFamily = look.Font, ToolTip = "Preferences → Calls" });
        edit.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseCallSheet(instant: true); Collapse(); PreferencesWindow.ShowFor(this, 8); };
        head.Children.Add(edit);
        _sheetBody.Children.Add(head);
        var grid = new UniformGrid { Columns = 2 };
        Grid.SetRow(grid, 1);
        int i = 0;
        foreach (var reply in _settings.CallReplies.Where(r => !string.IsNullOrWhiteSpace(r)).Take(6))
        {
            var text = Own(new TextBlock { Text = reply, FontSize = 11, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), FontFamily = look.Font });
            var b = CallPlate(text, look, null, out var plate, 36);
            b.Margin = new Thickness(0, 0, i % 2 == 0 ? 5 : 0, 5);
            b.ToolTip = "Copy it, open the call's chat · Ctrl+V sends";
            string r = reply;
            b.Click += (_, _) => SendQuickReply(r);
            b.Opacity = 0;
            b.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(25 * i++) });
            grid.Children.Add(b);
        }
        if (i == 0) grid.Children.Add(Own(new TextBlock { Text = Loc.T("No replies yet: add some in Preferences → Calls"), Foreground = Frozen(look.Label), FontSize = 11, TextWrapping = TextWrapping.Wrap }));
        _sheetBody.Children.Add(grid);
    }

    /// <summary>A quick reply: copied, and the call's chat brought up so Ctrl+V sends it.</summary>
    async void SendQuickReply(string text)
    {
        _ignoreClipboardUntil = DateTime.UtcNow.AddMilliseconds(600);
        try { Clipboard.SetText(text); } catch { }
        CloseCallSheet(instant: true);
        await OpenCallChatAsync();
        After(250, () => ShowOsd("copied", "\uE8C8", null, Loc.T("Copied · Ctrl+V to send"), text, null, glyphBrush: Blue, seconds: 2.2, prio: Prio.Low));
    }

    /// <summary>"Headphones (WH-1000XM4 Hands-Free)" → "WH-1000XM4".</summary>
    static string ShortDevice(string name)
    {
        int open = name.IndexOf('('), close = name.LastIndexOf(')');
        string s = open >= 0 && close > open ? name[(open + 1)..close] : name;
        s = s.Replace("Hands-Free", "").Replace("Stereo", "").Replace("AG Audio", "").Trim();
        return s.Length > 24 ? s[..23] + "…" : s.Length == 0 ? name : s;
    }

    // ───────────────────────────── talking time ─────────────────────────────

    readonly DispatcherTimer _callTalk = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    int _talkOn, _talkAll, _talkTick;

    /// <summary>
    /// Your voice, a few times a second (twenty while the page shows it): how much of the call you've talked, the
    /// page's auras, and whether you're talking while the call is muted.
    /// </summary>
    void SampleTalk()
    {
        if (_call == null) { _callTalk.Stop(); return; }
        bool watching = _mode == Mode.Expanded && _page == CallsPage;
        var interval = TimeSpan.FromMilliseconds(watching ? 50 : 250);
        if (_callTalk.Interval != interval) _callTalk.Interval = interval;
        if (!_audio.TryGetMicPeak(out float peak)) return;
        WatchMutedTalk(peak, interval.TotalSeconds);
        WatchMic(peak, interval.TotalSeconds);
        bool heard = !CallMuted && _micGate.Voiced;
        if (!watching || ++_talkTick % 5 == 0) // a steady four samples a second for the share
        {
            _talkAll++;
            if (heard) _talkOn++;
        }
        _voiceTarget = CallMuted && !_pttTalking || !_micGate.Voiced ? 0 : Math.Max(0.15, _micGate.Level);
    }

    // ───────────────────────────── when a call starts and ends ─────────────────────────────

    bool _pausedForCall, _focusForCall, _leftCallsPage;

    /// <summary>A call began: the tiles for its app, and what you asked for in Preferences (music, other apps, notifications).</summary>
    void OnCallBegan()
    {
        _talkOn = _talkAll = 0;
        _deafened = _appMuted = _mutedWarning = false;
        ResetMicWatch();
        _callMixKey = null;
        _leftCallsPage = false;
        _callTalk.Start();
        BuildCallButtons();
        _ = FindCallMixAsync();
        if (_settings.CallPauseMusic && _media.IsPlaying) { _ = _media.TogglePlayPauseAsync(); _pausedForCall = true; }
        if (_settings.CallQuietOthers) _ = SetQuietOthersAsync(true);
        if (_settings.CallFocus && !_focus.IsOn) { _focus.Set(true); _focusForCall = true; }
        if (_mode == Mode.Expanded && _settings.CallsOpenOnCall && !PageHidden(CallsPage)) GoToPage(CallsPage);
        UpdateCallsPage();
    }

    /// <summary>The call ended: everything put back as it was (the call's own mic unmuted for next time), and the call kept in the history.</summary>
    void OnCallFinished(CallState call, TimeSpan length)
    {
        _callTalk.Stop();
        _pttTalking = false;
        if (_appMuted) { _appMuted = false; _ = AudioService.SetAppMicMutedAsync(call.Key, false); }
        if (_deafened) { _deafened = false; if (_callMixKey != null) _audio.SetMixerMute(_callMixKey, false); }
        if (_presenter) SetPresenter(false);
        if (_ducked.Count > 0) _ = SetQuietOthersAsync(false);
        if (_pausedForCall && !_media.IsPlaying) _ = _media.TogglePlayPauseAsync();
        if (_focusForCall && _focus.IsOn) _focus.Set(false);
        _pausedForCall = _focusForCall = false;
        CloseCallSheet(instant: true);
        if (_settings.CallHistory && length.TotalSeconds >= 10)
        {
            _history.Insert(0, new CallLog(call.Name, call.Path, call.Key, call.Started, (int)length.TotalSeconds));
            if (_history.Count > 30) _history.RemoveRange(30, _history.Count - 30);
            SaveCallHistory();
        }
        _badges.Clear();
        _cBadges?.Children.Clear();
        UpdateCallsPage();
    }

    /// <summary>Opening the notch during a call lands on this page (unless you've moved away from it during this call).</summary>
    void LandOnCallsPage()
    {
        if (_call != null && _settings.CallsOpenOnCall && !_leftCallsPage && !PageHidden(CallsPage) && _page != CallsPage)
            GoToPage(CallsPage, instant: true);
    }

    // ───────────────────────────── history ─────────────────────────────

    sealed record CallLog(string Name, string? Path, string Key, DateTime Start, int Seconds);
    List<CallLog> _history = new();
    static string HistoryFile => System.IO.Path.Combine(Settings.Folder, "calls.json");

    void LoadCallHistory()
    {
        try { if (File.Exists(HistoryFile)) _history = JsonSerializer.Deserialize<List<CallLog>>(File.ReadAllText(HistoryFile)) ?? new(); }
        catch { _history = new(); }
    }

    void SaveCallHistory()
    {
        var copy = _history.ToList();
        Task.Run(() => { try { Directory.CreateDirectory(Settings.Folder); File.WriteAllText(HistoryFile, JsonSerializer.Serialize(copy)); } catch { } });
    }

    internal void PrefClearCallHistory()
    {
        _history.Clear();
        try { File.Delete(HistoryFile); } catch { }
        UpdateCallsPage();
    }
}
