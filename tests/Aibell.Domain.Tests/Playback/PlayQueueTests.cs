using Aibell.Domain.Media;
using Aibell.Domain.Playback;
using NUnit.Framework;

namespace Aibell.Domain.Tests.Playback;

[TestFixture]
public sealed class PlayQueueTests
{
    [Test]
    public void Restore_ActiveAndRepeatedRequests_PreservesIdentityAndRestartsStopped()
    {
        PlayQueueModel queue = new();
        Track repeated = CreateTrack("Repeated");
        queue.Enqueue([repeated, repeated]);
        queue.TryAdvance();
        PlayQueueEntry[] saved = queue.Items.ToArray();

        queue.Restore(queue.Items);

        Assert.Multiple(() =>
        {
            Assert.That(queue.Items, Is.EqualTo(saved));
            Assert.That(queue.Current, Is.Null);
            Assert.That(queue.HasPending, Is.True);
        });
        queue.TryAdvance();
        Assert.That(queue.Current!.Id, Is.EqualTo(saved[0].Id));
        queue.TryAdvance();
        Assert.That(queue.Current!.Id, Is.EqualTo(saved[1].Id));
    }

    [Test]
    public void Enqueue_NextAndLastBatches_PreservesSelectionOrderAndCurrentEntry()
    {
        PlayQueueModel queue = new();
        Track first = CreateTrack("First");
        Track second = CreateTrack("Second");
        Track third = CreateTrack("Third");
        queue.Enqueue([first, third]);
        queue.TryAdvance();

        queue.Enqueue([second, first], next: true);
        queue.Enqueue([second]);

        Assert.Multiple(() =>
        {
            Assert.That(queue.Items.Select(entry => entry.Track), Is.EqualTo(new[] { first, second, first, third, second }));
            Assert.That(queue.Current!.Track, Is.EqualTo(first));
            Assert.That(queue.Items.Select(entry => entry.Id).Distinct().Count(), Is.EqualTo(5));
        });
    }

    [Test]
    public void Enqueue_NextWithoutCurrent_PrependsBatch()
    {
        PlayQueueModel queue = new();
        Track first = CreateTrack("First");
        Track second = CreateTrack("Second");
        queue.Enqueue([first]);
        queue.Enqueue([second, first], next: true);

        Assert.That(queue.Items.Select(entry => entry.Track), Is.EqualTo(new[] { second, first, first }));
    }

    [Test]
    public void Advance_ConsumesActiveEntryBeforeStartingNext()
    {
        PlayQueueModel queue = new();
        Track first = CreateTrack("First");
        Track second = CreateTrack("Second");
        queue.Enqueue([first, second]);

        Assert.That(queue.TryAdvance(), Is.True);
        Assert.That(queue.Current!.Track, Is.EqualTo(first));
        Assert.That(queue.TryAdvance(), Is.True);
        Assert.That(queue.Current!.Track, Is.EqualTo(second));
        Assert.That(queue.HasPending, Is.False);
        Assert.That(queue.TryAdvance(), Is.False);
        Assert.That(queue.Items, Is.Empty);
    }

    [Test]
    public void Remove_DuplicateTrack_RemovesOnlyChosenEntry()
    {
        PlayQueueModel queue = new();
        Track track = CreateTrack("Repeated");
        queue.Enqueue([track, track, track]);
        queue.TryAdvance();
        Guid active = queue.Current!.Id;
        Guid removed = queue.Items[1].Id;

        queue.Remove(removed);

        Assert.Multiple(() =>
        {
            Assert.That(queue.Items, Has.Count.EqualTo(2));
            Assert.That(queue.Current!.Id, Is.EqualTo(active));
            Assert.That(queue.Items.Any(entry => entry.Id == removed), Is.False);
        });
    }

    [Test]
    public void Remove_ActiveEntry_LeavesNextRequestAvailable()
    {
        PlayQueueModel queue = new();
        queue.Enqueue([CreateTrack("First"), CreateTrack("Second")]);
        queue.TryAdvance();
        queue.Remove(queue.Current!.Id);

        Assert.That(queue.Current, Is.Null);
        Assert.That(queue.TryAdvance(), Is.True);
        Assert.That(queue.Current!.Track.Title, Is.EqualTo("Second"));
    }

    [Test]
    public void Move_ActiveEntry_KeepsCurrentAtFront()
    {
        PlayQueueModel queue = new();
        queue.Enqueue([CreateTrack("First"), CreateTrack("Second"), CreateTrack("Third")]);
        queue.TryAdvance();
        Guid active = queue.Current!.Id;
        queue.Move(active, queue.Items[2].Id, insertAfter: true);

        string[] expected = ["First", "Second", "Third"];
        Assert.That(queue.Items.Select(entry => entry.Track.Title), Is.EqualTo(expected));
        Assert.That(queue.Current!.Id, Is.EqualTo(active));
        queue.TryAdvance();
        Assert.That(queue.Current!.Track.Title, Is.EqualTo("Second"));
    }

    [Test]
    public void Select_LaterEntry_PromotesCurrentAndKeepsQueueNextImmediatelyAfterIt()
    {
        PlayQueueModel queue = new();
        Track first = CreateTrack("First");
        Track second = CreateTrack("Second");
        Track third = CreateTrack("Third");
        queue.Enqueue([first, second, third]);
        queue.TrySelect(queue.Items[2].Id);
        queue.Enqueue([second], next: true);

        Assert.That(queue.Items.Select(entry => entry.Track), Is.EqualTo(new[] { third, second, first, second }));
        queue.TryAdvance();
        Assert.That(queue.Current!.Track, Is.EqualTo(second));
    }

    [Test]
    public void Select_UnknownEntry_DoesNotConsumeCurrent()
    {
        PlayQueueModel queue = new();
        queue.Enqueue([CreateTrack("First")]);
        queue.TryAdvance();
        PlayQueueEntry current = queue.Current!;

        Assert.That(queue.TrySelect(Guid.NewGuid()), Is.False);
        Assert.That(queue.Current, Is.SameAs(current));
        queue.Clear();
        Assert.That(queue.Current, Is.Null);
        Assert.That(queue.Items, Is.Empty);
    }

    private static Track CreateTrack(string title) => new(Guid.NewGuid(), new Uri($"file:///music/{title}.flac"),
        title, "Artist", "Album", TimeSpan.FromMinutes(3));
}
