namespace Aibell.Audio.Spc.Tests;

[TestFixture]
public class RsnArchiveTests
{
    private static string Path4 => Path.Combine(AppContext.BaseDirectory, "fixtures", "rar", "songs4.rsn");

    private static string Path5 => Path.Combine(AppContext.BaseDirectory, "fixtures", "rar", "songs5.rsn");

    [TestCase(false)]
    [TestCase(true)]
    public void ListTracks_ReturnsTheSpcFilesInArchiveOrder(bool rar5)
    {
        using FileStream stream = File.OpenRead(rar5 ? Path5 : Path4);

        Assert.That(RsnArchive.ListTracks(stream), Is.EqualTo(new[] { "song_01.spc", "song_02.spc", "song_03.spc" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReadTrack_ReturnsAPlayableSpc(bool rar5)
    {
        using FileStream stream = File.OpenRead(rar5 ? Path5 : Path4);

        byte[] bytes = RsnArchive.ReadTrack(stream, "song_02.spc", out string name, out int number);
        SpcFile spc = SpcFile.Parse(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(name, Is.EqualTo("song_02.spc"));
            Assert.That(number, Is.EqualTo(2));
            Assert.That(spc.Tags.Title, Is.EqualTo("Song 2"));
            Assert.That(spc.Tags.Game, Is.EqualTo("Test Game"));
            Assert.That(spc.PlayLength, Is.EqualTo(TimeSpan.FromSeconds(10)));
        });
        using SpcDecoder decoder = SpcDecoder.Open(spc);
        var pcm = new float[2 * 4000];
        Assert.That(decoder.Read(pcm, 4000), Is.EqualTo(4000));
        Assert.That(pcm.Max(Math.Abs), Is.GreaterThan(0.1f));
    }

    [Test]
    public void ReadTrack_WithoutAName_TakesTheFirstTrack()
    {
        using FileStream stream = File.OpenRead(Path4);

        RsnArchive.ReadTrack(stream, "", out string name, out int number);

        Assert.That((name, number), Is.EqualTo(("song_01.spc", 1)));
    }

    [Test]
    public void ReadTrack_UnknownName_RaisesInvalidData()
    {
        using FileStream stream = File.OpenRead(Path4);

        Assert.Throws<InvalidDataException>(() => RsnArchive.ReadTrack(stream, "missing.spc"));
    }

    [Test]
    public void Locations_AreDistinctPerTrackAndRoundTrip()
    {
        Uri first = RsnArchive.CreateLocation(Path4, "song_01.spc");
        Uri second = RsnArchive.CreateLocation(Path4, "song_02.spc");
        Uri nested = RsnArchive.CreateLocation(Path4, "dir/Song #3 & more.spc");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.EqualTo(second), "Uri equality must tell the tracks of one archive apart");
            Assert.That(first.LocalPath, Is.EqualTo(Path.GetFullPath(Path4)));
            Assert.That(RsnArchive.TryParseLocation(second, out string archive, out string entry), Is.True);
            Assert.That((archive, entry), Is.EqualTo((Path.GetFullPath(Path4), "song_02.spc")));
            Assert.That(RsnArchive.TryParseLocation(new Uri(nested.AbsoluteUri), out _, out string nestedEntry), Is.True);
            Assert.That(nestedEntry, Is.EqualTo("dir/Song #3 & more.spc"));
        });
    }

    [Test]
    public void TryParseLocation_BareArchiveHasNoEntry_AndOtherFilesAreRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RsnArchive.TryParseLocation(new Uri(Path.GetFullPath(Path4)), out _, out string entry), Is.True);
            Assert.That(entry, Is.Empty);
            Assert.That(RsnArchive.TryParseLocation(new Uri(Path.GetFullPath("song.spc")), out _, out _), Is.False);
            Assert.That(RsnArchive.TryParseLocation(new Uri("https://example.com/a.rsn"), out _, out _), Is.False);
        });
    }
}
