using System.Text;

namespace WinPool.Application;

public sealed record ManageExportProperty(string Label, string Value);

public sealed record ManageExportColumn(
    string Name,
    IReadOnlyList<ManageExportProperty> Properties);

/// <summary>
/// Creates the frozen Manage comparison-table CSV shape: the first column is
/// the ordered union of property labels and every subsequent column is one
/// selected storage object.
/// </summary>
public static class ManageCategoryCsvExporter
{
    public static string Create(
        string nameHeader,
        IReadOnlyList<ManageExportColumn> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameHeader);
        ArgumentNullException.ThrowIfNull(columns);
        var labels = new List<string>();
        foreach (var property in columns.SelectMany(column => column.Properties))
        {
            if (!labels.Contains(property.Label, StringComparer.Ordinal))
            {
                labels.Add(property.Label);
            }
        }

        var builder = new StringBuilder();
        AppendRow(builder, [nameHeader, .. columns.Select(column => column.Name)]);
        foreach (var label in labels)
        {
            AppendRow(
                builder,
                [
                    label,
                    .. columns.Select(column =>
                        column.Properties.FirstOrDefault(property =>
                            property.Label.Equals(label, StringComparison.Ordinal))?.Value
                        ?? string.Empty)
                ]);
        }
        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, IEnumerable<string> values) =>
        builder.AppendLine(string.Join(",", values.Select(Escape)));

    private static string Escape(string value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"')
            || value.Contains('\n') || value.Contains('\r')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
    }
}
