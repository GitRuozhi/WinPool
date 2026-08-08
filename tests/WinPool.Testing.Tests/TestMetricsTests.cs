using WinPool.Domain;
using WinPool.Testing;

namespace WinPool.Testing.Tests;

public sealed class TestMetricsTests
{
    [Fact]
    public void NormalizesThroughputIopsAndLatencyDistribution()
    {
        var metrics = TestMetrics.Normalize(
            measuredBytes: 20 * 1024 * 1024,
            completedOperations: 2_000,
            measuredDuration: TimeSpan.FromSeconds(2),
            latencyHistogram:
            [
                new(100_000, 50),
                new(200_000, 45),
                new(1_000_000, 5)
            ]);

        Assert.Equal(10, metrics.MebibytesPerSecond);
        Assert.Equal(1_000, metrics.OperationsPerSecond);
        Assert.Equal(100, metrics.P50Microseconds);
        Assert.Equal(200, metrics.P95Microseconds);
        Assert.Equal(1_000, metrics.P99Microseconds);
        Assert.Equal(AlgorithmConfidence.Derived, metrics.LatencyAlgorithm.Confidence);
    }

    [Fact]
    public void EmptyHistogramReturnsUnavailableRatherThanZero()
    {
        Assert.Null(TestMetrics.PercentileMicroseconds([], 0.99));
    }

    [Fact]
    public void RepeatAggregationUsesMedianIncludingForRunP99()
    {
        Assert.Equal(20, TestMetrics.Median([10, 30, 20]));
        Assert.Equal(25, TestMetrics.Median([10, 20, 30, 40]));

        var runs = new[]
        {
            TestMetrics.Normalize(1, 1, TimeSpan.FromSeconds(1), [new(100_000, 1)]),
            TestMetrics.Normalize(1, 1, TimeSpan.FromSeconds(1), [new(900_000, 1)]),
            TestMetrics.Normalize(1, 1, TimeSpan.FromSeconds(1), [new(300_000, 1)])
        };
        Assert.Equal(300, TestMetrics.MedianRunP99(runs));
    }
}
