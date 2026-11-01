using System.Security.Cryptography;
using System.Text.Json;
using Aibell.Domain.Media;

namespace Aibell.Infrastructure.Metadata;

internal sealed record MetadataEditJournal(int Version, string SourceName, string SourceHash,
    string? StageHash, bool Ready, MetadataEdit Edit)
{
    public const string Prefix = ".aibell-edit-";

    public static string JournalPath(string stagePath) => Path.ChangeExtension(stagePath, ".json");
    public static string LeasePath(string stagePath) => Path.ChangeExtension(stagePath, ".lease");

    public static FileStream AcquireLease(string stagePath)
    {
        string path = LeasePath(stagePath);
        RejectLink(path);
        return new FileStream(path, PrivateOptions(FileMode.OpenOrCreate));
    }

    public static FileStreamOptions PrivateOptions(FileMode mode)
    {
        FileStreamOptions options = new() { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    public void Save(string stagePath, bool first)
    {
        string path = JournalPath(stagePath);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream output = new(temporary, PrivateOptions(FileMode.CreateNew)))
            {
                JsonSerializer.Serialize(output, this);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: !first);
        }
        finally { File.Delete(temporary); }
    }

    public static async Task<MetadataEditJournal> LoadAsync(string journalPath, CancellationToken cancellationToken)
    {
        RejectLink(journalPath);
        await using FileStream stream = File.OpenRead(journalPath);
        if (stream.Length > 24 * 1024 * 1024) throw new InvalidDataException("The metadata journal exceeds the supported size.");
        MetadataEditJournal journal = await JsonSerializer.DeserializeAsync<MetadataEditJournal>(stream,
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The metadata journal is empty.");
        if (journal.Version != 1 || string.IsNullOrWhiteSpace(journal.SourceName) ||
            journal.SourceName is "." or ".." || journal.SourceName.IndexOfAny(['/', '\\']) >= 0 ||
            Path.IsPathRooted(journal.SourceName) ||
            journal.SourceName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            journal.SourceName.StartsWith(Prefix, StringComparison.Ordinal) ||
            !IsHash(journal.SourceHash) || (journal.Ready && !IsHash(journal.StageHash)) ||
            (!journal.Ready && journal.StageHash is not null) || journal.Edit is null)
            throw new InvalidDataException("The metadata journal has an unsupported or invalid format.");
        journal.Edit.Validate();
        return journal;
    }

    public static string StagePath(string journalPath)
    {
        string name = Path.GetFileNameWithoutExtension(journalPath);
        if (Path.GetExtension(journalPath) != ".json" || !name.StartsWith(Prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
            throw new InvalidDataException("The metadata journal filename is invalid.");
        return Path.ChangeExtension(journalPath, ".stage");
    }

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        RejectLink(path);
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    public static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null ||
            File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Symbolic links are not accepted in metadata recovery.");
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
