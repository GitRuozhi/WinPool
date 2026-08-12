using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class SqliteSchemaTests
{
    private static readonly string[] RequiredTables =
    [
        "schema_info", "preferences", "workspace_state", "systems",
        "inventory_snapshots", "local_inventory_document", "storage_objects", "storage_relationships",
        "operation_plans", "operation_steps", "execution_events",
        "simulation_documents", "simulation_edit_commits", "system_support_audit_events",
        "system_support_recovery",
        "monitor_sessions", "monitor_devices", "monitor_samples", "storage_health_events",
        "monitor_rollups",
        "test_definitions", "test_runs", "test_steps", "test_events", "test_metrics",
        "latency_histograms", "copy_batch_manifests", "copy_batches",
        "copy_batch_entries", "artifacts", "algorithm_registry",
        "inventory_comparisons", "external_tools", "tool_install_events",
        "agent_sessions", "worker_processes"
    ];

    [Fact]
    public async Task InitializeCreatesVersionedWalSchemaAndAllRequiredTables()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var connection = await database.Store.OpenConnectionAsync();

        Assert.Equal("wal", await ScalarTextAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(
            WinPoolSqliteStore.CurrentSchemaVersion,
            await ScalarInt64Async(connection, "SELECT schema_version FROM schema_info WHERE singleton=1;"));

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type='table' AND name NOT LIKE 'sqlite_%';
            """;
        var actual = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual.Add(reader.GetString(0));
        }

        Assert.All(RequiredTables, table => Assert.Contains(table, actual));
    }

    [Fact]
    public async Task ForeignKeysRejectOrphanMonitoringDevices()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var connection = await database.Store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO monitor_devices(session_id, device_id, sanitized_name, source_kind)
            VALUES ('missing', 'disk-0', 'Disk 0', 0);
            """;

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task MonitorSampleIndexesMatchHighVolumeQueryShapes()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var connection = await database.Store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_schema
            WHERE type='index' AND tbl_name='monitor_samples';
            """;
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        Assert.Contains("ix_monitor_samples_session_time", names);
        Assert.Contains("ix_monitor_samples_session_device_time", names);
    }

    [Fact]
    public async Task LegacySchemaTwoDatabaseIsRejectedWithoutChangingItsFiles()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using (var connection = await database.Store.OpenConnectionAsync())
        {
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                ALTER TABLE worker_processes DROP COLUMN owns_job_object;
                UPDATE schema_info
                SET schema_version = 2
                WHERE singleton = 1;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        await AssertLegacyDatabaseIsUnchangedAsync(database.Store, 2);
    }

    [Fact]
    public async Task LegacySchemaSevenDatabaseIsRejectedWithoutChangingItsFiles()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using (var connection = await database.Store.OpenConnectionAsync())
        {
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                ALTER TABLE test_runs DROP COLUMN plan_json;
                UPDATE schema_info
                SET schema_version = 7
                WHERE singleton = 1;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        await AssertLegacyDatabaseIsUnchangedAsync(database.Store, 7);
    }

    [Fact]
    public async Task LegacySchemaTenDatabaseIsRejectedWithoutChangingItsFiles()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var sessionId = Guid.NewGuid().ToString("N");
        var correlationId = Guid.NewGuid().ToString("N");
        await using (var connection = await database.Store.OpenConnectionAsync())
        {
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP INDEX IF EXISTS ix_worker_processes_live_pid;
                DROP TABLE worker_processes;
                CREATE TABLE worker_processes(
                    process_id INTEGER PRIMARY KEY,
                    agent_session_id TEXT NOT NULL REFERENCES agent_sessions(session_id),
                    process_kind INTEGER NOT NULL,
                    correlation_id TEXT NOT NULL,
                    started_at_utc_ms INTEGER NOT NULL,
                    last_heartbeat_utc_ms INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    owns_job_object INTEGER NOT NULL DEFAULT 0,
                    shutdown_deadline_utc_ms INTEGER
                );
                INSERT INTO agent_sessions(
                    session_id, process_id, started_at_utc_ms,
                    ended_at_utc_ms, shutdown_clean)
                VALUES($session, 10, 1000, NULL, 0);
                INSERT INTO worker_processes(
                    process_id, agent_session_id, process_kind, correlation_id,
                    started_at_utc_ms, last_heartbeat_utc_ms, state,
                    owns_job_object, shutdown_deadline_utc_ms)
                VALUES(42, $session, 1, $correlation, 1000, 2000, 3, 1, 3000);
                UPDATE schema_info SET schema_version = 10 WHERE singleton = 1;
                """;
            downgrade.Parameters.AddWithValue("$session", sessionId);
            downgrade.Parameters.AddWithValue("$correlation", correlationId);
            await downgrade.ExecuteNonQueryAsync();
        }

        await AssertLegacyDatabaseIsUnchangedAsync(database.Store, 10);
    }

    [Fact]
    public async Task ExistingDatabaseWithoutSchemaInfoIsRejectedWithoutChangingItsFiles()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using (var connection = await database.Store.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE schema_info;";
            await command.ExecuteNonQueryAsync();
        }

        await AssertLegacyDatabaseIsUnchangedAsync(database.Store, null);
    }

    [Theory]
    [InlineData("DROP TABLE external_tools;", "tables")]
    [InlineData("ALTER TABLE worker_processes DROP COLUMN owns_job_object;", "worker_processes.columns")]
    [InlineData("DROP INDEX ix_worker_processes_live_pid;", "worker_processes.indexes")]
    public async Task CurrentSchemaMismatchIsRejectedWithoutChangingItsFiles(
        string mutation,
        string expectedMismatch)
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using (var connection = await database.Store.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = mutation;
            await command.ExecuteNonQueryAsync();
        }

        await AssertCurrentCorruptDatabaseIsUnchangedAsync(
            database.Store,
            expectedMismatch);
    }

    [Fact]
    public async Task CurrentSchemaForeignKeyMismatchIsRejectedWithoutChangingItsFiles()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using (var connection = await database.Store.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=OFF;
                DROP TABLE monitor_devices;
                CREATE TABLE monitor_devices(
                    session_id TEXT NOT NULL,
                    device_id TEXT NOT NULL,
                    sanitized_name TEXT NOT NULL,
                    source_kind INTEGER NOT NULL,
                    PRIMARY KEY(session_id, device_id)
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        await AssertCurrentCorruptDatabaseIsUnchangedAsync(
            database.Store,
            "monitor_devices.foreign_keys");
    }

    [Fact]
    public async Task BatchWriterFlushesSamplesWithoutPersistingProviderIdentity()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var sessionId = SessionId.New();
        var systemId = SystemId.New();
        var target = new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "SECRET-PROVIDER-ID");
        var first = new MonitorSample(
            sessionId,
            target,
            DateTimeOffset.UtcNow,
            [
                new(MonitorMetricKind.ActiveTimePercent, 42),
                new(MonitorMetricKind.ReadBytesPerSecond, 1_024),
                new(MonitorMetricKind.WriteBytesPerSecond, 2_048),
                new(MonitorMetricKind.AverageQueueLength, 3)
            ],
            MayBeAffectedByActiveTest: true);
        var persistedDeviceId = MonitorSampleBatchWriter.PersistedDeviceId(first);

        await using (var connection = await database.Store.OpenConnectionAsync())
        {
            await using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT INTO monitor_sessions(
                    session_id, started_at_utc_ms, clock_source, state)
                VALUES ($session, $started, 'Stopwatch+UTC', 2);
                INSERT INTO monitor_devices(
                    session_id, device_id, sanitized_name, source_kind)
                VALUES ($session, $device, 'Disk', 0);
                """;
            setup.Parameters.AddWithValue("$session", sessionId.Value.ToString("N"));
            setup.Parameters.AddWithValue("$started", first.SampledAtUtc.ToUnixTimeMilliseconds());
            setup.Parameters.AddWithValue("$device", persistedDeviceId);
            await setup.ExecuteNonQueryAsync();
        }

        await using var lease =
            AgentWriteOwnerLease.Acquire(database.Store, "schema-test-agent");
        await using (var writer = new MonitorSampleBatchWriter(
                         database.Store,
                         lease,
                         capacity: 16,
                         maximumBatchSize: 2,
                         maximumBatchDelay: TimeSpan.FromMilliseconds(20)))
        {
            await writer.EnqueueAsync(first);
            await writer.EnqueueAsync(first with { SampledAtUtc = first.SampledAtUtc.AddMilliseconds(200) });
            await writer.CompleteAndFlushAsync();
            Assert.Equal(0, writer.RejectedSamples);
        }

        await using var verify = await database.Store.OpenConnectionAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = """
            SELECT COUNT(*), MIN(activity_pct), MIN(sample_flags)
            FROM monitor_samples
            WHERE session_id=$session AND device_id=$device;
            """;
        query.Parameters.AddWithValue("$session", sessionId.Value.ToString("N"));
        query.Parameters.AddWithValue("$device", persistedDeviceId);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(42, reader.GetDouble(1));
        Assert.Equal(1, reader.GetInt64(2));
        Assert.DoesNotContain("SECRET", persistedDeviceId, StringComparison.Ordinal);
    }

    private static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
    }

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task AssertLegacyDatabaseIsUnchangedAsync(
        WinPoolSqliteStore store,
        int? expectedVersion)
    {
        var paths = new[] { store.DatabasePath, store.DatabasePath + "-wal", store.DatabasePath + "-shm" };
        var before = paths.ToDictionary(
            path => path,
            path => File.Exists(path) ? File.ReadAllBytes(path) : null,
            StringComparer.Ordinal);

        var exception = await Assert.ThrowsAsync<LegacySqliteSchemaNotSupportedException>(
            () => store.InitializeAsync());
        Assert.Equal(expectedVersion, exception.ActualVersion);
        Assert.Contains(LegacySqliteSchemaNotSupportedException.StableCode, exception.Message);

        foreach (var path in paths)
        {
            Assert.Equal(before[path] is not null, File.Exists(path));
            if (before[path] is not null)
            {
                Assert.Equal(before[path], File.ReadAllBytes(path));
            }
        }
    }

    private static async Task AssertCurrentCorruptDatabaseIsUnchangedAsync(
        WinPoolSqliteStore store,
        string expectedMismatch)
    {
        var paths = new[] { store.DatabasePath, store.DatabasePath + "-wal", store.DatabasePath + "-shm" };
        var before = paths.ToDictionary(
            path => path,
            path => File.Exists(path) ? File.ReadAllBytes(path) : null,
            StringComparer.Ordinal);

        var exception = await Assert.ThrowsAsync<CurrentSqliteSchemaCorruptException>(
            () => store.InitializeAsync());
        Assert.Equal(expectedMismatch, exception.Mismatch);
        Assert.Contains(CurrentSqliteSchemaCorruptException.StableCode, exception.Message);

        foreach (var path in paths)
        {
            Assert.Equal(before[path] is not null, File.Exists(path));
            if (before[path] is not null)
            {
                Assert.Equal(before[path], File.ReadAllBytes(path));
            }
        }
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private TemporaryDatabase(string directory, WinPoolSqliteStore store)
        {
            Directory = directory;
            Store = store;
        }

        public string Directory { get; }
        public WinPoolSqliteStore Store { get; }

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "WinPool.Persistence.Tests",
                Guid.NewGuid().ToString("N"));
            var store = new WinPoolSqliteStore(Path.Combine(directory, "winpool.db"));
            await store.InitializeAsync();
            return new TemporaryDatabase(directory, store);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
