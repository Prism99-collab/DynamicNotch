using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// What the AI can know about your day, kept small and on this PC only:
///  • today's notifications (they used to vanish when the notch restarted): kept for the day, gone the next;
///  • how long each app was in front, per day (never while you're away from the PC), for "what did I use most";
///  • your default browser, so "the browser" means the one you actually use;
///  • the usual names of Windows' own apps ("cmd" is the Command Prompt, not another app with cmd in its name).
/// </summary>
public partial class NotchWindow
{
    // ───────────────────────────── today's notifications ─────────────────────────────

    static string ToastsFile => Path.Combine(Settings.Folder, "notifications-today.json");
    bool _toastsLoaded;
    int _toastsSave;

    sealed record SavedToast(DateTime At, string App, string Title, string Body);

    /// <summary>Today's notifications from before the notch started (once).</summary>
    void LoadTodaysToasts()
    {
        if (_toastsLoaded) return;
        _toastsLoaded = true;
        try
        {
            if (!File.Exists(ToastsFile)) return;
            var saved = JsonSerializer.Deserialize<List<SavedToast>>(File.ReadAllText(ToastsFile)) ?? new();
            lock (_recentToasts)
            {
                var today = saved.Where(t => t.At.Date == DateTime.Today).Select(t => (t.At, t.App, t.Title, t.Body));
                _recentToasts.InsertRange(0, today.Where(t => !_recentToasts.Any(r => r.At == t.At && r.Title == t.Title)));
            }
        }
        catch { }
    }

    async void SaveTodaysToasts()
    {
        int v = ++_toastsSave;
        await Task.Delay(1500);
        if (v != _toastsSave) return;
        List<SavedToast> list;
        lock (_recentToasts) list = _recentToasts.Where(t => t.At.Date == DateTime.Today).Select(t => new SavedToast(t.At, t.App, t.Title, t.Body.Length > 300 ? t.Body[..300] : t.Body)).ToList();
        try { await Task.Run(() => { Directory.CreateDirectory(Settings.Folder); File.WriteAllText(ToastsFile, JsonSerializer.Serialize(list)); }); } catch { }
    }

    // ───────────────────────────── time in each app ─────────────────────────────

    static string AppTimeFile => Path.Combine(Settings.Folder, "app-time.json");
    Dictionary<string, Dictionary<string, int>>? _appTime;    // "yyyy-MM-dd" → app → seconds
    readonly Dictionary<uint, string> _appNames = new();
    DispatcherTimer? _appTick;
    DateTime _appSaved = DateTime.UtcNow;

    /// <summary>Every 5 seconds: the app in front gets 5 more seconds (not while you're away, not the notch itself).</summary>
    void StartAppTime()
    {
        if (_appTick != null) return;
        try { _appTime = File.Exists(AppTimeFile) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(AppTimeFile)) : null; } catch { }
        _appTime ??= new();
        _appTick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _appTick.Tick += (_, _) =>
        {
            try
            {
                if (IdleTime() > TimeSpan.FromMinutes(3)) return;
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == _hwnd) return;
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == 0 || pid == (uint)Environment.ProcessId) return;
                string name = AppNameOf(pid);
                if (name.Length == 0) return;
                string day = DateTime.Today.ToString("yyyy-MM-dd");
                if (!_appTime!.TryGetValue(day, out var apps)) _appTime[day] = apps = new();
                apps[name] = apps.GetValueOrDefault(name) + 5;
                TrackWork(fg, pid, name); // the document, PDF or page in front (NotchWindow.WorkPage)
                if ((DateTime.UtcNow - _appSaved).TotalMinutes >= 2) SaveAppTime();
            }
            catch { }
        };
        _appTick.Start();
    }

    string AppNameOf(uint pid)
    {
        if (_appNames.TryGetValue(pid, out var known)) return known;
        string name = "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
            try { if (p.MainModule?.FileName is { } file && FileVersionInfo.GetVersionInfo(file).FileDescription is { Length: > 1 } d) name = d; } catch { }
        }
        catch { }
        if (Regex.IsMatch(name, @"^(explorer|ShellExperienceHost|SearchHost|StartMenuExperienceHost|LockApp|ApplicationFrameHost)$", RegexOptions.IgnoreCase)) name = name == "explorer" ? "File Explorer" : "";
        if (_appNames.Count > 400) _appNames.Clear();
        _appNames[pid] = name;
        return name;
    }

    void SaveAppTime()
    {
        _appSaved = DateTime.UtcNow;
        _ = Task.Run(WorkLog.Save);
        if (_appTime == null) return;
        // two weeks are kept
        string oldest = DateTime.Today.AddDays(-14).ToString("yyyy-MM-dd");
        foreach (var d in _appTime.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList()) _appTime.Remove(d);
        string json = JsonSerializer.Serialize(_appTime);
        _ = Task.Run(() => { try { Directory.CreateDirectory(Settings.Folder); File.WriteAllText(AppTimeFile + ".tmp", json); File.Move(AppTimeFile + ".tmp", AppTimeFile, true); } catch { } });
    }

    /// <summary>For notch_data: time in each app, today and yesterday (or one day), most used first.</summary>
    string AppTimeText(DateTime? only)
    {
        if (_appTime == null) return "APP TIME: not recorded yet (it starts counting from now).";
        static string Long(int s) => s >= 3600 ? $"{s / 3600} h {s % 3600 / 60:00} min" : $"{Math.Max(1, s / 60)} min";
        var sb = new StringBuilder("APP TIME (how long each app was in front; time away from the PC isn't counted):\n");
        var days = only != null ? new[] { only.Value } : new[] { DateTime.Today, DateTime.Today.AddDays(-1) };
        foreach (var d in days)
        {
            string label = d == DateTime.Today ? "today" : d == DateTime.Today.AddDays(-1) ? "yesterday" : d.ToString("ddd d MMM");
            if (!_appTime.TryGetValue(d.ToString("yyyy-MM-dd"), out var apps) || apps.Count == 0) { sb.AppendLine($"- {label}: nothing recorded"); continue; }
            sb.AppendLine($"- {label} (total {Long(apps.Values.Sum())}): " + string.Join(", ", apps.OrderByDescending(a => a.Value).Take(10).Select(a => $"{a.Key} {Long(a.Value)}")));
        }
        return sb.ToString().TrimEnd();
    }

    // ───────────────────────────── the default browser, and Windows' own apps ─────────────────────────────

    static (string? Name, DateTime At) _browser;

    /// <summary>The browser web links open in ("brave", "chrome", "msedge", "firefox"...), or null.</summary>
    static string? DefaultBrowser()
    {
        if ((DateTime.UtcNow - _browser.At).TotalMinutes < 2) return _browser.Name;
        string? name = null;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            string id = k?.GetValue("ProgId")?.ToString() ?? "";
            name = id switch
            {
                _ when id.StartsWith("Brave", StringComparison.OrdinalIgnoreCase) => "brave",
                _ when id.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase) => "chrome",
                _ when id.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase) => "msedge",
                _ when id.StartsWith("Firefox", StringComparison.OrdinalIgnoreCase) => "firefox",
                _ when id.StartsWith("Opera", StringComparison.OrdinalIgnoreCase) => "opera",
                _ when id.StartsWith("Vivaldi", StringComparison.OrdinalIgnoreCase) => "vivaldi",
                _ => null,
            };
        }
        catch { }
        _browser = (name, DateTime.UtcNow);
        return name;
    }

    static string BrowserName(string? id) => id switch { "brave" => "Brave", "chrome" => "Google Chrome", "msedge" => "Microsoft Edge", "firefox" => "Firefox", "opera" => "Opera", "vivaldi" => "Vivaldi", _ => "the default browser" };

    /// <summary>What people call Windows' own apps → their real names, in English and French.</summary>
    static readonly (Regex Said, string[] Names, string? Exe)[] KnownApps =
    {
        (new(@"^(cmd|command prompt|commande|invite de commandes?|terminal cmd)$", RegexOptions.IgnoreCase), new[] { "Command Prompt", "Invite de commandes" }, "cmd.exe"),
        (new(@"^(power ?shell|ps)$", RegexOptions.IgnoreCase), new[] { "Windows PowerShell", "PowerShell" }, "powershell.exe"),
        (new(@"^(terminal|windows terminal)$", RegexOptions.IgnoreCase), new[] { "Terminal", "Windows Terminal" }, "wt.exe"),
        (new(@"^(explorer|file explorer|files|explorateur|explorateur de fichiers|my files)$", RegexOptions.IgnoreCase), new[] { "File Explorer", "Explorateur de fichiers" }, "explorer.exe"),
        (new(@"^(task manager|gestionnaire des t[âa]ches|taskmgr)$", RegexOptions.IgnoreCase), new[] { "Task Manager", "Gestionnaire des tâches" }, "taskmgr.exe"),
        (new(@"^(calc|calculator|calculatrice)$", RegexOptions.IgnoreCase), new[] { "Calculator", "Calculatrice" }, null),
        (new(@"^(notepad|bloc[- ]notes)$", RegexOptions.IgnoreCase), new[] { "Notepad", "Bloc-notes" }, "notepad.exe"),
        (new(@"^(settings|param[eè]tres|windows settings)$", RegexOptions.IgnoreCase), new[] { "Settings", "Paramètres" }, null),
        (new(@"^(control panel|panneau de configuration)$", RegexOptions.IgnoreCase), new[] { "Control Panel", "Panneau de configuration" }, "control.exe"),
        (new(@"^(paint)$", RegexOptions.IgnoreCase), new[] { "Paint" }, null),
    };

    /// <summary>One of Windows' own apps, by the name people use for it (null: not one of them).</summary>
    LauncherIndex.Entry? KnownApp(string said)
    {
        string s = said.Trim();
        foreach (var (rx, names, exe) in KnownApps)
        {
            if (!rx.IsMatch(s)) continue;
            foreach (var n in names)
                if (_index.Entries.FirstOrDefault(e => e.Kind == LauncherIndex.Kind.App && e.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
            if (exe != null && _index.Entries.FirstOrDefault(e => e.Kind == LauncherIndex.Kind.App && e.Target.EndsWith("\\" + exe, StringComparison.OrdinalIgnoreCase)) is { } byFile) return byFile;
        }
        return null;
    }
}
