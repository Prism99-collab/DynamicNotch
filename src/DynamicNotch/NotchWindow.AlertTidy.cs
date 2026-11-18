using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Notifications tidied before they're shown:
///  • the same one twice (apps and Windows often re-send) is shown once;
///  • Windows' own wordy alerts get a few words instead ("Capture d'écran copiée dans le Presse-papiers · Enregistrement
///    automatique dans le dossier…" → the notch's own screenshot alert, once); a screenshot or a copy the notch already
///    announces isn't announced again;
///  • any other long alert from an app (not a message from a person: those stay word for word, and stay on this PC) is
///    put in a few words by the AI, when a Google key is set. It shows the short local version at once and swaps in
///    the AI's words a moment later; the words are kept, so the same alert next time is instant and costs nothing.
/// </summary>
public partial class NotchWindow
{
    readonly Dictionary<string, DateTime> _seenToasts = new();
    DateTime _screenshotToastAt;

    /// <summary>Seen this exact one in the last minute and a half? (then it isn't shown again)</summary>
    bool SeenToast(ToastInfo t)
    {
        var now = DateTime.UtcNow;
        string key = (t.App + "|" + t.Title + "|" + t.Body).ToLowerInvariant();
        foreach (var old in _seenToasts.Where(kv => (now - kv.Value).TotalSeconds > 90).Select(kv => kv.Key).ToList()) _seenToasts.Remove(old);
        if (_seenToasts.ContainsKey(key)) return true;
        _seenToasts[key] = now;
        return false;
    }

    static readonly Regex ScreenshotToast = new(@"capture d.[ée]cran|screenshot|screen ?shot|captura( de pantalla)?|bildschirmfoto|schermata|snip", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CopiedToast = new(@"(copied|copi[ée]e?s?|copiad[oa]|kopiert|copiat[oa])\b.*(clipboard|presse.papiers|portapapeles|zwischenablage|appunti)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Windows' wordy alerts, in a few words (English, French, Spanish, German): null when it isn't one of them.</summary>
    static (string Title, string? Sub)? KnownAlert(string app, string all)
    {
        (string, string?)? M(string pattern, string title, string? sub = null) =>
            Regex.IsMatch(all, pattern, RegexOptions.IgnoreCase) ? (Loc.T(title), sub == null ? null : Loc.T(sub)) : null;
        return M(@"(game )?clip (recorded|enregistr|grabado|aufgezeichnet)|enregistrement de jeu", "Clip saved")
            ?? M(@"usb.*(not recognized|non reconnu|no reconocido|nicht erkannt)|(not recognized|non reconnu).*usb", "USB not recognized", "Try another port")
            ?? M(@"(restart|red[ée]marr|reinici|neu ?start).*(update|mise à jour|actualiza|aktualisier)|(update|mise à jour|actualiza).*(restart|red[ée]marr|reinici|neustart)", "Update ready", "Restart to finish")
            ?? M(@"(threats?|menaces?|amenazas?|bedrohung).*(found|d[ée]tect|encontrad|gefunden)", "Threat found", "Click to review")
            ?? M(@"(no threats|aucune menace|ninguna amenaza|keine bedrohung)", "Scan clean", "No threats")
            ?? M(@"(low (disk|storage)|running out of (disk )?space|espace disque (insuffisant|faible)|poco espacio|wenig speicher)", "Disk almost full")
            ?? M(@"(battery saver|[ée]conomiseur (de batterie|d.[ée]nergie)|ahorro de bater[ií]a|energiesparmodus).*(on|activ|aktiv)", "Battery saver on")
            ?? M(@"(ready to pair|pr[êe]t.*(associ|coupl)|listo para emparejar)", "Ready to pair")
            ?? M(@"(was|has been|is now) installed|a [ée]t[ée] install[ée]|se ha instalado|wurde installiert", "Installed")
            ?? M(@"(was|has been) updated|a [ée]t[ée] mis(e)? à jour|se ha actualizado|wurde aktualisiert", "Updated")
            ?? M(@"(driver|pilote|controlador|treiber).*(install|ready|prêt|listo|bereit)", "Device ready");
    }

    /// <summary>
    /// What happens to a notification before <see cref="ShowToast"/>: true when it was handled here (dropped as a repeat,
    /// told as the notch's own alert, or shown in a few words).
    /// </summary>
    (bool Drop, Action? Show) TidyToast(ToastInfo t)
    {
        if (SeenToast(t)) return (true, null);
        string all = Clean(t.Title) + " · " + Clean(t.Body);

        // a screenshot: the notch's own screenshot alert says it (with the picture), once
        if (ScreenshotToast.IsMatch(all) && (CopiedToast.IsMatch(all) || Regex.IsMatch(all, @"saved|enregistr|guardad|gespeichert|dossier|folder|carpeta", RegexOptions.IgnoreCase)))
        {
            _screenshotToastAt = DateTime.UtcNow;
            if (_osd?.Kind == "screenshot" || (DateTime.UtcNow - _lastScreenshotAt).TotalSeconds < 8) return (true, null);
            _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                // saved meanwhile? that alert already showed it; else it was only copied
                if ((DateTime.UtcNow - _lastScreenshotAt).TotalSeconds < 8 || _osd?.Kind == "screenshot") return;
                ShowOsd("screenshot", "", null, Loc.T("Screenshot copied"), null, null, glyphBrush: Blue, seconds: 1.8, prio: Prio.Low, onClick: () => ExpandTo(3));
            }));
            return (true, null);
        }
        // "Copied to clipboard" from some app: the notch's "Copied" already said it
        if (CopiedToast.IsMatch(all) && all.Length < 140) return (true, null);
        // "Your microphone/camera is being used by…": the notch's privacy dot already shows it
        if (Regex.IsMatch(all, @"(microphone|micro|camera|cam[ée]ra|c[áa]mara|kamera|webcam)\b.*(in use|being used|utilis|en uso|verwendet)", RegexOptions.IgnoreCase)) return (true, null);

        if (FindCode(all) != null) return (false, null); // a sign-in code has its own alert
        string app = Clean(t.App);
        if (KnownAlert(app, all) is { } known)
        {
            return (false, () => ToastWords(t, known.Title, known.Sub ?? (app.Length > 0 && !known.Title.Contains(app, StringComparison.OrdinalIgnoreCase) ? app : null)));
        }

        // long words from an app (not a person's message): a few words, by the AI
        if (Personal(t.App) || all.Length < 80 || !_settings.AiAlerts || !OpenRouter.IsGemini) return (false, null);
        string key = Regex.Replace(app + "|" + all, @"\s+", " ").ToLowerInvariant();
        if (AlertWords.TryGetValue(key, out var kept) && kept.Length >= 1)
        {
            return (false, () => ToastWords(t, kept[0], kept.Length > 1 && kept[1].Length > 0 ? kept[1] : null));
        }
        return (false, () => { ShowToast(t); _ = ShortenLater(t, key, app, all); }); // the usual way now; the AI's words replace it when they come
    }

    /// <summary>Messages from people (chats, mail): word for word, and never sent anywhere.</summary>
    static bool Personal(string app) =>
        Regex.IsMatch(app, @"whatsapp|telegram|discord|signal|messenger|instagram|teams|slack|skype|mail|outlook|gmail|thunderbird|sms|messages|phone link|lien avec votre t[ée]l[ée]phone|your phone|wechat|line|viber|snapchat|facebook|x\.com|twitter|reddit", RegexOptions.IgnoreCase);

    void ToastWords(ToastInfo t, string title, string? sub)
    {
        var brand = BrandColor(t.App);
        System.Windows.Media.Color? accent = brand ?? (t.Icon != null ? IconHelper.Accent(t.Icon) is { } ic ? Vivid(ic) : null : null);
        ShowOsd("toast:" + t.App, t.Icon == null ? "" : null, null, title, sub, null, art: t.Icon,
            glyphBrush: accent is { } a ? Frozen(a) : null, seconds: 3.4, prio: Prio.High, onClick: () => OpenApp(t.Aumid),
            motion: SoundsLikeFailure(title) ? Motion.Tremble : Motion.Auto);
    }

    // ── the AI's words, kept on this PC ──

    static readonly string AlertWordsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicNotch", "alert-words.json");
    static Dictionary<string, string[]>? _alertWords;
    static int _alertAsks;
    static DateTime _alertAsksDay;

    static Dictionary<string, string[]> AlertWords
    {
        get
        {
            if (_alertWords != null) return _alertWords;
            try { _alertWords = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(AlertWordsFile)); } catch { }
            return _alertWords ??= new();
        }
    }

    async Task ShortenLater(ToastInfo t, string key, string app, string all)
    {
        // at most 40 a day: alerts never eat the chat's free requests
        if (_alertAsksDay != DateTime.Today) { _alertAsksDay = DateTime.Today; _alertAsks = 0; }
        if (++_alertAsks > 40) return;
        string? apiKey = OpenRouter.LoadKey();
        if (apiKey == null) return;
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] =
                "You make a Windows notification minimal for a small notch pop-up. Reply with only JSON: {\"title\":\"...\",\"sub\":\"...\"}. " +
                "title: at most 4 words, what happened. sub: at most 6 words, only what's useful (a name, a number, what to do), or \"\". " +
                "Same language as the notification. No app boilerplate, no ending period, no emojis." },
            new JsonObject { ["role"] = "user", ["content"] = $"App: {app}\n{(all.Length > 300 ? all[..300] : all)}" },
        };
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var turn = await OpenRouter.Stream(apiKey, OpenRouter.DefaultModel, messages, null, _ => { }, null, cts.Token, temperature: 0.2);
            if (turn.Error != null || Regex.Match(turn.Text, @"\{[\s\S]*\}") is not { Success: true } m) return;
            var json = JsonNode.Parse(m.Value);
            string title = json?["title"]?.ToString().Trim().TrimEnd('.') ?? "", sub = json?["sub"]?.ToString().Trim().TrimEnd('.') ?? "";
            if (title.Length == 0 || title.Length > 48) return;
            if (sub.Length > 60) sub = Short(sub, 60);
            var words = AlertWords;
            words[key] = new[] { title, sub };
            while (words.Count > 400) words.Remove(words.Keys.First());
            try { Directory.CreateDirectory(Path.GetDirectoryName(AlertWordsFile)!); File.WriteAllText(AlertWordsFile, JsonSerializer.Serialize(words)); } catch { }
            // still on screen: the short words take its place, quietly
            if (_mode == Mode.Osd && _osd?.Kind == "toast:" + t.App && (DateTime.UtcNow - _osdShownAt).TotalSeconds < _osd.Seconds - 0.8)
                ShowOsd("toast:" + t.App, t.Icon == null ? "" : null, null, title, sub.Length > 0 ? sub : null, _osd.Right, art: t.Icon,
                    glyphBrush: _osd.GlyphBrush, seconds: Math.Max(2.2, _osd.Seconds - (DateTime.UtcNow - _osdShownAt).TotalSeconds), prio: Prio.High,
                    onClick: _osd.OnClick, motion: Motion.None);
        }
        catch { }
    }
}
