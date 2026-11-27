using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SuperBar.ControlCenter.Views;

/// <summary>
/// The Control Center's one layout breakpoint, measured on the window (not on the element): below
/// <see cref="WideMinWidth"/> a page is Compact (a single column), from there on it is Wide (multi-column grids and
/// split views). Every responsive element reads it from here, so the whole window switches at the same width.
/// </summary>
public static class Breakpoints
{
    /// <summary>Window content width, in DIPs, from which the Wide layout applies.</summary>
    public const double WideMinWidth = 1100;

    /// <summary>True when the window showing <paramref name="xamlRoot"/> is wide. False while the element is not in a window.</summary>
    public static bool IsWide(XamlRoot? xamlRoot) => xamlRoot is not null && xamlRoot.Size.Width >= WideMinWidth;

    /// <summary>
    /// Calls <paramref name="onChanged"/> with the current state when <paramref name="element"/> is loaded, and again
    /// whenever its window crosses the breakpoint. The subscription lives only while the element is in the tree, so
    /// pages that are navigated away from are not kept alive.
    /// </summary>
    public static void Observe(FrameworkElement element, Action<bool> onChanged)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(onChanged);

        XamlRoot? observed = null;
        bool? last = null;

        void Update()
        {
            var wide = IsWide(element.XamlRoot);
            if (wide != last)
            {
                last = wide;
                onChanged(wide);
            }
        }

        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Update();

        void Detach()
        {
            if (observed is not null)
            {
                observed.Changed -= OnRootChanged;
                observed = null;
            }
        }

        void Attach()
        {
            Detach();
            observed = element.XamlRoot;
            if (observed is not null)
            {
                observed.Changed += OnRootChanged;
            }

            Update();
        }

        element.Loaded += (_, _) => Attach();
        element.Unloaded += (_, _) => Detach();
        if (element.IsLoaded)
        {
            Attach();
        }
    }
}

/// <summary>
/// Lays its children out in equal-width columns: <see cref="CompactColumns"/> of them below the breakpoint and
/// <see cref="WideColumns"/> from it on (see <see cref="Breakpoints"/>). Children flow left to right and wrap; every
/// child of a row gets the height of the tallest one, so cards in a row line up. Usable directly and as the
/// <c>ItemsPanel</c> of an <c>ItemsControl</c>.
/// </summary>
public sealed partial class ResponsiveGrid : Panel
{
    public static readonly DependencyProperty WideColumnsProperty = DependencyProperty.Register(
        nameof(WideColumns), typeof(int), typeof(ResponsiveGrid), new PropertyMetadata(2, OnLayoutPropertyChanged));

    public static readonly DependencyProperty CompactColumnsProperty = DependencyProperty.Register(
        nameof(CompactColumns), typeof(int), typeof(ResponsiveGrid), new PropertyMetadata(1, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(ResponsiveGrid), new PropertyMetadata(8d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(ResponsiveGrid), new PropertyMetadata(0d, OnLayoutPropertyChanged));

    /// <summary>How many columns a child takes. Clamped to the current column count, so a span of 2 is a full row in a two-column grid and an ordinary cell when compact.</summary>
    public static readonly DependencyProperty ColumnSpanProperty = DependencyProperty.RegisterAttached(
        "ColumnSpan", typeof(int), typeof(ResponsiveGrid), new PropertyMetadata(1, OnColumnSpanChanged));

    private readonly List<double> _rowHeights = [];
    private int _columns = 1;

    public ResponsiveGrid() => Breakpoints.Observe(this, _ => InvalidateMeasure());

    /// <summary>Columns from the breakpoint on. Default 2.</summary>
    public int WideColumns
    {
        get => (int)GetValue(WideColumnsProperty);
        set => SetValue(WideColumnsProperty, value);
    }

    /// <summary>Columns below the breakpoint. Default 1.</summary>
    public int CompactColumns
    {
        get => (int)GetValue(CompactColumnsProperty);
        set => SetValue(CompactColumnsProperty, value);
    }

    /// <summary>Gap between columns. Default 8 (the <c>CardGap</c> token).</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>Gap between rows. Default 0, because the <c>Card</c> style already carries the gap as its bottom margin.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public static int GetColumnSpan(DependencyObject element) => (int)element.GetValue(ColumnSpanProperty);

    public static void SetColumnSpan(DependencyObject element, int value) => element.SetValue(ColumnSpanProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        _columns = Math.Max(1, Breakpoints.IsWide(XamlRoot) ? WideColumns : CompactColumns);
        _rowHeights.Clear();

        // Unconstrained (e.g. inside a horizontal StackPanel): columns take the width of the widest child.
        var constrained = !double.IsInfinity(availableSize.Width);
        var columnWidth = constrained ? ColumnWidth(availableSize.Width) : double.PositiveInfinity;

        var column = 0;
        var rowHeight = 0d;
        var widestColumn = 0d;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            var span = Span(child);
            if (column + span > _columns)
            {
                _rowHeights.Add(rowHeight);
                rowHeight = 0;
                column = 0;
            }

            child.Measure(new Size(constrained ? SpanWidth(columnWidth, span) : double.PositiveInfinity, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            widestColumn = Math.Max(widestColumn, (child.DesiredSize.Width - ColumnSpacing * (span - 1)) / span);
            column += span;
        }

        if (column > 0)
        {
            _rowHeights.Add(rowHeight);
        }

        var width = constrained ? availableSize.Width : SpanWidth(widestColumn, _columns);
        var height = _rowHeights.Sum() + RowSpacing * Math.Max(0, _rowHeights.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnWidth = ColumnWidth(finalSize.Width);
        var column = 0;
        var row = 0;
        var y = 0d;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            var span = Span(child);
            if (column + span > _columns)
            {
                y += RowHeight(row) + RowSpacing;
                row++;
                column = 0;
            }

            // The whole row height, not the child's own: cards in a row end at the same line.
            child.Arrange(new Rect(column * (columnWidth + ColumnSpacing), y, SpanWidth(columnWidth, span), RowHeight(row)));
            column += span;
        }

        return finalSize;
    }

    private double ColumnWidth(double totalWidth) => Math.Max(0, (totalWidth - ColumnSpacing * (_columns - 1)) / _columns);

    private double SpanWidth(double columnWidth, int span) => columnWidth * span + ColumnSpacing * (span - 1);

    private double RowHeight(int row) => row < _rowHeights.Count ? _rowHeights[row] : 0;

    private int Span(UIElement child) => Math.Clamp(GetColumnSpan(child), 1, _columns);

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ResponsiveGrid)d).InvalidateMeasure();

    private static void OnColumnSpanChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement { Parent: ResponsiveGrid grid })
        {
            grid.InvalidateMeasure();
        }
    }
}

/// <summary>
/// A row of children (typically buttons) at their natural size, left to right, that wraps onto a new line when the
/// width runs out, so a button row never clips at the window's minimum size. Unlike <see cref="ResponsiveGrid"/> it
/// does not depend on the breakpoint, only on the width it gets.
/// </summary>
public sealed partial class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(8d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(8d, OnLayoutPropertyChanged));

    /// <summary>Gap between children on a line. Default 8 (<c>SpacingS</c>).</summary>
    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    /// <summary>Gap between lines. Default 8 (<c>SpacingS</c>).</summary>
    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Layout(availableSize.Width, arrange: false);
        return new Size(double.IsInfinity(availableSize.Width) ? size.Width : Math.Min(size.Width, availableSize.Width), size.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    /// <summary>Measures (or arranges) the children line by line; returns the size of the widest line by all lines.</summary>
    private Size Layout(double width, bool arrange)
    {
        var x = 0d;
        var y = 0d;
        var lineHeight = 0d;
        var widest = 0d;
        var lineStart = 0;
        var visible = new List<UIElement>();
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            if (!arrange)
            {
                child.Measure(new Size(width, double.PositiveInfinity));
            }

            visible.Add(child);
        }

        for (var i = 0; i < visible.Count; i++)
        {
            var desired = visible[i].DesiredSize;
            var next = x == 0 ? desired.Width : x + HorizontalSpacing + desired.Width;
            if (x > 0 && next > width)
            {
                ArrangeLine(visible, lineStart, i, y, lineHeight, arrange);
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
                lineStart = i;
                next = desired.Width;
            }

            x = next;
            widest = Math.Max(widest, x);
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        if (visible.Count > lineStart)
        {
            ArrangeLine(visible, lineStart, visible.Count, y, lineHeight, arrange);
            y += lineHeight;
        }

        return new Size(widest, y);
    }

    private void ArrangeLine(List<UIElement> children, int start, int end, double y, double lineHeight, bool arrange)
    {
        if (!arrange)
        {
            return;
        }

        var x = 0d;
        for (var i = start; i < end; i++)
        {
            var desired = children[i].DesiredSize;
            children[i].Arrange(new Rect(x, y, desired.Width, lineHeight));
            x += desired.Width + HorizontalSpacing;
        }
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WrapPanel)d).InvalidateMeasure();
}

/// <summary>
/// The root of every Control Center page template: the page title and subtitle, an optional action on the title row,
/// and the page content below them, in one vertically scrolling column that is centered and capped at
/// <see cref="ContentMaxWidth"/>. Its look (padding, typography, the scroll viewer) is the implicit
/// <c>views:PageContainer</c> style in ControlCenterStyles.xaml.
/// </summary>
public sealed partial class PageContainer : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageContainer), new PropertyMetadata(""));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageContainer), new PropertyMetadata(""));

    public static readonly DependencyProperty HeaderActionProperty = DependencyProperty.Register(
        nameof(HeaderAction), typeof(object), typeof(PageContainer), new PropertyMetadata(null));

    public static readonly DependencyProperty ContentMaxWidthProperty = DependencyProperty.Register(
        nameof(ContentMaxWidth), typeof(double), typeof(PageContainer), new PropertyMetadata(960d));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>Shown at the right of the title row, e.g. the page's primary "+ Add" button.</summary>
    public object? HeaderAction
    {
        get => GetValue(HeaderActionProperty);
        set => SetValue(HeaderActionProperty, value);
    }

    /// <summary>
    /// Width cap of the centered column: the <c>PageMaxWidthNarrow</c> token (the default) for single-column pages,
    /// <c>PageMaxWidthWide</c> for pages that go multi-column when wide.
    /// </summary>
    public double ContentMaxWidth
    {
        get => (double)GetValue(ContentMaxWidthProperty);
        set => SetValue(ContentMaxWidthProperty, value);
    }
}
