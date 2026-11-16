namespace Aibell.Application.Lyrics;

/// <summary>What is known about the track that needs lyrics.</summary>
public sealed record LyricsQuery(string Title, string Artist, string Album, TimeSpan Duration)
{
    public bool IsSearchable => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Artist);
}

/// <summary>One lyrics record a provider found. Either text may be missing; an instrumental record carries neither.</summary>
public sealed record LyricsCandidate(
    string ProviderId,
    string ExternalId,
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    string? SyncedText,
    string? PlainText,
    bool IsInstrumental)
{
    public bool HasSyncedText => !string.IsNullOrWhiteSpace(SyncedText);
}

public sealed record LyricsProviderDescriptor(string Id, string DisplayName);

/// <summary>
/// A source of lyrics, contributed by a plugin. Network failures, timeouts and unknown tracks return no result; only the caller's
/// cancellation propagates.
/// </summary>
public interface ILyricsProvider
{
    LyricsProviderDescriptor Descriptor { get; }

    /// <summary>The best match for the track, or null when the source has none.</summary>
    Task<LyricsCandidate?> FindAsync(LyricsQuery query, CancellationToken cancellationToken = default);

    /// <summary>Candidates for a manual search; empty when nothing matches.</summary>
    Task<IReadOnlyList<LyricsCandidate>> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default);
}
