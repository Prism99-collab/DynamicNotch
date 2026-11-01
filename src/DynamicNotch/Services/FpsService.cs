using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DynamicNotch.Services;

/// <summary>
/// A game's real frame rate, live. Every frame any game shows goes through the graphics kernel (DirectX 9 to 12,
/// Vulkan and OpenGL alike), which reports it as an event in Windows' own tracing system: the kernel's "present"
/// event, filtered at the source so nothing else is delivered. Each frame's exact timestamp is kept, and the rate
/// is worked out from the real frame times of the last half second, as often as it's asked for.
/// (Windows versions without source filtering fall back to the DirectX-only events.)
/// The trace runs only while a game is in front. Reading these events needs the "Performance Log Users" group
/// (or administrator rights); <see cref="Available"/> says whether this account has it, <see cref="RequestAccess"/>
/// sets it up once.
/// </summary>
public sealed class FpsService : IDisposable
{
    const string SessionName = "DynamicNotch-Fps";
    static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");   // Microsoft-Windows-DXGI
    static readonly Guid D3d9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");   // Microsoft-Windows-D3D9
    static readonly Guid KernelProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D"); // Microsoft-Windows-DxgKrnl
    const ushort KernelPresent = 184;          // DxgKrnl Present_Info: one per frame, whatever the graphics API
    const ulong KernelPresentKeyword = 0x8000000;

    readonly object _gate = new();
    ulong _session, _trace;
    Thread? _thread;
    EventRecordCallback? _callback; // kept alive while native code holds it
    volatile int _pid;
    bool _kernel;              // counting the graphics kernel's presents (else DXGI / D3D9)

    // the timestamps (performance-counter ticks) of the latest frames, written by the trace thread only
    const int Ring = 2048;
    readonly long[] _times = new long[Ring];
    int _head;

    /// <summary>Could the last <see cref="Start"/> open a trace? null until tried.</summary>
    public bool? Available { get; private set; }

    /// <summary>Starts counting the frames of a process (stops any previous count).</summary>
    public bool Start(int pid)
    {
        lock (_gate)
        {
            if (_session != 0 && _pid == pid) return true; // already counting this game: keep its frames
            _pid = pid;
            Volatile.Write(ref _head, 0);
            if (_session != 0) return true;
            try
            {
                if (!OpenSession()) { Available = false; return false; }
                Available = true;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("fps: " + ex.Message);
                CloseSession();
                Available = false;
                return false;
            }
        }
    }

    /// <summary>
    /// The frame rate right now, from the real frame times of the last half second (longer when frames are rare).
    /// Cheap enough to ask many times a second. 0 when the game hasn't shown a frame for a while; null if not counting.
    /// </summary>
    public double? Read()
    {
        if (_session == 0) return null;
        int head = Volatile.Read(ref _head);
        if (head == 0) return 0;
        long freq = System.Diagnostics.Stopwatch.Frequency;
        long last = _times[(head - 1) & (Ring - 1)];
        // frames arrive in small batches; a game that stopped showing frames reads as 0
        if (System.Diagnostics.Stopwatch.GetTimestamp() - last > freq * 2) return 0;
        double rate = RateOver(head, last, freq / 2);
        return rate > 0 ? rate : RateOver(head, last, freq * 2);
    }

    double RateOver(int head, long last, long window)
    {
        int n = 1;
        long first = last;
        int available = Math.Min(head, Ring);
        for (int i = 1; i < available; i++)
        {
            long t = _times[(head - 1 - i) & (Ring - 1)];
            if (last - t > window) break;
            first = t;
            n++;
        }
        return n >= 3 && last > first ? (n - 1) * (double)System.Diagnostics.Stopwatch.Frequency / (last - first) : 0;
    }

    public void Stop()
    {
        lock (_gate) CloseSession();
        _pid = 0;
    }

    public void Dispose() => Stop();

    // ───────────── the trace ─────────────

    bool OpenSession()
    {
        // a session left over by a crash would block a new one: stop it first
        IntPtr stale = NewProperties();
        ControlTrace(0, SessionName, stale, 1 /* STOP */);
        Marshal.FreeHGlobal(stale);
        var props = NewProperties();
        int r = StartTrace(out _session, SessionName, props);
        Marshal.FreeHGlobal(props);
        if (r != 0) { _session = 0; if (r != 5) AppLog.Write($"fps: trace refused ({r})"); return false; } // 5 = access denied

        // the graphics kernel's present event alone (every API), filtered at the source
        _kernel = EnableKernelPresents();
        if (!_kernel)
        {
            var dxgi = DxgiProvider; var d3d9 = D3d9Provider;
            EnableTraceEx2(_session, ref dxgi, 1, 4, 0, 0, 0, IntPtr.Zero);
            EnableTraceEx2(_session, ref d3d9, 1, 4, 0, 0, 0, IntPtr.Zero);
        }

        _callback = OnEvent;
        var log = new EVENT_TRACE_LOGFILEW
        {
            LoggerName = Marshal.StringToHGlobalUni(SessionName),
            ProcessTraceMode = 0x100 | 0x10000000, // real time | event records
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback),
        };
        _trace = OpenTrace(ref log);
        Marshal.FreeHGlobal(log.LoggerName);
        if (_trace == ulong.MaxValue) { _trace = 0; CloseSession(); return false; }
        ulong handle = _trace;
        _thread = new Thread(() => { try { ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero); } catch { } })
            { IsBackground = true, Name = "fps trace", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        AppLog.Write($"fps: counting frames ({(_kernel ? "graphics kernel, every API" : "DirectX only")})");
        return true;
    }

    void CloseSession()
    {
        if (_trace != 0) { CloseTrace(_trace); _trace = 0; }
        if (_session != 0)
        {
            var props = NewProperties();
            ControlTrace(_session, null, props, 1 /* STOP */);
            Marshal.FreeHGlobal(props);
            _session = 0;
        }
        _thread = null;
    }

    /// <summary>Called by Windows for every event: a "present" from the game's process is one frame.</summary>
    void OnEvent(IntPtr record)
    {
        int pid = _pid;
        if (pid == 0 || Marshal.ReadInt32(record, 12) != pid) return;
        ushort id = (ushort)Marshal.ReadInt16(record, 40);
        var provider = Marshal.PtrToStructure<Guid>(record + 24);
        bool frame = _kernel
            ? provider == KernelProvider && id == KernelPresent
            // DXGI Present_Start (42) / PresentMultiplaneOverlay_Start (55); D3D9 Present_Start (1)
            : (provider == DxgiProvider && id is 42 or 55) || (provider == D3d9Provider && id == 1);
        if (!frame) return;
        long stamp = Marshal.ReadInt64(record, 16); // EVENT_HEADER.TimeStamp: performance-counter ticks (ClientContext 1)
        int head = _head;
        _times[head & (Ring - 1)] = stamp;
        Volatile.Write(ref _head, head + 1);
        OnFrame?.Invoke(stamp);
    }

    /// <summary>Every frame's timestamp as it's counted (the elevated helper passes them on to the notch).</summary>
    public Action<long>? OnFrame;

    /// <summary>Enables only the graphics kernel's present events (an event-ID filter, Windows 8.1 and later).</summary>
    bool EnableKernelPresents()
    {
        IntPtr ids = Marshal.AllocHGlobal(8), desc = Marshal.AllocHGlobal(16), prm = Marshal.AllocHGlobal(48);
        try
        {
            // EVENT_FILTER_EVENT_ID { FilterIn = true, Count = 1, Events = { 184 } }
            Marshal.WriteByte(ids, 0, 1); Marshal.WriteByte(ids, 1, 0); Marshal.WriteInt16(ids, 2, 1); Marshal.WriteInt16(ids, 4, (short)KernelPresent);
            // EVENT_FILTER_DESCRIPTOR { Ptr, Size, Type = EVENT_FILTER_TYPE_EVENT_ID }
            Marshal.WriteInt64(desc, 0, ids.ToInt64()); Marshal.WriteInt32(desc, 8, 6); Marshal.WriteInt32(desc, 12, unchecked((int)0x80000200));
            // ENABLE_TRACE_PARAMETERS { Version = 2, ..., EnableFilterDesc, FilterDescCount = 1 }
            for (int i = 0; i < 48; i++) Marshal.WriteByte(prm, i, 0);
            Marshal.WriteInt32(prm, 0, 2); Marshal.WriteInt64(prm, 32, desc.ToInt64()); Marshal.WriteInt32(prm, 40, 1);
            var k = KernelProvider;
            return EnableTraceEx2(_session, ref k, 1, 4, KernelPresentKeyword, 0, 0, prm) == 0;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(ids); Marshal.FreeHGlobal(desc); Marshal.FreeHGlobal(prm); }
    }

    static IntPtr NewProperties()
    {
        const int size = 120 + 2 * 256;
        IntPtr p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, size);          // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);            // Wnode.ClientContext: performance counter clock
        Marshal.WriteInt32(p, 44, 0x00020000);   // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 4);            // BufferSize (KB): small, so frames arrive within a fraction of a second
        Marshal.WriteInt32(p, 64, 0x00000100 | 0x00000010); // LogFileMode: REAL_TIME_MODE | USE_MS_FLUSH_TIMER
        Marshal.WriteInt32(p, 68, 50);           // FlushTimer: 50 ms, so frames arrive continuously, not in 1 s batches
        Marshal.WriteInt32(p, 116, 120);         // LoggerNameOffset
        return p;
    }

    // ───────────── one-time setup on PCs where the account can't trace yet ─────────────

    /// <summary>The account is in "Performance Log Users" (or runs elevated).</summary>
    public static bool AccountCanTrace()
    {
        try
        {
            var me = WindowsIdentity.GetCurrent();
            return me.Groups?.Any(g => g.Value == "S-1-5-32-559") == true || new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Adds this account to "Performance Log Users" (one administrator prompt). Windows applies it at the next
    /// sign-in. Returns false if the prompt was declined.
    /// </summary>
    public static bool RequestAccess()
    {
        try
        {
            string group = new SecurityIdentifier("S-1-5-32-559").Translate(typeof(NTAccount)).Value; // localized name
            if (group.Contains('\\')) group = group[(group.IndexOf('\\') + 1)..];
            string user = WindowsIdentity.GetCurrent().Name;
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("net.exe", $"localgroup \"{group}\" \"{user}\" /add")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(15000);
            return p != null && (p.ExitCode == 0 || p.ExitCode == 2); // 2: already a member
        }
        catch { return false; }
    }

    // ───────────── interop ─────────────

    delegate void EventRecordCallback(IntPtr record);

    [StructLayout(LayoutKind.Explicit, Size = 448)]
    struct EVENT_TRACE_LOGFILEW
    {
        [FieldOffset(0)] public IntPtr LogFileName;
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
        [FieldOffset(440)] public IntPtr Context;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int StartTrace(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int ControlTrace(ulong handle, string? name, IntPtr properties, uint code);
    [DllImport("advapi32.dll")] static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong anyKeyword, ulong allKeyword, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILEW logfile);
    [DllImport("advapi32.dll")] static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] static extern int CloseTrace(ulong handle);
}
