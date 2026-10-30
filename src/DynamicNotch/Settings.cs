using System.IO;
using System.Text.Json;

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
    public bool SlimOverWindows { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool KeepRunning { get; set; } = true;
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
    public DateTime? LastDownloadsNag { get; set; }

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicNotch");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
