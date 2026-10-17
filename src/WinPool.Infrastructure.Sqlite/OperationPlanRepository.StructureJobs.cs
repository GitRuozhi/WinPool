using Microsoft.Data.Sqlite;
using WinPool.Domain;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed record PersistedStructureJob(Guid JobId, string IntentHash, string GoalJson,
    string MachineBinding, string SessionBinding, RealOperationState State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? ResultJson,
    IReadOnlyList<OperationId> Children);

public sealed partial class OperationPlanRepository
{
    public string DiagnosticsRoot => Path.GetDirectoryName(store.DatabasePath)!;

    public async Task<bool> HasStructureWriteBarrierAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM real_structure_jobs WHERE state IN ($accepted,$running,$unknown));";
        command.Parameters.AddWithValue("$accepted", (int)RealOperationState.Accepted);
        command.Parameters.AddWithValue("$running", (int)RealOperationState.Running);
        command.Parameters.AddWithValue("$unknown", (int)RealOperationState.OutcomeUnknown);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    public async Task<PersistedStructureJob?> GetStructureJobAsync(Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT intent_hash,goal_json,machine_binding,session_binding,state,
                created_utc_ms,updated_utc_ms,result_json FROM real_structure_jobs WHERE job_id=$id;
            """;
        command.Parameters.AddWithValue("$id", Id(jobId));
        PersistedStructureJob? result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            result = new(jobId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                (RealOperationState)reader.GetInt32(4), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)), reader.IsDBNull(7) ? null : reader.GetString(7), []);
        }
        await using var children = connection.CreateCommand();
        children.CommandText = "SELECT operation_id FROM real_structure_job_children WHERE job_id=$id ORDER BY ordinal;";
        children.Parameters.AddWithValue("$id", Id(jobId));
        var ids = new List<OperationId>();
        await using var childReader = await children.ExecuteReaderAsync(cancellationToken);
        while (await childReader.ReadAsync(cancellationToken)) ids.Add(new(Guid.Parse(childReader.GetString(0))));
        return result with { Children = ids };
    }

    public Task<IReadOnlyList<PersistedStructureJob>> ListUnfinishedStructureJobsAsync(
        CancellationToken cancellationToken = default) => ListStructureJobsAsync(false, cancellationToken);

    public Task<IReadOnlyList<PersistedStructureJob>> ListRecoverableStructureJobsAsync(
        CancellationToken cancellationToken = default) => ListStructureJobsAsync(true, cancellationToken);

    private async Task<IReadOnlyList<PersistedStructureJob>> ListStructureJobsAsync(bool includeStopped,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = includeStopped
            ? """
                SELECT candidate.job_id FROM real_structure_jobs AS candidate
                WHERE candidate.state <> $succeeded AND NOT EXISTS (
                    SELECT 1 FROM real_structure_jobs AS resumed
                    WHERE replace(json_extract(resumed.goal_json,'$.Goal.ResumeFromJobId'),'-','') = candidate.job_id)
                ORDER BY candidate.created_utc_ms,candidate.job_id;
                """
            : "SELECT job_id FROM real_structure_jobs WHERE state IN ($prepared,$accepted,$running,$unknown);";
        command.Parameters.AddWithValue("$prepared", (int)RealOperationState.Prepared);
        command.Parameters.AddWithValue("$accepted", (int)RealOperationState.Accepted);
        command.Parameters.AddWithValue("$running", (int)RealOperationState.Running);
        command.Parameters.AddWithValue("$unknown", (int)RealOperationState.OutcomeUnknown);
        if (includeStopped) command.Parameters.AddWithValue("$succeeded", (int)RealOperationState.Succeeded);
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) ids.Add(Guid.Parse(reader.GetString(0)));
        var jobs = new List<PersistedStructureJob>();
        foreach (var id in ids)
            if (await GetStructureJobAsync(id, cancellationToken) is { } job) jobs.Add(job);
        return jobs;
    }

    public async Task SaveStructureJobAsync(PersistedStructureJob job, CancellationToken cancellationToken = default)
    {
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO real_structure_jobs(job_id,intent_hash,goal_json,machine_binding,session_binding,state,
                created_utc_ms,updated_utc_ms,result_json) VALUES($id,$hash,$goal,$machine,$session,$state,$created,$updated,$result);
            """;
        command.Parameters.AddWithValue("$id", Id(job.JobId));
        command.Parameters.AddWithValue("$hash", job.IntentHash);
        command.Parameters.AddWithValue("$goal", job.GoalJson);
        command.Parameters.AddWithValue("$machine", job.MachineBinding);
        command.Parameters.AddWithValue("$session", job.SessionBinding);
        command.Parameters.AddWithValue("$state", (int)job.State);
        command.Parameters.AddWithValue("$created", job.CreatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$updated", job.UpdatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$result", (object?)job.ResultJson ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TransitionStructureJobAsync(Guid jobId, RealOperationState expected,
        RealOperationState next, string? result, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE real_structure_jobs SET state=$next,result_json=$result,
                updated_utc_ms=MAX(updated_utc_ms+1,$at) WHERE job_id=$id AND state=$expected;
            """;
        command.Parameters.AddWithValue("$next", (int)next);
        command.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", Id(jobId));
        command.Parameters.AddWithValue("$expected", (int)expected);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task LinkStructureChildAsync(Guid jobId, OperationId operationId, int ordinal,
        CancellationToken cancellationToken = default)
    {
        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO real_structure_job_children(job_id,operation_id,ordinal)
            SELECT $job,$operation,$ordinal WHERE EXISTS(
                SELECT 1 FROM real_structure_jobs WHERE job_id=$job AND state=$running);
            """;
        command.Parameters.AddWithValue("$job", Id(jobId));
        command.Parameters.AddWithValue("$operation", Id(operationId.Value));
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$running", (int)RealOperationState.Running);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("The parent job is not durably running.");
    }
}
