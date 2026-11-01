using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace DynamicNotch.Services;

/// <summary>
/// Reading a game's frames needs the "Performance Log Users" group (or administrator rights), and Windows only
/// applies a new group membership at the next sign-in: turning "FPS in games" on used to mean "sign out and back in
/// first", and restarting the notch didn't help. Now the one Windows prompt does both: it adds the account to the
/// group for good, and starts a small helper (the same app, elevated, no window) that reads the frames right away
/// for the rest of this session and hands them to the notch through a few KB of shared memory. Once you've signed in
/// again the notch reads them itself and the helper isn't needed any more.
/// The helper stops counting when no game is in front, stops entirely when FPS is turned off, and quits on its own
/// when the notch has been gone for a while.
/// </summary>
public sealed class FrameCounter : IDisposable
{
    // the shared block: magic, target pid (-1 = quit), heartbeat, head, helper pid, then the frame times
    internal const int Magic = 0x4E46504E, OffMagic = 0, OffTarget = 4, OffBeat = 8, OffHead = 16, OffHelper = 20, OffTimes = 24, Ring = 2048;
    internal const long Size = OffTimes + Ring * 8L;

    readonly FpsService _local = new();
    readonly object _gate = new();
    MemoryMappedFile? _map;
    MemoryMappedViewAccessor? _view;
    bool _remote;

    /// <summary>The shared block's name: one per Windows session and account, stable across notch restarts.</summary>
    public static string MapName
    {
        get
        {
            string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..12];
            return $"Local\\DynamicNotchFps-{Process.GetCurrentProcess().SessionId}-{hash}";
        }
    }

    bool Open(bool create)
    {
        if (_view != null) return true;
        try
        {
            _map = create ? MemoryMappedFile.CreateOrOpen(MapName, Size) : MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
            _view = _map.CreateViewAccessor(0, Size);
            return true;
        }
        catch { _map?.Dispose(); _map = null; return false; }
    }

    /// <summary>A helper is running for this session and answered within the last 3 s.</summary>
    public bool HelperAlive
    {
        get
        {
            lock (_gate)
            {
                if (!Open(false)) return false;
                if (_view!.ReadInt32(OffMagic) != Magic) return false;
                long beat = _view.ReadInt64(OffBeat);
                return beat != 0 && Environment.TickCount64 - beat < 3000;
            }
        }
    }

    /// <summary>Can frames be read right now, one way or the other?</summary>
    public bool Ready => FpsService.AccountCanTrace() || HelperAlive;

    /// <summary>
    /// Starts the helper behind Windows' prompt (and, with <paramref name="join"/>, adds this account to the group so
    /// it's never needed again after the next sign-in). False if the prompt was declined or the helper didn't answer.
    /// Call off the UI thread: the prompt stays up as long as it likes.
    /// </summary>
    public bool LaunchHelper(bool join)
    {
        try
        {
            lock (_gate)
            {
                if (!Open(true)) return false;
                _view!.Write(OffMagic, Magic);
                _view.Write(OffTarget, 0);
                _view.Write(OffBeat, 0L);
                _view.Write(OffHead, 0);
            }
            if (Environment.ProcessPath is not { } exe) return false;
            string user = WindowsIdentity.GetCurrent().Name;
            string args = $"--fps-helper \"{MapName}\"" + (join ? $" --join \"{user}\"" : "");
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) return false;
            // the helper answers within a second or two
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(150);
                if (HelperAlive) return true;
                if (p.HasExited) return false;
            }
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Write("fps helper: " + ex.Message); // 1223: the prompt was declined
            return false;
        }
    }

    /// <summary>FPS was turned off: the helper quits.</summary>
    public void StopHelper()
    {
        lock (_gate)
        {
            if (_view == null && !Open(false)) return;
            _view!.Write(OffTarget, -1);
        }
    }

    public bool Start(int pid)
    {
        if (FpsService.AccountCanTrace())
        {
            _remote = false;
            return _local.Start(pid);
        }
        lock (_gate)
        {
            if (!HelperAliveUnlocked()) return false;
            _view!.Write(OffTarget, pid);
            _remote = true;
            return true;
        }
    }

    bool HelperAliveUnlocked()
    {
        if (!Open(false) || _view!.ReadInt32(OffMagic) != Magic) return false;
        long beat = _view.ReadInt64(OffBeat);
        return beat != 0 && Environment.TickCount64 - beat < 3000;
    }

    /// <summary>The frame rate right now (see <see cref="FpsService.Read"/>), from whichever reader is counting.</summary>
    public double? Read()
    {
        if (!_remote) return _local.Read();
        lock (_gate)
        {
            if (!HelperAliveUnlocked()) return null;
            int head = _view!.ReadInt32(OffHead);
            if (head <= 0) return 0;
            long freq = Stopwatch.Frequency; // the helper's timestamps are this PC's performance counter too
            long last = _view.ReadInt64(OffTimes + ((head - 1) & (Ring - 1)) * 8L);
            if (Stopwatch.GetTimestamp() - last > freq * 2) return 0;
            double rate = RateOver(head, last, freq / 2);
            return rate > 0 ? rate : RateOver(head, last, freq * 2);
        }
    }

    double RateOver(int head, long last, long window)
    {
        int n = 1;
        long first = last;
        int available = Math.Min(head, Ring);
        for (int i = 1; i < available; i++)
        {
            long t = _view!.ReadInt64(OffTimes + ((head - 1 - i) & (Ring - 1)) * 8L);
            if (last - t > window) break;
            first = t;
            n++;
        }
        return n >= 3 && last > first ? (n - 1) * (double)Stopwatch.Frequency / (last - first) : 0;
    }

    public void Stop()
    {
        if (_remote)
        {
            lock (_gate) { if (_view != null) _view.Write(OffTarget, 0); }
            _remote = false;
            return;
        }
        _local.Stop();
    }

    public void Dispose()
    {
        Stop();
        _local.Dispose();
        lock (_gate)
        {
            _view?.Dispose();
            _map?.Dispose();
            _view = null;
            _map = null;
        }
    }
}

/// <summary>
/// The helper itself: "DynamicNotch.exe --fps-helper &lt;block&gt; [--join &lt;account&gt;]", started elevated by
/// <see cref="FrameCounter.LaunchHelper"/>. No window, no tray icon. It counts the frames of whatever the notch
/// points it at, writes them into the shared block, and quits when told to or when the notch has been gone for 30
/// minutes.
/// </summary>
public static class FpsHelper
{
    public static int Run(string[] args)
    {
        string? map = null, join = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--fps-helper") map = args[i + 1];
            else if (args[i] == "--join") join = args[i + 1];
        }
        if (map == null) return 2;
        try
        {
            if (join != null) Join(join);
            using var mmf = MemoryMappedFile.OpenExisting(map, MemoryMappedFileRights.ReadWrite);
            using var view = mmf.CreateViewAccessor(0, FrameCounter.Size);
            if (view.ReadInt32(FrameCounter.OffMagic) != FrameCounter.Magic) return 3;
            view.Write(FrameCounter.OffHelper, Environment.ProcessId);
            using var fps = new FpsService();
            var gate = new object();
            fps.OnFrame = stamp =>
            {
                lock (gate)
                {
                    int head = view.ReadInt32(FrameCounter.OffHead);
                    view.Write(FrameCounter.OffTimes + (head & (FrameCounter.Ring - 1)) * 8L, stamp);
                    view.Write(FrameCounter.OffHead, head + 1);
                }
            };
            int current = 0;
            long notchSeen = Environment.TickCount64;
            AppLog.Write("fps helper: running");
            while (true)
            {
                lock (gate) view.Write(FrameCounter.OffBeat, Environment.TickCount64);
                int target = view.ReadInt32(FrameCounter.OffTarget);
                if (target == -1) break;
                if (target != current)
                {
                    if (target > 0)
                    {
                        lock (gate) view.Write(FrameCounter.OffHead, 0);
                        fps.Start(target);
                    }
                    else fps.Stop();
                    current = target;
                }
                if (NotchRunning()) notchSeen = Environment.TickCount64;
                else if (Environment.TickCount64 - notchSeen > 30 * 60_000) break;
                Thread.Sleep(250);
            }
            fps.Stop();
            lock (gate) view.Write(FrameCounter.OffBeat, 0L);
            AppLog.Write("fps helper: done");
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("fps helper failed: " + ex.Message);
            return 1;
        }
    }

    static bool NotchRunning()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("DynamicNotch"))
                using (p) if (p.Id != Environment.ProcessId) return true;
        }
        catch { return true; }
        return false;
    }

    /// <summary>Adds the account to "Performance Log Users" (its local name), so the next sign-in needs no helper.</summary>
    static void Join(string account)
    {
        try
        {
            string group = new SecurityIdentifier("S-1-5-32-559").Translate(typeof(NTAccount)).Value;
            if (group.Contains('\\')) group = group[(group.IndexOf('\\') + 1)..];
            using var p = Process.Start(new ProcessStartInfo("net.exe", $"localgroup \"{group}\" \"{account}\" /add")
                { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(15000);
        }
        catch (Exception ex) { AppLog.Write("fps helper join: " + ex.Message); }
    }
}
