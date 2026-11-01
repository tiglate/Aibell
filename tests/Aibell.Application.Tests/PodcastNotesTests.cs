using Aibell.Application.Podcasts;
using NUnit.Framework;

namespace Aibell.Application.Tests;

[TestFixture]
public sealed class PodcastNotesTests
{
    [Test]
    public void ToPlainText_HtmlShowNotes_KeepsParagraphsListsAndEntities()
    {
        string notes = "<p class=\"x\">First &amp; best</p><p>Topics:<br />one</p><ul><li>A</li><li>B&nbsp;C</li></ul>";
        Assert.That(PodcastNotes.ToPlainText(notes), Is.EqualTo("First & best\nTopics:\none\n• A\n• B C"));
    }

    [Test]
    public void ToPlainText_ScriptsStylesAndEncodedMarkup_NeverExecuteOrDisappearSilently()
    {
        string notes = "Hi<script>alert(1)</script><style>p{}</style> there &lt;b&gt;bold&lt;/b&gt;";
        Assert.That(PodcastNotes.ToPlainText(notes), Is.EqualTo("Hi there <b>bold</b>"));
    }

    [Test]
    public void ToPlainText_BlankLongAndPlainInputs_AreHandled()
    {
        Assert.That(PodcastNotes.ToPlainText(null), Is.Empty);
        Assert.That(PodcastNotes.ToPlainText("  \n "), Is.Empty);
        Assert.That(PodcastNotes.ToPlainText("plain   text\r\n\r\n\r\n\r\nmore"), Is.EqualTo("plain text\n\nmore"));
        string text = PodcastNotes.ToPlainText(new string('a', 20000));
        Assert.That(text.Length, Is.EqualTo(PodcastNotes.MaximumLength + 1));
        Assert.That(text, Does.EndWith("…"));
    }
}
