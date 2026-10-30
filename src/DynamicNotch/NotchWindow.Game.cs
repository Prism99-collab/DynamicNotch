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
    double _fpsShown;
    int _fpsText = -1;
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

    /// <summary>Playing, and the frame rate can be shown: the notch is the tiny FPS pill.</summary>
    bool InGame => _game != null && _fpsOn && _settings.GameStats;

    void WireGame()
    {
        _gameTimer.Tick += (_, _) => OnGameTick();
        _fpsTicker.Tick += (_, _) => OnFpsTick();
    }

    /// <summary>Called on every foreground check: is the app in front a game, and is the current game still in front?</summary>
    void CheckGame(IntPtr fg, bool fullscreen)
    {
        if (!_settings.GameStats) { if (_game != null) EndGame(); _gameProbePid = 0; return; }
        uint pid = 0;
        if (fg != IntPtr.Zero && fg != _hwnd) GetWindowThreadProcessId(fg, out pid);
        if (_gameProbePid != 0 && pid != _gameProbePid) _gameProbePid = 0;
        if (_game != null)
        {
            if (pid == _game.Pid) return;
            if (pid != 0) EndGame(); // Alt-Tab: gone
            else return;
        }
        if (pid == 0 || pid == Environment.ProcessId || pid == _gameStarting) return;
        if (_gameRuledOut == (pid, fullscreen) || pid == _gameProbePid) return;

        string? path = ProcessPathById(pid);
        if (path == null) return;
        string exe = Path.GetFileNameWithoutExtension(path);
        if (NotGames.Contains(exe) || NotApps.Contains(exe)) { _gameRuledOut = (pid, fullscreen); return; }
        string lower = path.ToLowerInvariant();
        if (GameFolders.Any(f => lower.Contains(f))) { StartGame(pid, path); return; }

        // anything else full screen: watch its GPU use for a few seconds
        if (fullscreen) { _gameProbePid = pid; _gameProbeHits = 0; _gameProbeReads = 0; if (!_gameTimer.IsEnabled) _gameTimer.Start(); }
        else _gameRuledOut = (pid, false);
    }

    async void StartGame(uint pid, string path)
    {
        if (_gameStarting == pid || _game != null) return;
        _gameStarting = pid;
        _gameProbePid = 0;
        var (name, _) = await DescribeAppAsync(path);
        _gameStarting = 0;
        if (_game != null) return;
        // still in front? (Alt-Tab while its name was looked up)
        GetWindowThreadProcessId(GetForegroundWindow(), out uint front);
        if (front != pid) return;
        _game = new GameState(pid, name);
        _gameFps = null;
        _gameSilent = 0;
        _fpsShown = 0;
        _fpsText = -1;
        GameFps.Text = "–";
        bool on = await FpsQueue(() => _fps.Start((int)pid));
        if (_game?.Pid != pid) { if (on && _game == null) _ = FpsQueue(() => { _fps.Stop(); return true; }); return; } // left the game meanwhile
        _fpsOn = on;
        AppLog.Write(_fpsOn ? $"game mode: {name}" : $"game mode: {name} (no FPS: this account can't read frame events; Settings → FPS in games sets it up)");
        if (!_gameTimer.IsEnabled) _gameTimer.Start();
        if (_fpsOn) _fpsTicker.Start();
        CheckForeground(); // full screen no longer hides the notch
        RefreshBase();
    }

    void EndGame()
    {
        if (_game == null) return;
        AppLog.Write($"game mode ended: {_game.Name}");
        _game = null;
        _gameFps = null;
        _fpsTicker.Stop();
        if (_fpsOn) { _fpsOn = false; _ = FpsQueue(() => { _fps.Stop(); return true; }); }
        if (_gameProbePid == 0) _gameTimer.Stop();
        RefreshBase();
    }

    async void OnGameTick()
    {
        if (_game != null)
        {
            if (!_fpsOn) return;
            // no frames for a while: restart the trace once (something may have stopped it), then step aside
            // rather than show an empty counter
            _gameSilent = _fps.Read() is { } r && r < 1 ? _gameSilent + 1 : 0;
            if (_gameSilent == 3)
            {
                uint pid0 = _game.Pid;
                _ = FpsQueue(() => { _fps.Stop(); return _fps.Start((int)pid0); });
            }
            else if (_gameSilent >= 7)
            {
                AppLog.Write($"game mode: {_game.Name} shows no frames, no FPS pill");
                _fpsOn = false;
                _fpsTicker.Stop();
                _ = FpsQueue(() => { _fps.Stop(); return true; });
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
        _fpsShown = _fpsShown <= 0 ? f : _fpsShown + (f - _fpsShown) * 0.35;
        int shown = (int)Math.Round(_fpsShown);
        if (shown == _fpsText || _mode != Mode.Game) return; // only redraw when the number actually changes
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

    void StopGameTracing()
    {
        try { _fps.Stop(); } catch { }
        try { _gameService.Dispose(); } catch { }
    }
}
