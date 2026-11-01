using System.Security.Cryptography;
using Aibell.Application.Library;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class AdditionalTagWritingTests
{
    private static readonly string[] Formats = ["flac", "m4a", "ogg", "opus", "mp3"];

    [TestCaseSource(nameof(Formats))]
    public async Task WriteAsync_KeywordsAndLicense_AddReplaceClearAndRestore(string extension)
    {
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-additional-tags-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = Path.Combine(directory, "fixture." + extension);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), source);
            ATL.Track seed = new(source);
            seed.AdditionalFields["AIBELL_KEEP"] = "Unrelated 日本語";
            Assert.That(seed.Save(), Is.True);
            string original = Hash(source);
            MetadataFileWriter writer = new(new AtlMetadataEditStager());
            List<MetadataFileWriteResult> edits = [];
            edits.Add(await writer.WriteAsync(new Uri(source), new(Keywords: "music, ação", LicenseUri: "https://example.com/license")));
            var details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source));
            Assert.That(details.Metadata.Keywords, Is.EqualTo("music, ação"));
            Assert.That(details.Metadata.LicenseUri, Is.EqualTo("https://example.com/license"));
            Assert.That((await new AtlMediaMetadataReader().ReadAsync(new Uri(source))).Keywords, Is.EqualTo("music, ação"));
            edits.Add(await writer.WriteAsync(new Uri(source), new(Keywords: "Replacement 日本語")));
            details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source));
            Assert.That(details.Metadata.LicenseUri, Is.EqualTo("https://example.com/license"));
            edits.Add(await writer.WriteAsync(new Uri(source), new(LicenseUri: "https://example.com/new")));
            details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source));
            Assert.That(details.Metadata.Keywords, Is.EqualTo("Replacement 日本語"));
            Assert.That(details.Metadata.LicenseUri, Is.EqualTo("https://example.com/new"));
            edits.Add(await writer.WriteAsync(new Uri(source), new(Keywords: "")));
            details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source));
            Assert.That(details.Metadata.Keywords, Is.Empty);
            Assert.That(details.Metadata.LicenseUri, Is.EqualTo("https://example.com/new"));
            edits.Add(await writer.WriteAsync(new Uri(source), new(LicenseUri: "")));
            details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source));
            Assert.That(details.Metadata.Keywords, Is.Empty);
            Assert.That(details.Metadata.LicenseUri, Is.Empty);
            Assert.That(new ATL.Track(source).AdditionalFields.Values, Does.Contain("Unrelated 日本語"));
            // Undo every committed edit in reverse order; each retained version must match exactly.
            foreach (MetadataFileWriteResult edit in Enumerable.Reverse(edits))
                await writer.RestoreAsync(edit.Backup);
            Assert.That(Hash(source), Is.EqualTo(original));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestCaseSource(nameof(Formats))]
    public async Task WriteAsync_ExistingAliases_UpdatesAllAliasesAndPreservesUnrelatedFields(string extension)
    {
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-tag-aliases-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = Path.Combine(directory, "fixture." + extension);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), source);
            ATL.Track seed = new(source);
            seed.AdditionalFields["KEYWORD"] = "Old keywords";
            seed.AdditionalFields["PODCASTKEYWORDS"] = "Other keywords";
            seed.AdditionalFields["LICENSE_URI"] = "https://example.com/old";
            seed.AdditionalFields["LICENSEURL"] = "https://example.com/other";
            seed.AdditionalFields["AIBELL_KEEP"] = "Keep this";
            seed.AdditionalFields["AIBELL_KEYWORDS"] = "Private keywords";
            seed.AdditionalFields["AIBELL_LICENSE"] = "Private license";
            Assert.That(seed.Save(), Is.True);
            MetadataFileWriter writer = new(new AtlMetadataEditStager());
            await writer.WriteAsync(new Uri(source), new(Keywords: "New keywords", LicenseUri: "https://example.com/new"));
            ATL.Track read = new(source);
            Assert.That(read.AdditionalFields.Values, Does.Not.Contain("Old keywords"));
            Assert.That(read.AdditionalFields.Values, Does.Not.Contain("Other keywords"));
            Assert.That(read.AdditionalFields.Values, Does.Not.Contain("https://example.com/old"));
            Assert.That(read.AdditionalFields.Values, Does.Not.Contain("https://example.com/other"));
            Assert.That(read.AdditionalFields.Values, Does.Contain("Keep this"));
            Assert.That(read.AdditionalFields.Values, Does.Contain("Private keywords"));
            Assert.That(read.AdditionalFields.Values, Does.Contain("Private license"));
            await writer.WriteAsync(new Uri(source), new(Keywords: "", LicenseUri: ""));
            var details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source));
            Assert.That(details.Metadata.Keywords, Is.Empty);
            Assert.That(details.Metadata.LicenseUri, Is.Empty);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestCaseSource(nameof(Formats))]
    [Category("MetadataWriteDecode")]
    public async Task WriteAsync_KeywordsAndLicense_PreservesDecodedAudio(string extension)
    {
        if (Environment.GetEnvironmentVariable("AIBELL_METADATA_WRITE_TESTS") != "1") Assert.Ignore("Enable FFmpeg metadata decode tests.");
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-tag-decode-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = Path.Combine(directory, "fixture." + extension);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture." + extension), source);
            string original = await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source);
            MetadataFileWriter writer = new(new AtlMetadataEditStager());
            await writer.WriteAsync(new Uri(source), new(Keywords: "music 日本語", LicenseUri: "https://example.com/license"));
            Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source), Is.EqualTo(original));
            await writer.WriteAsync(new Uri(source), new(Keywords: "", LicenseUri: ""));
            Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source), Is.EqualTo(original));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
