using Aibell.Domain.Media;
using NUnit.Framework;

namespace Aibell.Domain.Tests.Media;

[TestFixture]
public sealed class TrackIdentityTests
{
    [Test]
    public void FromLocation_ReturnsStableIdentity()
    {
        Uri location = new("file:///music/album/track.flac");

        Guid first = TrackIdentity.FromLocation(location);
        Guid second = TrackIdentity.FromLocation(location);

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void FromLocation_DistinguishesLocations()
    {
        Guid first = TrackIdentity.FromLocation(new Uri("file:///music/one.flac"));
        Guid second = TrackIdentity.FromLocation(new Uri("file:///music/two.flac"));

        Assert.That(second, Is.Not.EqualTo(first));
    }
}
