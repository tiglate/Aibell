namespace Aibell.Metadata.MusicBrainz;

internal interface IMusicBrainzRateLimiter
{
    Task WaitAsync(CancellationToken cancellationToken);
}
