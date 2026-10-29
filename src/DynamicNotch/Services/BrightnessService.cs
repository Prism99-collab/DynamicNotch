using System.Management;
using System.Windows.Threading;

namespace DynamicNotch.Services;

/// <summary>Built-in display brightness via WMI. All WMI work happens off the UI thread.</summary>
public sealed class BrightnessService
{
    readonly Dispatcher _ui;
    ManagementEventWatcher? _watch;
    int _pending = -1, _busy;
    long _lastSetTicks;

    public bool Supported { get; private set; }
    public int Level { get; private set; }

    /// <summary>Argument is true when the change came from outside (keyboard keys, Windows settings).</summary>
    public event Action<bool>? Changed;

    public BrightnessService(Dispatcher ui) => _ui = ui;

    static ManagementScope Scope => new(@"\\.\root\WMI");

    public void Init() => Task.Run(() =>
    {
        int l = Read();
        if (l < 0) return;
        _ui.InvokeAsync(() => { Supported = true; Level = l; Changed?.Invoke(false); });
        try
        {
            _watch = new ManagementEventWatcher(Scope, new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watch.EventArrived += (_, e) =>
            {
                // runs on a background thread: an exception here would end the whole app
                int v;
                try { v = Convert.ToInt32(e.NewEvent["Brightness"]); }
                catch { return; }
                bool self = DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastSetTicks) < TimeSpan.TicksPerMillisecond * 900;
                _ui.InvokeAsync(() =>
                {
                    if (v == Level && self) return;
                    Level = v;
                    Changed?.Invoke(!self);
                });
            };
            _watch.Start();
        }
        catch { }
    });

    public Task RefreshAsync() => Task.Run(() =>
    {
        int l = Read();
        if (l >= 0) _ui.InvokeAsync(() => { Level = l; Changed?.Invoke(false); });
    });

    static int Read()
    {
        try
        {
            using var s = new ManagementObjectSearcher(Scope, new SelectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));
            foreach (var o in s.Get())
                using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return -1;
    }

    /// <summary>Called continuously while dragging; only the latest value is ever written.</summary>
    public void Set(int level)
    {
        level = Math.Clamp(level, 0, 100);
        Level = level;
        Interlocked.Exchange(ref _lastSetTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _pending, level);
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(() =>
        {
            while (true)
            {
                int v = Interlocked.Exchange(ref _pending, -1);
                if (v >= 0) { Write(v); continue; }
                Interlocked.Exchange(ref _busy, 0);
                // a value may have arrived between the check and releasing the flag
                if (Volatile.Read(ref _pending) < 0 || Interlocked.Exchange(ref _busy, 1) == 1) return;
            }
        });
    }

    static void Write(int v)
    {
        try
        {
            using var cls = new ManagementClass(Scope, new ManagementPath("WmiMonitorBrightnessMethods"), null);
            foreach (ManagementObject o in cls.GetInstances())
                using (o) { o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v }); break; }
        }
        catch { }
    }
}
