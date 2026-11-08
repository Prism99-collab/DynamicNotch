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

/// <param name="PingMs">the latest round trip</param>
/// <param name="TypicalMs">the median of the last few: what a call or a game will feel (one slow reply doesn't count)</param>
/// <param name="JitterMs">how much the round trip wobbles: high jitter makes calls choppy even with a low ping</param>
/// <param name="Captive">connected, but the network wants you to sign in (a hotel, a café, an airport)</param>
public sealed record ConnectionDetails(string Name, bool IsWifi, int? SignalBars, long? PingMs, bool Online,
    long? TypicalMs = null, long? JitterMs = null, bool Captive = false, int? SignalPercent = null, string? WifiGeneration = null, int? LinkMbps = null);

/// <summary>Live system numbers. Only sampled while the System page is on screen.</summary>
public sealed class SystemMonitor
{
    readonly CpuMeter _cpu = new();
    long _rx, _tx;
    double? _downEma, _upEma;
    DateTime _netAt;
    double? _wattsEma;
    NetworkInterface[]? _nics;
    DateTime _nicsAt;
    int _busy;

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
        double cpu = _cpu.Read() / 100; // the same number as Task Manager, the FPS pill and the strain watch

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
            // the adapters the traffic really leaves through: up, and with a gateway. A VPN or a Hyper-V / WSL switch
            // carries the same bytes as the card under it; counting both would double the speed shown
            var live = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToArray();
            var routed = live.Where(HasGateway).ToArray();
            var chosen = routed.Length > 0 ? routed : live;
            // a different set of adapters (Wi-Fi to cable, a VPN on): start counting again, no jump from the change
            if (_nics == null || !chosen.Select(n => n.Id).SequenceEqual(_nics.Select(n => n.Id))) { _netAt = default; _downEma = _upEma = null; }
            _nics = chosen;
            _nicsAt = now;
        }
        long rx = 0, tx = 0;
        foreach (var ni in _nics)
        {
            try { var s = ni.GetIPStatistics(); rx += s.BytesReceived; tx += s.BytesSent; } catch { }
        }
        double secs = (now - _netAt).TotalSeconds;
        double down = 0, up = 0;
        if (_netAt != default && secs > 0.2 && rx >= _rx && tx >= _tx) // an adapter that just left would make it negative
        {
            down = (rx - _rx) / secs;
            up = (tx - _tx) / secs;
            // a light average (half the new reading): steady numbers that still follow a download within seconds
            _downEma = _downEma is { } d0 && secs < 10 ? d0 * 0.5 + down * 0.5 : down;
            _upEma = _upEma is { } u0 && secs < 10 ? u0 * 0.5 + up * 0.5 : up;
            down = _downEma.Value;
            up = _upEma.Value;
        }
        _rx = rx; _tx = tx; _netAt = now;

        return new SystemSnapshot(cpu, m.dwMemoryLoad / 100.0, used, m.ullTotalPhys, disk, free, down, up,
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    static bool HasGateway(NetworkInterface ni)
    {
        try { return ni.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any)); }
        catch { return false; }
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

    readonly Queue<long> _pings = new();
    readonly object _pingGate = new();

    /// <summary>
    /// Network name, signal strength, the Wi-Fi link (quality %, generation, rate) and the round trip to the internet.
    /// The round trip tries a few public servers by ICMP (some networks block one, or block ping altogether) and,
    /// failing that, times a TCP handshake to port 443, so a network that only filters ping isn't shown as dead. The
    /// last few round trips give a typical value and the jitter.
    /// </summary>
    public async Task<ConnectionDetails> ConnectionAsync()
    {
        string name = "Offline";
        bool wifi = false, online = false, captive = false;
        int? bars = null;
        try
        {
            var p = WinNet.NetworkInformation.GetInternetConnectionProfile();
            if (p != null)
            {
                var level = p.GetNetworkConnectivityLevel();
                online = level == WinNet.NetworkConnectivityLevel.InternetAccess;
                captive = level == WinNet.NetworkConnectivityLevel.ConstrainedInternetAccess;
                wifi = p.IsWlanConnectionProfile;
                name = wifi ? p.ProfileName : p.IsWwanConnectionProfile ? "Mobile data" : "Ethernet";
                bars = p.GetSignalBars();
            }
        }
        catch { }

        WifiInfo.Link? link = wifi ? await Task.Run(WifiInfo.Current) : null;
        if (link != null) bars = (int)Math.Round(link.SignalPercent / 20.0); // finer than Windows' bars, same 0–5 scale

        long? ms = online ? await RoundTripAsync() : null;
        long? typical = null, jitter = null;
        lock (_pingGate)
        {
            if (ms is { } m) { _pings.Enqueue(m); while (_pings.Count > 5) _pings.Dequeue(); }
            else if (!online) _pings.Clear();
            if (_pings.Count >= 2)
            {
                var sorted = _pings.OrderBy(x => x).ToArray();
                typical = sorted[sorted.Length / 2];
                var arr = _pings.ToArray();
                jitter = (long)Math.Round(Enumerable.Range(1, arr.Length - 1).Average(i => Math.Abs(arr[i] - arr[i - 1])));
            }
            else if (ms is { } only) typical = only;
        }
        return new ConnectionDetails(name, wifi, bars, ms, online, typical, jitter, captive, link?.SignalPercent, link?.Generation, link?.RateMbps);
    }

    static readonly string[] PingHosts = { "1.1.1.1", "8.8.8.8", "9.9.9.9" };

    static async Task<long?> RoundTripAsync()
    {
        foreach (var host in PingHosts)
        {
            try
            {
                using var ping = new Ping(); // one per call: two at once on the same Ping throw (a false "timed out")
                var reply = await ping.SendPingAsync(host, 1200);
                if (reply.Status == IPStatus.Success) return reply.RoundtripTime;
            }
            catch { }
        }
        // ICMP filtered (offices, some ISPs): the time a TCP handshake takes is the same round trip
        foreach (var host in new[] { "1.1.1.1", "8.8.8.8" })
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                using var cts = new CancellationTokenSource(1500);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await socket.ConnectAsync(System.Net.IPAddress.Parse(host), 443, cts.Token);
                return Math.Max(1, sw.ElapsedMilliseconds);
            }
            catch { }
        }
        return null;
    }

    public static string Bytes(double b) =>
        b >= 1L << 30 ? $"{b / (1L << 30):0.#} GB" :
        b >= 1L << 20 ? $"{b / (1L << 20):0.#} MB" :
        b >= 1L << 10 ? $"{b / (1L << 10):0} KB" : $"{b:0} B";

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(1, t.Minutes)} min";
}
