using System.Security.Cryptography;
using Aibell.Application.Library;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class MetadataBackupManagerTests
{
    private string directory = null!;
    private string source = null!;
    private readonly MetadataFileWriter writer = new(new AtlMetadataEditStager());
    [SetUp]
    public void SetUp()
    {
        directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-backup-manager-" + Guid.NewGuid().ToString("N"))).FullName;
        source = Path.Combine(directory, "fixture.flac");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.flac"), source);
    }
    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [Test]
    public async Task InspectAndDelete_HistoricalBackup_DeletesOnlySelectedVerifiedArtifacts()
    {
        MetadataFileWriteResult first = await writer.WriteAsync(new Uri(source), new(Title: "First"));
        MetadataFileWriteResult second = await writer.WriteAsync(new Uri(source), new(Title: "Second"));
        MetadataBackupScan scan = await writer.InspectBackupsAsync(new Uri(source));
        Assert.That(scan.Issues, Is.Empty);
        Assert.That(scan.Backups, Has.Count.EqualTo(2));
        Assert.That(scan.Backups.Count(backup => backup.CanRestore), Is.EqualTo(1));
        MetadataBackupInfo historical = scan.Backups.Single(backup => !backup.CanRestore);
        Assert.That(historical.Backup, Is.EqualTo(first.Backup));
        string hash = Hash(source);
        await writer.DeleteBackupAsync(historical);
        Assert.That(Hash(source), Is.EqualTo(hash));
        Assert.That(File.Exists(first.Backup.Backup.LocalPath), Is.False);
        Assert.That(File.Exists(first.Backup.Journal.LocalPath), Is.False);
        Assert.That(File.Exists(second.Backup.Backup.LocalPath), Is.True);
        Assert.That((await writer.InspectBackupsAsync(new Uri(source))).Backups, Has.Count.EqualTo(1));
        await writer.RestoreAsync(second.Backup);
        Assert.That(new ATL.Track(source).Title, Is.EqualTo("First"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task DeleteBackupAsync_ChangedJournalOrBackup_RejectsAndPreservesEvidence(bool journal)
    {
        await writer.WriteAsync(new Uri(source), new(Title: "Edited"));
        MetadataBackupInfo backup = (await writer.InspectBackupsAsync(new Uri(source))).Backups.Single();
        File.WriteAllText(journal ? backup.Backup.Journal.LocalPath : backup.Backup.Backup.LocalPath, "External change");
        string hash = Hash(source);
        Assert.That(async () => await writer.DeleteBackupAsync(backup), Throws.InstanceOf<IOException>());
        Assert.That(Hash(source), Is.EqualTo(hash));
        Assert.That(File.Exists(backup.Backup.Journal.LocalPath), Is.True);
        Assert.That(File.Exists(backup.Backup.Backup.LocalPath), Is.True);
        MetadataBackupScan scan = await writer.InspectBackupsAsync(new Uri(source));
        Assert.That(scan.Backups, Is.Empty); Assert.That(scan.Issues, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DeleteBackupAsync_PreCancelled_LeavesFiles()
    {
        await writer.WriteAsync(new Uri(source), new(Title: "Edited"));
        MetadataBackupInfo backup = (await writer.InspectBackupsAsync(new Uri(source))).Backups.Single();
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.That(async () => await writer.DeleteBackupAsync(backup, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(File.Exists(backup.Backup.Backup.LocalPath), Is.True);
        Assert.That(File.Exists(backup.Backup.Journal.LocalPath), Is.True);
    }

    [Test]
    public async Task InspectBackupsAsync_ExternalSourceChange_KeepsHistoricalBackupsWithoutRestore()
    {
        await writer.WriteAsync(new Uri(source), new(Title: "Edited"));
        File.WriteAllText(source, "Changed externally");
        MetadataBackupScan scan = await writer.InspectBackupsAsync(new Uri(source));
        Assert.That(scan.Backups.Single().CanRestore, Is.False);
        Assert.That(scan.Issues, Is.Empty);
        await writer.DeleteBackupAsync(scan.Backups.Single());
        Assert.That(File.ReadAllText(source), Is.EqualTo("Changed externally"));
    }

    [Test]
    public async Task InspectBackupsAsync_UnrelatedSource_ExcludesItsBackups()
    {
        await writer.WriteAsync(new Uri(source), new(Title: "Edited"));
        string other = Path.Combine(directory, "other.flac"); File.Copy(source, other);
        MetadataBackupScan scan = await writer.InspectBackupsAsync(new Uri(other));
        Assert.That(scan.Backups, Is.Empty); Assert.That(scan.Issues, Is.Empty);
    }

    [Test]
    [Platform("Linux")]
    public async Task InspectAndDelete_SymbolicBackup_RejectsWithoutTouchingTarget()
    {
        MetadataFileWriteResult result = await writer.WriteAsync(new Uri(source), new(Title: "Edited"));
        MetadataBackupInfo info = (await writer.InspectBackupsAsync(new Uri(source))).Backups.Single();
        string target = Path.Combine(directory, "kept.flac"); File.Move(result.Backup.Backup.LocalPath, target);
        File.CreateSymbolicLink(result.Backup.Backup.LocalPath, target);
        Assert.That(async () => await writer.DeleteBackupAsync(info), Throws.TypeOf<InvalidDataException>());
        Assert.That((await writer.InspectBackupsAsync(new Uri(source))).Issues, Has.Count.EqualTo(1));
        Assert.That(File.Exists(target), Is.True);
    }
    [TestCase("version")]
    [TestCase("traversal")]
    [TestCase("oversize")]
    public async Task InspectBackupsAsync_InvalidStructuredJournal_ReportsAndRetainsEvidence(string fault)
    {
        MetadataFileWriteResult result = await writer.WriteAsync(new Uri(source), new(Title: "Edited"));
        string text = await File.ReadAllTextAsync(result.Backup.Journal.LocalPath);
        text = fault switch
        {
            "version" => text.Replace("\"Version\":1", "\"Version\":999", StringComparison.Ordinal),
            "traversal" => text.Replace("fixture.flac", "../fixture.flac", StringComparison.Ordinal),
            _ => new string('x', 65537)
        };
        await File.WriteAllTextAsync(result.Backup.Journal.LocalPath, text);
        Assert.That(await writer.GetBackupsAsync(new Uri(source)), Is.Empty);
        Assert.That((await writer.InspectBackupsAsync(new Uri(source))).Issues, Has.Count.EqualTo(1));
        Assert.That(File.Exists(result.Backup.Backup.LocalPath), Is.True);
        Assert.That(new ATL.Track(source).Title, Is.EqualTo("Edited"));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
