using System.Security.Cryptography;
using Aibell.Application.Library;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class MetadataEditRecoveryTests
{
    private string directory = null!;
    private readonly MetadataEditRecovery recovery = new();

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task Journal_ExtendedAndOriginalPatches_RoundTripWithoutLosingFields()
    {
        string stage = Path.Combine(directory, $".aibell-edit-{Guid.NewGuid():N}.stage");
        MetadataEdit patch = new(AlbumArtist: "Album artist", Composer: "Composer 日本語", Conductor: "Conductor",
            Grouping: "Suite", Copyright: "2026", SortTitle: "Title", SortArtist: "Artist",
            SortAlbumArtist: "Album artist", SortAlbum: "Album", TrackTotal: 12, DiscNumber: 2,
            DiscTotal: 3, BeatsPerMinute: 120.5f, Keywords: "Keywords 日本語", LicenseUri: "https://example.com/license",
            ShowName: "Show ação", SeasonNumber: 2, EpisodeNumber: 7);
        MetadataEditJournal journal = new(1, "fixture.m4a", new string('A', 64), null, false, patch);
        journal.Save(stage, first: true);
        string path = MetadataEditJournal.JournalPath(stage);
        Assert.That((await MetadataEditJournal.LoadAsync(path, default)).Edit, Is.EqualTo(patch));
        // Journals written before the extended fields existed still deserialize.
        string oldPatch = "{\"Title\":\"Old preview\"}";
        string json = System.Text.Json.JsonSerializer.Serialize(journal);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        json = json.Replace(document.RootElement.GetProperty("Edit").GetRawText(), oldPatch, StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, json);
        Assert.That((await MetadataEditJournal.LoadAsync(path, default)).Edit, Is.EqualTo(new MetadataEdit(Title: "Old preview")));
    }

    [Test]
    public async Task ScanAsync_AbandonedValidatedPreview_DiscardPreservesOriginal()
    {
        Snapshot snapshot = await AbandonedAsync();
        string original = Hash(snapshot.Source);
        MetadataEditRecoveryScan scan = await recovery.ScanAsync([snapshot.Directory]);
        Assert.That(scan.Issues, Is.Empty);
        PendingMetadataEdit edit = scan.Pending.Single();
        Assert.That(edit.State, Is.EqualTo(PendingMetadataEditState.Validated));
        await recovery.DiscardAsync(edit);
        Assert.That(Hash(snapshot.Source), Is.EqualTo(original));
        Assert.That(Directory.GetFiles(snapshot.Directory), Is.EqualTo(new[] { snapshot.Source }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ScanAsync_InterruptedPreparation_CanDiscardPartialOrMissingPreview(bool partial)
    {
        Snapshot snapshot = await AbandonedAsync();
        MetadataEditJournal journal = await MetadataEditJournal.LoadAsync(snapshot.Journal, default);
        (journal with { Ready = false, StageHash = null }).Save(snapshot.Stage, first: false);
        if (partial) await File.WriteAllTextAsync(snapshot.Stage, "partial copy");
        else File.Delete(snapshot.Stage);
        PendingMetadataEdit edit = (await recovery.ScanAsync([snapshot.Directory])).Pending.Single();
        Assert.That(edit.State, Is.EqualTo(PendingMetadataEditState.Incomplete));
        await recovery.DiscardAsync(edit);
        Assert.That(Directory.GetFiles(snapshot.Directory), Is.EqualTo(new[] { snapshot.Source }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ScanAsync_SourceOrValidatedPreviewChanged_PreservesFilesAndReportsIssue(bool source)
    {
        Snapshot snapshot = await AbandonedAsync();
        await File.WriteAllTextAsync(source ? snapshot.Source : snapshot.Stage, "external change");
        MetadataEditRecoveryScan scan = await recovery.ScanAsync([snapshot.Directory]);
        Assert.That(scan.Pending, Is.Empty);
        Assert.That(scan.Issues, Has.Count.EqualTo(1));
        Assert.That(File.Exists(snapshot.Stage), Is.True);
        Assert.That(File.Exists(snapshot.Journal), Is.True);
    }

    [TestCase("corrupt")]
    [TestCase("version")]
    [TestCase("traversal")]
    [TestCase("invalid-edit")]
    public async Task ScanAsync_InvalidJournal_PreservesPreviewAndReportsIssue(string kind)
    {
        Snapshot snapshot = await AbandonedAsync();
        MetadataEditJournal journal = await MetadataEditJournal.LoadAsync(snapshot.Journal, default);
        if (kind == "corrupt") await File.WriteAllTextAsync(snapshot.Journal, "not json");
        else
        {
            journal = kind switch
            {
                "version" => journal with { Version = 2 },
                "traversal" => journal with { SourceName = "../outside.flac" },
                _ => journal with { Edit = new MetadataEdit(Year: 10000) }
            };
            journal.Save(snapshot.Stage, first: false);
        }
        MetadataEditRecoveryScan scan = await recovery.ScanAsync([snapshot.Directory]);
        Assert.That(scan.Pending, Is.Empty);
        Assert.That(scan.Issues, Has.Count.EqualTo(1));
        Assert.That(File.Exists(snapshot.Stage), Is.True);
    }

    [Test]
    public async Task ScanAsync_ActivePreview_SkipsOwnedEditAndNormalDisposeRemovesJournal()
    {
        string source = CopySource(directory);
        await using IStagedMetadataEdit stage = await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: "Preview"));
        Assert.That(File.Exists(MetadataEditJournal.JournalPath(stage.StagedFile.LocalPath)), Is.True);
        MetadataEditRecoveryScan scan = await recovery.ScanAsync([directory]);
        Assert.That(scan.Pending, Is.Empty);
        Assert.That(scan.Issues, Is.Empty);
        await stage.DisposeAsync();
        Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { source }));
    }

    [Test]
    public async Task DiscardAsync_LeaseAcquiredAfterScan_RejectsWithoutDeletingFiles()
    {
        Snapshot snapshot = await AbandonedAsync();
        PendingMetadataEdit edit = (await recovery.ScanAsync([snapshot.Directory])).Pending.Single();
        using FileStream owner = MetadataEditJournal.AcquireLease(snapshot.Stage);
        Assert.That(async () => await recovery.DiscardAsync(edit), Throws.InstanceOf<IOException>());
        Assert.That(File.Exists(snapshot.Stage), Is.True);
    }

    [TestCase("source")]
    [TestCase("stage")]
    [TestCase("journal")]
    public async Task DiscardAsync_InputsChangedAfterScan_RejectsWithoutDeletingFiles(string changed)
    {
        Snapshot snapshot = await AbandonedAsync();
        PendingMetadataEdit edit = (await recovery.ScanAsync([snapshot.Directory])).Pending.Single();
        string path = changed switch { "source" => snapshot.Source, "stage" => snapshot.Stage, _ => snapshot.Journal };
        await File.AppendAllTextAsync(path, "\n");
        Assert.That(async () => await recovery.DiscardAsync(edit), Throws.InstanceOf<IOException>());
        Assert.That(File.Exists(snapshot.Stage), Is.True);
        Assert.That(File.Exists(snapshot.Journal), Is.True);
    }

    [Test]
    public async Task DiscardAsync_Cancelled_PreservesPreviewJournalAndOriginal()
    {
        Snapshot snapshot = await AbandonedAsync();
        PendingMetadataEdit edit = (await recovery.ScanAsync([snapshot.Directory])).Pending.Single();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.That(async () => await recovery.DiscardAsync(edit, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(File.Exists(snapshot.Stage), Is.True);
        Assert.That(File.Exists(snapshot.Journal), Is.True);
        Assert.That(Hash(snapshot.Source), Is.EqualTo(edit.SourceHash));
    }

    [Test]
    public async Task ScanAsync_UnrecognizedFiles_LeavesThemUntouched()
    {
        string stage = Path.Combine(directory, ".aibell-edit-unknown.stage");
        string journal = Path.Combine(directory, ".aibell-edit-not-a-guid.json");
        await File.WriteAllTextAsync(stage, "unknown preview");
        await File.WriteAllTextAsync(journal, "{}");
        MetadataEditRecoveryScan scan = await recovery.ScanAsync([directory]);
        Assert.That(scan.Pending, Is.Empty);
        Assert.That(scan.Issues, Has.Count.EqualTo(1));
        Assert.That(await File.ReadAllTextAsync(stage), Is.EqualTo("unknown preview"));
        Assert.That(await File.ReadAllTextAsync(journal), Is.EqualTo("{}"));
    }

    [Test]
    [Platform("Linux")]
    public async Task ScanAsync_DanglingLeaseSymbolicLink_DoesNotCreateItsTarget()
    {
        Snapshot snapshot = await AbandonedAsync();
        string target = Path.Combine(directory, "unrelated-file");
        File.CreateSymbolicLink(MetadataEditJournal.LeasePath(snapshot.Stage), target);
        MetadataEditRecoveryScan scan = await recovery.ScanAsync([snapshot.Directory]);
        Assert.That(scan.Pending, Is.Empty);
        Assert.That(scan.Issues, Has.Count.EqualTo(1));
        Assert.That(File.Exists(target), Is.False);
        Assert.That(File.Exists(snapshot.Stage), Is.True);
    }

    [Test]
    public async Task ScanAsync_AbandonedArtworkEdit_RetainsImagePatchAndDiscardsOnlyPreview()
    {
        byte[] image = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "artwork", "cover.png"));
        Snapshot snapshot = await AbandonedAsync(new(Artwork: new(ImageData: image)));
        string original = Hash(snapshot.Source);
        MetadataEditJournal journal = await MetadataEditJournal.LoadAsync(snapshot.Journal, default);
        Assert.That(journal.Edit.Artwork!.ImageData, Is.EqualTo(image));
        MetadataEditRecoveryScan scan = await recovery.ScanAsync([snapshot.Directory]);
        Assert.That(scan.Issues, Is.Empty); Assert.That(scan.Pending, Has.Count.EqualTo(1));
        await recovery.DiscardAsync(scan.Pending[0]);
        Assert.That(Hash(snapshot.Source), Is.EqualTo(original));
        Assert.That(File.Exists(snapshot.Stage), Is.False);
    }

    [Test]
    public async Task ScanRecursivelyAsync_NestedAndOverlappingRoots_InspectsEachFolderOnce()
    {
        Snapshot snapshot = await AbandonedAsync();
        var shallow = await recovery.ScanAsync([directory]);
        Assert.That(shallow.Pending, Is.Empty);
        var recursive = await recovery.ScanRecursivelyAsync([directory, snapshot.Directory, snapshot.Directory + Path.DirectorySeparatorChar]);
        Assert.That(recursive.Pending, Has.Count.EqualTo(1));
        Assert.That(recursive.Issues, Is.Empty);
        Assert.That(recursive.Pending.Single().Source.LocalPath, Is.EqualTo(snapshot.Source));
    }

    [Test]
    public async Task ScanRecursivelyAsync_UnavailableRoot_ReportsIssueAndContinuesOtherRoots()
    {
        Snapshot snapshot = await AbandonedAsync();
        var scan = await recovery.ScanRecursivelyAsync([Path.Combine(directory, "missing"), snapshot.Directory]);
        Assert.That(scan.Pending, Has.Count.EqualTo(1));
        Assert.That(scan.Issues, Has.Count.EqualTo(1));
        Assert.That(scan.Issues.Single().Message, Does.Contain("unavailable"));
    }

    [Test]
    public async Task ScanRecursivelyAsync_LinkedFolder_DoesNotInspectOrCreateLeaseInTarget()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Creating symlinks requires Windows privileges.");
        Snapshot snapshot = await AbandonedAsync();
        string root = Directory.CreateDirectory(Path.Combine(directory, "scan-root")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), snapshot.Directory);
        var scan = await recovery.ScanRecursivelyAsync([root]);
        Assert.That(scan.Pending, Is.Empty);
        Assert.That(scan.Issues, Has.Count.EqualTo(1));
        Assert.That(File.Exists(MetadataEditJournal.LeasePath(snapshot.Stage)), Is.False);
        Assert.That(File.Exists(snapshot.Stage), Is.True);
    }

    [Test]
    public async Task DiscardAsync_FolderRedirectedAfterInspection_PreservesFiles()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Creating symlinks requires Windows privileges.");
        Snapshot snapshot = await AbandonedAsync();
        var scan = await recovery.ScanAsync([snapshot.Directory]);
        string moved = snapshot.Directory + "-moved";
        Directory.Move(snapshot.Directory, moved);
        Directory.CreateSymbolicLink(snapshot.Directory, moved);
        Assert.That(async () => await recovery.DiscardAsync(scan.Pending.Single()), Throws.TypeOf<InvalidDataException>());
        Assert.That(File.Exists(Path.Combine(moved, Path.GetFileName(snapshot.Stage))), Is.True);
        Assert.That(File.Exists(Path.Combine(moved, Path.GetFileName(snapshot.Source))), Is.True);
    }

    [Test]
    public async Task ScanRecursivelyAsync_Cancelled_DoesNotCreateInspectionArtifacts()
    {
        Snapshot snapshot = await AbandonedAsync();
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.That(async () => await recovery.ScanRecursivelyAsync([directory], cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(File.Exists(MetadataEditJournal.LeasePath(snapshot.Stage)), Is.False);
    }

    private async Task<Snapshot> AbandonedAsync(Aibell.Domain.Media.MetadataEdit? patch = null)
    {
        string live = Path.Combine(directory, "live");
        string abandoned = Path.Combine(directory, "abandoned");
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(abandoned);
        string source = CopySource(live);
        await using IStagedMetadataEdit stage = await new AtlMetadataEditStager().StageAsync(new Uri(source), patch ?? new(Title: "Preview"));
        // Clone the crash-visible files into a separate directory, where no live
        // process owns the lease. The actual stage remains owned and disposed.
        string sourceCopy = Path.Combine(abandoned, Path.GetFileName(source));
        string stageCopy = Path.Combine(abandoned, Path.GetFileName(stage.StagedFile.LocalPath));
        string journalCopy = MetadataEditJournal.JournalPath(stageCopy);
        File.Copy(source, sourceCopy);
        File.Copy(stage.StagedFile.LocalPath, stageCopy);
        File.Copy(MetadataEditJournal.JournalPath(stage.StagedFile.LocalPath), journalCopy);
        return new(abandoned, sourceCopy, stageCopy, journalCopy);
    }

    private static string CopySource(string directory)
    {
        string source = Path.Combine(directory, "fixture.flac");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.flac"), source);
        return source;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed record Snapshot(string Directory, string Source, string Stage, string Journal);
}
