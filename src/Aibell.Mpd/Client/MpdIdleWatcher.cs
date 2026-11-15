namespace Aibell.Mpd.Client;

/// <summary>Tells the application when something changes on the server, so its views can refresh without polling.</summary>
public interface IMpdChangeSource
{
    /// <summary>Runs until cancelled, calling <paramref name="onChanged"/> with the changed subsystems (<c>player</c>, <c>playlist</c>, …).</summary>
    Task WatchAsync(Func<IReadOnlyList<string>, Task> onChanged, CancellationToken cancellationToken);
}

/// <summary>Watches a server over a connection of its own with MPD's <c>idle</c> command.</summary>
public sealed class MpdIdleWatcher(MpdEndpoint endpoint) : IMpdChangeSource
{
    private static readonly string[] Subsystems =
        ["database", "update", "stored_playlist", "playlist", "player", "mixer", "output", "options", "partition"];

    public async Task WatchAsync(Func<IReadOnlyList<string>, Task> onChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        await using MpdConnection connection = await MpdConnection.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<string> changed = await connection.IdleAsync(Subsystems, cancellationToken).ConfigureAwait(false);
            if (changed.Count > 0) await onChanged(changed).ConfigureAwait(false);
        }
    }
}
