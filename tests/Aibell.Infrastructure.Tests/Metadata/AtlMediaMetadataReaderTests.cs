using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class AtlMediaMetadataReaderTests
{
    private static readonly string[] SupportedFixtures =
    [
        "fixture.flac", "fixture.m4a", "fixture.mp3", "fixture.ogg", "fixture.opus", "fixture.wav"
    ];

    [TestCaseSource(nameof(SupportedFixtures))]
    public async Task ReadAsync_SupportedFixture_ReturnsDuration(string fileName)
    {
        AtlMediaMetadataReader reader = new();
        Uri location = FixtureUri("generated", fileName);

        Domain.Media.MediaMetadata metadata = await reader.ReadAsync(location);

        Assert.Multiple(() =>
        {
            Assert.That(metadata.Title, Is.EqualTo("Fixture Tone"));
            Assert.That(metadata.Artist, Is.EqualTo("Aibell Test Runner"));
            Assert.That(metadata.Album, Is.EqualTo("Synthetic Fixtures"));
            Assert.That(metadata.Duration, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(metadata.BitRate, Is.GreaterThan(0));
            Assert.That(metadata.Channels, Is.GreaterThan(0));
            Assert.That(metadata.SampleRate, Is.GreaterThan(0));
            Assert.That(metadata.FileSize, Is.GreaterThan(0));
            Assert.That(metadata.DateModified, Is.Not.Null);
        });
    }

    [Test]
    public void ReadAsync_CorruptFile_ThrowsInvalidDataException()
    {
        AtlMediaMetadataReader reader = new();

        Assert.That(
            async () => await reader.ReadAsync(FixtureUri("corrupt.mp3")),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task ReadAsync_SpcFile_UsesItsTags()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".spc");
        File.WriteAllBytes(path, new Audio.Spc.Tests.SpcBuilder { Title = "Overworld", Game = "A Game", Artist = "Composer", Seconds = "100", FadeMilliseconds = "8000" }.Build());
        try
        {
            Domain.Media.MediaMetadata metadata = await new AtlMediaMetadataReader().ReadAsync(new Uri(path));

            Assert.Multiple(() =>
            {
                Assert.That(metadata.Title, Is.EqualTo("Overworld"));
                Assert.That(metadata.Album, Is.EqualTo("A Game"));
                Assert.That(metadata.Artist, Is.EqualTo("Composer"));
                Assert.That(metadata.Duration, Is.EqualTo(TimeSpan.FromSeconds(108)));
                Assert.That(metadata.Channels, Is.EqualTo(2));
                Assert.That(metadata.SampleRate, Is.EqualTo(32000));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadAsync_SpcWithoutTitle_UsesTheFileName()
    {
        string path = Path.Combine(Path.GetTempPath(), "song_0A_" + Guid.NewGuid().ToString("N") + ".spc");
        File.WriteAllBytes(path, new Audio.Spc.Tests.SpcBuilder { HasTag = false }.Build());
        try
        {
            Domain.Media.MediaMetadata metadata = await new AtlMediaMetadataReader().ReadAsync(new Uri(path));

            Assert.That(metadata.Title, Does.StartWith("song_0A_"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ReadAsync_RsnEntry_UsesTheTagsOfThatTrack()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rsn");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rar", "songs4.rsn"), path);
        try
        {
            Domain.Media.MediaMetadata metadata = await new AtlMediaMetadataReader().ReadAsync(Audio.Spc.RsnArchive.CreateLocation(path, "song_03.spc"));

            Assert.Multiple(() =>
            {
                Assert.That(metadata.Title, Is.EqualTo("Song 3"));
                Assert.That(metadata.Album, Is.EqualTo("Test Game"));
                Assert.That(metadata.TrackNumber, Is.EqualTo(3));
                Assert.That(metadata.Duration, Is.EqualTo(TimeSpan.FromSeconds(11)));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ReadAsync_DamagedRsn_ThrowsInvalidDataException()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rsn");
        File.WriteAllBytes(path, new byte[100]);
        try
        {
            Assert.That(async () => await new AtlMediaMetadataReader().ReadAsync(new Uri(path)), Throws.TypeOf<InvalidDataException>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ReadAsync_DamagedSpc_ThrowsInvalidDataException()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".spc");
        File.WriteAllBytes(path, new byte[300]);
        try
        {
            Assert.That(async () => await new AtlMediaMetadataReader().ReadAsync(new Uri(path)), Throws.TypeOf<InvalidDataException>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Uri FixtureUri(params string[] segments)
    {
        string path = segments.Aggregate(
            Path.Combine(AppContext.BaseDirectory, "fixtures"),
            Path.Combine);
        return new Uri(path);
    }
}
