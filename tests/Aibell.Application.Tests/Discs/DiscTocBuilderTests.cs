using Aibell.Application.Discs;
using NUnit.Framework;

namespace Aibell.Application.Tests.Discs;

[TestFixture]
public sealed class DiscTocBuilderTests
{
    [Test]
    public void Build_AllAudio_KeepsEveryTrackAndTheLeadOut()
    {
        DiscToc? toc = DiscTocBuilder.Build([new(1, false, 150), new(2, false, 15363), new(3, false, 32314)], 46592);

        Assert.That(toc, Is.Not.Null);
        Assert.That(toc!.TrackOffsets, Is.EqualTo(Items(150, 15363, 32314)));
        Assert.That(toc.LeadOutOffset, Is.EqualTo(46592));
    }

    [Test]
    public void Build_TrailingDataSession_EndsAudioBeforeTheSessionGap()
    {
        DiscToc? toc = DiscTocBuilder.Build([new(1, false, 150), new(2, false, 20000), new(3, true, 60000)], 90000);

        Assert.That(toc!.TrackCount, Is.EqualTo(2));
        Assert.That(toc.LeadOutOffset, Is.EqualTo(60000 - 11400));
    }

    [Test]
    public void Build_LeadingDataTrack_StartsAtFirstAudioTrackNumber()
    {
        DiscToc? toc = DiscTocBuilder.Build([new(1, true, 150), new(2, false, 5000), new(3, false, 9000)], 12000);

        Assert.That(toc!.FirstTrackNumber, Is.EqualTo(2));
        Assert.That(toc.TrackCount, Is.EqualTo(2));
    }

    [Test]
    public void Build_UnorderedInput_IsSortedByTrackNumber()
    {
        DiscToc? toc = DiscTocBuilder.Build([new(2, false, 9000), new(1, false, 150)], 12000);
        Assert.That(toc!.TrackOffsets, Is.EqualTo(Items(150, 9000)));
    }

    [Test]
    public void Build_OnlyDataTracks_ReturnsNull() =>
        Assert.That(DiscTocBuilder.Build([new(1, true, 150)], 5000), Is.Null);

    [Test]
    public void Build_NoTracks_ReturnsNull() =>
        Assert.That(DiscTocBuilder.Build([], 5000), Is.Null);

    [Test]
    public void Build_InconsistentOffsets_ReturnsNullInsteadOfThrowing() =>
        Assert.That(DiscTocBuilder.Build([new(1, false, 9000), new(2, false, 150)], 12000), Is.Null);

    private static T[] Items<T>(params T[] items) => items;
}
