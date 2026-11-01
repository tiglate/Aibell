namespace Aibell.Audio.Spc.Tests;

[TestFixture]
public class SpcFileTests
{
    [Test]
    public void Parse_ReadsRegistersAndMemory()
    {
        var builder = new SpcBuilder { Pc = 0x1234, A = 1, X = 2, Y = 3, Psw = 0x22, Sp = 0xCF };
        builder.Ram(0x0300, 9, 8, 7);
        builder.Dsp(0x0C, 0x55);

        SpcFile file = SpcFile.Parse(builder.Build());

        Assert.Multiple(() =>
        {
            Assert.That(file.Pc, Is.EqualTo(0x1234));
            Assert.That((file.A, file.X, file.Y, file.Psw, file.Sp), Is.EqualTo(((byte)1, (byte)2, (byte)3, (byte)0x22, (byte)0xCF)));
            Assert.That(file.Ram.Slice(0x0300, 3).ToArray(), Is.EqualTo(new byte[] { 9, 8, 7 }));
            Assert.That(file.DspRegisters[0x0C], Is.EqualTo(0x55));
        });
    }

    [Test]
    public void Parse_ReadsTextTag()
    {
        var builder = new SpcBuilder { Title = "Overworld", Game = "A Game", Artist = "Composer", Seconds = "120", FadeMilliseconds = "5000" };

        SpcFile file = SpcFile.Parse(builder.Build());

        Assert.Multiple(() =>
        {
            Assert.That(file.Tags.Title, Is.EqualTo("Overworld"));
            Assert.That(file.Tags.Game, Is.EqualTo("A Game"));
            Assert.That(file.Tags.Artist, Is.EqualTo("Composer"));
            Assert.That(file.PlayLength, Is.EqualTo(TimeSpan.FromSeconds(120)));
            Assert.That(file.FadeLength, Is.EqualTo(TimeSpan.FromSeconds(5)));
        });
    }

    [Test]
    public void Parse_ReadsBinaryTag()
    {
        var builder = new SpcBuilder { Title = "Binary", Artist = "Someone", BinaryLengths = (200, 7000) };

        SpcFile file = SpcFile.Parse(builder.Build());

        Assert.Multiple(() =>
        {
            Assert.That(file.Tags.Artist, Is.EqualTo("Someone"));
            Assert.That(file.PlayLength, Is.EqualTo(TimeSpan.FromSeconds(200)));
            Assert.That(file.FadeLength, Is.EqualTo(TimeSpan.FromSeconds(7)));
        });
    }

    [Test]
    public void Parse_IgnoresTagWhenFlagSaysNone()
    {
        SpcFile file = SpcFile.Parse(new SpcBuilder { HasTag = false, Title = "Hidden", Seconds = "60" }.Build());

        Assert.Multiple(() =>
        {
            Assert.That(file.Tags.Title, Is.Null);
            Assert.That(file.PlayLength, Is.EqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public void Parse_ReadsExtendedTagAndOverridesLengths()
    {
        var builder = new SpcBuilder { Title = "Short", Seconds = "10", FadeMilliseconds = "1000" }
            .Xid6(SpcBuilder.Text(0x01, "Extended title"), SpcBuilder.Text(0x13, "Publisher"), SpcBuilder.Small(0x14, 1994),
                SpcBuilder.Small(0x12, 7 << 8), SpcBuilder.Small(0x11, 2),
                SpcBuilder.Integer(0x30, 64000 * 4), SpcBuilder.Integer(0x31, 64000 * 10), SpcBuilder.Small(0x35, 3),
                SpcBuilder.Integer(0x32, 64000), SpcBuilder.Integer(0x33, 64000 * 6));

        SpcFile file = SpcFile.Parse(builder.Build());

        Assert.Multiple(() =>
        {
            Assert.That(file.Tags.Title, Is.EqualTo("Extended title"));
            Assert.That(file.Tags.Publisher, Is.EqualTo("Publisher"));
            Assert.That(file.Tags.Year, Is.EqualTo(1994));
            Assert.That(file.Tags.TrackNumber, Is.EqualTo(7));
            Assert.That(file.Tags.DiscNumber, Is.EqualTo(2));
            Assert.That(file.PlayLength, Is.EqualTo(TimeSpan.FromSeconds(4 + 10 * 3 + 1)));
            Assert.That(file.FadeLength, Is.EqualTo(TimeSpan.FromSeconds(6)));
        });
    }

    [Test]
    public void Parse_SurvivesDamagedExtendedTag()
    {
        byte[] data = new SpcBuilder().Xid6(SpcBuilder.Text(0x01, "Title")).Build();
        Array.Resize(ref data, data.Length - 6);

        Assert.DoesNotThrow(() => SpcFile.Parse(data));
    }

    [Test]
    public void ResolveLengths_UsesDefaultsWithoutTag()
    {
        (TimeSpan play, TimeSpan fade) = SpcFile.Parse(new SpcBuilder { HasTag = false }.Build()).ResolveLengths();

        Assert.Multiple(() =>
        {
            Assert.That(play, Is.EqualTo(TimeSpan.FromSeconds(SpcFile.DefaultSeconds)));
            Assert.That(fade, Is.EqualTo(TimeSpan.FromMilliseconds(SpcFile.DefaultFadeMilliseconds)));
        });
    }

    [Test]
    public void Parse_RejectsOtherData()
    {
        Assert.Throws<InvalidDataException>(() => SpcFile.Parse(new byte[0x10300]));
    }

    [Test]
    public void Parse_RejectsTruncatedFile()
    {
        byte[] data = new SpcBuilder().Build();

        Assert.Throws<InvalidDataException>(() => SpcFile.Parse(data.AsSpan(0, 0x8000)));
    }

    [Test]
    public void Sniffer_RecognizesSpc()
    {
        Assert.That(AudioSniffer.Detect(new SpcBuilder().Build().AsSpan(0, AudioSniffer.HeadLength)), Is.EqualTo(AudioContainer.Spc));
    }
}
