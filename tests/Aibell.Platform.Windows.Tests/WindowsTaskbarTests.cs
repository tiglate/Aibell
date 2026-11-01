using System.Runtime.InteropServices;
using Aibell.Application.Playback;
using NUnit.Framework;

namespace Aibell.Platform.Windows.Tests;

[TestFixture]
internal sealed partial class WindowsTaskbarTests
{
    private const uint WmCommand = 0x0111;
    private const int Clicked = 0x1800;

    private static nint Click(int notification, int id) => (nint)((notification << 16) | id);

    [TestCase(PlaybackStatus.Playing, TaskbarProgressMode.Normal)]
    [TestCase(PlaybackStatus.Paused, TaskbarProgressMode.Paused)]
    [TestCase(PlaybackStatus.Stopped, TaskbarProgressMode.None)]
    [TestCase(PlaybackStatus.Ended, TaskbarProgressMode.None)]
    [TestCase(PlaybackStatus.Faulted, TaskbarProgressMode.None)]
    public void ProgressFor_FollowsThePlaybackStatus(PlaybackStatus status, TaskbarProgressMode expected) =>
        Assert.That(TaskbarState.ProgressFor(status, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3), live: false).Mode, Is.EqualTo(expected));

    [Test]
    public void ProgressFor_ReportsMillisecondsAgainstTheDuration()
    {
        (TaskbarProgressMode mode, ulong completed, ulong total) =
            TaskbarState.ProgressFor(PlaybackStatus.Playing, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3), live: false);
        Assert.That((mode, completed, total), Is.EqualTo((TaskbarProgressMode.Normal, 30_000UL, 180_000UL)));
    }

    [TestCase(-5.0, 0UL)]
    [TestCase(500.0, 180_000UL)]
    public void ProgressFor_KeepsThePositionInsideTheTrack(double seconds, ulong expected) =>
        Assert.That(TaskbarState.ProgressFor(PlaybackStatus.Playing, TimeSpan.FromSeconds(seconds), TimeSpan.FromMinutes(3), live: false).Completed, Is.EqualTo(expected));

    [Test]
    public void ProgressFor_LiveStream_ShowsNoBar() =>
        Assert.That(TaskbarState.ProgressFor(PlaybackStatus.Playing, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(3), live: true).Mode, Is.EqualTo(TaskbarProgressMode.None));

    [Test]
    public void ProgressFor_UnknownDuration_ShowsNoBar() =>
        Assert.That(TaskbarState.ProgressFor(PlaybackStatus.Playing, TimeSpan.FromSeconds(5), TimeSpan.Zero, live: false), Is.EqualTo((TaskbarProgressMode.None, 0UL, 0UL)));

    [TestCase(TaskbarGlyph.Previous, 0.70)]
    [TestCase(TaskbarGlyph.Play, 0.40)]
    [TestCase(TaskbarGlyph.Pause, 0.30)]
    [TestCase(TaskbarGlyph.Next, 0.30)]
    public void Render_DrawsAnOpaqueGlyphOnATransparentBackground(TaskbarGlyph glyph, double insideX)
    {
        byte[] pixels = TaskbarGlyphs.Render(glyph);
        int size = TaskbarGlyphs.Size;
        int inside = (size / 2 * size + (int)(insideX * size)) * 4;
        Assert.That(pixels, Has.Length.EqualTo(size * size * 4));
        Assert.That(pixels[inside + 3], Is.EqualTo(255), "the glyph is solid");
        Assert.That(pixels[inside], Is.GreaterThan(200), "the fill is light");
        Assert.That(pixels[3], Is.EqualTo(0), "the corner is clear");
    }

    [Test]
    public void Render_PreviousMirrorsNext()
    {
        byte[] previous = TaskbarGlyphs.Render(TaskbarGlyph.Previous), next = TaskbarGlyphs.Render(TaskbarGlyph.Next);
        int size = TaskbarGlyphs.Size;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                Assert.That(previous[(y * size + x) * 4 + 3], Is.EqualTo(next[(y * size + size - 1 - x) * 4 + 3]).Within(1));
    }

    [Test]
    public void Render_PlayAndPauseDiffer() =>
        Assert.That(TaskbarGlyphs.Render(TaskbarGlyph.Play), Is.Not.EqualTo(TaskbarGlyphs.Render(TaskbarGlyph.Pause)));

    [Test]
    public void Constructor_NullWindow_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsTaskbar(0));

    [TestCase(0, TaskbarButton.Previous)]
    [TestCase(1, TaskbarButton.PlayPause)]
    [TestCase(2, TaskbarButton.Next)]
    [Platform("Win")]
    public void HandleMessage_ThumbnailClick_RaisesTheButton(int id, TaskbarButton expected)
    {
        using WindowsTaskbar taskbar = new(1);
        List<TaskbarButton> clicked = [];
        taskbar.ButtonClicked += clicked.Add;

        Assert.That(taskbar.HandleMessage(WmCommand, Click(Clicked, id)), Is.True);
        Assert.That(clicked, Is.EqualTo(new[] { expected }));
    }

    [Test]
    [Platform("Win")]
    public void HandleMessage_OtherCommandsAndUnknownIds_AreLeftToTheWindow()
    {
        using WindowsTaskbar taskbar = new(1);
        int raised = 0;
        taskbar.ButtonClicked += _ => raised++;

        Assert.That(taskbar.HandleMessage(WmCommand, Click(0, 1)), Is.False, "a menu command");
        Assert.That(taskbar.HandleMessage(0x0200, Click(Clicked, 1)), Is.False, "another message");
        Assert.That(taskbar.HandleMessage(WmCommand, Click(Clicked, 7)), Is.True, "an unknown button is consumed");
        Assert.That(raised, Is.Zero);
    }

    [Test]
    public void HandleMessage_AfterDispose_DoesNothing()
    {
        WindowsTaskbar taskbar = new(1);
        int raised = 0;
        taskbar.ButtonClicked += _ => raised++;
        taskbar.Dispose();
        taskbar.Dispose();

        Assert.That(taskbar.HandleMessage(WmCommand, Click(Clicked, 1)), Is.False);
        Assert.That(raised, Is.Zero);
    }

    [Test]
    public void Apply_BeforeTheShellCreatesTheButton_IsKeptWithoutCallingTheShell()
    {
        using WindowsTaskbar taskbar = new(1);
        Assert.DoesNotThrow(() => taskbar.Apply(TaskbarState.Idle with { Progress = TaskbarProgressMode.Normal, Completed = 1, Total = 2 }));
        Assert.That(taskbar.IsAttached, Is.False);
        Assert.Throws<ArgumentNullException>(() => taskbar.Apply(null!));
    }

    [Test]
    [Platform("Win")]
    [Apartment(ApartmentState.STA)]
    public void RealShell_AddsButtonsAndAcceptsProgressUpdates()
    {
        nint window = CreateWindowEx(0, "STATIC", "Aibell taskbar test", 0, 0, 0, 100, 100, 0, 0, 0, 0);
        Assert.That(window, Is.Not.Zero, "a hidden top-level window");
        try
        {
            using WindowsTaskbar taskbar = new(window);
            taskbar.Apply(new(TaskbarProgressMode.Normal, 30_000, 180_000, true, true, true, true, "Previous", "Pause", "Next"));
            Assert.That(taskbar.HandleMessage(WindowsTaskbar.ButtonCreatedMessage, 0), Is.False, "the announcement is not consumed");
            if (!taskbar.IsAttached) Assert.Inconclusive("This session's shell did not accept buttons for a window that was never shown.");

            Assert.DoesNotThrow(() =>
            {
                taskbar.Apply(new(TaskbarProgressMode.Paused, 31_000, 180_000, true, true, false, false, "Previous", "Play", "Next"));
                taskbar.Apply(TaskbarState.Idle);
                taskbar.Apply(new(TaskbarProgressMode.None, 0, 0, false, false, false, false, new string('x', 400), "", "😀"));
            });
        }
        finally { DestroyWindow(window); }
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
}
