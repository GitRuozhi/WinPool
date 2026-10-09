using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;

namespace WinPool.Infrastructure.Sqlite;

public sealed partial class OperationPlanRepository
{
    public IMaximumCapacityAttemptJournal CreateMaximumCapacityJournal(OperationId operationId,
        string stepId, Func<bool>? isStopRequested = null) =>
        new MaximumCapacityJournal(this, operationId, stepId, isStopRequested ?? (() => false));

    public async Task<IReadOnlyList<MaximumCapacityAttemptRecord>> ReadMaximumCapacityAttemptsAsync(
        OperationId operationId, string stepId, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_json, state, result_json, updated_at_utc_ms
            FROM operation_capacity_attempts WHERE operation_id=$operation AND step_id=$step
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$operation", Id(operationId.Value));
        command.Parameters.AddWithValue("$step", stepId);
        var rows = new List<MaximumCapacityAttemptRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(JsonSerializer.Deserialize<MaximumCapacityAttempt>(reader.GetString(0))
                    ?? throw new InvalidDataException("The capacity attempt is unreadable."),
                (MaximumCapacityAttemptState)reader.GetInt32(1),
                reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<MaximumCapacityAttemptResult>(reader.GetString(2))
                    ?? throw new InvalidDataException("The capacity attempt result is unreadable."),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
        return rows;
    }

    private async Task<bool> PrepareMaximumCapacityAttemptAsync(OperationId operationId, string stepId,
        MaximumCapacityAttempt attempt, CancellationToken cancellationToken)
    {
        AssertWriteOwnership();
        if (attempt.Ordinal <= 0 || string.IsNullOrWhiteSpace(attempt.SearchTargetKey)
            || !Enum.IsDefined(attempt.Phase) || attempt.CandidateBytes <= 0 || attempt.LastSuccessfulBytes < 0
            || string.IsNullOrWhiteSpace(attempt.TargetEvidenceJson)
            || string.IsNullOrWhiteSpace(attempt.BeforeFingerprint)
            || string.IsNullOrWhiteSpace(attempt.PhysicalMemberFingerprint)
            || string.IsNullOrWhiteSpace(attempt.BeforeEvidenceJson)) return false;
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operation_capacity_attempts(operation_id,step_id,ordinal,search_target_key,
                state,candidate_bytes,last_successful_bytes,attempt_json,result_json,updated_at_utc_ms)
            SELECT $operation,$step,$ordinal,$key,0,$candidate,$last,$attempt,NULL,$at
            WHERE EXISTS(SELECT 1 FROM operation_plans p JOIN operation_steps s
                ON p.operation_id=s.operation_id WHERE p.operation_id=$operation AND p.risk>=4
                AND p.state=3 AND s.step_id=$step AND s.state=2)
              AND $ordinal=1+COALESCE((SELECT MAX(ordinal) FROM operation_capacity_attempts
                WHERE operation_id=$operation AND step_id=$step),0)
              AND NOT EXISTS(SELECT 1 FROM operation_capacity_attempts
                WHERE operation_id=$operation AND step_id=$step AND state NOT IN(2,3))
              AND $last=COALESCE((SELECT last_successful_bytes FROM operation_capacity_attempts
                WHERE operation_id=$operation AND step_id=$step AND search_target_key=$key
                ORDER BY ordinal DESC LIMIT 1),
                (SELECT CAST(seed.value AS INTEGER) FROM operation_capacity_attempts a,
                    json_each(a.result_json,'$.SeedSuccessfulBytes') seed
                 WHERE a.operation_id=$operation AND a.step_id=$step AND a.state=2 AND seed.key=$key
                 ORDER BY a.ordinal DESC LIMIT 1),0);
            """;
        command.Parameters.AddWithValue("$operation", Id(operationId.Value));
        command.Parameters.AddWithValue("$step", stepId);
        command.Parameters.AddWithValue("$ordinal", attempt.Ordinal);
        command.Parameters.AddWithValue("$key", attempt.SearchTargetKey);
        command.Parameters.AddWithValue("$candidate", attempt.CandidateBytes);
        command.Parameters.AddWithValue("$last", attempt.LastSuccessfulBytes);
        command.Parameters.AddWithValue("$attempt", JsonSerializer.Serialize(attempt));
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await AppendEventAsync(connection, transaction, CapacityEvent(operationId, stepId,
            "operation.maximum.attempt_preparing", attempt.Ordinal), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> TransitionMaximumCapacityAttemptAsync(OperationId operationId, string stepId,
        int ordinal, MaximumCapacityAttemptResult? result, CancellationToken cancellationToken)
    {
        AssertWriteOwnership();
        var issued = result is null;
        if (ordinal <= 0 || result is not null &&
            (result.State is not (MaximumCapacityAttemptState.Verified
                or MaximumCapacityAttemptState.CapacityRejectedUnchanged
                or MaximumCapacityAttemptState.FailedWithoutCall or MaximumCapacityAttemptState.OutcomeUnknown)
             || result.LastSuccessfulBytes < 0 || string.IsNullOrWhiteSpace(result.Code)
             || string.IsNullOrWhiteSpace(result.ResultEvidenceJson))) return false;
        if (result?.SeedSuccessfulBytes is not null
            && !await ValidateMaximumSeedResultAsync(operationId, stepId, ordinal, result, cancellationToken))
            return false;
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE operation_capacity_attempts SET state=$next,
                result_json=$result,last_successful_bytes=COALESCE($last,last_successful_bytes),updated_at_utc_ms=$at
            WHERE operation_id=$operation AND step_id=$step AND ordinal=$ordinal
              AND (($issued=1 AND state=0)
                OR ($issued=0 AND $next=4 AND state=0)
                OR ($issued=0 AND $next IN(2,3,5) AND state=1))
              AND ($issued=1 OR ($next=2 AND $last=candidate_bytes)
                OR ($next<>2 AND $last=last_successful_bytes))
              AND ($next<>2 OR json_extract(attempt_json,'$.Phase')<>2
                OR json_type($result,'$.SeedSuccessfulBytes')='object')
              AND EXISTS(SELECT 1 FROM operation_plans p JOIN operation_steps s
                ON p.operation_id=s.operation_id WHERE p.operation_id=$operation AND p.risk>=4
                AND p.state=3 AND s.step_id=$step AND s.state=2);
            """;
        command.Parameters.AddWithValue("$operation", Id(operationId.Value));
        command.Parameters.AddWithValue("$step", stepId);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$next", issued ? 1 : (int)result!.State);
        command.Parameters.AddWithValue("$issued", issued ? 1 : 0);
        command.Parameters.AddWithValue("$result", result is null ? DBNull.Value : JsonSerializer.Serialize(result));
        command.Parameters.AddWithValue("$last", result is null ? DBNull.Value : result.LastSuccessfulBytes);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await AppendEventAsync(connection, transaction, CapacityEvent(operationId, stepId,
            issued ? "operation.maximum.attempt_call_issued" : "operation.maximum.attempt_" + result!.State,
            ordinal), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> ValidateMaximumSeedResultAsync(OperationId operationId, string stepId,
        int ordinal, MaximumCapacityAttemptResult result, CancellationToken cancellationToken)
    {
        if (result.State != MaximumCapacityAttemptState.Verified) return false;
        var operation = await GetAsync(operationId, cancellationToken);
        var step = operation?.Plan.RealOperation?.Steps.SingleOrDefault(item => item.Id == stepId);
        if (step?.Command is not CreateTieredVirtualDiskCommand
            { UseMaximumSize: true, CapacityTiers: { Count: > 1 } tiers }) return false;
        var attempts = await ReadMaximumCapacityAttemptsAsync(operationId, stepId, cancellationToken);
        var attempt = attempts.SingleOrDefault(item => item.Attempt.Ordinal == ordinal)?.Attempt;
        if (attempt is not { Phase: MaximumCapacityAttemptPhase.Seed, SearchTargetKey: "seed" }
            || attempt.LastSuccessfulBytes != 0 || result.SeedSuccessfulBytes!.Count != tiers.Count) return false;
        long total = 0;
        try
        {
            foreach (var tier in tiers)
            {
                if (tier.Tier.Existing is not { } id || tier.MaximumCapacity is not { } policy
                    || !result.SeedSuccessfulBytes.TryGetValue(id.ProviderKey, out var bytes)
                    || bytes <= 0 || bytes != MaximumCapacityAlgorithm.SeedBytes(policy.InitialCandidateBytes))
                    return false;
                total = checked(total + bytes);
            }
        }
        catch (OverflowException) { return false; }
        return total == attempt.CandidateBytes && result.LastSuccessfulBytes == total;
    }

    private static ExecutionEvent CapacityEvent(OperationId operationId, string stepId, string code, int ordinal) =>
        new(operationId, ExecutionEventKind.Progress, DateTimeOffset.UtcNow, code,
            JsonSerializer.Serialize(new { StepId = stepId, AttemptOrdinal = ordinal }));

    private sealed class MaximumCapacityJournal(OperationPlanRepository repository, OperationId operationId,
        string stepId, Func<bool> isStopRequested) : IMaximumCapacityAttemptJournal
    {
        public bool IsStopRequested => isStopRequested();
        public Task<IReadOnlyList<MaximumCapacityAttemptRecord>> ReadAsync(CancellationToken cancellationToken) =>
            repository.ReadMaximumCapacityAttemptsAsync(operationId, stepId, cancellationToken);
        public Task<bool> PrepareAsync(MaximumCapacityAttempt attempt, CancellationToken cancellationToken) =>
            IsStopRequested ? Task.FromResult(false) : repository.PrepareMaximumCapacityAttemptAsync(
                operationId, stepId, attempt, cancellationToken);
        public Task<bool> MarkCallIssuedAsync(int ordinal, CancellationToken cancellationToken) =>
            IsStopRequested ? Task.FromResult(false) : repository.TransitionMaximumCapacityAttemptAsync(
                operationId, stepId, ordinal, null, cancellationToken);
        public Task<bool> CompleteAsync(int ordinal, MaximumCapacityAttemptResult result, CancellationToken cancellationToken) =>
            repository.TransitionMaximumCapacityAttemptAsync(operationId, stepId, ordinal, result, cancellationToken);
    }
}
