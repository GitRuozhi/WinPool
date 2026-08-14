using System.Diagnostics;

namespace WinPool.Monitoring;

internal interface IMonotonicClock
{
    long Timestamp { get; }
    long Frequency { get; }
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemMonotonicClock : IMonotonicClock
{
    public long Timestamp => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// ALG-MON-001: elapsed time comes only from a monotonic clock. UTC is retained
/// for persistence and may move backwards without producing a negative interval.
/// </summary>
internal sealed class MonotonicSampleClock
{
    private readonly IMonotonicClock clock;
    private long? previousTimestamp;

    public MonotonicSampleClock(IMonotonicClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (clock.Frequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clock), "时钟频率必须大于零。");
        }

        this.clock = clock;
    }

    public NormalizedSampleTime Capture()
    {
        var timestamp = clock.Timestamp;
        var utc = clock.UtcNow;
        var elapsed = previousTimestamp is { } previous
            ? TimeSpan.FromSeconds(Math.Max(0, timestamp - previous) / (double)clock.Frequency)
            : TimeSpan.Zero;
        previousTimestamp = timestamp;
        return new NormalizedSampleTime(timestamp, utc, elapsed);
    }
}

internal readonly record struct NormalizedSampleTime(
    long MonotonicTimestamp,
    DateTimeOffset PersistedUtc,
    TimeSpan ElapsedSincePrevious);
