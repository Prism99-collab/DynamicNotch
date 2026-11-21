using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// "What's this song?" on Home: the waveform button next to the player listens to what the PC is playing (a video, a
/// stream, a game, a call...) or, when the PC is silent, to the room through the microphone, and names the song
/// (Services/SongId). While it listens the music row makes way for a card: the face listening inside rings that
/// follow the sound. Found: the cover, title and artist, with Shazam, YouTube, Spotify, copy and "ask the AI".
/// The notch may close meanwhile: the answer pops out of it. The card goes with ×, or once seen when the notch closes.
/// </summary>
public partial class NotchWindow
{
    Grid? _song;
    Grid? _songArt;
    Border? _songCover;
    NotchFace? _songFace;
    readonly List<Ellipse> _songRings = new();
    TextBlock? _songTitle, _songSub;
    WrapPanel? _songChips;
    CancellationTokenSource? _songCancel;
    SongMatch? _songFound;
    DateTime _songShownAt;
    bool _songBusy;
    static readonly System.Net.Http.HttpClient SongHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    Color SongAccent => _cyber ? CyCyan : PalAccent();

    void WireSongFind()
    {
        SongIdBtn.Click += (_, _) => { if (_songBusy) { StopSongFind(); HideSongCard(); } else StartSongFind(); };
    }

    void BuildSongCard()
    {
        if (_song != null || MediaRow.Parent is not Grid host) return;
        _song = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.Transparent, Opacity = 0 };
        _song.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        _song.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        _song.ColumnDefinitions.Add(new ColumnDefinition());
        _song.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // the left square: rings + the face while listening, the cover once found
        _songArt = new Grid { Width = 64, Height = 64, ClipToBounds = false };
        for (int i = 0; i < 3; i++)
        {
            var ring = new Ellipse { Width = 64, Height = 64, StrokeThickness = 1.6, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.5, 0.5), IsHitTestVisible = false };
            _songRings.Add(ring);
            _songArt.Children.Add(ring);
        }
        _songFace = new NotchFace(22) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _songArt.Children.Add(_songFace);
        _songCover = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(14), Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1), Cursor = Cursors.Hand, ToolTip = "Open it in Shazam" };
        _songCover.MouseLeftButtonUp += (_, e) => { if (_songFound != null) { e.Handled = true; OpenUri(_songFound.Url); } };
        _songArt.Children.Add(_songCover);
        _song.Children.Add(_songArt);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _songTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White };
        _songSub = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), Foreground = DimBrush };
        _songChips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0), Height = 22, ClipToBounds = true };
        words.Children.Add(_songTitle);
        words.Children.Add(_songSub);
        words.Children.Add(_songChips);
        Grid.SetColumn(words, 2);
        _song.Children.Add(words);

        var close = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Width = 26, Height = 26, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 8, 0, 0),
            Foreground = DimBrush, Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, -4, -6, 0), ToolTip = "Close",
        };
        close.MouseEnter += (_, _) => close.Foreground = Brushes.White;
        close.MouseLeave += (_, _) => close.Foreground = DimBrush;
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; StopSongFind(); HideSongCard(); };
        Grid.SetColumn(close, 3);
        _song.Children.Add(close);

        Grid.SetRow(_song, Grid.GetRow(MediaRow));
        Panel.SetZIndex(_song, 5);
        host.Children.Add(_song);
    }

    // ───────────────────────────── listening ─────────────────────────────

    async void StartSongFind()
    {
        if (_songBusy) return;
        BuildSongCard();
        if (_song == null) return;
        _songBusy = true;
        _songFound = null;
        _songCancel = new CancellationTokenSource();
        var ct = _songCancel.Token;
        ShowSongCard();
        SongListening(SongId.Source.Pc);
        SongIdLit(true);
        FeelSound(Sfx.Cue.On);
        SongId.Source? shown = null;
        try
        {
            var match = await SongId.IdentifyAsync(p =>
            {
                if (ct.IsCancellationRequested) return;
                if (shown != p.Source) { shown = p.Source; SongListening(p.Source); }
                SongLevel(p.Level);
                if (_songSub != null && p.Seconds >= 1)
                    _songSub.Text = (p.Source == SongId.Source.Pc ? Loc.T("Hearing your PC") : Loc.T("Hearing the room (mic)")) + $" · {p.Seconds:0}s";
            }, ct);
            if (ct.IsCancellationRequested) return;
            if (match != null) SongShowFound(match);
            else SongShowProblem(Loc.T("Couldn't catch that song"), shown == SongId.Source.Mic ? Loc.T("Bring it closer to the sound, then try again") : Loc.T("Let it play a little more, then try again"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) SongShowProblem(ex is InvalidOperationException ? Loc.T(ex.Message) : Loc.T("Something went wrong"), Loc.T("Try again in a moment"));
            AppLog.Write("song id: " + ex.Message);
        }
        finally
        {
            if (!ct.IsCancellationRequested) { _songBusy = false; SongRingsRun(false); SongIdLit(false); }
        }
    }

    void StopSongFind()
    {
        _songCancel?.Cancel();
        _songBusy = false;
        SongRingsRun(false);
        SongIdLit(false);
    }

    void SongListening(SongId.Source source)
    {
        if (_songTitle == null || _songSub == null || _songChips == null || _songFace == null || _songCover == null) return;
        PaintSong();
        _songTitle.Text = Loc.T("Listening…");
        _songSub.Text = source == SongId.Source.Pc ? Loc.T("Hearing your PC") : Loc.T("Nothing playing here: listening to the room");
        _songChips.Children.Clear();
        _songChips.Visibility = Visibility.Collapsed;
        _songCover.BeginAnimation(OpacityProperty, null);
        _songCover.Opacity = 0;
        _songCover.Background = null;
        _songFace.Visibility = _settings.NotchFace ? Visibility.Visible : Visibility.Collapsed;
        _songFace.SetMood(NotchFace.Mood.Music);
        _songFace.LookAt(0, 0);
        _songTitle.ToolTip = null; // nothing left over from the last song
        _songSub.ToolTip = null;
        SongRingsRun(true);
    }

    /// <summary>The rings ripple out of the face while it listens; louder sound, brighter rings.</summary>
    void SongRingsRun(bool on)
    {
        for (int i = 0; i < _songRings.Count; i++)
        {
            var ring = _songRings[i];
            var st = (ScaleTransform)ring.RenderTransform;
            if (!on || Eco)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ring.BeginAnimation(OpacityProperty, null);
                ring.Opacity = on ? 0.5 - i * 0.15 : 0;
                st.ScaleX = st.ScaleY = on ? 0.7 + i * 0.15 : 0.5;
                continue;
            }
            var begin = TimeSpan.FromMilliseconds(i * 600);
            var grow = Capped(new DoubleAnimation(0.42, 1.0, TimeSpan.FromMilliseconds(1800)) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, 30);
            var fade = Capped(new DoubleAnimationUsingKeyFrames
            {
                BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(1800),
                KeyFrames = { new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)), new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))), new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800)), new CubicEase { EasingMode = EasingMode.EaseIn }) },
            }, 30);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(OpacityProperty, fade);
        }
    }

    double _songLevelShown = -1;

    void SongLevel(double level)
    {
        // the rings' colour gets stronger with the sound (a few steps, not every frame)
        double step = Math.Round(Math.Clamp(level, 0, 1) * 4) / 4;
        if (Math.Abs(step - _songLevelShown) < 0.01) return;
        _songLevelShown = step;
        var acc = SongAccent;
        var pen = Frozen(A(acc, (byte)(0x60 + step * 0x9F)));
        foreach (var r in _songRings) r.Stroke = pen;
    }

    void PaintSong()
    {
        var acc = SongAccent;
        foreach (var r in _songRings) r.Stroke = Frozen(A(acc, 0x90));
        _songLevelShown = -1;
        if (_songFace != null) { _songFace.Neon = _cyber; _songFace.Accent = acc; _songFace.Eco = Eco; _songFace.SetOutfit(FaceOutfitNow(), animate: false); }
        if (_songTitle != null) _songTitle.FontFamily = _cyber ? CyFont : UiText;
        if (_songSub != null) _songSub.FontFamily = _cyber ? CyFont : UiText;
        if (_songCover != null) _songCover.CornerRadius = new CornerRadius(_cyber ? 0 : 14);
    }

    // ───────────────────────────── the answer ─────────────────────────────

    void SongShowFound(SongMatch m)
    {
        _songFound = m;
        _songShownAt = DateTime.UtcNow;
        SongRingsRun(false);
        if (_songTitle == null || _songSub == null || _songChips == null) return;
        _songTitle.Text = m.Title;
        _songTitle.ToolTip = m.Title;
        _songSub.Text = m.Album is { Length: > 0 } album && !string.Equals(album, m.Title, StringComparison.OrdinalIgnoreCase) ? $"{m.Artist} · {album}" : m.Artist;
        _songSub.ToolTip = _songSub.Text;
        _songChips.Children.Clear();
        _songChips.Visibility = Visibility.Visible;
        string q = Uri.EscapeDataString($"{m.Title} {m.Artist}");
        _songChips.Children.Add(SongChip("", "Shazam", () => OpenUri(m.Url), strong: true));
        _songChips.Children.Add(SongChip("", "YouTube", () => OpenUri("https://www.youtube.com/results?search_query=" + q)));
        _songChips.Children.Add(SongChip("", "Spotify", () => OpenSpotify($"{m.Title} {m.Artist}")));
        _songChips.Children.Add(SongChip("", null, () => { try { Clipboard.SetText($"{m.Title} - {m.Artist}"); } catch { } FeelIt(Feel.Copy); }, tip: Loc.T("Copy the title")));
        if (_settings.AiChat)
            _songChips.Children.Add(SongChip("", null, () => { OpenLauncher(null); if (_launcherOpen) EnterChat($"Tell me about the song \"{m.Title}\" by {m.Artist}: what it's about, when it came out, and similar songs I'd like."); }, tip: Loc.T("Ask the AI about it")));
        SongChipsIn();
        _ = LoadSongCover(m);
        // the face is delighted, then makes way for the cover
        _songFace?.Play(NotchFace.Emote.Excited, 1.0);
        FeelSound(Sfx.Cue.Done);
        _face?.Play(NotchFace.Emote.Excited, 1.2);
        // found while the notch is closed: it says so
        if (_mode != Mode.Expanded)
            ShowOsd("song", "", null, m.Title, m.Artist, null, seconds: 4, prio: Prio.Normal, onClick: () => ExpandTo(0));
        AppLog.Write("song id: found");
    }

    async Task LoadSongCover(SongMatch m)
    {
        if (m.CoverUrl == null || _songCover == null) { SongFaceStays(); return; }
        try
        {
            var bytes = await SongHttp.GetByteArrayAsync(m.CoverUrl);
            if (_songFound != m) return;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 160;
            img.StreamSource = new System.IO.MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            _songCover.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            await Task.Delay(Eco ? 0 : 650); // the face's moment first
            if (_songFound != m) return;
            // the cover takes the face's place: it springs in, the face shrinks away
            if (_songFace != null) _songFace.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Eco ? 1 : 160)));
            var st = (ScaleTransform)_songCover.RenderTransform;
            var pop = new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(Eco ? 1 : 520)) { EasingFunction = new SpringEase { Damping = 0.6 } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            _songCover.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Eco ? 1 : 200)));
        }
        catch { SongFaceStays(); }
    }

    /// <summary>The Spotify app when it's on this PC (it registers "spotify:"), searching for the song; else the web player.</summary>
    void OpenSpotify(string query)
    {
        string q = Uri.EscapeDataString(query);
        bool app = false;
        try
        {
            // the desktop app and the Store app both register the "spotify:" link; Windows can name its handler
            var name = new System.Text.StringBuilder(260);
            uint len = 260;
            app = AssocQueryString(0x1000 /* a protocol */, 4 /* its app's name */, "spotify", null, name, ref len) == 0 && name.Length > 0;
        }
        catch { }
        if (app)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("spotify:search:" + q) { UseShellExecute = true });
                return;
            }
            catch (Exception ex) { AppLog.Write("spotify app: " + ex.Message); }
        }
        OpenUri("https://open.spotify.com/search/" + q);
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int AssocQueryString(uint flags, int str, string assoc, string? extra, System.Text.StringBuilder output, ref uint length);

    void SongFaceStays() => _songFace?.SetMood(NotchFace.Mood.Music);

    void SongShowProblem(string title, string line)
    {
        SongRingsRun(false);
        if (_songTitle == null || _songSub == null || _songChips == null) return;
        _songShownAt = DateTime.UtcNow;
        _songTitle.Text = title;
        _songTitle.ToolTip = null;
        _songSub.Text = line;
        _songSub.ToolTip = null;
        _songChips.Children.Clear();
        _songChips.Visibility = Visibility.Visible;
        _songChips.Children.Add(SongChip("", Loc.T("Try again"), StartSongFind, strong: true));
        SongChipsIn();
        _songFace?.Play(NotchFace.Emote.Confused, 1.2);
        FeelSound(Sfx.Cue.Oops);
    }

    FrameworkElement SongChip(string glyph, string? text, Action act, bool strong = false, string? tip = null)
    {
        var acc = SongAccent;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, text == null ? 0 : 5, 0) });
        if (text != null) row.Children.Add(new TextBlock { Text = text, FontSize = 10.5, FontFamily = _cyber ? CyFont : UiText, VerticalAlignment = VerticalAlignment.Center });
        var idle = Frozen(strong ? A(acc, 0x38) : Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        var hot = Frozen(strong ? A(acc, 0x60) : Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF));
        var chip = new Border
        {
            Child = row, Height = 22, Padding = new Thickness(text == null ? 7 : 8, 0, text == null ? 7 : 9, 0), Margin = new Thickness(0, 0, 5, 0),
            CornerRadius = new CornerRadius(_cyber ? 0 : 11), Background = idle, BorderThickness = new Thickness(1),
            BorderBrush = Frozen(strong ? A(acc, 0x70) : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), Cursor = Cursors.Hand, ToolTip = tip,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        TextElement.SetForeground(chip, strong ? Frozen(Lighten(acc, 0.35)) : Frozen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)));
        chip.MouseEnter += (_, _) => chip.Background = hot;
        chip.MouseLeave += (_, _) => chip.Background = idle;
        chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; FeelSound(Sfx.Cue.Tap); act(); };
        return chip;
    }

    void SongChipsIn()
    {
        if (_songChips == null || Eco) return;
        int i = 0;
        foreach (var c in _songChips.Children.OfType<FrameworkElement>())
        {
            int delay = 60 + i++ * 45;
            var pop = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            pop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.6, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            pop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + 420)), new SpringEase { Damping = 0.6 }));
            var st = (ScaleTransform)c.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + 160))));
            c.BeginAnimation(OpacityProperty, fade);
        }
    }

    // ───────────────────────────── the card in and out ─────────────────────────────

    void ShowSongCard()
    {
        if (_song == null) return;
        if (_songFace != null) { _songFace.BeginAnimation(OpacityProperty, null); _songFace.Opacity = 1; }
        bool wasShown = _song.Visibility == Visibility.Visible;
        _song.Visibility = Visibility.Visible;
        MediaRow.IsHitTestVisible = false;
        var t = TimeSpan.FromMilliseconds(Eco ? 1 : 180);
        MediaRow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, t));
        if (wasShown) return;
        _song.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Eco ? 1 : 220)) { BeginTime = TimeSpan.FromMilliseconds(Eco ? 0 : 60) });
        if (!Eco)
        {
            var shift = new TranslateTransform(0, 8);
            _song.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = new SpringEase { Damping = 0.75 } });
        }
    }

    void HideSongCard()
    {
        if (_song == null || _song.Visibility != Visibility.Visible) return;
        _songFound = null;
        var card = _song;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Eco ? 1 : 150));
        fade.Completed += (_, _) => { if (!_songBusy && _songFound == null) card.Visibility = Visibility.Collapsed; };
        card.BeginAnimation(OpacityProperty, fade);
        MediaRow.IsHitTestVisible = true;
        MediaRow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Eco ? 1 : 220)) { BeginTime = TimeSpan.FromMilliseconds(Eco ? 0 : 80) });
    }

    /// <summary>The notch closed: an answer you've already seen goes away (the player is back next time).</summary>
    void SongCardOnCollapse()
    {
        if (_songBusy || _song?.Visibility != Visibility.Visible) return;
        if ((DateTime.UtcNow - _songShownAt).TotalSeconds >= 4) HideSongCard();
    }

    /// <summary>The waveform button: lit (accent) while it listens.</summary>
    void SongIdLit(bool on)
    {
        var acc = SongAccent;
        SongIdIcon.Stroke = on ? Frozen(acc) : Brushes.White;
        SongIdBtn.ToolTip = on ? Loc.T("Stop listening") : Loc.T("What's this song?");
        if (Eco) return;
        if (on)
        {
            var beat = Capped(new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(520)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() }, 20);
            SongIdIcon.BeginAnimation(OpacityProperty, beat);
        }
        else SongIdIcon.BeginAnimation(OpacityProperty, null);
    }
}
