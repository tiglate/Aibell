using System.Runtime.InteropServices;
using Aibell.Application.Discs;
using Microsoft.Win32.SafeHandles;

namespace Aibell.Platform.Linux;

/// <summary>
/// Audio CD drives on Linux: drives come from <c>/proc/sys/dev/cdrom/info</c>, the table of contents from the kernel's
/// <c>CDROMREADTOCHDR</c> and <c>CDROMREADTOCENTRY</c> ioctls. An empty drive, a missing device or a refused ioctl all
/// mean "no disc". This code needs a drive to run end to end; only its parsing is covered by automated tests.
/// </summary>
public sealed partial class LinuxOpticalDriveService : IOpticalDriveService
{
    private const string DriveInfoPath = "/proc/sys/dev/cdrom/info";
    private const uint CdromReadTocHeader = 0x5305;
    private const uint CdromReadTocEntry = 0x5306;
    private const byte LeadOutTrack = 0xAA;
    private const byte FormatLba = 0x01;
    internal const int TocEntrySize = 12;
    private const int LeadIn = 150;

    private readonly Func<string?> readDriveInfo;

    public LinuxOpticalDriveService() : this(ReadDriveInfo) { }

    internal LinuxOpticalDriveService(Func<string?> readDriveInfo) => this.readDriveInfo = readDriveInfo;

    public IReadOnlyList<OpticalDrive> ListDrives() => ParseDriveInfo(readDriveInfo());

    public Task<DiscToc?> ReadTocAsync(OpticalDrive drive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drive);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => ReadToc(drive.Id), cancellationToken);
    }

    internal static IReadOnlyList<OpticalDrive> ParseDriveInfo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        foreach (string line in text.Split('\n'))
        {
            if (!line.StartsWith("drive name:", StringComparison.Ordinal)) continue;
            return [.. line["drive name:".Length..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Where(name => name.All(character => char.IsAsciiLetterOrDigit(character)))
                .Select(name => new OpticalDrive("/dev/" + name, "CD/DVD drive (" + name + ")"))];
        }
        return [];
    }

    internal static RawDiscTrack DecodeEntry(ReadOnlySpan<byte> entry, int number)
    {
        // struct cdrom_tocentry: track, adr:4|ctrl:4, format, (padding), int lba, datamode.
        bool isData = (entry[1] & 0x40) != 0;
        int lba = BitConverter.ToInt32(entry[4..8]);
        return new RawDiscTrack(number, isData, lba + LeadIn);
    }

    private static string? ReadDriveInfo()
    {
        try
        {
            return File.Exists(DriveInfoPath) ? File.ReadAllText(DriveInfoPath) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The two kernel requests the table of contents needs. Only <see cref="KernelCdrom"/> touches a real device.</summary>
    internal interface ICdromControl : IDisposable
    {
        /// <summary>First and last track numbers, or null when the drive refuses (no disc, not a CD drive).</summary>
        (int First, int Last)? ReadHeader();

        /// <summary>The entry for a track number, or <see cref="LeadOutTrack"/> for the lead-out; null when refused.</summary>
        RawDiscTrack? ReadEntry(byte track);
    }

    internal static DiscToc? ReadToc(string device, Func<string, ICdromControl> open)
    {
        if (!device.StartsWith("/dev/", StringComparison.Ordinal)) return null;
        try
        {
            using ICdromControl control = open(device);
            if (control.ReadHeader() is not { } range || range.First < 1 || range.Last < range.First || range.Last > 99) return null;
            List<RawDiscTrack> tracks = [];
            for (int number = range.First; number <= range.Last; number++)
            {
                if (control.ReadEntry((byte)number) is not { } entry) return null;
                tracks.Add(entry);
            }
            return control.ReadEntry(LeadOutTrack) is { } leadOut ? DiscTocBuilder.Build(tracks, leadOut.Offset) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DiscToc? ReadToc(string device) => ReadToc(device, path => new KernelCdrom(path));

    private sealed class KernelCdrom(string device) : ICdromControl
    {
        private readonly SafeFileHandle handle = File.OpenHandle(device, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        public (int First, int Last)? ReadHeader()
        {
            Span<byte> header = stackalloc byte[2];
            return Ioctl(handle, CdromReadTocHeader, ref MemoryMarshal.GetReference(header)) == 0 ? (header[0], header[1]) : null;
        }

        public RawDiscTrack? ReadEntry(byte track)
        {
            Span<byte> entry = stackalloc byte[TocEntrySize];
            entry.Clear();
            entry[0] = track;
            entry[2] = FormatLba;
            return Ioctl(handle, CdromReadTocEntry, ref MemoryMarshal.GetReference(entry)) == 0 ? DecodeEntry(entry, track) : null;
        }

        public void Dispose() => handle.Dispose();
    }

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(SafeFileHandle fd, nuint request, ref byte argument);
}
