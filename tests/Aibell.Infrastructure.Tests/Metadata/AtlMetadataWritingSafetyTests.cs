using System.Diagnostics;
using System.Security.Cryptography;
using ATL;
using ATL.AudioData;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
[NonParallelizable]
public sealed class AtlMetadataWritingSafetyTests
{
    private static readonly string[] Formats = ["flac", "m4a", "mp3", "ogg", "opus", "wav"];
    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"aibell-write-safety-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [TestCaseSource(nameof(Formats))]
    public async Task Save_DisposableFixture_RoundTripsEditsAndPreservesUneditedTagsAndPictures(string extension)
    {
        await ExerciseEditsAsync(extension);
    }

    [TestCaseSource(nameof(Formats))]
    [Category("MetadataWriteDecode")]
    public async Task Save_DisposableFixture_PreservesDecodedAudioAcrossEveryEdit(string extension)
    {
        if (Environment.GetEnvironmentVariable("AIBELL_METADATA_WRITE_TESTS") != "1")
            Assert.Ignore("Set AIBELL_METADATA_WRITE_TESTS=1 and install FFmpeg to compare decoded audio.");
        string? originalAudioHash = null;
        await ExerciseEditsAsync(extension, async path =>
        {
            string hash = await DecodeAudioHashAsync(path);
            originalAudioHash ??= hash;
            Assert.That(hash, Is.EqualTo(originalAudioHash), "Metadata edits changed the decoded audio.");
        });
    }

    private async Task ExerciseEditsAsync(string extension, Func<string, Task>? verifyAudio = null)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", $"fixture.{extension}");
        string copy = Path.Combine(directory, $"edited.{extension}");
        byte[] originalFileHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        File.Copy(source, copy);
        byte[] front = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "artwork", "cover.png"));
        byte[] back = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "artwork", "back.png"));
        Track original = new(copy);
        double duration = original.DurationMs;
        if (verifyAudio is not null) await verifyAudio(copy);

        Track seeded = new(copy);
        seeded.AdditionalFields["AIBELL_PRESERVE"] = "Unedited 日本語 / ação";
        seeded.EmbeddedPictures.Add(PictureInfo.fromBinaryData(back, PictureInfo.PIC_TYPE.Back));
        seeded.EmbeddedPictures.Add(PictureInfo.fromBinaryData(front, PictureInfo.PIC_TYPE.Front));
        Save(seeded, extension);
        if (verifyAudio is not null) await verifyAudio(copy);

        Track edited = new(copy)
        {
            Title = "Edited 日本語 — ação",
            Genre = "Electronic",
            Year = 2025,
            TrackNumber = 2,
            Comment = "First line\nSecond line"
        };
        Save(edited, extension);
        if (verifyAudio is not null) await verifyAudio(copy);
        Track read = new(copy);
        Assert.Multiple(() =>
        {
            Assert.That(read.Title, Is.EqualTo("Edited 日本語 — ação"));
            Assert.That(read.Genre, Is.EqualTo("Electronic"));
            Assert.That(read.Year, Is.EqualTo(2025));
            Assert.That(read.TrackNumber, Is.EqualTo(2));
            Assert.That(read.Comment, Is.EqualTo("First line\nSecond line"));
            Assert.That(read.Artist, Is.EqualTo(original.Artist));
            Assert.That(read.Album, Is.EqualTo(original.Album));
            Assert.That(read.DurationMs, Is.EqualTo(duration).Within(1));
            Assert.That(read.AdditionalFields.Values, Does.Contain("Unedited 日本語 / ação"));
            Assert.That(read.EmbeddedPictures.Any(picture => picture.PictureData.SequenceEqual(front)), Is.True);
            Assert.That(read.EmbeddedPictures.Any(picture => picture.PictureData.SequenceEqual(back)), Is.True);
        });

        // Remove only the selected picture by its content: MP4 does not retain
        // front/back picture types. Keep the other picture and unrelated tags.
        PictureInfo removed = read.EmbeddedPictures.Single(picture => picture.PictureData.SequenceEqual(front));
        read.EmbeddedPictures.Remove(removed);
        read.Title = "";
        Save(read, extension);
        if (verifyAudio is not null) await verifyAudio(copy);
        // ATL normally substitutes the filename for an absent title. Inspect
        // the actual tag value with that process-wide fallback briefly disabled.
        Track cleared = ReadWithoutTitleFallback(copy);
        Assert.Multiple(() =>
        {
            Assert.That(cleared.Title, Is.Empty);
            Assert.That(cleared.Artist, Is.EqualTo(original.Artist));
            Assert.That(cleared.AdditionalFields.Values, Does.Contain("Unedited 日本語 / ação"));
            Assert.That(cleared.EmbeddedPictures, Has.Count.EqualTo(1));
            Assert.That(cleared.EmbeddedPictures.Single().PictureData, Is.EqualTo(back));
        });
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(source)), Is.EqualTo(originalFileHash),
            "The committed fixture must never be edited.");
    }

    private static void Save(Track track, string extension) => Assert.That(extension == "wav"
        ? track.Save(MetaDataIOFactory.TagType.ID3V2) : track.Save(), Is.True, "ATL reported a failed save.");

    private static Track ReadWithoutTitleFallback(string path)
    {
        bool previous = ATL.Settings.UseFileNameWhenNoTitle;
        try
        {
            ATL.Settings.UseFileNameWhenNoTitle = false;
            return new Track(path);
        }
        finally { ATL.Settings.UseFileNameWhenNoTitle = previous; }
    }

    internal static async Task<string> DecodeAudioHashAsync(string path)
    {
        ProcessStartInfo start = new("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-i", path,
            "-map", "0:a:0", "-vn", "-sn", "-dn", "-c:a", "pcm_s32le", "-f", "hash", "-hash", "sha256", "-"
        }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.That(process.ExitCode, Is.Zero, await errors);
        string hash = (await output).Trim();
        Assert.That(hash, Does.StartWith("SHA256="));
        Assert.That(hash, Has.Length.EqualTo(71));
        return hash;
    }
}
