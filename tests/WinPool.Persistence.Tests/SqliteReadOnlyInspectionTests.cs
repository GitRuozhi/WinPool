using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

public sealed class SqliteReadOnlyInspectionTests
{
    [Theory]
    [InlineData("data#root", "data", true)]
    [InlineData("data%23root", "data#root", false)]
    [InlineData("data%25root", "data%root", false)]
    public async Task EscapedDataRootReadsTheExactDatabaseWithoutWritingAnySourceFile(
        string directoryName, string decodedName, bool decoyIsFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPool-Uri-Inspection-" + Guid.NewGuid().ToString("N"));
        var actual = new WinPoolSqliteStore(Path.Combine(root, directoryName, "winpool.db"));
        var decoy = new WinPoolSqliteStore(decoyIsFile
            ? Path.Combine(root, decodedName)
            : Path.Combine(root, decodedName, "winpool.db"));
        try
        {
            // The decoy makes accidental URI fragment/percent decoding observable:
            // opening a valid different database must also fail this behavior test.
            await SeedAsync(actual, 123456789);
            await SeedAsync(decoy, 987654321);
            var before = Snapshot(root);
            await using (var inspection = await SqliteReadOnlyInspection.OpenAsync(actual.DatabasePath, CancellationToken.None))
            {
                await using var command = inspection.Connection.CreateCommand();
                command.CommandText = "SELECT applied_at_utc_ms FROM schema_info WHERE singleton=1;";
                Assert.Equal(123456789L, Convert.ToInt64(await command.ExecuteScalarAsync()));
                command.CommandText = "UPDATE schema_info SET applied_at_utc_ms=1 WHERE singleton=1;";
                var exception = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(8, exception.SqliteErrorCode); // SQLITE_READONLY
            }
            Assert.Equal(before, Snapshot(root));
        }
        finally
        {
            actual.DrainConnectionPool();
            decoy.DrainConnectionPool();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedAsync(WinPoolSqliteStore store, long marker)
    {
        await store.InitializeAsync();
        await using (var connection = await store.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE schema_info SET applied_at_utc_ms=$marker WHERE singleton=1;";
            command.Parameters.AddWithValue("$marker", marker);
            await command.ExecuteNonQueryAsync();
        }
        store.DrainConnectionPool();
    }

    private static string[] Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path =>
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Path.GetRelativePath(root, path) + "=" + Convert.ToHexString(SHA256.HashData(input));
        }).ToArray();
}
