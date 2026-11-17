using System.IO.Compression;

namespace Aibell.Skins.Classic;

/// <summary>
/// The files of a classic skin (.wsz and .zip are the same thing), looked up by file name without regard to case or folder:
/// skins are often zipped with their files inside a subfolder, and their names come in any case. The archive is read into
/// memory and never extracted, so no entry name can reach the file system.
/// </summary>
public sealed class SkinArchive
{
    /// <summary>The largest skin file accepted; real skins are well under a megabyte.</summary>
    public const long MaximumArchiveBytes = 16 * 1024 * 1024;

    private const int MaximumEntries = 1024;
    private const long MaximumEntryBytes = 8 * 1024 * 1024;
    private const long MaximumTotalBytes = 48 * 1024 * 1024;

    private readonly Dictionary<string, byte[]> files;

    private SkinArchive(Dictionary<string, byte[]> files) => this.files = files;

    /// <summary>The file names (without folders) this skin has, in no particular order.</summary>
    public IEnumerable<string> FileNames => files.Keys;

    public static SkinArchive FromFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        Dictionary<string, byte[]> copy = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, byte[] bytes) in files) copy[Path.GetFileName(name)] = bytes;
        return new SkinArchive(copy);
    }

    public static SkinArchive Read(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.Length > MaximumArchiveBytes) throw new InvalidSkinException(InvalidSkinReason.TooLarge);
        using MemoryStream stream = new(archive, writable: false);
        return Read(stream);
    }

    public static SkinArchive Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidSkinException(InvalidSkinReason.NotAnArchive, exception);
        }
        using (zip)
        {
            if (zip.Entries.Count > MaximumEntries) throw new InvalidSkinException(InvalidSkinReason.TooLarge);
            Dictionary<string, (int Depth, byte[] Bytes)> found = new(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string path = entry.FullName.Replace('\\', '/');
                if (path.EndsWith('/') || path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) continue;
                string name = path[(path.LastIndexOf('/') + 1)..];
                if (name.Length == 0 || name.StartsWith("._", StringComparison.Ordinal)) continue;
                int depth = path.Count(character => character == '/');
                // The copy nearest the top wins: a skin that bundles another one in a subfolder still shows its own files.
                if (found.TryGetValue(name, out var existing) && existing.Depth <= depth) continue;
                if (entry.Length > MaximumEntryBytes || (total += entry.Length) > MaximumTotalBytes)
                    throw new InvalidSkinException(InvalidSkinReason.TooLarge);
                found[name] = (depth, ReadEntry(entry));
            }
            Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string name, (int _, byte[] bytes)) in found) files[name] = bytes;
            return new SkinArchive(files);
        }
    }

    public static SkinArchive ReadDirectory(string directory)
    {
        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (new FileInfo(file).Length > MaximumEntryBytes) continue;
            files[Path.GetFileName(file)] = File.ReadAllBytes(file);
        }
        return new SkinArchive(files);
    }

    /// <summary>Whether this looks like a modern (XML and script) skin, which this player does not draw.</summary>
    public bool IsModernSkin => Has("skin.xml") && !Has("main.bmp");

    public bool Has(string name) => files.ContainsKey(name);

    public byte[]? Get(string name) => files.TryGetValue(name, out byte[]? bytes) ? bytes : null;

    /// <summary>A text file decoded as Windows-1252 would be (Latin-1 is close enough for skin configuration files).</summary>
    public string? GetText(string name) => Get(name) is { } bytes ? System.Text.Encoding.Latin1.GetString(bytes) : null;

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        try
        {
            using Stream input = entry.Open();
            using MemoryStream output = new();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                // The declared length can lie; the bytes actually inflated are what count.
                if (output.Length + read > MaximumEntryBytes) throw new InvalidSkinException(InvalidSkinReason.TooLarge);
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidSkinException(InvalidSkinReason.NotAnArchive, exception);
        }
    }
}

public enum InvalidSkinReason
{
    /// <summary>The file is not a zip archive, or it is damaged.</summary>
    NotAnArchive,

    /// <summary>The archive or one of its files is far bigger than any skin.</summary>
    TooLarge,

    /// <summary>The archive has no main window bitmap, so it is not a classic skin.</summary>
    NotAClassicSkin,

    /// <summary>A modern skin (XML and scripts), which this player cannot draw.</summary>
    ModernSkin,
}

public sealed class InvalidSkinException : Exception
{
    public InvalidSkinException() : this(InvalidSkinReason.NotAClassicSkin) { }

    public InvalidSkinException(string message) : base(message) { }

    public InvalidSkinException(string message, Exception innerException) : base(message, innerException) { }

    public InvalidSkinException(InvalidSkinReason reason, Exception? innerException = null)
        : base(reason switch
        {
            InvalidSkinReason.NotAnArchive => "The file is not a skin archive.",
            InvalidSkinReason.TooLarge => "The skin file is too large.",
            InvalidSkinReason.ModernSkin => "Modern skins are not supported; only classic skins are.",
            _ => "The file is not a classic skin.",
        }, innerException)
    {
        Reason = reason;
    }

    public InvalidSkinReason Reason { get; }
}
