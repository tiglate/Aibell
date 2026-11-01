using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aibell.Application.Library;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

[TestFixture]
public sealed class RiffWaveInfoTests
{
    [TestCase(8, 1)]
    [TestCase(16, 2)]
    [TestCase(24, 1)]
    [TestCase(32, 2)]
    public void Write_GrowShrinkAndRemove_PreservesAudioUnknownChunksAndUneditedEntries(int bits, int channels)
    {
        byte[] unknown = Chunk("JUNK", [1, 2, 3], 0x7f);
        byte[] date = Chunk("ICRD", Encoding.ASCII.GetBytes("2026-07-21\0"));
        byte[] custom = Chunk("XTRA", [7, 8, 9], 0x6a);
        using MemoryStream stream = Wave(Format(bits, channels), Info(Chunk("INAM", "Old\0"u8.ToArray()), date, custom), unknown,
            Chunk("data", new byte[196608 * channels * (bits / 8)]), Chunk("cue ", [5, 6]));
        RiffWaveInfo before = RiffWaveInfo.Read(stream);
        foreach (string title in new[] { new string('x', 131073), "Short", "" })
        {
            RiffWaveInfo.Read(stream).Write(stream, new(Title: title), default);
            RiffWaveInfo after = RiffWaveInfo.Read(stream);
            Assert.That(after.OtherChunksHash, Is.EqualTo(before.OtherChunksHash));
            Assert.That(after.Entries.Select(Convert.ToHexString), Does.Contain(Convert.ToHexString(date)));
            Assert.That(after.Entries.Select(Convert.ToHexString), Does.Contain(Convert.ToHexString(custom)));
            Assert.That(after.Entries.Length, Is.EqualTo(title.Length == 0 ? 2 : 3));
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Write_MissingOrEmptyInfo_AddsThenRemovesWithoutChangingOtherChunks(bool emptyInfo)
    {
        byte[][] chunks = emptyInfo ? [Format(), Info(), Chunk("data", [0, 1, 2, 3])] : [Format(), Chunk("data", [0, 1, 2, 3])];
        using MemoryStream stream = Wave(chunks);
        RiffWaveInfo original = RiffWaveInfo.Read(stream);
        original.Write(stream, new(Title: "New", TrackNumber: 12), default);
        RiffWaveInfo saved = RiffWaveInfo.Read(stream);
        Assert.That(saved.Entries, Has.Length.EqualTo(2));
        saved.Write(stream, new(Title: "", TrackNumber: 0), default);
        RiffWaveInfo cleared = RiffWaveInfo.Read(stream);
        Assert.That(cleared.Entries, Is.Empty);
        Assert.That(cleared.OtherChunksHash, Is.EqualTo(original.OtherChunksHash));
        Assert.That(cleared.Length, Is.Zero);
    }

    [Test]
    public void Write_ExistingTrackAliases_UpdatesAndClearsAllTogether()
    {
        using MemoryStream stream = Wave(Format(), Info(Chunk("IPRT", "1\0"u8.ToArray()),
            Chunk("ITRK", "1\0"u8.ToArray()), Chunk("TRCK", "1\0"u8.ToArray())), Chunk("data", [0, 1]));
        RiffWaveInfo.Read(stream).Write(stream, new(TrackNumber: 7), default);
        RiffWaveInfo saved = RiffWaveInfo.Read(stream);
        Assert.That(saved.Entries.Select(entry => Encoding.UTF8.GetString(entry, 8, 2)), Is.All.EqualTo("7\0"));
        saved.Write(stream, new(TrackNumber: 0), default);
        Assert.That(RiffWaveInfo.Read(stream).Entries, Is.Empty);
    }

    [TestCase("id3 ")]
    [TestCase("ID3 ")]
    [TestCase("bext")]
    [TestCase("iXML")]
    [TestCase("axml")]
    [TestCase("cart")]
    [TestCase("CSET")]
    public void Read_AdditionalMetadata_Rejects(string key)
    {
        using MemoryStream stream = Wave(Format(), Chunk(key, [0, 0]), Chunk("data", [0, 1]));
        Assert.That(() => RiffWaveInfo.Read(stream), Throws.TypeOf<NotSupportedException>());
    }

    [TestCase("root-length")]
    [TestCase("chunk-length")]
    [TestCase("info-length")]
    [TestCase("duplicate-format")]
    [TestCase("duplicate-data")]
    [TestCase("duplicate-info")]
    [TestCase("float")]
    [TestCase("rf64")]
    [TestCase("partial-frame")]
    public void Read_UnsupportedOrMalformedLayout_Rejects(string problem)
    {
        using MemoryStream stream = Wave(Format(), Info(Chunk("INAM", "Hi\0"u8.ToArray())), Chunk("data", [0, 1]));
        byte[] bytes = stream.ToArray();
        switch (problem)
        {
            case "root-length": bytes[4]++; break;
            case "chunk-length": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), uint.MaxValue); break;
            case "info-length": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52), uint.MaxValue); break;
            case "float": bytes[20] = 3; break;
            case "rf64": "RF64"u8.CopyTo(bytes); break;
            default:
                using (MemoryStream malformed = problem switch
                {
                    "duplicate-format" => Wave(Format(), Format(), Chunk("data", [0, 1])),
                    "duplicate-data" => Wave(Format(), Chunk("data", [0, 1]), Chunk("data", [0, 1])),
                    "duplicate-info" => Wave(Format(), Info(), Info(), Chunk("data", [0, 1])),
                    _ => Wave(Format(), Chunk("data", [0]))
                }) bytes = malformed.ToArray();
                break;
        }
        using MemoryStream invalid = new(bytes);
        Assert.That(RiffWaveInfo.CanWrite(invalid, default), Is.False);
    }

    [Test]
    public void Write_PreCancelled_DoesNotChangeBytes()
    {
        using MemoryStream stream = Wave(Format(), Chunk("data", [0, 1]));
        RiffWaveInfo original = RiffWaveInfo.Read(stream);
        byte[] bytes = stream.ToArray();
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.That(() => original.Write(stream, new(Title: "new"), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        // Writes occur only in disposable staged files; pre-cancellation still avoids mutation.
        Assert.That(stream.ToArray(), Is.EqualTo(bytes));
    }

    [Test]
    public async Task StageWriteAndRestore_BasicTags_RoundTripsAndRestoresExactSource()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aibell-wave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "fixture.wav");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.wav"), source);
            string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
            MetadataFileWriter writer = new(new AtlMetadataEditStager());
            MetadataEdit edit = new(Title: "Edited ação 日本語", Artist: "New artist", Album: "New album", Genre: "Rock", Year: 2025,
                TrackNumber: 7, Comment: "One\nTwo");
            MetadataFileWriteResult result = await writer.WriteAsync(new Uri(source), edit, hash);
            TrackFileDetails read = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source));
            Assert.That(read.FileWriteRestriction, Is.Null);
            Assert.That(read.Metadata.Title, Is.EqualTo(edit.Title));
            Assert.That(read.Metadata.Artist, Is.EqualTo(edit.Artist));
            Assert.That(read.Metadata.Album, Is.EqualTo(edit.Album));
            Assert.That(read.Metadata.Genre, Is.EqualTo(edit.Genre));
            Assert.That(read.Metadata.Year, Is.EqualTo(edit.Year));
            Assert.That(read.Metadata.TrackNumber, Is.EqualTo(edit.TrackNumber));
            Assert.That(read.Metadata.Comment, Is.EqualTo(edit.Comment));
            MetadataFileWriteResult cleared = await writer.WriteAsync(new Uri(source), new(Title: "", Artist: "", Album: "", Genre: "", Year: 0, TrackNumber: 0, Comment: ""));
            read = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source));
            Assert.That(read.Metadata, Is.EqualTo(new TrackMetadata()));
            await writer.RestoreAsync(cleared.Backup);
            await writer.RestoreAsync(result.Backup);
            Assert.That(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))), Is.EqualTo(hash));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task StageAsync_TitleOnly_PreservesOriginalDateAndUnknownInfoBytes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aibell-wave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "fixture.wav");
            byte[] date = Chunk("ICRD", "2026-07-21\0"u8.ToArray());
            byte[] custom = Chunk("XTRA", [7, 8, 9], 0x7a);
            using (MemoryStream wave = Wave(Format(), Info(Chunk("INAM", "Original\0"u8.ToArray()), date, custom), Chunk("data", new byte[96000])))
                await File.WriteAllBytesAsync(source, wave.ToArray());
            await using IStagedMetadataEdit staged = await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: "New title"));
            using FileStream preview = File.OpenRead(staged.StagedFile.LocalPath);
            RiffWaveInfo read = RiffWaveInfo.Read(preview);
            Assert.That(read.Entries.Select(Convert.ToHexString), Does.Contain(Convert.ToHexString(date)));
            Assert.That(read.Entries.Select(Convert.ToHexString), Does.Contain(Convert.ToHexString(custom)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task StageAsync_MixedMetadata_RejectsAndKeepsOriginalWithoutOrphans()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aibell-wave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "fixture.wav");
            using MemoryStream wave = Wave(Format(), Info(Chunk("INAM", "Original\0"u8.ToArray())), Chunk("bext", new byte[602]), Chunk("data", new byte[96000]));
            byte[] bytes = wave.ToArray(); await File.WriteAllBytesAsync(source, bytes);
            TrackFileDetails details = await new AtlTrackFileDetailsReader().ReadAsync(new Uri(source));
            Assert.That(details.FileWriteRestriction, Does.Contain("RIFF-preservation"));
            Assert.That(async () => await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: "New title")), Throws.TypeOf<NotSupportedException>());
            Assert.That(await File.ReadAllBytesAsync(source), Is.EqualTo(bytes));
            Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { source }));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    [Category("MetadataWriteDecode")]
    public async Task StageAsync_Wave_PreservesDecodedAudio()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_METADATA_WRITE_TESTS") != "1") Assert.Ignore("Enable FFmpeg metadata decode tests.");
        string source = Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "fixture.wav");
        string original = await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(source);
        await using IStagedMetadataEdit staged = await new AtlMetadataEditStager().StageAsync(new Uri(source), new(Title: "Staged title", Year: 0));
        Assert.That(await AtlMetadataWritingSafetyTests.DecodeAudioHashAsync(staged.StagedFile.LocalPath), Is.EqualTo(original));
    }

    internal static MemoryStream Wave(params byte[][] chunks)
    {
        MemoryStream result = new(); result.Write("RIFF"u8); result.Write(new byte[4]); result.Write("WAVE"u8);
        foreach (byte[] chunk in chunks) result.Write(chunk);
        result.Position = 4;
        byte[] length = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)(result.Length - 8)); result.Write(length);
        result.Position = 0; return result;
    }
    internal static byte[] Chunk(string key, byte[] payload, byte padding = 0)
    {
        byte[] result = new byte[8 + payload.Length + (payload.Length & 1)];
        Encoding.ASCII.GetBytes(key, result); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(result, 8); if ((payload.Length & 1) != 0) result[^1] = padding; return result;
    }
    internal static byte[] Info(params byte[][] entries) => Chunk("LIST", "INFO"u8.ToArray().Concat(entries.SelectMany(entry => entry)).ToArray());
    internal static byte[] Format(int bits = 16, int channels = 1)
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)(48000 * channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), (ushort)(channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14), (ushort)bits);
        return Chunk("fmt ", payload);
    }
}
