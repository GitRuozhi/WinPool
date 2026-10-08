using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Sqlite;

public sealed class MonitorEditGapRepository(ISqliteDatabaseStore store, AgentWriteOwnerLease? writeOwner = null)
{
    public async Task SaveAsync(MonitorEditGap gap, CancellationToken cancellationToken = default)
    {
        if (writeOwner is null) throw new InvalidOperationException("The Agent write owner is required to persist edit gaps.");
        writeOwner.AssertOwnership(store);
        if (string.IsNullOrWhiteSpace(gap.GapId) || gap.SessionId.Value == Guid.Empty || gap.SystemId.Value == Guid.Empty
            || gap.TargetId.System != gap.SystemId || gap.OperationId.Value == Guid.Empty || string.IsNullOrWhiteSpace(gap.StepId)
            || gap.EndedAtUtc < gap.StartedAtUtc || !Enum.IsDefined(gap.Status))
            throw new InvalidDataException("The monitoring edit gap is invalid.");
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO monitor_edit_gaps(gap_id,session_id,system_id,target_kind,target_provider_key,counter_identity,
                operation_id,step_id,started_at_utc_ms,ended_at_utc_ms,status,reason_code)
            VALUES($id,$session,$system,$kind,$target,$counter,$operation,$step,$start,$end,$state,$reason)
            ON CONFLICT(gap_id) DO UPDATE SET ended_at_utc_ms=excluded.ended_at_utc_ms,
                status=excluded.status,reason_code=excluded.reason_code
            WHERE monitor_edit_gaps.session_id=excluded.session_id AND monitor_edit_gaps.system_id=excluded.system_id
                AND monitor_edit_gaps.target_kind=excluded.target_kind AND monitor_edit_gaps.target_provider_key=excluded.target_provider_key
                AND monitor_edit_gaps.counter_identity=excluded.counter_identity AND monitor_edit_gaps.operation_id=excluded.operation_id
                AND monitor_edit_gaps.step_id=excluded.step_id AND monitor_edit_gaps.started_at_utc_ms=excluded.started_at_utc_ms
                AND (monitor_edit_gaps.ended_at_utc_ms IS NULL OR monitor_edit_gaps.ended_at_utc_ms=excluded.ended_at_utc_ms);
            """;
        command.Parameters.AddWithValue("$id", gap.GapId);
        command.Parameters.AddWithValue("$session", gap.SessionId.Value.ToString("N"));
        command.Parameters.AddWithValue("$system", gap.SystemId.Value.ToString("N"));
        command.Parameters.AddWithValue("$kind", (int)gap.TargetId.Kind);
        command.Parameters.AddWithValue("$target", gap.TargetId.ProviderKey);
        command.Parameters.AddWithValue("$counter", gap.CounterIdentity);
        command.Parameters.AddWithValue("$operation", gap.OperationId.Value.ToString("N"));
        command.Parameters.AddWithValue("$step", gap.StepId);
        command.Parameters.AddWithValue("$start", gap.StartedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", gap.EndedAtUtc is { } ended ? ended.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("$state", (int)gap.Status);
        command.Parameters.AddWithValue("$reason", gap.ReasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidDataException("An existing edit gap has different immutable evidence.");
    }

    public async Task<IReadOnlyList<MonitorEditGap>> ListAsync(SessionId? sessionId = null, bool openOnly = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT gap_id,session_id,system_id,target_kind,target_provider_key,counter_identity,operation_id,step_id,started_at_utc_ms,ended_at_utc_ms,status,reason_code FROM monitor_edit_gaps WHERE ($session IS NULL OR session_id=$session) AND ($open=0 OR ended_at_utc_ms IS NULL) ORDER BY started_at_utc_ms,gap_id;";
        command.Parameters.AddWithValue("$session", sessionId is { } session ? session.Value.ToString("N") : DBNull.Value);
        command.Parameters.AddWithValue("$open", openOnly ? 1 : 0);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        var gaps = new List<MonitorEditGap>();
        while (await rows.ReadAsync(cancellationToken))
        {
            var system = new SystemId(Guid.ParseExact(rows.GetString(2), "N"));
            gaps.Add(new(rows.GetString(0), new SessionId(Guid.ParseExact(rows.GetString(1), "N")), system,
                new StorageObjectId(system, (StorageObjectKind)rows.GetInt32(3), rows.GetString(4)), rows.GetString(5),
                new OperationId(Guid.ParseExact(rows.GetString(6), "N")), rows.GetString(7),
                DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(8)), rows.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(9)),
                (MonitorEditTargetStatus)rows.GetInt32(10), rows.GetString(11)));
        }
        return gaps;
    }
}
