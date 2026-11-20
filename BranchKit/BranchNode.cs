namespace BranchKit;

/// <summary>
/// One node in a <see cref="BranchTree"/>. Knows nothing about undo: it is just a point in a branching
/// history with a label and a timestamp. Derive from it (or use <see cref="BranchNode{T}"/>) to attach data.
/// </summary>
public abstract class BranchNode
{
    private readonly List<BranchNode> _children = [];

    protected BranchNode(DateTime timestamp) => Timestamp = timestamp;

    /// <summary>The tree this node belongs to (null until added).</summary>
    public BranchTree? Tree { get; internal set; }

    /// <summary>Parent node; null for the root.</summary>
    public BranchNode? Parent { get; internal set; }

    /// <summary>Monotonic creation order across the whole tree.</summary>
    public int Sequence { get; internal set; }

    /// <summary>Distance from the original root. Only meaningful relative to other nodes.</summary>
    public int Depth { get; internal set; }

    /// <summary>When the node was created (settable, e.g. when a node is extended by a merge).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Branches from this node, oldest first.</summary>
    public IReadOnlyList<BranchNode> Children => _children;

    /// <summary>The child that "forward" movement prefers: the most recently visited branch.</summary>
    public BranchNode? ActiveChild { get; internal set; }

    public bool IsRoot => Parent is null;

    /// <summary>True once the node has been trimmed out of its tree.</summary>
    public bool IsDetached { get; internal set; }

    /// <summary>Text shown in graph views.</summary>
    public abstract string Label { get; }

    internal void AddChild(BranchNode child)
    {
        _children.Add(child);
        ActiveChild = child;
    }

    internal void RemoveChild(BranchNode child)
    {
        _children.Remove(child);
        if (ActiveChild == child)
            ActiveChild = _children.Count > 0 ? _children[^1] : null;
    }

    /// <summary>True if <paramref name="node"/> is this node or one of its ancestors.</summary>
    public bool IsDescendantOf(BranchNode node)
    {
        for (var n = this; n is not null; n = n.Parent)
            if (n == node) return true;
        return false;
    }

    public override string ToString() => $"#{Sequence} {Label}";
}

/// <summary>A ready-made node carrying a value of type <typeparamref name="T"/>.</summary>
public sealed class BranchNode<T>(T value, string label, DateTime? timestamp = null)
    : BranchNode(timestamp ?? DateTime.Now)
{
    public T Value { get; } = value;

    public override string Label { get; } = label;
}
