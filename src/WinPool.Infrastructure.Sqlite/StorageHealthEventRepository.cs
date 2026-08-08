using System.Security.Cryptography;
using System.Text;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

public sealed class StorageHealthEventRepository
{
    private readonly WinPoolSqliteStore _store;
    private readonly AgentWriteOwnerLease _writeOwner;

    public StorageHealthEventRepository(
        WinPoolSqliteStore store,
        AgentWriteOwnerLease writeOwner)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _writeOwner = writeOwner ?? throw new ArgumentNullException(nameof(writeOwner));
        _writeOwner.AssertOwnership(_store);
    }

    public async Task AddAsync(
        StorageHealthEvent storageEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storageEvent);
        _writeOwner.AssertOwnership(_store);
        await using var connection = await _store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO storage_health_events(
                event_key, channel, provider, record_id, windows_event_id,
                severity, occurred_at_utc_ms, message)
            VALUES(
                $key, $channel, $provider, $record, $event,
                $severity, $occurred, $message);
            """;
        command.Parameters.AddWithValue("$key", CreateKey(storageEvent));
        command.Parameters.AddWithValue("$channel", Limit(storageEvent.Channel, 256));
        command.Parameters.AddWithValue("$provider", Limit(storageEvent.Provider, 256));
        command.Parameters.AddWithValue(
            "$record",
            storageEvent.RecordId.HasValue
                ? storageEvent.RecordId.Value
                : DBNull.Value);
        command.Parameters.AddWithValue("$event", storageEvent.EventId);
        command.Parameters.AddWithValue("$severity", (int)storageEvent.Severity);
        command.Parameters.AddWithValue(
            "$occurred",
            storageEvent.OccurredAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$message", Limit(storageEvent.Message, 8192));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StorageHealthEvent>> ListRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is <= 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var connection = await _store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT channel, provider, record_id, windows_event_id, severity,
                   occurred_at_utc_ms, message
            FROM storage_health_events
            ORDER BY occurred_at_utc_ms DESC, event_key DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", maximumCount);
        var result = new List<StorageHealthEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new StorageHealthEvent(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.GetInt32(3),
                    (StorageHealthEventSeverity)reader.GetInt32(4),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                    reader.GetString(6)));
        }

        result.Reverse();
        return result;
    }

    private static string CreateKey(StorageHealthEvent storageEvent)
    {
        var material = string.Join(
            '\n',
            storageEvent.Channel,
            storageEvent.Provider,
            storageEvent.RecordId?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "<none>",
            storageEvent.EventId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            storageEvent.OccurredAtUtc.ToUniversalTime().ToString("O"),
            storageEvent.Message);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private static string Limit(string? value, int maximumLength)
    {
        value ??= string.Empty;
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }
}
