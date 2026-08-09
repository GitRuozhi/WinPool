namespace WinPool.Monitoring;

public sealed record MonitorRollup(
    DateTimeOffset BucketStartUtc,
    DateTimeOffset BucketEndUtc,
    double First,
    double Last,
    double Minimum,
    double Maximum,
    double ArithmeticMean,
    int SampleCount,
    int MissingCount,
    double TimeIntegral);

/// <summary>
/// ALG-ROLLUP-001: summarizes a regular time bucket without replacing raw data.
/// The time integral uses the trapezoidal rule between present samples.
/// </summary>
public static class MonitorRollupCalculator
{
    public static MonitorRollup Calculate(
        DateTimeOffset bucketStartUtc,
        TimeSpan bucketDuration,
        TimeSpan expectedInterval,
        IReadOnlyList<(DateTimeOffset TimestampUtc, double Value)> samples)
    {
        if (bucketDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketDuration));
        }

        if (expectedInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedInterval));
        }

        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            var expectedEmpty = ExpectedCount(bucketDuration, expectedInterval);
            return new MonitorRollup(
                bucketStartUtc,
                bucketStartUtc + bucketDuration,
                0,
                0,
                0,
                0,
                0,
                0,
                expectedEmpty,
                0);
        }

        var bucketEnd = bucketStartUtc + bucketDuration;
        var ordered = samples
            .Where(sample =>
                sample.TimestampUtc >= bucketStartUtc
                && sample.TimestampUtc < bucketEnd
                && double.IsFinite(sample.Value))
            .OrderBy(sample => sample.TimestampUtc)
            .ToArray();
        if (ordered.Length == 0)
        {
            return Calculate(bucketStartUtc, bucketDuration, expectedInterval, []);
        }

        var integral = 0d;
        for (var index = 1; index < ordered.Length; index++)
        {
            var seconds = Math.Max(
                0,
                (ordered[index].TimestampUtc - ordered[index - 1].TimestampUtc).TotalSeconds);
            integral += (ordered[index - 1].Value + ordered[index].Value) * 0.5d * seconds;
        }

        return new MonitorRollup(
            bucketStartUtc,
            bucketEnd,
            ordered[0].Value,
            ordered[^1].Value,
            ordered.Min(sample => sample.Value),
            ordered.Max(sample => sample.Value),
            ordered.Average(sample => sample.Value),
            ordered.Length,
            Math.Max(0, ExpectedCount(bucketDuration, expectedInterval) - ordered.Length),
            integral);
    }

    private static int ExpectedCount(TimeSpan duration, TimeSpan interval) =>
        checked((int)Math.Ceiling(duration.TotalSeconds / interval.TotalSeconds));
}
