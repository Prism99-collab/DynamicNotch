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

    public async Task InitAsync()
    {
        try { _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { return; }
        _mgr.CurrentSessionChanged += (_, _) => _ui.InvokeAsync(PickSession);
        _mgr.SessionsChanged += (_, _) => _ui.InvokeAsync(PickSession);
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
            s = _mgr.GetCurrentSession();
            if (s == null || s.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                foreach (var x in _mgr.GetSessions())
                    if (x.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) { s = x; break; }
            }
        }
        catch { }

        string? id = null;
        try { id = s?.SourceAppUserModelId; } catch { }
        if (s != null && id == _sessionId && _session != null) return;

        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnProps;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }

        _session = s;
        _sessionId = id;

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
        var (art, accent) = bytes == null ? (null, Colors.White) : await Task.Run(() => Decode(bytes));
        if (version != _version) return;

        bool newTrack = title != Title || artist != Artist;
        Title = title;
        Artist = artist;
        Art = art;
        Accent = accent;
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

    static (ImageSource?, Color) Decode(byte[] data)
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
            return (bmp, AccentFrom(data));
        }
        catch { return (null, Colors.White); }
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
