using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Aibell.Desktop.Services;
using NUnit.Framework;

namespace Aibell.Desktop.UiTests;

[TestFixture]
public sealed class ApplicationErrorReporterTests
{
    private string directory = null!;

    [SetUp] public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "aibell-errors-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Test]
    public void Report_WritesTheLogAndAnnouncesAShortSummary()
    {
        ApplicationErrorReporter reporter = new(directory);
        List<string> messages = [];
        reporter.Reported += messages.Add;

        reporter.Report(new InvalidOperationException("first line\nsecond line"), "ui");

        Assert.Multiple(() =>
        {
            Assert.That(messages.Single(), Does.StartWith("Something went wrong (InvalidOperationException: first line second line)"));
            Assert.That(messages.Single(), Does.Contain(reporter.LogPath));
            string log = File.ReadAllText(reporter.LogPath);
            Assert.That(log, Does.Contain("[ui]").And.Contain("InvalidOperationException").And.Contain("second line"));
        });
    }

    [Test]
    public void Report_LongMessagesAreTruncatedAndTheLogRotates()
    {
        ApplicationErrorReporter reporter = new(directory);
        string announced = "";
        reporter.Reported += message => announced = message;
        reporter.Report(new InvalidOperationException(new string('x', 500)), "task");
        Assert.That(announced.Length, Is.LessThan(500));

        Directory.CreateDirectory(directory);
        File.WriteAllBytes(reporter.LogPath, new byte[ApplicationErrorReporter.MaximumLogBytes + 1]);
        reporter.Report(new InvalidOperationException("after rotation"), "task");

        Assert.That(File.Exists(Path.Combine(directory, "errors.old.log")), Is.True);
        Assert.That(new FileInfo(reporter.LogPath).Length, Is.LessThan(ApplicationErrorReporter.MaximumLogBytes));
        Assert.That(File.ReadAllText(reporter.LogPath), Does.Contain("after rotation"));
    }

    [Test]
    public void Report_UnwritableLogFolderNeverThrowsAndStillAnnounces()
    {
        string blocker = Path.Combine(Path.GetTempPath(), "aibell-errors-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where the folder should be");
        try
        {
            ApplicationErrorReporter reporter = new(Path.Combine(blocker, "logs"));
            string announced = "";
            reporter.Reported += message => announced = message;
            Assert.That(() => reporter.Report(new InvalidOperationException("boom"), "ui"), Throws.Nothing);
            Assert.That(announced, Does.Contain("boom"));
        }
        finally { File.Delete(blocker); }
    }

    [AvaloniaTest]
    public void Attach_AnExceptionOnTheUiThreadIsLoggedAndDoesNotEscape()
    {
        ApplicationErrorReporter reporter = new(directory);
        string announced = "";
        reporter.Reported += message => announced = message;
        reporter.Attach();
        reporter.Attach(); // idempotent

        Dispatcher.UIThread.Post(() => throw new InvalidOperationException("handler failed"));
        Dispatcher.UIThread.RunJobs();

        Assert.That(announced, Does.Contain("handler failed"));
        Assert.That(File.ReadAllText(reporter.LogPath), Does.Contain("[ui]"));
    }

    [Test]
    public void Report_WithContext_LogsItAndHandsTheUiTheTechnicalDetails()
    {
        ApplicationErrorReporter reporter = new(directory);
        ErrorReport? seen = null;
        reporter.ReportedDetailed += report => seen = report;

        ErrorReport returned = reporter.Report(new InvalidOperationException("boom"), "playback", "Track: /music/a.ogg\nPlayer: built-in", "This track could not be played.");

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.SameAs(returned));
            Assert.That(returned.Message, Is.EqualTo("This track could not be played."));
            Assert.That(returned.Details, Does.StartWith("Track: /music/a.ogg").And.Contain("InvalidOperationException: boom").And.Contain(reporter.LogPath));
            Assert.That(returned.ShowDialog, Is.True);
            Assert.That(File.ReadAllText(reporter.LogPath), Does.Contain("[playback]").And.Contain("Track: /music/a.ogg").And.Contain("boom"));
        });
    }

    [Test]
    public void Report_BackgroundTaskFailures_AreLoggedWithoutAWindow()
    {
        ApplicationErrorReporter reporter = new(directory);

        ErrorReport returned = reporter.Report(new InvalidOperationException("unobserved"), "task");

        Assert.That(returned.ShowDialog, Is.False);
        Assert.That(File.ReadAllText(reporter.LogPath), Does.Contain("unobserved"));
    }

    [Test]
    public void Report_WithoutAnException_LogsTitleMessageAndDetails()
    {
        ApplicationErrorReporter reporter = new(directory);

        ErrorReport returned = reporter.Report("Aibell closed unexpectedly", "The last session did not end normally.", "Last activity: playing", "previous-session");

        Assert.Multiple(() =>
        {
            Assert.That(returned.Title, Is.EqualTo("Aibell closed unexpectedly"));
            Assert.That(returned.Details, Does.StartWith("Last activity: playing").And.Contain(reporter.LogPath));
            Assert.That(File.ReadAllText(reporter.LogPath), Does.Contain("[previous-session]").And.Contain("did not end normally"));
        });
    }

    [Test]
    public void Report_UnwritableLogFolder_StillAnnouncesTheProblem()
    {
        string blocker = Path.Combine(Path.GetTempPath(), "aibell-errors-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a folder");
        try
        {
            ApplicationErrorReporter reporter = new(Path.Combine(blocker, "logs"));
            ErrorReport? seen = null;
            reporter.ReportedDetailed += report => seen = report;

            Assert.That(() => reporter.Report(new InvalidOperationException("x"), "ui"), Throws.Nothing);
            Assert.That(seen, Is.Not.Null);
        }
        finally { File.Delete(blocker); }
    }
}
