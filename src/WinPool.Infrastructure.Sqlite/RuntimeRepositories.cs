using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Sqlite;

public sealed class AlgorithmRegistryRepository
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public AlgorithmRegistryRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public AlgorithmRegistryRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task RegisterAsync(
        AlgorithmIdentity algorithm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(algorithm);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm.EvidenceReference);
        AssertWriteOwnership();

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO algorithm_registry(
                algorithm_id, version, confidence, evidence_reference,
                registered_at_utc_ms)
            VALUES($id, $version, $confidence, $evidence, $registered)
            ON CONFLICT(algorithm_id, version) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", algorithm.Id.Trim());
        command.Parameters.AddWithValue("$version", algorithm.Version.Trim());
        command.Parameters.AddWithValue("$confidence", (int)algorithm.Confidence);
        command.Parameters.AddWithValue("$evidence", algorithm.EvidenceReference.Trim());
        command.Parameters.AddWithValue(
            "$registered",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return;
        }

        command.Parameters.Clear();
        command.CommandText = """
            SELECT confidence, evidence_reference
            FROM algorithm_registry
            WHERE algorithm_id = $id AND version = $version;
            """;
        command.Parameters.AddWithValue("$id", algorithm.Id.Trim());
        command.Parameters.AddWithValue("$version", algorithm.Version.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || reader.GetInt32(0) != (int)algorithm.Confidence
            || !string.Equals(
                reader.GetString(1),
                algorithm.EvidenceReference.Trim(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "同一算法版本的置信度或证据引用不可原地改写；请登记新版本。");
        }
    }

    public async Task<IReadOnlyList<AlgorithmIdentity>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT algorithm_id, version, confidence, evidence_reference
            FROM algorithm_registry
            ORDER BY algorithm_id, version;
            """;
        var results = new List<AlgorithmIdentity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    reader.GetString(0),
                    reader.GetString(1),
                    (AlgorithmConfidence)reader.GetInt32(2),
                    reader.GetString(3)));
        }

        return results;
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

public sealed record PersistedAgentSession(
    AgentInstanceId SessionId,
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    bool ShutdownClean);

public sealed class AgentSessionRepository
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public AgentSessionRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public AgentSessionRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task StartAsync(
        AgentInstanceId sessionId,
        int processId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Agent session ID is required.", nameof(sessionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_sessions(
                session_id, process_id, started_at_utc_ms, ended_at_utc_ms,
                shutdown_clean)
            VALUES($session, $process, $started, NULL, 0);
            """;
        command.Parameters.AddWithValue("$session", Id(sessionId.Value));
        command.Parameters.AddWithValue("$process", processId);
        command.Parameters.AddWithValue(
            "$started",
            startedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EndAsync(
        AgentInstanceId sessionId,
        DateTimeOffset endedAtUtc,
        bool shutdownClean,
        CancellationToken cancellationToken = default)
    {
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_sessions
            SET ended_at_utc_ms = $ended,
                shutdown_clean = $clean
            WHERE session_id = $session AND ended_at_utc_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$ended", endedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$clean", shutdownClean ? 1 : 0);
        command.Parameters.AddWithValue("$session", Id(sessionId.Value));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException(
                $"找不到活动 Agent 会话 {sessionId.Value:N}。");
        }
    }

    public async Task<IReadOnlyList<PersistedAgentSession>> RecoverOpenSessionsAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default)
    {
        AssertWriteOwnership();
        var open = await ListOpenAsync(cancellationToken);
        if (open.Count == 0)
        {
            return open;
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_sessions
            SET ended_at_utc_ms = $recovered
            WHERE ended_at_utc_ms IS NULL;
            """;
        command.Parameters.AddWithValue(
            "$recovered",
            recoveredAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return open;
    }

    public async Task<IReadOnlyList<PersistedAgentSession>> ListUncleanAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, process_id, started_at_utc_ms,
                   ended_at_utc_ms, shutdown_clean
            FROM agent_sessions
            WHERE shutdown_clean = 0
            ORDER BY started_at_utc_ms DESC, session_id DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$take", take);
        var results = new List<PersistedAgentSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    new AgentInstanceId(
                        Guid.ParseExact(reader.GetString(0), "N")),
                    reader.GetInt32(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    reader.IsDBNull(3)
                        ? null
                        : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    reader.GetInt32(4) != 0));
        }

        return results;
    }

    private async Task<IReadOnlyList<PersistedAgentSession>> ListOpenAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, process_id, started_at_utc_ms
            FROM agent_sessions
            WHERE ended_at_utc_ms IS NULL
            ORDER BY started_at_utc_ms, session_id;
            """;
        var results = new List<PersistedAgentSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    new AgentInstanceId(
                        Guid.ParseExact(reader.GetString(0), "N")),
                    reader.GetInt32(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    EndedAtUtc: null,
                    ShutdownClean: false));
        }

        return results;
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

    private static string Id(Guid value) => value.ToString("N");
}

public sealed record PersistedExternalToolState(
    ToolId ToolId,
    ToolAvailability Availability,
    string? ConfiguredPath,
    string? DetectedVersion,
    string? Sha256,
    DateTimeOffset DetectedAtUtc);

public sealed class ExternalToolStateRepository
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public ExternalToolStateRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ExternalToolStateRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task SaveAsync(
        ToolState state,
        DateTimeOffset detectedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.ToolId.Value);
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // schema v1 named this column signature_state. Until a migration splits
        // signature verification from availability, it stores ToolAvailability.
        command.CommandText = """
            INSERT INTO external_tools(
                tool_id, configured_path, detected_version, sha256,
                signature_state, detected_at_utc_ms)
            VALUES($tool, $path, $version, $sha, $availability, $detected)
            ON CONFLICT(tool_id) DO UPDATE SET
                configured_path = excluded.configured_path,
                detected_version = excluded.detected_version,
                sha256 = excluded.sha256,
                signature_state = excluded.signature_state,
                detected_at_utc_ms = excluded.detected_at_utc_ms;
            """;
        command.Parameters.AddWithValue("$tool", state.ToolId.Value);
        command.Parameters.AddWithValue(
            "$path",
            state.PathSource == ToolPathSource.CustomPath
                && !string.IsNullOrWhiteSpace(state.ExecutablePath)
                    ? state.ExecutablePath
                    : DBNull.Value);
        command.Parameters.AddWithValue(
            "$version",
            (object?)state.Version ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha", (object?)state.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$availability", (int)state.Availability);
        command.Parameters.AddWithValue(
            "$detected",
            detectedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PersistedExternalToolState?> GetAsync(
        ToolId toolId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT configured_path, detected_version, sha256,
                   signature_state, detected_at_utc_ms
            FROM external_tools
            WHERE tool_id = $tool;
            """;
        command.Parameters.AddWithValue("$tool", toolId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new(
            toolId,
            (ToolAvailability)reader.GetInt32(3),
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)));
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

public enum WorkerProcessSaveResult
{
    Applied,
    IgnoredStale
}

public sealed class WorkerProcessRepository
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public WorkerProcessRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public WorkerProcessRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task<WorkerProcessSaveResult> SaveAsync(
        AgentInstanceId agentSessionId,
        ProcessRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (agentSessionId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Agent session ID is required.",
                nameof(agentSessionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(registration.ProcessId);
        if (registration.ProcessInstanceId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Process instance ID is required.",
                nameof(registration));
        }
        if (!Enum.IsDefined(registration.State))
        {
            throw new ArgumentOutOfRangeException(
                nameof(registration),
                "Worker process state is invalid.");
        }
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO worker_processes(
                process_instance_id, process_id, agent_session_id, process_kind, correlation_id,
                started_at_utc_ms, last_heartbeat_utc_ms, state,
                owns_job_object, shutdown_deadline_utc_ms)
            VALUES(
                $instance, $process, $session, $kind, $correlation, $started,
                $heartbeat, $state, $ownsJob, $deadline)
            ON CONFLICT(process_instance_id) DO UPDATE SET
                process_id = excluded.process_id,
                agent_session_id = excluded.agent_session_id,
                process_kind = excluded.process_kind,
                correlation_id = excluded.correlation_id,
                started_at_utc_ms = excluded.started_at_utc_ms,
                last_heartbeat_utc_ms = excluded.last_heartbeat_utc_ms,
                state = excluded.state,
                owns_job_object = excluded.owns_job_object,
                shutdown_deadline_utc_ms = excluded.shutdown_deadline_utc_ms
            WHERE
                worker_processes.state = excluded.state
                OR (worker_processes.state = $starting
                    AND excluded.state IN ($running, $failed))
                OR (worker_processes.state = $running
                    AND excluded.state IN ($stopping, $exited, $unresponsive, $failed))
                OR (worker_processes.state = $stopping
                    AND excluded.state IN ($exited, $failed))
                OR (worker_processes.state = $unresponsive
                    AND excluded.state IN ($running, $exited, $failed));
            """;
        command.Parameters.AddWithValue(
            "$instance",
            registration.ProcessInstanceId.Value.ToString("N"));
        command.Parameters.AddWithValue("$process", registration.ProcessId);
        command.Parameters.AddWithValue("$session", agentSessionId.Value.ToString("N"));
        command.Parameters.AddWithValue("$kind", (int)registration.Kind);
        command.Parameters.AddWithValue(
            "$correlation",
            registration.CorrelationId.Value.ToString("N"));
        command.Parameters.AddWithValue(
            "$started",
            registration.StartedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$heartbeat",
            registration.LastHeartbeatUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$state", (int)registration.State);
        command.Parameters.AddWithValue("$starting", (int)SupervisedProcessState.Starting);
        command.Parameters.AddWithValue("$running", (int)SupervisedProcessState.Running);
        command.Parameters.AddWithValue("$stopping", (int)SupervisedProcessState.Stopping);
        command.Parameters.AddWithValue("$exited", (int)SupervisedProcessState.Exited);
        command.Parameters.AddWithValue(
            "$unresponsive",
            (int)SupervisedProcessState.Unresponsive);
        command.Parameters.AddWithValue("$failed", (int)SupervisedProcessState.Failed);
        command.Parameters.AddWithValue(
            "$ownsJob",
            registration.OwnsJobObject ? 1 : 0);
        command.Parameters.AddWithValue(
            "$deadline",
            registration.ShutdownDeadlineUtc is null
                ? DBNull.Value
                : registration.ShutdownDeadlineUtc.Value.ToUnixTimeMilliseconds());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 0
            ? WorkerProcessSaveResult.IgnoredStale
            : WorkerProcessSaveResult.Applied;
    }

    public async Task<IReadOnlyList<ProcessRegistration>> ListAsync(
        AgentInstanceId agentSessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT process_instance_id, process_id, process_kind, correlation_id,
                   started_at_utc_ms, last_heartbeat_utc_ms, state,
                   owns_job_object, shutdown_deadline_utc_ms
            FROM worker_processes
            WHERE agent_session_id = $session
            ORDER BY started_at_utc_ms, process_instance_id;
            """;
        command.Parameters.AddWithValue(
            "$session",
            agentSessionId.Value.ToString("N"));
        var results = new List<ProcessRegistration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    new ProcessInstanceId(Guid.ParseExact(reader.GetString(0), "N")),
                    reader.GetInt32(1),
                    (WorkerKind)reader.GetInt32(2),
                    new CorrelationId(Guid.ParseExact(reader.GetString(3), "N")),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                    (SupervisedProcessState)reader.GetInt32(6),
                    OwnsJobObject: reader.GetInt32(7) != 0,
                    ShutdownDeadlineUtc: reader.IsDBNull(8)
                        ? null
                        : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8))));
        }

        return results;
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
