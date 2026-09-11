namespace WinPool.Application;

public sealed record TopologyLayoutInput(
    bool ShowHeader,
    bool IsExpanded,
    TopologyChildrenLayout ChildrenLayout,
    IReadOnlyList<TopologyLayoutInput> Children,
    bool NoWrapChildren = false,
    bool DistributeByCapacity = false,
    IReadOnlyList<double>? CapacityWeights = null);

public sealed record TopologyLayoutResult(
    int UnitWidth,
    int UnitHeight,
    double PixelWidth,
    int FlowColumns,
    IReadOnlyList<IReadOnlyList<int>> Rows,
    IReadOnlyList<TopologyLayoutResult> Children,
    IReadOnlyList<double> ChildWidths);

public static class TopologyLayoutEngine
{
    public const int LeafMinWidth = 112;
    public const int AncestorChrome = 26;
    public const int SiblingSpacing = 6;
    public const int MinimumSiblingUnitWidth = 2;
    public const int MinimumRelaxedRowHeight = 5;

    /// <summary>
    /// Equal-growth threshold t of the three-stage strip allocation: every
    /// child grows equally up to this width before capacity distribution
    /// starts. Indicative value pending the implementation screenshot
    /// confirmation (Plan §2.7).
    /// </summary>
    public const double EqualGrowthWidth = 200;

    public static int RelaxedRowHeightCap(int rowHeight)
    {
        var height = Math.Max(1, rowHeight);
        return Math.Max(
            MinimumRelaxedRowHeight,
            Math.Max(height + 1, (int)Math.Ceiling(height * 1.3)));
    }

    public static TopologyLayoutResult Layout(TopologyLayoutInput root, double availableWidth)
    {
        ArgumentNullException.ThrowIfNull(root);
        var avail = Math.Max(1, availableWidth);
        Node rootNode;
        if (!root.IsExpanded || root.Children.Count == 0)
        {
            rootNode = MeasureSubtree(root, int.MaxValue);
        }
        else if (root.ChildrenLayout == TopologyChildrenLayout.WeightedFlow)
        {
            rootNode = PackSiblings(root, avail);
        }
        else
        {
            var measured = MeasureSubtree(root, int.MaxValue);
            rootNode = measured.PixelWidth > avail
                ? ShrinkToFit(root, avail)
                : measured;
        }

        AllocateChildWidths(root, rootNode, avail);
        return rootNode.ToResult();
    }

    /// <summary>
    /// Assigns the final width of every child slot on the completed unit
    /// plan. The root container is the available width supplied by the
    /// calling surface adapter; every nested container is that node's
    /// assigned width minus the ancestor chrome, which reproduces the
    /// WinUI measure chain (card border padding plus items margin).
    /// With no declared metadata the distribution reproduces the previous
    /// panel arithmetic exactly: WeightedFlow slots stretch by unit
    /// weight, Flow rows fill equally, Stack children take the full width.
    /// </summary>
    private static void AllocateChildWidths(
        TopologyLayoutInput input,
        Node node,
        double containerWidth)
    {
        if (node.Children.Count == 0 || input.Children.Count != node.Children.Count)
        {
            node.ChildWidths = [];
            return;
        }

        var widths = new double[node.Children.Count];
        switch (input.ChildrenLayout)
        {
            case TopologyChildrenLayout.Stack:
                for (var i = 0; i < node.Children.Count; i++)
                {
                    widths[i] = Math.Max(1, containerWidth);
                    AllocateChildWidths(
                        input.Children[i],
                        node.Children[i],
                        Math.Max(1, containerWidth - AncestorChrome));
                }

                break;

            case TopologyChildrenLayout.WeightedFlow:
                foreach (var row in node.Rows)
                {
                    var spacing = SiblingSpacing * Math.Max(0, row.Count - 1);
                    var minSum = 0d;
                    var unitSum = 0d;
                    foreach (var index in row)
                    {
                        minSum += node.Children[index].PixelWidth;
                        unitSum += node.Children[index].UnitWidth;
                    }

                    unitSum = Math.Max(1, unitSum);
                    var extra = containerWidth - spacing - minSum;
                    foreach (var index in row)
                    {
                        var child = node.Children[index];
                        var stretch = extra > 0 ? extra * child.UnitWidth / unitSum : 0;
                        var width = Math.Max(1, child.PixelWidth + stretch);
                        widths[index] = width;
                        AllocateChildWidths(
                            input.Children[index],
                            child,
                            Math.Max(1, width - AncestorChrome));
                    }
                }

                break;

            default:
                foreach (var row in node.Rows)
                {
                    if (input.DistributeByCapacity
                        && input.CapacityWeights is { Count: > 0 })
                    {
                        AllocateCapacityStripRow(input, node, row, containerWidth, widths);
                        continue;
                    }

                    var usable = Math.Max(
                        1,
                        containerWidth - (Math.Max(0, row.Count - 1) * SiblingSpacing));
                    var itemWidth = usable / row.Count;
                    foreach (var index in row)
                    {
                        widths[index] = itemWidth;
                        AllocateChildWidths(
                            input.Children[index],
                            node.Children[index],
                            Math.Max(1, itemWidth - AncestorChrome));
                    }
                }

                break;
        }

        node.ChildWidths = widths;
    }

    /// <summary>
    /// Three-stage strip allocation (Plan §2.4): keep every child at its
    /// minimum while the row does not fit; grow all children equally until
    /// each reaches the comfort width; distribute any further width by the
    /// declared capacity weights. The row's last child absorbs the rounding
    /// residual; zero-weight children stop at the comfort width; a
    /// non-positive total weight falls back to an equal split.
    /// </summary>
    private static void AllocateCapacityStripRow(
        TopologyLayoutInput input,
        Node node,
        IReadOnlyList<int> row,
        double containerWidth,
        double[] widths)
    {
        var count = row.Count;
        var spacing = SiblingSpacing * Math.Max(0, count - 1);
        var minimumSum = 0d;
        for (var i = 0; i < count; i++)
        {
            minimumSum += node.Children[row[i]].PixelWidth;
        }

        var declaredWeights = input.CapacityWeights!;
        var weights = new double[count];
        var positiveWeightSum = 0d;
        for (var i = 0; i < count; i++)
        {
            var childIndex = row[i];
            var weight = childIndex < declaredWeights.Count
                ? Math.Max(0, declaredWeights[childIndex])
                : 0;
            weights[i] = weight;
            positiveWeightSum += weight;
        }

        void Assign(int position, double width)
        {
            var index = row[position];
            widths[index] = width;
            AllocateChildWidths(
                input.Children[index],
                node.Children[index],
                Math.Max(1, width - AncestorChrome));
        }

        var rowMinimum = minimumSum + spacing;
        if (containerWidth <= rowMinimum)
        {
            for (var i = 0; i < count; i++)
            {
                Assign(i, Math.Max(1, node.Children[row[i]].PixelWidth));
            }

            return;
        }

        var rowComfortable = (count * EqualGrowthWidth) + spacing;
        if (containerWidth <= rowComfortable)
        {
            var growth = (containerWidth - rowMinimum) / count;
            for (var i = 0; i < count; i++)
            {
                // Zero-weight children stay at the minimum through the
                // equal-growth stage (Plan §2.4).
                Assign(
                    i,
                    weights[i] > 0
                        ? node.Children[row[i]].PixelWidth + growth
                        : node.Children[row[i]].PixelWidth);
            }

            return;
        }

        var distributed = 0d;
        var weightSum = positiveWeightSum > 0 ? positiveWeightSum : count;
        for (var i = 0; i < count - 1; i++)
        {
            var weight = positiveWeightSum > 0 ? weights[i] : 1;
            var width = EqualGrowthWidth
                + ((containerWidth - rowComfortable) * weight / weightSum);
            Assign(i, width);
            distributed += width;
        }

        Assign(count - 1, Math.Max(EqualGrowthWidth, containerWidth - spacing - distributed));
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

        // Keep the minimum row count found by the existing feasibility pass,
        // then redistribute consecutive siblings as evenly as possible. This
        // stays inside the root layout plan and never measures UI elements.
        if (rows.Count > 1)
        {
            var baseCount = root.Children.Count / rows.Count;
            var extraRows = root.Children.Count % rows.Count;
            var balancedRows = new List<List<int>>();
            var balancedNodes = new Node[root.Children.Count];
            var balanced = true;
            var start = 0;
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var count = baseCount + (rowIndex < extraRows ? 1 : 0);
                var inputs = root.Children.Skip(start).Take(count).ToList();
                var placed = TryPlace(inputs, availableWidth);
                if (placed is null)
                {
                    balanced = false;
                    break;
                }

                var row = new List<int>(count);
                for (var i = 0; i < count; i++)
                {
                    balancedNodes[start + i] = placed[i];
                    row.Add(start + i);
                }
                balancedRows.Add(row);
                start += count;
            }

            if (balanced)
            {
                ordered = balancedNodes;
                rows = balancedRows;
            }
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
        var columns = input.NoWrapChildren
            ? Math.Max(1, n)
            : Math.Clamp(columnBudget, 1, n);
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
        public IReadOnlyList<double> ChildWidths { get; set; } = [];

        public TopologyLayoutResult ToResult() =>
            new(
                UnitWidth,
                UnitHeight,
                PixelWidth,
                FlowColumns,
                Rows.Select(row => (IReadOnlyList<int>)row).ToList(),
                Children.Select(child => child.ToResult()).ToList(),
                ChildWidths);
    }
}
