using System.IO;
using System.IO.Compression;
using System.IO.Enumeration;

namespace DynamicNotch.Services;

/// <summary>
/// The notch's own list of file and folder names on every fixed drive, so the search finds anything on any PC, even
/// where Windows' index is off or doesn't cover a drive. Only names, folders and dates are kept (never what's inside),
/// in a compact list on this PC (files.idx in the settings folder).
///  • Built once, in the background, on a low-priority thread, a minute after the notch starts; read back from disk on
///    the next start (well under a second), then refreshed when it's more than a day old.
///  • Kept up to date while the notch runs: Windows tells it when a file is made, renamed or deleted.
///  • Leaves out what nobody searches for: Windows, Program Files, ProgramData, app data, caches, code dependencies,
///    the Recycle Bin, hidden and system folders.
///  • Asked on its own thread, a moment after you stop typing; only the newest question is answered.
/// </summary>
public sealed class FileIndex
{
    public sealed record Hit(string Path, string Name, bool Folder, DateTime Modified);

    // one entry per file or folder: its name, the folder it's in, whether it's a folder, when it last changed
    readonly object _gate = new();
    List<string> _dirs = new();               // full folder paths
    Dictionary<string, int> _dirIndex = new(StringComparer.OrdinalIgnoreCase);
    List<string> _names = new();
    List<int> _parent = new();
    List<int> _minutes = new();               // last change, minutes since 2000 (negative: a folder)
    List<bool> _gone = new();
    int _count;

    public bool Ready { get; private set; }
    public int Count => _count;
    public event Action<string, List<Hit>>? Results;

    static string IndexFile => Path.Combine(Settings.Folder, "files.idx");
    static readonly DateTime Epoch = new(2000, 1, 1);
    const int MaxEntries = 900_000, MaxPerFolder = 6000;

    readonly List<FileSystemWatcher> _watchers = new();
    volatile bool _rescanWanted;

    // ───────────────────────────── building ─────────────────────────────

    /// <summary>Reads the saved list (if any), then builds a new one when it's missing or a day old. All in the background.</summary>
    public void Start(Func<bool> mayWork)
    {
        var t = new Thread(() =>
        {
            try
            {
                bool loaded = Load();
                if (loaded) { Ready = true; Watch(); }
                // the first build waits for the PC to settle after start-up
                Thread.Sleep(loaded ? 90_000 : 45_000);
                while (true)
                {
                    bool stale = !loaded || _rescanWanted || DateTime.UtcNow - SavedAt() > TimeSpan.FromHours(24);
                    if (stale && mayWork())
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        Build();
                        Save();
                        loaded = true;
                        _rescanWanted = false;
                        Ready = true;
                        Watch();
                        AppLog.Write($"file index: {_count:N0} names in {_dirs.Count:N0} folders, {sw.Elapsed.TotalSeconds:0.0} s");
                    }
                    Thread.Sleep(TimeSpan.FromMinutes(20));
                    SaveIfChanged();
                }
            }
            catch (Exception ex) { AppLog.Write("file index: " + ex.Message); }
        }) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "File index" };
        t.Start();
    }

    /// <summary>Where to look: every fixed drive; on the Windows drive, your own folders and anything that isn't Windows or apps.</summary>
    static List<string> Roots()
    {
        var roots = new List<string>();
        string sys = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                if (!string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase)) { roots.Add(d.Name); continue; }
                // your own folders (each one, so app data's constant churn is never watched), and files lying in the profile itself
                if (Directory.Exists(profile))
                {
                    foreach (var top in Directory.EnumerateDirectories(profile))
                    {
                        var name = Path.GetFileName(top);
                        if (SkipAnywhere.Contains(name) || name.StartsWith('.')) continue;
                        var attr = File.GetAttributes(top);
                        if ((attr & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                        roots.Add(top);
                    }
                    roots.Add("|files|" + profile);
                }
                foreach (var top in Directory.EnumerateDirectories(d.Name))
                {
                    var name = Path.GetFileName(top);
                    if (SkipTop.Contains(name)) continue;
                    var attr = File.GetAttributes(top);
                    if ((attr & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                    roots.Add(top);
                }
                // the root's own files (rare, but some people keep things there)
                roots.Add("|files|" + d.Name);
            }
            catch { }
        }
        return roots;
    }

    static readonly HashSet<string> SkipTop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "$Recycle.Bin", "System Volume Information", "Recovery",
        "PerfLogs", "MSOCache", "Windows.old", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS", "OneDriveTemp", "Config.Msi", "Intel", "AMD", "NVIDIA",
    };

    static readonly HashSet<string> SkipAnywhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "node_modules", "$Recycle.Bin", "System Volume Information", "__pycache__", ".git", ".svn", ".hg", ".vs", ".idea", ".gradle",
        "venv", ".venv", "site-packages", "obj", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "WindowsApps", "WpSystem",
        "$WinREAgent", "Config.Msi", "packages", ".nuget", ".cache", ".npm", "bower_components", "Temp",
    };

    void Build()
    {
        var dirs = new List<string>();
        var dirIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(200_000);
        var parent = new List<int>(200_000);
        var minutes = new List<int>(200_000);
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary,
        };
        int Dir(string path)
        {
            if (dirIndex.TryGetValue(path, out int i)) return i;
            dirs.Add(path);
            return dirIndex[path] = dirs.Count - 1;
        }
        foreach (var root in Roots())
        {
            bool filesOnly = root.StartsWith("|files|");
            string start = filesOnly ? root[7..] : root;
            var stack = new Stack<string>();
            stack.Push(start);
            while (stack.Count > 0 && names.Count < MaxEntries)
            {
                string folder = stack.Pop();
                int di = Dir(folder);
                int inFolder = 0;
                try
                {
                    var items = new FileSystemEnumerable<(string Name, bool IsDir, bool Link, DateTime When)>(folder,
                        (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0, e.LastWriteTimeUtc.UtcDateTime), opts);
                    foreach (var (name, isDir, link, when) in items)
                    {
                        if (++inFolder > MaxPerFolder) break;
                        if (isDir)
                        {
                            if (link || SkipAnywhere.Contains(name) || name.StartsWith('.') || name.StartsWith('$')) continue;
                            names.Add(name); parent.Add(di); minutes.Add(-1 - Minutes(when));
                            if (!filesOnly) stack.Push(Path.Combine(folder, name));
                        }
                        else
                        {
                            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~$")) continue;
                            names.Add(name); parent.Add(di); minutes.Add(Minutes(when));
                        }
                    }
                }
                catch { }
            }
        }
        lock (_gate)
        {
            _dirs = dirs; _dirIndex = dirIndex; _names = names; _parent = parent; _minutes = minutes;
            _gone = new List<bool>(new bool[names.Count]);
            _count = names.Count;
        }
    }

    static int Minutes(DateTime utc) => utc < Epoch ? 0 : (int)Math.Min(int.MaxValue - 2, (utc - Epoch).TotalMinutes);

    // ───────────────────────────── kept up to date ─────────────────────────────

    void Watch()
    {
        lock (_watchers)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            foreach (var root in Roots().Where(r => !r.StartsWith("|files|")))
            {
                try
                {
                    var w = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    };
                    w.Created += (_, e) => Added(e.FullPath);
                    w.Deleted += (_, e) => Removed(e.FullPath);
                    w.Renamed += (_, e) => { Removed(e.OldFullPath); Added(e.FullPath); };
                    w.Error += (_, _) => _rescanWanted = true; // too much at once: the next round rebuilds
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch { }
            }
        }
    }

    static bool Skipped(string path)
    {
        foreach (var part in path.Split('\\'))
            if (SkipAnywhere.Contains(part) || part.StartsWith('.') && part.Length > 1) return true;
        return false;
    }

    void Added(string path)
    {
        try
        {
            if (Skipped(path)) return;
            string? folder = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);
            if (folder == null || name.Length == 0) return;
            bool isDir = Directory.Exists(path);
            int when = Minutes(DateTime.UtcNow);
            lock (_gate)
            {
                if (_count >= MaxEntries) return;
                if (!_dirIndex.TryGetValue(folder, out int di)) { _dirs.Add(folder); di = _dirIndex[folder] = _dirs.Count - 1; }
                _names.Add(name); _parent.Add(di); _minutes.Add(isDir ? -1 - when : when); _gone.Add(false);
                _count = _names.Count;
                if (isDir && !_dirIndex.ContainsKey(path)) { _dirs.Add(path); _dirIndex[path] = _dirs.Count - 1; }
            }
            _dirty = true;
        }
        catch { }
    }

    /// <summary>Something was deleted: results are checked on disk before they're shown, so it simply stops showing.</summary>
    void Removed(string path) => _dirty = true;

    volatile bool _dirty;

    // ───────────────────────────── on disk ─────────────────────────────

    DateTime SavedAt() { try { return File.Exists(IndexFile) ? File.GetLastWriteTimeUtc(IndexFile) : DateTime.MinValue; } catch { return DateTime.MinValue; } }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            string tmp = IndexFile + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var w = new BinaryWriter(gz))
            {
                lock (_gate)
                {
                    w.Write(2); // format
                    w.Write(_dirs.Count);
                    foreach (var d in _dirs) w.Write(d);
                    int live = 0;
                    for (int i = 0; i < _names.Count; i++) if (!_gone[i]) live++;
                    w.Write(live);
                    for (int i = 0; i < _names.Count; i++)
                    {
                        if (_gone[i]) continue;
                        w.Write(_names[i]); w.Write(_parent[i]); w.Write(_minutes[i]);
                    }
                }
            }
            File.Move(tmp, IndexFile, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex) { AppLog.Write("file index save: " + ex.Message); }
    }

    bool Load()
    {
        try
        {
            if (!File.Exists(IndexFile)) return false;
            using var fs = File.OpenRead(IndexFile);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(gz);
            if (r.ReadInt32() != 2) return false;
            int nd = r.ReadInt32();
            var dirs = new List<string>(nd);
            var dirIndex = new Dictionary<string, int>(nd, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nd; i++) { var d = r.ReadString(); dirs.Add(d); dirIndex[d] = i; }
            int n = r.ReadInt32();
            var names = new List<string>(n); var parent = new List<int>(n); var minutes = new List<int>(n);
            for (int i = 0; i < n; i++) { names.Add(r.ReadString()); parent.Add(r.ReadInt32()); minutes.Add(r.ReadInt32()); }
            lock (_gate)
            {
                _dirs = dirs; _dirIndex = dirIndex; _names = names; _parent = parent; _minutes = minutes;
                _gone = new List<bool>(new bool[n]);
                _count = n;
            }
            return true;
        }
        catch (Exception ex) { AppLog.Write("file index load: " + ex.Message); return false; }
    }

    /// <summary>Saves what changed since (called now and then, and when the notch closes).</summary>
    public void SaveIfChanged() { if (_dirty && Ready) Save(); }

    // ───────────────────────────── asking ─────────────────────────────

    string? _pending;
    readonly AutoResetEvent _wake = new(false);
    Thread? _asker;

    public void Query(string query)
    {
        lock (_gate) _pending = query;
        if (_asker == null)
        {
            _asker = new Thread(AskLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "File index search" };
            _asker.Start();
        }
        _wake.Set();
    }

    void AskLoop()
    {
        while (true)
        {
            _wake.WaitOne();
            string? q;
            while (true)
            {
                lock (_gate) q = _pending;
                Thread.Sleep(70); // one question per pause in the typing
                bool changed;
                lock (_gate) changed = !ReferenceEquals(q, _pending);
                if (!changed) break;
            }
            lock (_gate) _pending = null;
            if (q == null || !Ready) continue;
            try
            {
                var found = Find(q);
                bool newer;
                lock (_gate) newer = _pending != null;
                if (!newer) Results?.Invoke(q, found);
            }
            catch (Exception ex) { AppLog.Write("file index search: " + ex.Message); }
        }
    }

    /// <summary>
    /// Names holding every word typed, best first: the whole name, then its start, then a word's start, then anywhere;
    /// newer things before older ones of the same fit.
    /// </summary>
    public List<Hit> Find(string query, int max = 40)
    {
        var words = query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Sum(w => w.Length) < 2) return new();
        string whole = query.Trim();
        var best = new List<(int I, int Fit, int When)>();
        lock (_gate)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_gone[i]) continue;
                string n = _names[i];
                if (n.IndexOf(words[0], StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool all = true;
                for (int k = 1; k < words.Length && all; k++) all = n.IndexOf(words[k], StringComparison.OrdinalIgnoreCase) >= 0;
                if (!all) continue;
                int fit = string.Equals(Path.GetFileNameWithoutExtension(n), whole, StringComparison.OrdinalIgnoreCase) || string.Equals(n, whole, StringComparison.OrdinalIgnoreCase) ? 4
                    : n.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 3
                    : WordStart(n, words[0]) ? 2 : 1;
                int when = _minutes[i] < 0 ? -1 - _minutes[i] : _minutes[i];
                best.Add((i, fit, when));
                if (best.Count > 4000) break; // a one-letter question: plenty to pick the best from
            }
            var picked = best.OrderByDescending(b => b.Fit).ThenByDescending(b => b.When).Take(max * 2)
                .Select(b => new Hit(Path.Combine(_dirs[_parent[b.I]], _names[b.I]), _names[b.I], _minutes[b.I] < 0, Epoch.AddMinutes(b.When).ToLocalTime()))
                .ToList();
            best = null!;
            // still there? (deleted or moved things stop showing at once, without searching the whole list on every delete)
            return picked.Where(h => h.Folder ? Directory.Exists(h.Path) : File.Exists(h.Path)).Take(max).ToList();
        }
    }

    static bool WordStart(string name, string w)
    {
        for (int i = 1; i <= name.Length - w.Length; i++)
            if (!char.IsLetterOrDigit(name[i - 1]) && string.Compare(name, i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0) return true;
        return false;
    }
}
