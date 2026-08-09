using WinPool.Application;
using WinPool.Domain;
using WinPool.Monitoring;

namespace WinPool.Infrastructure.Sqlite;

public sealed class SqliteMonitorSessionPersistenceFactory
    : IMonitorSessionPersistenceFactory
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease writeOwner;
    private readonly int channelCapacity;
    private readonly int maximumBatchSize;
    private readonly TimeSpan maximumBatchDelay;

    public SqliteMonitorSessionPersistenceFactory(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner,
        int channelCapacity = 8_192,
        int maximumBatchSize = 1_000,
        TimeSpan? maximumBatchDelay = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.writeOwner = writeOwner
            ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
        if (channelCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCapacity));
        }

        if (maximumBatchSize is <= 0 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBatchSize));
        }

        this.channelCapacity = channelCapacity;
        this.maximumBatchSize = maximumBatchSize;
        this.maximumBatchDelay = maximumBatchDelay ?? TimeSpan.FromMilliseconds(250);
    }

    public IMonitorSessionPersistence Create(SessionId sessionId)
    {
        writeOwner.AssertOwnership(store);
        return new SqliteMonitorSessionPersistence(
            store,
            writeOwner,
            sessionId,
            channelCapacity,
            maximumBatchSize,
            maximumBatchDelay);
    }
}

internal sealed class SqliteMonitorSessionPersistence
    : IMonitorSessionPersistence
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease writeOwner;
    private readonly SessionId expectedSessionId;
    private readonly int channelCapacity;
    private readonly int maximumBatchSize;
    private readonly TimeSpan maximumBatchDelay;
    private readonly MonitorSessionRepository sessions;
    private readonly MonitorDeviceRepository devices;
    private MonitorSampleBatchWriter? writer;
    private bool started;
    private bool completed;

    public SqliteMonitorSessionPersistence(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner,
        SessionId expectedSessionId,
        int channelCapacity,
        int maximumBatchSize,
        TimeSpan maximumBatchDelay)
    {
        this.store = store;
        this.writeOwner = writeOwner;
        this.expectedSessionId = expectedSessionId;
        this.channelCapacity = channelCapacity;
        this.maximumBatchSize = maximumBatchSize;
        this.maximumBatchDelay = maximumBatchDelay;
        sessions = new MonitorSessionRepository(store, writeOwner);
        devices = new MonitorDeviceRepository(store, writeOwner);
    }

    public async Task StartAsync(
        MonitoringSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (started || session.SessionId != expectedSessionId)
        {
            throw new InvalidOperationException("监控持久化会话身份无效或已启动。");
        }

        writeOwner.AssertOwnership(store);
        await sessions.CreateAsync(
            new PersistedMonitorSession(
                session.SessionId,
                session.CreatedAtUtc,
                null,
                "Stopwatch+UTC",
                MonitoringSessionState.Running,
                0),
            cancellationToken);
        foreach (var target in session.Request.Targets)
        {
            var sampleIdentity = new MonitorSample(
                session.SessionId,
                target.ObjectId,
                session.CreatedAtUtc,
                [],
                false);
            await devices.UpsertAsync(
                new PersistedMonitorDevice(
                    session.SessionId,
                    MonitorSampleBatchWriter.PersistedDeviceId(sampleIdentity),
                    SanitizeName(target),
                    (int)target.ObjectId.Kind),
                cancellationToken);
        }

        writer = new MonitorSampleBatchWriter(
            store,
            writeOwner,
            channelCapacity,
            maximumBatchSize,
            maximumBatchDelay);
        started = true;
    }

    public bool TryWrite(MonitorSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return started
               && !completed
               && sample.SessionId == expectedSessionId
               && writer!.TryEnqueue(sample);
    }

    public Task AddDroppedSamplesAsync(
        long count,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        return sessions.AddDroppedSamplesAsync(
            expectedSessionId,
            count,
            cancellationToken);
    }

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        EnsureActive();
        return writer!.FlushAsync(cancellationToken);
    }

    public async Task CompleteAsync(
        MonitoringSessionState finalState,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        await writer!.CompleteAndFlushAsync(cancellationToken);
        await sessions.CompleteAsync(
            expectedSessionId,
            finalState,
            endedAtUtc,
            cancellationToken);
        completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (writer is not null)
        {
            await writer.DisposeAsync();
        }
    }

    private void EnsureActive()
    {
        writeOwner.AssertOwnership(store);
        if (!started || completed)
        {
            throw new InvalidOperationException("监控持久化会话未启动或已经完成。");
        }
    }

    private static string SanitizeName(MonitorTarget target)
    {
        var name = target.CounterIdentity.Trim();
        if (name.Length > 128)
        {
            name = name[..128];
        }

        return name.Length == 0
            ? target.ObjectId.Kind.ToString()
            : name;
    }
}
