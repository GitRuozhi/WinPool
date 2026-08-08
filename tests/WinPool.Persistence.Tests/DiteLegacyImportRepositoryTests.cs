using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class DiteLegacyImportRepositoryTests
{
    [Fact]
    public async Task AgentOwnedImportIsAtomicAndIdempotentBySourceHash()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using var lease = AgentWriteOwnerLease.Acquire(
            database.Store,
            "dite-import-test-agent");
        var repository = new DiteLegacyImportRepository(database.Store, lease);
        var importedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        var source = CreateImport();

        var first = await repository.SaveAsync(source, importedAt);
        var duplicate = await repository.SaveAsync(
            source,
            importedAt.AddMinutes(1));
        var loaded = await new DiteLegacyImportRepository(database.Store)
            .GetBySha256Async(source.SourceSha256.ToUpperInvariant());
        var history = await new DiteLegacyImportRepository(database.Store)
            .ListAsync(10);
        var summaries = await new DiteLegacyImportRepository(database.Store)
            .GetSummariesAsync(first.Import.ImportId);

        Assert.False(first.AlreadyExisted);
        Assert.True(duplicate.AlreadyExisted);
        Assert.Equal(first.Import.ImportId, duplicate.Import.ImportId);
        Assert.Equal(importedAt, duplicate.Import.ImportedAtUtc);
        Assert.Equal(2, loaded!.RunCount);
        Assert.Equal(3, loaded.MetricCount);
        Assert.Equal(first.Import.ImportId, Assert.Single(history).ImportId);
        var throughput = Assert.Single(
            summaries!,
            item => item.MetricId == "throughput");
        Assert.Equal(2, throughput.Count);
        Assert.Equal(100, throughput.Minimum);
        Assert.Equal(110, throughput.Median);
        Assert.Equal(120, throughput.Maximum);

        await using var connection = await database.Store.OpenConnectionAsync();
        Assert.Equal(
            1,
            await ScalarAsync(connection, "SELECT COUNT(*) FROM legacy_test_imports;"));
        Assert.Equal(
            2,
            await ScalarAsync(connection, "SELECT COUNT(*) FROM legacy_test_runs;"));
        Assert.Equal(
            3,
            await ScalarAsync(connection, "SELECT COUNT(*) FROM legacy_test_metrics;"));
        Assert.Equal(
            "first.log",
            await ScalarTextAsync(
                connection,
                """
                SELECT log_file_name FROM legacy_test_runs
                WHERE run_ordinal=0;
                """));
    }

    [Fact]
    public async Task ReadOnlyRepositoryCannotPersistAndInvalidMetricsAreRejected()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var readOnly = new DiteLegacyImportRepository(database.Store);
        await Assert.ThrowsAsync<AgentWriteOwnershipException>(
            () => readOnly.SaveAsync(CreateImport(), DateTimeOffset.UtcNow));

        await using var lease = AgentWriteOwnerLease.Acquire(
            database.Store,
            "dite-validation-agent");
        var writer = new DiteLegacyImportRepository(database.Store, lease);
        var invalid = CreateImport() with
        {
            Runs =
            [
                CreateImport().Runs[0] with
                {
                    Metrics =
                    [
                        new("throughput", 1, "MiB/s"),
                        new("throughput", 2, "MiB/s")
                    ]
                }
            ]
        };
        await Assert.ThrowsAsync<InvalidDataException>(
            () => writer.SaveAsync(invalid, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task VersionSixDatabaseCreatesVersionSevenLegacyImportTables()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using (var connection = await database.Store.OpenConnectionAsync())
        {
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TABLE legacy_test_metrics;
                DROP TABLE legacy_test_runs;
                DROP TABLE legacy_test_imports;
                UPDATE schema_info SET schema_version = 6 WHERE singleton = 1;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        await database.Store.InitializeAsync();

        await using var verify = await database.Store.OpenConnectionAsync();
        Assert.Equal(
            WinPoolSqliteStore.CurrentSchemaVersion,
            await ScalarAsync(
                verify,
                "SELECT schema_version FROM schema_info WHERE singleton=1;"));
        Assert.Equal(
            3,
            await ScalarAsync(
                verify,
                """
                SELECT COUNT(*) FROM sqlite_schema
                WHERE type='table' AND name IN (
                    'legacy_test_imports',
                    'legacy_test_runs',
                    'legacy_test_metrics');
                """));
    }

    private static DiteLegacyImportResult CreateImport() =>
        new(
            "Dite-results.csv",
            new string('a', 64),
            [
                new(
                    "2026-07-01 10:00:00",
                    "H:",
                    "DiskSpd",
                    "Sequential",
                    @"C:\evidence\first.log",
                    [
                        new("throughput", 100, "MiB/s"),
                        new("iops", 1_000, "count")
                    ]),
                new(
                    "2026-07-01 11:00:00",
                    "H:",
                    "DiskSpd",
                    "Sequential",
                    "second.log",
                    [new("throughput", 120, "MiB/s")])
            ],
            []);

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
                   await command.ExecuteScalarAsync(),
                   System.Globalization.CultureInfo.InvariantCulture)
               ?? string.Empty;
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
            var store = new WinPoolSqliteStore(
                Path.Combine(directory, "winpool.db"));
            await store.InitializeAsync();
            return new(directory, store);
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
