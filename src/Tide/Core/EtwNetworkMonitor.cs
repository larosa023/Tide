using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tide.Core;

public interface INetworkSink
{
    /// <summary>Called on the ETW thread for every send/receive. Must be cheap.</summary>
    void OnTraffic(uint pid, uint bytes, bool sent);

    /// <summary>Called when a process opens or accepts a TCP connection.</summary>
    void OnConnect(uint pid);
}

/// <summary>
/// Real-time consumer for the Microsoft-Windows-Kernel-Network ETW provider. This is the same data
/// source Resource Monitor uses: every TCP/UDP send and receive, tagged with the owning process.
/// Implemented directly on the Win32 ETW API so Tide has no third-party dependencies.
/// </summary>
public sealed class EtwNetworkMonitor : IDisposable
{
    private const string SessionName = "Tide-Network-Monitor";
    private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    // Win32 constants
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_VERBOSE = 5;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const int ERROR_ALREADY_EXISTS = 183;
    private const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;

    // EVENT_TRACE_PROPERTIES is 120 bytes on 64-bit Windows; the session name buffer follows it.
    private const int PropertiesSize = 120;
    private const int PropertiesTotal = PropertiesSize + 1024 * 2;

    // EVENT_TRACE_LOGFILEW layout on 64-bit Windows.
    private const int LogFileSize = 448;
    private const int LogFile_LoggerName = 8;
    private const int LogFile_ProcessTraceMode = 28;
    private const int LogFile_EventRecordCallback = 424;

    // EVENT_RECORD offsets on 64-bit Windows.
    private const int Rec_ProviderId = 24;
    private const int Rec_EventId = 40;
    private const int Rec_UserDataLength = 86;
    private const int Rec_UserData = 96;

    private readonly INetworkSink _sink;
    private readonly long _providerLo;
    private readonly long _providerHi;
    private ulong _session;
    private ulong _trace = INVALID_PROCESSTRACE_HANDLE;
    private Thread? _thread;
    private EventRecordCallback? _callback; // must stay referenced while the trace runs
    private IntPtr _logFile;
    private IntPtr _loggerName;
    private int _disposed;

    public EtwNetworkMonitor(INetworkSink sink)
    {
        _sink = sink;
        var bytes = KernelNetworkProvider.ToByteArray();
        _providerLo = BitConverter.ToInt64(bytes, 0);
        _providerHi = BitConverter.ToInt64(bytes, 8);
    }

    /// <summary>When true, traffic to and from 127.0.0.0/8 and ::1 is ignored.</summary>
    public volatile bool IgnoreLoopback = true;

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Raised (on the ETW thread) if the trace stops unexpectedly.</summary>
    public event Action<string>? Faulted;

    public void Start()
    {
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Tide requires 64-bit Windows.");

        StartSession();

        var provider = KernelNetworkProvider;
        int err = EnableTraceEx2(_session, ref provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
            TRACE_LEVEL_VERBOSE, ulong.MaxValue, 0, 0, IntPtr.Zero);
        if (err != 0)
        {
            StopSession();
            throw new Win32Exception(err, "Could not enable the Windows network trace provider.");
        }

        _callback = OnEventRecord;
        _loggerName = Marshal.StringToHGlobalUni(SessionName);
        _logFile = Marshal.AllocHGlobal(LogFileSize);
        Marshal.Copy(new byte[LogFileSize], 0, _logFile, LogFileSize);
        Marshal.WriteIntPtr(_logFile, LogFile_LoggerName, _loggerName);
        Marshal.WriteInt32(_logFile, LogFile_ProcessTraceMode,
            unchecked((int)(PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD)));
        Marshal.WriteIntPtr(_logFile, LogFile_EventRecordCallback, Marshal.GetFunctionPointerForDelegate(_callback));

        _trace = OpenTraceW(_logFile);
        if (_trace == INVALID_PROCESSTRACE_HANDLE)
        {
            int e = Marshal.GetLastWin32Error();
            StopSession();
            throw new Win32Exception(e, "Could not open the network trace.");
        }

        _thread = new Thread(ProcessLoop) { IsBackground = true, Name = "Tide ETW", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void ProcessLoop()
    {
        var handles = new[] { _trace };
        int err = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
        if (Volatile.Read(ref _disposed) == 0)
        {
            Log.Write($"ProcessTrace returned {err}");
            Faulted?.Invoke(new Win32Exception(err).Message);
        }
    }

    private void OnEventRecord(IntPtr record)
    {
        try
        {
            if (Marshal.ReadInt64(record, Rec_ProviderId) != _providerLo ||
                Marshal.ReadInt64(record, Rec_ProviderId + 8) != _providerHi)
                return;

            int id = (ushort)Marshal.ReadInt16(record, Rec_EventId);
            int len = (ushort)Marshal.ReadInt16(record, Rec_UserDataLength);
            IntPtr data = Marshal.ReadIntPtr(record, Rec_UserData);
            if (len < 8 || data == IntPtr.Zero) return;

            uint pid = (uint)Marshal.ReadInt32(data, 0);

            // Event IDs from the Microsoft-Windows-Kernel-Network manifest.
            // Every payload starts with PID (uint32) then size (uint32), followed by daddr, saddr.
            switch (id)
            {
                case 10: // TCP send IPv4
                case 42: // UDP send IPv4
                    if (IgnoreLoopback && IsLoopbackV4(data, len)) return;
                    _sink.OnTraffic(pid, (uint)Marshal.ReadInt32(data, 4), sent: true);
                    break;
                case 11: // TCP receive IPv4
                case 43: // UDP receive IPv4
                    if (IgnoreLoopback && IsLoopbackV4(data, len)) return;
                    _sink.OnTraffic(pid, (uint)Marshal.ReadInt32(data, 4), sent: false);
                    break;
                case 26: // TCP send IPv6
                case 58: // UDP send IPv6
                    if (IgnoreLoopback && IsLoopbackV6(data, len)) return;
                    _sink.OnTraffic(pid, (uint)Marshal.ReadInt32(data, 4), sent: true);
                    break;
                case 27: // TCP receive IPv6
                case 59: // UDP receive IPv6
                    if (IgnoreLoopback && IsLoopbackV6(data, len)) return;
                    _sink.OnTraffic(pid, (uint)Marshal.ReadInt32(data, 4), sent: false);
                    break;
                case 12: // TCP connect IPv4
                case 15: // TCP accept IPv4
                    if (IgnoreLoopback && IsLoopbackV4(data, len)) return;
                    _sink.OnConnect(pid);
                    break;
                case 28: // TCP connect IPv6
                case 31: // TCP accept IPv6
                    if (IgnoreLoopback && IsLoopbackV6(data, len)) return;
                    _sink.OnConnect(pid);
                    break;
            }
        }
        catch
        {
            // Never let an exception escape into native code.
        }
    }

    private static bool IsLoopbackV4(IntPtr data, int len)
    {
        if (len < 16) return false;
        // Addresses are in network byte order, so the first octet is the lowest byte.
        int daddr = Marshal.ReadInt32(data, 8);
        int saddr = Marshal.ReadInt32(data, 12);
        return (daddr & 0xFF) == 127 || (saddr & 0xFF) == 127;
    }

    private static bool IsLoopbackV6(IntPtr data, int len)
    {
        if (len < 40) return false;
        return IsV6Loopback(data, 8) || IsV6Loopback(data, 24);
    }

    private static bool IsV6Loopback(IntPtr data, int offset)
    {
        long hi = Marshal.ReadInt64(data, offset);
        long lo = Marshal.ReadInt64(data, offset + 8);
        if (hi != 0) return false;
        // ::1
        if (lo == unchecked((long)0x0100000000000000)) return true;
        // ::ffff:127.x.x.x (IPv4-mapped loopback)
        return (lo & 0x00000000FFFFFFFF) == 0xFFFF0000 && ((lo >> 32) & 0xFF) == 127;
    }

    private void StartSession()
    {
        IntPtr props = AllocProperties();
        try
        {
            int err = StartTraceW(out _session, SessionName, props);
            if (err == ERROR_ALREADY_EXISTS)
            {
                // A previous run didn't shut down cleanly; ETW sessions outlive their process.
                ControlTraceW(0, SessionName, props, EVENT_TRACE_CONTROL_STOP);
                Marshal.FreeHGlobal(props);
                props = AllocProperties();
                err = StartTraceW(out _session, SessionName, props);
            }
            if (err == 5)
                throw new UnauthorizedAccessException("Tide needs to run as administrator to read network activity.");
            if (err != 0)
                throw new Win32Exception(err, "Could not start the network trace session.");
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
    }

    private void StopSession()
    {
        IntPtr props = AllocProperties();
        try
        {
            ControlTraceW(_session, null, props, EVENT_TRACE_CONTROL_STOP);
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
    }

    /// <summary>Stops a session left behind by a crashed instance, if any.</summary>
    public static void StopOrphanedSession()
    {
        IntPtr props = AllocProperties();
        try
        {
            ControlTraceW(0, SessionName, props, EVENT_TRACE_CONTROL_STOP);
        }
        catch
        {
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
    }

    private static IntPtr AllocProperties()
    {
        IntPtr p = Marshal.AllocHGlobal(PropertiesTotal);
        Marshal.Copy(new byte[PropertiesTotal], 0, p, PropertiesTotal);
        Marshal.WriteInt32(p, 0, PropertiesTotal);                           // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                                        // Wnode.ClientContext = QPC
        Marshal.WriteInt32(p, 44, unchecked((int)WNODE_FLAG_TRACED_GUID));   // Wnode.Flags
        Marshal.WriteInt32(p, 48, 64);                                       // BufferSize (KB)
        Marshal.WriteInt32(p, 52, 4);                                        // MinimumBuffers
        Marshal.WriteInt32(p, 56, 64);                                       // MaximumBuffers
        Marshal.WriteInt32(p, 64, unchecked((int)EVENT_TRACE_REAL_TIME_MODE)); // LogFileMode
        Marshal.WriteInt32(p, 68, 1);                                        // FlushTimer (s)
        Marshal.WriteInt32(p, 116, PropertiesSize);                          // LoggerNameOffset
        return p;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            StopSession();
            if (_trace != INVALID_PROCESSTRACE_HANDLE)
                CloseTrace(_trace);
            _thread?.Join(2000);
        }
        catch (Exception ex)
        {
            Log.Write("ETW shutdown: " + ex.Message);
        }
        finally
        {
            if (_logFile != IntPtr.Zero) Marshal.FreeHGlobal(_logFile);
            if (_loggerName != IntPtr.Zero) Marshal.FreeHGlobal(_loggerName);
            _logFile = _loggerName = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern int EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong traceHandle);
}
