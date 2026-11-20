using System.Runtime.InteropServices;

namespace DynamicNotch.Services;

/// <summary>
/// The face's little voice. Every feeling it shows has its own sound, said in tiny syllables with real vowel colour
/// (a voice-like tone shaped by two formants, the way a mouth shapes "a", "i", "o"):
///  hello "hi-ii!", happy "he-he-he", love a warm "awww", surprised "oh!", proud / done "ta-daa", annoyed "hmph",
///  tired a breathy sigh, wink "tsk", dizzy a wobbly "wooo", a yawn, a poke ("eep!", "oof", "bip"), thinking "hmm".
/// Each time it's said a little differently (pitch and pace vary), so it never sounds like a recording.
/// Neon says the same things as a small robot: square-ish, its grain crushed.
/// Made in code (no sound files), ahead of time on a background thread, and played straight to the sound card
/// through a device kept open (no lag), one at a time: a new one cuts the last.
/// </summary>
public static class FaceSound
{
    /// <summary>Off in the preferences ("Face sounds").</summary>
    public static bool Enabled { get; set; } = true;

    const int Rate = 22050, Variants = 3;
    static readonly Dictionary<(string, bool, int), short[]> Made = new();
    static readonly object Gate = new();
    static readonly Random Rand = new();
    static DateTime _last;

    record struct Syl(char V, double F0, double F1, double Ms, double Mid = 0, double Vib = 0, double VibHz = 6, double Breath = 0, double Gap = 0, double Vol = 1, char V2 = '\0');

    static Syl[] Words(string what, int variant) => what switch
    {
        "hello" or "wave" => new Syl[] { new('i', 560, 760, 85, Gap: 26), new('i', 760, 900, 175, Mid: 1040, Vib: .02, VibHz: 7) },
        "happy" => new Syl[] { new('e', 720, 740, 48, Gap: 22), new('e', 800, 820, 48, Gap: 22), new('e', 880, 980, 72) },
        "love" => new Syl[] { new('a', 560, 520, 430, Mid: 780, Vib: .035, VibHz: 6, V2: 'o') },
        "surprised" => new Syl[] { new('o', 420, 920, 135) },
        "proud" => new Syl[] { new('a', 520, 540, 80, Gap: 32), new('a', 700, 820, 230, Mid: 880, Vib: .02, V2: 'i') },
        "done" => new Syl[] { new('a', 640, 660, 70, Gap: 26), new('i', 960, 1120, 170, Vib: .015) },
        "annoyed" => new Syl[] { new('u', 300, 210, 150, Breath: .18, Gap: 10), new('m', 210, 190, 70, Vol: .7) },
        "tired" => new Syl[] { new('a', 400, 240, 380, Breath: .4) },
        "wink" => new Syl[] { new('i', 1000, 1320, 62) },
        "dizzy" => new Syl[] { new('o', 640, 300, 640, Vib: .09, VibHz: 8) },
        "yawn" => new Syl[] { new('a', 330, 260, 780, Mid: 560, Breath: .28, V2: 'o') },
        "poke" => (variant % 3) switch
        {
            0 => new Syl[] { new('i', 900, 1220, 70) },               // eep!
            1 => new Syl[] { new('o', 430, 320, 95, Breath: .1) },    // oof
            _ => new Syl[] { new('e', 760, 920, 58) },                // bip
        },
        "sad" => new Syl[] { new('o', 440, 300, 360, Vib: .03, VibHz: 5) },                                  // "aww…"
        "alert" => new Syl[] { new('a', 620, 940, 95) },                                                      // "ah!"
        "excited" => new Syl[] { new('i', 760, 780, 55, Gap: 26), new('i', 900, 920, 55, Gap: 26), new('i', 1060, 1180, 90) }, // "wee-ee-ee"
        "gulp" => new Syl[] { new('u', 300, 220, 70, Gap: 45), new('m', 280, 300, 110, Vol: .7) },            // "gulp. mm"
        "overload" => new Syl[] { new('o', 700, 260, 680, Vib: .11, VibHz: 9, Breath: .15) },                  // a sparking wobble
        "think" => new Syl[] { new('m', 260, 270, 320, Mid: 300, Vol: .6) },
        "sleep" => new Syl[] { new('m', 200, 170, 420, Breath: .3, Vol: .45) },
        _ => Array.Empty<Syl>(),
    };

    static readonly string[] All = { "hello", "happy", "love", "surprised", "proud", "done", "annoyed", "tired", "wink", "dizzy", "yawn", "poke", "think", "sleep", "sad", "alert", "excited", "gulp", "overload" };

    /// <summary>Makes every sound ahead of time (a fraction of a second, off the screen's thread), and opens the device.</summary>
    public static void Warm() => Task.Run(() =>
    {
        try
        {
            foreach (bool neon in new[] { false, true })
                foreach (var w in All)
                    for (int v = 0; v < Variants; v++) Get(w, neon, v);
        }
        catch { }
    });

    /// <summary>Says how it feels (a feeling's name: "happy", "love", "poke"...). Nothing for one it has no sound for.</summary>
    public static void Say(string what, bool neon)
    {
        if (!Enabled || (DateTime.UtcNow - _last).TotalMilliseconds < 70) return;
        _last = DateTime.UtcNow;
        int variant = Rand.Next(Variants);
        Task.Run(() =>
        {
            try
            {
                var pcm = Get(what.ToLowerInvariant() == "wave" ? "hello" : what.ToLowerInvariant(), neon, variant);
                if (pcm.Length > 0) Out.Play(pcm);
            }
            catch { }
        });
    }

    static short[] Get(string what, bool neon, int variant)
    {
        lock (Gate) if (Made.TryGetValue((what, neon, variant), out var have)) return have;
        var made = Make(Words(what, variant), neon, variant);
        lock (Gate) Made[(what, neon, variant)] = made;
        return made;
    }

    // the vowels: where a mouth puts its two main resonances (Hz)
    static (double F1, double F2) Formants(char v) => v switch
    {
        'a' => (800, 1250), 'e' => (520, 1900), 'i' => (320, 2500), 'o' => (500, 880), 'u' => (330, 780), _ => (260, 900), // 'm': a hum
    };

    static short[] Make(Syl[] syls, bool neon, int variant)
    {
        if (syls.Length == 0) return Array.Empty<short>();
        // each variant: a touch higher or lower, a touch quicker or slower
        double pitch = variant switch { 0 => 0.92, 1 => 0.98, _ => 0.87 }, pace = variant switch { 0 => 1.04, 1 => 0.97, _ => 1.1 }; // a touch lower and slower: warmer
        var buf = new List<float>();
        var noise = new Random(variant * 31 + 7);
        foreach (var s in syls)
        {
            int n = (int)(s.Ms * pace / 1000 * Rate);
            var (a1, a2) = Formants(s.V);
            var (b1, b2) = Formants(s.V2 == '\0' ? s.V : s.V2);
            double ph = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n;
                double f = s.Mid > 0
                    ? (t < 0.5 ? s.F0 * Math.Pow(s.Mid / s.F0, t / 0.5) : s.Mid * Math.Pow(s.F1 / s.Mid, (t - 0.5) / 0.5))
                    : s.F0 * Math.Pow(s.F1 / s.F0, t);
                f *= pitch * (1 + s.Vib * Math.Sin(2 * Math.PI * s.VibHz * i / Rate));
                ph += 2 * Math.PI * f / Rate;
                double F1 = a1 + (b1 - a1) * t, F2 = a2 + (b2 - a2) * t;
                double v = 0;
                for (int k = 1; k <= 14; k++)
                {
                    double hf = k * f;
                    if (hf > 5200) break;
                    if (neon && k % 2 == 0) continue; // the robot: odd harmonics only
                    double amp = Math.Pow(k, neon ? -0.75 : -1.1) * (Bump(hf, F1, 150) + 0.6 * Bump(hf, F2, 200) + 0.1); // the highs softened
                    v += amp * Math.Sin(k * ph);
                }
                if (s.Breath > 0) v += s.Breath * (noise.NextDouble() * 2 - 1) * 0.9;
                double env = Math.Pow(Math.Min(1, i / (Rate * 0.014)), 1.5) * (t < 0.62 ? 1 : Math.Pow((1 - t) / 0.38, 1.7)); // a soft start, a gentle fade
                buf.Add((float)(v * env * s.Vol));
            }
            for (int i = 0; i < (int)(s.Gap * pace / 1000 * Rate); i++) buf.Add(0);
        }
        // warmth: a gentle low-pass, then a little room (two soft echoes), then levelled; Neon keeps a fine grain
        int tail = Rate * 9 / 100;
        for (int i = 0; i < tail; i++) buf.Add(0);
        double lp = 0, k1 = neon ? 0.38 : 0.3;
        for (int i = 0; i < buf.Count; i++) { lp += (buf[i] - lp) * k1; buf[i] = (float)lp; }
        int d1 = Rate * 23 / 1000, d2 = Rate * 41 / 1000;
        for (int i = buf.Count - 1; i >= d1; i--) buf[i] += 0.16f * buf[i - d1] + (i >= d2 ? 0.09f * buf[i - d2] : 0);
        double peak = buf.Count == 0 ? 1 : buf.Max(x => Math.Abs(x));
        double vol = (neon ? 0.13 : 0.15) / Math.Max(0.001, peak);
        var pcm = new short[buf.Count + Rate / 50]; // a little silence at the end: no click
        for (int i = 0; i < buf.Count; i++)
        {
            double x = buf[i] * vol;
            if (neon) x = Math.Round(x * 96) / 96;
            pcm[i] = (short)Math.Clamp(x * short.MaxValue, short.MinValue, short.MaxValue);
        }
        return pcm;
    }

    static double Bump(double f, double centre, double width) => Math.Exp(-(f - centre) * (f - centre) / (2 * width * width));

    // ───────────────────────────── straight to the sound card ─────────────────────────────

    static class Out
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat { public ushort Tag, Channels; public uint Rate, BytesPerSec; public ushort Align, Bits, Extra; }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHeader { public IntPtr Data; public uint Length, Recorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        static readonly object Lock = new();
        static IntPtr _dev, _hdr, _data;
        static Timer? _close;
        static readonly uint HdrSize = (uint)Marshal.SizeOf<WaveHeader>();

        public static void Play(short[] pcm)
        {
            lock (Lock)
            {
                if (_dev == IntPtr.Zero)
                {
                    var fmt = new WaveFormat { Tag = 1, Channels = 1, Rate = Rate, BytesPerSec = Rate * 2, Align = 2, Bits = 16 };
                    if (waveOutOpen(out _dev, 0xFFFFFFFF, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) { _dev = IntPtr.Zero; return; }
                }
                Release(); // a new one cuts the last
                int bytes = pcm.Length * 2;
                _data = Marshal.AllocHGlobal(bytes);
                Marshal.Copy(pcm, 0, _data, pcm.Length);
                _hdr = Marshal.AllocHGlobal((int)HdrSize);
                Marshal.StructureToPtr(new WaveHeader { Data = _data, Length = (uint)bytes }, _hdr, false);
                if (waveOutPrepareHeader(_dev, _hdr, HdrSize) != 0 || waveOutWrite(_dev, _hdr, HdrSize) != 0)
                {
                    // the device went away (headphones unplugged...): open again next time
                    Release();
                    waveOutClose(_dev);
                    _dev = IntPtr.Zero;
                    return;
                }
                // quiet for a while: let the sound card go
                _close ??= new Timer(_ => { lock (Lock) { Release(); if (_dev != IntPtr.Zero) { waveOutClose(_dev); _dev = IntPtr.Zero; } } });
                _close.Change(20000, Timeout.Infinite);
            }
        }

        static void Release()
        {
            if (_hdr == IntPtr.Zero) return;
            waveOutReset(_dev);
            waveOutUnprepareHeader(_dev, _hdr, HdrSize);
            Marshal.FreeHGlobal(_hdr);
            Marshal.FreeHGlobal(_data);
            _hdr = _data = IntPtr.Zero;
        }
    }
}
