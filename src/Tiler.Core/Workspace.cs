namespace Tiler.Core;

/// <summary>
/// The tiling of one monitor: a binary tree of splits filling <see cref="Bounds"/>.
/// Every window gets a tile; where a window goes depends on the point it was dropped at.
/// </summary>
public sealed class Workspace(IntRect bounds, int gap)
{
    public const double DefaultRatio = 0.5;

    /// <summary>Resizing never squeezes a tile below this share of its split.</summary>
    public const double MinRatio = 0.1;

    LayoutNode? root;

    public IntRect Bounds { get; set; } = bounds;
    public int Gap { get; set; } = gap;

    /// <summary>Smallest size each window accepts; tiles don't shrink below it while there is room.</summary>
    public Func<nint, IntSize>? MinSize { get; set; }

    public IEnumerable<nint> Windows => Leaves().Select(leaf => leaf.Window);
    public int Count => Leaves().Count();

    public bool Contains(nint window) => Find(window) != null;

    public Workspace Clone() => new(Bounds, Gap) { root = root?.Clone(), MinSize = MinSize };

    /// <summary>
    /// Adds a window. With a point, the tile nearest to it is split on the side the point is on;
    /// without one, the largest tile is split along its longer side.
    /// When that split can't fit the minimum sizes, the perpendicular split of the same tile is tried,
    /// and unless <paramref name="keepTarget"/> is set, the other tiles too, largest first.
    /// </summary>
    /// <param name="keepTarget">The user dropped the window on this tile: never move it to another one.</param>
    public void Add(nint window, IntPoint? point = null, bool keepTarget = false)
    {
        if (Contains(window))
            return;

        if (root == null)
        {
            root = new WindowNode(window);
            return;
        }

        var tiles = ArrangeLeaves();
        var candidates = new List<(nint Target, DropZone Zone)>();
        if (point is { } p)
        {
            var target = Nearest(tiles, p);
            AddWithRotation(candidates, target.Window, EdgeToward(tiles[target], p));
        }
        if (point == null || !keepTarget)
        {
            foreach (var (tile, rect) in tiles.OrderByDescending(t => (long)t.Value.Width * t.Value.Height))
                AddWithRotation(candidates, tile.Window, rect.Width >= rect.Height ? DropZone.Right : DropZone.Bottom);
        }

        var (targetWindow, zone) = FirstThatFits(candidates, (ws, t, z) => ws.InsertBeside(ws.Find(t)!, new WindowNode(window), z));
        InsertBeside(Find(targetWindow)!, new WindowNode(window), zone);
    }

    /// <summary>
    /// Moves a tiled window to where it was dropped: over the centre of another tile the two swap,
    /// near its edge the window moves to that side of it. Dropping on its own tile changes nothing.
    /// </summary>
    public bool MoveTo(nint window, IntPoint point)
    {
        var leaf = Find(window);
        if (leaf == null)
            return false;

        var tiles = ArrangeLeaves();
        var target = Nearest(tiles, point);
        if (target == leaf)
            return false;

        var zone = DropZones.Hit(tiles[target], Clamp(point, tiles[target]));
        if (zone == DropZone.Center)
        {
            (leaf.Window, target.Window) = (target.Window, leaf.Window);
            return true;
        }

        var candidates = new List<(nint Target, DropZone Zone)>();
        AddWithRotation(candidates, target.Window, zone);
        (_, zone) = FirstThatFits(candidates, (ws, t, z) =>
        {
            var moved = ws.Find(window)!;
            ws.RemoveLeaf(moved);
            ws.InsertBeside(ws.Find(t)!, moved, z);
        });

        RemoveLeaf(leaf);
        InsertBeside(target, leaf, zone);
        return true;
    }

    /// <summary>True when every window gets at least its minimum size.</summary>
    public bool Fits()
    {
        if (root == null)
            return true;
        var min = MinSizeOf(root, []);
        return min.Width <= Bounds.Width && min.Height <= Bounds.Height;
    }

    /// <summary>The split as asked, then the perpendicular one on the same side (left becomes top, right becomes bottom).</summary>
    static void AddWithRotation(List<(nint, DropZone)> candidates, nint target, DropZone zone)
    {
        candidates.Add((target, zone));
        candidates.Add((target, zone switch
        {
            DropZone.Left => DropZone.Top,
            DropZone.Top => DropZone.Left,
            DropZone.Right => DropZone.Bottom,
            _ => DropZone.Right,
        }));
    }

    /// <summary>Tries each placement on a copy and returns the first that fits; the first one if none does.</summary>
    (nint Target, DropZone Zone) FirstThatFits(List<(nint Target, DropZone Zone)> candidates, Action<Workspace, nint, DropZone> place)
    {
        if (MinSize == null)
            return candidates[0];
        foreach (var candidate in candidates)
        {
            var trial = Clone();
            place(trial, candidate.Target, candidate.Zone);
            if (trial.Fits())
                return candidate;
        }
        return candidates[0];
    }

    public bool Remove(nint window)
    {
        if (Find(window) is not { } leaf)
            return false;
        RemoveLeaf(leaf);
        return true;
    }

    /// <summary>
    /// The user dragged the window's border to <paramref name="newRect"/>: moves the split lines
    /// that run along its edges, so that the neighbours follow.
    /// </summary>
    public bool Resize(nint window, IntRect newRect)
    {
        var leaf = Find(window);
        if (leaf == null)
            return false;

        var rects = ArrangeNodes();
        var old = rects[leaf];
        bool changed = false;
        LayoutNode child = leaf;
        for (var split = leaf.Parent; split != null; child = split, split = split.Parent)
        {
            var area = rects[split];
            var childRect = rects[child];
            bool first = child == split.First;
            if (split.Kind == SplitKind.Columns)
            {
                if (first && old.Right == childRect.Right && newRect.Right != old.Right)
                    changed |= SetRatio(split, newRect.Right - area.Left, area.Width);
                else if (!first && old.Left == childRect.Left && newRect.Left != old.Left)
                    changed |= SetRatio(split, newRect.Left - Gap - area.Left, area.Width);
            }
            else
            {
                if (first && old.Bottom == childRect.Bottom && newRect.Bottom != old.Bottom)
                    changed |= SetRatio(split, newRect.Bottom - area.Top, area.Height);
                else if (!first && old.Top == childRect.Top && newRect.Top != old.Top)
                    changed |= SetRatio(split, newRect.Top - Gap - area.Top, area.Height);
            }
        }
        return changed;
    }

    /// <summary>Visual rectangle of every window.</summary>
    public Dictionary<nint, IntRect> Arrange() =>
        ArrangeLeaves().ToDictionary(tile => tile.Key.Window, tile => tile.Value);

    bool SetRatio(SplitNode split, int firstSize, int totalSize)
    {
        int available = totalSize - Gap;
        if (available <= 0)
            return false;
        split.Ratio = Math.Clamp(firstSize / (double)available, MinRatio, 1 - MinRatio);
        return true;
    }

    void InsertBeside(WindowNode target, WindowNode leaf, DropZone zone)
    {
        var (kind, leafFirst) = DropZones.Describe(zone);
        var parent = target.Parent;
        var split = leafFirst
            ? new SplitNode(kind, DefaultRatio, leaf, target)
            : new SplitNode(kind, DefaultRatio, target, leaf);

        if (parent == null)
        {
            root = split;
            split.Parent = null;
        }
        else
        {
            parent.ReplaceChild(target, split);
        }
    }

    /// <summary>The sibling of the removed leaf takes over its parent's space.</summary>
    void RemoveLeaf(WindowNode leaf)
    {
        var parent = leaf.Parent;
        leaf.Parent = null;
        if (parent == null)
        {
            root = null;
            return;
        }

        var sibling = parent.First == leaf ? parent.Second : parent.First;
        if (parent.Parent is { } grandparent)
        {
            grandparent.ReplaceChild(parent, sibling);
        }
        else
        {
            root = sibling;
            sibling.Parent = null;
        }
    }

    /// <summary>The side of <paramref name="tile"/> facing the point; the centre falls back to the longer axis.</summary>
    static DropZone EdgeToward(IntRect tile, IntPoint point)
    {
        var p = Clamp(point, tile);
        var zone = DropZones.Hit(tile, p);
        if (zone != DropZone.Center)
            return zone;
        if (tile.Width >= tile.Height)
            return p.X < tile.Left + tile.Width / 2 ? DropZone.Left : DropZone.Right;
        return p.Y < tile.Top + tile.Height / 2 ? DropZone.Top : DropZone.Bottom;
    }

    /// <summary>The tile under the point, or the closest one when the point is in a gap or off the tiles.</summary>
    static WindowNode Nearest(Dictionary<WindowNode, IntRect> tiles, IntPoint p) =>
        tiles.MinBy(tile => DistanceSquared(tile.Value, p)).Key;

    static long DistanceSquared(IntRect r, IntPoint p)
    {
        long dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - (r.Right - 1));
        long dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - (r.Bottom - 1));
        return dx * dx + dy * dy;
    }

    static IntPoint Clamp(IntPoint p, IntRect r) =>
        new(Math.Clamp(p.X, r.Left, Math.Max(r.Left, r.Right - 1)), Math.Clamp(p.Y, r.Top, Math.Max(r.Top, r.Bottom - 1)));

    Dictionary<WindowNode, IntRect> ArrangeLeaves()
    {
        var result = new Dictionary<WindowNode, IntRect>();
        foreach (var (node, rect) in ArrangeNodes())
        {
            if (node is WindowNode leaf)
                result[leaf] = rect;
        }
        return result;
    }

    Dictionary<LayoutNode, IntRect> ArrangeNodes()
    {
        var result = new Dictionary<LayoutNode, IntRect>();
        if (root != null)
        {
            var mins = new Dictionary<LayoutNode, IntSize>();
            MinSizeOf(root, mins);
            Arrange(root, Bounds, result, mins);
        }
        return result;
    }

    void Arrange(LayoutNode node, IntRect area, Dictionary<LayoutNode, IntRect> result, Dictionary<LayoutNode, IntSize> mins)
    {
        result[node] = area;
        if (node is SplitNode split)
        {
            var (firstMin, secondMin) = (mins[split.First], mins[split.Second]);
            var (first, second) = split.Kind == SplitKind.Columns
                ? LayoutMath.SplitArea(area, split.Kind, split.Ratio, Gap, firstMin.Width, secondMin.Width)
                : LayoutMath.SplitArea(area, split.Kind, split.Ratio, Gap, firstMin.Height, secondMin.Height);
            Arrange(split.First, first, result, mins);
            Arrange(split.Second, second, result, mins);
        }
    }

    /// <summary>
    /// A split needs both minimums plus the gap along its axis and the larger of them across it,
    /// so an outer split leaves enough room for everything nested inside.
    /// </summary>
    IntSize MinSizeOf(LayoutNode node, Dictionary<LayoutNode, IntSize> mins)
    {
        IntSize size;
        if (node is SplitNode split)
        {
            var a = MinSizeOf(split.First, mins);
            var b = MinSizeOf(split.Second, mins);
            size = split.Kind == SplitKind.Columns
                ? new IntSize(a.Width + Gap + b.Width, Math.Max(a.Height, b.Height))
                : new IntSize(Math.Max(a.Width, b.Width), a.Height + Gap + b.Height);
        }
        else
        {
            size = MinSize?.Invoke(((WindowNode)node).Window) ?? default;
        }
        mins[node] = size;
        return size;
    }

    WindowNode? Find(nint window) => Leaves().FirstOrDefault(leaf => leaf.Window == window);

    IEnumerable<WindowNode> Leaves() => root == null ? [] : Leaves(root);

    static IEnumerable<WindowNode> Leaves(LayoutNode node)
    {
        if (node is WindowNode leaf)
        {
            yield return leaf;
        }
        else if (node is SplitNode split)
        {
            foreach (var l in Leaves(split.First))
                yield return l;
            foreach (var l in Leaves(split.Second))
                yield return l;
        }
    }
}
