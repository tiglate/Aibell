using Aibell.Application.Devices;
using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Application.Tests.Devices;

[TestFixture]
public sealed class DevicePathBuilderTests
{
    private static Track Song(string title = "Neon Knights", string artist = "Black Sabbath", string album = "Heaven and Hell", int number = 1, string albumArtist = "", int disc = 0) =>
        new(Guid.NewGuid(), new Uri("file:///music/neon.mp3"), title, artist, album, TimeSpan.FromMinutes(3), AlbumArtist: albumArtist, TrackNumber: number, DiscNumber: disc);

    [Test]
    public void Relative_UsesArtistAlbumNumberAndTitle() =>
        Assert.That(DevicePathBuilder.Relative(Song(), "Music", ".MP3"), Is.EqualTo("Music/Black Sabbath/Heaven and Hell/01 - Neon Knights.mp3"));

    [Test]
    public void Relative_PrefersTheAlbumArtistSoCompilationsStayTogether() =>
        Assert.That(DevicePathBuilder.Relative(Song(artist: "Guest", albumArtist: "Various Artists"), "Music", ".flac"),
            Does.StartWith("Music/Various Artists/"));

    [Test]
    public void Relative_SecondDiscCarriesTheDiscNumber() =>
        Assert.That(DevicePathBuilder.Relative(Song(number: 4, disc: 2), "Music", ".ogg"), Does.EndWith("/2-04 - Neon Knights.ogg"));

    [Test]
    public void Relative_MissingTagsFallBackToUnknownNames() =>
        Assert.That(DevicePathBuilder.Relative(Song(title: " ", artist: "", album: "", number: 0), "Music", ".mp3"),
            Is.EqualTo("Music/Unknown Artist/Unknown Album/neon.mp3"));

    [TestCase("AC/DC", "AC_DC")]
    [TestCase("What?", "What_")]
    [TestCase("a<b>c:d\"e|f*g", "a_b_c_d_e_f_g")]
    [TestCase("Ends with dots...", "Ends with dots")]
    [TestCase("  padded  ", "padded")]
    [TestCase("CON", "_CON")]
    [TestCase("nul.txt", "_nul.txt")]
    [TestCase("COM1", "_COM1")]
    public void Segment_MakesNamesSafeForEveryFileSystem(string input, string expected) =>
        Assert.That(DevicePathBuilder.Segment(input, "fallback"), Is.EqualTo(expected));

    [TestCase("")]
    [TestCase("...")]
    [TestCase("???")]
    [TestCase(null)]
    public void Segment_NothingUsable_UsesTheFallback(string? input) =>
        Assert.That(DevicePathBuilder.Segment(input, "Fallback"), Is.EqualTo("Fallback"));

    [Test]
    public void Segment_ControlCharactersAreReplaced() =>
        Assert.That(DevicePathBuilder.Segment("a\u0001b\tc", "x"), Is.EqualTo("a_b_c"));

    [Test]
    public void Relative_LongNamesAreShortenedToTheLimit()
    {
        string path = DevicePathBuilder.Relative(Song(title: new string('t', 400), artist: new string('a', 400), album: new string('b', 400)), "Music", ".mp3");

        Assert.That(path.Length, Is.LessThanOrEqualTo(DevicePathBuilder.MaximumRelativeLength));
        Assert.That(path.Split('/').All(segment => segment.Length <= DevicePathBuilder.MaximumSegmentLength + 10), Is.True);
        Assert.That(path, Does.EndWith(".mp3"));
    }

    [TestCase("../evil")]
    [TestCase("..\\evil")]
    [TestCase("/abs")]
    public void Relative_FolderNamesCannotEscapeTheDevice(string folder)
    {
        string[] parts = DevicePathBuilder.Relative(Song(), folder, ".mp3").Split('/');
        Assert.That(parts, Has.Length.EqualTo(4), "the folder option is a single path segment");
        Assert.That(parts, Does.Not.Contain(".."));
        Assert.That(parts, Does.Not.Contain(""));
        Assert.That(parts[0], Does.Not.Contain("\\"));
    }

    [TestCase("exe;rm")]
    [TestCase("verylongextension")]
    [TestCase("")]
    public void Relative_UnsafeExtensionIsDropped(string extension) =>
        Assert.That(DevicePathBuilder.Relative(Song(), "Music", extension), Does.EndWith("Neon Knights"));
}
