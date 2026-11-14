using System.Runtime.InteropServices;

namespace DynamicNotch.Services;

/// <summary>
/// The call buttons that only the call app itself can do (camera, share, hang up, raise hand), done by pressing that
/// app's own keyboard shortcut in its window, at your click. Only for apps whose shortcuts are documented and stable:
/// Teams, Zoom, Google Meet (and Discord for deafen). Safety first:
///  • keys go only to the call's own window, once it's really in front (for Meet: only if the tab in front is Meet,
///    so a shortcut can never land in another tab);
///  • any key you're holding (Ctrl, Shift, Alt, Win) is let go first, so nothing combines by accident;
///  • turned off in Preferences → Calls, the page simply doesn't offer these buttons.
/// </summary>
public static class CallKeys
{
    public enum Act { Camera, Share, HangUp, Hand, Deafen, Chat }

    /// <summary>Which call app this is, as far as shortcuts go ("teams", "zoom", "meet", "discord"), or null.</summary>
    public static string? AppOf(string key, string name)
    {
        string k = (key + " " + name).ToLowerInvariant();
        if (k.Contains("teams")) return "teams";
        if (k.Contains("zoom")) return "zoom";
        if (k.Contains("discord")) return "discord";
        if (k.Contains("chrome") || k.Contains("msedge") || k.Contains("brave") || k.Contains("firefox") || k.Contains("opera") || k.Contains("vivaldi") || k.Contains("arc.exe"))
            return "meet"; // a call in a browser: Meet is the one with shortcuts (checked by the tab's title before any key)
        return null;
    }

    const ushort Ctrl = 0x11, Shift = 0x10, Alt = 0x12;

    /// <summary>The documented shortcut for an action in an app, or null when the app has none.</summary>
    public static (ushort[] Mods, ushort Key)? For(string? app, Act act) => (app, act) switch
    {
        ("teams", Act.Camera) => (new[] { Ctrl, Shift }, (ushort)'O'),
        ("teams", Act.Share) => (new[] { Ctrl, Shift }, (ushort)'E'),
        ("teams", Act.HangUp) => (new[] { Ctrl, Shift }, (ushort)'H'),
        ("teams", Act.Hand) => (new[] { Ctrl, Shift }, (ushort)'K'),
        ("zoom", Act.Camera) => (new[] { Alt }, (ushort)'V'),
        ("zoom", Act.Share) => (new[] { Alt }, (ushort)'S'),
        ("zoom", Act.HangUp) => (new[] { Alt }, (ushort)'Q'),
        ("zoom", Act.Hand) => (new[] { Alt }, (ushort)'Y'),
        ("meet", Act.Camera) => (new[] { Ctrl }, (ushort)'E'),
        ("meet", Act.Hand) => (new[] { Ctrl, Alt }, (ushort)'H'),
        ("discord", Act.Deafen) => (new[] { Ctrl, Shift }, (ushort)'D'),
        // the call's own chat panel (only where the app has a documented shortcut for it)
        ("zoom", Act.Chat) => (new[] { Alt }, (ushort)'H'),
        ("meet", Act.Chat) => (new[] { Ctrl, Alt }, (ushort)'C'),
        _ => null,
    };

    /// <summary>Presses the shortcut in the window (already brought to the front by the caller). False if it isn't in front.</summary>
    public static bool Press(IntPtr window, (ushort[] Mods, ushort Key) shortcut)
    {
        if (window == IntPtr.Zero || GetForegroundWindow() != window) return false;
        var inputs = new List<INPUT>();
        // let go of anything held, so the shortcut is exactly this one
        foreach (ushort held in new ushort[] { Ctrl, Shift, Alt, 0x5B, 0x5C })
            if ((GetAsyncKeyState(held) & 0x8000) != 0) inputs.Add(Key(held, up: true));
        foreach (var m in shortcut.Mods) inputs.Add(Key(m, up: false));
        inputs.Add(Key(shortcut.Key, up: false));
        inputs.Add(Key(shortcut.Key, up: true));
        for (int i = shortcut.Mods.Length - 1; i >= 0; i--) inputs.Add(Key(shortcut.Mods[i], up: true));
        var arr = inputs.ToArray();
        return SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>()) == arr.Length;
    }

    static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = up ? 0x0002u : 0u } },
    };

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
}
