using System.Runtime.InteropServices;

namespace DynamicNotch.Services;

/// <summary>
/// The Wi-Fi link itself, from Windows' own WLAN service: its signal quality in % (finer than the 0–5 bars), its
/// generation (Wi-Fi 4, 5, 6, 6E/7) and the link rate it negotiated. Read-only, no SSID or location needed; on a PC
/// without Wi-Fi, with the WLAN service off, or where Windows won't say, it simply returns nothing.
/// </summary>
public static class WifiInfo
{
    public sealed record Link(int SignalPercent, string? Generation, int? RateMbps);

    public static Link? Current()
    {
        IntPtr client = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out client) != 0) return null;
            if (WlanEnumInterfaces(client, IntPtr.Zero, out list) != 0) return null;
            int n = Marshal.ReadInt32(list, 0);
            const int infoSize = 16 + 512 + 4; // GUID, description[256], state
            for (int i = 0; i < n; i++)
            {
                IntPtr info = list + 8 + i * infoSize;
                if (Marshal.ReadInt32(info, 16 + 512) != 1) continue; // wlan_interface_state_connected
                var guid = Marshal.PtrToStructure<Guid>(info);
                if (WlanQueryInterface(client, ref guid, 7 /* current_connection */, IntPtr.Zero, out uint size, out IntPtr data, IntPtr.Zero) != 0 || data == IntPtr.Zero)
                    continue;
                try
                {
                    if (size < 588) continue;
                    // WLAN_CONNECTION_ATTRIBUTES: state, mode, profile name[256], then the association's
                    // SSID (36), BSS type (4), BSSID (6 + 2 padding), PHY type, PHY index, signal quality, rx rate, tx rate
                    const int assoc = 8 + 512;
                    int phy = Marshal.ReadInt32(data, assoc + 48);
                    int quality = Marshal.ReadInt32(data, assoc + 56);
                    uint rx = (uint)Marshal.ReadInt32(data, assoc + 60), tx = (uint)Marshal.ReadInt32(data, assoc + 64); // kbps
                    if (quality is < 0 or > 100) continue;
                    uint rate = Math.Max(rx, tx);
                    return new Link(quality, Generation(phy), rate is > 0 and < 50_000_000 ? (int)(rate / 1000) : null);
                }
                finally { WlanFreeMemory(data); }
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    // DOT11_PHY_TYPE: ht = 802.11n, vht = ac, he = ax, eht = be
    static string? Generation(int phy) => phy switch
    {
        7 => "Wi-Fi 4", 8 => "Wi-Fi 5", 10 => "Wi-Fi 6", 11 => "Wi-Fi 7", 4 or 6 => "Wi-Fi", _ => null,
    };

    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr client);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr client, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr client, ref Guid iface, int opcode, IntPtr reserved, out uint size, out IntPtr data, IntPtr valueType);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr memory);
}
