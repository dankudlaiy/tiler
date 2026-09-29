using Tiler.Core;

namespace Tiler.Core.Tests;

public class DropZonesTests
{
    static readonly IntRect Target = IntRect.FromSize(100, 100, 1000, 500);

    [Theory]
    [InlineData(150, 350, DropZone.Left)]
    [InlineData(1050, 350, DropZone.Right)]
    [InlineData(600, 120, DropZone.Top)]
    [InlineData(600, 580, DropZone.Bottom)]
    [InlineData(600, 350, DropZone.Center)]
    [InlineData(50, 350, DropZone.None)]
    public void Hit_picks_zone_by_position(int x, int y, DropZone expected)
    {
        Assert.Equal(expected, DropZones.Hit(Target, new IntPoint(x, y)));
    }

    [Fact]
    public void Hit_compares_edges_relative_to_size()
    {
        // 150 px from the left of a 1000 px window is 15% in; 100 px from the top of 500 px is 20% in.
        Assert.Equal(DropZone.Left, DropZones.Hit(Target, new IntPoint(250, 200)));
    }

    [Fact]
    public void SplitArea_leaves_gap_between_halves()
    {
        var (first, second) = LayoutMath.SplitArea(IntRect.FromSize(0, 0, 1008, 600), SplitKind.Columns, 0.5, 8);

        Assert.Equal(IntRect.FromSize(0, 0, 500, 600), first);
        Assert.Equal(IntRect.FromSize(508, 0, 500, 600), second);
    }
}
