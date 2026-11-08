using System.Runtime.InteropServices;

namespace DynamicNotch.Services;

/// <summary>
/// How hard the PC is working, without administrator rights: CPU and GPU load and GPU temperature (the same readers
/// as game mode, <see cref="GameService"/>), memory in use, and the hottest ACPI thermal zone (Windows' "Thermal
/// Zone Information" counter, the closest thing to a CPU temperature Windows gives without a driver; not every PC
/// has one). Call <see cref="Sample"/> off the UI thread, a few seconds apart.
/// </summary>
public sealed class StrainService : IDisposable
{
    /// <param name="GpuKnown">false: this PC's driver doesn't report GPU load (Gpu is 0 then, and must not be shown)</param>
    public sealed record Reading(double Cpu, double Gpu, double Ram, double? GpuTemp, double? CpuTemp, bool GpuKnown = true);

    readonly GameService _gpu = new() { TrackProcesses = true };
    readonly object _gate = new();
    IntPtr _query, _zones;
    bool _zonesTried;

    /// <summary>Each process's GPU load at the last <see cref="Sample"/> (process id → %).</summary>
    public IReadOnlyDictionary<uint, double> GpuByProcess => _gpu.GpuByProcess;

    public Reading Sample()
    {
        var s = _gpu.Sample(0);
        double? cpuTemp;
        lock (_gate) cpuTemp = Trusted(ThermalZone(), s.Cpu);
        return new Reading(s.Cpu, s.Gpu, MemoryLoad(), s.TempC, cpuTemp, GpuAvailable);
    }

    /// <summary>Does this PC report GPU load? False on a driver without Windows' GPU counters (then GPU stays hidden).</summary>
    public bool GpuAvailable => _gpu.GpuAvailable != false;

    // a thermal zone that never moves isn't a thermometer: many laptops' firmware reports a fixed value (often
    // 27.8 °C, ACPI's default) or a zone that isn't near the CPU at all. If it hasn't changed by a single degree in
    // 8 minutes while the CPU load swung by 50 points, it's stuck: hidden for the rest of the session
    readonly List<(DateTime At, double Temp, double Cpu)> _zoneSeen = new();
    bool _zoneStuck;

    double? Trusted(double? temp, double cpu)
    {
        if (temp is not { } t || _zoneStuck) return null;
        var now = DateTime.UtcNow;
        _zoneSeen.Add((now, t, cpu));
        _zoneSeen.RemoveAll(x => now - x.At > TimeSpan.FromMinutes(10));
        if (_zoneSeen.Count >= 20 && now - _zoneSeen[0].At > TimeSpan.FromMinutes(8)
            && _zoneSeen.Max(x => x.Temp) - _zoneSeen.Min(x => x.Temp) < 1 && _zoneSeen.Max(x => x.Cpu) - _zoneSeen.Min(x => x.Cpu) >= 50)
        {
            _zoneStuck = true;
            _available = false;
            AppLog.Write($"cpu temperature: the thermal zone stays at {t:0}°C whatever the load: not a real reading, hidden");
            return null;
        }
        return t;
    }

    /// <summary>Does this PC report a thermal zone temperature at all?</summary>
    public static bool CpuTempAvailable
    {
        get
        {
            if (_available is { } a) return a;
            try
            {
                using var probe = new StrainService();
                probe.ThermalZone();
                _available = probe.ThermalZone() != null;
            }
            catch { _available = false; }
            return _available.Value;
        }
    }
    static bool? _available;

    static double MemoryLoad()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? m.dwMemoryLoad : 0;
    }

    /// <summary>The hottest thermal zone in °C, or null when the PC has none (or reports nonsense).</summary>
    double? ThermalZone()
    {
        if (!_zonesTried)
        {
            _zonesTried = true;
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return null; }
            if (PdhAddEnglishCounter(_query, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out _zones) != 0)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                return null;
            }
        }
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArray(_zones, PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero) != PDH_MORE_DATA || size == 0) return null;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_zones, PDH_FMT_DOUBLE, ref size, ref count, buf) != 0) return null;
            double best = 0;
            int item = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var v = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * item);
                if (v.Status != 0) continue;
                double c = v.Value - 273.15; // Kelvin
                if (c > best && c < 125) best = c;
            }
            // under 28 °C a "CPU temperature" is ACPI's placeholder (27.8 °C) or a zone far from the CPU, not a reading
            return best >= 28.5 ? Math.Round(best) : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
        }
        _gpu.Dispose();
    }

    const uint PDH_FMT_DOUBLE = 0x00000200;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr Name; public uint Status; public double Value; }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
}
