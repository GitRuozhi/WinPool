using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinPool.Application;

namespace WinPool_App.Controls;

/// <summary>
/// A wrapping storage-card panel that expands every row to the container width.
/// </summary>
public sealed class AdaptiveFlowPanel : Panel
{
    public double MinimumItemWidth { get; set; } = 150;

    public double HorizontalSpacing { get; set; } = 6;

    public double VerticalSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 1200 : Math.Max(0, availableSize.Width);
        var desiredHeight = 0d;
        var rows = EqualFillFlowLayout.CreateRows(
            Children.Count,
            width,
            MinimumItemWidth,
            HorizontalSpacing);
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
        var rows = EqualFillFlowLayout.CreateRows(
            Children.Count,
            width,
            MinimumItemWidth,
            HorizontalSpacing);

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
}
