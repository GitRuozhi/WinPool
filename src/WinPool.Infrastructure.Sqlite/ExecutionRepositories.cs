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
    Rejected,
    Prepared,
    Accepted,
    PartiallyCompleted,
    OutcomeUnknown
}

public enum PersistedOperationStepState
{
    NotStarted,
    PreparingCall,
    CallIssued,
    AwaitingProvider,
    Verifying,
    Verified,
    Failed,
    OutcomeUnknown,
    Skipped
}

public sealed record PersistedOperation(
    OperationPlan Plan,
    PersistedOperationState State,
    Guid? PreparationId = null,
    string? PreparationIntentHash = null,
    string? AuthorizationDigest = null,
    DateTimeOffset? AcceptedAt = null);

public sealed record PersistedOperationStep(
    string StepId,
    int Sequence,
    PersistedOperationStepState State,
    string? TargetJson,
    string? EvidenceJson,
    DateTimeOffset? UpdatedAt);

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
        if (plan.Risk >= RiskLevel.R4StorageStructureMutation)
        {
            throw new InvalidOperationException("真实写入计划必须使用带 PreparationId 的 PrepareAsync。");
        }
        await SaveCoreAsync(plan, state, null, null, cancellationToken);
    }

    private async Task SaveCoreAsync(
        OperationPlan plan,
        PersistedOperationState state,
        Guid? preparationId,
        string? preparationIntentHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.OperationId.Value == Guid.Empty
            || plan.EnvironmentId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(plan.PlanHash))
        {
            throw new ArgumentException("操作计划身份不完整。", nameof(plan));
        }
        if (plan.RealOperation is not null
            && plan.Risk < RiskLevel.R4StorageStructureMutation)
        {
            throw new ArgumentException("真实操作不能写入低风险模拟记录。", nameof(plan));
        }
        if (plan.Risk >= RiskLevel.R4StorageStructureMutation
            && state != PersistedOperationState.Prepared)
        {
            throw new InvalidOperationException("真实写入计划必须先以 Prepared 状态持久化。");
        }
        if (plan.Risk >= RiskLevel.R4StorageStructureMutation
            && (preparationId is null || string.IsNullOrWhiteSpace(preparationIntentHash)))
        {
            throw new InvalidOperationException("真实写入准备身份不完整。");
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (plan.Risk >= RiskLevel.R4StorageStructureMutation)
        {
            await using var barrier = connection.CreateCommand();
            barrier.Transaction = transaction;
            barrier.CommandText = """
                SELECT COUNT(*) FROM operation_plans
                WHERE risk >= 4 AND state NOT IN (4, 5, 6, 7, 10);
                """;
            if (Convert.ToInt64(await barrier.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                throw new InvalidOperationException("已有未终结真实操作，不能准备另一个计划。");
            }
        }
        await using var planCommand = connection.CreateCommand();
        planCommand.Transaction = transaction;
        planCommand.CommandText = """
            INSERT INTO operation_plans(
                operation_id, plan_hash, environment_id, risk, state,
                sanitized_json, created_at_utc_ms, preparation_id, preparation_intent_hash)
            VALUES($operation, $hash, $environment, $risk, $state, $json, $created,
                $preparation, $intentHash);
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
        planCommand.Parameters.AddWithValue("$preparation",
            preparationId.HasValue ? (object)Id(preparationId.Value) : DBNull.Value);
        planCommand.Parameters.AddWithValue("$intentHash",
            (object?)preparationIntentHash ?? DBNull.Value);
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
            stepState.Value = (int)PersistedOperationStepState.NotStarted;
            json.Value = JsonSerializer.Serialize(plan.Steps[index], JsonOptions);
            await stepCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PersistedOperation> PrepareAsync(
        OperationPlan plan,
        Guid preparationId,
        string preparationIntentHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (preparationId == Guid.Empty)
        {
            throw new ArgumentException("PreparationId 不能为空。", nameof(preparationId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(preparationIntentHash);
        if (plan.Risk < RiskLevel.R4StorageStructureMutation)
        {
            throw new ArgumentException("PrepareAsync 只接受真实存储修改计划。", nameof(plan));
        }
        var existing = await GetByPreparationIdAsync(preparationId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.PreparationIntentHash, preparationIntentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("同一 PreparationId 对应不同操作意图。");
            }
            return existing;
        }
        existing = await GetAsync(plan.OperationId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Plan.PlanHash, plan.PlanHash, StringComparison.Ordinal)
                || existing.PreparationId != preparationId
                || !string.Equals(existing.PreparationIntentHash, preparationIntentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("同一 OperationId 对应不同计划或准备身份。");
            }
            return existing;
        }
        try
        {
            await SaveCoreAsync(plan, PersistedOperationState.Prepared,
                preparationId, preparationIntentHash, cancellationToken);
            return new PersistedOperation(plan, PersistedOperationState.Prepared,
                preparationId, preparationIntentHash);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            existing = await GetByPreparationIdAsync(preparationId, cancellationToken);
            if (existing is not null
                && string.Equals(existing.PreparationIntentHash, preparationIntentHash,
                    StringComparison.Ordinal))
            {
                return existing;
            }
            if (existing is not null)
            {
                throw new InvalidOperationException("同一 PreparationId 对应不同操作意图。", exception);
            }
            throw;
        }
    }

    public async Task<PersistedOperation> AcceptAsync(
        OperationId operationId,
        string planHash,
        string authorizationDigest,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationDigest);
        if (authorizationDigest.Length != 64
            || authorizationDigest.Any(character => character is not
                (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("授权摘要必须是小写 SHA-256 十六进制值。",
                nameof(authorizationDigest));
        }
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE operation_plans
            SET state = $accepted, authorization_digest = $digest,
                accepted_at_utc_ms = $at
            WHERE operation_id = $operation AND plan_hash = $hash
              AND risk >= 4 AND state = $prepared AND authorization_digest IS NULL;
            """;
        command.Parameters.AddWithValue("$accepted", (int)PersistedOperationState.Accepted);
        command.Parameters.AddWithValue("$digest", authorizationDigest);
        command.Parameters.AddWithValue("$at", acceptedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$operation", Id(operationId.Value));
        command.Parameters.AddWithValue("$hash", planHash);
        command.Parameters.AddWithValue("$prepared", (int)PersistedOperationState.Prepared);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("计划已接受、状态已改变或哈希不匹配；不能重复启动。");
        }
        await using var steps = connection.CreateCommand();
        steps.Transaction = transaction;
        steps.CommandText = """
            UPDATE operation_steps
            SET state = $initial, target_json = NULL, evidence_json = NULL,
                updated_at_utc_ms = $at
            WHERE operation_id = $operation AND state = $initial;
            """;
        steps.Parameters.AddWithValue("$initial", (int)PersistedOperationStepState.NotStarted);
        steps.Parameters.AddWithValue("$at", acceptedAt.ToUnixTimeMilliseconds());
        steps.Parameters.AddWithValue("$operation", Id(operationId.Value));
        await steps.ExecuteNonQueryAsync(cancellationToken);
        await AppendEventAsync(connection, transaction,
            new ExecutionEvent(operationId, ExecutionEventKind.Accepted, acceptedAt,
                "operation.accepted", "Authorized operation accepted."), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetAsync(operationId, cancellationToken))
            ?? throw new InvalidDataException("已接受计划未能读取。");
    }

    public async Task<PersistedOperation?> GetAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state, sanitized_json, preparation_id, preparation_intent_hash,
                   authorization_digest, accepted_at_utc_ms
            FROM operation_plans
            WHERE operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$operation", Id(operationId.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadOperation(reader);
    }

    public async Task<PersistedOperation?> GetByPreparationIdAsync(
        Guid preparationId,
        CancellationToken cancellationToken = default)
    {
        if (preparationId == Guid.Empty)
        {
            throw new ArgumentException("PreparationId 不能为空。", nameof(preparationId));
        }
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state, sanitized_json, preparation_id, preparation_intent_hash,
                   authorization_digest, accepted_at_utc_ms
            FROM operation_plans WHERE preparation_id = $preparation;
            """;
        command.Parameters.AddWithValue("$preparation", Id(preparationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOperation(reader) : null;
    }

    private static PersistedOperation ReadOperation(SqliteDataReader reader)
    {
        var plan = JsonSerializer.Deserialize<OperationPlan>(reader.GetString(1), JsonOptions)
            ?? throw new InvalidDataException("持久化操作计划为空。");
        return new PersistedOperation(plan,
            (PersistedOperationState)reader.GetInt32(0),
            reader.IsDBNull(2) ? null : Guid.ParseExact(reader.GetString(2), "N"),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)));
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
            UPDATE operation_plans SET state = $state
            WHERE operation_id = $operation AND risk < 4;
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$operation", Id(operationId.Value));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException($"找不到操作计划 {operationId.Value:N}。");
        }
    }

    public async Task<IReadOnlyList<PersistedOperationStep>> GetStepsAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT step_id, sequence_no, state, target_json, evidence_json, updated_at_utc_ms
            FROM operation_steps WHERE operation_id = $operation ORDER BY sequence_no;
            """;
        command.Parameters.AddWithValue("$operation", Id(operationId.Value));
        var result = new List<PersistedOperationStep>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PersistedOperationStep(reader.GetString(0), reader.GetInt32(1),
                (PersistedOperationStepState)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
        }
        return result;
    }

    public async Task<IReadOnlyList<PersistedOperation>> ListUnfinishedAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state, sanitized_json, preparation_id, preparation_intent_hash,
                   authorization_digest, accepted_at_utc_ms FROM operation_plans
            WHERE risk >= 4 AND state NOT IN (4, 5, 6, 7, 10)
            ORDER BY created_at_utc_ms, operation_id;
            """;
        var result = new List<PersistedOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadOperation(reader));
        }
        return result;
    }

    public async Task<bool> HasRealWriteBarrierAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM operation_plans
                WHERE risk >= 4 AND state NOT IN (4, 5, 6, 7, 10));
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    public async Task<bool> TransitionAsync(
        OperationId operationId,
        PersistedOperationState expected,
        PersistedOperationState next,
        ExecutionEvent executionEvent,
        CancellationToken cancellationToken = default)
    {
        if (executionEvent.OperationId != operationId)
        {
            throw new ArgumentException("事件和计划 ID 不一致。", nameof(executionEvent));
        }
        if (!IsAllowedTransition(expected, next))
        {
            throw new ArgumentException($"不允许的操作状态转移：{expected} → {next}。");
        }
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Persisted numeric values are fixed by the 17→18 schema contract.
        // Terminal states release the global write barrier, so check the
        // step evidence and verified-prefix shape in the same CAS statement.
        command.CommandText = """
            UPDATE operation_plans SET state = $next
            WHERE operation_id = $operation AND risk >= 4 AND state = $expected
              AND ($next <> 5 OR $expected NOT IN (3, 11) OR (
                  EXISTS(SELECT 1 FROM operation_steps
                         WHERE operation_id = $operation)
                  AND NOT EXISTS(SELECT 1 FROM operation_steps
                                 WHERE operation_id = $operation AND state <> 8)))
              AND ($next NOT IN (4, 6, 10) OR
                  ($next = 4 AND EXISTS(SELECT 1 FROM operation_steps
                      WHERE operation_id = $operation)
                      AND NOT EXISTS(SELECT 1 FROM operation_steps
                          WHERE operation_id = $operation AND
                              (state <> 5 OR evidence_json IS NULL OR trim(evidence_json) = '')))
                  OR ($next = 6 AND EXISTS(SELECT 1 FROM operation_steps
                      WHERE operation_id = $operation AND state = 6)
                      AND NOT EXISTS(SELECT 1 FROM operation_steps
                          WHERE operation_id = $operation AND
                              (state NOT IN (6, 8) OR
                               (state = 6 AND
                                (evidence_json IS NULL OR trim(evidence_json) = '')))))
                  OR ($next = 10 AND EXISTS(SELECT 1 FROM operation_steps
                      WHERE operation_id = $operation AND state = 5)
                      AND EXISTS(SELECT 1 FROM operation_steps
                      WHERE operation_id = $operation AND state IN (6, 8))
                      AND NOT EXISTS(SELECT 1 FROM operation_steps
                          WHERE operation_id = $operation AND
                              (state NOT IN (5, 6, 8) OR
                               (state IN (5, 6) AND
                                (evidence_json IS NULL OR trim(evidence_json) = ''))))
                      AND NOT EXISTS(SELECT 1 FROM operation_steps AS later
                          JOIN operation_steps AS earlier
                            ON earlier.operation_id = later.operation_id
                           AND earlier.sequence_no < later.sequence_no
                          WHERE later.operation_id = $operation
                            AND later.state = 5 AND earlier.state <> 5)));
            """;
        command.Parameters.AddWithValue("$next", (int)next);
        command.Parameters.AddWithValue("$operation", Id(operationId.Value));
        command.Parameters.AddWithValue("$expected", (int)expected);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await AppendEventAsync(connection, transaction, executionEvent, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TransitionStepAsync(
        OperationId operationId,
        string stepId,
        PersistedOperationStepState expected,
        PersistedOperationStepState next,
        string? targetJson,
        string? evidenceJson,
        ExecutionEvent executionEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (executionEvent.OperationId != operationId)
        {
            throw new ArgumentException("事件和计划 ID 不一致。", nameof(executionEvent));
        }
        if ((next is PersistedOperationStepState.PreparingCall or PersistedOperationStepState.CallIssued)
            && string.IsNullOrWhiteSpace(targetJson))
        {
            throw new ArgumentException("调用前必须记录准确目标。", nameof(targetJson));
        }
        if (next is PersistedOperationStepState.Verified or PersistedOperationStepState.Failed
            && expected is not PersistedOperationStepState.NotStarted
            && string.IsNullOrWhiteSpace(evidenceJson))
        {
            throw new ArgumentException("恢复或验证结果必须附可核对的事实。", nameof(evidenceJson));
        }
        if (!IsAllowedStepTransition(expected, next))
        {
            throw new ArgumentException($"不允许的步骤状态转移：{expected} → {next}。");
        }
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE operation_steps SET state = $next,
                target_json = COALESCE($target, target_json),
                evidence_json = COALESCE($evidence, evidence_json),
                updated_at_utc_ms = $at
            WHERE operation_id = $operation AND step_id = $step
              AND state = $expected
              AND EXISTS(SELECT 1 FROM operation_plans
                         WHERE operation_id = $operation AND risk >= 4
                           AND (state IN (3, 9)
                                OR (state = 11 AND (
                                    ($expected IN (1, 2, 3, 4, 7) AND $next IN (5, 6))
                                    OR ($expected = 0 AND $next = 8)))));
            """;
        command.Parameters.AddWithValue("$next", (int)next);
        command.Parameters.AddWithValue("$target", (object?)targetJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidence", (object?)evidenceJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", executionEvent.At.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$operation", Id(operationId.Value));
        command.Parameters.AddWithValue("$step", stepId);
        command.Parameters.AddWithValue("$expected", (int)expected);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await AppendEventAsync(connection, transaction, executionEvent, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal static async Task AppendEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExecutionEvent executionEvent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionEvent.Code);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO execution_events(
                operation_id, timestamp_utc_ms, kind, code, sanitized_message)
            VALUES($operation, $timestamp, $kind, $code, $message);
            """;
        command.Parameters.AddWithValue("$operation", Id(executionEvent.OperationId.Value));
        command.Parameters.AddWithValue("$timestamp", executionEvent.At.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$kind", (int)executionEvent.Kind);
        command.Parameters.AddWithValue("$code", executionEvent.Code.Trim());
        command.Parameters.AddWithValue("$message", executionEvent.Message ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsAllowedTransition(
        PersistedOperationState expected,
        PersistedOperationState next) => (expected, next) switch
    {
        (PersistedOperationState.Prepared, PersistedOperationState.Cancelled or PersistedOperationState.Rejected) => true,
        (PersistedOperationState.Accepted, PersistedOperationState.Running or PersistedOperationState.Cancelled or PersistedOperationState.OutcomeUnknown) => true,
        (PersistedOperationState.Running, PersistedOperationState.Completed or PersistedOperationState.Cancelled or PersistedOperationState.Failed or PersistedOperationState.PartiallyCompleted or PersistedOperationState.OutcomeUnknown) => true,
        (PersistedOperationState.OutcomeUnknown, PersistedOperationState.Completed or PersistedOperationState.Cancelled or PersistedOperationState.Failed or PersistedOperationState.PartiallyCompleted) => true,
        _ => false
    };

    private static bool IsAllowedStepTransition(
        PersistedOperationStepState expected,
        PersistedOperationStepState next) => (expected, next) switch
    {
        (PersistedOperationStepState.NotStarted, PersistedOperationStepState.PreparingCall or PersistedOperationStepState.Failed or PersistedOperationStepState.Skipped) => true,
        (PersistedOperationStepState.PreparingCall, PersistedOperationStepState.CallIssued or PersistedOperationStepState.Skipped or PersistedOperationStepState.Verified or PersistedOperationStepState.Failed or PersistedOperationStepState.OutcomeUnknown) => true,
        (PersistedOperationStepState.CallIssued, PersistedOperationStepState.AwaitingProvider or PersistedOperationStepState.Verifying or PersistedOperationStepState.Verified or PersistedOperationStepState.Failed or PersistedOperationStepState.OutcomeUnknown) => true,
        (PersistedOperationStepState.AwaitingProvider, PersistedOperationStepState.Verifying or PersistedOperationStepState.Verified or PersistedOperationStepState.Failed or PersistedOperationStepState.OutcomeUnknown) => true,
        (PersistedOperationStepState.Verifying, PersistedOperationStepState.Verified or PersistedOperationStepState.Failed or PersistedOperationStepState.OutcomeUnknown) => true,
        (PersistedOperationStepState.OutcomeUnknown, PersistedOperationStepState.Verified or PersistedOperationStepState.Failed) => true,
        _ => false
    };

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
