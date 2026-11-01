using System.Windows;
using System.Windows.Media;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// What the Preferences window may do to the notch: everything goes through the same paths the notch's own
/// switches use, so both always agree (and both refresh when either changes something).
/// Also: which optional pages are shown, and the language of the notch's own labels.
/// </summary>
public partial class NotchWindow
{
    internal Settings PrefSettings => _settings;

    /// <summary>A switch flipped in Preferences: saved, and the notch catches up.</summary>
    internal void PrefChanged()
    {
        _settings.Save();
        OnSettingsChanged();
        SyncSmartPage();
    }

    internal void PrefSetFps(bool on) => SetFpsInGames(on);

    internal void PrefSetBreaks(bool on) { _settings.SmartBreaks = on; _activeSince = null; PrefChanged(); }

    internal void PrefSetMeeting(bool on)
    {
        _settings.MeetingMode = on;
        if (!on && _inMeeting) EndMeetingMode();
        PrefChanged();
    }

    // colours (0 accent, 1 glow, 2 edge, 3 clock)
    internal string? PrefColor(int i) => GetPaint((Paint)i);
    internal Color PrefShownColor(int i) => Hex(GetPaint((Paint)i)) ?? DefaultFor((Paint)i);
    internal static int PrefPaintCount => PaintCount;
    internal static string PrefPaintName(int i) => PaintNames[i];
    internal static string PrefPaintHint(int i) => PaintHints[i];
    internal void PrefSetColor(int i, string? hex, bool save)
    {
        if (i == (int)Paint.Panel && Hex(hex) is { } pc)
        {
            // the body stays dark, whatever was picked: text on it must stay readable
            var (h, s, v) = Controls.ColorWheel.ToHsv(pc);
            if (v > 0.38) hex = ToHex(Controls.ColorWheel.Hsv(h, s, 0.38));
        }
        SetPaint((Paint)i, hex);
        if (save) { _settings.Save(); ApplyCustomColors(); }
        else ApplyCustomColorsSoon(); // dragging: once per frame, not once per mouse move
        if (_studioBuilt) SelectPaint(_paint);
    }
    internal static int PresetCount => Presets.Length;
    internal static string PresetName(int i) => Presets[i].Name;
    internal static Color[]? PresetColors(int i) => Presets[i].A == null ? null : new[] { Hex(Presets[i].A)!.Value, Hex(Presets[i].G)!.Value, Hex(Presets[i].E)!.Value };
    internal void PrefPreset(int i) => ApplyPreset(Presets[i]);
    internal void PrefShuffle() => Shuffle();

    // ───────────── optional pages ─────────────

    internal static readonly int[] OptionalPages = { 1, 2, 3, 4 };

    bool PageHidden(int page) => page is > 0 and < 5 && _settings.HiddenPages.Contains(page);

    /// <summary>Where a page sits among the ones shown (the strip skips hidden pages).</summary>
    int PagePos(int page)
    {
        int pos = 0;
        for (int i = 0; i < page; i++) if (!PageHidden(i)) pos++;
        return pos;
    }

    int PageAtPos(int pos)
    {
        for (int i = 0, p = 0; i < PageNames.Length; i++)
        {
            if (PageHidden(i)) continue;
            if (p == pos) return i;
            p++;
        }
        return PageNames.Length - 1;
    }

    int VisiblePageCount => Enumerable.Range(0, PageNames.Length).Count(i => !PageHidden(i));

    /// <summary>The next shown page in a direction (or the same one at the ends).</summary>
    int StepPage(int dir) => PageAtPos(Math.Clamp(PagePos(_page) + dir, 0, VisiblePageCount - 1));

    internal void PrefSetPage(int page, bool shown)
    {
        _settings.HiddenPages.Remove(page);
        if (!shown) _settings.HiddenPages.Add(page);
        _settings.Save();
        ApplyPageVisibility();
    }

    void ApplyPageVisibility()
    {
        for (int i = 0; i < PagesStrip.Children.Count && i < PageNames.Length; i++)
        {
            var vis = PageHidden(i) ? Visibility.Collapsed : Visibility.Visible;
            PagesStrip.Children[i].Visibility = vis;
            if (i < Dots.Children.Count) Dots.Children[i].Visibility = vis;
        }
        CyberArtPages();
        GoToPage(PageHidden(_page) ? 0 : _page, instant: true);
    }

    // ───────────── language ─────────────

    readonly List<(System.Windows.Controls.TextBlock Text, string En)> _locTexts = new();

    /// <summary>A label of the notch that follows the language.</summary>
    System.Windows.Controls.TextBlock Localized(System.Windows.Controls.TextBlock tb, string en)
    {
        _locTexts.Add((tb, en));
        tb.Text = Loc.T(en);
        return tb;
    }

    internal void PrefRelabel()
    {
        foreach (var (tb, en) in _locTexts) tb.Text = Loc.T(en);
        foreach (var (b, i) in SettingsTabs.Children.OfType<System.Windows.Controls.Button>().Take(SettingsTabNames.Length).Select((b, i) => (b, i)))
            b.Content = Loc.T(SettingsTabNames[i]);
        SettingsHint.Text = Loc.T("Win+Alt+N opens the notch · drag sideways or swipe the touchpad to change page");
        BreakLabel.Text = Loc.T("Break reminder every");
        if (_studioBuilt)
        {
            // the Colors page is rebuilt in the new language the next time it's shown (or now, if it's on screen)
            ColorsPage.Children.Clear();
            _studioBuilt = false;
            if (_mode == Mode.Expanded && _page == 4) OnColorsShown();
        }
        UpdateDots(animate: false);
        AppLog.Write("language: " + Loc.Lang);
    }
}
