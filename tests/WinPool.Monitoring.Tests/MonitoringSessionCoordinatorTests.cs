using System.Runtime.CompilerServices;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Monitoring.Tests;

public sealed class MonitoringSessionCoordinatorTests
{
    [Fact]
    public async Task StartsStreamsStopsAndCompletesPersistence()
    {
        var fixture = new Fixture();
        var persistence = new RecordingPersistence();
        var source = new ControlledSource();
        var coordinator = new MonitoringSessionCoordinator(
            source,
            new FixedPersistenceFactory(persistence),
            latestWindowCapacity: 4,
            subscriberCapacity: 4);

        var started = await coordinator.StartAsync(
            fixture.Request,
            CancellationToken.None);
        Assert.True(started.IsSuccess);
        Assert.Equal(MonitoringSessionState.Running, started.Value!.State);
        source.Publish(fixture.Sample(1));
        var watched = await FirstAsync(
            coordinator.WatchAsync(fixture.Request.SessionId, CancellationToken.None));
        var stopped = await coordinator.StopAsync(
            fixture.Request.SessionId,
            CancellationToken.None);

        Assert.Equal(1, Metric(watched, MonitorMetricKind.ActiveTimePercent));
        Assert.True(stopped.IsSuccess);
        Assert.Equal(MonitoringSessionState.Stopped, stopped.Value!.State);
        Assert.Equal(MonitoringSessionState.Stopped, persistence.FinalState);
        Assert.Single(persistence.Samples);
    }

    [Fact]
    public async Task ReconnectedWatcherReceivesLatestBoundedWindow()
    {
        var fixture = new Fixture();
        var source = new ControlledSource();
        var coordinator = new MonitoringSessionCoordinator(
            source,
            latestWindowCapacity: 2,
            subscriberCapacity: 2);
        await coordinator.StartAsync(fixture.Request, CancellationToken.None);
        source.Publish(fixture.Sample(1));
        source.Publish(fixture.Sample(2));
        source.Publish(fixture.Sample(3));
        await source.WaitUntilConsumedAsync(3);
        var diagnostics = coordinator.CurrentDiagnostics;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var values = new List<double>();
        await foreach (var sample in coordinator.WatchAsync(
                           fixture.Request.SessionId,
                           cancellation.Token))
        {
            values.Add(Metric(sample, MonitorMetricKind.ActiveTimePercent));
            if (values.Count == 2)
            {
                break;
            }
        }

        Assert.Equal([2d, 3d], values);
        Assert.True(diagnostics.DroppedSamples >= 1);
        Assert.Equal(2, diagnostics.BufferedSamples);
        await coordinator.StopAsync(fixture.Request.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task PersistenceBackpressureIsCountedWithoutBlockingSampling()
    {
        var fixture = new Fixture();
        var persistence = new RecordingPersistence { AcceptSamples = false };
        var source = new ControlledSource();
        var coordinator = new MonitoringSessionCoordinator(
            source,
            new FixedPersistenceFactory(persistence));
        await coordinator.StartAsync(fixture.Request, CancellationToken.None);
        source.Publish(fixture.Sample(1));
        source.Publish(fixture.Sample(2));
        await source.WaitUntilConsumedAsync(2);

        await coordinator.StopAsync(fixture.Request.SessionId, CancellationToken.None);

        Assert.True(persistence.DroppedSamples >= 2);
    }

    [Fact]
    public async Task PersistenceLifecycleFailuresBecomeFailedResultsAndReleaseSession()
    {
        var fixture = new Fixture();
        var persistence = new RecordingPersistence
        {
            ThrowOnFlush = true,
            ThrowOnComplete = true,
            ThrowOnDispose = true
        };
        var source = new ControlledSource();
        var coordinator = new MonitoringSessionCoordinator(
            source,
            new FixedPersistenceFactory(persistence));
        var started = await coordinator.StartAsync(
            fixture.Request,
            CancellationToken.None);
        Assert.True(started.IsSuccess);

        var flushed = await coordinator.FlushAsync(
            fixture.Request.SessionId,
            CancellationToken.None);
        var stopped = await coordinator.StopAsync(
            fixture.Request.SessionId,
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Failed, flushed.Status);
        Assert.True(stopped.IsSuccess);
        Assert.Equal(MonitoringSessionState.Failed, stopped.Value!.State);
        Assert.Equal(0, coordinator.TrackedSessionCount);
    }

    [Fact]
    public async Task StartAndDisposeFailuresReturnFailedWithoutRetainingSession()
    {
        var fixture = new Fixture();
        var persistence = new RecordingPersistence
        {
            ThrowOnStart = true,
            ThrowOnDispose = true
        };
        var coordinator = new MonitoringSessionCoordinator(
            new ControlledSource(),
            new FixedPersistenceFactory(persistence));

        var result = await coordinator.StartAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Failed, result.Status);
        Assert.Equal(0, coordinator.TrackedSessionCount);
    }

    [Fact]
    public async Task SubscriberBackpressureDropsOldestAndReportsQueuePressure()
    {
        var fixture = new Fixture();
        var source = new ControlledSource();
        var coordinator = new MonitoringSessionCoordinator(
            source,
            latestWindowCapacity: 8,
            subscriberCapacity: 2);
        await coordinator.StartAsync(fixture.Request, CancellationToken.None);

        await using var watcher = coordinator
            .WatchAsync(fixture.Request.SessionId, CancellationToken.None)
            .GetAsyncEnumerator();
        var firstMove = watcher.MoveNextAsync().AsTask();
        source.Publish(fixture.Sample(1));
        Assert.True(await firstMove.WaitAsync(TimeSpan.FromSeconds(2)));

        source.Publish(fixture.Sample(2));
        source.Publish(fixture.Sample(3));
        source.Publish(fixture.Sample(4));
        await source.WaitUntilConsumedAsync(4);

        var diagnostics = coordinator.CurrentDiagnostics;
        Assert.Equal(1, diagnostics.SubscriberDroppedSamples);
        Assert.Equal(1, diagnostics.DroppedSamples);
        Assert.Equal(1, diagnostics.ActiveSubscribers);
        Assert.Equal(2, diagnostics.SubscriberBufferedSamples);
        Assert.Equal(2, diagnostics.SubscriberCapacity);

        Assert.True(await watcher.MoveNextAsync());
        Assert.Equal(3, Metric(watcher.Current, MonitorMetricKind.ActiveTimePercent));
        await coordinator.StopAsync(fixture.Request.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task SustainedPressureKeepsLatestSamplesAndCountsEveryDropSource()
    {
        const int sampleCount = 20_000;
        const int latestCapacity = 128;
        const int subscriberCapacity = 32;
        const int subscriberCount = 3;
        var fixture = new Fixture();
        var persistence = new RecordingPersistence { AcceptSamples = false };
        var source = new ControlledSource();
        var coordinator = new MonitoringSessionCoordinator(
            source,
            new FixedPersistenceFactory(persistence),
            latestWindowCapacity: latestCapacity,
            subscriberCapacity: subscriberCapacity);
        await coordinator.StartAsync(fixture.Request, CancellationToken.None);

        var watchers = Enumerable.Range(0, subscriberCount)
            .Select(_ => coordinator
                .WatchAsync(fixture.Request.SessionId, CancellationToken.None)
                .GetAsyncEnumerator())
            .ToArray();
        try
        {
            var firstMoves = watchers.Select(watcher => watcher.MoveNextAsync().AsTask()).ToArray();
            source.Publish(fixture.Sample(1));
            await Task.WhenAll(firstMoves).WaitAsync(TimeSpan.FromSeconds(2));

            for (var value = 2; value <= sampleCount; value++)
            {
                source.Publish(fixture.Sample(value));
            }

            await source.WaitUntilConsumedAsync(sampleCount, TimeSpan.FromSeconds(10));
            var diagnostics = coordinator.CurrentDiagnostics;

            Assert.Equal(sampleCount - latestCapacity, diagnostics.WindowDroppedSamples);
            Assert.Equal(sampleCount, diagnostics.PersistenceDroppedSamples);
            Assert.Equal(
                (sampleCount - 1 - subscriberCapacity) * subscriberCount,
                diagnostics.SubscriberDroppedSamples);
            Assert.Equal(0, diagnostics.RejectedSourceSamples);
            Assert.Equal(subscriberCount, diagnostics.ActiveSubscribers);
            Assert.Equal(subscriberCapacity * subscriberCount, diagnostics.SubscriberBufferedSamples);
            Assert.Equal(subscriberCapacity * subscriberCount, diagnostics.SubscriberCapacity);
            Assert.Equal(
                diagnostics.WindowDroppedSamples
                + diagnostics.PersistenceDroppedSamples
                + diagnostics.SubscriberDroppedSamples,
                diagnostics.DroppedSamples);
            Assert.Equal(
                Enumerable.Range(sampleCount - latestCapacity + 1, latestCapacity)
                    .Select(value => (double)value),
                coordinator.CurrentSamples.Select(sample =>
                    Metric(sample, MonitorMetricKind.ActiveTimePercent)));
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                await watcher.DisposeAsync();
            }

            await coordinator.StopAsync(fixture.Request.SessionId, CancellationToken.None);
        }

        Assert.Equal(
            (sampleCount - latestCapacity)
            + sampleCount
            + ((sampleCount - 1 - subscriberCapacity) * subscriberCount),
            persistence.DroppedSamples);
        Assert.Equal(MonitoringSessionState.Stopped, persistence.FinalState);
    }

    [Fact]
    public async Task RejectsSecondActiveSessionAndInvalidRate()
    {
        var fixture = new Fixture();
        var coordinator = new MonitoringSessionCoordinator(new ControlledSource());
        var invalid = fixture.Request with
        {
            SessionId = SessionId.New(),
            SamplingInterval = TimeSpan.FromMilliseconds(10)
        };

        var invalidResult = await coordinator.StartAsync(invalid, CancellationToken.None);
        var first = await coordinator.StartAsync(fixture.Request, CancellationToken.None);
        var second = await coordinator.StartAsync(
            fixture.Request with { SessionId = SessionId.New() },
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, invalidResult.Status);
        Assert.True(first.IsSuccess);
        Assert.Equal(ApplicationStatus.Rejected, second.Status);
        await coordinator.StopAsync(fixture.Request.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task ForeignTargetSampleIsRejectedAndReportedAsDropped()
    {
        var fixture = new Fixture();
        var persistence = new RecordingPersistence();
        var source = new ControlledSource();
        var coordinator = new MonitoringSessionCoordinator(
            source,
            new FixedPersistenceFactory(persistence));
        await coordinator.StartAsync(fixture.Request, CancellationToken.None);
        var otherSystem = SystemId.New();
        source.Publish(
            fixture.Sample(1) with
            {
                TargetId = new StorageObjectId(
                    otherSystem,
                    StorageObjectKind.PhysicalDisk,
                    "foreign")
            });
        await source.WaitUntilConsumedAsync(1);

        await coordinator.StopAsync(fixture.Request.SessionId, CancellationToken.None);

        Assert.Empty(persistence.Samples);
        Assert.True(persistence.DroppedSamples >= 1);
    }

    [Fact]
    public async Task WildcardTargetAcceptsDynamicallyDiscoveredDiskOfSameKind()
    {
        var fixture = new Fixture();
        var source = new ControlledSource();
        var persistence = new RecordingPersistence();
        var wildcard = fixture.Request with
        {
            Targets =
            [
                new MonitorTarget(
                    new StorageObjectId(
                        fixture.Request.SystemId,
                        StorageObjectKind.PhysicalDisk,
                        "pdh-wildcard"),
                    "*")
            ]
        };
        var coordinator = new MonitoringSessionCoordinator(
            source,
            new FixedPersistenceFactory(persistence));
        await coordinator.StartAsync(wildcard, CancellationToken.None);
        source.Publish(
            fixture.Sample(7) with
            {
                TargetId = new StorageObjectId(
                    fixture.Request.SystemId,
                    StorageObjectKind.PhysicalDisk,
                    "pdh:0 Disk")
            });
        await source.WaitUntilConsumedAsync(1);

        await coordinator.StopAsync(wildcard.SessionId, CancellationToken.None);

        Assert.Equal(7, Metric(Assert.Single(persistence.Samples),
            MonitorMetricKind.ActiveTimePercent));
    }

    private static async Task<MonitorSample> FirstAsync(
        IAsyncEnumerable<MonitorSample> samples)
    {
        await foreach (var sample in samples)
        {
            return sample;
        }

        throw new InvalidOperationException("No monitor sample was produced.");
    }

    private static double Metric(MonitorSample sample, MonitorMetricKind kind) =>
        sample.Values.Single(value => value.Kind == kind).Value;

    private sealed class Fixture
    {
        private readonly StorageObjectId target;

        public Fixture()
        {
            var system = SystemId.New();
            target = new StorageObjectId(
                system,
                StorageObjectKind.PhysicalDisk,
                "disk-0");
            Request = new MonitorRequest(
                SessionId.New(),
                system,
                [new MonitorTarget(target, "0 Disk")],
                [
                    MonitorMetricKind.ActiveTimePercent,
                    MonitorMetricKind.ReadBytesPerSecond,
                    MonitorMetricKind.WriteBytesPerSecond
                ],
                TimeSpan.FromSeconds(1),
                ContinueWhenUiCloses: true);
        }

        public MonitorRequest Request { get; }

        public MonitorSample Sample(double value) =>
            new(
                Request.SessionId,
                target,
                DateTimeOffset.UtcNow,
                [new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, value)]);
    }

    private sealed class ControlledSource : IMonitorSource
    {
        private readonly System.Threading.Channels.Channel<MonitorSample> channel =
            System.Threading.Channels.Channel.CreateUnbounded<MonitorSample>();
        private long consumed;

        public void Publish(MonitorSample sample) => channel.Writer.TryWrite(sample);

        public Task WaitUntilConsumedAsync(long count) =>
            WaitUntilConsumedAsync(count, TimeSpan.FromSeconds(2));

        public async Task WaitUntilConsumedAsync(long count, TimeSpan timeoutValue)
        {
            using var timeout = new CancellationTokenSource(timeoutValue);
            while (Interlocked.Read(ref consumed) < count)
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        public async IAsyncEnumerable<MonitorSample> SampleAsync(
            MonitorRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var sample in channel.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Increment(ref consumed);
                yield return sample;
            }
        }
    }

    private sealed class FixedPersistenceFactory(RecordingPersistence persistence)
        : IMonitorSessionPersistenceFactory
    {
        public IMonitorSessionPersistence Create(SessionId sessionId) => persistence;
    }

    private sealed class RecordingPersistence : IMonitorSessionPersistence
    {
        public bool AcceptSamples { get; set; } = true;
        public bool ThrowOnStart { get; set; }
        public bool ThrowOnFlush { get; set; }
        public bool ThrowOnComplete { get; set; }
        public bool ThrowOnDispose { get; set; }
        public List<MonitorSample> Samples { get; } = [];
        public long DroppedSamples { get; private set; }
        public MonitoringSessionState? FinalState { get; private set; }

        public Task StartAsync(
            MonitoringSession session,
            CancellationToken cancellationToken)
        {
            if (ThrowOnStart)
            {
                throw new IOException("Injected monitor start failure.");
            }

            return Task.CompletedTask;
        }

        public bool TryWrite(MonitorSample sample)
        {
            if (!AcceptSamples)
            {
                return false;
            }

            Samples.Add(sample);
            return true;
        }

        public Task AddDroppedSamplesAsync(
            long count,
            CancellationToken cancellationToken)
        {
            DroppedSamples += count;
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            if (ThrowOnFlush)
            {
                throw new IOException("Injected monitor flush failure.");
            }

            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            MonitoringSessionState finalState,
            DateTimeOffset endedAtUtc,
            CancellationToken cancellationToken)
        {
            if (ThrowOnComplete)
            {
                throw new IOException("Injected monitor completion failure.");
            }

            FinalState = finalState;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (ThrowOnDispose)
            {
                throw new IOException("Injected monitor disposal failure.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
