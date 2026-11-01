namespace Aibell.Domain.Podcasts;

/// <summary>A persisted position for a downloaded episode, captured automatically or by an explicit save.</summary>
public sealed record PodcastPlaybackProgress(TimeSpan Position, TimeSpan Duration, DateTimeOffset SavedAt)
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(30);
    public bool CanResume => Position > TimeSpan.Zero && Position < Duration;

    public void Validate()
    {
        if (Duration <= TimeSpan.Zero || Duration > MaximumDuration) throw new ArgumentOutOfRangeException(nameof(Duration));
        if (Position < TimeSpan.Zero || Position > Duration) throw new ArgumentOutOfRangeException(nameof(Position));
    }

    public TimeSpan ResumeStart(TimeSpan actualDuration) =>
        Position > TimeSpan.Zero && Position < actualDuration ? Position : TimeSpan.Zero;
}
