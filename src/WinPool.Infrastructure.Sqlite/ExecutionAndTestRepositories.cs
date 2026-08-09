using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Sqlite;

public enum PersistedOperationState
{
    Planned,
    AwaitingAuthorization,
    Authorized,
    Running,
    Completed,
    Cancelled,
    Failed,
    Rejected
}

public sealed record PersistedOperation(
    OperationPlan Plan,
    PersistedOperationState State);

public sealed class OperationPlanRepository
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public OperationPlanRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public OperationPlanRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task SaveAsync(
        OperationPlan plan,
        PersistedOperationState state = PersistedOperationState.Planned,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.OperationId.Value == Guid.Empty
            || plan.EnvironmentId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(plan.PlanHash))
        {
            throw new ArgumentException("操作计划身份不完整。", nameof(plan));
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var planCommand = connection.CreateCommand();
        planCommand.Transaction = transaction;
        planCommand.CommandText = """
            INSERT INTO operation_plans(
                operation_id, plan_hash, environment_id, risk, state,
                sanitized_json, created_at_utc_ms)
            VALUES($operation, $hash, $environment, $risk, $state, $json, $created);
            """;
        planCommand.Parameters.AddWithValue("$operation", Id(plan.OperationId.Value));
        planCommand.Parameters.AddWithValue("$hash", plan.PlanHash);
        planCommand.Parameters.AddWithValue("$environment", Id(plan.EnvironmentId.Value));
        planCommand.Parameters.AddWithValue("$risk", (int)plan.Risk);
        planCommand.Parameters.AddWithValue("$state", (int)state);
        planCommand.Parameters.AddWithValue("$json", JsonSerializer.Serialize(plan, JsonOptions));
        planCommand.Parameters.AddWithValue(
            "$created",
            plan.CreatedAt.ToUnixTimeMilliseconds());
        await planCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var stepCommand = connection.CreateCommand();
        stepCommand.Transaction = transaction;
        stepCommand.CommandText = """
            INSERT INTO operation_steps(
                operation_id, step_id, sequence_no, state, sanitized_json)
            VALUES($operation, $step, $sequence, $state, $json);
            """;
        var operation = stepCommand.Parameters.Add("$operation", SqliteType.Text);
        var step = stepCommand.Parameters.Add("$step", SqliteType.Text);
        var sequence = stepCommand.Parameters.Add("$sequence", SqliteType.Integer);
        var stepState = stepCommand.Parameters.Add("$state", SqliteType.Integer);
        var json = stepCommand.Parameters.Add("$json", SqliteType.Text);
        stepCommand.Prepare();
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            operation.Value = Id(plan.OperationId.Value);
            step.Value = plan.Steps[index].Id;
            sequence.Value = index;
            stepState.Value = (int)ApplicationTaskState.Created;
            json.Value = JsonSerializer.Serialize(plan.Steps[index], JsonOptions);
            await stepCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PersistedOperation?> GetAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state, sanitized_json
            FROM operation_plans
            WHERE operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$operation", Id(operationId.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var plan = JsonSerializer.Deserialize<OperationPlan>(reader.GetString(1), JsonOptions)
            ?? throw new InvalidDataException("持久化操作计划为空。");
        return new PersistedOperation(
            plan,
            (PersistedOperationState)reader.GetInt32(0));
    }

    public async Task SetStateAsync(
        OperationId operationId,
        PersistedOperationState state,
        CancellationToken cancellationToken = default)
    {
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE operation_plans SET state = $state WHERE operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$operation", Id(operationId.Value));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException($"找不到操作计划 {operationId.Value:N}。");
        }
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

    internal static string Id(Guid value) => value.ToString("N");
}

public sealed record PersistedExecutionEvent(
    long EventId,
    ExecutionEvent Event);

public sealed class ExecutionEventRepository
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public ExecutionEventRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ExecutionEventRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task AppendAsync(
        ExecutionEvent executionEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionEvent.Code);
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO execution_events(
                operation_id, timestamp_utc_ms, kind, code, sanitized_message)
            VALUES($operation, $timestamp, $kind, $code, $message);
            """;
        command.Parameters.AddWithValue(
            "$operation",
            OperationPlanRepository.Id(executionEvent.OperationId.Value));
        command.Parameters.AddWithValue(
            "$timestamp",
            executionEvent.At.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$kind", (int)executionEvent.Kind);
        command.Parameters.AddWithValue("$code", executionEvent.Code.Trim());
        command.Parameters.AddWithValue("$message", executionEvent.Message ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersistedExecutionEvent>> ListAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, timestamp_utc_ms, kind, code, sanitized_message
            FROM execution_events
            WHERE operation_id = $operation
            ORDER BY timestamp_utc_ms, event_id;
            """;
        command.Parameters.AddWithValue(
            "$operation",
            OperationPlanRepository.Id(operationId.Value));
        var events = new List<PersistedExecutionEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(
                new PersistedExecutionEvent(
                    reader.GetInt64(0),
                    new ExecutionEvent(
                        operationId,
                        (ExecutionEventKind)reader.GetInt32(2),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                        reader.GetString(3),
                        reader.GetString(4))));
        }

        return events;
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

public sealed record PersistedSystemSupportAuditEvent(
    long EventId,
    SystemSupportAuditEvent Event);

public sealed class SystemSupportAuditRepository : ISystemSupportAuditSink
{
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public SystemSupportAuditRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public SystemSupportAuditRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async ValueTask WriteAsync(
        SystemSupportAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditEvent.PlanHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditEvent.Code);
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO system_support_audit_events(
                correlation_id, plan_hash, action_kind, stage, occurred_at_utc_ms,
                code, user_text_key, redacted_diagnostic, policy_rule_version)
            VALUES(
                $correlation, $plan, $action, $stage, $timestamp,
                $code, $userText, $diagnostic, $policy);
            """;
        command.Parameters.AddWithValue(
            "$correlation",
            OperationPlanRepository.Id(auditEvent.CorrelationId.Value));
        command.Parameters.AddWithValue("$plan", auditEvent.PlanHash.Trim());
        command.Parameters.AddWithValue("$action", (int)auditEvent.ActionKind);
        command.Parameters.AddWithValue("$stage", (int)auditEvent.Stage);
        command.Parameters.AddWithValue(
            "$timestamp",
            auditEvent.OccurredAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$code", auditEvent.Code.Trim());
        command.Parameters.AddWithValue("$userText", auditEvent.UserTextKey);
        command.Parameters.AddWithValue("$diagnostic", auditEvent.RedactedDiagnostic);
        command.Parameters.AddWithValue("$policy", auditEvent.PolicyRuleVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersistedSystemSupportAuditEvent>> ListAsync(
        string planHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                event_id, correlation_id, action_kind, stage, occurred_at_utc_ms,
                code, user_text_key, redacted_diagnostic, policy_rule_version
            FROM system_support_audit_events
            WHERE plan_hash = $plan
            ORDER BY occurred_at_utc_ms, event_id;
            """;
        command.Parameters.AddWithValue("$plan", planHash.Trim());
        var events = new List<PersistedSystemSupportAuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(
                new PersistedSystemSupportAuditEvent(
                    reader.GetInt64(0),
                    new SystemSupportAuditEvent(
                        new CorrelationId(Guid.ParseExact(reader.GetString(1), "N")),
                        planHash.Trim(),
                        (SystemSupportActionKind)reader.GetInt32(2),
                        (SystemSupportAuditStage)reader.GetInt32(3),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        reader.GetString(8))));
        }

        return events;
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

public enum PersistedTestRunState
{
    Created,
    Queued,
    Running,
    Verifying,
    Completed,
    Cancelled,
    Failed,
    Interrupted
}

public sealed record PersistedTestRun(
    TestRunId RunId,
    TestDefinitionId DefinitionId,
    PersistedTestRunState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string PlanHash,
    string EnvironmentSnapshotJson);

public sealed record PersistedTestWorkerEvent(
    long EventId,
    TestRunId RunId,
    string StepId,
    WorkerEventKind Kind,
    WorkerEventImportance Importance,
    DateTimeOffset OccurredAtUtc,
    string Code,
    int? ProcessId,
    int? ExitCode,
    int RawByteCount);

public sealed record PersistedTestMetric(
    string MetricId,
    double Value,
    string Unit,
    string Aggregation);

public sealed record PersistedStepMetric(
    string? StepId,
    string MetricId,
    double Value,
    string Unit,
    string Aggregation);

public sealed record PersistedTestStep(
    string StepId,
    int Sequence,
    ApplicationTaskState State,
    ToolId? ToolId);

public sealed class TestRunRepository
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public TestRunRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public TestRunRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task SaveDefinitionAsync(
        TestDefinition definition,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO test_definitions(
                definition_id, name, sanitized_json, created_at_utc_ms)
            VALUES($definition, $name, $json, $created)
            ON CONFLICT(definition_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue(
            "$definition",
            OperationPlanRepository.Id(definition.Id.Value));
        command.Parameters.AddWithValue("$name", definition.Name.Trim());
        command.Parameters.AddWithValue(
            "$json",
            JsonSerializer.Serialize(definition, JsonOptions));
        command.Parameters.AddWithValue("$created", createdAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateRunAsync(
        TestPlan plan,
        string environmentSnapshotJson,
        PersistedTestRunState state = PersistedTestRunState.Created,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentSnapshotJson);
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var runCommand = connection.CreateCommand();
        runCommand.Transaction = transaction;
        runCommand.CommandText = """
            INSERT INTO test_runs(
                run_id, definition_id, state, started_at_utc_ms,
                ended_at_utc_ms, plan_hash, environment_snapshot_json,
                plan_json)
            VALUES(
                $run, $definition, $state, $started, NULL, $hash,
                $environment, $plan);
            """;
        runCommand.Parameters.AddWithValue("$run", OperationPlanRepository.Id(plan.RunId.Value));
        runCommand.Parameters.AddWithValue(
            "$definition",
            OperationPlanRepository.Id(plan.DefinitionId.Value));
        runCommand.Parameters.AddWithValue("$state", (int)state);
        runCommand.Parameters.AddWithValue("$started", plan.CreatedAtUtc.ToUnixTimeMilliseconds());
        runCommand.Parameters.AddWithValue("$hash", plan.PlanHash);
        runCommand.Parameters.AddWithValue("$environment", environmentSnapshotJson);
        runCommand.Parameters.AddWithValue(
            "$plan",
            JsonSerializer.Serialize(plan, JsonOptions));
        await runCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var stepCommand = connection.CreateCommand();
        stepCommand.Transaction = transaction;
        stepCommand.CommandText = """
            INSERT INTO test_steps(
                run_id, step_id, sequence_no, state, tool_id, sanitized_json)
            VALUES($run, $step, $sequence, $state, $tool, $json);
            """;
        var run = stepCommand.Parameters.Add("$run", SqliteType.Text);
        var step = stepCommand.Parameters.Add("$step", SqliteType.Text);
        var sequence = stepCommand.Parameters.Add("$sequence", SqliteType.Integer);
        var stepState = stepCommand.Parameters.Add("$state", SqliteType.Integer);
        var tool = stepCommand.Parameters.Add("$tool", SqliteType.Text);
        var json = stepCommand.Parameters.Add("$json", SqliteType.Text);
        stepCommand.Prepare();
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var item = plan.Steps[index];
            run.Value = OperationPlanRepository.Id(plan.RunId.Value);
            step.Value = item.Id;
            sequence.Value = index;
            stepState.Value = (int)ApplicationTaskState.Created;
            tool.Value = item.ToolId is { } toolId ? toolId.Value : DBNull.Value;
            json.Value = JsonSerializer.Serialize(item, JsonOptions);
            await stepCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PersistedTestRun?> GetAsync(
        TestRunId runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                definition_id, state, started_at_utc_ms, ended_at_utc_ms,
                plan_hash, environment_snapshot_json
            FROM test_runs
            WHERE run_id = $run;
            """;
        command.Parameters.AddWithValue("$run", OperationPlanRepository.Id(runId.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PersistedTestRun(
            runId,
            new TestDefinitionId(Guid.ParseExact(reader.GetString(0), "N")),
            (PersistedTestRunState)reader.GetInt32(1),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            reader.IsDBNull(3)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            reader.GetString(4),
            reader.GetString(5));
    }

    public async Task<IReadOnlyList<PersistedTestRun>> ListRunsAsync(
        IReadOnlyCollection<PersistedTestRunState>? states,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var stateValues = states?.Distinct().ToArray() ?? [];
        var filter = stateValues.Length == 0
            ? string.Empty
            : $"WHERE state IN ({string.Join(
                ", ",
                stateValues.Select((_, index) => $"$state{index}"))})";
        command.CommandText = $"""
            SELECT run_id, definition_id, state, started_at_utc_ms,
                   ended_at_utc_ms, plan_hash, environment_snapshot_json
            FROM test_runs
            {filter}
            ORDER BY started_at_utc_ms DESC, run_id DESC
            LIMIT $limit;
            """;
        for (var index = 0; index < stateValues.Length; index++)
        {
            command.Parameters.AddWithValue(
                $"$state{index}",
                (int)stateValues[index]);
        }

        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<PersistedTestRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    new TestRunId(Guid.ParseExact(reader.GetString(0), "N")),
                    new TestDefinitionId(Guid.ParseExact(reader.GetString(1), "N")),
                    (PersistedTestRunState)reader.GetInt32(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    reader.IsDBNull(4)
                        ? null
                        : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    reader.GetString(5),
                    reader.GetString(6)));
        }

        return results;
    }

    public async Task<TestPlan?> GetPlanAsync(
        TestRunId runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT plan_json
            FROM test_runs
            WHERE run_id=$run;
            """;
        command.Parameters.AddWithValue(
            "$run",
            OperationPlanRepository.Id(runId.Value));
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (json is null
            || string.IsNullOrWhiteSpace(json)
            || StringComparer.Ordinal.Equals(json.Trim(), "{}"))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TestPlan>(json, JsonOptions)
                ?? throw new InvalidDataException(
                    "The persisted test plan was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The persisted test plan could not be read.",
                exception);
        }
    }

    public async Task<IReadOnlyList<TestRunId>> RecoverInterruptedRunsAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default)
    {
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        var recovered = new List<TestRunId>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT run_id
                FROM test_runs
                WHERE state IN ($queued, $running, $verifying)
                ORDER BY started_at_utc_ms, run_id;
                """;
            select.Parameters.AddWithValue(
                "$queued",
                (int)PersistedTestRunState.Queued);
            select.Parameters.AddWithValue(
                "$running",
                (int)PersistedTestRunState.Running);
            select.Parameters.AddWithValue(
                "$verifying",
                (int)PersistedTestRunState.Verifying);
            await using var reader = await select.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                recovered.Add(
                    new(
                        Guid.ParseExact(
                            reader.GetString(0),
                            "N")));
            }
        }

        if (recovered.Count > 0)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE test_runs
                SET state=$interrupted, ended_at_utc_ms=$ended
                WHERE state IN ($queued, $running, $verifying);
                """;
            update.Parameters.AddWithValue(
                "$interrupted",
                (int)PersistedTestRunState.Interrupted);
            update.Parameters.AddWithValue(
                "$ended",
                recoveredAtUtc.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue(
                "$queued",
                (int)PersistedTestRunState.Queued);
            update.Parameters.AddWithValue(
                "$running",
                (int)PersistedTestRunState.Running);
            update.Parameters.AddWithValue(
                "$verifying",
                (int)PersistedTestRunState.Verifying);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return recovered;
    }

    public async Task ResumeInterruptedAsync(
        TestRunId runId,
        string expectedPlanHash,
        DateTimeOffset resumedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPlanHash);
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await using var run = connection.CreateCommand();
        run.Transaction = transaction;
        run.CommandText = """
            UPDATE test_runs
            SET state=$running, started_at_utc_ms=$resumed,
                ended_at_utc_ms=NULL
            WHERE run_id=$run AND state=$interrupted
              AND plan_hash=$hash;
            """;
        run.Parameters.AddWithValue(
            "$running",
            (int)PersistedTestRunState.Running);
        run.Parameters.AddWithValue(
            "$resumed",
            resumedAtUtc.ToUnixTimeMilliseconds());
        run.Parameters.AddWithValue(
            "$run",
            OperationPlanRepository.Id(runId.Value));
        run.Parameters.AddWithValue(
            "$interrupted",
            (int)PersistedTestRunState.Interrupted);
        run.Parameters.AddWithValue("$hash", expectedPlanHash.Trim());
        if (await run.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "Only an interrupted run with the same immutable plan hash can resume.");
        }

        await using var steps = connection.CreateCommand();
        steps.Transaction = transaction;
        steps.CommandText = """
            UPDATE test_steps
            SET state=$created
            WHERE run_id=$run AND state NOT IN ($succeeded);
            """;
        steps.Parameters.AddWithValue(
            "$created",
            (int)ApplicationTaskState.Created);
        steps.Parameters.AddWithValue(
            "$run",
            OperationPlanRepository.Id(runId.Value));
        steps.Parameters.AddWithValue(
            "$succeeded",
            (int)ApplicationTaskState.Succeeded);
        await steps.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteAsync(
        TestRunId runId,
        PersistedTestRunState state,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (state is not (
            PersistedTestRunState.Completed
            or PersistedTestRunState.Cancelled
            or PersistedTestRunState.Failed
            or PersistedTestRunState.Interrupted))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE test_runs
            SET state = $state, ended_at_utc_ms = $ended
            WHERE run_id = $run;
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$ended", endedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$run", OperationPlanRepository.Id(runId.Value));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException($"找不到测试运行 {runId.Value:N}。");
        }
    }

    public async Task UpdateStepStateAsync(
        TestRunId runId,
        string stepId,
        ApplicationTaskState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE test_steps
            SET state = $state
            WHERE run_id = $run AND step_id = $step;
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue(
            "$run",
            OperationPlanRepository.Id(runId.Value));
        command.Parameters.AddWithValue("$step", stepId.Trim());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException(
                $"找不到测试步骤 {runId.Value:N}/{stepId}。");
        }
    }

    public async Task AddMetricAsync(
        TestRunId runId,
        string? stepId,
        string metricName,
        double value,
        string unit,
        string aggregation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregation);
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO test_metrics(
                run_id, step_id, metric_name, metric_value, unit, aggregation)
            VALUES($run, $step, $name, $value, $unit, $aggregation)
            ON CONFLICT(run_id, step_id, metric_name, aggregation) DO UPDATE SET
                metric_value = excluded.metric_value,
                unit = excluded.unit;
            """;
        command.Parameters.AddWithValue("$run", OperationPlanRepository.Id(runId.Value));
        command.Parameters.AddWithValue(
            "$step",
            string.IsNullOrWhiteSpace(stepId) ? DBNull.Value : stepId.Trim());
        command.Parameters.AddWithValue("$name", metricName.Trim());
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$unit", unit.Trim());
        command.Parameters.AddWithValue("$aggregation", aggregation.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersistedTestMetric>> ListMetricsAsync(
        TestRunId runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT metric_name, metric_value, unit, aggregation
            FROM test_metrics
            WHERE run_id = $run
            ORDER BY metric_name, aggregation;
            """;
        command.Parameters.AddWithValue(
            "$run",
            OperationPlanRepository.Id(runId.Value));
        var results = new List<PersistedTestMetric>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    reader.GetString(0),
                    reader.GetDouble(1),
                    reader.GetString(2),
                    reader.GetString(3)));
        }

        return results;
    }

    public async Task<IReadOnlyList<PersistedStepMetric>> ListStepMetricsAsync(
        TestRunId runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT step_id, metric_name, metric_value, unit, aggregation
            FROM test_metrics
            WHERE run_id = $run
            ORDER BY step_id, metric_name, aggregation;
            """;
        command.Parameters.AddWithValue(
            "$run",
            OperationPlanRepository.Id(runId.Value));
        var results = new List<PersistedStepMetric>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.GetString(1),
                    reader.GetDouble(2),
                    reader.GetString(3),
                    reader.GetString(4)));
        }

        return results;
    }

    public async Task<IReadOnlyList<PersistedTestStep>> ListStepsAsync(
        TestRunId runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT step_id, sequence_no, state, tool_id
            FROM test_steps
            WHERE run_id = $run
            ORDER BY sequence_no, step_id;
            """;
        command.Parameters.AddWithValue(
            "$run",
            OperationPlanRepository.Id(runId.Value));
        var results = new List<PersistedTestStep>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    (ApplicationTaskState)reader.GetInt32(2),
                    reader.IsDBNull(3)
                        ? null
                        : new ToolId(reader.GetString(3))));
        }

        return results;
    }

    public async Task AddWorkerEventsAsync(
        TestRunId runId,
        IReadOnlyList<WorkerEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        if (events.Any(item =>
                item.RunId != runId
                || string.IsNullOrWhiteSpace(item.StepId)
                || string.IsNullOrWhiteSpace(item.Code)))
        {
            throw new ArgumentException(
                "Worker event batches must belong to one run and contain typed codes.",
                nameof(events));
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO test_events(
                run_id, step_id, event_kind, importance, occurred_at_utc_ms,
                code, process_id, exit_code, raw_byte_count)
            VALUES(
                $run, $step, $kind, $importance, $occurred,
                $code, $process, $exit, $bytes);
            """;
        var run = command.Parameters.Add("$run", SqliteType.Text);
        var step = command.Parameters.Add("$step", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Integer);
        var importance = command.Parameters.Add("$importance", SqliteType.Integer);
        var occurred = command.Parameters.Add("$occurred", SqliteType.Integer);
        var code = command.Parameters.Add("$code", SqliteType.Text);
        var process = command.Parameters.Add("$process", SqliteType.Integer);
        var exit = command.Parameters.Add("$exit", SqliteType.Integer);
        var bytes = command.Parameters.Add("$bytes", SqliteType.Integer);
        command.Prepare();
        foreach (var item in events)
        {
            run.Value = OperationPlanRepository.Id(runId.Value);
            step.Value = item.StepId.Trim();
            kind.Value = (int)item.Kind;
            importance.Value = (int)item.Importance;
            occurred.Value = item.OccurredAtUtc.ToUnixTimeMilliseconds();
            code.Value = item.Code.Trim();
            process.Value = item.ProcessId is null ? DBNull.Value : item.ProcessId.Value;
            exit.Value = item.ExitCode is null ? DBNull.Value : item.ExitCode.Value;
            bytes.Value = item.RawBytes.Length;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersistedTestWorkerEvent>> ListWorkerEventsAsync(
        TestRunId runId,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, step_id, event_kind, importance,
                   occurred_at_utc_ms, code, process_id, exit_code,
                   raw_byte_count
            FROM test_events
            WHERE run_id = $run
            ORDER BY event_id
            LIMIT $take;
            """;
        command.Parameters.AddWithValue(
            "$run",
            OperationPlanRepository.Id(runId.Value));
        command.Parameters.AddWithValue("$take", take);
        var results = new List<PersistedTestWorkerEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new(
                    reader.GetInt64(0),
                    runId,
                    reader.GetString(1),
                    (WorkerEventKind)reader.GetInt32(2),
                    (WorkerEventImportance)reader.GetInt32(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.GetInt32(8)));
        }

        return results;
    }

    public async Task AddLatencyHistogramAsync(
        TestRunId runId,
        string stepId,
        IReadOnlyDictionary<long, long> buckets,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentNullException.ThrowIfNull(buckets);
        if (buckets.Any(pair => pair.Key <= 0 || pair.Value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(buckets));
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO latency_histograms(
                run_id, step_id, bucket_upper_ns, sample_count)
            VALUES($run, $step, $bucket, $count)
            ON CONFLICT(run_id, step_id, bucket_upper_ns) DO UPDATE SET
                sample_count = excluded.sample_count;
            """;
        var run = command.Parameters.Add("$run", SqliteType.Text);
        var step = command.Parameters.Add("$step", SqliteType.Text);
        var bucket = command.Parameters.Add("$bucket", SqliteType.Integer);
        var count = command.Parameters.Add("$count", SqliteType.Integer);
        command.Prepare();
        foreach (var pair in buckets.OrderBy(pair => pair.Key))
        {
            run.Value = OperationPlanRepository.Id(runId.Value);
            step.Value = stepId.Trim();
            bucket.Value = pair.Key;
            count.Value = pair.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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
