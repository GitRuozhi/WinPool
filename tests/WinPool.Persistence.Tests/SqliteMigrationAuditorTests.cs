using Microsoft.Data.Sqlite;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class SqliteMigrationAuditorTests
{
    [Fact]
    public async Task CopiedDatabaseMatchesSchemaRowsAndPrimaryKeyDigests()
    {
        using var directory = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(directory.Path, "source.db");
        var destinationPath = Path.Combine(directory.Path, "destination.db");
        var store = new WinPoolSqliteStore(sourcePath);
        await store.InitializeAsync();
        await using (var connection = await store.OpenConnectionAsync())
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO simulation_documents(
                    document_id, document_schema_version, display_name,
                    sanitized_json, sha256, revision, created_at_utc_ms, updated_at_utc_ms)
                VALUES
                    ('migration-a', 1, 'A', '{"value":1}',
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 1, 100, 100),
                    ('migration-b', 1, 'B', '{"value":2}',
                        'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 1, 200, 200);
                PRAGMA wal_checkpoint(TRUNCATE);
                """;
            await insert.ExecuteNonQueryAsync();
        }

        File.Copy(sourcePath, destinationPath);
        var auditor = new SqliteMigrationAuditor();
        var source = await auditor.CaptureAsync(sourcePath);
        var destination = await auditor.CaptureAsync(destinationPath);

        Assert.True(source.IsHealthy);
        Assert.Equal(WinPoolSqliteStore.CurrentSchemaVersion, source.SchemaVersion);
        Assert.True(source.HasSameLogicalIdentity(destination));
        var documents = source.Tables.Single(item => item.TableName == "simulation_documents");
        Assert.Equal(2, documents.RowCount);
        Assert.Equal(["document_id"], documents.PrimaryKeyColumns);
        Assert.Equal(64, documents.PrimaryKeySha256.Length);
    }

    [Fact]
    public async Task SameRowCountWithDifferentPrimaryKeyDoesNotMatch()
    {
        using var directory = TemporaryDirectory.Create();
        var firstPath = Path.Combine(directory.Path, "first.db");
        var secondPath = Path.Combine(directory.Path, "second.db");
        await CreateWithPresetAsync(firstPath, "alpha");
        await CreateWithPresetAsync(secondPath, "bravo");
        var auditor = new SqliteMigrationAuditor();

        var first = await auditor.CaptureAsync(firstPath);
        var second = await auditor.CaptureAsync(secondPath);

        Assert.False(first.HasSameLogicalIdentity(second));
        Assert.Equal(
            first.Tables.Select(item => item.RowCount),
            second.Tables.Select(item => item.RowCount));
    }

    private static async Task CreateWithPresetAsync(string path, string documentId)
    {
        var store = new WinPoolSqliteStore(path);
        await store.InitializeAsync();
        await using var connection = await store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO simulation_documents(
                document_id, document_schema_version, display_name,
                sanitized_json, sha256, revision, created_at_utc_ms, updated_at_utc_ms)
            VALUES($documentId, 1, 'Test', '{}',
                'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc', 1, 1, 1);
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        command.Parameters.AddWithValue("$documentId", documentId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WinPool.SqliteMigrationAudit.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new(path);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
