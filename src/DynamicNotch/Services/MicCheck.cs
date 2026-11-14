using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DynamicNotch.Services;

/// <summary>
/// "Do I sound right?" before a call, two ways:
///  • Live: your microphone straight into your headset, as you speak (a few hundredths of a second behind), until you
///    stop it (or after 20 seconds);
///  • Replay: you speak, and as soon as you pause it plays back what it heard (at most five seconds): how the others
///    will hear you, and safe on speakers (no echo).
/// Either way it reports how loud you are as you go, and a verdict at the end. Nothing is saved or sent anywhere; the
/// audio lives in memory for those few seconds only.
/// </summary>
public static class MicCheck
{
    /// <summary>What the test heard: the share of the time you spoke, how loud, how often it clipped.</summary>
    public sealed record Verdict(bool Heard, float Loudness, float Clipping);

    /// <param name="level">How loud, 0–1, as you speak and as it plays back (called often, on a background thread).</param>
    /// <param name="phase">"live", "listen", "play", "done" or "error" (on a background thread).</param>
    public static Task<Verdict?> RunAsync(bool live, Action<float> level, Action<string> phase, CancellationToken stop) => Task.Run(async () =>
    {
        var stats = new Stats();
        try
        {
            if (live) await LiveAsync(level, phase, stats, stop);
            else await ReplayAsync(level, phase, stats, stop);
            phase("done");
            return stats.Result();
        }
        catch (Exception ex)
        {
            AppLog.Write("mic check: " + ex.Message);
            phase("error");
            return null;
        }
    });

    /// <summary>Mic → headset, with as little delay as Windows allows.</summary>
    static async Task LiveAsync(Action<float> level, Action<string> phase, Stats stats, CancellationToken stop)
    {
        using var enumerator = new MMDeviceEnumerator();
        var mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        var speaker = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var capture = new WasapiCapture(mic, true, 10);
        var fmt = capture.WaveFormat;
        // a short buffer: anything older than ~80 ms is dropped, so the delay can never build up
        var buffer = new BufferedWaveProvider(fmt) { BufferDuration = TimeSpan.FromMilliseconds(80), DiscardOnBufferOverflow = true, ReadFully = true };
        capture.DataAvailable += (_, e) =>
        {
            buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            float p = Peak(e.Buffer, 0, e.BytesRecorded, fmt);
            stats.Add(p);
            level(p);
        };
        using var output = new WasapiOut(speaker, AudioClientShareMode.Shared, true, 20);
        output.Init(ToDevice(buffer, speaker));
        capture.StartRecording();
        output.Play();
        phase("live");
        try { await Task.Delay(TimeSpan.FromSeconds(20), stop); } catch (TaskCanceledException) { }
        capture.StopRecording();
        output.Stop();
    }

    /// <summary>Speak; the moment you pause (or after five seconds), you hear it back.</summary>
    static async Task ReplayAsync(Action<float> level, Action<string> phase, Stats stats, CancellationToken stop)
    {
        using var capture = new WasapiCapture(new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications), true, 20);
        var fmt = capture.WaveFormat;
        var recorded = new MemoryStream();
        var started = DateTime.UtcNow;
        DateTime? spoke = null, quietSince = null;
        var done = new TaskCompletionSource();
        capture.DataAvailable += (_, e) =>
        {
            recorded.Write(e.Buffer, 0, e.BytesRecorded);
            float p = Peak(e.Buffer, 0, e.BytesRecorded, fmt);
            stats.Add(p);
            level(p);
            var now = DateTime.UtcNow;
            if (p > 0.03f) { spoke ??= now; quietSince = null; }
            else if (spoke != null) quietSince ??= now;
            // you've said something and paused for a beat: play it now; or it's been five seconds
            if (spoke != null && quietSince != null && (now - quietSince.Value).TotalMilliseconds > 550 && (now - spoke.Value).TotalMilliseconds > 500
                || (now - started).TotalSeconds >= 5)
                done.TrySetResult();
        };
        var stopped = new TaskCompletionSource();
        capture.RecordingStopped += (_, _) => stopped.TrySetResult();
        phase("listen");
        capture.StartRecording();
        await Task.WhenAny(done.Task, Task.Delay(Timeout.Infinite, stop).ContinueWith(_ => { }));
        capture.StopRecording();
        await Task.WhenAny(stopped.Task, Task.Delay(500));
        if (stop.IsCancellationRequested || spoke == null) return;

        // play it back from just before you started speaking, with its loudness as it goes
        var bytes = recorded.ToArray();
        int from = (int)Math.Max(0, ((spoke.Value - started).TotalSeconds - 0.15) * fmt.AverageBytesPerSecond);
        from -= from % fmt.BlockAlign;
        using var source = new RawSourceWaveStream(new MemoryStream(bytes, from, bytes.Length - from), fmt);
        using var output = new WasapiOut(AudioClientShareMode.Shared, true, 40);
        output.Init(source);
        phase("play");
        output.Play();
        int block = Math.Max(fmt.AverageBytesPerSecond / 25, fmt.BlockAlign);
        while (output.PlaybackState == PlaybackState.Playing && !stop.IsCancellationRequested)
        {
            long at = from + Math.Clamp(source.Position, 0, Math.Max(0, bytes.Length - from - block));
            level(Peak(bytes, (int)at, block, fmt));
            await Task.Delay(40);
        }
        output.Stop();
    }

    /// <summary>The mic's audio, in the channels and rate the headset plays.</summary>
    static IWaveProvider ToDevice(IWaveProvider source, MMDevice speaker)
    {
        var mix = speaker.AudioClient.MixFormat;
        ISampleProvider s = source.ToSampleProvider();
        if (s.WaveFormat.Channels == 1 && mix.Channels >= 2) s = new MonoToStereoSampleProvider(s);
        else if (s.WaveFormat.Channels > 2) s = new MultiplexingSampleProvider(new[] { s }, 2);
        if (s.WaveFormat.SampleRate != mix.SampleRate) s = new WdlResamplingSampleProvider(s, mix.SampleRate);
        return s.ToWaveProvider();
    }

    sealed class Stats
    {
        int _all, _spoke, _clipped;
        double _sum;
        public void Add(float p)
        {
            lock (this)
            {
                _all++;
                if (p > 0.03f) { _spoke++; _sum += Math.Sqrt(p); }
                if (p >= 0.985f) _clipped++;
            }
        }
        public Verdict Result()
        {
            lock (this) return new Verdict(_spoke >= 3, _spoke > 0 ? (float)(_sum / _spoke) : 0, _spoke > 0 ? (float)_clipped / _spoke : 0);
        }
    }

    /// <summary>The loudest sample in a stretch of audio, 0–1 (32-bit float or 16-bit samples).</summary>
    static float Peak(byte[] buf, int offset, int count, WaveFormat fmt)
    {
        float max = 0;
        int end = Math.Min(buf.Length, offset + count);
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || fmt.BitsPerSample == 32 && fmt.Encoding == WaveFormatEncoding.Extensible)
        {
            for (int i = offset; i + 3 < end; i += 4) max = Math.Max(max, Math.Abs(BitConverter.ToSingle(buf, i)));
        }
        else if (fmt.BitsPerSample == 16)
        {
            for (int i = offset; i + 1 < end; i += 2) max = Math.Max(max, Math.Abs(BitConverter.ToInt16(buf, i) / 32768f));
        }
        return Math.Clamp(max, 0, 1);
    }
}
