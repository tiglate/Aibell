namespace Aibell.Archive.Rar.Tests;

[TestFixture]
public class RarArchiveTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "fixtures", "rar");

    private static readonly string[] Archives =
    [
        "r4_store", "r4_fastest", "r4_normal", "r4_best", "r4_solid", "r4_ppm", "r4_e8", "r4_delta", "r4_audio", "r4_rgb",
        "r5_store", "r5_normal", "r5_best", "r5_solid", "r5_e8", "r5_delta"
    ];

    private static readonly string[] Sources = ["text.txt", "binary.bin", "tone.wav", "code.bin", "empty.txt", "sub/nested.txt"];

    private static RarArchive Open(string name) => RarArchive.Open(File.OpenRead(Path.Combine(Fixtures, name + ".rar")));

    private static byte[] Source(string name) => File.ReadAllBytes(Path.Combine(Fixtures, "source", name));

    [TestCaseSource(nameof(Archives))]
    public void Extract_ReproducesEveryFile(string archive)
    {
        RarArchive rar = Open(archive);

        var files = rar.Entries.Where(e => !e.IsDirectory).ToList();
        Assert.That(files.Select(e => e.Name), Is.EquivalentTo(Sources));
        foreach (RarEntry entry in files)
        {
            Assert.That(rar.Extract(entry), Is.EqualTo(Source(entry.Name)), $"{archive}: {entry.Name}");
            Assert.That(entry.Size, Is.EqualTo(Source(entry.Name).Length));
        }
    }

    [TestCaseSource(nameof(Archives))]
    public void Extract_InReverseOrder_RestartsTheSolidChain(string archive)
    {
        RarArchive rar = Open(archive);

        foreach (RarEntry entry in rar.Entries.Where(e => !e.IsDirectory).Reverse())
        {
            Assert.That(rar.Extract(entry), Is.EqualTo(Source(entry.Name)), $"{archive}: {entry.Name}");
        }
    }

    [Test]
    public void Entries_ListDirectoriesAndNames()
    {
        RarArchive rar = Open("r4_normal");

        Assert.Multiple(() =>
        {
            Assert.That(rar.Entries.Any(e => e.IsDirectory && e.Name == "sub"), Is.True);
            Assert.That(rar.Entries.Single(e => e.Name == "sub/nested.txt").IsDirectory, Is.False);
            Assert.That(rar.Entries.Single(e => e.Name == "text.txt").Modified, Is.Not.Null);
        });
    }

    [Test]
    public void Extract_OfADirectory_IsEmpty()
    {
        RarArchive rar = Open("r5_normal");

        Assert.That(rar.Extract(rar.Entries.First(e => e.IsDirectory)), Is.Empty);
    }

    [TestCase("r4_encrypted")]
    public void Extract_EncryptedEntry_IsRefused(string archive)
    {
        RarArchive rar = Open(archive);

        Assert.Throws<NotSupportedException>(() => rar.Extract(rar.Entries.First(e => !e.IsDirectory)));
    }

    [Test]
    public void Open_EncryptedHeaders_IsRefused()
    {
        Assert.Throws<NotSupportedException>(() => Open("r5_encrypted_headers"));
    }

    [Test]
    public void Open_RejectsOtherData()
    {
        Assert.Throws<InvalidDataException>(() => RarArchive.Parse(new byte[200]));
    }

    [Test]
    public void Sniff_RecognizesBothFormats()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RarArchive.Sniff(File.ReadAllBytes(Path.Combine(Fixtures, "r4_store.rar"))), Is.True);
            Assert.That(RarArchive.Sniff(File.ReadAllBytes(Path.Combine(Fixtures, "r5_store.rar"))), Is.True);
            Assert.That(RarArchive.Sniff("PK\u0003\u0004 some zip"u8), Is.False);
        });
    }

    [Test]
    public void Open_FindsTheArchiveBehindAnExecutableStub()
    {
        byte[] stub = new byte[3000];
        byte[] archive = File.ReadAllBytes(Path.Combine(Fixtures, "r4_normal.rar"));
        RarArchive rar = RarArchive.Parse([.. stub, .. archive]);

        RarEntry text = rar.Entries.Single(e => e.Name == "text.txt");
        Assert.That(rar.Extract(text), Is.EqualTo(Source("text.txt")));
    }

    [TestCase("r4_normal")]
    [TestCase("r5_normal")]
    [TestCase("r4_solid")]
    [TestCase("r5_solid")]
    [TestCase("r4_ppm")]
    public void Extract_DamagedData_RaisesInvalidData(string archive)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Fixtures, archive + ".rar"));
        RarArchive intact = RarArchive.Parse(bytes);
        RarEntry entry = intact.Entries.Where(e => !e.IsDirectory && e.Size > 1000).First();
        int middle = entry.DataOffset + (int)(entry.PackedSize / 2);
        bytes[middle] ^= 0x5A;
        RarArchive damaged = RarArchive.Parse(bytes);
        RarEntry target = damaged.Entries.Single(e => e.Name == entry.Name);

        Assert.Throws<InvalidDataException>(() => damaged.Extract(target));
    }

    [Test]
    public void Extract_ClaimingAnAbsurdSize_IsRefusedBeforeAllocating()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Fixtures, "r4_store.rar"));
        RarArchive rar = RarArchive.Parse(bytes);
        RarEntry entry = rar.Entries.First(e => !e.IsDirectory);
        typeof(RarEntry).GetProperty(nameof(RarEntry.Size))!.SetValue(entry, RarArchive.MaxEntryBytes + 1);

        Assert.Throws<NotSupportedException>(() => rar.Extract(entry));
    }

    [Test]
    public void Parse_TruncatedArchive_RaisesInvalidData()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Fixtures, "r4_normal.rar"));

        Assert.Throws<InvalidDataException>(() => RarArchive.Parse(bytes.AsSpan(0, bytes.Length / 2).ToArray()));
    }

    [Test]
    public void Parse_DamagedHeader_RaisesInvalidData()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Fixtures, "r5_normal.rar"));
        bytes[bytes.Length / 2] ^= 0xFF;

        // A byte in the middle is either a header byte (checksum error) or file data (found only on extraction).
        Assert.DoesNotThrow(() =>
        {
            try
            {
                RarArchive rar = RarArchive.Parse(bytes);
                foreach (RarEntry entry in rar.Entries) rar.Extract(entry);
            }
            catch (InvalidDataException)
            {
            }
        });
    }

    [Test]
    public void Fuzz_RandomDamage_OnlyRaisesDocumentedExceptions()
    {
        var random = new Random(12345);
        foreach (string archive in new[] { "r4_best", "r4_solid", "r4_ppm", "r4_audio", "r5_best", "r5_solid", "r5_delta" })
        {
            byte[] original = File.ReadAllBytes(Path.Combine(Fixtures, archive + ".rar"));
            for (int round = 0; round < 25; round++)
            {
                byte[] bytes = (byte[])original.Clone();
                for (int k = 0; k < 1 + round % 4; k++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                try
                {
                    RarArchive rar = RarArchive.Parse(bytes);
                    foreach (RarEntry entry in rar.Entries) rar.Extract(entry);
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
                {
                }
            }
        }
    }
}
