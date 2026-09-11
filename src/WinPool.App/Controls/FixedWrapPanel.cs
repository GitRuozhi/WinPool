using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinPool_App.Controls;

public sealed class FixedWrapPanel : Panel
{
    public double ItemWidth { get; set; } = 176;

    public double ItemHeight { get; set; } = 44;

    public double Spacing { get; set; } = 6;

    /// <summary>
    /// Number of consecutive children kept together vertically as one
    /// wrapping column. The default preserves ordinary row-major wrapping.
    /// </summary>
    public int ItemsPerColumn { get; set; } = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? ItemWidth : Math.Max(ItemWidth, availableSize.Width);
        var perLine = Math.Max(1, (int)Math.Floor((width + Spacing) / (ItemWidth + Spacing)));
        foreach (var child in Children)
        {
            child.Measure(new Size(ItemWidth, ItemHeight));
        }

        var itemsPerColumn = Math.Max(1, ItemsPerColumn);
        var columns = Children.Count == 0 ? 0 : (Children.Count + itemsPerColumn - 1) / itemsPerColumn;
        var bands = columns == 0 ? 0 : (columns + perLine - 1) / perLine;
        var columnHeight = itemsPerColumn * ItemHeight + (itemsPerColumn - 1) * Spacing;
        return new Size(width, bands == 0 ? 0 : bands * columnHeight + (bands - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var perLine = Math.Max(1, (int)Math.Floor((finalSize.Width + Spacing) / (ItemWidth + Spacing)));
        var itemsPerColumn = Math.Max(1, ItemsPerColumn);
        var columnHeight = itemsPerColumn * ItemHeight + (itemsPerColumn - 1) * Spacing;
        for (var index = 0; index < Children.Count; index++)
        {
            var group = index / itemsPerColumn;
            var rowInColumn = index % itemsPerColumn;
            var band = group / perLine;
            var column = group % perLine;
            Children[index].Arrange(new Rect(
                column * (ItemWidth + Spacing),
                band * (columnHeight + Spacing) + rowInColumn * (ItemHeight + Spacing),
                ItemWidth,
                ItemHeight));
        }

        var columns = Children.Count == 0 ? 0 : (Children.Count + itemsPerColumn - 1) / itemsPerColumn;
        var bands = columns == 0 ? 0 : (columns + perLine - 1) / perLine;
        return new Size(finalSize.Width, bands == 0 ? 0 : bands * columnHeight + (bands - 1) * Spacing);
    }
}
