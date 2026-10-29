using System.Windows.Threading;
using Windows.Devices.Enumeration;

namespace DynamicNotch.Services;

/// <summary>Watches paired Bluetooth devices and reports connects / disconnects (no polling).</summary>
public sealed class BluetoothService
{
    const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    // classic Bluetooth (headphones, speakers, phones); BLE mice/keyboards reconnect constantly and would be noisy
    const string Aqs = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";

    readonly Dispatcher _ui;
    readonly Dictionary<string, (string Name, bool Connected)> _devices = new();
    DeviceWatcher? _watcher;
    bool _ready;
    (string Name, bool Connected, DateTime At) _last;

    public event Action<string, bool>? ConnectionChanged;
    public event Action? Changed;

    public string? ConnectedName => _devices.Values.Where(d => d.Connected).Select(d => d.Name).FirstOrDefault();

    public BluetoothService(Dispatcher ui) => _ui = ui;

    public void Start()
    {
        try
        {
            _watcher = DeviceInformation.CreateWatcher(Aqs, new[] { IsConnectedKey }, DeviceInformationKind.AssociationEndpoint);
            // these run on background threads: never let an exception escape (it would end the app)
            _watcher.Added += (_, d) =>
            {
                try
                {
                    bool c = d.Properties.TryGetValue(IsConnectedKey, out var v) && v is true;
                    string name = d.Name, id = d.Id;
                    _ui.InvokeAsync(() => { _devices[id] = (name, c); if (_ready) Changed?.Invoke(); });
                }
                catch { }
            };
            _watcher.Updated += (_, u) =>
            {
                try
                {
                    if (!u.Properties.TryGetValue(IsConnectedKey, out var v) || v is not bool c) return;
                    string id = u.Id;
                    _ui.InvokeAsync(() => Update(id, c));
                }
                catch { }
            };
            _watcher.Removed += (_, u) => { try { string id = u.Id; _ui.InvokeAsync(() => _devices.Remove(id)); } catch { } };
            _watcher.EnumerationCompleted += (_, _) => _ui.InvokeAsync(() => { _ready = true; Changed?.Invoke(); });
            _watcher.Start();
        }
        catch { }
    }

    void Update(string id, bool connected)
    {
        if (!_devices.TryGetValue(id, out var d) || d.Connected == connected) return;
        _devices[id] = (d.Name, connected);
        Changed?.Invoke();
        if (!_ready) return;
        var now = DateTime.UtcNow;
        if (_last.Name == d.Name && _last.Connected == connected && (now - _last.At).TotalSeconds < 4) return;
        _last = (d.Name, connected, now);
        ConnectionChanged?.Invoke(d.Name, connected);
    }

    public static string GlyphFor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("pods") || n.Contains("buds") || n.Contains("head") || n.Contains("wh-") || n.Contains("wf-") ||
            n.Contains("beats") || n.Contains("jbl") || n.Contains("ear")) return ""; // headphones
        if (n.Contains("speaker") || n.Contains("soundbar") || n.Contains("boom")) return "";
        if (n.Contains("phone") || n.Contains("galaxy") || n.Contains("pixel") || n.Contains("redmi")) return "";
        if (n.Contains("mouse")) return "";
        if (n.Contains("keyboard")) return "";
        if (n.Contains("controller") || n.Contains("xbox")) return "";
        return "";
    }
}
