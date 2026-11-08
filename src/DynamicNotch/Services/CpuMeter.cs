using System.Runtime.InteropServices;

namespace DynamicNotch.Services;

/// <summary>
/// The CPU load the way Task Manager shows it on Windows 10 and 11: "% Processor Utility", which counts the CPU's
/// real speed (a CPU at full turbo reads higher than one crawling in power saving), clamped at 100 % like Task
/// Manager does. A PC where that counter can't be read falls back to the classic busy time. Every reader of CPU load
/// in the notch (the System page, the FPS pill, the strain watch) uses this one, so they all agree. Not thread-safe:
/// one meter per reader, called a second or more apart; the first call primes it.
/// </summary>
public sealed class CpuMeter : IDisposable
{
    IntPtr _query, _counter;
    bool _tried;
    long _idle, _kernel, _user;

    /// <summary>0–100.</summary>
    public double Read()
    {
        double classic = Classic();
        if (!_tried)
        {
            _tried = true;
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
            else if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _counter) != 0)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
            else PdhCollectQueryData(_query);
            return classic;
        }
        if (_query != IntPtr.Zero && PdhCollectQueryData(_query) == 0
            && PdhGetFormattedCounterValue(_counter, PDH_FMT_DOUBLE, IntPtr.Zero, out var v) == 0 && v.CStatus == 0 && double.IsFinite(v.Value))
            return Math.Clamp(v.Value, 0, 100);
        return classic;
    }

    double Classic()
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
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    const uint PDH_FMT_DOUBLE = 0x200;

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
