using Tiler.Core;

namespace Tiler.Core.Tests;

public class WorkspaceTests
{
    const nint A = 1, B = 2, C = 3;

    // 1008 = 500 + 8 + 500, 608 = 300 + 8 + 300: halves come out even.
    static Workspace Empty() => new(IntRect.FromSize(0, 0, 1008, 608), 8);

    static readonly IntRect LeftHalf = IntRect.FromSize(0, 0, 500, 608);
    static readonly IntRect RightHalf = IntRect.FromSize(508, 0, 500, 608);

    [Fact]
    public void First_window_fills_the_workspace()
    {
        var ws = Empty();
        ws.Add(A);

        Assert.Equal(ws.Bounds, ws.Arrange()[A]);
    }

    [Fact]
    public void Without_a_point_the_largest_tile_splits_along_its_longer_side()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B);

        var tiles = ws.Arrange();
        Assert.Equal(LeftHalf, tiles[A]);
        Assert.Equal(RightHalf, tiles[B]);
    }

    [Fact]
    public void Drop_point_picks_the_side()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B, new IntPoint(20, 300));

        var tiles = ws.Arrange();
        Assert.Equal(LeftHalf, tiles[B]);
        Assert.Equal(RightHalf, tiles[A]);
    }

    [Fact]
    public void Drop_in_the_middle_of_a_new_tile_splits_toward_the_point()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B, new IntPoint(480, 300));

        Assert.Equal(LeftHalf, ws.Arrange()[B]);
    }

    [Fact]
    public void Third_window_splits_only_the_tile_under_it()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B);
        ws.Add(C, new IntPoint(750, 590));

        var tiles = ws.Arrange();
        Assert.Equal(LeftHalf, tiles[A]);
        Assert.Equal(IntRect.FromSize(508, 0, 500, 300), tiles[B]);
        Assert.Equal(IntRect.FromSize(508, 308, 500, 300), tiles[C]);
    }

    [Fact]
    public void Point_in_a_gap_uses_the_nearest_tile()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B);
        ws.Add(C, new IntPoint(504, 20)); // in the gap, 4 px from B and 5 px from A

        var tiles = ws.Arrange();
        Assert.Equal(LeftHalf, tiles[A]);
        Assert.Equal(IntRect.FromSize(508, 0, 246, 608), tiles[C]);
    }

    [Fact]
    public void Moving_to_the_centre_of_another_tile_swaps()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B);

        Assert.True(ws.MoveTo(A, new IntPoint(758, 304)));

        var tiles = ws.Arrange();
        Assert.Equal(RightHalf, tiles[A]);
        Assert.Equal(LeftHalf, tiles[B]);
    }

    [Fact]
    public void Moving_to_an_edge_of_another_tile_restructures()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B);

        Assert.True(ws.MoveTo(A, new IntPoint(758, 600)));

        var tiles = ws.Arrange();
        Assert.Equal(IntRect.FromSize(0, 0, 1008, 300), tiles[B]);
        Assert.Equal(IntRect.FromSize(0, 308, 1008, 300), tiles[A]);
    }

    [Fact]
    public void Dropping_on_own_tile_changes_nothing()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B);

        Assert.False(ws.MoveTo(A, new IntPoint(100, 100)));
        Assert.Equal(LeftHalf, ws.Arrange()[A]);
    }

    [Fact]
    public void Removing_closes_the_gap()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B);
        ws.Add(C, new IntPoint(750, 590));

        ws.Remove(B);

        var tiles = ws.Arrange();
        Assert.Equal(LeftHalf, tiles[A]);
        Assert.Equal(RightHalf, tiles[C]);
        Assert.False(ws.Contains(B));
    }

    [Fact]
    public void Removing_the_last_window_empties_the_workspace()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Remove(A);

        Assert.Equal(0, ws.Count);
        ws.Add(B);
        Assert.Equal(ws.Bounds, ws.Arrange()[B]);
    }

    [Fact]
    public void Resizing_moves_the_shared_border()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B);

        Assert.True(ws.Resize(A, IntRect.FromSize(0, 0, 600, 608)));

        var tiles = ws.Arrange();
        Assert.Equal(IntRect.FromSize(0, 0, 600, 608), tiles[A]);
        Assert.Equal(IntRect.FromSize(608, 0, 400, 608), tiles[B]);
    }

    [Fact]
    public void Resizing_a_nested_tile_moves_the_right_splits()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B);
        ws.Add(C, new IntPoint(750, 590));

        // B's left edge belongs to the root split, its bottom edge to the split with C.
        Assert.True(ws.Resize(B, new IntRect(408, 0, 1008, 400)));

        var tiles = ws.Arrange();
        Assert.Equal(IntRect.FromSize(0, 0, 400, 608), tiles[A]);
        Assert.Equal(new IntRect(408, 0, 1008, 400), tiles[B]);
        Assert.Equal(new IntRect(408, 408, 1008, 608), tiles[C]);
    }

    [Fact]
    public void Resizing_is_clamped()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B);

        ws.Resize(A, IntRect.FromSize(0, 0, 5, 608));

        Assert.Equal(100, ws.Arrange()[A].Width);
    }

    [Fact]
    public void Clone_is_independent()
    {
        var ws = Empty();
        ws.Add(A);
        ws.Add(B);

        var preview = ws.Clone();
        preview.MoveTo(A, new IntPoint(758, 304));
        preview.Add(C);

        Assert.Equal(LeftHalf, ws.Arrange()[A]);
        Assert.Equal(2, ws.Count);
        Assert.Equal(3, preview.Count);
    }
}
