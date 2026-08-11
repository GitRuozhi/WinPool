using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool_App.Controls;

/// <summary>
/// Greedily wraps top-level storage containers and allocates each row in
/// proportion to the maximum parallel storage-slot count of each container.
/// </summary>
public sealed class WeightedPoolPanel : Panel
{
    public double SlotWidth { get; set; } = 150;
    public double HorizontalSpacing { get; set; } = 6;
    public double VerticalSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = NormalizeWidth(availableSize.Width);
        var rows = BuildRows(width);
        var height = 0d;
        foreach (var row in rows)
        {
            var widths = AllocateWidths(row, width);
            var rowHeight = 0d;
            for (var index = 0; index < row.Count; index++)
            {
                row[index].Measure(new Size(widths[index], double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, row[index].DesiredSize.Height);
            }
            height += rowHeight;
            if (!ReferenceEquals(row, rows[^1]))
            {
                height += VerticalSpacing;
            }
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = NormalizeWidth(finalSize.Width);
        var rows = BuildRows(width);
        var y = 0d;
        foreach (var row in rows)
        {
            var widths = AllocateWidths(row, width);
            var rowHeight = row.Count == 0 ? 0 : row.Max(x => x.DesiredSize.Height);
            var x = 0d;
            for (var index = 0; index < row.Count; index++)
            {
                var itemWidth = index == row.Count - 1
                    ? Math.Max(0, width - x)
                    : widths[index];
                row[index].Arrange(new Rect(x, y, itemWidth, rowHeight));
                x += itemWidth + HorizontalSpacing;
            }
            y += rowHeight + VerticalSpacing;
        }
        return finalSize;
    }

    private List<List<UIElement>> BuildRows(double width)
    {
        var children = Children.Cast<UIElement>().ToList();
        var weights = children.Select(Weight).ToList();
        return WeightedPoolLayout
            .CreateRows(weights, width, SlotWidth, HorizontalSpacing)
            .Select(row => row.Select(index => children[index]).ToList())
            .ToList();
    }

    private List<double> AllocateWidths(IReadOnlyList<UIElement> row, double width)
        => WeightedPoolLayout
            .AllocateWidths(row.Select(Weight).ToList(), width, HorizontalSpacing)
            .ToList();

    private static int Weight(UIElement child) =>
        child is ContentPresenter { Content: TopologyNodeViewModel viewModel }
            ? Math.Max(1, viewModel.LayoutWeight)
            : 1;

    private static double NormalizeWidth(double value) =>
        double.IsInfinity(value) || double.IsNaN(value) ? 1200 : Math.Max(1, value);
}
