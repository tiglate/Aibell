using System.Runtime.InteropServices;
using Aibell.Application.Discs;
using Microsoft.Win32.SafeHandles;

namespace Aibell.Platform.Windows;

/// <summary>
/// Audio CD drives on Windows: optical drives come from <see cref="DriveInfo"/>, the table of contents from
/// <c>IOCTL_CDROM_READ_TOC</c>. An empty drive or a refused request means "no disc". This code needs a drive to run end
/// to end; only the parsing of the returned structure is covered by automated tests.
/// </summary>
public sealed partial class WindowsOpticalDriveService : IOpticalDriveService
{
    private const uint IoctlCdromReadToc = 0x00024000;
    private const int MaximumTracks = 100;
    private const int HeaderSize = 4;
    private const int EntrySize = 8;
    private const byte LeadOutTrack = 0xAA;
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    public IReadOnlyList<OpticalDrive> ListDrives()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            return [.. DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.CDRom)
                .Select(drive => new OpticalDrive(drive.Name.TrimEnd('\\'), "CD/DVD drive (" + drive.Name.TrimEnd('\\') + ")"))];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public Task<DiscToc?> ReadTocAsync(OpticalDrive drive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drive);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => OperatingSystem.IsWindows() ? ReadToc(drive.Id) : null, cancellationToken);
    }

    /// <summary>Decodes a <c>CDROM_TOC</c>: big-endian length, first and last track, then 8-byte entries ending in an M:S:F address.</summary>
    internal static DiscToc? ParseToc(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize + EntrySize) return null;
        int length = (buffer[0] << 8) | buffer[1];
        int count = Math.Min((length - 2) / EntrySize, Math.Min(MaximumTracks, (buffer.Length - HeaderSize) / EntrySize));
        List<RawDiscTrack> tracks = [];
        int? leadOut = null;
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = buffer.Slice(HeaderSize + index * EntrySize, EntrySize);
            // Entry layout: reserved, adr:4|control:4 (control in the low nibble of the second field), track, reserved, 0, M, S, F.
            int offset = entry[5] * 60 * DiscToc.SectorsPerSecond + entry[6] * DiscToc.SectorsPerSecond + entry[7];
            if (entry[2] == LeadOutTrack) leadOut = offset;
            else if (entry[2] is >= 1 and <= 99) tracks.Add(new RawDiscTrack(entry[2], (entry[1] & 0x04) != 0, offset));
        }
        return leadOut is { } end ? DiscTocBuilder.Build(tracks, end) : null;
    }

    private static DiscToc? ReadToc(string driveId)
    {
        if (driveId.Length != 2 || !char.IsAsciiLetter(driveId[0]) || driveId[1] != ':') return null;
        using SafeFileHandle handle = CreateFile(@"\\.\" + driveId, GenericRead, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid) return null;
        byte[] buffer = new byte[HeaderSize + MaximumTracks * EntrySize];
        return DeviceIoControl(handle, IoctlCdromReadToc, 0, 0, buffer, (uint)buffer.Length, out uint returned, 0) && returned >= HeaderSize
            ? ParseToc(buffer.AsSpan(0, (int)returned)) : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, nint overlapped);
}
