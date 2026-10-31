using System.IO;
using System.Windows.Threading;

namespace DynamicNotch.Services;

public enum DeviceKind { Mouse, Headset }

/// <summary>
/// Live: a fresh reading. Stale: the last reading is too old to trust (the mouse is asleep or off; the same
/// rule as Battery Monitor). Off: the receiver is plugged in but the device doesn't answer.
/// </summary>
public enum BatteryState { Live, Stale, Off }

public sealed record DeviceBattery(DeviceKind Kind, string Name, int? Level, bool Charging, BatteryState State, DateTime At);

/// <summary>
/// Battery of the wireless mouse and headset, read straight from their receivers, the same way Battery Monitor does:
///  • HyperX Cloud II Core (03F0:0995, vendor usage FF13:0001): asked with output report 66 89 every 30 s; byte 4 is the
///    percentage, byte 2 = 0x10 means charging (an idle 0x1F is double-checked with 66 88: 0x15 / 0x03 = charging).
///    No answer: the headset is off.
///  • Attack Shark X3 (1D57:FA60 receiver, or FA50 on its cable; interface 2, usage 000A:0000): listened to, never
///    written to. Frames 03 10 40 STATE LEVEL; LEVEL 1..10 = 10..100 %, STATE 01 = on battery, 02 / 03 / 80 = charging.
///    15 s without a frame and the last reading is stale.
/// A device whose receiver isn't plugged in simply isn't listed. Everything runs off the UI thread.
/// </summary>
public sealed class DeviceBatteryService : IDisposable
{
    const ushort HyperXVid = 0x03F0, HyperXPid = 0x0995;
    const ushort SharkVid = 0x1D57, SharkWireless = 0xFA60, SharkWired = 0xFA50;
    static readonly TimeSpan MouseFresh = TimeSpan.FromSeconds(15);

    readonly Dispatcher _ui;
    readonly CancellationTokenSource _stop = new();
    readonly object _gate = new();
    DeviceBattery? _mouse, _headset;
    DateTime _mouseReportAt;
    volatile bool _refreshHeadset;

    /// <summary>A reading appeared, changed, went stale or disappeared (UI thread).</summary>
    public event Action? Changed;

    public DeviceBatteryService(Dispatcher ui) => _ui = ui;

    /// <summary>The devices with a receiver plugged in, mouse first.</summary>
    public IReadOnlyList<DeviceBattery> Devices
    {
        get
        {
            lock (_gate)
            {
                var list = new List<DeviceBattery>(2);
                if (_mouse != null) list.Add(_mouse.State == BatteryState.Live && DateTime.UtcNow - _mouseReportAt > MouseFresh
                    ? _mouse with { State = BatteryState.Stale, Charging = false } : _mouse);
                if (_headset != null) list.Add(_headset);
                return list;
            }
        }
    }

    public void Start()
    {
        _ = Task.Run(() => MouseLoop(_stop.Token));
        _ = Task.Run(() => HeadsetLoop(_stop.Token));
        _ = Task.Run(() => StaleWatch(_stop.Token));
    }

    /// <summary>Ask the headset again now (the notch just opened).</summary>
    public void Refresh() => _refreshHeadset = true;

    public void Dispose() => _stop.Cancel();

    void Publish() => _ui.InvokeAsync(() => Changed?.Invoke());

    // ───────────── mouse: listen ─────────────

    async Task MouseLoop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            FileStream? stream = null;
            try
            {
                var interfaces = Hid.Find(SharkVid, SharkWireless, SharkWired);
                bool wired = interfaces.Any(i => i.Pid == SharkWired);
                var target = interfaces.Where(i => i.Mi == 2 && i.UsagePage == 0x0A && i.Usage == 0)
                    .OrderBy(i => i.Pid == SharkWireless ? 0 : 1).FirstOrDefault();
                if (target == null)
                {
                    SetMouse(null); // receiver unplugged: not listed
                    await Task.Delay(4000, stop);
                    continue;
                }
                stream = Hid.Open(target.Path);
                if (stream == null) { await Task.Delay(4000, stop); continue; }
                lock (_gate) _mouse ??= new DeviceBattery(DeviceKind.Mouse, "Attack Shark X3", null, false, BatteryState.Stale, DateTime.MinValue);
                Publish();

                var buf = new byte[Math.Max(target.InputLength, 8)];
                var rescanAt = DateTime.UtcNow.AddSeconds(5);
                while (!stop.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    timeout.CancelAfter(2000);
                    int n;
                    try { n = await stream.ReadAsync(buf, timeout.Token); }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested) { n = -1; }
                    if (n == 0) break; // device gone
                    if (n > 0 && Parse(buf, n) is { } r)
                    {
                        lock (_gate)
                        {
                            _mouseReportAt = DateTime.UtcNow;
                            var next = new DeviceBattery(DeviceKind.Mouse, "Attack Shark X3", r.Level, r.Charging, BatteryState.Live, DateTime.Now);
                            bool changed = _mouse == null || _mouse.Level != next.Level || _mouse.Charging != next.Charging || _mouse.State != BatteryState.Live;
                            _mouse = next;
                            if (changed) Publish();
                        }
                    }
                    // the receiver may have been swapped for the cable (or unplugged): look again now and then
                    if (DateTime.UtcNow > rescanAt)
                    {
                        rescanAt = DateTime.UtcNow.AddSeconds(5);
                        var now = Hid.Find(SharkVid, SharkWireless, SharkWired);
                        if (!now.Any(i => i.Path == target.Path) || now.Any(i => i.Pid == SharkWired) != wired) break;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppLog.Write("[debug] mouse battery: " + ex.Message); }
            finally { stream?.Dispose(); }
            try { await Task.Delay(1000, stop); } catch { return; }
        }
    }

    /// <summary>03 10 40 STATE LEVEL, exactly the frames the official X3 driver accepts.</summary>
    static (int Level, bool Charging)? Parse(byte[] d, int n)
    {
        if (n < 5 || d[0] != 0x03 || d[1] != 0x10 || d[2] != 0x40) return null;
        if (d[3] is not (0x01 or 0x02 or 0x03 or 0x80)) return null;
        if (d[4] is < 1 or > 10) return null;
        return (d[4] * 10, d[3] != 0x01);
    }

    void SetMouse(DeviceBattery? value)
    {
        lock (_gate)
        {
            if (_mouse == null && value == null) return;
            _mouse = value;
        }
        Publish();
    }

    /// <summary>A mouse that went quiet turns stale on screen even though nothing new arrived.</summary>
    async Task StaleWatch(CancellationToken stop)
    {
        bool wasLive = false;
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(3000, stop); } catch { return; }
            bool live;
            lock (_gate) live = _mouse?.State == BatteryState.Live && DateTime.UtcNow - _mouseReportAt <= MouseFresh;
            if (live != wasLive) Publish();
            wasLive = live;
        }
    }

    // ───────────── headset: ask ─────────────

    async Task HeadsetLoop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var target = Hid.Find(HyperXVid, HyperXPid).FirstOrDefault(i => i.UsagePage == 0xFF13 && i.Usage == 0x0001);
                DeviceBattery? next = null;
                if (target != null)
                {
                    var (level, charging) = await QueryHeadset(target, stop);
                    next = level is { } v
                        ? new DeviceBattery(DeviceKind.Headset, "HyperX Cloud II Core", v, charging, BatteryState.Live, DateTime.Now)
                        : new DeviceBattery(DeviceKind.Headset, "HyperX Cloud II Core", _headset?.Level, false, BatteryState.Off, DateTime.Now);
                }
                bool changed;
                lock (_gate)
                {
                    changed = _headset?.Level != next?.Level || _headset?.Charging != next?.Charging || _headset?.State != next?.State;
                    _headset = next;
                }
                if (changed) Publish();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppLog.Write("[debug] headset battery: " + ex.Message); }

            // every 30 s, or sooner when the notch opens
            for (int i = 0; i < 60 && !stop.IsCancellationRequested && !_refreshHeadset; i++)
                try { await Task.Delay(500, stop); } catch { return; }
            _refreshHeadset = false;
        }
    }

    static async Task<(int? Level, bool Charging)> QueryHeadset(Hid.Interface target, CancellationToken stop)
    {
        using var stream = Hid.Open(target.Path);
        if (stream == null) return (null, false);
        var resp = await Command(stream, target, 0x89, stop);
        if (resp == null || resp.Length < 5 || resp[0] != 0x66 || resp[1] != 0x89 || resp[4] > 100) return (null, false);
        int level = resp[4];
        bool charging = resp[2] == 0x10;
        if (!charging && resp[2] == 0x1F)
        {
            // idle / low-power: the status command says whether it's on the cable
            var status = await Command(stream, target, 0x88, stop);
            charging = status != null && status.Length >= 3 && status[0] == 0x66 && status[1] == 0x88 && status[2] is 0x15 or 0x03;
        }
        return (level, charging);
    }

    /// <summary>Sends 66 CMD and waits (up to 2 s) for the answer to that command.</summary>
    static async Task<byte[]?> Command(FileStream stream, Hid.Interface target, byte cmd, CancellationToken stop)
    {
        int outLen = Math.Max(target.OutputLength, 2);
        var report = new byte[outLen];
        report[0] = 0x66;
        report[1] = cmd;
        if (!Hid.SetOutputReport(stream, report)) return null;
        var buf = new byte[Math.Max(target.InputLength, 8)];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(2000);
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buf, timeout.Token);
                if (n <= 0) return null;
                if (n >= 3 && buf[0] == 0x66 && buf[1] == cmd) return buf[..n];
                // another report (or another app's answer): keep listening until the timeout
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { return null; }
    }
}
