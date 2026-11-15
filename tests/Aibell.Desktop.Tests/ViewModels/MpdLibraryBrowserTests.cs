using Aibell.Desktop.Services;
using Aibell.Desktop.TestSupport;
using Aibell.Domain.Media;
using Aibell.Mpd.Client;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.ViewModels;

[TestFixture]
public sealed class MpdLibraryBrowserTests
{
    private sealed class FakeSource(IReadOnlyList<MpdSong> songs) : IMpdLibrarySource
    {
        public bool IsConnected { get; set; } = true;
        public int Fetches { get; private set; }
        public Task<IReadOnlyList<MpdSong>> GetAllSongsAsync(CancellationToken cancellationToken) { Fetches++; return Task.FromResult(songs); }
        public Task<IReadOnlyList<MpdSong>> GetPlaylistSongsAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MpdSong>>([songs[2], songs[0]]);
        public Task RemoveFromPlaylistAsync(string name, string songUri, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (MpdLibraryBrowser Browser, FakeSource Source) Create()
    {
        FakeSource source = new(new FakeMpdClient().Library);
        return (new MpdLibraryBrowser(source), source);
    }

    [Test]
    public async Task Facets_CountTheSongsOfTheServer()
    {
        (MpdLibraryBrowser browser, _) = Create();
        Assert.That((await browser.ListGenresAsync()).Select(item => (item.Name, item.TrackCount)), Is.EqualTo(new[] { ("", 1), ("Jazz", 1), ("Rock", 2) }), "no genre first, then by name");
        Assert.That((await browser.ListArtistsAsync(null)).Select(item => (item.Name, item.TrackCount)), Is.EqualTo(new[] { ("", 1), ("Band A", 2), ("Band B", 1) }));
        Assert.That((await browser.ListArtistsAsync("Rock")).Select(item => item.Name), Is.EqualTo(new[] { "Band A" }));
        Assert.That((await browser.ListAlbumsAsync(null, null)).Select(item => (item.Artist, item.Title, item.TrackCount)),
            Is.EqualTo(new[] { ("", "", 1), ("Band A", "First", 2), ("Band B", "Second", 1) }));
        Assert.That((await browser.ListAlbumsAsync("Jazz", null)).Single().Title, Is.EqualTo("Second"));
        Assert.That((await browser.ListAlbumsAsync(null, "band a")).Single().Title, Is.EqualTo("First"), "names match without regard to case");
        Assert.That(await browser.ListAlbumsAsync("Jazz", "Band A"), Is.Empty);
    }

    [Test]
    public async Task Tracks_AreFilteredSearchedAndOrderedLikeAnAlbumList()
    {
        (MpdLibraryBrowser browser, _) = Create();
        Assert.That((await browser.GetTracksAsync(null, null, null, "")).Select(track => track.Title), Is.EqualTo(new[] { "Loose", "One", "Two", "Blue" }),
            "by artist, album, then disc and track number");
        Assert.That((await browser.GetTracksAsync("Rock", null, null, "")).Select(track => track.Title), Is.EqualTo(new[] { "One", "Two" }));
        Assert.That((await browser.GetTracksAsync(null, "Band B", "Second", "")).Single().Title, Is.EqualTo("Blue"));
        Assert.That((await browser.GetTracksAsync(null, null, "", "")).Single().Title, Is.EqualTo("Loose"), "an empty album name is the songs without one");
        Assert.That((await browser.GetTracksAsync(null, null, null, " BLUE ")).Single().Title, Is.EqualTo("Blue"));
        Assert.That((await browser.GetTracksAsync(null, null, null, "jazz")).Single().Title, Is.EqualTo("Blue"));
        Assert.That((await browser.GetTracksAsync(null, null, null, "band a")).Count, Is.EqualTo(2));
        Assert.That((await browser.GetTracksAsync(null, null, null, "loose.mp3")).Single().Title, Is.EqualTo("Loose"), "the file name is searched too");
        Assert.That(await browser.GetTracksAsync(null, null, null, "zzz"), Is.Empty);
        Track one = (await browser.GetTracksAsync(null, null, null, "one")).Single();
        Assert.That((one.TrackNumber, one.Source, MpdTracks.TryGetSongUri(one.Location, out string path) ? path : ""), Is.EqualTo((1, "MPD", "a/one.flac")));
    }

    [Test]
    public async Task PlaylistTracks_KeepTheirStoredOrder()
    {
        (MpdLibraryBrowser browser, FakeSource source) = Create();
        Assert.That((await browser.GetPlaylistTracksAsync("mix", "")).Select(track => track.Title), Is.EqualTo(new[] { "Blue", "Two" }));
        Assert.That((await browser.GetPlaylistTracksAsync("mix", "two")).Select(track => track.Title), Is.EqualTo(new[] { "Two" }));
        source.IsConnected = false;
        Assert.That(await browser.GetPlaylistTracksAsync("mix", ""), Is.Empty);
    }

    [Test]
    public async Task TheLibraryIsFetchedOnceUntilInvalidated_AndEmptyWhenNotConnected()
    {
        (MpdLibraryBrowser browser, FakeSource source) = Create();
        await browser.ListGenresAsync();
        await browser.ListArtistsAsync(null);
        await browser.GetTracksAsync(null, null, null, "");
        Assert.That(source.Fetches, Is.EqualTo(1));
        browser.Invalidate();
        await browser.ListGenresAsync();
        Assert.That(source.Fetches, Is.EqualTo(2));
        source.IsConnected = false;
        browser.Invalidate();
        Assert.That(await browser.ListGenresAsync(), Is.Empty);
        Assert.That(source.Fetches, Is.EqualTo(2), "nothing is asked of a server that is not connected");
    }
}
