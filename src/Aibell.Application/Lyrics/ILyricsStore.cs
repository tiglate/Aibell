using Aibell.Domain.Lyrics;

namespace Aibell.Application.Lyrics;

/// <summary>What the cache knows about a track. <see cref="Lyrics"/> is null for a remembered "nothing found" answer.</summary>
public sealed record StoredLyrics(TrackLyrics? Lyrics, DateTimeOffset StoredAt);

/// <summary>The local lyrics cache: fetched lyrics, the user's own choices and typed text, and remembered misses.</summary>
public interface ILyricsStore
{
    Task<StoredLyrics?> GetAsync(Guid trackId, CancellationToken cancellationToken = default);

    /// <summary>Stores lyrics fetched automatically. Never replaces an entry the user chose or typed; returns false when it kept that entry.</summary>
    Task<bool> SaveFetchedAsync(Guid trackId, TrackLyrics lyrics, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Remembers that automatic lookups found nothing. Never replaces an existing entry; returns false when one exists.</summary>
    Task<bool> SaveMissAsync(Guid trackId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Stores the user's own choice or text, replacing whatever was there.</summary>
    Task SaveUserAsync(Guid trackId, TrackLyrics lyrics, DateTimeOffset at, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid trackId, CancellationToken cancellationToken = default);
}

/// <summary>Reads the lyrics stored in an audio file's tags. Returns null for none, an unreadable file or a non-file location.</summary>
public interface ILyricsFileReader
{
    Task<TrackLyrics?> ReadAsync(Uri location, CancellationToken cancellationToken = default);
}
