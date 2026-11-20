namespace BranchKit;

/// <summary>
/// Git-log-style layout: one row per node in chronological order, and branches packed into lanes.
/// <para>
/// A "chain" starts at the root or at any non-first child and continues through first children.
/// Each chain occupies its lane from its parent's row (where the branch-off curve starts) to its last
/// node. Chains are sorted by start row and greedily assigned the lowest free lane (interval colouring),
/// so lanes get reused once a branch has finished.
/// </para>
/// </summary>
public sealed class BranchLayout
{
    private readonly Dictionary<BranchNode, int> _rows;
    private readonly Dictionary<BranchNode, int> _lanes;

    private BranchLayout(IReadOnlyList<BranchNode> rows, Dictionary<BranchNode, int> rowOf, Dictionary<BranchNode, int> laneOf, int laneCount)
    {
        Rows = rows;
        _rows = rowOf;
        _lanes = laneOf;
        LaneCount = laneCount;
    }

    /// <summary>Nodes by row; row 0 is the oldest.</summary>
    public IReadOnlyList<BranchNode> Rows { get; }

    public int LaneCount { get; }

    public int RowOf(BranchNode node) => _rows[node];

    public int LaneOf(BranchNode node) => _lanes[node];

    public bool Contains(BranchNode node) => _rows.ContainsKey(node);

    public static BranchLayout Compute(BranchTree tree) => Compute(tree.Nodes);

    public static BranchLayout Compute(IReadOnlyList<BranchNode> nodes)
    {
        var rowOf = new Dictionary<BranchNode, int>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++) rowOf[nodes[i]] = i;

        var chains = new List<Chain>();
        var chainOf = new Dictionary<BranchNode, Chain>(nodes.Count);

        foreach (var node in nodes)
        {
            var row = rowOf[node];
            Chain chain;
            if (node.Parent is { } parent && chainOf.TryGetValue(parent, out var parentChain) && parent.Children[0] == node)
            {
                chain = parentChain;
            }
            else
            {
                var start = node.Parent is { } p && rowOf.TryGetValue(p, out var pr) ? pr : row;
                chain = new Chain(start, chains.Count);
                chains.Add(chain);
            }

            chain.End = row;
            chain.Members.Add(node);
            chainOf[node] = chain;
        }

        var laneEnds = new List<int>();
        var laneOf = new Dictionary<BranchNode, int>(nodes.Count);
        foreach (var chain in chains.OrderBy(c => c.Start).ThenBy(c => c.Order))
        {
            var lane = laneEnds.FindIndex(end => end < chain.Start);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(chain.End);
            }
            else
            {
                laneEnds[lane] = chain.End;
            }

            foreach (var n in chain.Members) laneOf[n] = lane;
        }

        return new BranchLayout(nodes.ToArray(), rowOf, laneOf, Math.Max(1, laneEnds.Count));
    }

    private sealed class Chain(int start, int order)
    {
        public int Start { get; } = start;
        public int Order { get; } = order;
        public int End { get; set; }
        public List<BranchNode> Members { get; } = [];
    }
}
