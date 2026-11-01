using Aibell.Domain.Audiobooks;
using NUnit.Framework;

namespace Aibell.Domain.Tests.Audiobooks;

[TestFixture]
public sealed class AudiobookRulesTests
{
    [TestCase("file:///tmp/book.mp3", true)]
    [TestCase("file:///tmp/Book.M4B", true)]
    [TestCase("file:///tmp/book.txt", false)]
    [TestCase("file:///tmp/.mp3", false)]
    [TestCase("file:///tmp/book", false)]
    [TestCase("file:///tmp/dir.mp3/book", false)]
    [TestCase("https://example.invalid/book.mp3", false)]
    [TestCase("file://server/share/book.mp3", false)]
    [TestCase("file:///tmp/book.mp3?x=1", false)]
    [TestCase("file:///tmp/book.mp3#x", false)]
    public void IsSupported_AcceptsOnlyLocalAudioFileUris(string location, bool supported) =>
        Assert.That(AudiobookLocation.IsSupported(new Uri(location)), Is.EqualTo(supported));

    [Test]
    public void IsSupported_NullRelativeOrOversized_ReturnsFalse()
    {
        Assert.That(AudiobookLocation.IsSupported(null), Is.False);
        Assert.That(AudiobookLocation.IsSupported(new Uri("book.mp3", UriKind.Relative)), Is.False);
        Assert.That(AudiobookLocation.IsSupported(new Uri("file:///" + new string('a', 8200) + ".mp3")), Is.False);
    }

    [Test]
    public void Key_KeepsPathCaseSoCaseSensitiveFilesStayDistinct() =>
        Assert.That(AudiobookLocation.Key(new Uri("file:///tmp/Book.mp3")), Is.Not.EqualTo(AudiobookLocation.Key(new Uri("file:///tmp/book.mp3"))));

    [TestCase(-5, 100, 0)]
    [TestCase(30, 100, 30)]
    [TestCase(100, 100, 100)]
    [TestCase(500, 100, 100)]
    [TestCase(30, 0, 0)]
    public void Clamp_BoundsToRealDuration(int saved, int duration, int expected) =>
        Assert.That(ResumeRules.Clamp(TimeSpan.FromSeconds(saved), TimeSpan.FromSeconds(duration)), Is.EqualTo(TimeSpan.FromSeconds(expected)));

    [TestCase(30, 100, 30)]
    [TestCase(99, 100, 99)]
    [TestCase(100, 100, 0)]
    [TestCase(900, 100, 0)]
    [TestCase(30, 0, 0)]
    public void ResumeStart_RestartsOnlyWhenSavedReachedTheEnd(int saved, int duration, int expected) =>
        Assert.That(ResumeRules.ResumeStart(TimeSpan.FromSeconds(saved), TimeSpan.FromSeconds(duration)), Is.EqualTo(TimeSpan.FromSeconds(expected)));

    [Test]
    public void BoundDuration_GuardsNegativeAndUnboundedMetadata()
    {
        Assert.That(ResumeRules.BoundDuration(TimeSpan.FromSeconds(-1)), Is.EqualTo(TimeSpan.Zero));
        Assert.That(ResumeRules.BoundDuration(TimeSpan.MaxValue), Is.EqualTo(ResumeRules.MaximumDuration));
    }

    [Test]
    public void Validate_RejectsPositionBeyondDurationAndBlankTitle()
    {
        Uri location = new("file:///tmp/book.mp3");
        AudiobookShelfEntry good = new(location, "Book", "Author", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3), DateTimeOffset.UnixEpoch);
        Assert.That(good.Validate, Throws.Nothing);
        Assert.That(() => (good with { SavedPosition = TimeSpan.FromMinutes(11) }).Validate(), Throws.ArgumentException);
        Assert.That(() => (good with { SavedPosition = TimeSpan.FromTicks(-1) }).Validate(), Throws.ArgumentException);
        Assert.That(() => (good with { Title = "  " }).Validate(), Throws.ArgumentException);
        Assert.That(() => (good with { Location = new Uri("https://x.invalid/a.mp3") }).Validate(), Throws.ArgumentException);
    }
}
