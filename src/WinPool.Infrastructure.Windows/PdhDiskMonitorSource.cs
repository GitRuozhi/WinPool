using System.Runtime.CompilerServices;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Fixed read-only PDH provider hidden behind IMonitorSource. It never changes
/// disks, volumes, pools or operating-system settings.
/// </summary>
public sealed class PdhDiskMonitorSource : IMonitorSource
{
    private readonly Func<bool> isTestActive;

    public PdhDiskMonitorSource(Func<bool>? isTestActive = null)
    {
        this.isTestActive = isTestActive ?? (() => false);
    }

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
            var byIdentity = rawSamples.ToDictionary(
                sample => sample.InstanceName,
                StringComparer.OrdinalIgnoreCase);
            var sampledAtUtc = DateTimeOffset.UtcNow;
            foreach (var target in request.Targets)
            {
                if (target.ObjectId.Kind == WinPool.Domain.StorageObjectKind.VirtualDisk)
                {
                    foreach (var discovered in SelectVirtualDisks(
                                 target.CounterIdentity,
                                 virtualDiskSamples))
                    {
                        yield return new MonitorSample(
                            request.SessionId,
                            new WinPool.Domain.StorageObjectId(
                                request.SystemId,
                                target.ObjectId.Kind,
                                $"pdh-storage-spaces:{discovered.InstanceName}"),
                            sampledAtUtc,
                            RequestedVirtualDiskValues(request.Metrics, discovered),
                            isTestActive());
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
                            RequestedValues(request.Metrics, discovered),
                            isTestActive());
                    }

                    continue;
                }

                if (!byIdentity.TryGetValue(target.CounterIdentity, out var raw))
                {
                    continue;
                }

                var values = RequestedValues(request.Metrics, raw);
                yield return new MonitorSample(
                    request.SessionId,
                    target.ObjectId,
                    sampledAtUtc,
                    values,
                    isTestActive());
            }
        }
    }

    private static IEnumerable<StorageSpacesVirtualDiskSample> SelectVirtualDisks(
        string counterIdentity,
        IReadOnlyList<StorageSpacesVirtualDiskSample> samples) =>
        counterIdentity == "*"
            ? samples
            : samples.Where(
                sample => StringComparer.OrdinalIgnoreCase.Equals(
                    sample.InstanceName,
                    counterIdentity));

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
