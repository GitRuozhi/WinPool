using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class MonitorSampleBatchWriterFailureTests
{
    [Fact]
    public async Task HealthyBlockedBatchIsPendingButNeverReportedAsConfirmedLoss()
    {
        var path = Path.Combine(Path.GetTempPath(), $"WinPool writer pending {Guid.NewGuid():N}.db");
        var inner = new MonitoringSqliteStore(path);
        await inner.InitializeAsync();
        var gate = new GateStore(inner);
        await using var lease = AgentWriteOwnerLease.Acquire(gate, "writer-pending-agent");
        var (sessionId, target) = await CreatePersistedSessionAsync(inner, lease);
        var clock = new ManualTimeProvider();
        await using var writer = new MonitorSampleBatchWriter(
            gate,
            lease,
            capacity: 2,
            maximumBatchSize: 1,
            maximumBatchDelay: TimeSpan.FromMilliseconds(1),
            timeProvider: clock);

        Assert.True(writer.TryEnqueue(CreateSample(sessionId, target, 1)));
        await WaitUntilAsync(() => Volatile.Read(ref gate.OpenAttempts) > 0);
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(1, writer.PendingSamples);
        Assert.True(writer.OldestPendingMilliseconds >= 3_000);
        Assert.Equal(0, writer.ConfirmedLostSamples);

        gate.Release();
        await writer.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, writer.PendingSamples);
        var persisted = await ReadPersistedActivitiesAsync(path);
        Assert.Equal(new[] { 1d }, persisted);
    }

    [Fact]
    public async Task FlushUsesAcceptedIdentityBoundaryWhenFullIngressWriteIsCancelled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"WinPool writer flush {Guid.NewGuid():N}.db");
        var inner = new MonitoringSqliteStore(path);
        await inner.InitializeAsync();
        var gate = new GateStore(inner);
        await using var lease = AgentWriteOwnerLease.Acquire(gate, "writer-flush-agent");
        var (sessionId, target) = await CreatePersistedSessionAsync(inner, lease);
        await using var writer = new MonitorSampleBatchWriter(
            gate,
            lease,
            capacity: 1,
            maximumBatchSize: 1,
            maximumBatchDelay: TimeSpan.FromMilliseconds(1));

        Assert.True(writer.TryEnqueue(CreateSample(sessionId, target, 1)));
        await WaitUntilAsync(() => Volatile.Read(ref gate.OpenAttempts) > 0);
        Assert.True(writer.TryEnqueue(CreateSample(sessionId, target, 2)));

        using var cancellation = new CancellationTokenSource();
        var rejected = writer.EnqueueAsync(CreateSample(sessionId, target, 3), cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rejected);

        var flush = writer.FlushAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(40));
        Assert.False(flush.IsCompleted);

        gate.Release();
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, writer.PendingSamples);
        Assert.Equal(1, writer.RejectedSamples);
        var persisted = await ReadPersistedActivitiesAsync(path);
        Assert.Equal(new[] { 1d, 2d }, persisted);
    }

    [Fact]
    public async Task WriteFailureClosesIngressAndReportsExactUnpersistedSampleCount()
    {
        var store = new AlwaysFailingStore(Path.Combine(
            Path.GetTempPath(),
            $"WinPool writer failure {Guid.NewGuid():N}.db"));
        await using var lease = AgentWriteOwnerLease.Acquire(store, "writer-failure-agent");
        var writer = new MonitorSampleBatchWriter(
            store,
            lease,
            capacity: 4,
            maximumBatchSize: 1,
            maximumBatchDelay: TimeSpan.FromMilliseconds(1));
        var sessionId = SessionId.New();
        var systemId = SystemId.New();
        var sample = new MonitorSample(
            sessionId,
            new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "writer-failure"),
            DateTimeOffset.UtcNow,
            [new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, 1)]);

        Assert.True(writer.TryEnqueue(sample));
        var failure = await Assert.ThrowsAsync<IOException>(
            () => writer.FlushAsync(CancellationToken.None));

        Assert.Contains("intentional writer failure", failure.Message, StringComparison.Ordinal);
        Assert.NotNull(writer.Failure);
        Assert.False(writer.TryEnqueue(sample));
        Assert.Equal(1, writer.ConfirmedLostSamples);
        Assert.Equal(1, writer.RejectedSamples);
        await Assert.ThrowsAsync<IOException>(
            () => writer.CompleteAndFlushAsync(CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => writer.DisposeAsync().AsTask());
    }

    private sealed class AlwaysFailingStore(string databasePath) : ISqliteDatabaseStore
    {
        public string DatabasePath { get; } = Path.GetFullPath(databasePath);

        public Task<SqliteConnection> OpenConnectionAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<SqliteConnection>(
                new IOException("intentional writer failure"));
        }
    }

    private static async Task<(SessionId SessionId, StorageObjectId Target)> CreatePersistedSessionAsync(
        MonitoringSqliteStore store,
        AgentWriteOwnerLease lease)
    {
        var systemId = SystemId.New();
        var sessionId = SessionId.New();
        var target = new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "writer-target");
        await new MonitorSessionRepository(store, lease).CreateAsync(
            new PersistedMonitorSession(
                sessionId,
                DateTimeOffset.UtcNow,
                null,
                "test",
                MonitoringSessionState.Running,
                0));
        return (sessionId, target);
    }

    private static MonitorSample CreateSample(
        SessionId sessionId,
        StorageObjectId target,
        int value) =>
        new(
            sessionId,
            target,
            DateTimeOffset.UtcNow.AddMilliseconds(value),
            [new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, value)]);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
        }
    }

    private static async Task<double[]> ReadPersistedActivitiesAsync(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT activity_pct FROM monitor_samples ORDER BY activity_pct;";
        var values = new List<double>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetDouble(0));
        }

        return values.ToArray();
    }

    private sealed class GateStore(MonitoringSqliteStore inner) : ISqliteDatabaseStore
    {
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string DatabasePath => inner.DatabasePath;

        public int OpenAttempts;

        public async Task<SqliteConnection> OpenConnectionAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref OpenAttempts);
            await release.Task.WaitAsync(cancellationToken);
            return await inner.OpenConnectionAsync(cancellationToken);
        }

        public void Release() => release.TrySetResult();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref timestamp);

        public void Advance(TimeSpan elapsed) =>
            Interlocked.Add(ref timestamp, elapsed.Ticks);
    }
}
