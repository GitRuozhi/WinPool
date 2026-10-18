using System.Security.Cryptography;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Sqlite;

public sealed record PersistedEvidencePage(IReadOnlyList<PersistedOperationStep> Steps,
    IReadOnlyList<RealEvidenceReference> References, IReadOnlyDictionary<string, string> Codes);

public sealed partial class OperationPlanRepository
{
    public async Task<PersistedEvidencePage> ReadEvidencePageAsync(OperationId operationId, int offset, int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.step_id,s.sequence_no,s.state,
                CASE WHEN length(CAST(s.target_json AS BLOB))<=65536 THEN s.target_json END,
                CASE WHEN length(CAST(s.evidence_json AS BLOB))<=65536 THEN s.evidence_json END,
                s.updated_at_utc_ms, COALESCE(length(CAST(s.target_json AS BLOB)),0),
                COALESCE(length(CAST(s.evidence_json AS BLOB)),0),
                COALESCE((SELECT MAX(event_id) FROM execution_events WHERE operation_id=s.operation_id),0),
                (SELECT code FROM execution_events WHERE operation_id=s.operation_id AND sanitized_message=s.step_id ORDER BY event_id DESC LIMIT 1)
            FROM operation_steps s WHERE operation_id=$id ORDER BY sequence_no LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$id", Id(operationId.Value));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var steps = new List<PersistedOperationStep>();
        var references = new List<RealEvidenceReference>();
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            steps.Add(new(id, reader.GetInt32(1), (PersistedOperationStepState)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
            var revision = reader.GetInt64(8);
            references.Add(new(id, RealEvidenceField.Target, reader.GetInt64(6), revision, reader.GetInt64(6) <= 65536));
            references.Add(new(id, RealEvidenceField.Result, reader.GetInt64(7), revision, reader.GetInt64(7) <= 65536));
            if (!reader.IsDBNull(9)) codes[id] = reader.GetString(9);
        }
        return new(steps, references, codes);
    }

    public async Task<RealEvidenceWindow?> ReadEvidenceWindowAsync(OperationId operationId, string stepId,
        RealEvidenceField field, long byteOffset, int byteCount, long expectedRevision, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stepId) || !Enum.IsDefined(field) || byteOffset < 0
            || byteCount is < 1 or > 256 * 1024 || expectedRevision < 0)
            throw new ArgumentException("The evidence byte window is invalid.");
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var column = field == RealEvidenceField.Target ? "target_json" : "evidence_json";
        command.CommandText = $"""
            SELECT COALESCE(length(CAST({column} AS BLOB)),0),
                COALESCE(substr(CAST({column} AS BLOB),$offset+1,$count),X''),
                COALESCE((SELECT MAX(event_id) FROM execution_events WHERE operation_id=s.operation_id),0)
            FROM operation_steps s WHERE operation_id=$id AND step_id=$step;
            """;
        command.Parameters.AddWithValue("$id", Id(operationId.Value));
        command.Parameters.AddWithValue("$step", stepId);
        command.Parameters.AddWithValue("$offset", byteOffset);
        command.Parameters.AddWithValue("$count", byteCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var length = reader.GetInt64(0);
        var revision = reader.GetInt64(2);
        if (revision != expectedRevision || byteOffset > length) return null;
        var bytes = reader.GetFieldValue<byte[]>(1);
        return new(stepId, field, revision, length, byteOffset, bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
