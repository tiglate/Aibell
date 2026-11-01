using Aibell.Playback.Core;
using NUnit.Framework;

namespace Aibell.Playback.Miniaudio.Tests;

/// <summary>
/// Manual check against a real station; needs the network and so never runs with the normal suite. Run it with
/// <c>AIBELL_RADIO_URL=https://... dotnet test tests/Aibell.Playback.Miniaudio.Tests --filter Name~RealStation</c>
/// (optionally AIBELL_RADIO_SECONDS, default 8). It opens the address the way the player does and decodes a few seconds.
/// </summary>
[TestFixture]
public sealed class RadioSmokeTests
{
    [Test]
    [Explicit("Needs the network and a station address in AIBELL_RADIO_URL.")]
    public async Task RealStation_DecodesSomeSecondsOfAudio()
    {
        string? address = Environment.GetEnvironmentVariable("AIBELL_RADIO_URL");
        if (string.IsNullOrWhiteSpace(address)) Assert.Ignore("Set AIBELL_RADIO_URL.");
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("AIBELL_RADIO_SECONDS"), out int configured) ? configured : 8;
        using HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = Timeout.InfiniteTimeSpan };

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        using IPcmSource source = await new AudioSourceFactory(client).OpenAsync(new Uri(address!), 48000, CancellationToken.None);
        TestContext.Out.WriteLine($"opened in {watch.ElapsedMilliseconds} ms");
        float[] buffer = new float[4096 * 2];
        long frames = 0;
        float peak = 0;
        double energy = 0;
        DateTime end = DateTime.UtcNow.AddSeconds(seconds + 20);
        while (frames < 48000L * seconds && DateTime.UtcNow < end)
        {
            int read = source.Read(buffer, 4096);
            if (read == 0) break;
            frames += read;
            for (int i = 0; i < read * 2; i++)
            {
                peak = Math.Max(peak, Math.Abs(buffer[i]));
                energy += buffer[i] * buffer[i];
            }
        }
        TestContext.Out.WriteLine($"{source.GetType().Name}: {frames / 48000.0:0.0} s decoded, peak {peak:0.000}, rms {Math.Sqrt(energy / Math.Max(1, frames * 2)):0.000}");
        Assert.That(frames, Is.GreaterThanOrEqualTo(48000L * seconds), "not enough audio arrived");
        Assert.That(peak, Is.GreaterThan(0.01f), "the audio is silent");
        Assert.That(peak, Is.LessThanOrEqualTo(1.5f), "the audio is far beyond full scale");
    }
}
