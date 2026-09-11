namespace WinPool.Application;

public sealed record EqualFillFlowRow(int StartIndex, int Count, double ItemWidth);

public static class EqualFillFlowLayout
{
    public const double DefaultMinimumItemWidth = 150;

    public static IReadOnlyList<EqualFillFlowRow> CreateRows(
        int itemCount,
        double availableWidth,
        double minimumItemWidth = DefaultMinimumItemWidth,
        double spacing = 6)
    {
        if (itemCount <= 0)
        {
            return [];
        }

        var width = Math.Max(1, availableWidth);
        var itemsPerRow = Math.Max(
            1,
            (int)Math.Floor((width + spacing) / (Math.Max(1, minimumItemWidth) + spacing)));
        var rowCount = (int)Math.Ceiling((double)itemCount / itemsPerRow);
        var baseCount = itemCount / rowCount;
        var extraRows = itemCount % rowCount;
        var rows = new List<EqualFillFlowRow>();
        for (var rowIndex = 0, start = 0; rowIndex < rowCount; rowIndex++)
        {
            var count = baseCount + (rowIndex < extraRows ? 1 : 0);
            var usableWidth = Math.Max(1, width - (Math.Max(0, count - 1) * spacing));
            rows.Add(new EqualFillFlowRow(start, count, usableWidth / count));
            start += count;
        }

        return rows;
    }

    public static IReadOnlyList<EqualFillFlowRow> CreateRowsForColumnCount(
        int itemCount,
        int columns,
        double availableWidth,
        double spacing = 6)
    {
        if (itemCount <= 0)
        {
            return [];
        }

        var width = Math.Max(1, availableWidth);
        var itemsPerRow = Math.Max(1, columns);
        var rowCount = (int)Math.Ceiling((double)itemCount / itemsPerRow);
        var baseCount = itemCount / rowCount;
        var extraRows = itemCount % rowCount;
        var rows = new List<EqualFillFlowRow>();
        for (var rowIndex = 0, start = 0; rowIndex < rowCount; rowIndex++)
        {
            var count = baseCount + (rowIndex < extraRows ? 1 : 0);
            var usableWidth = Math.Max(1, width - (Math.Max(0, count - 1) * spacing));
            rows.Add(new EqualFillFlowRow(start, count, usableWidth / count));
            start += count;
        }

        return rows;
    }
}
