using System.Runtime.InteropServices;

namespace DynamicNotch.Services;

/// <summary>
/// What a game is doing to the PC, without administrator rights: GPU load (Windows' "GPU Engine" performance
/// counters, read the way Task Manager does, total and for the game's own process), GPU temperature (from the
/// graphics driver, like Task Manager's GPU page) and CPU load. Call <see cref="Sample"/> off the UI thread.
/// </summary>
public sealed class GameService : IDisposable
{
    public sealed record Stats(double Gpu, double GpuApp, double? TempC, double Cpu);

    /// <summary>Also note each process's share of the GPU on every reading (for the strain log).</summary>
    public bool TrackProcesses { get; set; }
    /// <summary>The GPU load of each process at the last reading (process id → %), when <see cref="TrackProcesses"/> is on.</summary>
    public IReadOnlyDictionary<uint, double> GpuByProcess { get; private set; } = new Dictionary<uint, double>();

    readonly object _gate = new();
    IntPtr _query, _counter;
    bool _pdhTried;
    uint[]? _adapters;
    DateTime _adaptersAt;

    /// <summary>
    /// Does this PC's graphics driver report GPU load at all? (Windows' GPU counters need a WDDM 2 driver; a very old
    /// or basic display driver has none: then the GPU numbers are hidden rather than shown as 0 %.) Null until the
    /// first reading.
    /// </summary>
    public bool? GpuAvailable { get; private set; }

    /// <summary>One reading. The first call primes the counters (they measure the time between two calls).</summary>
    public Stats Sample(uint pid)
    {
        lock (_gate)
        {
            var (gpu, app) = GpuLoad(pid);
            return new Stats(gpu, app, GpuTemperature(), CpuLoad());
        }
    }

    // ───────────── GPU load: \GPU Engine(*)\Utilization Percentage, the way Task Manager reads it ─────────────
    // Every engine of every GPU (3D, video decode / encode, compute, copy...). A GPU's load is its busiest engine
    // type (all its processes together), and the PC's GPU load is its busiest GPU: a laptop's integrated and
    // dedicated graphics are never added together, and a video or a CUDA job counts as work, like in Task Manager.

    (double Total, double App) GpuLoad(uint pid)
    {
        if (!_pdhTried)
        {
            _pdhTried = true;
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; GpuAvailable = false; return (0, 0); }
            if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                GpuAvailable = false;
                return (0, 0);
            }
            PdhCollectQueryData(_query);
            return (0, 0);
        }
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return (0, 0);

        uint size = 0, count = 0;
        int r = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
        if (r != PDH_MORE_DATA || size == 0) { GpuAvailable ??= false; return (0, 0); }
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, ref count, buf) != 0) return (0, 0);
            GpuAvailable = count > 0;
            var engines = new Dictionary<string, double>();          // "luid…_phys_0|3D" → all processes on it
            var mine = new Dictionary<string, double>();             // the same, only the given process
            var perPid = TrackProcesses ? new Dictionary<(uint, string), double>() : null;
            int item = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var v = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * item);
                if (v.Status != 0 || v.Value <= 0 || Marshal.PtrToStringUni(v.Name) is not { } name) continue;
                // "pid_1234_luid_0x00000000_0x0000D1B4_phys_0_eng_3_engtype_VideoDecode"
                int l = name.IndexOf("_luid_", StringComparison.Ordinal), e = name.IndexOf("_eng_", StringComparison.Ordinal), t = name.IndexOf("engtype_", StringComparison.Ordinal);
                if (l < 0 || e < l || t < 0) continue;
                string key = name[(l + 6)..e] + "|" + name[(t + 8)..];
                engines[key] = engines.GetValueOrDefault(key) + v.Value;
                if (!uint.TryParse(name.AsSpan(4, Math.Max(0, l - 4)), out uint owner)) continue;
                if (owner == pid && pid != 0) mine[key] = mine.GetValueOrDefault(key) + v.Value;
                if (perPid != null) perPid[(owner, key)] = perPid.GetValueOrDefault((owner, key)) + v.Value;
            }
            if (perPid != null)
                GpuByProcess = perPid.GroupBy(kv => kv.Key.Item1).ToDictionary(g => g.Key, g => Math.Min(100, g.Max(kv => kv.Value)));
            double total = engines.Count > 0 ? engines.Values.Max() : 0;
            double app = mine.Count > 0 ? mine.Values.Max() : 0;
            return (Math.Clamp(total, 0, 100), Math.Clamp(app, 0, 100));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ───────────── GPU temperature: the driver's performance data (WDDM 2.4+) ─────────────

    double? GpuTemperature()
    {
        try
        {
            if (_adapters == null || DateTime.UtcNow - _adaptersAt > TimeSpan.FromMinutes(5))
            {
                CloseAdapters();
                _adapters = OpenAdapters();
                _adaptersAt = DateTime.UtcNow;
            }
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
                    if (c > best && c is >= 15 and < 125) best = c; // 0 = not reported; anything else outside is a driver's nonsense
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

    readonly CpuMeter _cpu = new();

    double CpuLoad() => _cpu.Read(); // the same number as Task Manager (Services/CpuMeter)

    public void Dispose()
    {
        lock (_gate)
        {
            if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
            _cpu.Dispose();
            CloseAdapters();
        }
    }

    void CloseAdapters()
    {
        if (_adapters != null)
            foreach (uint h in _adapters) { var c = new D3DKMT_CLOSEADAPTER { hAdapter = h }; D3DKMTCloseAdapter(ref c); }
        _adapters = null;
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
}
