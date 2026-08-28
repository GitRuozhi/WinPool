using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinPool.App.Services;
using WinPool.App.ViewModels;
using WinPool.Application;

namespace WinPool_App.Controls;

public sealed class WeightedPoolPanel : Panel
{
    public double HorizontalSpacing { get; set; } = TopologyLayoutEngine.SiblingSpacing;
    public double VerticalSpacing { get; set; } = TopologyLayoutEngine.SiblingSpacing;

    protected override Size MeasureOverride(Size availableSize)
    {
        var plan = PlanLayout(availableSize.Width);
        var children = Children.Cast<UIElement>().ToList();
        if (children.Count == 0)
        {
            return new Size(plan.AvailableWidth, 0);
        }

        var height = 0d;
        var neededWidth = 0d;
        foreach (var row in plan.Rows)
        {
            var rowHeight = 0d;
            var rowWidth = 0d;
            for (var i = 0; i < row.Count; i++)
            {
                var child = children[row[i].Index];
                child.Measure(new Size(row[i].Width, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                rowWidth += row[i].Width;
                if (i > 0)
                {
                    rowWidth += HorizontalSpacing;
                }
            }

            height += rowHeight;
            neededWidth = Math.Max(neededWidth, rowWidth);
            if (!ReferenceEquals(row, plan.Rows[^1]))
            {
                height += VerticalSpacing;
            }
        }

        return new Size(Math.Max(plan.AvailableWidth, neededWidth), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var plan = PlanLayout(finalSize.Width);
        var children = Children.Cast<UIElement>().ToList();
        var y = 0d;
        var arranged = new bool[children.Count];
        foreach (var row in plan.Rows)
        {
            var rowHeight = 0d;
            foreach (var slot in row)
            {
                rowHeight = Math.Max(rowHeight, children[slot.Index].DesiredSize.Height);
            }

            var x = 0d;
            for (var i = 0; i < row.Count; i++)
            {
                var slot = row[i];
                var width = i == row.Count - 1
                    ? Math.Max(0, Math.Max(plan.AvailableWidth, finalSize.Width) - x)
                    : slot.Width;
                children[slot.Index].Arrange(new Rect(x, y, width, rowHeight));
                arranged[slot.Index] = true;
                x += width + HorizontalSpacing;
            }

            y += rowHeight + VerticalSpacing;
        }

        for (var i = 0; i < children.Count; i++)
        {
            if (!arranged[i])
            {
                children[i].Arrange(new Rect(0, 0, 0, 0));
            }
        }

        return finalSize;
    }

    private LayoutPlan PlanLayout(double availableWidth)
    {
        var owner = TopologyVisualTree.FindOwnerViewModel(this);
        var width = ResolveAvailableWidth(availableWidth, owner);
        if (owner is null || !owner.IsExpanded || owner.Children.Count == 0)
        {
            return new LayoutPlan(width, []);
        }

        var result = TopologyLayoutEngine.Layout(
            TopologyLayoutMapper.FromViewModel(owner),
            width);
        owner.ApplyLayout(result);
        var slots = AllocateRows(result, width);
        return new LayoutPlan(width, slots);
    }

    private static List<List<RowSlot>> AllocateRows(TopologyLayoutResult result, double availableWidth)
    {
        var rows = new List<List<RowSlot>>();
        foreach (var row in result.Rows)
        {
            var nodes = row.Select(index => result.Children[index]).ToList();
            var spacing = TopologyLayoutEngine.SiblingSpacing * Math.Max(0, nodes.Count - 1);
            var mins = nodes.Select(node => node.PixelWidth).ToList();
            var minSum = mins.Sum();
            var extra = availableWidth - spacing - minSum;
            var unitSum = Math.Max(1, nodes.Sum(node => node.UnitWidth));
            var slots = new List<RowSlot>();
            for (var i = 0; i < row.Count; i++)
            {
                var stretch = extra > 0 ? extra * nodes[i].UnitWidth / unitSum : 0;
                slots.Add(new RowSlot(row[i], Math.Max(1, mins[i] + stretch)));
            }

            rows.Add(slots);
        }

        return rows;
    }

    private static double ResolveAvailableWidth(double availableWidth, TopologyNodeViewModel? owner)
    {
        if (!double.IsInfinity(availableWidth) && !double.IsNaN(availableWidth) && availableWidth > 1)
        {
            return availableWidth;
        }

        var viewport = owner?.HostViewportWidth ?? 1400;
        return Math.Max(1, viewport);
    }

    private sealed record RowSlot(int Index, double Width);

    private sealed record LayoutPlan(double AvailableWidth, List<List<RowSlot>> Rows);
}
