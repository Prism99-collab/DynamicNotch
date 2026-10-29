using System.IO;
using System.Windows.Threading;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch.Services;

/// <summary>Downloads in progress / finished, new screenshots, and the "recent files" shelf.</summary>
public sealed class FilesService
{
    static readonly string[] TempExts = { ".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp" };

    readonly Dispatcher _ui;
    readonly Dictionary<string, long> _active = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer _sizeTimer;
    FileSystemWatcher? _dl, _shots;

    public string? DownloadsDir { get; }
    public string? ScreenshotsDir { get; }

    public int ActiveDownloads => _active.Count;
    public long ActiveBytes { get; private set; }
    public double BytesPerSecond { get; private set; }

    public event Action? DownloadsChanged;
    public event Action<string>? DownloadCompleted;
    public event Action<string>? ScreenshotSaved;

    public FilesService(Dispatcher ui)
    {
        _ui = ui;
        DownloadsDir = KnownFolder(FOLDERID_Downloads);
        ScreenshotsDir = KnownFolder(FOLDERID_Screenshots);
        _sizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _sizeTimer.Tick += (_, _) => MeasureActive();
    }

    public void Start()
    {
        try
        {
            if (DownloadsDir != null && Directory.Exists(DownloadsDir))
            {
                _dl = new FileSystemWatcher(DownloadsDir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
                _dl.Created += (_, e) => _ui.InvokeAsync(() => { if (IsTemp(e.FullPath)) Track(e.FullPath); });
                _dl.Renamed += (_, e) => _ui.InvokeAsync(() => OnRenamed(e.OldFullPath, e.FullPath));
                _dl.Deleted += (_, e) => _ui.InvokeAsync(() => OnDeleted(e.FullPath));
                _dl.EnableRaisingEvents = true;
            }
        }
        catch { }
        try
        {
            if (ScreenshotsDir != null && Directory.Exists(ScreenshotsDir))
            {
                _shots = new FileSystemWatcher(ScreenshotsDir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
                _shots.Created += (_, e) =>
                {
                    if (!IconHelper.IsImage(e.FullPath)) return;
                    _ui.InvokeAsync(async () => { await Task.Delay(400); ScreenshotSaved?.Invoke(e.FullPath); });
                };
                _shots.EnableRaisingEvents = true;
            }
        }
        catch { }
    }

    static bool IsTemp(string path) => TempExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    void Track(string path)
    {
        if (_active.ContainsKey(path)) return;
        _active[path] = 0;
        if (!_sizeTimer.IsEnabled) _sizeTimer.Start();
        DownloadsChanged?.Invoke();
    }

    void OnRenamed(string oldPath, string newPath)
    {
        bool wasTemp = IsTemp(oldPath), isTemp = IsTemp(newPath);
        if (wasTemp) _active.Remove(oldPath);
        if (isTemp) Track(newPath);
        else if (wasTemp) Completed(newPath);
        if (wasTemp || isTemp) DownloadsChanged?.Invoke();
        StopIfIdle();
    }

    void OnDeleted(string path)
    {
        if (!_active.Remove(path)) return;
        // Firefox: "file.zip.part" disappears once "file.zip" is complete
        string final = path[..^Path.GetExtension(path).Length];
        if (File.Exists(final) && !IsTemp(final)) Completed(final);
        DownloadsChanged?.Invoke();
        StopIfIdle();
    }

    void Completed(string path)
    {
        if (Path.GetFileName(path).StartsWith("Unconfirmed", StringComparison.OrdinalIgnoreCase)) return;
        DownloadCompleted?.Invoke(path);
    }

    void StopIfIdle()
    {
        if (_active.Count > 0) return;
        _sizeTimer.Stop();
        ActiveBytes = 0;
        BytesPerSecond = 0;
    }

    void MeasureActive()
    {
        long total = 0;
        foreach (var p in _active.Keys.ToList())
        {
            try
            {
                var fi = new FileInfo(p);
                if (!fi.Exists) { _active.Remove(p); continue; }
                total += fi.Length;
            }
            catch { }
        }
        BytesPerSecond = BytesPerSecond * 0.5 + Math.Max(0, total - ActiveBytes) * 0.5;
        ActiveBytes = total;
        StopIfIdle();
        DownloadsChanged?.Invoke();
    }

    /// <summary>Newest files across Downloads, Screenshots and Desktop.</summary>
    public Task<List<FileInfo>> RecentAsync(int count) => Task.Run(() =>
    {
        var dirs = new[] { DownloadsDir, ScreenshotsDir, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        var all = new List<FileInfo>();
        foreach (var d in dirs.Where(d => d != null && Directory.Exists(d)).Distinct())
        {
            try
            {
                all.AddRange(new DirectoryInfo(d!).EnumerateFiles()
                    .Where(f => !IsTemp(f.FullName) && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0
                                && !f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) && f.Extension != ".lnk")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(count));
            }
            catch { }
        }
        return all.OrderByDescending(f => f.LastWriteTimeUtc).Take(count).ToList();
    });
}
