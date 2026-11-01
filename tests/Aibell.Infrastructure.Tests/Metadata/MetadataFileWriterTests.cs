using System.Security.Cryptography;
using Aibell.Application.Library;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Files;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class MetadataFileWriterTests
{
    private static readonly string[] Formats = ["flac", "m4a", "ogg", "opus", "mp3"];
    private string directory = null!;
    private readonly MetadataFileWriter writer = new(new AtlMetadataEditStager());

    [SetUp]
    public void SetUp() => directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-write-" + Guid.NewGuid().ToString("N"))).FullName;
    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [TestCaseSource(nameof(Formats))]
    public async Task WriteAndRestore_AtomicReplacement_KeepsOriginalAndSupportsUndo(string extension)
    {
        string source = Copy(extension);
        string hash = Hash(source);
        UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(source);
        MetadataFileWriteResult result = await writer.WriteAsync(new Uri(source), new(Title: "Written ação 日本語", Composer: "Composer", TrackTotal: 12), hash);
        Assert.Multiple(() =>
        {
            Assert.That(new ATL.Track(source).Title, Is.EqualTo("Written ação 日本語"));
            Assert.That(Hash(result.Backup.Backup.LocalPath), Is.EqualTo(hash));
            Assert.That(result.Warning, Is.Null);
            Assert.That(Directory.GetFiles(directory, "*.stage"), Is.Empty);
            Assert.That(Directory.GetFiles(directory, ".aibell-edit-*.json"), Is.Empty);
        });
        if (!OperatingSystem.IsWindows()) Assert.That(File.GetUnixFileMode(source), Is.EqualTo(mode));
        IReadOnlyList<MetadataFileBackup> found = await writer.GetBackupsAsync(new Uri(source));
        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0], Is.EqualTo(result.Backup));
        MetadataFileWriteResult restored = await writer.RestoreAsync(found[0]);
        Assert.That(Hash(source), Is.EqualTo(hash));
        Assert.That(Hash(restored.Backup.Backup.LocalPath), Is.EqualTo(result.Backup.WrittenHash));
        Assert.That(Hash(result.Backup.Backup.LocalPath), Is.EqualTo(hash));
        await writer.RestoreAsync(restored.Backup);
        Assert.That(new ATL.Track(source).Title, Is.EqualTo("Written ação 日本語"));
        List<Uri> discovered = [];
        await foreach (Uri file in new FileSystemMediaDiscovery().DiscoverAsync([directory])) discovered.Add(file);
        Assert.That(discovered, Is.EqualTo(new[] { new Uri(source) }));
    }

    [TestCaseSource(nameof(Formats))]
    [Category("MetadataWriteDecode")]
    public async Task WriteAsync_ExtendedEditAndRestore_PreservesDecodedAudio(string extension)
    {
        if (Environment.GetEnvironmentVariable("AIBELL_METADATA_WRITE_TESTS") != "1") Assert.Ignore("Enable FFmpeg metadata decode tests.");
        string source = Copy(extension);
        string audio = await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source);
        MetadataFileWriteResult result = await writer.WriteAsync(new Uri(source), new(Title: "File title", AlbumArtist: "Artist", Composer: "Composer", DiscNumber: 1, DiscTotal: 2));
        Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source), Is.EqualTo(audio));
        await writer.RestoreAsync(result.Backup);
        Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source), Is.EqualTo(audio));
    }

    [TestCase("BeforeReplace")]
    [TestCase("Replacement")]
    public void WriteAsync_FailureBeforeCommit_LeavesOriginalAndCleansStage(string failure)
    {
        string source = Copy("flac"); string hash = Hash(source);
        MetadataFileWriter failing = new(new AtlMetadataEditStager(), (stage, file, backup) =>
        { if (failure == "Replacement") throw new IOException("Injected disk failure"); File.Replace(stage, file, backup); },
        phase => { if (phase == failure) throw new IOException("Injected journal/commit failure"); });
        Assert.That(async () => await failing.WriteAsync(new Uri(source), new(Title: "No change")), Throws.TypeOf<IOException>());
        Assert.That(Hash(source), Is.EqualTo(hash));
        Assert.That(Directory.GetFiles(directory, "*.stage"), Is.Empty);
        Assert.That(Directory.GetFiles(directory, ".aibell-write-*.json"), Is.Empty);
        Assert.That(Directory.GetFiles(directory, "*.backup"), Is.Empty);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task WriteAsync_ErrorReportedAfterCommit_ReturnsCommittedReceiptAndRecoverableBackup(bool replacementError)
    {
        string source = Copy("flac"); string hash = Hash(source);
        MetadataFileWriter failing = new(new AtlMetadataEditStager(), (stage, file, backup) =>
        { File.Replace(stage, file, backup); if (replacementError) throw new IOException("After rename"); },
        phase => { if (!replacementError && phase == "AfterReplace") throw new IOException("After commit"); });
        MetadataFileWriteResult result = await failing.WriteAsync(new Uri(source), new(Title: "Committed"));
        Assert.That(result.Warning, Is.Not.Empty);
        Assert.That(new ATL.Track(source).Title, Is.EqualTo("Committed"));
        Assert.That((await writer.GetBackupsAsync(new Uri(source))).Count, Is.EqualTo(1));
        await writer.RestoreAsync(result.Backup);
        Assert.That(Hash(source), Is.EqualTo(hash));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task WriteAsync_CancellationAcrossBoundary_ReportsCorrectOutcome(bool before)
    {
        string source = Copy("flac"); string hash = Hash(source);
        using CancellationTokenSource cancellation = new();
        MetadataFileWriter cancelling = new(new AtlMetadataEditStager(), (stage, file, backup) => File.Replace(stage, file, backup),
            phase => { if (phase == (before ? "BeforeReplace" : "AfterReplace")) cancellation.Cancel(); });
        if (before)
        {
            Assert.That(async () => await cancelling.WriteAsync(new Uri(source), new(Title: "Cancelled"), cancellationToken: cancellation.Token),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.That(Hash(source), Is.EqualTo(hash));
        }
        else
        {
            MetadataFileWriteResult result = await cancelling.WriteAsync(new Uri(source), new(Title: "Committed"), cancellationToken: cancellation.Token);
            Assert.That(new ATL.Track(source).Title, Is.EqualTo("Committed"));
            Assert.That(Hash(result.Backup.Backup.LocalPath), Is.EqualTo(hash));
        }
    }

    [Test]
    public async Task WriteAsync_PreviewCleanupFails_StillReportsTheSavedFileWithAWarning()
    {
        string source = Copy("flac");
        MetadataFileWriter flaky = new(new CleanupFailsStager(new AtlMetadataEditStager()));

        MetadataFileWriteResult result = await flaky.WriteAsync(new Uri(source), new(Title: "Saved anyway"), Hash(source));

        Assert.That(new ATL.Track(source).Title, Is.EqualTo("Saved anyway"), "the edit is committed before cleanup is attempted");
        Assert.That(result.Warning, Does.Contain("saved").And.Contain("cleanup failed").And.Contain("busy"));
        Assert.That(Hash(result.Backup.Backup.LocalPath), Is.Not.EqualTo(Hash(source)), "the backup still holds the original");
    }

    [Test]
    public void WriteAsync_StaleEditorFingerprint_RejectsWithoutReplacement()
    {
        string source = Copy("flac"); string hash = Hash(source);
        ATL.Track external = new(source) { Title = "External change" }; Assert.That(external.Save(), Is.True);
        Assert.That(async () => await writer.WriteAsync(new Uri(source), new(Title: "Stale dialog"), hash), Throws.TypeOf<IOException>());
        Assert.That(new ATL.Track(source).Title, Is.EqualTo("External change"));
        Assert.That(Directory.GetFiles(directory, "*.backup"), Is.Empty);
    }

    [Test]
    public void WriteAsync_SourceChangesBeforeReplacement_RejectsWithoutOverwriting()
    {
        string source = Copy("flac");
        MetadataFileWriter conflicting = new(new AtlMetadataEditStager(), (stage, file, backup) => File.Replace(stage, file, backup),
            phase => { if (phase == "BeforeReplace") File.WriteAllText(source, "External writer"); });
        Assert.That(async () => await conflicting.WriteAsync(new Uri(source), new(Title: "Edited")), Throws.TypeOf<IOException>());
        Assert.That(File.ReadAllText(source), Is.EqualTo("External writer"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RestoreAsync_SourceOrBackupChanged_RejectsWithoutReplacing(bool changeSource)
    {
        string source = Copy("flac");
        MetadataFileWriteResult result = await writer.WriteAsync(new Uri(source), new(Title: "Edited"));
        File.WriteAllText(changeSource ? source : result.Backup.Backup.LocalPath, "External change");
        string before = Hash(source);
        Assert.That(async () => await writer.RestoreAsync(result.Backup), Throws.TypeOf<IOException>());
        Assert.That(Hash(source), Is.EqualTo(before));
        Assert.That(await writer.GetBackupsAsync(new Uri(source)), Is.Empty);
    }

    [Test]
    public async Task RestoreAsync_ForgedReceipt_RejectsWithoutReplacing()
    {
        string source = Copy("flac");
        MetadataFileWriteResult result = await writer.WriteAsync(new Uri(source), new(Title: "Edited"));
        Assert.That(async () => await writer.RestoreAsync(result.Backup with { OriginalHash = new string('A', 64) }), Throws.TypeOf<InvalidDataException>());
        Assert.That(new ATL.Track(source).Title, Is.EqualTo("Edited"));
    }

    [Test]
    public async Task GetBackupsAsync_InvalidOrUnknownJournal_PreservesFiles()
    {
        string source = Copy("flac");
        MetadataFileWriteResult result = await writer.WriteAsync(new Uri(source), new(Title: "Edited"));
        await File.WriteAllTextAsync(result.Backup.Journal.LocalPath, "{invalid}");
        Assert.That(await writer.GetBackupsAsync(new Uri(source)), Is.Empty);
        Assert.That(File.Exists(result.Backup.Backup.LocalPath), Is.True);
        Assert.That(File.Exists(result.Backup.Journal.LocalPath), Is.True);
    }

    [Test]
    public async Task WriteAsync_SourceLeaseHeld_RejectsConcurrentWriter()
    {
        string source = Copy("flac"); string hash = Hash(source);
        MetadataFileWriter overlapping = new(new AtlMetadataEditStager(), (stage, file, backup) => File.Replace(stage, file, backup), phase =>
        {
            if (phase == "BeforeReplace")
                Assert.That(async () => await writer.WriteAsync(new Uri(source), new(Title: "Concurrent")), Throws.TypeOf<IOException>());
        });
        await overlapping.WriteAsync(new Uri(source), new(Title: "First"));
        Assert.That(Hash(source), Is.Not.EqualTo(hash));
    }

    [Test]
    public void WriteAsync_ReadOnlySource_Rejects()
    {
        string source = Copy("flac"); string hash = Hash(source);
        FileAttributes attributes = File.GetAttributes(source);
        try
        {
            File.SetAttributes(source, attributes | FileAttributes.ReadOnly);
            Assert.That(async () => await writer.WriteAsync(new Uri(source), new(Title: "Edited")), Throws.TypeOf<UnauthorizedAccessException>());
            Assert.That(Hash(source), Is.EqualTo(hash));
        }
        finally { File.SetAttributes(source, attributes); }
    }

    [Test]
    [Platform("Linux")]
    public void WriteAsync_SymbolicLink_Rejects()
    {
        string source = Copy("flac"); string link = Path.Combine(directory, "linked.flac");
        File.CreateSymbolicLink(link, source);
        Assert.That(async () => await writer.WriteAsync(new Uri(link), new(Title: "Edited")), Throws.TypeOf<InvalidDataException>());
        Assert.That(new ATL.Track(source).Title, Is.EqualTo("Fixture Tone"));
    }

    private string Copy(string extension)
    {
        string path = Path.Combine(directory, "fixture." + extension);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), path);
        return path;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class CleanupFailsStager(IMetadataEditStager inner) : IMetadataEditStager
    {
        public async Task<IStagedMetadataEdit> StageAsync(Uri location, MetadataEdit edit, CancellationToken cancellationToken = default) =>
            new FailingDispose(await inner.StageAsync(location, edit, cancellationToken));
    }

    private sealed class FailingDispose(IStagedMetadataEdit inner) : IStagedMetadataEdit
    {
        public Uri Source => inner.Source;
        public Uri StagedFile => inner.StagedFile;
        public string SourceHash => inner.SourceHash;
        public string StagedHash => inner.StagedHash;
        public MetadataEdit Edit => inner.Edit;
        public Task VerifyUnchangedAsync(CancellationToken cancellationToken = default) => inner.VerifyUnchangedAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            throw new IOException("the preview is busy");
        }
    }
}
