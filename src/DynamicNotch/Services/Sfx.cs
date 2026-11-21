using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DynamicNotch.Services;

/// <summary>
/// Pixo's little interface sounds: bright, short and soft, like glass and wood rather than speakers' bass.
///  pick a rising two-note pluck · on / off a blip up or down · copy a light double tick · pin a tiny sparkle ·
///  delete "nom-nom" (the face eats it) · clear an airy sweep and a ding · done a warm two-bell chime ·
///  oops a soft "bu-bum" down · nothing found a questioning "hm?" · a screenshot a shutter · the timer a bell run.
/// All made in code (no files), once, on a background thread. Every sound keeps above 250 Hz (no boom on laptop
/// speakers), has a soft attack (no click) and a short room behind it, and is levelled quietly: it accompanies,
/// never startles. Played through <see cref="SoundOut"/>, which mixes them with the face's voice.
/// </summary>
public static class Sfx
{
    public enum Cue { Tap, Pick, On, Off, Copy, Pin, Unpin, Eat, Clear, Done, Oops, Nothing, Snap, Timer, Love, Spin, Hush, Open }

    /// <summary>Off in the preferences ("Interface sounds"); quiet on its own during calls and presenting.</summary>
    public static bool Enabled { get; set; } = true;
    public static bool Hushed { get; set; }

    const int Rate = SoundOut.Rate;
    static readonly Dictionary<Cue, short[]> Made = new();
    static readonly object Gate = new();
    static readonly Dictionary<Cue, DateTime> Last = new();

    public static void Warm() => Task.Run(() =>
    {
        try { foreach (var c in Enum.GetValues<Cue>()) Get(c); } catch { }
    });

    /// <param name="always">An alarm (the timer): plays even with interface sounds off.</param>
    public static void Play(Cue cue, bool always = false)
    {
        if (!always && (!Enabled || Hushed)) return;
        lock (Gate)
        {
            // the same sound again within a moment (a key held, a double click): once is enough
            if (Last.TryGetValue(cue, out var at) && (DateTime.UtcNow - at).TotalMilliseconds < 90) return;
            Last[cue] = DateTime.UtcNow;
        }
        Task.Run(() => { try { var pcm = Get(cue); if (pcm.Length > 0) SoundOut.Play(pcm); } catch { } });
    }

    static short[] Get(Cue c)
    {
        lock (Gate) if (Made.TryGetValue(c, out var have)) return have;
        var made = SoundOut.Finish(Make(c), c is Cue.Tap or Cue.Copy ? 0.09 : c is Cue.Timer or Cue.Done ? 0.17 : 0.13);
        lock (Gate) Made[c] = made;
        return made;
    }

    // notes (Hz)
    const double C6 = 1046.5, D6 = 1174.7, E6 = 1318.5, G6 = 1568.0, A6 = 1760.0, B6 = 1975.5, C7 = 2093.0, E7 = 2637.0, G7 = 3136.0, A5 = 880.0, E5 = 659.3, G5 = 784.0;

    static float[] Make(Cue c)
    {
        var b = new Buf();
        switch (c)
        {
            case Cue.Tap: b.Pluck(0, 2350, .028, .5, bright: .2); break;
            case Cue.Pick: b.Pluck(0, E6, .11, .7); b.Pluck(.055, B6, .16, .85); break;
            case Cue.On: b.Glide(0, 880, 1480, .075, .7); b.Pluck(.05, A6, .09, .35); break;
            case Cue.Off: b.Glide(0, 1250, 760, .08, .6); break;
            case Cue.Copy: b.Pluck(0, 2600, .035, .55, bright: .3); b.Pluck(.05, 3100, .035, .45, bright: .3); break;
            case Cue.Pin: b.Bell(0, C7, .22, .4); b.Bell(.045, E7, .22, .35); b.Bell(.09, G7, .3, .32); break;
            case Cue.Unpin: b.Bell(0, G6, .14, .4); b.Bell(.05, D6, .18, .35); break;
            case Cue.Eat: b.Glide(0, 760, 380, .075, .8, round: true); b.Glide(.1, 640, 330, .07, .7, round: true); break; // nom-nom
            case Cue.Clear: b.Air(0, 1400, 5200, .24, .5); b.Bell(.2, G6, .5, .45); break;
            case Cue.Done: b.Bell(0, G6, .55, .7); b.Bell(.09, C7, .8, .75); break;
            case Cue.Oops: b.Pluck(0, A5, .13, .6, bright: .05); b.Pluck(.12, E5, .2, .55, bright: .05); break;
            case Cue.Nothing: b.Glide(0, 700, 980, .16, .55, round: true); break; // a questioning "hm?"
            case Cue.Snap: b.Air(0, 2500, 3500, .03, .9); b.Air(.06, 2200, 3200, .04, .7); break;
            case Cue.Timer: foreach (var (t, f) in new[] { (0.0, C6), (.14, E6), (.28, G6), (.42, C7) }) b.Bell(t, f, .9, .6); break;
            case Cue.Love: b.Bell(0, E6, .4, .45); b.Bell(.11, A6, .55, .5); break;
            case Cue.Spin: b.Glide(0, 600, 1900, .32, .45, round: true); b.Bell(.28, C7, .4, .35); break;
            case Cue.Hush: b.Air(0, 3500, 1800, .22, .35); break; // "shhh"
            case Cue.Open: b.Air(0, 900, 2600, .14, .3); break;
        }
        return b.Done();
    }

    /// <summary>A sound being made: notes are added at their time, then it's filtered and given a little room.</summary>
    sealed class Buf
    {
        float[] _s = new float[Rate * 2];
        int _len;
        static readonly Random Rand = new(7);

        void Add(int i, double v)
        {
            if (i < 0) return;
            if (i >= _s.Length) Array.Resize(ref _s, Math.Max(_s.Length * 2, i + Rate));
            _s[i] += (float)v;
            if (i + 1 > _len) _len = i + 1;
        }

        static double Attack(int i, double ms = 3) => Math.Min(1, i / (Rate * ms / 1000));

        /// <summary>A bell or glass: partials that ring out, the high ones first.</summary>
        public void Bell(double at, double f, double secs, double vol)
        {
            double[] ratio = { 1, 2.0, 3.0, 4.16, 5.43 }, amp = { 1, .32, .14, .07, .035 }, decay = { 1, .55, .38, .27, .2 };
            int start = (int)(at * Rate), n = (int)(secs * Rate * 1.6);
            for (int k = 0; k < ratio.Length; k++)
            {
                double fk = f * ratio[k];
                if (fk > 11000) break;
                double tau = secs * decay[k] / 3; // seconds to fall to a third
                for (int j = 0; j < 2; j++) // two voices a hair apart: a living shimmer instead of a beep
                {
                    double fd = fk * (j == 0 ? 0.9993 : 1.0007);
                    for (int i = 0; i < n; i++)
                    {
                        double t = (double)i / Rate;
                        Add(start + i, vol * .5 * amp[k] * Math.Sin(2 * Math.PI * fd * t) * Math.Exp(-t / tau) * Attack(i, 2.5));
                    }
                }
            }
        }

        /// <summary>A plucked note: wood and glass, a quick fall (bright: a touch of its octave).</summary>
        public void Pluck(double at, double f, double secs, double vol, double bright = .12)
        {
            int start = (int)(at * Rate), n = (int)(secs * Rate * 2.2);
            double tau = secs / 3;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                double v = Math.Sin(2 * Math.PI * f * t) + bright * Math.Sin(4 * Math.PI * f * t) * Math.Exp(-t / (tau * .4)) + .08 * Math.Sin(6 * Math.PI * f * t) * Math.Exp(-t / (tau * .25));
                Add(start + i, vol * .55 * v * Math.Exp(-t / tau) * Attack(i, 2));
            }
        }

        /// <summary>A sliding tone ("bloop", "blip"): round = a softer, rounder vowel.</summary>
        public void Glide(double at, double from, double to, double secs, double vol, bool round = false)
        {
            int start = (int)(at * Rate), n = (int)(secs * Rate), tail = (int)(.03 * Rate);
            double ph = 0;
            for (int i = 0; i < n + tail; i++)
            {
                double t = Math.Min(1, (double)i / n);
                double f = from * Math.Pow(to / from, Ease(t));
                ph += 2 * Math.PI * f / Rate;
                double env = Attack(i, 4) * (i < n ? 1 - .35 * t : (1 - .35) * (1 - (double)(i - n) / tail));
                double v = Math.Sin(ph) + (round ? .18 : .3) * Math.Sin(2 * ph) + (round ? .03 : .08) * Math.Sin(3 * ph);
                Add(start + i, vol * .5 * v * env);
            }
            static double Ease(double t) => 1 - Math.Pow(1 - t, 2);
        }

        /// <summary>Air: soft noise through a sweeping band (a whoosh, a "shh", a shutter).</summary>
        public void Air(double at, double from, double to, double secs, double vol)
        {
            int start = (int)(at * Rate), n = (int)(secs * Rate);
            double y1 = 0, y2 = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n;
                double f = from * Math.Pow(to / from, t), q = 2.2;
                // a band-pass (two-pole resonator) following the sweep
                double w = 2 * Math.PI * f / Rate, r = Math.Exp(-w / (2 * q));
                double x = Rand.NextDouble() * 2 - 1;
                double y = (1 - r) * x + 2 * r * Math.Cos(w) * y1 - r * r * y2;
                y2 = y1; y1 = y;
                double env = Math.Sin(Math.PI * Math.Min(1, t * 1.15)) * Attack(i, 4);
                Add(start + i, vol * 1.6 * y * Math.Max(0, env));
            }
        }

        public float[] Done()
        {
            var o = new float[_len];
            Array.Copy(_s, o, _len);
            return o;
        }
    }
}

/// <summary>
/// The one way Pixo's own sounds reach the speakers: a device kept open while sounds play (no lag on the next one),
/// let go after a quiet while. A sound that starts while another is still ringing is mixed into what's left of it,
/// so the face's voice and a click play together instead of cutting each other off.
/// </summary>
public static class SoundOut
{
    public const int Rate = 48000;

    /// <summary>Finishing touches for any sound: no rumble below ~250 Hz, a small room, levelled to a quiet peak.</summary>
    public static short[] Finish(float[] s, double peak)
    {
        if (s.Length == 0) return Array.Empty<short>();
        int tailN = Rate * 12 / 100;
        var buf = new float[s.Length + tailN];
        Array.Copy(s, buf, s.Length);
        // high-pass (one pole, ~250 Hz), twice: laptop speakers can't play lower, they only buzz
        for (int pass = 0; pass < 2; pass++)
        {
            double a = Math.Exp(-2 * Math.PI * 250 / Rate), prevX = 0, prevY = 0;
            for (int i = 0; i < buf.Length; i++) { double y = a * (prevY + buf[i] - prevX); prevX = buf[i]; prevY = y; buf[i] = (float)y; }
        }
        // a gentle top: the harshest air above ~12 kHz softened
        double lp = 0, k = 1 - Math.Exp(-2 * Math.PI * 12000.0 / Rate);
        for (int i = 0; i < buf.Length; i++) { lp += (buf[i] - lp) * k; buf[i] = (float)lp; }
        // a little room: a few soft early reflections, darker as they go
        int[] taps = { Rate * 19 / 1000, Rate * 31 / 1000, Rate * 47 / 1000, Rate * 67 / 1000 };
        double[] gains = { .14, .1, .07, .045 };
        var dry = (float[])buf.Clone();
        for (int t = 0; t < taps.Length; t++)
            for (int i = buf.Length - 1; i >= taps[t]; i--) buf[i] += (float)(gains[t] * dry[i - taps[t]]);
        double max = 0;
        foreach (var x in buf) max = Math.Max(max, Math.Abs(x));
        double vol = peak / Math.Max(1e-6, max);
        var pcm = new short[buf.Length + Rate / 100];
        for (int i = 0; i < buf.Length; i++)
        {
            double x = buf[i] * vol;
            x = Math.Tanh(x * 1.2) / 1.2; // never a hard edge
            double fade = i > buf.Length - Rate / 50 ? (buf.Length - i) / (double)(Rate / 50) : 1; // the room fades out, no click
            pcm[i] = (short)Math.Clamp(x * fade * short.MaxValue, short.MinValue, short.MaxValue);
        }
        return pcm;
    }

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
    static short[]? _cur;
    static long _curAt;
    static readonly uint HdrSize = (uint)Marshal.SizeOf<WaveHeader>();

    public static void Play(short[] pcm)
    {
        lock (Lock)
        {
            if (_dev == IntPtr.Zero)
            {
                var fmt = new WaveFormat { Tag = 1, Channels = 1, Rate = Rate, BytesPerSec = Rate * 2, Align = 2, Bits = 16 };
                if (waveOutOpen(out _dev, 0xFFFFFFFF, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) { _dev = IntPtr.Zero; return; }
                _cur = null;
            }
            // still ringing: the new one is mixed into what's left
            if (_cur != null)
            {
                long played = (Stopwatch.GetTimestamp() - _curAt) * Rate / Stopwatch.Frequency;
                int from = (int)Math.Clamp(played, 0, _cur.Length);
                int rest = _cur.Length - from;
                if (rest > Rate / 100)
                {
                    var mixed = new short[Math.Max(rest, pcm.Length)];
                    for (int i = 0; i < mixed.Length; i++)
                    {
                        int v = (i < rest ? _cur[from + i] : 0) + (i < pcm.Length ? pcm[i] : 0);
                        mixed[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                    }
                    pcm = mixed;
                }
            }
            Release();
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
                _cur = null;
                return;
            }
            _cur = pcm;
            _curAt = Stopwatch.GetTimestamp();
            // quiet for a while: let the sound card go
            _close ??= new Timer(_ => { lock (Lock) { Release(); _cur = null; if (_dev != IntPtr.Zero) { waveOutClose(_dev); _dev = IntPtr.Zero; } } });
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
