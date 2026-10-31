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
        Loc.Set(_settings.Language);
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
        // the tray menu stays short: open, Preferences (everything else lives there), lock, exit
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add(Controls.DarkMenu.Header("Dynamic Notch"));
        var open = menu.Items.Add("", null, (_, _) => { if (_window?.IsLocked == true) _window.SetLocked(false); _window?.Expand(byHotkey: true); });
        var prefs = (Forms.ToolStripMenuItem)menu.Items.Add("", null, (_, _) => { if (_window != null) PreferencesWindow.ShowFor(_window); });
        prefs.Font = new Drawing.Font("Segoe UI Variable Text", 9.5f, Drawing.FontStyle.Bold);
        var lockItem = new Forms.ToolStripMenuItem("") { CheckOnClick = true };
        lockItem.Click += (_, _) => _window?.SetLocked(lockItem.Checked);
        _checks.Add((lockItem, () => _window?.IsLocked == true));
        menu.Items.Add(lockItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = menu.Items.Add("", null, (_, _) => ExitApp());
        exit.Tag = "danger";
        void Texts()
        {
            open.Text = Loc.T("Open control center") + "    Win+Alt+N";
            prefs.Text = Loc.T("Preferences…");
            lockItem.Text = Loc.T("Lock the notch (shows things, won't react)");
            exit.Text = Loc.T("Exit");
        }
        Texts();
        menu.Opening += (_, _) => { Texts(); foreach (var (item, get) in _checks) item.Checked = get(); };
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
            if (e.Button != Forms.MouseButtons.Left || _window == null) return;
            if (_window.IsLocked) _window.SetLocked(false); // a click on the tray icon is how a locked notch comes back
            else _window.Expand(byHotkey: true);
        };
        if (_window != null)
        {
            void ShowLock() { if (_tray != null) _tray.Text = _window.IsLocked ? "Dynamic Notch (locked, click to unlock)" : "Dynamic Notch"; }
            _window.LockChanged += ShowLock;
            ShowLock();
            // the tray icon (and the windows' icon) in the theme
            _window.CyberChanged += cyber => { if (_tray != null) _tray.Icon = CreateTrayIcon(cyber); };
            if (_settings.Cyberpunk) _tray.Icon = CreateTrayIcon(true);
        }
    }

    /// <summary>The notch's own icon, for its windows (Preferences).</summary>
    public static System.Windows.Media.ImageSource? WindowIcon { get; private set; }

    /// <summary>Starts a fresh copy and closes this one (after a reset of the preferences).</summary>
    public void Restart()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, KeepAlive.RestartArg) { UseShellExecute = false });
        }
        catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _window?.Shutdown();
        Shutdown();
    }

    static Drawing.Icon CreateTrayIcon(bool cyber = false)
    {
        var icon = cyber ? DrawCyberTrayIcon() : DrawTrayIcon();
        try
        {
            WindowIcon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        catch { }
        return icon;
    }

    /// <summary>The cyberpunk notch: a cut black plate with a yellow frame and a cyan trace.</summary>
    static Drawing.Icon DrawCyberTrayIcon()
    {
        const int s = 64;
        using var bmp = new Drawing.Bitmap(s, s);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            var pts = new[] { new Drawing.PointF(3, 18), new(61, 18), new(61, 37), new(52, 46), new(11, 46), new(3, 38) };
            using var black = new Drawing.SolidBrush(Drawing.Color.FromArgb(0, 6, 14));
            g.FillPolygon(black, pts);
            using var yellow = new Drawing.Pen(Drawing.Color.FromArgb(0xFE, 0xE8, 0x01), 4) { LineJoin = Drawing.Drawing2D.LineJoin.Miter };
            g.DrawPolygon(yellow, pts);
            using var cyan = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x54, 0xC1, 0xE6));
            g.FillRectangle(cyan, 12, 30, 22, 4);
            using var dot = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xFE, 0xE8, 0x01));
            g.FillRectangle(dot, 44, 28, 7, 7);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    static Drawing.Icon DrawTrayIcon()
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
