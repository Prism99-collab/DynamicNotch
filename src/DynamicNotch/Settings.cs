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
    /// <summary>The companion's size: 0.85 small, 1 medium, 1.2 large.</summary>
    public double CompanionSize { get; set; } = 1.0;
    public DateTime? LastDownloadsNag { get; set; }

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicNotch");

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
