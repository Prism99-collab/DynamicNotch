using System.IO;
using System.Windows.Threading;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// Game mode: while a game is in front, the notch shrinks to the smallest pill it has, the frame rate and
/// nothing else, and stays out of the way (no pop-ups, no side island, no glow). Alt-Tab and it's gone.
/// A game is something from a game library folder (Steam, Epic, Xbox, Riot, EA, Ubisoft, GOG, Battle.net...),
/// or any full-screen app that keeps the GPU busy (and isn't a browser or a video player).
/// The frame rate is counted from the frames the game hands to Windows (see <see cref="FpsService"/>).
/// </summary>
public partial class NotchWindow
{
    sealed record GameState(uint Pid, string Name);

    GameState? _game;
    double? _gameFps;
    bool _fpsOn;              // frames are being counted for the current game
    readonly FpsService _fps = new();
    readonly GameService _gameService = new();
    readonly DispatcherTimer _gameTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    // the number itself: 20 times a second, gliding toward the real rate so it reads as one continuous value
    readonly DispatcherTimer _fpsTicker = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
    int _fpsText = -1;
    // after Alt-Tab the trace keeps counting that game for a minute: going back is instant, number included
    GameState? _lastGame;
    readonly DispatcherTimer _fpsLinger = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
    bool _fpsResting;         // in the game, but it shows no frames (paused, loading, minimised): the pill waits
    uint _gameProbePid;       // the full-screen app being watched to see whether it's a game
    int _gameProbeHits, _gameProbeReads;
    bool _gameSampling;
    int _gameSilent;          // seconds in a row the game showed no frames
    (uint Pid, bool Fullscreen) _gameRuledOut; // the app in front was already looked at and isn't a game
    uint _gameStarting;       // a game being set up right now (its name and the frame trace are on their way)
    Task _fpsQueue = Task.CompletedTask; // trace starts and stops, strictly one after another

    /// <summary>Runs a trace start / stop after the previous one: leaving one game and entering the next can't cross.</summary>
    Task<T> FpsQueue<T>(Func<T> op)
    {
        var next = _fpsQueue.ContinueWith(_ => op(), TaskScheduler.Default);
        _fpsQueue = next;
        return next;
    }

    static readonly string[] GameFolders =
    {
        @"\steamapps\common\", @"\epic games\", @"\xboxgames\", @"\windowsapps\microsoft.minecraft", @"\riot games\",
        @"\ea games\", @"\electronic arts\", @"\ubisoft game launcher\games\", @"\gog galaxy\games\", @"\gog games\",
        @"\battle.net\", @"\blizzard\", @"\rockstar games\", @"\games\", @"\minecraft",
    };

    // full screen, busy GPU, still not a game
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "brave", "chrome", "msedge", "firefox", "opera", "opera_gx", "vivaldi", "arc", "vlc", "mpv", "mpc-hc64", "mpc-be64", "potplayer",
        "potplayermini64", "wmplayer", "video.ui", "microsoft.media.player", "netflix", "spotify", "discord", "obs64", "explorer",
        "powerpnt", "blender", "resolve", "premiere", "afterfx", "photoshop", "unity", "unrealeditor", "steamwebhelper", "epicgameslauncher",
        "riotclientux", "riotclientservices", "battle.net", "eadesktop", "ubisoftconnect", "galaxyclient",
    };

    // in a game library folder but not the game: launchers, clients, helpers, updaters, crash reporters, overlays
    static readonly string[] NotTheGame =
    {
        "launcher", "client", "helper", "service", "updater", "update", "crash", "report", "setup", "install", "patch",
        "redist", "bootstrap", "webhelper", "cef", "overlay", "socialclub", "agent", "anticheat", "battleye", "uninstall", "prereq",
    };

    /// <summary>A real game binary ("…-Win64-Shipping" and friends pass even with "client" in the name).</summary>
    static bool LooksLikeTheGame(string exe) =>
        exe.Contains("shipping", StringComparison.OrdinalIgnoreCase) || exe.Contains("-win64", StringComparison.OrdinalIgnoreCase)
        || !NotTheGame.Any(w => exe.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>A game fills (most of) the screen; a launcher is a window.</summary>
    bool CoversScreen(IntPtr fg, double share = 0.6)
    {
        if (!GetWindowRect(fg, out var r) || MonitorBounds(fg) is not { } m) return false;
        double w = Math.Min(r.Right, m.Right) - Math.Max(r.Left, m.Left), h = Math.Min(r.Bottom, m.Bottom) - Math.Max(r.Top, m.Top);
        double area = (double)(m.Right - m.Left) * (m.Bottom - m.Top);
        return w > 0 && h > 0 && w * h >= area * share;
    }

    /// <summary>The bounds of the monitor a window is on (a game may be on another screen than the notch).</summary>
    static RECT? MonitorBounds(IntPtr hwnd)
    {
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* NEAREST */), ref info) ? info.rcMonitor : null;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>Playing, and the frame rate can be shown: the notch is the tiny FPS pill.</summary>
    bool InGame => _game != null && _fpsOn && _settings.GameStats;

    void WireGame()
    {
        _gameTimer.Tick += (_, _) => OnGameTick();
        _fpsTicker.Tick += (_, _) => OnFpsTick();
        _fpsLinger.Tick += (_, _) => StopLinger();
    }

    /// <summary>The minute after leaving a game is over (or another game started): the trace stops.</summary>
    void StopLinger()
    {
        _fpsLinger.Stop();
        if (_lastGame == null) return;
        _lastGame = null;
        if (_game == null) _ = FpsQueue(() => { _fps.Stop(); return true; });
    }

    /// <summary>Back in the game just left: game mode again at once, the trace never stopped, the number is already live.</summary>
    bool ResumeGame(uint pid)
    {
        if (_lastGame is not { } g || g.Pid != pid) return false;
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); } // throws once it has quit (HasExited is refused for anti-cheat games)
        catch { return false; }
        _fpsLinger.Stop();
        _lastGame = null;
        _game = g;
        _fpsOn = true;
        _fpsResting = false;
        _gameSilent = 0;
        GameQuiet(true);
        _fpsText = -1;
        OnFpsTick(); // the live number before the pill even appears
        _fpsTicker.Start();
        if (!_gameTimer.IsEnabled) _gameTimer.Start();
        AppLog.Write($"game mode: back in {g.Name}");
        return true;
    }

    /// <summary>Called on every foreground check: is the app in front a game, and is the current game still in front?</summary>
    void CheckGame(IntPtr fg, bool fullscreen)
    {
        // full screen on its own monitor counts, wherever that monitor is
        if (!fullscreen && fg != IntPtr.Zero && fg != _hwnd) fullscreen = CoversScreen(fg, 0.98);
        if (!_settings.GameStats) { if (_game != null) EndGame(linger: false); StopLinger(); _gameProbePid = 0; return; }
        uint pid = 0;
        if (fg != IntPtr.Zero && fg != _hwnd) GetWindowThreadProcessId(fg, out pid);
        pid = HostedApp(fg, pid);
        if (_gameProbePid != 0 && pid != _gameProbePid) _gameProbePid = 0;
        if (_game != null)
        {
            if (pid == _game.Pid) return;
            if (pid != 0) EndGame(); // Alt-Tab: gone
            else return;
        }
        if (pid == 0 || pid == Environment.ProcessId || pid == _gameStarting) return;
        if (ResumeGame(pid)) return;
        if (_gameRuledOut == (pid, fullscreen) || pid == _gameProbePid) return;

        string? path = ProcessPathById(pid);
        if (path == null)
        {
            // protected by an anti-cheat (League with Vanguard, …): Windows hides its path but not its name.
            // A protected app filling its screen is a game, unless its name says launcher / client / helper.
            string? name = null;
            try { using var proc = System.Diagnostics.Process.GetProcessById((int)pid); name = proc.ProcessName; } catch { }
            if (name == null) return;
            if (NotGames.Contains(name) || NotApps.Contains(name) || !LooksLikeTheGame(name)) { _gameRuledOut = (pid, fullscreen); return; }
            if (fullscreen) StartGame(pid, null, name);
            return;
        }
        string exe = Path.GetFileNameWithoutExtension(path);
        if (NotGames.Contains(exe) || NotApps.Contains(exe) || !LooksLikeTheGame(exe)) { _gameRuledOut = (pid, fullscreen); return; }
        string lower = path.ToLowerInvariant();
        // from a game library: the game, once its window fills the screen (a launcher there is just a window)
        if (GameFolders.Any(f => lower.Contains(f)))
        {
            if (fullscreen || CoversScreen(fg)) { StartGame(pid, path); return; }
            return; // not ruled out: it may still go full screen (checked again on the next look)
        }

        // anything else full screen: watch its GPU use for a few seconds
        if (fullscreen) { _gameProbePid = pid; _gameProbeHits = 0; _gameProbeReads = 0; if (!_gameTimer.IsEnabled) _gameTimer.Start(); }
        else _gameRuledOut = (pid, false);
    }

    async void StartGame(uint pid, string? path, string? knownName = null)
    {
        if (_gameStarting == pid || _game != null) return;
        _gameStarting = pid;
        _gameProbePid = 0;
        var name = path != null ? (await DescribeAppAsync(path)).App : knownName ?? "Game";
        _gameStarting = 0;
        if (_game != null) return;
        // still in front? (Alt-Tab while its name was looked up)
        GetWindowThreadProcessId(GetForegroundWindow(), out uint front);
        if (front != pid) return;
        _game = new GameState(pid, name);
        GameQuiet(true);
        _gameFps = null;
        _gameSilent = 0;
        _fpsResting = false;
        _fpsText = -1;
        GameFps.Text = "–";
        if (_lastGame != null) { _lastGame = null; _fpsLinger.Stop(); } // another game: the trace moves to it
        bool on = await FpsQueue(() => _fps.Start((int)pid));
        if (_game?.Pid != pid) { if (on && _game == null) _ = FpsQueue(() => { _fps.Stop(); return true; }); return; } // left the game meanwhile
        _fpsOn = on;
        AppLog.Write(_fpsOn ? $"game mode: {name}" : $"game mode: {name} (no FPS: this account can't read frame events; Settings → FPS in games sets it up)");
        if (!_gameTimer.IsEnabled) _gameTimer.Start();
        if (_fpsOn) _fpsTicker.Start();
        CheckForeground(); // full screen no longer hides the notch
        RefreshBase();
    }

    void EndGame(bool linger = true)
    {
        if (_game == null) return;
        AppLog.Write($"game mode ended: {_game.Name}");
        var left = _game;
        _game = null;
        GameQuiet(false);
        _gameFps = null;
        _fpsTicker.Stop();
        if (_fpsOn || _fpsResting)
        {
            _fpsOn = false;
            _fpsResting = false;
            // keep counting it for a minute: Alt-Tab back and the pill is there at once, already live
            if (linger) { _lastGame = left; _fpsLinger.Stop(); _fpsLinger.Start(); }
            else _ = FpsQueue(() => { _fps.Stop(); return true; });
        }
        if (_gameProbePid == 0) _gameTimer.Stop();
        RefreshBase();
    }

    async void OnGameTick()
    {
        if (_game != null)
        {
            if (!_fpsOn && !_fpsResting) return;
            bool drawing = _fps.Read() is { } r && r >= 1;
            if (_fpsResting)
            {
                // frames again (the menu closed, the level loaded): the pill comes back
                if (!drawing) return;
                AppLog.Write($"game mode: {_game.Name} shows frames again");
                _fpsResting = false;
                _fpsOn = true;
                _gameSilent = 0;
                _fpsText = -1;
                OnFpsTick();
                _fpsTicker.Start();
                CheckForeground();
                RefreshBase();
                return;
            }
            // no frames for a while: restart the trace once (something may have stopped it), then let the pill
            // rest rather than show an empty counter
            _gameSilent = drawing ? 0 : _gameSilent + 1;
            if (_gameSilent == 3)
            {
                uint pid0 = _game.Pid;
                _ = FpsQueue(() => { _fps.Stop(); return _fps.Start((int)pid0); });
            }
            else if (_gameSilent >= 7)
            {
                AppLog.Write($"game mode: {_game.Name} shows no frames, the FPS pill rests");
                _fpsOn = false;
                _fpsResting = true;
                _fpsTicker.Stop();
                CheckForeground();
                RefreshBase();
            }
            return;
        }

        // watching a full-screen app: three busy GPU readings in a row make it a game
        uint pid = _gameProbePid;
        if (pid == 0) { _gameTimer.Stop(); return; }
        if (_gameSampling) return;
        _gameSampling = true;
        GameService.Stats s;
        try { s = await Task.Run(() => _gameService.Sample(pid)); }
        catch { return; }
        finally { _gameSampling = false; }
        if (pid != _gameProbePid) return;
        _gameProbeHits = s.GpuApp >= 25 ? _gameProbeHits + 1 : 0;
        if (++_gameProbeReads > 15 && _gameProbeHits == 0) { _gameRuledOut = (pid, true); _gameProbePid = 0; _gameTimer.Stop(); return; }
        if (_gameProbeHits >= 3 && ProcessPathById(pid) is { } path) StartGame(pid, path);
    }

    /// <summary>The live number: the real rate from the last half second of frame times, eased so the digits glide.</summary>
    void OnFpsTick()
    {
        if (!_fpsOn || _game == null) { _fpsTicker.Stop(); return; }
        if (_fps.Read() is not { } f || f < 1) return;
        _gameFps = f;
        // the exact rate of the last half second of real frame times, no easing: never behind the game
        int shown = (int)Math.Round(f);
        if (shown == _fpsText) return; // only redraw when the number actually changes
        _fpsText = shown;
        GameFps.Text = shown.ToString();
    }

    /// <summary>
    /// The "FPS in games" switch. Reading a game's frames needs this account in Windows' "Performance Log Users"
    /// group: turning it on where that's missing asks for it once (an administrator prompt), and Windows applies
    /// it at the next sign-in.
    /// </summary>
    async void SetFpsInGames(bool on)
    {
        if (on && !FpsService.AccountCanTrace())
        {
            // Windows' permission prompt stays up as long as it likes: wait for it off the UI thread
            if (!await Task.Run(FpsService.RequestAccess))
            {
                ShowOsd("fps", "", null, "FPS in games stays off", "Windows needs your OK once to read a game's frames", null, glyphBrush: Orange, seconds: 3.5, force: true);
                SyncSettingsPage();
                return;
            }
            ShowOsd("fps", "", null, "Almost there", "Sign out and back in once, then FPS shows in games", null, glyphBrush: Green, seconds: 4.5, force: true);
        }
        _settings.GameStats = on;
        _settings.Save();
        CheckForeground();
        RefreshBase();
    }

    /// <summary>
    /// While a game is in front the notch gets out of its way: its process drops to below-normal priority and
    /// asks Windows for efficiency mode (EcoQoS: lower clocks, efficiency cores), so the game's threads always win.
    /// </summary>
    static void GameQuiet(bool on)
    {
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            me.PriorityClass = on ? System.Diagnostics.ProcessPriorityClass.BelowNormal : System.Diagnostics.ProcessPriorityClass.Normal;
            var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = on ? 1u : 0u, StateMask = on ? 1u : 0u };
            SetProcessInformation(me.Handle, 4 /* ProcessPowerThrottling */, ref state, (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch { }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    /// <summary>
    /// Store / Xbox games: the window in front belongs to a host (ApplicationFrameHost) and the game is the process
    /// behind its content. That's the one drawing the frames.
    /// </summary>
    static uint HostedApp(IntPtr fg, uint pid)
    {
        if (pid == 0 || fg == IntPtr.Zero) return pid;
        string? path = ProcessPathById(pid);
        if (path == null || !Path.GetFileNameWithoutExtension(path).Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return pid;
        uint inner = pid;
        EnumChildWindows(fg, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint p);
            if (p != 0 && p != pid) { inner = p; return false; }
            return true;
        }, IntPtr.Zero);
        return inner;
    }

    delegate bool ChildProc(IntPtr hwnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, ChildProc proc, IntPtr lParam);

    void StopGameTracing()
    {
        GameQuiet(false);
        try { _fps.Stop(); } catch { }
        try { _gameService.Dispose(); } catch { }
    }
}
