using Aibell.Domain.Audiobooks;
using NUnit.Framework;

namespace Aibell.Domain.Tests.Audiobooks;

[TestFixture]
public sealed class AudiobookChapterRulesTests
{
    private static AudiobookChapter At(double seconds, string title) => new(TimeSpan.FromSeconds(seconds), title);

    [Test]
    public void Normalize_UnorderedMarkers_SortsStablyByStart()
    {
        AudiobookChapterCatalog catalog = AudiobookChapterRules.Normalize(new(TimeSpan.FromMinutes(10), [At(60, "B"), At(0, "A"), At(120, "C")]));

        Assert.That(catalog.Chapters.Select(c => c.Title), Is.EqualTo(["A", "B", "C"]));
        Assert.That(catalog.Skipped, Is.Zero);
    }

    [Test]
    public void Normalize_NegativeAtOrBeyondDurationAndDuplicateStarts_AreDroppedAndCounted()
    {
        AudiobookChapterCatalog catalog = AudiobookChapterRules.Normalize(new(TimeSpan.FromSeconds(100),
            [At(-1, "Negative"), At(0, "First"), At(0, "Duplicate"), At(50, "Middle"), At(100, "AtEnd"), At(500, "Beyond")]));

        Assert.That(catalog.Chapters.Select(c => c.Title), Is.EqualTo(["First", "Middle"]), "file order breaks equal starts");
        Assert.That(catalog.Skipped, Is.EqualTo(4));
    }

    [Test]
    public void Normalize_UnknownDuration_YieldsNoChapters()
    {
        AudiobookChapterCatalog catalog = AudiobookChapterRules.Normalize(new(TimeSpan.Zero, [At(0, "A")]));

        Assert.That(catalog.Chapters, Is.Empty);
        Assert.That(catalog.Skipped, Is.EqualTo(1));
    }

    [Test]
    public void Normalize_BlankAndLongTitles_AreNamedByFinalPositionAndCut()
    {
        AudiobookChapterCatalog catalog = AudiobookChapterRules.Normalize(new(TimeSpan.FromMinutes(5),
            [At(10, "  "), At(0, new string('x', 500)), At(20, "  Padded  ")]));

        Assert.That(catalog.Chapters[0].Title, Has.Length.EqualTo(AudiobookChapterRules.MaximumTitleLength));
        Assert.That(catalog.Chapters[1].Title, Is.EqualTo("Chapter 2"));
        Assert.That(catalog.Chapters[2].Title, Is.EqualTo("Padded"));
    }

    [Test]
    public void Normalize_MoreThanTheCap_KeepsTheEarliestAndReportsTruncation()
    {
        AudiobookChapter[] many = [.. Enumerable.Range(0, AudiobookChapterRules.MaximumChapters + 5).Select(i => At(i, "c" + i))];

        AudiobookChapterCatalog catalog = AudiobookChapterRules.Normalize(new(TimeSpan.FromHours(10), many));

        Assert.That(catalog.Chapters, Has.Count.EqualTo(AudiobookChapterRules.MaximumChapters));
        Assert.That(catalog.Truncated, Is.True);
        Assert.That(catalog.Skipped, Is.EqualTo(5));
        Assert.That(catalog.Chapters[^1].Title, Is.EqualTo("c" + (AudiobookChapterRules.MaximumChapters - 1)));
    }
    [Test]
    public void Normalize_DescendingOversizedInput_RetainsTheEarliestMarkersAndFirstDuplicate()
    {
        List<AudiobookChapter> markers = [.. Enumerable.Range(0, 10_000).Reverse().Select(index => At(index, "first " + index))];
        markers.Add(At(0, "duplicate opening"));
        markers.Add(null!);

        AudiobookChapterCatalog catalog = AudiobookChapterRules.Normalize(new(TimeSpan.FromHours(10), markers));

        Assert.That(catalog.Chapters, Has.Count.EqualTo(AudiobookChapterRules.MaximumChapters));
        Assert.That(catalog.Chapters[0].Title, Is.EqualTo("first 0"));
        Assert.That(catalog.Chapters[^1].Start, Is.EqualTo(TimeSpan.FromSeconds(999)));
        Assert.That(catalog.Skipped, Is.EqualTo(9002));
        Assert.That(catalog.Truncated, Is.True);
    }

}
