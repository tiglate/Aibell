using Aibell.Application.Playback;
using Aibell.Audio.Spc.Tests;
using Aibell.Playback.Core;
using NUnit.Framework;

namespace Aibell.Playback.Miniaudio.Tests;

[TestFixture]
public sealed class SpcPlaybackTests
{
    [TestCase(".spc")]
    [TestCase(".SPC")]
    public async Task Factory_PlaysSpcWithoutTheManagedEngineSetting(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, SpcBuilder.SquareVoice().Build());
        try
        {
            using HttpClient client = new();
            // 32 kHz sound unit output, converted to the 48 kHz device rate.
            using IPcmSource source = await new AudioSourceFactory(client).OpenAsync(new Uri(path), 48000, CancellationToken.None);
            float[] buffer = new float[2 * 9600];
            Assert.That(source.Read(buffer, 9600), Is.EqualTo(9600));
            Assert.That(buffer.Max(Math.Abs), Is.GreaterThan(0.1f));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Factory_SpcSeek_ReachesTheTargetPosition()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".spc");
        SpcBuilder builder = SpcBuilder.SquareVoice();
        builder.Seconds = "5";
        builder.FadeMilliseconds = "1000";
        File.WriteAllBytes(path, builder.Build());
        try
        {
            using HttpClient client = new();
            using IPcmSource source = await new AudioSourceFactory(client).OpenAsync(new Uri(path), 48000, CancellationToken.None);
            Assert.That(source.Seek(4 * 48000), Is.True);
            float[] buffer = new float[2 * 4800];
            Assert.That(source.Read(buffer, 4800), Is.EqualTo(4800));
            Assert.That(buffer.Max(Math.Abs), Is.GreaterThan(0.05f));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestCase("songs4.rsn", "song_02.spc")]
    [TestCase("songs5.rsn", "song_03.spc")]
    [TestCase("songs4.rsn", "")]
    public async Task Factory_PlaysATrackOfAnRsnArchive(string fixture, string entry)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rsn");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rar", fixture), path);
        try
        {
            Uri location = entry.Length == 0 ? new Uri(path) : Aibell.Audio.Spc.RsnArchive.CreateLocation(path, entry);
            using HttpClient client = new();
            using IPcmSource source = await new AudioSourceFactory(client).OpenAsync(location, 48000, CancellationToken.None);
            float[] buffer = new float[2 * 9600];
            Assert.That(source.Read(buffer, 9600), Is.EqualTo(9600));
            Assert.That(buffer.Max(Math.Abs), Is.GreaterThan(0.1f));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Factory_DamagedRsn_IsRefused()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rsn");
        File.WriteAllBytes(path, new byte[1000]);
        try
        {
            using HttpClient client = new();
            Assert.That(async () => await new AudioSourceFactory(client).OpenAsync(new Uri(path), 48000, CancellationToken.None),
                Throws.TypeOf<NotSupportedException>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Factory_DamagedSpc_IsRefused()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".spc");
        File.WriteAllBytes(path, new byte[1000]);
        try
        {
            using HttpClient client = new();
            Assert.That(async () => await new AudioSourceFactory(client).OpenAsync(new Uri(path), 48000, CancellationToken.None),
                Throws.TypeOf<NotSupportedException>());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
