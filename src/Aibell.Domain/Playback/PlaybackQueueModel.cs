using Aibell.Domain.Media;

namespace Aibell.Domain.Playback;

public enum RepeatMode
{
    Off,
    All,
    One
}

public sealed class PlaybackQueueModel
{
    private readonly List<Track> items = [];
    private readonly List<Track> originalOrder = [];
    // For each entry of items, its position in originalOrder: the same track can appear twice, and shuffling reorders items.
    private readonly List<int> sources = [];
    private int currentIndex = -1;

    public IReadOnlyList<Track> Items => items;

    public Track? Current => currentIndex >= 0 ? items[currentIndex] : null;

    /// <summary>The position of the current track in the list as it was given (before any shuffling), or -1.</summary>
    public int CurrentSourceIndex => currentIndex >= 0 ? sources[currentIndex] : -1;

    /// <summary>Changes whenever the list is replaced, extended or cleared, so a caller can tell whether it is still the list it set.</summary>
    public long Version { get; private set; }

    public bool CanMovePrevious => currentIndex > 0;

    public bool CanMoveNext => currentIndex + 1 < items.Count;

    public bool ShuffleEnabled { get; private set; }

    public RepeatMode RepeatMode { get; private set; }

    public void Enqueue(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        originalOrder.Add(track);
        items.Add(track);
        sources.Add(originalOrder.Count - 1);
        Version++;
    }

    public bool TryMoveNext()
    {
        if (!CanMoveNext)
        {
            return false;
        }

        currentIndex++;
        return true;
    }

    public bool TryMovePrevious()
    {
        if (!CanMovePrevious)
        {
            return false;
        }

        currentIndex--;
        return true;
    }

    public bool TryAdvanceAfterEnd()
    {
        if (currentIndex < 0 || items.Count == 0)
        {
            return false;
        }

        if (RepeatMode == RepeatMode.One)
        {
            return true;
        }

        if (TryMoveNext())
        {
            return true;
        }

        if (RepeatMode == RepeatMode.All)
        {
            currentIndex = 0;
            return true;
        }

        return false;
    }

    public bool TrySelect(Guid trackId)
    {
        int index = items.FindIndex(track => track.Id == trackId);
        if (index < 0)
        {
            return false;
        }

        currentIndex = index;
        return true;
    }

    public void Replace(IEnumerable<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        originalOrder.Clear();
        originalOrder.AddRange(tracks);
        ResetItems();
        if (ShuffleEnabled)
        {
            ShuffleItems(Random.Shared, 0);
        }

        currentIndex = -1;
        Version++;
    }

    /// <summary>
    /// Replaces the list and makes the track at <paramref name="startIndex"/> current. With shuffle on, that track comes first and
    /// the others follow in random order, so every track still plays once.
    /// </summary>
    public void Replace(IEnumerable<Track> tracks, int startIndex, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        originalOrder.Clear();
        originalOrder.AddRange(tracks);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(startIndex, originalOrder.Count);
        ResetItems();
        currentIndex = startIndex;
        if (ShuffleEnabled)
        {
            MoveToFront(startIndex);
            ShuffleItems(random ?? Random.Shared, 1);
            currentIndex = 0;
        }

        Version++;
    }

    public void SetShuffle(bool enabled, Random? random = null)
    {
        if (ShuffleEnabled == enabled)
        {
            return;
        }

        int currentSource = CurrentSourceIndex;
        ResetItems();
        if (enabled)
        {
            ShuffleItems(random ?? Random.Shared, 0);
        }

        ShuffleEnabled = enabled;
        currentIndex = currentSource >= 0 ? sources.IndexOf(currentSource) : -1;
    }

    public RepeatMode CycleRepeatMode()
    {
        RepeatMode = RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off
        };
        return RepeatMode;
    }

    public void SetRepeatMode(RepeatMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        RepeatMode = mode;
    }

    private void ResetItems()
    {
        items.Clear();
        items.AddRange(originalOrder);
        sources.Clear();
        sources.AddRange(Enumerable.Range(0, originalOrder.Count));
    }

    private void MoveToFront(int index)
    {
        (items[0], items[index]) = (items[index], items[0]);
        (sources[0], sources[index]) = (sources[index], sources[0]);
    }

    // Shuffles items from position first onwards.
    private void ShuffleItems(Random random, int first)
    {
        for (int index = items.Count - 1; index > first; index--)
        {
            int target = first + random.Next(index - first + 1);
            (items[index], items[target]) = (items[target], items[index]);
            (sources[index], sources[target]) = (sources[target], sources[index]);
        }
    }

    public void Clear()
    {
        items.Clear();
        originalOrder.Clear();
        sources.Clear();
        currentIndex = -1;
        Version++;
        ShuffleEnabled = false;
        RepeatMode = RepeatMode.Off;
    }
}
