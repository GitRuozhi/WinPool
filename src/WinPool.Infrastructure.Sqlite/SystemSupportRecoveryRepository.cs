using System.Text.Json;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed class SystemSupportRecoveryRepository : ISystemSupportRecoveryStore
{
    private const int ProcessSchedulingState = 1;
    private const int PowerPlanState = 2;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public SystemSupportRecoveryRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public SystemSupportRecoveryRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task SaveAsync(
        SystemSupportRecoveryEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.RecoveryId == Guid.Empty ||
            string.IsNullOrWhiteSpace(entry.PlanHash))
        {
            throw new ArgumentException("The recovery entry identity is incomplete.");
        }

        AssertWriteOwnership();
        var (kind, json) = SerializeState(entry.State);
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO system_support_recovery(
                recovery_id, plan_hash, action_kind, state_kind, state_json,
                prepared_at_utc_ms)
            VALUES($id, $plan, $action, $kind, $json, $prepared);
            """;
        command.Parameters.AddWithValue("$id", entry.RecoveryId.ToString("N"));
        command.Parameters.AddWithValue("$plan", entry.PlanHash.Trim());
        command.Parameters.AddWithValue("$action", (int)entry.ActionKind);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue(
            "$prepared",
            entry.PreparedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAsync(
        Guid recoveryId,
        CancellationToken cancellationToken)
    {
        if (recoveryId == Guid.Empty)
        {
            throw new ArgumentException("A recovery ID is required.", nameof(recoveryId));
        }

        AssertWriteOwnership();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM system_support_recovery WHERE recovery_id = $id;";
        command.Parameters.AddWithValue("$id", recoveryId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SystemSupportRecoveryEntry>> GetPendingAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT recovery_id, plan_hash, action_kind, state_kind, state_json,
                   prepared_at_utc_ms
            FROM system_support_recovery
            ORDER BY prepared_at_utc_ms, recovery_id;
            """;
        var entries = new List<SystemSupportRecoveryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(
                new(
                    Guid.ParseExact(reader.GetString(0), "N"),
                    reader.GetString(1),
                    (SystemSupportActionKind)reader.GetInt32(2),
                    DeserializeState(reader.GetInt32(3), reader.GetString(4)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
        }

        return entries;
    }

    private static (int Kind, string Json) SerializeState(
        SystemSupportRecoveryState state) =>
        state switch
        {
            ProcessSchedulingRecoveryState scheduling =>
                (ProcessSchedulingState, JsonSerializer.Serialize(
                    scheduling.Snapshot,
                    JsonOptions)),
            PowerPlanRecoveryState power =>
                (PowerPlanState, JsonSerializer.Serialize(
                    power.Snapshot,
                    JsonOptions)),
            _ => throw new ArgumentException(
                "Unsupported system-support recovery state.",
                nameof(state))
        };

    private static SystemSupportRecoveryState DeserializeState(
        int kind,
        string json) =>
        kind switch
        {
            ProcessSchedulingState => new ProcessSchedulingRecoveryState(
                JsonSerializer.Deserialize<TestProcessSchedulingSnapshot>(
                    json,
                    JsonOptions)
                ?? throw new InvalidDataException("Scheduling recovery state is empty.")),
            PowerPlanState => new PowerPlanRecoveryState(
                JsonSerializer.Deserialize<PowerPlanSnapshot>(json, JsonOptions)
                ?? throw new InvalidDataException("Power-plan recovery state is empty.")),
            _ => throw new InvalidDataException(
                $"Unknown system-support recovery state kind {kind}.")
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
}
