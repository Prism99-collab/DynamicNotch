using System.IO;
using System.Text.Json;

namespace DynamicNotch.Services;

/// <summary>
/// What the notch's search knows: every app in the Start menu (desktop and Store apps, through the shell's own
/// Apps folder), your recent files, what's in Downloads, Desktop and Documents, and the Windows settings pages people
/// look for most. Built in the background on a thread of its own, refreshed at most every few minutes, never while
/// you type; how often you open each thing is remembered so the usual ones come first.
/// </summary>
public sealed class LauncherIndex
{
    public enum Kind { App, File, Folder, Setting }

    public sealed record Entry(string Name, string Target, Kind Kind, string? Detail)
    {
        /// <summary>Lower-cased name and its words, for matching (made once).</summary>
        public string Key { get; } = Name.ToLowerInvariant();
    }

    List<Entry> _entries = new();
    DateTime _builtAt;
    bool _building;
    readonly Dictionary<string, (int Count, DateTime Last)> _usage = new(StringComparer.OrdinalIgnoreCase);
    static string UsageFile => Path.Combine(Settings.Folder, "launcher.json");

    public IReadOnlyList<Entry> Entries => _entries;
    public event Action? Changed;

    public LauncherIndex() => LoadUsage();

    /// <summary>Builds (or refreshes) the list in the background when it's missing or older than 5 minutes.</summary>
    public void Refresh(bool force = false)
    {
        if (_building || !force && _entries.Count > 0 && DateTime.UtcNow - _builtAt < TimeSpan.FromMinutes(5)) return;
        _building = true;
        var t = new Thread(() =>
        {
            try
            {
                var list = new List<Entry>();
                list.AddRange(Apps());
                list.AddRange(SettingsPages);
                list.AddRange(Places());
                list.AddRange(Files());
                // one entry per target, apps first
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _entries = list.Where(e => seen.Add(e.Kind + "|" + e.Target)).ToList();
                _builtAt = DateTime.UtcNow;
            }
            catch (Exception ex) { AppLog.Write("launcher index: " + ex.Message); }
            finally { _building = false; }
            Changed?.Invoke();
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Launcher index" };
        t.SetApartmentState(ApartmentState.STA); // the shell's Apps folder is a COM object that wants STA
        t.Start();
    }

    // ───── apps: the shell's Apps folder has them all, desktop and Store, with names as the Start menu shows them

    static IEnumerable<Entry> Apps()
    {
        var found = new List<Entry>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return found;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string path = item.Path; // a Store app's id, or the exe / shortcut path of a desktop app
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                if (IsNoise(name)) continue;
                found.Add(new Entry(name, "shell:AppsFolder\\" + path, Kind.App, null));
            }
        }
        catch (Exception ex) { AppLog.Write("launcher apps: " + ex.Message); }
        return found;
    }

    /// <summary>Start menu clutter nobody searches for.</summary>
    static bool IsNoise(string name)
    {
        string n = name.ToLowerInvariant();
        return n.StartsWith("uninstall") || n.Contains("readme") || n.EndsWith(" help") || n.EndsWith(" website") || n.Contains("release notes")
            || n.EndsWith(" documentation") || n.EndsWith(" manual") || n.EndsWith(" license");
    }

    // ───── the folders everybody has ("open downloads")

    static IEnumerable<Entry> Places()
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<Entry>();
        void Add(string name, string path) { if (path.StartsWith("shell:") || Directory.Exists(path)) list.Add(new Entry(name, path, Kind.Folder, "Place")); }
        Add("Downloads", Path.Combine(user, "Downloads"));
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add("This PC", "shell:MyComputerFolder");
        Add("Recycle Bin", "shell:RecycleBinFolder");
        return list;
    }

    // ───── files: recent ones, and the three folders people keep things in

    static IEnumerable<Entry> Files()
    {
        var found = new List<Entry>();
        string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        try
        {
            if (Directory.Exists(recent))
                foreach (var f in new DirectoryInfo(recent).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).Take(150))
                {
                    string name = Path.GetFileNameWithoutExtension(f.Name);
                    bool folder = !Path.HasExtension(name);
                    found.Add(new Entry(name, f.FullName, folder ? Kind.Folder : Kind.File, "Recent"));
                }
        }
        catch { }
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (dir, label) in new[]
        {
            (Path.Combine(user, "Downloads"), "Downloads"),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents"),
        })
            Walk(dir, label, 0, found);
        return found;
    }

    /// <summary>Two levels deep, at most 1500 things per folder, no hidden or system files.</summary>
    static void Walk(string dir, string label, int depth, List<Entry> into)
    {
        if (!Directory.Exists(dir) || depth > 2) return;
        int count = 0;
        try
        {
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (++count > 1500) break;
                if ((e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || e.Name.StartsWith('.')) continue;
                if (e is DirectoryInfo d)
                {
                    into.Add(new Entry(d.Name, d.FullName, Kind.Folder, label));
                    if (depth < 1) Walk(d.FullName, label + " › " + d.Name, depth + 1, into);
                }
                else if (!e.Name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    && !e.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !e.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    into.Add(new Entry(e.Name, e.FullName, Kind.File, label));
            }
        }
        catch { }
    }

    // ───── the settings pages people look for

    static readonly Entry[] SettingsPages =
    {
        S("Bluetooth & devices", "ms-settings:bluetooth", "bluetooth headphones mouse"),
        S("Wi-Fi", "ms-settings:network-wifi", "wifi wireless internet network"),
        S("Display", "ms-settings:display", "screen resolution scale brightness monitor"),
        S("Night light", "ms-settings:nightlight", "blue light"),
        S("Sound", "ms-settings:sound", "audio speakers microphone volume"),
        S("Battery & power", "ms-settings:powersleep", "power sleep battery saver energy"),
        S("Apps", "ms-settings:appsfeatures", "uninstall installed programs"),
        S("Startup apps", "ms-settings:startupapps", "start with windows boot"),
        S("Notifications", "ms-settings:notifications", "alerts do not disturb"),
        S("Personalization", "ms-settings:personalization", "wallpaper background colors theme"),
        S("Windows Update", "ms-settings:windowsupdate", "updates upgrade"),
        S("Storage", "ms-settings:storagesense", "disk space cleanup"),
        S("Mouse", "ms-settings:mousetouchpad", "pointer cursor"),
        S("Touchpad", "ms-settings:devices-touchpad", "gestures trackpad"),
        S("Keyboard & language", "ms-settings:regionlanguage", "input layout language"),
        S("Printers & scanners", "ms-settings:printers", "print"),
        S("Accounts", "ms-settings:yourinfo", "user profile sign in"),
        S("Privacy & security", "ms-settings:privacy", "camera microphone location permissions"),
        S("Date & time", "ms-settings:dateandtime", "clock timezone"),
        S("Gaming", "ms-settings:gaming-gamebar", "game bar xbox"),
        S("Control Panel", "control.exe", "control panel classic"),
        S("Task Manager", "taskmgr.exe", "processes performance kill"),
        S("Device Manager", "devmgmt.msc", "drivers hardware"),
    };

    static Entry S(string name, string target, string words) => new(name, target, Kind.Setting, words);

    // ───── how often you open things

    public void Used(Entry e)
    {
        string k = e.Kind + "|" + e.Target;
        _usage[k] = (_usage.TryGetValue(k, out var u) ? u.Count + 1 : 1, DateTime.UtcNow);
        var snapshot = _usage.ToDictionary(p => p.Key, p => new[] { p.Value.Count, (int)(p.Value.Last - DateTime.UnixEpoch).TotalMinutes });
        _ = Task.Run(() =>
        {
            try { Directory.CreateDirectory(Settings.Folder); File.WriteAllText(UsageFile, JsonSerializer.Serialize(snapshot)); } catch { }
        });
    }

    /// <summary>0 for never, rising with how often and how recently it was opened.</summary>
    public double Habit(Entry e)
    {
        if (!_usage.TryGetValue(e.Kind + "|" + e.Target, out var u)) return 0;
        double days = (DateTime.UtcNow - u.Last).TotalDays;
        return Math.Log(1 + u.Count) * 18 + Math.Max(0, 14 - days);
    }

    /// <summary>What you open most, for the empty search.</summary>
    public IEnumerable<Entry> Favourites(int n) =>
        _entries.Where(e => e.Kind == Kind.App).Select(e => (e, h: Habit(e))).Where(x => x.h > 0).OrderByDescending(x => x.h).Take(n).Select(x => x.e);

    void LoadUsage()
    {
        try
        {
            if (!File.Exists(UsageFile)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(UsageFile));
            if (d == null) return;
            foreach (var (k, v) in d)
                if (v.Length == 2) _usage[k] = (v[0], DateTime.UnixEpoch.AddMinutes(v[1]));
        }
        catch { }
    }

    // ───── matching

    /// <summary>
    /// How well a query fits a name: the whole name first, then the start of a word, then the initials ("vsc" →
    /// Visual Studio Code), then inside a word, then the letters in order. 0 means no match.
    /// </summary>
    public static double Score(string query, Entry e)
    {
        string q = query.ToLowerInvariant().Trim(), k = e.Key;
        if (q.Length == 0) return 0;
        double s;
        if (k == q) s = 1000;
        else if (k.StartsWith(q)) s = 800 - Math.Min(100, k.Length - q.Length);
        else if (WordStart(k, q)) s = 650 - Math.Min(100, k.Length);
        else if (Initials(k).StartsWith(q) && q.Length >= 2) s = 600;
        else if (k.Contains(q)) s = 450 - k.IndexOf(q, StringComparison.Ordinal);
        else if (e.Kind == Kind.Setting && e.Detail != null && e.Detail.Contains(q)) s = 420;
        else if (q.Length >= 3 && !q.Contains(' ') && InOrder(k, q)) s = 200 - Math.Min(100, k.Length); // loose letters: single words only
        else if (q.Length >= 4 && e.Kind is Kind.App or Kind.Setting && Near(k, q)) s = 330; // a typo: "spotfy", "dicord"
        else return 0;
        // apps and settings matter more than files of the same name
        s += e.Kind switch { Kind.App => 60, Kind.Setting => 30, Kind.Folder => 5, _ => 0 };
        return s;
    }

    static bool WordStart(string k, string q)
    {
        for (int i = 1; i < k.Length; i++)
            if (!char.IsLetterOrDigit(k[i - 1]) && string.CompareOrdinal(k, i, q, 0, q.Length) == 0) return true;
        return false;
    }

    static string Initials(string k)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < k.Length; i++)
            if (char.IsLetterOrDigit(k[i]) && (i == 0 || !char.IsLetterOrDigit(k[i - 1]))) sb.Append(k[i]);
        return sb.ToString();
    }

    /// <summary>A word in the name that's one slip away from what was typed (two for long words).</summary>
    static bool Near(string k, string q)
    {
        int max = q.Length >= 8 ? 2 : 1;
        for (int i = 0; i < k.Length; i++)
        {
            if (i > 0 && char.IsLetterOrDigit(k[i - 1])) continue;
            for (int len = q.Length - max; len <= q.Length + max; len++)
                if (len > 0 && i + len <= k.Length && Distance(k.AsSpan(i, len), q) <= max) return true;
        }
        return false;
    }

    /// <summary>Edits between two short strings, a swap of neighbours counting as one.</summary>
    static int Distance(ReadOnlySpan<char> a, string b)
    {
        int n = a.Length, m = b.Length;
        Span<int> d = stackalloc int[(n + 1) * (m + 1)];
        int W = m + 1;
        for (int i = 0; i <= n; i++) d[i * W] = i;
        for (int j = 0; j <= m; j++) d[j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(d[(i - 1) * W + j] + 1, d[i * W + j - 1] + 1), d[(i - 1) * W + j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[(i - 2) * W + j - 2] + 1);
                d[i * W + j] = v;
            }
        return d[n * W + m];
    }

    static bool InOrder(string k, string q)
    {
        int j = 0;
        foreach (char c in k) if (j < q.Length && c == q[j]) j++;
        return j == q.Length;
    }
}
