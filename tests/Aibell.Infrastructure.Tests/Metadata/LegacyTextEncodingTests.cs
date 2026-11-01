using System.Text;
using Aibell.Domain.Media;
using Aibell.Infrastructure.Metadata;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests.Metadata;

/// <summary>
/// The charset detector ATL can use for undeclared text encodings (Ude.NetStandard) is deliberately not shipped, because its
/// license terms cannot be verified. These tests pin that reading tags whose text has no declared encoding still works.
/// </summary>
[TestFixture]
public sealed class LegacyTextEncodingTests
{
    private string folder = "";

    [SetUp]
    public void CreateFolder() => folder = Directory.CreateTempSubdirectory("aibell-encoding").FullName;

    [TearDown]
    public void DeleteFolder() => Directory.Delete(folder, recursive: true);

    [Test]
    public void UdeAssembly_IsNotLoadedByTheApplication() =>
        Assert.That(AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name), Does.Not.Contain("Ude.NetStandard"));

    [Test]
    public async Task Read_Id3v1TagWithAccentedLatin1Text_ReturnsTheTagWithoutThrowing()
    {
        string path = Path.Combine(folder, "v1.mp3");
        byte[] audio = File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "generated", "fixture.mp3"));
        // Strip any existing tags by appending a fresh ID3v1 block with Latin-1 bytes (caf\xE9, Beyonc\xE9).
        byte[] tag = new byte[128];
        Encoding.ASCII.GetBytes("TAG").CopyTo(tag, 0);
        Encoding.Latin1.GetBytes("Café del Mar").CopyTo(tag, 3);
        Encoding.Latin1.GetBytes("Beyoncé").CopyTo(tag, 33);
        Encoding.Latin1.GetBytes("Élégie").CopyTo(tag, 63);
        File.WriteAllBytes(path, [.. audio, .. tag]);

        MediaMetadata metadata = await new AtlMediaMetadataReader().ReadAsync(new Uri(path));

        Assert.That(metadata.Title, Is.Not.Empty);
        Assert.That(metadata.Artist, Is.Not.Empty);
    }

    [Test]
    public async Task Read_Id3v2TagWithLatin1FrameAndNoBom_ReturnsTheTitle()
    {
        string path = Path.Combine(folder, "v2.mp3");
        byte[] audio = File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "generated", "fixture.mp3"));
        byte[] text = [0x00, .. Encoding.Latin1.GetBytes("Naïve café")];
        byte[] frame = [.. Encoding.ASCII.GetBytes("TIT2"), 0, 0, 0, (byte)text.Length, 0, 0, .. text];
        byte[] header = [.. Encoding.ASCII.GetBytes("ID3"), 3, 0, 0, 0, 0, 0, (byte)frame.Length];
        File.WriteAllBytes(path, [.. header, .. frame, .. audio]);

        MediaMetadata metadata = await new AtlMediaMetadataReader().ReadAsync(new Uri(path));

        Assert.That(metadata.Title, Does.Contain("caf"));
    }
}
