using System.Security.Cryptography;
using System.Text.Json;
using Aibell.Application.Library;
using Aibell.Domain.Media;

namespace Aibell.Infrastructure.Metadata;

// File.Replace is the commit boundary. Cancellation is honored only before it.
public sealed partial class MetadataFileWriter : IMetadataFileWriter, IMetadataBackupManager
{
    private const string Prefix = ".aibell-write-";
    private readonly IMetadataEditStager stager;
    private readonly Action<string, string, string> replace;
    private readonly Action<string>? checkpoint;

    public MetadataFileWriter(IMetadataEditStager stager) : this(stager, (stage, source, backup) => File.Replace(stage, source, backup), null) { }
    internal MetadataFileWriter(IMetadataEditStager stager, Action<string, string, string> replace, Action<string>? checkpoint)
    { this.stager = stager; this.replace = replace; this.checkpoint = checkpoint; }

    public async Task<MetadataFileWriteResult> WriteAsync(Uri location, MetadataEdit edit, string? expectedSourceHash = null, CancellationToken cancellationToken = default)
    {
        string source = SourcePath(location);
        using FileStream lease = LockSource(source);
        IStagedMetadataEdit staged = await stager.StageAsync(location, edit, cancellationToken).ConfigureAwait(false);
        MetadataFileWriteResult result;
        try
        {
            if (expectedSourceHash is not null && staged.SourceHash != expectedSourceHash)
                throw new IOException("The audio file changed since the editor opened. Reopen track properties before saving to the file.");
            await staged.VerifyUnchangedAsync(cancellationToken).ConfigureAwait(false);
            result = await CommitAsync(source, staged.StagedFile.LocalPath, staged.SourceHash, staged.StagedHash, cancellationToken).ConfigureAwait(false);
        }
        catch { await staged.DisposeAsync().ConfigureAwait(false); throw; }
        try { await staged.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return result with { Warning = (result.Warning ?? "") + " The file was saved, but preview cleanup failed: " + exception.Message }; }
        return result;
    }

    public async Task<IReadOnlyList<MetadataFileBackup>> GetBackupsAsync(Uri location, CancellationToken cancellationToken = default)
    {
        string source = SourcePath(location, requireWritable: false);
        List<MetadataFileBackup> backups = [];
        string currentHash = await HashAsync(source, cancellationToken).ConfigureAwait(false);
        foreach (string journal in Directory.EnumerateFiles(Path.GetDirectoryName(source)!, Prefix + "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Unrecognized or damaged journals are retained for manual inspection.
            try
            {
                WriteJournal entry = await ReadJournalAsync(journal, cancellationToken).ConfigureAwait(false);
                if (entry.SourceName != Path.GetFileName(source)) continue;
                if (currentHash != entry.WrittenHash) continue;
                string backup = Path.Combine(Path.GetDirectoryName(source)!, entry.BackupName);
                if (await HashAsync(backup, cancellationToken).ConfigureAwait(false) != entry.OriginalHash) continue;
                // A prepared journal is recoverable after a crash immediately after replacement.
                backups.Add(ToBackup(source, backup, journal, entry));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        return backups.OrderByDescending(backup => backup.CreatedAt).ToArray();
    }

    public async Task<MetadataFileWriteResult> RestoreAsync(MetadataFileBackup backup, CancellationToken cancellationToken = default)
    {
        string source = SourcePath(backup.Source);
        using FileStream lease = LockSource(source);
        WriteJournal entry = await ReadJournalAsync(backup.Journal.LocalPath, cancellationToken).ConfigureAwait(false);
        string directory = Path.GetDirectoryName(source)!;
        if (Path.GetFullPath(backup.Journal.LocalPath) != Path.Combine(directory, Path.GetFileName(backup.Journal.LocalPath)) ||
            entry.SourceName != Path.GetFileName(source) || Path.Combine(directory, entry.BackupName) != backup.Backup.LocalPath ||
            entry.OriginalHash != backup.OriginalHash || entry.WrittenHash != backup.WrittenHash)
            throw new InvalidDataException("The backup record does not match its journal.");
        string currentHash = await HashAsync(source, cancellationToken).ConfigureAwait(false);
        if (currentHash != entry.WrittenHash || await HashAsync(backup.Backup.LocalPath, cancellationToken).ConfigureAwait(false) != entry.OriginalHash)
            throw new IOException("The source or backup has changed. Restore was cancelled without replacing the source.");
        string stage = Path.Combine(directory, Prefix + Guid.NewGuid().ToString("N") + ".restore");
        try
        {
            await using (FileStream input = new(backup.Backup.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (FileStream output = new(stage, MetadataEditJournal.PrivateOptions(FileMode.CreateNew)))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            if (await HashAsync(stage, cancellationToken).ConfigureAwait(false) != entry.OriginalHash)
                throw new IOException("The restore copy failed validation.");
            return await CommitAsync(source, stage, currentHash, entry.OriginalHash, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    private async Task<MetadataFileWriteResult> CommitAsync(string source, string stage, string originalHash, string writtenHash, CancellationToken token)
    {
        string directory = Path.GetDirectoryName(source)!;
        string name = Prefix + Guid.NewGuid().ToString("N");
        string backup = Path.Combine(directory, name + ".backup");
        string journal = Path.Combine(directory, name + ".json");
        WriteJournal entry = new(1, Path.GetFileName(source), Path.GetFileName(backup), originalHash, writtenHash, DateTimeOffset.UtcNow);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stage, File.GetUnixFileMode(source));
        SaveJournal(journal, entry);
        MetadataFileBackup receipt = ToBackup(source, backup, journal, entry);
        try
        {
            checkpoint?.Invoke("BeforeReplace");
            MetadataEditJournal.RejectLink(source);
            if (await HashAsync(source, token).ConfigureAwait(false) != originalHash ||
                await HashAsync(stage, token).ConfigureAwait(false) != writtenHash)
                throw new IOException("The source or preview changed before replacement.");
            token.ThrowIfCancellationRequested();
            using FileStream sourceGuard = new(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (Convert.ToHexString(await SHA256.HashDataAsync(sourceGuard, token).ConfigureAwait(false)) != originalHash)
                throw new IOException("The source changed before replacement.");
            token.ThrowIfCancellationRequested();
            replace(stage, source, backup);
        }
        catch (Exception exception)
        {
            if (File.Exists(backup))
            {
                // Some filesystem errors can be reported after the rename committed.
                try
                {
                    if (await HashAsync(source, CancellationToken.None).ConfigureAwait(false) == writtenHash &&
                        await HashAsync(backup, CancellationToken.None).ConfigureAwait(false) == originalHash)
                        return new(receipt, "The file was saved, but replacement reported: " + exception.Message);
                }
                catch (Exception verification) when (verification is IOException or UnauthorizedAccessException) { }
                throw new IOException("File replacement may have completed. Keep the backup and journal and reopen track properties before retrying. " + backup, exception);
            }
            File.Delete(journal);
            throw;
        }
        // Never report cancellation or an unchanged file once replacement succeeded.
        string? warning = null;
        try
        {
            checkpoint?.Invoke("AfterReplace");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (await HashAsync(source, CancellationToken.None).ConfigureAwait(false) != writtenHash ||
                await HashAsync(backup, CancellationToken.None).ConfigureAwait(false) != originalHash)
                warning = "Replacement completed, but verification found a subsequent file change. Keep the backup and journal for inspection.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        { warning = "The file was saved; backup verification needs attention: " + exception.Message; }
        return new(receipt, warning);
    }

    private static string SourcePath(Uri location, bool requireWritable = true)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!location.IsAbsoluteUri || !location.IsFile) throw new ArgumentException("File writing requires a local file.", nameof(location));
        string source = Path.GetFullPath(location.LocalPath);
        MetadataEditJournal.RejectLink(source);
        if (requireWritable && (File.GetAttributes(source) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("The audio file is read-only.");
        // A linked parent can redirect stage/backup operations between validation and replacement.
        for (DirectoryInfo? parent = Directory.GetParent(source); parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null) throw new NotSupportedException("File writing through a symbolic-link directory is not supported.");
        return source;
    }

    private static FileStream LockSource(string source)
    {
        string key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? source.ToUpperInvariant() : source)));
        string path = Path.Combine(Path.GetDirectoryName(source)!, Prefix + key + ".lease");
        MetadataEditJournal.RejectLink(path);
        // Leave the lock inode in place after closing, so another owner cannot lock an unlinked inode.
        return new FileStream(path, MetadataEditJournal.PrivateOptions(FileMode.OpenOrCreate));
    }

    private static Task<string> HashAsync(string path, CancellationToken token) => MetadataEditJournal.HashAsync(path, token);
    private static MetadataFileBackup ToBackup(string source, string backup, string journal, WriteJournal entry) =>
        new(new Uri(source), new Uri(backup), new Uri(journal), entry.OriginalHash, entry.WrittenHash, entry.CreatedAt);

    private static void SaveJournal(string path, WriteJournal entry)
    {
        using FileStream output = new(path, MetadataEditJournal.PrivateOptions(FileMode.CreateNew));
        JsonSerializer.Serialize(output, entry); output.Flush(flushToDisk: true);
    }

    private static async Task<WriteJournal> ReadJournalAsync(string path, CancellationToken token)
    {
        MetadataEditJournal.RejectLink(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        if (!stem.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(stem[Prefix.Length..], "N", out _))
            throw new InvalidDataException("Invalid file-write journal name.");
        await using FileStream stream = File.OpenRead(path);
        if (stream.Length > 65536) throw new InvalidDataException("The file-write journal exceeds the supported size.");
        WriteJournal entry = await JsonSerializer.DeserializeAsync<WriteJournal>(stream, cancellationToken: token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Empty file-write journal.");
        if (entry.Version != 1 || string.IsNullOrWhiteSpace(entry.SourceName) || entry.SourceName is "." or ".." ||
            entry.SourceName.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(entry.SourceName) ||
            entry.BackupName != stem + ".backup" || !IsHash(entry.OriginalHash) || !IsHash(entry.WrittenHash))
            throw new InvalidDataException("Invalid file-write journal.");
        return entry;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private sealed record WriteJournal(int Version, string SourceName, string BackupName, string OriginalHash,
        string WrittenHash, DateTimeOffset CreatedAt);
}
