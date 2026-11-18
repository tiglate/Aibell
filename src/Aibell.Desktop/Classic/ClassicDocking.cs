using Avalonia;

namespace Aibell.Desktop.Classic;

/// <summary>
/// The geometry of Classic Player Mode's separate windows, in screen pixels: which windows are docked (edge to edge), how a
/// moving window snaps to the others and to the screen's edges, and how docked windows follow when one changes size. Hidden
/// windows are passed as null and take no part.
/// </summary>
internal static class ClassicDocking
{
    /// <summary>How close, in screen pixels, an edge must come to another before it snaps to it.</summary>
    public const int SnapDistance = 10;

    private const int Tolerance = 1;

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> share part of an edge (one sits right against the other).</summary>
    public static bool Touch(PixelRect a, PixelRect b)
    {
        bool overlapX = a.X < b.Right && b.X < a.Right;
        bool overlapY = a.Y < b.Bottom && b.Y < a.Bottom;
        bool sideBySide = (Math.Abs(a.Right - b.X) <= Tolerance || Math.Abs(b.Right - a.X) <= Tolerance) && overlapY;
        bool stacked = (Math.Abs(a.Bottom - b.Y) <= Tolerance || Math.Abs(b.Bottom - a.Y) <= Tolerance) && overlapX;
        return sideBySide || stacked;
    }

    /// <summary>The windows docked to <paramref name="start"/>, directly or through others, including itself.</summary>
    public static HashSet<int> Group(IReadOnlyList<PixelRect?> rects, int start)
    {
        HashSet<int> group = [start];
        Queue<int> pending = new([start]);
        while (pending.Count > 0)
        {
            int current = pending.Dequeue();
            for (int other = 0; other < rects.Count; other++)
            {
                if (group.Contains(other) || rects[other] is not { } rect || rects[current] is not { } from || !Touch(from, rect)) continue;
                group.Add(other);
                pending.Enqueue(other);
            }
        }
        return group;
    }

    /// <summary>
    /// The move to make instead of <paramref name="delta"/> so that the moving windows' edges catch on the other windows' edges
    /// and on the screen's edges when they come within <see cref="SnapDistance"/>.
    /// </summary>
    public static PixelPoint Snap(IReadOnlyList<PixelRect> moving, IReadOnlyList<PixelRect> others, PixelRect screen, PixelPoint delta)
    {
        int? bestX = null, bestY = null;
        void Consider(ref int? best, int difference)
        {
            if (Math.Abs(difference) <= SnapDistance && (best is null || Math.Abs(difference) < Math.Abs(best.Value))) best = difference;
        }
        foreach (PixelRect original in moving)
        {
            PixelRect m = original.Translate(new PixelVector(delta.X, delta.Y));
            foreach (PixelRect o in others)
            {
                // Edges only catch when the windows are level with each other (allowing for the snap distance at the corners).
                bool levelY = m.Y < o.Bottom + SnapDistance && o.Y < m.Bottom + SnapDistance;
                bool levelX = m.X < o.Right + SnapDistance && o.X < m.Right + SnapDistance;
                if (levelY)
                {
                    Consider(ref bestX, o.X - m.Right);
                    Consider(ref bestX, o.Right - m.X);
                    Consider(ref bestX, o.X - m.X);
                    Consider(ref bestX, o.Right - m.Right);
                }
                if (levelX)
                {
                    Consider(ref bestY, o.Y - m.Bottom);
                    Consider(ref bestY, o.Bottom - m.Y);
                    Consider(ref bestY, o.Y - m.Y);
                    Consider(ref bestY, o.Bottom - m.Bottom);
                }
            }
            Consider(ref bestX, screen.X - m.X);
            Consider(ref bestX, screen.Right - m.Right);
            Consider(ref bestY, screen.Y - m.Y);
            Consider(ref bestY, screen.Bottom - m.Bottom);
        }
        return new PixelPoint(delta.X + (bestX ?? 0), delta.Y + (bestY ?? 0));
    }

    /// <summary>
    /// Where the windows docked below (or to the right of) <paramref name="resized"/> go when it takes <paramref name="size"/>:
    /// they move with its bottom (or right) edge, together with everything docked to them.
    /// </summary>
    public static Dictionary<int, PixelPoint> FollowResize(IReadOnlyList<PixelRect?> rects, int resized, PixelSize size)
    {
        Dictionary<int, PixelPoint> moved = [];
        if (rects[resized] is not { } old) return moved;
        // The clusters hanging off an edge are found without the resized window, so they do not lead back through it.
        PixelRect?[] others = [.. rects];
        others[resized] = null;
        void Shift(Func<PixelRect, bool> attached, PixelVector shift)
        {
            if (shift == default) return;
            HashSet<int> cluster = [];
            for (int index = 0; index < others.Length; index++)
                if (others[index] is { } rect && attached(rect)) cluster.UnionWith(Group(others, index));
            foreach (int index in cluster)
                moved[index] = (moved.TryGetValue(index, out PixelPoint already) ? already : others[index]!.Value.Position) + shift;
        }
        Shift(rect => Math.Abs(rect.Y - old.Bottom) <= Tolerance && rect.X < old.Right && old.X < rect.Right, new PixelVector(0, size.Height - old.Height));
        Shift(rect => Math.Abs(rect.X - old.Right) <= Tolerance && rect.Y < old.Bottom && old.Y < rect.Bottom, new PixelVector(size.Width - old.Width, 0));
        return moved;
    }

    /// <summary>The starting arrangement: the main window centred in the work area, the others stacked under it.</summary>
    public static PixelPoint[] DefaultLayout(PixelRect workArea, IReadOnlyList<PixelSize> sizes)
    {
        int total = sizes.Sum(size => size.Height);
        int width = sizes.Count == 0 ? 0 : sizes.Max(size => size.Width);
        int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        int y = workArea.Y + Math.Max(0, (workArea.Height - total) / 2);
        PixelPoint[] positions = new PixelPoint[sizes.Count];
        for (int index = 0; index < sizes.Count; index++)
        {
            positions[index] = new PixelPoint(x, y);
            y += sizes[index].Height;
        }
        return positions;
    }

    /// <summary>Whether enough of <paramref name="rect"/> lies on one of the screens to grab it (a saved spot on a monitor since unplugged fails).</summary>
    public static bool IsReachable(PixelRect rect, IEnumerable<PixelRect> screens) =>
        screens.Any(screen => screen.Intersect(rect) is { Width: >= 24, Height: >= 8 });
}
