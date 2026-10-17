using System.Collections.Concurrent;
using System.Threading.Channels;
using WinPool.App.Services;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.App.Tests;

public sealed class MonitoringEventCacheTests
{
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
        public required AgentSnapshot Snapshot { get; init; }
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
