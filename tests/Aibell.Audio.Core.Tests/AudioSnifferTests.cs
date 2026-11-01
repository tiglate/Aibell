namespace Aibell.Audio.Core.Tests;

public class AudioSnifferTests
{
    [Test]
    public void Detect_RecognizesEachContainer()
    {
        Assert.That(AudioSniffer.Detect("RIFF\0\0\0\0WAVEfmt "u8), Is.EqualTo(AudioContainer.Wav));
        Assert.That(AudioSniffer.Detect([(byte)'R', (byte)'F', (byte)'6', (byte)'4', 255, 255, 255, 255, (byte)'W', (byte)'A', (byte)'V', (byte)'E']), Is.EqualTo(AudioContainer.Wav));
        Assert.That(AudioSniffer.Detect("FORM\0\0\0\0AIFFCOMM"u8), Is.EqualTo(AudioContainer.Aiff));
        Assert.That(AudioSniffer.Detect("FORM\0\0\0\0AIFCFVER"u8), Is.EqualTo(AudioContainer.Aiff));
        Assert.That(AudioSniffer.Detect("FORM\0\0\0\08SVX"u8), Is.EqualTo(AudioContainer.Unknown));
        Assert.That(AudioSniffer.Detect("fLaC\0\0\0\"extra"u8), Is.EqualTo(AudioContainer.Flac));
        Assert.That(AudioSniffer.Detect("OggS\0\x02extra"u8), Is.EqualTo(AudioContainer.OggOther));
        Assert.That(AudioSniffer.Detect(OggFirstPage("\x01vorbis"u8.ToArray())), Is.EqualTo(AudioContainer.OggVorbis));
        Assert.That(AudioSniffer.Detect(OggFirstPage("OpusHead\x01\x02"u8.ToArray())), Is.EqualTo(AudioContainer.OggOpus));
        Assert.That(AudioSniffer.Detect(OggFirstPage("\x7fFLAC\x01\0"u8.ToArray())), Is.EqualTo(AudioContainer.OggOther));
        Assert.That(AudioSniffer.Detect([0, 0, 0, 32, .. "ftypM4A "u8, 0, 0, 0, 0]), Is.EqualTo(AudioContainer.Mp4));
        Assert.That(AudioSniffer.Detect("RIFF\0\0\0\0AVI LIST"u8), Is.EqualTo(AudioContainer.Unknown));
        Assert.That(AudioSniffer.Detect("ID3\x03"u8), Is.EqualTo(AudioContainer.Unknown));
        Assert.That(AudioSniffer.Detect([]), Is.EqualTo(AudioContainer.Unknown));
    }

    private static byte[] OggFirstPage(byte[] packet)
    {
        byte[] page = new byte[27 + 1 + packet.Length];
        "OggS"u8.CopyTo(page);
        page[5] = 2;
        page[26] = 1;
        page[27] = (byte)packet.Length;
        packet.CopyTo(page, 28);
        return page;
    }

    [Test]
    public void Id3v2Length_ReadsTheSyncsafeSize()
    {
        byte[] tag = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 2, 1];
        Assert.That(AudioSniffer.Id3v2Length(tag), Is.EqualTo(10 + 257));
        tag[5] = 0x10;
        Assert.That(AudioSniffer.Id3v2Length(tag), Is.EqualTo(10 + 257 + 10));
        tag[8] = 0x80;
        Assert.That(AudioSniffer.Id3v2Length(tag), Is.Zero);
        Assert.That(AudioSniffer.Id3v2Length("OggS......."u8), Is.Zero);
        Assert.That(AudioSniffer.Id3v2Length("ID3"u8), Is.Zero);
    }
}
