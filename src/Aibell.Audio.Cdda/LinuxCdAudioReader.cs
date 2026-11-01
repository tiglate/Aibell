using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Aibell.Audio.Cdda;

/// <summary>Audio extraction on Linux with the kernel's <c>CDROMREADAUDIO</c> ioctl on the drive's device node (the user must be in the <c>cdrom</c> group).</summary>
internal sealed unsafe partial class LinuxCdAudioReader : ICdAudioReader
{
    private const uint CdromReadAudio = 0x530E;
    private const uint CdromSelectSpeed = 0x5322;
    private const byte CdromLba = 0x01;
    private const int NoMedium = 123;
    // A drive left at full speed is loud and no more accurate; playback needs 1x, so a few times that leaves room for re-reads.
    private const int PlaybackSpeed = 4;

    private readonly SafeFileHandle handle;

    public LinuxCdAudioReader(string device)
    {
        handle = File.OpenHandle(device, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _ = SelectSpeed(handle, CdromSelectSpeed, PlaybackSpeed);
    }

    /// <summary><c>struct cdrom_read_audio</c>: the address union (an LBA here), its format, the frame count and the destination.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ReadAudioRequest
    {
        public int Lba;
        public byte AddressFormat;
        public int Frames;
        public byte* Buffer;
    }

    public bool ReadSectors(int lba, int count, Span<byte> destination)
    {
        if (destination.Length < count * CddaDecoder.SectorBytes) throw new ArgumentException("The buffer is too small.", nameof(destination));
        fixed (byte* pointer = destination)
        {
            ReadAudioRequest request = new() { Lba = lba, AddressFormat = CdromLba, Frames = count, Buffer = pointer };
            if (Ioctl(handle, CdromReadAudio, ref request) == 0) return true;
        }
        if (Marshal.GetLastPInvokeError() == NoMedium) throw new IOException("There is no disc in the drive.");
        return false;
    }

    public void Dispose() => handle.Dispose();

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(SafeFileHandle fd, nuint request, ref ReadAudioRequest argument);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int SelectSpeed(SafeFileHandle fd, nuint request, nuint speed);
}
