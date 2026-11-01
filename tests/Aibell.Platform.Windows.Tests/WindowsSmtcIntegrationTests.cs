using Aibell.Application.Playback;
using Aibell.Platform.Windows.Native;
using NUnit.Framework;

namespace Aibell.Platform.Windows.Tests;

/// <summary>
/// Exercises the real System Media Transport Controls. These run only on Windows 10+ and report as ignored elsewhere.
/// Physical media-key and shell-flyout behavior still requires manual verification.
/// </summary>
[TestFixture]
[Platform("Win")]
internal sealed class WindowsSmtcIntegrationTests
{
    [SetUp]
    public void RequireSmtc() => Assume.That(WindowsMediaSession.IsSupported, Is.True);

    [Test]
    public async Task Transport_StartApplyReadBackDispose_RoundTripsShellState()
    {
        SmtcMediaTransport transport = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await transport.StartAsync(timeout.Token);
        try
        {
            transport.Apply(TransportMapper.Map(TestStates.Playing(title: "Integration 日本語")));
            (TransportPlayback Playback, bool PlayEnabled, string Title, bool HasThumbnail) observed = default;
            while (observed.Title != "Integration 日本語" && !timeout.IsCancellationRequested)
            {
                await Task.Delay(20, timeout.Token);
                observed = await transport.ReadBackAsync(timeout.Token);
            }
            Assert.Multiple(() =>
            {
                Assert.That(observed.Title, Is.EqualTo("Integration 日本語"));
                Assert.That(observed.Playback, Is.EqualTo(TransportPlayback.Playing));
                Assert.That(observed.PlayEnabled, Is.True);
            });
        }
        finally { await transport.DisposeAsync(); }
    }

    // A valid 1x1 PNG.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Test]
    public async Task Transport_ArtworkFile_SetsAndClearsThumbnailWithoutLockingTheFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aibell-smtc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string cover = Path.Combine(directory, "cover.png");
        await File.WriteAllBytesAsync(cover, TinyPng);
        SmtcMediaTransport transport = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await transport.StartAsync(timeout.Token);
        try
        {
            transport.Apply(TransportMapper.Map(TestStates.Playing(title: "With art") with { Artwork = new Uri(cover) }));
            Assert.That(await WaitForAsync(transport, observed => observed.Title == "With art" && observed.HasThumbnail, timeout.Token), Is.True,
                "the shell should hold a thumbnail after the artwork file is published");
            Assert.DoesNotThrow(() => File.Delete(cover), "the shell must not keep the cover file open");

            transport.Apply(TransportMapper.Map(TestStates.Playing(title: "Without art")));
            Assert.That(await WaitForAsync(transport, observed => observed.Title == "Without art" && !observed.HasThumbnail, timeout.Token), Is.True,
                "a track without artwork must not keep the previous thumbnail");
        }
        finally
        {
            await transport.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Transport_MissingArtworkFile_StillPublishesMetadata()
    {
        SmtcMediaTransport transport = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await transport.StartAsync(timeout.Token);
        try
        {
            transport.Apply(TransportMapper.Map(TestStates.Playing(title: "No file") with { Artwork = new Uri(@"C:\does-not-exist\cover.png") }));
            Assert.That(await WaitForAsync(transport, observed => observed.Title == "No file" && !observed.HasThumbnail, timeout.Token), Is.True);
        }
        finally { await transport.DisposeAsync(); }
    }

    private static async Task<bool> WaitForAsync(SmtcMediaTransport transport,
        Func<(TransportPlayback Playback, bool PlayEnabled, string Title, bool HasThumbnail), bool> condition, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(20, cancellationToken);
            if (condition(await transport.ReadBackAsync(cancellationToken))) return true;
        }
        return false;
    }

    [Test]
    public async Task Session_StartStopTwiceInOneProcess_Succeeds()
    {
        for (int index = 0; index < 2; index++)
        {
            FakeController controller = new() { State = TestStates.Playing() };
            await using WindowsMediaSession session = new();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            await session.StartAsync(controller, timeout.Token);
            controller.Raise(TestStates.Playing(PlaybackStatus.Paused));
        }
    }
}
