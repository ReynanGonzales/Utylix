using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace IdmClone.Engine;

/// <summary>
/// Counts the frames each program shows, the way PresentMon does: Windows' own event tracing reports every "Present" of DirectX 9 / 10 / 11 / 12 programs
/// (provider Microsoft-Windows-DXGI and Microsoft-Windows-D3D9). Nothing is put into the game: no injection, so anti-cheat has nothing to see.
/// A trace session needs administrator rights (the guard has them); it only runs while someone is looking at the numbers.
/// </summary>
public sealed class FpsMeter : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");   // Microsoft-Windows-DXGI, event 42 = Present start
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");   // Microsoft-Windows-D3D9, event 1 = Present
    private const string SessionName = "UtylixFpsMeter";
    private const int WindowMs = 1500;

    private readonly object _lock = new();
    private readonly Dictionary<int, Queue<double>> _frames = new();     // per process: when its frames happened (milliseconds on the QPC clock)
    private static readonly long _qpcFrequency = QpcFrequency();
    private static long QpcFrequency() { QueryPerformanceFrequency(out long f); return f <= 0 ? 10_000_000 : f; }
    private long _session;
    private long _trace = -1;
    private Thread? _thread;
    private IntPtr _properties;
    private EventRecordCallback? _callback;        // (kept alive: ETW calls it from its own thread)

    /// <summary>"" while running or not started, otherwise why it could not start ("needs administrator rights", ...).</summary>
    public string Problem { get; private set; } = "";
    public bool Running => _thread != null && _thread.IsAlive;

    public bool Start()
    {
        if (Running) return true;
        Problem = "";
        _properties = BuildProperties();
        int rc = StartTrace(out _session, SessionName, _properties);
        if (rc == 183)                                  // (a session of an earlier run that did not end: stop it and try once more)
        {
            Marshal.FreeHGlobal(_properties);
            _properties = BuildProperties();
            ControlTrace(0, SessionName, _properties, 1);
            Marshal.FreeHGlobal(_properties);
            _properties = BuildProperties();
            rc = StartTrace(out _session, SessionName, _properties);
        }
        if (rc != 0)
        {
            Problem = rc == 5 ? "needs administrator rights" : "could not start (" + rc + ")";
            Marshal.FreeHGlobal(_properties); _properties = IntPtr.Zero;
            return false;
        }
        byte level = 5;
        if (EnableTraceEx2(_session, Dxgi, 1, level, ulong.MaxValue, 0, 0, IntPtr.Zero) != 0 | EnableTraceEx2(_session, D3D9, 1, level, ulong.MaxValue, 0, 0, IntPtr.Zero) != 0)
        { /* one provider missing is fine: the other still counts */ }
        _callback = OnEvent;
        var log = new EventTraceLogfile { LoggerName = SessionName, ProcessTraceMode = 0x00000100 | 0x10000000, EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback) };
        _trace = OpenTrace(ref log);
        if (_trace == -1 || _trace == unchecked((long)0xFFFFFFFF))
        {
            Problem = "could not open the trace";
            StopSession();
            return false;
        }
        long handle = _trace;
        _thread = new Thread(() => { var h = new[] { handle }; ProcessTrace(h, 1, IntPtr.Zero, IntPtr.Zero); }) { IsBackground = true, Name = "Utylix FPS meter" };
        _thread.Start();
        return true;
    }

    public void Stop()
    {
        if (_session == 0 && _trace == -1) return;
        StopSession();
        _thread?.Join(2000);
        _thread = null;
        lock (_lock) _frames.Clear();
    }

    private void StopSession()
    {
        if (_session != 0)
        {
            var p = BuildProperties();
            ControlTrace(_session, null, p, 1);
            Marshal.FreeHGlobal(p);
            _session = 0;
        }
        if (_trace != -1 && _trace != unchecked((long)0xFFFFFFFF)) { CloseTrace(_trace); }
        _trace = -1;
        if (_properties != IntPtr.Zero) { Marshal.FreeHGlobal(_properties); _properties = IntPtr.Zero; }
    }

    public void Dispose() => Stop();

    private void OnEvent(IntPtr record)
    {
        try
        {
            // EVENT_RECORD: ProcessId at 12, ProviderId at 24, EventDescriptor.Id at 40
            ushort id = (ushort)Marshal.ReadInt16(record, 40);
            if (id != 42 && id != 1) return;
            var provider = new byte[16];
            Marshal.Copy(record + 24, provider, 0, 16);
            var g = new Guid(provider);
            if (!((g == Dxgi && id == 42) || (g == D3D9 && id == 1))) return;
            int pid = Marshal.ReadInt32(record, 12);
            // the event's own time (QPC): Windows hands the events over in batches (about once a second), so the time of arrival says nothing
            double at = Marshal.ReadInt64(record, 16) * 1000.0 / _qpcFrequency;
            lock (_lock)
            {
                if (!_frames.TryGetValue(pid, out var q)) _frames[pid] = q = new Queue<double>();
                q.Enqueue(at);
                while (q.Count > 0 && at - q.Peek() > 4000) q.Dequeue();
            }
        }
        catch (Exception e) when (e is AccessViolationException or ArgumentException) { }
    }

    /// <summary>Frames per second of every program that showed frames lately (measured over the last 1.5 s of its own events).</summary>
    public Dictionary<int, int> Snapshot()
    {
        var result = new Dictionary<int, int>();
        QueryPerformanceCounter(out long counter);
        double nowMs = counter * 1000.0 / _qpcFrequency;
        lock (_lock)
        {
            foreach (var (pid, q) in _frames)
            {
                if (q.Count < 3) continue;
                double last = double.MinValue;
                foreach (double t in q) last = Math.Max(last, t);
                if (nowMs - last > 3500) continue;                  // (nothing for a while: not showing frames now)
                int n = 0; double first = last;
                foreach (double t in q) if (last - t <= WindowMs) { n++; first = Math.Min(first, t); }
                double span = last - first;
                if (n >= 3 && span > 50) result[pid] = (int)Math.Round((n - 1) * 1000.0 / span);
            }
        }
        return result;
    }

    // ---- EVENT_TRACE_PROPERTIES (x64: 120 bytes, the session name behind it) ----
    private static IntPtr BuildProperties()
    {
        int size = 120 + (SessionName.Length + 1) * 2 + 8;
        IntPtr p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, size);                 // Wnode.BufferSize
        Marshal.WriteInt32(p, 48, 16);                  // BufferSize in KB (small ones fill, and are delivered, sooner)
        Marshal.WriteInt32(p, 52, 4);                   // MinimumBuffers
        Marshal.WriteInt32(p, 56, 16);                  // MaximumBuffers
        Marshal.WriteInt32(p, 68, 1);                   // FlushTimer = 1 s: without it a quiet program's events wait for a full buffer (many seconds)
        Marshal.WriteInt32(p, 40, 1);                   // Wnode.ClientContext = QPC
        Marshal.WriteInt32(p, 44, 0x00020000);          // Wnode.Flags = WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 64, 0x00000100);          // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 116, 120);                // LoggerNameOffset
        return p;
    }

    private delegate void EventRecordCallback(IntPtr eventRecord);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EventTraceLogfile
    {
        public string? LogFileName;
        public string? LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        // EVENT_TRACE CurrentEvent (88 bytes)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 88)] public byte[] CurrentEvent;
        // TRACE_LOGFILE_HEADER (x64: 280 bytes)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 280)] public byte[] LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceFrequency(out long frequency);
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceCounter(out long counter);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int StartTrace(out long handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int ControlTrace(long handle, string? name, IntPtr properties, uint code);
    [DllImport("advapi32.dll")] private static extern int EnableTraceEx2(long handle, in Guid provider, uint controlCode, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern long OpenTrace(ref EventTraceLogfile logfile);
    [DllImport("advapi32.dll")] private static extern int ProcessTrace(long[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] private static extern int CloseTrace(long handle);
}
