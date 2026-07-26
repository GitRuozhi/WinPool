namespace WinPool.Core;

public static class WeightedPoolLayout
{
    public static IReadOnlyList<IReadOnlyList<int>> CreateRows(
        IReadOnlyList<int> weights,
        double availableWidth,
        double slotWidth = 150,
        double spacing = 6)
    {
        var width = Math.Max(1, availableWidth);
        var rows = new List<IReadOnlyList<int>>();
        var current = new List<int>();
        var required = 0d;

        for (var index = 0; index < weights.Count; index++)
        {
            var weight = Math.Max(1, weights[index]);
            var itemWidth = (weight * slotWidth) + (Math.Max(0, weight - 1) * spacing);
            var next = current.Count == 0 ? itemWidth : required + spacing + itemWidth;
            if (current.Count > 0 && next > width)
            {
                rows.Add(current);
                current = [];
                required = 0;
            }

            current.Add(index);
            required = required == 0 ? itemWidth : required + spacing + itemWidth;
        }

        if (current.Count > 0)
        {
            rows.Add(current);
        }

        return rows;
    }

    public static IReadOnlyList<double> AllocateWidths(
        IReadOnlyList<int> weights,
        double availableWidth,
        double spacing = 6)
    {
        var usable = Math.Max(0, availableWidth - Math.Max(0, weights.Count - 1) * spacing);
        var normalized = weights.Select(weight => Math.Max(1, weight)).ToList();
        var total = Math.Max(1, normalized.Sum());
        return normalized.Select(weight => usable * weight / total).ToList();
    }
}
