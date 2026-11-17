using System.Globalization;
using Aibell.Application.Playback;
using Aibell.Application.Settings;
using Aibell.Domain.Media;
using Aibell.Domain.Playback;

namespace Aibell.Desktop.ViewModels;

public enum ClassicPlaylistSort
{
    Title,
    FileName,
    Path,
    Reverse,
    Randomize,
}

/// <summary>
/// The logic of Classic Player Mode, apart from drawing: a playlist that keeps its tracks, which entry is current, the selection,
/// and the transport rules of the classic players (Play restarts, Previous and Next walk the playlist). Playback itself goes
/// through the shared player, so switching modes never interrupts a track.
/// </summary>
public sealed class ClassicPlayerViewModel
{
    private readonly MainWindowViewModel player;
    private readonly List<PlayQueueEntry> entries = [];
    private readonly HashSet<Guid> selected = [];
    private readonly Func<Random> random;
    // The playback list version this playlist last handed to the player; while it matches, the player is playing from here.
    private long ownedVersion = -1;
    private int anchor = -1;
    private bool loaded;

    public ClassicPlayerViewModel(MainWindowViewModel player, Func<Random>? random = null)
    {
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.random = random ?? (() => Random.Shared);
        if (player.ArePreferencesLoaded) Load();
        else player.PreferencesLoaded += (_, _) => Load();
    }

    /// <summary>Raised when the playlist, its selection or its current entry changes.</summary>
    public event EventHandler? PlaylistChanged;

    public MainWindowViewModel Player => player;

    public IReadOnlyList<PlayQueueEntry> Entries => entries;

    /// <summary>The entry Play starts from and the playlist highlights; -1 when the playlist is empty.</summary>
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>The entry the keyboard moves from.</summary>
    public int FocusIndex { get; private set; } = -1;

    public ClassicPlayerPreferences Preferences => player.ClassicPlayer;

    /// <summary>Whether the player is playing from this playlist (rather than from the library or the play queue).</summary>
    public bool OwnsPlayback => ownedVersion == player.PlaybackListVersion && !player.IsPlayingFromPlayQueue;

    public bool IsSelected(int index) => index >= 0 && index < entries.Count && selected.Contains(entries[index].Id);

    public int SelectedCount => selected.Count;

    public TimeSpan TotalDuration => entries.Aggregate(TimeSpan.Zero, (total, entry) => total + entry.Track.Duration);

    public TimeSpan SelectedDuration => entries.Where(entry => selected.Contains(entry.Id)).Aggregate(TimeSpan.Zero, (total, entry) => total + entry.Track.Duration);

    // ---- Display ---------------------------------------------------------------------------------------------------

    /// <summary>The track the main window describes: the one playing, or the current playlist entry when nothing is.</summary>
    public Track? DisplayedTrack => player.PlaybackSnapshot.Track ?? (CurrentIndex >= 0 ? entries[CurrentIndex].Track : null);

    /// <summary>"3. Artist - Title (3:42)", the scrolling title of the classic players.</summary>
    public string MarqueeText
    {
        get
        {
            if (DisplayedTrack is not { } track) return "Aibell";
            int number = OwnsPlayback || player.PlaybackSnapshot.Track is null ? CurrentIndex + 1 : 0;
            string text = DescribeTrack(track);
            if (track.Duration > TimeSpan.Zero) text += $" ({FormatDuration(track.Duration)})";
            return number > 0 ? $"{number}. {text}" : text;
        }
    }

    /// <summary>The time shown by the big digits: elapsed, or remaining as a negative value; null while stopped.</summary>
    public TimeSpan? DisplayTime
    {
        get
        {
            PlaybackSnapshot snapshot = player.PlaybackSnapshot;
            if (snapshot.Track is null || snapshot.Status is PlaybackStatus.Stopped or PlaybackStatus.Ended) return null;
            if (!Preferences.ShowRemainingTime || snapshot.Track.Duration <= TimeSpan.Zero) return snapshot.Position;
            TimeSpan remaining = snapshot.Track.Duration - snapshot.Position;
            return remaining > TimeSpan.Zero ? -remaining : TimeSpan.Zero;
        }
    }

    public string BitRateText => DisplayedTrack is { BitRate: > 0 } track ? Math.Min(999, track.BitRate).ToString(CultureInfo.InvariantCulture) : string.Empty;

    public string SampleRateText => DisplayedTrack is { SampleRate: > 0 } track ? Math.Min(99, (int)Math.Round(track.SampleRate / 1000.0)).ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>1 for mono, 2 for stereo (or more), 0 when unknown or stopped.</summary>
    public int ChannelIndicator => player.PlaybackSnapshot.Status is PlaybackStatus.Playing or PlaybackStatus.Paused && DisplayedTrack is { Channels: > 0 } track
        ? Math.Min(track.Channels, 2) : 0;

    /// <summary>"0:42/17:05", the playlist's selected and total running time.</summary>
    public string RunningTimeText =>
        $"{FormatDuration(SelectedDuration)}/{FormatDuration(TotalDuration)}";

    public static string DescribeTrack(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        string title = string.IsNullOrWhiteSpace(track.Title) ? Path.GetFileNameWithoutExtension(track.Location.LocalPath) : track.Title;
        return string.IsNullOrWhiteSpace(track.Artist) ? title : $"{track.Artist} - {title}";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return duration.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes}:{duration.Seconds:00}");
    }

    /// <summary>Follows the player: when it moves on by itself (end of track, Next), the playlist's current entry follows.</summary>
    public void Synchronize()
    {
        if (!OwnsPlayback) return;
        int position = player.PlaybackListPosition;
        if (position >= 0 && position < entries.Count && position != CurrentIndex)
        {
            CurrentIndex = position;
            SaveSoon();
            OnChanged();
        }
    }

    // ---- Transport -------------------------------------------------------------------------------------------------

    /// <summary>Play: resumes when paused, otherwise starts the current entry from the beginning.</summary>
    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        PlaybackSnapshot snapshot = player.PlaybackSnapshot;
        if (snapshot.Status == PlaybackStatus.Paused)
        {
            await player.TogglePlaybackAsync(cancellationToken);
            return;
        }
        if (entries.Count == 0)
        {
            // Nothing to play from here: let the player play what it has (the play queue or the last track).
            if (snapshot.Track is not null && snapshot.Status != PlaybackStatus.Playing) await player.TogglePlaybackAsync(cancellationToken);
            return;
        }
        await PlayEntryAsync(Math.Max(0, CurrentIndex), cancellationToken);
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        if (player.PlaybackSnapshot.Status is PlaybackStatus.Playing or PlaybackStatus.Paused) await player.TogglePlaybackAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => player.StopPlaybackAsync(cancellationToken);

    public Task NextAsync(CancellationToken cancellationToken = default) => StepAsync(1, cancellationToken);

    public Task PreviousAsync(CancellationToken cancellationToken = default) => StepAsync(-1, cancellationToken);

    private async Task StepAsync(int direction, CancellationToken cancellationToken)
    {
        if (OwnsPlayback && player.PlaybackSnapshot.Status is PlaybackStatus.Playing or PlaybackStatus.Paused)
        {
            if (direction > 0 ? player.CanGoNext : player.CanGoPrevious)
            {
                await (direction > 0 ? player.MoveNextAsync(cancellationToken) : player.MovePreviousAsync(cancellationToken));
                Synchronize();
                return;
            }
            if (player.CurrentRepeatMode == RepeatMode.Off) return;
        }
        if (entries.Count == 0) return;
        int target = CurrentIndex < 0 ? 0 : CurrentIndex + direction;
        // Repeat wraps around the ends; without it the ends stop.
        if (target < 0 || target >= entries.Count)
        {
            if (player.CurrentRepeatMode == RepeatMode.Off) return;
            target = (target + entries.Count) % entries.Count;
        }
        if (player.PlaybackSnapshot.Status is PlaybackStatus.Playing or PlaybackStatus.Paused) await PlayEntryAsync(target, cancellationToken);
        else SetCurrent(target);
    }

    public async Task PlayEntryAsync(int index, CancellationToken cancellationToken = default)
    {
        if (index < 0 || index >= entries.Count) return;
        SetCurrent(index);
        await player.PlayListAsync(Tracks(), index, cancellationToken);
        ownedVersion = player.PlaybackListVersion;
        OnChanged();
    }

    public Task ToggleShuffleAsync(CancellationToken cancellationToken = default) => player.ToggleShuffleAsync(cancellationToken);

    /// <summary>The classic repeat button is on or off: on repeats the whole playlist.</summary>
    public Task ToggleRepeatAsync(CancellationToken cancellationToken = default) =>
        player.SetRepeatAsync(player.CurrentRepeatMode == RepeatMode.Off ? RepeatMode.All : RepeatMode.Off, cancellationToken);

    public Task ToggleRemainingTimeAsync(CancellationToken cancellationToken = default) =>
        player.UpdateClassicPlayerAsync(preferences => preferences with { ShowRemainingTime = !preferences.ShowRemainingTime }, cancellationToken);

    // ---- Editing ---------------------------------------------------------------------------------------------------

    /// <summary>Adds tracks at the end (or before <paramref name="insertAt"/>), returning the index of the first one added.</summary>
    public int Add(IEnumerable<Track> tracks, int? insertAt = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        PlayQueueEntry[] added = [.. tracks.Select(track => new PlayQueueEntry(Guid.NewGuid(), track))];
        if (added.Length == 0) return -1;
        int at = insertAt is int index ? Math.Clamp(index, 0, entries.Count) : entries.Count;
        Edit(() =>
        {
            entries.InsertRange(at, added);
            if (CurrentIndex >= at) CurrentIndex += added.Length;
            if (CurrentIndex < 0) CurrentIndex = 0;
        });
        return at;
    }

    /// <summary>Replaces the whole playlist, as loading a list does.</summary>
    public void Replace(IEnumerable<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        PlayQueueEntry[] replacement = [.. tracks.Select(track => new PlayQueueEntry(Guid.NewGuid(), track))];
        Edit(() =>
        {
            entries.Clear();
            entries.AddRange(replacement);
            selected.Clear();
            CurrentIndex = entries.Count > 0 ? 0 : -1;
        }, keepPlaying: false);
    }

    public void RemoveSelected() => RemoveWhere(entry => selected.Contains(entry.Id));

    public void Crop() => RemoveWhere(entry => !selected.Contains(entry.Id));

    public void RemoveAll() => RemoveWhere(_ => true);

    /// <summary>Removes the second and later copies of each file.</summary>
    public void RemoveDuplicates()
    {
        HashSet<Uri> seen = [];
        HashSet<Guid> duplicates = [.. entries.Where(entry => !seen.Add(entry.Track.Location)).Select(entry => entry.Id)];
        RemoveWhere(entry => duplicates.Contains(entry.Id));
    }

    private void RemoveWhere(Func<PlayQueueEntry, bool> remove)
    {
        Edit(() =>
        {
            Guid? current = CurrentIndex >= 0 ? entries[CurrentIndex].Id : null;
            int before = CurrentIndex;
            int removedBefore = entries.Take(Math.Max(0, before)).Count(remove);
            bool currentRemoved = current is not null && remove(entries[CurrentIndex]);
            entries.RemoveAll(entry => remove(entry));
            selected.IntersectWith(entries.Select(entry => entry.Id));
            if (entries.Count == 0) CurrentIndex = -1;
            else if (currentRemoved) CurrentIndex = Math.Min(Math.Max(0, before - removedBefore), entries.Count - 1);
            else if (current is Guid id) CurrentIndex = entries.FindIndex(entry => entry.Id == id);
            FocusIndex = Math.Min(FocusIndex, entries.Count - 1);
        });
    }

    public void Sort(ClassicPlaylistSort sort)
    {
        Edit(() =>
        {
            Guid? current = CurrentIndex >= 0 ? entries[CurrentIndex].Id : null;
            List<PlayQueueEntry> sorted = sort switch
            {
                ClassicPlaylistSort.Title => [.. entries.OrderBy(entry => DescribeTrack(entry.Track), StringComparer.CurrentCultureIgnoreCase)],
                ClassicPlaylistSort.FileName => [.. entries.OrderBy(entry => Path.GetFileName(entry.Track.Location.LocalPath), StringComparer.CurrentCultureIgnoreCase)],
                ClassicPlaylistSort.Path => [.. entries.OrderBy(entry => entry.Track.Location.LocalPath, StringComparer.CurrentCultureIgnoreCase)],
                ClassicPlaylistSort.Reverse => [.. Enumerable.Reverse(entries)],
                _ => [.. entries.OrderBy(_ => random().Next())],
            };
            entries.Clear();
            entries.AddRange(sorted);
            if (current is Guid id) CurrentIndex = entries.FindIndex(entry => entry.Id == id);
        });
    }

    /// <summary>Moves the selected entries up (negative) or down by <paramref name="offset"/> places, as a block.</summary>
    public void MoveSelected(int offset)
    {
        if (selected.Count == 0 || offset == 0) return;
        Edit(() =>
        {
            Guid? current = CurrentIndex >= 0 ? entries[CurrentIndex].Id : null;
            int first = entries.FindIndex(entry => selected.Contains(entry.Id));
            int last = entries.FindLastIndex(entry => selected.Contains(entry.Id));
            offset = Math.Clamp(offset, -first, entries.Count - 1 - last);
            if (offset == 0) return;
            PlayQueueEntry[] moving = [.. entries.Where(entry => selected.Contains(entry.Id))];
            PlayQueueEntry[] staying = [.. entries.Where(entry => !selected.Contains(entry.Id))];
            // The block keeps its shape: each moved entry lands offset places from where it was, the rest fill the gaps in order.
            PlayQueueEntry?[] result = new PlayQueueEntry?[entries.Count];
            foreach (PlayQueueEntry entry in moving) result[entries.IndexOf(entry) + offset] = entry;
            int next = 0;
            for (int index = 0; index < result.Length; index++) result[index] ??= staying[next++];
            entries.Clear();
            entries.AddRange(result!);
            if (current is Guid id) CurrentIndex = entries.FindIndex(entry => entry.Id == id);
            if (FocusIndex >= 0) FocusIndex = Math.Clamp(FocusIndex + offset, 0, entries.Count - 1);
        });
    }

    // ---- Selection -------------------------------------------------------------------------------------------------

    /// <summary>A click on row <paramref name="index"/>: alone, with Ctrl (toggle) or with Shift (range from the last click).</summary>
    public void Select(int index, bool toggle = false, bool range = false)
    {
        if (index < 0 || index >= entries.Count) return;
        if (range && anchor >= 0)
        {
            if (!toggle) selected.Clear();
            for (int row = Math.Min(anchor, index); row <= Math.Max(anchor, index); row++) selected.Add(entries[row].Id);
        }
        else if (toggle)
        {
            if (!selected.Add(entries[index].Id)) selected.Remove(entries[index].Id);
            anchor = index;
        }
        else
        {
            selected.Clear();
            selected.Add(entries[index].Id);
            anchor = index;
        }
        FocusIndex = index;
        OnChanged();
    }

    public void SelectAll()
    {
        selected.UnionWith(entries.Select(entry => entry.Id));
        OnChanged();
    }

    public void SelectNone()
    {
        selected.Clear();
        OnChanged();
    }

    public void InvertSelection()
    {
        HashSet<Guid> inverted = [.. entries.Select(entry => entry.Id).Where(id => !selected.Contains(id))];
        selected.Clear();
        selected.UnionWith(inverted);
        OnChanged();
    }

    public IReadOnlyList<Track> SelectedTracks() => [.. entries.Where(entry => selected.Contains(entry.Id)).Select(entry => entry.Track)];

    public IReadOnlyList<Track> Tracks() => [.. entries.Select(entry => entry.Track)];

    // ---- Internals -------------------------------------------------------------------------------------------------

    private void SetCurrent(int index)
    {
        if (CurrentIndex == index) return;
        CurrentIndex = index;
        SaveSoon();
        OnChanged();
    }

    // Applies an edit, keeps the player's list in step when it is playing from here, saves, and redraws.
    private void Edit(Action change, bool keepPlaying = true)
    {
        bool owned = OwnsPlayback;
        change();
        if (owned && keepPlaying)
        {
            player.UpdatePlaybackList(Tracks(), CurrentIndex);
            ownedVersion = player.PlaybackListVersion;
        }
        else if (owned)
        {
            ownedVersion = -1;
        }
        SaveSoon();
        OnChanged();
    }

    private void SaveSoon()
    {
        if (!loaded) return;
        PlayQueueEntry[] saved = [.. entries];
        int current = CurrentIndex;
        _ = player.UpdateClassicPlayerAsync(preferences => preferences with { Playlist = saved, PlaylistCurrent = current });
    }

    private void Load()
    {
        ClassicPlayerPreferences preferences = player.ClassicPlayer;
        entries.Clear();
        entries.AddRange(preferences.Playlist ?? []);
        selected.Clear();
        CurrentIndex = preferences.PlaylistCurrent >= 0 ? preferences.PlaylistCurrent : entries.Count > 0 ? 0 : -1;
        loaded = true;
        OnChanged();
    }

    private void OnChanged() => PlaylistChanged?.Invoke(this, EventArgs.Empty);
}
