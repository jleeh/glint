using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glint.GameOverlay;

/// <summary>Where present events come from. The real one is an ETW session; tests inject a fake.</summary>
public interface IPresentSource : IDisposable
{
    bool Running { get; }
    /// <summary>"" when fine, else why there are no events (e.g. "needs admin").</summary>
    string Status { get; }
    /// <summary>Ticks per second of the timestamps passed to the callback.</summary>
    double Frequency { get; }
    long Now();
    /// <summary>Begin delivering (pid, timestamp, <see cref="PresentStream"/>) on a background thread.</summary>
    bool Start(Action<int, long, int> onPresent);
    void Stop();
}

/// <summary>
/// FPS without touching the game: a real-time ETW session (like PresentMon) with only the present events of
/// Microsoft-Windows-DXGI and Microsoft-Windows-DxgKrnl enabled, filtered by event id in the provider, so no DLL
/// is injected and no game memory is read (anti-cheat safe). Plain P/Invoke and fixed offsets: no TraceEvent,
/// no TDH, nothing parsed but the event header. Needs admin (Glint runs elevated).
/// The session only exists while the overlay is on screen; a session left behind by a crash is stopped at startup.
/// </summary>
public sealed class EtwPresentSource : IPresentSource
{
    public const string DefaultSessionName = "Glint-Overlay";
    static readonly Guid DxgiGuid = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid DxgKrnlGuid = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    static readonly long DxgiLo = Lo(DxgiGuid), DxgKrnlLo = Lo(DxgKrnlGuid);
    static long Lo(Guid g) => BitConverter.ToInt64(g.ToByteArray(), 0);

    readonly string _name;
    readonly object _lock = new();
    ulong _session, _trace = InvalidTrace;
    IntPtr _logfile, _loggerName;
    Thread? _thread;
    Action<int, long, int>? _cb;
    readonly EventRecordCallback _callback;   // kept alive for the native side
    long _events;

    public EtwPresentSource(string sessionName = DefaultSessionName) { _name = sessionName; _callback = OnEvent; }

    public bool Running { get; private set; }
    public string Status { get; private set; } = "";
    public double Frequency => Stopwatch.Frequency;   // ClientContext 1 + raw timestamps = QPC, same clock as Stopwatch
    public long Now() => Stopwatch.GetTimestamp();
    /// <summary>Events delivered since Start (all processes).</summary>
    public long Events => Interlocked.Read(ref _events);

    public bool Start(Action<int, long, int> onPresent)
    {
        lock (_lock)
        {
            if (Running) return true;
            _cb = onPresent;
            Interlocked.Exchange(ref _events, 0);
            StopSession(_name);                                  // a leftover from a crash would block StartTrace
            var props = Props(_name);
            try
            {
                int err = StartTraceW(out _session, _name, props);
                if (err != 0) { Status = err == 5 ? "ETW needs admin (run Glint elevated)" : $"StartTrace error {err}"; return false; }
            }
            finally { Marshal.FreeHGlobal(props); }
            int e1 = Enable(DxgiGuid, 4, 0x2, new ushort[] { 42 });              // DXGI keyword Events, Present::Start
            int e2 = Enable(DxgKrnlGuid, 4, 0x8000000, PresentStream.DxgKrnlIds); // DxgKrnl keyword Present
            if (e1 != 0 && e2 != 0) { Status = $"EnableTraceEx2 error {e1}/{e2}"; StopSession(_name); return false; }

            // EVENT_TRACE_LOGFILEW (x64, 448 bytes) written by offset: LoggerName @8, ProcessTraceMode @28, EventRecordCallback @424
            _logfile = Marshal.AllocHGlobal(448);
            Zero(_logfile, 448);
            _loggerName = Marshal.StringToHGlobalUni(_name);
            Marshal.WriteIntPtr(_logfile, 8, _loggerName);
            Marshal.WriteInt32(_logfile, 28, 0x100 | 0x1000 | 0x10000000);  // REAL_TIME | RAW_TIMESTAMP | EVENT_RECORD
            Marshal.WriteIntPtr(_logfile, 424, Marshal.GetFunctionPointerForDelegate(_callback));
            _trace = OpenTraceW(_logfile);
            if (_trace == InvalidTrace)
            {
                Status = $"OpenTrace error {Marshal.GetLastWin32Error()}";
                StopSession(_name); FreeLogfile(); return false;
            }
            ulong h = _trace;
            _thread = new Thread(() => { try { ProcessTrace(new[] { h }, 1, IntPtr.Zero, IntPtr.Zero); } catch { } })
                { IsBackground = true, Name = "Glint overlay ETW", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
            Running = true;
            Status = e1 != 0 ? "DXGI provider unavailable (DxgKrnl only)" : e2 != 0 ? "DxgKrnl provider unavailable (DXGI only)" : "";
            AppDomain.CurrentDomain.ProcessExit += OnExit;
            return true;
        }
    }

    void OnExit(object? s, EventArgs e) => Stop();

    public void Stop()
    {
        lock (_lock)
        {
            if (!Running) return;
            Running = false;
            AppDomain.CurrentDomain.ProcessExit -= OnExit;
            StopSession(_name);                     // ProcessTrace returns once the session is gone
            if (_trace != InvalidTrace) { CloseTrace(_trace); _trace = InvalidTrace; }
            // free the logfile block only once ProcessTrace has returned (leak it rather than risk a use-after-free)
            if (_thread == null || _thread.Join(3000)) FreeLogfile();
            _thread = null;
            _cb = null;
        }
    }

    public void Dispose() => Stop();

    void FreeLogfile()
    {
        if (_logfile != IntPtr.Zero) { Marshal.FreeHGlobal(_logfile); _logfile = IntPtr.Zero; }
        if (_loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerName); _loggerName = IntPtr.Zero; }
    }

    /// <summary>Stop a session by name (e.g. one a crashed Glint left running). Harmless if there is none.</summary>
    public static bool StopSession(string name = DefaultSessionName)
    {
        var p = Props(name);
        try { return ControlTraceW(0, name, p, 1 /* EVENT_TRACE_CONTROL_STOP */) == 0; }
        catch { return false; }
        finally { Marshal.FreeHGlobal(p); }
    }

    // EVENT_RECORD: EventHeader.ProcessId @12, TimeStamp @16, ProviderId @24, EventDescriptor.Id @40
    void OnEvent(IntPtr rec)
    {
        try
        {
            long lo = Marshal.ReadInt64(rec, 24);
            int id = (ushort)Marshal.ReadInt16(rec, 40);
            int stream;
            if (lo == DxgiLo) { if (id != 42) return; stream = PresentStream.Dxgi; }
            else if (lo == DxgKrnlLo) { if (Array.IndexOf(PresentStream.DxgKrnlIds, (ushort)id) < 0) return; stream = PresentStream.DxgKrnl(id); }
            else return;
            Interlocked.Increment(ref _events);
            _cb?.Invoke(Marshal.ReadInt32(rec, 12), Marshal.ReadInt64(rec, 16), stream);
        }
        catch { }
    }

    int Enable(Guid provider, byte level, ulong keywords, ushort[] ids)
    {
        // EVENT_FILTER_EVENT_ID { FilterIn, Reserved, Count, Events[] } → only these ids leave the provider
        int idsSize = 4 + 2 * ids.Length;
        IntPtr idf = Marshal.AllocHGlobal(idsSize), desc = Marshal.AllocHGlobal(16), prm = Marshal.AllocHGlobal(48);
        try
        {
            Marshal.WriteByte(idf, 0, 1); Marshal.WriteByte(idf, 1, 0); Marshal.WriteInt16(idf, 2, (short)ids.Length);
            for (int i = 0; i < ids.Length; i++) Marshal.WriteInt16(idf, 4 + 2 * i, (short)ids[i]);
            Marshal.WriteInt64(desc, 0, idf.ToInt64()); Marshal.WriteInt32(desc, 8, idsSize); Marshal.WriteInt32(desc, 12, unchecked((int)0x80000200));
            Zero(prm, 48);
            Marshal.WriteInt32(prm, 0, 2);                  // ENABLE_TRACE_PARAMETERS_VERSION_2
            Marshal.WriteIntPtr(prm, 32, desc);             // EnableFilterDesc
            Marshal.WriteInt32(prm, 40, 1);                 // FilterDescCount
            var g = provider;
            int err = EnableTraceEx2(_session, ref g, 1, level, keywords, 0, 0, prm);
            if (err != 0) err = EnableTraceEx2(_session, ref g, 1, level, keywords, 0, 0, IntPtr.Zero); // old OS: no id filter
            return err;
        }
        finally { Marshal.FreeHGlobal(idf); Marshal.FreeHGlobal(desc); Marshal.FreeHGlobal(prm); }
    }

    /// <summary>EVENT_TRACE_PROPERTIES (120 bytes on x64) + room for the session name.</summary>
    static IntPtr Props(string name)
    {
        const int size = 120 + 2 * 1024;
        var p = Marshal.AllocHGlobal(size);
        Zero(p, size);
        Marshal.WriteInt32(p, 0, size);           // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);             // Wnode.ClientContext = QPC timestamps
        Marshal.WriteInt32(p, 44, 0x00020000);    // Wnode.Flags = WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 64);            // BufferSize KB (DxgKrnl is busy: DWM and every app present too)
        Marshal.WriteInt32(p, 52, 4);             // MinimumBuffers
        Marshal.WriteInt32(p, 56, 32);            // MaximumBuffers (≤ 2 MB, only while the overlay is visible; small buffers drop events)
        Marshal.WriteInt32(p, 64, 0x100);         // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);             // FlushTimer = 1 s (deliver promptly at low event rates)
        Marshal.WriteInt32(p, 116, 120);          // LoggerNameOffset
        return p;
    }

    static void Zero(IntPtr p, int n) { for (int i = 0; i < n; i += 8) Marshal.WriteInt64(p, i, 0); }

    const ulong InvalidTrace = ulong.MaxValue;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void EventRecordCallback(IntPtr eventRecord);
    [DllImport("advapi32", CharSet = CharSet.Unicode)] static extern int StartTraceW(out ulong handle, string name, IntPtr props);
    [DllImport("advapi32", CharSet = CharSet.Unicode)] static extern int ControlTraceW(ulong handle, string? name, IntPtr props, uint code);
    [DllImport("advapi32")] static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, IntPtr parameters);
    [DllImport("advapi32", SetLastError = true)] static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32")] static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32")] static extern int CloseTrace(ulong handle);
}
