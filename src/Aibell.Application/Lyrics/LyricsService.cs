using Aibell.Domain.Lyrics;
using Aibell.Domain.Media;

namespace Aibell.Application.Lyrics;

public enum LyricsStatus
{
    /// <summary>Lyrics were found; see <see cref="LyricsResult.Lyrics"/>.</summary>
    Found,

    /// <summary>No source has lyrics for the track.</summary>
    NotFound,

    /// <summary>Lyrics are switched off.</summary>
    Disabled,

    /// <summary>The item has no lyrics to look for (radio, stream, device, or missing title or artist) and nothing stored.</summary>
    Unavailable,

    /// <summary>Lookups were tried but no provider could be reached or none is installed.</summary>
    NoProvider,
}

public sealed record LyricsResult(LyricsStatus Status, TrackLyrics? Lyrics = null);

/// <summary>
/// Finds lyrics for a track: the user's own entry, then the audio file's tags, then the cache, then the lyrics plugins. Nothing is sent
/// anywhere unless lyrics are enabled; the caller cancels a lookup when the track changes. Misses are remembered for a while so that an
/// unmatched track does not hit the network on every play.
/// </summary>
public sealed class LyricsService(
    ILyricsStore store,
    ILyricsFileReader fileReader,
    IEnumerable<ILyricsProvider> providers,
    Func<bool> isEnabled,
    TimeProvider? clock = null)
{
    public static readonly TimeSpan MissLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan LookupLimit = TimeSpan.FromSeconds(25);
    private readonly ILyricsProvider[] providerList = [.. providers];
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public bool HasProviders => providerList.Length > 0;

    public static bool CanHaveLyrics(Track track) => track.Location.IsFile;

    public static LyricsQuery QueryFor(Track track) => new(track.Title, track.Artist, track.Album, track.Duration);

    public async Task<LyricsResult> ResolveAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!isEnabled()) return new LyricsResult(LyricsStatus.Disabled);
        if (!CanHaveLyrics(track)) return new LyricsResult(LyricsStatus.Unavailable);

        StoredLyrics? stored = await store.GetAsync(track.Id, cancellationToken).ConfigureAwait(false);
        if (stored?.Lyrics is { IsUserOwned: true } owned) return new LyricsResult(LyricsStatus.Found, owned);
        TrackLyrics? embedded = await fileReader.ReadAsync(track.Location, cancellationToken).ConfigureAwait(false);
        if (embedded is { IsEmpty: false }) return new LyricsResult(LyricsStatus.Found, embedded);
        if (stored?.Lyrics is { } cached) return new LyricsResult(LyricsStatus.Found, cached);
        if (stored is not null && time.GetUtcNow() - stored.StoredAt < MissLifetime) return new LyricsResult(LyricsStatus.NotFound);

        LyricsQuery query = QueryFor(track);
        if (!query.IsSearchable) return new LyricsResult(LyricsStatus.Unavailable);
        if (providerList.Length == 0) return new LyricsResult(LyricsStatus.NoProvider);

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(LookupLimit);
        bool reachedAny = false;
        foreach (ILyricsProvider provider in providerList)
        {
            LyricsCandidate? candidate;
            try
            {
                candidate = await provider.FindAsync(query, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                continue;
            }
            reachedAny = true;
            TrackLyrics? lyrics = candidate is null ? null : ToLyrics(candidate, LyricsOrigin.Provider);
            if (lyrics is null) continue;
            await store.SaveFetchedAsync(track.Id, lyrics, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            return new LyricsResult(LyricsStatus.Found, lyrics);
        }
        // An unreachable service is not an answer: do not remember it as "no lyrics".
        if (reachedAny) await store.SaveMissAsync(track.Id, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return new LyricsResult(LyricsStatus.NotFound);
    }

    /// <summary>Candidates from every provider, for the manual search. A provider that fails contributes nothing.</summary>
    public async Task<IReadOnlyList<LyricsCandidate>> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!isEnabled()) return [];
        List<LyricsCandidate> results = [];
        foreach (ILyricsProvider provider in providerList)
        {
            try
            {
                results.AddRange(await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // One broken provider must not hide the others.
            }
        }
        return results;
    }

    /// <summary>Makes a search result the track's lyrics. Returns null when the candidate carries no usable text.</summary>
    public async Task<TrackLyrics?> SelectAsync(Track track, LyricsCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(candidate);
        TrackLyrics? lyrics = ToLyrics(candidate, LyricsOrigin.UserSelected);
        if (lyrics is null) return null;
        await store.SaveUserAsync(track.Id, lyrics, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return lyrics;
    }

    /// <summary>Stores text the user typed or pasted. Empty text removes the user's entry. LRC timestamps make the lyrics synced.</summary>
    public async Task<TrackLyrics?> SaveWrittenAsync(Track track, string? text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        TrackLyrics? lyrics = TrackLyrics.FromText(text, text, false, LyricsOrigin.UserWritten);
        if (lyrics is null)
        {
            await store.RemoveAsync(track.Id, cancellationToken).ConfigureAwait(false);
            return null;
        }
        await store.SaveUserAsync(track.Id, lyrics, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return lyrics;
    }

    /// <summary>Forgets the stored entry so that the next lookup starts over.</summary>
    public Task ForgetAsync(Track track, CancellationToken cancellationToken = default) => store.RemoveAsync(track.Id, cancellationToken);

    private static TrackLyrics? ToLyrics(LyricsCandidate candidate, LyricsOrigin origin) =>
        TrackLyrics.FromText(candidate.SyncedText, candidate.PlainText, candidate.IsInstrumental, origin, candidate.ProviderId);
}
