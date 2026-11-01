using System.Runtime.InteropServices;

namespace Aibell.Platform.Windows.Native;

internal static partial class NativeMethods
{
    public const uint WmCommand = 0x0111;
    public const int ThumbButtonClicked = 0x1800;

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string name);

    [LibraryImport("ole32.dll")]
    public static unsafe partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("gdi32.dll")]
    public static unsafe partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte* bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        public uint IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    [LibraryImport("user32.dll")]
    public static partial nint CreateIconIndirect(in IconInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);
}

/// <summary>THUMBBUTTON from shobjidl_core.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ThumbButton
{
    public const uint MaskIcon = 0x2, MaskTooltip = 0x4, MaskFlags = 0x8;
    public const uint FlagEnabled = 0x0, FlagDisabled = 0x1;

    public uint Mask;
    public uint Id;
    public uint Bitmap;
    public nint Icon;
    public fixed char Tip[260];
    public uint Flags;
}

/// <summary>
/// <c>ITaskbarList3</c> called through its vtable (the same approach as the SMTC interop: no COM wrapper generation).
/// Slots follow <c>ITaskbarList</c> (3-7), <c>ITaskbarList2</c> (8) and <c>ITaskbarList3</c> (9-20). Every call must come
/// from the thread that created the object, which is the UI thread.
/// </summary>
internal sealed unsafe class TaskbarList : IDisposable
{
    private static readonly Guid ClassId = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly Guid InterfaceId = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");
    private const uint InProcServer = 1;
    private const int SetProgressValueSlot = 9, SetProgressStateSlot = 10, ThumbBarAddButtonsSlot = 15, ThumbBarUpdateButtonsSlot = 16;

    private nint instance;

    private TaskbarList(nint instance) => this.instance = instance;

    /// <summary>Creates and initializes the shell object, or returns null when the shell refuses.</summary>
    public static TaskbarList? Create()
    {
        if (NativeMethods.CoCreateInstance(ClassId, 0, InProcServer, InterfaceId, out nint created) < 0 || created == 0) return null;
        TaskbarList list = new(created);
        if (((delegate* unmanaged[Stdcall]<nint, int>)Slot(created, 3))(created) < 0)
        {
            list.Dispose();
            return null;
        }
        return list;
    }

    public int SetProgressState(nint window, uint flags) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Slot(instance, SetProgressStateSlot))(instance, window, flags);

    public int SetProgressValue(nint window, ulong completed, ulong total) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, ulong, ulong, int>)Slot(instance, SetProgressValueSlot))(instance, window, completed, total);

    public int AddButtons(nint window, ThumbButton[] buttons)
    {
        fixed (ThumbButton* first = buttons)
            return ((delegate* unmanaged[Stdcall]<nint, nint, uint, ThumbButton*, int>)Slot(instance, ThumbBarAddButtonsSlot))(instance, window, (uint)buttons.Length, first);
    }

    public int UpdateButtons(nint window, ThumbButton[] buttons)
    {
        fixed (ThumbButton* first = buttons)
            return ((delegate* unmanaged[Stdcall]<nint, nint, uint, ThumbButton*, int>)Slot(instance, ThumbBarUpdateButtonsSlot))(instance, window, (uint)buttons.Length, first);
    }

    private static nint Slot(nint comObject, int index) => (*(nint**)comObject)[index];

    public void Dispose()
    {
        nint released = Interlocked.Exchange(ref instance, 0);
        if (released != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(released, 2))(released);
    }
}
