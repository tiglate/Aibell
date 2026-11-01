using Aibell.Application.Devices;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Devices;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Devices;

[TestFixture]
public sealed class DeviceSyncServiceTests
{
    private string root = "";
    private string library = "";
    private string device = "";
    private RemovableVolume volume = null!;

    [SetUp]
    public void CreateFolders()
    {
        root = Directory.CreateTempSubdirectory("aibell-sync").FullName;
        library = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
        device = Directory.CreateDirectory(Path.Combine(root, "device")).FullName;
        volume = new RemovableVolume("test", "STICK", device, 1_000_000_000, 1_000_000_000, "vfat");
    }

    [TearDown]
    public void DeleteFolders() => Directory.Delete(root, recursive: true);

    private Track Song(string fileName, string title, string artist = "Band", string album = "Album", int number = 1, byte[]? content = null, string? extension = null)
    {
        string path = Path.Combine(library, fileName + (extension ?? ".mp3"));
        File.WriteAllBytes(path, content ?? [1, 2, 3, 4, 5]);
        return new Track(Guid.NewGuid(), new Uri(path), title, artist, album, TimeSpan.FromSeconds(200), TrackNumber: number);
    }

    private static DeviceSyncService Service(long? free = null) => new(_ => free);

    [Test]
    public async Task Execute_CopiesIntoArtistAlbumFoldersAndWritesAPlaylist()
    {
        Track one = Song("a", "One"), two = Song("b", "Two", number: 2);
        DeviceSyncService service = Service();

        DeviceSyncPlan plan = service.Plan(volume, [one, two], new DeviceSyncOptions());
        DeviceSyncResult result = await service.ExecuteAsync(plan);

        Assert.That(plan.CopyCount, Is.EqualTo(2));
        Assert.That(result, Has.Property("Copied").EqualTo(2).And.Property("Failures").Empty);
        Assert.That(File.ReadAllBytes(Path.Combine(device, "Music", "Band", "Album", "01 - One.mp3")), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.That(File.Exists(Path.Combine(device, "Music", "Band", "Album", "02 - Two.mp3")), Is.True);
        string playlist = File.ReadAllText(result.PlaylistPath!);
        Assert.That(playlist, Does.StartWith("#EXTM3U").And.Contain("Band/Album/01 - One.mp3").And.Contain("#EXTINF:200,Band - One"));
        Assert.That(Path.GetFileName(result.PlaylistPath), Is.EqualTo("Aibell.m3u8"));
        Assert.That(Directory.GetFiles(device, "*.aibell-part", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task Execute_RunTwice_SecondTimeEverythingIsUpToDateAndNothingIsRewritten()
    {
        Track one = Song("a", "One");
        DeviceSyncService service = Service();
        await service.ExecuteAsync(service.Plan(volume, [one], new DeviceSyncOptions()));
        string copy = Path.Combine(device, "Music", "Band", "Album", "01 - One.mp3");
        DateTime written = File.GetLastWriteTimeUtc(copy);

        DeviceSyncPlan again = service.Plan(volume, [one], new DeviceSyncOptions());
        DeviceSyncResult result = await service.ExecuteAsync(again);

        Assert.That(again.UpToDateCount, Is.EqualTo(1));
        Assert.That(again.CopyCount, Is.Zero);
        Assert.That(result.Copied, Is.Zero);
        Assert.That(File.GetLastWriteTimeUtc(copy), Is.EqualTo(written));
    }

    [Test]
    public async Task Execute_DifferentFileAtTheDestination_IsAConflictAndIsKeptUnlessReplacingIsAllowed()
    {
        Track one = Song("a", "One");
        string existing = Path.Combine(device, "Music", "Band", "Album", "01 - One.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllBytes(existing, [9, 9, 9]);
        DeviceSyncService service = Service();

        DeviceSyncPlan keep = service.Plan(volume, [one], new DeviceSyncOptions());
        DeviceSyncResult kept = await service.ExecuteAsync(keep);
        Assert.That(keep.ConflictCount, Is.EqualTo(1));
        Assert.That(kept, Has.Property("Conflicts").EqualTo(1).And.Property("Replaced").EqualTo(0));
        Assert.That(File.ReadAllBytes(existing), Is.EqualTo(new byte[] { 9, 9, 9 }), "a different file is never overwritten by default");

        DeviceSyncPlan replace = service.Plan(volume, [one], new DeviceSyncOptions(ReplaceDifferent: true));
        DeviceSyncResult replaced = await service.ExecuteAsync(replace);
        Assert.That(replaced.Replaced, Is.EqualTo(1));
        Assert.That(File.ReadAllBytes(existing), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void Plan_ExcludesMissingUnsupportedAndNonFileTracksWithReasons()
    {
        Track missing = Song("gone", "Gone");
        File.Delete(missing.Location.LocalPath);
        Track unsupported = Song("doc", "Doc", extension: ".txt");
        Track disc = new(Guid.NewGuid(), new Uri("cdda:///dev/sr0?track=1"), "Disc", "x", "y", TimeSpan.FromMinutes(1));
        Track good = Song("ok", "Ok");

        DeviceSyncPlan plan = Service().Plan(volume, [missing, unsupported, disc, good], new DeviceSyncOptions());

        Assert.That(plan.Items.Select(item => item.Track), Is.EqualTo(Items(good)));
        Assert.That(plan.Excluded.Select(exclusion => exclusion.Reason), Is.EqualTo(Items("Missing", "Unsupported", "NotAFile")));
    }

    [Test]
    public void Plan_SameDestinationTwice_GetsANumberedSecondFile()
    {
        Track first = Song("a", "Same"), second = Song("b", "Same");

        DeviceSyncPlan plan = Service().Plan(volume, [first, second], new DeviceSyncOptions());

        Assert.That(plan.Items.Select(item => item.RelativePath), Is.EqualTo(Items("Music/Band/Album/01 - Same.mp3", "Music/Band/Album/01 - Same (2).mp3")));
    }

    [Test]
    public void Plan_FolderOptionsCannotEscapeTheDevice()
    {
        DeviceSyncPlan plan = Service().Plan(volume, [Song("a", "One")], new DeviceSyncOptions(MusicFolder: "../outside", PlaylistName: "../../pl"));

        Assert.That(plan.MusicRoot, Does.StartWith(device));
        Assert.That(plan.Items.Single().RelativePath.Split('/'), Does.Not.Contain(".."));
        Assert.That(Path.GetFullPath(Path.Combine(device, plan.Items.Single().RelativePath)), Does.StartWith(plan.MusicRoot));
        Assert.That(plan.Options.PlaylistName, Does.Not.Contain("/"));
    }

    [Test]
    public void Plan_TotalsAndFit()
    {
        RemovableVolume tiny = volume with { FreeBytes = 8 };
        DeviceSyncPlan plan = Service().Plan(tiny, [Song("a", "One"), Song("b", "Two", number: 2)], new DeviceSyncOptions());

        Assert.That(plan.BytesToCopy, Is.EqualTo(10));
        Assert.That(plan.Fits, Is.False);
        Assert.That(Service().Plan(volume, [Song("c", "Three")], new DeviceSyncOptions()).Fits, Is.True);
    }

    [Test]
    public async Task Execute_NotEnoughFreeSpace_StopsBeforeWritingAnything()
    {
        DeviceSyncService service = Service(free: 2);
        DeviceSyncPlan plan = service.Plan(volume, [Song("a", "One")], new DeviceSyncOptions());

        await Assert.ThatAsync(() => service.ExecuteAsync(plan), Throws.InstanceOf<IOException>().With.Message.Contains("free"));
        Assert.That(Directory.GetFileSystemEntries(device), Is.Empty);
    }

    [Test]
    public async Task Execute_DeviceRemoved_Throws()
    {
        DeviceSyncService service = Service();
        DeviceSyncPlan plan = service.Plan(volume, [Song("a", "One")], new DeviceSyncOptions());
        Directory.Delete(device, recursive: true);

        await Assert.ThatAsync(() => service.ExecuteAsync(plan), Throws.InstanceOf<DirectoryNotFoundException>());
    }

    [Test]
    public async Task Execute_OneUnreadableFile_IsReportedAndTheOthersStillCopy()
    {
        Track bad = Song("bad", "Bad"), good = Song("good", "Good", number: 2);
        DeviceSyncService service = Service();
        DeviceSyncPlan plan = service.Plan(volume, [bad, good], new DeviceSyncOptions());
        File.Delete(bad.Location.LocalPath);

        DeviceSyncResult result = await service.ExecuteAsync(plan);

        Assert.That(result.Copied, Is.EqualTo(1));
        Assert.That(result.Failures, Has.Count.EqualTo(1));
        Assert.That(result.Failures[0], Does.Contain("01 - Bad.mp3"));
        Assert.That(File.Exists(Path.Combine(device, "Music", "Band", "Album", "02 - Good.mp3")), Is.True);
        Assert.That(Directory.GetFiles(device, "*.aibell-part", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task Execute_PlanTamperedToLeaveTheMusicFolder_WritesNothingOutsideIt()
    {
        Track song = Song("a", "One");
        DeviceSyncService service = Service();
        DeviceSyncPlan honest = service.Plan(volume, [song], new DeviceSyncOptions());
        SyncItem escaping = honest.Items[0] with { RelativePath = "../escaped.mp3" };
        DeviceSyncPlan tampered = honest with { Items = [escaping] };

        DeviceSyncResult result = await service.ExecuteAsync(tampered);

        Assert.That(result.Copied, Is.Zero);
        Assert.That(result.Failures, Has.Count.EqualTo(1));
        Assert.That(result.Failures[0], Does.Contain("outside the music folder"));
        Assert.That(File.Exists(Path.Combine(root, "escaped.mp3")), Is.False);
        Assert.That(Directory.GetFiles(root, "escaped*", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task Execute_PlaylistThatCannotBeWritten_IsReportedAndTheSongsAreStillOnTheDevice()
    {
        Track song = Song("a", "One");
        DeviceSyncService service = Service();
        DeviceSyncPlan plan = service.Plan(volume, [song], new DeviceSyncOptions());
        // A folder where the playlist file should go makes the final move fail.
        Directory.CreateDirectory(Path.Combine(device, "Music", "Aibell.m3u8"));

        DeviceSyncResult result = await service.ExecuteAsync(plan);

        Assert.That(result.Copied, Is.EqualTo(1));
        Assert.That(result.PlaylistPath, Is.Null);
        Assert.That(result.Failures, Has.Count.EqualTo(1));
        Assert.That(result.Failures[0], Does.StartWith("Aibell.m3u8:"));
        Assert.That(File.Exists(Path.Combine(device, "Music", "Band", "Album", "01 - One.mp3")), Is.True);
        Assert.That(Directory.GetFiles(device, "*.aibell-part", SearchOption.AllDirectories), Is.Empty, "no half-written playlist is left behind");
    }

    [Test]
    public async Task Execute_FolderTheUserCannotWriteTo_IsReportedWithoutStoppingOrTouchingOtherFiles()
    {
        Assume.That(!OperatingSystem.IsWindows() && Environment.UserName != "root", "needs POSIX permissions and a non-root user");
        Track locked = Song("a", "Locked", artist: "Closed"), open = Song("b", "Open", artist: "Open", number: 2);
        DeviceSyncService service = Service();
        DeviceSyncPlan plan = service.Plan(volume, [locked, open], new DeviceSyncOptions());
        string closedFolder = Path.Combine(device, "Music", "Closed", "Album");
        Directory.CreateDirectory(closedFolder);
        File.WriteAllText(Path.Combine(device, "Music", "Closed", "Album", "keep.txt"), "the user's own file");
#pragma warning disable CA1416 // guarded by the Assume above
        File.SetUnixFileMode(closedFolder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        try
        {
            DeviceSyncResult result = await service.ExecuteAsync(plan);

            Assert.That(result.Failures, Has.Count.EqualTo(1));
            Assert.That(result.Failures[0], Does.Contain("01 - Locked.mp3"));
            Assert.That(result.Copied, Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(device, "Music", "Open", "Album", "02 - Open.mp3")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(closedFolder, "keep.txt")), Is.EqualTo("the user's own file"));
            Assert.That(Directory.GetFiles(device, "*.aibell-part", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
#pragma warning disable CA1416
            File.SetUnixFileMode(closedFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        }
    }

    [Test]
    public async Task Execute_WithTheRealFreeSpaceProbe_CopiesIntoAnOrdinaryFolder()
    {
        Track song = Song("a", "One");
        DeviceSyncService service = new();
        DeviceSyncPlan plan = service.Plan(volume, [song], new DeviceSyncOptions());

        DeviceSyncResult result = await service.ExecuteAsync(plan);

        Assert.That(result, Has.Property("Copied").EqualTo(1).And.Property("Failures").Empty);
    }

    [Test]
    public async Task Execute_Cancelled_StopsCleansUpAndWritesNoPlaylist()
    {
        Track big = Song("big", "Big", content: new byte[3 * 1024 * 1024]), next = Song("next", "Next", number: 2);
        DeviceSyncService service = Service();
        DeviceSyncPlan plan = service.Plan(volume, [big, next], new DeviceSyncOptions());
        using CancellationTokenSource cancel = new();
        Progress progress = new(report => { if (report.BytesCopied > 0) cancel.Cancel(); });

        DeviceSyncResult result = await service.ExecuteAsync(plan, progress, cancel.Token);

        Assert.That(result.Cancelled, Is.True);
        Assert.That(result.PlaylistPath, Is.Null);
        Assert.That(File.Exists(Path.Combine(device, "Music", "Band", "Album", "02 - Next.mp3")), Is.False);
        Assert.That(Directory.GetFiles(device, "*.aibell-part", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task Execute_ReportsProgressUpToTheTotal()
    {
        DeviceSyncService service = Service();
        DeviceSyncPlan plan = service.Plan(volume, [Song("a", "One"), Song("b", "Two", number: 2)], new DeviceSyncOptions());
        List<DeviceSyncProgress> reports = [];

        await service.ExecuteAsync(plan, new Progress(reports.Add));

        Assert.That(reports.Last(), Has.Property("Completed").EqualTo(2).And.Property("Total").EqualTo(2).And.Property("BytesCopied").EqualTo(10));
    }

    [Test]
    public async Task Execute_PlaylistCanBeSwitchedOff()
    {
        DeviceSyncService service = Service();
        DeviceSyncResult result = await service.ExecuteAsync(service.Plan(volume, [Song("a", "One")], new DeviceSyncOptions(WritePlaylist: false)));

        Assert.That(result.PlaylistPath, Is.Null);
        Assert.That(Directory.GetFiles(Path.Combine(device, "Music"), "*.m3u8"), Is.Empty);
    }

    [Test]
    public async Task Execute_KeepsTheSourceModificationTime()
    {
        Track one = Song("a", "One");
        File.SetLastWriteTimeUtc(one.Location.LocalPath, new DateTime(2020, 5, 6, 7, 8, 10, DateTimeKind.Utc));
        DeviceSyncService service = Service();

        await service.ExecuteAsync(service.Plan(volume, [one], new DeviceSyncOptions()));

        Assert.That(File.GetLastWriteTimeUtc(Path.Combine(device, "Music", "Band", "Album", "01 - One.mp3")), Is.EqualTo(new DateTime(2020, 5, 6, 7, 8, 10, DateTimeKind.Utc)));
    }

    private sealed class Progress(Action<DeviceSyncProgress> report) : IProgress<DeviceSyncProgress>
    {
        public void Report(DeviceSyncProgress value) => report(value);
    }

    private static T[] Items<T>(params T[] items) => items;
}
