using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicNotch;

/// <summary>User preferences, stored as JSON in %APPDATA%\DynamicNotch\settings.json.</summary>
public sealed class Settings
{
    public bool MinimalWhenIdle { get; set; } = true;
    public bool EcoOnBattery { get; set; } = true;
    public bool NotificationAlerts { get; set; } = true;
    public bool ClipboardAlerts { get; set; } = true;
    public bool SystemAlerts { get; set; } = true;
    /// <summary>"Opening Spotify" when an app starts.</summary>
    public bool AppLaunchAlerts { get; set; } = true;
    public bool ShowMediaActivity { get; set; } = true;
    public bool AnimatedVisualizer { get; set; } = true;
    public bool HoverToOpen { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    /// <summary>Game mode: a tiny frame-rate pill while a game is in front.</summary>
    public bool GameStats { get; set; } = true;
    public bool SlimOverWindows { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool KeepRunning { get; set; } = true;
    /// <summary>Dragging a file onto the notch keeps it on the shelf.</summary>
    public bool ShelfDrop { get; set; } = true;
    /// <summary>The notch shows things but reacts to nothing (unlocked from the tray icon).</summary>
    public bool Locked { get; set; }
    /// <summary>The neon skin (the yellow button in the control center).</summary>
    public bool Neon { get; set; }
    /// <summary>The same switch under its name in settings saved before 1.8.1: read, never written again.</summary>
    [JsonPropertyName("Cyberpunk"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacyNeon { get => false; set { if (value) Neon = true; } }
    /// <summary>"original", "neon", "liquid", "crystal" or "abyss".</summary>
    public string Theme { get; set; } = "original";
    /// <summary>Strain warnings ("Overload risk" in Neon) and what they watch.</summary>
    public bool StrainWarnings { get; set; } = true;
    public bool StrainCpu { get; set; } = true;
    public bool StrainGpu { get; set; } = true;
    public bool StrainRam { get; set; } = true;
    public bool StrainGpuTemp { get; set; } = true;
    public bool StrainCpuTemp { get; set; }
    /// <summary>Where each strain target's line is (% or °C).</summary>
    public double StrainCpuLine { get; set; } = 90;
    public double StrainGpuLine { get; set; } = 95;
    public double StrainRamLine { get; set; } = 90;
    public double StrainGpuTempLine { get; set; } = 85;
    public double StrainCpuTempLine { get; set; } = 90;
    /// <summary>The FPS pill's extras (off by default) and their colours (#RRGGBB; null = soft white).</summary>
    public bool FpsCpu { get; set; }
    public bool FpsGpu { get; set; }
    public bool FpsCpuTemp { get; set; }
    public bool FpsGpuTemp { get; set; }
    public string? FpsCpuColor { get; set; }
    public string? FpsGpuColor { get; set; }
    /// <summary>The whole FPS pill's size in games (0.85 small, 1 normal, 1.2 large, 1.4 extra large).</summary>
    public double FpsScale { get; set; } = 1;
    /// <summary>Your colours from the Colors page (#RRGGBB); null = the notch decides.</summary>
    public string? ColorAccent { get; set; }
    public string? ColorGlow { get; set; }
    public string? ColorEdge { get; set; }
    public string? ColorClock { get; set; }
    /// <summary>The notch's own body (kept dark, so text stays readable).</summary>
    public string? ColorPanel { get; set; }
    /// <summary>The side pills' outline and glow (instead of each activity's own colour).</summary>
    public string? ColorPills { get; set; }
    /// <summary>"en", "fr" or "ar".</summary>
    public string Language { get; set; } = "en";
    /// <summary>Control-center pages switched off in Preferences (1 System, 2 Tools, 3 Clipboard &amp; Files, 4 Colors).</summary>
    public List<int> HiddenPages { get; set; } = new();
    /// <summary>The first-start hello was shown (a new install says where the notch is and how to open it).</summary>
    public bool Welcomed { get; set; }

    // Smart page: every helper can be switched off; meeting mode is opt-in
    public bool SmartBatteryDrain { get; set; } = true;
    public bool SmartChargeReminder { get; set; } = true;
    public bool SmartConnection { get; set; } = true;
    public bool SmartDownloads { get; set; } = true;
    public bool SmartBreaks { get; set; } = true;
    public int BreakMinutes { get; set; } = 90;
    public bool MeetingMode { get; set; }

    /// <summary>The little hacker who drops by on her swing.</summary>
    public bool Companion { get; set; } = true;
    public bool CompanionInCalls { get; set; } = true;
    /// <summary>The little face that lives in the idle notch.</summary>
    public bool NotchFace { get; set; } = true;
    /// <summary>The face's hat: auto (the season's), none, beanie, cap, party, crown, santa, witch.</summary>
    public string FaceOutfit { get; set; } = "auto";
    /// <summary>Unused for 5 s, the idle notch and the music pill tuck in small.</summary>
    public bool CompactWhenResting { get; set; } = true;
    /// <summary>The side pills tuck into the notch as a small light, sliding out when they start or the pointer is near.</summary>
    public bool TuckPills { get; set; } = true;
    /// <summary>The face's little sounds (hello, a poke, an answer done).</summary>
    public bool FaceSounds { get; set; } = true;
    /// <summary>The companion's size: 0.85 small, 1 medium, 1.2 large.</summary>
    public double CompanionSize { get; set; } = 1.0;
    public DateTime? LastDownloadsNag { get; set; }

    // ── calls (the Calls page)
    /// <summary>Opening the notch during a call lands on the Calls page.</summary>
    public bool CallsOpenOnCall { get; set; } = true;
    /// <summary>Camera, share, hang up and raise hand press the call app's own shortcut (Teams, Zoom, Meet).</summary>
    public bool CallShortcuts { get; set; } = true;
    /// <summary>When a call starts: pause the music, quiet the other apps, hold notifications.</summary>
    public bool CallPauseMusic { get; set; }
    public bool CallQuietOthers { get; set; }
    public bool CallFocus { get; set; }
    /// <summary>Presenter mode also hides the desktop icons.</summary>
    public bool PresenterHidesIcons { get; set; } = true;
    /// <summary>A short list of past calls (app, when, how long) on the Calls page.</summary>
    public bool CallHistory { get; set; } = true;
    /// <summary>One-click messages for the call's chat.</summary>
    public List<string> CallReplies { get; set; } = new() { "Be right back", "Can you hear me?", "One sec, I'm muted", "I'll call you back", "Sharing my screen now" };

    // ── the search
    /// <summary>The key that opens the search: "alt-space", "ctrl-space", "ctrl-alt-space", "win-alt-space" or "off".</summary>
    public string SearchHotkey { get; set; } = "alt-space";
    /// <summary>With the notch open and the pointer on it, typing opens the search.</summary>
    public bool SearchTypeToOpen { get; set; } = true;
    /// <summary>The magnifier in the control center's header.</summary>
    public bool SearchButton { get; set; } = true;
    /// <summary>The empty search: your most-used apps, recent searches, things to try.</summary>
    public bool SearchFrequent { get; set; } = true;
    public bool SearchRecent { get; set; } = true;
    public bool SearchTry { get; set; } = true;
    /// <summary>Apps taken out of "Frequent" (their Start menu ids).</summary>
    public List<string> SearchHiddenApps { get; set; } = new();
    /// <summary>Files anywhere on the PC from Windows' index, and the words inside documents.</summary>
    public bool SearchWholePc { get; set; } = true;
    public bool SearchInsideFiles { get; set; } = true;
    /// <summary>Open windows, and what you copied or kept on the shelf.</summary>
    public bool SearchWindows { get; set; } = true;
    public bool SearchClipboard { get; set; } = true;
    /// <summary>Plain-language commands ("volume 30", "remind me…") and quick answers (sums, time zones…).</summary>
    public bool SearchCommands { get; set; } = true;
    /// <summary>What you pick for what you type moves to the top.</summary>
    public bool SearchLearn { get; set; } = true;
    /// <summary>The web search at the bottom: "google", "bing", "duckduckgo" or "brave".</summary>
    public string SearchEngine { get; set; } = "google";
    /// <summary>The AI assistant offered for questions: "chatgpt", "claude", "perplexity", "copilot" or "off".</summary>
    public string SearchAssistant { get; set; } = "chatgpt";
    /// <summary>The AI chat inside the search (OpenRouter; the key is kept apart, encrypted).</summary>
    public bool AiChat { get; set; } = true;
    /// <summary>The Work page: Recent folded away; what you work on not noted (paused).</summary>
    public bool WorkRecentHidden { get; set; }
    public bool WorkPaused { get; set; }
    /// <summary>The OpenRouter model id ("openrouter/free" picks a free one).</summary>
    public string AiModel { get; set; } = "openrouter/free";
    /// <summary>It may act on the PC (commands, files, clipboard, opening things).</summary>
    public bool AiTools { get; set; } = true;
    /// <summary>It may search the web and read pages.</summary>
    public bool AiWeb { get; set; } = true;
    /// <summary>Chats are kept on the PC to come back to.</summary>
    public bool AiHistory { get; set; } = true;
    /// <summary>Asking from the search goes on with the last chat when it was used in the last 30 minutes.</summary>
    public bool AiContinue { get; set; } = true;
    /// <summary>How it answers: "brief", "balanced" or "detailed".</summary>
    public string AiStyle { get; set; } = "brief";
    /// <summary>"precise", "balanced" or "creative" (the model's temperature).</summary>
    public string AiCreativity { get; set; } = "balanced";
    /// <summary>What it should always know or do (the user's own words).</summary>
    public string AiInstructions { get; set; } = "";
    /// <summary>How much taller the chat was pulled in the notch (px).</summary>
    public double AiChatExtra { get; set; }
    /// <summary>Where the popped-out chat window was ("x,y,w,h"), and whether it stays on top.</summary>
    public string AiPopBounds { get; set; } = "";
    public bool AiPopTopmost { get; set; } = true;
    /// <summary>Risky commands (deleting, installing, changing settings...) wait for your OK.</summary>
    public bool AiConfirm { get; set; } = true;
    /// <summary>What waits for your OK: "risky" (deleting, installing, killing, downloads, registry, security), "changes" (anything that changes something) or "never".</summary>
    public string AiAsk { get; set; } = "risky";
    /// <summary>Talking to the AI (the mic, Ctrl+M) and hearing its answers.</summary>
    public bool AiVoice { get; set; } = true;
    /// <summary>"windows" (free, words appear as you speak) or "openrouter" (needs credit, any language).</summary>
    public string AiVoiceEngine { get; set; } = "windows";
    /// <summary>The language you speak ("auto": Windows' speech language), e.g. "fr-FR".</summary>
    public string AiVoiceLanguage { get; set; } = "auto";
    /// <summary>What you say is sent as soon as you stop talking.</summary>
    public bool AiVoiceSend { get; set; } = true;
    /// <summary>Answers read aloud: "voice" (when you talked), "always" or "never".</summary>
    public string AiSpeak { get; set; } = "voice";
    /// <summary>The Windows voice that reads (empty: the default one), and how fast.</summary>
    public string AiVoiceId { get; set; } = "";
    public double AiVoiceRate { get; set; } = 1.0;
    /// <summary>The model that makes pictures (empty: a free one if any, else the cheapest).</summary>
    public string AiImageModel { get; set; } = "";
    /// <summary>The engine the AI searches with first: "google", "brave" or "duckduckgo".</summary>
    public string AiSearchEngine { get; set; } = "google";
    /// <summary>The AI's provider: "gemini" (Google, free, the default) or "openrouter".</summary>
    public string AiProvider { get; set; } = "gemini";
    /// <summary>The Gemini model ("openrouter/free" = Auto: the best that still has free requests today).</summary>
    public string AiGeminiModel { get; set; } = "openrouter/free";
    /// <summary>It keeps short facts about you (what you tell it to remember) and uses them.</summary>
    public bool AiMemory { get; set; } = true;
    /// <summary>Long alerts from apps put in a few words by the AI (never messages from people).</summary>
    public bool AiAlerts { get; set; } = true;
    /// <summary>The free Groq key was offered once (after Google was connected).</summary>
    public bool AiGroqAsked { get; set; }
    /// <summary>Chats older than this many days are deleted (0: keep the newest 60, whatever their age).</summary>
    public int AiKeepDays { get; set; }

    /// <summary>%APPDATA%\Pixo. The first time, the old %APPDATA%\DynamicNotch moves there whole (settings, keys, history).</summary>
    public static string Folder => _folder ??= FindFolder();
    static string? _folder;

    static string FindFolder()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string now = Path.Combine(roaming, "Pixo"), old = Path.Combine(roaming, "DynamicNotch");
        try { if (!Directory.Exists(now) && Directory.Exists(old)) Directory.Move(old, now); } catch { }
        // a move that couldn't happen (a file in use): keep using the old folder, nothing is lost
        return Directory.Exists(now) || !Directory.Exists(old) ? now : old;
    }

    static string FilePath => Path.Combine(Folder, "settings.json");

    static string BackupPath => Path.Combine(Folder, "settings.bak.json");
    static readonly object SaveLock = new();

    // A damaged settings.json (power cut mid-write, disk hiccup, a bad hand edit) must never wipe the user's
    // preferences: fall back to the last good copy, and keep the damaged file aside instead of saving over it.
    public static Settings Load()
    {
        if (TryRead(FilePath) is { } main) return Upgrade(main);
        bool damaged = File.Exists(FilePath);
        if (TryRead(BackupPath) is { } backup)
        {
            if (damaged) KeepAside();
            return Upgrade(backup);
        }
        if (damaged) KeepAside();
        return new Settings();
    }

    /// <summary>Settings saved by older versions, in today's words (the neon theme had another name before 1.8.1).</summary>
    static Settings Upgrade(Settings s)
    {
        if (string.Equals(s.Theme, "cyber" + "punk", StringComparison.OrdinalIgnoreCase)) s.Theme = "neon";
        return s;
    }

    static Settings? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text) || text.IndexOf('\0') >= 0) return null;
            return JsonSerializer.Deserialize<Settings>(text);
        }
        catch { return null; }
    }

    static void KeepAside()
    {
        try { File.Copy(FilePath, Path.Combine(Folder, "settings.damaged.json"), true); } catch { }
    }

    // Written to a temp file first, then swapped in, the previous good file becoming the backup:
    // at every moment either the old or the new settings are whole on disk.
    public void Save()
    {
        lock (SaveLock)
        {
            string tmp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Folder);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs))
                {
                    w.Write(json);
                    w.Flush();
                    fs.Flush(true);
                }
                if (TryRead(FilePath) != null)
                {
                    try { File.Replace(tmp, FilePath, BackupPath, true); return; }
                    catch { File.Copy(FilePath, BackupPath, true); }
                }
                File.Move(tmp, FilePath, true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
