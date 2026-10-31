using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DynamicNotch.Services;

/// <summary>
/// Plain Windows HID access (SetupAPI + hid.dll): find a device's interfaces by vendor / product / interface number
/// and HID usage, open them for asynchronous reads, and send output reports. No driver, nothing written that the
/// device's own software doesn't already send.
/// </summary>
static class Hid
{
    public sealed record Interface(string Path, ushort Vid, ushort Pid, int Mi, ushort UsagePage, ushort Usage, int InputLength, int OutputLength);

    /// <summary>The device's HID interfaces (only the ones matching VID / PID are opened to read their usage).</summary>
    public static List<Interface> Find(ushort vid, params ushort[] pids)
    {
        var list = new List<Interface>();
        HidD_GetHidGuid(out var guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x02 | 0x10 /* PRESENT | DEVICEINTERFACE */);
        if (set == new IntPtr(-1)) return list;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            string want = $"vid_{vid:x4}&pid_";
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    string lower = path.ToLowerInvariant();
                    int at = lower.IndexOf(want, StringComparison.Ordinal);
                    if (at < 0) continue;
                    if (!ushort.TryParse(lower.AsSpan(at + want.Length, 4), System.Globalization.NumberStyles.HexNumber, null, out ushort pid) || !pids.Contains(pid)) continue;
                    int mi = -1;
                    int miAt = lower.IndexOf("&mi_", StringComparison.Ordinal);
                    if (miAt >= 0) int.TryParse(lower.AsSpan(miAt + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out mi);
                    if (Caps(path) is { } caps) list.Add(new Interface(path, vid, pid, mi, caps.UsagePage, caps.Usage, caps.Input, caps.Output));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    static (ushort UsagePage, ushort Usage, int Input, int Output)? Caps(string path)
    {
        using var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // no access needed for capabilities
        if (h.IsInvalid) return null;
        if (!HidD_GetPreparsedData(h, out IntPtr pre)) return null;
        try
        {
            var caps = new byte[64];
            if (HidP_GetCaps(pre, caps) != 0x00110000 /* HIDP_STATUS_SUCCESS */) return null;
            return (BitConverter.ToUInt16(caps, 2), BitConverter.ToUInt16(caps, 0), BitConverter.ToUInt16(caps, 4), BitConverter.ToUInt16(caps, 6));
        }
        finally { HidD_FreePreparsedData(pre); }
    }

    /// <summary>Opens an interface for overlapped reads (and writes when possible). Null if Windows refuses.</summary>
    public static FileStream? Open(string path)
    {
        var h = CreateFile(path, 0x80000000 | 0x40000000, 3, IntPtr.Zero, 3, 0x40000000 /* OVERLAPPED */, IntPtr.Zero);
        if (h.IsInvalid)
        {
            h.Dispose();
            h = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero); // read only (input reports)
            if (h.IsInvalid) { h.Dispose(); return null; }
        }
        try { return new FileStream(h, FileAccess.ReadWrite, 0, isAsync: true); }
        catch
        {
            try { return new FileStream(h, FileAccess.Read, 0, isAsync: true); }
            catch { h.Dispose(); return null; }
        }
    }

    /// <summary>Sends an output report through the HID class driver (works whatever the endpoint layout).</summary>
    public static bool SetOutputReport(FileStream stream, byte[] report) => HidD_SetOutputReport(stream.SafeFileHandle, report, report.Length);

    // ───────────── interop ─────────────

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, byte[] caps);
    [DllImport("hid.dll")] static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] report, int length);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
