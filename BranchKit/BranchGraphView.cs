using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BranchKit;

/// <summary>
/// Renders a <see cref="BranchTree"/> as a git-style graph: newest at the top, one lane per branch.
/// Click a row to activate it (by default: move the tree to that node). Up/Down step chronologically
/// through all nodes (across branches). Host it in a <see cref="ScrollViewer"/>.
/// <list type="bullet">
/// <item>Large ringed dot — current node</item>
/// <item>Filled dot — on the line from the root to the current node</item>
/// <item>Hollow dot — on another branch, or ahead of the current node</item>
/// <item>Tag (see <see cref="Marker"/>) — one highlighted node, e.g. "saved" or "released"</item>
/// </list>
/// Subclass and override <see cref="CanActivate"/>, <see cref="OnActivate"/> and <see cref="Describe"/>
/// to change what clicking does and what the tooltip says.
/// </summary>
public class BranchGraphView : Control
{
    private static readonly Brush[] DefaultLaneBrushes = CreatePalette(
        "#3B82F6", "#F59E0B", "#10B981", "#A855F7", "#EF4444", "#14B8A6", "#EC4899", "#84CC16");

    private BranchLayout? _layout;
    private FormattedText?[] _labels = [];
    private FormattedText?[] _times = [];
    private FormattedText? _markerTag;
    private int _hoverRow = -1;
    private BranchNode? _lastCurrent;
    private BranchTree? _subscribedTree;

    static BranchGraphView()
    {
        FocusableProperty.OverrideMetadata(typeof(BranchGraphView), new FrameworkPropertyMetadata(true));
        PaddingProperty.OverrideMetadata(typeof(BranchGraphView), new FrameworkPropertyMetadata(new Thickness(6, 4, 8, 4)));
    }

    public BranchGraphView()
    {
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    // ───────────────────────────── dependency properties ─────────────────────────────

    public static readonly DependencyProperty TreeProperty = DependencyProperty.Register(
        nameof(Tree), typeof(BranchTree), typeof(BranchGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((BranchGraphView)d).OnTreeChanged()));

    public BranchTree? Tree
    {
        get => (BranchTree?)GetValue(TreeProperty);
        set => SetValue(TreeProperty, value);
    }

    public static readonly DependencyProperty MarkerProperty = DependencyProperty.Register(
        nameof(Marker), typeof(BranchNode), typeof(BranchGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A node to tag with <see cref="MarkerText"/> (e.g. the save point). Null = none.</summary>
    public BranchNode? Marker
    {
        get => (BranchNode?)GetValue(MarkerProperty);
        set => SetValue(MarkerProperty, value);
    }

    public static readonly DependencyProperty MarkerTextProperty = DependencyProperty.Register(
        nameof(MarkerText), typeof(string), typeof(BranchGraphView),
        new FrameworkPropertyMetadata("marked", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string? MarkerText
    {
        get => (string?)GetValue(MarkerTextProperty);
        set => SetValue(MarkerTextProperty, value);
    }

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(BranchGraphView),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public double RowHeight
    {
        get => (double)GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    public static readonly DependencyProperty LaneWidthProperty = DependencyProperty.Register(
        nameof(LaneWidth), typeof(double), typeof(BranchGraphView),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public double LaneWidth
    {
        get => (double)GetValue(LaneWidthProperty);
        set => SetValue(LaneWidthProperty, value);
    }

    public static readonly DependencyProperty LaneBrushesProperty = DependencyProperty.Register(
        nameof(LaneBrushes), typeof(IList<Brush>), typeof(BranchGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Colours cycled per lane. Null = built-in palette.</summary>
    public IList<Brush>? LaneBrushes
    {
        get => (IList<Brush>?)GetValue(LaneBrushesProperty);
        set => SetValue(LaneBrushesProperty, value);
    }

    public static readonly DependencyProperty CurrentRowBrushProperty = DependencyProperty.Register(
        nameof(CurrentRowBrush), typeof(Brush), typeof(BranchGraphView),
        new FrameworkPropertyMetadata(Frozen(Color.FromArgb(0x33, 0x3B, 0x82, 0xF6)), FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? CurrentRowBrush
    {
        get => (Brush?)GetValue(CurrentRowBrushProperty);
        set => SetValue(CurrentRowBrushProperty, value);
    }

    public static readonly DependencyProperty HoverRowBrushProperty = DependencyProperty.Register(
        nameof(HoverRowBrush), typeof(Brush), typeof(BranchGraphView),
        new FrameworkPropertyMetadata(Frozen(Color.FromArgb(0x18, 0x80, 0x80, 0x80)), FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? HoverRowBrush
    {
        get => (Brush?)GetValue(HoverRowBrushProperty);
        set => SetValue(HoverRowBrushProperty, value);
    }

    public static readonly DependencyProperty ShowTimestampsProperty = DependencyProperty.Register(
        nameof(ShowTimestamps), typeof(bool), typeof(BranchGraphView),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ShowTimestamps
    {
        get => (bool)GetValue(ShowTimestampsProperty);
        set => SetValue(ShowTimestampsProperty, value);
    }

    /// <summary>Raised before a node is activated; set Cancel to veto (e.g. to ask about unsaved edits).</summary>
    public event EventHandler<BranchNodeActivatingEventArgs>? NodeActivating;

    // ───────────────────────────── extension points ─────────────────────────────

    /// <summary>Whether clicking/keyboard navigation may activate <paramref name="node"/> right now.</summary>
    protected virtual bool CanActivate(BranchNode node) => Tree is not null;

    /// <summary>What activating a node does. Default: move the tree there.</summary>
    protected virtual void OnActivate(BranchNode node) => Tree?.MoveTo(node);

    /// <summary>Tooltip text for a row.</summary>
    protected virtual string? Describe(BranchNode node)
    {
        var branches = node.Children.Count > 1 ? $"\n{node.Children.Count} branches" : "";
        return $"#{node.Sequence} {node.Label}\n{node.Timestamp:G}{branches}";
    }

    /// <summary>Forces the layout to be recomputed. Call when something the view reads changes outside the tree.</summary>
    public void Refresh()
    {
        _layout = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ───────────────────────────── tree wiring ─────────────────────────────

    private void OnTreeChanged()
    {
        Unhook();
        if (IsLoaded) Hook();
        _layout = null;
        _hoverRow = -1;
    }

    private void Hook()
    {
        if (_subscribedTree is not null || Tree is not { } t) return;
        t.Changed += OnTreeEvent;
        _subscribedTree = t;
        Refresh();
    }

    private void Unhook()
    {
        if (_subscribedTree is null) return;
        _subscribedTree.Changed -= OnTreeEvent;
        _subscribedTree = null;
    }

    private void OnTreeEvent(object? sender, EventArgs e)
    {
        Refresh();

        if (Tree?.Current != _lastCurrent)
        {
            _lastCurrent = Tree?.Current;
            Dispatcher.InvokeAsync(BringCurrentIntoView, DispatcherPriority.Loaded);
        }
    }

    private void BringCurrentIntoView()
    {
        if (Tree?.Current is { } cur && EnsureLayout() is { } layout && layout.Contains(cur))
            BringIntoView(RowRect(layout.RowOf(cur), layout.Rows.Count));
    }

    // ───────────────────────────── layout ─────────────────────────────

    private BranchLayout? EnsureLayout()
    {
        if (_layout is null && Tree is { } t) _layout = BranchLayout.Compute(t);
        return _layout;
    }

    private double GraphWidth(BranchLayout layout) => Padding.Left + layout.LaneCount * LaneWidth + 6;

    private double RowTop(int row, int rowCount) => Padding.Top + (rowCount - 1 - row) * RowHeight;

    private Rect RowRect(int row, int rowCount) => new(0, RowTop(row, rowCount), Math.Max(ActualWidth, 1), RowHeight);

    private Point Center(BranchLayout layout, BranchNode node) => new(
        Padding.Left + layout.LaneOf(node) * LaneWidth + LaneWidth / 2,
        RowTop(layout.RowOf(node), layout.Rows.Count) + RowHeight / 2);

    private int RowAt(double y)
    {
        if (_layout is not { } layout) return -1;
        var index = (int)Math.Floor((y - Padding.Top) / RowHeight);
        var row = layout.Rows.Count - 1 - index;
        return index >= 0 && row >= 0 ? row : -1;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var layout = EnsureLayout();
        if (layout is null) return new Size(0, 0);

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var dim = DimForeground();
        var n = layout.Rows.Count;
        _labels = new FormattedText?[n];
        _times = new FormattedText?[n];
        _markerTag = string.IsNullOrEmpty(MarkerText)
            ? null
            : new FormattedText(MarkerText, CultureInfo.CurrentUICulture, FlowDirection, typeface, FontSize * 0.8, Background ?? Brushes.White, dip);

        double maxText = 0;
        for (var i = 0; i < n; i++)
        {
            var node = layout.Rows[i];
            var label = new FormattedText(node.Label, CultureInfo.CurrentUICulture, FlowDirection, typeface, FontSize, Foreground, dip);
            _labels[i] = label;
            var w = label.WidthIncludingTrailingWhitespace;
            if (ShowTimestamps)
            {
                var time = new FormattedText(node.Timestamp.ToString("T", CultureInfo.CurrentCulture), CultureInfo.CurrentUICulture,
                    FlowDirection, typeface, FontSize * 0.85, dim, dip);
                _times[i] = time;
                w += 10 + time.Width;
            }
            if (_markerTag is not null && node == Marker) w += 10 + _markerTag.Width + 8;
            maxText = Math.Max(maxText, w);
        }

        return new Size(GraphWidth(layout) + maxText + Padding.Right, Padding.Top + n * RowHeight + Padding.Bottom);
    }

    // ───────────────────────────── rendering ─────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        var background = Background ?? Brushes.Transparent; // transparent keeps the whole surface hit-testable
        dc.DrawRectangle(background, null, new Rect(RenderSize));

        if (Tree is not { } tree || EnsureLayout() is not { } layout) return;
        var n = layout.Rows.Count;
        if (_labels.Length != n) return; // measure pending

        var dotFill = Background is SolidColorBrush { Color.A: 255 } ? Background : SystemColors.WindowBrush;
        var line = new HashSet<BranchNode>();
        for (var x = tree.Current; x is not null; x = x.Parent) line.Add(x);

        // Row highlights
        if (_hoverRow >= 0 && _hoverRow < n && HoverRowBrush is { } hover)
            dc.DrawRectangle(hover, null, RowRect(_hoverRow, n));
        if (layout.Contains(tree.Current) && CurrentRowBrush is { } curBrush)
            dc.DrawRectangle(curBrush, null, RowRect(layout.RowOf(tree.Current), n));

        // Edges
        foreach (var node in layout.Rows)
        {
            if (node.Parent is not { } parent || !layout.Contains(parent)) continue;
            var from = Center(layout, parent);
            var to = Center(layout, node);
            var pen = new Pen(LaneBrush(layout.LaneOf(node)), line.Contains(node) ? 2 : 1.5);
            pen.Freeze();

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(from, false, false);
                if (Math.Abs(from.X - to.X) < 0.5)
                {
                    ctx.LineTo(to, true, false);
                }
                else
                {
                    var bendY = from.Y - RowHeight;
                    ctx.BezierTo(new Point(from.X, from.Y - RowHeight * 0.55), new Point(to.X, bendY + RowHeight * 0.45), new Point(to.X, bendY), true, true);
                    ctx.LineTo(to, true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        // Dots and labels
        var textX = GraphWidth(layout);
        for (var row = 0; row < n; row++)
        {
            var node = layout.Rows[row];
            var c = Center(layout, node);
            var brush = LaneBrush(layout.LaneOf(node));

            if (node == tree.Current)
            {
                dc.DrawEllipse(dotFill, new Pen(brush, 2), c, 7, 7);
                dc.DrawEllipse(brush, null, c, 4, 4);
            }
            else if (line.Contains(node))
            {
                dc.DrawEllipse(brush, null, c, 4, 4);
            }
            else
            {
                dc.DrawEllipse(dotFill, new Pen(brush, 1.5), c, 3.5, 3.5);
            }

            var label = _labels[row]!;
            var top = RowTop(row, n);
            dc.PushOpacity(line.Contains(node) ? 1.0 : 0.6);
            dc.DrawText(label, new Point(textX, top + (RowHeight - label.Height) / 2));
            var x = textX + label.WidthIncludingTrailingWhitespace + 10;

            if (_times[row] is { } time)
            {
                dc.DrawText(time, new Point(x, top + (RowHeight - time.Height) / 2));
                x += time.Width + 10;
            }
            dc.Pop();

            if (node == Marker && _markerTag is { } tag)
            {
                var tagRect = new Rect(x, top + (RowHeight - tag.Height) / 2 - 1, tag.Width + 8, tag.Height + 2);
                dc.DrawRoundedRectangle(brush, null, tagRect, 3, 3);
                dc.DrawText(tag, new Point(x + 4, tagRect.Top + 1));
            }
        }
    }

    // ───────────────────────────── input ─────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var row = RowAt(e.GetPosition(this).Y);
        if (row == _hoverRow) return;
        _hoverRow = row;
        Cursor = row >= 0 ? Cursors.Hand : null;
        ToolTip = row >= 0 && _layout is { } l ? Describe(l.Rows[row]) : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverRow = -1;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var row = RowAt(e.GetPosition(this).Y);
        if (row >= 0 && _layout is { } layout)
        {
            Activate(layout.Rows[row]);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Tree is not { } t || EnsureLayout() is not { } layout || !layout.Contains(t.Current)) return;
        var row = layout.RowOf(t.Current);
        var target = e.Key switch
        {
            Key.Up => row + 1,
            Key.Down => row - 1,
            Key.Home => layout.Rows.Count - 1,
            Key.End => 0,
            _ => -1,
        };
        if (target >= 0 && target < layout.Rows.Count)
        {
            Activate(layout.Rows[target]);
            e.Handled = true;
        }
    }

    private void Activate(BranchNode node)
    {
        if (!CanActivate(node)) return;
        var args = new BranchNodeActivatingEventArgs(node);
        NodeActivating?.Invoke(this, args);
        if (!args.Cancel) OnActivate(node);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private Brush LaneBrush(int lane)
    {
        var brushes = LaneBrushes is { Count: > 0 } custom ? custom : DefaultLaneBrushes;
        return brushes[lane % brushes.Count];
    }

    private Brush DimForeground()
    {
        if (Foreground is SolidColorBrush s) return Frozen(Color.FromArgb(0x99, s.Color.R, s.Color.G, s.Color.B));
        return Foreground;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Brush[] CreatePalette(params string[] hex) =>
        hex.Select(h => (Brush)Frozen((Color)ColorConverter.ConvertFromString(h))).ToArray();
}

public sealed class BranchNodeActivatingEventArgs(BranchNode node) : EventArgs
{
    public BranchNode Node { get; } = node;
    public bool Cancel { get; set; }
}
