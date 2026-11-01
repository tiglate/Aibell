using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Aibell.Audio.Cdda;

/// <summary>Audio extraction on Windows with <c>IOCTL_CDROM_RAW_READ</c> (CDDA track mode) on the drive's volume handle.</summary>
internal sealed unsafe partial class WindowsCdAudioReader : ICdAudioReader
{
    // CTL_CODE(IOCTL_CDROM_BASE = 2, 0x000F, METHOD_OUT_DIRECT = 2, FILE_READ_ACCESS = 1)
    internal const uint IoctlCdromRawRead = (2u << 16) | (1u << 14) | (0x000Fu << 2) | 2u;
    private const int TrackModeCdda = 2;
    private const int ProvidedSectorBytes = 2048;
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const int ErrorNotReady = 21;
    private const int ErrorNoMediaInDrive = 1112;

    private readonly SafeFileHandle handle;

    public WindowsCdAudioReader(string driveLetter)
    {
        handle = CreateFile(@"\\.\" + driveLetter, GenericRead, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid) throw new IOException("The drive cannot be opened.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
    }

    /// <summary><c>RAW_READ_INFO</c>: the disc offset is always counted in 2048-byte sectors, whatever is read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawReadInfo
    {
        public long DiskOffset;
        public uint SectorCount;
        public int TrackMode;
    }

    public bool ReadSectors(int lba, int count, Span<byte> destination)
    {
        uint size = (uint)(count * CddaDecoder.SectorBytes);
        if (destination.Length < size) throw new ArgumentException("The buffer is too small.", nameof(destination));
        RawReadInfo info = new() { DiskOffset = (long)lba * ProvidedSectorBytes, SectorCount = (uint)count, TrackMode = TrackModeCdda };
        fixed (byte* pointer = destination)
        {
            if (DeviceIoControl(handle, IoctlCdromRawRead, &info, (uint)sizeof(RawReadInfo), pointer, size, out uint returned, 0) && returned == size) return true;
        }
        if (Marshal.GetLastPInvokeError() is ErrorNotReady or ErrorNoMediaInDrive) throw new IOException("There is no disc in the drive.");
        return false;
    }

    public void Dispose() => handle.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, RawReadInfo* input, uint inputSize, byte* output, uint outputSize, out uint returned, nint overlapped);
}
