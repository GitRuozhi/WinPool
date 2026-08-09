using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Infrastructure.Sqlite;

public sealed class UserPreferencesRepository : IUserPreferencesRepository
{
    private const string GlobalKey = "global";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public UserPreferencesRepository(WinPoolSqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public UserPreferencesRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        ArgumentNullException.ThrowIfNull(writeOwner);
        writeOwner.AssertOwnership(store);
        this.writeOwner = writeOwner;
    }

    public async Task<ApplicationResult<UserPreferences>> LoadAsync(
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM preferences WHERE key=$key;";
        command.Parameters.AddWithValue("$key", GlobalKey);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (json is null)
        {
            return ApplicationResult<UserPreferences>.Succeeded(new UserPreferences(), correlationId);
        }

        try
        {
            var preferences = JsonSerializer.Deserialize<UserPreferences>(json, JsonOptions);
            return preferences is null
                ? Failure(correlationId, "preferences.empty", "The stored preference document is empty.")
                : ApplicationResult<UserPreferences>.Succeeded(preferences, correlationId);
        }
        catch (JsonException)
        {
            return Failure(
                correlationId,
                "preferences.invalid_json",
                "The stored preference document is invalid.");
        }
    }

    public async Task<ApplicationResult> SaveAsync(
        UserPreferences preferences,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        AssertWriteOwnership();

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO preferences(key, json, updated_at_utc_ms)
            VALUES($key, $json, $updated)
            ON CONFLICT(key) DO UPDATE SET
                json=excluded.json,
                updated_at_utc_ms=excluded.updated_at_utc_ms;
            """;
        command.Parameters.AddWithValue("$key", GlobalKey);
        command.Parameters.Add("$json", SqliteType.Text).Value =
            JsonSerializer.Serialize(preferences, JsonOptions);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return ApplicationResult.Succeeded(correlationId);
    }

    private static ApplicationResult<UserPreferences> Failure(
        CorrelationId correlationId,
        string code,
        string diagnostic) =>
        ApplicationResult<UserPreferences>.FromStatus(
            ApplicationStatus.Failed,
            correlationId,
            new ApplicationMessage(
                code,
                code,
                diagnostic,
                ApplicationMessageSeverity.Error,
                []));

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
