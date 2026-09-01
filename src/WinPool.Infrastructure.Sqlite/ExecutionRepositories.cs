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
