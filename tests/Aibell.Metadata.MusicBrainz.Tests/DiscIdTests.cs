using Aibell.Application.Discs;
using NUnit.Framework;

namespace Aibell.Metadata.MusicBrainz.Tests;

[TestFixture]
public sealed class DiscIdTests
{
    // The worked example on https://musicbrainz.org/doc/Disc_ID_Calculation
    private static readonly DiscToc Example = new([150, 15363, 32314, 46592, 63414, 80489], 95462);

    [Test]
    public void Compute_DocumentedExample_ReturnsPublishedDiscId() =>
        Assert.That(DiscId.Compute(Example), Is.EqualTo("49HHV7Eb8UKF3aQiNmu1GR8vKTY-"));

    [Test]
    public void Compute_IsAlwaysTwentyEightUrlSafeCharacters()
    {
        string id = DiscId.Compute(new DiscToc([150, 20000], 40000));
        Assert.That(id, Has.Length.EqualTo(28));
        Assert.That(id, Does.Not.Contain("+").And.Not.Contain("/").And.Not.Contain("="));
    }

    [Test]
    public void TocQuery_ListsFirstLastLeadOutAndOffsets() =>
        Assert.That(DiscId.TocQuery(Example), Is.EqualTo("1+6+95462+150+15363+32314+46592+63414+80489"));

    [Test]
    public void Compute_FirstTrackAboveOne_ChangesTheId() =>
        Assert.That(DiscId.Compute(new DiscToc([150, 15363], 30000, firstTrackNumber: 2)), Is.Not.EqualTo(DiscId.Compute(new DiscToc([150, 15363], 30000))));
}
