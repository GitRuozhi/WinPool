using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinPool.Application;

namespace WinPool_App.Controls;

public sealed class AdaptiveFlowPanel : Panel
{
    private const double FallbackMeasureWidth = 1200;

    public double MinimumItemWidth { get; set; } = TopologyLayoutEngine.LeafMinWidth;

    public double HorizontalSpacing { get; set; } = TopologyLayoutEngine.SiblingSpacing;

    public double VerticalSpacing { get; set; } = TopologyLayoutEngine.SiblingSpacing;

    protected override Size MeasureOverride(Size availableSize)
    {
        var plan = ResolvePlan(availableSize);
        var desiredHeight = 0d;
        foreach (var row in plan)
        {
            var rowHeight = 0d;
            foreach (var slot in row)
            {
                var child = Children[slot.Index];
                child.Measure(new Size(slot.Width, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            desiredHeight += rowHeight + VerticalSpacing;
        }

        if (plan.Count > 0)
        {
            desiredHeight -= VerticalSpacing;
        }

        var width = double.IsInfinity(availableSize.Width) ? FallbackMeasureWidth : Math.Max(0, availableSize.Width);
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
            foreach (var slot in row)
            {
                lineHeight = Math.Max(lineHeight, Children[slot.Index].DesiredSize.Height);
            }

            foreach (var slot in row)
            {
                var isLastInRow = ReferenceEquals(slot, row[^1]);
                var itemWidth = isLastInRow
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

        var width = double.IsInfinity(availableSize.Width) ? FallbackMeasureWidth : Math.Max(0, availableSize.Width);
        var columns = owner is { LayoutFlowColumns: > 0 }
            ? owner.LayoutFlowColumns
            : Math.Max(1, Children.Count);
        return EqualFillFlowLayout
            .CreateRowsForColumnCount(Children.Count, columns, width, HorizontalSpacing)
            .Select(row => Enumerable
                .Range(row.StartIndex, row.Count)
                .Select(index => new RowSlot(index, row.ItemWidth))
                .ToList())
            .ToList();
    }

    private sealed record RowSlot(int Index, double Width);
}
