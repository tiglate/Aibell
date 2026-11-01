using Aibell.Domain.Media;

namespace Aibell.Application.Discs;

/// <summary>How the disc window reaches the one shared player.</summary>
public interface IDiscPlaybackHandoff
{
    /// <summary>Queues <paramref name="tracks"/> from <paramref name="startIndex"/> onward and starts the first one.</summary>
    Task PlayDiscAsync(IReadOnlyList<Track> tracks, int startIndex, CancellationToken cancellationToken = default);
}
