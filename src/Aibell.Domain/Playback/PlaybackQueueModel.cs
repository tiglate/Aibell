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
    private int currentIndex = -1;

    public IReadOnlyList<Track> Items => items;

    public Track? Current => currentIndex >= 0 ? items[currentIndex] : null;

    public bool CanMovePrevious => currentIndex > 0;

    public bool CanMoveNext => currentIndex + 1 < items.Count;

    public bool ShuffleEnabled { get; private set; }

    public RepeatMode RepeatMode { get; private set; }

    public void Enqueue(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        originalOrder.Add(track);
        items.Add(track);
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
        items.Clear();
        items.AddRange(originalOrder);
        if (ShuffleEnabled)
        {
            ShuffleItems(Random.Shared);
        }

        currentIndex = -1;
    }

    public void SetShuffle(bool enabled, Random? random = null)
    {
        if (ShuffleEnabled == enabled)
        {
            return;
        }

        Guid? currentId = Current?.Id;
        items.Clear();
        items.AddRange(originalOrder);
        if (enabled)
        {
            ShuffleItems(random ?? Random.Shared);
        }

        ShuffleEnabled = enabled;
        currentIndex = currentId is Guid id
            ? items.FindIndex(track => track.Id == id)
            : -1;
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

    private void ShuffleItems(Random random)
    {
        for (int index = items.Count - 1; index > 0; index--)
        {
            int target = random.Next(index + 1);
            (items[index], items[target]) = (items[target], items[index]);
        }
    }

    public void Clear()
    {
        items.Clear();
        originalOrder.Clear();
        currentIndex = -1;
        ShuffleEnabled = false;
        RepeatMode = RepeatMode.Off;
    }
}
