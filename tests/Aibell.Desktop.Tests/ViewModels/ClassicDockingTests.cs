using Aibell.Desktop.Classic;
using Avalonia;
using NUnit.Framework;

namespace Aibell.Desktop.Tests.ViewModels;

[TestFixture]
public sealed class ClassicDockingTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);
    private static readonly PixelRect Main = new(100, 100, 275, 116);
    private static readonly PixelRect Equalizer = new(100, 216, 275, 116);
    private static readonly PixelRect Playlist = new(100, 332, 275, 232);

    [Test]
    public void WindowsEdgeToEdgeAreDocked()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ClassicDocking.Touch(Main, Equalizer), Is.True, "stacked");
            Assert.That(ClassicDocking.Touch(Main, new PixelRect(375, 150, 100, 50)), Is.True, "side by side");
            Assert.That(ClassicDocking.Touch(Main, new PixelRect(376, 150, 100, 50)), Is.True, "a pixel off still counts");
            Assert.That(ClassicDocking.Touch(Main, new PixelRect(380, 150, 100, 50)), Is.False, "a gap does not");
            Assert.That(ClassicDocking.Touch(Main, new PixelRect(375, 216, 100, 50)), Is.False, "corners touching are not docked");
        });
    }

    [Test]
    public void TheGroupFollowsChainsAndSkipsHiddenWindows()
    {
        PixelRect?[] rects = [Main, Equalizer, Playlist];
        Assert.That(ClassicDocking.Group(rects, 0), Is.EquivalentTo(new[] { 0, 1, 2 }));
        Assert.That(ClassicDocking.Group([Main, null, Playlist], 0), Is.EquivalentTo(new[] { 0 }), "with the equalizer hidden the chain breaks");
        Assert.That(ClassicDocking.Group([Main, Equalizer, new PixelRect(900, 900, 275, 116)], 0), Is.EquivalentTo(new[] { 0, 1 }));
    }

    [Test]
    public void MovingWindowsCatchOnNearbyEdges()
    {
        PixelRect playlist = new(600, 500, 275, 232);
        // Dragged to 6 pixels right of the main window's right edge and roughly level: it catches there.
        PixelPoint snapped = ClassicDocking.Snap([playlist], [Main], Screen, new PixelPoint(375 + 6 - 600, 104 - 500));
        Assert.That(playlist.Translate(new PixelVector(snapped.X, snapped.Y)).Position, Is.EqualTo(new PixelPoint(375, 100)));

        PixelPoint free = ClassicDocking.Snap([playlist], [Main], Screen, new PixelPoint(-50, -40));
        Assert.That(free, Is.EqualTo(new PixelPoint(-50, -40)), "far from everything nothing changes");

        PixelPoint edge = ClassicDocking.Snap([Main], [], Screen, new PixelPoint(-95, 1080 - 116 - 100 - 3));
        Assert.That(Main.Translate(new PixelVector(edge.X, edge.Y)), Is.EqualTo(new PixelRect(0, 1080 - 116, 275, 116)), "screen edges catch too");

        PixelPoint level = ClassicDocking.Snap([new PixelRect(700, 100, 275, 116)], [Main], Screen, new PixelPoint(0, 300));
        Assert.That(level, Is.EqualTo(new PixelPoint(0, 300)), "edges far below are not level, so they do not catch");
    }

    [Test]
    public void DockedWindowsFollowAResize()
    {
        PixelRect side = new(375, 216, 100, 116);
        PixelRect?[] rects = [Main, Equalizer, Playlist, side];
        // Rolling the main window up into a 14-pixel bar pulls everything under it up by 102 pixels.
        Dictionary<int, PixelPoint> moved = ClassicDocking.FollowResize(rects, 0, new PixelSize(275, 14));
        Assert.Multiple(() =>
        {
            Assert.That(moved[1], Is.EqualTo(new PixelPoint(100, 114)));
            Assert.That(moved[2], Is.EqualTo(new PixelPoint(100, 230)));
            Assert.That(moved[3], Is.EqualTo(new PixelPoint(375, 114)), "and what is docked to them, at the side, too");
        });
        Dictionary<int, PixelPoint> wider = ClassicDocking.FollowResize(rects, 1, new PixelSize(550, 232));
        Assert.That(wider[2], Is.EqualTo(new PixelPoint(100, 448)));
        Assert.That(wider[3], Is.EqualTo(new PixelPoint(650, 216)), "the window right of the equalizer moves with its right edge");
        Assert.That(ClassicDocking.FollowResize([null, Equalizer], 0, new PixelSize(1, 1)), Is.Empty);
    }

    [Test]
    public void TheDefaultLayoutStacksTheWindowsInTheMiddle()
    {
        PixelPoint[] positions = ClassicDocking.DefaultLayout(new PixelRect(0, 0, 1920, 1024), [new PixelSize(275, 116), new PixelSize(275, 116), new PixelSize(275, 232)]);
        Assert.That(positions, Is.EqualTo(new[] { new PixelPoint(822, 280), new PixelPoint(822, 396), new PixelPoint(822, 512) }));
        Assert.That(ClassicDocking.IsReachable(Main, [Screen]), Is.True);
        Assert.That(ClassicDocking.IsReachable(new PixelRect(3000, 100, 275, 116), [Screen]), Is.False);
    }
}
