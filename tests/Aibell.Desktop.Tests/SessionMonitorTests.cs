using Aibell.Desktop.Services;
using NUnit.Framework;

namespace Aibell.Desktop.Tests;

[TestFixture]
public sealed class SessionMonitorTests
{
    private string directory = null!;

    [SetUp] public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "aibell-session-" + Guid.NewGuid().ToString("N"));

    [TearDown] public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

    [Test]
    public void Begin_FirstRun_ReportsNothingAndLeavesAMarker()
    {
        SessionMonitor monitor = new(directory);

        Assert.That(monitor.Begin(), Is.Null);

        Assert.That(File.Exists(monitor.MarkerPath), Is.True);
        Assert.That(monitor.PreviousCrash, Is.Null);
    }

    [Test]
    public void End_RemovesTheMarkerSoTheNextStartIsClean()
    {
        SessionMonitor first = new(directory);
        first.Begin();
        first.SetActivity(new SessionActivity("playing", "/music/a.mp3", "Miniaudio"));
        first.End();

        Assert.That(File.Exists(first.MarkerPath), Is.False);
        Assert.That(new SessionMonitor(directory).Begin(), Is.Null);
    }

    [Test]
    public void Begin_AfterAnUncleanEnd_ReportsTheLastActivity()
    {
        DateTimeOffset now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        // A process id that cannot be running stands in for the crashed session.
        SessionMonitor crashed = new(directory, new FixedTime(now), processId: int.MaxValue);
        crashed.Begin();
        crashed.SetActivity(new SessionActivity("playing", "/music/Ghost - Jigolo Har Megiddo.ogg", "Miniaudio"));

        PreviousSessionCrash? found = new SessionMonitor(directory).Begin();

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Started, Is.EqualTo(now));
        Assert.That(found.Activity, Is.EqualTo(new SessionActivity("playing", "/music/Ghost - Jigolo Har Megiddo.ogg", "Miniaudio")));
        Assert.That(found.ActivityAt, Is.EqualTo(now));
    }

    [Test]
    public void SetActivity_Cleared_IsNotReportedAsTheCause()
    {
        SessionMonitor crashed = new(directory, processId: int.MaxValue);
        crashed.Begin();
        crashed.SetActivity(new SessionActivity("playing", "/music/a.mp3", "Vlc"));
        crashed.SetActivity(null);

        PreviousSessionCrash? found = new SessionMonitor(directory).Begin();

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Activity, Is.Null);
    }

    [Test]
    public void Begin_MarkerOfAnotherRunningAibell_IsNotACrash()
    {
        // This test process is alive and has the same process name, like a second Aibell sharing the profile folder.
        SessionMonitor other = new(directory, processId: Environment.ProcessId);
        other.Begin();

        Assert.That(new SessionMonitor(directory, processId: Environment.ProcessId + 1).Begin(), Is.Null);
    }

    [Test]
    public void Begin_UnreadableMarker_StillCountsAsAnUncleanEnd()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "session.json"), "{ not json");

        Assert.That(new SessionMonitor(directory).Begin(), Is.Not.Null);
    }

    [Test]
    public void SetActivity_UnwritableFolder_DoesNotThrow()
    {
        string blocker = Path.Combine(Path.GetTempPath(), "aibell-session-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where the folder should be");
        try
        {
            SessionMonitor monitor = new(Path.Combine(blocker, "logs"));

            Assert.That(() => { monitor.Begin(); monitor.SetActivity(new SessionActivity("playing", "x", "y")); monitor.End(); }, Throws.Nothing);
        }
        finally { File.Delete(blocker); }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
