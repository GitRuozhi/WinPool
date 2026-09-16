using Microsoft.Data.Sqlite;
using WinPool.Application;

namespace WinPool.Infrastructure.Sqlite;

/// <summary>Reads the last committed facts before the Agent connects. Never initializes a database.</summary>
public sealed class ReadOnlyLocalInventoryReader(string databasePath)
{
    public async Task<LocalInventoryDocumentPayload?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return null;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 2
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT schema_version FROM schema_info WHERE singleton = 1;";
        var actual = Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken));
        if (actual < WinPoolSqliteStore.CurrentSchemaVersion)
            throw new LegacySqliteSchemaNotSupportedException(actual);
        if (actual > WinPoolSqliteStore.CurrentSchemaVersion)
            throw new UnsupportedSqliteSchemaVersionException(actual, WinPoolSqliteStore.CurrentSchemaVersion);
        return (await LocalInventoryDocumentRepository.ReadAsync(connection, cancellationToken))?.Document;
    }
}
