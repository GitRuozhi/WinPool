using WinPool.Application;
using WinPool.Domain;
using WinPool.Monitoring;

namespace WinPool.Infrastructure.Sqlite;

public sealed class SqliteMonitorSessionPersistenceFactory
    : IMonitorSessionPersistenceFactory
{
    private readonly ISqliteDatabaseStore store;
    private readonly AgentWriteOwnerLease writeOwner;
    private readonly int channelCapacity;
    private readonly int maximumBatchSize;
    private readonly TimeSpan maximumBatchDelay;

    public SqliteMonitorSessionPersistenceFactory(
        ISqliteDatabaseStore store,
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
    : IMonitorSessionPersistence, IMonitorSessionPersistenceDiagnostics
{
    /// <summary>
    /// Normal batching waits at most 250 ms. Two seconds requires at least
    /// eight missed batch opportunities before the UI reports a write delay;
    /// any successful commit immediately clears the condition.
    /// </summary>
    internal static readonly TimeSpan PersistenceDelayThreshold = TimeSpan.FromSeconds(2);

    private readonly ISqliteDatabaseStore store;
    private readonly AgentWriteOwnerLease writeOwner;
    private readonly SessionId expectedSessionId;
    private readonly int channelCapacity;
    private readonly int maximumBatchSize;
    private readonly TimeSpan maximumBatchDelay;
    private readonly TimeProvider timeProvider;
    private readonly MonitorSessionRepository sessions;
    private readonly MonitorDeviceRepository devices;
    private readonly object writerGate = new();
    private MonitorSampleBatchWriter? writer;
    // A writer which has faulted has already completed its reader task. Keep
    // it as the diagnostic source while the rotating owner probes whether the
    // database can accept writes again; this prevents a transient recovery
    // attempt from hiding a real persistence failure.
    private MonitorSampleBatchWriter? retiredFailedWriter;
    private bool started;
    private bool completed;

    internal long ConfirmedLostSamples => GetWriterSnapshot()?.ConfirmedLostSamples ?? 0;

    internal int PendingSamples => GetWriterSnapshot()?.PendingSamples ?? 0;

    internal long OldestPendingMilliseconds => GetWriterSnapshot()?.OldestPendingMilliseconds ?? 0;

    internal Exception? BackgroundFailure => GetWriterSnapshot()?.Failure;

    // Reference identity is deliberately exposed only inside this assembly so
    // the rotating owner can carry each faulted writer's exact pending count
    // once across replacement without confusing a later writer fault for the
    // same occurrence.
    internal object? WriterIdentity => GetWriterSnapshot();

    public MonitorPersistenceDiagnostics GetDiagnostics()
    {
        var pendingSamples = PendingSamples;
        var oldestPendingMilliseconds = OldestPendingMilliseconds;
        return new(
            ConfirmedLostSamples: ConfirmedLostSamples,
            PendingSamples: pendingSamples,
            OldestPendingMilliseconds: oldestPendingMilliseconds,
            IsDelayed: pendingSamples > 0
                && oldestPendingMilliseconds >= PersistenceDelayThreshold.TotalMilliseconds,
            Failure: BackgroundFailure?.Message,
            FailureOccurrenceId: BackgroundFailure is null
                ? null
                : expectedSessionId.Value.ToString("N"));
    }

    public SqliteMonitorSessionPersistence(
        ISqliteDatabaseStore store,
        AgentWriteOwnerLease writeOwner,
        SessionId expectedSessionId,
        int channelCapacity,
        int maximumBatchSize,
        TimeSpan maximumBatchDelay,
        TimeProvider? timeProvider = null)
    {
        this.store = store;
        this.writeOwner = writeOwner;
        this.expectedSessionId = expectedSessionId;
        this.channelCapacity = channelCapacity;
        this.maximumBatchSize = maximumBatchSize;
        this.maximumBatchDelay = maximumBatchDelay;
        this.timeProvider = timeProvider ?? TimeProvider.System;
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
                []);
            await devices.UpsertAsync(
                new PersistedMonitorDevice(
                    session.SessionId,
                    MonitorSampleBatchWriter.PersistedDeviceId(sampleIdentity),
                    NormalizeName(target),
                    (int)target.ObjectId.Kind),
                cancellationToken);
        }

        InitializeWriter();
        started = true;
    }

    internal async Task ResumeAsync(
        MonitoringSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (started || session.SessionId != expectedSessionId)
        {
            throw new InvalidOperationException("监控持久化会话身份无效或已经恢复。");
        }

        writeOwner.AssertOwnership(store);
        var existing = await sessions.GetAsync(expectedSessionId, cancellationToken);
        if (existing is null)
        {
            throw new IOException("无法恢复不存在的监控持久化会话。");
        }

        InitializeWriter();
        started = true;
    }

    public bool TryWrite(MonitorSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (writerGate)
        {
            return started
                   && !completed
                   && sample.SessionId == expectedSessionId
                   && writer is not null
                   && writer.TryEnqueue(sample);
        }
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
        return GetRequiredWriter().FlushAsync(cancellationToken);
    }

    public async Task CompleteAsync(
        MonitoringSessionState finalState,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        await GetRequiredWriter().CompleteAndFlushAsync(cancellationToken);
        await sessions.CompleteAsync(
            expectedSessionId,
            finalState,
            endedAtUtc,
            cancellationToken);
        completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        var current = GetWriterSnapshot();
        if (current is not null)
        {
            await current.DisposeAsync();
        }
    }

    /// <summary>
    /// Replaces only a writer whose task has already faulted.  The caller must
    /// first divert incoming samples into its bounded rotation buffer; a new
    /// writer is installed only after a real no-op session-row write succeeds.
    /// This lets an ordinary disk-full/lock failure recover without treating a
    /// healthy fixed database as a rotation candidate.
    /// </summary>
    internal async Task RecoverFaultedWriterAsync(CancellationToken cancellationToken)
    {
        MonitorSampleBatchWriter failedWriter;
        lock (writerGate)
        {
            EnsureActive();
            failedWriter = writer
                ?? throw new IOException("The monitoring sample writer is unavailable for recovery.");
            if (failedWriter.Failure is null)
            {
                return;
            }
        }

        if (!ReferenceEquals(retiredFailedWriter, failedWriter))
        {
            try
            {
                await failedWriter.DisposeAsync();
            }
            catch when (failedWriter.Failure is not null)
            {
                // The failure is intentionally retained below until a
                // replacement can be proven writable.
            }

            retiredFailedWriter = failedWriter;
        }

        await VerifySessionRowCanBeWrittenAsync(cancellationToken);
        var replacement = new MonitorSampleBatchWriter(
            store,
            writeOwner,
            channelCapacity,
            maximumBatchSize,
            maximumBatchDelay,
            timeProvider);
        lock (writerGate)
        {
            EnsureActive();
            if (!ReferenceEquals(writer, failedWriter))
            {
                throw new InvalidOperationException(
                    "The monitoring writer changed while its failure was being recovered.");
            }

            writer = replacement;
            retiredFailedWriter = null;
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

    private void InitializeWriter()
    {
        var initialized = new MonitorSampleBatchWriter(
            store,
            writeOwner,
            channelCapacity,
            maximumBatchSize,
            maximumBatchDelay,
            timeProvider);
        lock (writerGate)
        {
            writer = initialized;
            retiredFailedWriter = null;
        }
    }

    private MonitorSampleBatchWriter? GetWriterSnapshot()
    {
        lock (writerGate)
        {
            return writer;
        }
    }

    private MonitorSampleBatchWriter GetRequiredWriter() =>
        GetWriterSnapshot()
        ?? throw new IOException("The monitoring sample writer is unavailable.");

    private async Task VerifySessionRowCanBeWrittenAsync(CancellationToken cancellationToken)
    {
        writeOwner.AssertOwnership(store);
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // This is deliberately value-preserving but still acquires SQLite's
        // write path. A read-only probe would wrongly claim recovery while a
        // full or locked data root still cannot accept monitoring commits.
        command.CommandText = """
            UPDATE monitor_sessions
            SET dropped_samples = dropped_samples + 0
            WHERE session_id = $session;
            """;
        command.Parameters.AddWithValue("$session", MonitorSessionRepository.ToDatabaseId(expectedSessionId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new IOException("The monitoring session is unavailable for writer recovery.");
        }
    }

    private static string NormalizeName(MonitorTarget target)
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
