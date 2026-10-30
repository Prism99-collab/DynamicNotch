using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch.Services;

/// <summary>
/// Knows when any app is using the microphone or camera (the same data behind Windows' privacy indicator).
/// Event driven: sleeps on a registry change notification instead of polling.
/// </summary>
public sealed class PrivacyService
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    readonly Dispatcher _ui;

    public bool MicInUse { get; private set; }
    public bool CamInUse { get; private set; }
    public string? MicApp { get; private set; }
    public string? CamApp { get; private set; }
    /// <summary>Which app holds the microphone: its exe path, or its package name for Store apps.</summary>
    public string? MicAppKey { get; private set; }
    /// <summary>Every app holding the microphone right now (an always-on one like NVIDIA Broadcast can sit beside a call).</summary>
    public IReadOnlyList<string> MicAppKeys { get; private set; } = Array.Empty<string>();
    public event Action? Changed;

    public PrivacyService(Dispatcher ui) => _ui = ui;

    public void Start() => new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "privacy" }.Start();

    void Loop()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Root);
            if (key == null) return;
            using var ev = new AutoResetEvent(false);
            Scan();
            while (true)
            {
                // LAST_SET | NAME
                int r = RegNotifyChangeKeyValue(key.Handle, true, 0x4 | 0x1, ev.SafeWaitHandle, true);
                if (r != 0) { Thread.Sleep(3000); Scan(); continue; }
                ev.WaitOne();
                Thread.Sleep(250); // writes come in bursts
                Scan();
            }
        }
        catch { }
    }

    void Scan()
    {
        var mics = Active("microphone");
        var cams = Active("webcam");
        bool mic = mics.Count > 0, cam = cams.Count > 0;
        string? micApp = mic ? mics[0].Name : null, micKey = mic ? mics[0].Key : null, camApp = cam ? cams[0].Name : null;
        var micKeys = mics.Select(m => m.Key).ToList();
        _ui.InvokeAsync(() =>
        {
            if (mic == MicInUse && cam == CamInUse && micApp == MicApp && camApp == CamApp && micKeys.SequenceEqual(MicAppKeys)) return;
            MicInUse = mic; CamInUse = cam; MicApp = micApp; CamApp = camApp; MicAppKey = micKey; MicAppKeys = micKeys;
            Changed?.Invoke();
        });
    }

    /// <summary>Every app using a capability right now: a display name and its key (exe path, or package name for Store apps).</summary>
    static List<(string Name, string Key)> Active(string capability)
    {
        var list = new List<(string Name, string Key)>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"{Root}\{capability}");
            if (k == null) return list;
            foreach (var name in k.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    using var np = k.OpenSubKey(name);
                    if (np == null) continue;
                    foreach (var exe in np.GetSubKeyNames())
                    {
                        using var e = np.OpenSubKey(exe);
                        string path = exe.Replace('#', '\\');
                        if (Active(e)) list.Add((Path.GetFileNameWithoutExtension(path), path));
                    }
                }
                else
                {
                    using var s = k.OpenSubKey(name);
                    if (Active(s)) list.Add((PrettyPackage(name), name));
                }
            }
        }
        catch { }
        return list;
    }

    static bool Active(RegistryKey? k) =>
        k != null && k.GetValue("LastUsedTimeStop") is long stop && stop == 0 && k.GetValue("LastUsedTimeStart") is long start && start != 0;

    static string PrettyPackage(string pkg)
    {
        string n = pkg.Split('_')[0];
        int dot = n.LastIndexOf('.');
        return dot >= 0 ? n[(dot + 1)..] : n;
    }
}
