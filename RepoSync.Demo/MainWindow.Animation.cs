using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RepoSync;

public partial class MainWindow
{
    // Highlights a cell that changed: gold flash fading out over a few seconds.
    void Flash(RepoItem item, string header, Color color, Color settle)
    {
        try
        {
            Grid.UpdateLayout();
            var col = Grid.Columns.FirstOrDefault(c => (string?)c.Header == header);
            if (col == null || Grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) return;
            row.UpdateLayout();
            var presenter = FindChild<System.Windows.Controls.Primitives.DataGridCellsPresenter>(row);
            if (presenter?.ItemContainerGenerator.ContainerFromIndex(col.DisplayIndex) is not DataGridCell cell) return;

            var brush = new SolidColorBrush(color);
            cell.Background = brush;
            var anim = new ColorAnimation(color, settle, TimeSpan.FromSeconds(3))
            {
                BeginTime = TimeSpan.FromSeconds(0.6), // hold the highlight briefly
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            anim.Completed += (_, _) => cell.ClearValue(BackgroundProperty);
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        catch { /* cosmetic only */ }
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    // Erase the old text character by character, then type the new text. Total ≈ 1.5 s.
    static async Task TypeSwap(string old, string @new, Action<string> set)
    {
        var per = Math.Clamp(1500 / Math.Max(1, old.Length + @new.Length), 4, 30);
        for (int i = old.Length - 1; i >= 0; i--) { set(old[..i]); await Task.Delay(per); }
        for (int i = 1; i <= @new.Length; i++) { set(@new[..i]); await Task.Delay(per); }
    }
}
