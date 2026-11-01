using Aibell.Domain.Playback;
using Aibell.Playback.LibVLC;
using LibVLCSharp.Shared;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.Playback;

/// <summary>LibVLC's equalizer plays 12 dB quieter at a preamp of 0; these tests decode to memory and compare loudness.</summary>
[TestFixture]
[NonParallelizable]
[Category("NativePlayback")]
public sealed class LibVlcEqualizerLoudnessTests
{
    [SetUp]
    public void RequireNativeRuntime()
    {
        if (Environment.GetEnvironmentVariable("AIBELL_NATIVE_PLAYBACK_TESTS") != "1")
            Assert.Ignore("Set AIBELL_NATIVE_PLAYBACK_TESTS=1 to validate the installed native LibVLC runtime.");
    }

    [Test]
    public void FlatCurve_KeepsTheLoudnessOfTheUnequalizedSignal()
    {
        double plain = RootMeanSquare(_ => { });
        double flat = RootMeanSquare(player => Apply(player, new EqualizerSettings { IsEnabled = true }));
        double quieter = RootMeanSquare(player => Apply(player, new EqualizerSettings { IsEnabled = true, Preamp = -6 }));

        Assert.That(DecibelsBetween(plain, flat), Is.EqualTo(0).Within(0.5), "a flat equalizer must not change the volume");
        Assert.That(DecibelsBetween(plain, quieter), Is.EqualTo(-6).Within(0.5), "the preamp is relative to unity");
    }

    private static void Apply(MediaPlayer player, EqualizerSettings settings)
    {
        using Equalizer native = new();
        native.SetPreamp(LibVlcPlaybackEngine.NativePreamp(settings));
        for (uint band = 0; band < EqualizerSettings.BandCount; band++) native.SetAmp((float)settings.Bands[(int)band], band);
        player.SetEqualizer(native);
    }

    private static double DecibelsBetween(double reference, double measured) => 20 * Math.Log10(measured / reference);

    private static double RootMeanSquare(Action<MediaPlayer> configure)
    {
        double sum = 0;
        long count = 0;
        using LibVLC libVlc = new("--no-video", "--quiet");
        using MediaPlayer player = new(libVlc);
        player.SetAudioFormat("S16N", 44100, 2);
        player.SetAudioCallbacks(
            (_, samples, frames, _) =>
            {
                int length = (int)frames * 2;
                short[] buffer = new short[length];
                System.Runtime.InteropServices.Marshal.Copy(samples, buffer, 0, length);
                lock (buffer) { foreach (short value in buffer) sum += (double)value * value; count += length; }
            },
            (_, _) => { }, (_, _) => { }, (_, _) => { }, _ => { });
        configure(player);
        using Media media = new(libVlc, new Uri(Path.Combine(AppContext.BaseDirectory, "fixtures", "fixture.wav")));
        player.Media = media;
        using ManualResetEventSlim ended = new();
        player.EndReached += (_, _) => ended.Set();
        player.Play();
        Assert.That(ended.Wait(TimeSpan.FromSeconds(15)), Is.True, "the fixture finishes decoding");
        Thread.Sleep(300);
        return Math.Sqrt(sum / Math.Max(count, 1));
    }
}
