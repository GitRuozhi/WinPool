namespace WinPool.Application;

public sealed record EqualFillFlowRow(int StartIndex, int Count, double ItemWidth);

public static class EqualFillFlowLayout
{
    public static IReadOnlyList<EqualFillFlowRow> CreateRows(
        int itemCount,
        double availableWidth,
        double minimumItemWidth = 150,
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
        var rows = new List<EqualFillFlowRow>();
        for (var start = 0; start < itemCount; start += itemsPerRow)
        {
            var count = Math.Min(itemsPerRow, itemCount - start);
            var usableWidth = Math.Max(1, width - (Math.Max(0, count - 1) * spacing));
            rows.Add(new EqualFillFlowRow(start, count, usableWidth / count));
        }

        return rows;
    }
}
