using Aibell.Domain.Podcasts;
using NUnit.Framework;

namespace Aibell.Domain.Tests.Podcasts;

[TestFixture]
public sealed class PodcastLocationTests
{
    [TestCase("https://example.invalid/feed.xml", true)]
    [TestCase("http://example.invalid/feed?show=1", true)]
    [TestCase("file:///tmp/feed.xml", false)]
    [TestCase("ftp://example.invalid/feed.xml", false)]
    [TestCase("https://user:password@example.invalid/feed", false)]
    [TestCase("https://example.invalid/feed#section", false)]
    [TestCase("feed.xml", false)]
    [TestCase("https://example.invalid/a path", false)]
    public void IsSupported_ValidatesNetworkLocationWithoutConnecting(string location, bool supported) =>
        Assert.That(PodcastLocation.IsSupported(new Uri(location, UriKind.RelativeOrAbsolute)), Is.EqualTo(supported));

    [Test]
    public void IsSupported_NullOrOversized_ReturnsFalse()
    {
        Assert.That(PodcastLocation.IsSupported(null), Is.False);
        Assert.That(PodcastLocation.IsSupported(new Uri("https://example.invalid/" + new string('a', 4096))), Is.False);
    }
}
