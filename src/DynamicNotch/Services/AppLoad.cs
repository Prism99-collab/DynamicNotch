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

    Dictionary<int, long> _last = new();
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
        var times = new Dictionary<int, long>(_last.Count + 16);
        var groups = new Dictionary<string, (int Count, double Cpu, double Gpu, long Mem, int Pid, bool Mine)>(StringComparer.OrdinalIgnoreCase);
        int me = Environment.ProcessId;
        // every process with its name, session, CPU time and memory in one call to Windows: nothing is opened per
        // process (opening hundreds of them, many refusing, was most of the page's cost on a slow laptop)
        foreach (var p in Snapshot())
        {
            if (p.Pid == 0 || p.Pid == me) continue;
            long t = p.Time;
            times[p.Pid] = t;
            double cpu = wallMs > 0 && _last.TryGetValue(p.Pid, out var t0) ? Math.Max(0, (t - t0) / 10000.0 / wallMs * 100) : 0;
            double gpu = gpuByPid?.GetValueOrDefault((uint)p.Pid) ?? 0;
            var g = groups.GetValueOrDefault(p.Name);
            groups[p.Name] = (g.Count + 1, g.Cpu + cpu, g.Gpu + gpu, g.Mem + p.Mem, g.Pid == 0 ? p.Pid : g.Pid, g.Mine || p.Session == _session);
        }
        _last = times;
        _lastAt = now;
        // only the apps that could make the list are looked up (where they live, their proper name): the rest never show
        var notable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var by in new Func<KeyValuePair<string, (int Count, double Cpu, double Gpu, long Mem, int Pid, bool Mine)>, double>[] { kv => kv.Value.Cpu, kv => kv.Value.Gpu, kv => kv.Value.Mem })
            foreach (var kv in groups.OrderByDescending(by).Take(10)) notable.Add(kv.Key);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var list = new List<App>(groups.Count);
        foreach (var (name, g) in groups)
        {
            string? path = null;
            string display = name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
            if (_paths.TryGetValue(name, out var known)) path = known;
            else if (notable.Contains(name)) _paths[name] = path = PathOf(g.Pid);
            if (_names.TryGetValue(name, out var dn)) display = dn;
            else if (notable.Contains(name)) _names[name] = display = DisplayName(name, path);
            list.Add(new App(name, display, path, g.Count, Math.Min(100, g.Cpu), Math.Min(100, g.Gpu), g.Mem / (1024 * 1024),
                g.Mine && !Protected.Contains(name) && path != null && !path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }

    readonly int _session = Process.GetCurrentProcess().SessionId;
    IntPtr _buf;
    int _bufSize;

    record struct Proc(int Pid, string Name, int Session, long Time, long Mem);

    /// <summary>All processes from NtQuerySystemInformation(SystemProcessInformation): CPU time in 100 ns units, working set in bytes.</summary>
    List<Proc> Snapshot()
    {
        var list = new List<Proc>(400);
        if (IntPtr.Size != 8) return list; // the layout read below is the 64-bit one (x64 and arm64 builds)
        for (int tries = 0; tries < 4; tries++)
        {
            if (_buf == IntPtr.Zero) { _bufSize = 512 * 1024; _buf = Marshal.AllocHGlobal(_bufSize); }
            int status = NtQuerySystemInformation(5, _buf, _bufSize, out int needed);
            if (status == unchecked((int)0xC0000004)) // more processes than room: a bigger buffer, kept for next time
            {
                Marshal.FreeHGlobal(_buf);
                _bufSize = Math.Max(needed, _bufSize) + 64 * 1024;
                _buf = Marshal.AllocHGlobal(_bufSize);
                continue;
            }
            if (status != 0) return list;
            IntPtr e = _buf;
            while (true)
            {
                // SYSTEM_PROCESS_INFORMATION
                int next = Marshal.ReadInt32(e, 0);
                long cpu = Marshal.ReadInt64(e, 40) + Marshal.ReadInt64(e, 48);   // user + kernel
                int nameLen = Marshal.ReadInt16(e, 56) & 0xFFFF;
                IntPtr namePtr = Marshal.ReadIntPtr(e, 64);
                int pid = (int)Marshal.ReadInt64(e, 80);
                int session = Marshal.ReadInt32(e, 100);
                long ws = Marshal.ReadInt64(e, 144);
                string name = pid == 4 ? "System" : "";
                if (nameLen > 0 && namePtr != IntPtr.Zero)
                {
                    name = Marshal.PtrToStringUni(namePtr, nameLen / 2);
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                }
                list.Add(new Proc(pid, name, session, cpu, ws));
                if (next <= 0) break;
                e += next;
            }
            return list;
        }
        return list;
    }

    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

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
