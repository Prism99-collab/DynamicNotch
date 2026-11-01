using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicNotch.Services;

public sealed record AudioDevice(string Id, string Name, bool IsHeadphones, bool IsDefault);

/// <summary>One app in the mixer: all of its sound sessions on the current output, as one volume.</summary>
public sealed record MixerApp(string Key, string? Path, bool IsSystem, float Volume, bool Muted, bool Active);

/// <summary>Volume, mute, microphone and output-device switching through Windows Core Audio. Fully event driven.</summary>
public sealed class AudioService : IMMNotificationClient
{
    readonly Dispatcher _ui;
    MMDeviceEnumerator? _en;
    MMDevice? _out, _mic;

    public float Volume { get; private set; }
    public bool Muted { get; private set; }
    public bool HasMic => _mic != null;
    public bool MicMuted { get; private set; }
    public string OutputName { get; private set; } = "";
    public bool OutputIsHeadphones { get; private set; }

    public event Action? VolumeChanged;
    public event Action? OutputChanged;
    public event Action? MicChanged;

    public AudioService(Dispatcher ui) => _ui = ui;

    /// <summary>Safe to call again after sleep / unlock: re-attaches to the current devices.</summary>
    public void Init()
    {
        if (_en == null)
        {
            try
            {
                _en = new MMDeviceEnumerator();
                _en.RegisterEndpointNotificationCallback(this);
            }
            catch { _en = null; return; }
        }
        try { BindOutput(notify: false); } catch { }
        try { BindMic(notify: false); } catch { }
        RefreshDevices();
        VolumeChanged?.Invoke();
    }

    void BindOutput(bool notify)
    {
        if (_out != null)
        {
            try { _out.AudioEndpointVolume.OnVolumeNotification -= OnOutputVolume; } catch { }
            _out.Dispose();
            _out = null;
        }
        try { _out = _en?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
        if (_out != null)
        {
            try
            {
                var v = _out.AudioEndpointVolume;
                Volume = v.MasterVolumeLevelScalar;
                Muted = v.Mute;
                v.OnVolumeNotification += OnOutputVolume;
            }
            catch { }
            string id = "";
            try { id = _out.ID; } catch { }
            if (Names.TryGetValue(id, out var known)) { OutputName = known.Name; OutputIsHeadphones = known.Headphones; }
            else
            {
                // a device not described yet: read its name in the background, then let the UI know
                Task.Run(() => DescribeById(id)).ContinueWith(t => _ui.InvokeAsync(() =>
                {
                    string now = "";
                    try { now = _out?.ID ?? ""; } catch { }
                    if (now != id) return; // switched again meanwhile
                    OutputName = t.Result.Name;
                    OutputIsHeadphones = t.Result.Headphones;
                    if (notify) OutputChanged?.Invoke();
                }));
                return;
            }
        }
        if (notify) OutputChanged?.Invoke();
    }

    void BindMic(bool notify)
    {
        if (_mic != null)
        {
            try { _mic.AudioEndpointVolume.OnVolumeNotification -= OnMicVolume; } catch { }
            _mic.Dispose();
            _mic = null;
        }
        try { _mic = _en?.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); } catch { }
        if (_mic != null)
        {
            try
            {
                MicMuted = _mic.AudioEndpointVolume.Mute;
                _mic.AudioEndpointVolume.OnVolumeNotification += OnMicVolume;
            }
            catch { }
        }
        if (notify) MicChanged?.Invoke();
    }

    void OnOutputVolume(AudioVolumeNotificationData d)
    {
        float vol;
        bool muted;
        try { vol = d.MasterVolume; muted = d.Muted; } catch { return; } // COM thread: never throw
        _ui.InvokeAsync(() =>
        {
            // our own slider already stored the value, so only real outside changes fire the event
            bool changed = Math.Abs(vol - Volume) > 0.002f || muted != Muted;
            Volume = vol;
            Muted = muted;
            if (changed) VolumeChanged?.Invoke();
        });
    }

    void OnMicVolume(AudioVolumeNotificationData d)
    {
        bool muted = d.Muted;
        _ui.InvokeAsync(() =>
        {
            if (muted == MicMuted) return;
            MicMuted = muted;
            MicChanged?.Invoke();
        });
    }

    /// <summary>
    /// How loud the speakers are right now (left / right peak, 0–1), what Windows' own volume meter shows.
    /// A cheap read of the default output, made only while the visualizer is on screen.
    /// </summary>
    public bool TryGetPeaks(out float left, out float right)
    {
        left = right = 0;
        if (_out == null) return false;
        try
        {
            var meter = _out.AudioMeterInformation;
            var peaks = meter.PeakValues;
            if (peaks.Count >= 2) { left = peaks[0]; right = peaks[1]; }
            else left = right = meter.MasterPeakValue;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// The apps recording from any microphone right now (exe paths), straight from Windows' audio engine:
    /// every active capture stream, whichever app or virtual mic setup it goes through. Off the UI thread.
    /// </summary>
    public static Task<List<string>> ActiveCaptureAppsAsync() => Task.Run(() =>
    {
        var paths = new List<string>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (dev)
                {
                    try
                    {
                        var sessions = dev.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            using var s = sessions[i];
                            try
                            {
                                if (s.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                                uint pid = s.GetProcessID;
                                if (pid == 0 || pid == Environment.ProcessId) continue;
                                if (Interop.Native.ProcessPathById(pid) is { } path && !paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        return paths;
    });

    /// <summary>The apps playing sound right now on the default output (exe paths). Off the UI thread.</summary>
    public static Task<List<string>> ActiveRenderAppsAsync() => Task.Run(() =>
    {
        var paths = new List<string>();
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = dev.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var s = sessions[i];
                try
                {
                    if (s.State != AudioSessionState.AudioSessionStateActive || s.IsSystemSoundsSession) continue;
                    uint pid = s.GetProcessID;
                    if (pid == 0 || pid == Environment.ProcessId) continue;
                    if (Interop.Native.ProcessPathById(pid) is { } path && !paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
                }
                catch { }
            }
        }
        catch { }
        return paths;
    });

    /// <summary>How loud an app is right now on the default output (0–1, its loudest session); 0 when silent or absent.</summary>
    public static Task<float> AppPeakAsync(string path) => Task.Run(() =>
    {
        float peak = 0;
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = dev.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var s = sessions[i];
                try
                {
                    if (s.State != AudioSessionState.AudioSessionStateActive) continue;
                    if (!string.Equals(Interop.Native.ProcessPathById(s.GetProcessID), path, StringComparison.OrdinalIgnoreCase)) continue;
                    peak = Math.Max(peak, s.AudioMeterInformation.MasterPeakValue);
                }
                catch { }
            }
        }
        catch { }
        return peak;
    });

    /// <summary>How loud your microphone hears you right now (0–1); 0 while it's muted.</summary>
    public bool TryGetMicPeak(out float level)
    {
        level = 0;
        if (_mic == null) return false;
        if (MicMuted) return true;
        try { level = _mic.AudioMeterInformation.MasterPeakValue; return true; }
        catch { return false; }
    }

    public void SetVolume(float v)
    {
        if (_out == null) return;
        v = Math.Clamp(v, 0f, 1f);
        Volume = v;
        try
        {
            _out.AudioEndpointVolume.MasterVolumeLevelScalar = v;
            if (Muted && v > 0) { Muted = false; _out.AudioEndpointVolume.Mute = false; }
        }
        catch { }
    }

    public void ToggleMute()
    {
        if (_out == null) return;
        Muted = !Muted;
        try { _out.AudioEndpointVolume.Mute = Muted; } catch { }
    }

    public void ToggleMic()
    {
        if (_mic == null) return;
        MicMuted = !MicMuted;
        try { _mic.AudioEndpointVolume.Mute = MicMuted; } catch { }
    }

    // device lists -----------------------------------------------------------------------------
    // Reading every device's name and form factor is slow (worse with wireless headsets), so the lists
    // are kept ready in the background and refreshed only when Windows reports a device change.

    List<AudioDevice> _outputs = new(), _inputs = new();
    int _scanVersion, _scanLogged;

    public IReadOnlyList<AudioDevice> Outputs => _outputs;
    public IReadOnlyList<AudioDevice> Inputs => _inputs;
    /// <summary>The device lists changed (UI thread).</summary>
    public event Action? DevicesChanged;

    public void RefreshDevices()
    {
        int version = Interlocked.Increment(ref _scanVersion);
        Task.Run(() =>
        {
            // Windows can be busy right after a device change: try again shortly instead of showing nothing
            var took = System.Diagnostics.Stopwatch.StartNew();
            List<AudioDevice>? outs = null, ins = null;
            for (int attempt = 0; attempt < 3 && (outs == null || ins == null); attempt++)
            {
                if (attempt > 0) Thread.Sleep(300);
                outs ??= Scan(DataFlow.Render, Role.Multimedia);
                ins ??= Scan(DataFlow.Capture, Role.Console);
            }
            _ui.InvokeAsync(() =>
            {
                if (version != Volatile.Read(ref _scanVersion)) return; // a newer scan is on its way
                if ((outs?.Count ?? _outputs.Count) != _outputs.Count || (ins?.Count ?? _inputs.Count) != _inputs.Count || _scanLogged++ == 0)
                    AppLog.Write($"audio devices: {outs?.Count.ToString() ?? "scan failed"} outputs, {ins?.Count.ToString() ?? "scan failed"} inputs ({took.ElapsedMilliseconds} ms)");
                if (outs != null) _outputs = outs; // a failed scan keeps the last good list
                if (ins != null) _inputs = ins;
                DevicesChanged?.Invoke();
            });
        });
    }

    /// <summary>Active devices of one kind, or null if Windows couldn't be asked.</summary>
    static List<AudioDevice>? Scan(DataFlow flow, Role role)
    {
        var list = new List<AudioDevice>();
        try
        {
            var en = new MMDeviceEnumerator();
            string? def = null;
            try { using var d = en.GetDefaultAudioEndpoint(flow, role); def = d.ID; } catch { }
            var all = en.EnumerateAudioEndPoints(flow, DeviceState.Active);
            for (int i = 0; i < all.Count; i++)
            {
                // one odd device (virtual cable, a headset that's half connected) shouldn't hide the rest
                try
                {
                    using var d = all[i];
                    string id = d.ID;
                    var (name, headphones) = Describe(d);
                    list.Add(new AudioDevice(id, name, headphones, id == def));
                }
                catch (Exception ex) { AppLog.Write($"audio device skipped ({flow}): {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"audio device scan failed ({flow}): {ex.Message}");
            return null;
        }
        return list;
    }

    /// <summary>Makes a speaker / headset / microphone the Windows default, without blocking the UI.</summary>
    public void SetDefaultDevice(string id)
    {
        // tick the new choice right away; Windows confirms through OnDefaultDeviceChanged
        if (_outputs.Any(d => d.Id == id)) _outputs = _outputs.Select(d => d with { IsDefault = d.Id == id }).ToList();
        if (_inputs.Any(d => d.Id == id)) _inputs = _inputs.Select(d => d with { IsDefault = d.Id == id }).ToList();
        DevicesChanged?.Invoke();
        Task.Run(() =>
        {
            try
            {
                var cfg = (IPolicyConfig)new PolicyConfigClient();
                cfg.SetDefaultEndpoint(id, 0); // console
                cfg.SetDefaultEndpoint(id, 1); // multimedia
                cfg.SetDefaultEndpoint(id, 2); // communications
                Marshal.ReleaseComObject(cfg);
            }
            catch { }
        });
    }

    static bool IsHeadphones(string name, uint? formFactor)
    {
        if (formFactor is 3 or 5) return true; // headphones / headset
        string n = name.ToLowerInvariant();
        return n.Contains("headphone") || n.Contains("headset") || n.Contains("airpods") || n.Contains("buds") || n.Contains("hands-free");
    }

    // Device names: NAudio reads a name by walking every property of the device (about half a second per
    // speaker on this PC), so each device is described once, off the UI thread, and remembered by its id.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Name, bool Headphones)> Names = new();

    static (string Name, bool Headphones) Describe(MMDevice d)
    {
        string id = d.ID;
        if (Names.TryGetValue(id, out var known)) return known;
        string name = "";
        try { name = d.FriendlyName; } catch { }
        uint? formFactor = null;
        try { if (d.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor]?.Value is uint u) formFactor = u; } catch { }
        var info = (name, IsHeadphones(name, formFactor));
        if (name.Length > 0) Names[id] = info; // never remember a failed read
        return info;
    }

    static (string Name, bool Headphones) DescribeById(string id)
    {
        if (Names.TryGetValue(id, out var known)) return known;
        try
        {
            using var d = new MMDeviceEnumerator().GetDevice(id);
            return Describe(d);
        }
        catch { return ("", false); }
    }

    // per-app volume (the app that's playing media) ----------------------------------------------

    // Everything here runs on background threads with its own device handle (Core Audio is free-threaded),
    // so opening the control center or dragging the slider never waits on the audio service.
    // ───────────── mixer ─────────────
    readonly object _mixGate = new();
    MMDeviceEnumerator? _mixEnum;
    MMDevice? _mixDevice;
    readonly Dictionary<string, List<AudioSessionControl>> _mix = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> _mixPending = new();
    int _mixBusy;

    /// <summary>
    /// Every app with a sound session on the current output (one entry per app, however many sessions it has),
    /// plus Windows' own system sounds. Read off the UI thread; the sessions are kept for the volume writes.
    /// </summary>
    public Task<List<MixerApp>> MixerAppsAsync() => Task.Run(() =>
    {
        var result = new List<MixerApp>();
        lock (_mixGate)
        {
            ReleaseMix();
            try
            {
                _mixEnum ??= new MMDeviceEnumerator();
                _mixDevice = _mixEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var mgr = _mixDevice.AudioSessionManager;
                mgr.RefreshSessions();
                var sessions = mgr.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    try
                    {
                        if (s.State == AudioSessionState.AudioSessionStateExpired) { s.Dispose(); continue; }
                        string key;
                        if (s.IsSystemSoundsSession) key = "system";
                        else
                        {
                            uint pid = s.GetProcessID;
                            if (pid == 0 || pid == Environment.ProcessId) { s.Dispose(); continue; }
                            key = Interop.Native.ProcessPathById(pid) ?? $"pid:{pid}";
                        }
                        if (!_mix.TryGetValue(key, out var list)) _mix[key] = list = new();
                        list.Add(s);
                    }
                    catch { try { s.Dispose(); } catch { } }
                }
                foreach (var (key, list) in _mix)
                {
                    float vol = 0; bool muted = true, active = false;
                    foreach (var s in list)
                    {
                        try
                        {
                            var sv = s.SimpleAudioVolume;
                            vol = Math.Max(vol, sv.Volume);
                            muted &= sv.Mute;
                            active |= s.State == AudioSessionState.AudioSessionStateActive;
                        }
                        catch { }
                    }
                    bool system = key == "system";
                    result.Add(new MixerApp(key, system || key.StartsWith("pid:") ? null : key, system, vol, muted, active));
                }
            }
            catch { }
        }
        return result;
    });

    /// <summary>Called continuously while a mixer slider is dragged; only each app's latest value is written.</summary>
    public void SetMixerVolume(string key, float v)
    {
        _mixPending[key] = Math.Clamp(v, 0f, 1f);
        if (Interlocked.Exchange(ref _mixBusy, 1) == 1) return;
        Task.Run(() =>
        {
            while (true)
            {
                foreach (var k in _mixPending.Keys.ToList())
                {
                    if (!_mixPending.TryRemove(k, out float level)) continue;
                    lock (_mixGate)
                    {
                        if (!_mix.TryGetValue(k, out var list)) continue;
                        foreach (var s in list)
                        {
                            try
                            {
                                var sv = s.SimpleAudioVolume;
                                sv.Volume = level;
                                if (sv.Mute && level > 0) sv.Mute = false; // dragging up unmutes, like Windows' own mixer
                            }
                            catch { }
                        }
                    }
                }
                Interlocked.Exchange(ref _mixBusy, 0);
                if (_mixPending.IsEmpty || Interlocked.Exchange(ref _mixBusy, 1) == 1) return;
            }
        });
    }

    public void SetMixerMute(string key, bool mute) => Task.Run(() =>
    {
        lock (_mixGate)
            if (_mix.TryGetValue(key, out var list))
                foreach (var s in list) try { s.SimpleAudioVolume.Mute = mute; } catch { }
    });

    /// <summary>The mixer was closed: let go of the sessions.</summary>
    public void ReleaseMixer() => Task.Run(() => { lock (_mixGate) ReleaseMix(); });

    void ReleaseMix()
    {
        foreach (var list in _mix.Values)
            foreach (var s in list) try { s.Dispose(); } catch { }
        _mix.Clear();
        try { _mixDevice?.Dispose(); } catch { }
        _mixDevice = null;
    }

    readonly object _appGate = new();
    readonly List<AudioSessionControl> _appSessions = new();
    MMDeviceEnumerator? _appEnum;
    MMDevice? _appDevice;
    int _appPending = -1, _appBusy;

    /// <summary>
    /// Finds the audio sessions of the app behind a media session (Spotify, a browser, Media Player...).
    /// Returns its volume 0–1, or null when it has no sound session on the current output.
    /// </summary>
    public Task<float?> FindAppVolumeAsync(string? mediaAppId) => Task.Run(() =>
    {
        lock (_appGate)
        {
            ReleaseAppSessions();
            var keys = AppKeys(mediaAppId);
            if (keys.Count == 0) return (float?)null;
            try
            {
                _appEnum ??= new MMDeviceEnumerator();
                _appDevice = _appEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var mgr = _appDevice.AudioSessionManager;
                mgr.RefreshSessions();
                var sessions = mgr.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    bool match = false;
                    try
                    {
                        if (!s.IsSystemSoundsSession)
                        {
                            string id = s.GetSessionIdentifier ?? "";
                            string path = Interop.Native.ProcessPathById(s.GetProcessID) ?? "";
                            match = keys.Any(k => id.Contains(k, StringComparison.OrdinalIgnoreCase) || path.Contains(k, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                    catch { }
                    if (match) _appSessions.Add(s); else s.Dispose();
                }
            }
            catch { }

            // the loudest of its sessions
            float? v = null;
            foreach (var s in _appSessions)
                try { var sv = s.SimpleAudioVolume; float x = sv.Mute ? 0 : sv.Volume; v = Math.Max(v ?? 0, x); } catch { }
            return v;
        }
    });

    /// <summary>
    /// How loud the media app (found by <see cref="FindAppVolumeAsync"/>) sounds right now, 0–1. A browser with several
    /// tabs tells Windows about one tab only, which may be paused while another plays: this is what's really heard.
    /// Call off the UI thread.
    /// </summary>
    public float AppPeak()
    {
        lock (_appGate)
        {
            float peak = 0;
            foreach (var s in _appSessions)
                try { peak = Math.Max(peak, s.AudioMeterInformation.MasterPeakValue); } catch { }
            return peak;
        }
    }

    /// <summary>Called continuously while dragging; only the latest value is ever written.</summary>
    public void SetAppVolume(float v)
    {
        Interlocked.Exchange(ref _appPending, (int)Math.Round(Math.Clamp(v, 0f, 1f) * 1000));
        if (Interlocked.Exchange(ref _appBusy, 1) == 1) return;
        Task.Run(() =>
        {
            while (true)
            {
                int p = Interlocked.Exchange(ref _appPending, -1);
                if (p >= 0)
                {
                    float level = p / 1000f;
                    lock (_appGate)
                        foreach (var s in _appSessions)
                        {
                            try
                            {
                                var sv = s.SimpleAudioVolume;
                                sv.Volume = level;
                                if (sv.Mute && level > 0) sv.Mute = false;
                            }
                            catch { }
                        }
                    continue;
                }
                Interlocked.Exchange(ref _appBusy, 0);
                // a value may have arrived between the check and releasing the flag
                if (Volatile.Read(ref _appPending) < 0 || Interlocked.Exchange(ref _appBusy, 1) == 1) return;
            }
        });
    }

    void ReleaseAppSessions()
    {
        foreach (var s in _appSessions) try { s.Dispose(); } catch { }
        _appSessions.Clear();
        try { _appDevice?.Dispose(); } catch { }
        _appDevice = null;
    }

    /// <summary>What to look for in an audio session's id / exe path for a media app id.</summary>
    static List<string> AppKeys(string? appId)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(appId)) return keys;
        int bang = appId.IndexOf('!');
        if (bang > 0)
        {
            // Store app ("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"): its exe lives in a folder named after the package
            string family = appId[..bang];
            int us = family.IndexOf('_');
            keys.Add((us > 0 ? family[..us] : family) + "_");
            return keys;
        }
        string exe = appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? appId : appId + ".exe";
        // browsers report a short name; Firefox reports a hash of its install folder
        if (appId.Equals("MSEdge", StringComparison.OrdinalIgnoreCase)) exe = "msedge.exe";
        else if (appId.Length == 16 && appId.All(Uri.IsHexDigit)) exe = "firefox.exe";
        keys.Add("\\" + Path.GetFileName(exe));
        return keys;
    }

    // IMMNotificationClient ------------------------------------------------------------------
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) _ui.InvokeAsync(() => BindOutput(notify: true));
        else if (flow == DataFlow.Capture && role == Role.Console) _ui.InvokeAsync(() => BindMic(notify: true));
        else return;
        RefreshDevices();
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => RefreshDevices();
    public void OnDeviceAdded(string pwstrDeviceId) => RefreshDevices();
    public void OnDeviceRemoved(string deviceId) => RefreshDevices();
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    // Undocumented but long-stable interface Windows' own sound settings use to change the default device.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility();
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }
}
