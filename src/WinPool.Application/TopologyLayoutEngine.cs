namespace WinPool.Application;

public sealed record TopologyLayoutInput(
    bool ShowHeader,
    bool IsExpanded,
    TopologyChildrenLayout ChildrenLayout,
    IReadOnlyList<TopologyLayoutInput> Children);

public sealed record TopologyLayoutResult(
    int UnitWidth,
    int UnitHeight,
    double PixelWidth,
    int FlowColumns,
    IReadOnlyList<IReadOnlyList<int>> Rows,
    IReadOnlyList<TopologyLayoutResult> Children);

public static class TopologyLayoutEngine
{
    public const int LeafMinWidth = 112;
    public const int AncestorChrome = 26;
    public const int SiblingSpacing = 6;
    public const int MinimumSiblingUnitWidth = 2;

    public static int RelaxedRowHeightCap(int rowHeight)
    {
        var height = Math.Max(1, rowHeight);
        return Math.Max(height + 1, (int)Math.Ceiling(height * 1.3));
    }

    public static TopologyLayoutResult Layout(TopologyLayoutInput root, double availableWidth)
    {
        ArgumentNullException.ThrowIfNull(root);
        var avail = Math.Max(1, availableWidth);
        if (!root.IsExpanded || root.Children.Count == 0)
        {
            return MeasureSubtree(root, int.MaxValue).ToResult();
        }

        if (root.ChildrenLayout == TopologyChildrenLayout.WeightedFlow)
        {
            return PackSiblings(root, avail).ToResult();
        }

        var measured = MeasureSubtree(root, int.MaxValue);
        if (measured.PixelWidth > avail)
        {
            return ShrinkToFit(root, avail).ToResult();
        }

        return measured.ToResult();
    }

    private static Node PackSiblings(TopologyLayoutInput root, double availableWidth)
    {
        var remaining = root.Children.ToList();
        var ordered = new Node[root.Children.Count];
        var rows = new List<List<int>>();
        var nextIndex = 0;

        while (remaining.Count > 0)
        {
            var rowInputs = new List<(int Index, TopologyLayoutInput Input)>
            {
                (nextIndex, remaining[0])
            };
            remaining.RemoveAt(0);
            nextIndex++;

            while (remaining.Count > 0)
            {
                var trial = rowInputs
                    .Select(item => item.Input)
                    .Append(remaining[0])
                    .ToList();
                if (TryPlace(trial, availableWidth) is null)
                {
                    break;
                }

                rowInputs.Add((nextIndex, remaining[0]));
                remaining.RemoveAt(0);
                nextIndex++;
            }

            var rowNodes = TryPlace(
                rowInputs.Select(item => item.Input).ToList(),
                availableWidth)
                ?? Equalize(rowInputs.Select(item => item.Input).ToList());

            var row = new List<int>();
            for (var i = 0; i < rowInputs.Count; i++)
            {
                ordered[rowInputs[i].Index] = rowNodes[i];
                row.Add(rowInputs[i].Index);
            }

            rows.Add(row);
        }

        var children = ordered.ToList();
        var unitWidth = 1;
        var unitHeight = root.ShowHeader ? 1 : 0;
        var pixelWidth = 0d;
        foreach (var row in rows)
        {
            var rowUnitWidth = 0;
            var rowHeight = 1;
            var rowPixel = 0d;
            for (var i = 0; i < row.Count; i++)
            {
                var child = children[row[i]];
                rowUnitWidth += child.UnitWidth;
                rowHeight = Math.Max(rowHeight, child.UnitHeight);
                rowPixel += child.PixelWidth;
                if (i > 0)
                {
                    rowPixel += SiblingSpacing;
                }
            }

            unitWidth = Math.Max(unitWidth, rowUnitWidth);
            unitHeight += rowHeight;
            pixelWidth = Math.Max(pixelWidth, rowPixel);
        }

        return new Node
        {
            UnitWidth = unitWidth,
            UnitHeight = unitHeight,
            PixelWidth = Math.Max(LeafMinWidth, pixelWidth),
            FlowColumns = rows.Count == 0 ? 1 : rows.Max(row => row.Count),
            Rows = rows,
            Children = children
        };
    }

    private static List<Node>? TryPlace(
        IReadOnlyList<TopologyLayoutInput> inputs,
        double availableWidth)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        if (inputs.Count == 1)
        {
            var single = Equalize(inputs);
            if (single[0].PixelWidth > availableWidth)
            {
                return [ShrinkToFit(inputs[0], availableWidth)];
            }

            return single;
        }

        var equalized = Equalize(inputs);
        if (RowPixelWidth(equalized) <= availableWidth)
        {
            return equalized;
        }

        return TryRelaxToFit(inputs, equalized, availableWidth);
    }

    private static List<Node>? TryRelaxToFit(
        IReadOnlyList<TopologyLayoutInput> inputs,
        IReadOnlyList<Node> equalized,
        double availableWidth)
    {
        var cap = RelaxedRowHeightCap(equalized.Max(node => node.UnitHeight));
        var naturals = inputs.Select(input => MeasureSubtree(input, int.MaxValue)).ToList();
        var budgets = equalized.Select(node => Math.Max(1, node.UnitWidth)).ToArray();
        var nodes = equalized.ToList();

        while (RowPixelWidth(nodes) > availableWidth)
        {
            var best = -1;
            for (var i = 0; i < inputs.Count; i++)
            {
                if (budgets[i] <= MinimumBudget(inputs[i], naturals[i].UnitWidth, inputs.Count))
                {
                    continue;
                }

                var candidate = MeasureSubtree(inputs[i], budgets[i] - 1);
                if (candidate.UnitHeight > cap)
                {
                    continue;
                }

                if (best < 0 || PreferShrink(inputs, nodes, i, best))
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                return null;
            }

            budgets[best]--;
            nodes[best] = MeasureSubtree(inputs[best], budgets[best]);
        }

        return nodes;
    }

    private static List<Node> Equalize(IReadOnlyList<TopologyLayoutInput> inputs)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        var naturals = inputs.Select(input => MeasureSubtree(input, int.MaxValue)).ToList();
        var rowHeight = naturals.Max(node => node.UnitHeight);
        var results = new List<Node>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var chosen = naturals[i];
            var maxWidth = Math.Max(1, naturals[i].UnitWidth);
            var minBudget = MinimumBudget(inputs[i], maxWidth, inputs.Count);
            for (var budget = minBudget; budget <= maxWidth; budget++)
            {
                var candidate = MeasureSubtree(inputs[i], budget);
                if (candidate.UnitHeight <= rowHeight)
                {
                    chosen = candidate;
                    break;
                }
            }

            results.Add(chosen);
        }

        return results;
    }

    private static int MinimumBudget(
        TopologyLayoutInput input,
        int naturalWidth,
        int siblingCount)
    {
        if (siblingCount <= 1 || !RequiresMinimumSiblingWidth(input))
        {
            return 1;
        }

        return Math.Min(MinimumSiblingUnitWidth, Math.Max(1, naturalWidth));
    }

    private static bool PreferShrink(
        IReadOnlyList<TopologyLayoutInput> inputs,
        IReadOnlyList<Node> nodes,
        int candidate,
        int current)
    {
        var candidatePartitioned = InnerFlowsWrapPartitionedDisks(inputs[candidate]);
        var currentPartitioned = InnerFlowsWrapPartitionedDisks(inputs[current]);
        if (candidatePartitioned != currentPartitioned)
        {
            return !candidatePartitioned;
        }

        if (nodes[candidate].UnitWidth != nodes[current].UnitWidth)
        {
            return nodes[candidate].UnitWidth > nodes[current].UnitWidth;
        }

        return nodes[candidate].PixelWidth > nodes[current].PixelWidth;
    }

    private static bool RequiresMinimumSiblingWidth(TopologyLayoutInput input) =>
        HasHeaderedFlowChild(input) || InnerFlowsWrapPartitionedDisks(input);

    private static bool HasHeaderedFlowChild(TopologyLayoutInput input) =>
        input.Children.Any(child =>
            child.ShowHeader
            && child.IsExpanded
            && child.ChildrenLayout is TopologyChildrenLayout.Flow
                or TopologyChildrenLayout.WeightedFlow
            && child.Children.Count > 0);

    private static bool InnerFlowsWrapPartitionedDisks(TopologyLayoutInput input)
    {
        if (!input.IsExpanded || input.Children.Count == 0)
        {
            return false;
        }

        if (input.ChildrenLayout is TopologyChildrenLayout.Flow
            or TopologyChildrenLayout.WeightedFlow)
        {
            return EnumerateFlowItems(input).Any(item => item.Children.Count > 0);
        }

        return input.Children.Any(InnerFlowsWrapPartitionedDisks);
    }

    private static IEnumerable<TopologyLayoutInput> EnumerateFlowItems(TopologyLayoutInput flow)
    {
        foreach (var child in flow.Children)
        {
            if (!child.ShowHeader)
            {
                foreach (var nested in child.Children)
                {
                    yield return nested;
                }

                continue;
            }

            yield return child;
        }
    }

    private static Node ShrinkToFit(TopologyLayoutInput input, double availableWidth)
    {
        var natural = MeasureSubtree(input, int.MaxValue);
        var chosen = MeasureSubtree(input, 1);
        for (var budget = natural.UnitWidth; budget >= 1; budget--)
        {
            var candidate = MeasureSubtree(input, budget);
            if (candidate.PixelWidth <= availableWidth)
            {
                return candidate;
            }
        }

        return chosen;
    }

    private static double RowPixelWidth(IReadOnlyList<Node> row)
    {
        if (row.Count == 0)
        {
            return 0;
        }

        return row.Sum(node => node.PixelWidth) + (SiblingSpacing * (row.Count - 1));
    }

    private static Node MeasureSubtree(TopologyLayoutInput input, int columnBudget)
    {
        if (!input.IsExpanded || input.Children.Count == 0)
        {
            return Leaf();
        }

        if (input.ChildrenLayout == TopologyChildrenLayout.Stack)
        {
            var children = input.Children
                .Select(child => MeasureSubtree(child, columnBudget))
                .ToList();
            return new Node
            {
                UnitWidth = Math.Max(1, children.Max(child => child.UnitWidth)),
                UnitHeight = (input.ShowHeader ? 1 : 0) + children.Sum(child => child.UnitHeight),
                PixelWidth = AncestorChrome + children.Max(child => child.PixelWidth),
                FlowColumns = 0,
                Rows = [],
                Children = children
            };
        }

        var n = input.Children.Count;
        var columns = Math.Clamp(columnBudget, 1, n);
        var measured = input.Children
            .Select(child => MeasureSubtree(child, columnBudget))
            .ToList();
        var rows = new List<List<int>>();
        for (var start = 0; start < n; start += columns)
        {
            var count = Math.Min(columns, n - start);
            rows.Add(Enumerable.Range(start, count).ToList());
        }

        var unitWidth = 1;
        var contentHeight = 0;
        var innerPixel = 0d;
        foreach (var row in rows)
        {
            var rowUnitWidth = 0;
            var rowHeight = 1;
            var rowPixel = 0d;
            for (var i = 0; i < row.Count; i++)
            {
                var child = measured[row[i]];
                rowUnitWidth += child.UnitWidth;
                rowHeight = Math.Max(rowHeight, child.UnitHeight);
                rowPixel += child.PixelWidth;
                if (i > 0)
                {
                    rowPixel += SiblingSpacing;
                }
            }

            unitWidth = Math.Max(unitWidth, rowUnitWidth);
            contentHeight += rowHeight;
            innerPixel = Math.Max(innerPixel, rowPixel);
        }

        return new Node
        {
            UnitWidth = unitWidth,
            UnitHeight = (input.ShowHeader ? 1 : 0) + contentHeight,
            PixelWidth = AncestorChrome + innerPixel,
            FlowColumns = columns,
            Rows = rows,
            Children = measured
        };
    }

    private static Node Leaf() =>
        new()
        {
            UnitWidth = 1,
            UnitHeight = 1,
            PixelWidth = LeafMinWidth,
            FlowColumns = 0,
            Rows = [],
            Children = []
        };

    private sealed class Node
    {
        public int UnitWidth { get; init; }
        public int UnitHeight { get; init; }
        public double PixelWidth { get; init; }
        public int FlowColumns { get; init; }
        public List<List<int>> Rows { get; init; } = [];
        public List<Node> Children { get; init; } = [];

        public TopologyLayoutResult ToResult() =>
            new(
                UnitWidth,
                UnitHeight,
                PixelWidth,
                FlowColumns,
                Rows.Select(row => (IReadOnlyList<int>)row).ToList(),
                Children.Select(child => child.ToResult()).ToList());
    }
}
