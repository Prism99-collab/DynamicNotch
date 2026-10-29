using System.Windows.Threading;
using Microsoft.Win32;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch.Services;

public enum PowerMode { Efficiency, Balanced, Performance }

/// <summary>Battery state, plug/unplug events and the Windows 11 power mode.</summary>
public sealed class PowerService
{
    static readonly Guid EfficiencyGuid = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    static readonly Guid PerformanceGuid = new("ded574b5-45a0-4f42-8737-46345c09c238");

    readonly Dispatcher _ui;
    DispatcherTimer? _timer;

    public bool HasBattery { get; private set; }
    public int Percent { get; private set; } = 100;
    public bool PluggedIn { get; private set; }
    public bool Saver { get; private set; }

    public event Action? Changed;
    public event Action<bool>? PluggedChanged;

    public PowerService(Dispatcher ui) => _ui = ui;

    public void Init()
    {
        Read();
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode != PowerModes.StatusChange) return;
            _ui.InvokeAsync(() =>
            {
                bool was = PluggedIn;
                Read();
                Changed?.Invoke();
                if (was != PluggedIn && HasBattery) PluggedChanged?.Invoke(PluggedIn);
            });
        };
        // percentage creeps slowly; a lazy refresh is plenty
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => { int p = Percent; Read(); if (p != Percent) Changed?.Invoke(); };
        _timer.Start();
    }

    void Read()
    {
        if (!GetSystemPowerStatus(out var s)) return;
        HasBattery = s.BatteryFlag != 128 && s.BatteryLifePercent != 255;
        Percent = s.BatteryLifePercent == 255 ? 100 : s.BatteryLifePercent;
        PluggedIn = s.ACLineStatus == 1;
        Saver = s.SystemStatusFlag == 1;
    }

    public PowerMode Mode
    {
        get
        {
            try
            {
                if (PowerGetEffectiveOverlayScheme(out var g) == 0)
                {
                    if (g == EfficiencyGuid) return PowerMode.Efficiency;
                    if (g == PerformanceGuid) return PowerMode.Performance;
                }
            }
            catch { }
            return PowerMode.Balanced;
        }
    }

    public PowerMode CycleMode()
    {
        var next = (PowerMode)(((int)Mode + 1) % 3);
        var g = next switch { PowerMode.Efficiency => EfficiencyGuid, PowerMode.Performance => PerformanceGuid, _ => Guid.Empty };
        try { PowerSetActiveOverlayScheme(g); } catch { }
        return Mode;
    }
}
