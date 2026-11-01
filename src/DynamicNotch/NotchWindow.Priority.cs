using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// You choose what the notch shows when a lot is live at once. On Home, a switcher in the status line lists
/// everything live (call, music, timer, focus, download): pick one and it takes the notch, the rest ride in the
/// side island. With a call and music, the Home card flips between them too: scroll or drag up / down on it.
/// </summary>
public partial class NotchWindow
{
    LiveActivity _pinned = LiveActivity.None;
    bool _cardMedia;          // during a call: Home shows the music card instead of the call card
    string _switchKey = "";
    readonly List<SwitchItem> _switchItems = new();
    Point _cardDown;
    bool _cardDrag;

    void WirePriority()
    {
        _media.PlayersChanged += () => { RenderSwitch(); if (_mode == Mode.Expanded) UpdateCardDots(animate: true); }; // a tab started, stopped, closed, or came to the front
        LiveMenu.MouseLeave += (_, _) => HideLiveMenu();
        foreach (var row in new FrameworkElement[] { MediaRow, CallRow })
        {
            row.RenderTransform = new TranslateTransform();
            row.MouseWheel += OnCardWheel;
            row.PreviewMouseLeftButtonDown += (_, e) => { _cardDown = e.GetPosition(this); _cardDrag = true; };
            row.PreviewMouseMove += OnCardDrag;
            row.PreviewMouseLeftButtonUp += (_, _) => { _cardDrag = false; if (row.IsMouseCaptured) row.ReleaseMouseCapture(); };
        }
    }

    /// <summary>Your pick goes first; a pick that's no longer live is forgotten, so the next call takes the front again.</summary>
    void ApplyPin(List<LiveActivity> list)
    {
        // during a call, the card you flipped to counts as your pick too
        var pin = _pinned != LiveActivity.None ? _pinned : OnCall && _cardMedia ? LiveActivity.Media : LiveActivity.None;
        if (pin == LiveActivity.None) return;
        int i = list.IndexOf(pin);
        if (i < 0) { _pinned = LiveActivity.None; return; }
        if (i == 0) return;
        list.RemoveAt(i);
        list.Insert(0, pin);
    }

    /// <summary>You put the music in front (picked it, or flipped Home's card to it during a call).</summary>
    bool MediaChosen => _pinned == LiveActivity.Media || (OnCall && _cardMedia);

    /// <summary>Home's card shows the call (not the music) right now.</summary>
    bool CallCardShown => OnCall && !(_cardMedia && _media.HasSession);

    /// <summary>Where the card in front sits in the deck: the call, the player in front, or else the first player.</summary>
    int FrontCardIndex(List<SwitchItem> deck)
    {
        if (CallCardShown) return deck.FindIndex(i => i.Kind == LiveActivity.Call);
        int at = deck.FindIndex(i => i.Kind == LiveActivity.Media && ReferenceEquals(i.Player, _media.Current));
        return at >= 0 ? at : deck.FindIndex(i => i.Kind == LiveActivity.Media);
    }

    /// <summary>A call starting or ending resets the choice: the call takes the front, the card shows it.</summary>
    void ResetPriority()
    {
        _pinned = LiveActivity.None;
        _cardMedia = false;
    }

    /// <summary>More than one card to go through on Home: the call and / or several players.</summary>
    bool HasCardDeck => CardDeck().Count >= 2;

    /// <summary>The Home card's deck: the call (when you're on one), then every player that's open, playing or paused.</summary>
    List<SwitchItem> CardDeck() => SwitchItems().Where(i => i.Kind is LiveActivity.Call or LiveActivity.Media).ToList();

    /// <summary>
    /// The next card in the deck (wheel or swipe on the Home card): the call, then each player in turn, round and
    /// round. A player chosen this way stays in front until you pick another or close it.
    /// </summary>
    void StepCard(int dir)
    {
        var deck = CardDeck();
        if (deck.Count < 2) return;
        bool callShown = CallCardShown;
        int at = Math.Max(0, FrontCardIndex(deck));
        var next = deck[(at + dir + deck.Count) % deck.Count];
        bool toMedia = next.Kind == LiveActivity.Media;
        if (toMedia && next.Player != null) _media.Select(next.Player);
        if (toMedia == !callShown)
        {
            // player to player: the same card, its content slides to the next one
            SlideCard(MediaRow, up: dir > 0);
            _pinned = LiveActivity.Media;
            RefreshBase();
            RenderSwitch();
        }
        else FlipCard(toMedia, pin: true, up: dir > 0);
    }

    /// <summary>The card's content leaves upward (or downward) and the next one rises in; the card's own values never change.</summary>
    void SlideCard(FrameworkElement row, bool up)
    {
        if (_devicesOpen || row.RenderTransform is not TranslateTransform t || t.IsFrozen) return;
        double dir = up ? 1 : -1;
        _lastCardFlip = DateTime.UtcNow;
        var move = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        move.KeyFrames.Add(new EasingDoubleKeyFrame(-12 * dir, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        move.KeyFrames.Add(new DiscreteDoubleKeyFrame(14 * dir, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170))));
        move.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(560)), new SpringEase { Damping = 0.62 }));
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380))));
        t.BeginAnimation(TranslateTransform.YProperty, move);
        row.BeginAnimation(OpacityProperty, fade);
        UpdateCardDots(animate: true);
    }

    void Pin(LiveActivity a)
    {
        if (a == LiveActivity.None) return;
        _pinned = a;
        if (a == LiveActivity.Call) FlipCard(false);
        else if (a == LiveActivity.Media) FlipCard(true);
        RefreshBase();
        RenderSwitch();
    }

    // ───────────────────────────── the switcher in the status line ─────────────────────────────

    /// <summary>One chip: an activity, or one particular player (a browser tab, Spotify...) for media.</summary>
    sealed record SwitchItem(LiveActivity Kind, MediaService.Player? Player)
    {
        public string Key => Player != null ? $"m{Player.Id}:{Player.Title}:{Player.Art?.GetHashCode()}" : Kind.ToString();
    }

    const int MaxChips = 5; // what fits between the clock and the battery; the rest go behind "+N"
    const double ChipW = 30, ChipGap = 4;
    bool _liveMenuOpen;
    string _menuKey = "";

    static int Rank(LiveActivity a) => a switch
    {
        LiveActivity.Game => -1, LiveActivity.Call => 0, LiveActivity.Media => 1, LiveActivity.Timer => 2, LiveActivity.Focus => 3, _ => 4,
    };

    /// <summary>Everything you could put in the notch: each live activity, and every player that's playing right now.</summary>
    List<SwitchItem> SwitchItems()
    {
        var items = new List<SwitchItem>();
        foreach (var a in Activities())
            if (a != LiveActivity.Media) items.Add(new SwitchItem(a, null));
        if (_settings.ShowMediaActivity)
        {
            var cur = _media.Current;
            // every player that's open stays here, playing or paused, until it's closed
            foreach (var p in _media.Players) items.Add(new SwitchItem(LiveActivity.Media, p));
            if (_media.HasSession && !items.Any(i => i.Kind == LiveActivity.Media)) items.Add(new SwitchItem(LiveActivity.Media, null));
        }
        // a steady order, so chips don't jump around when you pick one
        return items.OrderBy(i => Rank(i.Kind)).ThenBy(i => i.Player?.Id ?? 0).ToList();
    }

    SwitchItem? FrontItem(List<SwitchItem> items)
    {
        var a = CurrentActivity;
        // the call and the music: the lit chip is always the card Home shows
        if (OnCall && a is LiveActivity.Call or LiveActivity.Media) a = CallCardShown ? LiveActivity.Call : LiveActivity.Media;
        if (a == LiveActivity.Media)
            return items.FirstOrDefault(i => i.Kind == LiveActivity.Media && ReferenceEquals(i.Player, _media.Current))
                ?? items.FirstOrDefault(i => i.Kind == LiveActivity.Media);
        return items.FirstOrDefault(i => i.Kind == a);
    }

    /// <summary>One chip per live thing, centred in the status line on Home; a lit capsule glides to the one in front.</summary>
    void RenderSwitch()
    {
        var items = _mode == Mode.Expanded && _page == 0 ? SwitchItems() : new List<SwitchItem>();
        bool show = items.Count >= 2;
        PageTitle.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show)
        {
            bool wasShown = LiveSwitch.Visibility == Visibility.Visible;
            LiveSwitch.BeginAnimation(OpacityProperty, null);
            LiveSwitch.Visibility = Visibility.Collapsed;
            _switchKey = "";
            if (wasShown) RenderDeviceRings();
            HideLiveMenu();
            return;
        }

        var front = FrontItem(items);
        // too many to fit: the first few, always including the one in front, then "+N"
        var visible = items;
        int maxChips = _devices.Devices.Count > 0 ? 3 : MaxChips; // the battery rings take part of the header
        if (items.Count > maxChips)
        {
            visible = items.Take(maxChips - 1).ToList();
            if (front != null && !visible.Contains(front)) { visible[^1] = front; visible = visible.OrderBy(items.IndexOf).ToList(); }
        }
        int hidden = items.Count - visible.Count;

        bool appearing = LiveSwitch.Visibility != Visibility.Visible;
        string key = string.Join(",", visible.Select(i => i.Key)) + $"|+{hidden}|{_call?.Name}|{_media.Accent}";
        if (key != _switchKey)
        {
            _switchKey = key;
            _switchItems.Clear();
            _switchItems.AddRange(visible);
            LiveChips.Children.Clear();
            foreach (var i in visible) LiveChips.Children.Add(MakeChip(i));
            if (hidden > 0) LiveChips.Children.Add(MakeMoreChip(hidden));
        }
        if (LiveSwitch.Visibility != Visibility.Visible)
        {
            LiveSwitch.Visibility = Visibility.Visible;
            LiveSwitch.Opacity = 0;
            Fade(LiveSwitch, 1, 200);
        }
        if (appearing) { PopChipsIn(); RenderDeviceRings(); } // the switcher takes the middle of the header
        if (front != null) MoveHighlight(front, animate: LiveSwitch.Opacity > 0.05);
        if (_liveMenuOpen)
        {
            string menuKey = string.Join(",", items.Select(i => i.Key)) + "|" + front?.Key;
            if (menuKey != _menuKey) { _menuKey = menuKey; RenderLiveMenu(items, front); }
        }
    }

    FrameworkElement ChipIcon(SwitchItem item, double size)
    {
        var a = item.Kind;
        ImageSource? img = a switch
        {
            LiveActivity.Call => _call?.Icon,
            LiveActivity.Media => item.Player?.Art ?? (item.Player == null || ReferenceEquals(item.Player, _media.Current) ? _media.Art : null),
            _ => null,
        };
        if (img != null)
            return new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(a == LiveActivity.Media ? size / 2 : size / 4),
                Background = IconBrush(img), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        return new TextBlock
        {
            Text = a switch
            {
                LiveActivity.Call => "",
                LiveActivity.Media => "",
                LiveActivity.Timer => "",
                LiveActivity.Focus => "",
                LiveActivity.Game => "\uE7FC",
                _ => "",
            },
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = size * 0.8,
            Foreground = new SolidColorBrush(ItemColor(item)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    FrameworkElement MakeChip(SwitchItem item)
    {
        var chip = new Button
        {
            Style = (Style)FindResource("CircleButton"),
            Width = ChipW, Height = 22, Margin = new Thickness(0, 0, ChipGap, 0),
            Background = Brushes.Transparent,
            Content = ChipIcon(item, 15),
            ToolTip = ItemName(item),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(),
        };
        chip.Click += (_, _) => { PopChip(chip); Pick(item); };
        return chip;
    }

    FrameworkElement MakeMoreChip(int hidden)
    {
        var chip = new Button
        {
            Style = (Style)FindResource("CircleButton"),
            Width = ChipW, Height = 22, Margin = new Thickness(0, 0, ChipGap, 0),
            Background = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
            Content = new TextBlock { Text = $"+{hidden}", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Frozen(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            ToolTip = "Everything that's live",
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(),
        };
        chip.Click += (_, _) => { PopChip(chip); if (_liveMenuOpen) HideLiveMenu(); else ShowLiveMenu(); };
        return chip;
    }

    static void PopChip(FrameworkElement chip)
    {
        var pop = new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new SpringEase { Damping = 0.5 } };
        ((ScaleTransform)chip.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ((ScaleTransform)chip.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>Put this in the notch: a particular player becomes the one the notch and the card follow.</summary>
    void Pick(SwitchItem item)
    {
        if (item.Kind == LiveActivity.Media && item.Player != null) _media.Select(item.Player);
        Pin(item.Kind);
    }

    void MoveHighlight(SwitchItem front, bool animate)
    {
        int i = Math.Max(0, _switchItems.FindIndex(x => x.Key == front.Key));
        double x = i * (ChipW + ChipGap);
        var c = ItemColor(front);
        var fill = Color.FromArgb(0x40, c.R, c.G, c.B);
        var rim = Color.FromArgb(0x90, c.R, c.G, c.B);
        if (LiveHighlight.Background is not SolidColorBrush { IsFrozen: false } bg) LiveHighlight.Background = bg = new SolidColorBrush(fill);
        if (LiveHighlight.BorderBrush is not SolidColorBrush { IsFrozen: false } bb) LiveHighlight.BorderBrush = bb = new SolidColorBrush(rim);
        if (!animate)
        {
            LiveHighlightShift.BeginAnimation(TranslateTransform.XProperty, null);
            LiveHighlightShift.X = x;
            bg.BeginAnimation(SolidColorBrush.ColorProperty, null);
            bb.BeginAnimation(SolidColorBrush.ColorProperty, null);
            bg.Color = fill; bb.Color = rim;
            return;
        }
        if (Math.Abs(LiveHighlightShift.X - x) < 0.5 && bg.Color == fill) return;
        var dur = TimeSpan.FromMilliseconds(520);
        LiveHighlightShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, dur) { EasingFunction = new SpringEase { Damping = 0.7 } });
        // the capsule stretches as it travels, like a drop of light
        var stretch = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        LiveHighlightScale.BeginAnimation(ScaleTransform.ScaleXProperty, stretch);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(fill, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        bb.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(rim, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    Color ItemColor(SwitchItem item)
    {
        if (item.Kind == LiveActivity.Media && item.Player != null && !ReferenceEquals(item.Player, _media.Current))
            return item.Player.Art != null && Services.IconHelper.Accent(item.Player.Art) is { } k ? k : Colors.White;
        return ActivityColor(item.Kind);
    }

    Color ActivityColor(LiveActivity a) => a switch
    {
        LiveActivity.Call => ((SolidColorBrush)CallBrush).Color,
        LiveActivity.Media => _media.Accent,
        LiveActivity.Timer => ((SolidColorBrush)TimerBrush).Color,
        LiveActivity.Focus => ((SolidColorBrush)FocusPurple).Color,
        LiveActivity.Download => ((SolidColorBrush)Blue).Color,
        LiveActivity.Game => ((SolidColorBrush)Green).Color,
        _ => Colors.White,
    };

    string ItemName(SwitchItem item) => item.Kind switch
    {
        LiveActivity.Media when item.Player is { } p => string.IsNullOrEmpty(p.Title) ? p.AppName : $"{p.Title} · {p.AppName}",
        LiveActivity.Media => string.IsNullOrEmpty(_media.Title) ? "Music" : _media.Title,
        LiveActivity.Call => _call?.Name is { } n ? $"Call · {n}" : "Call",
        LiveActivity.Timer => "Timer",
        LiveActivity.Focus => "Focus",
        LiveActivity.Game => _game?.Name ?? "Game",
        _ => "Download",
    };

    string ItemSub(SwitchItem item) => item.Kind switch
    {
        LiveActivity.Media when item.Player is { } p => p.AppName + (string.IsNullOrEmpty(p.Artist) ? "" : " · " + p.Artist),
        LiveActivity.Media => "Playing",
        LiveActivity.Call => _audio.MicMuted ? "Muted" : "On a call" + (_call is { } c ? " · " + FormatTimer(DateTime.UtcNow - c.Started) : ""),
        LiveActivity.Timer => FormatTimer(_timerEnd - DateTime.UtcNow < TimeSpan.Zero ? TimeSpan.Zero : _timerEnd - DateTime.UtcNow) + " left",
        LiveActivity.Focus => "Notifications held",
        LiveActivity.Game => _gameFps is { } f ? $"{f:0} fps" : "Playing",
        _ => "Downloading",
    };

    // ───────────────────────────── "+N": everything live, in a list ─────────────────────────────

    void ShowLiveMenu()
    {
        _liveMenuOpen = true;
        var items = SwitchItems();
        _menuKey = string.Join(",", items.Select(i => i.Key)) + "|" + FrontItem(items)?.Key;
        RenderLiveMenu(items, FrontItem(items));
        LiveMenu.Visibility = Visibility.Visible;
        var ease = new SpringEase { Damping = 0.72 };
        LiveMenuScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        LiveMenuScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        LiveMenu.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    void HideLiveMenu()
    {
        if (!_liveMenuOpen) return;
        _liveMenuOpen = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        fade.Completed += (_, _) => { if (!_liveMenuOpen) LiveMenu.Visibility = Visibility.Collapsed; };
        LiveMenu.BeginAnimation(OpacityProperty, fade);
        LiveMenuScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, TimeSpan.FromMilliseconds(130)));
    }

    void RenderLiveMenu(List<SwitchItem> items, SwitchItem? front)
    {
        if (items.Count < 2) { HideLiveMenu(); return; }
        LiveMenuList.Children.Clear();
        foreach (var item in items)
        {
            bool on = front != null && item.Key == front.Key;
            var c = ItemColor(item);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = ChipIcon(item, 22);
            row.Children.Add(icon);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
            text.Children.Add(new TextBlock { Text = ItemName(item), FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = ItemSub(item), FontSize = 10.5, Foreground = DimBrush, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            // the one in the notch now: a lit dot in its colour
            var dot = new Border { Width = on ? 14 : 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center,
                Background = on ? Frozen(c) : Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 0, 4, 0) };
            Grid.SetColumn(dot, 2);
            row.Children.Add(dot);
            var btn = new Button { Style = (Style)FindResource("RowButton"), Content = row, Height = 40, Margin = new Thickness(0, 1, 0, 1) };
            if (on) btn.Background = Frozen(Color.FromArgb(0x26, c.R, c.G, c.B));
            btn.Click += (_, _) => { Pick(item); HideLiveMenu(); };
            LiveMenuList.Children.Add(btn);
        }
    }

    // ───────────────────────────── the Home card deck (call ⇄ music) ─────────────────────────────

    void OnCardWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasCardDeck) return;
        e.Handled = true;
        if ((DateTime.UtcNow - _lastCardFlip).TotalMilliseconds < 380) return;
        StepCard(e.Delta < 0 ? 1 : -1);
    }

    void OnCardDrag(object sender, MouseEventArgs e)
    {
        if (!_cardDrag || !HasCardDeck || e.LeftButton != MouseButtonState.Pressed || Mouse.Captured is PillSlider) return;
        var p = e.GetPosition(this);
        double dx = p.X - _cardDown.X, dy = p.Y - _cardDown.Y;
        if (Math.Abs(dy) < 14 || Math.Abs(dy) < Math.Abs(dx) * 1.3) return;
        _cardDrag = false;
        _maybeSwipe = false; // it's a card flip, not a page swipe
        ((UIElement)sender).CaptureMouse(); // and not a button click either
        StepCard(dy < 0 ? 1 : -1);
    }

    DateTime _lastCardFlip;
    bool CardFlipping => (DateTime.UtcNow - _lastCardFlip).TotalMilliseconds < 700;

    /// <summary>The card in front slides away and the other rises in on a spring.</summary>
    void FlipCard(bool media, bool pin = false, bool up = true)
    {
        // the card's state first, then everything that reads it (the lit chip, the dots, the notch itself):
        // the other way round the chip lit the card you were leaving
        bool changed = media != _cardMedia;
        _cardMedia = media;
        if (pin) _pinned = media ? LiveActivity.Media : LiveActivity.Call;
        if (pin || changed) RefreshBase(); // which also moves the lit chip
        if (!changed) return;
        _lastCardFlip = DateTime.UtcNow;
        if (!OnCall) return;
        FrameworkElement show = media ? MediaRow : CallRow, hide = media ? CallRow : MediaRow;
        double dir = up ? 1 : -1;
        var outT = (TranslateTransform)hide.RenderTransform;
        var outA = new DoubleAnimation(-18 * dir, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        int flip = ++_flipVersion;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            if (flip != _flipVersion) return; // a newer flip owns both cards now
            hide.Visibility = Visibility.Collapsed;
            SettleCard(hide); // ready to come back clean, whatever brings it back
        };
        outT.BeginAnimation(TranslateTransform.YProperty, outA);
        hide.BeginAnimation(OpacityProperty, fade);

        // the incoming card animates from 0, but its own value stays 1: nothing that later stops
        // an animation on it can leave it invisible
        SettleCard(show);
        show.Visibility = Visibility.Visible;
        var inT = (TranslateTransform)show.RenderTransform;
        var rise = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(22 * dir, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(22 * dir, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
        rise.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(630)), new SpringEase { Damping = 0.62 }));
        var appear = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        appear.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        appear.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
        appear.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(290))));
        inT.BeginAnimation(TranslateTransform.YProperty, rise);
        show.BeginAnimation(OpacityProperty, appear);
        if (_devicesOpen)
        {
            // the sound-device list is open over the cards: the new card waits, faded, until it closes
            show.BeginAnimation(OpacityProperty, null);
            show.Opacity = 0;
        }
        UpdateCardDots(animate: true);
        if (media) RefreshClockAndProgress();
    }

    int _flipVersion;

    /// <summary>Which card Home shows: the call (unless you flipped to the music), otherwise the music.</summary>
    void ApplyCardVisibility()
    {
        bool call = CallCardShown;
        ShowCard(CallRow, call);
        ShowCard(MediaRow, !call);
        UpdateCardDots(animate: false);
    }

    /// <summary>A card that comes back (the call ended, a call started) always comes back whole: fully visible, in place.</summary>
    void ShowCard(FrameworkElement row, bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        if (row.Visibility == v) return;
        if (on) { _flipVersion++; if (!_devicesOpen) SettleCard(row); } // the device list keeps the cards faded until it closes
        row.Visibility = v;
    }

    static void SettleCard(FrameworkElement row)
    {
        row.BeginAnimation(OpacityProperty, null);
        row.Opacity = 1;
        if (row.RenderTransform is TranslateTransform t && !t.IsFrozen)
        {
            t.BeginAnimation(TranslateTransform.YProperty, null);
            t.Y = 0;
        }
    }

    /// <summary>
    /// Little dots beside the card, one per card in the deck (the call, each player): the one in front is long and
    /// wears its colour. Up to 6 dots; with more, they still say "there's more".
    /// </summary>
    void UpdateCardDots(bool animate)
    {
        var deck = CardDeck();
        bool show = deck.Count >= 2;
        CardDots.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        int n = Math.Min(6, deck.Count);
        while (CardDots.Children.Count < n) CardDots.Children.Add(new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 0, 3) });
        while (CardDots.Children.Count > n) CardDots.Children.RemoveAt(CardDots.Children.Count - 1);
        int front = Math.Clamp(Math.Max(0, FrontCardIndex(deck)), 0, n - 1);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < n; i++)
        {
            var dot = (Border)CardDots.Children[i];
            dot.Margin = new Thickness(0, 0, 0, i < n - 1 ? 3 : 0);
            bool on = i == front;
            double h = on ? 14 : 4;
            if (animate) dot.BeginAnimation(HeightProperty, new DoubleAnimation(h, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
            else { dot.BeginAnimation(HeightProperty, null); dot.Height = h; }
            var c = on ? (deck[i].Kind == LiveActivity.Call ? ((SolidColorBrush)CallBrush).Color : _media.Accent) : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
            dot.Background = Frozen(c);
        }
    }
}
