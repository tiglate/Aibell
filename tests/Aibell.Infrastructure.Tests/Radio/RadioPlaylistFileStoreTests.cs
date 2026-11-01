using System.Text;
using Aibell.Application.Radio;
using Aibell.Domain.Radio;
using Aibell.Infrastructure.Radio;
using Aibell.Infrastructure.Storage;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Radio;

[TestFixture]
public sealed class RadioPlaylistFileStoreTests
{
    private static readonly string[] HlsAndPlainNames = ["Rádio HLS", "Plain"];
    private static readonly int[] SkippedEntryThree = [3];
    private static readonly bool[] HlsThenPlainFlags = [true, false];
    private static readonly int[] SkippedEntryTwo = [2];

    private static readonly int[] UnsupportedEntries = [3, 4, 5];
    private static readonly string[] OrderedNames = ["First", "Second"];
    private string directory = null!;
    private readonly RadioPlaylistFileStore store = new();

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-station-playlists-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [Test]
    public async Task ReadAsync_M3u_PreservesTitlesAndQueryStringsAndReportsUnsupportedLocations()
    {
        Uri source = await WriteAsync("stations.m3u8", """
            #EXTM3U
            #EXTINF:-1,Rádio, Jazz
            https://radio.example.invalid/live?channel=1&quality=high
            http://other.example.invalid/live
            ../music.flac
            https://example.invalid/nested.pls
            https://user:password@example.invalid/private
            """);
        byte[] original = await File.ReadAllBytesAsync(source.LocalPath);

        RadioPlaylistContent result = await store.ReadAsync(source);

        Assert.Multiple(() =>
        {
            Assert.That(result.Stations, Has.Count.EqualTo(2));
            Assert.That(result.Stations[0].Name, Is.EqualTo("Rádio, Jazz"));
            Assert.That(result.Stations[0].Stream.Query, Is.EqualTo("?channel=1&quality=high"));
            Assert.That(result.Stations[1].Name, Is.EqualTo("other.example.invalid"));
            Assert.That(result.Issues.Select(issue => issue.Entry), Is.EqualTo(UnsupportedEntries));
            Assert.That(result.Stations.Select(station => station.Id).Distinct().Count(), Is.EqualTo(2));
        });
        Assert.That(await File.ReadAllBytesAsync(source.LocalPath), Is.EqualTo(original));
    }

    [Test]
    public async Task ReadAsync_Pls_OrdersSparseIndicesAndMatchesTitlesWithoutDependingOnLineOrder()
    {
        Uri source = await WriteAsync("stations.pls", """
            ; saved by another player
            [PLAYLIST]
            Title8=Second
            File8=https://second.example.invalid/live
            File2=https://first.example.invalid/live
            Title2=First
            NumberOfEntries=2
            Version=2
            Length2=-1
            """);

        RadioPlaylistContent result = await store.ReadAsync(source);

        Assert.Multiple(() =>
        {
            Assert.That(result.Stations.Select(station => station.Name), Is.EqualTo(OrderedNames));
            Assert.That(result.Stations[0].Stream.Host, Is.EqualTo("first.example.invalid"));
            Assert.That(result.Issues, Is.Empty);
        });
    }

    [TestCase("File1=https://example.invalid/live")]
    [TestCase("[playlist]\nFile0=https://example.invalid/live")]
    [TestCase("[playlist]\nFile10001=https://example.invalid/live")]
    [TestCase("[playlist]\nFile1=https://example.invalid/live\nFile1=https://other.invalid/live")]
    [TestCase("[playlist]\nTitle1=First\nTitle1=Second")]
    [TestCase("[playlist]\nTitlebad=First")]
    public async Task ReadAsync_AmbiguousOrInvalidPls_RejectsWholeDocument(string text)
    {
        Uri source = await WriteAsync("invalid.pls", text);
        await Assert.ThatAsync(async () => await store.ReadAsync(source), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task ReadAsync_M3uStationWithHlsAddress_ImportsItAsAStation()
    {
        Uri source = await WriteAsync("stations.m3u", """
            #EXTM3U
            #EXTINF:-1,Rádio HLS
            https://radio.example.invalid/live/master.m3u8?token=a&b=2
            #EXTINF:-1,Plain
            https://radio.example.invalid/live.mp3
            https://example.invalid/wrapper.m3u
            """);

        RadioPlaylistContent result = await store.ReadAsync(source);

        Assert.Multiple(() =>
        {
            Assert.That(result.Stations.Select(station => station.Name), Is.EqualTo(HlsAndPlainNames));
            Assert.That(result.Stations[0].IsHls, Is.True);
            Assert.That(result.Stations[0].Stream.Query, Is.EqualTo("?token=a&b=2"));
            Assert.That(result.Stations[1].IsHls, Is.False);
            Assert.That(result.Issues.Select(issue => issue.Entry), Is.EqualTo(SkippedEntryThree), "other playlist wrappers are still skipped");
        });
    }

    [Test]
    public async Task ReadAsync_M3u8FileListingStations_ImportsHlsAndDirectEntries()
    {
        Uri source = await WriteAsync("radios.m3u8", "#EXTM3U\n#EXTINF:-1,One\nhttps://a.example.invalid/hls/index.m3u8\n#EXTINF:-1,Two\nhttps://b.example.invalid/live\n");
        RadioPlaylistContent result = await store.ReadAsync(source);
        Assert.That(result.Stations.Select(station => station.IsHls), Is.EqualTo(HlsThenPlainFlags));
    }

    [Test]
    public async Task ReadAsync_PlsStationWithHlsAddress_ImportsIt()
    {
        Uri source = await WriteAsync("stations.pls", "[playlist]\nFile1=https://radio.example.invalid/hls/live.m3u8\nTitle1=Live HLS\nFile2=https://radio.example.invalid/list.pls\nNumberOfEntries=2\n");

        RadioPlaylistContent result = await store.ReadAsync(source);

        Assert.Multiple(() =>
        {
            Assert.That(result.Stations.Single().Name, Is.EqualTo("Live HLS"));
            Assert.That(result.Stations.Single().IsHls, Is.True);
            Assert.That(result.Issues.Select(issue => issue.Entry), Is.EqualTo(SkippedEntryTwo));
        });
    }

    [Test]
    public async Task ReadAsync_HlsManifest_RejectsWholeDocument()
    {
        Uri source = await WriteAsync("stream.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:10\nhttps://example.invalid/segment.ts");
        await Assert.ThatAsync(async () => await store.ReadAsync(source), Throws.TypeOf<NotSupportedException>());
    }

    [TestCase("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=64000\nhttps://example.invalid/low/index.m3u8\n")]
    [TestCase("#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:-1,Looks like a station\nhttps://example.invalid/live.m3u8\n")]
    public async Task ReadAsync_MasterOrMediaManifestFile_IsNotAStationListEvenWhenItNamesM3u8Addresses(string text)
    {
        Uri source = await WriteAsync("manifest.m3u8", text);
        await Assert.ThatAsync(async () => await store.ReadAsync(source), Throws.TypeOf<NotSupportedException>().With.Message.Contains("HLS manifest"));
    }

    [Test]
    public async Task ReadAsync_OversizedFileOrTooManyEntries_RejectsWholeDocument()
    {
        Uri source = await WriteAsync("large.m3u", new string(' ', 4 * 1024 * 1024 + 1));
        await Assert.ThatAsync(async () => await store.ReadAsync(source), Throws.TypeOf<InvalidDataException>());
        await File.WriteAllTextAsync(source.LocalPath, string.Concat(Enumerable.Repeat("https://example.invalid/live\n", 10001)));
        await Assert.ThatAsync(async () => await store.ReadAsync(source), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task ReadAsync_UnicodeBomAndInvalidEncoding_UsesExplicitEncodingPolicy()
    {
        Uri source = await WriteAsync("unicode.m3u8", "");
        await File.WriteAllTextAsync(source.LocalPath, "#EXTINF:-1,Rádio\nhttps://example.invalid/live", Encoding.Unicode);
        Assert.That((await store.ReadAsync(source)).Stations.Single().Name, Is.EqualTo("Rádio"));
        await File.WriteAllBytesAsync(source.LocalPath, [0xff, 0xff, 0xff]);
        await Assert.ThatAsync(async () => await store.ReadAsync(source), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task ReadAsync_RemoteSourceOrCancellation_DoesNotReadNetworkOrMissingFile()
    {
        await Assert.ThatAsync(async () => await store.ReadAsync(new Uri("https://example.invalid/stations.pls")), Throws.TypeOf<ArgumentException>());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThatAsync(async () => await store.ReadAsync(new Uri(Path.Combine(directory, "missing.m3u")), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task PrepareAndImport_DeduplicatesAgainstCatalogAndRechecksDuplicatesAtCommit()
    {
        Uri source = await WriteAsync("catalog.m3u", """
            #EXTINF:-1,Existing replacement
            https://existing.example.invalid/live
            #EXTINF:-1,First
            https://new.example.invalid/live
            #EXTINF:-1,Repeated
            https://new.example.invalid/live
            file:///tmp/music.flac
            """);
        byte[] original = await File.ReadAllBytesAsync(source.LocalPath);
        await using SqliteTrackRepository repository = new(Path.Combine(directory, "library.db"));
        RadioStation existing = new(Guid.NewGuid(), "Keep", new Uri("https://existing.example.invalid/live"), Rating: 4);
        await repository.AddStationAsync(existing);
        await repository.RecordStationPlayedAsync(existing.Id, DateTimeOffset.UtcNow);
        RadioPlaylistImportService service = new(store, repository);

        RadioPlaylistImportPreview preview = await service.PrepareAsync(source);
        Assert.Multiple(() =>
        {
            Assert.That(preview.Stations, Has.Count.EqualTo(1));
            Assert.That(preview.Stations.Single().Name, Is.EqualTo("First"));
            Assert.That(preview.Duplicates, Is.EqualTo(2));
            Assert.That(preview.Issues, Has.Count.EqualTo(1));
        });
        Assert.That(await repository.ListStationsAsync(), Has.Count.EqualTo(1));
        RadioStation concurrent = preview.Stations.Single() with { Id = Guid.NewGuid(), Name = "Saved during review" };
        await repository.AddStationAsync(concurrent);
        RadioStationImportResult result = await service.ImportAsync(preview);
        IReadOnlyList<RadioStation> stations = await repository.ListStationsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(new RadioStationImportResult(0, 1)));
            Assert.That(stations.Single(station => station.Id == existing.Id).PlayCount, Is.EqualTo(1));
            Assert.That(stations.Single(station => station.Id == concurrent.Id).Name, Is.EqualTo(concurrent.Name));
        });
        Assert.That(await repository.ListAsync(), Is.Empty);
        Assert.That(await File.ReadAllBytesAsync(source.LocalPath), Is.EqualTo(original));
    }

    private async Task<Uri> WriteAsync(string name, string text)
    {
        string path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, text);
        return new Uri(path);
    }
}
