using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RepoSync;

// ---- Vertical mode: switched on automatically whenever the window is taller than it is wide. A narrow layout: Text wraps, buttons and headers become symbols. Everything is switched from here so the XAML stays single.
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
        ("TxtFolder", "Local folder:", "📁"), ("TxtFeed", "NuGet feed:", "📦"), ("TxtShowGraph", "Show graph", "⑂"), ("TxtGraphBottom", "Graph at bottom when vertical", "⤓"),
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

        ApplyLayout();
    }

    void GraphOption_Click(object sender, RoutedEventArgs e) { ApplyLayout(); SaveSettings(); }

    // Where the commit graph goes: hidden, on the right (narrower in vertical mode), or under the table (vertical mode only, if chosen).
    void ApplyLayout()
    {
        bool show = ShowGraphBox.IsChecked == true;
        bool bottom = show && _compact && GraphBottomBox.IsChecked == true;
        bool right = show && !bottom;
        GraphPanel.Visibility = Split.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        Body.ColumnDefinitions[1].Width = new GridLength(right ? 5 : 0);
        Body.ColumnDefinitions[2].Width = new GridLength(right ? (_compact ? 170 : 380) : 0);
        Body.RowDefinitions[1].Height = new GridLength(bottom ? 5 : 0);
        Body.RowDefinitions[2].Height = new GridLength(bottom ? 200 : 0);

        System.Windows.Controls.Grid.SetColumn(GraphPanel, bottom ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(GraphPanel, bottom ? 2 : 0);
        System.Windows.Controls.Grid.SetColumn(Split, bottom ? 0 : 1);
        System.Windows.Controls.Grid.SetRow(Split, bottom ? 1 : 0);
        Split.ResizeDirection = bottom ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        Split.Width = bottom ? double.NaN : 5;
        Split.Height = bottom ? 5 : double.NaN;
        Split.HorizontalAlignment = HorizontalAlignment.Stretch;
        Split.VerticalAlignment = VerticalAlignment.Stretch;
    }
}
