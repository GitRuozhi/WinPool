using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Monitoring.Tests;

public sealed class MonitoringAlgorithmTests
{
    [Fact]
    public void Clock_UsesMonotonicElapsed_WhenUtcMovesBackwards()
    {
        var fake = new FakeClock
        {
            Frequency = 1_000,
            Timestamp = 10_000,
            UtcNow = DateTimeOffset.Parse("2026-07-29T10:00:00Z")
        };
        var clock = new MonotonicSampleClock(fake);

        Assert.Equal(TimeSpan.Zero, clock.Capture().ElapsedSincePrevious);
        fake.Timestamp = 10_250;
        fake.UtcNow = DateTimeOffset.Parse("2026-07-29T09:00:00Z");

        var second = clock.Capture();

        Assert.Equal(TimeSpan.FromMilliseconds(250), second.ElapsedSincePrevious);
        Assert.Equal(fake.UtcNow, second.PersistedUtc);
    }

    [Fact]
    public void LatestWindow_DropsOldest_AndCountsDrops()
    {
        var window = new LatestMonitorWindow(2);
        var samples = Enumerable.Range(0, 3).Select(CreateSample).ToArray();

        foreach (var sample in samples)
        {
            window.Add(sample);
        }

        Assert.Equal(1, window.DroppedSamples);
        Assert.Equal(samples.Skip(1), window.Snapshot());
    }

    [Theory]
    [InlineData(102_400, 200_000)]
    [InlineData(210_000, 500_000)]
    [InlineData(900_000, 1_000_000)]
    public void FriendlyCeiling_UsesOneTwoFiveSteps(double value, double expected)
    {
        Assert.Equal(expected, MonitorScale.FriendlyCeiling(value));
    }

    [Fact]
    public void Scale_ShrinksOnlyAfterHysteresisThreshold()
    {
        var scale = new MonitorScale();
        Assert.Equal(1_000_000, scale.Update([900_000]));
        Assert.Equal(1_000_000, scale.Update([500_000]));
        Assert.Equal(200_000, scale.Update([150_000]));
        Assert.Contains("推测", MonitorScale.ConfidenceLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollup_PreservesStatisticsMissingCountAndIntegral()
    {
        var start = DateTimeOffset.Parse("2026-07-29T10:00:00Z");
        var rollup = MonitorRollupCalculator.Calculate(
            start,
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(1),
            [
                (start, 0d),
                (start.AddSeconds(1), 2d),
                (start.AddSeconds(3), 4d)
            ]);

        Assert.Equal(0, rollup.First);
        Assert.Equal(4, rollup.Last);
        Assert.Equal(2, rollup.ArithmeticMean);
        Assert.Equal(3, rollup.SampleCount);
        Assert.Equal(1, rollup.MissingCount);
        Assert.Equal(7, rollup.TimeIntegral);
    }

    [Fact]
    public void EmptyRollup_ReportsAllExpectedSamplesMissing()
    {
        var rollup = MonitorRollupCalculator.Calculate(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(500),
            []);

        Assert.Equal(0, rollup.SampleCount);
        Assert.Equal(4, rollup.MissingCount);
    }

    private static MonitorSample CreateSample(int index)
    {
        var system = new SystemId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        return new MonitorSample(
            new SessionId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new StorageObjectId(system, StorageObjectKind.PhysicalDisk, $"disk-{index}"),
            DateTimeOffset.UnixEpoch.AddSeconds(index),
            []);
    }

    private static MonitorSample CreateStorageSample(
        int index,
        DateTimeOffset sampledAtUtc,
        double activity,
        double queue,
        double readBytes,
        double writeBytes)
    {
        var system = new SystemId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        return new(
            new SessionId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new StorageObjectId(system, StorageObjectKind.PhysicalDisk, $"disk-{index}"),
            sampledAtUtc,
            [
                new(MonitorMetricKind.ActiveTimePercent, activity),
                new(MonitorMetricKind.AverageQueueLength, queue),
                new(MonitorMetricKind.ReadBytesPerSecond, readBytes),
                new(MonitorMetricKind.WriteBytesPerSecond, writeBytes)
            ]);
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public long Timestamp { get; set; }
        public long Frequency { get; set; }
        public DateTimeOffset UtcNow { get; set; }
    }
}
