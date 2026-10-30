using System.Runtime.InteropServices;

namespace DynamicNotch.Services;

/// <summary>
/// What a game is doing to the PC, without administrator rights: GPU load (Windows' "GPU Engine" performance
/// counters, the same numbers as Task Manager, total and for the game's own process), GPU temperature (from the
/// graphics driver, like Task Manager's GPU page) and CPU load. Call <see cref="Sample"/> off the UI thread.
/// </summary>
public sealed class GameService : IDisposable
{
    public sealed record Stats(double Gpu, double GpuApp, double? TempC, double Cpu);

    readonly object _gate = new();
    IntPtr _query, _counter;
    bool _pdhTried;
    long _idle, _kernel, _user;
    uint[]? _adapters;

    /// <summary>One reading. The first call primes the counters (they measure the time between two calls).</summary>
    public Stats Sample(uint pid)
    {
        lock (_gate)
        {
            var (gpu, app) = GpuLoad(pid);
            return new Stats(gpu, app, GpuTemperature(), CpuLoad());
        }
    }

    // ───────────── GPU load: \GPU Engine(*engtype_3D)\Utilization Percentage ─────────────

    (double Total, double App) GpuLoad(uint pid)
    {
        if (!_pdhTried)
        {
            _pdhTried = true;
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return (0, 0); }
            if (PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                return (0, 0);
            }
            PdhCollectQueryData(_query);
            return (0, 0);
        }
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return (0, 0);

        uint size = 0, count = 0;
        int r = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
        if (r != PDH_MORE_DATA || size == 0) return (0, 0);
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, ref count, buf) != 0) return (0, 0);
            double total = 0, app = 0;
            string mine = $"pid_{pid}_";
            int item = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var v = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * item);
                if (v.Status != 0) continue;
                total += v.Value;
                if (pid != 0 && Marshal.PtrToStringUni(v.Name) is { } name && name.StartsWith(mine, StringComparison.Ordinal)) app += v.Value;
            }
            return (Math.Clamp(total, 0, 100), Math.Clamp(app, 0, 100));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ───────────── GPU temperature: the driver's performance data (WDDM 2.4+) ─────────────

    double? GpuTemperature()
    {
        try
        {
            _adapters ??= OpenAdapters();
            double best = 0;
            IntPtr perf = Marshal.AllocHGlobal(PerfDataSize);
            try
            {
                foreach (uint h in _adapters)
                {
                    for (int k = 0; k < PerfDataSize; k++) Marshal.WriteByte(perf, k, 0);
                    var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = h, Type = KMTQAITYPE_ADAPTERPERFDATA, pPrivateDriverData = perf, PrivateDriverDataSize = PerfDataSize };
                    if (D3DKMTQueryAdapterInfo(ref q) != 0) continue;
                    double c = Marshal.ReadInt32(perf, 56) / 10.0; // deci-degrees Celsius
                    if (c > best && c < 130) best = c;
                }
            }
            finally { Marshal.FreeHGlobal(perf); }
            return best > 0 ? best : null; // many drivers don't report it
        }
        catch { return null; }
    }

    static uint[] OpenAdapters()
    {
        var e = new D3DKMT_ENUMADAPTERS2();
        if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return Array.Empty<uint>();
        int size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
        IntPtr buf = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
        try
        {
            e.pAdapters = buf;
            if (D3DKMTEnumAdapters2(ref e) != 0) return Array.Empty<uint>();
            var list = new uint[e.NumAdapters];
            for (int i = 0; i < e.NumAdapters; i++) list[i] = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buf + i * size).hAdapter;
            return list;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ───────────── CPU load: all cores, since the last call ─────────────

    double CpuLoad()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
        long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
        bool first = _idle == 0;
        _idle = idle; _kernel = kernel; _user = user;
        long busy = dk + du; // kernel time includes idle time
        return first || busy <= 0 ? 0 : Math.Clamp((busy - di) * 100.0 / busy, 0, 100);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
            if (_adapters != null)
                foreach (uint h in _adapters) { var c = new D3DKMT_CLOSEADAPTER { hAdapter = h }; D3DKMTCloseAdapter(ref c); }
            _adapters = null;
        }
    }

    // ───────────── interop ─────────────

    const uint PDH_FMT_DOUBLE = 0x200;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    const int KMTQAITYPE_ADAPTERPERFDATA = 62;
    const int PerfDataSize = 64;

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr Name; public uint Status; public double Value; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ADAPTERINFO { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public int PrivateDriverDataSize; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 e);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO q);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER c);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
