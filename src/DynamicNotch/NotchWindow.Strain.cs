using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Strain warnings (Cyberpunk: "Cyberpsychosis risk"). Every 5 s, while it's on, the notch reads what you chose to
/// watch (CPU, GPU, memory, GPU and CPU temperature); when one stays over its line for 20 s straight, it warns you
/// once, with the reading and how long it's lasted, and a click opens Task Manager. Then that target rests for six
/// minutes. Never in a game or over a full-screen app, so it can't break your focus there.
/// In Cyberpunk the warning comes the way chrome overload does in the game: a red glitch, the notch shaking.
/// </summary>
public partial class NotchWindow
{
    sealed record StrainTarget(string Key, string Name, Func<Settings, double> Line, string Unit, Func<StrainService.Reading, double?> Read, Func<Settings, bool> On);

    static readonly StrainTarget[] StrainTargets =
    {
        new("cpu", "CPU", s => s.StrainCpuLine, "%", r => r.Cpu, s => s.StrainCpu),
        new("gpu", "GPU", s => s.StrainGpuLine, "%", r => r.Gpu, s => s.StrainGpu),
        new("ram", "Memory", s => s.StrainRamLine, "%", r => r.Ram, s => s.StrainRam),
        new("gputemp", "GPU temperature", s => s.StrainGpuTempLine, "°C", r => r.GpuTemp, s => s.StrainGpuTemp),
        new("cputemp", "CPU temperature", s => s.StrainCpuTempLine, "°C", r => r.CpuTemp, s => s.StrainCpuTemp),
    };

    const int StrainSamples = 4; // 4 readings 5 s apart: 20 s over the line
    readonly DispatcherTimer _strainTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    readonly Dictionary<string, int> _strainOver = new();
    readonly Dictionary<string, DateTime> _strainRested = new();
    StrainService? _strain;
    bool _strainBusy;

    void WireStrain()
    {
        _strainTimer.Tick += async (_, _) => await StrainTick();
        SyncStrain();
    }

    /// <summary>The watch runs only while it's on and something is chosen.</summary>
    void SyncStrain()
    {
        bool run = _settings.StrainWarnings && StrainTargets.Any(t => t.On(_settings));
        if (run && !_strainTimer.IsEnabled) _strainTimer.Start();
        else if (!run)
        {
            _strainTimer.Stop();
            _strainOver.Clear();
            _strain?.Dispose();
            _strain = null;
        }
    }

    async Task StrainTick()
    {
        // never while you play or watch something full screen: not even the reading
        if (_strainBusy || _mode == Mode.Game || InGame || _hidden) { _strainOver.Clear(); return; }
        _strainBusy = true;
        try
        {
            _strain ??= new StrainService();
            var service = _strain;
            var r = await Task.Run(service.Sample);
            if (!_settings.StrainWarnings) return;
            foreach (var t in StrainTargets)
            {
                if (!t.On(_settings) || t.Read(r) is not { } value) { _strainOver.Remove(t.Key); continue; }
                int over = value >= t.Line(_settings) ? _strainOver.GetValueOrDefault(t.Key) + 1 : 0;
                _strainOver[t.Key] = over;
                if (over < StrainSamples) continue;
                if (_strainRested.TryGetValue(t.Key, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(6)) continue;
                _strainRested[t.Key] = DateTime.UtcNow;
                _strainOver[t.Key] = 0;
                StrainWarn(t, value, over * 5);
                break; // one warning at a time
            }
        }
        catch (Exception ex) { AppLog.Write("strain: " + ex.Message); }
        finally { _strainBusy = false; }
    }

    void StrainWarn(StrainTarget t, double value, int seconds)
    {
        AppLog.Write($"strain warning: {t.Key} {value:0}{t.Unit} for {seconds}s");
        string reading = $"{Loc.T(t.Name)} {value:0}{t.Unit} · {string.Format(Loc.T("{0} s"), seconds)}";
        void TaskManager() { try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { } }
        if (_cyber)
        {
            ShowOsd("strain", "", null, Loc.T("CYBERPSYCHOSIS RISK"), reading, null,
                glyphBrush: Frozen(CyRed), seconds: 5, force: true, prio: Prio.High, motion: Motion.Shake, onClick: TaskManager);
            // chrome overload: the notch glitches red
            CyberGlitch(strong: true, tint: CyRed);
            After(900, () => CyberGlitch(strong: false, tint: CyRed));
        }
        else
        {
            ShowOsd("strain", "", null, Loc.T("Your PC is working hard"), reading, null,
                glyphBrush: Orange, seconds: 4.5, force: true, prio: Prio.High, motion: Motion.Shake, onClick: TaskManager);
        }
    }

    // ───────────── Preferences ─────────────

    internal (string Key, string Name, string Line)[] PrefStrainTargets =>
        StrainTargets.Select(t => (t.Key, t.Name, $"{t.Line(_settings):0}{t.Unit}")).ToArray();

    /// <summary>A target's line, its unit, and how far it can go (percentages 50–100, temperatures 60–105 °C).</summary>
    internal (double Value, string Unit, double Min, double Max) PrefStrainLine(string key)
    {
        var t = StrainTargets.First(x => x.Key == key);
        bool temp = t.Unit == "°C";
        return (t.Line(_settings), t.Unit, temp ? 60 : 50, temp ? 105 : 100);
    }

    internal void PrefSetStrainLine(string key, double value)
    {
        var (_, _, min, max) = PrefStrainLine(key);
        value = Math.Clamp(Math.Round(value), min, max);
        switch (key)
        {
            case "cpu": _settings.StrainCpuLine = value; break;
            case "gpu": _settings.StrainGpuLine = value; break;
            case "ram": _settings.StrainRamLine = value; break;
            case "gputemp": _settings.StrainGpuTempLine = value; break;
            case "cputemp": _settings.StrainCpuTempLine = value; break;
        }
        _settings.Save();
        _strainOver.Clear(); // counted again against the new line
    }

    // the FPS pill's extras
    internal bool PrefFpsStat(string key) => key switch
    {
        "cpu" => _settings.FpsCpu, "gpu" => _settings.FpsGpu, "cputemp" => _settings.FpsCpuTemp, _ => _settings.FpsGpuTemp,
    };

    internal void PrefSetFpsStat(string key, bool on)
    {
        switch (key)
        {
            case "cpu": _settings.FpsCpu = on; break;
            case "gpu": _settings.FpsGpu = on; break;
            case "cputemp": _settings.FpsCpuTemp = on && StrainService.CpuTempAvailable; break;
            default: _settings.FpsGpuTemp = on; break;
        }
        _settings.Save();
        if (!AnyGameStat) ShowGameStats(null);
    }

    internal string? PrefFpsColor(bool cpu) => cpu ? _settings.FpsCpuColor : _settings.FpsGpuColor;

    internal double PrefFpsScale => _settings.FpsScale;

    internal void PrefSetFpsScale(double k)
    {
        _settings.FpsScale = k;
        _settings.Save();
        ApplyFpsScale(); // at once, even mid-game
    }

    internal void PrefSetFpsColor(bool cpu, string? hex)
    {
        if (cpu) _settings.FpsCpuColor = hex; else _settings.FpsGpuColor = hex;
        _settings.Save();
    }

    internal bool PrefStrainOn(string key) => key switch
    {
        "cpu" => _settings.StrainCpu, "gpu" => _settings.StrainGpu, "ram" => _settings.StrainRam,
        "gputemp" => _settings.StrainGpuTemp, "cputemp" => _settings.StrainCpuTemp && StrainService.CpuTempAvailable, _ => _settings.StrainWarnings,
    };

    internal void PrefSetStrain(string key, bool on)
    {
        switch (key)
        {
            case "cpu": _settings.StrainCpu = on; break;
            case "gpu": _settings.StrainGpu = on; break;
            case "ram": _settings.StrainRam = on; break;
            case "gputemp": _settings.StrainGpuTemp = on; break;
            case "cputemp": _settings.StrainCpuTemp = on && StrainService.CpuTempAvailable; break;
            default: _settings.StrainWarnings = on; break;
        }
        _settings.Save();
        SyncStrain();
        SyncSmartPage();
    }
}
