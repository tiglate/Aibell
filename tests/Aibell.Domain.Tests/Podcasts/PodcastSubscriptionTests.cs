using Aibell.Domain.Podcasts;
using NUnit.Framework;

namespace Aibell.Domain.Tests.Podcasts;

[TestFixture]
public sealed class PodcastSubscriptionTests
{
    [TestCase(" https://Example.COM/feed.xml ", "https://example.com/feed.xml")]
    [TestCase("http://example.com:8080/a?b=1", "http://example.com:8080/a?b=1")]
    public void NormalizeFeedUrl_SupportedLocation_ReturnsCanonicalUri(string text, string expected) =>
        Assert.That(PodcastSubscription.NormalizeFeedUrl(text)?.AbsoluteUri, Is.EqualTo(expected));

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("ftp://example.com/feed")]
    [TestCase("file:///tmp/feed.xml")]
    [TestCase("https://user:pass@example.com/feed")]
    [TestCase("https://example.com/feed#top")]
    [TestCase("feed.xml")]
    [TestCase("https://exa mple.com/feed")]
    public void NormalizeFeedUrl_UnsupportedOrCredentialBearing_ReturnsNull(string? text) =>
        Assert.That(PodcastSubscription.NormalizeFeedUrl(text), Is.Null);

    [Test]
    public void Validate_BoundsTitleAndRequiresIdentity()
    {
        PodcastSubscription valid = new(Guid.NewGuid(), new Uri("https://example.com/feed"), "Show", "", DateTimeOffset.UnixEpoch);
        Assert.That(valid.Validate, Throws.Nothing);
        Assert.That((valid with { Id = Guid.Empty }).Validate, Throws.ArgumentException);
        Assert.That((valid with { Title = " " }).Validate, Throws.ArgumentException);
        Assert.That((valid with { Title = new string('x', 501) }).Validate, Throws.ArgumentException);
        Assert.That((valid with { FeedUrl = new Uri("https://u:p@example.com/feed") }).Validate, Throws.ArgumentException);
    }

    [Test]
    public void DownloadState_FollowsLocalPath()
    {
        StoredPodcastEpisode episode = new(Guid.NewGuid(), "id", "T", new Uri("https://example.com/a.mp3"));
        Assert.That(episode.DownloadState, Is.EqualTo(PodcastDownloadState.NotDownloaded));
        Assert.That((episode with { LocalPath = "/x/a.mp3" }).DownloadState, Is.EqualTo(PodcastDownloadState.Downloaded));
    }
}
