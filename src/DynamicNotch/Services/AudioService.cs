using System.Runtime.InteropServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicNotch.Services;

public sealed record AudioDevice(string Id, string Name, bool IsHeadphones, bool IsDefault);

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
                OutputName = _out.FriendlyName;
                OutputIsHeadphones = IsHeadphones(_out);
            }
            catch { }
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

    public List<AudioDevice> GetOutputs()
    {
        var list = new List<AudioDevice>();
        if (_en == null) return list;
        string? defId = null;
        try { defId = _out?.ID; } catch { }
        try
        {
            foreach (var d in _en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (d) list.Add(new AudioDevice(d.ID, d.FriendlyName, IsHeadphones(d), d.ID == defId));
            }
        }
        catch { }
        return list;
    }

    public void SetDefaultOutput(string id)
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
    }

    static bool IsHeadphones(MMDevice d)
    {
        try
        {
            var ff = d.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor];
            if (ff?.Value is uint u && (u == 3 || u == 5)) return true; // headphones / headset
        }
        catch { }
        string n = "";
        try { n = d.FriendlyName.ToLowerInvariant(); } catch { }
        return n.Contains("headphone") || n.Contains("headset") || n.Contains("airpods") || n.Contains("buds") || n.Contains("hands-free");
    }

    // IMMNotificationClient ------------------------------------------------------------------
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) _ui.InvokeAsync(() => BindOutput(notify: true));
        else if (flow == DataFlow.Capture && role == Role.Console) _ui.InvokeAsync(() => BindMic(notify: true));
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
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
