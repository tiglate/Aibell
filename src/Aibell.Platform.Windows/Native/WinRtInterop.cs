using System.Runtime.InteropServices;

namespace Aibell.Platform.Windows.Native;

internal static partial class NativeMethods
{
    public const uint WmApp = 0x8000;
    public const uint WsPopup = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    /// <summary>Copies <paramref name="size"/> bytes into a new reference-counted IStream (shlwapi). Returns 0 on failure.</summary>
    [LibraryImport("shlwapi.dll")]
    public static unsafe partial nint SHCreateMemStream(byte* initial, uint size);

    /// <summary>Wraps an IStream as a WinRT IRandomAccessStream (shcore). The IStream is AddRef'd, not consumed.</summary>
    [LibraryImport("shcore.dll")]
    public static partial int CreateRandomAccessStreamOverStream(nint stream, int options, in Guid iid, out nint result);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>The returned string is allocated with CoTaskMemAlloc and must be freed by the caller.</summary>
    [LibraryImport("shell32.dll")]
    public static partial int GetCurrentProcessExplicitAppUserModelID(out nint appId);

    [LibraryImport("combase.dll")]
    public static partial int RoInitialize(int initType);

    [LibraryImport("combase.dll")]
    public static partial void RoUninitialize();

    [LibraryImport("combase.dll")]
    public static partial int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);

    [LibraryImport("combase.dll")]
    public static unsafe partial int WindowsCreateString(char* source, uint length, out nint value);

    [LibraryImport("combase.dll")]
    public static partial int WindowsDeleteString(nint value);

    [LibraryImport("combase.dll")]
    public static partial nint WindowsGetStringRawBuffer(nint value, out uint length);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    public static partial int GetMessage(out Message message, nint window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}

/// <summary>Owned WinRT string handle (HSTRING). Windows Runtime APIs treat a null handle as the empty string.</summary>
internal readonly struct HString : IDisposable
{
    private readonly nint handle;
    private HString(nint handle) => this.handle = handle;
    public nint Handle => handle;

    public static unsafe HString Create(string value)
    {
        if (string.IsNullOrEmpty(value)) return new(0);
        nint result;
        fixed (char* source = value) Marshal.ThrowExceptionForHR(NativeMethods.WindowsCreateString(source, (uint)value.Length, out result));
        return new(result);
    }

    public static unsafe string ToManaged(nint handle)
    {
        if (handle == 0) return "";
        nint buffer = NativeMethods.WindowsGetStringRawBuffer(handle, out uint length);
        return buffer == 0 ? "" : new string((char*)buffer, 0, checked((int)length));
    }

    public static void Delete(nint handle)
    {
        if (handle != 0) _ = NativeMethods.WindowsDeleteString(handle);
    }

    public void Dispose()
    {
        if (handle != 0) _ = NativeMethods.WindowsDeleteString(handle);
    }
}

/// <summary>Raw COM vtable access. Slot numbers count IUnknown (0-2) and IInspectable (3-5) first.</summary>
internal static unsafe class VTable
{
    public static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    public static int Call(nint instance, int index) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(instance, index))(instance);

    public static int Call(nint instance, int index, int argument) =>
        ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(instance, index))(instance, argument);

    public static int Call(nint instance, int index, nint argument) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(instance, index))(instance, argument);

    public static int Call(nint instance, int index, byte argument) =>
        ((delegate* unmanaged[Stdcall]<nint, byte, int>)Slot(instance, index))(instance, argument);

    public static int CallOut(nint instance, int index, out nint result)
    {
        nint value = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, index))(instance, &value);
        result = value;
        return hr;
    }

    public static int CallOut(nint instance, int index, out int result)
    {
        int value = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(instance, index))(instance, &value);
        result = value;
        return hr;
    }

    public static int CallOut(nint instance, int index, out byte result)
    {
        byte value = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(instance, index))(instance, &value);
        result = value;
        return hr;
    }

    public static int CallOut(nint instance, int index, nint argument, out nint result)
    {
        nint value = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(instance, index))(instance, argument, &value);
        result = value;
        return hr;
    }

    public static int CallAddHandler(nint instance, int index, nint handler, out long token)
    {
        long value = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Slot(instance, index))(instance, handler, &value);
        token = value;
        return hr;
    }

    public static int CallRemoveHandler(nint instance, int index, long token) =>
        ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(instance, index))(instance, token);

    public static int CallGetForWindow(nint factory, nint window, in Guid iid, out nint result)
    {
        nint value = 0;
        fixed (Guid* id = &iid)
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(factory, SmtcSlots.InteropGetForWindow))(factory, window, id, &value);
            result = value;
            return hr;
        }
    }
}

/// <summary>
/// Interface identifiers and vtable slots, verified against the Windows SDK (windows.media.h,
/// systemmediatransportcontrolsinterop.h). Slots count from the start of the vtable.
/// </summary>
internal static class SmtcSlots
{
    public static readonly Guid InteropIid = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
    public static readonly Guid ControlsIid = new("99fa3ff4-1742-42a6-902e-087d41f965ec");
    public static readonly Guid DisplayUpdaterIid = new("8abbc53e-fa55-4ecf-ad8e-c984e5dd1550");
    public static readonly Guid MusicPropertiesIid = new("6bbf0c59-d0a0-4d26-92a0-f978e1d18e7b");
    public static readonly Guid MusicProperties2Iid = new("00368462-97d3-44b9-b00f-008afcefaf18");
    public static readonly Guid ButtonPressedArgsIid = new("b7f47116-a56f-4dc8-9e11-92031f4a87c2");
    public static readonly Guid ButtonPressedHandlerIid = new("0557e996-7b23-5bae-aa81-ea0d671143a4");
    public static readonly Guid AgileObjectIid = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    public const string ClassName = "Windows.Media.SystemMediaTransportControls";

    public const int InteropGetForWindow = 6;

    public const int GetPlaybackStatus = 6;
    public const int PutPlaybackStatus = 7;
    public const int GetDisplayUpdater = 8;
    public const int PutIsEnabled = 11;
    public const int GetIsPlayEnabled = 12;
    public const int PutIsPlayEnabled = 13;
    public const int PutIsStopEnabled = 15;
    public const int PutIsPauseEnabled = 17;
    public const int PutIsPreviousEnabled = 25;
    public const int PutIsNextEnabled = 27;
    public const int AddButtonPressed = 32;
    public const int RemoveButtonPressed = 33;

    // IRandomAccessStream, IRandomAccessStreamReferenceStatics (windows.storage.streams.h); BSOS_DEFAULT = 0.
    public static readonly Guid RandomAccessStreamIid = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
    public static readonly Guid StreamReferenceStaticsIid = new("857309dc-3fbf-4e7d-986f-ef3b1a07a964");
    public const string StreamReferenceClassName = "Windows.Storage.Streams.RandomAccessStreamReference";
    public const int StreamReferenceCreateFromStream = 8;

    public const int UpdaterPutType = 7;
    public const int UpdaterGetThumbnail = 10;
    public const int UpdaterPutThumbnail = 11;
    public const int UpdaterGetMusicProperties = 12;
    public const int UpdaterClearAll = 16;
    public const int UpdaterUpdate = 17;
    public const int MediaPlaybackTypeMusic = 1;

    public const int MusicGetTitle = 6;
    public const int MusicPutTitle = 7;
    public const int MusicPutAlbumArtist = 9;
    public const int MusicPutArtist = 11;
    public const int Music2PutAlbumTitle = 7;

    public const int ButtonPressedArgsGetButton = 6;
}
