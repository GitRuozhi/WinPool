using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinPool.Application;

namespace WinPool_App.Controls;

public sealed class AdaptiveFlowPanel : Panel
{
    private const double FallbackMeasureWidth = 1200;

    public double HorizontalSpacing { get; set; } = TopologyLayoutEngine.SiblingSpacing;

    public double VerticalSpacing { get; set; } = TopologyLayoutEngine.SiblingSpacing;

    protected override Size MeasureOverride(Size availableSize)
    {
        var plan = ResolvePlan(availableSize);
        var desiredHeight = 0d;
        var widestRow = 0d;
        foreach (var row in plan)
        {
            var rowHeight = 0d;
            var rowWidth = 0d;
            for (var i = 0; i < row.Count; i++)
            {
                var child = Children[row[i].Index];
                child.Measure(new Size(row[i].Width, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                rowWidth += row[i].Width;
                if (i > 0)
                {
                    rowWidth += HorizontalSpacing;
                }
            }

            desiredHeight += rowHeight + VerticalSpacing;
            widestRow = Math.Max(widestRow, rowWidth);
        }

        if (plan.Count > 0)
        {
            desiredHeight -= VerticalSpacing;
        }

        // A no-wrap strip may overflow its card: report the full row width
        // so the hosting scroll surface scrolls instead of clipping.
        var width = double.IsInfinity(availableSize.Width)
            ? Math.Max(FallbackMeasureWidth, widestRow)
            : Math.Max(Math.Max(0, availableSize.Width), widestRow);
        return new Size(width, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Max(0, finalSize.Width);
        var plan = ResolvePlan(new Size(width, finalSize.Height));
        var y = 0d;
        foreach (var row in plan)
        {
            var x = 0d;
            var lineHeight = 0d;
            var rowWidth = 0d;
            foreach (var slot in row)
            {
                lineHeight = Math.Max(lineHeight, Children[slot.Index].DesiredSize.Height);
                rowWidth += slot.Width;
            }

            rowWidth += HorizontalSpacing * Math.Max(0, row.Count - 1);
            var rowFits = rowWidth <= width + 0.5;
            foreach (var slot in row)
            {
                var isLastInRow = ReferenceEquals(slot, row[^1]);
                var itemWidth = isLastInRow && rowFits
                    ? Math.Max(0, width - x)
                    : slot.Width;
                Children[slot.Index].Arrange(new Rect(x, y, itemWidth, lineHeight));
                x += itemWidth + HorizontalSpacing;
            }

            y += lineHeight + VerticalSpacing;
        }

        return finalSize;
    }

    /// <summary>
    /// Engine path: row membership and slot widths come from the cascaded
    /// unit plan of the surface root layout. Fallback path (no cascade
    /// reached this panel yet): the previous equal-fill computation.
    /// </summary>
    private List<List<RowSlot>> ResolvePlan(Size availableSize)
    {
        var owner = TopologyVisualTree.FindOwnerViewModel(this);
        if (owner is not null
            && owner.LayoutRows.Count > 0
            && owner.LayoutChildWidths.Count == Children.Count)
        {
            return owner.LayoutRows
                .Select(row => row
                    .Select(index => new RowSlot(index, owner.LayoutChildWidths[index]))
                    .ToList())
                .ToList();
        }

        // Cascade-missing fallback: a single row at the mechanical leaf
        // minimum. The panel makes no width-distribution decision here
        // (Plan §8.3); the next engine pass at the surface root corrects it.
        return [Children
            .Select((_, index) => new RowSlot(index, TopologyLayoutEngine.LeafMinWidth))
            .ToList()];
    }

    private sealed record RowSlot(int Index, double Width);
}
