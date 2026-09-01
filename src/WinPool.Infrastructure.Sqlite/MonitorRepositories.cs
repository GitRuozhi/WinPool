using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Sqlite;

public sealed record PersistedMonitorSession(
    SessionId SessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string ClockSource,
    MonitoringSessionState State,
    long DroppedSamples);

public sealed record PersistedMonitorDevice(
    SessionId SessionId,
    string DeviceId,
    string SanitizedName,
    int SourceKind);

public sealed record PersistedMonitorSample(
    long RowId,
    SessionId SessionId,
    string DeviceId,
    DateTimeOffset SampledAtUtc,
    double ActivityPercent,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    double QueueLength);

public readonly record struct MonitorSampleCursor(
    long TimestampUtcMilliseconds,
    long RowId);

public sealed record MonitorSamplePage(
    IReadOnlyList<PersistedMonitorSample> Items,
    MonitorSampleCursor? Continuation);

public sealed class MonitorSessionRepository
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public MonitorSessionRepository(WinPoolSqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public MonitorSessionRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        ArgumentNullException.ThrowIfNull(writeOwner);
        writeOwner.AssertOwnership(store);
        this.writeOwner = writeOwner;
    }

    public async Task CreateAsync(
        PersistedMonitorSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.ClockSource);
        if (session.DroppedSamples < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(session));
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO monitor_sessions(
                session_id, started_at_utc_ms, ended_at_utc_ms,
                clock_source, state, dropped_samples)
            VALUES(
                $session, $started, $ended, $clock, $state, $dropped);
            """;
        command.Parameters.AddWithValue("$session", ToDatabaseId(session.SessionId));
        command.Parameters.AddWithValue("$started", session.StartedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$ended",
            session.EndedAtUtc is { } ended
                ? ended.ToUnixTimeMilliseconds()
                : DBNull.Value);
        command.Parameters.AddWithValue("$clock", session.ClockSource.Trim());
        command.Parameters.AddWithValue("$state", (int)session.State);
        command.Parameters.AddWithValue("$dropped", session.DroppedSamples);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PersistedMonitorSession?> GetAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT started_at_utc_ms, ended_at_utc_ms, clock_source, state, dropped_samples
            FROM monitor_sessions
            WHERE session_id = $session;
            """;
        command.Parameters.AddWithValue("$session", ToDatabaseId(sessionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadSession(sessionId, reader);
    }

    public async Task CompleteAsync(
        SessionId sessionId,
        MonitoringSessionState finalState,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (finalState is not (
            MonitoringSessionState.Stopped
            or MonitoringSessionState.Interrupted
            or MonitoringSessionState.Failed))
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalState),
                finalState,
                "会话只能完成为 Stopped、Interrupted 或 Failed。");
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE monitor_sessions
            SET state = $state, ended_at_utc_ms = $ended
            WHERE session_id = $session;
            """;
        command.Parameters.AddWithValue("$state", (int)finalState);
        command.Parameters.AddWithValue("$ended", endedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$session", ToDatabaseId(sessionId));
        await EnsureExactlyOneRowAsync(command, sessionId, cancellationToken);
    }

    public async Task AddDroppedSamplesAsync(
        SessionId sessionId,
        long count,
        CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE monitor_sessions
            SET dropped_samples = dropped_samples + $count
            WHERE session_id = $session;
            """;
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$session", ToDatabaseId(sessionId));
        await EnsureExactlyOneRowAsync(command, sessionId, cancellationToken);
    }

    public async Task<int> RecoverInterruptedSessionsAsync(
        DateTimeOffset recoveredAtUtc,
        long minimumUnflushedSamples = 1,
        CancellationToken cancellationToken = default)
    {
        if (minimumUnflushedSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumUnflushedSamples));
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE monitor_sessions
            SET state = $interrupted,
                ended_at_utc_ms = COALESCE(ended_at_utc_ms, $recovered),
                dropped_samples = dropped_samples + $minimumLost
            WHERE state IN ($created, $starting, $running, $stopping)
                AND ended_at_utc_ms IS NULL;
            """;
        command.Parameters.AddWithValue(
            "$interrupted",
            (int)MonitoringSessionState.Interrupted);
        command.Parameters.AddWithValue("$recovered", recoveredAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$minimumLost", minimumUnflushedSamples);
        command.Parameters.AddWithValue("$created", (int)MonitoringSessionState.Created);
        command.Parameters.AddWithValue("$starting", (int)MonitoringSessionState.Starting);
        command.Parameters.AddWithValue("$running", (int)MonitoringSessionState.Running);
        command.Parameters.AddWithValue("$stopping", (int)MonitoringSessionState.Stopping);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PersistedMonitorSession ReadSession(
        SessionId sessionId,
        SqliteDataReader reader) =>
        new(
            sessionId,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
            reader.IsDBNull(1)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            reader.GetString(2),
            (MonitoringSessionState)reader.GetInt32(3),
            reader.GetInt64(4));

    private static async Task EnsureExactlyOneRowAsync(
        SqliteCommand command,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException(
                $"找不到监控会话 {sessionId.Value:N}。");
        }
    }

    internal static string ToDatabaseId(SessionId sessionId) =>
        sessionId.Value.ToString("N");

    private void AssertWriteOwnership()
    {
        if (writeOwner is null)
        {
            throw new AgentWriteOwnershipException(
                "此 repository 是只读实例；写入需要 AgentWriteOwnerLease。");
        }

        writeOwner.AssertOwnership(store);
    }
}

public sealed class MonitorDeviceRepository
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public MonitorDeviceRepository(WinPoolSqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public MonitorDeviceRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        ArgumentNullException.ThrowIfNull(writeOwner);
        writeOwner.AssertOwnership(store);
        this.writeOwner = writeOwner;
    }

    public async Task UpsertAsync(
        PersistedMonitorDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.DeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.SanitizedName);

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO monitor_devices(
                session_id, device_id, sanitized_name, source_kind)
            VALUES($session, $device, $name, $source)
            ON CONFLICT(session_id, device_id) DO UPDATE SET
                sanitized_name = excluded.sanitized_name,
                source_kind = excluded.source_kind;
            """;
        command.Parameters.AddWithValue(
            "$session",
            MonitorSessionRepository.ToDatabaseId(device.SessionId));
        command.Parameters.AddWithValue("$device", device.DeviceId.Trim());
        command.Parameters.AddWithValue("$name", device.SanitizedName.Trim());
        command.Parameters.AddWithValue("$source", device.SourceKind);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersistedMonitorDevice>> ListAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, sanitized_name, source_kind
            FROM monitor_devices
            WHERE session_id = $session
            ORDER BY device_id;
            """;
        command.Parameters.AddWithValue(
            "$session",
            MonitorSessionRepository.ToDatabaseId(sessionId));
        var devices = new List<PersistedMonitorDevice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(
                new PersistedMonitorDevice(
                    sessionId,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2)));
        }

        return devices;
    }

    private void AssertWriteOwnership()
    {
        if (writeOwner is null)
        {
            throw new AgentWriteOwnershipException(
                "此 repository 是只读实例；写入需要 AgentWriteOwnerLease。");
        }

        writeOwner.AssertOwnership(store);
    }
}

public sealed class MonitorSampleRepository
{
    public const int MaximumPageSize = 10_000;

    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public MonitorSampleRepository(WinPoolSqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public MonitorSampleRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        ArgumentNullException.ThrowIfNull(writeOwner);
        writeOwner.AssertOwnership(store);
        this.writeOwner = writeOwner;
    }

    public async Task WriteBatchAsync(
        IReadOnlyList<MonitorSample> batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var deviceCommand = connection.CreateCommand();
        deviceCommand.Transaction = transaction;
        deviceCommand.CommandText = """
            INSERT INTO monitor_devices(
                session_id, device_id, sanitized_name, source_kind)
            VALUES($session, $device, $name, $source)
            ON CONFLICT(session_id, device_id) DO NOTHING;
            """;
        var deviceSession = deviceCommand.Parameters.Add("$session", SqliteType.Text);
        var deviceIdentity = deviceCommand.Parameters.Add("$device", SqliteType.Text);
        var deviceName = deviceCommand.Parameters.Add("$name", SqliteType.Text);
        var deviceSource = deviceCommand.Parameters.Add("$source", SqliteType.Integer);
        deviceCommand.Prepare();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO monitor_samples(
                session_id, device_id, timestamp_utc_ms, activity_pct,
                read_bytes_per_sec, write_bytes_per_sec, queue_length)
            VALUES(
                $session, $device, $timestamp, $activity,
                $read, $write, $queue);
            """;
        var session = command.Parameters.Add("$session", SqliteType.Text);
        var device = command.Parameters.Add("$device", SqliteType.Text);
        var timestamp = command.Parameters.Add("$timestamp", SqliteType.Integer);
        var activity = command.Parameters.Add("$activity", SqliteType.Real);
        var read = command.Parameters.Add("$read", SqliteType.Real);
        var write = command.Parameters.Add("$write", SqliteType.Real);
        var queue = command.Parameters.Add("$queue", SqliteType.Real);
        command.Prepare();

        foreach (var sample in batch)
        {
            ArgumentNullException.ThrowIfNull(sample);
            var persistedDeviceId = MonitorSampleBatchWriter.PersistedDeviceId(sample);
            deviceSession.Value = MonitorSessionRepository.ToDatabaseId(sample.SessionId);
            deviceIdentity.Value = persistedDeviceId;
            deviceName.Value = $"{sample.TargetId.Kind} {persistedDeviceId[..8]}";
            deviceSource.Value = (int)sample.TargetId.Kind;
            await deviceCommand.ExecuteNonQueryAsync(cancellationToken);

            session.Value = MonitorSessionRepository.ToDatabaseId(sample.SessionId);
            device.Value = persistedDeviceId;
            timestamp.Value = sample.SampledAtUtc.ToUnixTimeMilliseconds();
            activity.Value = Metric(sample, MonitorMetricKind.ActiveTimePercent);
            read.Value = Metric(sample, MonitorMetricKind.ReadBytesPerSecond);
            write.Value = Metric(sample, MonitorMetricKind.WriteBytesPerSecond);
            queue.Value = Metric(sample, MonitorMetricKind.AverageQueueLength);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task<MonitorSamplePage> ReadPageAsync(
        SessionId sessionId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int pageSize,
        MonitorSampleCursor? after = null,
        string? deviceId = null,
        CancellationToken cancellationToken = default) =>
        ReadPageCoreAsync(
            sessionId,
            fromUtc,
            toUtc,
            pageSize,
            after,
            deviceId,
            includeContinuation: true,
            cancellationToken);

    public async Task<IReadOnlyList<PersistedMonitorSample>> ReadRangeAsync(
        SessionId sessionId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maximumResults = MaximumPageSize,
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        var page = await ReadPageCoreAsync(
            sessionId,
            fromUtc,
            toUtc,
            maximumResults,
            after: null,
            deviceId,
            includeContinuation: false,
            cancellationToken);
        return page.Items;
    }

    private async Task<MonitorSamplePage> ReadPageCoreAsync(
        SessionId sessionId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int pageSize,
        MonitorSampleCursor? after,
        string? deviceId,
        bool includeContinuation,
        CancellationToken cancellationToken)
    {
        if (fromUtc > toUtc)
        {
            throw new ArgumentException("采样范围起点不能晚于终点。", nameof(fromUtc));
        }

        if (pageSize is <= 0 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                rowid, device_id, timestamp_utc_ms, activity_pct,
                read_bytes_per_sec, write_bytes_per_sec, queue_length
            FROM monitor_samples
            WHERE session_id = $session
                AND timestamp_utc_ms >= $from
                AND timestamp_utc_ms < $to
                AND ($device IS NULL OR device_id = $device)
                AND (
                    $cursorTimestamp IS NULL
                    OR timestamp_utc_ms > $cursorTimestamp
                    OR (timestamp_utc_ms = $cursorTimestamp AND rowid > $cursorRowId))
            ORDER BY timestamp_utc_ms, rowid
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue(
            "$session",
            MonitorSessionRepository.ToDatabaseId(sessionId));
        command.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", toUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$device",
            string.IsNullOrWhiteSpace(deviceId) ? DBNull.Value : deviceId.Trim());
        command.Parameters.AddWithValue(
            "$cursorTimestamp",
            after is { } cursor ? cursor.TimestampUtcMilliseconds : DBNull.Value);
        command.Parameters.AddWithValue(
            "$cursorRowId",
            after is { } rowCursor ? rowCursor.RowId : DBNull.Value);
        command.Parameters.AddWithValue(
            "$limit",
            checked(pageSize + (includeContinuation ? 1 : 0)));

        var samples = new List<PersistedMonitorSample>(pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(
                new PersistedMonitorSample(
                    reader.GetInt64(0),
                    sessionId,
                    reader.GetString(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6)));
        }

        MonitorSampleCursor? continuation = null;
        if (includeContinuation && samples.Count > pageSize)
        {
            samples.RemoveAt(samples.Count - 1);
            var last = samples[^1];
            continuation = new MonitorSampleCursor(
                last.SampledAtUtc.ToUnixTimeMilliseconds(),
                last.RowId);
        }

        return new MonitorSamplePage(samples, continuation);
    }

    private static double Metric(MonitorSample sample, MonitorMetricKind kind) =>
        sample.Values.FirstOrDefault(value => value.Kind == kind)?.Value ?? 0d;

    private void AssertWriteOwnership()
    {
        if (writeOwner is null)
        {
            throw new AgentWriteOwnershipException(
                "此 repository 是只读实例；写入需要 AgentWriteOwnerLease。");
        }

        writeOwner.AssertOwnership(store);
    }
}
