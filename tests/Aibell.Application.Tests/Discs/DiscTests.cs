using Aibell.Application.Discs;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Application.Tests.Discs;

[TestFixture]
public sealed class DiscTests
{
    private static readonly DiscToc Toc = new([150, 15363, 32314], 46592);
    private static readonly OpticalDrive Drive = new("/dev/sr0", "CD drive");

    [Test]
    public void DurationOf_UsesNextTrackOrLeadOut()
    {
        Assert.That(Toc.DurationOf(0), Is.EqualTo(TimeSpan.FromSeconds(15213 / 75.0)));
        Assert.That(Toc.DurationOf(2), Is.EqualTo(TimeSpan.FromSeconds(14278 / 75.0)));
    }

    [Test]
    public void SectorRangeOf_RemovesTheLeadInAndEndsAtTheNextTrackOrLeadOut()
    {
        Assert.That(Toc.SectorRangeOf(0), Is.EqualTo((0, 15213)));
        Assert.That(Toc.SectorRangeOf(2), Is.EqualTo((32164, 46442)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Toc.SectorRangeOf(3));
    }

    [TestCase(0)]
    [TestCase(100)]
    public void Constructor_BadTrackCount_Throws(int count) =>
        Assert.Throws<ArgumentException>(() => _ = new DiscToc([.. Enumerable.Range(0, count).Select(i => 150 + i * 10)], 5000));

    [Test]
    public void Constructor_OffsetsNotIncreasing_Throws() =>
        Assert.Throws<ArgumentException>(() => _ = new DiscToc([150, 100], 5000));

    [Test]
    public void Constructor_LeadOutBeforeLastTrack_Throws() =>
        Assert.Throws<ArgumentException>(() => _ = new DiscToc([150, 2000], 2000));

    [Test]
    public void LastTrackNumber_RespectsFirstTrackNumber() =>
        Assert.That(new DiscToc([150, 300], 600, firstTrackNumber: 3).LastTrackNumber, Is.EqualTo(4));

    [TestCase("/dev/sr0", 3, "cdda:///dev/sr0?track=3")]
    [TestCase("E:", 12, "cdda:///E:?track=12")]
    public void Create_ThenTryParse_RoundTrips(string drive, int track, string expected)
    {
        Uri location = DiscLocation.Create(drive, track);

        Assert.That(location.AbsoluteUri, Is.EqualTo(expected));
        Assert.That(DiscLocation.TryParse(location, out string parsedDrive, out int parsedTrack), Is.True);
        Assert.That(parsedDrive, Is.EqualTo(drive));
        Assert.That(parsedTrack, Is.EqualTo(track));
    }

    [TestCase("file:///music/a.mp3")]
    [TestCase("cdda:///dev/sr0")]
    [TestCase("cdda:///dev/sr0?track=0")]
    [TestCase("cdda:///dev/sr0?track=abc")]
    [TestCase("cdda:///dev/sr0?track=1&x=2")]
    public void TryParse_NotADiscTrack_ReturnsFalse(string text) =>
        Assert.That(DiscLocation.TryParse(new Uri(text), out _, out _), Is.False);

    [Test]
    public void TryParse_Null_ReturnsFalse() => Assert.That(DiscLocation.TryParse(null, out _, out _), Is.False);

    [Test]
    public void Build_WithoutDetails_UsesGenericTitlesAndDurations()
    {
        IReadOnlyList<Track> tracks = DiscTracks.Build(Drive, Toc);

        Assert.That(tracks.Select(track => track.Title), Is.EqualTo(Items("Track 1", "Track 2", "Track 3")));
        Assert.That(tracks.Select(track => track.TrackNumber), Is.EqualTo(Items(1, 2, 3)));
        Assert.That(tracks[1].Duration, Is.EqualTo(Toc.DurationOf(1)));
        Assert.That(tracks[0].Location.AbsoluteUri, Is.EqualTo("cdda:///dev/sr0?track=1"));
    }

    [Test]
    public void Build_WithDetails_AppliesAlbumArtistGenreYearAndTitles()
    {
        IReadOnlyList<Track> tracks = DiscTracks.Build(Drive, Toc, "The Album", "The Band", "rock", 1999,
            ["One", "", "Three"], [null!, "Guest", ""]);

        Assert.That(tracks.Select(track => track.Title), Is.EqualTo(Items("One", "Track 2", "Three")));
        Assert.That(tracks.Select(track => track.Artist), Is.EqualTo(Items("The Band", "Guest", "The Band")));
        Assert.That(tracks.All(track => track.Album == "The Album" && track.Genre == "rock" && track.Year == 1999 && track.AlbumArtist == "The Band"));
    }

    [Test]
    public void Build_SameDiscTwice_GivesSameTrackIds()
    {
        Guid[] first = [.. DiscTracks.Build(Drive, Toc).Select(track => track.Id)];
        Assert.That(DiscTracks.Build(Drive, Toc).Select(track => track.Id), Is.EqualTo(first));
    }

    private static T[] Items<T>(params T[] items) => items;
}
