namespace Tiler.Core;

/// <summary>
/// Size of a window that can be smaller than its tile; it sits in the centre of the tile.
/// Each axis is null when the window fills its tile along it; the default fills the whole tile.
/// </summary>
public readonly record struct FreeSize(int? Width, int? Height)
{
    /// <summary>The window's rectangle in the tile: never larger than the tile, never below the minimum.</summary>
    public IntRect Place(IntRect tile, IntSize min)
    {
        var (left, right) = PlaceAxis(tile.Left, tile.Right, Width, min.Width);
        var (top, bottom) = PlaceAxis(tile.Top, tile.Bottom, Height, min.Height);
        return new IntRect(left, top, right, bottom);
    }

    /// <summary>The size of <paramref name="window"/> in <paramref name="tile"/>, with the axes it spans left to fill.</summary>
    public static FreeSize Of(IntRect window, IntRect tile) => new(
        window.Width < tile.Width ? window.Width : null,
        window.Height < tile.Height ? window.Height : null);

    static (int Start, int End) PlaceAxis(int start, int end, int? length, int min)
    {
        if (length is not { } l)
            return (start, end);
        int size = Math.Min(Math.Max(l, min), end - start);
        int offset = (end - start - size) / 2;
        return (start + offset, start + offset + size);
    }
}
