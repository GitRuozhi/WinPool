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
    public async Task VersionTwoDatabaseMigratesWorkerJobOwnershipToCurrentVersion()
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

        await database.Store.InitializeAsync();

        await using var verify = await database.Store.OpenConnectionAsync();
        Assert.Equal(
            WinPoolSqliteStore.CurrentSchemaVersion,
            await ScalarInt64Async(
                verify,
                "SELECT schema_version FROM schema_info WHERE singleton=1;"));
        await using var columns = verify.CreateCommand();
        columns.CommandText = "PRAGMA table_info(worker_processes);";
        var names = new List<string>();
        await using var reader = await columns.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        Assert.Contains("owns_job_object", names);
    }

    [Fact]
    public async Task VersionSevenDatabaseAddsPersistedTestPlanForRecovery()
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

        await database.Store.InitializeAsync();

        await using var verify = await database.Store.OpenConnectionAsync();
        await using var columns = verify.CreateCommand();
        columns.CommandText = "PRAGMA table_info(test_runs);";
        var names = new List<string>();
        await using var reader = await columns.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        Assert.Contains("plan_json", names);
        Assert.Equal(
            WinPoolSqliteStore.CurrentSchemaVersion,
            await ScalarInt64Async(
                verify,
                "SELECT schema_version FROM schema_info WHERE singleton=1;"));
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
