namespace Aibell.Audio.Spc.Tests;

[TestFixture]
public class SpcDecoderTests
{
    private static SpcDecoder Open(SpcBuilder builder) => SpcDecoder.Open(new MemoryStream(builder.Build()));

    private static float[] Read(SpcDecoder decoder, int frames)
    {
        var pcm = new float[frames * 2];
        int got = decoder.Read(pcm, frames);
        Array.Resize(ref pcm, got * 2);
        return pcm;
    }

    [Test]
    public void Format_IsStereoAt32Kilohertz()
    {
        using SpcDecoder decoder = Open(SpcBuilder.SquareVoice());

        Assert.Multiple(() =>
        {
            Assert.That(decoder.SampleRate, Is.EqualTo(32000));
            Assert.That(decoder.Channels, Is.EqualTo(2));
        });
    }

    [Test]
    public void TotalFrames_IsPlayTimePlusFade()
    {
        SpcBuilder builder = SpcBuilder.SquareVoice();
        builder.Seconds = "20";
        builder.FadeMilliseconds = "4000";
        using SpcDecoder decoder = Open(builder);

        Assert.That(decoder.TotalFrames, Is.EqualTo(24L * 32000));
    }

    [Test]
    public void TotalFrames_FallsBackToTheDefaultLength()
    {
        using SpcDecoder decoder = Open(SpcBuilder.SquareVoice());

        Assert.That(decoder.TotalFrames, Is.EqualTo((SpcFile.DefaultSeconds + SpcFile.DefaultFadeMilliseconds / 1000) * 32000L));
    }

    [Test]
    public void Read_ProducesSoundAndEndsAtTheTotalLength()
    {
        SpcBuilder builder = SpcBuilder.SquareVoice();
        builder.Seconds = "1";
        builder.FadeMilliseconds = "500";
        using SpcDecoder decoder = Open(builder);

        float[] first = Read(decoder, 8000);
        float[] rest = Read(decoder, 100000);

        Assert.Multiple(() =>
        {
            Assert.That(first.Max(), Is.GreaterThan(0.1f));
            Assert.That(first.Length / 2 + rest.Length / 2, Is.EqualTo(48000));
            Assert.That(decoder.Read(new float[64], 32), Is.EqualTo(0));
        });
    }

    [Test]
    public void Fade_RampsDownToSilence()
    {
        SpcBuilder builder = SpcBuilder.SquareVoice();
        builder.Seconds = "1";
        builder.FadeMilliseconds = "1000";
        using SpcDecoder decoder = Open(builder);

        float[] all = Read(decoder, 64000);
        float before = PeakOf(all, 20000, 30000);
        float middle = PeakOf(all, 44000, 52000);
        float end = PeakOf(all, 63000, 64000);

        Assert.Multiple(() =>
        {
            Assert.That(middle, Is.InRange(before * 0.35f, before * 0.65f));
            Assert.That(end, Is.LessThan(before * 0.05f));
        });
    }

    [Test]
    public void Read_IsIdenticalWhateverTheBlockSize()
    {
        byte[] data = SpcBuilder.SquareVoice().Build();
        using SpcDecoder whole = SpcDecoder.Open(new MemoryStream(data));
        using SpcDecoder pieces = SpcDecoder.Open(new MemoryStream(data));

        float[] expected = Read(whole, 5000);
        var actual = new List<float>();
        for (int i = 0; i < 5; i++) actual.AddRange(Read(pieces, 1000));

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Seek_BackwardRestartsFromTheSavedState()
    {
        byte[] data = SpcBuilder.SquareVoice().Build();
        using SpcDecoder decoder = SpcDecoder.Open(new MemoryStream(data));
        float[] first = Read(decoder, 4000);

        Assert.That(decoder.Seek(1000), Is.True);
        float[] again = Read(decoder, 1000);

        Assert.That(again, Is.EqualTo(first.AsSpan(2000, 2000).ToArray()));
    }

    [Test]
    public void Seek_ForwardMatchesPlayingThrough()
    {
        byte[] data = SpcBuilder.SquareVoice().Build();
        using SpcDecoder playing = SpcDecoder.Open(new MemoryStream(data));
        float[] through = Read(playing, 6000);
        using SpcDecoder seeking = SpcDecoder.Open(new MemoryStream(data));

        Assert.That(seeking.Seek(5000), Is.True);

        Assert.That(Read(seeking, 1000), Is.EqualTo(through.AsSpan(10000, 2000).ToArray()));
    }

    [Test]
    public void Seek_RejectsPositionsOutsideTheStream()
    {
        using SpcDecoder decoder = Open(SpcBuilder.SquareVoice());

        Assert.Multiple(() =>
        {
            Assert.That(decoder.Seek(-1), Is.False);
            Assert.That(decoder.Seek(decoder.TotalFrames + 1), Is.False);
        });
    }

    [Test]
    public void Open_RejectsOtherData()
    {
        Assert.Throws<InvalidDataException>(() => SpcDecoder.Open(new MemoryStream(new byte[100])));
    }

    // Optional check against real files: set AIBELL_SPC_SAMPLES to a folder of .spc files.
    [Test]
    public void SampleFiles_DecodeWithoutErrors()
    {
        string? folder = Environment.GetEnvironmentVariable("AIBELL_SPC_SAMPLES");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) Assert.Ignore("AIBELL_SPC_SAMPLES is not set.");

        int checkedFiles = 0;
        foreach (string path in Directory.EnumerateFiles(folder, "*.spc", SearchOption.AllDirectories))
        {
            using SpcDecoder decoder = SpcDecoder.Open(File.OpenRead(path));
            float[] pcm = Read(decoder, 32000 * 3);
            Assert.That(pcm.All(float.IsFinite), Is.True, path);
            Assert.That(pcm, Has.Length.EqualTo(32000 * 3 * 2), path);
            checkedFiles++;
        }
        Assert.That(checkedFiles, Is.GreaterThan(0));
    }

    private static float PeakOf(float[] pcm, int fromFrame, int toFrame)
    {
        float peak = 0;
        for (int i = fromFrame; i < toFrame; i++) peak = Math.Max(peak, Math.Abs(pcm[i * 2]));
        return peak;
    }
}
