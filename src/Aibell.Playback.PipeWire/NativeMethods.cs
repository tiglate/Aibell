using System.Runtime.InteropServices;

namespace Aibell.Playback.PipeWire;

/// <summary>
/// The PipeWire library bindings (<c>libpipewire-0.3.so.0</c>) used by <see cref="PipeWireStream"/>.
/// </summary>
internal static partial class NativeMethods
{
    public const string PipeWire = "libpipewire-0.3.so.0";

    public const uint PwVersionStreamEvents = 2;
    public const int PwDirectionOutput = 1;
    public const uint PwIdAny = 0xffffffffu;

    public const uint PwStreamFlagAutoconnect = 1 << 0;
    public const uint PwStreamFlagMapBuffers = 1 << 2;
    public const uint PwStreamFlagRtProcess = 1 << 3;

    public const int PwStreamStateError = -1;
    public const int PwStreamStateUnconnected = 0;
    public const int PwStreamStateConnecting = 1;
    public const int PwStreamStatePaused = 2;
    public const int PwStreamStateStreaming = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct PwStreamEvents
    {
        public uint Version;
        public nint Destroy;
        public nint StateChanged;
        public nint ControlInfo;
        public nint IoChanged;
        public nint ParamChanged;
        public nint AddBuffer;
        public nint RemoveBuffer;
        public nint Process;
        public nint Drained;
        public nint Command;
        public nint TriggerDone;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpaChunk
    {
        public uint Offset;
        public uint Size;
        public int Stride;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SpaData
    {
        public uint Type;
        public uint Flags;
        public long Fd;
        public uint MapOffset;
        public uint MaxSize;
        public nint Data;
        public SpaChunk* Chunk;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SpaBuffer
    {
        public uint NMetas;
        public uint NDatas;
        public nint Metas;
        public SpaData* Datas;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct PwBuffer
    {
        public SpaBuffer* Buffer;
        public nint UserData;
        public ulong Requested;
        public ulong Time;
    }

    [LibraryImport(PipeWire, EntryPoint = "pw_init")]
    public static partial void PwInit(nint argc, nint argv);

    [LibraryImport(PipeWire, EntryPoint = "pw_thread_loop_new", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PwThreadLoopNew(string name, nint props);

    [LibraryImport(PipeWire, EntryPoint = "pw_thread_loop_get_loop")]
    public static partial nint PwThreadLoopGetLoop(nint loop);

    [LibraryImport(PipeWire, EntryPoint = "pw_thread_loop_start")]
    public static partial int PwThreadLoopStart(nint loop);

    [LibraryImport(PipeWire, EntryPoint = "pw_thread_loop_stop")]
    public static partial void PwThreadLoopStop(nint loop);

    [LibraryImport(PipeWire, EntryPoint = "pw_thread_loop_destroy")]
    public static partial void PwThreadLoopDestroy(nint loop);

    [LibraryImport(PipeWire, EntryPoint = "pw_thread_loop_lock")]
    public static partial void PwThreadLoopLock(nint loop);

    [LibraryImport(PipeWire, EntryPoint = "pw_thread_loop_unlock")]
    public static partial void PwThreadLoopUnlock(nint loop);

    [LibraryImport(PipeWire, EntryPoint = "pw_properties_new_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PwPropertiesNewString(string args);

    [LibraryImport(PipeWire, EntryPoint = "pw_stream_new_simple", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PwStreamNewSimple(nint loop, string name, nint props, nint events, nint data);

    [LibraryImport(PipeWire, EntryPoint = "pw_stream_connect")]
    public static unsafe partial int PwStreamConnect(nint stream, int direction, uint targetId, uint flags, nint* parameters, uint nParams);

    [LibraryImport(PipeWire, EntryPoint = "pw_stream_dequeue_buffer")]
    public static unsafe partial PwBuffer* PwStreamDequeueBuffer(nint stream);

    [LibraryImport(PipeWire, EntryPoint = "pw_stream_queue_buffer")]
    public static unsafe partial int PwStreamQueueBuffer(nint stream, PwBuffer* buffer);

    [LibraryImport(PipeWire, EntryPoint = "pw_stream_set_active")]
    public static partial int PwStreamSetActive(nint stream, [MarshalAs(UnmanagedType.I1)] bool active);

    [LibraryImport(PipeWire, EntryPoint = "pw_stream_flush")]
    public static partial int PwStreamFlush(nint stream, [MarshalAs(UnmanagedType.I1)] bool drain);

    [LibraryImport(PipeWire, EntryPoint = "pw_stream_destroy")]
    public static partial void PwStreamDestroy(nint stream);
}
