using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicNotch.Services;

/// <summary>Now-playing info for any app that talks to Windows media controls (Spotify, browsers, Media Player...).</summary>
public sealed class MediaService
{
    readonly Dispatcher _ui;
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    string? _sessionId;
    int _version;
    bool _initialized;

    TimeSpan _pos;
    DateTimeOffset _posAt;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public ImageSource? Art { get; private set; }
    public Color Accent { get; private set; } = Colors.White;
    /// <summary>A second colour from the cover (the next strongest hue, or a neighbour of the first): bars and glows blend the two.</summary>
    public Color Accent2 { get; private set; } = Color.FromRgb(0x9A, 0xA4, 0xB8);
    public bool HasSession => _session != null;
    /// <summary>App id of the player ("Spotify.exe", "Chrome", "SpotifyAB.SpotifyMusic_…!Spotify").</summary>
    public string? SourceApp => _sessionId;
    public bool IsPlaying { get; private set; }
    public TimeSpan Duration { get; private set; }

    public TimeSpan Position
    {
        get
        {
            var p = IsPlaying ? _pos + (DateTimeOffset.Now - _posAt) : _pos;
            if (p < TimeSpan.Zero) p = TimeSpan.Zero;
            return Duration > TimeSpan.Zero && p > Duration ? Duration : p;
        }
    }

    /// <summary>Anything changed (UI thread).</summary>
    public event Action? Changed;
    /// <summary>A new song started (UI thread).</summary>
    public event Action? TrackChanged;

    public MediaService(Dispatcher ui) => _ui = ui;

    /// <summary>One app or browser tab that can play: what it shows and whether it's playing right now.</summary>
    public sealed class Player
    {
        internal Player(GlobalSystemMediaTransportControlsSession session, int id, string appId) { Session = session; Id = id; AppId = appId; }
        internal readonly GlobalSystemMediaTransportControlsSession Session;
        internal int Version;
        public int Id { get; }
        public string AppId { get; }
        public string Title { get; internal set; } = "";
        public string Artist { get; internal set; } = "";
        public ImageSource? Art { get; internal set; }
        public bool Playing { get; internal set; }

        /// <summary>"Brave", "Spotify", "Media Player": a readable name from the app id.</summary>
        public string AppName
        {
            get
            {
                string a = AppId;
                int bang = a.IndexOf('!');
                if (bang >= 0) a = a[(bang + 1)..];
                else if (a.Contains('_')) a = a.Split('_')[0].Split('.').Last();
                if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) a = a[..^4];
                if (a.Length > 0) a = char.ToUpperInvariant(a[0]) + a[1..];
                return a switch { "Msedge" => "Edge", "App" => "Media", "" => "Media", _ => a };
            }
        }
    }

    readonly List<Player> _players = new();
    Player? _chosen;
    int _nextPlayerId;

    /// <summary>Everything that has a media session open, in the order it appeared.</summary>
    public IReadOnlyList<Player> Players => _players;
    /// <summary>The one the notch and the Home card follow.</summary>
    public Player? Current => _players.FirstOrDefault(p => ReferenceEquals(p.Session, _session));
    /// <summary>A player appeared, closed, started or stopped, or changed what it plays (UI thread).</summary>
    public event Action? PlayersChanged;

    /// <summary>You picked this one: the notch and the card follow it until it closes.</summary>
    public void Select(Player p)
    {
        if (!_players.Contains(p)) return;
        _chosen = p;
        PickSession();
    }

    void SyncPlayers()
    {
        if (_mgr == null) return;
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try { sessions = _mgr.GetSessions(); } catch { return; }
        bool changed = false;
        for (int i = _players.Count - 1; i >= 0; i--)
        {
            var p = _players[i];
            if (sessions.Any(x => ReferenceEquals(x, p.Session))) continue;
            try { p.Session.PlaybackInfoChanged -= OnAnyPlayback; p.Session.MediaPropertiesChanged -= OnAnyProps; } catch { }
            _players.RemoveAt(i);
            if (ReferenceEquals(_chosen, p)) _chosen = null; // closed: forget the choice
            changed = true;
        }
        foreach (var x in sessions)
        {
            if (_players.Any(p => ReferenceEquals(p.Session, x))) continue;
            string id = "";
            try { id = x.SourceAppUserModelId ?? ""; } catch { }
            var p = new Player(x, ++_nextPlayerId, id);
            try { x.PlaybackInfoChanged += OnAnyPlayback; x.MediaPropertiesChanged += OnAnyProps; } catch { }
            p.Playing = IsSessionPlaying(x);
            _players.Add(p);
            _ = LoadPlayerAsync(p);
            changed = true;
        }
        if (!changed) return;
        AppLog.Write($"media players: {_players.Count} ({string.Join(", ", _players.Select(p => p.AppName))})");
        PlayersChanged?.Invoke();
    }

    static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession x)
    {
        try { return x.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    void OnAnyPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        _ui.InvokeAsync(() =>
        {
            var p = _players.FirstOrDefault(q => ReferenceEquals(q.Session, s));
            if (p == null) return;
            bool playing = IsSessionPlaying(s);
            if (playing == p.Playing) return;
            p.Playing = playing;
            PlayersChanged?.Invoke();
        });

    void OnAnyProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        _ui.InvokeAsync(() =>
        {
            var p = _players.FirstOrDefault(q => ReferenceEquals(q.Session, s));
            if (p != null) _ = LoadPlayerAsync(p);
        });

    async Task LoadPlayerAsync(Player p)
    {
        int version = ++p.Version;
        await Task.Delay(150);
        if (version != p.Version) return;
        GlobalSystemMediaTransportControlsSessionMediaProperties? props;
        try { props = await p.Session.TryGetMediaPropertiesAsync(); }
        catch { return; }
        if (props == null || version != p.Version) return;
        byte[]? bytes = null;
        try { bytes = await ReadAllAsync(props.Thumbnail); } catch { }
        ImageSource? art = bytes == null ? null : await Task.Run(() => DecodeSmall(bytes, 96));
        if (version != p.Version) return;
        p.Title = props.Title ?? "";
        p.Artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
        p.Art = art;
        PlayersChanged?.Invoke();
    }

    static ImageSource? DecodeSmall(byte[] data, int px)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = px;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public async Task InitAsync()
    {
        try { _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { return; }
        _mgr.CurrentSessionChanged += (_, _) => _ui.InvokeAsync(PickSession);
        _mgr.SessionsChanged += (_, _) => _ui.InvokeAsync(() => { SyncPlayers(); PickSession(); });
        SyncPlayers();
        PickSession();
        await Task.Delay(400);
        _initialized = true;
    }

    void PickSession()
    {
        if (_mgr == null) return;
        GlobalSystemMediaTransportControlsSession? s = null;
        try
        {
            if (_chosen != null && _players.Contains(_chosen)) s = _chosen.Session; // your pick wins while it's open
            else s = _mgr.GetCurrentSession();
            if (!ReferenceEquals(s, _chosen?.Session) &&
                (s == null || s.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing))
            {
                foreach (var x in _mgr.GetSessions())
                    if (x.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) { s = x; break; }
            }
        }
        catch { }

        string? id = null;
        try { id = s?.SourceAppUserModelId; } catch { }
        // several browser tabs share one app id, so the session itself is what counts
        if (s != null && ReferenceEquals(s, _session)) return;

        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnProps;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }

        _session = s;
        _sessionId = id;
        PlayersChanged?.Invoke(); // the one in front changed

        if (s == null)
        {
            Title = Artist = "";
            Art = null;
            IsPlaying = false;
            Duration = TimeSpan.Zero;
            Changed?.Invoke();
            return;
        }

        s.MediaPropertiesChanged += OnProps;
        s.PlaybackInfoChanged += OnPlayback;
        s.TimelinePropertiesChanged += OnTimeline;
        ReadPlayback();
        ReadTimeline();
        _ = LoadPropertiesAsync();
    }

    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        _ui.InvokeAsync(() => _ = LoadPropertiesAsync());

    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        _ui.InvokeAsync(() =>
        {
            bool was = IsPlaying;
            ReadPlayback();
            ReadTimeline();
            if (was != IsPlaying) Changed?.Invoke();
            // another app may have started playing; prefer it
            if (!IsPlaying) PickSession();
        });

    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        _ui.InvokeAsync(ReadTimeline);

    void ReadPlayback()
    {
        try { IsPlaying = _session?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { IsPlaying = false; }
    }

    void ReadTimeline()
    {
        try
        {
            var t = _session?.GetTimelineProperties();
            if (t == null) return;
            Duration = t.EndTime - t.StartTime;
            _pos = t.Position;
            _posAt = t.LastUpdatedTime.Year < 2000 ? DateTimeOffset.Now : t.LastUpdatedTime;
        }
        catch { }
    }

    async Task LoadPropertiesAsync()
    {
        var session = _session;
        if (session == null) return;
        int version = ++_version;

        await Task.Delay(120); // apps fire several property events in a row; settle first
        if (version != _version) return;

        GlobalSystemMediaTransportControlsSessionMediaProperties? props;
        try { props = await session.TryGetMediaPropertiesAsync(); }
        catch { return; }
        if (props == null || version != _version) return;

        string title = props.Title ?? "", artist = props.Artist ?? "";
        if (string.IsNullOrEmpty(artist)) artist = props.AlbumArtist ?? "";

        byte[]? bytes = null;
        try { bytes = await ReadAllAsync(props.Thumbnail); } catch { }
        var (art, accent, accent2) = bytes == null ? (null, Colors.White, Color.FromRgb(0x9A, 0xA4, 0xB8)) : await Task.Run(() => Decode(bytes));
        if (version != _version) return;

        bool newTrack = title != Title || artist != Artist;
        Title = title;
        Artist = artist;
        Art = art;
        Accent = accent;
        Accent2 = accent2;
        ReadPlayback();
        ReadTimeline();
        Changed?.Invoke();
        if (newTrack && _initialized && !string.IsNullOrEmpty(title)) TrackChanged?.Invoke();
    }

    static async Task<byte[]?> ReadAllAsync(IRandomAccessStreamReference? r)
    {
        if (r == null) return null;
        using var ras = await r.OpenReadAsync();
        using var s = ras.AsStreamForRead();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.Length > 0 ? ms.ToArray() : null;
    }

    static (ImageSource?, Color, Color) Decode(byte[] data)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 192; // sharp at 64 DIP on 300% displays
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            var accent = AccentFrom(data);
            return (bmp, accent, SecondFrom(data, accent));
        }
        catch { return (null, Colors.White, Color.FromRgb(0x9A, 0xA4, 0xB8)); }
    }

    /// <summary>Picks a vivid representative color from the cover for the visualizer.</summary>
    static Color AccentFrom(byte[] data)
    {
        var small = new BitmapImage();
        small.BeginInit();
        small.CacheOption = BitmapCacheOption.OnLoad;
        small.DecodePixelWidth = 16;
        small.StreamSource = new MemoryStream(data);
        small.EndInit();
        var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);

        double r = 0, g = 0, b = 0, total = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            double bb = px[i], gg = px[i + 1], rr = px[i + 2];
            double max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
            if (max < 30) continue;
            double sat = (max - min) / max;
            double wgt = sat * sat * (max / 255) + 0.01;
            r += rr * wgt; g += gg * wgt; b += bb * wgt; total += wgt;
        }
        if (total <= 0) return Colors.White;
        r /= total; g /= total; b /= total;

        // normalise to a bright, saturated tone so it reads well on black
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        if (mx <= 0 || (mx - mn) / mx < 0.18) return Colors.White;
        double k = 245 / mx;
        return Color.FromRgb((byte)Math.Min(255, r * k), (byte)Math.Min(255, g * k), (byte)Math.Min(255, b * k));
    }

    /// <summary>
    /// The cover's second colour: its pixels sorted into twelve hues (weighted by how vivid they are), the strongest
    /// hue at least 35° away from the first. A one-colour cover gets a neighbour of its own hue (an analogous pair);
    /// a colourless one, silver.
    /// </summary>
    static Color SecondFrom(byte[] data, Color main)
    {
        if (main == Colors.White) return Color.FromRgb(0x9A, 0xA4, 0xB8);
        try
        {
            var small = new BitmapImage();
            small.BeginInit();
            small.CacheOption = BitmapCacheOption.OnLoad;
            small.DecodePixelWidth = 16;
            small.StreamSource = new MemoryStream(data);
            small.EndInit();
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            var weight = new double[12];
            var sum = new double[12, 3];
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i], g = px[i + 1], r = px[i + 2];
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max < 40 || max - min < 25) continue;
                double hue = Hue(r, g, b);
                int bin = (int)(hue / 30) % 12;
                double wgt = Math.Pow((max - min) / max, 2) * (max / 255);
                weight[bin] += wgt;
                sum[bin, 0] += r * wgt; sum[bin, 1] += g * wgt; sum[bin, 2] += b * wgt;
            }
            double mainHue = Hue(main.R, main.G, main.B);
            int best = -1;
            for (int k = 0; k < 12; k++)
            {
                double centre = k * 30 + 15, d = Math.Abs(centre - mainHue);
                if (Math.Min(d, 360 - d) < 35 || weight[k] < 0.15) continue;
                if (best < 0 || weight[k] > weight[best]) best = k;
            }
            if (best >= 0)
            {
                double r = sum[best, 0] / weight[best], g = sum[best, 1] / weight[best], b = sum[best, 2] / weight[best];
                double mx = Math.Max(r, Math.Max(g, b));
                double k = 235 / Math.Max(1, mx);
                return Color.FromRgb((byte)Math.Min(255, r * k), (byte)Math.Min(255, g * k), (byte)Math.Min(255, b * k));
            }
        }
        catch { }
        // one colour: its neighbour on the wheel, a little deeper
        return Shift(main, 28, 0.85);
    }

    static double Hue(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d <= 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }

    static Color Shift(Color c, double degrees, double value)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double s = max <= 0 ? 0 : (max - min) / max, v = max * value;
        double h = (Hue(c.R, c.G, c.B) + degrees) % 360;
        double cc = v * s, x = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - cc;
        (double rr, double gg, double bb) = h switch
        {
            < 60 => (cc, x, 0.0), < 120 => (x, cc, 0.0), < 180 => (0.0, cc, x), < 240 => (0.0, x, cc), < 300 => (x, 0.0, cc), _ => (cc, 0.0, x),
        };
        return Color.FromRgb((byte)((rr + m) * 255), (byte)((gg + m) * 255), (byte)((bb + m) * 255));
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_session == null) return;
        IsPlaying = !IsPlaying; // optimistic: UI reacts instantly
        Changed?.Invoke();
        try { await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task NextAsync() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public async Task PreviousAsync() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }

    public async Task SeekAsync(TimeSpan to)
    {
        if (_session == null) return;
        _pos = to;
        _posAt = DateTimeOffset.Now;
        try { await _session.TryChangePlaybackPositionAsync(to.Ticks); } catch { }
    }
}
