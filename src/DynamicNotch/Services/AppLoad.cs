using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicNotch.Services;

/// <summary>
/// What each app is doing to the PC right now: CPU (since the last reading, the way Task Manager counts it), memory,
/// and GPU when given. All of an app's processes are added together ("chrome" ×24 is one line). Call it off the UI
/// thread, a couple of seconds apart: it never sleeps, it compares with the previous call.
/// </summary>
public sealed class AppLoad
{
    public sealed record App(string Name, string Display, string? Path, int Count, double Cpu, double Gpu, long MemoryMb, bool CanEnd);

    Dictionary<int, TimeSpan> _last = new();
    long _lastAt;
    readonly Dictionary<string, string?> _paths = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows' own processes: shown, but never offered to end.</summary>
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm",
        "explorer", "fontdrvhost", "sihost", "ctfmon", "Memory Compression", "audiodg", "spoolsv", "SearchHost",
        "StartMenuExperienceHost", "ShellExperienceHost", "RuntimeBroker", "TextInputHost", "MsMpEng", "SecurityHealthService",
        "conhost", "dllhost", "taskhostw", "WmiPrvSE", "LsaIso", "MemCompression", "SystemSettings",
    };

    public List<App> Sample(IReadOnlyDictionary<uint, double>? gpuByPid)
    {
        long now = Stopwatch.GetTimestamp();
        double wallMs = _lastAt == 0 ? 0 : (now - _lastAt) * 1000.0 / Stopwatch.Frequency * Environment.ProcessorCount;
        var times = new Dictionary<int, TimeSpan>();
        var groups = new Dictionary<string, (int Count, double Cpu, double Gpu, long Mem, int Pid, bool Mine)>(StringComparer.OrdinalIgnoreCase);
        int session = Process.GetCurrentProcess().SessionId, me = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                try
                {
                    if (p.Id == 0 || p.Id == me) continue;
                    string name = p.ProcessName;
                    long mem = 0;
                    try { mem = p.WorkingSet64; } catch { }
                    double cpu = 0;
                    try
                    {
                        var t = p.TotalProcessorTime;
                        times[p.Id] = t;
                        if (wallMs > 0 && _last.TryGetValue(p.Id, out var t0)) cpu = Math.Max(0, (t - t0).TotalMilliseconds / wallMs * 100);
                    }
                    catch { }
                    double gpu = gpuByPid?.GetValueOrDefault((uint)p.Id) ?? 0;
                    bool mine = false;
                    try { mine = p.SessionId == session; } catch { }
                    var g = groups.GetValueOrDefault(name);
                    groups[name] = (g.Count + 1, g.Cpu + cpu, g.Gpu + gpu, g.Mem + mem, g.Pid == 0 ? p.Id : g.Pid, g.Mine || mine);
                }
                catch { }
            }
        _last = times;
        _lastAt = now;
        var list = new List<App>(groups.Count);
        foreach (var (name, g) in groups)
        {
            if (!_paths.TryGetValue(name, out var path)) _paths[name] = path = PathOf(g.Pid);
            if (!_names.TryGetValue(name, out var display)) _names[name] = display = DisplayName(name, path);
            list.Add(new App(name, display, path, g.Count, Math.Min(100, g.Cpu), Math.Min(100, g.Gpu), g.Mem / (1024 * 1024),
                g.Mine && !Protected.Contains(name) && path != null && !path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }

    /// <summary>Closes every process of an app (yours, in this session). False if any refused.</summary>
    public static bool End(string name)
    {
        bool ok = true;
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName(name))
            using (p)
            {
                try { if (p.SessionId == session && p.Id != Environment.ProcessId) p.Kill(entireProcessTree: false); }
                catch { ok = false; }
            }
        return ok;
    }

    /// <summary>"Google Chrome" rather than "chrome": the app's own description, when it has a sensible one.</summary>
    static string DisplayName(string name, string? path)
    {
        try
        {
            if (path != null && FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() is { Length: > 1 and < 40 } d) return d;
        }
        catch { }
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    static string? PathOf(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
