namespace WinPool.Monitoring;

/// <summary>
/// ALG-MON-002. The hysteresis ratios are speculative pending recorded-session
/// visual validation; callers must surface that status when exposing the setting.
/// </summary>
public sealed class MonitorScale
{
    public const double MinimumBytesPerSecond = 100d * 1024d;
    public const string ConfidenceLabel = "【推测，待验证】";

    private readonly double growRatio;
    private readonly double shrinkRatio;

    public MonitorScale(double growRatio = 1d, double shrinkRatio = 0.45d)
    {
        if (growRatio is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(growRatio));
        }

        if (shrinkRatio is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(shrinkRatio));
        }

        this.growRatio = growRatio;
        this.shrinkRatio = shrinkRatio;
        CurrentMaximum = MinimumBytesPerSecond;
    }

    public double CurrentMaximum { get; private set; }

    public double Update(IEnumerable<double> visibleValues)
    {
        ArgumentNullException.ThrowIfNull(visibleValues);
        var maximum = visibleValues
            .Where(double.IsFinite)
            .Select(value => Math.Max(0d, value))
            .DefaultIfEmpty(0d)
            .Max();

        var desired = FriendlyCeiling(Math.Max(maximum, MinimumBytesPerSecond));
        if (desired > CurrentMaximum * growRatio
            || desired < CurrentMaximum * shrinkRatio)
        {
            CurrentMaximum = desired;
        }

        return CurrentMaximum;
    }

    public static double FriendlyCeiling(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        value = Math.Max(value, MinimumBytesPerSecond);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var friendly = normalized switch
        {
            <= 1d => 1d,
            <= 2d => 2d,
            <= 5d => 5d,
            _ => 10d
        };
        return friendly * magnitude;
    }
}
