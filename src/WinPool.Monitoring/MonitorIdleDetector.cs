using WinPool.Application;

namespace WinPool.Monitoring;

public sealed record MonitorIdlePolicy(
    double MaximumActivityPercent,
    double MaximumQueueLength,
    double MaximumCombinedBytesPerSecond,
    TimeSpan StableDuration,
    TimeSpan Timeout,
    TimeSpan MaximumSampleAge,
    TimeSpan PollInterval)
{
    public static MonitorIdlePolicy CopyBatchDefault { get; } = new(
        5,
        0.25,
        1024 * 1024,
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMilliseconds(250));
}

public sealed record MonitorIdleObservation(
    bool IsIdle,
    int QualifiedTargetCount,
    double MaximumActivityPercent,
    double MaximumQueueLength,
    double MaximumCombinedBytesPerSecond,
    DateTimeOffset OldestSampleUtc);

public sealed record MonitorIdleEvidence(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    TimeSpan StableDuration,
    MonitorIdleObservation FinalObservation);

public sealed class MonitorIdleDetector(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<MonitorIdleEvidence> WaitAsync(
        Func<IReadOnlyList<MonitorSample>> readSamples,
        MonitorIdlePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readSamples);
        Validate(policy);
        var started = timeProvider.GetUtcNow();
        DateTimeOffset? idleSince = null;
        MonitorIdleObservation? last = null;
        while (timeProvider.GetUtcNow() - started < policy.Timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = timeProvider.GetUtcNow();
            last = Evaluate(readSamples(), policy, now);
            if (last.IsIdle)
            {
                idleSince ??= now;
                if (now - idleSince >= policy.StableDuration)
                {
                    return new(
                        started,
                        now,
                        now - idleSince.Value,
                        last);
                }
            }
            else
            {
                idleSince = null;
            }

            await Task.Delay(policy.PollInterval, cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Monitored storage did not settle before the typed timeout; qualifiedTargets={last?.QualifiedTargetCount ?? 0}.");
    }

    public static MonitorIdleObservation Evaluate(
        IReadOnlyList<MonitorSample> samples,
        MonitorIdlePolicy policy,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(samples);
        Validate(policy);
        var qualified = samples
            .GroupBy(item => item.TargetId)
            .Select(group => group.MaxBy(item => item.SampledAtUtc)!)
            .Select(sample => TryRead(sample, nowUtc, policy.MaximumSampleAge))
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .ToArray();
        if (qualified.Length == 0)
        {
            return new(false, 0, 0, 0, 0, DateTimeOffset.MinValue);
        }

        var maxActivity = qualified.Max(item => item.Activity);
        var maxQueue = qualified.Max(item => item.Queue);
        var maxBytes = qualified.Max(item => item.Bytes);
        return new(
            maxActivity <= policy.MaximumActivityPercent
                && maxQueue <= policy.MaximumQueueLength
                && maxBytes <= policy.MaximumCombinedBytesPerSecond,
            qualified.Length,
            maxActivity,
            maxQueue,
            maxBytes,
            qualified.Min(item => item.SampledAtUtc));
    }

    private static (double Activity, double Queue, double Bytes, DateTimeOffset SampledAtUtc)?
        TryRead(
            MonitorSample sample,
            DateTimeOffset nowUtc,
            TimeSpan maximumAge)
    {
        var activity = Metric(sample, MonitorMetricKind.ActiveTimePercent);
        var queue = Metric(sample, MonitorMetricKind.AverageQueueLength);
        var read = Metric(sample, MonitorMetricKind.ReadBytesPerSecond);
        var write = Metric(sample, MonitorMetricKind.WriteBytesPerSecond);
        if (activity is null || queue is null || read is null || write is null
            || nowUtc - sample.SampledAtUtc < TimeSpan.Zero
            || nowUtc - sample.SampledAtUtc > maximumAge)
        {
            return null;
        }

        var bytes = read.Value + write.Value;
        return double.IsFinite(activity.Value)
            && double.IsFinite(queue.Value)
            && double.IsFinite(bytes)
            && activity.Value >= 0
            && queue.Value >= 0
            && bytes >= 0
                ? (activity.Value, queue.Value, bytes, sample.SampledAtUtc)
                : null;
    }

    private static double? Metric(MonitorSample sample, MonitorMetricKind kind) =>
        sample.Values.FirstOrDefault(item => item.Kind == kind)?.Value;

    private static void Validate(MonitorIdlePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!double.IsFinite(policy.MaximumActivityPercent)
            || policy.MaximumActivityPercent is < 0 or > 100
            || !double.IsFinite(policy.MaximumQueueLength)
            || policy.MaximumQueueLength < 0
            || !double.IsFinite(policy.MaximumCombinedBytesPerSecond)
            || policy.MaximumCombinedBytesPerSecond < 0
            || policy.StableDuration <= TimeSpan.Zero
            || policy.Timeout < policy.StableDuration
            || policy.MaximumSampleAge <= TimeSpan.Zero
            || policy.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }
    }
}
