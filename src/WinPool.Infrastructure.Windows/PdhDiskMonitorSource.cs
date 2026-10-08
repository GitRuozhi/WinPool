using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Globalization;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Fixed read-only PDH provider hidden behind IMonitorSource. It never changes
/// disks, volumes, pools or operating-system settings.
/// </summary>
public sealed class PdhDiskMonitorSource : IMonitorSource, IRebindableMonitorSource
{
    private readonly ConcurrentDictionary<WinPool.Domain.SessionId, MonitorTarget[]> selections = new();
    private Func<MonitorTargetAvailability, CancellationToken, Task>? availabilityObserver;

    public void SetTargets(WinPool.Domain.SessionId sessionId, IReadOnlyList<MonitorTarget> targets) => selections[sessionId] = targets.ToArray();
    public void ReleaseTargets(WinPool.Domain.SessionId sessionId) => selections.TryRemove(sessionId, out _);
    public void SetAvailabilityObserver(Func<MonitorTargetAvailability, CancellationToken, Task> observer) => availabilityObserver = observer;

    public async IAsyncEnumerable<MonitorSample> SampleAsync(
        MonitorRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var sampler = new DiskPerformanceSampler();
        using var virtualDiskSampler = new StorageSpacesVirtualDiskSampler();
        using var timer = new PeriodicTimer(request.SamplingInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var rawSamples = await Task.Run(
                sampler.Sample,
                cancellationToken).ConfigureAwait(false);
            var virtualDiskSamples = await Task.Run(
                virtualDiskSampler.Sample,
                cancellationToken).ConfigureAwait(false);
            var sampledAtUtc = DateTimeOffset.UtcNow;
            foreach (var target in (IReadOnlyList<MonitorTarget>?)selections.GetValueOrDefault(request.SessionId) ?? request.Targets)
            {
                var virtualState = target.CounterSource == MonitorCounterSource.StorageSpacesVirtualDisk
                    || target.CounterSource is null && target.ObjectId.Kind == WinPool.Domain.StorageObjectKind.VirtualDisk;
                if (!request.Metrics.Any(metric => virtualState
                    ? metric is MonitorMetricKind.VirtualDiskActiveBytes or MonitorMetricKind.VirtualDiskMissingBytes
                        or MonitorMetricKind.VirtualDiskStaleBytes or MonitorMetricKind.VirtualDiskNeedRegenerationBytes
                        or MonitorMetricKind.VirtualDiskRegeneratingBytes or MonitorMetricKind.VirtualDiskPendingDeletionBytes
                    : metric is MonitorMetricKind.ActiveTimePercent or MonitorMetricKind.ReadBytesPerSecond
                        or MonitorMetricKind.WriteBytesPerSecond or MonitorMetricKind.AverageQueueLength)) continue;
                if (target.SuspendedForEdit)
                {
                    if (availabilityObserver is { } observeSuspended)
                        await observeSuspended(new(request.SessionId, target.ObjectId, target.CounterIdentity, false, sampledAtUtc), cancellationToken);
                    continue;
                }
                if (virtualState)
                {
                    var matched = SelectVirtualDisks(target.CounterIdentity, virtualDiskSamples).ToArray();
                    if (target.CounterIdentity != "*" && availabilityObserver is { } observeVirtual)
                        await observeVirtual(new(request.SessionId, target.ObjectId, target.CounterIdentity, matched.Length == 1, sampledAtUtc), cancellationToken);
                    foreach (var discovered in target.CounterIdentity == "*" || matched.Length == 1 ? matched : [])
                    {
                        yield return new MonitorSample(
                            request.SessionId,
                            target.CounterIdentity == "*" ? new WinPool.Domain.StorageObjectId(
                                request.SystemId,
                                target.ObjectId.Kind,
                                $"pdh-storage-spaces:{discovered.InstanceName}") : target.ObjectId,
                            sampledAtUtc,
                            RequestedVirtualDiskValues(request.Metrics, discovered)) { CounterSource = MonitorCounterSource.StorageSpacesVirtualDisk };
                    }

                    continue;
                }

                if (target.CounterIdentity == "*")
                {
                    foreach (var discovered in rawSamples)
                    {
                        yield return new MonitorSample(
                            request.SessionId,
                            new WinPool.Domain.StorageObjectId(
                                request.SystemId,
                                target.ObjectId.Kind,
                                $"pdh:{discovered.InstanceName}"),
                            sampledAtUtc,
                            RequestedValues(request.Metrics, discovered)) { CounterSource = MonitorCounterSource.PhysicalDisk };
                    }

                    continue;
                }

                var matches = SelectPhysicalDisks(target.CounterIdentity, rawSamples).ToArray();
                if (availabilityObserver is { } observePhysical)
                    await observePhysical(new(request.SessionId, target.ObjectId, target.CounterIdentity, matches.Length == 1, sampledAtUtc), cancellationToken);
                if (matches.Length != 1)
                {
                    continue;
                }

                var values = RequestedValues(request.Metrics, matches[0]);
                yield return new MonitorSample(
                    request.SessionId,
                    target.ObjectId,
                    sampledAtUtc,
                    values) { CounterSource = MonitorCounterSource.PhysicalDisk };
            }
        }
    }

    internal static IEnumerable<DiskPerformanceSample> SelectPhysicalDisks(string counterIdentity, IReadOnlyList<DiskPerformanceSample> samples)
    {
        if (!counterIdentity.StartsWith("disk-number:", StringComparison.Ordinal))
            return samples.Where(x => StringComparer.OrdinalIgnoreCase.Equals(x.InstanceName, counterIdentity));
        return uint.TryParse(counterIdentity[12..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? samples.Where(x => uint.TryParse(x.InstanceName.Split(' ', 2)[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index == number)
            : [];
    }

    internal static IEnumerable<StorageSpacesVirtualDiskSample> SelectVirtualDisks(
        string counterIdentity,
        IReadOnlyList<StorageSpacesVirtualDiskSample> samples)
    {
        if (counterIdentity.StartsWith("vd-disk-number:", StringComparison.Ordinal))
        {
            if (!uint.TryParse(counterIdentity[15..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return [];
            var suffix = " - Disk " + number.ToString(CultureInfo.InvariantCulture);
            var matches = samples.Where(sample => sample.InstanceName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches : [];
        }
        return counterIdentity.StartsWith("vd-guid:", StringComparison.Ordinal) && Guid.TryParse(counterIdentity[8..], out var guid)
            ? samples.Where(sample => WindowsMonitorTargetIdentityResolver.GuidPattern().Matches(sample.InstanceName)
                .Select(match => Guid.Parse(match.Value)).Distinct().SequenceEqual([guid]))
            : counterIdentity == "*"
            ? samples
            : samples.Where(
                sample => StringComparer.OrdinalIgnoreCase.Equals(
                    sample.InstanceName,
                    counterIdentity));
    }

    private static IReadOnlyList<MonitorMetricValue> RequestedVirtualDiskValues(
        IReadOnlyList<MonitorMetricKind> requested,
        StorageSpacesVirtualDiskSample raw)
    {
        var values = new List<MonitorMetricValue>();
        foreach (var metric in requested.Distinct())
        {
            double? value = metric switch
            {
                MonitorMetricKind.VirtualDiskActiveBytes => raw.ActiveBytes,
                MonitorMetricKind.VirtualDiskMissingBytes => raw.MissingBytes,
                MonitorMetricKind.VirtualDiskStaleBytes => raw.StaleBytes,
                MonitorMetricKind.VirtualDiskNeedRegenerationBytes => raw.NeedRegenerationBytes,
                MonitorMetricKind.VirtualDiskRegeneratingBytes => raw.RegeneratingBytes,
                MonitorMetricKind.VirtualDiskPendingDeletionBytes => raw.PendingDeletionBytes,
                _ => null
            };
            if (value.HasValue)
            {
                values.Add(new MonitorMetricValue(metric, value.Value));
            }
        }

        return values;
    }

    private static IReadOnlyList<MonitorMetricValue> RequestedValues(
        IReadOnlyList<MonitorMetricKind> requested,
        DiskPerformanceSample raw)
    {
        var values = new List<MonitorMetricValue>(requested.Count);
        foreach (var metric in requested.Distinct())
        {
            double? value = metric switch
            {
                MonitorMetricKind.ActiveTimePercent => raw.ActivityPercent,
                MonitorMetricKind.ReadBytesPerSecond => raw.ReadBytesPerSecond,
                MonitorMetricKind.WriteBytesPerSecond => raw.WriteBytesPerSecond,
                MonitorMetricKind.AverageQueueLength => raw.AverageQueueLength,
                _ => null
            };
            if (value is { } measured)
            {
                values.Add(new MonitorMetricValue(metric, measured));
            }
        }

        return values;
    }
}
