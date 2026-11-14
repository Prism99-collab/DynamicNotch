using System.IO;
using System.IO.Compression;
using System.IO.Enumeration;

namespace DynamicNotch.Services;

/// <summary>
/// The notch's own list of file and folder names on every fixed drive, so the search finds anything on any PC, even
/// where Windows' index is off or doesn't cover a drive. Only names, folders and dates are kept (never what's inside),
/// in a compact list on this PC (files.idx in the settings folder).
///  • Built in the background on a low-priority thread (a few seconds after the very first start, so a new PC finds
///    everything almost at once); read back from disk on later starts, refreshed when it's a day old.
///  • Kept up to date while the notch runs: Windows tells it when a file is made, renamed or deleted.
///  • Leaves out what nobody searches for: Windows, Program Files, app data, caches, code dependencies, the Recycle
///    Bin, hidden and system folders, and system or game-data files (.dll, .sys, .pak, .pyd, caches…).
///  • Asked on every key, in a few milliseconds: every name lives folded (lower case, no accents) in one block of
///    text that's scanned at memory speed, the matches for what you typed a moment ago are narrowed instead of
///    searched again, and recent answers are kept for Backspace.
/// </summary>
public sealed class FileIndex
{
    public sealed record Hit(string Path, string Name, bool Folder, DateTime Modified);

    // one entry per file or folder: its name, the folder it's in, when it last changed (negative: a folder)
    readonly object _gate = new();
    List<string> _dirs = new();
    Dictionary<string, int> _dirIndex = new(StringComparer.OrdinalIgnoreCase);
    List<string> _names = new();
    List<int> _parent = new();
    List<int> _minutes = new();
    List<bool> _gone = new();
    List<int> _dirBonus = new();      // per folder: your own folders up, deep folders down
    int _count;

    // the names, folded, one after another ("\n" between them), and where each one starts; the same for initials
    char[] _buf = Array.Empty<char>();
    int _bufLen;
    List<int> _start = new();
    char[] _ini = Array.Empty<char>();
    int _iniLen;
    List<int> _iniStart = new();
    int _version; // changes whenever the list does (answers kept for later are only good for one version)

    public bool Ready { get; private set; }
    public int Count => _count;

    static string IndexFile => Path.Combine(Settings.Folder, "files.idx");
    static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    const int MaxEntries = 900_000, MaxPerFolder = 6000, MatchCap = 40_000;

    readonly List<FileSystemWatcher> _watchers = new();
    volatile bool _rescanWanted;

    // ───────────────────────────── what's never worth showing ─────────────────────────────

    /// <summary>System, build and game-data files: nobody searches for these, and they drown what you're looking for.</summary>
    static readonly HashSet<string> JunkExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "dll", "sys", "drv", "ocx", "cpl", "efi", "mui", "nls", "cat", "inf", "manifest", "mum", "winmd", "tlb", "ax", "rll", "resources",
        "pyd", "pyc", "pyo", "so", "dylib", "lib", "a", "obj", "o", "pdb", "ilk", "exp", "idb", "ipdb", "iobj", "tlog", "class", "map", "nupkg",
        "cache", "tmp", "temp", "dmp", "mdmp", "hprof", "etl", "blf", "regtrans-ms", "edb", "jfm", "chk", "log1", "log2", "ldb", "wal", "shm",
        "db-wal", "db-shm", "db-journal", "lock", "crdownload", "part", "partial", "!ut", "download",
        "dat", "bin", "pak", "uasset", "umap", "ucas", "utoc", "bundle", "assets", "ress", "resource", "vpk", "bsa", "ba2", "sig", "cab", "msp",
        "blob", "pack", "idx", "vdf", "acf", "bik", "bk2", "wem", "bnk", "pck", "rpf", "forge", "ff", "big", "arc", "ytd", "ydr", "ydd",
    };

    static readonly HashSet<string> JunkName = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "thumbs.db", ".ds_store", "ntuser.dat", "iconcache.db" };

    public static bool IsJunk(string name, bool folder)
    {
        if (folder) return false;
        if (name.StartsWith("~$") || JunkName.Contains(name)) return true;
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 && JunkExt.Contains(name[(dot + 1)..]);
    }

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
                // the first build only waits a moment (a new PC should find everything soon); a refresh waits for the PC to settle
                Thread.Sleep(loaded ? 90_000 : 8_000);
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
        "Cache", "Caches", "GPUCache", "Code Cache", "ShaderCache", "CachedData", "Crashpad", "CrashDumps", "DerivedDataCache", "Intermediate",
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
                            if (IsJunk(name, false)) continue;
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
            BuildBuffers();
        }
    }

    static int Minutes(DateTime utc) => utc < Epoch ? 0 : (int)Math.Min(int.MaxValue - 2, (utc - Epoch).TotalMinutes);

    /// <summary>The block of folded names and the block of initials, and each folder's lift (called under the lock).</summary>
    void BuildBuffers()
    {
        int total = 0;
        foreach (var n in _names) total += n.Length + 1;
        _buf = new char[Math.Max(16, total + total / 8)];
        _ini = new char[Math.Max(16, _names.Count * 4)];
        _start = new List<int>(_names.Count + 1024);
        _iniStart = new List<int>(_names.Count + 1024);
        _bufLen = _iniLen = 0;
        foreach (var n in _names) Append(n);
        _dirBonus = new List<int>(_dirs.Count + 256);
        foreach (var d in _dirs) _dirBonus.Add(DirBonus(d));
        _version++;
    }

    void Append(string name)
    {
        string f = TextFold.Fold(name);
        if (_bufLen + f.Length + 1 > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _bufLen + f.Length + 1));
        _start.Add(_bufLen);
        f.AsSpan().CopyTo(_buf.AsSpan(_bufLen));
        _bufLen += f.Length;
        _buf[_bufLen++] = '\n';
        string ini = TextFold.Initials(Path.GetFileNameWithoutExtension(name));
        if (_iniLen + ini.Length + 1 > _ini.Length) Array.Resize(ref _ini, Math.Max(_ini.Length * 2, _iniLen + ini.Length + 1));
        _iniStart.Add(_iniLen);
        ini.AsSpan().CopyTo(_ini.AsSpan(_iniLen));
        _iniLen += ini.Length;
        _ini[_iniLen++] = '\n';
    }

    static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string[] YourPlaces = new[]
    {
        Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
        Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic,
    }.Select(Environment.GetFolderPath).Append(Path.Combine(Profile, "Downloads")).Where(p => p.Length > 0).ToArray();

    /// <summary>Your Desktop, Documents, Downloads, Pictures, Videos and Music lift a little; deep folders sink a little.</summary>
    static int DirBonus(string dir)
    {
        int bonus = 0;
        foreach (var place in YourPlaces)
            if (dir.StartsWith(place, StringComparison.OrdinalIgnoreCase)) { bonus = 160; break; }
        if (bonus == 0 && Profile.Length > 0 && dir.StartsWith(Profile, StringComparison.OrdinalIgnoreCase)) bonus = 60;
        int depth = 0;
        foreach (char c in dir) if (c == '\\') depth++;
        return bonus - Math.Min(150, Math.Max(0, depth - 3) * 14);
    }

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
                    w.Deleted += (_, _) => _dirty = true; // results are checked on disk before they're shown
                    w.Renamed += (_, e) => Added(e.FullPath);
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
            if (IsJunk(name, isDir)) return;
            int when = Minutes(DateTime.UtcNow);
            lock (_gate)
            {
                if (_count >= MaxEntries) return;
                if (!_dirIndex.TryGetValue(folder, out int di)) { _dirs.Add(folder); _dirBonus.Add(DirBonus(folder)); di = _dirIndex[folder] = _dirs.Count - 1; }
                _names.Add(name); _parent.Add(di); _minutes.Add(isDir ? -1 - when : when); _gone.Add(false);
                Append(name);
                _count = _names.Count;
                if (isDir && !_dirIndex.ContainsKey(path)) { _dirs.Add(path); _dirBonus.Add(DirBonus(path)); _dirIndex[path] = _dirs.Count - 1; }
                _version++;
            }
            _dirty = true;
        }
        catch { }
    }

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
            for (int i = 0; i < n; i++)
            {
                string name = r.ReadString(); int p = r.ReadInt32(), m = r.ReadInt32();
                if (IsJunk(name, m < 0)) continue; // a list saved before the junk was left out: cleaned on the way in
                names.Add(name); parent.Add(p); minutes.Add(m);
            }
            lock (_gate)
            {
                _dirs = dirs; _dirIndex = dirIndex; _names = names; _parent = parent; _minutes = minutes;
                _gone = new List<bool>(new bool[names.Count]);
                _count = names.Count;
                BuildBuffers();
            }
            return true;
        }
        catch (Exception ex) { AppLog.Write("file index load: " + ex.Message); return false; }
    }

    /// <summary>Saves what changed since (called now and then, and when the notch closes).</summary>
    public void SaveIfChanged() { if (_dirty && Ready) Save(); }

    // ───────────────────────────── asking ─────────────────────────────

    sealed record Kept(string Terms, string FilterKey, List<int> Matches, bool Complete, int Version);
    readonly Dictionary<string, Kept> _kept = new();
    Kept? _last;

    /// <summary>
    /// Files and folders whose name holds every word typed (in any order, accents ignored), or whose initials start
    /// with it ("ic" → InvoiceCalc), and that pass the filter; best first: the whole name, its start, a word's start,
    /// anywhere; your own folders and recent things before deep and old ones. Thread-safe; a few milliseconds.
    /// </summary>
    public List<Hit> Find(string terms, FileFilter? filter, int max = 40)
    {
        string folded = TextFold.Fold(terms.Trim());
        var typed = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        if (typed.Length == 0 && filter == null) return new();
        if (filter == null && typed.Sum(w => w.Length) < 2) return new();
        var byLength = typed.OrderByDescending(w => w.Length).ToArray();
        var rt = Runtime(filter);
        var picked = new List<(string Path, string Name, bool Folder, DateTime When)>();
        lock (_gate)
        {
            var matches = Matches(folded, byLength, filter, rt);
            int want = filter?.MinSize > 0 ? 300 : max * 2;
            foreach (int i in Rank(matches, folded, typed, want))
            {
                int m = _minutes[i];
                bool folder = m < 0;
                picked.Add((Path.Combine(_dirs[_parent[i]], _names[i]), _names[i], folder, Epoch.AddMinutes(folder ? -1 - m : m).ToLocalTime()));
            }
        }
        // still there, and big enough? (deleted things stop showing at once; sizes are only read for the few that count)
        var hits = new List<Hit>(max);
        foreach (var p in picked)
        {
            if (hits.Count >= max) break;
            try
            {
                if (p.Folder ? !Directory.Exists(p.Path) : !File.Exists(p.Path)) continue;
                if (filter?.MinSize > 0 && (p.Folder || new FileInfo(p.Path).Length < filter.MinSize)) continue;
            }
            catch { continue; }
            hits.Add(new Hit(p.Path, p.Name, p.Folder, p.When));
        }
        return hits;
    }

    /// <summary>The filter in the list's own terms: extensions, and dates as minutes.</summary>
    sealed record FilterRt(string[]? Exts, bool Folders, int After, int Before);

    static FilterRt? Runtime(FileFilter? f) => f == null ? null : new FilterRt(f.Exts?.ToArray(), f.FoldersOnly,
        f.After is { } a ? Minutes(a.ToUniversalTime()) : int.MinValue, f.Before is { } b ? Minutes(b.ToUniversalTime()) : int.MaxValue);

    bool Allowed(int i, FilterRt? rt)
    {
        if (_gone[i]) return false;
        if (rt == null) return true;
        int m = _minutes[i];
        bool folder = m < 0;
        if (rt.Folders != folder && (rt.Folders || rt.Exts != null)) return false;
        int when = folder ? -1 - m : m;
        if (when < rt.After || when >= rt.Before) return false;
        if (rt.Exts != null && !folder)
        {
            var name = _names[i].AsSpan();
            int dot = name.LastIndexOf('.');
            if (dot < 0) return false;
            var ext = name[(dot + 1)..];
            bool ok = false;
            foreach (var e in rt.Exts) if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
            if (!ok) return false;
        }
        return true;
    }

    ReadOnlySpan<char> NameSpan(int i)
    {
        int s = _start[i];
        int e = i + 1 < _start.Count ? _start[i + 1] - 1 : _bufLen - 1;
        return _buf.AsSpan(s, e - s);
    }

    int EntryAt(List<int> starts, int pos)
    {
        int lo = 0, hi = starts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (starts[mid] <= pos) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    List<int> Matches(string folded, string[] words, FileFilter? filter, FilterRt? rt)
    {
        string fk = filter?.Key ?? "";
        string key = folded + "|" + fk;
        if (_kept.TryGetValue(key, out var known) && known.Version == _version) return known.Matches;

        var result = new List<int>();
        bool complete = true;
        // a letter added to what was just asked: only those can still match
        if (_last is { Complete: true } last && last.Version == _version && last.FilterKey == fk && last.Terms.Length > 2 && folded.StartsWith(last.Terms, StringComparison.Ordinal))
        {
            foreach (int i in last.Matches)
                if (HasAll(i, words)) result.Add(i);
        }
        else if (words.Length == 0)
        {
            for (int i = 0; i < _count; i++)
                if (Allowed(i, rt)) { result.Add(i); if (result.Count >= MatchCap) { complete = false; break; } }
        }
        else
        {
            // the longest word, found across every name in one pass over the block
            string w0 = words[0];
            var buf = _buf.AsSpan(0, _bufLen);
            int p = 0;
            while (p < buf.Length)
            {
                int at = buf[p..].IndexOf(w0.AsSpan());
                if (at < 0) break;
                int pos = p + at;
                int i = EntryAt(_start, pos);
                // one or two letters: only where a word starts ("in" → Invoice, not "main"); more: anywhere
                bool startsWord = pos == _start[i] || !char.IsLetterOrDigit(buf[pos - 1]);
                if (startsWord || folded.Length > 2)
                {
                    if (Allowed(i, rt) && HasAll(i, words, skipFirst: true)) { result.Add(i); if (result.Count >= MatchCap) { complete = false; break; } }
                    p = i + 1 < _start.Count ? _start[i + 1] : buf.Length; // on to the next name
                }
                else p = pos + 1; // a later word in the same name may still start with it
            }
            // initials: "ic" finds InvoiceCalc (one short word only)
            if (words.Length == 1 && w0.Length is >= 2 and <= 6 && complete)
            {
                var have = new HashSet<int>(result);
                var ini = _ini.AsSpan(0, _iniLen);
                p = 0;
                while (p < ini.Length)
                {
                    int at = ini[p..].IndexOf(w0.AsSpan());
                    if (at < 0) break;
                    int pos = p + at;
                    int i = EntryAt(_iniStart, pos);
                    if (_iniStart[i] == pos && have.Add(i) && Allowed(i, rt)) result.Add(i);
                    p = i + 1 < _iniStart.Count ? _iniStart[i + 1] : ini.Length;
                }
            }
        }
        var kept = new Kept(folded, fk, result, complete, _version);
        _last = kept;
        if (_kept.Count >= 24) _kept.Clear();
        _kept[key] = kept;
        return result;
    }

    bool HasAll(int i, string[] words, bool skipFirst = false)
    {
        var name = NameSpan(i);
        for (int k = skipFirst ? 1 : 0; k < words.Length; k++)
            if (name.IndexOf(words[k].AsSpan()) < 0 && !InitialsStart(i, words[k])) return false;
        return true;
    }

    bool InitialsStart(int i, string w)
    {
        int s = _iniStart[i];
        int e = i + 1 < _iniStart.Count ? _iniStart[i + 1] - 1 : _iniLen - 1;
        return _ini.AsSpan(s, e - s).StartsWith(w.AsSpan());
    }

    /// <summary>The best of the matches: how well the name fits, your own folders, how recent; only the top few are sorted.</summary>
    IEnumerable<int> Rank(List<int> matches, string folded, string[] typed, int take)
    {
        int now = Minutes(DateTime.UtcNow);
        string first = typed.Length > 0 ? typed[0] : "";
        var top = new PriorityQueue<int, long>();
        int seen = 0;
        foreach (int i in matches)
        {
            if (++seen > MatchCap) break;
            int fit = 0;
            if (first.Length > 0)
            {
                var name = NameSpan(i);
                int dot = name.LastIndexOf('.');
                var stem = dot > 0 ? name[..dot] : name;
                fit = stem.SequenceEqual(folded.AsSpan()) || name.SequenceEqual(folded.AsSpan()) ? 5
                    : name.StartsWith(first.AsSpan()) ? 4
                    : WordStart(name, first) || InitialsStart(i, first) ? 3 : 1;
            }
            int m = _minutes[i];
            int when = m < 0 ? -1 - m : m;
            int age = now - when;
            int fresh = age < 7 * 1440 ? 140 : age < 30 * 1440 ? 70 : age < 365 * 1440 ? 25 : 0;
            long score = fit * 1000L + _dirBonus[_parent[i]] + fresh;
            // with no words (a filter alone), the newest first
            if (first.Length == 0) score = when;
            top.Enqueue(i, score);
            if (top.Count > take) top.Dequeue();
        }
        var list = new List<(int I, long S)>();
        while (top.TryDequeue(out int i, out long s)) list.Add((i, s));
        return list.OrderByDescending(x => x.S).Select(x => x.I);
    }

    static bool WordStart(ReadOnlySpan<char> name, string w)
    {
        for (int i = 1; i <= name.Length - w.Length; i++)
            if (!char.IsLetterOrDigit(name[i - 1]) && name[i..].StartsWith(w.AsSpan())) return true;
        return false;
    }
}
