using Aibell.Domain.Podcasts;

namespace Aibell.Application.Podcasts;

public interface IPodcastPositionRepository
{
    // Stores only if the episode still refers to this downloaded file. No file I/O or admission to Music.
    Task<bool> SaveEpisodePositionAsync(Guid subscriptionId, string episodeId, string localPath,
        PodcastPlaybackProgress progress, CancellationToken cancellationToken = default);
}

/// <summary>Reads the shared player on its owning thread for an explicit Save position request.</summary>
public interface IPodcastPositionSource
{
    ValueTask<PodcastPlaybackSnapshot?> GetPodcastPlaybackAsync(CancellationToken cancellationToken = default);
}

public sealed record PodcastPlaybackSnapshot(Uri File, TimeSpan Position, bool CanSavePosition);
