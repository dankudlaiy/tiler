namespace Tiler.Core;

public enum DropZone
{
    None,
    Left,
    Right,
    Top,
    Bottom,
    /// <summary>Swap the dragged window with the target.</summary>
    Center,
}

public static class DropZones
{
    /// <summary>Half the size of the central swap area, as a fraction of the target's size.</summary>
    public const double CenterHalfSize = 0.18;

    /// <summary>
    /// Picks the zone under the cursor. The middle of the target swaps; elsewhere the nearest
    /// edge wins, measured relative to the target's size so that wide and tall windows behave alike.
    /// </summary>
    public static DropZone Hit(IntRect target, IntPoint cursor)
    {
        if (target.IsEmpty || !target.Contains(cursor))
            return DropZone.None;

        double nx = (cursor.X - target.Left) / (double)target.Width - 0.5;
        double ny = (cursor.Y - target.Top) / (double)target.Height - 0.5;

        if (Math.Abs(nx) < CenterHalfSize && Math.Abs(ny) < CenterHalfSize)
            return DropZone.Center;
        if (Math.Abs(nx) >= Math.Abs(ny))
            return nx < 0 ? DropZone.Left : DropZone.Right;
        return ny < 0 ? DropZone.Top : DropZone.Bottom;
    }

    internal static (SplitKind Kind, bool DroppedFirst) Describe(DropZone zone) => zone switch
    {
        DropZone.Left => (SplitKind.Columns, true),
        DropZone.Right => (SplitKind.Columns, false),
        DropZone.Top => (SplitKind.Rows, true),
        DropZone.Bottom => (SplitKind.Rows, false),
        _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, "Only edge zones split a window."),
    };
}
