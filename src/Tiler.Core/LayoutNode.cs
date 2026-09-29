namespace Tiler.Core;

public abstract class LayoutNode
{
    public SplitNode? Parent { get; internal set; }

    internal abstract LayoutNode Clone();
}

public sealed class WindowNode(nint window) : LayoutNode
{
    public nint Window { get; internal set; } = window;

    internal override LayoutNode Clone() => new WindowNode(Window);
}

public sealed class SplitNode : LayoutNode
{
    public SplitNode(SplitKind kind, double ratio, LayoutNode first, LayoutNode second)
    {
        Kind = kind;
        Ratio = ratio;
        First = first;
        Second = second;
        first.Parent = this;
        second.Parent = this;
    }

    public SplitKind Kind { get; }

    /// <summary>Share of the area that goes to <see cref="First"/>.</summary>
    public double Ratio { get; internal set; }

    public LayoutNode First { get; private set; }
    public LayoutNode Second { get; private set; }

    internal override LayoutNode Clone() => new SplitNode(Kind, Ratio, First.Clone(), Second.Clone());

    internal void ReplaceChild(LayoutNode child, LayoutNode replacement)
    {
        if (First == child)
            First = replacement;
        else if (Second == child)
            Second = replacement;
        else
            throw new InvalidOperationException("Not a child of this split.");
        replacement.Parent = this;
    }
}
