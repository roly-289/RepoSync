using BranchKit;

namespace RepoSync;

/// <summary>Builds the BranchKit commit tree shown beside the repo list.</summary>
public static class CommitGraph
{
    // Commit graph (first-parent tree, latest 300 commits across all refs). Current = HEAD.
    public static (BranchTree? tree, string info) Build(string dir)
    {
        const int Max = 300;
        var (code, log) = GitService.Run(dir, $"log --all --topo-order --reverse -n {Max} --format=%H%x1f%P%x1f%ct%x1f%D%x1f%s");
        if (code != 0 || log == "") return (null, "no commits");

        var map = new Dictionary<string, BranchNode>();
        BranchTree? tree = null;
        BranchNode? root = null;
        var merges = 0;
        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\x1f');
            if (f.Length < 5) continue;
            var label = f[3] == "" ? f[4] : $"{f[4]}  [{f[3]}]";
            var time = DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[2])).LocalDateTime;
            var node = new BranchNode<string>(f[0], label, time);
            var parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tree == null) { tree = new BranchTree(node); root = node; }
            else
            {
                // First parent = the line it continues; further parents = branches merged in (needs BranchKit with merge support).
                var first = parents.Length > 0 && map.TryGetValue(parents[0], out var p) ? p : root;
                var merged = parents.Skip(1).Select(h => map.GetValueOrDefault(h))
                    .Where(m => m != null && m != first).Distinct().Select(m => m!).ToList();
                if (merged.Count > 0) merges++;
                tree.Add(node, first, merged.Count > 0 ? merged : null);
            }
            map[f[0]] = node;
        }
        if (tree == null) return (null, "no commits");

        var head = GitService.Run(dir, "rev-parse HEAD").output;
        if (map.TryGetValue(head, out var h)) tree.Select(h);
        return (tree, $"{map.Count} commits, {merges} merges" + (map.Count == Max ? " (latest shown)" : ""));
    }
}
