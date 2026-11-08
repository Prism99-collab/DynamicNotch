using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DynamicNotch.Services;

/// <summary>One reading of the PC (every 5 s while the strain watch runs).</summary>
/// <summary>One reading of the PC (every 5 s while the strain watch runs). A value the PC doesn't report is null.</summary>
public sealed record StrainSample(DateTime At, double Cpu, double? Gpu, double Ram, double? GpuTemp, double? CpuTemp);

/// <summary>An app's share of the PC at the moment of a warning (all its processes together: "chrome" ×24).</summary>
public sealed record ProcUse(string Name, int Count, double Cpu, double Gpu, long MemoryMb);

/// <summary>
/// One strain warning ("Cyberpsychosis risk") and everything around it: what crossed its line and for how long,
/// every reading at that moment and for the minute before, the apps doing the most, the power state, the app in
/// front, and the machine itself. <see cref="Causes"/> puts it in words.
/// </summary>
public sealed record StrainEvent(
    DateTime At, string Key, string Name, string Unit, double Value, double Line, int Seconds,
    StrainSample Now, List<StrainSample> Before,
    List<ProcUse> TopCpu, List<ProcUse> TopGpu, List<ProcUse> TopMemory,
    string? Foreground, bool? PluggedIn, int? Battery, string PowerMode,
    string CpuName, int Cores, long MemoryGb, List<string> Causes)
{
    public bool Seen { get; set; }
}

/// <summary>
/// The strain log: the last 60 warnings, kept in the notch's folder (strain-log.json), newest first, plus a readable
/// report (strain-log.txt) on demand. Everything is read with Windows' own counters, so it works on any PC; a reading
/// a PC doesn't give (a temperature, a battery) is simply left out.
/// </summary>
public static class StrainLog
{
    const int Keep = 60;
    static string FilePath => Path.Combine(Settings.Folder, "strain-log.json");
    public static string ReportPath => Path.Combine(Settings.Folder, "strain-log.txt");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static List<StrainEvent>? _events;

    public static IReadOnlyList<StrainEvent> Events => _events ??= Load();
    public static int Unseen => Events.Count(e => !e.Seen);

    static List<StrainEvent> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<StrainEvent>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { AppLog.Write("strain log: unreadable, starting a new one (" + ex.Message + ")"); }
        return new();
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            // written beside the old one, then swapped in: a crash mid-save never loses the log
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_events, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { AppLog.Write("strain log: " + ex.Message); }
    }

    public static void Add(StrainEvent e)
    {
        var list = _events ??= Load();
        list.Insert(0, e);
        if (list.Count > Keep) list.RemoveRange(Keep, list.Count - Keep);
        Save();
    }

    public static void MarkSeen()
    {
        if (_events == null || _events.All(e => e.Seen)) return;
        foreach (var e in _events) e.Seen = true;
        Save();
    }

    public static void Clear()
    {
        _events = new();
        Save();
    }

    // ───────────── what was going on ─────────────

    /// <summary>
    /// Every app's CPU (measured over one second), memory and GPU share, grouped by name, biggest first. Takes about a
    /// second; call it off the UI thread. Processes Windows won't let it look into are skipped.
    /// </summary>
    public static (List<ProcUse> Cpu, List<ProcUse> Gpu, List<ProcUse> Memory) TopApps(IReadOnlyDictionary<uint, double> gpuByPid)
    {
        var first = new Dictionary<int, TimeSpan>();
        var info = new Dictionary<int, (string Name, long Mem)>();
        foreach (var p in Process.GetProcesses())
            using (p)
                try
                {
                    if (p.Id == 0) continue;
                    info[p.Id] = (p.ProcessName, p.WorkingSet64);
                    first[p.Id] = p.TotalProcessorTime;
                }
                catch { }
        var sw = Stopwatch.StartNew();
        Thread.Sleep(1000);
        double wall = sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount;
        var cpu = new Dictionary<int, double>();
        foreach (var p in Process.GetProcesses())
            using (p)
                try
                {
                    if (first.TryGetValue(p.Id, out var t0)) cpu[p.Id] = (p.TotalProcessorTime - t0).TotalMilliseconds / wall * 100;
                }
                catch { }
        var apps = info.GroupBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcUse(g.Key, g.Count(),
                Math.Round(g.Sum(kv => cpu.GetValueOrDefault(kv.Key)), 1),
                Math.Round(Math.Min(100, g.Sum(kv => gpuByPid.GetValueOrDefault((uint)kv.Key))), 1),
                g.Sum(kv => kv.Value.Mem) / (1024 * 1024)))
            .Where(a => !a.Name.Equals("Idle", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return (apps.OrderByDescending(a => a.Cpu).Take(6).Where(a => a.Cpu >= 0.5).ToList(),
                apps.OrderByDescending(a => a.Gpu).Take(6).Where(a => a.Gpu >= 0.5).ToList(),
                apps.OrderByDescending(a => a.MemoryMb).Take(6).ToList());
    }

    public static string CpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (k?.GetValue("ProcessorNameString") is string s && s.Trim().Length > 0) return s.Trim();
        }
        catch { }
        return "CPU";
    }

    /// <summary>The likely reasons, in plain words, from what was measured.</summary>
    public static List<string> Explain(string key, double value, double line, int seconds, StrainSample now, List<StrainSample> before,
        List<ProcUse> cpu, List<ProcUse> gpu, List<ProcUse> mem, bool? plugged, string powerMode)
    {
        var why = new List<string>();
        string Top(List<ProcUse> list, Func<ProcUse, double> v, string unit) =>
            string.Join(", ", list.Take(3).Select(a => $"{a.Name}{(a.Count > 1 ? $" ×{a.Count}" : "")} {v(a):0}{unit}"));
        // how it built up: steady, or a jump
        if (before.Count >= 3)
        {
            double? first = Pick(before[0], key), mid = Pick(before[before.Count / 2], key);
            if (first is { } f && value - f >= 25) why.Add($"It rose fast: from {f:0} to {value:0} in about {(now.At - before[0].At).TotalSeconds:0} s.");
            else if (first is { } g && mid is { } m && g >= line * 0.9 && m >= line * 0.9) why.Add("It had already been high for the whole minute before.");
        }
        switch (key)
        {
            case "cpu":
                if (cpu.Count > 0) why.Add($"Busiest apps: {Top(cpu, a => a.Cpu, "%")}.");
                if (cpu.Count > 0 && cpu[0].Cpu >= value * 0.5) why.Add($"{cpu[0].Name} alone was about half of it or more.");
                // protected processes (an antivirus scan, Windows Update, drivers) can't be looked into by any app
                if (cpu.Sum(a => a.Cpu) < value * 0.5) why.Add("Much of it was in system processes Windows keeps private (often an antivirus scan, Windows Update or a driver).");
                break;
            case "gpu":
                if (gpu.Count > 0) why.Add($"The graphics card was used most by: {Top(gpu, a => a.Gpu, "%")}.");
                else why.Add("No single app stood out on the graphics card (a video, the desktop's effects, or several apps together).");
                break;
            case "ram":
                if (mem.Count > 0) why.Add($"Biggest memory users: {string.Join(", ", mem.Take(3).Select(a => $"{a.Name}{(a.Count > 1 ? $" ×{a.Count}" : "")} {a.MemoryMb / 1024.0:0.0} GB"))}.");
                why.Add("Closing tabs or apps you're not using frees memory fastest.");
                break;
            case "cputemp":
            case "gputemp":
                bool cpuSide = key == "cputemp";
                double load = cpuSide ? now.Cpu : now.Gpu ?? 0;
                if (load >= 70)
                {
                    var list = cpuSide ? cpu : gpu;
                    why.Add($"The heat came with heavy work: {(cpuSide ? "CPU" : "GPU")} at {load:0}%{(list.Count > 0 ? $", mostly {Top(list, a => cpuSide ? a.Cpu : a.Gpu, "%")}" : "")}.");
                }
                else why.Add($"It was hot while the {(cpuSide ? "CPU" : "GPU")} was only at {load:0}%: the cooling may be struggling (blocked vents, dust, a soft surface under a laptop, a warm room).");
                if (powerMode == "Performance") why.Add("The power mode was set to Performance, which lets it run hotter.");
                break;
        }
        if (plugged == false && key is "cpu" or "gpu") why.Add("It was on battery.");
        return why;
    }

    public static double? Pick(StrainSample s, string key) => key switch
    {
        "cpu" => s.Cpu, "gpu" => s.Gpu, "ram" => s.Ram, "gputemp" => s.GpuTemp, "cputemp" => s.CpuTemp, _ => null,
    };

    // ───────────── the readable report ─────────────

    public static string WriteReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Dynamic Notch · strain log (Cyberpsychosis risk)");
        sb.AppendLine($"{Events.Count} warning(s), newest first · {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine(new string('═', 64));
        foreach (var e in Events)
        {
            sb.AppendLine();
            sb.AppendLine($"{e.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}   {e.Name} {e.Value:0}{e.Unit} for {e.Seconds} s (line {e.Line:0}{e.Unit})");
            sb.AppendLine(new string('─', 64));
            foreach (var c in e.Causes) sb.AppendLine("  • " + c);
            var n = e.Now;
            sb.AppendLine($"  At that moment: CPU {n.Cpu:0}%" + (n.Gpu is { } g ? $" · GPU {g:0}%" : "") + $" · memory {n.Ram:0}%" +
                (n.CpuTemp is { } ct ? $" · CPU {ct:0}°C" : "") + (n.GpuTemp is { } gt ? $" · GPU {gt:0}°C" : ""));
            if (e.Before.Count > 0)
                sb.AppendLine("  The minute before (" + e.Name + "): " + string.Join(" → ", e.Before.Select(s => Pick(s, e.Key) is { } v ? $"{v:0}" : "–")));
            void List(string title, List<ProcUse> l, Func<ProcUse, string> v)
            {
                if (l.Count == 0) return;
                sb.AppendLine($"  {title}:");
                foreach (var a in l) sb.AppendLine($"     {a.Name}{(a.Count > 1 ? $" ×{a.Count}" : ""),-6}  {v(a)}");
            }
            List("CPU", e.TopCpu, a => $"{a.Cpu:0.0}%");
            List("GPU", e.TopGpu, a => $"{a.Gpu:0.0}%");
            List("Memory", e.TopMemory, a => $"{a.MemoryMb:N0} MB");
            sb.AppendLine($"  In front: {e.Foreground ?? "–"} · power: {(e.PluggedIn == null ? "desktop" : e.PluggedIn == true ? "plugged in" : "on battery")}" +
                (e.Battery is { } b ? $" {b}%" : "") + $" · mode: {e.PowerMode}");
            sb.AppendLine($"  PC: {e.CpuName} · {e.Cores} threads · {e.MemoryGb} GB");
        }
        try { File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(true)); }
        catch (Exception ex) { AppLog.Write("strain report: " + ex.Message); }
        return ReportPath;
    }
}
