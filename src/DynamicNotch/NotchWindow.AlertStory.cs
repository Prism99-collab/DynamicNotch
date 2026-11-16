using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Notifications told the short way: who and what, nothing else.
///  • the sender on top, the message under it (an app's name repeated as a title is dropped, line breaks folded,
///    long text cut at a word, links shown as their site);
///  • a sign-in code is the whole story: "Code from Google · 482 913", one click copies it;
///  • several from the same app in a row add up ("3 new" on the side) instead of replacing each other;
///  • it stays as long as it takes to read, in the app's own colour (its brand colour when known, else its icon's).
/// </summary>
public partial class NotchWindow
{
    (string App, int Count, DateTime At) _toastBurst;

    // a code is a number right after a word that names it ("Your code: 482913", "OTP is 4829"), or a number that
    // says it is one ("482913 is your Instagram code"). Order numbers, years, prices and times never count.
    static readonly Regex CodeAfterWord = new(@"\b(?:code|otp|passcode|pin|verification|security code|login code|one[- ]time password|code de v[ée]rification|c[óo]digo|كود|رمز)\b\D{0,24}?(?<![\d#$€£])(\d{3}[ -]\d{3}|\d{4,8})(?![\d\-/:.,%])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CodeBeforeWord = new(@"(?<![\d#$€£\-/:.,])(\d{3}[ -]\d{3}|\d{4,8})(?![\d\-/:.,%])\s+(?:is your|is the|est votre|est ton|es tu|هو)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NotACode = new(@"(order|commande|invoice|facture|tracking|suivi|ticket|n°|no\.|#|ref)\W{0,3}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string? FindCode(string text)
    {
        var m = CodeAfterWord.Match(text);
        if (!m.Success) m = CodeBeforeWord.Match(text);
        if (!m.Success) return null;
        var g = m.Groups[1];
        string before = text[Math.Max(0, g.Index - 14)..g.Index];
        if (NotACode.IsMatch(before)) return null;
        string code = g.Value.Replace(" ", "").Replace("-", "");
        if (code.Length == 4 && int.TryParse(code, out int y) && y is >= 1990 and <= 2099) return null; // a year
        return code;
    }

    static readonly Regex Links = new(@"https?://(?:www\.)?([^/\s]+)\S*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Brand colours of common apps (their icons are often too dark or too grey to pick one from).</summary>
    static readonly (string Key, Color Color)[] Brands =
    {
        ("discord", Color.FromRgb(0x58, 0x65, 0xF2)), ("whatsapp", Color.FromRgb(0x25, 0xD3, 0x66)), ("telegram", Color.FromRgb(0x2A, 0xAB, 0xEE)),
        ("teams", Color.FromRgb(0x7B, 0x83, 0xEB)), ("slack", Color.FromRgb(0xE0, 0x1E, 0x5A)), ("outlook", Color.FromRgb(0x1E, 0x90, 0xFF)),
        ("gmail", Color.FromRgb(0xEA, 0x43, 0x35)), ("mail", Color.FromRgb(0x1E, 0x90, 0xFF)), ("spotify", Color.FromRgb(0x1E, 0xD7, 0x60)),
        ("steam", Color.FromRgb(0x66, 0xC0, 0xF4)), ("messenger", Color.FromRgb(0x00, 0x84, 0xFF)), ("instagram", Color.FromRgb(0xE1, 0x30, 0x6C)),
        ("signal", Color.FromRgb(0x3A, 0x76, 0xF0)), ("zoom", Color.FromRgb(0x2D, 0x8C, 0xFF)), ("skype", Color.FromRgb(0x00, 0xAF, 0xF0)),
        ("calendar", Color.FromRgb(0xFF, 0x9F, 0x0A)), ("github", Color.FromRgb(0xA3, 0x71, 0xF7)), ("chrome", Color.FromRgb(0x4C, 0x8B, 0xF5)),
        ("edge", Color.FromRgb(0x2E, 0xB8, 0xE6)), ("brave", Color.FromRgb(0xFB, 0x54, 0x2B)), ("firefox", Color.FromRgb(0xFF, 0x7A, 0x18)),
        ("xbox", Color.FromRgb(0x10, 0xC0, 0x10)), ("epic", Color.FromRgb(0xE6, 0xE6, 0xE6)), ("riot", Color.FromRgb(0xFF, 0x46, 0x55)),
        ("security", Color.FromRgb(0x30, 0xD1, 0x58)), ("defender", Color.FromRgb(0x30, 0xD1, 0x58)),
    };

    static Color? BrandColor(string app)
    {
        foreach (var (key, color) in Brands)
            if (app.Contains(key, StringComparison.OrdinalIgnoreCase)) return color;
        return null;
    }

    /// <summary>A colour you can actually see on black: too dark gets lifted, too grey gets a little more colour.</summary>
    static Color Vivid(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max - min < 0.08) return c; // a grey stays grey (white icons)
        double lift = max < 0.62 ? 0.62 / Math.Max(0.05, max) : 1;
        r = Math.Min(1, r * lift); g = Math.Min(1, g * lift); b = Math.Min(1, b * lift);
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = Links.Replace(s, m => m.Groups[1].Value); // a link is its site
        return Spaces.Replace(s.Replace("\r", " ").Replace("\n", " · "), " ").Trim(' ', '·');
    }

    /// <summary>Cut at a word, near this many characters.</summary>
    static string Short(string s, int max)
    {
        if (s.Length <= max) return s;
        int cut = s.LastIndexOf(' ', max);
        return (cut > max * 0.6 ? s[..cut] : s[..max]).TrimEnd(' ', ',', '·', '-') + "…";
    }

    /// <summary>A notification, told the short way (see the class summary).</summary>
    void ShowToast(ToastInfo t)
    {
        string app = Clean(t.App);
        string title = Clean(t.Title), body = Clean(t.Body);
        // the app's name as the title says nothing: the first line is the story then
        if (title.Length == 0 || title.Equals(app, StringComparison.OrdinalIgnoreCase))
        {
            int dot = body.IndexOf(" · ", StringComparison.Ordinal);
            title = dot > 0 ? body[..dot] : body;
            body = dot > 0 ? body[(dot + 3)..] : app;
        }
        if (title.Length == 0) title = app;

        // a run from the same app adds up
        var now = DateTime.UtcNow;
        bool burst = _toastBurst.App == t.App && (now - _toastBurst.At).TotalSeconds < 20 && _mode == Mode.Osd && _osd?.Kind == "toast:" + t.App;
        _toastBurst = (t.App, burst ? _toastBurst.Count + 1 : 1, now);
        string? right = _toastBurst.Count > 1 ? string.Format(Loc.T("{0} new"), _toastBurst.Count) : null;

        var brand = BrandColor(t.App);
        Color? accent = brand ?? (t.Icon != null ? IconHelper.Accent(t.Icon) is { } ic ? Vivid(ic) : null : null);
        Brush? glyphBrush = accent is { } a ? Frozen(a) : null;
        Action? click = () => OpenApp(t.Aumid);

        // a sign-in code: the code is all you need, a click copies it
        string all = title + " " + body;
        if (FindCode(all) is { } code)
        {
            string shown = code.Length == 6 ? code[..3] + " " + code[3..] : code;
            ShowOsd("toast:" + t.App, t.Icon == null ? "" : null, null, string.Format(Loc.T("Code from {0}"), app.Length > 0 ? app : title),
                Loc.T("Click to copy it"), shown, art: t.Icon, glyphBrush: glyphBrush, seconds: 9, prio: Prio.High,
                onClick: () =>
                {
                    // the notch's own copy: not an alert, and a code never goes into the clipboard history
                    _ignoreClipboardUntil = DateTime.UtcNow.AddSeconds(1.5);
                    try { Clipboard.SetText(code); } catch { return; }
                    ShowOsd("toast:" + t.App, t.Icon == null ? "" : null, null, string.Format(Loc.T("Code from {0}"), app.Length > 0 ? app : title),
                        Loc.T("Copied"), shown, art: t.Icon, glyphBrush: glyphBrush, seconds: 1.6, prio: Prio.High, motion: Motion.None);
                });
            return;
        }

        // the sender stays whole; the message is cut to what fits a glance
        string sub = Short(body.Length > 0 ? body : app, 110);
        int words = (title + " " + sub).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        double seconds = Math.Clamp(2.6 + words * 0.22, 3.2, 7);

        ShowOsd("toast:" + t.App, t.Icon == null ? "" : null, null, Short(title, 60), sub, right,
            art: t.Icon, glyphBrush: glyphBrush, seconds: seconds, prio: Prio.High, onClick: click,
            motion: burst ? Motion.None : SoundsLikeFailure(all) ? Motion.Tremble : Motion.Auto); // "Sign-in failed" shivers instead of pulsing
    }

    /// <summary>A fresh pop-up's words arrive in a quick cascade: the title, then the line under it (the glow stays put).</summary>
    void OsdWordsIn(bool withSub)
    {
        if (Eco) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        void In(FrameworkElement el, int delay)
        {
            if (el.RenderTransform is not TranslateTransform tt || tt.IsFrozen) el.RenderTransform = tt = new TranslateTransform();
            var begin = TimeSpan.FromMilliseconds(delay);
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = begin, EasingFunction = ease });
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = begin, EasingFunction = ease });
        }
        In(OsdTitle, 40);
        if (withSub) In(OsdSub, 95);
        if (OsdRight.Text.Length > 0) In(OsdRight, 140);
    }
}
