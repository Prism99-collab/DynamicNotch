using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// The cyberpunk theme all the way through the content, not just the shell. Every element of the notch, the ones
/// in the XAML and the ones built later (list rows, chips, notification art, device rings, switches), goes
/// through one pass while the theme is on:
///  • controls wear the cut-corner styles of Cyber.xaml (tiles, switches, buttons, rows, chips, scrollbars);
///  • rounded cards, artwork, chips, menus, the tip and the side islands get 45° cuts (top-left and bottom-right,
///    or both bottom corners for what hangs from the top edge); small round things go square; ears go angular;
///  • colours move into the 2077 palette: blue → cyan, green → teal, orange / gold → yellow, red → neon red,
///    purple / pink → neon magenta, the greys → night blue;
///  • round line caps go flat, sliders go square with a diamond knob, ring gauges become segmented HUD dials.
/// Each change remembers what it replaced, and is undone exactly when the theme is switched off (unless the app
/// has since put something new there itself, which then stays).
/// </summary>
public partial class NotchWindow
{
    static bool _skinOn;
    static NotchWindow? _skinWindow;
    static ResourceDictionary? _cyStyles;
    sealed class Skinned
    {
        public readonly Dictionary<DependencyProperty, (object Before, object Mine)> Props = new();
        public SizeChangedEventHandler? Cut;
    }
    static readonly ConditionalWeakTable<DependencyObject, Skinned> _skinned = new();
    bool _skinQueued;

    static readonly Color CyRed = Color.FromRgb(0xFF, 0x2A, 0x4F), CyMagenta = Color.FromRgb(0xFF, 0x2E, 0x88);
    static readonly Dictionary<int, Color> CyPalette = new()
    {
        [0x30D158] = Color.FromRgb(0x39, 0xC4, 0xB6), [0x34C759] = Color.FromRgb(0x39, 0xC4, 0xB6),
        [0x0A84FF] = Color.FromRgb(0x54, 0xC1, 0xE6), [0x64D2FF] = Color.FromRgb(0x54, 0xC1, 0xE6), [0x8E8CFF] = Color.FromRgb(0x54, 0xC1, 0xE6),
        [0x5E5CE6] = Color.FromRgb(0x39, 0xC4, 0xB6),
        [0xFF9F0A] = Color.FromRgb(0xFE, 0xE8, 0x01), [0xFFD60A] = Color.FromRgb(0xFE, 0xE8, 0x01), [0xFFC40A] = Color.FromRgb(0xFE, 0xE8, 0x01),
        [0xFFE15A] = Color.FromRgb(0xFE, 0xE8, 0x01),
        [0xFF453A] = CyRed, [0xFF3B30] = CyRed,
        [0xBF5AF2] = CyMagenta, [0xFF375F] = CyMagenta, [0xAF52DE] = CyMagenta,
    };

    /// <summary>A colour of the original look, in the 2077 palette (alpha kept). Anything else is left alone.</summary>
    static Color CyMap(Color c)
    {
        if (CyPalette.TryGetValue(c.R << 16 | c.G << 8 | c.B, out var m)) return Color.FromArgb(c.A, m.R, m.G, m.B);
        // the panel greys (cards, tiles, menus) become night blue
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max - min <= 6 && max >= 0x10 && max <= 0x44)
            return Color.FromArgb(c.A, (byte)(max * 0.22), (byte)(max * 0.66), (byte)(max * 0.92));
        return c;
    }

    static Brush? MapBrush(Brush b)
    {
        switch (b)
        {
            case SolidColorBrush s:
                if (!s.IsFrozen && s.HasAnimatedProperties) return null; // something is animating it: leave it to its owner
                var m = CyMap(s.Color);
                return m == s.Color ? null : Frozen(m);
            case GradientBrush g when g.IsFrozen || !g.HasAnimatedProperties:
                var copy = g.CloneCurrentValue();
                bool any = false;
                foreach (var stop in copy.GradientStops)
                {
                    var k = CyMap(stop.Color);
                    if (k != stop.Color) { stop.Color = k; any = true; }
                }
                if (!any) return null;
                copy.Freeze();
                return copy;
        }
        return null;
    }

    void WireCyberSkin()
    {
        _skinWindow = this;
        try { _cyStyles = new ResourceDictionary { Source = new Uri("/DynamicNotch;component/Cyber.xaml", UriKind.Relative) }; }
        catch (Exception ex) { Services.AppLog.Write("cyber styles: " + ex.Message); }
        // every element as it appears (rows, chips, art built later) gets the skin while it's on
        EventManager.RegisterClassHandler(typeof(FrameworkElement), LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (_skinOn && s is FrameworkElement fe && _skinWindow is { } w && w.SkinnableHere(fe)) w.SkinOne(fe, true);
        }));
    }

    /// <summary>The theme was switched: the whole notch goes through the pass (on), or back (off).</summary>
    void SkinAll(bool on)
    {
        _skinOn = on;
        PillSlider.Square = on;
        RingGauge.Segmented = on;
        Walk(Root, on);
    }

    /// <summary>
    /// The app keeps setting things as it goes (a new pop-up's colour, a tile turning on, the battery's fill):
    /// after any of that, one quiet pass at background priority catches up. Never in a game.
    /// </summary>
    void CyberSkinSoon()
    {
        if (!_cyber || _skinQueued || _mode == Mode.Game) return;
        _skinQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _skinQueued = false;
            if (_cyber && _mode != Mode.Game) Walk(Root, true, full: false);
        });
    }

    /// <param name="full">false for the quiet catch-up passes: what's collapsed is skipped (it's caught when it shows, by its Loaded or the next pass)</param>
    void Walk(DependencyObject node, bool on, bool full = true)
    {
        if (!full && node is UIElement { Visibility: not Visibility.Visible }) return;
        if (node is FrameworkElement fe)
        {
            if (ReferenceEquals(fe, _hud)) return; // the HUD is drawn for the theme already
            if (fe is PillSlider ps) { ps.Restyle(); return; } // draws itself
            if (fe is RingGauge rg) rg.InvalidateVisual();
            SkinOne(fe, on);
        }
        if (node is TextBlock tb) Retype(tb, _cyberFonts);
        int n = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(node, i), on, full);
    }

    /// <summary>Not part of the content: the shell (skinned by the theme itself), glows, and the slider's own parts.</summary>
    bool SkinnableHere(FrameworkElement fe)
    {
        if (ReferenceEquals(fe, Pill) || ReferenceEquals(fe, Rim) || ReferenceEquals(fe, EarL) || ReferenceEquals(fe, EarR) || ReferenceEquals(fe, _hud)) return false;
        var parent = VisualTreeHelper.GetParent(fe);
        if (parent is PillSlider || ReferenceEquals(parent, _hud)) return false;
        if (parent is Canvas && ReferenceEquals(VisualTreeHelper.GetParent(parent), _hud)) return false;
        return Window.GetWindow(fe) == this;
    }

    void SkinOne(FrameworkElement fe, bool on)
    {
        if (!on) { Unskin(fe); return; }
        if (fe is PillSlider slider) { slider.Restyle(); return; } // draws itself (one created after the switch, too)
        // the side pills get their own plates (NotchWindow.CyberPills): never clipped, their ears and neck stay
        if (ReferenceEquals(fe, SecondBubble) || ReferenceEquals(fe, PrivacyBubble) || ReferenceEquals(fe, SecondRim) || ReferenceEquals(fe, PrivacyRim)) return;
        if (ReferenceEquals(fe, Pill) || ReferenceEquals(fe, Rim) || ReferenceEquals(fe, EarL) || ReferenceEquals(fe, EarR)) return;
        string name = fe.Name ?? "";
        bool glow = name.Contains("Aura") || name.Contains("Glow") || name.Contains("Shine");

        // controls: the cut-corner styles
        if (_cyStyles != null)
        {
            if (fe is ScrollBar sb && sb.Orientation == Orientation.Vertical) PutStyle(fe, "CyScrollBar");
            else if (fe is ButtonBase && fe.TemplatedParent == null)
            {
                var local = fe.ReadLocalValue(StyleProperty);
                string? key = local is Style st ? CyKeyFor(st) : fe is QuickToggle && local == DependencyProperty.UnsetValue ? "CyQuickToggle" : null;
                if (key != null) PutStyle(fe, key);
            }
        }
        if (ReferenceEquals(fe, LockBtn) && _cyStyles?["CyLockTemplate"] is ControlTemplate lockT && !ReferenceEquals(fe.ReadLocalValue(Control.TemplateProperty), lockT))
            Put(fe, Control.TemplateProperty, lockT);
        // parts of a control's template follow its template (and its triggers); only the control itself is touched
        if (fe.TemplatedParent != null || glow) return;

        switch (fe)
        {
            case Border b:
                Recolor(b, Border.BackgroundProperty);
                Recolor(b, Border.BorderBrushProperty);
                CutCorners(b);
                break;
            case Panel p:
                Recolor(p, Panel.BackgroundProperty);
                break;
            case TextBlock t:
                Recolor(t, TextBlock.ForegroundProperty);
                break;
            case Shape sh:
                Recolor(sh, Shape.FillProperty);
                Recolor(sh, Shape.StrokeProperty);
                if (sh.StrokeStartLineCap == PenLineCap.Round) Put(sh, Shape.StrokeStartLineCapProperty, PenLineCap.Flat);
                if (sh.StrokeEndLineCap == PenLineCap.Round) Put(sh, Shape.StrokeEndLineCapProperty, PenLineCap.Flat);
                if (sh is Rectangle r && (r.RadiusX > 0 || r.RadiusY > 0)) { Put(r, Rectangle.RadiusXProperty, 0.0); Put(r, Rectangle.RadiusYProperty, 0.0); }
                if (sh is Path ear) AngularEar(ear);
                break;
        }
        if (fe is Control c)
        {
            Recolor(c, Control.ForegroundProperty);
            Recolor(c, Control.BackgroundProperty);
        }
        if (fe is QuickToggle q)
        {
            Recolor(q, QuickToggle.OnBrushProperty);
            Recolor(q, QuickToggle.TintBrushProperty);
        }
        if (fe is RingGauge g) Recolor(g, RingGauge.RingBrushProperty);
    }

    static string? CyKeyFor(Style st)
    {
        foreach (var key in new[] { "CircleButton", "RowButton", "Switch", "ActionTile", "Capsule" })
            if (ReferenceEquals(Application.Current.TryFindResource(key), st)) return "Cy" + key;
        return null;
    }

    static void PutStyle(FrameworkElement fe, string key)
    {
        if (_cyStyles?[key] is Style cy && !ReferenceEquals(fe.ReadLocalValue(StyleProperty), cy)) Put(fe, StyleProperty, cy);
    }

    static void Recolor(DependencyObject o, DependencyProperty dp)
    {
        if (o.ReadLocalValue(dp) is not Brush b) return;
        if (_skinned.TryGetValue(o, out var s) && s.Props.TryGetValue(dp, out var rec) && ReferenceEquals(rec.Mine, b)) return; // ours already
        if (MapBrush(b) is { } m) Put(o, dp, m);
    }

    /// <summary>Sets a value, remembering what was there (the first time, or when the app has replaced ours since).</summary>
    static void Put(DependencyObject o, DependencyProperty dp, object value)
    {
        var s = _skinned.GetOrCreateValue(o);
        object cur = o.ReadLocalValue(dp);
        if (s.Props.TryGetValue(dp, out var rec) && Equals(cur, rec.Mine)) { s.Props[dp] = (rec.Before, value); }
        else s.Props[dp] = (cur, value);
        o.SetValue(dp, value);
    }

    static void Unskin(DependencyObject o)
    {
        if (!_skinned.TryGetValue(o, out var s)) return;
        if (s.Cut != null && o is FrameworkElement fe) fe.SizeChanged -= s.Cut;
        foreach (var (dp, (before, mine)) in s.Props)
        {
            if (!Equals(o.ReadLocalValue(dp), mine)) continue; // the app has put something new there since: it stays
            if (before == DependencyProperty.UnsetValue) o.ClearValue(dp); else o.SetValue(dp, before);
        }
        _skinned.Remove(o);
    }

    /// <summary>Rounded → cut. Small radii simply go square; bigger ones get 45° cuts that follow the size.</summary>
    static void CutCorners(Border b)
    {
        var cr = b.CornerRadius;
        double max = Math.Max(Math.Max(cr.TopLeft, cr.TopRight), Math.Max(cr.BottomLeft, cr.BottomRight));
        if (max <= 0) return;
        _skinned.TryGetValue(b, out var s);
        bool ours = s != null && s.Props.TryGetValue(Border.CornerRadiusProperty, out var rec) && Equals(b.ReadLocalValue(Border.CornerRadiusProperty), rec.Mine);
        if (ours) return;
        if (max < 4) { Put(b, Border.CornerRadiusProperty, new CornerRadius(0)); return; }
        var clip = b.ReadLocalValue(ClipProperty);
        bool ourClip = s != null && s.Props.TryGetValue(ClipProperty, out var cRec) && Equals(clip, cRec.Mine);
        if (clip is Geometry && !ourClip) return; // clipped by the app for its own reasons: just leave it round

        // what hangs from the top edge (islands, necks) cuts both bottom corners; cards cut top-left and bottom-right;
        // small round things (avatars, art) cut all four
        bool hanging = cr.TopLeft == 0 && cr.TopRight == 0;
        bool round = cr.TopLeft > 0 && cr.TopRight > 0 && cr.BottomLeft > 0 && cr.BottomRight > 0 && max >= 6 && (b.Width is > 0 and <= 28 || b.Height is > 0 and <= 28);
        double tl = hanging ? 0 : cr.TopLeft, tr = round ? cr.TopRight : 0, br = cr.BottomRight, bl = hanging || round ? cr.BottomLeft : 0;
        Put(b, Border.CornerRadiusProperty, new CornerRadius(0));
        s = _skinned.GetOrCreateValue(b);
        if (s.Cut != null) b.SizeChanged -= s.Cut;
        s.Cut = (_, _) => ApplyCut(b, tl, tr, br, bl);
        b.SizeChanged += s.Cut;
        ApplyCut(b, tl, tr, br, bl);
    }

    static void ApplyCut(Border b, double tl, double tr, double br, double bl)
    {
        if (!_skinOn) return;
        double w = b.ActualWidth, h = b.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double lim = Math.Min(w, h) / 2.6;
        double C(double r) => r <= 0 ? 0 : Math.Min(Math.Max(3, r * 0.75), Math.Min(10, lim));
        double a = C(tl), bb = C(tr), c = C(br), d = C(bl);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, a), true, true);
            ctx.LineTo(new Point(a, 0), false, false);
            ctx.LineTo(new Point(w - bb, 0), false, false);
            ctx.LineTo(new Point(w, bb), false, false);
            ctx.LineTo(new Point(w, h - c), false, false);
            ctx.LineTo(new Point(w - c, h), false, false);
            ctx.LineTo(new Point(d, h), false, false);
            ctx.LineTo(new Point(0, h - d), false, false);
        }
        g.Freeze();
        Put(b, ClipProperty, g);
    }

    static readonly Regex Numbers = new(@"-?\d+(\.\d+)?", RegexOptions.Compiled);

    /// <summary>The islands' little rounded ears become 45° flares, like the notch's.</summary>
    static void AngularEar(Path p)
    {
        if (p.Width is not (> 0 and <= 12) || p.Height is not (> 0 and <= 12) || p.Data == null) return;
        if (_skinned.TryGetValue(p, out var s) && s.Props.ContainsKey(Path.DataProperty) && Equals(p.ReadLocalValue(Path.DataProperty), s.Props[Path.DataProperty].Mine)) return;
        string text = p.Data.ToString();
        if (!text.Contains('A')) return; // no arc: not an ear
        var n = Numbers.Matches(text).Select(m => m.Value).Take(6).ToArray();
        if (n.Length < 6) return;
        var tri = Geometry.Parse($"M{n[0]},{n[1]} L{n[2]},{n[3]} L{n[4]},{n[5]} Z");
        tri.Freeze();
        Put(p, Path.DataProperty, tri);
    }

    /// <summary>A page changed in the cyber skin: a light glitch, the title decodes.</summary>
    void CyberPage()
    {
        if (!_cyber) return;
        CyberGlitch(strong: false, light: true);
        CyberDecode(PageTitle);
        CyberTag();
        CyberSkinSoon();
    }
}
