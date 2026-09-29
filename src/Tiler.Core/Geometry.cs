namespace Tiler.Core;

public readonly record struct IntPoint(int X, int Y);

/// <summary>Screen rectangle in physical pixels; Right and Bottom are exclusive.</summary>
public readonly record struct IntRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static IntRect FromSize(int x, int y, int width, int height) => new(x, y, x + width, y + height);

    public bool Contains(IntPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public IntRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public override string ToString() => $"({Left},{Top} {Width}x{Height})";
}

public enum SplitKind
{
    /// <summary>Children sit side by side.</summary>
    Columns,
    /// <summary>Children are stacked top to bottom.</summary>
    Rows,
}

public static class LayoutMath
{
    /// <summary>Splits <paramref name="area"/> in two, leaving <paramref name="gap"/> pixels between the halves.</summary>
    public static (IntRect First, IntRect Second) SplitArea(IntRect area, SplitKind kind, double firstRatio, int gap)
    {
        if (kind == SplitKind.Columns)
        {
            int first = (int)Math.Round(Math.Max(0, area.Width - gap) * firstRatio);
            return (area with { Right = area.Left + first }, area with { Left = area.Left + first + gap });
        }
        else
        {
            int first = (int)Math.Round(Math.Max(0, area.Height - gap) * firstRatio);
            return (area with { Bottom = area.Top + first }, area with { Top = area.Top + first + gap });
        }
    }
}
