using DynamicNotch.Controls;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// What the companion understands, so its visits fit you:
///  • What you're doing (the app in front): coding, it keeps you quiet company; a document, it reads along; a video,
///    it watches with you; a design tool, it's impressed; browsing, it's curious. In a meeting, a call, presenting or
///    focus mode it doesn't come down at all, and while you type fast it waits.
///  • How much you want it: smiling at it or poking it brings it back sooner; ignoring it while you work, or sending it
///    up by reaching for the notch, makes it come less often. It remembers (Settings: CompanionLike).
///  • The mood of your day: playful in the evening and at weekends, calm and gentle after a long stretch of work,
///    tired when the PC has been overloaded or the battery is low, sleepy late at night.
/// All of it is read from what Pixo already knows (the app in front, the clock, the battery, the overload log): no
/// extra work in the background, no network.
/// </summary>
public partial class NotchWindow
{
    enum Doing { Other, Code, Docs, Video, Design, Browse }

    sealed record CScene(double Secs, Action<NotchFace, Func<bool>> Play);

    bool _cEngaged;
    DateTime _cArrivedAt;

    static readonly string[] CodeApps = { "devenv", "code", "cursor", "rider64", "idea64", "pycharm64", "webstorm64", "clion64", "goland64", "sublime_text", "notepad++", "zed", "windowsterminal", "wt", "powershell", "pwsh", "cmd", "androidstudio64", "studio64" };
    static readonly string[] DocApps = { "winword", "excel", "powerpnt", "onenote", "notion", "obsidian", "notepad", "acrobat", "acrord32", "wps", "libreoffice", "soffice", "typora" };
    static readonly string[] VideoApps = { "vlc", "mpc-hc64", "mpc-be64", "potplayermini64", "netflix", "disneyplus", "primevideo", "wmplayer", "mediaplayer", "microsoft.media.player" };
    static readonly string[] DesignApps = { "figma", "photoshop", "illustrator", "afterfx", "premiere pro", "adobe premiere pro", "blender", "inkscape", "gimp-2.10", "krita", "canva", "resolve", "davinci resolve" };
    static readonly string[] Browsers = { "chrome", "msedge", "brave", "firefox", "opera", "vivaldi", "arc" };

    /// <summary>What you're doing, from the app in front (and a browser's tab title).</summary>
    Doing WhatYoureDoing()
    {
        string name = (ForegroundName() ?? "").ToLowerInvariant();
        if (name.Length == 0) return Doing.Other;
        if (CodeApps.Contains(name)) return Doing.Code;
        if (DocApps.Contains(name)) return Doing.Docs;
        if (VideoApps.Contains(name)) return Doing.Video;
        if (DesignApps.Contains(name)) return Doing.Design;
        if (Browsers.Contains(name))
        {
            string title = WindowTitle(GetForegroundWindow()).ToLowerInvariant();
            if (title.Contains("youtube") || title.Contains("netflix") || title.Contains("twitch") || title.Contains("prime video") || title.Contains("disney+")) return Doing.Video;
            if (title.Contains("figma") || title.Contains("canva")) return Doing.Design;
            if (title.Contains("google docs") || title.Contains("word") && title.Contains("online") || title.Contains("notion")) return Doing.Docs;
            if (title.Contains("github") || title.Contains("stack overflow") || title.Contains("localhost")) return Doing.Code;
            return Doing.Browse;
        }
        return Doing.Other;
    }

    /// <summary>Not now: a meeting, a call, presenting, focus mode, or you're typing away.</summary>
    bool CompanionBusy() =>
        _inMeeting || _presenter || OnCall || _focus.IsOn || _faceKeys.Count(k => k) >= 2;

    // ───── the mood of your day ─────

    bool Playful
    {
        get
        {
            var now = DateTime.Now;
            return now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || now.DayOfWeek == DayOfWeek.Friday && now.Hour >= 18 || now.Hour is >= 19 and < 23;
        }
    }

    bool LongSession => _activeSince is { } since && (DateTime.Now - since).TotalMinutes >= 90;

    bool PcTired => StrainLog.Events.Any(e => (DateTime.UtcNow - e.At.ToUniversalTime()).TotalMinutes < 10) || PalLowBattery;

    /// <summary>The wait until the next visit: about a minute or two, longer if you'd rather it came less, shorter if you enjoy it.</summary>
    TimeSpan CompanionGap()
    {
        double secs = 55 + Random.Shared.NextDouble() * 50;
        double like = Math.Clamp(_settings.CompanionLike, -10, 10);
        secs *= Math.Clamp(1 - like * 0.08, 0.6, 2.4);
        if (Playful) secs *= 0.8;
        if (LongSession) secs *= 1.4; // deep in work: fewer visits
        return TimeSpan.FromSeconds(secs);
    }

    /// <summary>After a visit: did you enjoy it (smiled, poked) or carry on without it while working?</summary>
    void CompanionLearn(bool shooed)
    {
        int like = _settings.CompanionLike;
        bool workedThrough = IdleTime() < DateTime.UtcNow - _cArrivedAt; // input came while it was out
        if (_cEngaged) like++;
        else if (shooed) like -= 2;
        else if (workedThrough) like--;
        like = Math.Clamp(like, -10, 10);
        if (like == _settings.CompanionLike) return;
        _settings.CompanionLike = like;
        _settings.Save();
    }

    // ───── the scene ─────

    /// <summary>What it does on this visit: what you're doing first, then the mood of the day, then a bit of chance.</summary>
    CScene CompanionPick()
    {
        var doing = WhatYoureDoing();
        int hour = DateTime.Now.Hour;
        static CScene Two(NotchFace.Emote a, NotchFace.Emote? b, double secs) => new(secs, (f, alive) =>
        {
            f.Play(a, 1.8);
            if (b is { } then) _ = Later(2000, () => { if (alive()) f.Play(then, 1.6); });
        });

        if (PcTired)
            return new(4.5, (f, alive) => { f.Play(NotchFace.Emote.Tired, 2.6); _ = Later(2800, () => { if (alive()) f.Play(NotchFace.Emote.Happy, 1.2); }); });
        if (hour >= 23 || hour < 5)
            return Two(NotchFace.Emote.Yawn, NotchFace.Emote.Wink, 5);

        switch (doing)
        {
            case Doing.Code:
                // quiet company: it watches you work, a small nod, a wink as it leaves
                return new(4.5, (f, alive) =>
                {
                    f.SetMood(NotchFace.Mood.Focus);
                    _ = Later(1600, () => { if (alive()) f.Hop(.25); });
                    _ = Later(3300, () => { if (alive()) { f.SetMood(NotchFace.Mood.Idle); f.Play(NotchFace.Emote.Wink, .9); } });
                });
            case Doing.Docs:
                // it reads along, line by line, then smiles
                return new(5.5, (f, alive) =>
                {
                    for (int line = 0; line < 2; line++)
                        for (int i = 0; i <= 6; i++)
                        {
                            double x = -0.8 + i * 0.27, y = -0.3 - line * 0.25;
                            int at = line * 1500 + i * 190;
                            _ = Later(at, () => { if (alive()) f.LookAt(x, y); });
                        }
                    _ = Later(3300, () => { if (alive()) f.Play(NotchFace.Emote.Happy, 1.4); });
                });
            case Doing.Video:
                // it watches with you (eyes on the screen), a little "oh!" now and then
                return new(6.5, (f, alive) =>
                {
                    for (int i = 0; i < 20; i++) { int at = i * 300; _ = Later(at, () => { if (alive()) f.LookAt(0, -0.7); }); }
                    _ = Later(3000, () => { if (alive()) f.Play(NotchFace.Emote.Surprised, .9); });
                });
            case Doing.Design:
                return Two(NotchFace.Emote.Proud, NotchFace.Emote.Happy, 5); // impressed
        }

        if (_media.IsPlaying) return new(6.5, (f, _) => f.SetMood(NotchFace.Mood.Music)); // music: it dances
        if (LongSession)
            // a long stretch of work: calm, a stretch, a soft smile
            return new(4.5, (f, alive) => { f.Fidget(); _ = Later(1800, () => { if (alive()) f.Play(NotchFace.Emote.Happy, 1.4); }); });
        if (hour < 11 && Random.Shared.Next(3) == 0) return Two(NotchFace.Emote.Hello, NotchFace.Emote.Happy, 5);
        if (Playful)
            return Random.Shared.Next(4) switch
            {
                0 => Two(NotchFace.Emote.Excited, NotchFace.Emote.Love, 5),
                1 => Two(NotchFace.Emote.Wave, NotchFace.Emote.Wink, 5),
                2 => Two(NotchFace.Emote.Love, NotchFace.Emote.Excited, 5),
                _ => Two(NotchFace.Emote.Hello, NotchFace.Emote.Proud, 5),
            };
        if (doing == Doing.Browse) return Two(NotchFace.Emote.Surprised, NotchFace.Emote.Happy, 5); // curious
        return Random.Shared.Next(5) switch
        {
            0 => Two(NotchFace.Emote.Hello, NotchFace.Emote.Wink, 5),
            1 => Two(NotchFace.Emote.Surprised, NotchFace.Emote.Happy, 5),
            2 => Two(NotchFace.Emote.Proud, NotchFace.Emote.Happy, 5),
            3 => Two(NotchFace.Emote.Happy, NotchFace.Emote.Wave, 5.5),
            _ => new(4.5, (f, _) => f.Fidget()),
        };
    }

    /// <summary>Runs an action on the UI thread after a delay (for the scenes' timing).</summary>
    static async Task Later(int ms, Action act)
    {
        await Task.Delay(ms);
        act();
    }
}
