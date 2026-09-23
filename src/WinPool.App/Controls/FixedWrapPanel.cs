using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinPool_App.Controls;

public sealed class FixedWrapPanel : Panel
{
    public double ItemWidth { get; set; } = 176;

    public double MinimumItemHeight { get; set; } = 44;

    public double Spacing { get; set; } = 6;

    /// <summary>
    /// Number of consecutive children kept together vertically as one
    /// wrapping column. The default preserves ordinary row-major wrapping.
    /// </summary>
    public int ItemsPerColumn { get; set; } = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? ItemWidth : Math.Max(ItemWidth, availableSize.Width);
        var perLine = ItemsPerLine(width);
        foreach (var child in Children)
        {
            child.Measure(new Size(ItemWidth, double.PositiveInfinity));
        }

        var itemsPerColumn = Math.Max(1, ItemsPerColumn);
        var columns = Children.Count == 0 ? 0 : (Children.Count + itemsPerColumn - 1) / itemsPerColumn;
        var bands = columns == 0 ? 0 : (columns + perLine - 1) / perLine;
        var rowHeights = MeasureRowHeights(perLine, itemsPerColumn, bands);
        return new Size(width, MeasureHeight(rowHeights));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var perLine = ItemsPerLine(finalSize.Width);
        var itemsPerColumn = Math.Max(1, ItemsPerColumn);
        var columns = Children.Count == 0 ? 0 : (Children.Count + itemsPerColumn - 1) / itemsPerColumn;
        var bands = columns == 0 ? 0 : (columns + perLine - 1) / perLine;
        var rowHeights = MeasureRowHeights(perLine, itemsPerColumn, bands);
        var bandOffsets = new double[bands];
        var currentOffset = 0d;
        for (var band = 0; band < bands; band++)
        {
            bandOffsets[band] = currentOffset;
            currentOffset += BandHeight(rowHeights[band]);
            if (band + 1 < bands)
            {
                currentOffset += Spacing;
            }
        }

        for (var index = 0; index < Children.Count; index++)
        {
            var group = index / itemsPerColumn;
            var rowInColumn = index % itemsPerColumn;
            var band = group / perLine;
            var column = group % perLine;
            var rowOffset = 0d;
            for (var row = 0; row < rowInColumn; row++)
            {
                rowOffset += rowHeights[band][row] + Spacing;
            }

            Children[index].Arrange(new Rect(
                column * (ItemWidth + Spacing),
                bandOffsets[band] + rowOffset,
                ItemWidth,
                rowHeights[band][rowInColumn]));
        }

        return new Size(finalSize.Width, MeasureHeight(rowHeights));
    }

    private int ItemsPerLine(double width) =>
        Math.Max(1, (int)Math.Floor((width + Spacing) / (ItemWidth + Spacing)));

    private double[][] MeasureRowHeights(int perLine, int itemsPerColumn, int bands)
    {
        var rows = new double[bands][];
        for (var band = 0; band < bands; band++)
        {
            rows[band] = new double[itemsPerColumn];
        }

        for (var index = 0; index < Children.Count; index++)
        {
            var group = index / itemsPerColumn;
            var band = group / perLine;
            var row = index % itemsPerColumn;
            rows[band][row] = Math.Max(MinimumItemHeight, Math.Max(rows[band][row], Children[index].DesiredSize.Height));
        }

        return rows;
    }

    private double MeasureHeight(double[][] rowHeights)
    {
        var total = 0d;
        for (var band = 0; band < rowHeights.Length; band++)
        {
            total += BandHeight(rowHeights[band]);
            if (band + 1 < rowHeights.Length)
            {
                total += Spacing;
            }
        }

        return total;
    }

    private double BandHeight(double[] rowHeights)
    {
        var total = 0d;
        var hasRow = false;
        foreach (var rowHeight in rowHeights)
        {
            if (rowHeight <= 0)
            {
                continue;
            }

            if (hasRow)
            {
                total += Spacing;
            }

            total += rowHeight;
            hasRow = true;
        }

        return total;
    }
}
