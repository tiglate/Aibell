using Aibell.Application.Playback;
using Aibell.Audio.Midi.Tests;
using Aibell.Playback.Core;
using NUnit.Framework;

namespace Aibell.Playback.Miniaudio.Tests;

[TestFixture]
public sealed class MidiPlaybackTests
{
    // One quarter note (A4) at the default 120 bpm, 96 ticks per quarter.
    private static readonly byte[] Song =
    [
        .. "MThd"u8.ToArray(), 0, 0, 0, 6, 0, 0, 0, 1, 0, 96,
        .. "MTrk"u8.ToArray(), 0, 0, 0, 12, 0, 0x90, 69, 100, 96, 0x80, 69, 0, 0, 0xFF, 0x2F, 0
    ];

    [TestCase(".mid")]
    [TestCase(".MIDI")]
    [TestCase(".rmi")]
    public async Task Factory_PlaysMidiWithoutTheManagedEngineSetting(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, Song);
        try
        {
            using HttpClient client = new();
            using IPcmSource source = await new AudioSourceFactory(client).OpenAsync(new Uri(path), 48000, CancellationToken.None);
            float[] buffer = new float[2 * 4800];
            Assert.That(source.Read(buffer, 4800), Is.EqualTo(4800));
            Assert.That(buffer.Max(Math.Abs), Is.GreaterThan(0.02f));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // 882 Hz at key 60 (a looped sine of 50 points at 44.1 kHz), so the song's A4 (key 69) sounds at 1483 Hz; the built-in piano is far from that.
    private static byte[] SineBank()
    {
        var bank = new SoundFontBuilder();
        int sample = bank.AddSine(50);
        int instrument = bank.AddInstrument("sine", new SoundFontBuilder.ZoneSpec([(54, 1), (38, -7973)], sample));
        bank.AddPreset("Sine", 0, 0, SoundFontBuilder.ZoneSpec.Of(instrument));
        return bank.ToArray();
    }

    private static async Task<double> PlayedHertz(string? soundFontPath)
    {
        string midi = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mid");
        File.WriteAllBytes(midi, Song);
        try
        {
            using HttpClient client = new();
            using IPcmSource source = await new AudioSourceFactory(client, soundFontPath: soundFontPath).OpenAsync(new Uri(midi), 48000, CancellationToken.None);
            float[] buffer = new float[2 * 24000];
            Assert.That(source.Read(buffer, 24000), Is.EqualTo(24000));
            int crossings = 0;
            for (int i = 2400 + 1; i < 24000; i++) if (buffer[(i - 1) * 2] < 0 && buffer[i * 2] >= 0) crossings++;
            return crossings / ((24000 - 2400) / 48000.0);
        }
        finally
        {
            File.Delete(midi);
        }
    }

    [Test]
    public async Task Factory_PlaysMidiFromTheChosenSoundFont()
    {
        string bank = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sf2");
        File.WriteAllBytes(bank, SineBank());
        try
        {
            Assert.That(await PlayedHertz(bank), Is.EqualTo(882 * Math.Pow(2, 9 / 12.0)).Within(30));
            Assert.That(await PlayedHertz(null), Is.Not.EqualTo(882 * Math.Pow(2, 9 / 12.0)).Within(150));
        }
        finally
        {
            File.Delete(bank);
        }
    }

    [TestCase("missing")]
    [TestCase("garbage")]
    public async Task Factory_BadSoundFont_FallsBackToTheBuiltInInstruments(string kind)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sf2");
        if (kind == "garbage") File.WriteAllBytes(path, new byte[500]);
        try
        {
            Assert.That(await PlayedHertz(path), Is.Not.EqualTo(882 * Math.Pow(2, 9 / 12.0)).Within(150));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
