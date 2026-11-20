using System.Windows;
using System.Windows.Media;
using DynamicNotch.Controls;

namespace DynamicNotch;

/// <summary>
/// Pixo's face rides along in every pop-up: a small face at the pop-up's right end (the glowing visor in Neon),
/// wearing the pop-up's own colour, feeling what just happened:
///  a message: surprised, then it reads it; copied: a wink; an app opening: hello; a download done: a hop and
///  sparks, failed: sad, a tear; a screenshot: surprised (flash!); a reminder or timer: alert "!"; a call ringing:
///  excited; the call ending: a wave; the charger in or a full battery: hearts; low battery: tired; overload: spiral
///  eyes, red, sweating; a file on the shelf: it eats it; Wi-Fi or Bluetooth back: happy, lost: sad; the mic open by
///  mistake: annoyed; Caps Lock: surprised; the volume: dozing at 0, happy in between, squeezed shut at the top...
/// Anything else reads by its colour: red sad, orange alert, green happy; otherwise it just looks at the words.
/// It costs nothing between pop-ups (it's hidden with them).
/// </summary>
public partial class NotchWindow
{
    NotchFace? _osdFace;
    static readonly Thickness OsdInnerPlain = new(18, 0, 14, 3), OsdInnerWithFace = new(18, 0, 40, 3);

    /// <summary>The face's reaction to a pop-up (sameKind: the same pop-up updating, a volume key held...).</summary>
    void FaceInOsd(OsdRequest r, bool sameKind)
    {
        bool want = _settings.NotchFace && r.Kind is not ("fps" or "pill");
        if (!want)
        {
            if (_osdFace != null) _osdFace.Visibility = Visibility.Collapsed;
            OsdInner.Margin = OsdInnerPlain;
            return;
        }
        if (_osdFace == null)
        {
            _osdFace = new NotchFace(13)
            {
                Sideways = true, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 13, 2), IsHitTestVisible = false,
            };
            Osd.Children.Add(_osdFace);
        }
        var face = _osdFace;
        face.Visibility = Visibility.Visible;
        OsdInner.Margin = OsdInnerWithFace;
        face.Neon = _cyber;
        face.Visor = _cyber;
        face.Eco = Eco;
        face.Accent = PalAccent();
        face.SetOutfit(FaceOutfitNow(), animate: false);

        // it wears the pop-up's colour while it's up
        var colour = AccentFor(r) ?? (r.GlyphBrush as SolidColorBrush)?.Color;
        if (colour is { } c && !r.Level.HasValue) face.Tint(c, r.Seconds + 0.4);

        if (r.Level is { } level)
        {
            // a level being set (volume, brightness): it follows it
            face.SetMood(level <= 0.001 ? NotchFace.Mood.Sleep : NotchFace.Mood.Idle);
            if (level >= 0.995 && r.Kind == "volume") face.Play(NotchFace.Emote.Annoyed, 0.9);       // too loud!
            else if (!sameKind && level > 0.001) face.Play(NotchFace.Emote.Happy, 0.8);
            face.LookAt(level * 2 - 1, r.Kind == "brightness" ? 0.6 : 0); // its eyes ride the level
            return;
        }
        face.SetMood(NotchFace.Mood.Idle);
        if (sameKind) return; // the same pop-up updating: no new reaction each time

        var (feel, secs) = FeelingFor(r);
        if (feel is { } e) face.Play(e, Math.Min(secs, r.Seconds + 0.2));
        // then it reads the words (they're to its left)
        After((int)(Math.Min(secs, 1.2) * 1000), () => { if (ReferenceEquals(_osd, r)) face.LookAt(-0.85, -0.1); });
    }

    (NotchFace.Emote? Feel, double Secs) FeelingFor(OsdRequest r)
    {
        string k = r.Kind;
        string text = ((r.Title ?? "") + " " + (r.Subtitle ?? "")).ToLowerInvariant();
        if (k.StartsWith("toast")) return (NotchFace.Emote.Surprised, 1.0); // a message: "oh!", then it reads it
        return k switch
        {
            "message" => (NotchFace.Emote.Surprised, 1.0),
            "copied" => (NotchFace.Emote.Wink, 0.9),
            "launch" => (NotchFace.Emote.Hello, 1.2),
            "download" or "ai" => (NotchFace.Emote.Done, 1.6),
            "task" => (NotchFace.Emote.Proud, 1.6),
            "download-failed" or "dead-mic" or "smart-net" => (NotchFace.Emote.Sad, 2.2),
            "screenshot" => (NotchFace.Emote.Surprised, 1.0),
            "smart-break" => (NotchFace.Emote.Yawn, 2.0),
            "reminder" or "timer" => (NotchFace.Emote.Alert, 1.6),
            "ring" or "call" => (NotchFace.Emote.Excited, 2.4),
            "call-end" => (NotchFace.Emote.Wave, 1.8),
            "power" or "full-charge" => (NotchFace.Emote.Love, 2.0),
            "battery" or "smart-drain" or "smart-charge" or "bt-battery" => (NotchFace.Emote.Tired, 2.4),
            "strain" => (NotchFace.Emote.Overload, 3.0),
            "shelf" => (NotchFace.Emote.Gulp, 1.4),
            "wifi" or "bt" or "usb" or "output" or "display" => Lost(r) ? (NotchFace.Emote.Sad, 1.8) : (NotchFace.Emote.Happy, 1.2),
            "open-mic" or "muted-talk" => (NotchFace.Emote.Annoyed, 1.2),
            "mic" or "privacy" or "caps" => (NotchFace.Emote.Surprised, 1.0),
            "keyboard" or "smart-downloads" => (NotchFace.Emote.Wink, 0.9),
            "lock" => (text.Contains("unlock") ? NotchFace.Emote.Happy : NotchFace.Emote.Wink, 1.0),
            "focus" or "meeting" => (NotchFace.Emote.Proud, 1.4),
            "welcome" => (NotchFace.Emote.Wave, 2.0),
            "media" => (NotchFace.Emote.Happy, 1.0),
            _ => ByColour(r),
        };
    }

    /// <summary>Something went away (disconnected, lost, unplugged...), judged from the pop-up's words and colour.</summary>
    static bool Lost(OsdRequest r)
    {
        string t = ((r.Title ?? "") + " " + (r.Subtitle ?? "")).ToLowerInvariant();
        if (t.Contains("disconnect") || t.Contains("lost") || t.Contains("removed") || t.Contains("unplugged") || t.Contains("no internet") || t.Contains("offline")) return true;
        return r.GlyphBrush is SolidColorBrush b && b.Color.R > 200 && b.Color.G < 110; // red
    }

    /// <summary>A pop-up it has no special feeling for: red sad, orange / yellow alert, green happy, else just curious.</summary>
    static (NotchFace.Emote?, double) ByColour(OsdRequest r)
    {
        if (r.GlyphBrush is not SolidColorBrush b) return (NotchFace.Emote.Surprised, 0.9);
        var c = b.Color;
        if (c.R > 200 && c.G < 110 && c.B < 130) return (NotchFace.Emote.Sad, 1.8);
        if (c.R > 200 && c.G > 120 && c.B < 90) return (NotchFace.Emote.Alert, 1.4);
        if (c.G > 170 && c.R < 120) return (NotchFace.Emote.Happy, 1.1);
        return (NotchFace.Emote.Surprised, 0.9);
    }
}
