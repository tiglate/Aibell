using System.Runtime.InteropServices;

namespace Aibell.Platform.Windows.Native;

internal static partial class NativeMethods
{
    public const uint WmTimer = 0x0113;
    public const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    public const uint NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    public const uint NiifNoSound = 0x10, NiifRespectQuietTime = 0x80;
    public const uint NotifyIconVersion4 = 4;
    public const nint IdiApplication = 32512;

    /// <summary>NOTIFYICONDATAW (Vista and later layout). Field order and sizes follow shellapi.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShellNotifyIcon(uint message, in NotifyIconData data);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    public static partial nint LoadIcon(nint instance, nint name);

    [LibraryImport("user32.dll", EntryPoint = "SetTimer")]
    public static partial nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint window, nuint id);
}
