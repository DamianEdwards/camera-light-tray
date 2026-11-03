using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Etw;

namespace CameraLightTray.Monitor;

internal sealed unsafe class EtwMonitor(Action<int, long, int, ulong, uint> transition, Action<string> invalidate) : IDisposable
{
    private static readonly Guid Provider = new("B8197C10-845F-40CA-82AB-9341E98CFC2B");
    private static readonly Guid Session = new("C61D473F-4614-4B5F-99C0-1523593CB456");
    private const string Name = "CameraLightTray.Camera.v1";
    private static EtwMonitor? _instance;
    private readonly Action<int, long, int, ulong, uint> _transition = transition;
    private readonly Action<string> _invalidate = invalidate;
    private SessionHandle? _session;
    private ConsumerHandle? _consumer;
    private uint _lost;
    private Task? _processing;
    private volatile bool _stopping;

    internal void Start(Action ready)
    {
        _instance = this;
        int size = sizeof(EVENT_TRACE_PROPERTIES) + (Name.Length + 1) * 2;
        byte* memory = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            var properties = Initialize(memory, size);
            fixed (char* name = Name)
            {
                new ReadOnlySpan<char>(name, Name.Length + 1).CopyTo(
                    new Span<char>(memory + sizeof(EVENT_TRACE_PROPERTIES), Name.Length + 1));
                uint error = (uint)PInvoke.StartTrace(out CONTROLTRACE_HANDLE handle, Name, ref *properties);
                if (error == 183)
                {
                    // SCM permits one service instance. Recover only this product's orphaned
                    // session after a crash, never an arbitrary logger with a colliding name.
                    error = (uint)PInvoke.ControlTrace(default, Name, ref *properties, EVENT_TRACE_CONTROL.EVENT_TRACE_CONTROL_QUERY);
                    if (error != 0 || properties->Wnode.Guid != Session)
                        throw new InvalidOperationException("Camera ETW session name is already in use with an unverifiable identity.");
                    error = (uint)PInvoke.ControlTrace(default, Name, ref *properties, EVENT_TRACE_CONTROL.EVENT_TRACE_CONTROL_STOP);
                    if (error != 0) throw new Win32Exception((int)error, "Cannot recover orphaned camera ETW session");
                    properties = Initialize(memory, size);
                    error = (uint)PInvoke.StartTrace(out handle, Name, ref *properties);
                }
                if (error != 0) throw new Win32Exception((int)error, "Cannot start camera ETW session");
                _session = new(handle);

                byte* filter = stackalloc byte[12];
                new Span<byte>(filter, 12).Clear();
                filter[0] = 1;
                *(ushort*)(filter + 2) = 4;
                ushort* ids = (ushort*)(filter + 4);
                ids[0] = 207; ids[1] = 209; ids[2] = 211; ids[3] = 213;
                EVENT_FILTER_DESCRIPTOR descriptor = new() { Ptr = (ulong)filter, Size = 12, Type = 0x80000200 };
                ENABLE_TRACE_PARAMETERS parameters = new() { Version = 2, EnableFilterDesc = &descriptor, FilterDescCount = 1 };
                Guid provider = Provider;
                error = (uint)PInvoke.EnableTraceEx2(handle, in provider, 1, 5, ulong.MaxValue, 0, 0, parameters);
                if (error != 0) throw new Win32Exception((int)error, "Camera provider access denied or event filtering unsupported");
                EVENT_TRACE_LOGFILEW log = new()
                {
                    LoggerName = name,
                    Anonymous1 = new() { ProcessTraceMode = 0x10000100 },
                    Anonymous2 = new() { EventRecordCallback = &OnEvent }
                };
                var consumer = PInvoke.OpenTrace(&log);
                if (consumer.Value == ulong.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot consume camera ETW session");
                _consumer = new(consumer);
            }
            ready();
            _processing = Task.Factory.StartNew(Process, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { Dispose(); throw; }
        finally { NativeMemory.Free(memory); }
    }

    private static EVENT_TRACE_PROPERTIES* Initialize(byte* memory, int size)
    {
        new Span<byte>(memory, size).Clear();
        var properties = (EVENT_TRACE_PROPERTIES*)memory;
        properties->Wnode.BufferSize = (uint)size;
        properties->Wnode.Guid = Session;
        properties->Wnode.Flags = 0x20000;
        properties->Wnode.ClientContext = 1;
        properties->LogFileMode = 0x100; // Real-time only; no ETL files or frame payloads.
        properties->BufferSize = 64;
        properties->MinimumBuffers = 4;
        properties->MaximumBuffers = 32;
        properties->FlushTimer = 1;
        properties->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
        Name.AsSpan().CopyTo(new Span<char>(memory + sizeof(EVENT_TRACE_PROPERTIES), Name.Length + 1));
        return properties;
    }

    private void Process()
    {
        var consumer = _consumer!.Value;
        uint error = (uint)PInvoke.ProcessTrace(&consumer, 1, null, null);
        if (!_stopping) _invalidate($"Camera ETW consumer stopped (error {error}); state unknown.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(EVENT_RECORD* record)
    {
        var instance = _instance;
        if (instance is null || record->EventHeader.ProviderId != Provider ||
            record->EventHeader.EventDescriptor.Id is not (207 or 209 or 211 or 213)) return;
        try
        {
            ulong engine = Property(record, "CaptureEnginePointer");
            uint result = checked((uint)Property(record, "hrStatus"));
            instance._transition((int)record->EventHeader.ProcessId, record->EventHeader.TimeStamp,
                record->EventHeader.EventDescriptor.Id, engine, result);
        }
        catch (Exception ex) { instance._invalidate("Unsupported camera event schema: " + ex.Message); }
    }

    private static ulong Property(EVENT_RECORD* record, string name)
    {
        fixed (char* text = name)
        {
            PROPERTY_DATA_DESCRIPTOR property = new() { PropertyName = (ulong)text, ArrayIndex = uint.MaxValue };
            uint size;
            uint error = PInvoke.TdhGetPropertySize(record, 0, null, 1, &property, &size);
            if (error != 0 || size is not (4 or 8)) throw new InvalidDataException($"Unsupported {name} field (error {error}).");
            ulong value = 0;
            error = PInvoke.TdhGetProperty(record, 0, null, 1, &property, size, (byte*)&value);
            if (error != 0) throw new Win32Exception((int)error);
            return value;
        }
    }

    internal void CheckLoss()
    {
        if (_session is null || _session.IsClosed) return;
        int size = sizeof(EVENT_TRACE_PROPERTIES) + (Name.Length + 1) * 2;
        byte* memory = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            var properties = (EVENT_TRACE_PROPERTIES*)memory;
            properties->Wnode.BufferSize = (uint)size;
            properties->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
            uint error = (uint)PInvoke.ControlTrace(_session.Value, null, ref *properties, EVENT_TRACE_CONTROL.EVENT_TRACE_CONTROL_QUERY);
            if (error != 0) { _invalidate($"ETW health query failed ({error})."); return; }
            uint lost = properties->EventsLost + properties->LogBuffersLost + properties->RealTimeBuffersLost;
            if (lost != _lost) { _lost = lost; _invalidate("Camera events were lost; state is unknown until helper restart and new observations."); }
        }
        finally { NativeMemory.Free(memory); }
    }

    public void Dispose()
    {
        _stopping = true;
        _consumer?.Dispose();
        _session?.Dispose();
        _instance = null;
    }

    private sealed class ConsumerHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal ConsumerHandle(PROCESSTRACE_HANDLE value) : base(true) => SetHandle((nint)value.Value);
        internal PROCESSTRACE_HANDLE Value => new((ulong)DangerousGetHandle());
        protected override bool ReleaseHandle()
        {
            var error = PInvoke.CloseTrace(new((ulong)handle));
            return error == 0 || (uint)error == 7007; // ProcessTrace completes an asynchronous close.
        }
    }
    private sealed class SessionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SessionHandle(CONTROLTRACE_HANDLE value) : base(true) => SetHandle((nint)value.Value);
        internal CONTROLTRACE_HANDLE Value => new((ulong)DangerousGetHandle());
        protected override bool ReleaseHandle()
        {
            byte* memory = stackalloc byte[512];
            new Span<byte>(memory, 512).Clear();
            var properties = (EVENT_TRACE_PROPERTIES*)memory;
            properties->Wnode.BufferSize = 512;
            properties->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
            return PInvoke.ControlTrace(new((ulong)handle), null, ref *properties, EVENT_TRACE_CONTROL.EVENT_TRACE_CONTROL_STOP) == 0;
        }
    }
}
