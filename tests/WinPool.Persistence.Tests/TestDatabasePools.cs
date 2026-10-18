using WinPool.Infrastructure.Sqlite;

namespace WinPool.Persistence.Tests;

internal static class TestDatabasePools
{
    // Test roots are unique. Never checkpoint another concurrently running fixture.
    public static void DrainDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var database in Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories))
            WinPoolSqliteStore.DrainConnectionPool(database);
    }
}
