using System.Collections.Concurrent;
using System.Threading.Channels;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Tests;

public sealed class MonitoringEventCacheTests
{
    [Fact]
    public async Task ReconnectSnapshotRejectsOlderStatesAndRetainsSamplesThroughNextBatch()
    {
        var fixture = new Fixture();
        var snapshot = fixture.Snapshot(Enumerable.Range(1, 20).Select(value => (long)value).ToArray())
            with { MonitorStateRevision = 3 };
        var connection = new Connection { Snapshot = snapshot };
        using var service = new MonitoringService(connection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await PushAsync(service, connection, new AgentStateReseedEvent(snapshot, "reconnect", DateTimeOffset.UtcNow), timeout.Token);

        // Queue age is determined at Agent capture, even if UTC moved forward.
        await PushAsync(service, connection, new AgentMonitorStateEvent(snapshot.AgentInstanceId, null,
            new(0, 0), [], DateTimeOffset.UtcNow.AddMinutes(1)) { MonitorStateRevision = 1 }, timeout.Token);
        await PushAsync(service, connection, new AgentMonitorStateEvent(snapshot.AgentInstanceId, snapshot.ActiveMonitoringSession,
            new(0, 0), [], DateTimeOffset.UtcNow.AddMinutes(1)) { MonitorStateRevision = 2 }, timeout.Token);
        await PushAsync(service, connection, new AgentMonitorStateEvent(snapshot.AgentInstanceId, null,
            new(0, 0), [], DateTimeOffset.UtcNow, AgentLifecycleState.Recovering)
            { MonitorStateRevision = 2 }, timeout.Token);
        await PushAsync(service, connection, fixture.Batch(21), timeout.Token);
        await PushAsync(service, connection, new AgentStateReseedEvent(
            snapshot with { MonitorStateRevision = 2 }, "late.snapshot", DateTimeOffset.UtcNow), timeout.Token);

        Assert.Equal(Enumerable.Range(1, 21).Select(value => (double)value),
            Assert.Single(service.GetWindows()).Value.Select(point => point.ActivityPercent));
        Assert.True(service.IsRemoteStateKnown);
        Assert.True(service.IsRunning);
        Assert.Equal(0, service.GetRuntimeDiagnostics().TransportGapCount);
        Assert.Empty(connection.Requests);
        await service.DetachAsync();
    }

    [Fact]
    public async Task NewerSessionStateLoadsOneWindowAndDoesNotMixOldSamples()
    {
        var first = new Fixture();
        var next = new Fixture();
        var firstSnapshot = first.Snapshot(1, 2) with { MonitorStateRevision = 3 };
        var nextSnapshot = next.Snapshot(1, 2) with
        {
            AgentInstanceId = firstSnapshot.AgentInstanceId,
            MonitorStateRevision = 5
        };
        var connection = new Connection { Snapshot = nextSnapshot };
        using var service = new MonitoringService(connection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await PushAsync(service, connection, new AgentStateReseedEvent(firstSnapshot, "initial", DateTimeOffset.UtcNow), timeout.Token);

        // A real newer boundary remains newer when UTC moves backwards.
        await PushAsync(service, connection, new AgentMonitorStateEvent(nextSnapshot.AgentInstanceId, nextSnapshot.ActiveMonitoringSession,
            new(0, 0), [], DateTimeOffset.UtcNow.AddMinutes(-1)) { MonitorStateRevision = 4 }, timeout.Token);
        Assert.Single(connection.Requests);
        await PushAsync(service, connection, first.Batch(3), timeout.Token);
        await PushAsync(service, connection, new AgentMonitorStateEvent(firstSnapshot.AgentInstanceId, firstSnapshot.ActiveMonitoringSession,
            new(0, 0), [], DateTimeOffset.UtcNow) { MonitorStateRevision = 3 }, timeout.Token);
        await PushAsync(service, connection, next.Batch(3) with { AgentInstanceId = nextSnapshot.AgentInstanceId }, timeout.Token);
        Assert.Equal([1d, 2d, 3d], Assert.Single(service.GetWindows()).Value.Select(point => point.ActivityPercent));
        Assert.Equal(next.Target, service.GetSeriesTarget("synthetic"));

        connection.Snapshot = next.Snapshot(1, 2, 3, 5) with
        {
            AgentInstanceId = nextSnapshot.AgentInstanceId,
            MonitorStateRevision = 6
        };
        await PushAsync(service, connection, next.Batch(5) with { AgentInstanceId = nextSnapshot.AgentInstanceId }, timeout.Token);
        var points = Assert.Single(service.GetWindows()).Value;
        Assert.Equal([1d, 2d, 3d, 5d], points.Select(point => point.ActivityPercent));
        Assert.True(points[^1].GapBefore);
        Assert.Equal(1, service.GetRuntimeDiagnostics().TransportGapCount);
        Assert.Equal(2, connection.Requests.Count);
        await service.DetachAsync();
    }

    [Fact]
    public async Task PushBatchesDeduplicateAndASequenceGapLoadsOneWindow()
    {
        var fixture = new Fixture();
        var connection = new Connection { Snapshot = fixture.Snapshot(1, 2, 4) };
        using var service = new MonitoringService(connection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await PushAsync(service, connection, new AgentStateReseedEvent(fixture.Snapshot(1), "test.initial", DateTimeOffset.UtcNow), timeout.Token);
        await PushAsync(service, connection, fixture.Batch(2), timeout.Token);
        await PushAsync(service, connection, fixture.Batch(2), timeout.Token);
        Assert.Equal(2, Assert.Single(service.GetWindows()).Value.Length);
        Assert.Empty(connection.Requests);

        await PushAsync(service, connection, fixture.Batch(4) with { HasGap = true }, timeout.Token);
        var points = Assert.Single(service.GetWindows()).Value;
        Assert.Equal(3, points.Length);
        Assert.True(points[^1].GapBefore);
        Assert.Equal(1, service.GetRuntimeDiagnostics().TransportGapCount);
        Assert.IsType<GetAgentSnapshotRequest>(Assert.Single(connection.Requests));
        await PushAsync(service, connection, fixture.Batch(4), timeout.Token);
        Assert.Single(connection.Requests);
        await service.DetachAsync();
    }

    [Fact]
    public async Task ReconnectReplacesWindowAndOldAgentSamplesCannotReenterIt()
    {
        var first = new Fixture();
        var replacement = new Fixture();
        var connection = new Connection { Snapshot = first.Snapshot(1) };
        using var service = new MonitoringService(connection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await PushAsync(service, connection, new AgentStateReseedEvent(first.Snapshot(1), "initial", DateTimeOffset.UtcNow), timeout.Token);
        await PushAsync(service, connection, new AgentEventTransportStateEvent(
            AgentEventTransportState.Disconnected, true, "test.gap", DateTimeOffset.UtcNow), timeout.Token);
        Assert.False(service.IsRemoteStateKnown);
        await PushAsync(service, connection, new AgentStateReseedEvent(replacement.Snapshot(1), "reconnect", DateTimeOffset.UtcNow), timeout.Token);
        await PushAsync(service, connection, first.Batch(2), timeout.Token);
        var oldSnapshot = first.Snapshot(1);
        await PushAsync(service, connection, new AgentMonitorStateEvent(oldSnapshot.AgentInstanceId, oldSnapshot.ActiveMonitoringSession,
            new(0, 0), [], DateTimeOffset.UtcNow) { MonitorStateRevision = 100 }, timeout.Token);

        Assert.True(service.IsRemoteStateKnown);
        Assert.Single(Assert.Single(service.GetWindows()).Value);
        Assert.Equal(replacement.Target, service.GetSeriesTarget("synthetic"));
        Assert.Empty(connection.Requests);
        await service.DetachAsync();
    }

    private static async Task PushAsync(MonitoringService service, Connection connection,
        AgentEvent item, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() => completed.TrySetResult();
        service.Changed += Changed;
        try
        {
            connection.Events.Writer.TryWrite(item);
            await completed.Task.WaitAsync(cancellationToken);
        }
        finally { service.Changed -= Changed; }
    }

    private sealed class Fixture
    {
        private readonly AgentInstanceId instance = new(Guid.NewGuid());
        private readonly SessionId session = SessionId.New();
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        public StorageObjectId Target { get; } = new(SystemId.New(), StorageObjectKind.PhysicalDisk, "synthetic");

        private MonitorSample Sample(long sequence) => new(session, Target,
            started.AddMilliseconds(sequence * 50), [new(MonitorMetricKind.ActiveTimePercent, sequence)]) { Sequence = sequence };

        public AgentMonitorBatchEvent Batch(long sequence) => new(instance, session,
            sequence, sequence, [Sample(sequence)], false, DateTimeOffset.UtcNow);

        public AgentSnapshot Snapshot(params long[] sequences) => new(instance, true,
            new(session, new(session, Target.System, [new(Target, "synthetic")],
                [MonitorMetricKind.ActiveTimePercent], TimeSpan.FromMilliseconds(50), true),
                MonitoringSessionState.Running, started, null),
            new(AgentLifecycleState.Running, null, [], [], false), [],
            sequences.Select(Sample).ToArray(), [], new(0, 0));
    }

    private sealed class Connection : IAgentConnection
    {
        public Channel<AgentEvent> Events { get; } = Channel.CreateUnbounded<AgentEvent>();
        public ConcurrentQueue<AgentRequest> Requests { get; } = new();
        public required AgentSnapshot Snapshot { get; set; }
        public Task<ApplicationResult<AgentHandshake>> ConnectAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Event projection must not connect again.");
        public IAsyncEnumerable<AgentEvent> WatchAsync(CancellationToken cancellationToken) =>
            Events.Reader.ReadAllAsync(cancellationToken);
        public Task<ApplicationResult<AgentResponse>> SendAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            Assert.IsType<GetAgentSnapshotRequest>(request);
            return Task.FromResult(ApplicationResult<AgentResponse>.Succeeded(
                new AgentSnapshotResponse(Snapshot), request.CorrelationId));
        }
    }
}
