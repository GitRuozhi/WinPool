using WinPool.Domain;
using System.Text.Json;

namespace WinPool.Infrastructure.Sqlite;

public sealed record PersistedRealStepStatus(string StepId, PersistedOperationStepState State, string? Code,
    string? CreatedObjectId, string? CreatedOsDiskId);
public sealed record PersistedRealOperationStatus(OperationId OperationId, PersistedOperationState State,
    long Revision, string? Code, IReadOnlyList<PersistedRealStepStatus> Steps, SystemId SystemId, string PlanHash,
    IReadOnlyList<StorageObjectId> Targets);

public sealed partial class OperationPlanRepository
{
    public async Task<PersistedRealOperationStatus?> ReadRealStatusAsync(OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // A single SQLite read snapshot keeps state, step progress and revision consistent
        // while an accepted worker is advancing the journal.
        command.CommandText = """
            SELECT p.state, COALESCE(e.event_id,0), e.code, s.step_id, s.state,
                   (SELECT code FROM execution_events
                    WHERE operation_id=p.operation_id AND sanitized_message=s.step_id
                    ORDER BY event_id DESC LIMIT 1), p.system_id, p.plan_hash,
                   p.target_ids_json, s.created_object_id, s.created_os_disk_id
            FROM operation_plans p
            LEFT JOIN execution_events e ON e.event_id=(
                SELECT MAX(event_id) FROM execution_events WHERE operation_id=p.operation_id)
            LEFT JOIN operation_steps s ON s.operation_id=p.operation_id
            WHERE p.operation_id=$id AND p.risk>=4 ORDER BY s.sequence_no;
            """;
        command.Parameters.AddWithValue("$id", Id(operationId.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var state = (PersistedOperationState)reader.GetInt32(0);
        var revision = reader.GetInt64(1);
        var code = reader.IsDBNull(2) ? null : reader.GetString(2);
        var systemId = new SystemId(Guid.Parse(reader.GetString(6)));
        var planHash = reader.GetString(7);
        var targets = JsonSerializer.Deserialize<StorageObjectId[]>(reader.GetString(8), JsonOptions)
            ?? throw new InvalidDataException("The frozen operation target identity is missing.");
        var steps = new List<PersistedRealStepStatus>();
        do
        {
            if (!reader.IsDBNull(3)) steps.Add(new(reader.GetString(3),
                (PersistedOperationStepState)reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                ReadIdentity(reader, 9), ReadIdentity(reader, 10)));
        } while (await reader.ReadAsync(cancellationToken));
        return new(operationId, state, revision, code, steps, systemId, planHash, targets);
    }

    private static string? ReadIdentity(Microsoft.Data.Sqlite.SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        var value = reader.GetString(index);
        if (value.Length == 0 || value.Length > 4096)
            throw new InvalidDataException("The verified result identity is invalid.");
        return value;
    }

    public async Task<IReadOnlyList<OperationId>> ListUnfinishedRealOperationIdsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id FROM operation_plans
            WHERE risk>=4 AND state NOT IN (4,5,6,7,10) ORDER BY created_at_utc_ms,operation_id;
            """;
        var result = new List<OperationId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(Guid.Parse(reader.GetString(0))));
        return result;
    }
}
