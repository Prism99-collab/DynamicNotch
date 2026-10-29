using System.Reflection;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DynamicNotch.Services;

/// <summary>
/// Plays the short sound bundled with incoming notch notifications. Opening an audio device takes
/// 20–100 ms (more on Bluetooth / wireless headsets), so all of it happens on a background thread: the
/// UI thread only asks, and the pop-up animation never waits for the speakers.
/// </summary>
public sealed class NotificationSoundService : IDisposable
{
    const string ResourceName = "DynamicNotch.Assets.notification.wav";

    readonly byte[]? _sound;
    readonly object _gate = new();
    WasapiOut? _output;
    WaveFileReader? _reader;
    int _generation;

    public NotificationSoundService()
    {
        try
        {
            using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (resource == null)
            {
                AppLog.Write($"notification sound resource missing: {ResourceName}");
                return;
            }
            using var copy = new MemoryStream();
            resource.CopyTo(copy);
            _sound = copy.ToArray();
            using var check = new WaveFileReader(new MemoryStream(_sound, writable: false));
            _ = check.TotalTime; // a broken file fails here, at startup, not at the first notification
        }
        catch (Exception ex)
        {
            AppLog.Write("notification sound failed to load: " + ex.Message);
            _sound = null;
        }
    }

    public void Play()
    {
        if (_sound == null) return;
        int generation = Interlocked.Increment(ref _generation);
        Task.Run(() =>
        {
            lock (_gate)
            {
                if (generation != _generation) return; // a newer notification already took over
                try
                {
                    StopLocked();
                    _reader = new WaveFileReader(new MemoryStream(_sound, writable: false));
                    // full-scale stream: Windows plays it at exactly the laptop's volume (and not at all when muted)
                    _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: false, latency: 80);
                    _output.Init(_reader);
                    _output.Play();
                }
                catch (Exception ex)
                {
                    AppLog.Write("notification sound failed to play: " + ex.Message);
                    StopLocked();
                    return;
                }
            }
            // release the device once the clip is over (it is ~3 s long)
            Task.Delay(_reader?.TotalTime + TimeSpan.FromMilliseconds(300) ?? TimeSpan.FromSeconds(3.3)).ContinueWith(_ =>
            {
                lock (_gate) if (generation == _generation) StopLocked();
            });
        });
    }

    void StopLocked()
    {
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _output = null;
        _reader = null;
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _generation);
        lock (_gate) StopLocked();
    }
}
