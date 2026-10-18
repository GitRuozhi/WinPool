using Microsoft.Data.Sqlite;

namespace WinPool.Infrastructure.Sqlite;

/// <summary>Schema probes never create or update sidecars beside the source.</summary>
internal sealed class SqliteReadOnlyInspection(SqliteConnection connection, string? temporaryDirectory) : IAsyncDisposable
{
    public SqliteConnection Connection { get; } = connection;

    public static async Task<SqliteReadOnlyInspection> OpenAsync(string databasePath, CancellationToken cancellationToken)
    {
        string? temporaryDirectory = null;
        var source = databasePath;
        var wal = databasePath + "-wal";
        if (File.Exists(wal) && new FileInfo(wal).Length > 0)
        {
            // A WAL can contain committed schema changes. immutable=1 would
            // ignore them, while opening the source normally may alter SHM.
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "WinPool-db-inspect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            source = Path.Combine(temporaryDirectory, Path.GetFileName(databasePath));
            try
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    if (!File.Exists(databasePath + suffix)) continue;
                    await using var input = new FileStream(databasePath + suffix, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var output = new FileStream(source + suffix, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, cancellationToken);
                }
            }
            catch
            {
                Directory.Delete(temporaryDirectory, true);
                throw;
            }
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = temporaryDirectory is null ? new Uri(Path.GetFullPath(databasePath)).AbsoluteUri + "?immutable=1" : source,
            Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            return new(connection, temporaryDirectory);
        }
        catch
        {
            await connection.DisposeAsync();
            if (temporaryDirectory is not null) Directory.Delete(temporaryDirectory, true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
    }
}
