using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WinPool.Application;

namespace WinPool.Testing;

/// <summary>
/// Read-only importer for the bilingual wide CSV format produced by Dite V23/V24.
/// It never executes Dite, follows log paths, or touches referenced test files.
/// </summary>
public sealed class DiteLegacyResultImporter
{
    public const long MaximumSourceBytes = 64L * 1024 * 1024;
    public const int MaximumColumns = 4_096;
    public const int MaximumRuns = 200_000;
    private static readonly string[] RequiredColumns =
        ["TestTime", "Drive", "Tool", "Profile"];

    public async Task<DiteLegacyImportResult> ImportAsync(
        string csvPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);
        var path = Path.GetFullPath(csvPath);
        if (!string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only Dite CSV evidence can be imported.");
        }

        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > MaximumSourceBytes)
        {
            throw new InvalidDataException(
                "The Dite CSV is missing, empty, or exceeds the 64 MiB import limit.");
        }

        byte[] bytes;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            bytes = new byte[checked((int)info.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        var text = DecodeUtf8(bytes);
        var rows = ParseRows(text);
        if (rows.Count < 2 || rows.Count - 1 > MaximumRuns)
        {
            throw new InvalidDataException(
                "The Dite CSV has no data rows or exceeds the run-count limit.");
        }

        var headers = rows[0].Select(NormalizeHeader).ToArray();
        if (headers.Length > MaximumColumns
            || headers.Any(string.IsNullOrWhiteSpace)
            || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != headers.Length
            || RequiredColumns.Any(required =>
                !headers.Contains(required, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The Dite CSV header is incomplete or contains duplicate columns.");
        }

        var runs = new List<DiteLegacyRun>();
        foreach (var row in rows.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))
            {
                continue;
            }

            if (row.Count != headers.Length)
            {
                throw new InvalidDataException(
                    "A Dite CSV row does not match the header column count.");
            }

            var values = headers
                .Select((header, index) => (header, value: row[index].Trim()))
                .ToDictionary(
                    pair => pair.header,
                    pair => pair.value,
                    StringComparer.OrdinalIgnoreCase);
            var metrics = new List<DiteLegacyMetric>();
            foreach (var pair in values)
            {
                if (RequiredColumns.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)
                    || string.Equals(
                        pair.Key,
                        "LogFileName",
                        StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                if (!double.TryParse(
                        pair.Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value)
                    || !double.IsFinite(value))
                {
                    continue;
                }

                metrics.Add(new(pair.Key, value, Unit(pair.Key)));
            }

            runs.Add(
                new(
                    values["TestTime"],
                    values["Drive"],
                    values["Tool"],
                    values["Profile"],
                    values.GetValueOrDefault("LogFileName"),
                    metrics));
        }

        if (runs.Count == 0)
        {
            throw new InvalidDataException("The Dite CSV has no importable runs.");
        }

        var summaries = runs
            .SelectMany(run => run.Metrics)
            .GroupBy(metric => new { metric.MetricId, metric.Unit })
            .Select(group =>
            {
                var values = group.Select(item => item.Value)
                    .OrderBy(value => value)
                    .ToArray();
                return new DiteLegacyMetricSummary(
                    group.Key.MetricId,
                    group.Key.Unit,
                    values.Length,
                    values[0],
                    Median(values),
                    values[^1],
                    TestMetricSemanticsCatalog.DescribeLegacy(
                        group.Key.MetricId,
                        group.Key.Unit));
            })
            .OrderBy(item => item.MetricId, StringComparer.Ordinal)
            .ToArray();
        return new(
            Path.GetFileName(path),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            runs,
            summaries);
    }

    internal static IReadOnlyList<IReadOnlyList<string>> ParseRows(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row.ToArray());
                    row.Clear();
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("The Dite CSV contains an unterminated quoted field.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(
                bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })
                    ? bytes.AsSpan(3)
                    : bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The Dite CSV is not valid UTF-8/UTF-8-BOM.",
                exception);
        }
    }

    private static string NormalizeHeader(string header)
    {
        var value = header.Trim();
        var separator = value.LastIndexOf(" | ", StringComparison.Ordinal);
        return separator >= 0 ? value[(separator + 3)..].Trim() : value;
    }

    private static string Unit(string metricId) =>
        metricId.EndsWith("_MiB/s", StringComparison.OrdinalIgnoreCase)
            ? "MiB/s"
            : metricId.EndsWith("_GiB", StringComparison.OrdinalIgnoreCase)
                ? "GiB"
                : metricId.EndsWith("_s", StringComparison.OrdinalIgnoreCase)
                    ? "s"
                    : metricId.Contains("Count", StringComparison.OrdinalIgnoreCase)
                        ? "count"
                        : "value";

    private static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2d;
}
