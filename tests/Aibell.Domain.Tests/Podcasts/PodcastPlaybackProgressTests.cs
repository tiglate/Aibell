using Aibell.Domain.Podcasts;
using NUnit.Framework;

namespace Aibell.Domain.Tests.Podcasts;

[TestFixture]
public sealed class PodcastPlaybackProgressTests
{
    [TestCase(0, 10, false)]
    [TestCase(1, 10, true)]
    [TestCase(10, 10, false)]
    public void Validate_BoundedPosition_ReportsResumeAvailability(int position, int duration, bool canResume)
    {
        PodcastPlaybackProgress progress = new(TimeSpan.FromTicks(position), TimeSpan.FromTicks(duration), DateTimeOffset.UnixEpoch);
        Assert.That(progress.Validate, Throws.Nothing);
        Assert.That(progress.CanResume, Is.EqualTo(canResume));
    }

    [TestCase(-1, 10)]
    [TestCase(11, 10)]
    [TestCase(0, 0)]
    [TestCase(0, -1)]
    public void Validate_InvalidBounds_Rejects(int position, int duration)
    {
        PodcastPlaybackProgress progress = new(TimeSpan.FromTicks(position), TimeSpan.FromTicks(duration), DateTimeOffset.UnixEpoch);
        Assert.That(progress.Validate, Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Validate_DurationLimit_UsesExactThirtyDayBoundary()
    {
        PodcastPlaybackProgress valid = new(TimeSpan.Zero, TimeSpan.FromDays(30), DateTimeOffset.UnixEpoch);
        Assert.That(valid.Validate, Throws.Nothing);
        Assert.That((valid with { Duration = valid.Duration.Add(TimeSpan.FromTicks(1)) }).Validate,
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(0, 20, 0)]
    [TestCase(9, 20, 9)]
    [TestCase(9, 9, 0)]
    [TestCase(9, 8, 0)]
    [TestCase(9, 0, 0)]
    public void ResumeStart_ActualFileDuration_RestartsOnlyAtOrBeyondEnd(int saved, int actual, int expected)
    {
        PodcastPlaybackProgress progress = new(TimeSpan.FromTicks(saved), TimeSpan.FromTicks(20), DateTimeOffset.UnixEpoch);
        Assert.That(progress.ResumeStart(TimeSpan.FromTicks(actual)), Is.EqualTo(TimeSpan.FromTicks(expected)));
    }
}
