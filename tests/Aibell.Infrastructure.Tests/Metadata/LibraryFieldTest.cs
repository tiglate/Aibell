using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Aibell.Application.Library;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

/// <summary>
/// Opt-in field test of file writing against a real library: <c>AIBELL_FIELD_TEST_LIBRARY=/path/to/music</c> (and optionally
/// <c>AIBELL_FIELD_TEST_SAMPLE</c>, the number of files, default 60, and <c>AIBELL_FIELD_TEST_REPORT</c>, a Markdown report path). It never
/// writes to the library: a sample of files is copied to a temporary folder, each copy is edited through the real writer, and the test checks
/// that the decoded audio and every field it did not edit are unchanged, then restores the backup and checks the original bytes come back.
/// Files the editor declines to write (a restriction message) are not failures; they are listed with their reason.
/// Requires FFmpeg for the audio comparison. Run with <c>--filter TestCategory=FieldTest</c>.
/// </summary>
[TestFixture]
[Category("FieldTest")]
[Explicit("Needs AIBELL_FIELD_TEST_LIBRARY")]
public sealed class LibraryFieldTest
{
    private static readonly string[] Extensions = [".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav"];

    private sealed record Outcome(string Path, string Extension, long Bytes, string Verdict, string Detail);

    [Test]
    public async Task EditingCopiesOfTheLibrary_NeverChangesAudioOrOtherFieldsAndRestoresExactly()
    {
        string? library = Environment.GetEnvironmentVariable("AIBELL_FIELD_TEST_LIBRARY");
        if (string.IsNullOrWhiteSpace(library) || !Directory.Exists(library)) Assert.Ignore("Set AIBELL_FIELD_TEST_LIBRARY to a music folder.");
        int sample = int.TryParse(Environment.GetEnvironmentVariable("AIBELL_FIELD_TEST_SAMPLE"), out int requested) && requested > 0 ? requested : 60;
        string work = Directory.CreateTempSubdirectory("aibell-field-test").FullName;
        List<Outcome> outcomes = [];
        try
        {
            List<string> chosen = Choose(library!, sample);
            Assume.That(chosen, Is.Not.Empty, "no supported audio files found");
            MetadataFileWriter writer = new(new AtlMetadataEditStager());
            AtlTrackFileDetailsReader reader = new();
            int number = 0;
            foreach (string original in chosen)
            {
                string copy = Path.Combine(work, $"{++number:D3}{Path.GetExtension(original).ToLowerInvariant()}");
                FileInfo info = new(original);
                DateTime modified = info.LastWriteTimeUtc;
                File.Copy(original, copy);
                outcomes.Add(await TryOne(writer, reader, original, copy, info.Length));
                Assert.That(new FileInfo(original).LastWriteTimeUtc, Is.EqualTo(modified), $"the original must be untouched: {original}");
                Assert.That(new FileInfo(original).Length, Is.EqualTo(info.Length));
                File.Delete(copy);
                foreach (string leftover in Directory.GetFiles(work)) File.Delete(leftover);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
            string report = Report(outcomes, library!);
            string? path = Environment.GetEnvironmentVariable("AIBELL_FIELD_TEST_REPORT");
            if (!string.IsNullOrWhiteSpace(path)) File.WriteAllText(path, report);
            TestContext.Out.WriteLine(report);
        }

        Outcome[] broken = [.. outcomes.Where(outcome => outcome.Verdict == "FAILED")];
        Assert.That(broken, Is.Empty, string.Join(Environment.NewLine, broken.Select(outcome => $"{outcome.Path}: {outcome.Detail}")));
    }

    private static async Task<Outcome> TryOne(MetadataFileWriter writer, AtlTrackFileDetailsReader reader, string original, string copy, long bytes)
    {
        string extension = Path.GetExtension(copy);
        Uri location = new(copy);
        try
        {
            TrackFileDetails before = await reader.ReadAsync(location);
            if (before.FileWriteRestriction is { } restriction) return new(original, extension, bytes, "library-only", restriction);
            string originalHash = Hash(copy);
            string audioBefore = await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(copy);
            string marker = " [field test ✓]";

            MetadataFileWriteResult result;
            try
            {
                result = await writer.WriteAsync(location, new MetadataEdit(Title: before.Metadata.Title + marker, Comment: "field test comment\nline two"), before.SourceHash);
            }
            catch (NotSupportedException exception)
            {
                return new(original, extension, bytes, "library-only", "writer declined: " + exception.Message);
            }

            TrackFileDetails after = await reader.ReadAsync(location);
            List<string> problems = [];
            if (after.Metadata.Title != before.Metadata.Title + marker) problems.Add($"title read back as '{after.Metadata.Title}'");
            string audioAfter = await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(copy);
            if (audioAfter != audioBefore) problems.Add("decoded audio changed");
            // Everything we did not edit must survive: the structured fields, the extra tags, and the pictures.
            TrackMetadata expected = before.Metadata with { Title = before.Metadata.Title + marker, Comment = after.Metadata.Comment };
            if (after.Metadata != expected) problems.Add("an unedited field changed: " + Differences(expected, after.Metadata));
            string[] lost = [.. before.AdditionalFields.Where(field => !after.AdditionalFields.TryGetValue(field.Key, out string? value) || value != field.Value).Select(field => field.Key)];
            if (lost.Length > 0) problems.Add("extra tags lost or changed: " + string.Join(", ", lost.Take(5)));
            if (!(before.EmbeddedArtwork ?? []).Select(art => art.Hash).SequenceEqual((after.EmbeddedArtwork ?? []).Select(art => art.Hash))) problems.Add("embedded pictures changed");

            IReadOnlyList<MetadataFileBackup> backups = await writer.GetBackupsAsync(location);
            if (backups.Count != 1) problems.Add($"{backups.Count} backups listed");
            else
            {
                await writer.RestoreAsync(backups[0]);
                if (Hash(copy) != originalHash) problems.Add("restore did not return the original bytes");
            }
            return problems.Count == 0
                ? new(original, extension, bytes, "ok", result.Warning ?? "")
                : new(original, extension, bytes, "FAILED", string.Join("; ", problems));
        }
        catch (Exception exception) when (exception is not AssertionException)
        {
            return new(original, extension, bytes, "FAILED", $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string Differences(TrackMetadata expected, TrackMetadata actual) =>
        string.Join(", ", typeof(TrackMetadata).GetProperties().Where(property => !Equals(property.GetValue(expected), property.GetValue(actual))).Select(property => property.Name));

    /// <summary>Round-robin over the top-level folders (artists) so the sample is not one album, preferring the smaller files first within each.</summary>
    private static List<string> Choose(string library, int sample)
    {
        List<IGrouping<string, string>> groups = [.. Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories)
            .Where(file => Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) && new FileInfo(file).Length < 200L * 1024 * 1024)
            .Order(StringComparer.Ordinal)
            .GroupBy(file => Path.GetRelativePath(library, file).Split(Path.DirectorySeparatorChar)[0])
            .OrderBy(group => group.Key, StringComparer.Ordinal)];
        List<string> chosen = [];
        for (int round = 0; chosen.Count < sample && groups.Any(group => group.Count() > round); round++)
            foreach (IGrouping<string, string> group in groups)
            {
                string[] files = [.. group];
                // Spread the picks across a folder's files rather than taking its first tracks.
                if (round < files.Length && chosen.Count < sample) chosen.Add(files[(int)((long)round * 7919 % files.Length)]);
            }
        return [.. chosen.Distinct(StringComparer.Ordinal)];
    }

    private static string Report(List<Outcome> outcomes, string library)
    {
        StringBuilder text = new();
        text.AppendLine("# Library field test").AppendLine();
        text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Library: `{library}` (read only). {outcomes.Count} files sampled, {outcomes.Sum(outcome => outcome.Bytes) / 1048576} MiB.").AppendLine();
        text.AppendLine("| Format | Written and verified | Library only | FAILED |").AppendLine("| --- | ---: | ---: | ---: |");
        foreach (IGrouping<string, Outcome> format in outcomes.GroupBy(outcome => outcome.Extension.ToLowerInvariant()).OrderBy(group => group.Key, StringComparer.Ordinal))
            text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"| {format.Key} | {format.Count(o => o.Verdict == "ok")} | {format.Count(o => o.Verdict == "library-only")} | {format.Count(o => o.Verdict == "FAILED")} |");
        foreach (IGrouping<string, Outcome> reasons in outcomes.Where(outcome => outcome.Verdict != "ok").GroupBy(outcome => outcome.Verdict + ": " + outcome.Detail).OrderByDescending(group => group.Count()))
        {
            text.AppendLine().AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"## {reasons.Count()} × {reasons.Key}");
            foreach (Outcome outcome in reasons.Take(10)) text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"- {outcome.Path}");
        }
        return text.ToString();
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
