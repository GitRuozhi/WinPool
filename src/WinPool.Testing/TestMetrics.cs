using WinPool.Domain;

namespace WinPool.Testing;

public sealed record LatencyHistogramBucket(long UpperBoundNanoseconds, long Count);

public sealed record NormalizedIoMetrics(
    double MebibytesPerSecond,
    double OperationsPerSecond,
    double? P50Microseconds,
    double? P90Microseconds,
    double? P95Microseconds,
    double? P99Microseconds,
    double? P999Microseconds,
    AlgorithmIdentity ThroughputAlgorithm,
    AlgorithmIdentity LatencyAlgorithm);

public static class TestMetrics
{
    public static readonly AlgorithmIdentity ThroughputAlgorithm =
        new("ALG-METRIC-001", "1.0.0", AlgorithmConfidence.Proven, "docs/Archive/V0.2/04_外部工具测试监控与SQLite.md §8.1");

    public static readonly AlgorithmIdentity LatencyAlgorithm =
        new("ALG-METRIC-002", "1.0.0", AlgorithmConfidence.Derived, "docs/Archive/V0.2/04_外部工具测试监控与SQLite.md §8.2");

    public static readonly AlgorithmIdentity RepeatAlgorithm =
        new("ALG-REPEAT-001", "1.0.0", AlgorithmConfidence.Derived, "docs/Archive/V0.2/04_外部工具测试监控与SQLite.md §8.3");

    public static NormalizedIoMetrics Normalize(
        long measuredBytes,
        long completedOperations,
        TimeSpan measuredDuration,
        IReadOnlyList<LatencyHistogramBucket> latencyHistogram)
    {
        if (measuredBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(measuredBytes));
        }

        if (completedOperations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedOperations));
        }

        if (measuredDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(measuredDuration));
        }

        var seconds = measuredDuration.TotalSeconds;
        return new(
            measuredBytes / seconds / (1024d * 1024d),
            completedOperations / seconds,
            PercentileMicroseconds(latencyHistogram, 0.50),
            PercentileMicroseconds(latencyHistogram, 0.90),
            PercentileMicroseconds(latencyHistogram, 0.95),
            PercentileMicroseconds(latencyHistogram, 0.99),
            PercentileMicroseconds(latencyHistogram, 0.999),
            ThroughputAlgorithm,
            LatencyAlgorithm);
    }

    public static double? PercentileMicroseconds(
        IReadOnlyList<LatencyHistogramBucket> histogram,
        double percentile)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        if (percentile is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        if (histogram.Any(bucket => bucket.Count < 0 || bucket.UpperBoundNanoseconds < 0))
        {
            throw new ArgumentException("Latency histogram values cannot be negative.", nameof(histogram));
        }

        var ordered = histogram
            .OrderBy(bucket => bucket.UpperBoundNanoseconds)
            .ToArray();
        var total = ordered.Aggregate(
            0L,
            (current, bucket) => checked(current + bucket.Count));
        if (total == 0)
        {
            return null;
        }

        var rank = Math.Max(1L, (long)Math.Ceiling(total * percentile));
        var cumulative = 0L;
        foreach (var bucket in ordered)
        {
            cumulative = checked(cumulative + bucket.Count);
            if (cumulative >= rank)
            {
                return bucket.UpperBoundNanoseconds / 1_000d;
            }
        }

        throw new InvalidOperationException("The histogram rank could not be resolved.");
    }

    public static double Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || values.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("At least one finite value is required.", nameof(values));
        }

        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] / 2d) + (ordered[middle] / 2d);
    }

    public static double MedianRunP99(IReadOnlyList<NormalizedIoMetrics> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var p99Values = runs
            .Select(run => run.P99Microseconds)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        return Median(p99Values);
    }
}
