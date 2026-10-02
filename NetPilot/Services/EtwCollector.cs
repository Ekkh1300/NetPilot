using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NetPilot.Services;

/// <summary>
/// Real per-application byte counters read from the Windows NT Kernel Logger
/// (EVENT_TRACE_FLAG_NETWORK_TCPIP). Everything runs on a dedicated background
/// thread: Start() only opens the session, ProcessTrace() blocks on that thread
/// and the decoded callback hands (pid, downBytes, upBytes) to the caller.
///
/// Payload layout is taken from the Microsoft-Windows-Kernel-Network manifest:
///   { UInt32 PID, UInt32 size, daddr, saddr, dport, sport, ... }
/// so PID is always at offset 0 and the byte count at offset 4 for both IPv4
/// and IPv6 events.
/// </summary>
public sealed class EtwCollector : IDisposable
{
    private const string SessionName = "NT Kernel Logger";

    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_FLAG_NETWORK_TCPIP = 0x00010000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint ERROR_ALREADY_EXISTS = 183;

    private static readonly Guid SystemTraceControlGuid = new("9e814aad-3204-11d2-9a82-006008a86939");
    private static readonly ulong InvalidTraceHandle = unchecked((ulong)-1);

    private readonly Action<int, long, long> _onBytes;

    private IntPtr _props;              // EVENT_TRACE_PROPERTIES + trailing session name
    private IntPtr _namePtr;            // session name handed to OpenTraceW
    private ulong _session;             // handle returned by StartTraceW
    private ulong _trace = InvalidTraceHandle; // handle returned by OpenTraceW
    private Thread _thread;
    private EventRecordCallback _callback;      // kept alive for the native side
    private volatile bool _stopping;

    /// <summary>Raised once per matching event: (pid, downloadedBytes, uploadedBytes).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    public EtwCollector(Action<int, long, long> onBytes)
    {
        _onBytes = onBytes ?? throw new ArgumentNullException(nameof(onBytes));
    }

    /// <summary>Starts the real-time kernel session and the consuming thread. Never throws.</summary>
    public bool Start()
    {
        try
        {
            int structSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
            int allocSize = structSize + 1024;
            _props = Marshal.AllocHGlobal(allocSize);

            // Rebuilds the properties block in place. Needed between every native call:
            // StartTraceW copies the *existing* session's properties back into this buffer
            // when it returns ERROR_ALREADY_EXISTS, which overwrites LoggerNameOffset and
            // makes the follow-up stop fail with ERROR_INVALID_PARAMETER (so the retry
            // keeps returning 183). Stale loggers are common here because the app is
            // frequently killed while a system-wide session is still open.
            void FillProps()
            {
                ZeroMemory(_props, allocSize);

                var p = new EVENT_TRACE_PROPERTIES
                {
                    BufferSize = 64,          // KB per buffer
                    MinimumBuffers = 64,
                    MaximumBuffers = 256,
                    MaximumFileSize = 0,
                    LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
                    FlushTimer = 1,           // seconds; keeps latency low
                    EnableFlags = EVENT_TRACE_FLAG_NETWORK_TCPIP,
                    AgeLimit = -1,
                    LogFileNameOffset = 0,    // real-time only, no file
                    LoggerNameOffset = (uint)structSize,
                };
                p.Wnode.BufferSize = (uint)allocSize;
                p.Wnode.Flags = WNODE_FLAG_TRACED_GUID;
                p.Wnode.ClientContext = 1;              // QueryPerformanceCounter
                p.Wnode.Guid = SystemTraceControlGuid;
                Marshal.StructureToPtr(p, _props, false);

                byte[] name = Encoding.Unicode.GetBytes(SessionName + "\0");
                Marshal.Copy(name, 0, IntPtr.Add(_props, structSize), name.Length);
            }

            FillProps();
            uint stopStatus = 0;
            uint status = StartTraceW(out _session, SessionName, _props);
            if (status == ERROR_ALREADY_EXISTS)
            {
                // A stale session (ours after a kill, or another profiler) owns the single
                // system logger. Take it over so per-app usage still works.
                _session = 0;
                FillProps();
                stopStatus = StopTraceW(0, SessionName, _props);
                FillProps();
                StopTraceW(InvalidTraceHandle, SessionName, _props);
                FillProps();
                status = StartTraceW(out _session, SessionName, _props);
            }
            if (status != 0)
            {
                App.LogCrash(new System.ComponentModel.Win32Exception((int)status,
                    $"StartTraceW failed for '{SessionName}' (stop={stopStatus})"));
                Cleanup();
                return false;
            }

            _callback = OnEvent;
            var log = new EVENT_TRACE_LOGFILEW
            {
                ProcessTraceMode = PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_REAL_TIME,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback),
            };

            // Prefer attaching by session name; fall back to a nameless open if the
            // build of Windows rejects that combination.
            _namePtr = Marshal.StringToHGlobalUni(SessionName);
            log.LoggerName = _namePtr;
            _trace = OpenTraceW(ref log);

            if (_trace == InvalidTraceHandle)
            {
                log.LoggerName = IntPtr.Zero;
                log.ProcessTraceMode = PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_REAL_TIME;
                log.EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback);
                _trace = OpenTraceW(ref log);
            }

            if (_trace == InvalidTraceHandle)
            {
                App.LogCrash(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    "OpenTraceW failed"));
                StopSession();
                Cleanup();
                return false;
            }

            _stopping = false;
            _thread = new Thread(Consume)
            {
                IsBackground = true,
                Name = "NetPilot-ETW",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            Cleanup();
            return false;
        }
    }

    private void Consume()
    {
        try
        {
            var handles = new[] { _trace };
            // Blocks until the session is stopped (returns ERROR_CANCELLED = 1223).
            ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }

    private void OnEvent(IntPtr pEventRecord)
    {
        if (_stopping) return;
        try
        {
            var rec = Marshal.PtrToStructure<EVENT_RECORD>(pEventRecord);
            bool? up = Direction(rec.EventHeader.EventDescriptor.Id, rec.EventHeader.EventDescriptor.Opcode);
            if (up == null) return;

            if (rec.UserData == IntPtr.Zero || rec.UserDataLength < 8) return;
            uint pid = unchecked((uint)Marshal.ReadInt32(rec.UserData, 0));
            uint size = unchecked((uint)Marshal.ReadInt32(rec.UserData, 4));

            if (pid == 0 || pid > 0x003FFFFF) return;   // Windows PIDs stay under 4M
            if (size == 0 || size > 0x08000000) return; // reject implausible lengths

            if (up.Value) _onBytes((int)pid, 0, size);
            else _onBytes((int)pid, size, 0);
        }
        catch { /* never let a malformed event kill the ETW thread */ }
    }

    /// <summary>Maps Kernel-Network event ids/opcodes to a transfer direction.</summary>
    private static bool? Direction(ushort id, byte opcode)
    {
        if (id is 10 or 26 or 42 or 58 || opcode is 10 or 42) return true;   // sent  -> upload
        if (id is 11 or 27 or 43 or 59 || opcode is 11 or 43) return false;  // recv  -> download
        return null;                                                          // connect/retransmit/...
    }

    private void StopSession()
    {
        if (_session == 0) return;
        try { StopTraceW(_session, SessionName, _props); } catch { }
        _session = 0;
    }

    private void Cleanup()
    {
        bool threadStopped = true;
        if (_thread != null)
        {
            // ProcessTrace only returns once StopSession() has stopped the session. If it
            // still hasn't returned within the budget, leave every native resource alone:
            // the delegate whose function pointer OpenTraceW holds is rooted in _callback
            // and the buffers are still reachable from the live thread. Leaking them for
            // the rest of the process beats an AccessViolationException from a freed
            // buffer / collected delegate under a running ETW thread.
            try { threadStopped = _thread.Join(5000); } catch { threadStopped = false; }
            if (threadStopped) _thread = null;
        }

        if (!threadStopped) return;

        if (_trace != InvalidTraceHandle)
        {
            try { CloseTrace(_trace); } catch { }
            _trace = InvalidTraceHandle;
        }
        if (_namePtr != IntPtr.Zero) { Marshal.FreeHGlobal(_namePtr); _namePtr = IntPtr.Zero; }
        if (_props != IntPtr.Zero) { Marshal.FreeHGlobal(_props); _props = IntPtr.Zero; }
        _callback = null;
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;

        // Stopping the session makes ProcessTrace return, then the handle can be closed.
        StopSession();
        Cleanup();
    }

    private static void ZeroMemory(IntPtr ptr, int size)
    {
        for (int i = 0; i < size; i++) Marshal.WriteByte(ptr, i, 0);
    }

    // ------------------------------------------------------------------
    // Native structures (x64 layouts verified against evntrace.h)
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_HEADER
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE
    {
        public EVENT_TRACE_HEADER Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    /// <summary>TRACE_LOGFILE_HEADER occupies bytes 120..400; we never read it.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 280)]
    private struct TRACE_LOGFILE_HEADER
    {
        [FieldOffset(0)] public uint BufferSize;
    }

    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct EVENT_TRACE_LOGFILEW
    {
        [FieldOffset(0)] public IntPtr LogFileName;
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(16)] public long CurrentTime;
        [FieldOffset(24)] public uint BuffersRead;
        [FieldOffset(28)] public uint ProcessTraceMode;   // union with LogFileMode
        [FieldOffset(32)] public EVENT_TRACE CurrentEvent;
        [FieldOffset(120)] public TRACE_LOGFILE_HEADER LogfileHeader;
        [FieldOffset(400)] public IntPtr BufferCallback;
        [FieldOffset(408)] public uint BufferSize;
        [FieldOffset(412)] public uint Filled;
        [FieldOffset(416)] public uint EventsLost;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
        [FieldOffset(432)] public uint IsKernelTrace;
        [FieldOffset(440)] public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_HEADER
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ETW_BUFFER_CONTEXT
    {
        public ushort ProcessorIndex;
        public ushort LoggerId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public ETW_BUFFER_CONTEXT BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    // ------------------------------------------------------------------
    // Native entry points
    // ------------------------------------------------------------------

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW", SetLastError = true)]
    private static extern uint StartTraceW(out ulong traceHandle, string instanceName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StopTraceW", SetLastError = true)]
    private static extern uint StopTraceW(ulong traceHandle, string instanceName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenTraceW", SetLastError = true)]
    private static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILEW logfile);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint ProcessTrace([In] ulong[] handleArray, uint handleCount,
        IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint CloseTrace(ulong traceHandle);
}
