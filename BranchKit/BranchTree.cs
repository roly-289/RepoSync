namespace BranchKit;

/// <summary>
/// A tree of <see cref="BranchNode"/>s with a "current" position, git-style: adding a node while
/// the current position is not a leaf starts a new branch, and nothing is ever discarded.
/// <para>
/// This class is pure structure. It does not apply, revert or store anything about what the nodes mean;
/// an undo manager, a snapshot browser, a conversation tree etc. layer their own behaviour on top.
/// </para>
/// </summary>
public sealed class BranchTree
{
    private readonly List<BranchNode> _nodes = [];
    private int _nextSequence;

    public BranchTree(BranchNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = Current = Attach(root);
    }

    /// <summary>The oldest retained node.</summary>
    public BranchNode Root { get; private set; }

    /// <summary>The node the tree is positioned at.</summary>
    public BranchNode Current { get; private set; }

    /// <summary>All nodes in chronological (creation) order. Index 0 is <see cref="Root"/>.</summary>
    public IReadOnlyList<BranchNode> Nodes => _nodes;

    public int Count => _nodes.Count;

    /// <summary>Raised after any change to the structure or the current position.</summary>
    public event EventHandler? Changed;

    public bool Contains(BranchNode node) => node.Tree == this && !node.IsDetached;

    /// <summary>
    /// Adds <paramref name="node"/> as a new child of <paramref name="parent"/> (default: <see cref="Current"/>)
    /// and moves to it.
    /// </summary>
    public T Add<T>(T node, BranchNode? parent = null) where T : BranchNode
    {
        ArgumentNullException.ThrowIfNull(node);
        parent ??= Current;
        if (!Contains(parent)) throw new ArgumentException("The parent is not part of this tree.", nameof(parent));
        if (node.Tree is not null) throw new ArgumentException("The node already belongs to a tree.", nameof(node));

        Attach(node);
        node.Parent = parent;
        node.Depth = parent.Depth + 1;
        parent.AddChild(node);
        Current = node;
        Raise();
        return node;
    }

    /// <summary>
    /// Moves one hop to a node adjacent to <see cref="Current"/> (its parent or one of its children),
    /// remembering the branch taken so that <see cref="BranchNode.ActiveChild"/> follows it.
    /// </summary>
    public void Step(BranchNode to)
    {
        var from = Current;
        if (to.Parent == from) from.ActiveChild = to;
        else if (from.Parent == to) to.ActiveChild = from;
        else throw new ArgumentException("Step only moves to the parent or a child of the current node.", nameof(to));
        Current = to;
        Raise();
    }

    /// <summary>
    /// The hops from <see cref="Current"/> to <paramref name="target"/>: first backwards (leaving
    /// <c>Node</c>, up to the common ancestor), then forwards (entering <c>Node</c>, down to the target).
    /// </summary>
    public IReadOnlyList<(BranchNode Node, bool Forward)> PathTo(BranchNode target)
    {
        if (!Contains(target)) throw new ArgumentException("The node is not part of this tree.", nameof(target));
        var ancestor = CommonAncestor(Current, target);

        var steps = new List<(BranchNode, bool)>();
        for (var n = Current; n != ancestor; n = n.Parent!) steps.Add((n, false));

        var down = new List<BranchNode>();
        for (var n = target; n != ancestor; n = n.Parent!) down.Add(n);
        down.Reverse();
        foreach (var n in down) steps.Add((n, true));
        return steps;
    }

    /// <summary>Moves to any node by walking <see cref="PathTo"/>.</summary>
    public void MoveTo(BranchNode target)
    {
        foreach (var (node, forward) in PathTo(target))
            Step(forward ? node : node.Parent!);
    }

    public static BranchNode CommonAncestor(BranchNode a, BranchNode b)
    {
        while (a.Depth > b.Depth) a = a.Parent!;
        while (b.Depth > a.Depth) b = b.Parent!;
        while (a != b) { a = a.Parent!; b = b.Parent!; }
        return a;
    }

    /// <summary>
    /// Drops nodes until at most <paramref name="maxNodes"/> remain (minimum 2): the oldest dead-branch
    /// leaves first, then the oldest states on the current line. <paramref name="protect"/> is never
    /// dropped as a leaf. Returns the nodes removed.
    /// </summary>
    public IReadOnlyList<BranchNode> Trim(int maxNodes, BranchNode? protect = null)
    {
        var removed = new List<BranchNode>();
        var max = Math.Max(maxNodes, 2);
        if (_nodes.Count <= max) return removed;

        var line = new HashSet<BranchNode>();
        for (var n = Current; n is not null; n = n.Parent) line.Add(n);

        while (_nodes.Count > max)
        {
            var leaf = _nodes.FirstOrDefault(n => n.Children.Count == 0 && !line.Contains(n) && n != protect);
            if (leaf is not null)
            {
                leaf.Parent!.RemoveChild(leaf);
                Detach(leaf);
                removed.Add(leaf);
                continue;
            }

            if (Root == Current || Root.Children.Count != 1) break;
            var oldRoot = Root;
            Detach(oldRoot);
            Root = oldRoot.Children[0];
            Root.Parent = null;
            line.Remove(oldRoot);
            removed.Add(oldRoot);
        }

        if (removed.Count > 0) Raise();
        return removed;
    }

    /// <summary>Discards every node; <paramref name="newRoot"/> becomes the root and the current node.</summary>
    public void Reset(BranchNode newRoot)
    {
        ArgumentNullException.ThrowIfNull(newRoot);
        foreach (var n in _nodes) n.IsDetached = true;
        _nodes.Clear();
        Root = Current = Attach(newRoot);
        Raise();
    }

    private T Attach<T>(T node) where T : BranchNode
    {
        node.Tree = this;
        node.Sequence = _nextSequence++;
        node.Depth = 0;
        _nodes.Add(node);
        return node;
    }

    private void Detach(BranchNode node)
    {
        node.IsDetached = true;
        _nodes.Remove(node);
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
