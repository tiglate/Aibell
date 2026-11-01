using Aibell.Domain.Media;

namespace Aibell.Domain.Playback;

public sealed record PlayQueueEntry(Guid Id, Track Track);

// Explicit requests take priority over the library's automatic playback order.
// Entry IDs distinguish repeated requests for the same track.
public sealed class PlayQueueModel
{
    private readonly List<PlayQueueEntry> items = [];

    public IReadOnlyList<PlayQueueEntry> Items => items;
    public PlayQueueEntry? Current { get; private set; }
    public bool HasPending => items.Any(entry => entry.Id != Current?.Id);

    public void Restore(IEnumerable<PlayQueueEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        PlayQueueEntry[] restored = entries.ToArray();
        Clear();
        items.AddRange(restored);
    }

    public void Enqueue(IEnumerable<Track> tracks, bool next = false)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        PlayQueueEntry[] entries = tracks.Select(track => new PlayQueueEntry(Guid.NewGuid(), track)).ToArray();
        int index = next ? Current is null ? 0 : items.FindIndex(entry => entry.Id == Current.Id) + 1 : items.Count;
        items.InsertRange(index, entries);
    }

    public void UpdateTrack(Track track)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index].Track.Id != track.Id) continue;
            items[index] = items[index] with { Track = track };
            if (Current?.Id == items[index].Id) Current = items[index];
        }
    }

    public bool TryAdvance()
    {
        CompleteCurrent();
        Current = items.FirstOrDefault();
        return Current is not null;
    }

    public bool TrySelect(Guid entryId)
    {
        PlayQueueEntry? entry = items.FirstOrDefault(item => item.Id == entryId);
        if (entry is null)
        {
            return false;
        }

        if (Current?.Id != entryId)
        {
            CompleteCurrent();
        }
        items.Remove(entry);
        items.Insert(0, entry);
        Current = entry;
        return true;
    }

    public void CompleteCurrent()
    {
        if (Current is not null)
        {
            items.RemoveAll(entry => entry.Id == Current.Id);
            Current = null;
        }
    }

    public void Remove(Guid entryId)
    {
        items.RemoveAll(entry => entry.Id == entryId);
        if (Current?.Id == entryId)
        {
            Current = null;
        }
    }

    public void Move(Guid entryId, Guid targetId, bool insertAfter)
    {
        if (entryId == targetId || entryId == Current?.Id)
        {
            return;
        }
        PlayQueueEntry? entry = items.FirstOrDefault(item => item.Id == entryId);
        int target = items.FindIndex(item => item.Id == targetId);
        if (entry is null || target < 0)
        {
            return;
        }
        items.Remove(entry);
        target = items.FindIndex(item => item.Id == targetId);
        int position = target + (insertAfter ? 1 : 0);
        items.Insert(Current is null ? position : Math.Max(1, position), entry);
    }

    public void Clear()
    {
        items.Clear();
        Current = null;
    }
}
