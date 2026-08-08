using System.Globalization;
using System.Text.RegularExpressions;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Testing;

/// <summary>
/// Assigns comparable semantic identities without changing or discarding the
/// original tool metric. Exact comparison requires the same canonical id,
/// canonical unit and workload key; unknown cache/profile context stays fenced.
/// </summary>
public static partial class TestMetricSemanticsCatalog
{
    public static readonly AlgorithmIdentity Algorithm = new(
        "ALG-METRIC-003",
        "1.0.0",
        AlgorithmConfidence.Derived,
        "Plan/04 §8 cross-tool semantics");

    public static TestMetricSemantic Describe(
        TestStep? step,
        string metricId,
        string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricId);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        var normalizedUnit = CanonicalUnit(unit);
        if (step?.Workload is { } workload)
        {
            var direction = workload.WritePercentage switch
            {
                0 => "read",
                100 => "write",
                _ => "mixed"
            };
            var workloadKey = WorkloadKey(
                direction,
                workload.AccessPattern,
                workload.BlockSizeBytes,
                workload.QueueDepth,
                workload.ThreadCount,
                workload.SoftwareCache,
                workload.WriteThrough);
            if (metricId is "throughput.total")
            {
                return Semantic(
                    $"throughput.{direction}",
                    "MiB/s",
                    workloadKey,
                    "median-across-runs",
                    normalizedUnit is "MiB/s",
                    normalizedUnit is "MiB/s"
                        ? null
                        : "metric.unit_conversion_required");
            }

            if (metricId is "iops.total")
            {
                return Semantic(
                    $"iops.{direction}",
                    "IOPS",
                    workloadKey,
                    "median-across-runs",
                    normalizedUnit is "IOPS",
                    normalizedUnit is "IOPS"
                        ? null
                        : "metric.unit_conversion_required");
            }

            if (metricId.StartsWith("latency.", StringComparison.Ordinal))
            {
                var suffix = metricId["latency.".Length..];
                var operationPrefix = suffix.StartsWith("read.", StringComparison.Ordinal)
                    ? "read."
                    : suffix.StartsWith("write.", StringComparison.Ordinal)
                        ? "write."
                        : string.Empty;
                var operationDirection = operationPrefix.Length == 0
                    ? direction
                    : operationPrefix[..^1];
                var statistic = operationPrefix.Length == 0
                    ? suffix
                    : suffix[operationPrefix.Length..];
                return Semantic(
                    $"latency.{operationDirection}.{statistic}",
                    "ms",
                    workloadKey,
                    statistic.StartsWith("p", StringComparison.Ordinal)
                        ? "median-of-run-percentiles"
                        : "median-across-runs",
                    normalizedUnit is "ms",
                    normalizedUnit is "ms"
                        ? null
                        : "metric.unit_conversion_required");
            }
        }

        if (step?.Action is TestActionKind.Copy
            && metricId is "throughput.total")
        {
            return Semantic(
                "throughput.copy",
                "MiB/s",
                "copy;dataset=unknown;cache=unknown",
                "median-across-runs",
                false,
                "metric.copy_context_required");
        }

        return Semantic(
            metricId,
            normalizedUnit,
            "context=unknown",
            "source-defined",
            false,
            "metric.context_unknown");
    }

    public static TestMetricSemantic DescribeLegacy(
        string metricId,
        string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricId);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        var benchmark = DiteBenchmarkMetric().Match(metricId);
        if (benchmark.Success)
        {
            var direction = benchmark.Groups["direction"].Value.ToLowerInvariant();
            var pattern = benchmark.Groups["pattern"].Value.Equals(
                "SEQ",
                StringComparison.OrdinalIgnoreCase)
                ? IoAccessPattern.Sequential
                : IoAccessPattern.Random;
            var blockBytes = ParseSize(benchmark.Groups["block"].Value);
            var queue = int.Parse(
                benchmark.Groups["queue"].Value,
                CultureInfo.InvariantCulture);
            var threads = int.Parse(
                benchmark.Groups["threads"].Value,
                CultureInfo.InvariantCulture);
            return Semantic(
                $"throughput.{direction}",
                "MiB/s",
                WorkloadKey(
                    direction,
                    pattern,
                    blockBytes,
                    queue,
                    threads,
                    null,
                    null),
                "legacy-median-min-max",
                false,
                "metric.legacy_cache_profile_unknown");
        }

        return metricId switch
        {
            "Speed_MiB/s" => Semantic(
                "throughput.copy",
                "MiB/s",
                "copy;dataset=unknown;cache=unknown",
                "legacy-median-min-max",
                false,
                "metric.copy_context_required"),
            "FileCount" => Semantic(
                "files.completed",
                "count",
                "copy;dataset=unknown",
                "legacy-median-min-max",
                false,
                "metric.copy_context_required"),
            "DataSize_GiB" => Semantic(
                "bytes.completed",
                "GiB",
                "copy;dataset=unknown",
                "legacy-median-min-max",
                false,
                "metric.copy_context_required"),
            "Duration_s" => Semantic(
                "duration.measured",
                "s",
                "copy;dataset=unknown",
                "legacy-median-min-max",
                false,
                "metric.copy_context_required"),
            _ => Semantic(
                metricId,
                CanonicalUnit(unit),
                "legacy;context=unknown",
                "legacy-median-min-max",
                false,
                "metric.legacy_unknown")
        };
    }

    public static bool CanCompare(
        TestMetricSemantic first,
        TestMetricSemantic second) =>
        first.ComparableAcrossTools
        && second.ComparableAcrossTools
        && StringComparer.Ordinal.Equals(
            first.CanonicalMetricId,
            second.CanonicalMetricId)
        && StringComparer.Ordinal.Equals(
            first.CanonicalUnit,
            second.CanonicalUnit)
        && StringComparer.Ordinal.Equals(first.WorkloadKey, second.WorkloadKey);

    private static TestMetricSemantic Semantic(
        string id,
        string unit,
        string workload,
        string aggregation,
        bool comparable,
        string? limitation = null) =>
        new(id, unit, workload, aggregation, comparable, limitation);

    private static string WorkloadKey(
        string direction,
        IoAccessPattern pattern,
        int blockBytes,
        int queueDepth,
        int threads,
        SoftwareCacheMode? softwareCache,
        WriteThroughMode? writeThrough) =>
        string.Join(
            ';',
            $"direction={direction}",
            $"pattern={pattern.ToString().ToLowerInvariant()}",
            $"block={blockBytes}",
            $"queue={queueDepth}",
            $"threads={threads}",
            $"software-cache={softwareCache?.ToString().ToLowerInvariant() ?? "unknown"}",
            $"write-through={writeThrough?.ToString().ToLowerInvariant() ?? "unknown"}");

    private static string CanonicalUnit(string unit) => unit.Trim() switch
    {
        "MiB/s" => "MiB/s",
        "IOPS" or "iops" => "IOPS",
        "milliseconds" or "ms" => "ms",
        "bytes" or "B" => "B",
        "seconds" or "s" => "s",
        _ => unit.Trim()
    };

    private static int ParseSize(string value)
    {
        var suffix = char.ToUpperInvariant(value[^1]);
        var number = int.Parse(value[..^1], CultureInfo.InvariantCulture);
        return checked(number * (suffix == 'M' ? 1024 * 1024 : 1024));
    }

    [GeneratedRegex(
        "^(?<direction>Read|Write)_(?<pattern>SEQ|RND)(?<block>[0-9]+[KM])_Q(?<queue>[0-9]+)T(?<threads>[0-9]+)_MiB/s$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DiteBenchmarkMetric();
}
