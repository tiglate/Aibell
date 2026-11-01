using System.Security.Cryptography;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class ArtworkWritingTests
{
    private static readonly string[] Formats = ["flac", "m4a", "ogg", "opus", "mp3"];
    [TestCaseSource(nameof(Formats))]
    public async Task WriteAndRestore_ReplaceRemoveAndAddCover_PreservesOtherPicturesTagsAndOriginal(string extension)
    {
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-artwork-write-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = Path.Combine(directory, "fixture." + extension);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), source);
            byte[] front = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "artwork", "cover.png"));
            byte[] back = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "artwork", "back.png"));
            ATL.Track seed = new(source);
            seed.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(back, ATL.PictureInfo.PIC_TYPE.Back));
            seed.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(front, ATL.PictureInfo.PIC_TYPE.Front));
            seed.AdditionalFields["AIBELL_KEEP"] = "Unrelated 日本語"; Assert.That(seed.Save(), Is.True);
            string original = Hash(File.ReadAllBytes(source));
            AtlTrackFileDetailsReader reader = new(); var details = await reader.ReadAsync(new Uri(source));
            var chosen = details.EmbeddedArtwork!.Single(picture => picture.Hash == Hash(front));
            MetadataFileWriter writer = new(new AtlMetadataEditStager());
            var replaced = await writer.WriteAsync(new Uri(source), new(Artwork: new(chosen.Index, chosen.Hash, back)));
            ATL.Track after = new(source);
            Assert.That(after.EmbeddedPictures.Select(picture => picture.PictureData), Is.EqualTo(new[] { back, back }));
            Assert.That(after.AdditionalFields.Values, Does.Contain("Unrelated 日本語"));
            Assert.That(after.Title, Is.EqualTo("Fixture Tone"));
            await writer.RestoreAsync(replaced.Backup);
            Assert.That(Hash(File.ReadAllBytes(source)), Is.EqualTo(original));
            var removed = await writer.WriteAsync(new Uri(source), new(Artwork: new(chosen.Index, chosen.Hash)));
            Assert.That(new ATL.Track(source).EmbeddedPictures.Single().PictureData, Is.EqualTo(back));
            await writer.RestoreAsync(removed.Backup);
            await writer.WriteAsync(new Uri(source), new(Artwork: new(ImageData: front)));
            Assert.That(new ATL.Track(source).EmbeddedPictures, Has.Count.EqualTo(3));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestCaseSource(nameof(Formats))]
    [Category("MetadataWriteDecode")]
    public async Task WriteAsync_EmbeddedCoverChange_KeepsDecodedAudio(string extension)
    {
        if (Environment.GetEnvironmentVariable("AIBELL_METADATA_WRITE_TESTS") != "1") Assert.Ignore("Enable FFmpeg metadata decode tests.");
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-artwork-decode-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = Path.Combine(directory, "fixture." + extension);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), source);
            string decoded = await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source);
            byte[] front = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "artwork", "cover.png"));
            MetadataFileWriter writer = new(new AtlMetadataEditStager());
            var result = await writer.WriteAsync(new Uri(source), new(Artwork: new(ImageData: front)));
            Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source), Is.EqualTo(decoded));
            await writer.RestoreAsync(result.Backup);
            Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source), Is.EqualTo(decoded));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void StageAsync_ChangedArtworkSelection_RejectsAndKeepsSource()
    {
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-artwork-conflict-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = Path.Combine(directory, "fixture.flac"); File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.flac"), source);
            string hash = Hash(File.ReadAllBytes(source));
            Assert.That(async () => await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Artwork: new(0, new string('A', 64)))), Throws.TypeOf<IOException>());
            Assert.That(Hash(File.ReadAllBytes(source)), Is.EqualTo(hash)); Assert.That(Directory.GetFiles(directory, "*.stage"), Is.Empty);
        }
        finally { Directory.Delete(directory, true); }
    }
    [TestCaseSource(nameof(Formats))]
    public async Task WriteAndRestore_JpegCover_RoundTripsExactImageBytes(string extension)
    {
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-jpeg-write-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = Path.Combine(directory, "fixture." + extension);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), source);
            string original = Hash(File.ReadAllBytes(source));
            byte[] image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "artwork", "cover.jpg"));
            MetadataFileWriter writer = new(new AtlMetadataEditStager());
            var result = await writer.WriteAsync(new Uri(source), new(Artwork: new(ImageData: image)));
            Assert.That(new ATL.Track(source).EmbeddedPictures.Single().PictureData, Is.EqualTo(image));
            await writer.RestoreAsync(result.Backup);
            Assert.That(Hash(File.ReadAllBytes(source)), Is.EqualTo(original));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
