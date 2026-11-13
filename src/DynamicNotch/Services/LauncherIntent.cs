using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// What the notch's search understands when you tell it to do something in your own words: "remind me to call mum at
/// 6", "volume 30 and dark mode", "turn off bluetooth", "focus for 45 minutes", "close spotify", "youtube lofi beats",
/// "shut down in an hour". Also the facts it knows without asking anyone: "time in tokyo", "days until new year",
/// "what day is 25 dec", "my ip", "storage", "password", "flip a coin". All read on the PC, nothing sent anywhere.
/// The parsing lives here (plain, testable); the notch does the doing.
/// </summary>
public static class LauncherIntent
{
    public enum Do
    {
        Volume, Brightness, Media, NowPlaying, Wifi, Bluetooth, Airplane, DarkMode, Focus, Timer, Remind, Reminders, CancelReminders,
        Open, Close, Web, Power, Sleep, Lock, Screenshot, RecycleBin, DesktopIcons, ClipText,
    }

    /// <summary>
    /// One thing to do. <c>Op</c> says exactly what ("set", "up", "mute", "next", "restart", "upper"...), <c>Num</c> a level
    /// or a step, <c>On</c> on/off (null flips it), <c>At</c> when, <c>For</c> how long, <c>Text</c> a name or a message.
    /// </summary>
    public sealed record Intent(Do Kind, string Op = "", string? Text = null, double Num = double.NaN, bool? On = null,
        DateTime? At = null, TimeSpan? For = null, string? Url = null, string? Label = null);

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ───────────────────────────── reading what was typed ─────────────────────────────

    /// <summary>Lower case, one space, no "please", "hey", "can you", "?" around the actual words.</summary>
    public static string Clean(string query)
    {
        string s = Regex.Replace(query.Trim().ToLowerInvariant().Replace('’', '\'').Replace('‘', '\''), @"\s+", " ");
        s = Regex.Replace(s, @"[?!.]+$", "").Trim();
        string prev;
        do
        {
            prev = s;
            s = Regex.Replace(s, @"^(?:hey notch|hey|hi|ok|okay|yo|please|pls|plz|notch|can you|could you|would you|will you|can u|could u|i want to|i want you to|i'd like to|i need to|i need you to|let's|lets|go ahead and|just|quickly|do(?! not\b))\b[\s,]*", "");
            s = Regex.Replace(s, @"[\s,]+(?:please|pls|plz|for me|right now|thanks|thank you|thx)$", "");
        } while (s != prev && s.Length > 0);
        return s.Trim();
    }

    /// <summary>What to do: one step, a few chained with "and" / "then" / commas, or nothing (it's a search).</summary>
    public static IReadOnlyList<Intent> Parse(string query, DateTime now)
    {
        string s = Clean(query);
        if (s.Length < 2) return Array.Empty<Intent>();
        // a chain only when every piece is a task on its own ("open spotify and discord" is one task with two names)
        var parts = Regex.Split(s, @"\s*,\s*(?:and\s+)?(?:then\s+)?|\s+and\s+then\s+|\s+then\s+|\s+and\s+|\s*;\s*|\s*\+\s+");
        if (parts.Length is >= 2 and <= 6)
        {
            var chain = new List<Intent>();
            foreach (var p in parts)
            {
                if (One(p.Trim(), now) is { } it && !(it.Kind == Do.Remind && it.At == null)) chain.Add(it);
                else { chain.Clear(); break; }
            }
            if (chain.Count >= 2) return chain;
        }
        return One(s, now) is { } single ? new[] { single } : Array.Empty<Intent>();
    }

    static Match M(string s, string pattern) => Regex.Match(s, pattern, RegexOptions.CultureInvariant);

    static Intent? One(string s, DateTime now)
    {
        if (s.Length < 2) return null;
        return Ask(s) ?? Reminder(s, now) ?? Timer(s, now) ?? Focus(s) ?? Volume(s) ?? Brightness(s) ?? Radios(s) ?? DarkMode(s)
            ?? Power(s, now) ?? Little(s) ?? Clip(s) ?? Media(s) ?? Web(s) ?? Close(s) ?? Open(s);
    }

    // ───────────────────────────── reminders and timers ─────────────────────────────

    static Intent? Reminder(string s, DateTime now)
    {
        if (M(s, @"^(?:show |list |see |open |check )?(?:me )?(?:my |all |the )?reminders$").Success) return new Intent(Do.Reminders);
        if (M(s, @"^(?:cancel|clear|delete|remove|stop|forget) (?:all |my |the |every )*reminders?$").Success) return new Intent(Do.CancelReminders);

        // an alarm is a reminder that says "Alarm"
        var a = M(s, @"^(?:set |make |add )?(?:an |the |my )?alarm (?:for |at )?(.+)$");
        if (a.Success)
        {
            string t = a.Groups[1].Value;
            var at = When(t, now) ?? When("at " + t, now);
            return at == null ? null : new Intent(Do.Remind, Text: "Alarm", At: at);
        }

        var m = M(s, @"^(?:remind me|set (?:a |an )?reminder|add (?:a )?reminder|make (?:a )?reminder|new reminder|create (?:a )?reminder|reminder|remember)(?: to| that| about| of| for)?(?:\s+|$)(.*)$");
        if (!m.Success)
        {
            // time first: "in 20 min remind me to stretch", "tomorrow at 9, remind me to call the bank"
            var f = M(s, @"^(.+?),? remind me(?: to| that| about| of)? (.+)$");
            if (!f.Success || When(f.Groups[1].Value, now) is not { } at0) return null;
            return Remind(f.Groups[2].Value, at0);
        }
        string rest = m.Groups[1].Value.Trim();
        if (rest.Length == 0) return new Intent(Do.Remind, Text: "");
        var w = rest.Split(' ');
        // "in 20 min to stretch"
        for (int j = 1; j < w.Length - 1; j++)
            if (w[j] is "to" or "that" or "about" or "of" && When(string.Join(' ', w[..j]), now) is { } at1)
                return Remind(string.Join(' ', w[(j + 1)..]), at1);
        // "to stretch in 20 min": the longest ending that reads as a time ("put the clothes in the dryer in 30 min")
        for (int i = 1; i < w.Length; i++)
            if (When(string.Join(' ', w[i..]), now) is { } at2)
                return Remind(string.Join(' ', w[..i]), at2);
        return new Intent(Do.Remind, Text: Tidy(rest)); // no time yet: the notch asks when
    }

    static Intent Remind(string text, DateTime at) => new(Do.Remind, Text: Tidy(text), At: at);

    static string Tidy(string text)
    {
        string t = Regex.Replace(text.Trim(), @"^(?:to|that|about) ", "").Trim(' ', ',');
        return t.Length == 0 ? t : char.ToUpper(t[0], Inv) + t[1..];
    }

    static Intent? Timer(string s, DateTime now)
    {
        if (s is "pomodoro" or "start a pomodoro" or "start pomodoro") return new Intent(Do.Timer, For: TimeSpan.FromMinutes(25), Label: "Pomodoro");
        var m = M(s, @"^(?:set |start |make |put on |run |add )?(?:a |an |the |my )?(?:timer|countdown)(?: for| of| on)?\s+(.+)$");
        if (!m.Success) m = M(s, @"^(?:set |start )?(?:a |an )?(.+?) (?:timer|countdown)$");
        if (!m.Success) m = M(s, @"^t\s?(\d{1,3})$");
        if (!m.Success) return null;
        return Dur(m.Groups[1].Value, "min") is { } d && d <= TimeSpan.FromHours(24) ? new Intent(Do.Timer, For: d) : null;
    }

    static Intent? Focus(string s)
    {
        const string F = @"(?:focus|focus mode|focus time|do not disturb|dnd|deep work)";
        var m = M(s, @"^(?:start |turn on |begin |enable |go into |enter |set )?" + F + @"(?: mode)?(?: on)?(?: for)? (.+)$");
        if (m.Success && Dur(m.Groups[1].Value, "min") is { } d) return new Intent(Do.Focus, On: true, For: d);
        if (M(s, @"^(?:start|turn on|begin|enable|go into|enter|activate) " + F + @"(?: mode)?$").Success || M(s, "^" + F + "(?: mode)? on$").Success)
            return new Intent(Do.Focus, On: true);
        if (s is "do not disturb" or "dnd" or "focus mode" or "deep work") return new Intent(Do.Focus);
        if (M(s, @"^(?:stop|end|turn off|disable|exit|leave|quit|cancel|deactivate) (?:the )?" + F + @"(?: mode)?$").Success || M(s, "^" + F + "(?: mode)? off$").Success)
            return new Intent(Do.Focus, On: false);
        return null;
    }

    // ───────────────────────────── levels: volume and brightness ─────────────────────────────

    static Intent? Volume(string s)
    {
        if (M(s, @"^(?:mute|silence)(?: (?:the |my )?(?:sound|volume|audio|pc|computer|laptop|speakers|everything|it))?$").Success || M(s, @"^(?:sound|volume|audio) off$").Success)
            return new Intent(Do.Volume, "mute");
        if (M(s, @"^unmute(?: (?:the |my )?(?:sound|volume|audio|pc|computer|laptop|speakers|everything|it))?$").Success || M(s, @"^(?:sound|volume|audio) (?:back )?on$").Success)
            return new Intent(Do.Volume, "unmute");
        if (s is "louder" or "a bit louder" or "turn it up" or "make it louder") return new Intent(Do.Volume, "up", Num: 10);
        if (s is "quieter" or "softer" or "a bit quieter" or "turn it down" or "make it quieter") return new Intent(Do.Volume, "down", Num: 10);
        if (M(s, @"^(?:turn|put|crank|bring) (?:it|the music|music) (up|down)$") is { Success: true } t) return new Intent(Do.Volume, t.Groups[1].Value, Num: 10);
        return Level(s, Do.Volume, @"(?:volume|sound|audio)");
    }

    static Intent? Brightness(string s)
    {
        if (s is "brighter" or "a bit brighter" or "make it brighter" or "make the screen brighter") return new Intent(Do.Brightness, "up", Num: 15);
        if (M(s, @"^(?:dimmer|darker|a bit dimmer|a bit darker|dim(?: (?:the |my )?(?:screen|display))?|make (?:it|the screen) (?:darker|dimmer))$").Success)
            return new Intent(Do.Brightness, "down", Num: 15);
        return Level(s, Do.Brightness, @"(?:brightness|screen brightness|display brightness|screen|display)");
    }

    /// <summary>"volume 30", "set the volume to 30%", "volume up by 20", "lower the brightness", "brightness max".</summary>
    static Intent? Level(string s, Do kind, string noun)
    {
        const string N = @"(\d{1,3}|max|maximum|full|min|minimum|half|zero|none)";
        var m = M(s, @"^(?:set |change |put |turn |make |adjust )?(?:the |my )?" + noun + @"(?: level)?(?: to| at| on|:)? " + N + @"\s*(?:%|percent)?$");
        if (!m.Success) m = M(s, "^" + N + @"\s*(?:%|percent)? " + noun + "$");
        if (m.Success) return Value(m.Groups[1].Value) is { } v ? new Intent(kind, "set", Num: v) : null;
        m = M(s, @"^(?:turn |put |bring |crank |move |push )?(?:the |my )?" + noun + @"(?: all the way)? (up|down)(?: (?:by )?(?:(a bit|a little|a lot|a tad)|(\d{1,3})\s*(?:%|percent)?))?$");
        if (!m.Success) m = M(s, @"^(?:turn|put|bring|crank) (up|down) (?:the |my )?" + noun + @"(?: (?:by )?(?:(a bit|a little|a lot|a tad)|(\d{1,3})\s*(?:%|percent)?))?$");
        if (m.Success) return new Intent(kind, m.Groups[1].Value, Num: Step(m.Groups[2].Value, m.Groups[3].Value));
        m = M(s, @"^(increase|raise|boost|up|lower|decrease|reduce|drop|cut) (?:the |my )?" + noun + @"(?: (?:by )?(?:(a bit|a little|a lot|a tad)|(\d{1,3})\s*(?:%|percent)?))?$");
        if (m.Success) return new Intent(kind, m.Groups[1].Value is "increase" or "raise" or "boost" or "up" ? "up" : "down", Num: Step(m.Groups[2].Value, m.Groups[3].Value));
        if (M(s, "^(?:max|maximum|full) " + noun + "$").Success) return new Intent(kind, "set", Num: 100);
        return null;
    }

    static double? Value(string v) => v switch
    {
        "max" or "maximum" or "full" => 100, "min" or "minimum" or "zero" or "none" => 0, "half" => 50,
        _ => int.TryParse(v, out int n) && n <= 100 ? n : null,
    };

    static double Step(string words, string number) =>
        number.Length > 0 && int.TryParse(number, out int n) ? Math.Clamp(n, 1, 100) : words switch { "a lot" => 25, "a tad" => 5, _ => 10 };

    // ───────────────────────────── switches ─────────────────────────────

    static Intent? Radios(string s)
    {
        foreach (var (rx, kind) in new[] { (@"wi-?fi|wireless|wlan", Do.Wifi), (@"bluetooth|bt", Do.Bluetooth), (@"airplane|aeroplane|flight|plane", Do.Airplane) })
        {
            string x = @"(?:the |my )?(?:" + rx + @")(?: mode| connection| radio)?";
            var m = M(s, @"^(?:turn|switch|put|set) (on|off) " + x + "$");
            if (!m.Success) m = M(s, @"^(?:turn |switch |set |put )?" + x + @" (on|off)$");
            if (m.Success) return new Intent(kind, On: m.Groups[1].Value == "on");
            m = M(s, @"^(enable|disable|activate|deactivate|start|stop|kill|connect|disconnect|cut|no) " + x + "$");
            if (m.Success) return new Intent(kind, On: m.Groups[1].Value is "enable" or "activate" or "start" or "connect");
            if (kind == Do.Airplane && M(s, @"^(?:go into |enter |use |go )?(?:airplane|aeroplane|flight) mode$").Success) return new Intent(kind, On: null);
        }
        return null;
    }

    static Intent? DarkMode(string s)
    {
        var m = M(s, @"^(?:turn |switch |set |go |change |put |use )?(?:on |to |into |the )?(dark|light) (?:mode|theme)(?: (on|off))?$");
        if (m.Success) return new Intent(Do.DarkMode, On: (m.Groups[1].Value != "light") == (m.Groups[2].Value != "off"));
        m = M(s, @"^(?:turn|switch) (on|off) (?:the )?(dark|light) (?:mode|theme)$");
        if (m.Success) return new Intent(Do.DarkMode, On: (m.Groups[2].Value != "light") == (m.Groups[1].Value == "on"));
        m = M(s, @"^(enable|disable|activate|deactivate) (?:the )?(dark|light) (?:mode|theme)$");
        if (m.Success) return new Intent(Do.DarkMode, On: (m.Groups[2].Value != "light") == (m.Groups[1].Value is "enable" or "activate"));
        if (s is "go dark" or "toggle dark mode" or "switch theme" or "switch dark mode") return new Intent(Do.DarkMode, On: s == "go dark" ? true : null);
        return null;
    }

    // ───────────────────────────── the PC itself ─────────────────────────────

    static Intent? Power(string s, DateTime now)
    {
        const string Pc = @"(?: (?:the |my |this )?(?:pc|computer|laptop|machine))?";
        if (M(s, @"^(?:cancel|abort|stop|don't|dont) (?:the )?(?:shut ?down|restart|reboot|power off)$").Success) return new Intent(Do.Power, "cancel");
        if (M(s, @"^(?:sign|log) (?:out|off)(?: of windows)?$").Success) return new Intent(Do.Power, "signout");
        var m = M(s, @"^(?:shut ?down|power off|turn off (?:the |my |this )?(?:pc|computer|laptop|machine)|shut (?:the |my |this )?(?:pc|computer|laptop) down)" + Pc + @"(?: (.+))?$");
        string op = "shutdown";
        if (!m.Success) { m = M(s, @"^(?:restart|reboot)" + Pc + @"(?: (.+))?$"); op = "restart"; }
        if (!m.Success) return null;
        TimeSpan delay = TimeSpan.FromSeconds(30); // a moment to say "cancel shutdown"
        string when = m.Groups[1].Value.Trim();
        if (when is "now" or "immediately" or "right away") delay = TimeSpan.FromSeconds(5);
        else if (when.Length > 0)
        {
            if (When(when, now) is not { } at || at - now < TimeSpan.FromSeconds(10)) return null;
            delay = at - now;
            if (delay > TimeSpan.FromDays(3)) return null;
        }
        return new Intent(Do.Power, op, For: delay);
    }

    /// <summary>Sleep, lock, a screenshot, the Recycle Bin, the desktop's icons: said in a sentence.</summary>
    static Intent? Little(string s)
    {
        if (M(s, @"^(?:go to sleep|put (?:the |my |this )?(?:pc|computer|laptop) to sleep|sleep (?:the |my |this )?(?:pc|computer|laptop)|sleep mode)$").Success) return new Intent(Do.Sleep);
        if (M(s, @"^lock (?:the |my |this )?(?:pc|computer|laptop|screen|machine|windows)$").Success) return new Intent(Do.Lock);
        if (M(s, @"^(?:take |make |grab |do )(?:a )?(?:screenshot|screen ?shot|snip|screen capture|screen grab)$|^(?:snip|capture|screenshot) (?:the |my )?screen$").Success) return new Intent(Do.Screenshot);
        if (M(s, @"^(?:empty|clear|clean|clean out|clean up) (?:the |my )?(?:recycle bin|recycling bin|trash|bin|rubbish|trash can)$").Success) return new Intent(Do.RecycleBin);
        var m = M(s, @"^(hide|show|unhide|bring back|remove) (?:the |my |all |all the )*(?:desktop icons|icons on (?:the |my )?desktop|icons)$");
        if (m.Success) return new Intent(Do.DesktopIcons, On: m.Groups[1].Value is "show" or "unhide" or "bring back");
        return null;
    }

    static Intent? Clip(string s)
    {
        const string C = @"(?:the |my )?(?:clipboard|copied text|what i copied|copied)";
        var m = M(s, @"^(?:make |convert |turn |change |put )?" + C + @"(?: (?:to|into|in))? (uppercase|upper case|caps|all caps|lowercase|lower case|title case|sentence case|plain text)$");
        if (!m.Success) m = M(s, @"^(uppercase|upper case|caps|lowercase|lower case|title case|sentence case) " + C + "$");
        if (m.Success)
        {
            string w = m.Groups[1].Value;
            return new Intent(Do.ClipText, w.StartsWith("upper") || w.Contains("caps") ? "upper" : w.StartsWith("lower") ? "lower" : w.StartsWith("title") ? "title" : w.StartsWith("sentence") ? "sentence" : "plain");
        }
        if (M(s, @"^(?:count (?:the )?(?:words|characters|chars|letters)|word count|character count|how many (?:words|characters|chars))(?: (?:in|on) " + C + ")?$").Success) return new Intent(Do.ClipText, "count");
        if (M(s, @"^(?:paste as plain text|strip (?:the )?formatting|remove (?:the )?formatting|clean (?:up )?" + C + "|plain text)$").Success) return new Intent(Do.ClipText, "plain");
        if (M(s, @"^(?:clear|empty|wipe) " + C + "$").Success) return new Intent(Do.ClipText, "clear");
        return null;
    }

    // ───────────────────────────── music ─────────────────────────────

    static Intent? Media(string s)
    {
        const string It = @"(?: (?:the |my |this )?(?:music|song|track|media|video|podcast|it|playback|audio))?";
        if (M(s, @"^(?:what(?:'s| is) (?:this |the )?(?:song|playing|track)|what song is (?:this|playing)|now playing|current song|what am i listening to|which song is this)$").Success) return new Intent(Do.NowPlaying);
        if (M(s, "^(?:play|resume|unpause|continue|start)" + It + "$").Success && s != "start") return new Intent(Do.Media, "play");
        if (M(s, "^(?:pause|stop|hold)" + It + "$").Success) return new Intent(Do.Media, "pause");
        if (M(s, @"^(?:play ?pause|play/pause|toggle (?:the )?(?:music|playback))$").Success) return new Intent(Do.Media, "toggle");
        if (M(s, @"^(?:next|skip)(?: (?:the |this )?(?:song|track|one|video|it))?$|^(?:play |go to )?(?:the )?next (?:song|track|one|video)$").Success) return new Intent(Do.Media, "next");
        if (M(s, @"^(?:previous|prev|last|go back)(?: (?:song|track|one|video))?$|^(?:play |go to )?(?:the )?(?:previous|last) (?:song|track|one|video)$").Success && s != "last" && s != "go back") return new Intent(Do.Media, "prev");
        var m = M(s, @"^(?:play|put on|listen to) (.+?) on (youtube|yt|spotify|soundcloud|deezer|apple music)$");
        if (m.Success) return Site(m.Groups[2].Value == "yt" ? "youtube" : m.Groups[2].Value, m.Groups[1].Value, play: true);
        m = M(s, @"^(?:play|put on|listen to) (.{2,})$");
        if (m.Success) return Site("youtube", m.Groups[1].Value, play: true);
        return null;
    }

    // ───────────────────────────── the web ─────────────────────────────

    static readonly Dictionary<string, (string Name, string Url)> Sites = new()
    {
        ["google"] = ("Google", "https://www.google.com/search?q="),
        ["youtube"] = ("YouTube", "https://www.youtube.com/results?search_query="),
        ["wikipedia"] = ("Wikipedia", "https://en.wikipedia.org/w/index.php?search="),
        ["amazon"] = ("Amazon", "https://www.amazon.com/s?k="),
        ["github"] = ("GitHub", "https://github.com/search?q="),
        ["reddit"] = ("Reddit", "https://www.reddit.com/search/?q="),
        ["maps"] = ("Maps", "https://www.google.com/maps/search/?api=1&query="),
        ["images"] = ("Images", "https://www.google.com/search?tbm=isch&q="),
        ["news"] = ("News", "https://news.google.com/search?q="),
        ["spotify"] = ("Spotify", "https://open.spotify.com/search/"),
        ["soundcloud"] = ("SoundCloud", "https://soundcloud.com/search?q="),
        ["deezer"] = ("Deezer", "https://www.deezer.com/search/"),
        ["apple music"] = ("Apple Music", "https://music.apple.com/search?term="),
        ["stack overflow"] = ("Stack Overflow", "https://stackoverflow.com/search?q="),
        ["bing"] = ("Bing", "https://www.bing.com/search?q="),
        ["duckduckgo"] = ("DuckDuckGo", "https://duckduckgo.com/?q="),
        ["x"] = ("X", "https://x.com/search?q="),
        ["twitter"] = ("X", "https://x.com/search?q="),
        ["pinterest"] = ("Pinterest", "https://www.pinterest.com/search/pins/?q="),
        ["imdb"] = ("IMDb", "https://www.imdb.com/find/?q="),
        ["translate"] = ("Translate", "https://translate.google.com/?sl=auto&tl=en&text="),
        // assistants that take the question in the address: the page opens with it asked
        ["chatgpt"] = ("ChatGPT", "https://chatgpt.com/?q="),
        ["claude"] = ("Claude", "https://claude.ai/new?q="),
        ["perplexity"] = ("Perplexity", "https://www.perplexity.ai/search?q="),
        ["copilot"] = ("Copilot", "https://copilot.microsoft.com/?q="),
    };

    static readonly HashSet<string> Assistants = new() { "chatgpt", "claude", "perplexity", "copilot" };

    static string SiteKey(string w) => w switch
    {
        "wiki" => "wikipedia", "yt" => "youtube", "google maps" => "maps", "map" => "maps", "image" => "images", "pictures" => "images", "photos" => "images",
        "stackoverflow" => "stack overflow", "ddg" => "duckduckgo", "gpt" or "chat gpt" or "chat" or "openai" => "chatgpt", _ => w,
    };

    static Intent? Site(string site, string what, bool play = false)
    {
        string key = SiteKey(site);
        if (!Sites.TryGetValue(key, out var info) || what.Trim().Length == 0) return null;
        string q = what.Trim();
        string label = Assistants.Contains(key) ? $"Ask {info.Name}: “{q}”" : play ? $"Play “{q}” on {info.Name}" : key == "maps" ? $"“{q}” on the map" : $"Search {info.Name} for “{q}”";
        return new Intent(Do.Web, Text: q, Url: info.Url + Uri.EscapeDataString(q), Label: label);
    }

    static readonly Dictionary<string, string> Languages = new()
    {
        ["english"] = "en", ["french"] = "fr", ["arabic"] = "ar", ["spanish"] = "es", ["german"] = "de", ["italian"] = "it", ["portuguese"] = "pt",
        ["dutch"] = "nl", ["russian"] = "ru", ["chinese"] = "zh-CN", ["japanese"] = "ja", ["korean"] = "ko", ["turkish"] = "tr", ["hindi"] = "hi",
        ["polish"] = "pl", ["swedish"] = "sv", ["greek"] = "el", ["hebrew"] = "iw", ["persian"] = "fa", ["urdu"] = "ur", ["indonesian"] = "id",
        ["ukrainian"] = "uk", ["romanian"] = "ro", ["vietnamese"] = "vi", ["thai"] = "th",
    };

    /// <summary>"ask chatgpt how to…", "chatgpt: write a poem", "gpt what is…", "explain recursion with claude".</summary>
    public static Intent? Ask(string s)
    {
        const string Who = @"(chatgpt|chat gpt|gpt|openai|claude|perplexity|copilot)";
        var m = M(s, @"^(?:ask|ask to|hey|use|open) " + Who + @"[:,]?(?: to| about| for| if| whether| how| what| why)? ?(.+)$");
        if (!m.Success) m = M(s, "^" + Who + @"[:,]? (.+)$");
        if (m.Success && m.Groups[2].Value.Trim().Length > 0)
        {
            // keep the question word the "ask … how/what/why" form swallowed
            string q = m.Groups[2].Value;
            var lead = M(s, "^(?:ask|ask to|hey|use|open) " + Who + @"[:,]? (how|what|why) ");
            if (lead.Success) q = lead.Groups[2].Value + " " + q;
            return Site(m.Groups[1].Value, q);
        }
        m = M(s, @"^(.+?),? (?:on|in|with|using|via) " + Who + "$");
        return m.Success ? Site(m.Groups[2].Value, m.Groups[1].Value) : null;
    }

    static Intent? Web(string s)
    {
        if (Ask(s) is { } ask) return ask;
        const string Names = @"youtube|yt|google|wikipedia|wiki|amazon|github|reddit|maps|google maps|map|images|image|pictures|photos|news|spotify|soundcloud|stack ?overflow|bing|duckduckgo|ddg|twitter|x|pinterest|imdb";
        var m = M(s, @"^(?:search|look up|lookup|find|look for|google)(?: for)? (.+?) (?:on|in|at|with) (" + Names + ")$");
        if (m.Success) return Site(m.Groups[2].Value, m.Groups[1].Value);
        m = M(s, @"^(?:search )?(youtube|yt|wiki|wikipedia|amazon|reddit|maps|images|imdb|pinterest)(?: for| search)? (.+)$");
        if (m.Success) return Site(m.Groups[1].Value, m.Groups[2].Value);
        m = M(s, @"^(?:search|look up|lookup|google|search the web for|search online for|web search)(?: for)? (.+)$");
        if (m.Success) return Site("google", m.Groups[1].Value);
        m = M(s, @"^(?:directions|route|navigate|drive|how do i get|how to get|take me) to (.+)$");
        if (m.Success) return new Intent(Do.Web, Text: m.Groups[1].Value, Url: "https://www.google.com/maps/dir/?api=1&destination=" + Uri.EscapeDataString(m.Groups[1].Value), Label: $"Directions to “{m.Groups[1].Value}”");
        m = M(s, @"^(?:where is|where's|show me|map of|find) (.+?) on (?:the )?map$|^where is (.+)$");
        if (m.Success) return Site("maps", m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        m = M(s, @"^(?:define|definition of|meaning of|what does (.+) mean|synonyms? (?:of|for)) ?(.*)$");
        if (m.Success)
        {
            string word = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
            bool syn = s.StartsWith("synonym");
            if (word.Trim().Length == 0) return null;
            return new Intent(Do.Web, Text: word, Url: "https://www.google.com/search?q=" + Uri.EscapeDataString((syn ? "synonyms " : "define ") + word),
                Label: syn ? $"Synonyms of “{word}”" : $"Define “{word}”");
        }
        m = M(s, @"^translate (.+?)(?: (?:to|into|in) ([a-z]+))?$");
        if (m.Success)
        {
            string text = m.Groups[1].Value, lang = m.Groups[2].Success ? m.Groups[2].Value : "english";
            string code = Languages.TryGetValue(lang, out var c) ? c : "en";
            string name = char.ToUpper(lang[0], Inv) + lang[1..];
            return new Intent(Do.Web, Text: text, Url: $"https://translate.google.com/?sl=auto&tl={code}&text={Uri.EscapeDataString(text)}&op=translate", Label: $"Translate “{text}” to {name}");
        }
        m = M(s, @"^(?:what(?:'s| is) the |how(?:'s| is) the |the )?(?:weather|forecast|temperature)(?: (?:like|today|tomorrow|now))?(?: (?:in|at|for) (.+?))?(?: (?:today|tomorrow|this week))?$");
        if (m.Success)
        {
            string place = m.Groups[1].Success ? m.Groups[1].Value : "";
            return new Intent(Do.Web, Text: place, Url: "https://www.google.com/search?q=" + Uri.EscapeDataString(("weather " + place).Trim()),
                Label: place.Length > 0 ? $"Weather in {Title(place)}" : "The weather where you are");
        }
        m = M(s, @"^(?:news|headlines|latest news)(?: (?:about|on|for) (.+))?$");
        if (m.Success) return m.Groups[1].Success ? Site("news", m.Groups[1].Value) : new Intent(Do.Web, Url: "https://news.google.com/", Label: "Today's headlines");
        m = M(s, @"^(?:pictures|photos|images) of (.+)$");
        if (m.Success) return Site("images", m.Groups[1].Value);
        return null;
    }

    static string Title(string s) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);

    // ───────────────────────────── apps ─────────────────────────────

    static Intent? Close(string s)
    {
        var m = M(s, @"^(close|quit|exit|kill|end|terminate|force quit|force close|force-quit|shut) (?:the |my |all |all the )*(.+?)(?: app| application| window| windows| program| down)?$");
        if (!m.Success) return null;
        string name = m.Groups[2].Value.Trim();
        if (name.Length < 2 || Regex.IsMatch(name, @"^(?:it|this|that|everything|all|all apps|all windows|pc|computer|laptop|windows|down|focus|search|notch)$")) return null;
        bool force = m.Groups[1].Value is "kill" or "terminate" or "force quit" or "force close" or "force-quit";
        return new Intent(Do.Close, force ? "force" : "close", Text: name);
    }

    static Intent? Open(string s)
    {
        var m = M(s, @"^(open|launch|start|run|fire up|bring up|pull up|show me|show|go to|take me to|switch to|open up|get me) (?:the |my |up )?(.+?)(?: app| application| program)?$");
        if (!m.Success) return null;
        string name = m.Groups[2].Value.Trim();
        if (name.Length < 2) return null;
        return new Intent(Do.Open, m.Groups[1].Value is "switch to" or "go to" or "bring up" or "pull up" ? "switch" : "open", Text: name);
    }

    // ───────────────────────────── durations and times ─────────────────────────────

    static readonly Dictionary<string, double> NumberWords = new()
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["fifteen"] = 15, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40,
        ["forty-five"] = 45, ["forty five"] = 45, ["fifty"] = 50, ["sixty"] = 60, ["ninety"] = 90, ["a couple of"] = 2, ["couple of"] = 2,
        ["a couple"] = 2, ["a few"] = 3, ["few"] = 3,
    };

    static readonly Regex DurPiece = new(
        @"\G\s*(?:and\s+|,\s*)?(\d+(?:\.\d+)?|a couple of|couple of|a couple|a few|few|forty[- ]five|an|a|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|twenty|thirty|forty|fifty|sixty|ninety)\s*(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s|days?|d)(?![a-z])(?:\s*(\d{1,2})(?![\d.]|\s*[a-z]))?",
        RegexOptions.CultureInvariant);

    /// <summary>"20 min", "1h30", "an hour and a half", "90 seconds", "2 hours 15 minutes"; a bare number takes <paramref name="bare"/>.</summary>
    public static TimeSpan? Dur(string text, string? bare = null)
    {
        string t = text.Trim().ToLowerInvariant();
        t = Regex.Replace(t, @"\b(?:an|one|a) hour and a half\b", "90 min");
        t = Regex.Replace(t, @"\b(?:half an hour|half hour|a half hour|half an hr)\b", "30 min");
        t = Regex.Replace(t, @"\b(?:a )?quarter (?:of an )?hour\b", "15 min");
        t = Regex.Replace(t, @"\b(\d+) and a half (hours?|minutes?|mins?)\b", "$1.5 $2");
        t = t switch { "minute" or "a minute" or "one minute" or "a moment" or "a min" => "1 min", "a sec" or "a second" or "a few seconds" => "10 s", _ => t };
        if (bare != null && Regex.IsMatch(t, @"^\d+(?:\.\d+)?$")) t += " " + bare;
        if (bare != null && Regex.Match(t, @"^(\d{1,2}):(\d{2})$") is { Success: true } hm) // 1:30 → 1 h 30 (minutes for a timer: 25:00 → 25 min)
            return bare == "min" ? TimeSpan.FromMinutes(int.Parse(hm.Groups[1].Value) + int.Parse(hm.Groups[2].Value) / 60.0) : null;
        double secs = 0;
        int pos = 0;
        bool any = false;
        while (pos < t.Length)
        {
            var m = DurPiece.Match(t, pos);
            if (!m.Success || m.Length == 0) break;
            string w = m.Groups[1].Value.Replace('-', ' ');
            double n = double.TryParse(w, NumberStyles.Float, Inv, out double d) ? d : NumberWords.TryGetValue(w, out double nw) ? nw : double.NaN;
            if (double.IsNaN(n)) return null;
            double unit = m.Groups[2].Value[0] switch { 'h' => 3600, 'm' => 60, 'd' => 86400, _ => 1 };
            secs += n * unit;
            if (m.Groups[3].Success) secs += int.Parse(m.Groups[3].Value) * unit / 60;
            pos = m.Index + m.Length;
            any = true;
        }
        if (!any || t[pos..].Trim().Length > 0 || secs < 1 || secs > 30 * 86400) return null;
        return TimeSpan.FromSeconds(Math.Round(secs));
    }

    static readonly string[] Weekdays = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };

    /// <summary>
    /// "in 20 min", "at 5pm", "at 17:30", "tomorrow", "tomorrow at 9", "tonight", "on friday at 10", "at noon", "this evening".
    /// Always the next time it comes round; null for anything else (or for a time already gone today, when a day was named).
    /// </summary>
    public static DateTime? When(string text, DateTime now)
    {
        string t = text.Trim().ToLowerInvariant().Replace("o'clock", "").Replace("oclock", "").Trim();
        var m = M(t, @"^(?:in|after|within) (.+)$");
        if (m.Success) return Dur(m.Groups[1].Value) is { } d ? now + d : null;
        m = M(t, @"^(.+) (?:from now|later)$");
        if (m.Success) return Dur(m.Groups[1].Value) is { } d2 ? now + d2 : null;

        // the day, wherever it is in the phrase
        var dm = M(t, @"\b(today|tonight|tomorrow|tmrw|tomorow|this (?:morning|afternoon|evening)|(?:on |next |this )?(?:mon|tues|wednes|thurs|fri|satur|sun)day)(?: (morning|afternoon|evening|night))?\b");
        string dayWord = dm.Success ? dm.Groups[1].Value : "", part = dm.Success ? dm.Groups[2].Value : "";
        string rest = dm.Success ? (t[..dm.Index] + " " + t[(dm.Index + dm.Length)..]).Trim() : t;
        rest = Regex.Replace(rest, @"\s+", " ").Trim().TrimEnd(',');
        if (dayWord.StartsWith("this ")) { part = dayWord[5..]; dayWord = "today"; }
        if (dayWord == "tonight") part = "night";

        int? hour = null; int minute = 0; string ampm = "";
        bool hadAt = false;
        if (rest.Length > 0)
        {
            var tm = M(rest, @"^(?:(at|by|around) )?(?:(noon|midday|midnight)|(\d{1,2})(?:[:.h](\d{2}))?\s*(am|pm|a\.m\.|p\.m\.)?)(?: (?:in the )?(morning|afternoon|evening|night))?$");
            if (!tm.Success) return null;
            hadAt = tm.Groups[1].Success;
            if (tm.Groups[2].Success) { hour = tm.Groups[2].Value == "midnight" ? 0 : 12; ampm = "fixed"; }
            else
            {
                hour = int.Parse(tm.Groups[3].Value);
                if (tm.Groups[4].Success) minute = int.Parse(tm.Groups[4].Value);
                ampm = tm.Groups[5].Value.Replace(".", "");
                if (hour > 23 || minute > 59 || ampm.Length > 0 && (hour > 12 || hour == 0)) return null;
            }
            if (tm.Groups[6].Success) part = tm.Groups[6].Value;
            // a bare "5" says nothing about time: it needs "at", am/pm, a colon or a day
            if (!hadAt && ampm.Length == 0 && !tm.Groups[4].Success && dayWord.Length == 0 && part.Length == 0) return null;
        }
        else if (dayWord.Length == 0) return null;

        DateTime day = now.Date;
        bool namedDay = dayWord.Length > 0;
        if (dayWord is "tomorrow" or "tmrw" or "tomorow") day = day.AddDays(1);
        else if (dayWord.EndsWith("day") && dayWord != "today")
        {
            string name = Regex.Replace(dayWord, @"^(?:on|next|this) ", "");
            int target = Array.IndexOf(Weekdays, name);
            if (target < 0) return null;
            int ahead = (target - (int)now.DayOfWeek + 7) % 7;
            if (ahead == 0 && dayWord.StartsWith("next ")) ahead = 7;
            day = day.AddDays(ahead);
        }

        int h;
        if (hour == null)
            h = part switch { "morning" => 9, "afternoon" => 14, "evening" => 19, "night" => 21, _ => 9 };
        else
        {
            h = hour.Value;
            if (ampm == "pm" && h < 12) h += 12;
            else if (ampm == "am" && h == 12) h = 0;
            else if (ampm.Length == 0)
            {
                if (part is "afternoon" or "evening" or "night" && h < 12) h += 12;
                else if (part.Length == 0 && h is >= 1 and <= 11)
                {
                    if (namedDay) { if (h <= 6) h += 12; } // "tomorrow at 5": the afternoon
                    else
                    {
                        // "at 5" means the next 5 o'clock that comes round
                        var am = now.Date.AddHours(h).AddMinutes(minute);
                        if (am <= now) h += 12;
                    }
                }
            }
        }
        var result = day.AddHours(h).AddMinutes(minute);
        if (result <= now)
        {
            if (!namedDay) result = result.AddDays(1);
            else if (dayWord.EndsWith("day") && dayWord != "today") result = result.AddDays(7);
            else return null;
        }
        return result;
    }

    // ───────────────────────────── facts ─────────────────────────────

    /// <summary>Something the notch can just tell you (Enter copies it). Battery and music are the notch's own to answer.</summary>
    public static LauncherAnswers.Answer? Fact(string query, DateTime now)
    {
        string raw = query.Trim();
        string s = Clean(query);
        if (s.Length < 2) return null;
        try { return Clock(s, now) ?? Calendar(s, now) ?? Machine(s) ?? Chance(s, raw); }
        catch { return null; }
    }

    static LauncherAnswers.Answer A(string title, string detail, string copy) => new(title, detail, copy);

    // the time anywhere

    static readonly Dictionary<string, (string Zone, string Name)> Cities = new()
    {
        ["tokyo"] = ("Asia/Tokyo", "Tokyo"), ["japan"] = ("Asia/Tokyo", "Japan"), ["osaka"] = ("Asia/Tokyo", "Osaka"),
        ["seoul"] = ("Asia/Seoul", "Seoul"), ["korea"] = ("Asia/Seoul", "Seoul"), ["beijing"] = ("Asia/Shanghai", "Beijing"), ["shanghai"] = ("Asia/Shanghai", "Shanghai"),
        ["china"] = ("Asia/Shanghai", "China"), ["hong kong"] = ("Asia/Hong_Kong", "Hong Kong"), ["taipei"] = ("Asia/Taipei", "Taipei"), ["singapore"] = ("Asia/Singapore", "Singapore"),
        ["bangkok"] = ("Asia/Bangkok", "Bangkok"), ["jakarta"] = ("Asia/Jakarta", "Jakarta"), ["manila"] = ("Asia/Manila", "Manila"), ["kuala lumpur"] = ("Asia/Kuala_Lumpur", "Kuala Lumpur"),
        ["mumbai"] = ("Asia/Kolkata", "Mumbai"), ["delhi"] = ("Asia/Kolkata", "Delhi"), ["new delhi"] = ("Asia/Kolkata", "New Delhi"), ["india"] = ("Asia/Kolkata", "India"),
        ["bangalore"] = ("Asia/Kolkata", "Bangalore"), ["karachi"] = ("Asia/Karachi", "Karachi"), ["pakistan"] = ("Asia/Karachi", "Pakistan"), ["dhaka"] = ("Asia/Dhaka", "Dhaka"),
        ["dubai"] = ("Asia/Dubai", "Dubai"), ["abu dhabi"] = ("Asia/Dubai", "Abu Dhabi"), ["uae"] = ("Asia/Dubai", "UAE"), ["doha"] = ("Asia/Qatar", "Doha"), ["qatar"] = ("Asia/Qatar", "Qatar"),
        ["riyadh"] = ("Asia/Riyadh", "Riyadh"), ["mecca"] = ("Asia/Riyadh", "Mecca"), ["makkah"] = ("Asia/Riyadh", "Makkah"), ["saudi arabia"] = ("Asia/Riyadh", "Saudi Arabia"),
        ["tehran"] = ("Asia/Tehran", "Tehran"), ["istanbul"] = ("Europe/Istanbul", "Istanbul"), ["turkey"] = ("Europe/Istanbul", "Turkey"), ["jerusalem"] = ("Asia/Jerusalem", "Jerusalem"),
        ["beirut"] = ("Asia/Beirut", "Beirut"), ["amman"] = ("Asia/Amman", "Amman"), ["baghdad"] = ("Asia/Baghdad", "Baghdad"), ["kuwait"] = ("Asia/Kuwait", "Kuwait"),
        ["cairo"] = ("Africa/Cairo", "Cairo"), ["egypt"] = ("Africa/Cairo", "Egypt"), ["casablanca"] = ("Africa/Casablanca", "Casablanca"), ["rabat"] = ("Africa/Casablanca", "Rabat"),
        ["marrakech"] = ("Africa/Casablanca", "Marrakech"), ["morocco"] = ("Africa/Casablanca", "Morocco"), ["tangier"] = ("Africa/Casablanca", "Tangier"), ["fes"] = ("Africa/Casablanca", "Fes"),
        ["agadir"] = ("Africa/Casablanca", "Agadir"), ["algiers"] = ("Africa/Algiers", "Algiers"), ["algeria"] = ("Africa/Algiers", "Algeria"), ["tunis"] = ("Africa/Tunis", "Tunis"),
        ["tunisia"] = ("Africa/Tunis", "Tunisia"), ["lagos"] = ("Africa/Lagos", "Lagos"), ["nairobi"] = ("Africa/Nairobi", "Nairobi"), ["johannesburg"] = ("Africa/Johannesburg", "Johannesburg"),
        ["cape town"] = ("Africa/Johannesburg", "Cape Town"), ["dakar"] = ("Africa/Dakar", "Dakar"), ["accra"] = ("Africa/Accra", "Accra"),
        ["london"] = ("Europe/London", "London"), ["uk"] = ("Europe/London", "the UK"), ["dublin"] = ("Europe/Dublin", "Dublin"), ["lisbon"] = ("Europe/Lisbon", "Lisbon"),
        ["paris"] = ("Europe/Paris", "Paris"), ["france"] = ("Europe/Paris", "France"), ["madrid"] = ("Europe/Madrid", "Madrid"), ["barcelona"] = ("Europe/Madrid", "Barcelona"),
        ["spain"] = ("Europe/Madrid", "Spain"), ["berlin"] = ("Europe/Berlin", "Berlin"), ["germany"] = ("Europe/Berlin", "Germany"), ["munich"] = ("Europe/Berlin", "Munich"),
        ["amsterdam"] = ("Europe/Amsterdam", "Amsterdam"), ["brussels"] = ("Europe/Brussels", "Brussels"), ["rome"] = ("Europe/Rome", "Rome"), ["milan"] = ("Europe/Rome", "Milan"),
        ["italy"] = ("Europe/Rome", "Italy"), ["zurich"] = ("Europe/Zurich", "Zurich"), ["geneva"] = ("Europe/Zurich", "Geneva"), ["vienna"] = ("Europe/Vienna", "Vienna"),
        ["stockholm"] = ("Europe/Stockholm", "Stockholm"), ["oslo"] = ("Europe/Oslo", "Oslo"), ["copenhagen"] = ("Europe/Copenhagen", "Copenhagen"), ["helsinki"] = ("Europe/Helsinki", "Helsinki"),
        ["warsaw"] = ("Europe/Warsaw", "Warsaw"), ["prague"] = ("Europe/Prague", "Prague"), ["athens"] = ("Europe/Athens", "Athens"), ["kyiv"] = ("Europe/Kyiv", "Kyiv"),
        ["moscow"] = ("Europe/Moscow", "Moscow"), ["russia"] = ("Europe/Moscow", "Moscow"),
        ["new york"] = ("America/New_York", "New York"), ["nyc"] = ("America/New_York", "New York"), ["boston"] = ("America/New_York", "Boston"), ["miami"] = ("America/New_York", "Miami"),
        ["washington"] = ("America/New_York", "Washington"), ["toronto"] = ("America/Toronto", "Toronto"), ["montreal"] = ("America/Toronto", "Montreal"), ["chicago"] = ("America/Chicago", "Chicago"),
        ["houston"] = ("America/Chicago", "Houston"), ["dallas"] = ("America/Chicago", "Dallas"), ["denver"] = ("America/Denver", "Denver"), ["phoenix"] = ("America/Phoenix", "Phoenix"),
        ["los angeles"] = ("America/Los_Angeles", "Los Angeles"), ["la"] = ("America/Los_Angeles", "Los Angeles"), ["san francisco"] = ("America/Los_Angeles", "San Francisco"),
        ["seattle"] = ("America/Los_Angeles", "Seattle"), ["vancouver"] = ("America/Vancouver", "Vancouver"), ["las vegas"] = ("America/Los_Angeles", "Las Vegas"),
        ["california"] = ("America/Los_Angeles", "California"), ["mexico city"] = ("America/Mexico_City", "Mexico City"), ["mexico"] = ("America/Mexico_City", "Mexico City"),
        ["sao paulo"] = ("America/Sao_Paulo", "São Paulo"), ["rio"] = ("America/Sao_Paulo", "Rio"), ["brazil"] = ("America/Sao_Paulo", "Brazil"), ["buenos aires"] = ("America/Argentina/Buenos_Aires", "Buenos Aires"),
        ["bogota"] = ("America/Bogota", "Bogotá"), ["lima"] = ("America/Lima", "Lima"), ["santiago"] = ("America/Santiago", "Santiago"), ["honolulu"] = ("Pacific/Honolulu", "Honolulu"),
        ["hawaii"] = ("Pacific/Honolulu", "Hawaii"), ["anchorage"] = ("America/Anchorage", "Anchorage"),
        ["sydney"] = ("Australia/Sydney", "Sydney"), ["melbourne"] = ("Australia/Melbourne", "Melbourne"), ["brisbane"] = ("Australia/Brisbane", "Brisbane"), ["perth"] = ("Australia/Perth", "Perth"),
        ["auckland"] = ("Pacific/Auckland", "Auckland"), ["new zealand"] = ("Pacific/Auckland", "New Zealand"),
        ["utc"] = ("UTC", "UTC"), ["gmt"] = ("UTC", "GMT"),
    };

    static TimeZoneInfo? Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch
        {
            try { return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win) ? TimeZoneInfo.FindSystemTimeZoneById(win) : null; }
            catch { return null; }
        }
    }

    static LauncherAnswers.Answer? Clock(string s, DateTime now)
    {
        var m = M(s, @"^(?:what(?:'s| is) the )?(?:current |local )?time(?: is it)?(?: now| right now)?(?: (?:in|at) (.+?))?(?: now| right now)?$");
        if (!m.Success) m = M(s, @"^what time is it(?: now| right now)?(?: (?:in|at) (.+?))?(?: now| right now)?$");
        if (!m.Success) m = M(s, @"^(.+?) time$");
        if (!m.Success) return null;
        var culture = CultureInfo.CurrentCulture;
        if (!m.Groups[1].Success || m.Groups[1].Value is "local" or "my" or "current")
        {
            if (s.EndsWith(" time") && !s.StartsWith("time") && !s.Contains("what")) return null; // "screen time", "game time"
            return A(now.ToString("t", culture), now.ToString("dddd d MMMM", culture) + " · Enter copies", now.ToString("t", culture));
        }
        string place = m.Groups[1].Value.Trim();
        if (!Cities.TryGetValue(place, out var city) || Zone(city.Zone) is not { } tz) return null;
        var there = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, tz);
        double diff = (there.Offset - DateTimeOffset.Now.Offset).TotalHours;
        string rel = Math.Abs(diff) < 0.01 ? "same time as you"
            : $"{Hours(Math.Abs(diff))} {(diff > 0 ? "ahead of" : "behind")} you";
        string dayNote = there.Date == now.Date ? "" : there.Date > now.Date ? " · tomorrow" : " · yesterday";
        return A(there.ToString("t", culture), $"{city.Name} · {there.ToString("ddd d MMM", culture)}{dayNote} · {rel}", there.ToString("t", culture));
    }

    static string Hours(double h) => h % 1 == 0 ? $"{h:0} h" : $"{Math.Floor(h):0} h {Math.Round(h % 1 * 60):0} min";

    // days, dates, holidays

    static LauncherAnswers.Answer? Calendar(string s, DateTime now)
    {
        var culture = CultureInfo.CurrentCulture;
        DateTime today = now.Date;
        if (M(s, @"^(?:today|date|today's date|todays date|the date|what(?:'s| is) (?:the )?date(?: today)?|what day is (?:it|today)|what(?:'s| is) today|what day it is)$").Success)
        {
            int week = ISOWeek.GetWeekOfYear(today);
            return A(today.ToString("dddd d MMMM yyyy", culture), $"Week {week} · day {today.DayOfYear} of {(DateTime.IsLeapYear(today.Year) ? 366 : 365)}", today.ToString("D", culture));
        }
        if (M(s, @"^(?:week|week number|what week is it|which week is it|week no|current week|what(?:'s| is) the week(?: number)?)$").Success)
        {
            int w = ISOWeek.GetWeekOfYear(today);
            var mon = ISOWeek.ToDateTime(today.Year, w, DayOfWeek.Monday);
            if (today.Month == 1 && w > 50) mon = ISOWeek.ToDateTime(today.Year - 1, w, DayOfWeek.Monday);
            return A($"Week {w}", $"{mon.ToString("d MMM", culture)} – {mon.AddDays(6).ToString("d MMM", culture)}", w.ToString(Inv));
        }

        var m = M(s, @"^(?:how many )?(?:days|sleeps|weeks) (?:until|till|til|to|before|left (?:until|till|to|before|for)?|left) ?(.+)$");
        if (!m.Success) m = M(s, @"^how (?:long|many days) (?:until|till|til|to|is it until|is it till|left until) (.+)$");
        if (!m.Success) m = M(s, @"^countdown to (.+)$");
        if (m.Success && Day(m.Groups[1].Value, today, future: true) is { } until)
        {
            int days = (until.Date - today).Days;
            bool weeks = s.Contains("weeks");
            string title = days == 0 ? "Today" : days == 1 ? "Tomorrow" : weeks ? $"{days / 7} weeks{(days % 7 > 0 ? $" {days % 7} days" : "")}" : $"{days:N0} days";
            return A(title, $"until {until.Name} · {until.Date.ToString("ddd d MMM yyyy", culture)}{until.Note}", days.ToString(Inv));
        }
        m = M(s, @"^(?:how many )?days (?:since|from|after) (.+)$|^how long (?:ago|since) (?:was )?(.+)$");
        if (m.Success && Day(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, today, future: false) is { } since && since.Date <= today)
        {
            int days = (today - since.Date).Days;
            return A($"{days:N0} days", $"since {since.Name} · {since.Date.ToString("ddd d MMM yyyy", culture)}", days.ToString(Inv));
        }
        m = M(s, @"^when (?:is|will be|does) (.+?)(?: start| begin)?$");
        if (m.Success && Day(m.Groups[1].Value, today, future: true) is { } when && when.Holiday)
        {
            int days = (when.Date - today).Days;
            return A(when.Date.ToString("dddd d MMMM yyyy", culture), $"{when.Name} · {(days == 0 ? "today" : days == 1 ? "tomorrow" : $"in {days} days")}{when.Note}", when.Date.ToString("D", culture));
        }
        m = M(s, @"^what day (?:is|was|will be|falls on|is it on|of the week is) (.+)$|^(?:which|what) weekday is (.+)$");
        if (m.Success && Day(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, today, future: true) is { } wd)
            return A(wd.Date.ToString("dddd", culture), wd.Date.ToString("d MMMM yyyy", culture) + wd.Note, wd.Date.ToString("dddd", culture));
        m = M(s, @"^(?:what(?:'s| is| will be) the )?date (?:in|after) (.+)$|^(.+) from (?:now|today)$|^in (\d+ (?:days?|weeks?|months?|years?))$");
        if (m.Success)
        {
            string span = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            var sm = M(span, @"^(\d+|a|an|one|two|three|four|five|six|seven|eight|nine|ten|twelve)\s+(days?|weeks?|months?|years?)$");
            if (sm.Success)
            {
                int n = int.TryParse(sm.Groups[1].Value, out int k) ? k : (int)NumberWords[sm.Groups[1].Value];
                var date = sm.Groups[2].Value[0] switch { 'd' => today.AddDays(n), 'w' => today.AddDays(7 * n), 'm' => today.AddMonths(n), _ => today.AddYears(n) };
                return A(date.ToString("dddd d MMMM yyyy", culture), $"{n} {sm.Groups[2].Value} from today", date.ToString("D", culture));
            }
        }
        return null;
    }

    sealed record Dated(DateTime Date, string Name, string Note = "", bool Holiday = false);

    /// <summary>A day named in words: a holiday, a weekday, "25 dec", "december 25 2027", "2026-12-25", "25/12".</summary>
    static Dated? Day(string text, DateTime today, bool future)
    {
        string t = text.Trim().Trim('?', '.').Replace("’", "'");
        t = Regex.Replace(t, @"^(?:the |next |this |upcoming )", "");
        DateTime Yearly(int month, int day)
        {
            var d = new DateTime(today.Year, month, day);
            return future ? (d < today ? d.AddYears(1) : d) : (d > today ? d.AddYears(-1) : d);
        }
        Dated H(int month, int day, string name) => new(Yearly(month, day), name, Holiday: true);
        Dated Hijri(int month, int day, string name)
        {
            var cal = new UmAlQuraCalendar();
            int y = cal.GetYear(today);
            DateTime best = today;
            for (int k = -1; k <= 1; k++)
            {
                try
                {
                    var d = cal.ToDateTime(y + k, month, day, 0, 0, 0, 0);
                    if (future ? d >= today : d <= today) { best = d; if (future) break; }
                }
                catch { }
            }
            return new Dated(best, name, " · approx., the moon decides", Holiday: true);
        }
        switch (t)
        {
            case "christmas" or "xmas" or "christmas day": return H(12, 25, "Christmas");
            case "christmas eve": return H(12, 24, "Christmas Eve");
            case "new year" or "new years" or "new year's" or "new year's day" or "new years day" or "nye day": return H(1, 1, "New Year");
            case "new year's eve" or "new years eve" or "nye": return H(12, 31, "New Year's Eve");
            case "halloween": return H(10, 31, "Halloween");
            case "valentine's day" or "valentines day" or "valentine's" or "valentines" or "valentine": return H(2, 14, "Valentine's Day");
            case "ramadan" or "ramadhan" or "ramzan": return Hijri(9, 1, "Ramadan");
            case "eid" or "eid al fitr" or "eid al-fitr" or "eid alfitr" or "eid ul fitr" or "eid-ul-fitr" or "eid fitr": return Hijri(10, 1, "Eid al-Fitr");
            case "eid al adha" or "eid al-adha" or "eid ul adha" or "eid-ul-adha" or "eid adha" or "eid kabir" or "eid el kebir": return Hijri(12, 10, "Eid al-Adha");
            case "islamic new year" or "hijri new year" or "muharram": return Hijri(1, 1, "the Islamic New Year");
            case "tomorrow": return new(today.AddDays(1), "tomorrow");
            case "today": return new(today, "today");
            case "yesterday": return new(today.AddDays(-1), "yesterday");
            case "weekend" or "the weekend": { int a = ((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7; return new(today.AddDays(a), "the weekend"); }
            case "end of the year" or "end of year" or "year end": return new(new DateTime(today.Year, 12, 31), "the end of the year");
            case "end of the month" or "end of month" or "month end": return new(new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)), "the end of the month");
        }
        int wd = Array.IndexOf(Weekdays, t);
        if (wd >= 0)
        {
            int ahead = (wd - (int)today.DayOfWeek + 7) % 7;
            if (ahead == 0) ahead = 7;
            return new(future ? today.AddDays(ahead) : today.AddDays(ahead - 7), Title(t));
        }
        string d0 = Regex.Replace(t, @"(\d+)(?:st|nd|rd|th)\b", "$1").Replace(" of ", " ").Replace(",", "");
        d0 = Regex.Replace(d0, @"\s+", " ").Trim();
        // month names in English or the PC's own language
        foreach (var culture in new[] { Inv, CultureInfo.CurrentCulture })
        {
            if (DateTime.TryParseExact(d0, new[] { "yyyy-M-d", "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "d MMM yyyy", "d MMMM yyyy", "MMM d yyyy", "MMMM d yyyy" },
                    culture, DateTimeStyles.AllowWhiteSpaces, out var full))
                return new(full, full.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
            if (DateTime.TryParseExact(d0, new[] { "d MMM", "d MMMM", "MMM d", "MMMM d", "d/M" }, culture, DateTimeStyles.AllowWhiteSpaces, out var part))
            {
                var d = new DateTime(today.Year, part.Month, part.Day);
                d = future ? (d < today ? d.AddYears(1) : d) : (d > today ? d.AddYears(-1) : d);
                return new(d, d.ToString("d MMM", CultureInfo.CurrentCulture));
            }
        }
        return null;
    }

    // this PC

    [StructLayout(LayoutKind.Sequential)]
    struct MemStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExt;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemStatus m);

    static string Bytes(double b) => b >= 1L << 40 ? $"{b / (1L << 40):0.#} TB" : b >= 1 << 30 ? $"{b / (1 << 30):0.#} GB" : $"{b / (1 << 20):0} MB";

    static LauncherAnswers.Answer? Machine(string s)
    {
        if (M(s, @"^(?:my |local |what(?:'s| is) my |show (?:me )?my )?(?:local )?ip(?: address)?$|^ip address$").Success)
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                var ip = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (ip != null) return A(ip.Address.ToString(), $"On your network · {ni.Name} · Enter copies", ip.Address.ToString());
            }
            return A("Not connected", "No network with an address right now", "");
        }
        if (M(s, @"^(?:uptime|up time|how long (?:has|have) (?:my |the |this )?(?:pc|computer|laptop) been (?:on|running|up)|time since (?:last )?(?:boot|restart))$").Success)
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            string title = up.TotalDays >= 1 ? $"{(int)up.TotalDays} d {up.Hours} h" : up.TotalHours >= 1 ? $"{up.Hours} h {up.Minutes} min" : $"{up.Minutes} min";
            var since = DateTime.Now - up;
            return A(title, $"On since {since.ToString("ddd d MMM", CultureInfo.CurrentCulture)}, {since.ToString("t", CultureInfo.CurrentCulture)} · a restart now and then keeps Windows quick", title);
        }
        if (M(s, @"^(?:disk|disks|storage|disk space|free space|space left|drive space|how much (?:disk |free )?space(?: (?:is )?left| do i have)?|storage left|free storage)(?: left)?$").Success)
        {
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).ToList();
            if (drives.Count == 0) return null;
            var main = drives.FirstOrDefault(d => d.Name.StartsWith(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:", StringComparison.OrdinalIgnoreCase)) ?? drives[0];
            string others = string.Join(" · ", drives.Where(d => d != main).Select(d => $"{d.Name.TrimEnd('\\')} {Bytes(d.AvailableFreeSpace)} free"));
            double pct = 100.0 * main.AvailableFreeSpace / main.TotalSize;
            return A($"{Bytes(main.AvailableFreeSpace)} free", $"on {main.Name.TrimEnd('\\')} of {Bytes(main.TotalSize)} ({pct:0}%){(others.Length > 0 ? " · " + others : "")}", Bytes(main.AvailableFreeSpace));
        }
        if (M(s, @"^(?:ram|memory|ram usage|memory usage|how much (?:ram|memory)(?: (?:is )?(?:used|free|left))?|free (?:ram|memory))$").Success)
        {
            var ms = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
            if (!GlobalMemoryStatusEx(ref ms)) return null;
            double used = ms.TotalPhys - ms.AvailPhys;
            return A($"{Bytes(used)} of {Bytes(ms.TotalPhys)} in use", $"{ms.Load}% · {Bytes(ms.AvailPhys)} free", $"{ms.Load}%");
        }
        if (M(s, @"^(?:(?:my )?(?:pc|computer|laptop) name|computer name|device name|hostname|host name|what(?:'s| is) my (?:pc|computer|device) (?:called|name))$").Success)
            return A(Environment.MachineName, $"Windows {Environment.OSVersion.Version.Build} · signed in as {Environment.UserName}", Environment.MachineName);
        return null;
    }

    // chance and things to copy

    static LauncherAnswers.Answer? Chance(string s, string raw)
    {
        if (M(s, @"^(?:flip|toss)(?: a| the)? coin$|^coin(?: flip| toss)?$|^heads or tails$").Success)
        {
            string side = RandomNumberGenerator.GetInt32(2) == 0 ? "Heads" : "Tails";
            return A(side, "Flipped just now · type it again for another go", side);
        }
        var m = M(s, @"^(?:roll|throw)(?: a| the| some)? (?:die|dice|d6)$|^dice$|^roll (\d{1,2})?d(\d{1,3})$|^roll (\d) dice$");
        if (m.Success)
        {
            int count = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 1;
            int sides = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 6;
            if (count is < 1 or > 20 || sides < 2) return null;
            var rolls = Enumerable.Range(0, count).Select(_ => RandomNumberGenerator.GetInt32(1, sides + 1)).ToList();
            string faces = sides == 6 && count <= 6 ? string.Concat(rolls.Select(r => "⚀⚁⚂⚃⚄⚅"[r - 1])) + "  " : "";
            return A($"{faces}{rolls.Sum()}", count > 1 ? string.Join(" + ", rolls) : $"A d{sides}", rolls.Sum().ToString(Inv));
        }
        m = M(s, @"^(?:random|random number|pick a number|rand|rng|random int)(?: (?:between|from))?(?: (-?\d+)\s*(?:and|to|-)\s*(-?\d+))?$");
        if (m.Success)
        {
            long lo = m.Groups[1].Success ? long.Parse(m.Groups[1].Value) : 1, hi = m.Groups[2].Success ? long.Parse(m.Groups[2].Value) : 100;
            if (lo > hi) (lo, hi) = (hi, lo);
            if (hi - lo > int.MaxValue - 1) return null;
            long n = lo + RandomNumberGenerator.GetInt32((int)(hi - lo + 1));
            return A(n.ToString("N0", CultureInfo.CurrentCulture), $"Between {lo} and {hi}", n.ToString(Inv));
        }
        m = M(s, @"^(?:pick|choose|decide|select|which)(?: one)?(?: between| from| for me)?:? (.+)$");
        string? options = m.Success ? m.Groups[1].Value : raw.TrimEnd().EndsWith('?') && s.Contains(" or ") && s.Split(' ').Length <= 12 ? s : null;
        if (options != null && options.Contains(" or ") || options != null && options.Contains(','))
        {
            var choices = Regex.Split(options!, @"\s*,\s*(?:or\s+)?|\s+or\s+").Select(c => c.Trim()).Where(c => c.Length > 0).Distinct().ToList();
            if (choices.Count is >= 2 and <= 10)
            {
                string pick = Tidy(choices[RandomNumberGenerator.GetInt32(choices.Count)]);
                return A($"→ {pick}", $"Picked from {choices.Count} at random", pick);
            }
        }
        m = M(s, @"^(?:generate |make |create |new |give me |random )?(?:a )?(?:strong |random |secure |new )?(?:password|passphrase)(?: of)?(?: (\d{1,2}))?(?: chars| characters| letters| long)?$");
        if (m.Success)
        {
            int len = m.Groups[1].Success ? Math.Clamp(int.Parse(m.Groups[1].Value), 8, 64) : 18;
            string pw = Password(len);
            return new LauncherAnswers.Answer(pw, $"{len} characters · made on this PC · Enter copies it, kept out of clipboard history", pw, Secret: true);
        }
        if (M(s, @"^(?:new |generate |random )?(?:a )?(?:uuid|guid)$").Success)
        {
            string g = Guid.NewGuid().ToString();
            return A(g, "A new unique id · Enter copies", g);
        }
        return null;
    }

    static string Password(int len)
    {
        const string lower = "abcdefghijkmnpqrstuvwxyz", upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", digits = "23456789", signs = "!@#$%&*-_+=?";
        string all = lower + upper + digits + signs;
        var chars = new char[len];
        chars[0] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[1] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = signs[RandomNumberGenerator.GetInt32(signs.Length)];
        for (int i = 4; i < len; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        for (int i = len - 1; i > 0; i--) { int j = RandomNumberGenerator.GetInt32(i + 1); (chars[i], chars[j]) = (chars[j], chars[i]); }
        return new string(chars);
    }
}
