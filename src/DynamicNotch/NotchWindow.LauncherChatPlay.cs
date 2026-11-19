using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Playing something, in one go and checked: Spotify opens straight on the search for it, the top result's own play
/// button is pressed (found by its name, in any language Spotify speaks: "Play", "Lire", "Reproducir"...), and it's
/// only called done once the notch hears the new song start. YouTube: the first video for the search opens and plays.
/// Used by the instant route ("play lofi", "mets du jazz sur spotify") and by the AI (play_music), instead of the slow
/// look-type-look-click way.
/// </summary>
public partial class NotchWindow
{
    static readonly Regex PlayWords = new(@"^(?<verb>play|put on|put|throw on|start playing|listen to|joue|jouer|mets|mettre|lance|écoute|ecoute|reproduce|pon|spiel|suona|run|start|launch|blast|queue up|shgel|tla9)\s+(?:me\s+|moi\s+|nous\s+|us\s+)?(?<q>.+?)(?:[\s,]+(?:but\s+|mais\s+|f\s+)?(?:on|in|sur|en|dans|auf|with|using|via|from|f)\s+(?<app>spotify|spoty|spotfy|spotif|youtube|yt))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PlayButton = new(@"^(?:Play|Lire|Reproducir|Abspielen|Riproduci|Tocar|Reproduzir|Spela|Afspelen|Odtwórz|Oynat|Воспроизвести|再生|播放|재생)\s*[:：]?\s+(?<what>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HttpClient PlayHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    bool HasSpotify()
    {
        try { return RunningWindows("spotify").Count > 0 || ResolveEntry("spotify") is { Kind: LauncherIndex.Kind.App } e && e.Name.Contains("spotify", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>"play X [on spotify|youtube]": what to play and where, or null when it isn't that.</summary>
    (string Query, string Where)? PlayRequest(string text)
    {
        string s = LauncherIntent.Clean(text);
        var m = PlayWords.Match(s);
        if (!m.Success) return null;
        string verb = m.Groups["verb"].Value.ToLowerInvariant();
        // "run", "start", "launch" mean playing only when it says where ("run lofi on spotify"); else they're about apps
        if (verb is "run" or "start" or "launch" or "put" && !m.Groups["app"].Success) return null;
        string q = m.Groups["q"].Value.Trim().Trim('"', '“', '”');
        string said = m.Groups["app"].Value.ToLowerInvariant();
        // your liked songs, your favourites, your music: played as they are, not searched for
        if (Regex.IsMatch(q, @"\b(favou?rites?|favs?|liked( songs| tracks| music)?|titres lik[ée]s|mes favoris|mes titres|my (own )?(music|songs|tracks|playlist)|ma musique|mes chansons)\b", RegexOptions.IgnoreCase)
            && (said.StartsWith("spot") || said.Length == 0 && HasSpotify())) return ("", "liked");
        // "something similar", "a nicer one", "another": it depends on what's playing: the AI picks a real search
        if (Regex.IsMatch(q, @"\b(similar|same|another|other|different|nicer|better|this one|that one|like this|like that|something|anything|whatever|random|smth|qqch|quelque chose|un autre|pareil|mieux)\b", RegexOptions.IgnoreCase)) return null;
        q = Regex.Replace(q, @"^(?:some|a|an|the|du|de la|des|de l'|de|la|le|les|un|une|del|el|los)\s+", "", RegexOptions.IgnoreCase).Trim();
        // "chill sound beats like lofi": what it's like is the better search
        if (Regex.Match(q, @"\b(?:like|kind of|style|genre|type|comme|style de|genre de)\s+(?<like>.{3,})$", RegexOptions.IgnoreCase) is { Success: true } lk) q = (Regex.Replace(q[..lk.Index], @"\b(?:sound|sounds|music|musique|songs?|tracks?|beats?)\b", "", RegexOptions.IgnoreCase).Trim() + " " + lk.Groups["like"].Value).Trim();
        if (q.Length < 2 || Regex.IsMatch(q, @"^(music|musique|something|smth|it|the music|again|pause|next|previous)$", RegexOptions.IgnoreCase)) return null;
        // "lance spotify" opens the app, it doesn't search for it
        try { if (ResolveEntry(q) is { Kind: LauncherIndex.Kind.App } named && Norm(named.Name).StartsWith(Norm(q)) && Norm(q).Length >= 3) return null; } catch { }
        string app = m.Groups["app"].Value.ToLowerInvariant();
        string where = app is "youtube" or "yt" ? "youtube" : app.StartsWith("spot") || HasSpotify() ? "spotify" : "youtube";
        return (q, where);
    }

    async Task<string> PlayMusic(string query, string where, CancellationToken cancel = default)
    {
        query = query.Trim();
        if (query.Length == 0 && where != "liked") return "Error: what should play?";
        where = where.Trim().ToLowerInvariant();
        if (where is "" or "auto") where = HasSpotify() ? "spotify" : "youtube";
        if (where == "liked" || Regex.IsMatch(query, @"^(liked songs?|my liked songs|favou?rites?|titres lik[ée]s)$", RegexOptions.IgnoreCase)) return await PlayLiked(cancel);
        return where == "spotify" ? await PlayOnSpotify(query, cancel) : await PlayOnYouTube(query, cancel);
    }

    async Task<string> PlayOnSpotify(string query, CancellationToken cancel)
    {
        string before = _media.Title;
        // what was on the screen before: the new search is told apart from it
        var win0 = RunningWindows("spotify").Select(w => w.Hwnd).FirstOrDefault();
        var shownBefore = win0 != IntPtr.Zero ? await Limited(() => PlayNames(win0), new List<string>()) : new List<string>();
        bool sameSearch = query.Equals(_lastSpotifySearch, StringComparison.OrdinalIgnoreCase);
        _lastSpotifySearch = query;
        try { Process.Start(new ProcessStartInfo("spotify:search:" + Uri.EscapeDataString(query)) { UseShellExecute = true }); }
        catch { return await PlayOnYouTube(query, cancel); } // Spotify isn't there after all: YouTube

        // its window (it may be starting)
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 40 && hwnd == IntPtr.Zero; i++)
        {
            hwnd = RunningWindows("spotify").OrderByDescending(w => { GetWindowRect(w.Hwnd, out var r); return (long)(r.Right - r.Left) * (r.Bottom - r.Top); }).Select(w => w.Hwnd).FirstOrDefault();
            if (hwnd == IntPtr.Zero) await Task.Delay(300, cancel);
        }
        if (hwnd == IntPtr.Zero) return await PlayOnYouTube(query, cancel); // Spotify didn't open: YouTube

        // the results of THIS search: the page's play buttons must have changed from what was there before
        // (pressing too early played an old result); the same search twice in a row may show the same page
        (AutomationElement El, string Name)? hit = null;
        var since = DateTime.UtcNow;
        while ((DateTime.UtcNow - since).TotalSeconds < 10 && !cancel.IsCancellationRequested)
        {
            await Task.Delay(400, cancel);
            var now = await Limited(() => PlayNames(hwnd), new List<string>(), 4);
            if (now.Count == 0) continue;
            bool fresh = !now.Take(6).SequenceEqual(shownBefore.Take(6)) || sameSearch && (DateTime.UtcNow - since).TotalSeconds > 2.5;
            if (!fresh) continue;
            await Task.Delay(350, cancel); // the page settles
            hit = await Limited(() => FirstPlay(hwnd), null, 4);
            if (hit != null) break;
        }
        if (hit == null) return $"Spotify is open on the search for “{query}”, but the results didn't load in time, so nothing was started. Press play on the top result, or ask again.";
        string what = PlayButton.Match(hit.Value.Name) is { Success: true } pm ? pm.Groups["what"].Value : hit.Value.Name;
        string pressed = await Limited(() => Press(hit.Value.El, "button"), "Error: Spotify didn't respond.");
        if (pressed.StartsWith("Error")) return $"Spotify shows “{what}” but it couldn't be started: press play.";

        // done only when it's really playing
        for (int i = 0; i < 20 && !cancel.IsCancellationRequested; i++)
        {
            await Task.Delay(250, cancel);
            if (_media.IsPlaying && _media.Title.Length > 0 && _media.Title != before)
                return $"Done: playing {_media.Title}{(_media.Artist.Length > 0 ? " — " + _media.Artist : "")} on Spotify";
        }
        return $"Pressed play on “{what}” in Spotify, but no new song was heard starting. Say it isn't confirmed.";
    }

    string? _lastSpotifySearch;

    /// <summary>The names of the play buttons on Spotify's page now, in order (to tell a new page from the old one).</summary>
    static List<string> PlayNames(IntPtr hwnd)
    {
        var names = new List<string>();
        try { foreach (var (_, kind, name) in Elements(hwnd)) if (kind == "button" && PlayButton.IsMatch(name)) names.Add(name); } catch { }
        return names;
    }

    /// <summary>Your Liked Songs (or favourites) on Spotify: the page opened, its big play button pressed, checked.</summary>
    async Task<string> PlayLiked(CancellationToken cancel)
    {
        string before = _media.Title;
        try { Process.Start(new ProcessStartInfo("spotify:collection:tracks") { UseShellExecute = true }); }
        catch { return "Error: Spotify couldn't be opened."; }
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 40 && hwnd == IntPtr.Zero; i++)
        {
            hwnd = RunningWindows("spotify").Select(w => w.Hwnd).FirstOrDefault();
            if (hwnd == IntPtr.Zero) await Task.Delay(300, cancel);
        }
        if (hwnd == IntPtr.Zero) return "Error: Spotify didn't open.";
        // the page's own play button: "Play"/"Lire" alone, or with the playlist's name (Liked Songs, Titres likés)
        var big = new Regex(@"^(?:Play|Lire|Reproducir|Abspielen|Riproduci|Tocar|Reproduzir)\s*[:：]?\s*(?:Liked Songs|Titres lik[ée]s|Canciones que te gustan|Lieblingssongs|Brani che ti piacciono|M[úu]sicas Curtidas)?$", RegexOptions.IgnoreCase);
        AutomationElement? play = null;
        for (int i = 0; i < 20 && play == null && !cancel.IsCancellationRequested; i++)
        {
            await Task.Delay(400, cancel);
            play = await Limited(() => { foreach (var (el, kind, name) in Elements(hwnd)) if (kind == "button" && big.IsMatch(name)) return el; return null; }, null, 4);
        }
        if (play == null) return "Spotify is open on your Liked Songs, but its play button didn't show up: press play.";
        await Limited(() => Press(play, "button"), "");
        for (int i = 0; i < 20 && !cancel.IsCancellationRequested; i++)
        {
            await Task.Delay(250, cancel);
            if (_media.IsPlaying && _media.Title.Length > 0 && _media.Title != before)
                return $"Done: playing your Liked Songs: {_media.Title}{(_media.Artist.Length > 0 ? " — " + _media.Artist : "")}";
        }
        return "Pressed play on your Liked Songs, but no new song was heard starting. Say it isn't confirmed.";
    }

    /// <summary>The first "Play …" button on Spotify's page (not the one for the song playing now).</summary>
    static (AutomationElement El, string Name)? FirstPlay(IntPtr hwnd)
    {
        try
        {
            foreach (var (el, kind, name) in Elements(hwnd))
                if (kind == "button" && PlayButton.IsMatch(name)) return (el, name);
        }
        catch { }
        return null;
    }

    async Task<string> PlayOnYouTube(string query, CancellationToken cancel)
    {
        string results = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, results);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
            using var res = await PlayHttp.SendAsync(req, cancel);
            string html = await res.Content.ReadAsStringAsync(cancel);
            var m = Regex.Match(html, @"""videoRenderer"":\{""videoId"":""(?<id>[\w-]{11})"".{0,900}?""title"":\{""runs"":\[\{""text"":""(?<title>(?:[^""\\]|\\.)*)""", RegexOptions.Singleline);
            if (m.Success)
            {
                string title = Regex.Unescape(m.Groups["title"].Value);
                OpenLink("https://www.youtube.com/watch?v=" + m.Groups["id"].Value);
                return $"Done: playing “{title}” on YouTube";
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return "Cancelled."; }
        catch { }
        OpenLink(results);
        return $"Done: opened YouTube's results for “{query}” (the first video couldn't be picked automatically)";
    }
}
