using Microsoft.Data.Sqlite;

namespace WinPool.Infrastructure.Sqlite;

public sealed class WinPoolSqliteStore
{
    // V0.41 starts from a deliberately clean data root. Do not add a
    // migration from schema 12: its generic preferences table mixed distinct
    // ownership domains and is intentionally rejected by InitializeAsync.
    public const int CurrentSchemaVersion = 14;

    private readonly string connectionString;

    public WinPoolSqliteStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        connectionString = BuildConnectionString(DatabasePath);
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Drains only the Microsoft.Data.Sqlite pool associated with this WinPool
    /// database. Open connections are marked so they cannot return to that pool.
    /// Callers must first stop new connections and wait for in-flight work.
    /// </summary>
    internal void DrainConnectionPool()
    {
        using var poolIdentity = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(poolIdentity);
    }

    internal static void DrainConnectionPool(string databasePath)
    {
        using var poolIdentity = new SqliteConnection(
            BuildConnectionString(Path.GetFullPath(databasePath)));
        SqliteConnection.ClearPool(poolIdentity);
    }

    private static string BuildConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            // Storage-location migration requires deterministic handle release.
            // WinPool has one Agent writer, so connection reuse is less valuable
            // than making each awaited disposal close its native file handles.
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("The database path has no parent directory.");
        Directory.CreateDirectory(parent);

        var existing = await InspectExistingDatabaseAsync(cancellationToken);
        if (existing.HasUserTables)
        {
            var version = existing.Version;
            if (version is null || version.Version < CurrentSchemaVersion)
            {
                throw new LegacySqliteSchemaNotSupportedException(version?.Version);
            }
            if (version.Version > CurrentSchemaVersion)
            {
                throw new UnsupportedSqliteSchemaVersionException(
                    version.Version,
                    CurrentSchemaVersion);
            }

            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaV1;
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.CommandText = """
            INSERT INTO schema_info(singleton, schema_version, applied_at_utc_ms)
            VALUES (1, $version, $applied)
            ON CONFLICT(singleton) DO UPDATE SET
                schema_version = excluded.schema_version,
                applied_at_utc_ms = excluded.applied_at_utc_ms;
            """;
        command.Parameters.AddWithValue("$version", CurrentSchemaVersion);
        command.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<ExistingDatabaseInspection> InspectExistingDatabaseAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(DatabasePath) || new FileInfo(DatabasePath).Length == 0)
        {
            return new ExistingDatabaseInspection(false, null);
        }

        var readOnlyConnectionString = new SqliteConnectionStringBuilder
        {
            // immutable=1 prevents a read-only schema probe from creating a
            // SQLite shared-memory sidecar beside an unsupported database.
            DataSource = $"file:{DatabasePath.Replace('\\', '/')}?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(readOnlyConnectionString);
        await connection.OpenAsync(cancellationToken);
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
            return new ExistingDatabaseInspection(false, null);
        }

        var version = await SqliteSchemaVersionReader.ReadAsync(connection, cancellationToken);
        if (version?.Version == CurrentSchemaVersion)
        {
            await CurrentSchemaVerifier.VerifyAsync(connection, cancellationToken);
        }

        return new ExistingDatabaseInspection(true, version);
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

    private sealed record ExistingDatabaseInspection(
        bool HasUserTables,
        SqliteSchemaVersion? Version);

    /// <summary>
    /// Validates a current-version database without opening it for write. The
    /// contract is the complete schema definition below, materialized only in
    /// an isolated in-memory connection and compared as tables, columns,
    /// indexes, foreign keys, and CHECK constraints.
    /// </summary>
    private static class CurrentSchemaVerifier
    {
        public static async Task VerifyAsync(
            SqliteConnection actualConnection,
            CancellationToken cancellationToken)
        {
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
                create.CommandText = SchemaV1;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            var expected = await ReadContractAsync(expectedConnection, cancellationToken);
            var actual = await ReadContractAsync(actualConnection, cancellationToken);
            var mismatch = FindMismatch(expected, actual);
            if (mismatch is not null)
            {
                throw new CurrentSqliteSchemaCorruptException(mismatch);
            }
        }

        private static async Task<SchemaContract> ReadContractAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
        {
            var tableNames = new List<string>();
            await using (var tables = connection.CreateCommand())
            {
                tables.CommandText = """
                    SELECT name
                    FROM sqlite_schema
                    WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                    ORDER BY name;
                    """;
                await using var reader = await tables.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    tableNames.Add(reader.GetString(0));
                }
            }

            var tablesByName = new Dictionary<string, TableContract>(StringComparer.Ordinal);
            foreach (var tableName in tableNames)
            {
                var definition = await ReadSchemaDefinitionAsync(
                    connection,
                    "table",
                    tableName,
                    cancellationToken)
                    ?? throw new InvalidDataException(
                        $"SQLite did not return a definition for table {tableName}.");
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
                var checks = await ReadCheckConstraintsAsync(
                    connection,
                    tableName,
                    cancellationToken);
                tablesByName.Add(
                    tableName,
                    new(
                        columns.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        indexes.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        foreignKeys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        checks.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        NormalizeSql(definition)));
            }

            return new(tablesByName);
        }

        private static async Task<IReadOnlyList<string>> ReadIndexesAsync(
            SqliteConnection connection,
            string tableName,
            CancellationToken cancellationToken)
        {
            var indexNames = await ReadRowsAsync(
                connection,
                $"PRAGMA index_list({QuoteIdentifier(tableName)});",
                reader => string.Join(
                    '|',
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetInt32(4)),
                cancellationToken);
            var indexes = new List<string>();
            foreach (var index in indexNames)
            {
                var name = index.Split('|', 2)[0];
                var columns = await ReadRowsAsync(
                    connection,
                    $"PRAGMA index_xinfo({QuoteIdentifier(name)});",
                    reader => string.Join(
                        '|',
                        reader.GetInt32(0),
                        reader.GetInt32(1),
                        reader.IsDBNull(2) ? "<null>" : reader.GetString(2),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? "<null>" : reader.GetString(4),
                        reader.GetInt32(5)),
                    cancellationToken);
                var definition = await ReadSchemaDefinitionAsync(
                    connection,
                    "index",
                    name,
                    cancellationToken,
                    required: false);
                indexes.Add(
                    $"{index}|{NormalizeSql(definition ?? "<implicit>")}|"
                    + string.Join(',', columns));
            }

            return indexes;
        }

        private static async Task<IReadOnlyList<string>> ReadCheckConstraintsAsync(
            SqliteConnection connection,
            string tableName,
            CancellationToken cancellationToken)
        {
            var definition = await ReadSchemaDefinitionAsync(
                connection,
                "table",
                tableName,
                cancellationToken)
                ?? throw new InvalidDataException(
                    $"SQLite did not return a definition for table {tableName}.");
            return ExtractCheckConstraints(definition);
        }

        private static async Task<string?> ReadSchemaDefinitionAsync(
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
                    throw new InvalidDataException(
                        $"SQLite did not return a definition for {type} {name}.");
                }

                return null;
            }

            return Convert.ToString(
                result,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string NormalizeSql(string definition) =>
            string.Concat(definition.Where(character => !char.IsWhiteSpace(character)));

        private static IReadOnlyList<string> ExtractCheckConstraints(string definition)
        {
            var checks = new List<string>();
            var searchOffset = 0;
            while (searchOffset < definition.Length)
            {
                var checkOffset = definition.IndexOf(
                    "CHECK",
                    searchOffset,
                    StringComparison.OrdinalIgnoreCase);
                if (checkOffset < 0)
                {
                    break;
                }

                var before = checkOffset == 0 ? '\0' : definition[checkOffset - 1];
                var afterOffset = checkOffset + "CHECK".Length;
                var after = afterOffset >= definition.Length ? '\0' : definition[afterOffset];
                if ((char.IsLetterOrDigit(before) || before == '_')
                    || (char.IsLetterOrDigit(after) || after == '_'))
                {
                    searchOffset = afterOffset;
                    continue;
                }

                var openingOffset = afterOffset;
                while (openingOffset < definition.Length
                       && char.IsWhiteSpace(definition[openingOffset]))
                {
                    openingOffset++;
                }
                if (openingOffset >= definition.Length || definition[openingOffset] != '(')
                {
                    throw new InvalidDataException("SQLite returned an invalid CHECK constraint.");
                }

                var depth = 0;
                var closingOffset = openingOffset;
                for (; closingOffset < definition.Length; closingOffset++)
                {
                    if (definition[closingOffset] == '(')
                    {
                        depth++;
                    }
                    else if (definition[closingOffset] == ')' && --depth == 0)
                    {
                        break;
                    }
                }
                if (depth != 0 || closingOffset >= definition.Length)
                {
                    throw new InvalidDataException("SQLite returned an unterminated CHECK constraint.");
                }

                var expression = definition[(openingOffset + 1)..closingOffset];
                checks.Add(string.Concat(expression.Where(character => !char.IsWhiteSpace(character))));
                searchOffset = closingOffset + 1;
            }

            return checks;
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

        private static string? FindMismatch(
            SchemaContract expected,
            SchemaContract actual)
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
                if (!StringComparer.Ordinal.Equals(
                        expectedTable.Definition,
                        actualTable.Definition))
                {
                    return $"{tableName}.definition";
                }
            }

            return null;
        }

        private static string QuoteIdentifier(string value) =>
            $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

        private sealed record SchemaContract(
            IReadOnlyDictionary<string, TableContract> Tables);

        private sealed record TableContract(
            IReadOnlyList<string> Columns,
            IReadOnlyList<string> Indexes,
            IReadOnlyList<string> ForeignKeys,
            IReadOnlyList<string> Checks,
            string Definition);
    }

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS schema_info(
            singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
            schema_version INTEGER NOT NULL,
            applied_at_utc_ms INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS workspace_state(
            singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
            json TEXT NOT NULL,
            updated_at_utc_ms INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS systems(
            system_id TEXT PRIMARY KEY,
            kind INTEGER NOT NULL,
            display_name TEXT NOT NULL,
            machine_binding_hash TEXT,
            created_at_utc_ms INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS inventory_snapshots(
            snapshot_id TEXT PRIMARY KEY,
            system_id TEXT NOT NULL REFERENCES systems(system_id),
            inventory_version TEXT NOT NULL,
            captured_at_utc_ms INTEGER NOT NULL,
            provider_kind INTEGER NOT NULL,
            sanitized_json TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_inventory_snapshots_system_time
            ON inventory_snapshots(system_id, captured_at_utc_ms DESC);
        CREATE TABLE IF NOT EXISTS local_inventory_document(
            singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
            snapshot_id TEXT NOT NULL REFERENCES inventory_snapshots(snapshot_id),
            document_id TEXT NOT NULL,
            document_schema_version INTEGER NOT NULL,
            display_name TEXT NOT NULL,
            sanitized_json TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            captured_at_utc_ms INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS storage_objects(
            snapshot_id TEXT NOT NULL REFERENCES inventory_snapshots(snapshot_id) ON DELETE CASCADE,
            object_id TEXT NOT NULL,
            object_kind INTEGER NOT NULL,
            provider_key_hash TEXT NOT NULL,
            sanitized_json TEXT NOT NULL,
            PRIMARY KEY(snapshot_id, object_id)
        );
        CREATE TABLE IF NOT EXISTS storage_relationships(
            snapshot_id TEXT NOT NULL REFERENCES inventory_snapshots(snapshot_id) ON DELETE CASCADE,
            from_object_id TEXT NOT NULL,
            to_object_id TEXT NOT NULL,
            relationship_kind TEXT NOT NULL,
            PRIMARY KEY(snapshot_id, from_object_id, to_object_id, relationship_kind)
        );
        CREATE TABLE IF NOT EXISTS operation_plans(
            operation_id TEXT PRIMARY KEY,
            plan_hash TEXT NOT NULL UNIQUE,
            environment_id TEXT NOT NULL,
            risk INTEGER NOT NULL,
            state INTEGER NOT NULL,
            sanitized_json TEXT NOT NULL,
            created_at_utc_ms INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS operation_steps(
            operation_id TEXT NOT NULL REFERENCES operation_plans(operation_id) ON DELETE CASCADE,
            step_id TEXT NOT NULL,
            sequence_no INTEGER NOT NULL,
            state INTEGER NOT NULL,
            sanitized_json TEXT NOT NULL,
            PRIMARY KEY(operation_id, step_id)
        );
        CREATE TABLE IF NOT EXISTS execution_events(
            event_id INTEGER PRIMARY KEY AUTOINCREMENT,
            operation_id TEXT NOT NULL REFERENCES operation_plans(operation_id),
            timestamp_utc_ms INTEGER NOT NULL,
            kind INTEGER NOT NULL,
            code TEXT NOT NULL,
            sanitized_message TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_execution_events_operation_time
            ON execution_events(operation_id, timestamp_utc_ms);
        CREATE TABLE IF NOT EXISTS simulation_documents(
            document_id TEXT PRIMARY KEY,
            document_schema_version INTEGER NOT NULL,
            display_name TEXT NOT NULL,
            sanitized_json TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            revision INTEGER NOT NULL,
            created_at_utc_ms INTEGER NOT NULL,
            updated_at_utc_ms INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS simulation_edit_commits(
            operation_id TEXT PRIMARY KEY REFERENCES operation_plans(operation_id),
            document_id TEXT NOT NULL,
            before_sha256 TEXT NOT NULL,
            after_sha256 TEXT NOT NULL,
            document_revision INTEGER NOT NULL,
            committed_at_utc_ms INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_simulation_edit_commits_document_revision
            ON simulation_edit_commits(document_id, document_revision DESC);
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
            activity_pct REAL NOT NULL,
            read_bytes_per_sec REAL NOT NULL,
            write_bytes_per_sec REAL NOT NULL,
            queue_length REAL NOT NULL,
            FOREIGN KEY(session_id, device_id)
                REFERENCES monitor_devices(session_id, device_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_monitor_samples_session_time
            ON monitor_samples(session_id, timestamp_utc_ms);
        CREATE INDEX IF NOT EXISTS ix_monitor_samples_session_device_time
            ON monitor_samples(session_id, device_id, timestamp_utc_ms);
        CREATE TABLE IF NOT EXISTS storage_health_events(
            event_key TEXT PRIMARY KEY,
            channel TEXT NOT NULL,
            provider TEXT NOT NULL,
            record_id INTEGER,
            windows_event_id INTEGER NOT NULL,
            severity INTEGER NOT NULL,
            occurred_at_utc_ms INTEGER NOT NULL,
            message TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_storage_health_events_time
            ON storage_health_events(occurred_at_utc_ms, event_key);
        CREATE TABLE IF NOT EXISTS monitor_rollups(
            session_id TEXT NOT NULL REFERENCES monitor_sessions(session_id) ON DELETE CASCADE,
            device_id TEXT NOT NULL,
            bucket_start_utc_ms INTEGER NOT NULL,
            bucket_width_ms INTEGER NOT NULL,
            sample_count INTEGER NOT NULL,
            sanitized_json TEXT NOT NULL,
            PRIMARY KEY(session_id, device_id, bucket_start_utc_ms, bucket_width_ms)
        );
        CREATE TABLE IF NOT EXISTS inventory_comparisons(
            comparison_id TEXT PRIMARY KEY,
            reference_snapshot_id TEXT NOT NULL REFERENCES inventory_snapshots(snapshot_id),
            candidate_snapshot_id TEXT NOT NULL REFERENCES inventory_snapshots(snapshot_id),
            sanitized_json TEXT NOT NULL,
            created_at_utc_ms INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS agent_sessions(
            session_id TEXT PRIMARY KEY,
            process_id INTEGER NOT NULL,
            started_at_utc_ms INTEGER NOT NULL,
            ended_at_utc_ms INTEGER,
            shutdown_clean INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS worker_processes(
            process_instance_id TEXT PRIMARY KEY,
            process_id INTEGER NOT NULL,
            agent_session_id TEXT NOT NULL REFERENCES agent_sessions(session_id),
            process_kind INTEGER NOT NULL,
            correlation_id TEXT NOT NULL,
            started_at_utc_ms INTEGER NOT NULL,
            last_heartbeat_utc_ms INTEGER NOT NULL,
            state INTEGER NOT NULL,
            owns_job_object INTEGER NOT NULL DEFAULT 0,
            shutdown_deadline_utc_ms INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_worker_processes_live_pid
            ON worker_processes(process_id, state);
        """;
}
