using System.IO;
using System.Windows;
using DynamicNotch.Services;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DynamicNotch;

public partial class App : Application
{
    Mutex? _mutex;
    Forms.NotifyIcon? _tray;
    NotchWindow? _window;
    Settings _settings = new();
    readonly List<(Forms.ToolStripMenuItem, Func<bool>)> _checks = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        bool auto = e.Args.Contains(KeepAlive.AutoArg, StringComparer.OrdinalIgnoreCase);
        bool restarted = e.Args.Contains(KeepAlive.RestartArg, StringComparer.OrdinalIgnoreCase);

        if (e.Args.Contains(KeepAlive.UninstallArg, StringComparer.OrdinalIgnoreCase))
        {
            // called by the uninstaller: close the running notch and undo everything it set up in Windows
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("DynamicNotch"))
                using (p) { if (p.Id != Environment.ProcessId) try { p.Kill(); p.WaitForExit(3000); } catch { } }
            KeepAlive.RemoveAll();
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, "DynamicNotch.SingleInstance", out bool first);
        if (!first)
        {
            // a restart after a crash: give the dying copy a moment to let go, otherwise just leave
            bool got = false;
            if (restarted) try { got = _mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { got = true; }
            if (!got) { Shutdown(); return; }
        }
        // automatic starts are skipped when the user deliberately chose Exit
        if (auto && KeepAlive.UserTurnedOff) { Shutdown(); return; }
        if (!auto) KeepAlive.ClearUserExit(); // opened by hand (icon, Start menu, login): back on

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            AppLog.Write($"FATAL (background thread): {a.ExceptionObject}");
            KeepAlive.RestartNow(_settings.KeepRunning); // come straight back
        };
        TaskScheduler.UnobservedTaskException += (_, a) => { AppLog.Write($"background task error: {a.Exception.GetBaseException()}"); a.SetObserved(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppLog.Write($"process exit (code {Environment.ExitCode})");
        SessionEnding += (_, a) => AppLog.Write($"Windows session ending ({a.ReasonSessionEnding})");
        base.OnStartup(e);

        try { Directory.CreateDirectory(Settings.Folder); } catch { }
        _settings = Settings.Load();
        // WPF caps every animation at 60 fps unless told otherwise: let them run at the screen's real refresh
        // rate (144 / 180 / 240 Hz...). Animations that pick their own rate (visualizer, glow) keep theirs.
        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata { DefaultValue = (int?)Interop.Native.RefreshRate() });
        AppLog.Write($"started ({(restarted ? "after a crash" : auto ? "automatically" : "by user/login")}), pid {Environment.ProcessId}");
        try
        {
            _window = new NotchWindow(_settings);
            _window.Show();
        }
        catch (Exception ex)
        {
            // never run invisibly: tell the user what happened and where the details are
            AppLog.Write($"FATAL (startup): {ex}");
            MessageBox.Show($"Dynamic Notch couldn't start on this PC.\n\n{ex.GetBaseException().Message}\n\nDetails are in {Path.Combine(Settings.Folder, "app.log")}",
                "Dynamic Notch", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        try { BuildTray(); } catch (Exception ex) { AppLog.Write("tray icon skipped: " + ex.Message); }
        KeepAlive.Sync(_settings); // start-at-sign-in and keep-alive, repaired on every launch
    }

    void BuildTray()
    {
        Forms.Application.EnableVisualStyles();
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add(Controls.DarkMenu.Header("Dynamic Notch"));
        menu.Items.Add("Open control center    Win+Alt+N", null, (_, _) => _window?.Expand(byHotkey: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        AddCheck(menu, "Minimal when nothing is happening", () => _settings.MinimalWhenIdle, v => _settings.MinimalWhenIdle = v);
        AddCheck(menu, "Notification alerts", () => _settings.NotificationAlerts, v => _settings.NotificationAlerts = v);
        AddCheck(menu, "System alerts", () => _settings.SystemAlerts, v => _settings.SystemAlerts = v);
        AddCheck(menu, "App launch alerts", () => _settings.AppLaunchAlerts, v => _settings.AppLaunchAlerts = v);
        AddCheck(menu, "\"Copied\" alerts", () => _settings.ClipboardAlerts, v => _settings.ClipboardAlerts = v);
        AddCheck(menu, "Show music activity", () => _settings.ShowMediaActivity, v => _settings.ShowMediaActivity = v);
        AddCheck(menu, "Animated music visualizer", () => _settings.AnimatedVisualizer, v => _settings.AnimatedVisualizer = v);
        AddCheck(menu, "Open by touching the top edge", () => _settings.HoverToOpen, v => _settings.HoverToOpen = v);
        AddCheck(menu, "Slim notch over browser tabs / windows", () => _settings.SlimOverWindows, v => _settings.SlimOverWindows = v);
        AddCheck(menu, "Hide in full-screen apps", () => _settings.HideInFullscreen, v => _settings.HideInFullscreen = v);
        AddCheck(menu, "Battery mode when unplugged", () => _settings.EcoOnBattery, v => _settings.EcoOnBattery = v);
        AddCheck(menu, "Start with Windows", () => _settings.StartWithWindows, v =>
        {
            _settings.StartWithWindows = v;
            KeepAlive.Sync(_settings);
        });
        AddCheck(menu, "Always keep running (restart after lock / crash)", () => _settings.KeepRunning, v =>
        {
            _settings.KeepRunning = v;
            KeepAlive.Sync(_settings);
        });
        menu.Opening += (_, _) => { foreach (var (item, get) in _checks) item.Checked = get(); };
        menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = menu.Items.Add("Exit", null, (_, _) => ExitApp());
        exit.Tag = "danger";
        Controls.DarkMenu.Apply(menu);

        _tray = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "Dynamic Notch",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _window?.Expand(byHotkey: true);
        };
    }

    void AddCheck(Forms.ContextMenuStrip menu, string text, Func<bool> get, Action<bool> set)
    {
        var item = new Forms.ToolStripMenuItem(text) { Checked = get(), CheckOnClick = true };
        _checks.Add((item, get));
        item.Click += (_, _) =>
        {
            set(item.Checked);
            _settings.Save();
            _window?.OnSettingsChanged();
        };
        menu.Items.Add(item);
    }

    static Drawing.Icon CreateTrayIcon()
    {
        const int s = 64;
        using var bmp = new Drawing.Bitmap(s, s);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var path = new Drawing.Drawing2D.GraphicsPath();
            var r = new Drawing.RectangleF(4, 20, 56, 24);
            float d = 24;
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();
            using var white = new Drawing.SolidBrush(Drawing.Color.White);
            g.FillPath(white, path);
            using var black = new Drawing.SolidBrush(Drawing.Color.Black);
            g.FillEllipse(black, 40, 27, 10, 10);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    void ExitApp()
    {
        KeepAlive.MarkUserExit(); // automatic starts respect this until the notch is opened again
        AppLog.Write("exit chosen by user");
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _window?.Shutdown();
        Shutdown();
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch { }
        AppLog.Write("UI error (handled): " + e.Exception.GetBaseException().Message);
        e.Handled = true; // a notch should never crash the desktop experience
    }
}
