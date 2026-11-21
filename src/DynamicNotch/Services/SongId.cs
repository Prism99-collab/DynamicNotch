using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DynamicNotch.Services;

/// <summary>A song that was recognised.</summary>
public sealed record SongMatch(string Title, string Artist, string Url, string? CoverUrl, string? Album);

/// <summary>
/// "What's this song?": listens to what the PC is playing (or, when the PC is silent, to the room through the mic),
/// makes an audio fingerprint of it and asks Shazam's recognition service. No key, no account.
/// The fingerprint is Shazam's own format (spectrogram peaks in four frequency bands), ported from the open-source
/// shazam-for-real (ISC licence, Aleksey Martynov) and SongRec (Marin M.). It asks after 3 seconds, then again when the
/// service says to (it may want more), up to about 12 seconds of sound.
/// </summary>
public static class SongId
{
    public enum Source { Pc, Mic }

    public sealed class Progress
    {
        public Source Source;
        public double Seconds;
        public double Level; // 0..1, how loud what it hears is (for the rings)
    }

    /// <summary>Not found (null), or the song. Throws only for a missing connection or microphone (the message says which).</summary>
    public static async Task<SongMatch?> IdentifyAsync(Action<Progress> progress, CancellationToken ct)
    {
        string tagId = Guid.NewGuid().ToString();
        var listen = new Listener();
        try
        {
            // the PC first: if nothing comes out of the speakers for a moment, the room (the mic)
            // opening a sound device can take a moment (a Bluetooth mic especially): never on the screen's thread
            var pc = listen;
            await Task.Run(() => pc.Start(Source.Pc), ct);
            var startedAt = DateTime.UtcNow;
            while (true)
            {
                await Task.Delay(60, ct);
                if (listen.Peak > 0.004) break; // sound from the PC (it arrives within a few milliseconds when something plays)
                if ((DateTime.UtcNow - startedAt).TotalSeconds >= 0.8)
                {
                    var old = listen;
                    _ = Task.Run(old.Stop);
                    listen = new Listener();
                    var mic = listen;
                    await Task.Run(() => mic.Start(Source.Mic), ct);
                    break;
                }
            }

            double askAt = 3.2, limit = 13;
            var began = DateTime.UtcNow;
            while (true)
            {
                await Task.Delay(100, ct);
                if (listen.Error is { } err) throw new InvalidOperationException(err);
                double secs = listen.Seconds;
                progress(new Progress { Source = listen.Kind, Seconds = secs, Level = listen.RecentLevel });
                // the PC's sound only arrives while something plays: a long silence ends the wait
                bool stuck = (DateTime.UtcNow - began).TotalSeconds > limit + 4;
                if (secs < askAt && !stuck) continue;
                // the sums happen off the screen's thread: the notch keeps moving while it thinks
                var l = listen;
                var (pcm, sig) = await Task.Run(() =>
                {
                    var p = Level(l.Take16k());
                    return (p, p.Length < Analysis.SampleRate ? Array.Empty<byte>() : Signature.Make(p));
                }, ct);
                if (pcm.Length < Analysis.SampleRate) { if (stuck) return null; askAt = secs + 1; continue; } // barely anything heard yet
                var (match, retryMs) = await Ask(tagId, pcm.Length * 1000 / Analysis.SampleRate, sig, ct);
                if (match != null) return match;
                if (stuck || retryMs <= 0 || secs >= limit) return null;
                askAt = Math.Min(limit, Math.Max(secs + 1, retryMs / 1000.0));
            }
        }
        finally { var last = listen; _ = Task.Run(last.Stop); }
    }

    // ───────────────────────────── listening ─────────────────────────────

    sealed class Listener
    {
        IWaveIn? _cap;
        MMDevice? _dev;
        readonly List<float> _mono = new();
        readonly object _gate = new();
        int _rate = 48000;
        float _peak, _recent;
        public Source Kind;
        public string? Error;

        public double Seconds { get { lock (_gate) return _mono.Count / (double)_rate; } }
        public float Peak { get { lock (_gate) return _peak; } }
        public double RecentLevel { get { lock (_gate) { var r = _recent; _recent *= 0.6f; return Math.Min(1, r * 4); } } }

        public void Start(Source kind)
        {
            Kind = kind;
            try
            {
                if (kind == Source.Pc) _cap = new WasapiLoopbackCapture();
                else
                {
                    using var en = new MMDeviceEnumerator();
                    _dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                    _cap = new WasapiCapture(_dev, true, 50);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(kind == Source.Mic ? "No microphone to listen with" : "Can't hear the PC's sound", ex);
            }
            var fmt = _cap.WaveFormat;
            _rate = fmt.SampleRate;
            int ch = Math.Max(1, fmt.Channels);
            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat || fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32; // shared mode mixes in float
            int bits = fmt.BitsPerSample;
            _cap.DataAvailable += (_, e) =>
            {
                int bytesPer = bits / 8, frame = bytesPer * ch;
                if (frame <= 0) return;
                int frames = e.BytesRecorded / frame;
                lock (_gate)
                {
                    if (_mono.Count > _rate * 14) return; // enough
                    for (int i = 0; i < frames; i++)
                    {
                        float sum = 0;
                        for (int c = 0; c < ch; c++)
                        {
                            int at = i * frame + c * bytesPer;
                            sum += isFloat && bits == 32 ? BitConverter.ToSingle(e.Buffer, at)
                                : bits == 16 ? BitConverter.ToInt16(e.Buffer, at) / 32768f
                                : bits == 24 ? ((e.Buffer[at] << 8 | e.Buffer[at + 1] << 16 | e.Buffer[at + 2] << 24) >> 8) / 8388608f
                                : bits == 32 ? BitConverter.ToInt32(e.Buffer, at) / 2147483648f : 0;
                        }
                        float v = sum / ch;
                        if (float.IsNaN(v)) v = 0;
                        _mono.Add(v);
                        float a = Math.Abs(v);
                        if (a > _peak) _peak = a;
                        if (a > _recent) _recent = a;
                    }
                }
            };
            _cap.RecordingStopped += (_, e) => { if (e.Exception != null) Error = Kind == Source.Mic ? "The microphone couldn't be used" : "The PC's sound couldn't be heard"; };
            try { _cap.StartRecording(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(kind == Source.Mic ? "The microphone couldn't be used (Windows privacy settings?)" : "Can't hear the PC's sound", ex);
            }
        }

        /// <summary>Everything heard so far, as 16 kHz mono.</summary>
        public float[] Take16k()
        {
            float[] src;
            int rate;
            lock (_gate) { src = _mono.ToArray(); rate = _rate; }
            return Resample(src, rate, Analysis.SampleRate);
        }

        public void Stop()
        {
            try { _cap?.StopRecording(); } catch { }
            try { _cap?.Dispose(); } catch { }
            try { _dev?.Dispose(); } catch { }
            _cap = null;
            _dev = null;
        }
    }

    /// <summary>
    /// Quiet sound (a song far from the mic, a player turned down) is brought up to a healthy level, so its
    /// frequency peaks clear the fingerprint's floor. Loud sound is left as it is.
    /// </summary>
    static float[] Level(float[] pcm)
    {
        float peak = 0;
        foreach (var v in pcm) { float a = Math.Abs(v); if (a > peak) peak = a; }
        if (peak < 1e-5f || peak >= 0.3f) return pcm;
        float gain = Math.Min(0.6f / peak, 40f);
        for (int i = 0; i < pcm.Length; i++) pcm[i] *= gain;
        return pcm;
    }

    /// <summary>
    /// Windowed-sinc resampling (a clean low-pass under the new rate's limit, no aliasing), with the filter worked out
    /// once into a table: a dozen seconds of sound take a few milliseconds.
    /// </summary>
    public static float[] Resample(float[] src, int from, int to)
    {
        if (from == to) return src;
        int n = (int)((long)src.Length * to / from);
        var dst = new float[n];
        double step = (double)from / to;
        double cutoff = Math.Min(1.0, (double)to / from) * 0.92; // relative to the source's Nyquist
        int half = (int)Math.Ceiling(12 / cutoff), taps = 2 * half;
        const int Phases = 256;
        var table = new float[(Phases + 1) * taps];
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases, sum = 0;
            for (int j = 0; j < taps; j++)
            {
                double d = frac - (j - half + 1);
                double x = Math.PI * d * cutoff;
                double sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(x) / x;
                double win = Math.Abs(d) >= half ? 0 : 0.5 + 0.5 * Math.Cos(Math.PI * d / half); // Hann
                double w = sinc * win;
                table[p * taps + j] = (float)w;
                sum += w;
            }
            if (Math.Abs(sum) > 1e-9) for (int j = 0; j < taps; j++) table[p * taps + j] = (float)(table[p * taps + j] / sum);
        }
        for (int i = 0; i < n; i++)
        {
            double t = i * step;
            int c = (int)t;
            int row = (int)Math.Round((t - c) * Phases) * taps;
            int k0 = c - half + 1;
            float acc = 0;
            if (k0 >= 0 && k0 + taps <= src.Length)
                for (int j = 0; j < taps; j++) acc += src[k0 + j] * table[row + j];
            else
                for (int j = 0; j < taps; j++) { int k = k0 + j; if (k >= 0 && k < src.Length) acc += src[k] * table[row + j]; }
            dst[i] = acc;
        }
        return dst;
    }

    // ───────────────────────────── the fingerprint ─────────────────────────────

    /// <summary>The spectrogram: 2048-sample Hann windows every 128 samples (125 a second) at 16 kHz.</summary>
    public sealed class Analysis
    {
        public const int SampleRate = 16000, ChunkSize = 128, WindowSize = ChunkSize * 16, BinCount = WindowSize / 2 + 1;
        static readonly float[] Hann = Enumerable.Range(0, WindowSize).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (WindowSize - 1)))).ToArray();
        public readonly List<float[]> Stripes = new();
        public int ProcessedSamples;

        public Analysis(float[] pcm)
        {
            var re = new double[WindowSize];
            var im = new double[WindowSize];
            for (int p = ChunkSize; p <= pcm.Length; p += ChunkSize)
            {
                ProcessedSamples = p;
                if (p < WindowSize) continue;
                int start = p - WindowSize;
                for (int i = 0; i < WindowSize; i++) { re[i] = pcm[start + i] * Hann[i]; im[i] = 0; }
                Fft(re, im);
                var stripe = new float[BinCount];
                for (int b = 0; b < BinCount; b++) stripe[b] = (float)(2 * (re[b] * re[b] + im[b] * im[b]));
                Stripes.Add(stripe);
            }
        }

        public static float BinToFreq(float bin) => bin * SampleRate / WindowSize;
        public static int FreqToBin(float freq) => (int)Math.Round(freq * WindowSize / SampleRate, MidpointRounding.ToEven);

        /// <summary>In-place radix-2 FFT (forward, unscaled).</summary>
        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }

    record struct Peak(int Stripe, float Bin, float LogMagnitude);

    static class Peaks
    {
        const int HStripe = 45, HBin = 1, VStripe = 3, VBin = 10;
        static readonly int[] Bands = { 250, 520, 1450, 3500, 5500 };
        static readonly int MinBin = Math.Max(Analysis.FreqToBin(Bands[0]), VBin), MaxBin = Math.Min(Analysis.FreqToBin(Bands[^1]), Analysis.BinCount - VBin);
        const float MinMag = 1f / 512 / 512;
        static readonly float LogMinMag = MathF.Log(MinMag);

        public static List<Peak>[] Find(Analysis a)
        {
            var bands = new List<Peak>[Bands.Length - 1];
            for (int i = 0; i < bands.Length; i++) bands[i] = new();
            var s = a.Stripes;
            // the same stripes the streaming version looks at: each once 45 newer ones exist
            for (int count = 2 * HStripe + 1; count <= s.Count; count++)
            {
                int stripe = count - HStripe - 1;
                for (int bin = MinBin; bin < MaxBin; bin++)
                {
                    float c = s[stripe][bin];
                    if (c < MinMag || !IsPeak(s, stripe, bin, HStripe, HBin) || !IsPeak(s, stripe, bin, VStripe, VBin)) continue;
                    float al = Log(s[stripe][bin - 1]), be = Log(c), ga = Log(s[stripe][bin + 1]);
                    float p = (al - ga) / (al - 2 * be + ga) / 2;
                    float interp = bin + p;
                    float freq = Analysis.BinToFreq(interp);
                    int band = -1;
                    if (freq >= Bands[0]) for (int i = 1; i < Bands.Length; i++) if (freq < Bands[i]) { band = i - 1; break; }
                    if (band >= 0) bands[band].Add(new Peak(stripe, interp, be));
                }
            }
            // as many as Shazam's own app keeps: the loudest
            int allowed = 12 + s.Count / 12;
            for (int i = 0; i < bands.Length; i++)
            {
                if (bands[i].Count <= allowed) continue;
                var kept = bands[i].OrderByDescending(x => x.LogMagnitude).Take(allowed).OrderBy(x => x.Stripe).ToList();
                bands[i] = kept;
            }
            return bands;
        }

        static float Log(float m) => 18 * 1024 * (1 - MathF.Log(m) / LogMinMag);

        static bool IsPeak(List<float[]> s, int stripe, int bin, int sd, int bd)
        {
            float c = s[stripe][bin];
            for (int i = -sd; i <= sd; i++)
                for (int b = -bd; b <= bd; b++)
                {
                    if (i == 0 && b == 0) continue;
                    if (s[stripe + i][bin + b] >= c) return false;
                }
            return true;
        }
    }

    public static class Signature
    {
        public static byte[] Make(float[] pcm16k)
        {
            var a = new Analysis(pcm16k);
            var bands = Peaks.Find(a);
            using var mem = new MemoryStream();
            using var w = new BinaryWriter(mem);
            w.Write(0xCAFE2580);
            w.Write(-1);
            w.Write(-1);
            w.Write(VersionCode());
            w.Write(0); w.Write(0); w.Write(0);
            w.Write(3 << 27); // 16 kHz
            w.Write(0); w.Write(0);
            w.Write(a.ProcessedSamples);
            w.Write(0x007C0000);
            w.Write(0x40000000);
            w.Write(-1);
            for (int i = 0; i < bands.Length; i++)
            {
                var data = BandData(bands[i]);
                w.Write(0x60030040 + i);
                w.Write(data.Length);
                w.Write(data);
            }
            w.Flush();
            int total = (int)mem.Length, content = total - 48;
            foreach (int i in new[] { 2, 13 }) { mem.Position = i * 4; w.Write(content); }
            w.Flush();
            uint crc = Crc32(mem.GetBuffer(), 8, total - 8);
            mem.Position = 4;
            w.Write(crc);
            w.Flush();
            return mem.ToArray();
        }

        static byte[] BandData(List<Peak> peaks)
        {
            using var mem = new MemoryStream();
            using var w = new BinaryWriter(mem);
            int at = 0;
            foreach (var p in peaks)
            {
                if (p.Stripe - at >= 100) { at = p.Stripe; w.Write((byte)255); w.Write(at); }
                w.Write((byte)(p.Stripe - at));
                w.Write((ushort)Math.Clamp(Math.Round(p.LogMagnitude, MidpointRounding.ToEven), 0, ushort.MaxValue));
                w.Write((ushort)Math.Clamp(Math.Round(64 * p.Bin, MidpointRounding.ToEven), 0, ushort.MaxValue));
                at = p.Stripe;
            }
            w.Flush();
            while (mem.Length % 4 != 0) w.Write((byte)0);
            w.Flush();
            return mem.ToArray();
        }

        static uint VersionCode()
        {
            int r = (10 << 25) + (1 << 20) + (3 << 15) + (7 << 10);
            return (uint)r ^ 0x80000000;
        }

        static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        static uint Crc32(byte[] buf, int offset, int count)
        {
            uint c = 0xFFFFFFFF;
            for (int i = offset; i < offset + count; i++) c = CrcTable[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }
    }

    // ───────────────────────────── asking ─────────────────────────────

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static string? _installId;

    /// <summary>A random id for this PC (the service wants one), kept so it stays the same.</summary>
    static string InstallId()
    {
        if (_installId != null) return _installId;
        string file = Path.Combine(Settings.Folder, "song-id");
        try { var s = File.ReadAllText(file).Trim(); if (Guid.TryParse(s, out _)) return _installId = s; } catch { }
        _installId = Guid.NewGuid().ToString();
        try { File.WriteAllText(file, _installId); } catch { }
        return _installId;
    }

    /// <summary>The raw answer of the last question (for checks).</summary>
    public static string LastJson { get; private set; } = "";

    public static async Task<(SongMatch? Match, int RetryMs)> Ask(string tagId, int sampleMs, byte[] sig, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new
        {
            signatures = new[] { new { uri = "data:audio/vnd.shazam.sig;base64," + Convert.ToBase64String(sig), samplems = sampleMs } },
            timezone = "GMT",
        });
        const string country = "US"; // only the links depend on it; this one always answers
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://amp.shazam.com/match/v1/en/{country}/android/{InstallId()}/{tagId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", "curl/7");
        HttpResponseMessage res;
        try { res = await Http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new InvalidOperationException("No internet connection", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("The song service didn't answer", ex); }
        using (res)
        {
            string json = await res.Content.ReadAsStringAsync(ct);
            LastJson = json;
            if (!res.IsSuccessStatusCode)
            {
                AppLog.Write($"song id: {(int)res.StatusCode}");
                throw new InvalidOperationException("The song service didn't answer");
            }
            return Parse(json);
        }
    }

    public static (SongMatch? Match, int RetryMs) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("results", out var results)) return (null, 0);
        string? id = null;
        if (results.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array && matches.GetArrayLength() > 0
            && matches[0].TryGetProperty("id", out var idEl))
            id = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.ToString();
        if (string.IsNullOrEmpty(id))
        {
            int retry = results.TryGetProperty("retry", out var r) && r.TryGetProperty("retryInMilliseconds", out var ms) && ms.TryGetInt32(out int v) ? v : 0;
            return (null, retry);
        }
        string title = "", artist = "", url = "https://www.shazam.com/track/" + id;
        string? cover = null, album = null;
        if (root.TryGetProperty("resources", out var resources) && resources.TryGetProperty("shazam-songs", out var songs) && songs.TryGetProperty(id, out var song)
            && song.TryGetProperty("attributes", out var at))
        {
            title = Str(at, "title") ?? "";
            artist = Str(at, "artist") ?? "";
            if (at.TryGetProperty("images", out var img)) cover = Str(img, "coverArtHq") ?? Str(img, "coverArt");
            // the song's own page (not the e-mail snippet, not the one with the match's timing in it)
            if (at.TryGetProperty("share", out var share) && Str(share, "href") is { Length: > 0 } href) url = href;
            else if (Str(at, "webUrl") is { Length: > 0 } web) url = web.Split('?')[0];
            // the album, from the Apple Music part when it's there
            if (resources.TryGetProperty("albums", out var albums) && albums.ValueKind == JsonValueKind.Object)
                foreach (var al in albums.EnumerateObject())
                    if (al.Value.TryGetProperty("attributes", out var aa) && Str(aa, "name") is { Length: > 0 } name) { album = name; break; }
        }
        if (title.Length == 0) return (null, 0);
        return (new SongMatch(title, artist, url, cover, album), 0);

        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
