namespace Aibell.Domain.Playlists;

public sealed record Playlist(
    Guid Id,
    string Name,
    IReadOnlyList<Guid> TrackIds,
    SmartPlaylistDefinition? Definition = null)
{
    public bool IsSmart => Definition is not null;
}
