using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinPool_App.Controls;

public sealed class FixedWrapPanel : Panel
{
    public double ItemWidth { get; set; } = 176;

    public double ItemHeight { get; set; } = 44;

    public double Spacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? ItemWidth : Math.Max(ItemWidth, availableSize.Width);
        var perLine = Math.Max(1, (int)Math.Floor((width + Spacing) / (ItemWidth + Spacing)));
        foreach (var child in Children)
        {
            child.Measure(new Size(ItemWidth, ItemHeight));
        }

        var rows = Children.Count == 0 ? 0 : (Children.Count + perLine - 1) / perLine;
        return new Size(width, rows == 0 ? 0 : rows * ItemHeight + (rows - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var perLine = Math.Max(1, (int)Math.Floor((finalSize.Width + Spacing) / (ItemWidth + Spacing)));
        for (var index = 0; index < Children.Count; index++)
        {
            var row = index / perLine;
            var column = index % perLine;
            Children[index].Arrange(new Rect(
                column * (ItemWidth + Spacing),
                row * (ItemHeight + Spacing),
                ItemWidth,
                ItemHeight));
        }

        var rows = Children.Count == 0 ? 0 : (Children.Count + perLine - 1) / perLine;
        return new Size(finalSize.Width, rows == 0 ? 0 : rows * ItemHeight + (rows - 1) * Spacing);
    }
}
