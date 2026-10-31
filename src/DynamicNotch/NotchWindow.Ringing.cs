using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// An incoming call, before you answer. Two signals, whichever comes first:
///  • a notification that says it's a call ("Incoming call", "… is calling you", in several languages);
///  • a calling app (Discord, Teams, WhatsApp…) flashing its taskbar button while it plays sound (its ringtone)
///    and you're not on a call yet; a message makes a short ping, a ring keeps sounding.
/// The notch then shows "Incoming call" with the app's icon and gives a soft pulse every second and a half until
/// you answer (the call activity takes over), it stops ringing, or 45 s pass. A click brings the app to the front.
/// </summary>
public partial class NotchWindow
{
    static readonly string[] CallWords =
    {
        "incoming call", "incoming voice call", "incoming video call", "is calling you", "is calling", "calling you",
        "appel entrant", "vous appelle", "llamada entrante", "te está llamando", "chiamata in arrivo", "eingehender anruf",
        "chamada recebida", "مكالمة واردة", "يتصل بك",
    };

    string? _ringPath;              // the app ringing (exe path), when known
    DateTime _ringSince;
    int _ringQuiet;                 // checks in a row where the ringing app made no sound
    readonly DispatcherTimer _ringTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
    bool _ringChecking;

    bool Ringing => _ringTimer.IsEnabled;

    void WireRinging()
    {
        _ringTimer.Tick += async (_, _) => await RingTick();
    }

    static bool LooksLikeCall(string text) =>
        CallWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)) && !text.Contains("missed", StringComparison.OrdinalIgnoreCase);

    /// <summary>From a notification: does it announce a call? (Then it's shown as ringing, not as a message.)</summary>
    bool TryRingFromToast(ToastInfo t)
    {
        if (OnCall || !LooksLikeCall(t.Title + " " + t.Body)) return false;
        string who = !string.IsNullOrWhiteSpace(t.Title) && !LooksLikeCall(t.Title) ? t.Title : t.App;
        StartRinging(null, t.App, who, t.Icon, () => OpenApp(t.Aumid));
        return true;
    }

    /// <summary>
    /// From a taskbar flash: a calling app that also plays sound for a moment is ringing.
    /// Returns true when it was taken as a call (so it isn't also shown as "needs your attention").
    /// </summary>
    async Task<bool> TryRingFromFlash(IntPtr hwnd, string? path)
    {
        if (OnCall || Ringing || path == null || !IsCallApp(path) || IsBrowser(path)) return false;
        // a ringtone keeps sounding; a message ping is over in half a second: listen for 2 s (8 samples),
        // and count it only if it's actually audible in at least half of them (an open but silent stream doesn't count)
        int audible = 0;
        for (int i = 0; i < 8; i++)
        {
            if (await AudioService.AppPeakAsync(path) > 0.004f) audible++;
            await Task.Delay(250);
        }
        if (audible < 4) return false;
        if (OnCall || Ringing) return false;
        var (app, icon) = await DescribeAppAsync(path);
        StartRinging(path, app, app, icon, () => FocusWindow(hwnd));
        return true;
    }

    static bool IsBrowser(string path) =>
        Path.GetFileNameWithoutExtension(path).ToLowerInvariant() is "chrome" or "msedge" or "brave" or "firefox" or "opera" or "vivaldi" or "arc";

    static async Task<bool> PlayingSound(string path) => await AudioService.AppPeakAsync(path) > 0.004f;

    void StartRinging(string? path, string app, string who, ImageSource? icon, Action onClick)
    {
        _ringPath = path;
        _ringSince = DateTime.UtcNow;
        _ringQuiet = 0;
        AppLog.Write($"incoming call: {app}");
        ShowOsd("ring", icon == null ? "" : null, null, "Incoming call", who == app ? app : $"{who} · {app}", null,
            art: icon, glyphBrush: Green, seconds: 45, force: true, prio: Prio.High, motion: Motion.DoublePulse, onClick: () => { StopRinging(); onClick(); });
        _ringTimer.Start();
    }

    /// <summary>Every 1.5 s while it rings: a soft pulse; stops when answered, when it goes quiet, or after 45 s.</summary>
    async Task RingTick()
    {
        if (_ringChecking) return;
        if (OnCall || DateTime.UtcNow - _ringSince > TimeSpan.FromSeconds(45)) { StopRinging(); return; }
        if (_mode == Mode.Osd && _osd?.Kind == "ring") Kick();
        if (_ringPath == null) return; // from a notification: it lasts as long as the alert
        _ringChecking = true;
        try { _ringQuiet = await PlayingSound(_ringPath) ? 0 : _ringQuiet + 1; }
        finally { _ringChecking = false; }
        if (_ringQuiet >= 3) StopRinging(); // quiet for ~4.5 s (longer than the gaps in a ringtone): declined, or the caller hung up
    }

    void StopRinging()
    {
        if (!Ringing) return;
        _ringTimer.Stop();
        _ringPath = null;
        if (_mode == Mode.Osd && _osd?.Kind == "ring") NextOsd();
    }
}
