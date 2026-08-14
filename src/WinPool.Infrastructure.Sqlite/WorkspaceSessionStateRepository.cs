using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed class WorkspaceSessionStateRepository
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public WorkspaceSessionStateRepository(WinPoolSqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public WorkspaceSessionStateRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        ArgumentNullException.ThrowIfNull(writeOwner);
        writeOwner.AssertOwnership(store);
        this.writeOwner = writeOwner;
    }

    public async Task<WorkspaceSessionState?> LoadAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM workspace_state WHERE singleton=1;";
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (json is null)
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<WorkspaceSessionState>(json, JsonOptions);
            return state is not null && WorkspaceSessionStateValidator.IsValid(state)
                ? state
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<WorkspaceSessionState> SaveAsync(
        WorkspaceSessionState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!WorkspaceSessionStateValidator.IsValid(state))
        {
            throw new ArgumentException("The workspace session state is invalid.", nameof(state));
        }
        AssertWriteOwnership();
        var normalized = state with
        {
            RememberedProviderKeys = new Dictionary<ManageWorkspaceCategory, string>(
                state.RememberedProviderKeys),
            UpdatedAtUtc = state.UpdatedAtUtc.ToUniversalTime()
        };

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspace_state(singleton, json, updated_at_utc_ms)
            VALUES(1, $json, $updated)
            ON CONFLICT(singleton) DO UPDATE SET
                json=excluded.json,
                updated_at_utc_ms=excluded.updated_at_utc_ms;
            """;
        command.Parameters.Add("$json", SqliteType.Text).Value =
            JsonSerializer.Serialize(normalized, JsonOptions);
        command.Parameters.AddWithValue(
            "$updated",
            normalized.UpdatedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return normalized;
    }

    private void AssertWriteOwnership()
    {
        if (writeOwner is null)
        {
            throw new AgentWriteOwnershipException(
                "This repository is read-only; writes require AgentWriteOwnerLease.");
        }
        writeOwner.AssertOwnership(store);
    }
}
