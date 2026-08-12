using Microsoft.Data.Sqlite;

namespace WinPool.Infrastructure.Sqlite;

public sealed record SqliteSchemaVersion(
    int Version,
    DateTimeOffset AppliedAtUtc);

public sealed class UnsupportedSqliteSchemaVersionException : InvalidOperationException
{
    public UnsupportedSqliteSchemaVersionException(int actualVersion, int maximumSupportedVersion)
        : base(
            $"数据库 schema 版本 {actualVersion} 高于当前支持的版本 " +
            $"{maximumSupportedVersion}，为防止数据损坏已拒绝打开。")
    {
        ActualVersion = actualVersion;
        MaximumSupportedVersion = maximumSupportedVersion;
    }

    public int ActualVersion { get; }

    public int MaximumSupportedVersion { get; }
}

public sealed class LegacySqliteSchemaNotSupportedException : InvalidOperationException
{
    public const string StableCode = "storage.schema.legacy_not_supported";

    public LegacySqliteSchemaNotSupportedException(int? actualVersion)
        : base(actualVersion is null
            ? $"{StableCode}: existing data has no supported WinPool schema."
            : $"{StableCode}: schema {actualVersion} is older than the required schema "
              + $"{WinPoolSqliteStore.CurrentSchemaVersion}.")
    {
        ActualVersion = actualVersion;
    }

    public int? ActualVersion { get; }
}

public sealed class CurrentSqliteSchemaCorruptException : InvalidOperationException
{
    public const string StableCode = "storage.schema.current_corrupt";

    public CurrentSqliteSchemaCorruptException(string mismatch) : base(
        $"{StableCode}: the schema-12 database does not match the required contract ({mismatch}).")
    {
        Mismatch = mismatch;
    }

    public string Mismatch { get; }
}

public sealed class SqliteSchemaVersionReader
{
    private readonly WinPoolSqliteStore store;

    public SqliteSchemaVersionReader(WinPoolSqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public async Task<SqliteSchemaVersion?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, cancellationToken);
    }

    public async Task<SqliteSchemaVersion?> EnsureSupportedAsync(
        CancellationToken cancellationToken = default)
    {
        var version = await ReadAsync(cancellationToken);
        ThrowIfNewer(version, WinPoolSqliteStore.CurrentSchemaVersion);
        return version;
    }

    internal static async Task<SqliteSchemaVersion?> ReadAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM sqlite_schema
                WHERE type = 'table' AND name = 'schema_info');
            """;
        var exists = Convert.ToInt64(
            await tableCommand.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
        if (!exists)
        {
            return null;
        }

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = """
            SELECT schema_version, applied_at_utc_ms
            FROM schema_info
            WHERE singleton = 1;
            """;
        await using var reader = await versionCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "schema_info 表存在，但缺少 singleton=1 的版本记录。");
        }

        var schemaVersion = reader.GetInt32(0);
        if (schemaVersion <= 0)
        {
            throw new InvalidDataException(
                $"schema_info 包含无效的 schema 版本 {schemaVersion}。");
        }

        return new SqliteSchemaVersion(
            schemaVersion,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)));
    }

    internal static void ThrowIfNewer(
        SqliteSchemaVersion? version,
        int maximumSupportedVersion)
    {
        if (version is { Version: var actual } && actual > maximumSupportedVersion)
        {
            throw new UnsupportedSqliteSchemaVersionException(
                actual,
                maximumSupportedVersion);
        }
    }
}
