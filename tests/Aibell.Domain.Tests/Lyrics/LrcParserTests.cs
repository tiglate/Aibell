using NUnit.Framework;
using Aibell.Domain.Lyrics;

namespace Aibell.Domain.Tests.Lyrics;

[TestFixture]
public sealed class LrcParserTests
{
    private static readonly string[] ChorusVerseChorus = ["Chorus", "Verse", "Chorus"];
    private static readonly string[] SortedByOffset = ["B", "A"];
    private static readonly string[] PlainWithBlank = ["first", "", "second"];

    [Test]
    public void ParsesCentisecondAndWholeSecondStamps()
    {
        IReadOnlyList<LyricLine> lines = LrcParser.Parse("[00:12.34]Hello\n[01:05]World");
        Assert.That(lines, Has.Count.EqualTo(2));
        Assert.That(lines[0], Is.EqualTo(new LyricLine(TimeSpan.FromSeconds(12.34), "Hello")));
        Assert.That(lines[1].Start, Is.EqualTo(TimeSpan.FromSeconds(65)));
    }

    [Test]
    public void ExpandsSeveralStampsOnOneLineAndSortsByTime()
    {
        IReadOnlyList<LyricLine> lines = LrcParser.Parse("[00:30.00][00:10.00]Chorus\n[00:20.00]Verse");
        Assert.That(lines.Select(line => line.Text), Is.EqualTo(ChorusVerseChorus));
    }

    [Test]
    public void AppliesTheOffsetTagAndDropsOtherHeaderTags()
    {
        IReadOnlyList<LyricLine> lines = LrcParser.Parse("[ar:Someone]\n[ti:Song]\n[offset:500]\n[00:01.00]A\n[00:00.20]B");
        Assert.That(lines.Select(line => line.Text), Is.EqualTo(SortedByOffset));
        Assert.That(lines[1].Start, Is.EqualTo(TimeSpan.FromSeconds(0.5)));
        Assert.That(lines[0].Start, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void PlainTextComesBackAsUntimedLinesWithBlankEdgesTrimmed()
    {
        IReadOnlyList<LyricLine> lines = LrcParser.Parse("\nfirst\n\nsecond\r\n\n");
        Assert.That(lines.Select(line => line.Text), Is.EqualTo(PlainWithBlank));
        Assert.That(lines.All(line => line.Start is null), Is.True);
    }

    [Test]
    public void KeepsBracketedTextThatIsNotATag()
    {
        Assert.That(LrcParser.Parse("[Chorus]\nla la")[0], Is.EqualTo(new LyricLine(null, "[Chorus]")));
        IReadOnlyList<LyricLine> timed = LrcParser.Parse("[00:01.00][Verse] go");
        Assert.That(timed, Has.Count.EqualTo(1));
        Assert.That(timed[0].Text, Is.EqualTo("[Verse] go"));
    }

    [Test]
    public void StripsEnhancedWordStamps()
    {
        Assert.That(LrcParser.Parse("[00:01.00]<00:01.00>Hel<00:01.50>lo")[0].Text, Is.EqualTo("Hello"));
    }

    [Test]
    public void EmptyInputGivesNoLines()
    {
        Assert.That(LrcParser.Parse(null), Is.Empty);
        Assert.That(LrcParser.Parse("  \n "), Is.Empty);
    }

    [Test]
    public void FormatRoundTripsSyncedLines()
    {
        string lrc = "[00:12.34]Hello\n[10:05.00]World";
        Assert.That(LrcParser.Format(LrcParser.Parse(lrc)), Is.EqualTo(lrc));
    }

    [Test]
    public void TrackLyricsPrefersSyncedTextThenPlainThenNull()
    {
        TrackLyrics? synced = TrackLyrics.FromText("[00:01.00]a", "a", false, LyricsOrigin.Provider, "x");
        Assert.That(synced!.IsSynced, Is.True);
        TrackLyrics? plain = TrackLyrics.FromText(null, "a\nb", false, LyricsOrigin.Provider);
        Assert.That(plain!.IsSynced, Is.False);
        Assert.That(plain.ToText(), Is.EqualTo("a\nb"));
        Assert.That(TrackLyrics.FromText(null, " ", false, LyricsOrigin.Provider), Is.Null);
        Assert.That(TrackLyrics.FromText(null, null, true, LyricsOrigin.Provider)!.IsInstrumental, Is.True);
        Assert.That(TrackLyrics.FromText(null, "[00:05.00]stamped in plain", false, LyricsOrigin.Embedded)!.IsSynced, Is.True);
    }
}
