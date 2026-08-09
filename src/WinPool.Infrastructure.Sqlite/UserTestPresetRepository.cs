using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed class UserTestPresetRepository : IUserTestPresetRepository
{
    private const string KeyPrefix = "test-preset:";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly WinPoolSqliteStore store;
    private readonly AgentWriteOwnerLease? writeOwner;

    public UserTestPresetRepository(WinPoolSqliteStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public UserTestPresetRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
        : this(store)
    {
        this.writeOwner = writeOwner ?? throw new ArgumentNullException(
            nameof(writeOwner));
        writeOwner.AssertOwnership(store);
    }

    public async Task<IReadOnlyList<UserTestPreset>> ListAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json
            FROM preferences
            WHERE key LIKE 'test-preset:%'
            ORDER BY updated_at_utc_ms DESC, key;
            """;
        var results = new List<UserTestPreset>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var preset = JsonSerializer.Deserialize<UserTestPreset>(
                reader.GetString(0),
                JsonOptions);
            if (preset is null || !UserTestPresetValidator.IsValid(preset))
            {
                throw new InvalidDataException(
                    "A persisted user test preset is invalid.");
            }

            results.Add(preset);
        }

        return results;
    }

    public async Task<UserTestPreset> SaveAsync(
        UserTestPreset preset,
        CancellationToken cancellationToken)
    {
        EnsureWriter();
        if (!UserTestPresetValidator.IsValid(preset))
        {
            throw new ArgumentException(
                "The user test preset is invalid.",
                nameof(preset));
        }

        var normalized = preset with { Name = preset.Name.Trim() };
        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO preferences(key, json, updated_at_utc_ms)
            VALUES($key, $json, $updated)
            ON CONFLICT(key) DO UPDATE SET
                json=excluded.json,
                updated_at_utc_ms=excluded.updated_at_utc_ms;
            """;
        command.Parameters.AddWithValue(
            "$key",
            KeyPrefix + normalized.PresetId.ToString("N"));
        command.Parameters.AddWithValue(
            "$json",
            JsonSerializer.Serialize(normalized, JsonOptions));
        command.Parameters.AddWithValue(
            "$updated",
            normalized.UpdatedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return normalized;
    }

    public async Task<bool> DeleteAsync(
        Guid presetId,
        CancellationToken cancellationToken)
    {
        EnsureWriter();
        if (presetId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(presetId));
        }

        await using var connection = await store.OpenConnectionAsync(
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM preferences WHERE key=$key;";
        command.Parameters.AddWithValue(
            "$key",
            KeyPrefix + presetId.ToString("N"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private void EnsureWriter()
    {
        if (writeOwner is null)
        {
            throw new AgentWriteOwnershipException(
                "User test preset writes require the Agent write-owner lease.");
        }

        writeOwner.AssertOwnership(store);
    }
}
