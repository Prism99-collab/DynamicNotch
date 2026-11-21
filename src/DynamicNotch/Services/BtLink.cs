using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;

namespace DynamicNotch.Services;

/// <summary>A Bluetooth device nearby that isn't paired yet (in pairing mode).</summary>
public sealed record BtNearby(string Id, string Name, int? Signal, bool LowEnergy, string Category);

/// <summary>
/// Connecting and pairing Bluetooth devices, like the quick settings do:
///  • a paired headset or speaker connects / disconnects through its audio driver's "one-shot reconnect" request
///    (the same request Windows' own Bluetooth button sends), found from its sound endpoints by container id;
///  • devices in pairing mode are found by a short scan and paired (just works, or the code shown on both sides);
///  • anything else (a phone, a controller) is left to Windows' Bluetooth page.
/// Every call blocks a little or a lot: callers run them off the screen's thread.
/// </summary>
public static class BtLink
{
    public enum Result { Done, NotAudio, Failed }

    /// <summary>Connects (true) or disconnects (false) a paired audio device; NotAudio when it has no sound endpoint.</summary>
    public static Result SetConnected(Guid container, bool connect)
    {
        IMMDeviceEnumerator? en = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (en.EnumAudioEndpoints(2 /* all */, 0xF, out var col) != 0 || col == null) return Result.Failed;
            col.GetCount(out uint n);
            bool any = false;
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var ep) != 0 || ep == null) continue;
                try
                {
                    if (ContainerOf(ep) != container) continue;
                    any = true;
                    string? filter = FilterOf(ep);
                    if (filter == null || !tried.Add(filter)) continue;
                    if (en.GetDevice(filter, out var dev) != 0 || dev == null) continue;
                    try
                    {
                        var iid = typeof(IKsControl).GUID;
                        if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o) != 0 || o is not IKsControl ks) continue;
                        try
                        {
                            var prop = new KsProperty { Set = BtAudioSet, Id = connect ? 0u : 1u /* ONESHOT_RECONNECT / ONESHOT_DISCONNECT */, Flags = 1 /* GET */ };
                            int hr = ks.KsProperty(ref prop, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);
                            if (hr == 0) return Result.Done;
                            AppLog.Write($"bluetooth: the driver said 0x{hr:X8}");
                        }
                        finally { Marshal.ReleaseComObject(ks); }
                    }
                    finally { Marshal.ReleaseComObject(dev); }
                }
                catch (Exception ex) { AppLog.Write("bluetooth: " + ex.Message); }
                finally { Marshal.ReleaseComObject(ep); }
            }
            Marshal.ReleaseComObject(col);
            return any ? Result.Failed : Result.NotAudio;
        }
        catch (Exception ex) { AppLog.Write("bluetooth: " + ex.Message); return Result.Failed; }
        finally { if (en != null) Marshal.ReleaseComObject(en); }
    }

    /// <summary>
    /// Opens a link to a paired device (it must be on and in range): a headset that hears from its PC brings its sound
    /// back up by itself, a phone shows up as connected. Holds the link a few seconds so the device can follow through.
    /// </summary>
    public static async Task<bool> WakeAsync(string id, bool lowEnergy, CancellationToken ct)
    {
        try
        {
            if (lowEnergy)
            {
                using var le = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(id);
                if (le == null) return false;
                var g = await le.GetGattServicesAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);
                await Task.Delay(3000, ct);
                return g.Status == Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success;
            }
            using var d = await Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(id);
            if (d == null) return false;
            var r = await d.GetRfcommServicesAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);
            await Task.Delay(3500, ct);
            return r.Error == Windows.Devices.Bluetooth.BluetoothError.Success;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { AppLog.Write($"bluetooth wake: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}"); return false; }
    }

    /// <summary>True when the device has a sound endpoint (so it can be connected from here).</summary>
    public static bool IsAudio(Guid container)
    {
        IMMDeviceEnumerator? en = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (en.EnumAudioEndpoints(2, 0xF, out var col) != 0 || col == null) return false;
            try
            {
                col.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    if (col.Item(i, out var ep) != 0 || ep == null) continue;
                    try { if (ContainerOf(ep) == container) return true; }
                    finally { Marshal.ReleaseComObject(ep); }
                }
            }
            finally { Marshal.ReleaseComObject(col); }
        }
        catch { }
        finally { if (en != null) Marshal.ReleaseComObject(en); }
        return false;
    }

    static readonly Guid BtAudioSet = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D"); // KSPROPSETID_BtAudio

    static Guid? ContainerOf(IMMDevice ep)
    {
        if (ep.OpenPropertyStore(0, out var store) != 0 || store == null) return null;
        try
        {
            var key = new PropertyKey { Fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAE6B9A"), Pid = 2 }; // PKEY_Device_ContainerId
            if (store.GetValue(ref key, out var v) != 0) return null;
            try { return v.Vt == 72 /* VT_CLSID */ && v.P != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(v.P) : null; }
            finally { PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    /// <summary>The audio driver (KS filter) behind a sound endpoint.</summary>
    static string? FilterOf(IMMDevice ep)
    {
        var iid = typeof(IDeviceTopology).GUID;
        if (ep.Activate(ref iid, 23, IntPtr.Zero, out var o) != 0 || o is not IDeviceTopology topo) return null;
        try
        {
            if (topo.GetConnector(0, out var con) != 0 || con == null) return null;
            try { return con.GetDeviceIdConnectedTo(out var id) == 0 ? id : null; }
            finally { Marshal.ReleaseComObject(con); }
        }
        finally { Marshal.ReleaseComObject(topo); }
    }

    // ───────────────────────────── pairing ─────────────────────────────

    const string ClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    const string LowEnergyProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";

    /// <summary>
    /// Watches for devices in pairing mode until <paramref name="ct"/> ends; <paramref name="found"/> gets the whole
    /// list each time it changes (on a background thread). Unnamed Low Energy beacons are left out.
    /// </summary>
    public static void Discover(Action<List<BtNearby>> found, CancellationToken ct)
    {
        string aqs = "(System.Devices.Aep.ProtocolId:=\"" + ClassicProtocol + "\" OR System.Devices.Aep.ProtocolId:=\"" + LowEnergyProtocol +
                     "\") AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#False";
        var props = new[] { "System.Devices.Aep.SignalStrength", "System.Devices.Aep.IsPresent", "System.Devices.Aep.Category", "System.Devices.Aep.ProtocolId", "System.Devices.Aep.CanPair" };
        var all = new Dictionary<string, BtNearby>();
        var gate = new object();
        DeviceWatcher w;
        try { w = DeviceInformation.CreateWatcher(aqs, props, DeviceInformationKind.AssociationEndpoint); }
        catch (Exception ex) { AppLog.Write("bluetooth scan: " + ex.Message); return; }
        void Publish() { List<BtNearby> list; lock (gate) list = all.Values.OrderByDescending(d => d.Signal ?? -200).Take(14).ToList(); try { found(list); } catch { } }
        static int? Sig(IReadOnlyDictionary<string, object> p) => p.TryGetValue("System.Devices.Aep.SignalStrength", out var s) && s is int v ? v : null;
        w.Added += (_, d) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(d.Name)) return;
                bool le = d.Properties.TryGetValue("System.Devices.Aep.ProtocolId", out var p) && p is Guid g && g == Guid.Parse(LowEnergyProtocol);
                string cat = d.Properties.TryGetValue("System.Devices.Aep.Category", out var c) && c is string[] cs && cs.Length > 0 ? cs[0] : "";
                lock (gate) all[d.Id] = new BtNearby(d.Id, d.Name.Trim(), Sig(d.Properties), le, cat);
                Publish();
            }
            catch { }
        };
        w.Updated += (_, u) =>
        {
            try
            {
                bool changed = false;
                lock (gate)
                    if (all.TryGetValue(u.Id, out var had))
                    {
                        if (u.Properties.TryGetValue("System.Devices.Aep.IsPresent", out var pr) && pr is false) { all.Remove(u.Id); changed = true; }
                        else if (Sig(u.Properties) is { } s && s != had.Signal) { all[u.Id] = had with { Signal = s }; changed = true; }
                    }
                if (changed) Publish();
            }
            catch { }
        };
        w.Removed += (_, u) => { try { bool r; lock (gate) r = all.Remove(u.Id); if (r) Publish(); } catch { } };
        try { w.Start(); } catch (Exception ex) { AppLog.Write("bluetooth scan: " + ex.Message); return; }
        ct.Register(() => { try { if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop(); } catch { } });
    }

    /// <summary>Pairs a device; <paramref name="showCode"/> gets the code to compare when the device shows one.</summary>
    public static async Task<(bool Ok, string Why)> PairAsync(string id, Action<string> showCode)
    {
        try
        {
            var dev = await DeviceInformation.CreateFromIdAsync(id);
            if (dev.Pairing.IsPaired) return (true, "");
            if (!dev.Pairing.CanPair) return (false, "It isn't ready to pair");
            var custom = dev.Pairing.Custom;
            custom.PairingRequested += (_, a) =>
            {
                switch (a.PairingKind)
                {
                    case DevicePairingKinds.ConfirmOnly: a.Accept(); break;
                    case DevicePairingKinds.DisplayPin:
                    case DevicePairingKinds.ConfirmPinMatch:
                        try { showCode(a.Pin); } catch { }
                        a.Accept();
                        break;
                }
            };
            var r = await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ConfirmPinMatch, DevicePairingProtectionLevel.Default);
            return r.Status switch
            {
                DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired => (true, ""),
                DevicePairingResultStatus.PairingCanceled or DevicePairingResultStatus.RejectedByHandler => (false, "Pairing was cancelled"),
                DevicePairingResultStatus.AuthenticationTimeout or DevicePairingResultStatus.ConnectionRejected => (false, "It didn't answer: is it still in pairing mode?"),
                DevicePairingResultStatus.AuthenticationFailure or DevicePairingResultStatus.Failed => (false, "Pairing failed"),
                DevicePairingResultStatus.OperationAlreadyInProgress => (false, "Already pairing"),
                _ => (false, "It needs a code typed: use Windows' Bluetooth page"),
            };
        }
        catch (Exception ex) { AppLog.Write("bluetooth pair: " + ex.Message); return (false, "Pairing failed"); }
    }

    /// <summary>Removes a pairing.</summary>
    public static async Task<bool> UnpairAsync(string id)
    {
        try
        {
            var dev = await DeviceInformation.CreateFromIdAsync(id);
            var r = await dev.Pairing.UnpairAsync();
            return r.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired;
        }
        catch { return false; }
    }

    // ───────────────────────────── COM ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)] struct KsProperty { public Guid Set; public uint Id; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Fmtid; public uint Pid; }
    [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort Vt; public ushort R1, R2, R3; public IntPtr P; public IntPtr P2; }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? o);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore? store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector? connector);
    }

    [ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IConnector
    {
        [PreserveSig] int GetConnectorType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IntPtr other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected(out bool connected);
        [PreserveSig] int GetConnectedTo(out IntPtr other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsProperty property, uint length, IntPtr data, uint dataLength, out uint returned);
        [PreserveSig] int KsMethod(IntPtr method, uint length, IntPtr data, uint dataLength, out uint returned);
        [PreserveSig] int KsEvent(IntPtr ev, uint length, IntPtr data, uint dataLength, out uint returned);
    }
}
