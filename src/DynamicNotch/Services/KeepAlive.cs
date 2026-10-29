using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DynamicNotch.Services;

/// <summary>
/// Starting with Windows and staying alive, all owned by the app itself (no admin rights needed):
/// <list type="bullet">
/// <item>a per-user Task Scheduler task starts it at sign-in ("Start with Windows") and brings it back
/// after unlock or an unexpected exit ("Always keep running");</item>
/// <item>Windows' restart manager relaunches it after a crash or a hang;</item>
/// <item>choosing Exit leaves it off until it is opened again or the PC restarts.</item>
/// </list>
/// </summary>
public static class KeepAlive
{
    public const string AutoArg = "--auto";
    public const string RestartArg = "--restarted";
    public const string UninstallArg = "--uninstall";
    const string TaskName = "Dynamic Notch";

    static string StopFlag => Path.Combine(Settings.Folder, "stopped.flag");

    /// <summary>True if the user chose Exit during this Windows session.</summary>
    public static bool UserTurnedOff
    {
        get
        {
            try
            {
                if (!File.Exists(StopFlag)) return false;
                // an Exit from before the last restart doesn't count: a fresh boot starts the notch again
                var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
                return File.GetLastWriteTime(StopFlag) > boot;
            }
            catch { return false; }
        }
    }

    public static void MarkUserExit()
    {
        try { Directory.CreateDirectory(Settings.Folder); File.WriteAllText(StopFlag, DateTime.Now.ToString("u")); } catch { }
        try { UnregisterApplicationRestart(); } catch { }
    }

    public static void ClearUserExit()
    {
        try { if (File.Exists(StopFlag)) File.Delete(StopFlag); } catch { }
    }

    /// <summary>Brings Windows in line with the two settings. Cheap to call; does the work on a background thread.</summary>
    public static void Sync(Settings s)
    {
        try
        {
            // restart after a crash or hang (Windows only does this once the app has run for 60 s)
            if (s.KeepRunning) RegisterApplicationRestart($"{AutoArg} {RestartArg}", 0);
            else UnregisterApplicationRestart();
        }
        catch { }

        bool logon = s.StartWithWindows, guard = s.KeepRunning;
        Task.Run(() =>
        {
            try
            {
                RemoveLegacyEntries();
                if (!logon && !guard) { RunSchtasks($"/Delete /TN \"{TaskName}\" /F", quiet: true); return; }
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) return;
                string xml = TaskXml(exe, logon, guard);
                // already right (and switched on: an interrupted upgrade may have left it disabled)
                if (CurrentTaskXml() is { } existing && !existing.Contains("<Enabled>false</Enabled>") && Normalize(existing) == Normalize(xml)) return;
                string path = Path.Combine(Path.GetTempPath(), $"DynamicNotch-task-{Environment.ProcessId}.xml");
                File.WriteAllText(path, xml, Encoding.Unicode);
                RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{path}\" /F");
                try { File.Delete(path); } catch { }
            }
            catch (Exception ex) { AppLog.Write("startup task setup failed: " + ex.Message); }
        });
    }

    /// <summary>Removes everything the app set up in Windows (called by the uninstaller).</summary>
    public static void RemoveAll()
    {
        try { UnregisterApplicationRestart(); } catch { }
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F", quiet: true);
        RemoveLegacyEntries();
    }

    /// <summary>Startup entries used by pre-release builds; the scheduled task replaces them all.</summary>
    static void RemoveLegacyEntries()
    {
        RunSchtasks("/Delete /TN \"DynamicNotch Guardian\" /F", quiet: true);
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue("DynamicNotch", false);
            using var ar = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true);
            ar?.DeleteValue("DynamicNotch", false);
            using var af = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", true);
            af?.DeleteValue("Dynamic Notch.lnk", false);
            string startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Dynamic Notch.lnk");
            if (File.Exists(startup)) File.Delete(startup);
        }
        catch { }
    }

    /// <summary>Last resort from a fatal-error handler: launch a fresh copy, then let this one die.</summary>
    public static void RestartNow(bool enabled)
    {
        if (!enabled || UserTurnedOff || Environment.ProcessPath is not { } exe) return;
        try { Process.Start(new ProcessStartInfo(exe, $"{AutoArg} {RestartArg}") { UseShellExecute = false }); } catch { }
    }

    static string? CurrentTaskXml()
    {
        var (code, output) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML", quiet: true);
        return code == 0 ? output : null;
    }

    // compare only what we control (Windows reformats the XML and adds its own fields)
    static string Normalize(string xml)
    {
        var sb = new StringBuilder();
        foreach (var tag in new[] { "LogonTrigger", "SessionStateChangeTrigger", "TimeTrigger", "Command", "Arguments" })
            sb.Append(tag).Append('=').Append(System.Text.RegularExpressions.Regex.Matches(xml, $"<{tag}[ >]").Count).Append(';');
        var cmd = System.Text.RegularExpressions.Regex.Match(xml, "<Command>(.*?)</Command>");
        sb.Append(cmd.Success ? cmd.Groups[1].Value.Trim('"').ToLowerInvariant() : "");
        return sb.ToString();
    }

    static (int Code, string Output) RunSchtasks(string args, bool quiet = false)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return (-1, "");
            string output = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            if (p.ExitCode != 0 && !quiet) AppLog.Write($"schtasks {args.Split(' ')[0]} failed ({p.ExitCode}): {err.Trim()}");
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            if (!quiet) AppLog.Write("schtasks failed: " + ex.Message);
            return (-1, "");
        }
    }

    static string TaskXml(string exe, bool logon, bool guard)
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name) ?? "";
        string dir = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "") ?? "";
        var triggers = new StringBuilder();
        if (logon)
            triggers.Append($"<LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger>");
        if (guard)
        {
            triggers.Append($"<SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>SessionUnlock</StateChange><UserId>{user}</UserId></SessionStateChangeTrigger>");
            triggers.Append($"<SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>ConsoleConnect</StateChange><UserId>{user}</UserId></SessionStateChangeTrigger>");
            triggers.Append("<TimeTrigger><Repetition><Interval>PT5M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>" +
                            "<StartBoundary>2024-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger>");
        }
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>Dynamic Notch</Author>
    <Description>Starts Dynamic Notch when you sign in and brings it back after lock, sleep or an unexpected exit. Choose Exit in its tray menu to keep it off; change this in the notch's Settings page.</Description>
  </RegistrationInfo>
  <Triggers>{triggers}</Triggers>
  <Principals>
    <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>{AutoArg}</Arguments><WorkingDirectory>{dir}</WorkingDirectory></Exec>
  </Actions>
</Task>
""";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterApplicationRestart(string commandLine, int flags);

    [DllImport("kernel32.dll")]
    static extern int UnregisterApplicationRestart();
}

/// <summary>Small rolling log so an unexpected exit always leaves a clue.</summary>
public static class AppLog
{
    static readonly object Gate = new();
    static string LogPath => Path.Combine(Settings.Folder, "app.log");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Folder);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 256 * 1024)
                {
                    var keep = File.ReadAllLines(LogPath).TakeLast(400);
                    File.WriteAllLines(LogPath, keep);
                }
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
            }
        }
        catch { }
    }
}
