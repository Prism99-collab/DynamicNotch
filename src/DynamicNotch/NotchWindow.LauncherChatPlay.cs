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
    static readonly Regex PlayWords = new(@"^(?<verb>play|put on|put|throw on|start playing|listen to|joue|jouer|mets|mettre|lance|écoute|ecoute|reproduce|pon|spiel|suona|run|start|launch|blast|queue up|shgel|tla9)\s+(?:me\s+|moi\s+|nous\s+|us\s+)?(?<q>.+?)(?:[\s,]+(?:but\s+|mais\s+|f\s+)?(?:on|in|sur|en|dans|auf|with|using|via|from|f)\s+(?<app>spotify|youtube|yt))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
        q = Regex.Replace(q, @"^(?:some|a|an|the|du|de la|des|de l'|de|la|le|les|un|une|del|el|los)\s+", "", RegexOptions.IgnoreCase).Trim();
        // "chill sound beats like lofi": what it's like is the better search
        if (Regex.Match(q, @"\b(?:like|kind of|style|genre|type|comme|style de|genre de)\s+(?<like>.{3,})$", RegexOptions.IgnoreCase) is { Success: true } lk) q = (Regex.Replace(q[..lk.Index], @"\b(?:sound|sounds|music|musique|songs?|tracks?|beats?)\b", "", RegexOptions.IgnoreCase).Trim() + " " + lk.Groups["like"].Value).Trim();
        if (q.Length < 2 || Regex.IsMatch(q, @"^(music|musique|something|smth|it|the music|again|pause|next|previous)$", RegexOptions.IgnoreCase)) return null;
        // "lance spotify" opens the app, it doesn't search for it
        try { if (ResolveEntry(q) is { Kind: LauncherIndex.Kind.App } named && Norm(named.Name).StartsWith(Norm(q)) && Norm(q).Length >= 3) return null; } catch { }
        string app = m.Groups["app"].Value.ToLowerInvariant();
        string where = app is "youtube" or "yt" ? "youtube" : app == "spotify" || HasSpotify() ? "spotify" : "youtube";
        return (q, where);
    }

    async Task<string> PlayMusic(string query, string where, CancellationToken cancel = default)
    {
        query = query.Trim();
        if (query.Length == 0) return "Error: what should play?";
        where = where.Trim().ToLowerInvariant();
        if (where is "" or "auto") where = HasSpotify() ? "spotify" : "youtube";
        return where == "spotify" ? await PlayOnSpotify(query, cancel) : await PlayOnYouTube(query, cancel);
    }

    async Task<string> PlayOnSpotify(string query, CancellationToken cancel)
    {
        string before = _media.Title;
        // what was on the screen before: the new search is told apart from it
        var win0 = RunningWindows("spotify").Select(w => w.Hwnd).FirstOrDefault();
        string? firstBefore = win0 != IntPtr.Zero ? await Limited(() => FirstPlay(win0)?.Name, null) : null;
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

        // the results of this search: the first play button that wasn't there before (or, after a moment, the first one)
        (AutomationElement El, string Name)? hit = null;
        var since = DateTime.UtcNow;
        while ((DateTime.UtcNow - since).TotalSeconds < 9 && !cancel.IsCancellationRequested)
        {
            await Task.Delay(350, cancel);
            var found = await Limited(() => FirstPlay(hwnd), null, 4);
            if (found == null) continue;
            if (found.Value.Name != firstBefore || (DateTime.UtcNow - since).TotalSeconds > 2.5) { hit = found; break; }
        }
        if (hit == null) return $"Spotify is open on the search for “{query}”, but its play button didn't show up: press play on the top result.";
        string what = PlayButton.Match(hit.Value.Name) is { Success: true } pm ? pm.Groups["what"].Value : hit.Value.Name;
        string pressed = await Limited(() => Press(hit.Value.El, "button"), "Error: Spotify didn't respond.");
        if (pressed.StartsWith("Error")) return $"Spotify shows “{what}” but it couldn't be started: press play.";

        // done only when it's really playing
        for (int i = 0; i < 20 && !cancel.IsCancellationRequested; i++)
        {
            await Task.Delay(250, cancel);
            if (_media.IsPlaying && _media.Title.Length > 0 && (_media.Title != before || what.Contains(_media.Title, StringComparison.OrdinalIgnoreCase)))
                return $"Done: playing {_media.Title}{(_media.Artist.Length > 0 ? " — " + _media.Artist : "")} on Spotify";
        }
        return $"Done: started “{what}” on Spotify";
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
