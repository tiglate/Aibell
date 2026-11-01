using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Aibell.Desktop.Controls;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

public sealed partial class TransportRangeAutomationTests
{
    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task TransportRange_AcceptedWorkerRequestThenAncestorUnavailable_DoesNotApply(bool volume, bool hidden)
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => model.CanSeekPlayback);
        TransportRangeSurface surface = window.FindControl<TransportRangeSurface>(volume ? "VolumeSurface" : "PositionSeekSurface")!;
        IRangeValueProvider provider = ControlAutomationPeer.CreatePeerForElement(surface).GetProvider<IRangeValueProvider>()!;
        int beforeSeek = engine.SeekCount;
        int beforeVolume = engine.VolumeCount;
        double beforeValue = engine.Snapshot.Volume;
        Assert.That(provider.IsReadOnly, Is.False);
        RequestFromWorkerWithoutPumping(provider, volume ? 0.33 : 30);

        Control ancestor = (Control)surface.Parent!;
        if (hidden) ancestor.IsVisible = false;
        else ancestor.IsEnabled = false;
        // No await or dispatcher pump occurs between acceptance and the ancestor state change.
        await PumpAsync();

        Assert.Multiple(() =>
        {
            Assert.That(provider.IsReadOnly, Is.True);
            Assert.That(engine.SeekCount, Is.EqualTo(beforeSeek), "queued seek must revalidate effective availability");
            Assert.That(engine.VolumeCount, Is.EqualTo(beforeVolume), "queued volume must revalidate effective availability");
            Assert.That(engine.Snapshot.Volume, Is.EqualTo(beforeValue));
        });
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task TransportRange_AcceptedWorkerSeekThenNewPlaybackSessionWithSameTrack_DiscardsStaleRequest(bool stopAndPlay)
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => model.CanSeekPlayback);
        TransportRangeSurface surface = window.FindControl<TransportRangeSurface>("PositionSeekSurface")!;
        IRangeValueProvider provider = ControlAutomationPeer.CreatePeerForElement(surface).GetProvider<IRangeValueProvider>()!;
        Assert.That(provider.IsReadOnly, Is.False);
        RequestFromWorkerWithoutPumping(provider, 30);

        // These fixture engine calls complete synchronously. A real thread join above prevents Avalonia's
        // synchronization context from pumping the accepted UI post before the session changes.
        if (stopAndPlay)
        {
            engine.StopAsync().GetAwaiter().GetResult();
            engine.PlayAsync().GetAwaiter().GetResult();
        }
        else
        {
            engine.LoadAsync(second).GetAwaiter().GetResult();
            engine.PlayAsync().GetAwaiter().GetResult();
            engine.LoadAsync(first).GetAwaiter().GetResult();
            engine.PlayAsync().GetAwaiter().GetResult();
        }
        Assert.That(engine.Snapshot.Track!.Id, Is.EqualTo(first.Id));
        await PumpAsync();

        Assert.That(engine.SeekCount, Is.Zero, "a matching track ID cannot revive a seek accepted for an earlier playback session");
        Assert.That(engine.Snapshot.Position, Is.EqualTo(TimeSpan.Zero));
    }

    [AvaloniaTest]
    public async Task TransportRange_RecoveredRangeThenStop_DisablesSeekWithLoadedTrack()
    {
        await OpenAsync();
        await model.TogglePlaybackAsync();
        await UntilAsync(() => model.CanSeekPlayback);
        TransportRangeSurface surface = window.FindControl<TransportRangeSurface>("PositionSeekSurface")!;
        surface.UpdateRangeState(0, 120, double.NaN, 5, 15, true);
        Assert.That(surface.IsEnabled, Is.False);
        surface.UpdateRangeState(0, 120, 0, 5, 15, true);
        Assert.That(surface.IsEnabled, Is.True);

        await model.StopPlaybackAsync();
        await PumpAsync();

        Assert.That(model.CanSeekPlayback, Is.False);
        Assert.That(surface.IsEnabled, Is.False, "A loaded track must not leave stopped transport seekable after recovery.");
        IRangeValueProvider provider = ControlAutomationPeer.CreatePeerForElement(surface).GetProvider<IRangeValueProvider>()!;
        Assert.That(provider.IsReadOnly, Is.True);
        provider.SetValue(30);
        await PumpAsync();
        Assert.That(engine.SeekCount, Is.Zero);
    }

    private static void RequestFromWorkerWithoutPumping(IRangeValueProvider provider, double value)
    {
        Exception? failure = null;
        Thread worker = new(() =>
        {
            try { provider.SetValue(value); }
            catch (Exception exception) { failure = exception; }
        })
        { IsBackground = true };
        worker.Start();
        Assert.That(worker.Join(TimeSpan.FromSeconds(5)), Is.True, "worker SetValue must return without a UI-thread round trip");
        Assert.That(failure, Is.Null);
    }
}
