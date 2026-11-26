using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RepoSync;

public partial class MainWindow
{
    // ---- Open: the repo's solution file ---------------------------------------------------------------------

    async Task RefreshSolutions(RepoItem r)
    {
        var dir = Path.Combine(FolderBox.Text.Trim(), r.Name);
        r.SetSolutions(await Task.Run(() => FindSolutions(dir)));
    }

    // Solution files in the repo root; if there are none, the nearest ones within two folders down.
    static List<string> FindSolutions(string repo)
    {
        var result = new List<string>();
        if (!Directory.Exists(Path.Combine(repo, ".git"))) return result;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "packages", "node_modules" };

        IEnumerable<string> Level(string folder) => Directory.EnumerateFiles(folder, "*.sln*", options)
            .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

        try
        {
            var current = new List<string> { repo };
            for (int depth = 0; depth <= 2 && result.Count == 0; depth++)
            {
                result.AddRange(current.SelectMany(Level));
                if (result.Count > 0) break;
                current = current.SelectMany(f => Directory.EnumerateDirectories(f, "*", options))
                    .Where(d => !skip.Contains(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.'))
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    // Main half of the split button: open the default solution.
    void RowOpen_Click(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement { DataContext: RepoItem r } && r.DefaultSolution() is { } path) OpenSolution(path, r);
    }

    // Dropdown half (only shown when a repo has several solutions): choose which one to open.
    void RowOpenMenu_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement el || el.DataContext is not RepoItem r || r.Solutions.Count == 0) return;
        var root = Path.Combine(FolderBox.Text.Trim(), r.Name);
        var def = r.DefaultSolution();
        var menu = new ContextMenu
        {
            PlacementTarget = el,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var path in r.Solutions)
        {
            var item = new MenuItem
            {
                Header = Path.GetRelativePath(root, path),
                FontWeight = path == def ? FontWeights.Bold : FontWeights.Normal,
            };
            item.Click += (_, _) => OpenSolution(path, r);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    static void OpenSolution(string path, RepoItem r)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } // opens in the app registered for .sln/.slnx
        catch (Exception ex) { r.Status = "Could not open solution: " + ex.Message; }
    }
}
