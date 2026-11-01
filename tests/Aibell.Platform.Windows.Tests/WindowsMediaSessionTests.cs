using Aibell.Application.Playback;
using NUnit.Framework;

namespace Aibell.Platform.Windows.Tests;

[TestFixture]
internal sealed class WindowsMediaSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource cancellation = new(Timeout);
        while (!condition()) await Task.Delay(5, cancellation.Token);
    }

    [Test]
    public async Task StartAsync_PublishesInitialStateAndSubscribesOnce()
    {
        FakeTransport transport = new();
        FakeController controller = new() { State = TestStates.Playing() };
        await using WindowsMediaSession session = new(() => transport);

        await session.StartAsync(controller);

        Assert.Multiple(() =>
        {
            Assert.That(transport.Started, Is.True);
            Assert.That(transport.Applied, Has.Length.EqualTo(1));
            Assert.That(transport.Applied[0].Title, Is.EqualTo("Title"));
            Assert.That(controller.StateSubscribers, Is.EqualTo(1));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(controller));
    }

    [Test]
    public async Task StateChanged_PublishesOnlyWhenTransportVisibleStateChanges()
    {
        FakeTransport transport = new();
        FakeController controller = new() { State = TestStates.Playing() };
        await using WindowsMediaSession session = new(() => transport);
        await session.StartAsync(controller);

        controller.Raise(TestStates.Playing() with { Playback = TestStates.Playing().Playback with { Position = TimeSpan.FromSeconds(30) } });
        controller.Raise(TestStates.Playing(PlaybackStatus.Paused));
        controller.Raise(TestStates.Idle);

        Assert.That(transport.Applied.Select(snapshot => snapshot.Playback), Is.EqualTo(new[]
        {
            TransportPlayback.Playing, TransportPlayback.Paused, TransportPlayback.Closed
        }));
    }

    [Test]
    public async Task SeekedEvent_IsNotRequiredAndDoesNotRepublish()
    {
        FakeTransport transport = new();
        FakeController controller = new() { State = TestStates.Playing() };
        await using WindowsMediaSession session = new(() => transport);
        await session.StartAsync(controller);

        controller.RaiseSeeked(TimeSpan.FromSeconds(5));

        Assert.That(transport.Applied, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task ButtonPressed_DispatchesCommandsInOrderAndIgnoresUnsupportedButtons()
    {
        FakeTransport transport = new();
        FakeController controller = new() { State = TestStates.Playing() };
        controller.Behavior = async (command, token) =>
        {
            if (command.Action == MediaSessionAction.Play) await Task.Delay(30, token);
        };
        await using WindowsMediaSession session = new(() => transport);
        await session.StartAsync(controller);

        transport.Press(TransportButton.Play);
        transport.Press(TransportButton.FastForward);
        transport.Press(TransportButton.Next);
        transport.Press(TransportButton.Pause);
        await WaitUntilAsync(() => controller.Snapshot().Length == 3);

        Assert.That(controller.Snapshot(), Is.EqualTo(new[] { MediaSessionAction.Play, MediaSessionAction.Next, MediaSessionAction.Pause }));
    }

    [Test]
    public async Task ButtonPressed_ControllerFailure_KeepsDispatching()
    {
        FakeTransport transport = new();
        FakeController controller = new() { State = TestStates.Playing() };
        controller.Behavior = (command, _) => command.Action == MediaSessionAction.Stop
            ? Task.FromException(new InvalidOperationException("boom")) : Task.CompletedTask;
        await using WindowsMediaSession session = new(() => transport);
        await session.StartAsync(controller);

        transport.Press(TransportButton.Stop);
        transport.Press(TransportButton.Next);
        await WaitUntilAsync(() => controller.Snapshot().SequenceEqual([MediaSessionAction.Next]));

        Assert.That(controller.Snapshot(), Is.EqualTo(new[] { MediaSessionAction.Next }));
    }

    [Test]
    public async Task DisposeAsync_CancelsInFlightCommandAndDetachesEverything()
    {
        FakeTransport transport = new();
        FakeController controller = new() { State = TestStates.Playing() };
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;
        controller.Behavior = async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        };
        WindowsMediaSession session = new(() => transport);
        await session.StartAsync(controller);
        transport.Press(TransportButton.Play);
        await entered.Task.WaitAsync(Timeout);

        await session.DisposeAsync();
        controller.Raise(TestStates.Playing(PlaybackStatus.Paused));
        transport.Press(TransportButton.Next);
        await session.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(cancelled, Is.True);
            Assert.That(transport.Disposed, Is.True);
            Assert.That(transport.HasButtonSubscribers, Is.False);
            Assert.That(controller.StateSubscribers, Is.Zero);
            Assert.That(transport.Applied, Has.Length.EqualTo(1));
            Assert.That(controller.Snapshot(), Is.Empty);
        });
    }

    [Test]
    public async Task StartAsync_TransportFailure_ThrowsInvalidOperationAndCleansUp()
    {
        FakeTransport transport = new() { StartBehavior = () => Task.FromException(new DllNotFoundException("windows.media.dll")) };
        FakeController controller = new();
        WindowsMediaSession session = new(() => transport);

        InvalidOperationException? failure = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(controller));

        Assert.Multiple(() =>
        {
            Assert.That(failure!.InnerException, Is.TypeOf<DllNotFoundException>());
            Assert.That(transport.Disposed, Is.True);
            Assert.That(controller.StateSubscribers, Is.Zero);
        });
    }

    [Test]
    public async Task StartAsync_Cancelled_PropagatesCancellationAndCleansUp()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeTransport transport = new() { StartBehavior = () => gate.Task };
        FakeController controller = new();
        using CancellationTokenSource cancellation = new();
        WindowsMediaSession session = new(() => transport);

        Task start = session.StartAsync(controller, cancellation.Token);
        await cancellation.CancelAsync();
        gate.SetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await start);
        Assert.That(transport.Disposed, Is.True);
    }

    [Test]
    public async Task StartAsync_AlreadyCancelled_DoesNotCreateTransport()
    {
        bool created = false;
        WindowsMediaSession session = new(() => { created = true; return new FakeTransport(); });
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => session.StartAsync(new FakeController(), cancellation.Token));
        Assert.That(created, Is.False);
    }

    [Test]
    public async Task StartAsync_AfterDispose_Throws()
    {
        WindowsMediaSession session = new(() => new FakeTransport());
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync(new FakeController()));
    }

    [Test]
    public async Task DefaultTransport_OffWindows_FailsWithInvalidOperation()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.False);
        await using WindowsMediaSession session = new();

        InvalidOperationException? failure = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(new FakeController()));

        Assert.That(failure!.InnerException, Is.TypeOf<PlatformNotSupportedException>());
    }

    [Test]
    public async Task StartAsync_FactoryThrows_WrapsAndLeavesSessionDisposed()
    {
        FakeController controller = new();
        WindowsMediaSession session = new(() => throw new DllNotFoundException("windows.media.dll"));

        InvalidOperationException? failure = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(controller));

        Assert.Multiple(() =>
        {
            Assert.That(failure!.InnerException, Is.TypeOf<DllNotFoundException>());
            Assert.That(controller.StateSubscribers, Is.Zero);
        });
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync(controller));
    }

    [Test]
    public async Task DisposeAsync_Concurrent_BothCallersWaitForTheSameDrain()
    {
        FakeTransport transport = new();
        FakeController controller = new() { State = TestStates.Playing() };
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Behavior = async (_, _) => await release.Task;
        WindowsMediaSession session = new(() => transport);
        await session.StartAsync(controller);
        transport.Press(TransportButton.Play);

        ValueTask first = session.DisposeAsync();
        ValueTask second = session.DisposeAsync();
        release.SetResult();
        await first;
        await second;

        Assert.That(transport.Disposed, Is.True);
    }

    [Test]
    public async Task StartAsync_AdoptedTransportFails_DisposesExactlyOnceAndPreservesFailure()
    {
        DllNotFoundException original = new("missing native library");
        FakeTransport transport = new()
        {
            StartBehavior = () => Task.FromException(original),
            ThrowOnRepeatedDispose = true
        };
        WindowsMediaSession session = new(() => transport);

        InvalidOperationException? failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.StartAsync(new FakeController()));
        await session.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(failure!.InnerException, Is.SameAs(original));
            Assert.That(transport.DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DisposeAsync_DuringAdoptedStartup_AllCallersShareTransportDrain()
    {
        TaskCompletionSource startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource disposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeTransport transport = new()
        {
            ThrowOnRepeatedDispose = true,
            StartBehavior = async () => { startEntered.SetResult(); await releaseStart.Task; },
            DisposeBehavior = async () => { disposeEntered.SetResult(); await releaseDispose.Task; }
        };
        FakeController controller = new();
        WindowsMediaSession session = new(() => transport);
        Task start = session.StartAsync(controller);
        await startEntered.Task.WaitAsync(Timeout);

        Task first = session.DisposeAsync().AsTask();
        await disposeEntered.Task.WaitAsync(Timeout);
        Task second = session.DisposeAsync().AsTask();
        releaseStart.SetResult();
        Assert.Multiple(() =>
        {
            Assert.That(first.IsCompleted, Is.False);
            Assert.That(second.IsCompleted, Is.False);
            Assert.That(start.IsCompleted, Is.False);
        });
        releaseDispose.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await start.WaitAsync(Timeout));

        Assert.Multiple(() =>
        {
            Assert.That(transport.DisposeCount, Is.EqualTo(1));
            Assert.That(controller.StateSubscribers, Is.Zero);
            Assert.That(transport.HasButtonSubscribers, Is.False);
        });
    }

    [Test]
    public async Task DisposeAsync_DuringFactoryCreation_ReleasesUnadoptedTransportExactlyOnce()
    {
        TaskCompletionSource factoryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseFactory = new();
        FakeTransport transport = new() { ThrowOnRepeatedDispose = true };
        WindowsMediaSession session = new(() =>
        {
            factoryEntered.SetResult();
            if (!releaseFactory.Wait(Timeout)) throw new TimeoutException("Factory was not released.");
            return transport;
        });
        Task start = Task.Run(() => session.StartAsync(new FakeController()));
        try
        {
            await factoryEntered.Task.WaitAsync(Timeout);
            await session.DisposeAsync();
            await session.DisposeAsync();
        }
        finally { releaseFactory.Set(); }
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await start.WaitAsync(Timeout));

        Assert.That(transport.DisposeCount, Is.EqualTo(1));
    }

}
