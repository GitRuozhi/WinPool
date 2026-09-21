using Microsoft.Data.Sqlite;

namespace WinPool.Infrastructure.Sqlite;

/// <summary>
/// Owns the short-lived active monitoring database. It intentionally has an
/// independent format number: a monitoring database is never a migration of
/// the core <c>winpool.db</c>, and an existing non-current database is refused
/// rather than modified in place.
/// </summary>
public sealed class MonitoringSqliteStore : ISqliteDatabaseStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string connectionString;

    public MonitoringSqliteStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        connectionString = BuildConnectionString(DatabasePath);
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("The monitoring database path has no parent directory.");
        Directory.CreateDirectory(parent);

        var inspection = await InspectExistingAsync(cancellationToken);
        if (inspection.HasUserTables)
        {
            if (inspection.SchemaVersion is null
                || inspection.SchemaVersion < CurrentSchemaVersion)
            {
                throw new LegacyMonitoringSchemaNotSupportedException(inspection.SchemaVersion);
            }

            if (inspection.SchemaVersion > CurrentSchemaVersion)
            {
                throw new UnsupportedMonitoringSchemaVersionException(
                    inspection.SchemaVersion.Value,
                    CurrentSchemaVersion);
            }

            await VerifyCurrentSchemaAsync(cancellationToken);
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CurrentSchemaDefinition;
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = """
            INSERT INTO monitoring_schema_info(singleton, schema_version, applied_at_utc_ms)
            VALUES(1, $version, $applied);
            """;
        command.Parameters.AddWithValue("$version", CurrentSchemaVersion);
        command.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    /// Makes the active database self-contained before it is sealed. The
    /// caller must already have stopped all monitoring connections. WAL and
    /// SHM are not renamed as a unit; after this succeeds only the database
    /// file contains committed monitoring data.
    /// </summary>
    public async Task CheckpointAndCloseAsync(CancellationToken cancellationToken = default)
    {
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using var result = await command.ExecuteReaderAsync(cancellationToken);
            if (!await result.ReadAsync(cancellationToken))
            {
                throw new IOException("SQLite did not report the monitoring checkpoint result.");
            }

            var busy = result.GetInt64(0);
            var logFrames = result.GetInt64(1);
            var checkpointedFrames = result.GetInt64(2);
            if (busy != 0 || logFrames != checkpointedFrames)
            {
                throw new IOException(
                    $"The monitoring checkpoint was incomplete (busy={busy}, log={logFrames}, checkpointed={checkpointedFrames}).");
            }
        }

        DrainConnectionPool();
        var walPath = DatabasePath + "-wal";
        if (File.Exists(walPath) && new FileInfo(walPath).Length > 0)
        {
            throw new IOException("The monitoring WAL still contains data after checkpoint.");
        }

        // A successful TRUNCATE checkpoint makes WAL data unnecessary, but
        // Windows can retain empty WAL/SHM files. Verify that no connection is
        // still open before removing only those rebuildable sidecars; the main
        // database is never deleted or renamed here.
        using (var exclusive = new FileStream(
                   DatabasePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None,
                   bufferSize: 1,
                   FileOptions.RandomAccess))
        {
            _ = exclusive.ReadByte();
        }

        if (File.Exists(walPath))
        {
            File.Delete(walPath);
        }

        var sharedMemoryPath = DatabasePath + "-shm";
        if (File.Exists(sharedMemoryPath))
        {
            File.Delete(sharedMemoryPath);
        }
    }

    public void DrainConnectionPool()
    {
        using var poolIdentity = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(poolIdentity);
    }

    private async Task<ExistingInspection> InspectExistingAsync(
        CancellationToken cancellationToken)
    {
        var walPath = DatabasePath + "-wal";
        var sharedMemoryPath = DatabasePath + "-shm";
        if (!File.Exists(DatabasePath))
        {
            if (File.Exists(walPath) || File.Exists(sharedMemoryPath))
            {
                throw new CurrentMonitoringSchemaCorruptException("orphaned_sidecar");
            }

            return new(false, null);
        }

        if (new FileInfo(DatabasePath).Length == 0)
        {
            if (File.Exists(walPath) || File.Exists(sharedMemoryPath))
            {
                throw new CurrentMonitoringSchemaCorruptException("empty_primary_with_sidecar");
            }

            return new(false, null);
        }

        await using var readOnly = await OpenReadOnlyDatabaseAsync(cancellationToken);
        var connection = readOnly.Connection;
        await using var tables = connection.CreateCommand();
        tables.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%';
            """;
        var hasUserTables = Convert.ToInt64(
            await tables.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) > 0;
        if (!hasUserTables)
        {
            return new(false, null);
        }

        await using var version = connection.CreateCommand();
        version.CommandText = """
            SELECT schema_version
            FROM monitoring_schema_info
            WHERE singleton = 1;
            """;
        try
        {
            var result = await version.ExecuteScalarAsync(cancellationToken);
            return new(true, result is null || result == DBNull.Value
                ? null
                : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (SqliteException)
        {
            return new(true, null);
        }
    }

    private async Task VerifyCurrentSchemaAsync(CancellationToken cancellationToken)
    {
        await using var actualReadOnly = await OpenReadOnlyDatabaseAsync(cancellationToken);
        var actualConnection = actualReadOnly.Connection;
        await using var expectedConnection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = ":memory:",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true
            }.ToString());
        await expectedConnection.OpenAsync(cancellationToken);
        await using (var create = expectedConnection.CreateCommand())
        {
            create.CommandText = CurrentSchemaDefinition;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var expected = await ReadContractAsync(expectedConnection, cancellationToken);
        var actual = await ReadContractAsync(actualConnection, cancellationToken);
        var mismatch = FindMismatch(expected, actual);
        if (mismatch is not null)
        {
            throw new CurrentMonitoringSchemaCorruptException(mismatch);
        }

        await using var integrity = actualConnection.CreateCommand();
        integrity.CommandText = "PRAGMA foreign_key_check;";
        await using var violations = await integrity.ExecuteReaderAsync(cancellationToken);
        if (await violations.ReadAsync(cancellationToken))
        {
            throw new CurrentMonitoringSchemaCorruptException("foreign_keys");
        }
    }

    private static string BuildConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();

    private async Task<ReadOnlyDatabase> OpenReadOnlyDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var walPath = DatabasePath + "-wal";
        string? copiedDirectory = null;
        var dataSource = DatabasePath;
        if (File.Exists(walPath))
        {
            // immutable=1 would ignore committed schema data still residing in
            // a WAL after an unclean exit. A normal read-only SQLite open sees
            // it, but can create a shared-memory sidecar beside an unknown
            // format. Inspect a private byte-for-byte copy instead, leaving the
            // source database and its WAL entirely untouched.
            copiedDirectory = Path.Combine(
                Path.GetTempPath(),
                $"WinPool-monitoring-inspect-{Guid.NewGuid():N}");
            Directory.CreateDirectory(copiedDirectory);
            dataSource = Path.Combine(copiedDirectory, Path.GetFileName(DatabasePath));
            File.Copy(DatabasePath, dataSource);
            File.Copy(walPath, dataSource + "-wal");
            var sourceSharedMemory = DatabasePath + "-shm";
            if (File.Exists(sourceSharedMemory))
            {
                File.Copy(sourceSharedMemory, dataSource + "-shm");
            }
        }

        // Without a WAL, immutable=1 prevents even a read-only schema probe
        // from creating a sidecar next to a legacy or corrupted source file.
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = copiedDirectory is null
                    ? $"file:{DatabasePath.Replace('\\', '/')}?immutable=1"
                    : dataSource,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 5
            }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            return new ReadOnlyDatabase(connection, copiedDirectory);
        }
        catch
        {
            await connection.DisposeAsync();
            if (copiedDirectory is not null)
            {
                Directory.Delete(copiedDirectory, recursive: true);
            }

            throw;
        }
    }

    private static async Task<SchemaContract> ReadContractAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tableNames = await ReadRowsAsync(
            connection,
            """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """,
            reader => reader.GetString(0),
            cancellationToken);
        var tables = new Dictionary<string, TableContract>(StringComparer.Ordinal);
        foreach (var tableName in tableNames)
        {
            var definition = await ReadDefinitionAsync(
                connection,
                "table",
                tableName,
                cancellationToken)
                ?? throw new InvalidDataException($"SQLite did not return table {tableName}.");
            var columns = await ReadRowsAsync(
                connection,
                $"PRAGMA table_info({QuoteIdentifier(tableName)});",
                reader => string.Join(
                    '|',
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? "<null>" : reader.GetString(4),
                    reader.GetInt32(5)),
                cancellationToken);
            var indexes = await ReadIndexesAsync(connection, tableName, cancellationToken);
            var foreignKeys = await ReadRowsAsync(
                connection,
                $"PRAGMA foreign_key_list({QuoteIdentifier(tableName)});",
                reader => string.Join(
                    '|',
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7)),
                cancellationToken);
            tables.Add(
                tableName,
                new TableContract(
                    columns.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    indexes.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    foreignKeys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    ExtractCheckConstraints(definition)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray(),
                    NormalizeSql(definition)));
        }

        return new SchemaContract(tables);
    }

    private static async Task<IReadOnlyList<string>> ReadIndexesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var indexNames = await ReadRowsAsync(
            connection,
            $"PRAGMA index_list({QuoteIdentifier(tableName)});",
            reader => reader.GetString(1),
            cancellationToken);
        var indexes = new List<string>();
        foreach (var indexName in indexNames)
        {
            var columns = await ReadRowsAsync(
                connection,
                $"PRAGMA index_xinfo({QuoteIdentifier(indexName)});",
                reader => string.Join(
                    '|',
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? "<null>" : reader.GetString(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? "<null>" : reader.GetString(4),
                    reader.GetInt32(5)),
                cancellationToken);
            var definition = await ReadDefinitionAsync(
                connection,
                "index",
                indexName,
                cancellationToken,
                required: false);
            indexes.Add($"{indexName}|{NormalizeSql(definition ?? "<implicit>")}|{string.Join(',', columns)}");
        }

        return indexes;
    }

    private static async Task<IReadOnlyList<string>> ReadRowsAsync(
        SqliteConnection connection,
        string commandText,
        Func<SqliteDataReader, string> project,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(project(reader));
        }

        return rows;
    }

    private static async Task<string?> ReadDefinitionAsync(
        SqliteConnection connection,
        string type,
        string name,
        CancellationToken cancellationToken,
        bool required = true)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sql
            FROM sqlite_schema
            WHERE type = $type AND name = $name;
            """;
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result == DBNull.Value)
        {
            if (required)
            {
                throw new InvalidDataException($"SQLite did not return {type} {name}.");
            }

            return null;
        }

        return Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? FindMismatch(SchemaContract expected, SchemaContract actual)
    {
        var expectedTables = expected.Tables.Keys.OrderBy(value => value, StringComparer.Ordinal);
        var actualTables = actual.Tables.Keys.OrderBy(value => value, StringComparer.Ordinal);
        if (!expectedTables.SequenceEqual(actualTables, StringComparer.Ordinal))
        {
            return "tables";
        }

        foreach (var tableName in expectedTables)
        {
            var expectedTable = expected.Tables[tableName];
            var actualTable = actual.Tables[tableName];
            if (!expectedTable.Columns.SequenceEqual(actualTable.Columns, StringComparer.Ordinal))
            {
                return $"{tableName}.columns";
            }
            if (!expectedTable.Indexes.SequenceEqual(actualTable.Indexes, StringComparer.Ordinal))
            {
                return $"{tableName}.indexes";
            }
            if (!expectedTable.ForeignKeys.SequenceEqual(actualTable.ForeignKeys, StringComparer.Ordinal))
            {
                return $"{tableName}.foreign_keys";
            }
            if (!expectedTable.Checks.SequenceEqual(actualTable.Checks, StringComparer.Ordinal))
            {
                return $"{tableName}.checks";
            }
            if (!string.Equals(expectedTable.Definition, actualTable.Definition, StringComparison.Ordinal))
            {
                return $"{tableName}.definition";
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractCheckConstraints(string definition)
    {
        var checks = new List<string>();
        var offset = 0;
        while (offset < definition.Length)
        {
            var check = definition.IndexOf("CHECK", offset, StringComparison.OrdinalIgnoreCase);
            if (check < 0)
            {
                break;
            }

            var before = check == 0 ? '\0' : definition[check - 1];
            var afterOffset = check + "CHECK".Length;
            var after = afterOffset >= definition.Length ? '\0' : definition[afterOffset];
            if (char.IsLetterOrDigit(before) || before == '_'
                || char.IsLetterOrDigit(after) || after == '_')
            {
                offset = afterOffset;
                continue;
            }

            var opening = afterOffset;
            while (opening < definition.Length && char.IsWhiteSpace(definition[opening]))
            {
                opening++;
            }
            if (opening >= definition.Length || definition[opening] != '(')
            {
                throw new InvalidDataException("SQLite returned an invalid CHECK constraint.");
            }

            var depth = 0;
            var closing = opening;
            for (; closing < definition.Length; closing++)
            {
                if (definition[closing] == '(')
                {
                    depth++;
                }
                else if (definition[closing] == ')' && --depth == 0)
                {
                    break;
                }
            }
            if (depth != 0 || closing >= definition.Length)
            {
                throw new InvalidDataException("SQLite returned an unterminated CHECK constraint.");
            }

            checks.Add(NormalizeSql(definition[(opening + 1)..closing]));
            offset = closing + 1;
        }

        return checks;
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string NormalizeSql(string definition) =>
        string.Concat(definition.Where(character => !char.IsWhiteSpace(character)));

    private sealed record ExistingInspection(bool HasUserTables, int? SchemaVersion);

    private sealed class ReadOnlyDatabase(
        SqliteConnection connection,
        string? copiedDirectory) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            if (copiedDirectory is not null && Directory.Exists(copiedDirectory))
            {
                Directory.Delete(copiedDirectory, recursive: true);
            }
        }
    }

    private sealed record SchemaContract(IReadOnlyDictionary<string, TableContract> Tables);

    private sealed record TableContract(
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> Indexes,
        IReadOnlyList<string> ForeignKeys,
        IReadOnlyList<string> Checks,
        string Definition);

    private const string CurrentSchemaDefinition = """
        CREATE TABLE IF NOT EXISTS monitoring_schema_info(
            singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
            schema_version INTEGER NOT NULL,
            applied_at_utc_ms INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS monitor_sessions(
            session_id TEXT PRIMARY KEY,
            started_at_utc_ms INTEGER NOT NULL,
            ended_at_utc_ms INTEGER,
            clock_source TEXT NOT NULL,
            state INTEGER NOT NULL,
            dropped_samples INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS monitor_devices(
            session_id TEXT NOT NULL REFERENCES monitor_sessions(session_id) ON DELETE CASCADE,
            device_id TEXT NOT NULL,
            sanitized_name TEXT NOT NULL,
            source_kind INTEGER NOT NULL,
            PRIMARY KEY(session_id, device_id)
        );
        CREATE TABLE IF NOT EXISTS monitor_samples(
            session_id TEXT NOT NULL,
            device_id TEXT NOT NULL,
            timestamp_utc_ms INTEGER NOT NULL,
            activity_pct REAL,
            read_bytes_per_sec REAL,
            write_bytes_per_sec REAL,
            read_operations_per_sec REAL,
            write_operations_per_sec REAL,
            queue_length REAL,
            average_latency_ms REAL,
            cpu_pct REAL,
            virtual_disk_active_bytes REAL,
            virtual_disk_missing_bytes REAL,
            virtual_disk_stale_bytes REAL,
            virtual_disk_need_regeneration_bytes REAL,
            virtual_disk_regenerating_bytes REAL,
            virtual_disk_pending_deletion_bytes REAL,
            FOREIGN KEY(session_id, device_id)
                REFERENCES monitor_devices(session_id, device_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_monitor_samples_session_time
            ON monitor_samples(session_id, timestamp_utc_ms);
        CREATE INDEX IF NOT EXISTS ix_monitor_samples_session_device_time
            ON monitor_samples(session_id, device_id, timestamp_utc_ms);
        """;
}

public sealed class LegacyMonitoringSchemaNotSupportedException : InvalidOperationException
{
    public const string StableCode = "monitoring.schema.legacy_not_supported";

    public LegacyMonitoringSchemaNotSupportedException(int? actualVersion)
        : base(actualVersion is null
            ? $"{StableCode}: existing monitoring data has no supported schema."
            : $"{StableCode}: schema {actualVersion} is older than required schema "
              + $"{MonitoringSqliteStore.CurrentSchemaVersion}.")
    {
        ActualVersion = actualVersion;
    }

    public int? ActualVersion { get; }
}

public sealed class UnsupportedMonitoringSchemaVersionException : InvalidOperationException
{
    public UnsupportedMonitoringSchemaVersionException(int actualVersion, int maximumSupportedVersion)
        : base($"monitoring.schema.newer_not_supported: schema {actualVersion} is newer than supported schema {maximumSupportedVersion}.")
    {
        ActualVersion = actualVersion;
        MaximumSupportedVersion = maximumSupportedVersion;
    }

    public int ActualVersion { get; }

    public int MaximumSupportedVersion { get; }
}

public sealed class CurrentMonitoringSchemaCorruptException : InvalidOperationException
{
    public CurrentMonitoringSchemaCorruptException(string mismatch)
        : base($"monitoring.schema.current_corrupt: monitoring database does not match its required contract ({mismatch}).")
    {
        Mismatch = mismatch;
    }

    public string Mismatch { get; }
}
