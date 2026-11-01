namespace Aibell.Domain.Audiobooks;

/// <summary>One chapter marker: where it starts in the file and its (possibly untrusted) title.</summary>
public sealed record AudiobookChapter(TimeSpan Start, string Title);

/// <summary>Raw, unvalidated chapter facts read from one file together with that file's real duration.</summary>
public sealed record AudiobookChapterListing(TimeSpan Duration, IReadOnlyList<AudiobookChapter> Chapters);

/// <summary>Validated chapters. <see cref="Skipped"/> counts markers that were dropped; <see cref="Truncated"/> is true when the cap was hit.</summary>
public sealed record AudiobookChapterCatalog(TimeSpan Duration, IReadOnlyList<AudiobookChapter> Chapters, int Skipped, bool Truncated)
{
    public static AudiobookChapterCatalog Empty { get; } = new(TimeSpan.Zero, [], 0, false);
}

/// <summary>
/// Chapter semantics. Markers are ordered by start (stable, so file order breaks ties). A marker with a negative start,
/// or one at or beyond the real duration, is dropped because seeking there would silently play the opening or nothing.
/// A later marker with the same start as an earlier one is dropped. A blank title becomes "Chapter N" (N is the final
/// 1-based position) and long titles are cut. At most <see cref="MaximumChapters"/> chapters are kept, earliest first.
/// An unknown duration (zero) yields no chapters, since no start can be proved in range.
/// </summary>
public static class AudiobookChapterRules
{
    public const int MaximumChapters = 1000;
    public const int MaximumTitleLength = 200;

    public static AudiobookChapterCatalog Normalize(AudiobookChapterListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        TimeSpan duration = ResumeRules.BoundDuration(listing.Duration);
        // Retain only the earliest candidates while scanning. Sorting the entire untrusted listing would
        // allocate and retain every marker before applying the public output cap.
        Dictionary<TimeSpan, AudiobookChapter> retained = [];
        SortedSet<TimeSpan> starts = [];
        int skipped = 0;
        bool truncated = false;
        foreach (AudiobookChapter chapter in listing.Chapters ?? [])
        {
            if (chapter is null || chapter.Start < TimeSpan.Zero || chapter.Start >= duration || retained.ContainsKey(chapter.Start))
            {
                skipped++;
                continue;
            }
            if (retained.Count == MaximumChapters)
            {
                truncated = true;
                skipped++;
                TimeSpan latest = starts.Max;
                if (chapter.Start >= latest) continue;
                starts.Remove(latest);
                retained.Remove(latest);
            }
            starts.Add(chapter.Start);
            retained.Add(chapter.Start, chapter);
        }
        List<AudiobookChapter> valid = [.. starts.Select(start => retained[start])];
        AudiobookChapter[] result = new AudiobookChapter[valid.Count];
        for (int index = 0; index < valid.Count; index++) result[index] = valid[index] with { Title = CleanTitle(valid[index].Title, index + 1) };
        return new(duration, result, skipped, truncated);
    }

    private static string CleanTitle(string? title, int number)
    {
        string trimmed = (title ?? "").Trim();
        if (trimmed.Length == 0) return "Chapter " + number;
        return trimmed.Length <= MaximumTitleLength ? trimmed : trimmed[..MaximumTitleLength];
    }
}
