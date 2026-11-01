using Aibell.Application.Playback;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;

namespace Aibell.Platform.Windows.Tests;

internal static class TestStates
{
    public static MediaSessionState Playing(PlaybackStatus status = PlaybackStatus.Playing, string title = "Title") => new(
        new(status, new(Guid.Parse("11111111-2222-3333-4444-555555555555"), new Uri("file:///music/tone.flac"), "Track", "Artist", "Album", TimeSpan.FromSeconds(60)),
            TimeSpan.FromSeconds(2), 0.8), new(Title: title, Artist: "Artist", Album: "Album", AlbumArtist: "Album Artist"),
        null, true, true, true, true, false, false, RepeatMode.Off);

    public static MediaSessionState Idle { get; } = new(new PlaybackSnapshot(PlaybackStatus.Stopped, null, TimeSpan.Zero, 1), null, null, false, false, false, false, false, false, RepeatMode.Off);
}

internal sealed class FakeController : IMediaSessionController
{
    private readonly object gate = new();
    public MediaSessionState State { get; set; } = TestStates.Idle;
    public List<MediaSessionAction> Executed { get; } = [];
    public Func<MediaSessionCommand, CancellationToken, Task>? Behavior { get; set; }
    public event EventHandler? StateChanged;
    public event EventHandler<TimeSpan>? Seeked;
    public int StateSubscribers => StateChanged?.GetInvocationList().Length ?? 0;

    public void Raise(MediaSessionState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseSeeked(TimeSpan position) => Seeked?.Invoke(this, position);

    public async Task ExecuteAsync(MediaSessionCommand command, CancellationToken cancellationToken = default)
    {
        if (Behavior is not null) await Behavior(command, cancellationToken);
        lock (gate) Executed.Add(command.Action);
    }

    public MediaSessionAction[] Snapshot()
    {
        lock (gate) return [.. Executed];
    }
}

internal sealed class FakeTransport : IMediaTransport
{
    private readonly object gate = new();
    private readonly List<TransportSnapshot> applied = [];
    public Func<Task>? StartBehavior { get; set; }
    public bool Disposed { get; private set; }
    public int DisposeCount { get; private set; }
    public bool ThrowOnRepeatedDispose { get; set; }
    public Func<Task>? DisposeBehavior { get; set; }
    public bool Started { get; private set; }
    public event Action<TransportButton>? ButtonPressed;

    public TransportSnapshot[] Applied
    {
        get { lock (gate) return [.. applied]; }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (StartBehavior is not null) await StartBehavior();
        cancellationToken.ThrowIfCancellationRequested();
        Started = true;
    }

    public void Apply(TransportSnapshot snapshot)
    {
        lock (gate) applied.Add(snapshot);
    }

    public void Press(TransportButton button) => ButtonPressed?.Invoke(button);
    public bool HasButtonSubscribers => ButtonPressed is not null;

    public async ValueTask DisposeAsync()
    {
        DisposeCount++;
        if (ThrowOnRepeatedDispose && DisposeCount > 1) throw new InvalidOperationException("Transport disposed twice.");
        Disposed = true;
        if (DisposeBehavior is not null) await DisposeBehavior();
    }
}
