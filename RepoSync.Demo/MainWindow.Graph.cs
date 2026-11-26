using System.IO;
using System.Windows.Controls;

namespace RepoSync;

public partial class MainWindow
{
    // ---- Commit graph beside the list ----------------------------------------------------------------------

    int _graphSeq;

    void Grid_SelectionChanged(object s, SelectionChangedEventArgs e) => _ = ShowGraph(Grid.SelectedItem as RepoItem);

    async Task ShowGraph(RepoItem? r)
    {
        var seq = ++_graphSeq;
        if (r == null) return;
        var dir = Path.Combine(FolderBox.Text.Trim(), r.Name);
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            Graph.Tree = null;
            GraphInfo.Text = $"{r.Name}: not cloned yet";
            return;
        }
        GraphInfo.Text = $"{r.Name}: loading…";
        var (tree, info) = await Task.Run(() => CommitGraph.Build(dir));
        if (seq != _graphSeq) return; // selection changed meanwhile
        Graph.Tree = tree;
        GraphInfo.Text = $"{r.Name}: {info}";
    }
}
