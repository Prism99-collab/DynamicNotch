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
    IntPtr _target; // physical monitor handle being controlled
    int _pending = -1, _busy, _min, _max = 100;

    public bool Supported { get; private set; }
    public int Level { get; private set; }

    public event Action? Changed;

    public MonitorBrightnessService(Dispatcher ui) => _ui = ui;

    /// <summary>Finds the external monitor to control (again after display changes) and reads its level.</summary>
    /// <param name="preferred">The monitor you're using right now (e.g. the foreground window's); used when it speaks DDC/CI.</param>
    public Task RefreshAsync(IntPtr preferred = default) => Task.Run(() =>
    {
        bool ok;
        int level = 0;
        lock (_gate)
        {
            Release();
            var found = new List<(IntPtr Mon, bool Primary, PHYSICAL_MONITOR P)>();
            var all = new List<PHYSICAL_MONITOR>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
            {
                try
                {
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(m, out uint n) || n == 0) return true;
                    var arr = new PHYSICAL_MONITOR[n];
                    if (!GetPhysicalMonitorsFromHMONITOR(m, n, arr)) return true;
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    bool primary = GetMonitorInfo(m, ref info) && (info.dwFlags & 1) != 0;
                    all.AddRange(arr);
                    foreach (var p in arr)
                        if (GetMonitorBrightness(p.hPhysicalMonitor, out _, out _, out _)) found.Add((m, primary, p));
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            _handles = all.ToArray();

            // the monitor in use, else the primary one, else any external screen that answers
            var pick = found.FirstOrDefault(f => f.Mon == preferred);
            if (pick.P.hPhysicalMonitor == IntPtr.Zero) pick = found.FirstOrDefault(f => f.Primary);
            if (pick.P.hPhysicalMonitor == IntPtr.Zero) pick = found.FirstOrDefault();
            _target = pick.P.hPhysicalMonitor;
            ok = _target != IntPtr.Zero && Read(out level);
        }
        _ui.InvokeAsync(() =>
        {
            bool changed = ok != Supported || (ok && level != Level);
            Supported = ok;
            if (ok) Level = level;
            if (changed) Changed?.Invoke();
        });
    });

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
                        if (_target != IntPtr.Zero)
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
        _target = IntPtr.Zero;
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
