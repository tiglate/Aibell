using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aibell.Platform.Windows.Native;

/// <summary>
/// Native COM object implementing <c>TypedEventHandler&lt;SystemMediaTransportControls,
/// SystemMediaTransportControlsButtonPressedEventArgs&gt;</c>. It is free threaded (answers IAgileObject) because
/// SMTC raises the event from an RPC thread-pool thread. <see cref="Detach"/> makes late callbacks no-ops.
/// </summary>
internal sealed unsafe class ButtonPressedHandler
{
    private const int ENoInterface = unchecked((int)0x80004002);
    private static readonly Guid UnknownIid = new("00000000-0000-0000-c000-000000000046");
    private static readonly nint* Table = BuildTable();

    private readonly Target target;

    private ButtonPressedHandler(Target target) => this.target = target;

    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public nint* VTable;
        public int References;
        public nint Handle;
    }

    private sealed class Target(Action<TransportButton> callback)
    {
        public volatile Action<TransportButton>? Callback = callback;
    }

    /// <summary>Allocates the native object holding one reference owned by the caller.</summary>
    public static ButtonPressedHandler Create(Action<TransportButton> callback, out nint pointer)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Target target = new(callback);
        Instance* instance = (Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));
        instance->VTable = Table;
        instance->References = 1;
        instance->Handle = GCHandle.ToIntPtr(GCHandle.Alloc(target));
        pointer = (nint)instance;
        return new(target);
    }

    public void Detach() => target.Callback = null;

    public static int AddRef(nint pointer) => Add((Instance*)pointer);
    public static int Release(nint pointer) => Remove((Instance*)pointer);

    private static nint* BuildTable()
    {
        nint* table = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<Instance*, Guid*, nint*, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint>)&AddRefCallback;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint>)&ReleaseCallback;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<Instance*, nint, nint, int>)&Invoke;
        return table;
    }

    private static int Add(Instance* instance) => Interlocked.Increment(ref instance->References);

    private static int Remove(Instance* instance)
    {
        int remaining = Interlocked.Decrement(ref instance->References);
        if (remaining != 0) return remaining;
        GCHandle.FromIntPtr(instance->Handle).Free();
        NativeMemory.Free(instance);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(Instance* self, Guid* iid, nint* result)
    {
        if (iid is null || result is null) return unchecked((int)0x80004003);
        if (*iid == UnknownIid || *iid == SmtcSlots.ButtonPressedHandlerIid || *iid == SmtcSlots.AgileObjectIid)
        {
            Add(self);
            *result = (nint)self;
            return 0;
        }
        *result = 0;
        return ENoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRefCallback(Instance* self) => (uint)Add(self);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseCallback(Instance* self) => (uint)Remove(self);

    // sender and args are borrowed for the duration of the call; do not release them.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Invoke(Instance* self, nint sender, nint args)
    {
        try
        {
            if (args == 0 || GCHandle.FromIntPtr(self->Handle).Target is not Target { Callback: { } callback }) return 0;
            if (VTable.CallOut(args, SmtcSlots.ButtonPressedArgsGetButton, out int button) >= 0) callback((TransportButton)button);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Exceptions must never cross the native boundary; the button press is dropped.
            Debug.WriteLine($"Ignored media button callback failure: {exception.Message}");
        }
        return 0;
    }
}
