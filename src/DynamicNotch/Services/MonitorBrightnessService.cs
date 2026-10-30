using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DynamicNotch.Services;

/// <summary>
/// Brightness of an external monitor through DDC/CI (the laptop's own panel goes through WMI in
/// <see cref="BrightnessService"/>). DDC/CI calls are slow (tens of ms each), so they all run off the UI
/// thread, one at a time, and only the latest slider value is ever written.
/// </summary>
public sealed class MonitorBrightnessService
{
    readonly Dispatcher _ui;
    readonly object _gate = new();
    PHYSICAL_MONITOR[] _handles = Array.Empty<PHYSICAL_MONITOR>();
    // physical handle being controlled and the screen it belongs to. Windows numbers physical monitor handles
    // from 0 (right after a boot the first monitor IS handle 0), so "found" is tracked on its own, never as handle != 0.
    IntPtr _target, _targetMonitor;
    bool _hasTarget;
    readonly HashSet<IntPtr> _noDdc = new(); // screens that have no external monitor to talk to (the laptop panel)
    int _pending = -1, _busy, _min, _max = 100, _retryVersion;
    volatile bool _supported;

    public bool Supported { get; private set; }
    public int Level { get; private set; }

    public event Action? Changed;

    public MonitorBrightnessService(Dispatcher ui) => _ui = ui;

    /// <summary>
    /// Reads the monitor's level, finding the monitor first when needed (<paramref name="rescan"/>: after startup,
    /// a display change or waking up). DDC/CI is a slow, sometimes flaky link: every step is retried, and a
    /// missed answer never hides the slider. It disappears only when no external monitor answers at all.
    /// </summary>
    /// <param name="preferred">The monitor you're using right now (e.g. the foreground window's); used when it speaks DDC/CI.</param>
    public Task RefreshAsync(IntPtr preferred = default, bool rescan = false) => Task.Run(() =>
    {
        bool found, ok = false;
        int level = 0;
        lock (_gate)
        {
            // working on another external screen than the one we hold: look again
            if (preferred != IntPtr.Zero && preferred != _targetMonitor && !_noDdc.Contains(preferred)) rescan = true;
            if (rescan || !_hasTarget)
            {
                // a monitor that just powered on (cold boot, wake) can take a moment before it answers
                for (int attempt = 0; attempt < 3 && !Scan(preferred); attempt++) Thread.Sleep(400);
            }
            found = _hasTarget;
            for (int attempt = 0; found && attempt < 3 && !(ok = Read(out level)); attempt++) Thread.Sleep(120);
        }
        _ui.InvokeAsync(() =>
        {
            // a found monitor that missed one answer keeps its slider; it goes only when no monitor answers at all
            bool supported = found && (ok || Supported);
            bool changed = supported != Supported || (ok && level != Level);
            if (supported != Supported) AppLog.Write(supported ? $"monitor brightness: found ({level}%)" : "monitor brightness: no external monitor answers");
            Supported = _supported = supported;
            if (ok) Level = level;
            if (changed) Changed?.Invoke();
        });
    });

    /// <summary>
    /// After startup, waking up or a display change: keeps looking for the external monitor for about two
    /// minutes (it may still be powering on), then stops. A newer call replaces the previous one.
    /// </summary>
    public void RetryUntilFound(Func<IntPtr> preferred)
    {
        int version = Interlocked.Increment(ref _retryVersion);
        _ = Task.Run(async () =>
        {
            foreach (int seconds in new[] { 3, 6, 10, 15, 25, 30, 40 })
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                if (version != Volatile.Read(ref _retryVersion) || _supported) return;
                IntPtr mon = IntPtr.Zero;
                try { mon = await _ui.InvokeAsync(preferred); } catch { }
                await RefreshAsync(mon, rescan: true);
            }
        });
    }

    /// <summary>Lists the screens and picks the external monitor to control. Returns false when none answers.</summary>
    bool Scan(IntPtr preferred)
    {
        Release();
        _noDdc.Clear();
        var found = new List<(IntPtr Mon, bool Primary, PHYSICAL_MONITOR P)>();
        var all = new List<PHYSICAL_MONITOR>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
        {
            try
            {
                bool any = false;
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(m, out uint n) && n > 0)
                {
                    var arr = new PHYSICAL_MONITOR[n];
                    if (GetPhysicalMonitorsFromHMONITOR(m, n, arr))
                    {
                        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                        bool primary = GetMonitorInfo(m, ref info) && (info.dwFlags & 1) != 0;
                        all.AddRange(arr);
                        foreach (var p in arr)
                        {
                            bool answers = GetMonitorBrightness(p.hPhysicalMonitor, out _, out _, out _);
                            if (answers) { found.Add((m, primary, p)); any = true; }
                        }
                    }
                }
                if (!any) _noDdc.Add(m);
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        _handles = all.ToArray();

        // the monitor in use, else the primary one, else any external screen that answers
        int i = found.FindIndex(f => f.Mon == preferred);
        if (i < 0) i = found.FindIndex(f => f.Primary);
        if (i < 0 && found.Count > 0) i = 0;
        if (i < 0) return false;
        _target = found[i].P.hPhysicalMonitor;
        _targetMonitor = found[i].Mon;
        _hasTarget = true;
        return true;
    }

    bool Read(out int level)
    {
        level = 0;
        if (!GetMonitorBrightness(_target, out uint min, out uint cur, out uint max) || max <= min) return false;
        _min = (int)min;
        _max = (int)max;
        level = (int)Math.Round((cur - min) * 100.0 / (max - min));
        return true;
    }

    /// <summary>0–100. Called continuously while dragging; only the latest value is written.</summary>
    public void Set(int level)
    {
        if (!Supported) return;
        level = Math.Clamp(level, 0, 100);
        Level = level;
        Interlocked.Exchange(ref _pending, level);
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(() =>
        {
            while (true)
            {
                int v = Interlocked.Exchange(ref _pending, -1);
                if (v >= 0)
                {
                    lock (_gate)
                        if (_hasTarget)
                            try { SetMonitorBrightness(_target, (uint)Math.Round(_min + v * (_max - _min) / 100.0)); } catch { }
                    continue;
                }
                Interlocked.Exchange(ref _busy, 0);
                // a value may have arrived between the check and releasing the flag
                if (Volatile.Read(ref _pending) < 0 || Interlocked.Exchange(ref _busy, 1) == 1) return;
            }
        });
    }

    void Release()
    {
        if (_handles.Length > 0) try { DestroyPhysicalMonitors((uint)_handles.Length, _handles); } catch { }
        _handles = Array.Empty<PHYSICAL_MONITOR>();
        _target = _targetMonitor = IntPtr.Zero;
        _hasTarget = false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public Interop.Native.RECT rcMonitor, rcWork; public int dwFlags; }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);
}
