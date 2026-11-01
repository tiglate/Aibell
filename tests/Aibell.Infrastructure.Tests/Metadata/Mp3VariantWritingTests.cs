using System.Diagnostics;
using Aibell.Application.Library;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
[Category("MetadataWriteDecode")]
public sealed class Mp3VariantWritingTests
{
    [TestCase(48000, 2, "128k", 3)]
    [TestCase(44100, 2, "vbr", 4)]
    [TestCase(24000, 1, "64k", 3)]
    [TestCase(16000, 2, "48k", 4)]
    [TestCase(8000, 1, "24k", 4)]
    public async Task WriteAndRestore_MpegVariants_PreservesFramesDecodeAndIndexedDuration(int sampleRate, int channels, string bitRate, int tagVersion)
    {
        if (Environment.GetEnvironmentVariable("AIBELL_METADATA_WRITE_TESTS") != "1") Assert.Ignore("Enable FFmpeg metadata decode tests.");
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aibell-mp3-variant-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = Path.Combine(directory, "variant.mp3");
            ProcessStartInfo info = new("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"sine=frequency=330:sample_rate={sampleRate}:duration=5", "-ac", channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-c:a", "libmp3lame", bitRate == "vbr" ? "-q:a" : "-b:a", bitRate == "vbr" ? "3" : bitRate,
                "-id3v2_version", tagVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), "-metadata", "title=Original", source }) info.ArgumentList.Add(argument);
            using Process process = Process.Start(info)!; string error = await process.StandardError.ReadToEndAsync();
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30)); await process.WaitForExitAsync(deadline.Token);
            Assert.That(process.ExitCode, Is.Zero, error);
            AtlMediaMetadataReader reader = new();
            var before = await reader.ReadAsync(new Uri(source));
            string decoded = await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source);
            MetadataFileWriter writer = new(new AtlMetadataEditStager());
            MetadataFileWriteResult result = await writer.WriteAsync(new Uri(source), new(Title: "Edited 日本語", Composer: "Composer", Keywords: "Keywords ação", LicenseUri: "https://example.com/license", ShowName: "Show 日本語", SeasonNumber: 2, EpisodeNumber: 7));
            var after = await reader.ReadAsync(new Uri(source));
            Assert.That(after.Duration, Is.EqualTo(before.Duration));
            Assert.That(after.BitRate, Is.EqualTo(before.BitRate));
            Assert.That(after.Keywords, Is.EqualTo("Keywords ação"));
            Assert.That(after.ShowName, Is.EqualTo("Show 日本語"));
            Assert.That(after.SeasonNumber, Is.EqualTo(2));
            Assert.That(after.EpisodeNumber, Is.EqualTo(7));
            Assert.That((await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source))).Metadata.LicenseUri,
                Is.EqualTo("https://example.com/license"));
            Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source), Is.EqualTo(decoded));
            await writer.RestoreAsync(result.Backup);
            Assert.That((await reader.ReadAsync(new Uri(source))).Title, Is.EqualTo("Original"));
            var restored = await reader.ReadAsync(new Uri(source));
            Assert.That(restored.Keywords, Is.Null);
            Assert.That(restored.ShowName, Is.Null);
            Assert.That(restored.SeasonNumber, Is.Zero);
            Assert.That(restored.EpisodeNumber, Is.Zero);
            Assert.That((await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source))).Metadata.LicenseUri, Is.Empty);
            Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source), Is.EqualTo(decoded));
        }
        finally { Directory.Delete(directory, true); }
    }
}
