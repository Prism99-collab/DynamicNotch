using System.Windows.Threading;
using Windows.Devices.Enumeration;

namespace DynamicNotch.Services;

/// <summary>A paired Bluetooth device as the notch sees it.</summary>
public sealed record BtDevice(string Id, string Name, bool Connected, string Category, Guid? Container, bool LowEnergy, int? Battery)
{
    /// <summary>Mice and keyboards reconnect every time they wake up: worth a tile label, never an alert.</summary>
    public bool IsQuietInput => Category.StartsWith("Input.Mouse", StringComparison.OrdinalIgnoreCase)
        || Category.StartsWith("Input.Keyboard", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("mouse", StringComparison.OrdinalIgnoreCase) || Name.Contains("keyboard", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Watches paired Bluetooth devices, classic (headphones, speakers, phones) and Low Energy (game controllers,
/// earbuds, mice...), reports connects / disconnects and reads their battery level. No polling, except a slow
/// battery refresh while something is connected.
/// </summary>
public sealed class BluetoothService
{
    const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    const string CategoryKey = "System.Devices.Aep.Category";
    const string ContainerKey = "System.Devices.Aep.ContainerId";
    // the battery level Windows shows in Settings > Bluetooth, stored on the device's own node
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    const string ClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    const string LowEnergyProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
    const string Aqs = "(System.Devices.Aep.ProtocolId:=\"" + ClassicProtocol + "\" OR System.Devices.Aep.ProtocolId:=\"" + LowEnergyProtocol +
                       "\") AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";

    readonly Dispatcher _ui;
    readonly Dictionary<string, BtDevice> _devices = new();
    readonly HashSet<string> _lowWarned = new();
    readonly DispatcherTimer _batteryTimer;
    DeviceWatcher? _watcher;
    bool _ready;
    (string Name, bool Connected, DateTime At) _last;

    /// <summary>A device connected (true) or disconnected (false); battery already read when known.</summary>
    public event Action<BtDevice, bool>? ConnectionChanged;
    /// <summary>A connected device's battery dropped to 15 % or below (once per charge).</summary>
    public event Action<BtDevice>? BatteryLow;
    public event Action? Changed;

    public BluetoothService(Dispatcher ui)
    {
        _ui = ui;
        _batteryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(2) };
        _batteryTimer.Tick += async (_, _) => await RefreshBatteriesAsync();
    }

    /// <summary>The device to name on the Bluetooth tile: audio first, then controllers / phones, mice and keyboards last.</summary>
    /// <summary>Every Bluetooth device connected now (with its battery when it reports one).</summary>
    public IReadOnlyList<BtDevice> ConnectedDevices { get { try { return _devices.Values.Where(d => d.Connected && d.Name.Length > 0).ToList(); } catch { return Array.Empty<BtDevice>(); } } }

    public BtDevice? Primary => _devices.Values.Where(d => d.Connected && d.Name.Length > 0)
        .OrderBy(d => d.IsQuietInput ? 2 : d.Category.StartsWith("Audio", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .FirstOrDefault();

    public string? ConnectedName => Primary?.Name;

    /// <summary>Every paired device with a name, one per device (a headset seen as classic and Low Energy counts once; connected wins).</summary>
    public IReadOnlyList<BtDevice> Paired
    {
        get
        {
            try
            {
                return _devices.Values.Where(d => d.Name.Length > 0)
                    .GroupBy(d => d.Container?.ToString() ?? d.Id)
                    .Select(g => g.OrderByDescending(d => d.Connected).ThenBy(d => d.LowEnergy).First() with { Connected = g.Any(d => d.Connected) })
                    .OrderByDescending(d => d.Connected).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch { return Array.Empty<BtDevice>(); }
        }
    }

    public void Start()
    {
        try
        {
            _watcher = DeviceInformation.CreateWatcher(Aqs, new[] { IsConnectedKey, CategoryKey, ContainerKey }, DeviceInformationKind.AssociationEndpoint);
            // these run on background threads: never let an exception escape (it would end the app)
            _watcher.Added += (watcher, d) =>
            {
                try
                {
                    var dev = Read(d.Id, d.Name, d.Properties, d.Properties.TryGetValue("System.Devices.Aep.ProtocolId", out var p) && p is Guid g
                        ? g == Guid.Parse(LowEnergyProtocol) : d.Id.StartsWith("BluetoothLE", StringComparison.OrdinalIgnoreCase));
                    _ui.InvokeAsync(() =>
                    {
                        _devices[dev.Id] = dev;
                        if (_ready) Changed?.Invoke();
                        if (dev.Connected) _ = RefreshBatteryAsync(dev.Id);
                    });
                }
                catch { }
            };
            _watcher.Updated += (_, u) =>
            {
                try
                {
                    if (!u.Properties.TryGetValue(IsConnectedKey, out var v) || v is not bool c) return;
                    string id = u.Id;
                    _ui.InvokeAsync(async () => await UpdateAsync(id, c));
                }
                catch { }
            };
            _watcher.Removed += (_, u) => { try { string id = u.Id; _ui.InvokeAsync(() => { _devices.Remove(id); Changed?.Invoke(); }); } catch { } };
            _watcher.EnumerationCompleted += (_, _) => _ui.InvokeAsync(() => { _ready = true; Changed?.Invoke(); UpdateBatteryTimer(); });
            // Windows stops the watcher when the radio goes off (or the stack restarts): it would miss every change after
            // that and keep showing devices as connected. Start a fresh one, then read the truth again.
            _watcher.Stopped += (w, _) => _ui.InvokeAsync(async () =>
            {
                if (_watcher != w || _stopping) return;
                await Task.Delay(3000);
                if (_watcher != w || _stopping) return;
                _watcher = null;
                Start();
                await ResyncAsync();
            });
            _watcher.Start();
        }
        catch { }
    }

    bool _stopping;

    /// <summary>
    /// Reads every paired device's connection from Windows again and corrects what's shown, quietly (no
    /// "connected / disconnected" pop-ups for a correction). Called when Bluetooth comes back on, when the device list
    /// is opened, and while it's open.
    /// </summary>
    public async Task ResyncAsync()
    {
        try
        {
            var found = await DeviceInformation.FindAllAsync(Aqs, new[] { IsConnectedKey, CategoryKey, ContainerKey }, DeviceInformationKind.AssociationEndpoint);
            bool changed = false;
            var seen = new HashSet<string>();
            foreach (var d in found)
            {
                seen.Add(d.Id);
                bool connected = d.Properties.TryGetValue(IsConnectedKey, out var v) && v is true;
                if (_devices.TryGetValue(d.Id, out var had))
                {
                    if (had.Connected == connected) continue;
                    _devices[d.Id] = had with { Connected = connected, Battery = connected ? had.Battery : null };
                    if (connected) _ = RefreshBatteryAsync(d.Id);
                }
                else
                {
                    bool le = d.Properties.TryGetValue("System.Devices.Aep.ProtocolId", out var p) && p is Guid g ? g == Guid.Parse(LowEnergyProtocol) : d.Id.StartsWith("BluetoothLE", StringComparison.OrdinalIgnoreCase);
                    _devices[d.Id] = Read(d.Id, d.Name, d.Properties, le);
                }
                changed = true;
            }
            foreach (var id in _devices.Keys.Where(k => !seen.Contains(k)).ToList()) { _devices.Remove(id); changed = true; }
            if (changed) { UpdateBatteryTimer(); Changed?.Invoke(); }
        }
        catch { }
    }

    /// <summary>The radio went off: nothing is connected any more (said at once, quietly).</summary>
    public void RadioOff()
    {
        bool changed = false;
        foreach (var (id, d) in _devices.ToList())
            if (d.Connected) { _devices[id] = d with { Connected = false, Battery = null }; changed = true; }
        if (!changed) return;
        UpdateBatteryTimer();
        Changed?.Invoke();
    }

    static BtDevice Read(string id, string name, IReadOnlyDictionary<string, object> props, bool lowEnergy)
    {
        bool connected = props.TryGetValue(IsConnectedKey, out var v) && v is true;
        string category = props.TryGetValue(CategoryKey, out var c) && c is string[] cats && cats.Length > 0 ? cats[0] : "";
        Guid? container = props.TryGetValue(ContainerKey, out var g) && g is Guid guid ? guid : null;
        return new BtDevice(id, name ?? "", connected, category, container, lowEnergy, null);
    }

    async Task UpdateAsync(string id, bool connected)
    {
        if (!_devices.TryGetValue(id, out var d) || d.Connected == connected) return;
        d = d with { Connected = connected, Battery = connected ? d.Battery : null };
        _devices[id] = d;
        Changed?.Invoke();
        UpdateBatteryTimer();
        if (!_ready || d.Name.Length == 0) return;
        if (connected)
        {
            // read the battery first so the "connected" alert can show it (never wait long for it)
            var read = RefreshBatteryAsync(id);
            await Task.WhenAny(read, Task.Delay(1500));
            d = _devices.GetValueOrDefault(id) ?? d;
        }
        else _lowWarned.Remove(id);
        if (d.IsQuietInput) return;
        var now = DateTime.UtcNow;
        if (_last.Name == d.Name && _last.Connected == connected && (now - _last.At).TotalSeconds < 4) return;
        _last = (d.Name, connected, now);
        ConnectionChanged?.Invoke(d, connected);
    }

    void UpdateBatteryTimer()
    {
        bool any = _devices.Values.Any(d => d.Connected);
        if (any && !_batteryTimer.IsEnabled) _batteryTimer.Start();
        else if (!any) _batteryTimer.Stop();
    }

    /// <summary>Re-reads every connected device's battery (also called when the control center opens).</summary>
    public async Task RefreshBatteriesAsync()
    {
        foreach (var id in _devices.Values.Where(d => d.Connected).Select(d => d.Id).ToList())
            await RefreshBatteryAsync(id);
    }

    async Task RefreshBatteryAsync(string id)
    {
        if (!_devices.TryGetValue(id, out var d) || d.Container is not { } container) return;
        int? level = null;
        try
        {
            var nodes = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"", new[] { BatteryKey }, DeviceInformationKind.Device);
            foreach (var n in nodes)
                if (n.Properties.TryGetValue(BatteryKey, out var b) && b != null) { level = Convert.ToInt32(b); break; }
        }
        catch { }
        if (!_devices.TryGetValue(id, out d) || !d.Connected || level == d.Battery) return;
        _devices[id] = d = d with { Battery = level is >= 0 and <= 100 ? level : null };
        AppLog.Write($"bluetooth: {d.Name} ({d.Category}{(d.LowEnergy ? ", LE" : "")}) battery {d.Battery?.ToString() ?? "unknown"}%");
        Changed?.Invoke();

        // one heads-up per charge; it resets once the device is charged again
        if (d.Battery is { } pct)
        {
            if (pct > 30) _lowWarned.Remove(id);
            else if (pct <= 15 && _lowWarned.Add(id)) BatteryLow?.Invoke(d);
        }
    }

    public static string GlyphFor(BtDevice d)
    {
        string c = d.Category.ToLowerInvariant(), n = d.Name.ToLowerInvariant();
        if (c.StartsWith("input.gaming") || n.Contains("controller") || n.Contains("xbox") || n.Contains("dualsense")) return ""; // game controller
        if (c.StartsWith("audio.headphone") || c.StartsWith("audio.headset") || n.Contains("pods") || n.Contains("buds") || n.Contains("head") ||
            n.Contains("wh-") || n.Contains("wf-") || n.Contains("beats") || n.Contains("ear")) return ""; // headphones
        if (c.StartsWith("audio") || n.Contains("speaker") || n.Contains("soundbar") || n.Contains("jbl") || n.Contains("boom")) return ""; // speaker
        if (c.StartsWith("communication.phone") || n.Contains("phone") || n.Contains("galaxy") || n.Contains("pixel")) return ""; // phone
        if (c.StartsWith("input.mouse") || n.Contains("mouse")) return "";
        if (c.StartsWith("input.keyboard") || n.Contains("keyboard")) return "";
        return ""; // Bluetooth
    }
}
