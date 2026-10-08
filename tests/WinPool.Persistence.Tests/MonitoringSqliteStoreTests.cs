using Microsoft.Data.Sqlite;
using WinPool.Infrastructure.Sqlite;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Persistence.Tests;

public sealed class MonitoringSqliteStoreTests
{
    [Fact]
    public async Task EditGapRestartPreservesUnknownEndpointAndRejectsIdentityOrStartRewrite()
    {
        await using var fixture = await MonitoringDatabaseFixture.CreateAsync();
        var store = new MonitoringSqliteStore(fixture.MonitoringPath);
        await store.InitializeAsync();
        await using var owner = AgentWriteOwnerLease.Acquire(store, "monitor-gap-test");
        var session = SessionId.New();
        var system = SystemId.New();
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1725000000000);
        await new MonitorSessionRepository(store, owner).CreateAsync(new(session, start, null, "UTC", MonitoringSessionState.Running, 0));
        var gap = new MonitorEditGap("exact-gap", session, system, new(system, StorageObjectKind.VirtualDisk, "old-vd"),
            "vd-guid:" + Guid.NewGuid(), OperationId.New(), "delete-vd", start, null,
            MonitorEditTargetStatus.PendingVerification, "monitor.edit.outcome_needs_reconciliation");
        var repository = new MonitorEditGapRepository(store, owner);
        await repository.SaveAsync(gap);
        await new MonitoringSqliteStore(fixture.MonitoringPath).InitializeAsync();
        Assert.Equal(gap, Assert.Single(await new MonitorEditGapRepository(store).ListAsync(openOnly: true)));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(gap with { StartedAtUtc = start.AddSeconds(1) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(gap with { TargetId = new(system, StorageObjectKind.VirtualDisk, "new-vd") }));
        var ended = gap with { EndedAtUtc = start.AddSeconds(2), Status = MonitorEditTargetStatus.RemovedByEdit };
        await repository.SaveAsync(ended);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(gap));
        Assert.Equal(ended, Assert.Single(await repository.ListAsync()));
    }

    [Fact]
    public async Task ExactVersionOneMigratesWithoutChangingHistoricalRows()
    {
        await using var fixture = await MonitoringDatabaseFixture.CreateAsync();
        var store = new MonitoringSqliteStore(fixture.MonitoringPath);
        await store.InitializeAsync();
        await using (var connection = await store.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE monitor_edit_gaps;
                UPDATE monitoring_schema_info SET schema_version=1;
                INSERT INTO monitor_sessions VALUES('old-session',123456,NULL,'UTC',3,7);
                INSERT INTO monitor_devices VALUES('old-session','old-device','old-name',4);
                INSERT INTO monitor_samples(session_id,device_id,timestamp_utc_ms,read_bytes_per_sec)
                    VALUES('old-session','old-device',123457,99);
                """;
            await command.ExecuteNonQueryAsync();
        }
        await new MonitoringSqliteStore(fixture.MonitoringPath).InitializeAsync();
        await using var after = await store.OpenConnectionAsync();
        Assert.Equal(2L, await ScalarAsync(after, "SELECT schema_version FROM monitoring_schema_info;"));
        Assert.Equal(123457L, await ScalarAsync(after, "SELECT timestamp_utc_ms FROM monitor_samples;"));
        Assert.Equal(99d, await ScalarAsync(after, "SELECT read_bytes_per_sec FROM monitor_samples;"));
        Assert.Equal(0L, await ScalarAsync(after, "SELECT COUNT(*) FROM monitor_edit_gaps;"));
    }

    [Fact]
    public async Task DamagedVersionOneDoesNotMigrate()
    {
        await using var fixture = await MonitoringDatabaseFixture.CreateAsync();
        var store = new MonitoringSqliteStore(fixture.MonitoringPath);
        await store.InitializeAsync();
        await using (var connection = await store.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE monitor_edit_gaps; UPDATE monitoring_schema_info SET schema_version=1; DROP INDEX ix_monitor_samples_session_device_time;";
            await command.ExecuteNonQueryAsync();
        }
        var before = ReadDatabaseFiles(fixture.MonitoringPath);
        await Assert.ThrowsAsync<CurrentMonitoringSchemaCorruptException>(() => store.InitializeAsync());
        AssertDatabaseFilesEqual(before, fixture.MonitoringPath);
    }

    [Fact]
    public async Task InitializesIndependentMonitoringSchemaWithoutChangingCoreDatabase()
    {
        await using var fixture = await MonitoringDatabaseFixture.CreateAsync();
        var core = new WinPoolSqliteStore(fixture.CorePath);
        await core.InitializeAsync();
        var coreBefore = File.ReadAllBytes(fixture.CorePath);

        var store = new MonitoringSqliteStore(fixture.MonitoringPath);
        await store.InitializeAsync();

        Assert.Equal(coreBefore, File.ReadAllBytes(fixture.CorePath));
        await using var monitoring = await store.OpenConnectionAsync();
        await using var tables = monitoring.CreateCommand();
        tables.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type='table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        var actual = new List<string>();
        await using (var reader = await tables.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                actual.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            [
                "monitor_devices",
                "monitor_edit_gaps",
                "monitor_samples",
                "monitor_sessions",
                "monitoring_schema_info"
            ],
            actual);
        Assert.Equal(
            MonitoringSqliteStore.CurrentSchemaVersion,
            Convert.ToInt32(
                await ScalarAsync(monitoring, "SELECT schema_version FROM monitoring_schema_info WHERE singleton=1;"),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task CurrentSchemaMissingMetricColumnIsRejectedWithoutMutation()
    {
        await using var fixture = await MonitoringDatabaseFixture.CreateAsync();
        var store = new MonitoringSqliteStore(fixture.MonitoringPath);
        await store.InitializeAsync();
        await using (var connection = await store.OpenConnectionAsync())
        {
            await using var mutation = connection.CreateCommand();
            mutation.CommandText = "ALTER TABLE monitor_samples DROP COLUMN cpu_pct;";
            await mutation.ExecuteNonQueryAsync();
        }

        var before = ReadDatabaseFiles(fixture.MonitoringPath);
        var exception = await Assert.ThrowsAsync<CurrentMonitoringSchemaCorruptException>(
            () => new MonitoringSqliteStore(fixture.MonitoringPath).InitializeAsync());

        Assert.Equal("monitor_samples.columns", exception.Mismatch);
        AssertDatabaseFilesEqual(before, fixture.MonitoringPath);
    }

    [Fact]
    public async Task CurrentSchemaMissingRequiredIndexIsRejectedWithoutMutation()
    {
        await using var fixture = await MonitoringDatabaseFixture.CreateAsync();
        var store = new MonitoringSqliteStore(fixture.MonitoringPath);
        await store.InitializeAsync();
        await using (var connection = await store.OpenConnectionAsync())
        {
            await using var mutation = connection.CreateCommand();
            mutation.CommandText = "DROP INDEX ix_monitor_samples_session_device_time;";
            await mutation.ExecuteNonQueryAsync();
        }

        var before = ReadDatabaseFiles(fixture.MonitoringPath);
        var exception = await Assert.ThrowsAsync<CurrentMonitoringSchemaCorruptException>(
            () => new MonitoringSqliteStore(fixture.MonitoringPath).InitializeAsync());

        Assert.Equal("monitor_samples.indexes", exception.Mismatch);
        AssertDatabaseFilesEqual(before, fixture.MonitoringPath);
    }

    [Fact]
    public async Task RetainedWalIsReadWhenInspectingExistingSchema()
    {
        await using var fixture = await MonitoringDatabaseFixture.CreateAsync();
        var store = new MonitoringSqliteStore(fixture.MonitoringPath);
        await store.InitializeAsync();
        await using var writer = await store.OpenConnectionAsync();
        await using (var update = writer.CreateCommand())
        {
            update.CommandText = "UPDATE monitoring_schema_info SET schema_version = $version WHERE singleton=1;";
            update.Parameters.AddWithValue("$version", MonitoringSqliteStore.CurrentSchemaVersion + 1);
            await update.ExecuteNonQueryAsync();
        }

        Assert.True(File.Exists(fixture.MonitoringPath + "-wal"));
        var exception = await Assert.ThrowsAsync<UnsupportedMonitoringSchemaVersionException>(
            () => new MonitoringSqliteStore(fixture.MonitoringPath).InitializeAsync());

        Assert.Equal(MonitoringSqliteStore.CurrentSchemaVersion + 1, exception.ActualVersion);
    }

    [Fact]
    public async Task CheckpointRejectsBusyReaderAndLeavesDatabaseUnsealed()
    {
        await using var fixture = await MonitoringDatabaseFixture.CreateAsync();
        var store = new MonitoringSqliteStore(fixture.MonitoringPath);
        await store.InitializeAsync();
        await using var reader = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = fixture.MonitoringPath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
        await reader.OpenAsync();
        await using (var begin = reader.CreateCommand())
        {
            begin.CommandText = "BEGIN; SELECT schema_version FROM monitoring_schema_info;";
            await begin.ExecuteNonQueryAsync();
        }

        await using (var writer = await store.OpenConnectionAsync())
        {
            await using var update = writer.CreateCommand();
            update.CommandText = "UPDATE monitoring_schema_info SET applied_at_utc_ms = applied_at_utc_ms + 1 WHERE singleton=1;";
            await update.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<IOException>(() => store.CheckpointAndCloseAsync());
        Assert.True(File.Exists(fixture.MonitoringPath));
        await using var rollback = reader.CreateCommand();
        rollback.CommandText = "ROLLBACK;";
        await rollback.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task CompleteCheckpointLeavesSelfContainedDatabase()
    {
        await using var fixture = await MonitoringDatabaseFixture.CreateAsync();
        var store = new MonitoringSqliteStore(fixture.MonitoringPath);
        await store.InitializeAsync();
        await using (var connection = await store.OpenConnectionAsync())
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE monitoring_schema_info SET applied_at_utc_ms = applied_at_utc_ms + 1 WHERE singleton=1;";
            await update.ExecuteNonQueryAsync();
        }

        await store.CheckpointAndCloseAsync();

        Assert.True(File.Exists(fixture.MonitoringPath));
        Assert.False(File.Exists(fixture.MonitoringPath + "-wal"));
        Assert.False(File.Exists(fixture.MonitoringPath + "-shm"));
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync();
    }

    private static IReadOnlyDictionary<string, byte[]?> ReadDatabaseFiles(string databasePath) =>
        new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
            .ToDictionary(
                path => path,
                path => File.Exists(path) ? File.ReadAllBytes(path) : null,
                StringComparer.Ordinal);

    private static void AssertDatabaseFilesEqual(
        IReadOnlyDictionary<string, byte[]?> before,
        string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            Assert.Equal(before[path] is not null, File.Exists(path));
            if (before[path] is { } bytes)
            {
                Assert.Equal(bytes, File.ReadAllBytes(path));
            }
        }
    }

    private sealed class MonitoringDatabaseFixture : IAsyncDisposable
    {
        private MonitoringDatabaseFixture(string directory)
        {
            Directory = directory;
            CorePath = Path.Combine(directory, "winpool.db");
            MonitoringPath = Path.Combine(directory, "monitoring.db");
        }

        public string Directory { get; }

        public string CorePath { get; }

        public string MonitoringPath { get; }

        public static Task<MonitoringDatabaseFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"WinPool monitoring {Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            return Task.FromResult(new MonitoringDatabaseFixture(directory));
        }

        public ValueTask DisposeAsync()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
