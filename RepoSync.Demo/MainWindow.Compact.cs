using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RepoSync;

// ---- Vertical mode: switched on automatically whenever the window is taller than it is wide. A narrow layout: Text wraps, buttons and headers become symbols, the font shrinks, and the
// ---- commit graph moves under the table. Everything is switched from here so the XAML stays single.
public partial class MainWindow
{
    bool _compact;
    DataGridLength[]? _wideCols;

    // Column order in MainWindow.xaml: Select, Repo, Branch, Last commit, Update, Pack, Open, Private, Last updated, Status, Message.
    static readonly (string Full, string Symbol)[] Heads =
    [
        ("Select", "☑"), ("Repo", "▤"), ("Branch", "⑂"), ("Last commit", "✎"), ("", ""), ("", ""), ("", ""),
        ("Private", "🔒"), ("Last updated", "🕒"), ("Status", "✔"), ("Message", "✉"),
    ];

    static readonly DataGridLength[] NarrowCols =
    [
        new(30), new(88), new(72), new(130), new(32), new(40), new(58), new(28), new(60), new(30),
        new(1, DataGridLengthUnitType.Star),
    ];

    // (resource key, normal text, vertical-mode symbol)
    static readonly (string Key, string Wide, string Narrow)[] Texts =
    [
        ("TxtUpdate", "Update", "⇩"), ("TxtOpen", "Open", "↗"),
        ("TxtSelectAll", "Select all", "☑"), ("TxtSelectNone", "Select none", "☐"),
        ("TxtCheck", "Check for updates", "⟳"), ("TxtPull", "Pull latest & checkout latest branch", "⇩ Pull"),
        ("TxtLoad", "Load repos", "☁"), ("TxtBrowse", "Browse…", "…"),
        ("TxtProfile", "GitHub profile:", "👤"), ("TxtToken", "Token (optional):", "🔑"), ("TxtTheme", "Theme:", "◐"),
        ("TxtFolder", "Local folder:", "📁"), ("TxtFeed", "NuGet feed:", "📦"),
        ("TxtIncludeNew", "Include libraries not yet in feed", "+new"),
    ];

    void ApplyCompact(bool on)
    {
        _compact = on;
        RepoItem.Compact = on;
        foreach (var r in _repos) r.RefreshText();

        foreach (var (key, wide, narrow) in Texts) Resources[key] = on ? narrow : wide;
        Resources["TopPad"] = on ? new Thickness(6, 2, 6, 2) : new Thickness(12, 2, 12, 2);
        Resources["BtnPad"] = on ? new Thickness(1, 0, 1, 0) : new Thickness(8, 0, 8, 0);

        // Smaller text.
        if (on) { Grid.FontSize = 10; GraphInfo.FontSize = 10; Graph.FontSize = 10; }
        else { Grid.ClearValue(Control.FontSizeProperty); GraphInfo.ClearValue(TextBlock.FontSizeProperty); Graph.ClearValue(Control.FontSizeProperty); }
        Graph.RowHeight = on ? 18 : 24;

        // Column widths, symbol headers (the full name is the tooltip) and wrapping text.
        _wideCols ??= Grid.Columns.Select(c => c.Width).ToArray();
        for (int i = 0; i < Grid.Columns.Count && i < Heads.Length; i++)
        {
            var col = Grid.Columns[i];
            col.Width = on ? NarrowCols[i] : _wideCols[i];
            var (full, sym) = Heads[i];
            col.Header = on && sym != ""
                ? new TextBlock { Text = sym, ToolTip = full, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol") }
                : full;
            if (col is DataGridTextColumn tc && i != 8)
            {
                if (on) tc.ElementStyle = (Style)FindResource("WrapText");
                else tc.ClearValue(DataGridTextColumn.ElementStyleProperty);
            }
        }

        // Layout: the graph sits beside the table normally, under it in vertical mode.
        Body.ColumnDefinitions[1].Width = new GridLength(on ? 0 : 5);
        Body.ColumnDefinitions[2].Width = new GridLength(on ? 0 : 380);
        Body.RowDefinitions[1].Height = new GridLength(on ? 5 : 0);
        Body.RowDefinitions[2].Height = new GridLength(on ? 200 : 0);
        System.Windows.Controls.Grid.SetColumn(GraphPanel, on ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(GraphPanel, on ? 2 : 0);
        System.Windows.Controls.Grid.SetColumn(Split, on ? 0 : 1);
        System.Windows.Controls.Grid.SetRow(Split, on ? 1 : 0);
        Split.ResizeDirection = on ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        Split.Width = on ? double.NaN : 5;
        Split.Height = on ? 5 : double.NaN;
        Split.HorizontalAlignment = HorizontalAlignment.Stretch;
        Split.VerticalAlignment = VerticalAlignment.Stretch;

    }
}
