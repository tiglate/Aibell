using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using NUnit.Framework;

namespace Aibell.Domain.Tests.Playback;

[TestFixture]
public sealed class PlaybackQueueModelTests
{
    [Test]
    public void MoveNext_AdvancesThroughQueuedTracks()
    {
        PlaybackQueueModel queue = new();
        Track first = CreateTrack("First");
        Track second = CreateTrack("Second");
        queue.Enqueue(first);
        queue.Enqueue(second);

        Assert.Multiple(() =>
        {
            Assert.That(queue.TryMoveNext(), Is.True);
            Assert.That(queue.Current, Is.EqualTo(first));
            Assert.That(queue.TryMoveNext(), Is.True);
            Assert.That(queue.Current, Is.EqualTo(second));
            Assert.That(queue.TryMoveNext(), Is.False);
        });
    }

    [Test]
    public void Clear_RemovesItemsAndCurrentTrack()
    {
        PlaybackQueueModel queue = new();
        queue.Enqueue(CreateTrack("Track"));
        queue.TryMoveNext();

        queue.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(queue.Items, Is.Empty);
            Assert.That(queue.Current, Is.Null);
            Assert.That(queue.TryMoveNext(), Is.False);
        });
    }

    [Test]
    public void SelectAndMovePrevious_NavigatesFromSelectedTrack()
    {
        PlaybackQueueModel queue = new();
        Track first = CreateTrack("First");
        Track second = CreateTrack("Second");
        queue.Replace([first, second]);

        bool selected = queue.TrySelect(second.Id);
        bool moved = queue.TryMovePrevious();

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.True);
            Assert.That(moved, Is.True);
            Assert.That(queue.Current, Is.EqualTo(first));
            Assert.That(queue.CanMovePrevious, Is.False);
            Assert.That(queue.CanMoveNext, Is.True);
        });
    }

    [Test]
    public void Replace_UnknownSelection_LeavesQueueUnselected()
    {
        PlaybackQueueModel queue = new();
        queue.Replace([CreateTrack("Track")]);

        bool selected = queue.TrySelect(Guid.NewGuid());

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.False);
            Assert.That(queue.Current, Is.Null);
            Assert.That(queue.CanMovePrevious, Is.False);
            Assert.That(queue.CanMoveNext, Is.True);
        });
    }

    [Test]
    public void Shuffle_PreservesCurrentTrackAndRestoresOriginalOrder()
    {
        PlaybackQueueModel queue = new();
        Track first = CreateTrack("First");
        Track second = CreateTrack("Second");
        Track third = CreateTrack("Third");
        queue.Replace([first, second, third]);
        queue.TrySelect(second.Id);

        queue.SetShuffle(true, new Random(42));

        Assert.Multiple(() =>
        {
            Assert.That(queue.ShuffleEnabled, Is.True);
            Assert.That(queue.Current, Is.EqualTo(second));
            Assert.That(queue.Items, Is.EquivalentTo(new[] { first, second, third }));
        });

        queue.SetShuffle(false);

        Assert.Multiple(() =>
        {
            Assert.That(queue.Items, Is.EqualTo(new[] { first, second, third }));
            Assert.That(queue.Current, Is.EqualTo(second));
        });
    }

    [Test]
    public void AdvanceAfterEnd_RespectsRepeatModes()
    {
        PlaybackQueueModel queue = new();
        Track first = CreateTrack("First");
        Track second = CreateTrack("Second");
        queue.Replace([first, second]);
        queue.TrySelect(second.Id);

        Assert.That(queue.TryAdvanceAfterEnd(), Is.False);

        Assert.That(queue.CycleRepeatMode(), Is.EqualTo(RepeatMode.All));
        Assert.That(queue.TryAdvanceAfterEnd(), Is.True);
        Assert.That(queue.Current, Is.EqualTo(first));

        Assert.That(queue.CycleRepeatMode(), Is.EqualTo(RepeatMode.One));
        Assert.That(queue.TryAdvanceAfterEnd(), Is.True);
        Assert.That(queue.Current, Is.EqualTo(first));

        Assert.That(queue.CycleRepeatMode(), Is.EqualTo(RepeatMode.Off));
    }

    [Test]
    public void RestoredModes_ApplyToTheNextReplacementQueue()
    {
        PlaybackQueueModel queue = new();
        Track first = CreateTrack("First");
        Track second = CreateTrack("Second");
        queue.SetShuffle(true, new Random(42));
        queue.SetRepeatMode(RepeatMode.One);

        queue.Replace([first, second]);

        Assert.Multiple(() =>
        {
            Assert.That(queue.ShuffleEnabled, Is.True);
            Assert.That(queue.RepeatMode, Is.EqualTo(RepeatMode.One));
            Assert.That(queue.Items, Is.EquivalentTo(new[] { first, second }));
        });
    }

    private static Track CreateTrack(string title)
    {
        return new Track(
            Guid.NewGuid(),
            new Uri($"file:///music/{title}.flac"),
            title,
            "Artist",
            "Album",
            TimeSpan.FromMinutes(3));
    }
}
