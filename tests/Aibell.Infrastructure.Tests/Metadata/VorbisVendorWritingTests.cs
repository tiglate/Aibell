using Aibell.Application.Library;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

/// <summary>
/// Found by the library field test: an Ogg Vorbis file encoded with libVorbis 1.3.5, whose encoder name is
/// "Xiph.Org libVorbis I 20150105 (⛄⛄⛄⛄)", lost every tag when ATL saved it (and ATL reported success).
/// </summary>
[TestFixture]
public sealed class VorbisVendorWritingTests
{
    private static readonly string[] OnlyTheTrack = ["track.ogg"];
    private const string Snowmen = "Xiph.Org libVorbis I 20150105 (⛄⛄⛄⛄)";
    private string directory = null!;

    [SetUp] public void SetUp() => directory = Directory.CreateTempSubdirectory("aibell-vendor").FullName;

    [TearDown] public void TearDown() => Directory.Delete(directory, recursive: true);

    private string Make(string vendor, string name = "track.ogg")
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, OggFixtures.WithVendor(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.ogg")), vendor));
        return path;
    }

    [Test]
    public void TheTagLibrary_StillErasesTheTagsOfAFileWithANonAsciiEncoderName()
    {
        // A characterization of the third-party bug that the restriction below works around. If this starts failing, ATL has been fixed:
        // remove AtlTagFields.UnsafeVendorRestriction and its checks.
        string path = Make(Snowmen);
        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            ATL.Track track = new(stream, ".ogg") { Title = "X" };
            Assert.That(track.Save(), Is.True, "ATL reports success");
        }

        using FileStream saved = File.OpenRead(path);
        Assert.That(new ATL.Track(saved, ".ogg").Artist, Is.Empty, "...yet the tags are gone");
    }

    [Test]
    public async Task Details_NonAsciiEncoderName_IsLibraryOnlyWithAnExplanation()
    {
        TrackFileDetails details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(Make(Snowmen)));

        Assert.That(details.FileWriteRestriction, Does.Contain("encoder name").And.Contain("Library editing remains available"));
        Assert.That(details.Metadata.Title, Is.EqualTo("Fixture Tone"), "reading and library editing are unaffected");
    }

    [Test]
    public async Task Details_AsciiEncoderName_IsWritable()
    {
        TrackFileDetails details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(Make("Xiph.Org libVorbis I 20150105")));

        Assert.That(details.FileWriteRestriction, Is.Null);
    }

    [Test]
    public async Task Write_NonAsciiEncoderName_IsDeclinedAndTheFileIsUntouched()
    {
        string path = Make(Snowmen);
        byte[] before = File.ReadAllBytes(path);
        MetadataFileWriter writer = new(new AtlMetadataEditStager());

        await Assert.ThatAsync(() => writer.WriteAsync(new Uri(path), new MetadataEdit(Title: "No")), Throws.TypeOf<NotSupportedException>().With.Message.Contains("encoder name"));

        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        // The writer keeps a small hidden lock file beside any file it has worked on, by design; stages and journals must not remain.
        Assert.That(Directory.GetFiles(directory).Select(Path.GetFileName).Where(name => !name!.EndsWith(".lease", StringComparison.Ordinal)), Is.EqualTo(OnlyTheTrack), "no stage or journal files are left behind");
    }

    [Test]
    public async Task Write_AsciiEncoderName_Works()
    {
        string path = Make("Xiph.Org libVorbis I 20150105");
        MetadataFileWriter writer = new(new AtlMetadataEditStager());

        await writer.WriteAsync(new Uri(path), new MetadataEdit(Title: "Yes"));

        using FileStream saved = File.OpenRead(path);
        Assert.That(new ATL.Track(saved, ".ogg"), Has.Property("Title").EqualTo("Yes").And.Property("Artist").EqualTo("Aibell Test Runner"));
    }
}
