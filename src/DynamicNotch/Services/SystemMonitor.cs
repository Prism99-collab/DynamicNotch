using System.IO;
using System.Net.NetworkInformation;
using Windows.Devices.Power;
using Windows.System.Power;
using static DynamicNotch.Interop.Native;
using WinNet = Windows.Networking.Connectivity;

namespace DynamicNotch.Services;

public sealed record SystemSnapshot(
    double Cpu, double Memory, ulong MemUsed, ulong MemTotal,
    double Disk, long DiskFree, double DownBps, double UpBps, TimeSpan Uptime);

public sealed record BatteryDetails(
    bool Present, bool Charging, bool Full, double? WattsNow, TimeSpan? TimeLeft, TimeSpan? TimeToFull,
    double? HealthPercent, double? RemainingWh, double? FullWh);

public sealed record ConnectionDetails(string Name, bool IsWifi, int? SignalBars, long? PingMs, bool Online);

/// <summary>Live system numbers. Only sampled while the System page is on screen.</summary>
public sealed class SystemMonitor
{
    long _idle, _kernel, _user;
    long _rx, _tx;
    DateTime _netAt;
    double? _wattsEma;
    NetworkInterface[]? _nics;
    DateTime _nicsAt;
    int _busy;
    readonly Ping _ping = new();

    public Task<SystemSnapshot?> SampleAsync() => Interlocked.Exchange(ref _busy, 1) == 1
        ? Task.FromResult<SystemSnapshot?>(null)
        : Task.Run(() =>
        {
            try { return (SystemSnapshot?)Sample(); }
            catch { return null; }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });

    SystemSnapshot Sample()
    {
        double cpu = 0;
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
            if (_kernel != 0 && dk + du > 0) cpu = Math.Clamp(1 - (double)di / (dk + du), 0, 1);
            _idle = idle; _kernel = kernel; _user = user;
        }

        var m = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        ulong used = m.ullTotalPhys - m.ullAvailPhys;

        double disk = 0;
        long free = 0;
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            free = d.AvailableFreeSpace;
            disk = 1 - (double)d.TotalFreeSpace / d.TotalSize;
        }
        catch { }

        var now = DateTime.UtcNow;
        // enumerating adapters is the slow part; refresh that list only every 30 s
        if (_nics == null || (now - _nicsAt).TotalSeconds > 30)
        {
            _nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToArray();
            _nicsAt = now;
        }
        long rx = 0, tx = 0;
        foreach (var ni in _nics)
        {
            try { var s = ni.GetIPStatistics(); rx += s.BytesReceived; tx += s.BytesSent; } catch { }
        }
        double secs = (now - _netAt).TotalSeconds;
        double down = 0, up = 0;
        if (_netAt != default && secs > 0.2)
        {
            down = Math.Max(0, (rx - _rx) / secs);
            up = Math.Max(0, (tx - _tx) / secs);
        }
        _rx = rx; _tx = tx; _netAt = now;

        return new SystemSnapshot(cpu, m.dwMemoryLoad / 100.0, used, m.ullTotalPhys, disk, free, down, up,
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>Battery time left / to full from the real power draw (smoothed), plus health.</summary>
    public Task<BatteryDetails> BatteryAsync() => Task.Run(() =>
    {
        try
        {
            var r = Battery.AggregateBattery.GetReport();
            if (r.Status == BatteryStatus.NotPresent) return new BatteryDetails(false, false, false, null, null, null, null, null, null);

            double? remaining = r.RemainingCapacityInMilliwattHours, full = r.FullChargeCapacityInMilliwattHours, design = r.DesignCapacityInMilliwattHours;
            double? rateMw = r.ChargeRateInMilliwatts;
            bool charging = r.Status == BatteryStatus.Charging;
            bool idle = r.Status == BatteryStatus.Idle;

            // the instantaneous rate jumps around; smooth it so the estimate doesn't flicker
            double? watts = rateMw is { } mw && mw != 0 ? Math.Abs(mw) / 1000.0 : null;
            if (watts is { } w) _wattsEma = _wattsEma is { } e ? e * 0.7 + w * 0.3 : w;

            TimeSpan? left = null, toFull = null;
            if (!charging && !idle && remaining is { } rem && _wattsEma is { } ew && ew > 0.3)
                left = TimeSpan.FromHours(rem / 1000.0 / ew);
            else if (!charging && GetSystemPowerStatus(out var ps) && ps.BatteryLifeTime > 0)
                left = TimeSpan.FromSeconds(ps.BatteryLifeTime);
            if (charging && remaining is { } rc && full is { } fc && _wattsEma is { } cw && cw > 0.3)
                toFull = TimeSpan.FromHours(Math.Max(0, fc - rc) / 1000.0 / cw);

            double? health = full is { } f && design is { } dsg && dsg > 0 ? Math.Min(100, f / dsg * 100) : null;
            bool isFull = idle && remaining is { } ri && full is { } fi && ri >= fi * 0.97;
            return new BatteryDetails(true, charging, isFull, _wattsEma, left, toFull, health, remaining / 1000.0, full / 1000.0);
        }
        catch { return new BatteryDetails(false, false, false, null, null, null, null, null, null); }
    });

    /// <summary>Network name, signal strength and round-trip time to a fast public DNS server.</summary>
    public async Task<ConnectionDetails> ConnectionAsync()
    {
        string name = "Offline";
        bool wifi = false, online = false;
        int? bars = null;
        try
        {
            var p = WinNet.NetworkInformation.GetInternetConnectionProfile();
            if (p != null)
            {
                online = p.GetNetworkConnectivityLevel() == WinNet.NetworkConnectivityLevel.InternetAccess;
                wifi = p.IsWlanConnectionProfile;
                name = wifi ? p.ProfileName : p.IsWwanConnectionProfile ? "Mobile data" : "Ethernet";
                bars = p.GetSignalBars();
            }
        }
        catch { }

        long? ms = null;
        if (online)
        {
            try
            {
                var reply = await _ping.SendPingAsync("1.1.1.1", 1200);
                if (reply.Status == IPStatus.Success) ms = reply.RoundtripTime;
            }
            catch { }
        }
        return new ConnectionDetails(name, wifi, bars, ms, online);
    }

    public static string Bytes(double b) =>
        b >= 1L << 30 ? $"{b / (1L << 30):0.#} GB" :
        b >= 1L << 20 ? $"{b / (1L << 20):0.#} MB" :
        b >= 1L << 10 ? $"{b / (1L << 10):0} KB" : $"{b:0} B";

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(1, t.Minutes)} min";
}
