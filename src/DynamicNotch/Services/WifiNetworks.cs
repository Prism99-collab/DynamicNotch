using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace DynamicNotch.Services;

/// <summary>A Wi-Fi network in range, as the sheet shows it.</summary>
public sealed record WifiNet(string Ssid, int Signal, bool Secured, bool Connected, bool Saved, string? ProfileName, int Auth, int Cipher, bool Connectable)
{
    /// <summary>Joinable with just a password (personal WPA / WPA2 / WPA3), or open.</summary>
    public bool PasswordWorks => !Secured || Auth is 1 or 2 or 4 or 7 or 9 or 10;
    /// <summary>Asks for a password to join (Enhanced Open networks are encrypted but have none).</summary>
    public bool NeedsPassword => Secured && Auth != 10;
    /// <summary>Company networks (a user name and a password, certificates): Windows' own page handles those.</summary>
    public bool Enterprise => Secured && Auth is 3 or 6 or 8 or 11;
    /// <summary>0..4 bars.</summary>
    public int Bars => Signal >= 80 ? 4 : Signal >= 60 ? 3 : Signal >= 35 ? 2 : Signal > 0 ? 1 : 0;
}

/// <summary>
/// Wi-Fi networks through Windows' own WLAN service (the same one Settings uses): scan, list, join (with a password
/// for a new network: Windows keeps it, in its own protected store, exactly like Settings), leave and forget.
/// Every call blocks a little, so callers run them off the screen's thread. Windows 11 asks for location access before
/// it gives apps the names of nearby networks: <see cref="NeedsLocation"/> says so, and the sheet offers the switch.
/// </summary>
public static class WifiNetworks
{
    public enum JoinResult { Joined, WrongPassword, Failed, NeedsSettings }

    /// <summary>Windows refused the list (location access is off for desktop apps).</summary>
    public static bool NeedsLocation { get; private set; }

    static IntPtr _client;
    static readonly object Gate = new();

    static IntPtr Client()
    {
        if (_client != IntPtr.Zero) return _client;
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return IntPtr.Zero;
        return _client = h;
    }

    static Guid? Interface()
    {
        var c = Client();
        if (c == IntPtr.Zero || WlanEnumInterfaces(c, IntPtr.Zero, out var list) != 0) return null;
        try
        {
            int n = Marshal.ReadInt32(list, 0);
            const int infoSize = 16 + 512 + 4;
            Guid? first = null;
            for (int i = 0; i < n; i++)
            {
                IntPtr info = list + 8 + i * infoSize;
                var g = Marshal.PtrToStructure<Guid>(info);
                first ??= g;
                if (Marshal.ReadInt32(info, 16 + 512) == 1) return g; // the connected one first
            }
            return first;
        }
        finally { WlanFreeMemory(list); }
    }

    /// <summary>Asks the adapter to look around (it answers within about 2–4 seconds; the list is ready right away with what it already knows).</summary>
    public static void Scan()
    {
        lock (Gate)
        {
            var c = Client();
            if (c == IntPtr.Zero || Interface() is not { } g) return;
            try { WlanScan(c, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); } catch { }
        }
    }

    /// <summary>Networks in range: the connected one first, then saved ones, then by signal. One entry per name.</summary>
    public static List<WifiNet> List()
    {
        lock (Gate)
        {
            var result = new List<WifiNet>();
            var c = Client();
            if (c == IntPtr.Zero || Interface() is not { } g) return result;
            int err = WlanGetAvailableNetworkList(c, ref g, 0x3, IntPtr.Zero, out var list);
            NeedsLocation = err == 5; // ERROR_ACCESS_DENIED: location is off for desktop apps
            if (err != 0) return result;
            try
            {
                int n = Marshal.ReadInt32(list, 0);
                const int size = 628;
                var byName = new Dictionary<string, WifiNet>();
                for (int i = 0; i < n; i++)
                {
                    IntPtr e = list + 8 + i * size;
                    string profile = Marshal.PtrToStringUni(e) ?? "";
                    int len = Math.Clamp(Marshal.ReadInt32(e, 512), 0, 32);
                    var bytes = new byte[len];
                    Marshal.Copy(e + 516, bytes, 0, len);
                    string ssid = DecodeSsid(bytes);
                    if (ssid.Length == 0 || ssid.All(ch => ch == '\0')) continue; // hidden network
                    int bss = Marshal.ReadInt32(e, 548);
                    if (bss != 1) continue; // infrastructure only (no ad hoc)
                    bool connectable = Marshal.ReadInt32(e, 556) != 0;
                    int signal = Marshal.ReadInt32(e, 604);
                    bool secured = Marshal.ReadInt32(e, 608) != 0;
                    int auth = Marshal.ReadInt32(e, 612), cipher = Marshal.ReadInt32(e, 616), flags = Marshal.ReadInt32(e, 620);
                    bool connected = (flags & 1) != 0, saved = (flags & 2) != 0 && profile.Length > 0;
                    var net = new WifiNet(ssid, signal, secured, connected, saved, saved ? profile : null, auth, cipher, connectable);
                    if (byName.TryGetValue(ssid, out var had))
                        net = had with
                        {
                            Signal = Math.Max(had.Signal, signal), Connected = had.Connected || connected, Saved = had.Saved || saved,
                            ProfileName = had.ProfileName ?? net.ProfileName, Connectable = had.Connectable || connectable,
                        };
                    byName[ssid] = net;
                }
                result = byName.Values.OrderByDescending(x => x.Connected).ThenByDescending(x => x.Saved && x.Signal > 25).ThenByDescending(x => x.Signal).ToList();
            }
            finally { WlanFreeMemory(list); }
            return result;
        }
    }

    /// <summary>
    /// The networks Windows has saved (no location needed): what the sheet lists when Windows won't name the
    /// networks around. The one it's on is marked connected.
    /// </summary>
    public static List<WifiNet> Saved()
    {
        lock (Gate)
        {
            var result = new List<WifiNet>();
            var c = Client();
            if (c == IntPtr.Zero || Interface() is not { } g) return result;
            if (WlanGetProfileList(c, ref g, IntPtr.Zero, out var list) != 0) return result;
            string? on = null;
            try
            {
                if (WlanQueryInterface(c, ref g, 7, IntPtr.Zero, out uint size, out var conn, IntPtr.Zero) == 0 && conn != IntPtr.Zero)
                    try { if (size >= 520 && Marshal.ReadInt32(conn) == 1) on = Marshal.PtrToStringUni(conn + 8); } finally { WlanFreeMemory(conn); }
            }
            catch { }
            on ??= CurrentName();
            try
            {
                int n = Marshal.ReadInt32(list, 0);
                for (int i = 0; i < n; i++)
                {
                    IntPtr e = list + 8 + i * 516;
                    string name = Marshal.PtrToStringUni(e) ?? "";
                    if (name.Length == 0) continue;
                    result.Add(new WifiNet(name, -1, true, name == on, true, name, 7, 4, true));
                }
            }
            finally { WlanFreeMemory(list); }
            return result.OrderByDescending(x => x.Connected).ThenBy(x => x.Ssid, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    static string DecodeSsid(byte[] b)
    {
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch { return Encoding.Latin1.GetString(b); }
    }

    /// <summary>
    /// Joins a network: a saved one by its profile; a new one with a profile made for it (and the password, given to
    /// Windows). Waits for the outcome (up to ~20 s). A new profile that doesn't work is removed again.
    /// </summary>
    public static JoinResult Join(WifiNet net, string? password, CancellationToken ct)
    {
        Guid g;
        IntPtr c;
        string profile;
        bool made = false;
        lock (Gate)
        {
            c = Client();
            if (c == IntPtr.Zero || Interface() is not { } gg) return JoinResult.Failed;
            g = gg;
            if (net.Saved && net.ProfileName != null && password == null) profile = net.ProfileName;
            else
            {
                if (net.Enterprise || !net.PasswordWorks) return JoinResult.NeedsSettings;
                string? xml = ProfileXml(net, password);
                if (xml == null) return JoinResult.NeedsSettings;
                int r = WlanSetProfile(c, ref g, 0, xml, null, true, IntPtr.Zero, out int reason);
                if (r != 0) { AppLog.Write($"wifi: profile refused ({r}, {reason})"); return r == 1206 /* bad profile */ ? JoinResult.WrongPassword : JoinResult.Failed; }
                profile = net.Ssid;
                made = true;
            }
            var p = new ConnectionParameters { Mode = 0 /* profile */, Profile = profile, BssType = 1 };
            int cr = WlanConnect(c, ref g, ref p, IntPtr.Zero);
            if (cr != 0) { AppLog.Write($"wifi: connect refused ({cr})"); if (made) Forget(net.Ssid); return JoinResult.Failed; }
        }
        // the outcome: connected to it, or back to disconnected after trying
        var start = DateTime.UtcNow;
        bool tried = false;
        while ((DateTime.UtcNow - start).TotalSeconds < 22 && !ct.IsCancellationRequested)
        {
            Thread.Sleep(300);
            var (state, ssid) = State(c, g);
            if (state == 1 && (ssid == net.Ssid || ssid == profile)) return JoinResult.Joined;
            if (state is 5 or 6 or 7) tried = true; // associating, discovering, authenticating
            if (state == 4 /* disconnected */ && (tried || (DateTime.UtcNow - start).TotalSeconds > 6))
            {
                if (made) Forget(net.Ssid);
                return net.Secured ? JoinResult.WrongPassword : JoinResult.Failed;
            }
            if (state == 1 && ssid != net.Ssid && ssid != profile && (DateTime.UtcNow - start).TotalSeconds > 8)
            {
                // Windows fell back to the previous network
                if (made) Forget(net.Ssid);
                return net.Secured ? JoinResult.WrongPassword : JoinResult.Failed;
            }
        }
        if (made && !ct.IsCancellationRequested) Forget(net.Ssid);
        return JoinResult.Failed;
    }

    /// <summary>The interface's state and the network it's on.</summary>
    static (int State, string? Ssid) State(IntPtr c, Guid g)
    {
        lock (Gate)
        {
            if (WlanQueryInterface(c, ref g, 6 /* interface_state */, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0 || data == IntPtr.Zero) return (0, null);
            int state;
            try { state = Marshal.ReadInt32(data); } finally { WlanFreeMemory(data); }
            if (state != 1) return (state, null);
            if (WlanQueryInterface(c, ref g, 7 /* current_connection */, IntPtr.Zero, out uint size, out var conn, IntPtr.Zero) != 0 || conn == IntPtr.Zero) return (state, CurrentName());
            try
            {
                if (size < 560) return (state, null);
                int len = Math.Clamp(Marshal.ReadInt32(conn, 520), 0, 32);
                var b = new byte[len];
                Marshal.Copy(conn + 524, b, 0, len);
                string ssid = DecodeSsid(b);
                // without location access Windows may blank the name: the profile's name stands in
                return (state, ssid.Trim(' ').Length > 0 ? ssid : Marshal.PtrToStringUni(conn + 8) is { Length: > 0 } pn ? pn : CurrentName());
            }
            finally { WlanFreeMemory(conn); }
        }
    }

    /// <summary>The Wi-Fi network Windows says it's on (its network list's name; readable without location access).</summary>
    public static string? CurrentName()
    {
        try
        {
            foreach (var p in Windows.Networking.Connectivity.NetworkInformation.GetConnectionProfiles())
                if (p.IsWlanConnectionProfile && p.GetNetworkConnectivityLevel() != Windows.Networking.Connectivity.NetworkConnectivityLevel.None)
                    return p.WlanConnectionProfileDetails?.GetConnectedSsid() is { Length: > 0 } ssid ? ssid : p.ProfileName;
        }
        catch { }
        return null;
    }

    public static void Disconnect()
    {
        lock (Gate)
        {
            var c = Client();
            if (c == IntPtr.Zero || Interface() is not { } g) return;
            WlanDisconnect(c, ref g, IntPtr.Zero);
        }
    }

    /// <summary>Removes the saved network (Windows forgets its password too).</summary>
    public static bool Forget(string profile)
    {
        lock (Gate)
        {
            var c = Client();
            if (c == IntPtr.Zero || Interface() is not { } g) return false;
            return WlanDeleteProfile(c, ref g, profile, IntPtr.Zero) == 0;
        }
    }

    /// <summary>Windows' profile for a new network (the password goes into Windows' store, never into Pixo's files).</summary>
    public static string? ProfileXml(WifiNet net, string? password)
    {
        string auth, enc;
        if (!net.Secured) { auth = "open"; enc = "none"; }
        else
        {
            (auth, enc) = net.Auth switch
            {
                4 => ("WPAPSK", net.Cipher == 2 ? "TKIP" : "AES"),
                7 => ("WPA2PSK", net.Cipher == 2 ? "TKIP" : "AES"),
                9 => ("WPA3SAE", "AES"),
                2 => ("open", "WEP"),
                10 => ("OWE", "AES"),
                1 when net.Cipher is 1 or 5 => ("open", "WEP"),
                _ => ("", ""),
            };
            if (auth.Length == 0 || net.NeedsPassword && string.IsNullOrEmpty(password)) return null;
        }
        string hex = Convert.ToHexString(Encoding.UTF8.GetBytes(net.Ssid));
        string name = SecurityElement.Escape(net.Ssid) ?? "";
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">");
        sb.Append("<name>").Append(name).Append("</name>");
        sb.Append("<SSIDConfig><SSID><hex>").Append(hex).Append("</hex><name>").Append(name).Append("</name></SSID></SSIDConfig>");
        sb.Append("<connectionType>ESS</connectionType><connectionMode>auto</connectionMode>");
        sb.Append("<MSM><security><authEncryption><authentication>").Append(auth).Append("</authentication><encryption>").Append(enc)
          .Append("</encryption><useOneX>false</useOneX>");
        if (auth == "WPA3SAE") sb.Append("<transitionMode xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v4\">true</transitionMode>");
        sb.Append("</authEncryption>");
        if (net.NeedsPassword)
            sb.Append("<sharedKey><keyType>").Append(enc == "WEP" ? "networkKey" : "passPhrase").Append("</keyType><protected>false</protected><keyMaterial>")
              .Append(SecurityElement.Escape(password)).Append("</keyMaterial></sharedKey>");
        sb.Append("</security></MSM></WLANProfile>");
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ConnectionParameters
    {
        public int Mode;
        [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
        public IntPtr Ssid, BssidList;
        public int BssType;
        public int Flags;
    }

    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, uint flags, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanSetProfile(IntPtr handle, ref Guid iface, uint flags, string xml, string? security, bool overwrite, IntPtr reserved, out int reason);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanDeleteProfile(IntPtr handle, ref Guid iface, string name, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanConnect(IntPtr handle, ref Guid iface, ref ConnectionParameters p, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanDisconnect(IntPtr handle, ref Guid iface, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out uint size, out IntPtr data, IntPtr type);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
    [DllImport("wlanapi.dll")] static extern int WlanGetProfileList(IntPtr handle, ref Guid iface, IntPtr reserved, out IntPtr list);
}
