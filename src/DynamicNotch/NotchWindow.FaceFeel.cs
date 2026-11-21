using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicNotch.Controls;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Pixo's face takes part in what you do, wherever you are, with a small sound to go with it:
///  • the control center: its face sits in the header (in the middle on Home, beside the page's name elsewhere) and
///    watches your pointer move over the panel;
///  • the search: the face is the prompt; it looks down at what you type and follows the caret, tilts its head with
///    a "?" when nothing matches, and brightens when what you typed is a command it understood;
///  • things you do: pick or run something (a nod, a rising pluck), turn something on or off (a wink, a blip up;
///    a hop, a blip down), copy (a wink, a light tick-tick), pin (hearts, a sparkle), delete (it eats it: "nom-nom"),
///    clear a list (it gulps it all, then is proud), something done (proud, a warm chime), something failed (sad, a
///    soft "bu-bum"), a new colour (it wears it), a new theme (a spin), a screenshot (a flash), mute (shh).
/// Closed, the face in the notch feels it instead. Sounds are quiet and short, stay off during calls and
/// presentations, and can be switched off ("Interface sounds"). Buttons in the open notch give a little when pressed.
/// </summary>
public partial class NotchWindow
{
    enum Feel { Pick, On, Off, Copy, Pin, Unpin, Eat, EatAll, Done, Oops, Nothing, Snap, Love, Spin, Hush }

    NotchFace? _searchFace;
    readonly DispatcherTimer _feelLook = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(70) };
    DateTime _searchTypedAt;
    POINT _feelLastCursor;
    string? _searchSawFor, _searchFeltFor;
    bool _searchNothing, _searchUnderstood;

    void WireFeel()
    {
        Sfx.Warm();
        _feelLook.Tick += (_, _) => FeelTick();
        WirePressGive();
    }

    /// <summary>The face that's on screen in the open notch, if any: the search's, else the header's.</summary>
    NotchFace? FeelFace() =>
        _mode != Mode.Expanded ? null
        : _launcherOpen ? (!_chatMode && _searchFace is { IsVisible: true } sf ? sf : null)
        : _page == WorkPage && _workFace is { IsVisible: true } wf ? wf // the Work page: Pixo at the foot of its rail
        : _headFace is { IsVisible: true } hf ? hf : null;

    /// <summary>Just the sound (the face already shows it some other way).</summary>
    void FeelSound(Sfx.Cue cue)
    {
        Sfx.Enabled = _settings.UiSounds;
        Sfx.Hushed = OnCall || _presenter || _inMeeting;
        Sfx.Play(cue);
    }

    /// <summary>Something you did: the face (open notch, or the notch's own face) feels it, and a small sound goes with it.</summary>
    void FeelIt(Feel f, Color? tint = null)
    {
        Sfx.Enabled = _settings.UiSounds;
        Sfx.Hushed = OnCall || _presenter || _inMeeting;
        var (emote, secs, cue) = f switch
        {
            Feel.Pick => (NotchFace.Emote.Nod, .7, Sfx.Cue.Pick),
            Feel.On => (NotchFace.Emote.Wink, .8, Sfx.Cue.On),
            Feel.Off => ((NotchFace.Emote?)null, 0.0, Sfx.Cue.Off),
            Feel.Copy => (NotchFace.Emote.Wink, .8, Sfx.Cue.Copy),
            Feel.Pin => (NotchFace.Emote.Love, 1.2, Sfx.Cue.Pin),
            Feel.Unpin => (NotchFace.Emote.Nod, .6, Sfx.Cue.Unpin),
            Feel.Eat or Feel.EatAll => (NotchFace.Emote.Gulp, 1.1, f == Feel.Eat ? Sfx.Cue.Eat : Sfx.Cue.Clear),
            Feel.Done => (NotchFace.Emote.Proud, 1.4, Sfx.Cue.Done),
            Feel.Oops => (NotchFace.Emote.Sad, 1.5, Sfx.Cue.Oops),
            Feel.Nothing => (NotchFace.Emote.Confused, 1.6, Sfx.Cue.Nothing),
            Feel.Snap => (NotchFace.Emote.Flash, .9, Sfx.Cue.Snap),
            Feel.Love => (NotchFace.Emote.Love, 1.3, Sfx.Cue.Love),
            Feel.Spin => (NotchFace.Emote.Spin, .9, Sfx.Cue.Spin),
            _ => (NotchFace.Emote.Hush, 1.4, Sfx.Cue.Hush),
        };
        Sfx.Play(cue);
        if (!_settings.NotchFace) return;
        var face = FeelFace();
        if (face == null)
        {
            if (emote is { } e) FaceReact(e, secs); // closed: the notch's own face (now, or when it's next back)
            return;
        }
        if (tint is { } c) face.Tint(c, 1.6);
        if (emote is { } em) face.Play(em, secs); else face.Hop(.35);
        // all eaten: a gulp, then it's pleased with itself
        if (f == Feel.EatAll) After(650, () => { if (face.IsVisible) face.Play(NotchFace.Emote.Proud, 1.0); });
    }

    // ───────────────────────────── watching: eyes on the pointer, the caret, the colour ─────────────────────────────

    /// <summary>The open notch's faces come and go with it (started when it opens, it stops itself when it closes).</summary>
    void FeelWake()
    {
        _feelLook.Interval = TimeSpan.FromMilliseconds(Eco ? 200 : 70);
        if (!_feelLook.IsEnabled) _feelLook.Start();
    }

    void FeelTick()
    {
        if (_mode != Mode.Expanded) { _feelLook.Stop(); return; }
        UpdateSearchFace();
        HeadFaceFit();
        if (!GetCursorPos(out var p)) return;
        bool moved = p.X != _feelLastCursor.X || p.Y != _feelLastCursor.Y;
        _feelLastCursor = p;

        if (_launcherOpen && _searchFace is { IsVisible: true } sf && _launchBox != null)
        {
            // typing: it looks down at the words, its eyes riding along with the caret
            bool typing = (DateTime.UtcNow - _searchTypedAt).TotalSeconds < 1.2;
            sf.SetMood(typing ? NotchFace.Mood.Focus : NotchFace.Mood.Idle);
            if (typing)
            {
                try
                {
                    var r = _launchBox.GetRectFromCharacterIndex(_launchBox.CaretIndex);
                    var at = _launchBox.TranslatePoint(new Point(r.X, r.Y + r.Height / 2), sf);
                    if (!double.IsInfinity(at.X)) sf.LookAt(Math.Clamp(at.X / 260, 0, 1), -.35);
                }
                catch { }
            }
            else if (moved) LookFrom(sf, p, 220, 140);
            return;
        }
        if (_page == WorkPage && _workFace is { IsVisible: true } wf) { if (moved) LookFrom(wf, p, 240, 160); return; }
        if (_headFace is { IsVisible: true } hf && moved) LookFrom(hf, p, 230, 150);
    }

    /// <summary>The face looks toward the pointer (screen pixels), within a reach in DIPs.</summary>
    void LookFrom(NotchFace face, POINT p, double reachX, double reachY)
    {
        try
        {
            var c = face.PointToScreen(new Point(face.ActualWidth / 2, face.ActualHeight / 2));
            double dx = (p.X - c.X) / _scale, dy = (p.Y - c.Y) / _scale;
            face.LookAt(dx / reachX, -dy / reachY);
        }
        catch { }
    }

    /// <summary>The header's face: in the middle on Home, beside the page's name elsewhere; away while the live switch is there.</summary>
    void HeadFaceFit()
    {
        if (_headFace == null) return;
        // (the Work page has Pixo at the foot of its rail instead)
        bool want = _settings.NotchFace && _mode == Mode.Expanded && !_launcherOpen && LiveSwitch.Visibility != Visibility.Visible && _page != WorkPage;
        var vis = want ? Visibility.Visible : Visibility.Collapsed;
        if (_headFace.Visibility != vis) _headFace.Visibility = vis;
        double x = PageTitle.ActualWidth > 0 && PageTitle.Visibility == Visibility.Visible ? -(PageTitle.ActualWidth / 2 + 13) : 0;
        if (Math.Abs(_headFaceAt.X - x) > .5) _headFaceAt.X = x;
    }

    // ───────────────────────────── the search's face ─────────────────────────────

    /// <summary>The search's prompt is Pixo's face (the chat keeps its own glyph; listening, the voice's level takes its place).</summary>
    void UpdateSearchFace()
    {
        if (_launchPrompt == null || _launchLineRow is not Grid lineRow || _launchBox == null) return;
        bool show = _settings.NotchFace && _launcherOpen && !_chatMode && !_listening;
        if (show && _searchFace == null)
        {
            _searchFace = new NotchFace(15) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
            Grid.SetColumn(_searchFace, 0);
            lineRow.Children.Add(_searchFace);
            _launchBox.TextChanged += (_, _) => { if (_launcherOpen) _searchTypedAt = DateTime.UtcNow; };
            _searchFace.Visibility = Visibility.Collapsed;
        }
        if (_searchFace == null) return;
        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        if (_searchFace.Visibility != vis)
        {
            _searchFace.Visibility = vis;
            if (show)
            {
                _searchFace.Neon = _cyber; _searchFace.Accent = PalAccent(); _searchFace.Eco = Eco;
                _searchFace.SetOutfit(FaceOutfitNow(), animate: false);
                _searchFace.Play(NotchFace.Emote.Happy, .8); // the search opens: it smiles up at you
            }
        }
        double promptOpacity = show ? 0 : 1;
        if (_launchPrompt.Opacity != promptOpacity) _launchPrompt.Opacity = promptOpacity;
    }

    /// <summary>
    /// The search's results are in: nothing at all for what you typed (a pause, then a "?"), or a command it
    /// understood (it brightens). Once per thing typed, and only once you've stopped typing for a moment.
    /// </summary>
    void SearchFaceSaw(string query, int found, bool understood)
    {
        query = query.Trim();
        if (query.Length < 2) { _searchSawFor = null; return; }
        _searchSawFor = query;
        _searchNothing = found == 0 && !understood;
        _searchUnderstood = understood;
        After(750, () =>
        {
            if (!_launcherOpen || _chatMode || _launchBox == null || _launchBox.Text.Trim() != query || _searchSawFor != query || _searchFeltFor == query) return;
            if ((DateTime.UtcNow - _searchTypedAt).TotalMilliseconds < 650) return;
            _searchFeltFor = query;
            if (_searchNothing) FeelIt(Feel.Nothing);
            else if (_searchUnderstood && _searchFace is { IsVisible: true } sf) sf.Play(NotchFace.Emote.Excited, .9);
        });
    }

    // ───────────────────────────── buttons give a little when pressed ─────────────────────────────

    ButtonBase? _pressedBtn;

    /// <summary>
    /// Every button in the open notch squeezes a hair when pressed and springs back when let go: the panel feels
    /// touchable. Only buttons that aren't already moved or scaled by something else, and they're left exactly as
    /// they were once back.
    /// </summary>
    void WirePressGive()
    {
        Expanded.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (Eco || e.OriginalSource is not DependencyObject d) return;
            var btn = FindButton(d);
            if (btn == null || btn.ActualWidth > 300 || !(btn.RenderTransform == null || ReferenceEquals(btn.RenderTransform, Transform.Identity))) return; // already someone's: left alone
            var st = new ScaleTransform(1, 1);
            btn.RenderTransformOrigin = new Point(0.5, 0.5);
            btn.RenderTransform = st;
            var down = new DoubleAnimation(btn.ActualWidth > 120 ? .98 : .94, TimeSpan.FromMilliseconds(80)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, down);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, down);
            _pressedBtn = btn;
        }), handledEventsToo: true);
        void Release()
        {
            if (_pressedBtn is not { } btn) return;
            _pressedBtn = null;
            if (btn.RenderTransform is not ScaleTransform st) return;
            var up = new DoubleAnimation(1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new SpringEase { Damping = 0.55 } };
            up.Completed += (_, _) => { if (ReferenceEquals(btn.RenderTransform, st) && !ReferenceEquals(_pressedBtn, btn)) btn.RenderTransform = Transform.Identity; };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, up);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, up);
        }
        Expanded.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => Release()), handledEventsToo: true);
        Expanded.MouseLeave += (_, _) => Release();
    }

    static ButtonBase? FindButton(DependencyObject d)
    {
        for (int i = 0; d != null && i < 12; i++)
        {
            if (d is ButtonBase b) return b;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
