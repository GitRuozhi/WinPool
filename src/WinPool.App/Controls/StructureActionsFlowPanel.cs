using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinPool_App.Controls;

/// <summary>Flows structure actions in their business order at their natural widths.</summary>
public sealed class StructureActionsFlowPanel : Panel
{
    public double Spacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize) => Layout(availableSize.Width, false);
    protected override Size ArrangeOverride(Size finalSize) => Layout(finalSize.Width, true);

    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, usedWidth = 0;
        foreach (var child in Children)
        {
            if (!arrange) child.Measure(new Size(width, double.PositiveInfinity));
            var size = child.DesiredSize;
            var itemWidth = Math.Min(width, size.Width);
            if (x > 0 && x + itemWidth > width)
            {
                y += rowHeight + Spacing;
                x = 0;
                rowHeight = 0;
            }
            if (arrange) child.Arrange(new Rect(x, y, itemWidth, size.Height));
            usedWidth = Math.Max(usedWidth, x + itemWidth);
            x += itemWidth + Spacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(double.IsFinite(width) ? width : usedWidth, y + rowHeight);
    }
}
