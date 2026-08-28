using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinPool.Application;

namespace WinPool_App.Controls;

public sealed class AdaptiveFlowPanel : Panel
{
    public double MinimumItemWidth { get; set; } = TopologyLayoutEngine.LeafMinWidth;

    public double HorizontalSpacing { get; set; } = TopologyLayoutEngine.SiblingSpacing;

    public double VerticalSpacing { get; set; } = TopologyLayoutEngine.SiblingSpacing;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 1200 : Math.Max(0, availableSize.Width);
        var rows = CreateRows(width);
        var desiredHeight = 0d;
        foreach (var row in rows)
        {
            var rowHeight = 0d;
            for (var index = row.StartIndex; index < row.StartIndex + row.Count; index++)
            {
                var child = Children[index];
                child.Measure(new Size(row.ItemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            desiredHeight += rowHeight + VerticalSpacing;
        }

        if (rows.Count > 0)
        {
            desiredHeight -= VerticalSpacing;
        }

        return new Size(width, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Max(0, finalSize.Width);
        var rows = CreateRows(width);
        var y = 0d;
        foreach (var row in rows)
        {
            var x = 0d;
            var lineHeight = 0d;
            for (var index = row.StartIndex; index < row.StartIndex + row.Count; index++)
            {
                lineHeight = Math.Max(lineHeight, Children[index].DesiredSize.Height);
            }

            for (var index = row.StartIndex; index < row.StartIndex + row.Count; index++)
            {
                var isLastInRow = index == row.StartIndex + row.Count - 1;
                var itemWidth = isLastInRow
                    ? Math.Max(0, width - x)
                    : row.ItemWidth;
                Children[index].Arrange(new Rect(x, y, itemWidth, lineHeight));
                x += itemWidth + HorizontalSpacing;
            }

            y += lineHeight + VerticalSpacing;
        }

        return finalSize;
    }

    private IReadOnlyList<EqualFillFlowRow> CreateRows(double width)
    {
        var owner = TopologyVisualTree.FindOwnerViewModel(this);
        var columns = owner is { LayoutFlowColumns: > 0 }
            ? owner.LayoutFlowColumns
            : Math.Max(1, Children.Count);
        return EqualFillFlowLayout.CreateRowsForColumnCount(
            Children.Count,
            columns,
            width,
            HorizontalSpacing);
    }
}
